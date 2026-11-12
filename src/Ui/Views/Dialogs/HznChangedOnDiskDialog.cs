using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What <see cref="HznChangedOnDiskDialog"/> was answered with.</summary>
public enum HznChangedChoice { Cancel, Reload, SaveAsC3d }

/// <summary>
/// HZN-5 R-hzn5-4 — the project file changed on disk after the design was opened, so the save was refused and nothing was written.
/// The edits that are still in memory are named first, since Reload discards them; Save As .c3d keeps them; there is no merge.
///
/// <para>The list has a fixed height and the buttons a fixed place, so nothing moves with the number of edits.</para>
/// </summary>
public static class HznChangedOnDiskDialog
{
    /// <summary>Most edit lines shown; the rest are counted.</summary>
    public const int MostLines = 6;

    public static async Task<HznChangedChoice> AskAsync(Window owner, string file, string design, IReadOnlyList<string> edits)
    {
        var choice = HznChangedChoice.Cancel;
        var dialog = Build(file, design, edits, c => choice = c);
        await dialog.ShowDialog(owner);
        return choice;
    }

    /// <summary>The edit lines as shown: at most <see cref="MostLines"/>, then how many more.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<string> edits) =>
        edits.Count <= MostLines ? edits : [.. edits.Take(MostLines - 1), $"and {edits.Count - (MostLines - 1)} more"];

    /// <summary>The dialog, unshown; <paramref name="answered"/> hears the button pressed (Cancel when it is just closed).</summary>
    public static Window Build(string file, string design, IReadOnlyList<string> edits, Action<HznChangedChoice> answered)
    {
        var choice = HznChangedChoice.Cancel;
        var dialog = new Window
        {
            Title                 = "Hertzon Project Changed",
            Width                 = 560,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };
        dialog.Closed += (_, _) => answered(choice);

        Button Make(string text, HznChangedChoice value, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => { choice = value; dialog.Close(); };
            return b;
        }

        static Button Tip(Button b, string text) { ToolTip.SetTip(b, text); return b; }

        var list = new StackPanel { Spacing = 2 };
        foreach (string line in Lines(edits))
            list.Children.Add(new TextBlock { Text = line, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (edits.Count == 0) list.Children.Add(new TextBlock { Text = "None.", FontSize = 12 });

        dialog.Content = new StackPanel
        {
            Margin  = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"{file} changed on disk", FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = $"It was saved by another program after {design} was opened here. Nothing was written.",
                    FontSize = 12, TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "Unsaved edits", FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                new Border
                {
                    Height = 20 * MostLines + 8, Padding = new Thickness(8, 4), BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray, CornerRadius = new CornerRadius(3), Child = list,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children =
                    {
                        Tip(Make("Reload", HznChangedChoice.Reload), "Discards the unsaved edits"),
                        Make("Save As .c3d\u2026", HznChangedChoice.SaveAsC3d),
                        Make("Cancel", HznChangedChoice.Cancel, isDefault: true, isCancel: true),
                    },
                },
            },
        };
        return dialog;
    }
}
