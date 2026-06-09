using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Reusable VM for the parameter editor — hosted both in the Properties inspector
/// (via SetContext, tracks selection) and in the double-click dialog (via SetTargetDirect,
/// targets one specific component).
/// </summary>
public sealed partial class ParameterEditorViewModel : ObservableObject
{
    private SchematicViewModel? _schematicVm;
    private EditableComponent?  _target;
    private bool                _isRefreshing;

    // ── Empty / non-empty state ────────────────────────────────────────────────

    [ObservableProperty] private bool   _isEmptyState = true;
    [ObservableProperty] private string _emptyMessage = "Select a component to edit its parameters.";

    public bool IsNotEmptyState => !IsEmptyState;
    partial void OnIsEmptyStateChanged(bool oldValue, bool newValue)
        => OnPropertyChanged(nameof(IsNotEmptyState));

    // ── Header ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _typeDisplayName  = "";
    [ObservableProperty] private string _stagedInstanceName = "";

    // ── Label-visibility flags ────────────────────────────────────────────────
    // Backing fields so we can set without triggering Execute during refresh.

    [ObservableProperty] private bool _showTypeLabel;
    [ObservableProperty] private bool _showInstanceName;

    partial void OnShowTypeLabelChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        _schematicVm.Execute(new SetLabelVisibilityCommand(_schematicVm.EditModel, _target, isTypeLabel: true, newValue));
    }

    partial void OnShowInstanceNameChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        _schematicVm.Execute(new SetLabelVisibilityCommand(_schematicVm.EditModel, _target, isTypeLabel: false, newValue));
    }

    // ── Close button (dialog host shows; embedded host hides) ─────────────────

    [ObservableProperty] private bool _showClose;

    // ── Parameter rows ─────────────────────────────────────────────────────────

    public ObservableCollection<ParameterRowViewModel> Rows { get; } = [];

    // ── Embedded host: bind to selection ──────────────────────────────────────

    /// <summary>
    /// Bind this VM to an active schematic's selection (Properties-region embedded mode).
    /// Pass null to clear (no open schematic).
    /// </summary>
    public void SetContext(SchematicViewModel? vm)
    {
        if (_schematicVm is not null)
        {
            _schematicVm.Selection.Changed   -= OnSelectionChanged;
            _schematicVm.EditModel.Changed   -= OnModelChanged;
        }

        _schematicVm = vm;

        if (_schematicVm is not null)
        {
            _schematicVm.Selection.Changed += OnSelectionChanged;
            _schematicVm.EditModel.Changed += OnModelChanged;
            UpdateFromSelection();
        }
        else
        {
            SetTarget(null);
        }
    }

    // ── Dialog host: target one specific component ────────────────────────────

    /// <summary>
    /// Bind this VM to one specific component (dialog mode).
    /// The caller is responsible for calling Dispose when the dialog closes.
    /// </summary>
    public void SetTargetDirect(SchematicViewModel vm, EditableComponent comp, bool showClose = true)
    {
        if (_schematicVm is not null)
            _schematicVm.EditModel.Changed -= OnModelChanged;

        _schematicVm = vm;
        ShowClose = showClose;
        _schematicVm.EditModel.Changed += OnModelChanged;
        SetTarget(comp);
    }

    // ── Core SetTarget (single Ground/null guard point) ───────────────────────

    private void SetTarget(EditableComponent? comp)
    {
        // Ground / null / empty → empty state (single guard point per spec)
        if (comp is null || comp.Symbol == SymbolKind.Ground)
        {
            _target = null;
            IsEmptyState = true;
            Rows.Clear();
            return;
        }

        _target = comp;
        _isRefreshing = true;

        IsEmptyState      = false;
        TypeDisplayName   = ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount);
        StagedInstanceName = comp.InstanceName;
        ShowTypeLabel     = comp.ShowTypeLabel;
        ShowInstanceName  = comp.ShowInstanceName;

        // Build rows — NumPorts and blank-name params omitted
        Rows.Clear();
        if (_schematicVm is not null)
        {
            foreach (var param in comp.Parameters)
            {
                if (param.Name == "NumPorts" || string.IsNullOrEmpty(param.Name)) continue;
                Rows.Add(new ParameterRowViewModel(param, _schematicVm, comp.Symbol));
            }
        }

        _isRefreshing = false;
    }

    // ── Commit helpers called by the view ─────────────────────────────────────

    /// <summary>Commit a staged instance name (no-op when unchanged or blank).</summary>
    public void CommitInstanceName()
    {
        if (_target is null || _schematicVm is null) return;
        string newName = StagedInstanceName.Trim();
        if (newName.Length == 0 || newName == _target.InstanceName) return;
        _schematicVm.Execute(new RenameComponentCommand(_schematicVm.EditModel, _target, newName));
    }

    // ── Model / selection change handlers ─────────────────────────────────────

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateFromSelection();

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_target is null) return;
        // Component deleted? → empty state
        if (_schematicVm is not null && _schematicVm.EditModel.FindComponent(_target.Id) is null)
        {
            SetTarget(null);
            return;
        }
        RefreshFromModel();
    }

    private void UpdateFromSelection()
    {
        if (_schematicVm is null) { SetTarget(null); return; }
        var ids = _schematicVm.Selection.Ids;
        if (ids.Count == 1)
        {
            string id = ids.First();
            SetTarget(_schematicVm.EditModel.FindComponent(id));
        }
        else
        {
            SetTarget(null);
        }
    }

    private void RefreshFromModel()
    {
        if (_target is null) return;
        _isRefreshing = true;
        TypeDisplayName    = ComponentTypeRegistry.DisplayName(_target.Symbol, _target.PortCount);
        StagedInstanceName = _target.InstanceName;
        ShowTypeLabel      = _target.ShowTypeLabel;
        ShowInstanceName   = _target.ShowInstanceName;
        foreach (var row in Rows)
            row.RefreshFromModel();
        _isRefreshing = false;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_schematicVm is null) return;
        _schematicVm.Selection.Changed -= OnSelectionChanged;
        _schematicVm.EditModel.Changed -= OnModelChanged;
    }
}
