using AndroCp.Core;
using System.Diagnostics;

// Cross-platform fake adb for the packaging smoke test. ARFT calls adb pull directly.
if (args.Length > 0 && args[0] == "pull")
{
    if (Environment.GetEnvironmentVariable("ANDROID_SERIAL") != "smoke-serial")
        throw new Exception("ARFT n’a pas sélectionné le téléphone demandé.");
    File.WriteAllBytes(args[2], new byte[] { 0, 1, 2, 255 });
    Console.WriteLine("Fake ADB pull OK");
    return;
}
if (args.Length == 3 && args[0] == "--arft-smoke")
{
    var temporary = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp/opencode",
        "androcp-arft-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        using var worker = new ArftClient(Environment.ProcessPath!, "smoke-serial", Console.Error.WriteLine,
            CancellationToken.None, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        var destination = Path.Combine(temporary, "été.bin");
        await worker.DownloadAsync("/sdcard/été.bin", destination, 4, CancellationToken.None);
        if (!File.ReadAllBytes(destination).SequenceEqual(new byte[] { 0, 1, 2, 255 })) throw new Exception("Contenu ARFT incorrect.");
        var rejected = false;
        var wrong = Path.Combine(temporary, "wrong.bin");
        try { await worker.DownloadAsync("/sdcard/wrong.bin", wrong, 5, CancellationToken.None); }
        catch (IOException) { rejected = true; }
        if (!rejected || File.Exists(wrong)) throw new Exception("Validation des tailles ARFT incorrecte.");
        Console.WriteLine("ARFT smoke OK : C# → Python → ARFT → ADB simulé, binaire, Unicode, sélection et erreurs.");
    }
    finally { Directory.Delete(temporary, true); }
    return;
}

// A subprocess fixture exercises real byte transfers without any connected phone.
if (args.Length > 0 && args[0] == "-s" && !OperatingSystem.IsWindows())
{
    var fakeRoot = Environment.GetEnvironmentVariable("ANDROCP_FAKE_ROOT") ?? throw new Exception("Missing fake root");
    if (args[2] == "push")
    {
        if (Environment.GetEnvironmentVariable("ANDROCP_FAKE_FAIL_PUSH") == "1")
        {
            Console.Error.WriteLine("Transfert interrompu simulé");
            Environment.ExitCode = 23;
            return;
        }
        File.Copy(args[3], args[4].Replace("/sdcard", fakeRoot, StringComparison.Ordinal), overwrite: true);
        return;
    }
    var command = args[^1].Replace("/sdcard", fakeRoot, StringComparison.Ordinal);
    var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
    info.ArgumentList.Add("-c");
    info.ArgumentList.Add(command);
    using var process = Process.Start(info)!;
    await process.WaitForExitAsync();
    Environment.ExitCode = process.ExitCode;
    if (process.ExitCode == 0 && command.Contains("mv -n", StringComparison.Ordinal) &&
        Environment.GetEnvironmentVariable("ANDROCP_FAKE_FAIL_AFTER_COMMIT") == "1") Environment.ExitCode = 42;
    return;
}

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("ÉCHEC : " + name);
    count++;
    Console.WriteLine("OK " + name);
}
var rules = new CopyRules(["Android"], ["Android/media/com.whatsapp"]);
Check(!rules.Includes("Android/data/app/file"), "Exclure Android/data");
Check(!rules.Includes("Android/media/other/file"), "Exclure les autres médias");
Check(rules.Includes("Android/media/com.whatsapp/WhatsApp/Media/file.jpg"), "Exception WhatsApp");
Check(!rules.Includes("Android/media/com.whatsapp.other/file"), "Respecter les limites des chemins");
Check(rules.Includes("android/file"), "Filtres sensibles à la casse");
Check(rules.Includes(".hidden/file"), "Inclure les fichiers cachés");
Check(rules.CanTraverse("Android/media"), "Traverser les parents des exceptions");
Check(!rules.CanTraverse("Android/data"), "Ne pas parcourir les dossiers exclus");
Check(new CopyRules([], []).Includes("anything"), "Sans exclusion");
foreach (var name in new[] { "CON", "con.txt", "COM1.log", "LPT9", "AUX", "NUL", "bad:name", "a?b", "trail.", "trail ", "a\\b", "a\nb" })
    Check(NamePolicy.Problem(name, true) is not null, "Nom Windows refusé : " + name);
