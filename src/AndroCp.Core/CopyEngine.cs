using System.Text;
using System.Text.Json;

namespace AndroCp.Core;

public sealed class CopyEngine(AdbClient adb)
{
    public static List<SourceFile> LocalInventory(string root, CopyRules rules, CancellationToken ct)
    {
        var files = new List<SourceFile>();
        void Walk(string directory)
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Les liens symboliques/jonctions ne sont pas pris en charge : " + directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (rules.CanTraverse(relative)) Walk(entry);
                }
                else if (rules.Includes(relative))
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Lien symbolique non pris en charge : " + entry);
                    files.Add(new(relative, new FileInfo(entry).Length));
                }
            }
        }
        Walk(root);
        return files;
    }

    public async Task<CopyResult> CopyAsync(CopyOptions options, ResolveName resolve,
        IProgress<CopyProgress> progress, Action<string> log, CancellationToken ct)
    {
        var download = options.Direction == CopyDirection.AndroidToComputer;
        var androidRoot = AdbClient.AndroidPath(download ? options.Source : options.Destination);
        var localRoot = Path.GetFullPath(download ? options.Destination : options.Source);
        if (download && !Directory.Exists(localRoot)) throw new IOException("Choisis un dossier de destination existant.");
        if (!Directory.Exists(localRoot)) throw new IOException("Dossier local introuvable.");
        CheckLocalParents(localRoot);
        var rules = new CopyRules(options.Exclusions, options.Exceptions);
        log("Inventaire en cours… Aucun fichier source ne sera supprimé.");
        var files = download
            ? await adb.InventoryAsync(options.Serial, androidRoot, rules, ct)
            : await Task.Run(() => LocalInventory(localRoot, rules, ct), ct);
        var total = files.Sum(x => x.Size);
        if (download)
        {
            var drive = new DriveInfo(Path.GetPathRoot(localRoot)!);
            // A mounted volume on Unix may not be the filesystem root; Windows uses a drive root.
            if (OperatingSystem.IsWindows() && drive.AvailableFreeSpace < total)
                throw new IOException("Espace disque insuffisant pour la copie.");
        }
        log($"{files.Count} fichiers — {total / 1073741824.0:F2} Gio. Préparation des noms…");
        var journalDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroCp", "journals");
        Directory.CreateDirectory(journalDirectory);
        var journalPath = Path.Combine(journalDirectory, $"copie-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        await using var journal = new StreamWriter(journalPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        async Task Record(object value) => await journal.WriteLineAsync(JsonSerializer.Serialize(value));
        await Record(new { type = "start", time = DateTimeOffset.Now, options, files = files.Count, bytes = total });
        log("Journal : " + journalPath);
        var map = new Dictionary<string, string?> (StringComparer.Ordinal);
        var reserved = new HashSet<string>(download ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var policies = new Dictionary<string, DecisionKind>();
        var plan = new List<(SourceFile File, string Destination)>();
        var skipped = 0;

        // Enumerate each destination directory once, not once per candidate file.
        // This avoids quadratic local scans and one ADB existence query per file.
        var destinationDirectories = new Dictionary<string, Dictionary<string, (string Name, bool IsDirectory)>>(StringComparer.Ordinal);
        async Task<Dictionary<string, (string Name, bool IsDirectory)>> DestinationDirectory(string relative)
        {
            if (destinationDirectories.TryGetValue(relative, out var cached)) return cached;
            var comparer = download ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var contents = new Dictionary<string, (string Name, bool IsDirectory)>(comparer);
            if (download)
            {
                var directory = Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                CheckLocalParents(directory);
                if (Directory.Exists(directory))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        var isDirectory = (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
                        var name = Path.GetFileName(entry);
                        contents[name] = (name, isDirectory);
                    }
                }
                else if (LocalExists(directory)) throw new IOException("Un parent de destination n’est pas un dossier : " + directory);
            }
            else
            {
                foreach (var entry in await adb.ListDirectoryAsync(options.Serial,
                    androidRoot + (relative.Length > 0 ? "/" + relative : ""), ct))
                    contents[entry.Name] = entry;
            }
            destinationDirectories[relative] = contents;
            return contents;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var parts = file.RelativePath.Split('/');
            if (parts.Any(x => x is "" or "." or "..")) throw new IOException("Chemin source non sûr : " + file.RelativePath);
            string sourcePrefix = "", destinationPrefix = "";
            var skip = false;
            for (var index = 0; index < parts.Length; index++)
            {
                var directory = index < parts.Length - 1;
                sourcePrefix = Join(sourcePrefix, parts[index]);
                if (map.TryGetValue(sourcePrefix, out var mapped))
                {
                    if (mapped is null) { skip = true; break; }
                    destinationPrefix = mapped;
                    continue;
                }
                var name = parts[index];
                var reason = NamePolicy.Problem(name, download);
                var candidate = Join(destinationPrefix, name);
                // Existing directories are reused only with exact casing; files are never overwritten.
                async Task<bool> Collision(string path)
                {
                    if (reserved.Contains(path)) return true;
                    var separator = path.LastIndexOf('/');
                    var parent = separator < 0 ? "" : path[..separator];
                    var component = path[(separator + 1)..];
                    var contents = await DestinationDirectory(parent);
                    if (!contents.TryGetValue(component, out var existing)) return false;
                    return !(directory && existing.IsDirectory && (!download || existing.Name == component));
                }
                if (reason is null && await Collision(candidate)) reason = "Un autre élément porte déjà ce nom (ou ne diffère que par la casse).";
                while (reason is not null)
                {
                    var category = reason;
                    var suggested = NamePolicy.Suggest(name, download);
                    var number = 1;
                    var baseName = suggested;
                    while (await Collision(Join(destinationPrefix, suggested))) suggested = NamePolicy.Numbered(baseName, number++, directory);
                    NameDecision decision;
                    if (policies.TryGetValue(category, out var policy)) decision = new(policy, suggested);
                    else decision = await resolve(new(sourcePrefix, reason, suggested, directory));
                    if (decision.Kind == DecisionKind.Cancel) throw new OperationCanceledException("Copie annulée par l’utilisateur.", ct);
                    if (decision.ApplyToSimilar) policies[category] = decision.Kind;
                    if (decision.Kind == DecisionKind.Skip)
                    {
                        map[sourcePrefix] = null;
                        skip = true;
                        break;
                    }
                    name = decision.Name;
                    reason = NamePolicy.Problem(name, download);
                    candidate = Join(destinationPrefix, name);
                    if (reason is null && await Collision(candidate)) reason = "Un autre élément porte déjà ce nom (ou ne diffère que par la casse).";
                }
                if (skip) break;
                candidate = Join(destinationPrefix, name);
                reserved.Add(candidate);
                map[sourcePrefix] = candidate;
                if (name != parts[index]) await Record(new { type = "rename", original = sourcePrefix, destination = candidate });
                destinationPrefix = candidate;
            }
            if (skip)
            {
                skipped++;
                await Record(new { type = "skip", source = file.RelativePath });
            }
            else plan.Add((file, destinationPrefix));
        }
        log($"Copie de {plan.Count} fichiers. {skipped} ignorés.");
        var copied = 0;
        var failed = 0;
        long bytes = 0;
        foreach (var (file, destination) in plan)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new(copied + failed + skipped, files.Count, bytes, total, file.RelativePath));
            try
            {
                if (download)
                    await Download(options.Serial, androidRoot + "/" + file.RelativePath,
                        Path.Combine(localRoot, destination.Replace('/', Path.DirectorySeparatorChar)), file.Size, ct);
                else
                    await Upload(options.Serial, Path.Combine(localRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                        androidRoot + "/" + destination, file.Size, ct);
                copied++;
                bytes += file.Size;
                await Record(new { type = "copied", source = file.RelativePath, destination, bytes = file.Size });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                failed++;
                log($"ERREUR {file.RelativePath} : {ex.Message}");
                await Record(new { type = "error", source = file.RelativePath, error = ex.Message });
            }
        }
        progress.Report(new(files.Count, files.Count, bytes, total, "Terminé"));
        await Record(new { type = "end", copied, skipped, failed, time = DateTimeOffset.Now });
        return new(copied, skipped, failed, journalPath);
    }

    private static string Join(string parent, string child) => parent.Length == 0 ? child : parent + "/" + child;
    private static bool LocalExists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
    private static void CheckLocalParents(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (LocalExists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Destination/source contenant un lien ou une jonction : " + current);
    }
    private async Task Download(string serial, string remote, string target, long expected, CancellationToken ct)
    {
        CheckLocalParents(target);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".androcp-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await adb.DownloadAsync(serial, remote, temporary, ct);
            if (new FileInfo(temporary).Length != expected) throw new IOException("Taille différente de l’inventaire. La source a peut-être changé.");
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    private async Task Upload(string serial, string local, string remote, long expected, CancellationToken ct)
    {
        var parent = remote[..remote.LastIndexOf('/')];
        var temporary = parent + "/.androcp-" + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            // Reject remote symlink ancestors before writing outside the selected destination.
            var prefixes = remote.Split('/').Skip(1).SkipLast(1).Aggregate(new List<string>(), (list, item) =>
            {
                list.Add((list.LastOrDefault() ?? "") + "/" + item);
                return list;
            });
            var check = string.Join("; ", prefixes.Where(x => x != "/sdcard").Select(x =>
                $"if [ -L {AdbClient.Quote(x)} ]; then echo 'Lien symbolique Android interdit' >&2; exit 1; fi"));
            await adb.ShellAsync(serial, (check.Length > 0 ? check + "; " : "") + "mkdir -p " + AdbClient.Quote(parent), ct);
            await adb.RunAsync(["-s", serial, "push", local, temporary], ct);
            var size = (await adb.ShellAsync(serial, "stat -c %s " + AdbClient.Quote(temporary), ct)).Trim();
            if (size != expected.ToString(System.Globalization.CultureInfo.InvariantCulture) || new FileInfo(local).Length != expected)
                throw new IOException("Taille différente de l’inventaire.");
            var q = AdbClient.Quote(remote);
            // mv -n prevents overwriting a file created since preflight; test confirms the move happened.
            await adb.ShellAsync(serial, $"if [ -e {q} ] || [ -L {q} ]; then echo 'Destination déjà existante' >&2; exit 1; fi; mv -n {AdbClient.Quote(temporary)} {q} && test ! -e {AdbClient.Quote(temporary)}", ct);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await adb.ShellAsync(serial, "rm -f " + AdbClient.Quote(temporary), cleanup.Token); }
            catch (Exception) { /* A disconnected device may retain a .part file; never delete user files. */ }
        }
    }
}
