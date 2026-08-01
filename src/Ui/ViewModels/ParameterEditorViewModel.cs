using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
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

    // ── MKlopf entry-mode switch (Z1/Z2 <-> W1/W2, L <-> F3db) ────────────────

    public IRelayCommand ToggleMklopfImpedanceEntryCommand { get; }
    public IRelayCommand ToggleMklopfLengthEntryCommand    { get; }

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
        ShowSnpFileCommand    = new AsyncRelayCommand(RevealSnpFileAsync,
            () => !string.IsNullOrWhiteSpace(SnpFilePath));
        OpenCvEditorCommand   = new AsyncRelayCommand(OpenCvEditorAsync);

        ToggleMklopfImpedanceEntryCommand = new RelayCommand(ToggleMklopfImpedanceEntry, () => IsMklopfTarget);
        ToggleMklopfLengthEntryCommand    = new RelayCommand(ToggleMklopfLengthEntry,    () => IsMklopfTarget);
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

    // ── NonlinearC C–V editor ──────────────────────────────────────────────────

    public bool ShowCvEditorButton => _target?.Symbol == SymbolKind.NonlinearC;

    /// <summary>Callback set by the view to open the C-V editor dialog.</summary>
    public Func<Task>? OpenCvEditorDialogAsync { get; set; }

    public IAsyncRelayCommand OpenCvEditorCommand { get; private set; } = null!;

    private async Task OpenCvEditorAsync()
    {
        if (OpenCvEditorDialogAsync is not null)
            await OpenCvEditorDialogAsync();
    }

    // ── SnP panel ─────────────────────────────────────────────────────────────

    public bool IsSnp => _target?.Symbol == SymbolKind.Snp;

    /// <summary>Callback set by the view so the VM can open a native file picker.</summary>
    public Func<Task<string?>>? PickSnpFileAsync { get; set; }

    /// <summary>Callback set by the view so a file-valued parameter can offer a Browse… picker.
    /// Falls back to the Touchstone picker's own seam when the host supplies only that one.
    ///
    /// <para>The setter pushes the picker onto rows that already exist. The view can only supply it
    /// once its DataContext is set, which is after <see cref="BuildRows"/> has run — assigning a
    /// plain field here leaves every already-built row holding null and no Browse… button.</para>
    /// </summary>
    public Func<Task<string?>>? PickModelFileAsync
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            foreach (var row in Rows)
                if (row.IsFilePathParam)
                    row.PickFileAsync = value;
        }
    }

    /// <summary>Callback set by the view to reveal a file in the OS file manager.</summary>
    public Func<string, Task>? RevealFileAsync { get; set; }

    public static string[] SnpPinConfigOptions { get; } = ["Standard", "SplitLR", "DualRow"];
    public static string[] SnpPitchOptions     { get; } = ["Tight",    "Loose"];

    public IAsyncRelayCommand PickSnpFileCommand    { get; private set; } = null!;
    public IAsyncRelayCommand ShowSnpFileCommand    { get; private set; } = null!;

    [ObservableProperty] private string _snpFilePath       = "";
    [ObservableProperty] private bool   _snpRefNode        = false;
    [ObservableProperty] private int    _snpPinConfigIndex = 0;
    [ObservableProperty] private int    _snpPitchIndex     = 1;
    [ObservableProperty] private string _snpPortCountText  = "";

    partial void OnSnpFilePathChanged(string value) => ShowSnpFileCommand.NotifyCanExecuteChanged();

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
        // Prefer a workspace-relative path (portable); falls back to absolute per SnpPathPolicy.
        string stored = SnpPathPolicy.ToStored(path, _schematicVm.WorkspaceRoot);
        var fileParam = newParams.FirstOrDefault(p => p.Name == "File");
        if (fileParam is not null) fileParam.Expression = stored;

        if (TouchstoneIO.TryGetPortCount(path, out int n, out _))
        {
            var numPorts = newParams.FirstOrDefault(p => p.Name == "NumPorts");
            if (numPorts is not null) numPorts.Expression = n.ToString();
        }

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    private async Task RevealSnpFileAsync()
    {
        if (RevealFileAsync is null || string.IsNullOrWhiteSpace(SnpFilePath)) return;
        string path = SnpFilePath;
        if (!System.IO.Path.IsPathRooted(path) && _schematicVm?.WorkspaceRoot is { Length: > 0 } root)
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path.Replace('\\', '/')));
        await RevealFileAsync(path);
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

    // ── Internal surface for view (C-V editor dialog) ────────────────────────

    internal SchematicViewModel? SchematicVm => _schematicVm;
    internal EditableComponent?  Target      => _target;

    // ── Close button (dialog host shows; embedded host hides) ─────────────────

    [ObservableProperty] private bool _showClose;

    // ── Extensible parameter types ─────────────────────────────────────────────

    /// <summary>True when the current target type supports user-added parameter groups (P1Tone, ToneSource, ZPort, SDD, VAR).</summary>
    public bool AllowsAddParameter
        => ComponentTypeRegistry.UserParamTemplate(_target?.Symbol ?? SymbolKind.Ground) is not null;

    // ── MKlopf entry-mode switch (brief-cell-first-and-ui-fixes.md follow-up: R-klp-3a/R-klp-3's
    // Z1/Z2<->W1/W2 and L<->F3db alternate entry routes had no UI to actually reach them — the
    // factory's own ContainsKey resolution was already correct; only the "how does the user get
    // there" affordance was missing.) ─────────────────────────────────────────────────────────────

    /// <summary>True only for a placed MKlopf instance — gates the toggle buttons in the view.</summary>
    public bool IsMklopfTarget => _target?.Symbol == SymbolKind.Mklopf;

    /// <summary>True when W1/W2 is the currently-authoritative impedance-entry route (Z1/Z2
    /// otherwise) — the two are mutually exclusive by construction (the toggle always removes one
    /// pair and adds the other), so checking for either name's presence is sufficient.</summary>
    public bool MklopfUsesWidthEntry => _target?.Parameters.Any(p => p.Name == "W1") == true;

    /// <summary>True when F3db is the currently-authoritative length-entry route (L otherwise).</summary>
    public bool MklopfUsesF3dbEntry => _target?.Parameters.Any(p => p.Name == "F3db") == true;

    /// <summary>Button label names the route it switches TO, not the one currently showing — a
    /// static "Entry mode" label with no indication of the destination would leave the user
    /// guessing what clicking it does.</summary>
    public string MklopfImpedanceToggleLabel => MklopfUsesWidthEntry ? "Use Z1/Z2" : "Use W1/W2";
    public string MklopfLengthToggleLabel     => MklopfUsesF3dbEntry  ? "Use L"     : "Use F3db";

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
            NotifyMklopfState();
            UpdateCanRemoveTopGroup();
            return;
        }

        _target = comp;
        _isRefreshing = true;

        IsEmptyState      = false;
        TypeDisplayName   = TypeDisplayNameFor(comp);
        StagedInstanceName = comp.InstanceName;
        ShowTypeLabel     = comp.ShowTypeLabel;
        ShowInstanceName  = comp.ShowInstanceName;

        // Build rows — NumPorts, NumFreqs, blank-name params, and SnP-specific params omitted.
        // SnP components use a custom panel instead of generic rows.
        //
        // (See AdoptCellDeclaredParameters below for why a cell-ref component is topped up first.)
        Rows.Clear();
        if (_schematicVm is not null && comp.Symbol != SymbolKind.Snp)
        {
            AdoptCellDeclaredParameters(comp);

            var built = new List<ParameterRowViewModel>();
            foreach (var param in comp.Parameters)
            {
                if (param.Name is "NumPorts" or "NumFreqs" or "CvData" || string.IsNullOrEmpty(param.Name)) continue;
                var row = new ParameterRowViewModel(param, _schematicVm, comp.Symbol, comp);
                if (row.IsFilePathParam) row.PickFileAsync = PickModelFileAsync;
                built.Add(row);
            }

            // WHICH FILE the part is modelled from comes first, then WHICH FORMULATION of it, then the
            // values. That is the order the questions actually arrive in — the later answers only mean
            // anything once the earlier ones are settled — and it puts the two a user of an imported kit
            // reaches for at the top instead of buried among a dozen numbers. Stable within each group,
            // so the kit's own ordering survives.
            foreach (var row in built.Where(r => r.IsFilePathParam)) Rows.Add(row);
            foreach (var row in built.Where(r => !r.IsFilePathParam && r.IsChoiceParam)) Rows.Add(row);
            foreach (var row in built.Where(r => !r.IsFilePathParam && !r.IsChoiceParam)) Rows.Add(row);
        }

        _isRefreshing = false;

        OnPropertyChanged(nameof(IsSnp));
        OnPropertyChanged(nameof(ShowCvEditorButton));
        OnPropertyChanged(nameof(AllowsAddParameter));
        NotifyMklopfState();
        UpdateCanRemoveTopGroup();
        if (comp.Symbol == SymbolKind.Snp) RefreshSnpProperties();
    }

    /// <summary>
    /// Gives a placed cell reference any parameter its cell declares but the instance does not yet
    /// carry, seeded at the cell's own default.
    ///
    /// <para><b>Why an instance can be missing one at all.</b> An instance's parameter list is
    /// seeded from the cell's published interface at placement — so a cell that GAINS a parameter
    /// afterwards leaves every instance placed before then without it. For an imported kit that is
    /// the ordinary case, not an edge one: the declarations a kit needs are picked up from the
    /// workspace's own kit folder at every open, precisely so a user can add them without
    /// re-importing, and parts placed before that must not be left behind.</para>
    ///
    /// <para><b>No undo entry, and never a value change.</b> This adds only what is absent, at the
    /// cell's declared default — the same value extraction would have used for a missing parameter
    /// anyway. Opening a dialog is not an edit, so it must not land on the undo stack; and an
    /// existing value is never touched, because it may well have been set deliberately.</para>
    /// </summary>
    private void AdoptCellDeclaredParameters(EditableComponent comp)
    {
        if (comp.CellRef is not { Length: > 0 } cellRef) return;
        if (_schematicVm?.EditModel.SchematicDirectory is not { Length: > 0 } dir) return;

        try
        {
            string ccellPath = Path.Combine(Path.GetFullPath(Path.Combine(dir, cellRef)),
                                            CellFolder.CcellFileName);
            if (!File.Exists(ccellPath)) return;

            var declared = CellPersistence.LoadFromFile(ccellPath).Parameters;
            for (int i = 0; i < declared.Count; i++)
            {
                var d = declared[i];
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                if (comp.Parameters.Any(p => p.Name.Equals(d.Name, StringComparison.Ordinal))) continue;

                comp.Parameters.Insert(Math.Min(i, comp.Parameters.Count), new EditableParameter
                {
                    Name            = d.Name,
                    Expression      = d.DefaultExpression,
                    Unit            = d.Unit,
                    Dimension       = d.Dimension,
                    ShowOnSchematic = d.ShowOnSchematic,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // An unreadable .ccell leaves the instance exactly as it was.
        }
    }


    private void NotifyMklopfState()
    {
        OnPropertyChanged(nameof(IsMklopfTarget));
        OnPropertyChanged(nameof(MklopfUsesWidthEntry));
        OnPropertyChanged(nameof(MklopfUsesF3dbEntry));
        OnPropertyChanged(nameof(MklopfImpedanceToggleLabel));
        OnPropertyChanged(nameof(MklopfLengthToggleLabel));
        ToggleMklopfImpedanceEntryCommand.NotifyCanExecuteChanged();
        ToggleMklopfLengthEntryCommand.NotifyCanExecuteChanged();
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

    // ── MKlopf entry-mode switch implementation ───────────────────────────────

    /// <summary>Z1/Z2 ⇄ W1/W2 — converts the CURRENT design (whichever route is active) to the
    /// other route's equivalent values on the resolved substrate, so switching never silently
    /// changes what the taper actually simulates as.</summary>
    private void ToggleMklopfImpedanceEntry()
    {
        if (_target is null || _schematicVm is null || !IsMklopfTarget) return;

        var (h, t, er, lengthUnit) = ResolveMklopfSubstrate();
        var reporter = new MicrostripValidityReporter($"{_target.InstanceName} (entry-mode switch)");
        var newParams = _target.Parameters.Select(p => p.Clone()).ToList();

        if (MklopfUsesWidthEntry)
        {
            double w1 = ReadMklopfSiValue(newParams, "W1", 1e-3);
            double w2 = ReadMklopfSiValue(newParams, "W2", 1e-3);
            var (z1, z2) = MicrostripKlopfEntryConversion.WidthToImpedance(w1, w2, h, t, er, reporter);
            ReplacePair(newParams, "W1", "W2",
                MklopfParam("Z1", FormatOhm(z1), "Ω", UnitDimension.Resistance),
                MklopfParam("Z2", FormatOhm(z2), "Ω", UnitDimension.Resistance));
        }
        else
        {
            double z1 = ReadMklopfSiValue(newParams, "Z1", 50.0);
            double z2 = ReadMklopfSiValue(newParams, "Z2", 50.0);
            var (w1, w2) = MicrostripKlopfEntryConversion.ImpedanceToWidth(z1, z2, h, t, er, reporter);
            ReplacePair(newParams, "Z1", "Z2",
                MklopfParam("W1", FormatLengthInUnit(w1, lengthUnit), lengthUnit, UnitDimension.Length),
                MklopfParam("W2", FormatLengthInUnit(w2, lengthUnit), lengthUnit, UnitDimension.Length));
        }

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    /// <summary>L ⇄ F3db — same "convert the current design, don't reset it" rule as
    /// <see cref="ToggleMklopfImpedanceEntry"/>, using whichever impedance route is currently active
    /// (via <see cref="ResolveMklopfZ1Z2"/>) to compute the length↔cutoff duality.</summary>
    private void ToggleMklopfLengthEntry()
    {
        if (_target is null || _schematicVm is null || !IsMklopfTarget) return;

        var (h, t, er, lengthUnit) = ResolveMklopfSubstrate();
        var reporter = new MicrostripValidityReporter($"{_target.InstanceName} (entry-mode switch)");
        var newParams = _target.Parameters.Select(p => p.Clone()).ToList();
        var (z1, z2) = ResolveMklopfZ1Z2(newParams, h, t, er, reporter);
        double gammaMax = ReadMklopfSiValue(newParams, "GammaMax", 0.05);

        if (MklopfUsesF3dbEntry)
        {
            double f3db = ReadMklopfSiValue(newParams, "F3db", 1e9);
            double l = MicrostripKlopfEntryConversion.F3dbToLength(z1, z2, gammaMax, f3db, h, t, er, reporter);
            ReplaceSingle(newParams, "F3db", MklopfParam("L", FormatLengthInUnit(l, lengthUnit), lengthUnit, UnitDimension.Length));
        }
        else
        {
            double l = ReadMklopfSiValue(newParams, "L", 0.02);
            double f3db = MicrostripKlopfEntryConversion.LengthToF3db(z1, z2, gammaMax, l, h, t, er, reporter);
            // F3db has no workspace-technology unit convention of its own (DefaultDisplayUnit only
            // governs LENGTH) — GHz is a fixed, reasonable RF default regardless of PCB vs. MMIC.
            ReplaceSingle(newParams, "L", MklopfParam("F3db", FormatFrequencyGHz(f3db), "GHz", UnitDimension.Frequency));
        }

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    /// <summary>Resolves Z1/Z2 from whichever route is CURRENTLY active in <paramref name="parms"/> —
    /// used by the length toggle, which needs Z1/Z2 regardless of which impedance-entry route is
    /// active (it never changes that route itself).</summary>
    private static (double Z1, double Z2) ResolveMklopfZ1Z2(
        List<EditableParameter> parms, double h, double t, double er, MicrostripValidityReporter reporter)
    {
        if (parms.Any(p => p.Name == "W1"))
        {
            double w1 = ReadMklopfSiValue(parms, "W1", 1e-3);
            double w2 = ReadMklopfSiValue(parms, "W2", 1e-3);
            return MicrostripKlopfEntryConversion.WidthToImpedance(w1, w2, h, t, er, reporter);
        }
        return (ReadMklopfSiValue(parms, "Z1", 50.0), ReadMklopfSiValue(parms, "Z2", 50.0));
    }

    /// <summary>
    /// The substrate this instance will actually simulate against: the schematic's own workspace
    /// technology (the SAME resolution <c>NetExtractor</c> uses at run time), or — when no
    /// technology resolves — the SAME hardcoded fallback <see cref="ComponentModelFactory"/> uses.
    /// One set of default numbers, read from one place (<see cref="ComponentModelFactory"/>'s own
    /// public constants), not re-guessed here. Also resolves the workspace's own LENGTH display
    /// unit (mil on a PCB board, µm on an MMIC die, mm otherwise — <see cref="MicrostripSubstrateInjection.
    /// LengthUnitFor"/>, the SAME mapping a freshly-placed component's own W/L defaults already use)
    /// so a converted W1/W2/L value is written in the workspace's own convention rather than always
    /// "mm" (owner-reported: the entry-mode switch ignored the workspace's own unit convention).
    /// </summary>
    private (double H, double T, double Er, string LengthUnit) ResolveMklopfSubstrate()
    {
        var tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(_schematicVm?.EditModel.SchematicDirectory);
        var overrides = MicrostripSubstrateInjection.BuildOverrides(tech, out _);

        double h = ComponentModelFactory.DefaultSubstrateHMeters;
        double t = ComponentModelFactory.DefaultSubstrateTMeters;
        double er = ComponentModelFactory.DefaultSubstrateEpsR;
        foreach (var o in overrides)
        {
            if (!double.TryParse(o.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            switch (o.Name)
            {
                case "H":  h = v;  break;
                case "T":  t = v;  break;
                case "Er": er = v; break;
            }
        }
        string lengthUnit = MicrostripSubstrateInjection.LengthUnitFor(tech);
        return (h, t, er, lengthUnit);
    }

    /// <summary>Reads a named parameter's value in SI base units (metres/ohms/hertz/dimensionless),
    /// applying its own displayed unit (via <see cref="UnitNormalizer"/> so a "Ω"/"µm"-style glyph
    /// resolves the same way <c>NetExtractor</c> resolves it at extraction time). Falls back to
    /// <paramref name="fallbackSi"/> — never throws — when the parameter is absent or its expression
    /// isn't a plain number (e.g. it references a variable); switching entry mode on a
    /// variable-driven field is therefore a best-effort conversion, not an exact one, which is
    /// stated here rather than silently assumed away.</summary>
    private static double ReadMklopfSiValue(List<EditableParameter> parms, string name, double fallbackSi)
    {
        var p = parms.FirstOrDefault(x => x.Name == name);
        if (p is null) return fallbackSi;
        if (!double.TryParse(p.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return fallbackSi;
        double scale = Units.Scale(UnitNormalizer.ToEngineUnit(p.Unit)) ?? 1.0;
        return raw * scale;
    }

    private static EditableParameter MklopfParam(string name, string expression, string unit, UnitDimension dim)
        => new() { Name = name, Expression = expression, Unit = unit, ShowOnSchematic = true, Dimension = dim };

    /// <summary>Removes the pair named <paramref name="oldA"/>/<paramref name="oldB"/> (adjacent by
    /// construction — every MKlopf entry route is always added/removed as a pair) and inserts the
    /// two replacements at the same position, preserving row order.</summary>
    private static void ReplacePair(List<EditableParameter> parms, string oldA, string oldB,
        EditableParameter newA, EditableParameter newB)
    {
        int idxA = parms.FindIndex(p => p.Name == oldA);
        int idxB = parms.FindIndex(p => p.Name == oldB);
        int insertAt = idxA >= 0 ? idxA : idxB;
        parms.RemoveAll(p => p.Name == oldA || p.Name == oldB);
        insertAt = Math.Clamp(insertAt < 0 ? parms.Count : insertAt, 0, parms.Count);
        parms.Insert(insertAt, newA);
        parms.Insert(insertAt + 1, newB);
    }

    private static void ReplaceSingle(List<EditableParameter> parms, string oldName, EditableParameter newParam)
    {
        int idx = parms.FindIndex(p => p.Name == oldName);
        parms.RemoveAll(p => p.Name == oldName);
        insertClamped(parms, idx, newParam);

        static void insertClamped(List<EditableParameter> list, int idx, EditableParameter p)
            => list.Insert(Math.Clamp(idx < 0 ? list.Count : idx, 0, list.Count), p);
    }

    private static string FormatOhm(double z) => Math.Round(z, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Formats a length in SI metres as a number in the given display unit (the SAME
    /// <see cref="Units.Scale"/>/<see cref="UnitNormalizer"/> resolution <see cref="ReadMklopfSiValue"/>
    /// uses in reverse — one shared meters-per-unit table, not a second hand-rolled conversion).</summary>
    private static string FormatLengthInUnit(double meters, string unit)
    {
        double scale = Units.Scale(UnitNormalizer.ToEngineUnit(unit)) ?? 1e-3; // metres per unit; "mm" itself if unrecognized
        return Math.Round(meters / scale, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string FormatFrequencyGHz(double hz)=> Math.Round(hz / 1e9, 6).ToString("0.######", CultureInfo.InvariantCulture);

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

        // Rebuild rows entirely whenever the visible parameter NAME SET changed — not just the
        // count. A same-count swap (the MKlopf entry-mode toggle replaces Z1/Z2 with W1/W2, or L
        // with F3db — 2-for-2 or 1-for-1) would otherwise pass a count-only check and fall through
        // to RefreshFromModel, which only refreshes EXISTING rows' STAGED values — every existing
        // row's own EditableParameter reference is already stale at that point regardless
        // (SetParametersCommand always clones fresh objects into comp.Parameters, even for
        // untouched params), so a same-count rename would silently leave a row's label AND value
        // bound to an object no longer in the model at all.
        var visibleNames = VisibleParamNames(_target);
        if (Rows.Count != visibleNames.Count || !Rows.Select(r => r.Name).SequenceEqual(visibleNames))
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
        TypeDisplayName    = TypeDisplayNameFor(_target);
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

    private static List<string> VisibleParamNames(EditableComponent comp)
    {
        // SnP uses a custom panel; generic rows are always empty.
        if (comp.Symbol == SymbolKind.Snp) return [];
        return comp.Parameters
            .Where(p => p.Name is not "NumPorts" and not "NumFreqs" and not "CvData" && !string.IsNullOrEmpty(p.Name))
            .Select(p => p.Name)
            .ToList();
    }

    private static bool ParamNameOrderEquals(IEnumerable<EditableParameter> a, IReadOnlyList<EditableParameter> b)
    {
        var aList = a.ToList();
        if (aList.Count != b.Count) return false;
        for (int i = 0; i < aList.Count; i++)
            if (aList[i].Name != b[i].Name) return false;
        return true;
    }

    /// <summary>
    /// The type shown for a component. A cell-reference component's type is the CELL it references —
    /// its placeholder <see cref="SymbolKind.Generic"/> renders as "X", which says nothing about what
    /// was actually placed. Derived from CellRef so it can never drift from what the canvas draws.
    /// </summary>
    private static string TypeDisplayNameFor(EditableComponent comp)
        => comp.CellRef is { Length: > 0 } cr
            ? System.IO.Path.GetFileName(cr.TrimEnd('/', '\\'))
            : ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount);
}
