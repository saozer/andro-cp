using AndroCp.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AndroCp.App;

public partial class MainWindow : Window
{
    private readonly AdbClient adb = new();
    private readonly Dictionary<string, string> sourcePaths = new();
    private readonly Dictionary<string, string> destinationPaths = new();
    private CopyDevice? currentSource, currentDestination;
    private CancellationTokenSource? cancellation, connectionCheck;
    private CopySession? activeSession;
    private long connectionVersion;
    private bool refreshing, checkingBeforeCopy, closed, canCopy, warningDismissed, initialized;
    private string? warningContext;

    public MainWindow()
    {
        InitializeComponent();
        initialized = true;
        Opened += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { closed = true; connectionCheck?.Cancel(); activeSession?.Dispose(); };
        Closing += (_, args) =>
        {
            if (cancellation is null) return;
            args.Cancel = true;
            activeSession?.Cancel();
            cancellation.Cancel();
            Status.Text = "Annulation en cours… Fermez la fenêtre une fois le nettoyage terminé.";
        };
    }
    private CopyDevice? Selected(bool source) => (source ? SourceDevice : DestinationDevice).SelectedItem as CopyDevice;
    private static string Key(CopyDevice? device) => device is null ? "none" : device.IsComputer ? "pc" : "android:" + device.Serial;
    private string ModeText => Selected(true) is { } source && Selected(false) is { } destination
        ? source.IsComputer ? destination.IsComputer ? "PC → PC" : "PC → Android"
            : destination.IsComputer ? "Android → PC" : source.Serial == destination.Serial ? "Même Android → copie locale (cp)" : "Android → Android (relais via ce PC)"
        : "Choisissez une source et une destination";
    private void AppendLog(string message)
    {
        void Append()
        {
            var text = LogBox.Text ?? "";
            if (text.Length > 24000) text = text[^16000..];
            LogBox.Text = text + $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            LogBox.CaretIndex = LogBox.Text.Length;
        }
        if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
    }
    private void UpdateCopyButton() => CopyButton.IsEnabled = canCopy && !refreshing && !checkingBeforeCopy && activeSession is null && !closed;
    private void ShowConnection(DeviceConnection connection, string? detail = null)
    {
        canCopy = connection.CanCopy;
        DeviceStatus.Text = connection.Message;
        if (connection.State != DeviceConnectionState.Checking)
        {
            var context = $"{Key(Selected(true))}|{Key(Selected(false))}:{detail ?? connection.State.ToString()}";
            if (context != warningContext) warningDismissed = false;
            warningContext = context;
        }
        ConnectionWarning.IsVisible = !connection.CanCopy && connection.State != DeviceConnectionState.Checking && !warningDismissed;
        ConnectionWarningText.Text = connection.Message;
        UpdateCopyButton();
    }
    private void DismissWarningClick(object? sender, RoutedEventArgs args) { warningDismissed = true; ConnectionWarning.IsVisible = false; }
    private void RememberPaths()
    {
        void Switch(CopyDevice? next, ref CopyDevice? previous, TextBox path, Dictionary<string, string> cache, bool source)
        {
            if (next is null) return;
            if (previous is null || Key(previous) != Key(next))
            {
                if (previous is not null) cache[Key(previous)] = path.Text ?? "";
                path.Text = cache.TryGetValue(Key(next), out var saved) ? saved : next.IsComputer
                    ? source ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : ""
                    : "/sdcard";
            }
            previous = next;
        }
        Switch(Selected(true), ref currentSource, SourcePath, sourcePaths, true);
        Switch(Selected(false), ref currentDestination, DestinationPath, destinationPaths, false);
    }
    private async Task RefreshAsync()
    {
        if (refreshing || checkingBeforeCopy || activeSession is not null || closed) return;
        refreshing = true;
        connectionVersion++;
        connectionCheck?.Cancel();
        ShowConnection(DeviceConnection.Checking);
        RefreshButton.IsEnabled = false;
        SourceDevice.IsEnabled = DestinationDevice.IsEnabled = false;
        var oldSource = Selected(true);
        var oldDestination = Selected(false);
        var devices = new List<CopyDevice> { CopyDevice.Computer };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            devices.AddRange((await adb.DevicesAsync(timeout.Token)).Select(d => new CopyDevice(d.Serial, d.Model, d.State)));
            VariantStatus.Text = devices.Count == 1 ? "Aucun Android détecté. La copie PC → PC reste disponible."
                : $"{devices.Count - 1} Android détecté(s). Même application et mêmes fonctions en Full et Tiny.";
        }
        catch (Exception ex)
        {
            VariantStatus.Text = "ADB indisponible ; PC → PC reste utilisable. Installez ADB ou utilisez la livraison Full.";
            AppendLog(ex.Message);
        }
        try
        {
            if (closed) return;
            foreach (var old in new[] { oldSource, oldDestination }.OfType<CopyDevice>())
                if (!devices.Any(d => Key(d) == Key(old))) devices.Add(old with { State = "missing" });
            SourceDevice.ItemsSource = devices;
            DestinationDevice.ItemsSource = devices.ToArray();
            SourceDevice.SelectedItem = oldSource is null ? devices.FirstOrDefault(d => d.State == "device") ?? devices[0]
                : devices.First(d => Key(d) == Key(oldSource));
            DestinationDevice.SelectedItem = oldDestination is null ? devices[0] : devices.First(d => Key(d) == Key(oldDestination));
            RememberPaths();
        }
        finally
        {
            refreshing = false;
            if (!closed) { RefreshButton.IsEnabled = true; SourceDevice.IsEnabled = DestinationDevice.IsEnabled = true; UpdateCopyButton(); }
        }
        if (!closed) await ValidateEndpointsAsync();
    }
    private async Task<bool> ValidateEndpointsAsync(bool showChecking = true)
    {
        var version = ++connectionVersion;
        connectionCheck?.Cancel();
        if (Selected(true) is not { } source || Selected(false) is not { } destination)
        {
            ShowConnection(new(DeviceConnectionState.NoSelection, "Choisissez l’appareil source et l’appareil destination."));
            return false;
        }
        using var check = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        connectionCheck = check;
        canCopy = false;
        if (showChecking) ShowConnection(DeviceConnection.Checking); else UpdateCopyButton();
        try
        {
            var serials = new[] { source, destination }.Where(d => !d.IsComputer).Select(d => d.Serial!).Distinct(StringComparer.Ordinal).ToArray();
            var results = await Task.WhenAll(serials.Select(serial => adb.CheckConnectionAsync(serial, check.Token)));
            if (closed || version != connectionVersion || Key(Selected(true)) != Key(source) || Key(Selected(false)) != Key(destination)) return false;
            var bySerial = serials.Zip(results).ToDictionary(x => x.First, x => x.Second);
            SourceDeviceStatus.Text = source.IsComputer ? "Ce PC — dossier local" : bySerial[source.Serial!].Message;
            DestinationDeviceStatus.Text = destination.IsComputer ? "Ce PC — dossier local" : bySerial[destination.Serial!].Message;
            var problems = new[] { source.IsComputer ? null : bySerial[source.Serial!], destination.IsComputer ? null : bySerial[destination.Serial!] }
                .OfType<DeviceConnection>().Where(c => !c.CanCopy).ToArray();
            var summary = problems.FirstOrDefault() ?? new(DeviceConnectionState.Ready, ModeText + " — prêt à copier.");
            ShowConnection(summary, string.Join('|', serials.Zip(results).Select(x => x.First + ":" + x.Second.State)));
            return summary.CanCopy;
        }
        catch (OperationCanceledException)
        {
            if (!closed && version == connectionVersion) ShowConnection(new(DeviceConnectionState.Failed, "Un appareil ne répond pas dans le délai prévu. Vérifiez la connexion puis réessayez."));
            return false;
        }
        catch (Exception ex)
        {
            if (!closed && version == connectionVersion)
            {
                var error = "ADB indisponible ou inaccessible : " + ex.Message;
                if (!source.IsComputer) SourceDeviceStatus.Text = error;
                if (!destination.IsComputer) DestinationDeviceStatus.Text = error;
                ShowConnection(new(DeviceConnectionState.Unavailable, error));
                AppendLog(ex.Message);
            }
            return false;
        }
        finally { if (ReferenceEquals(connectionCheck, check)) connectionCheck = null; }
    }
    private async void DevicesChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!initialized || refreshing || checkingBeforeCopy || activeSession is not null || closed) return;
        RememberPaths();
        await ValidateEndpointsAsync();
    }
    private async void RefreshClick(object? sender, RoutedEventArgs args) => await RefreshAsync();
    private async Task BrowseAsync(bool source)
    {
        if (Selected(source) is not { } device) return;
        var input = source ? SourcePath : DestinationPath;
        if (device.IsComputer)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choisir le dossier", AllowMultiple = false });
            if (folders.Count > 0) input.Text = folders[0].TryGetLocalPath();
        }
        else
        {
            var folder = await new AndroidFolderDialog(adb, device, input.Text).AskAsync(this);
            if (folder is not null) input.Text = folder;
        }
    }
    private async void BrowseSourceClick(object? sender, RoutedEventArgs args) => await BrowseAsync(true);
    private async void BrowseDestinationClick(object? sender, RoutedEventArgs args) => await BrowseAsync(false);
    private static string[] Lines(string? text) => (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private async void CopyClick(object? sender, RoutedEventArgs args)
    {
        if (activeSession is not null || checkingBeforeCopy || refreshing || closed) return;
        if (Selected(true) is not { } source || Selected(false) is not { } destination) return;
        if (string.IsNullOrWhiteSpace(SourcePath.Text) || string.IsNullOrWhiteSpace(DestinationPath.Text)) { Status.Text = "Renseignez les deux dossiers."; return; }
        checkingBeforeCopy = true;
        Configuration.IsEnabled = false;
        bool accessible;
        try { accessible = await ValidateEndpointsAsync(); }
        finally { checkingBeforeCopy = false; if (!closed) { Configuration.IsEnabled = true; UpdateCopyButton(); } }
        if (!accessible || closed) return;
        var persistent = ResumeOption.IsChecked == true;
        var options = CopyOptions.Between(new(source.Serial, SourcePath.Text!), new(destination.Serial, DestinationPath.Text!),
            Lines(Exclusions.Text), Lines(Exceptions.Text), persistent && PartialSpaceOption.IsChecked == true) with { EnableResume = persistent };
        var sessionDirectory = persistent ? null : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroCp", "sessions", Guid.NewGuid().ToString("N"));
        activeSession = new CopySession(options, sessionDirectory);
        await RunSessionAsync(activeSession);
    }
    private async Task RunSessionAsync(CopySession session)
    {
        var continuing = session.State == CopySessionState.Paused;
        cancellation = session.BeginRun();
        var token = cancellation.Token;
        Configuration.IsEnabled = false;
        CopyButton.IsEnabled = false;
        CancelButton.IsEnabled = PauseButton.IsEnabled = true;
        PauseButton.Content = "Pause";
        TransferProgress.Foreground = Brush.Parse("#0EA5E9");
        TransferProgress.IsIndeterminate = true;
        if (!continuing) { TransferProgress.Value = 0; TransferPercentageText.Text = "…"; }
        Status.Text = continuing ? "Reprise : inventaire et vérification des fichiers déjà copiés…" : "Inventaire et préparation…";
        var watch = session.Clock;
        var estimator = new CopyTimeEstimator();
        var completedSuccessfully = false;
        var operationFinished = false;
        void UpdateTiming(bool finished = false)
        {
            var remaining = finished ? (completedSuccessfully ? TimeSpan.Zero : (TimeSpan?)null) : estimator.EstimateRemaining(watch.Elapsed);
            var estimate = remaining is { } time ? (finished ? "" : "≈ ") + CopyTimeEstimator.FormatDuration(time) : finished ? "—" : "calcul en cours…";
            TimingText.Text = $"Écoulé : {CopyTimeEstimator.FormatDuration(watch.Elapsed)} · Restant estimé : {estimate}";
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateTiming();
        UpdateTiming(); timer.Start();
        var progress = new Progress<CopyProgress>(p =>
        {
            if (operationFinished) return;
            estimator.Update(p, watch.Elapsed); UpdateTiming();
            p = session.Report(p);
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = p.Percentage;
            TransferPercentageText.Text = $"{p.Percentage:F1} %";
            if (p.Current is "Terminé" or "Pause : espace insuffisant") { CurrentFile.Text = p.Current; return; }
            if (session.State != CopySessionState.Running) return;
            Status.Text = $"{p.Completed}/{p.Total} fichiers traités · {p.Bytes / 1073741824.0:F2}/{p.TotalBytes / 1073741824.0:F2} Gio · {(p.TransferredBytes ?? p.Bytes) / 1048576.0 / Math.Max(1, watch.Elapsed.TotalSeconds):F1} Mio/s (moyenne)";
            CurrentFile.Text = p.Current;
        });
        Task<NameDecision> Resolve(NameProblem problem)
        {
            var completion = new TaskCompletionSource<NameDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var dialog = new NameDialog(problem);
                    using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
                    completion.SetResult(await dialog.AskAsync(this));
                }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return completion.Task;
        }
        const string pausedMessage = "Copie en pause. Vous pouvez débrancher les téléphones ; rebranchez les mêmes appareils puis cliquez sur Continuer.";
        try
        {
            var result = await Task.Run(() => new CopyEngine(adb, session.QueueDirectory).CopyAsync(session.Options, Resolve, progress, AppendLog, token), token);
            completedSuccessfully = !result.PausedForSpace && result.Failed == 0;
            if (result.PausedForSpace || session.State == CopySessionState.Pausing)
            {
                session.ConfirmPaused(); completedSuccessfully = false;
                Status.Text = result.PausedForSpace ? "Copie en pause faute d’espace sur la destination. Libérez de l’espace puis cliquez sur Continuer." : pausedMessage;
            }
            else { session.Complete(); Status.Text = $"Copie terminée : {result.Copied} copiés, {result.Skipped} ignorés, {result.Failed} en erreur."; }
            AppendLog(Status.Text); AppendLog("Journal : " + result.Journal);
        }
        catch (Exception) when (session.State == CopySessionState.Pausing && token.IsCancellationRequested)
        { session.ConfirmPaused(); Status.Text = pausedMessage + " Le fichier interrompu sera recommencé."; AppendLog(Status.Text); }
        catch (OperationCanceledException) { session.Cancel(); Status.Text = "Copie annulée. Les fichiers terminés sont conservés."; AppendLog(Status.Text); }
        catch (Exception ex) when (session.State == CopySessionState.Cancelled) { Status.Text = "Copie annulée. Les fichiers terminés sont conservés."; AppendLog(Status.Text + " " + ex.Message); }
        catch (Exception ex) { session.Fail(); Status.Text = "Copie interrompue : " + ex.Message; AppendLog(Status.Text); }
        finally
        {
            operationFinished = true; timer.Stop(); session.EndRun(); UpdateTiming(finished: true);
            cancellation = null;
            TransferProgress.IsIndeterminate = false;
            if (session.State == CopySessionState.Paused)
            {
                TransferProgress.Foreground = Brush.Parse("#F59E0B"); PauseButton.Content = "Continuer";
                PauseButton.IsEnabled = CancelButton.IsEnabled = true;
                canCopy = false;
                DeviceStatus.Text = "En pause : l’accès aux mêmes appareils sera vérifié avant de continuer.";
                CurrentFile.Text = "En pause — aucun transfert actif"; ConnectionWarning.IsVisible = false; UpdateCopyButton();
            }
            else
            {
                activeSession = null; session.Dispose(); Configuration.IsEnabled = true;
                PauseButton.Content = "Pause"; PauseButton.IsEnabled = CancelButton.IsEnabled = false; UpdateCopyButton();
                if (!closed) await ValidateEndpointsAsync(showChecking: false);
            }
        }
    }
    private async void PauseClick(object? sender, RoutedEventArgs args)
    {
        if (closed || checkingBeforeCopy || activeSession is not { } session) return;
        if (session.State == CopySessionState.Running)
        {
            PauseButton.IsEnabled = false; Status.Text = "Mise en pause… Attendez la confirmation avant de débrancher."; session.RequestPause(); return;
        }
        if (session.State != CopySessionState.Paused || cancellation is not null) return;
        checkingBeforeCopy = true; PauseButton.IsEnabled = CancelButton.IsEnabled = false;
        Status.Text = "Vérification des appareils avant reprise…";
        try
        {
            if (Selected(true)?.Serial != session.Options.SourceEndpoint.Serial || Selected(false)?.Serial != session.Options.DestinationEndpoint.Serial || !await ValidateEndpointsAsync())
            { Status.Text = "Toujours en pause. Rebranchez et autorisez les mêmes téléphones, puis cliquez sur Continuer."; return; }
        }
        finally { checkingBeforeCopy = false; if (!closed) PauseButton.IsEnabled = CancelButton.IsEnabled = true; }
        if (!closed && ReferenceEquals(activeSession, session)) await RunSessionAsync(session);
    }
    private async void CancelClick(object? sender, RoutedEventArgs args)
    {
        if (checkingBeforeCopy || activeSession is not { } session) return;
        session.Cancel(); PauseButton.IsEnabled = CancelButton.IsEnabled = false;
        if (cancellation is not null) { Status.Text = "Annulation en cours…"; return; }
        session.Dispose(); activeSession = null; Configuration.IsEnabled = true;
        TransferProgress.Foreground = Brush.Parse("#0EA5E9"); PauseButton.Content = "Pause";
        Status.Text = "Copie abandonnée. Les fichiers terminés sont conservés."; AppendLog(Status.Text); UpdateCopyButton();
        if (!closed) await ValidateEndpointsAsync(showChecking: false);
    }
}
