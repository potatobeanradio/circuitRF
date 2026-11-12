using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// HZN-4 — File ▸ Export ▸ Hertzon Project…: the target, the design's name and what will be written. Holds no state of its own:
/// <see cref="HznExportDialogViewModel"/> plans and writes through HznExport. Returns true once the project is written.
/// </summary>
public partial class HznExportDialog : Window
{
    public HznExportDialog() => InitializeComponent();

    public HznExportDialog(HznExportDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
        vm.Pick = PickAsync;
        Opened += async (_, _) => await vm.PlanAsync();
    }

    private async Task<string?> PickAsync(bool save)
    {
        var types = new[] { new FilePickerFileType("Hertzon project") { Patterns = ["*.hzn"] } };
        if (save)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Hertzon Project",
                // NO extension on the suggested name: the storage provider appends DefaultExtension itself (SuggestedFileNameGateTests).
                SuggestedFileName = Path.GetFileNameWithoutExtension(((HznExportDialogViewModel)DataContext!).TargetPath),
                DefaultExtension = "hzn",
                ShowOverwritePrompt = true,
                FileTypeChoices = types,
            });
            return file?.TryGetLocalPath();
        }
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add to Hertzon Project", AllowMultiple = false, FileTypeFilter = types,
        });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }
}
