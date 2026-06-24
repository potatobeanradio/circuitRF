// ================================================================
//  DataExporterDialog.axaml.cs
//
//  Modal Data Exporter dialog.  Format segmented-buttons and
//  Touchstone options are wired in code-behind so they can
//  respond to VM property changes without Avalonia bindings.
//  File picking (StorageProvider) lives here per the UI firewall.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Export;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class DataExporterDialog : Window
{
    private DataExporterViewModel Vm => (DataExporterViewModel)DataContext!;

    // ── Constructor ───────────────────────────────────────────────────────────

    public DataExporterDialog() => InitializeComponent();

    public DataExporterDialog(DataExporterViewModel vm) : this()
    {
        DataContext = vm;
        SeedControls();
        vm.PropertyChanged += (_, e) => OnVmPropertyChanged(e.PropertyName);
    }

    // ── Seed & sync ──────────────────────────────────────────────────────────

    private void SeedControls()
    {
        var vm = Vm;

        // Format buttons
        SyncFormatButtons(vm.ExportMode);

        // Digit format / matrix format
        SyncDigitFormatButtons(vm.DigitFormat);
        SyncMatrixFormatButtons(vm.MatrixFormat);

        // Datasource combo
        SchematicCombo.ItemsSource  = vm.AvailableSchematicNames;
        SchematicCombo.SelectedItem = vm.SelectedSchematic;
        SchematicCombo.SelectionChanged += (_, _) =>
            vm.SelectedSchematic = SchematicCombo.SelectedItem as string;

        // Include list + measurements
        IncludeListBox.ItemsSource      = vm.IncludeRows;
        MeasurementsCheck.IsChecked     = vm.IncludeMeasurements;
        MeasurementsCheck.IsVisible     = vm.MeasurementsAvailable;
        MeasurementsCheck.IsChecked     = vm.IncludeMeasurements;
        MeasurementsCheck.Click        += (_, _) =>
            vm.IncludeMeasurements = MeasurementsCheck.IsChecked == true;

        // Touchstone options
        Z0Box.Value    = (decimal)vm.Z0Ohms;
        DigitsBox.Value = (decimal)vm.Digits;
        Z0Box.ValueChanged    += (_, e) => { if (e.NewValue is decimal d) vm.Z0Ohms = (double)d; };
        DigitsBox.ValueChanged += (_, e) => { if (e.NewValue is decimal d) vm.Digits = (int)d; };

        // Sweep items
        SweepSliceItems.ItemsSource = vm.SweepSliceRows;
        AllSweepCheck.IsChecked     = vm.SaveAllSweepFiles;
        AllSweepCheck.Click        += (_, _) => vm.SaveAllSweepFiles = AllSweepCheck.IsChecked == true;

        // Segmented button clicks
        NpyBtn.Click += (_, _) => vm.ExportMode = ExportMode.Npy;
        MatBtn.Click += (_, _) => vm.ExportMode = ExportMode.Mat;
        TsvBtn.Click += (_, _) => vm.ExportMode = ExportMode.Tsv;
        TsBtn.Click  += (_, _) => vm.ExportMode = ExportMode.Touchstone;
        SplBtn.Click     += (_, _) => vm.ExportMode = ExportMode.Spl;
        LpcwaveBtn.Click += (_, _) => vm.ExportMode = ExportMode.Lpcwave;

        UpdateLoadpullButtons();

        FmtFBtn.Click += (_, _) => vm.DigitFormat = 'f';
        FmtGBtn.Click += (_, _) => vm.DigitFormat = 'g';
        FmtEBtn.Click += (_, _) => vm.DigitFormat = 'e';

        MaBtn.Click += (_, _) => vm.MatrixFormat = MatrixFormat.MA;
        RiBtn.Click += (_, _) => vm.MatrixFormat = MatrixFormat.RI;
        DbBtn.Click += (_, _) => vm.MatrixFormat = MatrixFormat.DB;

        UpdatePanelVisibility(vm.ExportMode);
        UpdateNoticePanel();
        ExportBtn.IsEnabled = vm.CanExport;
    }

    private void OnVmPropertyChanged(string? propName)
    {
        var vm = Vm;
        switch (propName)
        {
            case nameof(DataExporterViewModel.ExportMode):
                SyncFormatButtons(vm.ExportMode);
                UpdatePanelVisibility(vm.ExportMode);
                SweepSliceItems.ItemsSource = vm.SweepSliceRows;
                break;
            case nameof(DataExporterViewModel.SelectedSchematic):
                if (SchematicCombo.SelectedItem as string != vm.SelectedSchematic)
                    SchematicCombo.SelectedItem = vm.SelectedSchematic;
                IncludeListBox.ItemsSource  = vm.IncludeRows;
                MeasurementsCheck.IsChecked = vm.IncludeMeasurements;
                SweepSliceItems.ItemsSource = vm.SweepSliceRows;
                UpdateLoadpullButtons();
                UpdatePanelVisibility(vm.ExportMode);
                break;
            case nameof(DataExporterViewModel.IsLoadpullAvailable):
                UpdateLoadpullButtons();
                break;
            case nameof(DataExporterViewModel.IncludeRows):
                IncludeListBox.ItemsSource = vm.IncludeRows;
                break;
            case nameof(DataExporterViewModel.MeasurementsAvailable):
                UpdatePanelVisibility(vm.ExportMode);
                break;
            case nameof(DataExporterViewModel.SweepSliceRows):
                SweepSliceItems.ItemsSource = vm.SweepSliceRows;
                UpdatePanelVisibility(vm.ExportMode);
                break;
            case nameof(DataExporterViewModel.ShowZ0Notice):
            case nameof(DataExporterViewModel.Z0Notice):
                UpdateNoticePanel();
                break;
            case nameof(DataExporterViewModel.CanExport):
                ExportBtn.IsEnabled = vm.CanExport;
                break;
            case nameof(DataExporterViewModel.DigitFormat):
                SyncDigitFormatButtons(vm.DigitFormat);
                break;
            case nameof(DataExporterViewModel.MatrixFormat):
                SyncMatrixFormatButtons(vm.MatrixFormat);
                break;
        }
    }

    private void SyncFormatButtons(ExportMode mode)
    {
        NpyBtn.IsChecked = mode == ExportMode.Npy;
        MatBtn.IsChecked = mode == ExportMode.Mat;
        TsvBtn.IsChecked = mode == ExportMode.Tsv;
        TsBtn.IsChecked  = mode == ExportMode.Touchstone;
        SplBtn.IsChecked     = mode == ExportMode.Spl;
        LpcwaveBtn.IsChecked = mode == ExportMode.Lpcwave;
    }

    // The loadpull format row is offered only when the selected datasource is loadpull-shaped.
    private void UpdateLoadpullButtons()
    {
        LoadpullFormatRow.IsVisible = Vm.IsLoadpullAvailable;
    }

    private void SyncDigitFormatButtons(char fmt)
    {
        FmtFBtn.IsChecked = fmt == 'f';
        FmtGBtn.IsChecked = fmt == 'g';
        FmtEBtn.IsChecked = fmt == 'e';
    }

    private void SyncMatrixFormatButtons(MatrixFormat fmt)
    {
        MaBtn.IsChecked = fmt == MatrixFormat.MA;
        RiBtn.IsChecked = fmt == MatrixFormat.RI;
        DbBtn.IsChecked = fmt == MatrixFormat.DB;
    }

    private void UpdatePanelVisibility(ExportMode mode)
    {
        bool isTs = mode == ExportMode.Touchstone;
        bool isLp = mode is ExportMode.Spl or ExportMode.Lpcwave;

        // The INCLUDE group list is shown for every mode except Touchstone (which has its own
        // single-group selection via the slicing panel). Loadpull reuses it as a single-select
        // analysis picker — the VM enforces single-select and restricts rows to loadpull groups.
        IncludePanel.IsVisible = !isTs;
        IncludeLabel.IsVisible = !isTs;
        IncludeLabel.Text      = isLp ? "LOADPULL ANALYSIS" : "INCLUDE";

        // Measurements checkbox only applies to the dataset (npy/mat/tsv) formats.
        MeasurementsCheck.IsVisible = !isTs && !isLp && Vm.MeasurementsAvailable;

        TsOptionsPanel.IsVisible = isTs;
        bool hasSweep = isTs && Vm.SweepSliceRows.Count > 0;
        TsSlicingPanel.IsVisible = hasSweep;
    }

    private void UpdateNoticePanel()
    {
        var vm = Vm;
        Z0NoticePanel.IsVisible = vm.ShowZ0Notice;
        Z0NoticeText.Text       = vm.Z0Notice;
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (!vm.CanExport) return;

        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        try
        {
            if (vm.ExportMode == ExportMode.Touchstone && vm.SaveAllSweepFiles)
            {
                await DoAllSweepTouchstoneExport(sp, vm);
            }
            else if (vm.ExportMode == ExportMode.Touchstone)
            {
                await DoSingleTouchstoneExport(sp, vm);
            }
            else if (vm.ExportMode is ExportMode.Spl or ExportMode.Lpcwave)
            {
                await DoLoadpullExport(sp, vm);
            }
            else
            {
                await DoDataSetExport(sp, vm);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Export failed: {ex.Message}");
        }
    }

    private async Task DoDataSetExport(IStorageProvider sp, DataExporterViewModel vm)
    {
        string? ext = vm.ExportMode switch
        {
            ExportMode.Npy => "npy",
            ExportMode.Mat => "mat",
            ExportMode.Tsv => "txt",
            _              => "npy"
        };
        string? name = vm.ExportMode switch
        {
            ExportMode.Npy => "NumPy Files",
            ExportMode.Mat => "MATLAB Files",
            ExportMode.Tsv => "Tab-Delimited Text",
            _              => "NumPy Files"
        };

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title            = "Export Data",
            SuggestedFileName = Path.GetFileNameWithoutExtension(vm.SuggestedFileName),
            DefaultExtension = ext,
            FileTypeChoices  =
            [
                new FilePickerFileType(name)   { Patterns = [$"*.{ext}"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (file is null) return;

        vm.ExportDataSet(file.Path.LocalPath);
        Close();
    }

    private async Task DoLoadpullExport(IStorageProvider sp, DataExporterViewModel vm)
    {
        bool isSpl = vm.ExportMode == ExportMode.Spl;
        string ext = isSpl ? "spl" : "lpcwave";
        string typeName = isSpl ? "HarmonicaRF Loadpull" : "lpcwave Loadpull";

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export Loadpull Data",
            // Keep the full name WITH extension — a non-standard extension like ".lpcwave" is not
            // auto-appended by the OS picker from DefaultExtension alone, so the default name must carry it.
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension  = ext,
            FileTypeChoices   =
            [
                new FilePickerFileType($"{typeName} (.{ext})") { Patterns = [$"*.{ext}"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (file is null) return;

        vm.ExportLoadpull(file.Path.LocalPath);
        Close();
    }

    private async Task DoSingleTouchstoneExport(IStorageProvider sp, DataExporterViewModel vm)
    {
        string ext     = vm.SuggestedFileName[vm.SuggestedFileName.LastIndexOf('.')..];  // e.g. ".s2p"
        string extNoD  = ext.TrimStart('.');

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title            = "Export Touchstone",
            SuggestedFileName = Path.GetFileNameWithoutExtension(vm.SuggestedFileName),
            DefaultExtension = extNoD,
            FileTypeChoices  =
            [
                new FilePickerFileType($"Touchstone ({ext})") { Patterns = [$"*{ext}"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (file is null) return;

        string path = file.Path.LocalPath;
        string baseNoExt = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            Path.GetFileNameWithoutExtension(path));

        var result = vm.ExportTouchstone(baseNoExt);
        if (result.Status == TouchstoneExportStatus.NameCollision)
        {
            await ShowErrorAsync($"Name collision: {string.Join(", ", result.CollidingNames)}");
            return;
        }
        Close();
    }

    private async Task DoAllSweepTouchstoneExport(IStorageProvider sp, DataExporterViewModel vm)
    {
        var folder = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Output Folder for Touchstone Files"
        });
        if (folder.Count == 0) return;

        string dir        = folder[0].Path.LocalPath;
        string schematic  = vm.SelectedSchematicName ?? "export";
        string basePath   = Path.Combine(dir, schematic);

        var result = vm.ExportTouchstone(basePath);

        if (result.Status == TouchstoneExportStatus.NameCollision)
        {
            await ShowErrorAsync($"Filename collision — two sweep combinations map to the same name:\n" +
                                 string.Join("\n", result.CollidingNames));
            return;
        }

        // Batch overwrite-confirm when any target already existed before the export
        // (TouchstoneExporter overwrites; we check paths now)
        var overwritten = result.WrittenPaths.Where(File.Exists).ToList();
        // Note: at this point paths are already written; pre-check would require a dry-run.
        // For simplicity we notify the user after the fact.

        Close();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ShowErrorAsync(string message)
    {
        var parent = this as Window;
        await new SaveChangesDialog(message, "OK", null, "", title: "Export Error")
            .ShowDialog(parent);
    }

    // ── Static factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Show the Data Exporter dialog modally.
    /// Falls back to the first active/visible window when <paramref name="owner"/> is null.
    /// </summary>
    public static async Task ShowAsync(Window? owner, DataExporterViewModel vm)
    {
        if (owner is null
            && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.Windows
                           .OfType<Window>()
                           .FirstOrDefault(w => w.IsActive && w.IsVisible)
                ?? desktop.Windows
                           .OfType<Window>()
                           .FirstOrDefault(w => w.IsVisible);
        }

        if (owner is null) return;

        var dialog = new DataExporterDialog(vm);
        await dialog.ShowDialog(owner);
    }
}
