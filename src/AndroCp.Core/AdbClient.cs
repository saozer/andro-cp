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
    private Process Start(IEnumerable<string> args, bool redirectOutput = true)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Impossible de démarrer ADB.");
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
    }
    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        using var process = Start(args);
        using var registration = ct.Register(() => Kill(process));
        var error = process.StandardError.ReadToEndAsync(ct);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var text = await output;
        var err = await error;
        if (process.ExitCode != 0) throw new IOException($"ADB ({process.ExitCode}) : {err.Trim()} {text.Trim()}");
        return text;
    }
    public Task<string> ShellAsync(string serial, string command, CancellationToken ct) =>
        RunAsync(ShellArguments(serial, command), ct);
    // No command in this client consumes stdin. -n prevents accidental input waits;
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
    public async Task<List<SourceFile>> InventoryAsync(string serial, string root, CopyRules rules, CancellationToken ct)
    {
        root = AndroidPath(root);
        // Prune excluded directories, but traverse ancestors of explicitly included exceptions.
        // The final filter is also applied locally before any copy.
        var prune = rules.FindPruneExpression();
        var command = $"cd {Quote(root)} && find . " + (prune.Length > 0 ? $"\\( {prune} \\) -prune -o " : "") + "-type f -printf '%s\\0%p\\0'";
        // Read binary to preserve NUL separators and embedded newlines in file names.
        using var process = Start(ShellArguments(serial, command));
        using var registration = ct.Register(() => Kill(process));
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes, ct);
        await process.WaitForExitAsync(ct);
        var error = await errorTask;
        if (process.ExitCode != 0 || error.Length > 0)
            throw new IOException("Inventaire Android incomplet : " + error.Trim());
        var fields = new UTF8Encoding(false, true).GetString(bytes.ToArray()).Split('\0');
        if (fields.Length % 2 != 1 || fields[^1] != "") throw new IOException("Inventaire Android malformé.");
        var files = new List<SourceFile>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var relative = fields[i + 1].StartsWith("./", StringComparison.Ordinal) ? fields[i + 1][2..] : fields[i + 1];
            if (rules.Includes(relative)) files.Add(new(relative, long.Parse(fields[i], CultureInfo.InvariantCulture)));
        }
        return files;
    }
    public async Task DownloadAsync(string serial, string remote, string local, CancellationToken ct)
    {
        using var process = Start(ShellArguments(serial, "cat " + Quote(remote)));
        using var registration = ct.Register(() => Kill(process));
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await using (var target = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            await process.StandardOutput.BaseStream.CopyToAsync(target, ct);
        await process.WaitForExitAsync(ct);
        var error = await errorTask;
        if (process.ExitCode != 0 || error.Length > 0) throw new IOException("Lecture Android : " + error.Trim());
    }
    public async Task<bool> ExistsAsync(string serial, string path, CancellationToken ct) =>
        (await ShellAsync(serial, $"if [ -e {Quote(path)} ] || [ -L {Quote(path)} ]; then echo yes; fi", ct)).Trim() == "yes";

    public async Task<IReadOnlyList<(string Name, bool IsDirectory)>> ListDirectoryAsync(
        string serial, string path, CancellationToken ct)
    {
        var quoted = Quote(AndroidPath(path));
        var noLink = path == "/sdcard" ? "" : $"if [ -L {quoted} ]; then echo 'Dossier lié interdit' >&2; exit 1; fi; ";
        var command = noLink + $"if [ ! -e {quoted} ]; then exit 0; fi; " +
            $"if [ ! -d {quoted} ]; then echo 'Un parent de destination n’est pas un dossier' >&2; exit 1; fi; " +
            $"find {quoted} -mindepth 1 -maxdepth 1 \\( -type d -printf 'd\\0%f\\0' -o -printf 'f\\0%f\\0' \\)";
        var fields = (await ShellAsync(serial, command, ct)).Split('\0');
        if (fields.Length % 2 != 1 || fields[^1] != "") throw new IOException("Liste de dossier Android malformée.");
        var entries = new List<(string Name, bool IsDirectory)>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            entries.Add((fields[i + 1], fields[i] == "d"));
        return entries;
    }
}
