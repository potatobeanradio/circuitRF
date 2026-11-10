using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins;

/// <summary>
/// The question asked once before a plugin first runs (<c>plugins.md</c> §9.4) — <c>PCellTrustDialog</c>'s layout:
/// what it is, where it came from, exactly what will run, and that it runs with the user's own permissions.
///
/// <para>Returns true (Allow), false (Don't Allow), or null when the window is closed without an answer; null
/// records nothing and asks again next time. <b>Allow is not the default button</b> — a consent prompt cleared by a
/// reflexive Enter has not obtained consent — and Escape is Don't Allow, the safe side.</para>
/// </summary>
public static class PluginTrustDialog
{
    public static async Task<bool?> AskAsync(Window owner, PluginEntry entry)
    {
        bool? answer = null;
        var dialog = Build(entry, a => answer = a);
        await dialog.ShowDialog(owner);
        return answer;
    }

    /// <summary>The prompt, unshown; <paramref name="answered"/> hears Allow or Don't Allow. What a snapshot renders.</summary>
    public static Window Build(PluginEntry entry, System.Action<bool> answered)
    {
        var m = entry.Manifest;
        var dialog = new Window
        {
            Title = "Plugin", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };

        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 3, ColumnSpacing = 10 };
        int r = 0;
        void Row(string label, string? value)
        {
            rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, FontSize = 11, Opacity = 0.6 };
            var v = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "not stated" : value, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            rows.Children.Add(l); rows.Children.Add(v);
            r++;
        }
        Row("Name", m?.Name ?? entry.DisplayName);
        Row("Version", m?.Version);
        Row("Author", m?.Author);
        Row("Source", PluginHost.SourceText(entry));
        Row("Runs", PluginHost.RunsText(entry));

        var dontAllow = new Button { Content = "Don't Allow", Width = 100, FontSize = 12, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var allow     = new Button { Content = "Allow", Width = 100, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center };
        dontAllow.Click += (_, _) => { answered(false); dialog.Close(); };
        allow.Click     += (_, _) => { answered(true); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20, 16), Spacing = 0,
            Children =
            {
                new TextBlock { Text = $"Allow {m?.Name ?? entry.DisplayName} to run?", FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "A plugin runs with your permissions and can read and write your files. Allow it only if you trust where it came from.",
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                },
                PluginUi.Tile(rows),
                new TextBlock
                {
                    Text = "Your answer is remembered on this machine, never in a workspace.",
                    FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0), Children = { dontAllow, allow },
                },
            },
        };
        return dialog;
    }
}
