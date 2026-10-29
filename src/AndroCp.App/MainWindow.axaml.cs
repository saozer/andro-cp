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
    private bool initialized;
    public MainWindow()
    {
        InitializeComponent();
        initialized = true;
        Opened += async (_, _) => await RefreshAsync();
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
        RefreshButton.IsEnabled = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var devices = await adb.DevicesAsync(timeout.Token);
            Devices.ItemsSource = devices;
            Devices.SelectedIndex = devices.Count > 0 ? 0 : -1;
            DeviceStatus.Text = devices.Count == 0 ? "Aucun appareil. Vérifiez le câble USB, les pilotes et le débogage USB."
                : $"{devices.Count} appareil(s). « unauthorized » : acceptez l’autorisation sur le téléphone. « offline » : reconnectez-le.";
        }
        catch (Exception ex)
        {
            DeviceStatus.Text = "ADB indisponible : placez platform-tools près de l’application ou installez ADB dans le PATH.";
            AppendLog(ex.Message);
        }
        finally { RefreshButton.IsEnabled = true; }
    }
    private async void RefreshClick(object? sender, RoutedEventArgs args) => await RefreshAsync();
    private void DirectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!initialized) return;
        SourceLabel.Text = Download ? "Source Android" : "Source locale";
        DestinationLabel.Text = Download ? "Destination locale" : "Destination Android";
        SourceBrowse.IsVisible = !Download;
        DestinationBrowse.IsVisible = Download;
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
        if (Devices.SelectedItem is not AndroidDevice device || device.State != "device")
        {
            Status.Text = "Choisissez un appareil connecté et autorisé (état « device »).";
            return;
        }
        if (string.IsNullOrWhiteSpace(SourcePath.Text) || string.IsNullOrWhiteSpace(DestinationPath.Text))
        {
            Status.Text = "Renseignez la source et la destination.";
            return;
        }
        var options = new CopyOptions(Download ? CopyDirection.AndroidToComputer : CopyDirection.ComputerToAndroid,
            device.Serial, SourcePath.Text, DestinationPath.Text, Lines(Exclusions.Text), Lines(Exceptions.Text));
        Configuration.IsEnabled = false;
        CopyButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        TransferProgress.IsIndeterminate = true;
        Status.Text = "Inventaire et préparation…";
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var watch = Stopwatch.StartNew();
        var progress = new Progress<CopyProgress>(p =>
        {
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = p.Total == 0 ? 100 : 100.0 * p.Completed / p.Total;
            if (p.Current == "Terminé") { CurrentFile.Text = "Terminé"; return; }
            Status.Text = $"{p.Completed}/{p.Total} fichiers traités · {p.Bytes / 1073741824.0:F2}/{p.TotalBytes / 1073741824.0:F2} Gio · {p.Bytes / 1048576.0 / Math.Max(1, watch.Elapsed.TotalSeconds):F1} Mio/s (moyenne)";
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
            Status.Text = $"Copie terminée : {result.Copied} copiés, {result.Skipped} ignorés, {result.Failed} en erreur.";
            AppendLog(Status.Text);
            AppendLog("Journal : " + result.Journal);
        }
        catch (OperationCanceledException) { Status.Text = "Copie annulée. Les fichiers déjà copiés sont conservés."; AppendLog(Status.Text); }
        catch (Exception ex) { Status.Text = "Copie interrompue : " + ex.Message; AppendLog(Status.Text); }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            Configuration.IsEnabled = true;
            CopyButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            TransferProgress.IsIndeterminate = false;
        }
    }
    private void CancelClick(object? sender, RoutedEventArgs args)
    {
        cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        Status.Text = "Annulation en cours…";
    }
}
