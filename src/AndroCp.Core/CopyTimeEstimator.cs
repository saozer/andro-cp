namespace AndroCp.Core;

/// <summary>File-level throughput estimate, excluding files reused by a resume.</summary>
public sealed class CopyTimeEstimator
{
    private readonly Queue<(TimeSpan Time, long Bytes)> samples = new();
    private TimeSpan previousUpdate;
    private TimeSpan lastTransfer;
    private long transferred;
    private long remaining;
    private bool receivedProgress;

    public void Update(CopyProgress progress, TimeSpan elapsed)
    {
        var actual = progress.TransferredBytes ?? progress.Bytes;
        if (actual < transferred || elapsed < previousUpdate)
        {
            samples.Clear();
            transferred = 0;
            previousUpdate = elapsed;
        }
        remaining = Math.Max(0, progress.TotalBytes - progress.Bytes);
        receivedProgress = true;
        if (actual > transferred)
        {
            if (samples.Count == 0) samples.Enqueue((previousUpdate, transferred));
            samples.Enqueue((elapsed, actual));
            lastTransfer = elapsed;
        }
        transferred = actual;
        previousUpdate = elapsed;
        while (samples.Count > 2 && samples.ElementAt(1).Time < elapsed - TimeSpan.FromSeconds(60)) samples.Dequeue();
    }

    public TimeSpan? EstimateRemaining(TimeSpan elapsed)
    {
        if (!receivedProgress) return null;
        if (remaining == 0) return TimeSpan.Zero;
        // No byte-by-byte reporting yet: don't pretend an old rate is reliable
        // while one large file or a verification stage is still running.
        if (samples.Count < 2 || elapsed - lastTransfer > TimeSpan.FromSeconds(30)) return null;
        var start = samples.Peek();
        var seconds = (elapsed - start.Time).TotalSeconds;
        var bytes = transferred - start.Bytes;
        if (seconds <= 0 || bytes <= 0) return null;
        var estimate = remaining * (seconds / bytes);
        return double.IsFinite(estimate) && estimate < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(estimate) : null;
    }

    public static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1
        ? $"{(int)duration.TotalDays} j {duration:hh\\:mm\\:ss}"
        : duration.ToString("hh\\:mm\\:ss");
}
