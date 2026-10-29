namespace AndroCp.Core;

public record AndroidDevice(string Serial, string State, string Model)
{
    public override string ToString() => $"{Model} — {Serial} ({State})";
}

public record CopyDevice(string? Serial, string Name, string State = "local")
{
    public bool IsComputer => Serial is null;
    public static CopyDevice Computer { get; } = new(null, "Ce PC");
    public override string ToString() => IsComputer ? Name : $"{Name} — {Serial} ({State})";
}
public record CopyEndpoint(string? Serial, string Path)
{
    public bool IsAndroid => Serial is not null;
}
public enum CopyDirection { AndroidToComputer, ComputerToAndroid, AndroidToAndroid, ComputerToComputer }
public record CopyOptions(CopyDirection Direction, string Serial, string Source, string Destination,
    IReadOnlyList<string> Exclusions, IReadOnlyList<string> Exceptions, bool EnableResume = true,
    bool AllowPartialSpace = false, string? DestinationSerial = null)
{
    public CopyEndpoint SourceEndpoint => new(Direction is CopyDirection.AndroidToComputer or CopyDirection.AndroidToAndroid ? Serial : null, Source);
    public CopyEndpoint DestinationEndpoint => new(Direction is CopyDirection.ComputerToAndroid or CopyDirection.AndroidToAndroid ? DestinationSerial ?? Serial : null, Destination);
    public static CopyOptions Between(CopyEndpoint source, CopyEndpoint destination, IReadOnlyList<string> exclusions,
        IReadOnlyList<string> exceptions, bool allowPartialSpace = false) => new(
            source.IsAndroid ? destination.IsAndroid ? CopyDirection.AndroidToAndroid : CopyDirection.AndroidToComputer
                : destination.IsAndroid ? CopyDirection.ComputerToAndroid : CopyDirection.ComputerToComputer,
            source.Serial ?? destination.Serial ?? "local", source.Path, destination.Path, exclusions, exceptions,
            EnableResume: true, AllowPartialSpace: allowPartialSpace, DestinationSerial: destination.Serial);
}
public record SourceFile(string RelativePath, long Size, long? ModifiedTicks = null);
public record CopyProgress(int Completed, int Total, long Bytes, long TotalBytes, string Current, long? TransferredBytes = null)
{
    public double Percentage => TotalBytes > 0 ? Math.Clamp(100.0 * Bytes / TotalBytes, 0, 100)
        : Total > 0 ? Math.Clamp(100.0 * Completed / Total, 0, 100)
        : Current == "Terminé" ? 100 : 0;
}
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
