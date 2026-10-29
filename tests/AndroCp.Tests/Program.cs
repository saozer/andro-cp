using AndroCp.Core;
using System.Diagnostics;

if (args.Length > 0 && args[0] == "devices")
{
    Console.WriteLine("List of devices attached");
    Console.Write(Environment.GetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST") ?? "");
    return;
}

// A subprocess fixture exercises real byte transfers without any connected phone.
if (args.Length > 0 && args[0] == "-s" && !OperatingSystem.IsWindows())
{
    var roots = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Environment.GetEnvironmentVariable("ANDROCP_FAKE_ROOTS") ?? "{}");
    var fakeRoot = roots!.TryGetValue(args[1], out var configured) ? configured : Environment.GetEnvironmentVariable("ANDROCP_FAKE_ROOT") ?? throw new Exception("Missing fake root");
    var trace = Environment.GetEnvironmentVariable("ANDROCP_FAKE_TRACE");
    if (trace is not null) File.WriteAllText(Path.Combine(trace, Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".json"), System.Text.Json.JsonSerializer.Serialize(args));
    if (args[2] == "push")
    {
        if (Environment.GetEnvironmentVariable("ANDROCP_FAKE_SLOW_IO") == "push" && Path.GetFileName(args[3]) == "b.txt")
        {
            File.WriteAllText(args[4].Replace("/sdcard", fakeRoot, StringComparison.Ordinal), "partial");
            File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("ANDROCP_FAKE_SIGNAL_DIRECTORY")!, Environment.ProcessId.ToString()), "started");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (Environment.GetEnvironmentVariable("ANDROCP_FAKE_FAIL_PUSH") == "1")
        {
            Console.Error.WriteLine("Transfert interrompu simulé");
            Environment.ExitCode = 23;
            return;
        }
        File.Copy(args[3], args[4].Replace("/sdcard", fakeRoot, StringComparison.Ordinal), overwrite: true);
        return;
    }
    var command = args[^1].Replace("/storage/emulated/0", fakeRoot, StringComparison.Ordinal).Replace("/sdcard", fakeRoot, StringComparison.Ordinal);
    if (Environment.GetEnvironmentVariable("ANDROCP_FAKE_SLOW_IO") == "cp" && command.StartsWith("cp -n ", StringComparison.Ordinal) && command.Contains("/b.txt", StringComparison.Ordinal))
    {
        File.WriteAllText(command.Split('\'')[3], "partial");
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("ANDROCP_FAKE_SIGNAL_DIRECTORY")!, Environment.ProcessId.ToString()), "started");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    if (Environment.GetEnvironmentVariable("ANDROCP_FAKE_SLOW_IO") == "download" && command.StartsWith("cat ", StringComparison.Ordinal) && command.Contains("/b.txt", StringComparison.Ordinal))
    {
        var output = Console.OpenStandardOutput();
        output.Write(new byte[] { 0, 1, 2, 3 });
        output.Flush();
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("ANDROCP_FAKE_SIGNAL_DIRECTORY")!, Environment.ProcessId.ToString()), "started");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    if (command.Contains("androcp-access-ok", StringComparison.Ordinal))
    {
        if (Environment.GetEnvironmentVariable("ANDROCP_FAKE_FAIL_PROBE") == "1")
        {
            Console.Error.WriteLine("Accès shell refusé simulé");
            Environment.ExitCode = 23;
            return;
        }
        if (Environment.GetEnvironmentVariable("ANDROCP_FAKE_BAD_PROBE") == "1")
        {
            Console.WriteLine("Réponse inattendue");
            return;
        }
    }
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
Check(!DeviceConnection.NoSelection(false).CanCopy && !DeviceConnection.NoSelection(true).CanCopy && !DeviceConnection.Checking.CanCopy,
    "Pas de copie sans sélection ou pendant la vérification ADB");
