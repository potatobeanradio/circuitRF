using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.VendorLibraries;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The small confirm step of File ▸ Add Vendor Library… (brief vendorlib-1 R-vl1-4a): the name parts
/// will reach the library by — the folder's own name, editable — over what indexing the folder
/// found: the part count, the folder groups and any unreadable-file sentence. Closing the window is
/// Cancel.
/// </summary>
public static class AddVendorLibraryDialog
{
    /// <summary>The name to add the library under, or null when the user cancelled.</summary>
    public static async Task<string?> AskAsync(Window owner, string folder, string defaultName,
                                               IReadOnlyList<string> found,
                                               CircuitRF.Design.VendorLibraries.ModelLibrary.LibrarySignatureInspection? signature = null)
    {
        string? result = null;
        var dialog = new Window
        {
            Title                 = "Add Vendor Library",
            Width                 = 560,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };

        var name    = new TextBox { Text = defaultName, MinWidth = 260 };
        var problem = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false,
            Foreground = dialog.TryFindResource("CrfWarningBrush", out var brush) && brush is IBrush b ? b : Brushes.OrangeRed,
        };

        var add = new Button { Content = "Add", IsDefault = true, MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center };
        add.Click += (_, _) =>
        {
            string n = (name.Text ?? "").Trim();
            if (VendorLibraryRefs.NameProblem(n) is { } why) { problem.Text = why; problem.IsVisible = true; return; }
            result = n;
            dialog.Close();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => dialog.Close();

        var lines = new StackPanel { Spacing = 3 };
        foreach (string line in found)
            lines.Children.Add(new SelectableTextBlock { Text = line, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        // brief vendorlib-15 §5c: a signed library says who signed it, and asks once about a key not yet trusted.
        if (signature is not null && Controls.LibrarySignatureLine.For(signature) is { } signed)
            lines.Children.Add(signed);

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new SelectableTextBlock { Text = folder, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                lines,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing     = 8,
                    Children    =
                    {
                        new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center },
                        name,
                    },
                },
                new TextBlock
                {
                    Text = "Placed parts reach the library by this name. The library is referenced where it is, never copied.",
                    FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
                },
                problem,
                new StackPanel
                {
                    Orientation         = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing             = 8,
                    Children            = { cancel, add },
                },
            },
        };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };

        await dialog.ShowDialog(owner);
        return result;
    }
}
