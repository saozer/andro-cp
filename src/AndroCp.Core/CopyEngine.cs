using System.Text;
using System.Text.Json;

namespace AndroCp.Core;

public sealed class CopyEngine(AdbClient adb, string? queueDirectory = null, Func<string, long>? availableSpace = null)
{
    public const long SpaceHeadroom = 8 * 1024 * 1024;
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
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Lien symbolique non pris en charge : " + entry);
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
        if (options.AllowPartialSpace && !options.EnableResume) throw new ArgumentException("Le mode espace disponible exige une file de reprise.");
        var source = new CopyStorage(adb, options.SourceEndpoint, availableSpace);
        var target = new CopyStorage(adb, options.DestinationEndpoint, availableSpace);
        var sourceCanonical = await source.ValidateAsync(source: true, ct);
        var targetCanonical = await target.ValidateAsync(source: false, ct);
        var sameDevice = source.IsAndroid == target.IsAndroid && source.Serial == target.Serial;
        if (sameDevice) CopyPathSafety.RejectOverlapping(sourceCanonical, targetCanonical, !source.IsAndroid && OperatingSystem.IsWindows());
        using var queue = options.EnableResume ? new TransferQueue(options, queueDirectory) : null;
        if (queue is not null) log("File de reprise : " + queue.FilePath + " — les pending ont été passés en retry.");
        var rules = new CopyRules(options.Exclusions, options.Exceptions);
        log("Inventaire en cours… Aucun fichier source ne sera supprimé.");
        var files = source.IsAndroid
            ? await adb.InventoryAsync(source.Serial!, source.Root, rules, ct)
            : await Task.Run(() => LocalInventory(source.Root, rules, ct), ct);
        log($"{files.Count} fichiers — {files.Sum(x => x.Size) / 1073741824.0:F2} Gio. Préparation des noms…");
        var journalDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroCp", "journals");
        Directory.CreateDirectory(journalDirectory);
        var journalPath = Path.Combine(journalDirectory, $"copie-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        await using var journal = new StreamWriter(journalPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        async Task Record(object value) => await journal.WriteLineAsync(JsonSerializer.Serialize(value));
        await Record(new { type = "start", time = DateTimeOffset.Now, options, files = files.Count });
        log("Journal : " + journalPath);
        var windowsNames = !target.IsAndroid;
        var comparer = windowsNames ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        var reserved = new HashSet<string>(comparer);
        var policies = new Dictionary<string, DecisionKind>();
        var plan = new List<(SourceFile File, string Destination)>();
        var skipped = 0;
        if (queue is not null)
        {
            foreach (var entry in queue.Entries)
            {
                CopyPathSafety.CheckRelative(entry.Source);
                CopyPathSafety.CheckRelative(entry.Destination);
                var original = entry.Source.Split('/');
                var destination = entry.Destination.Split('/');
                if (original.Length != destination.Length || destination.Any(x => NamePolicy.Problem(x, windowsNames) is not null))
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
        var directoryCache = new Dictionary<string, Dictionary<string, (string Name, bool IsDirectory)>>(StringComparer.Ordinal);
        async Task<Dictionary<string, (string Name, bool IsDirectory)>> DestinationDirectory(string relative)
        {
            if (directoryCache.TryGetValue(relative, out var cached)) return cached;
            var contents = new Dictionary<string, (string Name, bool IsDirectory)>(comparer);
            foreach (var entry in await target.ListAsync(relative, ct)) contents[entry.Name] = entry;
            directoryCache[relative] = contents;
            return contents;
        }
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            CopyPathSafety.CheckRelative(file.RelativePath);
            var previous = queue?.Find(file);
            if (previous is not null) { plan.Add((file, previous.Destination)); continue; }
            var parts = file.RelativePath.Split('/');
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
                async Task<bool> Collision(string path)
                {
                    if (reserved.Contains(path)) return true;
                    var (parent, component) = Split(path);
                    var contents = await DestinationDirectory(parent);
                    if (!contents.TryGetValue(component, out var existing)) return false;
                    return !(directory && existing.IsDirectory && (!windowsNames || existing.Name == component));
                }
                var reason = NamePolicy.Problem(name, windowsNames);
                if (reason is null && await Collision(Join(destinationPrefix, name))) reason = "Un autre élément porte déjà ce nom (ou ne diffère que par la casse).";
                while (reason is not null)
                {
                    var category = reason;
                    var suggested = NamePolicy.Suggest(name, windowsNames);
                    var number = 1;
                    var baseName = suggested;
                    while (await Collision(Join(destinationPrefix, suggested))) suggested = NamePolicy.Numbered(baseName, number++, directory);
                    var decision = policies.TryGetValue(category, out var policy) ? new NameDecision(policy, suggested)
                        : await resolve(new(sourcePrefix, reason, suggested, directory));
                    if (decision.Kind == DecisionKind.Cancel) throw new OperationCanceledException("Copie annulée par l’utilisateur.", ct);
                    if (decision.ApplyToSimilar) policies[category] = decision.Kind;
                    if (decision.Kind == DecisionKind.Skip) { map[sourcePrefix] = null; skip = true; break; }
                    name = decision.Name;
                    reason = NamePolicy.Problem(name, windowsNames);
                    if (reason is null && await Collision(Join(destinationPrefix, name))) reason = "Un autre élément porte déjà ce nom (ou ne diffère que par la casse).";
                }
                if (skip) break;
                var candidate = Join(destinationPrefix, name);
                reserved.Add(candidate);
                map[sourcePrefix] = candidate;
                if (name != parts[index]) await Record(new { type = "rename", original = sourcePrefix, destination = candidate });
                destinationPrefix = candidate;
            }
            if (skip) { skipped++; await Record(new { type = "skip", source = file.RelativePath }); }
            else { plan.Add((file, destinationPrefix)); queue?.Add(file, destinationPrefix); }
        }
        var total = plan.Sum(x => x.File.Size);
        log($"Copie de {plan.Count} fichiers. {skipped} ignorés. Mode : {options.Direction}" + (sameDevice && source.IsAndroid ? " (cp local Android)." : "."));
        if (!options.AllowPartialSpace)
        {
            long required = 0;
            foreach (var (file, destination) in plan)
            {
                var entry = queue?.Find(file);
                var (parent, component) = Split(destination);
                var contents = await DestinationDirectory(parent);
                var reusable = entry is not null && (entry.Status == "completed" || entry.Phase == "committing") &&
                    contents.TryGetValue(component, out var existing) && !existing.IsDirectory;
                if (!reusable) required += file.Size;
            }
            if (required > 0 && await target.FreeBytesAsync(ct) < required + SpaceHeadroom)
                throw new IOException("Espace insuffisant pour tous les fichiers restants. Activez « Poursuivre avec l’espace disponible » pour avancer fichier par fichier.");
        }
        var copied = 0;
        var failed = 0;
        var pausedForSpace = false;
        long bytes = 0, transferred = 0;
        foreach (var (file, destination) in plan)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new(copied + failed + skipped, files.Count, bytes, total, file.RelativePath, transferred));
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = queue?.Find(file);
                if (entry is not null && (entry.Status == "completed" || entry.Phase == "committing"))
                {
                    if (await target.ExistsAsync(destination, ct) && entry.Hash is not null && await target.HashAsync(destination, ct) == entry.Hash)
                    {
                        if (entry.Status != "completed") queue!.Save(entry with { Status = "completed" });
                        skipped++; bytes += file.Size;
                        await Record(new { type = "resumed", source = file.RelativePath, destination });
                        continue;
                    }
                    if (await target.ExistsAsync(destination, ct)) throw new IOException("Destination de reprise modifiée : aucun écrasement autorisé. Choisissez une nouvelle destination.");
                }
                if (entry is not null) entry = queue!.Save(entry with { Status = "pending", Phase = "transfer", Hash = null });
                var (parent, _) = Split(destination);
                var staging = Join(parent, ".androcp-" + (entry?.Id ?? Guid.NewGuid().ToString("N")) + ".part");
                try
                {
                    await source.CheckSourceAsync(file, ct);
                    var free = await target.FreeBytesAsync(ct);
                    if (free < file.Size + SpaceHeadroom)
                    {
                        // An interrupted staging file may itself consume the space we need.
                        await target.CleanupAsync(staging);
                        free = await target.FreeBytesAsync(ct);
                    }
                    if (free < file.Size + SpaceHeadroom)
                    {
                        pausedForSpace = true;
                        log($"PAUSE : {file.RelativePath} exige {file.Size / 1048576.0:F1} Mio + 8 Mio de marge ; {free / 1048576.0:F1} Mio disponibles.");
                        await Record(new { type = "paused_space", source = file.RelativePath });
                        break;
                    }
                    await target.PrepareAsync(destination, staging, ct);
                    var hash = await source.HashAsync(file.RelativePath, ct);
                    await TransferAsync(source, target, file.RelativePath, staging, sameDevice, ct);
                    await source.CheckSourceAsync(file, ct);
                    if ((await target.MetadataAsync(staging, ct)).Size != file.Size || await target.HashAsync(staging, ct) != hash)
                        throw new IOException("Validation du transfert en échec : taille ou SHA-256 différent.");
                    if (entry is not null) entry = queue!.Save(entry with { Phase = "committing", Hash = hash });
                    await target.CommitAsync(staging, destination, ct);
                    if (entry is not null) queue!.Save(entry with { Status = "completed" });
                }
                finally { await target.CleanupAsync(staging); }
                copied++; bytes += file.Size; transferred += file.Size;
                await Record(new { type = "copied", source = file.RelativePath, destination, bytes = file.Size });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                failed++;
                log($"ERREUR {file.RelativePath} : {ex.Message}");
                await Record(new { type = "error", source = file.RelativePath, error = ex.Message });
            }
        }
        progress.Report(new(copied + skipped + failed, files.Count, bytes, total, pausedForSpace ? "Pause : espace insuffisant" : "Terminé", transferred));
        await Record(new { type = "end", copied, skipped, failed, pausedForSpace, time = DateTimeOffset.Now });
        return new(copied, skipped, failed, journalPath, pausedForSpace);
    }
    private async Task TransferAsync(CopyStorage source, CopyStorage target, string relative, string staging, bool sameDevice, CancellationToken ct)
    {
        if (source.IsAndroid && target.IsAndroid)
        {
            if (sameDevice) await adb.ShellAsync(source.Serial!, "cp -n " + AdbClient.Quote(source.At(relative)) + " " + AdbClient.Quote(target.At(staging)), ct);
            else await adb.RelayAsync(source.Serial!, source.At(relative), target.Serial!, target.At(staging), ct);
        }
        else if (source.IsAndroid) await adb.DownloadAsync(source.Serial!, source.At(relative), target.At(staging), ct);
        else if (target.IsAndroid) await adb.RunAsync(["-s", target.Serial!, "push", source.At(relative), target.At(staging)], ct);
        else
        {
            await using var input = File.OpenRead(source.At(relative));
            await using var output = new FileStream(target.At(staging), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await input.CopyToAsync(output, 1024 * 1024, ct);
        }
    }
    private static string Join(string parent, string child) => parent.Length == 0 ? child : parent + "/" + child;
    private static (string Parent, string Component) Split(string path)
    {
        var separator = path.LastIndexOf('/');
        return (separator < 0 ? "" : path[..separator], path[(separator + 1)..]);
    }
}