foreach (var name in new[] { "photo.jpg", "été 2026", ".nomedia", "CONSOLE.txt", "COM10" })
    Check(NamePolicy.Problem(name, true) is null, "Nom Windows accepté : " + name);
Check(NamePolicy.Problem("photo:été?.jpg", false) is null, "Noms Android plus permissifs");
Check(NamePolicy.Problem("../x", false) is not null, "Interdire les séparateurs dans un composant");
Check(NamePolicy.Problem("..", false) is not null, "Interdire la traversée de chemin");
Check(NamePolicy.Suggest("CON.txt", true) == "_CON.txt", "Proposition pour nom réservé");
Check(NamePolicy.Suggest("a:b?.jpg", true) == "a_b_.jpg", "Proposition caractères interdits");
Check(NamePolicy.Numbered("photo.jpg", 2, false) == "photo (2).jpg", "Suffixe avant extension");
Check(NamePolicy.Numbered("folder.ext", 2, true) == "folder.ext (2)", "Suffixe de dossier");
Check(AdbClient.Quote("a'b") == "'a'\"'\"'b'", "Échapper les apostrophes shell");
Check(AdbClient.ShellArguments("serial", "cat '/sdcard/file'").SequenceEqual(
    new[] { "-s", "serial", "shell", "-T", "-n", "cat '/sdcard/file'" }), "ADB binaire sans entrée, sans terminal, avec code de sortie distant");
