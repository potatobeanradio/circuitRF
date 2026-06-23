using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Analyses;

public partial class LppBodyView : UserControl
{
    public LppBodyView() => InitializeComponent();

    // Opens a *.gam Save-as picker for the optional recommended-grid output file.
    private async void OnBrowseOutputGridClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LppBodyViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } sp) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Save recommended-grid output (.gam)",
            DefaultExtension       = "gam",
            ShowOverwritePrompt    = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Loadpull grid (*.gam)") { Patterns = ["*.gam"] },
            ],
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            vm.ApplyPickedOutputGridPath(path);
    }
}
