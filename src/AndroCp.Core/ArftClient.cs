using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace AndroCp.Core;

/// <summary>A dedicated Python worker: ARFT pulls into staging, never into user final paths.</summary>
public sealed class ArftClient : IDisposable
{
    private readonly Process process;
    private readonly Task errorReader;
    private readonly CancellationTokenRegistration cancellation;
    public static bool IsBundled => File.Exists(Path.Combine(AppContext.BaseDirectory, "python", "python.exe")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "arft", "bridge.py"));

    public ArftClient(string adb, string serial, Action<string> log, CancellationToken ct,
        string? pythonExecutable = null, string? bridgePath = null)
    {
        pythonExecutable ??= Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        bridgePath ??= Path.Combine(AppContext.BaseDirectory, "arft", "bridge.py");
        if (!File.Exists(pythonExecutable) || !File.Exists(bridgePath))
            throw new IOException("Python/ARFT intégrés absents. Utilisez la distribution AndroCp-win-arft-x64.");
        var info = new ProcessStartInfo(pythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-X"); info.ArgumentList.Add("utf8");
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(bridgePath);
        info.ArgumentList.Add("--adb"); info.ArgumentList.Add(adb);
        info.ArgumentList.Add("--serial"); info.ArgumentList.Add(serial);
        info.Environment["PYTHONUTF8"] = "1";
        process = Process.Start(info) ?? throw new IOException("Impossible de démarrer Python/ARFT.");
        cancellation = ct.Register(Kill);
        errorReader = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) log("ARFT : " + line);
        });
    }
    public async Task DownloadAsync(string remote, string staging, long size, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { remote, staging, size }).AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        var response = await process.StandardOutput.ReadLineAsync(ct) ?? throw new IOException("Le processus ARFT s’est arrêté. Consultez le journal.");
        using var result = JsonDocument.Parse(response);
        if (!result.RootElement.GetProperty("ok").GetBoolean())
            throw new IOException("ARFT : " + result.RootElement.GetProperty("error").GetString());
    }
    private void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        cancellation.Dispose();
        Kill();
        process.WaitForExit();
        errorReader.GetAwaiter().GetResult();
        process.Dispose();
    }
}
