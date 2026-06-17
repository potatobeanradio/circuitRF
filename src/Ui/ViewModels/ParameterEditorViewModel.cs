using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using CircuitRF.Ui.Commands;
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
    private bool                _isApplyingSort;

    // ── Undo/Redo — delegate to the target schematic's own stack ─────────────
    // The Parameter Editor has NO independent stack.  These commands act on
    // _schematicVm.UndoRedo so that parameter edits are undoable via the owning
    // schematic (whether the editor is embedded or open as a dialog).

    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }

    private UndoRedoStack? _hookedStack;

    // ── Add / Remove group commands (user-extensible component types only) ────

    public IRelayCommand AddGroupCommand       { get; }
    public IRelayCommand RemoveTopGroupCommand { get; }

    private bool _canRemoveTopGroup;
    public  bool  CanRemoveTopGroup => _canRemoveTopGroup;

    public ParameterEditorViewModel()
    {
        UndoCommand = new RelayCommand(
            () => _schematicVm?.UndoRedo.Undo(),
            () => _schematicVm?.UndoRedo.CanUndo ?? false);
        RedoCommand = new RelayCommand(
            () => _schematicVm?.UndoRedo.Redo(),
            () => _schematicVm?.UndoRedo.CanRedo ?? false);

        AddGroupCommand       = new RelayCommand(AddGroup);
        RemoveTopGroupCommand = new RelayCommand(RemoveTopGroup, () => _canRemoveTopGroup);
        PickSnpFileCommand    = new AsyncRelayCommand(PickFileAsync);
    }

    // Subscribe/unsubscribe PropertyChanged on the target schematic's stack so
    // that UndoCommand.CanExecute stays in sync as edits are made.
    private void HookSchematicStack(SchematicViewModel? vm)
    {
        if (_hookedStack is not null)
            _hookedStack.PropertyChanged -= OnSchematicStackChanged;

        _hookedStack = vm?.UndoRedo;

        if (_hookedStack is not null)
            _hookedStack.PropertyChanged += OnSchematicStackChanged;

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnSchematicStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
        if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
    }

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

    // ── SnP panel ─────────────────────────────────────────────────────────────

    public bool IsSnp => _target?.Symbol == SymbolKind.Snp;

    /// <summary>Callback set by the view so the VM can open a native file picker.</summary>
    public Func<Task<string?>>? PickSnpFileAsync { get; set; }

    public static string[] SnpPinConfigOptions { get; } = ["Standard", "SplitLR", "DualRow"];
    public static string[] SnpPitchOptions     { get; } = ["Tight",    "Loose"];

    public IAsyncRelayCommand PickSnpFileCommand { get; private set; } = null!;

    [ObservableProperty] private string _snpFilePath       = "";
    [ObservableProperty] private bool   _snpRefNode        = false;
    [ObservableProperty] private int    _snpPinConfigIndex = 0;
    [ObservableProperty] private int    _snpPitchIndex     = 1;
    [ObservableProperty] private string _snpPortCountText  = "";

    partial void OnSnpRefNodeChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        ApplySnpParam("RefNode", newValue ? "true" : "false");
    }

    partial void OnSnpPinConfigIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        string val = (uint)newValue < (uint)SnpPinConfigOptions.Length ? SnpPinConfigOptions[newValue] : "Standard";
        ApplySnpParam("PinConfig", val);
    }

    partial void OnSnpPitchIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        string val = (uint)newValue < (uint)SnpPitchOptions.Length ? SnpPitchOptions[newValue] : "Loose";
        ApplySnpParam("Pitch", val);
    }

    private async Task PickFileAsync()
    {
        if (_target is null || _schematicVm is null || PickSnpFileAsync is null) return;
        string? path = await PickSnpFileAsync();
        if (path is null) return;

        var newParams = _target.Parameters.Select(p => p.Clone()).ToList();
        var fileParam = newParams.FirstOrDefault(p => p.Name == "File");
        if (fileParam is not null) fileParam.Expression = path;

        if (TouchstoneIO.TryGetPortCount(path, out int n, out _))
        {
            var numPorts = newParams.FirstOrDefault(p => p.Name == "NumPorts");
            if (numPorts is not null) numPorts.Expression = n.ToString();
        }

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    private void ApplySnpParam(string name, string value)
    {
        var newParams = _target!.Parameters.Select(p => p.Clone()).ToList();
        var param = newParams.FirstOrDefault(p => p.Name == name);
        if (param is not null) param.Expression = value;
        _schematicVm!.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    private void RefreshSnpProperties()
    {
        if (_target?.Symbol != SymbolKind.Snp) return;
        string file = _target.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression ?? "";
        bool refNode = (_target.Parameters.FirstOrDefault(p => p.Name == "RefNode")?.Expression ?? "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        string cfgStr   = _target.Parameters.FirstOrDefault(p => p.Name == "PinConfig")?.Expression ?? "Standard";
        string pitchStr = _target.Parameters.FirstOrDefault(p => p.Name == "Pitch")?.Expression ?? "Loose";
        int cfgIdx   = Array.IndexOf(SnpPinConfigOptions, cfgStr); if (cfgIdx < 0) cfgIdx = 0;
        int pitchIdx = Array.IndexOf(SnpPitchOptions,     pitchStr); if (pitchIdx < 0) pitchIdx = 1;
        int portCount = _target.PortCount;

        // Set _isRefreshing so the partial callbacks don't call ApplySnpParam.
        _isRefreshing = true;
        SnpFilePath       = file;
        SnpRefNode        = refNode;
        SnpPinConfigIndex = cfgIdx;
        SnpPitchIndex     = pitchIdx;
        SnpPortCountText  = portCount >= 1 ? $"{portCount}-port" : "Unknown";
        _isRefreshing = false;
    }

    // ── Close button (dialog host shows; embedded host hides) ─────────────────

    [ObservableProperty] private bool _showClose;

    // ── Extensible parameter types ─────────────────────────────────────────────

    /// <summary>True when the current target type supports user-added parameter groups (P1Tone, ToneSource, ZPort, SDD, VAR).</summary>
    public bool AllowsAddParameter
        => ComponentTypeRegistry.UserParamTemplate(_target?.Symbol ?? SymbolKind.Ground) is not null;

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
        HookSchematicStack(vm);

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
        HookSchematicStack(vm);
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
            OnPropertyChanged(nameof(AllowsAddParameter));
            UpdateCanRemoveTopGroup();
            return;
        }

        _target = comp;
        _isRefreshing = true;

        IsEmptyState      = false;
        TypeDisplayName   = ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount);
        StagedInstanceName = comp.InstanceName;
        ShowTypeLabel     = comp.ShowTypeLabel;
        ShowInstanceName  = comp.ShowInstanceName;

        // Build rows — NumPorts, NumFreqs, blank-name params, and SnP-specific params omitted.
        // SnP components use a custom panel instead of generic rows.
        Rows.Clear();
        if (_schematicVm is not null && comp.Symbol != SymbolKind.Snp)
        {
            foreach (var param in comp.Parameters)
            {
                if (param.Name is "NumPorts" or "NumFreqs" || string.IsNullOrEmpty(param.Name)) continue;
                Rows.Add(new ParameterRowViewModel(param, _schematicVm, comp.Symbol, comp));
            }
        }

        _isRefreshing = false;

        OnPropertyChanged(nameof(IsSnp));
        OnPropertyChanged(nameof(AllowsAddParameter));
        UpdateCanRemoveTopGroup();
        if (comp.Symbol == SymbolKind.Snp) RefreshSnpProperties();
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

    /// <summary>
    /// Re-sort indexed params into canonical order, if order has changed.
    /// Called from the view code-behind after expression/name commit, and from Dispose.
    /// No-op for types that don't have a template, or when already sorted.
    /// </summary>
    public void TriggerResort()
    {
        if (_isApplyingSort || _target is null || _schematicVm is null) return;
        var template = ComponentTypeRegistry.UserParamTemplate(_target.Symbol);
        if (template is null) return;

        var sorted = CanonicalSort(template, _target.Parameters);
        if (ParamNameOrderEquals(_target.Parameters, sorted)) return;

        _isApplyingSort = true;
        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, sorted));
        _isApplyingSort = false;
    }

    // ── Add / Remove group implementations ────────────────────────────────────

    private void AddGroup()
    {
        if (_target is null || _schematicVm is null) return;
        var template = ComponentTypeRegistry.UserParamTemplate(_target.Symbol);
        if (template is null) return;

        var existing = _target.Parameters.ToList();

        // ToneSource: migrate scalar V/Freq → indexed V[1]/Freq[1] on first add
        if (_target.Symbol == SymbolKind.ToneSource)
            existing = MigrateToneSourceToIndexed(existing);

        int nextIdx = ComputeNextIndex(template, existing);

        // Append one group (one param per NameFormat)
        var newParams = new List<EditableParameter>(existing);
        for (int fi = 0; fi < template.NameFormats.Length; fi++)
        {
            string name = string.Format(template.NameFormats[fi], nextIdx);
            string unit = fi < template.DefaultUnits.Length     ? template.DefaultUnits[fi]     : "";
            bool   show = fi < template.ShowOnSchematic.Length  ? template.ShowOnSchematic[fi]  : false;
            var    dim  = fi < template.Dimensions.Length       ? template.Dimensions[fi]       : UnitDimension.None;
            newParams.Add(new EditableParameter { Name = name, Expression = "", Unit = unit, ShowOnSchematic = show, Dimension = dim });
        }

        // ToneSource: keep NumFreqs in sync
        if (_target.Symbol == SymbolKind.ToneSource)
            UpdateHiddenParam(newParams, "NumFreqs", CountToneGroups(newParams).ToString());

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    private void RemoveTopGroup()
    {
        if (_target is null || _schematicVm is null) return;
        var template = ComponentTypeRegistry.UserParamTemplate(_target.Symbol);
        if (template is null) return;

        int topIdx = FindTopGroupIndex(template, _target.Parameters);
        if (topIdx < 0) return;

        var toRemove = new HashSet<string>(
            template.NameFormats.Select(f => string.Format(f, topIdx)));

        var newParams = _target.Parameters.Where(p => !toRemove.Contains(p.Name)).ToList();

        // ToneSource: keep NumFreqs in sync
        if (_target.Symbol == SymbolKind.ToneSource)
            UpdateHiddenParam(newParams, "NumFreqs", CountToneGroups(newParams).ToString());

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    private void UpdateCanRemoveTopGroup()
    {
        bool value = _target is not null
            && ComponentTypeRegistry.UserParamTemplate(_target.Symbol) is { } tpl
            && FindTopGroupIndex(tpl, _target.Parameters) >= 0;

        if (value == _canRemoveTopGroup) return;
        _canRemoveTopGroup = value;
        OnPropertyChanged(nameof(CanRemoveTopGroup));
        RemoveTopGroupCommand.NotifyCanExecuteChanged();
    }

    // ── Model / selection change handlers ─────────────────────────────────────

    private void OnSelectionChanged(object? sender, System.EventArgs e) => UpdateFromSelection();

    private void OnModelChanged(object? sender, System.EventArgs e)
    {
        if (_target is null) return;
        // Component deleted? → empty state
        if (_schematicVm is not null && _schematicVm.EditModel.FindComponent(_target.Id) is null)
        {
            SetTarget(null);
            return;
        }

        if (_isApplyingSort)
        {
            // Re-sort command completed — just update rows from the new model state
            SetTarget(_target);
            return;
        }

        // If parameter count changed (add/remove group), rebuild rows entirely
        int expectedCount = VisibleParamCount(_target);
        if (Rows.Count != expectedCount)
        {
            SetTarget(_target);
            return;
        }

        RefreshFromModel();
        UpdateCanRemoveTopGroup();
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
        if (_target.Symbol == SymbolKind.Snp) RefreshSnpProperties();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        TriggerResort();
        if (_schematicVm is null) return;
        _schematicVm.Selection.Changed -= OnSelectionChanged;
        _schematicVm.EditModel.Changed -= OnModelChanged;
        HookSchematicStack(null);
    }

    // ── Static helpers — pure functions, fully testable ───────────────────────

    /// <summary>
    /// Parses a parameter name against each format in the template.
    /// Returns (index, formatIndex) if it matches any format, else null.
    /// </summary>
    public static (int Index, int FormatIndex)? TryParseTemplateIndex(IndexedParamGroup template, string paramName)
    {
        for (int fi = 0; fi < template.NameFormats.Length; fi++)
        {
            string format = template.NameFormats[fi];
            int placeholder = format.IndexOf("{0}", System.StringComparison.Ordinal);
            if (placeholder < 0) continue;

            string prefix = format[..placeholder];
            string suffix = format[(placeholder + 3)..];

            if (!paramName.StartsWith(prefix, System.StringComparison.Ordinal)) continue;
            if (!paramName.EndsWith(suffix, System.StringComparison.Ordinal))   continue;

            int startIdx = prefix.Length;
            int endIdx   = paramName.Length - suffix.Length;
            if (endIdx < startIdx) continue;

            string indexStr = paramName[startIdx..endIdx];
            if (indexStr.Length == 0) continue;
            if (int.TryParse(indexStr, out int idx) && idx >= 0)
                return (idx, fi);
        }
        return null;
    }

    /// <summary>
    /// Returns the next index to use when the user presses "+":
    /// the smallest i ≥ FirstAddIndex not in SkipIndices that isn't already a parameter.
    /// </summary>
    public static int ComputeNextIndex(IndexedParamGroup template, IEnumerable<EditableParameter> existing)
    {
        var usedIndices = new HashSet<int>();
        foreach (var p in existing)
        {
            var parsed = TryParseTemplateIndex(template, p.Name);
            if (parsed.HasValue)
                usedIndices.Add(parsed.Value.Index);
        }

        int i = template.FirstAddIndex;
        while (i <= 10000)
        {
            if (!template.IsSkipped(i) && !usedIndices.Contains(i))
                return i;
            i++;
        }
        return i;
    }

    /// <summary>
    /// Returns the highest index i ≥ FirstAddIndex (not in SkipIndices) for which the primary
    /// format (NameFormats[0]) matches an existing parameter. Returns −1 if none found.
    /// </summary>
    public static int FindTopGroupIndex(IndexedParamGroup template, IEnumerable<EditableParameter> parameters)
    {
        int topIdx = -1;
        foreach (var p in parameters)
        {
            var parsed = TryParseTemplateIndex(template, p.Name);
            if (parsed is { FormatIndex: 0 } &&
                parsed.Value.Index >= template.FirstAddIndex &&
                !template.IsSkipped(parsed.Value.Index) &&
                parsed.Value.Index > topIdx)
            {
                topIdx = parsed.Value.Index;
            }
        }
        return topIdx;
    }

    /// <summary>
    /// Sorts parameters into canonical order:
    /// 1. Non-indexed params (not matching any template format) — preserved in original order.
    /// 2. Indexed params sorted by (Index ASC, FormatIndex ASC).
    /// Returns the same parameter objects in a new list (no cloning).
    /// </summary>
    public static IReadOnlyList<EditableParameter> CanonicalSort(
        IndexedParamGroup template, IEnumerable<EditableParameter> parameters)
    {
        var paramList = parameters.ToList();
        var leading  = new List<EditableParameter>();
        var indexed  = new List<(int Index, int FormatIndex, EditableParameter Param)>();

        foreach (var p in paramList)
        {
            var parsed = TryParseTemplateIndex(template, p.Name);
            if (parsed.HasValue)
                indexed.Add((parsed.Value.Index, parsed.Value.FormatIndex, p));
            else
                leading.Add(p);
        }

        indexed.Sort((a, b) =>
        {
            int c = a.Index.CompareTo(b.Index);
            return c != 0 ? c : a.FormatIndex.CompareTo(b.FormatIndex);
        });

        var result = new List<EditableParameter>(paramList.Count);
        result.AddRange(leading);
        result.AddRange(indexed.Select(x => x.Param));
        return result;
    }

    // ── ToneSource migration helpers ──────────────────────────────────────────

    /// <summary>
    /// Converts a ToneSource with scalar V/Freq params to indexed V[1]/Freq[1] + NumFreqs=1.
    /// No-op if already in indexed form.
    /// </summary>
    public static List<EditableParameter> MigrateToneSourceToIndexed(List<EditableParameter> existing)
    {
        bool alreadyIndexed = existing.Any(p =>
            p.Name.StartsWith("Freq[", System.StringComparison.Ordinal) ||
            p.Name.StartsWith("V[", System.StringComparison.Ordinal));
        if (alreadyIndexed) return existing;

        var result = new List<EditableParameter>(existing.Count + 1);
        foreach (var p in existing)
        {
            var clone = p.Clone();
            if      (p.Name == "V")    clone.Name = "V[1]";
            else if (p.Name == "Freq") clone.Name = "Freq[1]";
            result.Add(clone);
        }

        if (!result.Any(p => p.Name == "NumFreqs"))
            result.Add(new EditableParameter { Name = "NumFreqs", Expression = "1", Unit = "", ShowOnSchematic = false });

        return result;
    }

    /// <summary>Counts the number of Freq[i] params (i ≥ 1) — the number of indexed tone groups.</summary>
    public static int CountToneGroups(IEnumerable<EditableParameter> parameters)
        => parameters.Count(p =>
        {
            if (!p.Name.StartsWith("Freq[", System.StringComparison.Ordinal) ||
                !p.Name.EndsWith("]")) return false;
            return int.TryParse(p.Name[5..^1], out _);
        });

    // ── Private utilities ─────────────────────────────────────────────────────

    private static void UpdateHiddenParam(List<EditableParameter> parameters, string name, string expression)
    {
        var existing = parameters.FirstOrDefault(p => p.Name == name);
        if (existing is not null)
            existing.Expression = expression;
        else
            parameters.Add(new EditableParameter { Name = name, Expression = expression, Unit = "", ShowOnSchematic = false });
    }

    private static int VisibleParamCount(EditableComponent comp)
    {
        // SnP uses a custom panel; generic rows are always empty.
        if (comp.Symbol == SymbolKind.Snp) return 0;
        return comp.Parameters.Count(p => p.Name is not "NumPorts" and not "NumFreqs" && !string.IsNullOrEmpty(p.Name));
    }

    private static bool ParamNameOrderEquals(IEnumerable<EditableParameter> a, IReadOnlyList<EditableParameter> b)
    {
        var aList = a.ToList();
        if (aList.Count != b.Count) return false;
        for (int i = 0; i < aList.Count; i++)
            if (aList[i].Name != b[i].Name) return false;
        return true;
    }
}
