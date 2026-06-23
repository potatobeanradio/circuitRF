using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Analyses;

public partial class LpBodyView : UserControl
{
    public LpBodyView() => InitializeComponent();

    // Opens a *.gam file picker and stores the result (relative to the schematic dir when possible).
    private async void OnBrowseGridClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LpBodyViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Select loadpull grid (.gam)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Loadpull grid (*.gam)") { Patterns = ["*.gam"] },
                new FilePickerFileType("All files")             { Patterns = ["*"] },
            ],
        });

        var picked = files.FirstOrDefault();
        var path   = picked?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            vm.ApplyPickedGridPath(path);
    }
}
