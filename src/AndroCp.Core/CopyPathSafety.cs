namespace AndroCp.Core;

public static class CopyPathSafety
{
    public static void RejectOverlapping(string source, string destination, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        source = source.Replace('\\', '/').TrimEnd('/');
        destination = destination.Replace('\\', '/').TrimEnd('/');
        if (source.Equals(destination, comparison) || source.StartsWith(destination + "/", comparison) ||
            destination.StartsWith(source + "/", comparison))
            throw new IOException("Source et destination identiques ou imbriquées sur le même appareil : choisissez deux dossiers distincts sans relation parent/enfant.");
    }
    public static void CheckRelative(string path)
    {
        if (path.Length == 0 || path.Split('/').Any(x => x is "" or "." or "..") || path.Contains('\0'))
            throw new IOException("Chemin relatif non sûr : " + path);
    }
    public static bool LocalExists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
    public static void CheckLocalParents(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (LocalExists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Chemin contenant un lien ou une jonction : " + current);
    }
}
