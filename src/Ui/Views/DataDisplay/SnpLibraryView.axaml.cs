using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class SnpLibraryView : UserControl
{
    public SnpLibraryView()
    {
        InitializeComponent();

        // Note: on Linux, Avalonia 12 does not implement the XDND protocol, so
        // these handlers are never invoked by the OS.  They remain wired here so
        // the feature lights up automatically if/when Avalonia adds Linux DnD support.
        AddHandler(DragDrop.DropEvent,     OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Use Contains() rather than TryGetFiles(): on some platforms the file
        // data is not transferred until the Drop event, so TryGetFiles() returns
        // empty during DragOver and would wrongly reject valid file drags.
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return;
        if (DataContext is not SnpLibraryViewModel vm) return;

        foreach (var item in items.OfType<IStorageFile>())
        {
            string localPath   = item.Path.LocalPath;
            string droppedName = Path.GetFileName(localPath);

            // If the dropped file's name matches a broken entry, restore it.
            var broken = vm.Entries.FirstOrDefault(e2 =>
                e2.IsBroken &&
                string.Equals(Path.GetFileName(e2.FilePath), droppedName,
                              System.StringComparison.OrdinalIgnoreCase));

            if (broken != null)
                await vm.RestoreBrokenEntry(broken, localPath);
            else
                await vm.LoadFileAsync(localPath);
        }

        e.Handled = true;
    }
}
