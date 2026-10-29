using System.Diagnostics;

namespace AndroCp.Core;

public enum CopySessionState { Created, Running, Pausing, Paused, Completed, Cancelled, Failed }

/// <summary>UI session lifecycle. Pause is confirmed only after the transfer task has unwound.</summary>
public sealed class CopySession(CopyOptions options, string? queueDirectory = null) : IDisposable
{
    private CancellationTokenSource? run;
    private long previousTransferred;
    private long currentTransferred;
    public CopyOptions Options { get; } = options with { EnableResume = true };
    public string? QueueDirectory { get; } = queueDirectory;
    public CopySessionState State { get; private set; } = CopySessionState.Created;
    public Stopwatch Clock { get; } = new();
    public CopyProgress? LastProgress { get; private set; }

    public CancellationTokenSource BeginRun()
    {
        if (State is not (CopySessionState.Created or CopySessionState.Paused))
            throw new InvalidOperationException("La session doit être nouvelle ou en pause pour démarrer.");
        if (run is not null) throw new InvalidOperationException("L’ancien transfert n’est pas encore arrêté.");
        previousTransferred += currentTransferred;
        currentTransferred = 0;
        run = new CancellationTokenSource();
        State = CopySessionState.Running;
        Clock.Start();
        return run;
    }
    public void RequestPause()
    {
        if (State != CopySessionState.Running) return;
        State = CopySessionState.Pausing;
        run?.Cancel();
    }
    public void ConfirmPaused()
    {
        if (State is not (CopySessionState.Running or CopySessionState.Pausing))
            throw new InvalidOperationException("Aucun transfert à mettre en pause.");
        State = CopySessionState.Paused;
        Clock.Stop();
    }
    public void Complete()
    {
        State = CopySessionState.Completed;
        Clock.Stop();
    }
    public void Fail()
    {
        State = CopySessionState.Failed;
        Clock.Stop();
    }
    public void Cancel()
    {
        State = CopySessionState.Cancelled;
        run?.Cancel();
        // Keep counting cleanup time until EndRun; an already paused clock stays stopped.
    }
    public void EndRun()
    {
        Clock.Stop();
        run?.Dispose();
        run = null;
    }
    public CopyProgress Report(CopyProgress progress)
    {
        currentTransferred = Math.Max(currentTransferred, progress.TransferredBytes ?? progress.Bytes);
        if (LastProgress is { } previous && previous.Total == progress.Total && previous.TotalBytes == progress.TotalBytes)
            progress = progress with { Completed = Math.Max(previous.Completed, progress.Completed), Bytes = Math.Max(previous.Bytes, progress.Bytes) };
        progress = progress with { TransferredBytes = previousTransferred + currentTransferred };
        LastProgress = progress;
        return progress;
    }
    public void Dispose()
    {
        run?.Cancel();
        EndRun();
    }
}
