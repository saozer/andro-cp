using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AndroCp.Core;

public sealed record QueueEntry(string Source, string Destination, long Size, long? ModifiedTicks,
    string Id, string Status = "queued", string Phase = "transfer", string? Hash = null);

/// <summary>Append-only durable queue. A partial last event is discarded after a crash.</summary>
public sealed class TransferQueue : IDisposable
{
    private readonly FileStream stream;
    private readonly Dictionary<string, QueueEntry> entries = new(StringComparer.Ordinal);
    public string FilePath { get; }
    public IEnumerable<QueueEntry> Entries => entries.Values;

    public TransferQueue(CopyOptions options, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroCp", "queues");
        Directory.CreateDirectory(directory);
        var identity = JsonSerializer.Serialize(new { options.Direction, options.Serial,
            Source = options.Direction == CopyDirection.ComputerToAndroid ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Source)) : AdbClient.AndroidPath(options.Source),
            Destination = options.Direction == CopyDirection.AndroidToComputer ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Destination)) : AdbClient.AndroidPath(options.Destination),
            Exclusions = options.Exclusions.Order(StringComparer.Ordinal), Exceptions = options.Exceptions.Order(StringComparer.Ordinal) });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        FilePath = Path.Combine(directory, key + ".jsonl");
        stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var contents = reader.ReadToEnd();
            var lastLineEnd = contents.LastIndexOf('\n');
            var valid = lastLineEnd < 0 ? "" : contents[..(lastLineEnd + 1)];
            var lines = valid.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                using var header = JsonDocument.Parse(lines[0]);
                if (header.RootElement.GetProperty("version").GetInt32() != 1 || header.RootElement.GetProperty("identity").GetString() != identity)
                    throw new IOException("La file de reprise ne correspond pas à cette copie.");
                foreach (var line in lines.Skip(1))
                {
                    var entry = JsonSerializer.Deserialize<QueueEntry>(line) ?? throw new IOException("Événement de reprise invalide.");
                    if (entry.Status is not ("queued" or "pending" or "retry" or "completed")) throw new IOException("État de reprise inconnu.");
                    if (!Guid.TryParseExact(entry.Id, "N", out _)) throw new IOException("Identifiant de staging invalide.");
                    entries[entry.Source] = entry;
                }
            }
            stream.SetLength(Encoding.UTF8.GetByteCount(valid));
            stream.Position = stream.Length;
            if (lines.Length == 0) Append(new { version = 1, identity });
            foreach (var entry in entries.Values.Where(x => x.Status == "pending").ToArray())
                Save(entry with { Status = "retry" });
        }
        catch { stream.Dispose(); throw; }
    }

    private void Append<T>(T value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    public QueueEntry? Find(SourceFile source)
    {
        if (!entries.TryGetValue(source.RelativePath, out var entry)) return null;
        if (entry.Size != source.Size || entry.ModifiedTicks != source.ModifiedTicks)
            throw new IOException("Source modifiée depuis la copie précédente : " + source.RelativePath + ". Choisissez une nouvelle destination pour une nouvelle copie.");
        return entry;
    }
    public QueueEntry Add(SourceFile source, string destination)
    {
        var entry = new QueueEntry(source.RelativePath, destination, source.Size, source.ModifiedTicks, Guid.NewGuid().ToString("N"));
        Save(entry);
        return entry;
    }
    public QueueEntry Save(QueueEntry entry)
    {
        Append(entry);
        entries[entry.Source] = entry;
        return entry;
    }
    public void Dispose() => stream.Dispose();
}
