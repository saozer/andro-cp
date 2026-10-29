using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace AndroCp.Core;

public sealed class CopyEngine(AdbClient adb, string? queueDirectory = null, Func<string, long>? availableSpace = null)
{
    public const long SpaceHeadroom = 8 * 1024 * 1024;
    private sealed class CopySpaceException(string message) : IOException(message);
    private long? AvailableSpace(string path) => availableSpace is not null ? availableSpace(path) :
        OperatingSystem.IsWindows() ? new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace : null;
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
                    files.Add(new(relative, new FileInfo(entry).Length, File.GetLastWriteTimeUtc(entry).Ticks));
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
        if (options.AllowPartialSpace && (!options.EnableResume || !download))
            throw new ArgumentException("Le mode espace disponible exige une reprise Android → PC.");
        var androidRoot = AdbClient.AndroidPath(download ? options.Source : options.Destination);
        var localRoot = Path.GetFullPath(download ? options.Destination : options.Source);
        if (download && !Directory.Exists(localRoot)) throw new IOException("Choisis un dossier de destination existant.");
        if (!Directory.Exists(localRoot)) throw new IOException("Dossier local introuvable.");
        CheckLocalParents(localRoot);
        using var queue = options.EnableResume ? new TransferQueue(options, queueDirectory) : null;
        if (queue is not null) log("File de reprise : " + queue.FilePath + " — les pending ont été passés en retry.");
        var rules = new CopyRules(options.Exclusions, options.Exceptions);
        log("Inventaire en cours… Aucun fichier source ne sera supprimé.");
        var files = download
            ? await adb.InventoryAsync(options.Serial, androidRoot, rules, ct)
            : await Task.Run(() => LocalInventory(localRoot, rules, ct), ct);
        var total = files.Sum(x => x.Size);
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
        if (queue is not null)
        {
            // Preserve previous per-component rename decisions, including parent folders.
            foreach (var entry in queue.Entries)
            {
                var original = entry.Source.Split('/');
                var destination = entry.Destination.Split('/');
                if (original.Length != destination.Length || destination.Any(x => NamePolicy.Problem(x, download) is not null))
                    throw new IOException("Chemin invalide dans la file de reprise.");
                for (var i = 1; i < original.Length; i++)
                {
                    var from = string.Join('/', original.Take(i));
                    var to = string.Join('/', destination.Take(i));
                    map[from] = to;
                    reserved.Add(to);
                }
                reserved.Add(entry.Destination);
            }
        }

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
            var previous = queue?.Find(file);
            if (previous is not null)
            {
                plan.Add((file, previous.Destination));
                continue;
            }
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
            else
            {
                plan.Add((file, destinationPrefix));
                queue?.Add(file, destinationPrefix);
            }
        }
        log($"Copie de {plan.Count} fichiers. {skipped} ignorés.");
        total = plan.Sum(x => x.File.Size);
        if (download && !options.AllowPartialSpace && AvailableSpace(localRoot) is { } freeSpace)
        {
            long required = 0;
            foreach (var (file, destination) in plan)
            {
                var entry = queue?.Find(file);
                var target = Path.Combine(localRoot, destination.Replace('/', Path.DirectorySeparatorChar));
                var reusable = entry is not null && (entry.Status == "completed" || entry.Phase == "committing") &&
                    File.Exists(target) && new FileInfo(target).Length == file.Size;
                if (!reusable) required += file.Size;
            }
            if (required > 0 && freeSpace < required + SpaceHeadroom)
                throw new IOException("Espace disque insuffisant pour tous les fichiers restants. Dans la variante ARFT, activez « Poursuivre avec l’espace disponible » pour avancer fichier par fichier.");
        }
        if (options.AllowPartialSpace) log("Mode espace disponible : contrôle avant chaque fichier, arrêt propre si le suivant ne tient pas. Marge : 8 Mio.");
        var copied = 0;
        var failed = 0;
        var pausedForSpace = false;
        long bytes = 0;
        long transferredBytes = 0;
        using var arft = options.UseArft && download ? new ArftClient(adb.Executable, options.Serial, log, ct) : null;
        foreach (var (file, destination) in plan)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new(copied + failed + skipped, files.Count, bytes, total, file.RelativePath, transferredBytes));
            try
            {
                var entry = queue?.Find(file);
                var local = Path.Combine(localRoot, (download ? destination : file.RelativePath).Replace('/', Path.DirectorySeparatorChar));
                var remote = androidRoot + "/" + (download ? file.RelativePath : destination);
                if (entry is not null && (entry.Status == "completed" || entry.Phase == "committing"))
                {
                    if (await MatchesCompleted(options.Serial, download, local, androidRoot + "/" + destination, entry, ct))
                    {
                        if (entry.Status != "completed") queue!.Save(entry with { Status = "completed" });
                        skipped++;
                        bytes += file.Size;
                        await Record(new { type = "resumed", source = file.RelativePath, destination });
                        continue;
                    }
                    var exists = download ? LocalExists(local) : await adb.ExistsAsync(options.Serial, remote, ct);
                    if (exists) throw new IOException("Destination de reprise modifiée ou en conflit : aucun écrasement autorisé. Choisissez une nouvelle destination.");
                }
                if (entry is not null) entry = queue!.Save(entry with { Status = "pending", Phase = "transfer", Hash = null });
                void BeforeCommit(string hash)
                {
                    if (entry is not null) entry = queue!.Save(entry with { Phase = "committing", Hash = hash });
                }
                if (download)
                    await Download(options.Serial, remote, local, file.Size, arft, entry?.Id, entry is null ? null : BeforeCommit, ct);
                else
                    await Upload(options.Serial, local, remote, file, entry?.Id, entry is null ? null : BeforeCommit, ct);
                if (entry is not null) queue!.Save(entry with { Status = "completed" });
                copied++;
                bytes += file.Size;
                transferredBytes += file.Size;
                await Record(new { type = "copied", source = file.RelativePath, destination, bytes = file.Size });
            }
            catch (CopySpaceException ex)
            {
                pausedForSpace = true;
                log("PAUSE : " + ex.Message + " Les fichiers terminés sont conservés ; libérez de l’espace puis relancez avec les mêmes paramètres.");
                await Record(new { type = "paused_space", source = file.RelativePath, reason = ex.Message });
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                failed++;
                log($"ERREUR {file.RelativePath} : {ex.Message}");
                await Record(new { type = "error", source = file.RelativePath, error = ex.Message });
            }
        }
        progress.Report(new(copied + skipped + failed, files.Count, bytes, total, pausedForSpace ? "Pause : espace insuffisant" : "Terminé", transferredBytes));
        await Record(new { type = "end", copied, skipped, failed, pausedForSpace, time = DateTimeOffset.Now });
        return new(copied, skipped, failed, journalPath, pausedForSpace);
    }

    private static string Join(string parent, string child) => parent.Length == 0 ? child : parent + "/" + child;
    private static bool LocalExists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
    private static void CheckLocalParents(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (LocalExists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Destination/source contenant un lien ou une jonction : " + current);
    }
    private static async Task<string> HashLocal(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
    }
    private async Task<bool> MatchesCompleted(string serial, bool download, string local, string remote, QueueEntry entry, CancellationToken ct)
    {
        if (entry.Hash is null) return false;
        if (download)
        {
            CheckLocalParents(local);
            return File.Exists(local) && new FileInfo(local).Length == entry.Size && await HashLocal(local, ct) == entry.Hash;
        }
        var q = AdbClient.Quote(remote);
        var result = (await adb.ShellAsync(serial,
            $"if [ -f {q} ] && [ ! -L {q} ]; then sha256sum < {q}; fi", ct)).Trim();
        return result.StartsWith(entry.Hash + " ", StringComparison.OrdinalIgnoreCase);
    }
    private async Task Download(string serial, string remote, string target, long expected, ArftClient? arft,
        string? stagingId, Action<string>? beforeCommit, CancellationToken ct)
    {
        CheckLocalParents(target);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".androcp-" + (stagingId ?? Guid.NewGuid().ToString("N")) + ".part");
        try
        {
            if (stagingId is not null)
            {
                CheckLocalParents(temporary);
                CheckLocalParents(temporary + ".part");
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(temporary + ".part")) File.Delete(temporary + ".part");
            }
            // Completed files are checked before reaching this point. Reclaim only
            // this queue entry's own stale staging files before checking free space.
            if (AvailableSpace(directory) is { } free && free < expected + SpaceHeadroom)
                throw new CopySpaceException($"Le fichier {Path.GetFileName(target)} exige {expected / 1048576.0:F1} Mio + 8 Mio de marge ; {free / 1048576.0:F1} Mio disponibles.");
            if (arft is null) await adb.DownloadAsync(serial, remote, temporary, ct);
            else await arft.DownloadAsync(remote, temporary, expected, ct);
            if (new FileInfo(temporary).Length != expected) throw new IOException("Taille différente de l’inventaire. La source a peut-être changé.");
            if (beforeCommit is not null) beforeCommit(await HashLocal(temporary, ct));
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporary + ".part")) File.Delete(temporary + ".part");
        }
    }
    private async Task Upload(string serial, string local, string remote, SourceFile source, string? stagingId,
        Action<string>? beforeCommit, CancellationToken ct)
    {
        var expected = source.Size;
        var parent = remote[..remote.LastIndexOf('/')];
        var temporary = parent + "/.androcp-" + (stagingId ?? Guid.NewGuid().ToString("N")) + ".part";
        try
        {
            if (new FileInfo(local).Length != expected || File.GetLastWriteTimeUtc(local).Ticks != source.ModifiedTicks)
                throw new IOException("Source modifiée depuis l’inventaire.");
            var hash = beforeCommit is not null ? await HashLocal(local, ct) : null;
            // Reject remote symlink ancestors before writing outside the selected destination.
            var prefixes = remote.Split('/').Skip(1).SkipLast(1).Aggregate(new List<string>(), (list, item) =>
            {
                list.Add((list.LastOrDefault() ?? "") + "/" + item);
                return list;
            });
            var check = string.Join("; ", prefixes.Where(x => x != "/sdcard").Select(x =>
                $"if [ -L {AdbClient.Quote(x)} ]; then echo 'Lien symbolique Android interdit' >&2; exit 1; fi"));
            await adb.ShellAsync(serial, (check.Length > 0 ? check + "; " : "") + "mkdir -p " + AdbClient.Quote(parent), ct);
            await adb.ShellAsync(serial, $"if [ -L {AdbClient.Quote(temporary)} ] || [ -d {AdbClient.Quote(temporary)} ]; then echo 'Conflit sur le staging' >&2; exit 1; fi", ct);
            await adb.RunAsync(["-s", serial, "push", local, temporary], ct);
            var size = (await adb.ShellAsync(serial, "stat -c %s " + AdbClient.Quote(temporary), ct)).Trim();
            if (size != expected.ToString(System.Globalization.CultureInfo.InvariantCulture) || new FileInfo(local).Length != expected ||
                File.GetLastWriteTimeUtc(local).Ticks != source.ModifiedTicks)
                throw new IOException("Taille différente de l’inventaire.");
            if (hash is not null) beforeCommit!(hash);
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
