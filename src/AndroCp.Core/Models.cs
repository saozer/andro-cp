namespace AndroCp.Core;

public record AndroidDevice(string Serial, string State, string Model)
{
    public override string ToString() => $"{Model} — {Serial} ({State})";
}

public enum CopyDirection { AndroidToComputer, ComputerToAndroid }
public record CopyOptions(CopyDirection Direction, string Serial, string Source, string Destination,
    IReadOnlyList<string> Exclusions, IReadOnlyList<string> Exceptions, bool EnableResume = false, bool UseArft = false,
    bool AllowPartialSpace = false);
public record SourceFile(string RelativePath, long Size, long? ModifiedTicks = null);
public record CopyProgress(int Completed, int Total, long Bytes, long TotalBytes, string Current);
public record CopyResult(int Copied, int Skipped, int Failed, string Journal, bool PausedForSpace = false);
public enum DecisionKind { Rename, Skip, Cancel }
public record NameProblem(string Original, string Reason, string Suggested, bool IsDirectory);
public record NameDecision(DecisionKind Kind, string Name = "", bool ApplyToSimilar = false);
public delegate Task<NameDecision> ResolveName(NameProblem problem);

public sealed class CopyRules
{
    private readonly string[] exclusions;
    private readonly string[] exceptions;
    public CopyRules(IEnumerable<string> exclusions, IEnumerable<string> exceptions)
    {
        this.exclusions = exclusions.Select(Normalize).Where(x => x.Length > 0).ToArray();
        this.exceptions = exceptions.Select(Normalize).Where(x => x.Length > 0).ToArray();
    }
    public static string Normalize(string value) => value.Trim().Replace('\\', '/').Trim('/');
    private static bool Within(string value, string root) => value == root || value.StartsWith(root + "/", StringComparison.Ordinal);
    public bool Includes(string path)
    {
        path = Normalize(path);
        return exceptions.Any(x => Within(path, x)) || !exclusions.Any(x => Within(path, x));
    }
    public bool CanTraverse(string path)
    {
        path = Normalize(path);
        return Includes(path) || exceptions.Any(x => Within(x, path));
    }
    public string FindPruneExpression()
    {
        if (exclusions.Length == 0) return "";
        static string Paths(IEnumerable<(string Path, bool Children)> paths) => "\\( " + string.Join(" -o ", paths.Distinct().Select(x =>
            "-path " + AdbClient.Quote("./" + x.Path.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("*", "\\*", StringComparison.Ordinal).Replace("?", "\\?", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal) + (x.Children ? "/*" : "")))) + " \\)";
        var excluded = exclusions.SelectMany(x => new[] { (x, false), (x, true) });
        var allowed = exceptions.SelectMany(x =>
        {
            var parts = x.Split('/');
            return Enumerable.Range(1, parts.Length).Select(n => (string.Join('/', parts.Take(n)), false)).Append((x, true));
        }).ToArray();
        return Paths(excluded) + (allowed.Length > 0 ? " ! " + Paths(allowed) : "");
    }
}
