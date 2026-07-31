using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Shows what an import made of a kit.
///
/// <para>Shown for every outcome, success included. The interesting result is almost never
/// all-or-nothing: a kit typically has parts circuitRF can place, drawings it cannot yet read, and
/// files it does not recognise at all, and the user needs to see the difference. Each finding
/// carries its suggested action, so an unreadable kit still tells the reader where to go next.</para>
/// </summary>
public static class PdkImportReportDialog
{
    public static async Task ShowAsync(Window owner, PdkImportReport report)
    {
        var (headline, colour) = report.Status switch
        {
            PdkImportStatus.Imported          => ("Imported", Brushes.MediumSeaGreen),
            PdkImportStatus.PartiallyImported => ("Partially imported", Brushes.Goldenrod),
            PdkImportStatus.NotRecognized     => ("Nothing usable found", Brushes.IndianRed),
            _                                 => ("Could not be read", Brushes.IndianRed),
        };

        var closeBtn = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 88,
        };

        var dialog = new Window
        {
            Title = $"Import PDK — {report.KitName}",
            Width = 720,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        closeBtn.Click += (_, _) => dialog.Close();

        // Selectable so the whole report can be copied out — an import summary is something people
        // paste into a bug report or a note, and a plain TextBlock silently refuses that.
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new SelectableTextBlock
        {
            Text = report.ToSummary(),
            FontFamily = new FontFamily("monospace"),
            TextWrapping = TextWrapping.Wrap,
        });

        dialog.Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Avalonia.Controls.Dock.Top,
                    Spacing = 2,
                    Margin  = new Avalonia.Thickness(0, 0, 0, 12),
                    Children =
                    {
                        new TextBlock { Text = headline, Foreground = colour,
                                        FontSize = 17, FontWeight = FontWeight.SemiBold },
                        new SelectableTextBlock { Text = report.RootPath, Opacity = 0.7,
                                                  TextWrapping = TextWrapping.Wrap },
                    },
                },
                new StackPanel
                {
                    [DockPanel.DockProperty] = Avalonia.Controls.Dock.Bottom,
                    Margin = new Avalonia.Thickness(0, 12, 0, 0),
                    Children = { closeBtn },
                },
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = body,
                },
            },
        };

        await dialog.ShowDialog(owner);
    }
}