var timing = new CopyTimeEstimator();
Check(timing.EstimateRemaining(TimeSpan.FromSeconds(1)) is null, "Pas d’estimation avant les premiers transferts");
timing.Update(new(0, 3, 0, 300, "préparation", 0), TimeSpan.FromSeconds(5));
timing.Update(new(1, 3, 100, 300, "suite", 100), TimeSpan.FromSeconds(15));
Check(timing.EstimateRemaining(TimeSpan.FromSeconds(15)) == TimeSpan.FromSeconds(20), "Estimer le temps restant à partir des fichiers réellement transférés");
Check(timing.EstimateRemaining(TimeSpan.FromSeconds(46)) is null, "Ne pas conserver une estimation devenue trop ancienne");
var resumeTiming = new CopyTimeEstimator();
resumeTiming.Update(new(1, 5, 200, 500, "réutilisé", 0), TimeSpan.FromSeconds(5));
Check(resumeTiming.EstimateRemaining(TimeSpan.FromSeconds(5)) is null, "Un fichier réutilisé ne gonfle pas le débit réseau de reprise");
resumeTiming.Update(new(2, 5, 300, 500, "transféré", 100), TimeSpan.FromSeconds(15));
Check(resumeTiming.EstimateRemaining(TimeSpan.FromSeconds(15)) == TimeSpan.FromSeconds(20), "La reprise distingue volume déjà présent et volume réellement copié");
resumeTiming.Update(new(5, 5, 500, 500, "Terminé", 300), TimeSpan.FromSeconds(35));
Check(resumeTiming.EstimateRemaining(TimeSpan.FromSeconds(35)) == TimeSpan.Zero, "Temps restant nul quand toutes les données sont traitées");
Check(CopyTimeEstimator.FormatDuration(TimeSpan.FromSeconds(3661)) == "01:01:01", "Afficher les durées en heures, minutes et secondes");
Check(CopyTimeEstimator.FormatDuration(TimeSpan.FromHours(25)) == "1 j 01:00:00", "Afficher les copies de plus de vingt-quatre heures");
Check(new CopyProgress(1, 10, 75, 100, "copie").Percentage == 75, "Pourcentage calculé sur le volume plutôt que le nombre de fichiers");
Check(new CopyProgress(10, 10, 75, 100, "Terminé").Percentage == 75, "Ne pas afficher 100 % si des données n’ont pas été copiées");
Check(new CopyProgress(1, 2, 0, 0, "copie").Percentage == 50, "Pourcentage par fichiers pour une copie de fichiers vides");
Check(new CopyProgress(0, 0, 0, 0, "Terminé").Percentage == 100, "Une copie vide terminée affiche 100 %");
Check(new CopyProgress(1, 1, 110, 100, "copie").Percentage == 100, "Borner le pourcentage à 100 %");
Check(CopyOptions.Between(new(null, "/a"), new(null, "/b"), [], []).Direction == CopyDirection.ComputerToComputer,
    "Déduire PC → PC sans sélecteur de sens");
Check(CopyOptions.Between(new("A", "/sdcard/a"), new("B", "/sdcard/b"), [], []).DestinationEndpoint.Serial == "B",
    "Conserver deux identifiants distincts pour Android → Android");
Check(CopyOptions.Between(new("A", "/sdcard/a"), new("A", "/sdcard/b"), [], []).Direction == CopyDirection.AndroidToAndroid,
    "Autoriser le même Android pour les deux emplacements");
