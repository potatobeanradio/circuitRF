using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-cosim-pattern-2 R-cp2-6 — the settings a saved EM result was made with, read-only, each beside the setup's value now, what
/// differs marked and listed first. A difference is information: running against older data is the point of the block.
/// </summary>
public static class EmRunSettingsDialog
{
    private const string Columns = "*,200,200";

    public static Window Build(string recordPath)
    {
        var (record, refusal) = EmResultRecord.Load(recordPath);
        var rows = new StackPanel { Spacing = 2 };
        string heading;
        if (record is null) heading = refusal!;
        else
        {
            var (settings, whyNot) = EmFieldBlocks.SettingsOf(record, recordPath);
            int differing = settings.Count(s => s.Differs);
            heading = record.Setup is null ? EmResultRecord.SettingsNotRecorded
                    : whyNot is not null ? $"Not compared with the setup now: {whyNot}"
                    : differing == 0 ? "The same as the setup now"
                    : $"{differing} setting{(differing == 1 ? "" : "s")} differ from the setup now";
            if (settings.Count > 0) rows.Children.Add(Row("Setting", "This run", "Setup now", header: true, differs: false));
            foreach (var s in settings) rows.Children.Add(Row(s.Field, s.Recorded ?? "(absent)", whyNot is not null ? "" : s.Now ?? "(absent)", header: false, s.Differs));
        }

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = $"Run Settings: {Path.GetFileName(recordPath)}", Width = 760, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 12,
                Children =
                {
                    new SelectableTextBlock { Text = record?.Summary() ?? "", FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap },
                    new ScrollViewer { MaxHeight = 460, Content = rows },
                    close,
                },
            },
        };
        close.Click += (_, _) => dialog.Close();
        return dialog;
    }

    private static Grid Row(string field, string recorded, string now, bool header, bool differs)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(0, 0, 8, 0) };
        if (differs && Application.Current?.TryFindResource("CrfWarningBrush", out var warning) == true && warning is IBrush brush)
            grid.Background = new SolidColorBrush(((ISolidColorBrush)brush).Color, 0.18);
        void Cell(int column, string text)
        {
            var block = new SelectableTextBlock
            {
                Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 1),
                FontWeight = header || differs ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
        Cell(0, differs ? "≠ " + field : field);
        Cell(1, recorded);
        Cell(2, now);
        return grid;
    }
}
