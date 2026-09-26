using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-em3d-24 R-em3d24-2a — the consent step before the install assistant fetches anything. The text is
/// <c>SolverInstaller.Consent</c>'s, the same the CLI prints without <c>--yes</c>: the program and version,
/// every upstream URL, where it goes, what it cost when measured and on what machine, the ParMETIS sentence
/// for Palace, and that it runs in the background and can be cancelled. <b>Nothing is downloaded before
/// Install is pressed</b>; Cancel (or closing the window) creates nothing.
///
/// <para>Selectable, because the URLs are the part a cautious user — or their IT department — will want to
/// copy.</para>
/// </summary>
public static class SolverInstallConsentDialog
{
    public static async Task<bool> AskAsync(Window owner, string program, string version, string consentText)
    {
        bool yes = false;
        var dialog = new Window
        {
            Title                 = $"Install {program}",
            Width                 = 640,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
        };

        var install = new Button { Content = $"_Install {program} {version}", IsDefault = true };
        var cancel  = new Button { Content = "Cancel", IsCancel = true };
        install.Click += (_, _) => { yes = true; dialog.Close(); };
        cancel.Click  += (_, _) => { yes = false; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Install {program} {version}?", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new SelectableTextBlock { Text = consentText, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                },
                new StackPanel
                {
                    Orientation         = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing             = 8,
                    Children            = { cancel, install },
                },
            },
        };

        await dialog.ShowDialog(owner);
        return yes;
    }
}
