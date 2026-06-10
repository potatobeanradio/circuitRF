using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands.Cell;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// VM for one row in the CellParameterEditorView — wraps a single CcellParameter.
/// Unlike ParameterRowViewModel (instance-editor, read-only Name), every field here
/// is editable: add / remove / rename rows is the cell-editor's whole purpose.
/// Staged values are committed explicitly via Commit* methods called from code-behind.
/// </summary>
public sealed partial class CellParameterRowViewModel : ObservableObject
{
    private readonly CellParameterEditorViewModel _editorVm;
    private bool _isRefreshing;

    /// <summary>The underlying CcellParameter — accessed by the VM for Remove by reference.</summary>
    internal CcellParameter Parameter { get; }

    // ── Staged values ─────────────────────────────────────────────────────────

    [ObservableProperty] private string        _stagedName      = "";
    [ObservableProperty] private string        _stagedDefault   = "";
    [ObservableProperty] private string        _stagedUnit      = "";
    [ObservableProperty] private UnitDimension _stagedDimension = UnitDimension.None;
    [ObservableProperty] private bool          _showOnSchematic;

    // ── Rename consequence warning ────────────────────────────────────────────
    // Shown inline while StagedName diverges from the current model name.
    // Clears on commit or RefreshFromModel.

    [ObservableProperty] private string? _renameWarning;
    public bool HasRenameWarning => RenameWarning is not null;
    partial void OnRenameWarningChanged(string? oldValue, string? newValue)
        => OnPropertyChanged(nameof(HasRenameWarning));

    // ── Unit options (dimension-keyed) ────────────────────────────────────────

    private string[] _unitOptions;
    public string[] UnitOptions
    {
        get => _unitOptions;
        private set { _unitOptions = value; OnPropertyChanged(); }
    }

    // ── Dimension options (all enum values) ───────────────────────────────────

    public static UnitDimension[] AllDimensions { get; } = Enum.GetValues<UnitDimension>();

    // ── Remove command ────────────────────────────────────────────────────────

    public IRelayCommand RemoveCommand { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    public CellParameterRowViewModel(CcellParameter param, CellParameterEditorViewModel editorVm)
    {
        Parameter     = param;
        _editorVm     = editorVm;
        _unitOptions  = ComponentTypeRegistry.UnitOptions(param.Dimension);
        RemoveCommand = new RelayCommand(() => editorVm.RemoveRow(this));

        _isRefreshing = true;
        _stagedName      = param.Name;
        _stagedDefault   = param.DefaultExpression;
        _stagedUnit      = param.Unit;
        _stagedDimension = param.Dimension;
        _showOnSchematic = param.ShowOnSchematic;
        _isRefreshing = false;
    }

    // ── INPC callbacks ────────────────────────────────────────────────────────

    partial void OnStagedNameChanged(string? oldValue, string newValue)
    {
        if (_isRefreshing) return;
        bool changing = newValue.Trim() is { Length: > 0 } trimmed && trimmed != Parameter.Name;
        RenameWarning = changing
            ? "Renaming changes the parameter identity. Instances using the old name will fall back to the default."
            : null;
    }

    partial void OnShowOnSchematicChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing) return;
        _editorVm.Execute(new SetCellParameterCommand(
            _editorVm.EditModel, Parameter,
            newName:      Parameter.Name,
            newDefault:   Parameter.DefaultExpression,
            newUnit:      Parameter.Unit,
            newDimension: Parameter.Dimension,
            newShow:      newValue,
            description:  $"Toggle show for {Parameter.Name}"));
        // Note: Execute fires RebuildRows, which orphans this row VM — that is expected.
    }

    // ── Commit methods called from code-behind ────────────────────────────────

    /// <summary>
    /// Commit a staged name change.  Reverts on empty or invalid name.
    /// On valid rename, fires the SetCellParameterCommand and orphans this row VM
    /// (RebuildRows will run and create a fresh row with the new name).
    /// </summary>
    public void CommitName()
    {
        string name = StagedName.Trim();
        if (name == Parameter.Name) { RenameWarning = null; return; }
        if (!IsValidParamName(name)) { RefreshFromModel(); return; }

        _editorVm.Execute(new SetCellParameterCommand(
            _editorVm.EditModel, Parameter,
            newName:      name,
            newDefault:   Parameter.DefaultExpression,
            newUnit:      Parameter.Unit,
            newDimension: Parameter.Dimension,
            newShow:      Parameter.ShowOnSchematic,
            description:  $"Rename {Parameter.Name} → {name}"));
        // After Execute, this row VM is orphaned; don't access state.
    }

    /// <summary>Commit a staged default-expression change.</summary>
    public void CommitDefault()
    {
        string def = StagedDefault.Trim();
        if (def == Parameter.DefaultExpression) return;
        _editorVm.Execute(new SetCellParameterCommand(
            _editorVm.EditModel, Parameter,
            newName:      Parameter.Name,
            newDefault:   def,
            newUnit:      Parameter.Unit,
            newDimension: Parameter.Dimension,
            newShow:      Parameter.ShowOnSchematic,
            description:  $"Edit default of {Parameter.Name}"));
    }

    /// <summary>Commit a unit selection from the Unit ComboBox.</summary>
    public void CommitUnit(string unit)
    {
        if (unit == Parameter.Unit) return;
        _editorVm.Execute(new SetCellParameterCommand(
            _editorVm.EditModel, Parameter,
            newName:      Parameter.Name,
            newDefault:   Parameter.DefaultExpression,
            newUnit:      unit,
            newDimension: Parameter.Dimension,
            newShow:      Parameter.ShowOnSchematic,
            description:  $"Set unit of {Parameter.Name}"));
    }

    /// <summary>
    /// Commit a dimension selection.  Resets the unit to the first valid option for the new
    /// dimension (the old unit may not be valid for it).
    /// </summary>
    public void CommitDimension(UnitDimension dim)
    {
        if (dim == Parameter.Dimension) return;
        string firstUnit = ComponentTypeRegistry.UnitOptions(dim)[0];
        _editorVm.Execute(new SetCellParameterCommand(
            _editorVm.EditModel, Parameter,
            newName:      Parameter.Name,
            newDefault:   Parameter.DefaultExpression,
            newUnit:      firstUnit,
            newDimension: dim,
            newShow:      Parameter.ShowOnSchematic,
            description:  $"Set dimension of {Parameter.Name}"));
    }

    /// <summary>Refresh all staged fields from the model (called after undo/redo row rebuild).</summary>
    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedName      = Parameter.Name;
        StagedDefault   = Parameter.DefaultExpression;
        StagedUnit      = Parameter.Unit;
        StagedDimension = Parameter.Dimension;
        ShowOnSchematic = Parameter.ShowOnSchematic;
        UnitOptions     = ComponentTypeRegistry.UnitOptions(Parameter.Dimension);
        RenameWarning   = null;
        _isRefreshing = false;
    }

    // ── Name validation ───────────────────────────────────────────────────────
    // Parameter names are expression identifiers: [A-Za-z_][A-Za-z0-9_]*

    internal static bool IsValidParamName(string name)
        => name.Length > 0
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
