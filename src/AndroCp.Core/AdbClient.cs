using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AndroCp.Core;

public sealed class AdbClient
{
    public string Executable { get; }
    public AdbClient(string? executable = null)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        Executable = executable ?? (File.Exists(bundled) ? bundled : "adb");
    }
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    public static string AndroidPath(string value)
    {
        value = value.TrimEnd('/');
        if (!value.StartsWith('/') || value.Contains('\0') || value.Split('/').Any(x => x is "." or ".."))
            throw new ArgumentException("Le chemin Android doit être absolu et sans . ou ..");
        // Keep this copier within the shared storage: it is not a root/backup tool.
        if (!(value == "/sdcard" || value.StartsWith("/sdcard/", StringComparison.Ordinal) ||
              value == "/storage" || value.StartsWith("/storage/", StringComparison.Ordinal)))
            throw new ArgumentException("Utilise /sdcard ou un chemin sous /storage.");
        return value;
    }
    private Process Start(IEnumerable<string> args, bool redirectOutput = true, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true, RedirectStandardInput = redirectInput, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Impossible de démarrer ADB.");
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
    }
    private static async Task StopAsync(Process process)
    {
        Kill(process);
        // A confirmed pause must not leave an ADB process transferring in the background.
        await process.WaitForExitAsync(CancellationToken.None);
    }
    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        using var process = Start(args);
        using var registration = ct.Register(() => Kill(process));
        var error = process.StandardError.ReadToEndAsync(ct);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            var text = await output;
            var err = await error;
            if (process.ExitCode != 0) throw new IOException($"ADB ({process.ExitCode}) : {err.Trim()} {text.Trim()}");
            return text;
        }
        finally { await StopAsync(process); }
    }
    public Task<string> ShellAsync(string serial, string command, CancellationToken ct) =>
        RunAsync(ShellArguments(serial, command), ct);
    // Ordinary shell commands do not consume stdin. -n prevents accidental input waits;
    // -T disables the PTY, keeping bytes intact. Unlike exec-out, shell v2 also
    // propagates the remote exit code and keeps stderr separate from file data.
    public static string[] ShellArguments(string serial, string command) =>
        ["-s", serial, "shell", "-T", "-n", command];
    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken ct)
    {
        var output = await RunAsync(["devices", "-l"], ct);
        return output.Split('\n').Skip(1).Select(x => x.Trim()).Where(x => x.Length > 0)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new AndroidDevice(parts[0], parts[1],
                parts.FirstOrDefault(x => x.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ') ?? "Android"))
            .ToArray();
    }
    public async Task<DeviceConnection> CheckConnectionAsync(string serial, CancellationToken ct)
    {
        // Re-read the device state: a ComboBox item may predate an authorization or disconnection.
        var device = (await DevicesAsync(ct)).FirstOrDefault(x => x.Serial == serial);
        if (device is null) return new(DeviceConnectionState.Missing, "Le téléphone sélectionné est déconnecté ou n’est plus détecté. Rebranchez-le puis cliquez sur Actualiser.");
        if (device.State == "unauthorized") return new(DeviceConnectionState.Unauthorized,
            "Téléphone non autorisé : déverrouillez-le et acceptez l’autorisation de débogage USB.");
        if (device.State == "offline") return new(DeviceConnectionState.Offline,
            "Téléphone hors ligne (offline) : reconnectez le câble USB puis cliquez sur Actualiser.");
        if (device.State != "device") return new(DeviceConnectionState.Failed,
            $"Téléphone indisponible (état ADB : {device.State}). Vérifiez son mode USB et ses autorisations.");
        try
        {
            // Harmless, selected-device-only probe. An authorized transport is not always a working shell.
            var response = await ShellAsync(serial, "printf 'androcp-access-ok'", ct);
            return response.Trim() == "androcp-access-ok"
                ? new(DeviceConnectionState.Ready, $"{device.Model} ({device.Serial}) — Connecté et autorisé, prêt à copier.")
                : new(DeviceConnectionState.Failed, "Le téléphone est détecté, mais la vérification d’accès ADB n’a pas abouti.");
        }
        catch (IOException ex)
        {
            return new(DeviceConnectionState.Failed, "L’accès ADB au téléphone a échoué : " + ex.Message);
        }
    }
    public async Task<List<SourceFile>> InventoryAsync(string serial, string root, CopyRules rules, CancellationToken ct)
    {
        root = AndroidPath(root);
        // Prune excluded directories, but traverse ancestors of explicitly included exceptions.
        // The final filter is also applied locally before any copy.
        var prune = rules.FindPruneExpression();
        var command = $"cd {Quote(root)} && find . " + (prune.Length > 0 ? $"\\( {prune} \\) -prune -o " : "") + "-type f -printf '%s\\0%T@\\0%p\\0'";
        // Read binary to preserve NUL separators and embedded newlines in file names.
        using var process = Start(ShellArguments(serial, command));
        using var registration = ct.Register(() => Kill(process));
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        using var bytes = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(bytes, ct);
            await process.WaitForExitAsync(ct);
        }
        finally { await StopAsync(process); }
        var error = await errorTask;
        if (process.ExitCode != 0 || error.Length > 0)
            throw new IOException("Inventaire Android incomplet : " + error.Trim());
        var fields = new UTF8Encoding(false, true).GetString(bytes.ToArray()).Split('\0');
        if (fields.Length % 3 != 1 || fields[^1] != "") throw new IOException("Inventaire Android malformé.");
        var files = new List<SourceFile>();
        for (var i = 0; i + 2 < fields.Length; i += 3)
        {
            var relative = fields[i + 2].StartsWith("./", StringComparison.Ordinal) ? fields[i + 2][2..] : fields[i + 2];
            var ticks = (long)(decimal.Parse(fields[i + 1], CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond);
            if (rules.Includes(relative)) files.Add(new(relative, long.Parse(fields[i], CultureInfo.InvariantCulture), ticks));
        }
        return files;
    }
    public async Task DownloadAsync(string serial, string remote, string local, CancellationToken ct)
    {
        using var process = Start(ShellArguments(serial, "cat " + Quote(remote)));
        using var registration = ct.Register(() => Kill(process));
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await using (var target = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                await process.StandardOutput.BaseStream.CopyToAsync(target, ct);
            await process.WaitForExitAsync(ct);
        }
        finally { await StopAsync(process); }
        var error = await errorTask;
        if (process.ExitCode != 0 || error.Length > 0) throw new IOException("Lecture Android : " + error.Trim());
    }
    public async Task<bool> ExistsAsync(string serial, string path, CancellationToken ct) =>
        (await ShellAsync(serial, $"if [ -e {Quote(path)} ] || [ -L {Quote(path)} ]; then echo yes; fi", ct)).Trim() == "yes";

    public async Task<IReadOnlyList<(string Name, bool IsDirectory)>> ListDirectoryAsync(
        string serial, string path, CancellationToken ct, bool allowRootLink = false)
    {
        var quoted = Quote(AndroidPath(path));
        var noLink = path == "/sdcard" || allowRootLink ? "" : $"if [ -L {quoted} ]; then echo 'Dossier lié interdit' >&2; exit 1; fi; ";
        var command = noLink + $"if [ ! -e {quoted} ]; then exit 0; fi; " +
            $"if [ ! -d {quoted} ]; then echo 'Un parent de destination n’est pas un dossier' >&2; exit 1; fi; " +
            $"find {Quote(AndroidPath(path) + "/.")} -mindepth 1 -maxdepth 1 \\( -type d -printf 'd\\0%f\\0' -o -printf 'f\\0%f\\0' \\)";
        var fields = (await ShellAsync(serial, command, ct)).Split('\0');
        if (fields.Length % 2 != 1 || fields[^1] != "") throw new IOException("Liste de dossier Android malformée.");
        var entries = new List<(string Name, bool IsDirectory)>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            entries.Add((fields[i + 1], fields[i] == "d"));
        return entries;
    }

    public async Task<string> CanonicalPathAsync(string serial, string path, bool requireDirectory, CancellationToken ct)
    {
        path = AndroidPath(path);
        var command = (requireDirectory ? $"test -d {Quote(path)} || exit 1; " : "") +
            $"p={Quote(path)}; suffix=''; while [ ! -e \"$p\" ] && [ ! -L \"$p\" ]; do " +
            "suffix=\"/${p##*/}$suffix\"; p=\"${p%/*}\"; [ -n \"$p\" ] || p=/; done; " +
            "test -d \"$p\" || exit 1; base=$(readlink -f \"$p\") || exit 1; printf '%s%s' \"$base\" \"$suffix\"";
        var canonical = (await ShellAsync(serial, command, ct)).TrimEnd('\r', '\n').TrimEnd('/');
        if (!canonical.StartsWith('/') || canonical.Contains('\0')) throw new IOException("Impossible de résoudre le chemin Android : " + path);
        return canonical;
    }
    public async Task<long> AvailableBytesAsync(string serial, string root, CancellationToken ct)
    {
        var output = await ShellAsync(serial, $"p={Quote(root)}; while [ ! -e \"$p\" ]; do p=\"${{p%/*}}\"; [ -n \"$p\" ] || p=/; done; df -k \"$p\"", ct);
        var fields = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 6 || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var free))
            throw new IOException("Impossible de mesurer l’espace disponible sur Android.");
        return checked(free * 1024);
    }
    public async Task RelayAsync(string sourceSerial, string sourcePath, string destinationSerial, string staging, CancellationToken ct)
    {
        // shell -T (WITHOUT -n) is required on the receiving side to forward stdin and EOF.
        // stdout/stderr remain separate through shell v2; no full staging copy on the PC.
        using var receiver = Start(["-s", destinationSerial, "shell", "-T", "cat > " + Quote(staging)], redirectInput: true);
        Process? sender = null;
        using var cancelled = ct.Register(() => { if (sender is not null) Kill(sender); Kill(receiver); });
        try
        {
            sender = Start(ShellArguments(sourceSerial, "cat " + Quote(sourcePath)));
            var sourceError = sender.StandardError.ReadToEndAsync(ct);
            var targetError = receiver.StandardError.ReadToEndAsync(ct);
            var targetOutput = receiver.StandardOutput.ReadToEndAsync(ct);
            await sender.StandardOutput.BaseStream.CopyToAsync(receiver.StandardInput.BaseStream, 1024 * 1024, ct);
            await receiver.StandardInput.BaseStream.FlushAsync(ct);
            receiver.StandardInput.Close();
            await Task.WhenAll(sender.WaitForExitAsync(ct), receiver.WaitForExitAsync(ct));
            var error = (await sourceError) + (await targetError);
            await targetOutput;
            if (sender.ExitCode != 0 || receiver.ExitCode != 0 || error.Length > 0)
                throw new IOException("Relais Android → Android interrompu : " + error.Trim());
        }
        finally
        {
            if (sender is not null) { await StopAsync(sender); sender.Dispose(); }
            await StopAsync(receiver);
        }
    }
}
