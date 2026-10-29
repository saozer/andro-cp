using System.Globalization;
using System.Security.Cryptography;

namespace AndroCp.Core;

internal sealed class CopyStorage(AdbClient adb, CopyEndpoint endpoint, Func<string, long>? availableSpace)
{
    public bool IsAndroid => endpoint.IsAndroid;
    public string? Serial => endpoint.Serial;
    public string Root { get; } = endpoint.IsAndroid ? AdbClient.AndroidPath(endpoint.Path) : Path.TrimEndingDirectorySeparator(Path.GetFullPath(endpoint.Path));
    public string At(string relative)
    {
        if (relative.Length == 0) return Root;
        CopyPathSafety.CheckRelative(relative);
        return IsAndroid ? Root + "/" + relative : Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    }
    public async Task<string> ValidateAsync(bool source, CancellationToken ct)
    {
        if (IsAndroid) return await adb.CanonicalPathAsync(Serial!, Root, requireDirectory: source, ct);
        CopyPathSafety.CheckLocalParents(Root);
        if (!Directory.Exists(Root)) throw new IOException("Choisissez un dossier local existant : " + Root);
        return Root;
    }
    public async Task<IReadOnlyList<(string Name, bool IsDirectory)>> ListAsync(string relative, CancellationToken ct)
    {
        var path = At(relative);
        if (IsAndroid) return await adb.ListDirectoryAsync(Serial!, path, ct, allowRootLink: relative.Length == 0);
        CopyPathSafety.CheckLocalParents(path);
        if (!Directory.Exists(path))
        {
            if (CopyPathSafety.LocalExists(path)) throw new IOException("Un parent de destination n’est pas un dossier : " + path);
            return [];
        }
        return Directory.EnumerateFileSystemEntries(path).Select(item =>
        {
            var attributes = File.GetAttributes(item);
            return (Path.GetFileName(item), (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory);
        }).ToArray();
    }
    public async Task<bool> ExistsAsync(string relative, CancellationToken ct) => IsAndroid
        ? await adb.ExistsAsync(Serial!, At(relative), ct) : CopyPathSafety.LocalExists(At(relative));
    public async Task<long> FreeBytesAsync(CancellationToken ct)
    {
        if (availableSpace is not null) return availableSpace(Root);
        if (IsAndroid) return await adb.AvailableBytesAsync(Serial!, Root, ct);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives().Where(d => Root.Equals(Path.TrimEndingDirectorySeparator(d.Name), comparison) ||
            Root.StartsWith(Path.TrimEndingDirectorySeparator(d.Name) + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault();
        return (drive ?? new DriveInfo(Path.GetPathRoot(Root)!)).AvailableFreeSpace;
    }
    public async Task<(long Size, long Modified)> MetadataAsync(string relative, CancellationToken ct)
    {
        var path = At(relative);
        if (!IsAndroid)
        {
            CopyPathSafety.CheckLocalParents(path);
            var file = new FileInfo(path);
            return (file.Length, file.LastWriteTimeUtc.Ticks);
        }
        var q = AdbClient.Quote(path);
        var fields = (await adb.ShellAsync(Serial!, $"test -f {q} && test ! -L {q} && stat -c '%s %Y' {q}", ct))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2) throw new IOException("Métadonnées Android invalides : " + path);
        return (long.Parse(fields[0], CultureInfo.InvariantCulture), checked(long.Parse(fields[1], CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond));
    }
    public async Task CheckSourceAsync(SourceFile file, CancellationToken ct)
    {
        var (size, modified) = await MetadataAsync(file.RelativePath, ct);
        var expected = file.ModifiedTicks;
        if (size != file.Size || expected is { } stamp &&
            (IsAndroid ? modified / TimeSpan.TicksPerSecond != stamp / TimeSpan.TicksPerSecond : modified != stamp))
            throw new IOException("Source modifiée depuis l’inventaire : " + file.RelativePath);
    }
    public async Task<string> HashAsync(string relative, CancellationToken ct)
    {
        var path = At(relative);
        if (!IsAndroid)
        {
            CopyPathSafety.CheckLocalParents(path);
            await using var input = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
        }
        var q = AdbClient.Quote(path);
        var hash = (await adb.ShellAsync(Serial!, $"test -f {q} && test ! -L {q} && sha256sum < {q}", ct))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new IOException("SHA-256 Android invalide : " + path);
        return hash.ToLowerInvariant();
    }
    public async Task PrepareAsync(string destination, string staging, CancellationToken ct)
    {
        if (!IsAndroid)
        {
            CopyPathSafety.CheckLocalParents(At(destination));
            CopyPathSafety.CheckLocalParents(At(staging));
            Directory.CreateDirectory(Path.GetDirectoryName(At(staging))!);
            if (File.Exists(At(staging))) File.Delete(At(staging));
            return;
        }
        var check = ParentChecks(destination);
        var parent = At(staging)[..At(staging).LastIndexOf('/')];
        var temp = AdbClient.Quote(At(staging));
        await adb.ShellAsync(Serial!, (check.Length > 0 ? check + "; " : "") +
            $"mkdir -p {AdbClient.Quote(parent)} || exit 1; if [ -L {temp} ] || [ -d {temp} ]; then echo 'Conflit sur le staging' >&2; exit 1; fi; rm -f {temp}", ct);
    }
    public async Task CommitAsync(string staging, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsAndroid) { File.Move(At(staging), At(destination), overwrite: false); return; }
        var temp = AdbClient.Quote(At(staging));
        var final = AdbClient.Quote(At(destination));
        await adb.ShellAsync(Serial!, $"if [ -e {final} ] || [ -L {final} ]; then echo 'Destination déjà existante' >&2; exit 1; fi; mv -n {temp} {final} && test ! -e {temp}", ct);
    }
    public async Task CleanupAsync(string staging)
    {
        if (!IsAndroid)
        {
            // May remain after a forced stop; it belongs to the queue and is reclaimed at retry.
            try { CopyPathSafety.CheckLocalParents(At(staging)); if (File.Exists(At(staging))) File.Delete(At(staging)); } catch (IOException) { }
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var check = ParentChecks(staging);
        var temp = AdbClient.Quote(At(staging));
        try { await adb.ShellAsync(Serial!, (check.Length > 0 ? check + "; " : "") + $"test ! -L {temp} && rm -f {temp}", timeout.Token); } catch (Exception) { }
    }
    private string ParentChecks(string relative)
    {
        var parents = relative.Split('/').SkipLast(1).ToArray();
        return string.Join("; ", Enumerable.Range(1, parents.Length).Select(n =>
            $"if [ -L {AdbClient.Quote(At(string.Join('/', parents.Take(n))))} ]; then echo 'Parent lié interdit' >&2; exit 1; fi"));
    }
}
