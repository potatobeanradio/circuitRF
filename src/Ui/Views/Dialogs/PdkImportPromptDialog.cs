using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Asks whether the kit to import is a folder or an archive, before opening the right picker.
///
/// <para>A single picker cannot do both on every platform — folder and file selection are separate
/// system dialogs — and guessing wrong sends the user to a picker that cannot see what they are
/// looking for. Asking first is one extra click and always lands them in the right place.</para>
/// </summary>
public static class PdkImportPromptDialog
{
    public enum Choice { Folder, Archive }

    public static async Task<Choice?> PickAsync(Window owner)
    {
        Choice? result = null;

        var dialog = new Window
        {
            Title = "Import PDK",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var folderBtn  = new Button { Content = "Kit _folder…",  HorizontalAlignment = HorizontalAlignment.Stretch };
        var archiveBtn = new Button { Content = "Kit _archive (.zip)…", HorizontalAlignment = HorizontalAlignment.Stretch };
        var cancelBtn  = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };

        folderBtn.Click  += (_, _) => { result = Choice.Folder;  dialog.Close(); };
        archiveBtn.Click += (_, _) => { result = Choice.Archive; dialog.Close(); };
        cancelBtn.Click  += (_, _) => { result = null;           dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Where is the kit?",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 15,
                },
                new TextBlock
                {
                    Text = "Choose the kit's top-level folder, or a .zip containing it. " +
                           "circuitRF will report what it finds inside.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                },
                folderBtn,
                archiveBtn,
                cancelBtn,
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
