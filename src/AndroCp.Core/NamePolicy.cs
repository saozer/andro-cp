using System.Text.RegularExpressions;

namespace AndroCp.Core;

public static partial class NamePolicy
{
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reserved();
    public static string? Problem(string name, bool windows)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return "Nom vide ou réservé.";
        if (name.Contains('/') || name.Contains('\0')) return "Séparateur ou caractère nul interdit.";
        if (!windows) return null;
        if (name.Any(c => c < 32 || "<>:\"\\|?*".Contains(c))) return "Caractère interdit sous Windows.";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "Un nom Windows ne peut pas finir par un espace ou un point.";
        if (Reserved().IsMatch(name)) return "Nom réservé sous Windows.";
        if (name.Length > 255) return "Nom trop long (255 caractères maximum).";
        return null;
    }
    public static string Suggest(string name, bool windows)
    {
        var cleaned = new string(name.Select(c => c == '/' || c == '\0' || (windows && (c < 32 || "<>:\"\\|?*".Contains(c))) ? '_' : c).ToArray());
        if (windows) cleaned = cleaned.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..") cleaned = "sans_nom";
        if (windows && Reserved().IsMatch(cleaned)) cleaned = "_" + cleaned;
        if (windows && cleaned.Length > 220) cleaned = cleaned[..220];
        return cleaned;
    }
    public static string Numbered(string name, int number, bool directory)
    {
        var extension = directory ? "" : Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        return $"{stem} ({number}){extension}";
    }
}
