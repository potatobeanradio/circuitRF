using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Cells;
using Material.Icons;
using Material.Icons.Avalonia;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What <see cref="HznConvertDialog"/> was answered with.</summary>
/// <param name="CellDir">The cell folder: the chosen folder with the cell's name.</param>
public sealed record HznConvertAnswer(string CellDir, bool AddMaterials, bool ReplaceView);

/// <summary>
/// HZN-6 R-hzn6-2 — Convert to .c3d…: the cell name, the folder it goes in, and the materials choice for one Hertzon design.
/// The dialog collects three answers and starts nothing: <c>HznConvert.Convert</c>, the function <c>convert file.hzn</c> also
/// calls, does the work, and its refusals are the caller's to post.
///
/// <para>The line under the fields is always present, so the buttons never move as a problem appears or clears.</para>
/// </summary>
public static class HznConvertDialog
{
    /// <summary>Asks about <paramref name="design"/>; null when cancelled.</summary>
    public static async Task<HznConvertAnswer?> AskAsync(Window owner, string design, string startFolder)
    {
        HznConvertAnswer? answer = null;
        var dialog = Build(design, startFolder, a => answer = a);
        await dialog.ShowDialog(owner);
        return answer;
    }

    /// <summary>Why a cell of <paramref name="name"/> in <paramref name="folder"/> cannot be written, or null when it can.</summary>
    public static string? Problem(string folder, string name, bool replace)
    {
        if (NameValidator.Validate(name) is { } bad) return bad;
        if (!Directory.Exists(folder)) return "That folder does not exist.";
        string cell = Path.Combine(folder, name);
        if (File.Exists(cell)) return $"{name} is a file.";
        if (!Directory.Exists(cell)) return null;
        if (!File.Exists(Path.Combine(cell, CellFolder.CcellFileName))) return $"{name} is a folder that is not a cell.";
        string view = Path.Combine(CellFolder.SubFolderPath(cell, ViewType.ThreeD), name + CellFolder.ViewExtension(ViewType.ThreeD));
        return File.Exists(view) && !replace ? $"{name} has a 3D view." : null;
    }

    /// <summary>The first name from <paramref name="design"/> that is a valid cell name, as a suggestion.</summary>
    public static string SuggestedName(string design)
    {
        var chars = design.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return new string(chars).Trim('_', '-', '.') is { Length: > 0 } s ? s : "HertzonDesign";
    }

    /// <summary>The dialog, unshown; <paramref name="answered"/> hears the answer (null: cancelled) as the window closes.</summary>
    public static Window Build(string design, string startFolder, Action<HznConvertAnswer?> answered, string? name = null)
    {
        HznConvertAnswer? result = null;
        var dialog = new Window
        {
            Title                 = "Convert to .c3d",
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };
        dialog.Closed += (_, _) => answered(result);

        var cellName = new TextBox { Text = name ?? SuggestedName(design), FontSize = 12 };
        var folder   = new TextBox { Text = startFolder, FontSize = 12 };
        var pick = new Button
        {
            Margin  = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 3),
            Content = new MaterialIcon { Kind = MaterialIconKind.FolderOutline, Width = 16, Height = 16 },
        };
        ToolTip.SetTip(pick, "A folder");
        var add     = new CheckBox { Content = "Add missing materials to the technology", FontSize = 12 };
        var replace = new CheckBox { Content = "Replace the cell's 3D view", FontSize = 12 };
        var problem = new TextBlock { FontSize = 11, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, MinHeight = 32 };
        var cancel  = new Button { Content = "Cancel", IsCancel = true };
        var convert = new Button { Content = "Convert", IsDefault = true };

        void Check()
        {
            string? why = Problem((folder.Text ?? "").Trim(), (cellName.Text ?? "").Trim(), replace.IsChecked == true);
            problem.Text = why ?? "";
            convert.IsEnabled = why is null;
        }
        cellName.TextChanged += (_, _) => Check();
        folder.TextChanged   += (_, _) => Check();
        replace.IsCheckedChanged += (_, _) => Check();
        Check();

        pick.Click += async (_, _) =>
        {
            try
            {
                var start = await dialog.StorageProvider.TryGetReachableFolderAsync(folder.Text);
                var picked = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Folder for the cell", AllowMultiple = false, SuggestedStartLocation = start,
                });
                if (picked.Count > 0 && picked[0].TryGetLocalPath() is { Length: > 0 } chosen) folder.Text = chosen;
            }
            catch (Exception ex) { problem.Text = "That folder could not be used: " + ex.Message; }
        };
        cancel.Click  += (_, _) => dialog.Close();
        convert.Click += (_, _) =>
        {
            result = new HznConvertAnswer(Path.Combine((folder.Text ?? "").Trim(), (cellName.Text ?? "").Trim()),
                                          add.IsChecked == true, replace.IsChecked == true);
            dialog.Close();
        };

        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(pick, 1);
        folderRow.Children.Add(folder);
        folderRow.Children.Add(pick);

        dialog.Content = new StackPanel
        {
            Margin  = new Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"Design {design}", FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Cell name", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) },
                cellName,
                new TextBlock { Text = "Folder", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) },
                folderRow,
                add,
                replace,
                problem,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children = { cancel, convert },
                },
            },
        };
        return dialog;
    }
}
