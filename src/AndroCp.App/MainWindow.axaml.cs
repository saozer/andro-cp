using System.Diagnostics;
using AndroCp.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AndroCp.App;

public partial class MainWindow : Window
{
    private readonly AdbClient adb = new();
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? connectionCheck;
    private long connectionVersion;
    private bool refreshing;
    private bool checkingBeforeCopy;
    private bool closed;
    private bool canCopy;
    private bool warningDismissed;
    private string? warningContext;
    private bool initialized;
    public MainWindow()
    {
        InitializeComponent();
        ResumeOption.IsVisible = ArftClient.IsBundled;
        PartialSpaceOption.IsVisible = ArftClient.IsBundled && Download;
        VariantStatus.Text = ArftClient.IsBundled ? "ARFT — reprise disponible. Relancez avec les mêmes chemins pour reprendre."
            : adb.Executable == "adb" ? "Tiny — ADB du système, sans reprise automatique."
            : "ADB intégré — sans reprise automatique.";
        initialized = true;
        Opened += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { closed = true; connectionCheck?.Cancel(); };
        Closing += (_, args) =>
        {
            if (cancellation is not null)
            {
                args.Cancel = true;
                cancellation.Cancel();
                Status.Text = "Annulation en cours… Fermez la fenêtre une fois le nettoyage terminé.";
            }
        };
    }
    private bool Download => Direction.SelectedIndex == 0;
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
    private async Task RefreshAsync()
    {
        if (refreshing || checkingBeforeCopy || cancellation is not null || closed) return;
        refreshing = true;
        connectionVersion++;
        connectionCheck?.Cancel();
        ShowConnection(DeviceConnection.Checking);
        RefreshButton.IsEnabled = false;
        Devices.IsEnabled = false;
        var selectedSerial = (Devices.SelectedItem as AndroidDevice)?.Serial;
        var loaded = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var devices = await adb.DevicesAsync(timeout.Token);
            if (closed) return;
            Devices.ItemsSource = devices;
            Devices.SelectedItem = devices.FirstOrDefault(x => x.Serial == selectedSerial)
                ?? devices.FirstOrDefault(x => x.State == "device") ?? devices.FirstOrDefault();
            loaded = true;
        }
        catch (Exception ex)
        {
            if (closed) return;
            Devices.ItemsSource = Array.Empty<AndroidDevice>();
            ShowConnection(new(DeviceConnectionState.Unavailable, ex is OperationCanceledException
                ? "La recherche ADB a dépassé le délai. Vérifiez la connexion puis cliquez sur Actualiser."
                : "ADB indisponible : vérifiez platform-tools ou le PATH. " + ex.Message));
            AppendLog(ex.Message);
        }
        finally
        {
            refreshing = false;
            if (!closed)
            {
                RefreshButton.IsEnabled = true;
                Devices.IsEnabled = true;
                UpdateCopyButton();
            }
        }
        if (loaded) await ValidateSelectedAsync();
    }
    private void UpdateCopyButton() => CopyButton.IsEnabled = canCopy && !refreshing && !checkingBeforeCopy && cancellation is null && !closed;
    private void ShowConnection(DeviceConnection connection)
    {
        canCopy = connection.CanCopy;
        DeviceStatus.Text = connection.Message;
        // A routine recheck of the same problem must not resurrect a dismissed banner.
        // A different selected device or a genuinely different state may warn again.
        if (connection.State != DeviceConnectionState.Checking)
        {
            var context = $"{(Devices.SelectedItem as AndroidDevice)?.Serial}:{connection.State}";
            if (context != warningContext) warningDismissed = false;
            warningContext = context;
        }
        ConnectionWarning.IsVisible = !connection.CanCopy && connection.State != DeviceConnectionState.Checking && !warningDismissed;
        ConnectionWarningText.Text = connection.Message;
        UpdateCopyButton();
    }
    private void DismissWarningClick(object? sender, RoutedEventArgs args)
    {
        warningDismissed = true;
        ConnectionWarning.IsVisible = false;
    }
    private async Task<bool> ValidateSelectedAsync(bool showChecking = true)
    {
        var version = ++connectionVersion;
        connectionCheck?.Cancel();
        if (Devices.SelectedItem is not AndroidDevice selected)
        {
            ShowConnection(DeviceConnection.NoSelection(Devices.ItemCount > 0));
            return false;
        }
        using var check = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        connectionCheck = check;
        canCopy = false;
        if (showChecking) ShowConnection(DeviceConnection.Checking);
        else UpdateCopyButton();
        try
        {
            var connection = await adb.CheckConnectionAsync(selected.Serial, check.Token);
            // Ignore obsolete results when the user changes selection or refreshes.
            if (closed || version != connectionVersion || (Devices.SelectedItem as AndroidDevice)?.Serial != selected.Serial) return false;
            ShowConnection(connection);
            return connection.CanCopy;
        }
        catch (OperationCanceledException)
        {
            if (!closed && version == connectionVersion)
                ShowConnection(new(DeviceConnectionState.Failed, "Le téléphone ne répond pas à ADB dans le délai prévu. Reconnectez-le puis cliquez sur Actualiser."));
            return false;
        }
        catch (Exception ex)
        {
            if (!closed && version == connectionVersion)
            {
                ShowConnection(new(DeviceConnectionState.Unavailable, "ADB indisponible ou inaccessible : " + ex.Message));
                AppendLog(ex.Message);
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(connectionCheck, check)) connectionCheck = null;
        }
    }
    private async void DevicesChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (initialized && !refreshing && !checkingBeforeCopy && cancellation is null && !closed)
            await ValidateSelectedAsync();
    }
    private async void RefreshClick(object? sender, RoutedEventArgs args) => await RefreshAsync();
    private void DirectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!initialized) return;
        SourceLabel.Text = Download ? "Source Android" : "Source locale";
        DestinationLabel.Text = Download ? "Destination locale" : "Destination Android";
        SourceBrowse.IsVisible = !Download;
        DestinationBrowse.IsVisible = Download;
        PartialSpaceOption.IsVisible = ArftClient.IsBundled && Download;
        (SourcePath.Text, DestinationPath.Text) = (DestinationPath.Text, SourcePath.Text);
    }
    private async Task BrowseAsync(TextBox input)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choisir le dossier", AllowMultiple = false });
        if (folders.Count > 0) input.Text = folders[0].TryGetLocalPath();
    }
    private async void BrowseSourceClick(object? sender, RoutedEventArgs args) => await BrowseAsync(SourcePath);
    private async void BrowseDestinationClick(object? sender, RoutedEventArgs args) => await BrowseAsync(DestinationPath);
    private static string[] Lines(string? text) => (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private async void CopyClick(object? sender, RoutedEventArgs args)
    {
        if (cancellation is not null || checkingBeforeCopy || refreshing || closed) return;
        if (Devices.SelectedItem is not AndroidDevice device)
        {
            ShowConnection(DeviceConnection.NoSelection(Devices.ItemCount > 0));
            Status.Text = "Choisissez un téléphone avant de copier.";
            return;
        }
        checkingBeforeCopy = true;
        Devices.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        bool accessible;
        try { accessible = await ValidateSelectedAsync(); }
        finally
        {
            checkingBeforeCopy = false;
            if (!closed)
            {
                Devices.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                UpdateCopyButton();
            }
        }
        if (!accessible || closed) return;
        if (string.IsNullOrWhiteSpace(SourcePath.Text) || string.IsNullOrWhiteSpace(DestinationPath.Text))
        {
            Status.Text = "Renseignez la source et la destination.";
            return;
        }
        var options = new CopyOptions(Download ? CopyDirection.AndroidToComputer : CopyDirection.ComputerToAndroid,
            device.Serial, SourcePath.Text, DestinationPath.Text, Lines(Exclusions.Text), Lines(Exceptions.Text),
            EnableResume: ArftClient.IsBundled && ResumeOption.IsChecked == true,
            UseArft: Download && ArftClient.IsBundled && ResumeOption.IsChecked == true,
            AllowPartialSpace: Download && ArftClient.IsBundled && ResumeOption.IsChecked == true && PartialSpaceOption.IsChecked == true);
        Configuration.IsEnabled = false;
        CopyButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        TransferProgress.IsIndeterminate = true;
        TransferProgress.Value = 0;
        TransferPercentageText.Text = "…";
        Status.Text = "Inventaire et préparation…";
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var watch = Stopwatch.StartNew();
        var estimator = new CopyTimeEstimator();
        var completedSuccessfully = false;
        var operationFinished = false;
        void UpdateTiming(bool finished = false)
        {
            var remaining = finished ? (completedSuccessfully ? TimeSpan.Zero : (TimeSpan?)null) : estimator.EstimateRemaining(watch.Elapsed);
            var estimate = remaining is { } time ? (finished ? "" : "≈ ") + CopyTimeEstimator.FormatDuration(time)
                : finished ? "—" : "calcul en cours…";
            TimingText.Text = $"Écoulé : {CopyTimeEstimator.FormatDuration(watch.Elapsed)} · Restant estimé : {estimate}";
        }
        var timingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timingTimer.Tick += (_, _) => UpdateTiming();
        UpdateTiming();
        timingTimer.Start();
        var progress = new Progress<CopyProgress>(p =>
        {
            if (operationFinished) return;
            estimator.Update(p, watch.Elapsed);
            UpdateTiming();
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = p.Percentage;
            TransferPercentageText.Text = $"{p.Percentage:F1} %";
            if (p.Current is "Terminé" or "Pause : espace insuffisant") { CurrentFile.Text = p.Current; return; }
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
        try
        {
            var result = await Task.Run(() => new CopyEngine(adb).CopyAsync(options, Resolve, progress, AppendLog, token), token);
            completedSuccessfully = !result.PausedForSpace && result.Failed == 0;
            Status.Text = result.PausedForSpace
                ? $"Copie en pause faute d’espace : {result.Copied} copiés, {result.Skipped} ignorés. Libérez de l’espace puis relancez les mêmes chemins pour reprendre."
                : $"Copie terminée : {result.Copied} copiés, {result.Skipped} ignorés, {result.Failed} en erreur.";
            AppendLog(Status.Text);
            AppendLog("Journal : " + result.Journal);
        }
        catch (OperationCanceledException) { Status.Text = "Copie annulée. Les fichiers déjà copiés sont conservés."; AppendLog(Status.Text); }
        catch (Exception ex) { Status.Text = "Copie interrompue : " + ex.Message; AppendLog(Status.Text); }
        finally
        {
            operationFinished = true;
            timingTimer.Stop();
            watch.Stop();
            UpdateTiming(finished: true);
            cancellation.Dispose();
            cancellation = null;
            Configuration.IsEnabled = true;
            UpdateCopyButton();
            CancelButton.IsEnabled = false;
            TransferProgress.IsIndeterminate = false;
            if (!closed) await ValidateSelectedAsync(showChecking: false);
        }
    }
    private void CancelClick(object? sender, RoutedEventArgs args)
    {
        cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        Status.Text = "Annulation en cours…";
    }
}
