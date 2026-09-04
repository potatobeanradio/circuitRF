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

    private ParameterEditorViewModel? _boundVm;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.WBondLayoutUpdated -= OnWBondLayoutUpdated;
            _boundVm.OpenMatchDesignerRequested -= OnOpenMatchDesigner;
        }
        _boundVm = DataContext as ParameterEditorViewModel;
        if (_boundVm is not null)
        {
            _boundVm.WBondLayoutUpdated += OnWBondLayoutUpdated;
            _boundVm.OpenMatchDesignerRequested += OnOpenMatchDesigner;
        }

        if (DataContext is ParameterEditorViewModel vm)
        {
            vm.PickSnpFileAsync       = PickSnpFileAsync;
            vm.PickSpiceFileAsync     = PickSpiceFileAsync;
            vm.PickModelFileAsync     = PickModelFileAsync;
            vm.PickModelParameterAsync = PickModelParameterAsync;
            vm.PickParameterSetFileAsync = PickParameterSetFileAsync;
            vm.PickSddEquationAsync   = PickSddEquationAsync;
            vm.RevealFileAsync        = RevealFileAsync;
            vm.OpenCvEditorDialogAsync = OpenCvEditorDialogAsync;
        }
    }

    /// <summary>
    /// Opens the Match Designer for the selected instance. Only the view knows which window owns this
    /// panel, which is why the view-model raises an event rather than opening one itself.
    /// </summary>
    private void OnOpenMatchDesigner(CircuitRF.Ui.Schematic.EditableComponent comp)
    {
        if (Vm?.SchematicVm is not { } schematicVm) return;
        Views.Match.MatchDesignerWindow.Show(schematicVm, comp, TopLevel.GetTopLevel(this) as Window);
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

    /// <summary>
    /// Picks the SPICE file a <c>SpiceModel</c> component runs.
    ///
    /// <para>The filter is wide on purpose, and wider than the project tree's own "Copy to Workspace
    /// as Cell…" menu item offers. That item appears on a bookmarked file with nothing having read
    /// it, so its extension list is the whole of what decides; here the user has already said what
    /// the file is by choosing it, and a vendor writes a model deck into whatever extension it
    /// pleases — <c>.lib</c> and <c>.txt</c> included.</para>
    /// </summary>
    private async Task<string?> PickSpiceFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var patterns = new List<string>();
        foreach (var ext in CircuitRF.Ui.Schematic.SpiceModelPeek.FileExtensions)
        {
            patterns.Add("*" + ext);
            patterns.Add("*" + ext.ToUpperInvariant());
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Open SPICE Model or Subcircuit",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SPICE model or subcircuit") { Patterns = [.. patterns] },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// Picks a model library for a file-valued parameter (today: a kit part's ModelLibrary override).
    ///
    /// <para>The filter names all three platforms' shared-library extensions rather than only this
    /// machine's. A worker's library is a property of the KIT, not of the host running the editor —
    /// a design authored on a Mac may name the Linux build the worker will actually load.</para>
    /// </summary>
    private async Task<string?> PickModelFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Choose Model File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                // BOTH forms in the DEFAULT filter, because a picker opens on its first entry and
                // this parameter accepts either. Splitting them left source-only below the fold: a
                // user who had just downloaded a model family opened the picker onto their own `.va`
                // and saw it greyed out, which reads as "circuitRF cannot take this file" rather
                // than "switch the dropdown". Both are first-class inputs — a compiled artefact is
                // loaded as-is, and source is compiled once and cached on its own content — so
                // neither belongs behind the other.
                new FilePickerFileType("Verilog-A model (*.va, *.vams, *.osdi)")
                {
                    Patterns = ["*.va", "*.vams", "*.osdi", "*.VA", "*.VAMS", "*.OSDI"],
                },
                // The two narrowing entries, for a folder holding both forms of the same model.
                new FilePickerFileType("Compiled Verilog-A model (*.osdi)")
                {
                    Patterns = ["*.osdi", "*.OSDI"],
                },
                new FilePickerFileType("Verilog-A source (*.va, *.vams)")
                {
                    Patterns = ["*.va", "*.vams", "*.VA", "*.VAMS"],
                },
                new FilePickerFileType("Model library (*.so, *.dll, *.dylib)")
                {
                    Patterns = ["*.so", "*.dll", "*.dylib", "*.SO", "*.DLL", "*.DYLIB"],
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
            CircuitRF.Ui.FileReveal.Reveal(path);
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

    /// <summary>
    /// The wBond panel's Update Layout button closes the dialog once the command has run — the owner
    /// asked for the layout to be left focused, and a parameter dialog still floating over it is exactly
    /// what would not be.
    ///
    /// <para>Driven by the view model's own <c>WBondLayoutUpdated</c> event rather than by a second
    /// <c>Click</c> handler on the same button: Avalonia raises <c>Click</c> BEFORE it executes
    /// <c>Command</c>, so closing from there would tear the DataContext down first and the update would
    /// never run.</para>
    ///
    /// <para>Gated on the host being a <see cref="ParameterEditorDialog"/> specifically, because this
    /// same control is also the DOCKED Properties inspector — which must not disappear — and Harmonica's
    /// own C(V) host, which is not this dialog either.</para>
    /// </summary>
    private void OnWBondLayoutUpdated()
    {
        if (TopLevel.GetTopLevel(this) is ParameterEditorDialog dialog) dialog.Close();
    }

    private async void OnParamBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: ViewModels.ParameterRowViewModel row })
            await row.BrowseForFileAsync();
    }

    /// <summary>Shows the model's own declared parameters and returns the chosen one, or null.</summary>
    private async Task<CircuitRF.Ui.Schematic.VerilogAParameterInfo?> PickModelParameterAsync(
        string modelName,
        IReadOnlyList<CircuitRF.Ui.Schematic.VerilogAParameterInfo> declared,
        IReadOnlyCollection<string> alreadyPresent)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var dlg = new ModelParameterPickerDialog(modelName, declared, alreadyPresent);
        return await dlg.ShowDialog<CircuitRF.Ui.Schematic.VerilogAParameterInfo?>(owner);
    }

    /// <summary>
    /// Picks a fitted parameter set to load onto a compiled model.
    ///
    /// <para>The filter names the extensions these sets actually arrive under — a model family ships
    /// its fitted parameters as an includable Verilog-A fragment, so <c>.va</c>/<c>.vams</c> and a
    /// plain <c>.txt</c>/<c>.inc</c> are all in use for the same content. Everything is offered as
    /// well, because the extension is convention rather than format.</para>
    /// </summary>
    private async Task<string?> PickParameterSetFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Load Model Parameters",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Verilog-A parameter set")
                {
                    Patterns = ["*.va", "*.vams", "*.inc", "*.txt",
                                "*.VA", "*.VAMS", "*.INC", "*.TXT"],
                },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Shows the slots this SDD can still use and returns the chosen one, or null.</summary>
    private async Task<CircuitRF.Ui.Schematic.SddEquationSlot?> PickSddEquationAsync(
        string instanceName,
        int portCount,
        IReadOnlyList<CircuitRF.Ui.Schematic.SddEquationSlot> slots,
        IReadOnlyList<string> notes)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var dlg = new SddEquationPickerDialog(instanceName, portCount, slots, notes);
        return await dlg.ShowDialog<CircuitRF.Ui.Schematic.SddEquationSlot?>(owner);
    }

    private void OnParamRemoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: ViewModels.ParameterRowViewModel row })
            row.RemoveSelf();
    }

    // ── wBond array names ─────────────────────────────────────────────────────
    //
    // Committed on Enter or lost focus — the staged-text idiom every other name field in this
    // application uses. Never per keystroke: an array name must be non-blank and unique (it is a pin
    // name), and a half-typed one is routinely neither, so a per-character commit would be refused
    // and snap the box back mid-word.

    private void OnWBondArrayNameCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: ViewModels.WBondArrayEditRow row })
            row.Commit();
    }

    private void OnWBondArrayNameKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)) return;
        if (sender is not Avalonia.Controls.Control { DataContext: ViewModels.WBondArrayEditRow row }) return;

        row.Commit();
        e.Handled = true;
    }

    // ── §5.5.1/WB44: the per-array controlling parameters ─────────────────────
    //
    // Committed on Enter or lost focus, exactly as the array-name box above and for the same reason:
    // a half-typed expression is routinely unparseable, and committing per keystroke would fight the
    // user for the box — here it would also re-run elaboration on every character.

    private void OnWBondControlLoopHeightCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: ViewModels.WBondControlRow row })
            row.CommitLoopHeight();
    }

    private void OnWBondControlLoopHeightKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)) return;
        if (sender is not Avalonia.Controls.Control { DataContext: ViewModels.WBondControlRow row }) return;

        row.CommitLoopHeight();
        e.Handled = true;
    }

    private void OnWBondControlDiameterCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: ViewModels.WBondControlRow row })
            row.CommitDiameter();
    }

    private void OnWBondControlDiameterKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)) return;
        if (sender is not Avalonia.Controls.Control { DataContext: ViewModels.WBondControlRow row }) return;

        row.CommitDiameter();
        e.Handled = true;
    }
}
