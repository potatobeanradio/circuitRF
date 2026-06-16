using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

public enum VarEditorMode { Text = 0, Rows = 1 }

/// <summary>
/// VM for the dual-mode VAR variable editor.
/// Mode A (Text, default): paste many "name = expression" lines at once.
/// Mode B (Rows): add / edit / remove variables one at a time.
/// Both modes operate on the same underlying EditableComponent.Parameters list
/// and route mutations through SchematicViewModel.Execute (undo/redo + dirty).
/// </summary>
public sealed partial class VarEditorViewModel : ObservableObject, IDisposable
{
    private SchematicViewModel? _schematicVm;
    private EditableComponent?  _comp;
    private bool                _isRefreshing;

    // ── Undo/Redo (delegate to the owning schematic's stack) ─────────────────

    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }

    private UndoRedoStack? _hookedStack;

    private void HookSchematicStack(SchematicViewModel? vm)
    {
        if (_hookedStack is not null)
            _hookedStack.PropertyChanged -= OnStackChanged;
        _hookedStack = vm?.UndoRedo;
        if (_hookedStack is not null)
            _hookedStack.PropertyChanged += OnStackChanged;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
        if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
    }

    // ── Mode ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private VarEditorMode _activeMode = VarEditorMode.Text;

    public bool IsTextMode => ActiveMode == VarEditorMode.Text;
    public bool IsRowsMode => ActiveMode == VarEditorMode.Rows;

    partial void OnActiveModeChanged(VarEditorMode oldValue, VarEditorMode newValue)
    {
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsRowsMode));
    }

    // ── Mode A (Text) state ───────────────────────────────────────────────────

    [ObservableProperty] private string _textContent = "";
    [ObservableProperty] private IReadOnlyList<VarTextParser.VarLine> _parsedLines = [];
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool   _hasValidationErrors;

    partial void OnTextContentChanged(string? oldValue, string newValue)
    {
        if (_isRefreshing) return;
        ReparseText(newValue);
    }

    private void ReparseText(string text)
    {
        var lines   = VarTextParser.ParseLines(text);
        ParsedLines = lines;

        var errors = lines
            .Where(l => !l.IsValid && !l.IsBlank && !l.IsComment)
            .Select(l => l.ErrorMessage ?? "Invalid line")
            .Distinct()
            .ToList();

        var dupes = VarTextParser.FindDuplicateNames(lines);
        if (dupes.Count > 0)
            errors.Add($"Duplicate variable name(s): {string.Join(", ", dupes)}");

        HasValidationErrors = errors.Count > 0;
        ValidationSummary   = string.Join(" · ", errors);
    }

    // ── Mode B (Rows) state ───────────────────────────────────────────────────

    public ObservableCollection<VarRowViewModel> Rows { get; } = [];

    // ── Header (shown in both modes) ──────────────────────────────────────────

    [ObservableProperty] private string _instanceName = "";
    [ObservableProperty] private bool   _showClose;

    // ── Construction ─────────────────────────────────────────────────────────

    public VarEditorViewModel()
    {
        UndoCommand = new RelayCommand(
            () => _schematicVm?.UndoRedo.Undo(),
            () => _schematicVm?.UndoRedo.CanUndo ?? false);
        RedoCommand = new RelayCommand(
            () => _schematicVm?.UndoRedo.Redo(),
            () => _schematicVm?.UndoRedo.CanRedo ?? false);
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    public void SetTarget(SchematicViewModel schematicVm, EditableComponent comp, bool showClose = true)
    {
        if (_schematicVm is not null)
            _schematicVm.EditModel.Changed -= OnModelChanged;

        _schematicVm = schematicVm;
        _comp        = comp;
        ShowClose    = showClose;

        HookSchematicStack(schematicVm);
        _schematicVm.EditModel.Changed += OnModelChanged;
        ActiveMode = VarEditorMode.Text;
        RefreshFromModel();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetTextMode()
    {
        if (ActiveMode == VarEditorMode.Text) return;
        // Switching Rows → Text: serialize current params into text.
        if (_comp is not null)
        {
            _isRefreshing = true;
            TextContent = VarTextParser.SerializeLines(_comp.Parameters);
            _isRefreshing = false;
            ReparseText(TextContent);
        }
        ActiveMode = VarEditorMode.Text;
    }

    [RelayCommand]
    private void SetRowsMode()
    {
        if (ActiveMode == VarEditorMode.Rows) return;
        // Switching Text → Rows: apply valid lines from current text.
        ApplyTextToModel();
        ActiveMode = VarEditorMode.Rows;
        RebuildRows();
    }

    /// <summary>
    /// Apply the multi-line text content to the model (Mode A commit).
    /// Valid lines replace the full Parameters list via SetVarParametersCommand.
    /// Invalid / blank / comment lines are silently skipped.
    /// No-op when no target is set.
    /// </summary>
    [RelayCommand]
    private void ApplyText()
    {
        ApplyTextToModel();
        RefreshFromModel();   // re-serialize so text reflects applied state
    }

    private void ApplyTextToModel()
    {
        if (_comp is null || _schematicVm is null) return;

        var lines = VarTextParser.ParseLines(TextContent);
        var newParams = lines
            .Where(l => l.IsValid)
            .Select(l => new EditableParameter { Name = l.Name!, Expression = l.Expression! })
            .ToList();

        // Only execute if something would actually change.
        bool changed = newParams.Count != _comp.Parameters.Count
            || newParams.Zip(_comp.Parameters).Any(t =>
                t.First.Name != t.Second.Name || t.First.Expression != t.Second.Expression);

        if (!changed) return;

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _comp, newParams));
    }

    [RelayCommand]
    private void AddRow()
    {
        if (_comp is null || _schematicVm is null) return;
        var p = new EditableParameter { Name = GenerateUniqueName(), Expression = "0" };
        _schematicVm.Execute(new AddParameterCommand(_schematicVm.EditModel, _comp, p));
    }

    // ── Internal surface for VarRowViewModel ─────────────────────────────────

    internal SchematicViewModel? SchematicVm  => _schematicVm;
    internal EditableComponent?  TargetComp   => _comp;

    internal void RemoveRow(VarRowViewModel row)
    {
        if (_comp is null || _schematicVm is null) return;
        var p = _comp.Parameters.FirstOrDefault(x => ReferenceEquals(x, row.Parameter));
        if (p is null) return;
        _schematicVm.Execute(new RemoveParameterCommand(_schematicVm.EditModel, _comp, p));
    }

    // ── Rebuild helpers ───────────────────────────────────────────────────────

    private void RefreshFromModel()
    {
        if (_comp is null) return;
        InstanceName = _comp.InstanceName;

        _isRefreshing = true;
        TextContent = VarTextParser.SerializeLines(_comp.Parameters);
        _isRefreshing = false;
        ReparseText(TextContent);

        if (ActiveMode == VarEditorMode.Rows)
            RebuildRows();
    }

    private void RebuildRows()
    {
        if (_comp is null) return;
        Rows.Clear();
        foreach (var p in _comp.Parameters)
            Rows.Add(new VarRowViewModel(p, this));
    }

    // ── Model change handler ──────────────────────────────────────────────────

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_comp is null || _schematicVm is null) return;
        // If the component was deleted, close gracefully.
        if (_schematicVm.EditModel.FindComponent(_comp.Id) is null)
        {
            _comp = null;
            return;
        }
        RefreshFromModel();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GenerateUniqueName()
    {
        if (_comp is null) return "V1";
        var existing = new HashSet<string>(_comp.Parameters.Select(p => p.Name), StringComparer.Ordinal);
        for (int i = 1; i <= 1000; i++)
        {
            string candidate = $"Var{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"Var{Guid.NewGuid().ToString("N")[..4]}";
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_schematicVm is not null)
            _schematicVm.EditModel.Changed -= OnModelChanged;
        HookSchematicStack(null);
    }
}

