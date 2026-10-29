using AndroCp.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AndroCp.App;

public sealed class NameDialog : Window
{
    private NameDecision result = new(DecisionKind.Cancel);
    public NameDialog(NameProblem problem)
    {
        Title = "Décision requise — Nom de destination";
        Width = 620; Height = 400; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = problem.IsDirectory ? "Renommer ce dossier ?" : "Renommer ce fichier ?", FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock { Text = problem.Original, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = problem.Reason, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange });
        var input = new TextBox { Text = problem.Suggested, Watermark = "Nom de destination" };
        panel.Children.Add(input);
        var apply = new CheckBox { Content = "Appliquer ce choix aux problèmes similaires pour cette copie" };
        panel.Children.Add(apply);
        panel.Children.Add(new TextBlock { Text = "Le choix groupé utilise les noms corrigés proposés, pas le nom manuel ci-dessus.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string text, DecisionKind kind)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) =>
            {
                result = new(kind, input.Text ?? "", apply.IsChecked == true);
                Close(result);
            };
            buttons.Children.Add(button);
        }
        Add("Annuler la copie", DecisionKind.Cancel);
        Add("Ignorer", DecisionKind.Skip);
        Add("Utiliser ce nom", DecisionKind.Rename);
        panel.Children.Add(buttons);
        Content = panel;
    }
    public async Task<NameDecision> AskAsync(Window owner)
    {
        Icon = owner.Icon;
        await ShowDialog<NameDecision?>(owner);
        return result;
    }
}