foreach (var pair in new[] { ("/a", "/a"), ("/a", "/a/child"), ("/a/child", "/a") })
{
    var rejected = false;
    try { CopyPathSafety.RejectOverlapping(pair.Item1, pair.Item2, false); } catch (IOException) { rejected = true; }
    Check(rejected, "Refuser les chemins identiques ou imbriqués : " + pair);
}
CopyPathSafety.RejectOverlapping("/a", "/another", false);
Check(true, "Ne pas confondre un préfixe de nom et un dossier enfant");
using (var session = new CopySession(new(CopyDirection.AndroidToComputer, "session-test", "/sdcard", "/local", [], []), "session-queue"))
{
    var firstRun = session.BeginRun();
    Check(session.State == CopySessionState.Running && session.Clock.IsRunning && session.Options.EnableResume,
        "Toute variante possède une file pour la pause de session");
    session.Report(new(1, 10, 100, 1000, "a", 100));
    session.RequestPause();
    Check(session.State == CopySessionState.Pausing && firstRun.IsCancellationRequested,
        "Demander pause annule le transfert mais ne confirme pas encore la déconnexion");
    var prematureResume = false;
    try { session.BeginRun(); } catch (InvalidOperationException) { prematureResume = true; }
    Check(prematureResume, "Interdire Continuer avant la fin de l’arrêt du transfert");
    session.ConfirmPaused();
    session.EndRun();
    var frozen = session.Clock.Elapsed;
    Check(session.State == CopySessionState.Paused && !session.Clock.IsRunning && session.Clock.Elapsed == frozen,
        "La pause confirmée fige le chronomètre");
    var nextRun = session.BeginRun();
    Check(session.State == CopySessionState.Running && !nextRun.IsCancellationRequested && session.Clock.Elapsed >= frozen,
        "Continuer utilise un nouveau token et conserve le temps de copie consommé");
    var recheck = session.Report(new(0, 10, 0, 1000, "réexamen", 0));
    Check(recheck.Percentage == 10 && recheck.TransferredBytes == 100, "Ne pas remettre le pourcentage à zéro pendant le réexamen de reprise");
    var more = session.Report(new(4, 10, 400, 1000, "b", 300));
    Check(more.Percentage == 40 && more.TransferredBytes == 400, "Cumuler les volumes réellement transférés avant et après la pause");
    session.Cancel();
    session.EndRun();
    var cancelledResume = false;
    try { session.BeginRun(); } catch (InvalidOperationException) { cancelledResume = true; }
    Check(session.State == CopySessionState.Cancelled && cancelledResume, "Annuler abandonne la session au lieu de la mettre en pause");
}
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
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "fake device model:My_phone\nother unauthorized\n");
        var connected = await fake.CheckConnectionAsync("fake", CancellationToken.None);
        Check(connected.CanCopy && connected.State == DeviceConnectionState.Ready && connected.Message.Contains("My phone", StringComparison.Ordinal),
            "Téléphone sélectionné autorisé : accès shell confirmé et message prêt à copier");
        Check(!connected.Message.Contains("unauthorized", StringComparison.Ordinal) && !connected.Message.Contains("offline", StringComparison.Ordinal),
            "Ne pas afficher les avertissements des autres téléphones pour celui qui est accessible");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "fake unauthorized\n");
        var unauthorized = await fake.CheckConnectionAsync("fake", CancellationToken.None);
        Check(!unauthorized.CanCopy && unauthorized.State == DeviceConnectionState.Unauthorized,
            "Relire l’état réel unauthorized plutôt que la sélection périmée");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "fake offline\n");
        var offline = await fake.CheckConnectionAsync("fake", CancellationToken.None);
        Check(!offline.CanCopy && offline.State == DeviceConnectionState.Offline, "Distinguer un téléphone offline");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "other device\n");
        var missing = await fake.CheckConnectionAsync("fake", CancellationToken.None);
        Check(!missing.CanCopy && missing.State == DeviceConnectionState.Missing, "Détecter la déconnexion du téléphone sélectionné");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "fake recovery\n");
        var recovery = await fake.CheckConnectionAsync("fake", CancellationToken.None);
        Check(!recovery.CanCopy && recovery.State == DeviceConnectionState.Failed, "Ne pas autoriser la copie pour un état ADB non pris en charge");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "fake device model:My_phone\n");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_FAIL_PROBE", "1");
        var denied = await fake.CheckConnectionAsync("fake", CancellationToken.None);
        Check(!denied.CanCopy && denied.State == DeviceConnectionState.Failed, "État device seul insuffisant : refuser si le shell ADB échoue");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_FAIL_PROBE", null);
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_BAD_PROBE", "1");
        Check(!(await fake.CheckConnectionAsync("fake", CancellationToken.None)).CanCopy, "Refuser une réponse de sonde ADB inattendue");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_BAD_PROBE", null);
        Check((await fake.CheckConnectionAsync("fake", CancellationToken.None)).CanCopy, "Reconnaître le rétablissement de l’accès ADB");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", null);
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

        foreach (var direction in new[] { CopyDirection.ComputerToAndroid, CopyDirection.AndroidToComputer })
        {
            var label = direction == CopyDirection.ComputerToAndroid ? "push" : "download";
            var area = Path.Combine(root, "pause-" + label);
            var remoteRoot = Path.Combine(area, "phone");
            var computerRoot = Path.Combine(area, "computer");
            var signals = Path.Combine(area, "signals");
            var queues = Path.Combine(area, "queues");
            Directory.CreateDirectory(remoteRoot);
            Directory.CreateDirectory(computerRoot);
            Directory.CreateDirectory(signals);
            var source = direction == CopyDirection.ComputerToAndroid ? computerRoot : remoteRoot;
            var pauseDestination = direction == CopyDirection.ComputerToAndroid ? remoteRoot : computerRoot;
            File.WriteAllText(Path.Combine(source, "a.txt"), "already completed");
            var pauseOptions = new CopyOptions(direction, "pause-device", direction == CopyDirection.ComputerToAndroid ? computerRoot : "/sdcard",
                direction == CopyDirection.ComputerToAndroid ? "/sdcard" : computerRoot, [], [], true);
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", remoteRoot);
            var pauseEngine = new CopyEngine(fake, queues);
            await pauseEngine.CopyAsync(pauseOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
            File.WriteAllText(Path.Combine(source, "b.txt"), "file restarted from zero");
            using var session = new CopySession(pauseOptions, queues);
            var run = session.BeginRun();
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SLOW_IO", label);
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SIGNAL_DIRECTORY", signals);
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(signals);
            watcher.Created += (_, change) => { if (int.TryParse(change.Name, out var pid)) started.TrySetResult(pid); };
            watcher.EnableRaisingEvents = true;
            var transfer = pauseEngine.CopyAsync(session.Options, Resolve, new Progress<CopyProgress>(), _ => { }, run.Token);
            int child;
            try { child = await started.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch
            {
                session.Cancel();
                try { await transfer; } catch (Exception) { }
                throw;
            }
            session.RequestPause();
            var interrupted = false;
            try { await transfer; }
            catch (Exception) when (run.IsCancellationRequested) { interrupted = true; }
            session.ConfirmPaused();
            session.EndRun();
            Check(interrupted && session.State == CopySessionState.Paused && !session.Clock.IsRunning,
                label + " : pause au milieu d’un fichier, confirmée seulement après arrêt de la tâche");
            var stopped = false;
            try { using var process = Process.GetProcessById(child); stopped = process.HasExited; }
            catch (ArgumentException) { stopped = true; }
            Check(stopped, label + " : aucun processus ADB ne continue le transfert après confirmation de pause");
            Check(File.ReadAllText(Path.Combine(pauseDestination, "a.txt")) == "already completed" && !File.Exists(Path.Combine(pauseDestination, "b.txt")),
                label + " : conserver les fichiers terminés sans exposer de fichier final partiel");
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SLOW_IO", null);
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SIGNAL_DIRECTORY", null);
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "");
            Check(!(await fake.CheckConnectionAsync("pause-device", CancellationToken.None)).CanCopy && session.State == CopySessionState.Paused,
                label + " : une déconnexion n’annule pas la session en pause");
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", "pause-device device model:Pause_phone\n");
            Check((await fake.CheckConnectionAsync("pause-device", CancellationToken.None)).CanCopy,
                label + " : vérifier le même téléphone après reconnexion");
            var continuedRun = session.BeginRun();
            var continued = await pauseEngine.CopyAsync(session.Options, Resolve, new Progress<CopyProgress>(), _ => { }, continuedRun.Token);
            session.Complete();
            session.EndRun();
            Check(continued.Copied == 1 && continued.Skipped == 1 && continued.Failed == 0 &&
                File.ReadAllText(Path.Combine(pauseDestination, "b.txt")) == "file restarted from zero",
                label + " : Continuer ignore le fichier terminé et rejoue le fichier interrompu sans doublon");
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_DEVICE_LIST", null);
        }
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", null);

        var phoneA = Path.Combine(root, "phone-A");
        var phoneB = Path.Combine(root, "phone-B");
        var tracing = Path.Combine(root, "trace");
        var multiQueues = Path.Combine(root, "multi-queues");
        Directory.CreateDirectory(Path.Combine(phoneA, "source", ".hidden"));
        Directory.CreateDirectory(Path.Combine(phoneA, "source", "empty"));
        Directory.CreateDirectory(Path.Combine(phoneA, "source", "excluded", "keep"));
        Directory.CreateDirectory(phoneB); Directory.CreateDirectory(tracing);
        var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(phoneA, "source", "été.bin"), payload);
        File.WriteAllText(Path.Combine(phoneA, "source", ".hidden", ".nomedia"), "");
        File.WriteAllText(Path.Combine(phoneA, "source", "bad:name.txt"), "Android name");
        File.WriteAllText(Path.Combine(phoneA, "source", "excluded", "drop.txt"), "excluded");
        File.WriteAllText(Path.Combine(phoneA, "source", "excluded", "keep", "keep.txt"), "exception");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOTS", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["A"] = phoneA, ["B"] = phoneB }));
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_TRACE", tracing);
        var multiEngine = new CopyEngine(fake, multiQueues);
        var relayOptions = CopyOptions.Between(new("A", "/sdcard/source"), new("B", "/sdcard/relay"), ["excluded"], ["excluded/keep"], true);
        var relayed = await multiEngine.CopyAsync(relayOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(relayed.Copied == 4 && relayed.Failed == 0, "Copier Android A vers Android B par flux");
        Check(File.ReadAllBytes(Path.Combine(phoneB, "relay", "été.bin")).SequenceEqual(payload), "Le relais conserve les octets binaires et les noms Unicode");
        Check(File.Exists(Path.Combine(phoneB, "relay", ".hidden", ".nomedia")) && File.Exists(Path.Combine(phoneB, "relay", "bad:name.txt")),
            "Android vers Android conserve fichiers cachés et noms Android sans contrainte Windows");
        Check(!File.Exists(Path.Combine(phoneB, "relay", "excluded", "drop.txt")) && File.Exists(Path.Combine(phoneB, "relay", "excluded", "keep", "keep.txt")) &&
            !Directory.Exists(Path.Combine(phoneB, "relay", "empty")), "Le relais applique exclusions, exceptions et absence de dossiers vides");
        string[][] Traces() => Directory.EnumerateFiles(tracing).Select(p => System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(p))!).ToArray();
        var relayTrace = Traces();
        Check(relayTrace.Any(a => a[1] == "A" && a[^1].StartsWith("cat '", StringComparison.Ordinal)) &&
            relayTrace.Any(a => a[1] == "B" && a[^1].StartsWith("cat >", StringComparison.Ordinal) && !a.Contains("-n")),
            "Relais : deux appareils explicites, stdin activé sur le receveur");
        var relayedAgain = await multiEngine.CopyAsync(relayOptions, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(relayedAgain.Copied == 0 && relayedAgain.Skipped == 4, "La reprise Android A vers B ne retransfère pas les fichiers validés");
        string relayQueue;
        using (var queue = new TransferQueue(relayOptions, multiQueues)) relayQueue = queue.FilePath;
        using (var queue = new TransferQueue(relayOptions with { DestinationSerial = "C" }, multiQueues))
            Check(queue.FilePath != relayQueue && !queue.Entries.Any(), "La file Android vers Android est isolée par appareil destination");
        foreach (var file in Directory.EnumerateFiles(tracing)) File.Delete(file);
        var localAndroid = CopyOptions.Between(new("A", "/sdcard/source"), new("A", "/sdcard/local-copy"), ["excluded"], ["excluded/keep"], true);
        var copiedOnDevice = await multiEngine.CopyAsync(localAndroid, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(copiedOnDevice.Copied == 4 && copiedOnDevice.Failed == 0 && File.ReadAllBytes(Path.Combine(phoneA, "local-copy", "été.bin")).SequenceEqual(payload),
            "Copier localement entre dossiers distincts sur le même Android");
        var cpTrace = Traces();
        Check(cpTrace.Any(a => a[^1].StartsWith("cp -n ", StringComparison.Ordinal)) && !cpTrace.Any(a => a[2] == "push" || a[^1].StartsWith("cat ", StringComparison.Ordinal)),
            "Même Android : cp local uniquement, aucun pull, push ni relais du contenu");
        foreach (var invalid in new[] { "/storage/emulated/0/source", "/storage/emulated/0/source/inside", "/storage/emulated/0" })
        {
            var aliasRejected = false;
            try { await multiEngine.CopyAsync(localAndroid with { Destination = invalid }, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None); }
            catch (IOException ex) { aliasRejected = ex.Message.Contains("imbriquées", StringComparison.Ordinal); }
            Check(aliasRejected, "Résoudre les alias /sdcard et /storage/emulated/0 avant de refuser : " + invalid);
        }
        Check(!Directory.Exists(Path.Combine(phoneA, "source", "inside")), "Un refus de copie récursive ne crée pas la destination");

        var caseDistinct = await multiEngine.CopyAsync(localAndroid with { Destination = "/sdcard/SOURCE" }, Resolve,
            new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
        Check(caseDistinct.Copied == 4 && caseDistinct.Failed == 0, "Même Android : deux dossiers différant par la casse restent distincts");
        var rootAlias = Path.Combine(root, "phone-root-alias");
        Directory.CreateSymbolicLink(rootAlias, phoneA);
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", rootAlias);
        var aliasListing = await fake.ListDirectoryAsync("root-alias", "/sdcard", CancellationToken.None);
        Check(aliasListing.Any(e => e.Name == "source" && e.IsDirectory), "Parcourir /sdcard quand la racine est un lien Android");
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOT", null);

        foreach (var mode in new[] { "cp", "relay" })
        {
            var srcRel = "pause-source-" + mode;
            var dstRel = "pause-target-" + mode;
            var dstRoot = mode == "cp" ? phoneA : phoneB;
            Directory.CreateDirectory(Path.Combine(phoneA, srcRel));
            File.WriteAllText(Path.Combine(phoneA, srcRel, "a.txt"), "completed");
            var job = CopyOptions.Between(new("A", "/sdcard/" + srcRel), new(mode == "cp" ? "A" : "B", "/sdcard/" + dstRel), [], [], true);
            await multiEngine.CopyAsync(job, Resolve, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
            File.WriteAllText(Path.Combine(phoneA, srcRel, "b.txt"), "restart me from zero");
            var signals = Path.Combine(root, "signals-" + mode); Directory.CreateDirectory(signals);
            using var session = new CopySession(job, multiQueues);
            var token = session.BeginRun().Token;
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(signals);
            watcher.Created += (_, e) => { if (int.TryParse(e.Name, out var id)) started.TrySetResult(id); };
            watcher.EnableRaisingEvents = true;
            var oldTraces = Directory.EnumerateFiles(tracing).ToHashSet();
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SLOW_IO", mode == "cp" ? "cp" : "download");
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SIGNAL_DIRECTORY", signals);
            var task = multiEngine.CopyAsync(session.Options, Resolve, new Progress<CopyProgress>(), _ => { }, token);
            try { await started.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { session.Cancel(); try { await task; } catch (Exception) { } throw; }
            session.RequestPause();
            try { await task; } catch (Exception) when (token.IsCancellationRequested) { }
            session.ConfirmPaused(); session.EndRun();
            Check(session.State == CopySessionState.Paused && !File.Exists(Path.Combine(dstRoot, dstRel, "b.txt")), mode + " : pause au milieu d’un transfert sans fichier final partiel");
            var allStopped = true;
            foreach (var traceFile in Directory.EnumerateFiles(tracing).Where(p => !oldTraces.Contains(p)))
            {
                var id = int.Parse(Path.GetFileName(traceFile).Split('-')[0]);
                try { using var p = Process.GetProcessById(id); allStopped &= p.HasExited; } catch (ArgumentException) { }
            }
            Check(allStopped, mode + " : tous les processus des deux côtés sont arrêtés avant déconnexion");
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SLOW_IO", null);
            Environment.SetEnvironmentVariable("ANDROCP_FAKE_SIGNAL_DIRECTORY", null);
            var continuedRun = session.BeginRun();
            var done = await multiEngine.CopyAsync(session.Options, Resolve, new Progress<CopyProgress>(), _ => { }, continuedRun.Token);
            session.Complete(); session.EndRun();
            Check(done.Copied == 1 && done.Skipped == 1 && done.Failed == 0 && File.ReadAllText(Path.Combine(dstRoot, dstRel, "b.txt")) == "restart me from zero",
                mode + " : Continuer rejoue seulement le fichier interrompu");
        }
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_ROOTS", null);
        Environment.SetEnvironmentVariable("ANDROCP_FAKE_TRACE", null);
    }

    var pcSource = Path.Combine(root, "pc-source");
    var pcTarget = Path.Combine(root, "pc-target");
    Directory.CreateDirectory(pcSource); Directory.CreateDirectory(pcTarget);
    File.WriteAllText(Path.Combine(pcSource, "file.txt"), "local copy without ADB");
    var localOptions = CopyOptions.Between(new(null, pcSource), new(null, pcTarget), [], []);
    var native = new CopyEngine(new AdbClient("/nonexistent-adb"), Path.Combine(root, "pc-queues"));
    Task<NameDecision> RenameNative(NameProblem problem) => Task.FromResult(new NameDecision(DecisionKind.Rename, problem.Suggested));
    var pcCopied = await native.CopyAsync(localOptions, RenameNative, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
    var pcResumed = await native.CopyAsync(localOptions, RenameNative, new Progress<CopyProgress>(), _ => { }, CancellationToken.None);
    Check(pcCopied.Copied == 1 && pcResumed.Skipped == 1 && File.ReadAllText(Path.Combine(pcTarget, "file.txt")) == "local copy without ADB", "PC vers PC et reprise sans ADB installé");
    var nestedRejected = false;
    try { await native.CopyAsync(localOptions with { Destination = pcSource }, RenameNative, new Progress<CopyProgress>(), _ => { }, CancellationToken.None); }
    catch (IOException) { nestedRejected = true; }
    Check(nestedRejected, "PC vers PC refuse aussi la source identique à la destination");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"\n{count} tests réussis.");
