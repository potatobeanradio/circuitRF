using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.ParameterEditor;

public partial class ParameterEditorView : UserControl
{
    // Suppress SelectionChanged re-entrancy when RefreshFromModel updates StagedUnit bindings.
    private bool _suppressUnitCommit;

    public ParameterEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ParameterEditorViewModel vm)
        {
            vm.PickSnpFileAsync       = PickSnpFileAsync;
            vm.RevealFileAsync        = RevealFileAsync;
            vm.OpenCvEditorDialogAsync = OpenCvEditorDialogAsync;
        }
    }

    private async Task OpenCvEditorDialogAsync()
    {
        var vm = Vm;
        if (vm?.SchematicVm is null || vm.Target is null) return;

        var cvVm = new NonlinearCvEditorViewModel();
        cvVm.SetTarget(vm.SchematicVm, vm.Target);

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new NonlinearCvEditorDialog { DataContext = cvVm };
        dialog.Closed += (_, _) => cvVm.Dispose();
        dialog.Show(owner!);
        await Task.CompletedTask;
    }

    private async Task<string?> PickSnpFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Open Touchstone File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Touchstone (*.sNp, *.snp)")
                {
                    Patterns = ["*.s1p","*.s2p","*.s3p","*.s4p","*.s5p","*.s6p","*.s7p","*.s8p",
                                "*.s9p","*.s10p","*.s11p","*.s12p","*.snp",
                                "*.S1P","*.S2P","*.S3P","*.S4P","*.S5P","*.S6P","*.S7P","*.S8P",
                                "*.S9P","*.S10P","*.S11P","*.S12P","*.SNP"],
                },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    private Task RevealFileAsync(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", ["-R", path]);
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", [$"/select,\"{path}\""]);
            else
            {
                string? dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.Diagnostics.Process.Start("xdg-open", [dir]);
            }
        }
        catch (Exception)
        {
            // A missing file or file manager shouldn't crash the app.
        }
        return Task.CompletedTask;
    }

    private ParameterEditorViewModel? Vm => DataContext as ParameterEditorViewModel;

    // ── Instance name ─────────────────────────────────────────────────────────

    private void OnInstanceNameLostFocus(object? sender, RoutedEventArgs e) => Vm?.CommitInstanceName();

    private void OnInstanceNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            Vm?.CommitInstanceName();
            e.Handled = true;
        }
    }

    // ── Parameter name TextBox (editable for extensible types; read-only otherwise) ──

    private void OnParamNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
            row.CommitName();
    }

    private void OnParamNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
            {
                row.CommitName();
                e.Handled = true;
            }
        }
    }

    // ── Parameter expression TextBox ──────────────────────────────────────────

    private void OnParamExprLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
        {
            row.CommitExpression();
            Vm?.TriggerResort();
        }
    }

    private void OnParamExprKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            if (sender is TextBox tb && tb.DataContext is ParameterRowViewModel row)
            {
                row.CommitExpression();
                Vm?.TriggerResort();
                e.Handled = true;
            }
        }
    }

    // ── Unit ComboBox ─────────────────────────────────────────────────────────

    private void OnUnitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressUnitCommit) return;
        if (sender is ComboBox cb && cb.DataContext is ParameterRowViewModel row
            && cb.SelectedItem is string unit)
        {
            _suppressUnitCommit = true;
            row.CommitUnit(unit);
            _suppressUnitCommit = false;
        }
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        // Open the Reference Guide section for this component (offline, bundled docs).
        if (DataContext is ParameterEditorViewModel { Target: { } target })
            DocLauncher.OpenComponent(target.Symbol);
        else
            DocLauncher.Open("reference/components.html");
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Walk up to the hosting Window and close it (dialog host only; ignored when embedded).
        if (TopLevel.GetTopLevel(this) is Window win)
            win.Close();
    }
}
