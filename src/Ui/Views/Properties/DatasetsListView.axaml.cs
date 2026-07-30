using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>
/// The docked Datasets section (R-dd-4/5) — alias · filename · status, with inline rename
/// (LostFocus/Enter commits, mirroring every other staged-text field in this panel family) and a
/// per-row Locate…/Re-point… file picker. The picker itself lives here, in code-behind, per the UI
/// firewall — the view model never touches <c>IStorageProvider</c>.
/// </summary>
public partial class DatasetsListView : UserControl
{
    public DatasetsListView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DatasetsListViewModel vm)
            vm.LocateFileRequested = LocateFileAsync;
    }

    private void OnAliasLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DatasetRowViewModel row }) row.CommitAlias();
    }

    private void OnAliasKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DatasetRowViewModel row }) return;
        if (e.Key is Key.Enter or Key.Return) row.CommitAlias();
    }

    private async Task<string?> LocateFileAsync(string currentPathOrName)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return null;

        string? startDir = null;
        try { startDir = Path.GetDirectoryName(currentPathOrName); } catch { /* ignore malformed hint */ }

        IStorageFolder? suggestedDir = null;
        if (startDir is not null && Directory.Exists(startDir))
            suggestedDir = await sp.TryGetFolderFromPathAsync(startDir);

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title                  = "Locate Dataset",
            AllowMultiple          = false,
            SuggestedStartLocation = suggestedDir,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Data Files")
                {
                    Patterns = new[] { "*.s1p", "*.s2p", "*.s3p", "*.s4p",
                                       "*.s5p", "*.s6p", "*.snp", "*.ts", "*.npy",
                                       "*.spl", "*.lpcwave" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        return files.Count == 1 ? files[0].Path.LocalPath : null;
    }
}
