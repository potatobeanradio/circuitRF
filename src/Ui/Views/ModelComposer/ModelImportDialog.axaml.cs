using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Modeling;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>The importer. It answers the data set the reader read, or null when cancelled; the import itself is the
/// window's run.</summary>
public partial class ModelImportDialog : Window
{
    /// <summary>Help: what each kind of data needs, and how to prepare it.</summary>
    internal const string HelpPage = "reference/model-composer.html", HelpAnchor = "preparing";

    public ModelImportDialog() => InitializeComponent();

    public ModelImportDialog(ModelImportViewModel vm) : this()
    {
        DataContext = vm;
        CancelButton.Click += (_, _) => Close(null);
        HelpButton.Click += (_, _) => DocLauncher.Open(HelpPage, HelpAnchor);
        ImportButton.Click += (_, _) =>
        {
            if (vm.Build(out string? why) is { } set) Close(set);
            else vm.Line = why ?? "";
        };
        FilesButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Data files",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    // Every port count a passive block can have (designer round 28: a board's .s6p was hidden behind All Files).
                    new FilePickerFileType("Data")
                    {
                        Patterns = [.. CircuitRF.Ui.Controls.TouchstonePatterns.Snp,
                                    "*.csv", "*.tsv", "*.txt", "*.npy", "*.npz", "*.mat", "*.ds", "*.spl", "*.lpcwave"],
                    },
                    new FilePickerFileType("All Files") { Patterns = ["*.*"] },
                ],
            });
            if (files.Count > 0) vm.SetSources([.. files.Select(f => f.Path.LocalPath)]);
        };
        FolderButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "A folder of data files" });
            if (folders.Count > 0) vm.SetSources([folders[0].Path.LocalPath]);
        };
    }
}
