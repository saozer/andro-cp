using AndroCp.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AndroCp.App;

public sealed class AndroidFolderDialog : Window
{
    private readonly AdbClient adb;
    private readonly CopyDevice device;
    private readonly TextBox path;
    private readonly ListBox folders = new() { Height = 290 };
    private readonly TextBlock message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button choose = new() { Content = "Choisir ce dossier" };
    private CancellationTokenSource? listing;
    private string loaded = "/sdcard";
    private bool loading, closed;

    public AndroidFolderDialog(AdbClient adb, CopyDevice device, string? initial)
    {
        this.adb = adb; this.device = device;
        Title = "Dossier Android — " + device.Name;
        Width = 680; Height = 580; MinWidth = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        path = new TextBox { Text = string.IsNullOrWhiteSpace(initial) ? "/sdcard" : initial };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(path);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Nav(string label, Func<string> destination)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) => { if (!loading) await LoadAsync(destination()); };
            navigation.Children.Add(button);
        }
        Nav("Ouvrir", () => path.Text ?? "/sdcard");
        Nav("Parent", () => loaded == "/sdcard" || loaded == "/storage" ? "/storage" : loaded[..loaded.LastIndexOf('/')]);
        Nav("Stockages", () => "/storage");
        Nav("Interne", () => "/sdcard");
        panel.Children.Add(navigation); panel.Children.Add(message); panel.Children.Add(folders);
        folders.DoubleTapped += async (_, _) => { if (!loading && folders.SelectedItem is string name) await LoadAsync(loaded + "/" + name); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler" }; cancel.Click += (_, _) => Close();
        choose.Click += async (_, _) => { if (!loading && await LoadAsync(path.Text ?? "")) Close(loaded); };
        actions.Children.Add(cancel); actions.Children.Add(choose); panel.Children.Add(actions); Content = panel;
        Opened += async (_, _) => await LoadAsync(path.Text ?? "/sdcard");
        Closed += (_, _) => { closed = true; listing?.Cancel(); };
    }
    private async Task<bool> LoadAsync(string directory)
    {
        if (loading || closed) return false;
        loading = true; choose.IsEnabled = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        listing = timeout;
        message.Text = "Lecture des dossiers…";
        try
        {
            directory = AdbClient.AndroidPath(directory);
            await adb.CanonicalPathAsync(device.Serial!, directory, requireDirectory: true, timeout.Token);
            var entries = await adb.ListDirectoryAsync(device.Serial!, directory, timeout.Token, allowRootLink: true);
            if (closed) return false;
            loaded = directory; path.Text = loaded;
            folders.ItemsSource = entries.Where(e => e.IsDirectory).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
            message.Text = "Double-cliquez pour ouvrir un dossier. Les chemins peuvent aussi être saisis manuellement.";
            return true;
        }
        catch (Exception ex) { if (!closed) message.Text = "Dossier inaccessible : " + ex.Message; return false; }
        finally { listing = null; loading = false; if (!closed) choose.IsEnabled = true; }
    }
    public Task<string?> AskAsync(Window owner) { Icon = owner.Icon; return ShowDialog<string?>(owner); }
}
