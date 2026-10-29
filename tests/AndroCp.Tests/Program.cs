using AndroCp.Core;
using System.Diagnostics;

// A subprocess fixture exercises real byte transfers without any connected phone.
if (args.Length > 0 && args[0] == "-s" && !OperatingSystem.IsWindows())
{
    var fakeRoot = Environment.GetEnvironmentVariable("ANDROCP_FAKE_ROOT") ?? throw new Exception("Missing fake root");
    if (args[2] == "push")
    {
        File.Copy(args[3], args[4].Replace("/sdcard", fakeRoot, StringComparison.Ordinal));
        return;
    }
    var command = args[^1].Replace("/sdcard", fakeRoot, StringComparison.Ordinal);
    var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
    info.ArgumentList.Add("-c");
    info.ArgumentList.Add(command);
    using var process = Process.Start(info)!;
    await process.WaitForExitAsync();
    Environment.ExitCode = process.ExitCode;
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
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"\n{count} tests réussis.");