foreach (var path in new[] { "/data/data", "/sdcard/../data", "/sdcard/./x", "relative" })
{
    var rejected = false;
    try { AdbClient.AndroidPath(path); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "Chemin Android refusé : " + path);
}
Check(AdbClient.AndroidPath("/sdcard/DCIM/") == "/sdcard/DCIM", "Normaliser chemin Android");
var temporaryRoot = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp/opencode";
var root = Path.Combine(temporaryRoot, "androcp-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Directory.CreateDirectory(Path.Combine(root, "empty"));
    Directory.CreateDirectory(Path.Combine(root, "Android", "data"));
    Directory.CreateDirectory(Path.Combine(root, "Android", "media", "com.whatsapp"));
    File.WriteAllText(Path.Combine(root, "Android", "data", "secret"), "excluded");
    File.WriteAllText(Path.Combine(root, "Android", "media", "com.whatsapp", "image.jpg"), "kept");
    File.WriteAllText(Path.Combine(root, ".hidden"), "hidden");
    File.WriteAllText(Path.Combine(root, "zero"), "");
    var queueOptions = new CopyOptions(CopyDirection.ComputerToAndroid, "queue-test", root, "/sdcard/test", [], [], true);
    var queueDirectory = Path.Combine(root, "queue-tests");
    var queueSource = new SourceFile("one.txt", 7, 123);
    string queuePath;
    using (var queue = new TransferQueue(queueOptions, queueDirectory))
    {
        queuePath = queue.FilePath;
        var entry = queue.Add(queueSource, "renamed.txt");
        Check(entry.Status == "queued", "Nouvelle entrée de reprise queued");
        queue.Save(entry with { Status = "pending" });
        var locked = false;
        try { using var concurrent = new TransferQueue(queueOptions, queueDirectory); }
        catch (IOException) { locked = true; }
        Check(locked, "Verrouiller une file contre deux copies concurrentes");
    }
    using (var queue = new TransferQueue(queueOptions, queueDirectory))
    {
        var entry = queue.Find(queueSource)!;
        Check(entry.Status == "retry" && entry.Destination == "renamed.txt", "pending devient retry au redémarrage, renommage conservé");
        entry = queue.Save(entry with { Status = "pending", Phase = "committing", Hash = "hash-test" });
        Check(entry.Status == "pending", "retry redevient pending avant traitement");
        queue.Save(entry with { Status = "completed" });
        var changed = false;
        try { queue.Find(queueSource with { ModifiedTicks = 124 }); } catch (IOException) { changed = true; }
        Check(changed, "Refuser la reprise d’une source modifiée");
    }
    File.AppendAllText(queuePath, "{\"partial");
    using (var queue = new TransferQueue(queueOptions, queueDirectory))
        Check(queue.Find(queueSource)!.Status == "completed", "Récupérer une file après événement partiellement écrit");
    using (var queue = new TransferQueue(queueOptions with { Serial = "other-phone" }, queueDirectory))
        Check(queue.FilePath != queuePath && !queue.Entries.Any(), "Isoler les files par téléphone");
    Directory.Delete(queueDirectory, true);
    var inventory = CopyEngine.LocalInventory(root, rules, CancellationToken.None);
    Check(inventory.Count == 3, "Inventaire : fichiers uniquement, exclusions appliquées");
    Check(inventory.Any(x => x.RelativePath == "zero" && x.Size == 0), "Conserver les fichiers vides");
    Check(inventory.Any(x => x.RelativePath == "Android/media/com.whatsapp/image.jpg"), "Préserver l’arborescence WhatsApp");
    if (!OperatingSystem.IsWindows())
    {
        File.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, ".hidden"));
        var rejected = false;
        try { CopyEngine.LocalInventory(root, rules, CancellationToken.None); } catch (IOException) { rejected = true; }
        Check(rejected, "Refuser de suivre les liens symboliques");
        File.Delete(Path.Combine(root, "link"));

        var fake = new AdbClient(Environment.ProcessPath!);
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", root);
        var listing = await fake.ListDirectoryAsync("fake", "/sdcard", CancellationToken.None);
        Check(listing.Any(x => x.Name == "Android" && x.IsDirectory) && listing.Any(x => x.Name == ".hidden" && !x.IsDirectory),
            "Lister un dossier Android, y compris les fichiers cachés");
        var remoteFailure = false;
        try { await fake.ShellAsync("fake", "printf 'erreur distante' >&2; exit 37", CancellationToken.None); }
        catch (IOException ex) { remoteFailure = ex.Message.Contains("37", StringComparison.Ordinal) && ex.Message.Contains("erreur distante", StringComparison.Ordinal); }
        Check(remoteFailure, "Propager les erreurs et codes de sortie Android");
        var remoteInventory = await fake.InventoryAsync("fake", "/sdcard", rules, CancellationToken.None);
        Check(remoteInventory.Count == 3, "Inventaire Android simulé : exclusions et exception");
        Directory.CreateDirectory(Path.Combine(root, "literal[ab]"));
        Directory.CreateDirectory(Path.Combine(root, "literala"));
        File.WriteAllText(Path.Combine(root, "literal[ab]", "x"), "excluded");
        File.WriteAllText(Path.Combine(root, "literala", "x"), "kept");
        var literalRules = new CopyRules(["Android", "literal[ab]"], ["Android/media/com.whatsapp"]);
        var literals = await fake.InventoryAsync("fake", "/sdcard", literalRules, CancellationToken.None);
        Check(literals.Any(x => x.RelativePath == "literala/x") && !literals.Any(x => x.RelativePath == "literal[ab]/x"), "Filtres littéraux sans expansion des caractères spéciaux");
        File.Delete(Path.Combine(root, "literal[ab]", "x"));
        Directory.Delete(Path.Combine(root, "literal[ab]"));
        File.Delete(Path.Combine(root, "literala", "x"));
        Directory.Delete(Path.Combine(root, "literala"));
        File.WriteAllText(Path.Combine(root, "bad:name.txt"), "rename me");
        Directory.CreateDirectory(Path.Combine(root, "bad:dir"));
        File.WriteAllText(Path.Combine(root, "bad:dir", "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "bad:dir", "b.txt"), "b");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, ".hidden"), "do not overwrite");
        var decisions = new List<NameProblem>();
        Task<NameDecision> Resolve(NameProblem problem)
        {
            decisions.Add(problem);
            return Task.FromResult(new NameDecision(DecisionKind.Rename, problem.Suggested));
        }
        var options = new CopyOptions(CopyDirection.AndroidToComputer, "fake", "/sdcard", destination,
            ["Android", "destination"], ["Android/media/com.whatsapp"]);
        var engine = new CopyEngine(fake);
        var result = await engine.CopyAsync(options, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(result.Copied == 6 && result.Failed == 0, "Copie Android vers PC simulée");
        Check(File.ReadAllText(Path.Combine(destination, ".hidden")) == "do not overwrite", "Aucun écrasement d’un fichier existant");
        Check(File.Exists(Path.Combine(destination, "bad_name.txt")), "Renommer un nom Windows interdit");
        Check(File.Exists(Path.Combine(destination, "bad_dir", "a.txt")) && File.Exists(Path.Combine(destination, "bad_dir", "b.txt")), "Renommage cohérent du dossier parent");
        Check(decisions.Count == 3, "Une décision par défaut, dossier parent traité une seule fois");
        Check(!Directory.Exists(Path.Combine(destination, "empty")), "Ne pas créer de dossier vide");
        Check(File.Exists(Path.Combine(destination, "Android", "media", "com.whatsapp", "image.jpg")), "Arborescence conservée à la copie");
        Check(!Directory.Exists(Path.Combine(destination, "Android", "data")), "Dossiers exclus absents");
        Check(!Directory.EnumerateFiles(destination, "*.part", SearchOption.AllDirectories).Any(), "Pas de fichier temporaire résiduel");
        var uploadRoot = Path.Combine(root, "upload-target");
        Directory.CreateDirectory(uploadRoot);
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", uploadRoot);
        var upload = await engine.CopyAsync(new(CopyDirection.ComputerToAndroid, "fake", destination, "/sdcard", [], []),
            Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(upload.Failed == 0 && upload.Copied == 7, "Copie PC vers Android simulée");
        Check(File.ReadAllText(Path.Combine(uploadRoot, "bad_name.txt")) == "rename me", "Vérifier le contenu transféré");
        Check(File.ReadAllText(Path.Combine(root, "bad:name.txt")) == "rename me", "Source inchangée");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var cancelled = false;
        try { await fake.InventoryAsync("fake", "/sdcard", rules, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Annulation de l’inventaire");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", null);

        var resumeSource = Path.Combine(root, "resume-source");
        var resumeTarget = Path.Combine(root, "resume-target");
        Directory.CreateDirectory(resumeSource);
        Directory.CreateDirectory(resumeTarget);
        File.WriteAllText(Path.Combine(resumeSource, "one.txt"), "payload");
        var resumeOptions = new CopyOptions(CopyDirection.ComputerToAndroid, "resume-device", resumeSource, "/sdcard", [], [], true);
        var resumeQueues = Path.Combine(root, "resume-queues");
        var resumeEngine = new CopyEngine(fake, resumeQueues);
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", resumeTarget);
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_FAIL_PUSH", "1");
        var failed = await resumeEngine.CopyAsync(resumeOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(failed.Failed == 1 && !File.Exists(Path.Combine(resumeTarget, "one.txt")), "Échec PC vers Android : pas de fichier final partiel");
        var events = Directory.EnumerateFiles(resumeQueues).Single();
        Check(File.ReadAllText(events).Contains("\"Status\":\"pending\"", StringComparison.Ordinal), "Échec enregistré durablement pending");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_FAIL_PUSH", null);
        var resumed = await resumeEngine.CopyAsync(resumeOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(resumed.Copied == 1 && resumed.Failed == 0, "Rejouer depuis zéro le fichier en retry");
        Check(File.ReadAllText(events).Contains("\"Status\":\"retry\"", StringComparison.Ordinal), "Transition pending vers retry persistée");
        var complete = await resumeEngine.CopyAsync(resumeOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(complete.Copied == 0 && complete.Skipped == 1, "Ne pas retransférer un fichier completed valide");
        File.WriteAllText(Path.Combine(resumeTarget, "one.txt"), "CHANGED");
        var conflict = await resumeEngine.CopyAsync(resumeOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(conflict.Failed == 1 && File.ReadAllText(Path.Combine(resumeTarget, "one.txt")) == "CHANGED", "Préserver une destination modifiée après copie");
        var commitOptions = resumeOptions with { Destination = "/sdcard/commit" };
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_FAIL_AFTER_COMMIT", "1");
        var committed = await resumeEngine.CopyAsync(commitOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(committed.Failed == 1 && File.Exists(Path.Combine(resumeTarget, "commit", "one.txt")), "Simuler perte de réponse après validation Android");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_FAIL_AFTER_COMMIT", null);
        var recovered = await resumeEngine.CopyAsync(commitOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(recovered.Copied == 0 && recovered.Skipped == 1, "Récupérer le commit interrompu par vérification SHA-256 sans doublon");

        var pullTarget = Path.Combine(root, "resume-pull");
        Directory.CreateDirectory(pullTarget);
        File.WriteAllText(Path.Combine(pullTarget, "one.txt"), "foreign-file");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", resumeSource);
        var pullOptions = new CopyOptions(CopyDirection.AndroidToComputer, "resume-pull", "/sdcard", pullTarget, [], [], true);
        var pullFirst = await resumeEngine.CopyAsync(pullOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        var decisionCount = decisions.Count;
        var pullAgain = await resumeEngine.CopyAsync(pullOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(pullFirst.Copied == 1 && pullAgain.Skipped == 1 && decisions.Count == decisionCount, "Reprise descendante conserve les décisions de renommage");
        Check(File.ReadAllText(Path.Combine(pullTarget, "one.txt")) == "foreign-file", "Reprise descendante ne remplace pas un fichier étranger");

        var spaceSource = Path.Combine(root, "space-source");
        var spaceTarget = Path.Combine(root, "space-target");
        var spaceQueues = Path.Combine(root, "space-queues");
        Directory.CreateDirectory(spaceSource);
        Directory.CreateDirectory(spaceTarget);
        File.WriteAllText(Path.Combine(spaceSource, "a.txt"), "aaaa");
        File.WriteAllText(Path.Combine(spaceSource, "b.txt"), "bbbb");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", spaceSource);
        var spaceOptions = new CopyOptions(CopyDirection.AndroidToComputer, "space-device", "/sdcard", spaceTarget, [], [], true);
        var blocked = false;
        try
        {
            await new CopyEngine(fake, spaceQueues, _ => CopyEngine.SpaceHeadroom + 4).CopyAsync(
                spaceOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        }
        catch (IOException ex) { blocked = ex.Message.Contains("Poursuivre", StringComparison.Ordinal); }
        Check(blocked && !Directory.EnumerateFiles(spaceTarget).Any(), "Mode strict : expliquer le refus si tout le volume restant ne tient pas");
        var spaceChecks = 0;
        var partial = await new CopyEngine(fake, spaceQueues, _ => ++spaceChecks == 1 ? CopyEngine.SpaceHeadroom + 4 : CopyEngine.SpaceHeadroom).CopyAsync(
            spaceOptions with { AllowPartialSpace = true }, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(partial.Copied == 1 && partial.PausedForSpace && partial.Failed == 0, "Mode espace disponible : avancer puis se mettre en pause sans faux succès");
        Check(Directory.EnumerateFiles(spaceTarget).Count() == 1 && !Directory.EnumerateFiles(spaceTarget, "*.part").Any(),
            "Pause espace : fichier terminé conservé, aucun temporaire partiel");
        using (var queue = new TransferQueue(spaceOptions, spaceQueues))
            Check(queue.Entries.Count(x => x.Status == "completed") == 1 && queue.Entries.Count(x => x.Status == "retry") == 1,
                "Pause espace : état pending durable et récupérable en retry");
        var stillFull = await new CopyEngine(fake, spaceQueues, _ => 0).CopyAsync(
            spaceOptions with { AllowPartialSpace = true }, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(stillFull.Skipped == 1 && stillFull.Copied == 0 && stillFull.PausedForSpace,
            "Valider un fichier déjà terminé même sans espace libre");
        var afterFreeing = await new CopyEngine(fake, spaceQueues, _ => CopyEngine.SpaceHeadroom + 4).CopyAsync(
            spaceOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(afterFreeing.Copied == 1 && afterFreeing.Skipped == 1 && !afterFreeing.PausedForSpace,
            "Après libération : seule la taille restante est nécessaire, pas une seconde copie complète");
        var allComplete = await new CopyEngine(fake, spaceQueues, _ => 0).CopyAsync(
            spaceOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(allComplete.Copied == 0 && allComplete.Skipped == 2 && !allComplete.PausedForSpace,
            "Reprise entièrement terminée sans espace supplémentaire pour les données");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", null);
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"\n{count} tests réussis.");
