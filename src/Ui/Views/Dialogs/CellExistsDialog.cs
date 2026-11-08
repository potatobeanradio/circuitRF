using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Cells;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What <see cref="CellExistsDialog"/> was answered with.</summary>
public enum CellExistsChoice { Cancel, Replace, NewName }

/// <summary>
/// brief-isc-1 — the question Create SnP schematic now asks when the cell it would make already exists: <b>Replace</b> writes the new
/// schematic over the cell's schematic (its other views stay), <b>Create</b> makes a cell of the name typed (a free name is offered),
/// Cancel makes nothing. Closing the window is Cancel. A run never asks: it leaves an existing cell alone.
/// </summary>
public static class CellExistsDialog
{
    /// <summary>Asks about <paramref name="cellName"/> in <paramref name="workspaceDir"/>.</summary>
    public static async Task<(CellExistsChoice Choice, string Name)> AskAsync(Window owner, string workspaceDir, string cellName)
    {
        var answer = (CellExistsChoice.Cancel, cellName);
        var dialog = Build(workspaceDir, cellName, a => answer = a);
        await dialog.ShowDialog(owner);
        return answer;
    }

    /// <summary>The first free name <c>&lt;cellName&gt;_2</c>, <c>_3</c>, … in <paramref name="workspaceDir"/>.</summary>
    public static string FreeName(string workspaceDir, string cellName)
    {
        for (int i = 2; ; i++)
        {
            string n = $"{cellName}_{i}";
            if (!Exists(workspaceDir, n)) return n;
        }
    }

    private static bool Exists(string workspaceDir, string name)
        => Directory.Exists(Path.Combine(workspaceDir, name)) || File.Exists(Path.Combine(workspaceDir, name));

    /// <summary>Why <paramref name="name"/> cannot be the new cell's, or null when it can.</summary>
    public static string? Problem(string workspaceDir, string name)
        => NameValidator.Validate(name) is { } bad ? bad
         : Exists(workspaceDir, name) ? $"A cell named {name} exists too."
         : null;

    /// <summary>The dialog, unshown; <paramref name="answered"/> hears the answer as the window closes. What a headless snapshot renders.</summary>
    public static Window Build(string workspaceDir, string cellName, Action<(CellExistsChoice, string)> answered)
    {
        var result = (CellExistsChoice.Cancel, cellName);
        var dialog = new Window
        {
            Title                 = "Create SnP Schematic",
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
        };
        dialog.Closed += (_, _) => answered(result);

        var name    = new TextBox { Text = FreeName(workspaceDir, cellName), FontSize = 12 };
        // Always present, so the buttons below never move as the name is typed.
        var problem = new TextBlock { FontSize = 11, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, MinHeight = 16 };
        var cancel  = new Button { Content = "Cancel", IsCancel = true };
        var replace = new Button { Content = "Replace" };
        var create  = new Button { Content = "Create", IsDefault = true };
        void Check()
        {
            string? why = Problem(workspaceDir, name.Text?.Trim() ?? "");
            problem.Text = why ?? "";
            create.IsEnabled = why is null;
        }
        name.TextChanged += (_, _) => Check();
        Check();
        cancel.Click  += (_, _) => { result = (CellExistsChoice.Cancel, cellName); dialog.Close(); };
        replace.Click += (_, _) => { result = (CellExistsChoice.Replace, cellName); dialog.Close(); };
        create.Click  += (_, _) => { result = (CellExistsChoice.NewName, name.Text!.Trim()); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Cell {cellName} already exists", FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "Replace writes the new schematic over that cell's schematic; its other views and its Data Display stay as they are. " +
                           "Or make a new cell under another name:",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12,
                },
                name,
                problem,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children = { cancel, replace, create },
                },
            },
        };
        return dialog;
    }
}