/// <summary>
/// Row VM for one variable in VAR Mode B (rows).
/// Wraps a single EditableParameter; name and expression are staged and committed
/// through the owning VarEditorViewModel (and thence through the schematic undo stack).
/// </summary>
public sealed partial class VarRowViewModel : ObservableObject
{
    internal EditableParameter Parameter { get; }

    private readonly VarEditorViewModel _editor;
    private bool _isRefreshing;

    [ObservableProperty] private string _stagedName = "";
    [ObservableProperty] private string _stagedExpression = "";
    [ObservableProperty] private string _stagedUnit = "";

    public VarRowViewModel(EditableParameter param, VarEditorViewModel editor)
    {
        Parameter  = param;
        _editor    = editor;

        _isRefreshing   = true;
        _stagedName       = param.Name;
        _stagedExpression = param.Expression;
        _stagedUnit       = param.Unit;
        _isRefreshing   = false;

        RemoveCommand = new RelayCommand(() => _editor.RemoveRow(this));
    }

    public IRelayCommand RemoveCommand { get; }

    public void CommitName()
    {
        if (_isRefreshing || _editor.SchematicVm is null || _editor.TargetComp is null) return;
        string name = StagedName.Trim();
        if (name.Length == 0 || name == Parameter.Name) return;
        _editor.SchematicVm.Execute(
            new SetParameterNameCommand(_editor.SchematicVm.EditModel, Parameter, name));
    }

    public void CommitExpression()
    {
        if (_isRefreshing || _editor.SchematicVm is null || _editor.TargetComp is null) return;
        string expr = StagedExpression.Trim();
        if (expr.Length == 0 || expr == Parameter.Expression) return;
        _editor.SchematicVm.Execute(
            new EditParameterCommand(_editor.SchematicVm.EditModel, Parameter, expr, Parameter.Unit));
    }

    public void CommitUnit(string unit)
    {
        if (_isRefreshing || _editor.SchematicVm is null || _editor.TargetComp is null) return;
        if (unit == Parameter.Unit) return;
        _editor.SchematicVm.Execute(
            new EditParameterCommand(_editor.SchematicVm.EditModel, Parameter, Parameter.Expression, unit));
    }

    internal void RefreshFromParam()
    {
        _isRefreshing   = true;
        StagedName        = Parameter.Name;
        StagedExpression  = Parameter.Expression;
        StagedUnit        = Parameter.Unit;
        _isRefreshing   = false;
    }
}
