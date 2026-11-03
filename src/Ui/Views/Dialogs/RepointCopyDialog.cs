using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief vendorlib-12 R-vl12-3d: after Copy to Workspace…, the one question — which placed parts point at the copy.
/// Pointing this instance is the default; closing the window leaves every part on the library model.
/// </summary>
public static class RepointCopyDialog
{
    public enum Choice { ThisInstance, EverySame, Leave }

    /// <summary>The window, built but not shown — what <see cref="AskAsync"/> shows and what a snapshot photographs.</summary>
    public static Window Build(string part, string copyName, int sameInSchematic, Action<Choice> answered)
    {
        var dialog = new Window
        {
            Title = "Copy to Workspace",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var one   = new RadioButton { GroupName = "repoint", Content = "Point this instance at the copy", IsChecked = true };
        var every = new RadioButton
        {
            GroupName = "repoint",
            Content = sameInSchematic > 1
                ? $"Point every {part} in this schematic at the copy ({sameInSchematic})"
                : $"Point every {part} in this schematic at the copy",
        };
        var leave = new RadioButton { GroupName = "repoint", Content = "Leave them on the library part" };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        Choice result = Choice.Leave;
        ok.Click += (_, _) =>
        {
            result = one.IsChecked == true ? Choice.ThisInstance : every.IsChecked == true ? Choice.EverySame : Choice.Leave;
            dialog.Close();
        };
        dialog.Closed += (_, _) => answered(result);

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"{part} was copied into this workspace as {copyName}.",
                    FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 4, 0, 4), Children = { one, every, leave } },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
            },
        };
        return dialog;
    }

    public static async Task<Choice> AskAsync(Window owner, string part, string copyName, int sameInSchematic)
    {
        Choice answer = Choice.Leave;
        var dialog = Build(part, copyName, sameInSchematic, c => answer = c);
        await dialog.ShowDialog(owner);
        return answer;
    }
}
