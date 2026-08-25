using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// VM for the .ctech editor — layer table, stackup, DRC rules, live validation.
///
/// <b>Undo is coarse-grained whole-<see cref="Technology"/> snapshots, not the fine-grained
/// per-field <see cref="IUiCommand"/>s the schematic and symbol editors use — a deliberate
/// departure.</b> Those editors mutate large geometry documents where cloning the whole document
/// per edit would be far too expensive, so they record just the delta. A <see cref="Technology"/>
/// is the opposite case: at most tens of layers, a handful of stackup entries, and a few DRC
/// rules — small enough that <see cref="TechPersistence.Serialize"/> (already exhaustively tested
/// as the .ctech round-trip) doubles as an exact, trivial-to-implement deep clone. One snapshot is
/// pushed per <i>committed</i> edit (a field commit, an add, a remove, a reorder) — never per
/// keystroke — via <see cref="CommitEdit"/>, which no-ops when the edit turned out to be a no-op.
/// <see cref="TechValidation.Validate"/> is re-run after every committed edit (cheap, never
/// throws — no reason to defer it).
/// </summary>
public sealed partial class TechEditorViewModel : ObservableObject
{
    /// <summary>Absolute path of the .ctech file. Never null — a Technology is always
    /// workspace-scoped configuration; unlike a layout there is no scratch/unsaved-floating state.</summary>
    public string FilePath { get; }

    /// <summary>The live, mutable working copy. Replaced wholesale by <see cref="ApplySnapshot"/>
    /// (Execute/Undo of a <see cref="TechSnapshotCommand"/>) — row view models hold references into
    /// whichever instance was current when they were built and are expected to be orphaned and
    /// rebuilt whenever that happens (mirrors <see cref="CellParameterRowViewModel"/>'s convention).</summary>
    public Technology Working { get; private set; }

    public UndoRedoStack UndoRedo { get; } = new();
    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    public IRelayCommand SaveCommand { get; }

    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private IReadOnlyList<string> _validationIssues = [];
    public bool HasValidationIssues => ValidationIssues.Count > 0;

    public ObservableCollection<LayerRowViewModel>        Layers        { get; } = [];
    public ObservableCollection<StackupLayerRowViewModel> StackupLayers { get; } = [];
    public ObservableCollection<DrcRuleRowViewModel>      DrcRules      { get; } = [];

    // ── Row filters (one per tab) ──────────────────────────────────────────────
    // A real process carries several hundred layers (an imported PDK measured 377), so every one of
    // these lists is virtualized and none of them is scannable by eye. Each tab therefore owns its
    // OWN filter: the Layers and Interchange tabs list the same layers for different purposes, and
    // narrowing one to "metal" while hunting a Gerber suffix on the other would be a surprise, not a
    // convenience.
    //
    // The Filtered* collections — not Layers/StackupLayers/DrcRules — are what the view binds to.
    // The unfiltered collections stay the authoritative projection of Working, rebuilt wholesale by
    // RebuildAll after every committed edit (see ApplySnapshot); each rebuild re-applies the filters,
    // so a filter survives an edit, an undo and a redo without the view having to know it exists.

    public ObservableCollection<LayerRowViewModel>        FilteredLayers            { get; } = [];
    public ObservableCollection<LayerRowViewModel>        FilteredInterchangeLayers { get; } = [];
    public ObservableCollection<StackupLayerRowViewModel> FilteredStackupLayers     { get; } = [];
    public ObservableCollection<DrcRuleRowViewModel>      FilteredDrcRules          { get; } = [];

    [ObservableProperty] private string _layerFilter       = "";
    [ObservableProperty] private string _interchangeFilter = "";
    [ObservableProperty] private string _stackupFilter     = "";
    [ObservableProperty] private string _drcFilter         = "";

    partial void OnLayerFilterChanged(string value)       => ApplyLayerFilter();
    partial void OnInterchangeFilterChanged(string value) => ApplyInterchangeFilter();
    partial void OnStackupFilterChanged(string value)     => ApplyStackupFilter();
    partial void OnDrcFilterChanged(string value)         => ApplyDrcFilter();

    /// <summary>"37 of 377" while a filter is narrowing the list, the plain total otherwise — the
    /// only cue that a filter is the reason a layer someone expects to see is not on screen.</summary>
    public string LayerFilterSummary       => Summarize(FilteredLayers.Count,            Layers.Count);
    public string InterchangeFilterSummary => Summarize(FilteredInterchangeLayers.Count, Layers.Count);
    public string StackupFilterSummary     => Summarize(FilteredStackupLayers.Count,     StackupLayers.Count);
    public string DrcFilterSummary         => Summarize(FilteredDrcRules.Count,          DrcRules.Count);

    private static string Summarize(int shown, int total) =>
        shown == total ? total.ToString() : $"{shown} of {total}";

    /// <summary>Case-insensitive substring, against the COMMITTED name rather than the staged text a
    /// row is displaying: filtering on the staged text would make a row vanish mid-rename (an empty
    /// field matches nothing), which is exactly when the user most needs to still see it.</summary>
    private static bool Matches(string name, string query) =>
        query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilters()
    {
        ApplyLayerFilter();
        ApplyInterchangeFilter();
        ApplyStackupFilter();
        ApplyDrcFilter();
    }

    private void ApplyLayerFilter()
    {
        var q = LayerFilter.Trim();
        FilteredLayers.Clear();
        foreach (var r in Layers)
            if (Matches(r.Layer.Name, q)) FilteredLayers.Add(r);
        OnPropertyChanged(nameof(LayerFilterSummary));
        NotifyBulkToggleState();
    }

    private void ApplyInterchangeFilter()
    {
        var q = InterchangeFilter.Trim();
        FilteredInterchangeLayers.Clear();
        foreach (var r in Layers)
            if (Matches(r.Layer.Name, q)) FilteredInterchangeLayers.Add(r);
        OnPropertyChanged(nameof(InterchangeFilterSummary));
    }

    private void ApplyStackupFilter()
    {
        var q = StackupFilter.Trim();
        FilteredStackupLayers.Clear();
        foreach (var r in StackupLayers)
            if (Matches(r.Layer.Name, q)) FilteredStackupLayers.Add(r);
        OnPropertyChanged(nameof(StackupFilterSummary));
    }

    private void ApplyDrcFilter()
    {
        var q = DrcFilter.Trim();
        FilteredDrcRules.Clear();
        foreach (var r in DrcRules)
            if (Matches(r.Rule.Name, q)) FilteredDrcRules.Add(r);
        OnPropertyChanged(nameof(DrcFilterSummary));
    }

    // ── Bulk Visible/Selectable, over the LISTED layers ────────────────────────
    // Deliberately scoped to what the filter is currently showing, not to Working.Layers: the two
    // coincide when no filter is set, and when one IS set "hide everything I am looking at" is the
    // useful operation — "hide all 377 layers including the ones I filtered away" is not. The
    // tooltips in the view say so, because the distinction is invisible otherwise.
    //
    // Setter-not-command so the ToggleButton's own checked state IS the answer to "is every listed
    // layer visible?", with no second source of truth to keep in step.

    public bool AllShownLayersVisible
    {
        get => FilteredLayers.Count > 0 && FilteredLayers.All(r => r.Layer.Visible);
        set => SetAllShownLayerFlags(value, null);
    }

    public bool AllShownLayersSelectable
    {
        get => FilteredLayers.Count > 0 && FilteredLayers.All(r => r.Layer.Selectable);
        set => SetAllShownLayerFlags(null, value);
    }

    private void NotifyBulkToggleState()
    {
        OnPropertyChanged(nameof(AllShownLayersVisible));
        OnPropertyChanged(nameof(AllShownLayersSelectable));
    }

    /// <summary>One undo entry for the whole sweep, not one per layer — the coarse whole-technology
    /// snapshot this editor already uses makes that free, and 377 undo steps to walk back out of one
    /// click would be the alternative (the same reasoning <see cref="MergeFrom"/> records).</summary>
    private void SetAllShownLayerFlags(bool? visible, bool? selectable)
    {
        var rows = FilteredLayers.ToList();
        if (rows.Count == 0) { NotifyBulkToggleState(); return; }

        var before = SnapshotJson();
        foreach (var r in rows)
        {
            if (visible    is { } v) r.Layer.Visible    = v;
            if (selectable is { } s) r.Layer.Selectable = s;
        }

        string what = rows.Count == Layers.Count ? "all layers" : $"{rows.Count} listed layers";
        var description = visible is { } vv
            ? (vv ? $"Show {what}"          : $"Hide {what}")
            : (selectable is true ? $"Make {what} selectable" : $"Make {what} unselectable");

        // A no-op commit rebuilds nothing (CommitEdit returns early), so the toggle's own state still
        // has to be re-announced here or the button would stay where the click left it.
        CommitEdit(before, description);
        NotifyBulkToggleState();
    }

    public static IReadOnlyList<BoundaryCondition> BoundaryConditions { get; } = Enum.GetValues<BoundaryCondition>();
    public static IReadOnlyList<DrcRuleKind>        DrcRuleKinds       { get; } = Enum.GetValues<DrcRuleKind>();
    public static IReadOnlyList<DrcSeverity>         DrcSeverities     { get; } = Enum.GetValues<DrcSeverity>();
    public static IReadOnlyList<LayoutUnit>          DisplayUnitOptions { get; } = Enum.GetValues<LayoutUnit>();

    private bool _suppressBoundaryCommit;
    private bool _suppressDisplayUnitCommit;

    [ObservableProperty] private BoundaryCondition _stackupTop;
    [ObservableProperty] private BoundaryCondition _stackupBottom;

    /// <summary>brief-technology-editor-units-and-layers.md R-tec-3/4: the seed for a NEWLY
    /// CREATED layout's own <c>DisplayUnit</c> (<c>WorkspaceViewModel.NewLayoutAsync</c>/
    /// <c>NewLayoutCommand</c> read <see cref="Technology.DefaultDisplayUnit"/> once, at creation
    /// time, exactly like <see cref="Technology.DefaultSnapDbu"/> already does). Editing this value
    /// here NEVER touches any already-open or already-saved layout — L0c's own invariant ("never
    /// re-seed an open layout's DisplayUnit/SnapDbu") stands unchanged; each <c>.clay</c> stores its
    /// own unit, and this technology value is consulted only at the one moment a layout is first
    /// created. Retargeting an EXISTING layout to this technology is a separate, already-built,
    /// explicit opt-in (<c>LayoutEditorViewModel.Retarget.cs</c>'s <c>adoptUnits</c> flag,
    /// default off) — this property does not change that.</summary>
    [ObservableProperty] private LayoutUnit _defaultDisplayUnit;

    partial void OnStackupTopChanged(BoundaryCondition value)
    {
        if (_suppressBoundaryCommit || value == Working.Stackup.Top) return;
        var before = SnapshotJson();
        Working.Stackup.Top = value;
        CommitEdit(before, "Change top boundary condition");
    }

    partial void OnStackupBottomChanged(BoundaryCondition value)
    {
        if (_suppressBoundaryCommit || value == Working.Stackup.Bottom) return;
        var before = SnapshotJson();
        Working.Stackup.Bottom = value;
        CommitEdit(before, "Change bottom boundary condition");
    }

    partial void OnDefaultDisplayUnitChanged(LayoutUnit value)
    {
        if (_suppressDisplayUnitCommit || value == Working.DefaultDisplayUnit) return;
        var before = SnapshotJson();
        Working.DefaultDisplayUnit = value;
        CommitEdit(before, "Change default display unit for new layouts");
    }

    /// <summary>
    /// Fired after every committed edit AND after every undo/redo (both go through
    /// <see cref="ApplySnapshot"/> — see there) with a deep clone of the new <see cref="Working"/> —
    /// the workspace's cue to call <c>TechnologyCache.SetLive(path, clone)</c> so open layouts see
    /// the in-progress edit immediately, without a Save (brief-L1-fix-path-seams-and-live-tech.md §2).
    /// Always a clone, never <see cref="Working"/> itself — see <see cref="ApplySnapshot"/>.
    /// </summary>
    public event Action<string, Technology>? TechLiveChanged;

    /// <summary>Fired after a successful save with the absolute path — the workspace's cue to
    /// call <c>TechnologyCache.Invalidate(path)</c>, which is what fires L0c's live-refresh seam.</summary>
    public event Action<string>? TechSaved;

    /// <summary>Raised when a save fails (e.g. a read-only / unwritable location). A failed save
    /// must surface an error, never crash the app — mirrors <see cref="LayoutEditorViewModel"/>.</summary>
    public event Action<string>? SaveError;

    public TechEditorViewModel(string filePath, Technology tech)
    {
        FilePath = filePath;
        Working  = tech;

        UndoCommand = new RelayCommand(() => UndoRedo.Undo(), () => UndoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => UndoRedo.Redo(), () => UndoRedo.CanRedo);
        SaveCommand = new RelayCommand(Save);

        UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo))    UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo))    RedoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.IsModified)) IsDirty = UndoRedo.IsModified;
        };

        RebuildAll();
    }

    // ── Save ───────────────────────────────────────────────────────────────────
    // Saving is permitted while validation issues exist — §2.4's rule is that a bad technology
    // warns and still works; refusing to save a work-in-progress would be worse than the problem.

    private void Save()
    {
        try
        {
            TechPersistence.SaveToFile(FilePath, Working);
        }
        catch (Exception ex)
        {
            SaveError?.Invoke($"Couldn't save technology to '{FilePath}': {ex.Message}");
            return;
        }
        UndoRedo.MarkSaved();   // IsModified → false → IsDirty → false, via the subscription above
        TechSaved?.Invoke(FilePath);
    }

    // ── Snapshot undo plumbing (internal — used by row view models) ───────────

    internal string SnapshotJson() => TechPersistence.Serialize(Working);

    /// <summary>Pushes an undo entry for an edit already applied in place to <see cref="Working"/>.
    /// No-ops (nothing pushed, nothing rebuilt) when the edit turned out not to change anything.</summary>
    internal void CommitEdit(string beforeJson, string description)
    {
        var afterJson = SnapshotJson();
        if (afterJson == beforeJson) return;
        UndoRedo.Execute(new TechSnapshotCommand(this, beforeJson, afterJson, description));
    }

    /// <summary>
    /// Replaces <see cref="Working"/> with <paramref name="tech"/> as a single UNDOABLE, DIRTYING edit
    /// — the seam for a change that was decided outside this editor and must still be visible in it and
    /// savable from it.
    ///
    /// <para>Import Board is the case that needed it. It recovers a board's own layers and stackup and
    /// installs them as a live (unsaved) <c>TechnologyCache</c> override, which every open layout
    /// resolves against immediately — but the <c>.ctech</c> EDITOR read the file from disk, so the
    /// recovered layers were invisible in the one place the import's own message told the user to go
    /// and save them from. Routing through the undo stack (rather than assigning <see cref="Working"/>)
    /// is what makes the editor show them, mark itself dirty, save them, and undo them.</para>
    ///
    /// <para>No-op when the content already matches, so re-applying the same override costs nothing and
    /// cannot dirty a clean editor.</para>
    /// </summary>
    internal void ReplaceWorkingAsEdit(Technology tech, string description)
    {
        string beforeJson = SnapshotJson();
        string afterJson  = TechPersistence.Serialize(tech);
        if (afterJson == beforeJson) return;
        UndoRedo.Execute(new TechSnapshotCommand(this, beforeJson, afterJson, description));
    }

    /// <summary>Replaces <see cref="Working"/> wholesale and re-projects every row collection.
    /// Called by <see cref="TechSnapshotCommand"/> Execute/Undo — never call directly. This is the
    /// ONE choke point for both a fresh commit (<see cref="CommitEdit"/> pushes a
    /// <see cref="TechSnapshotCommand"/> whose Execute() calls back in here) and undo/redo, which is
    /// exactly why <see cref="TechLiveChanged"/> only needs to fire from this one place to cover
    /// every case the brief's event table lists.</summary>
    internal void ApplySnapshot(string json)
    {
        Working = TechPersistence.Deserialize(json);
        RebuildAll();

        // R-fix-1: a SEPARATE deserialize of the same json, never Working itself — Working keeps
        // mutating in place until the next commit, and a later undo/redo replaces the Working
        // reference wholesale, so a consumer holding Working directly would either observe
        // half-applied edits or silently stop updating after the first undo. Reusing `json` (already
        // in hand) rather than re-serializing Working is the "one extra deserialize" the brief notes.
        TechLiveChanged?.Invoke(FilePath, TechPersistence.Deserialize(json));
    }

    private void RebuildAll()
    {
        _suppressDisplayUnitCommit = true;
        DefaultDisplayUnit = Working.DefaultDisplayUnit;
        _suppressDisplayUnitCommit = false;

        RebuildLayers();
        RebuildStackup();
        RebuildDrcRules();
        ApplyFilters();   // every rebuild replaces the row VMs — the filtered views must follow
        Revalidate();
    }

    private void Revalidate() => ValidationIssues = TechValidation.Validate(Working);

    partial void OnValidationIssuesChanged(IReadOnlyList<string> value)
        => OnPropertyChanged(nameof(HasValidationIssues));

    // ── Layer table ────────────────────────────────────────────────────────────

    private void RebuildLayers()
    {
        // Recomputed before the rows, because each row exposes it as its dropdown's item source and
        // a row built against the previous technology's table would offer the wrong stipples.
        _fillPatternChoices = null;

        Layers.Clear();
        foreach (var l in Working.Layers)
            Layers.Add(new LayerRowViewModel(l, this));
    }

    private IReadOnlyList<string>? _fillPatternChoices;

    /// <summary>
    /// The stipples a layer row may choose from — "(solid)" first, then the technology's own table
    /// in its own order.
    ///
    /// <para>Only what this technology defines. There is no built-in palette to add: a stipple is
    /// process data, arriving with the layer table that declares it, and offering circuitRF-invented
    /// masks alongside would let a layer be given a fill its process never specified.</para>
    /// </summary>
    internal IReadOnlyList<string> FillPatternChoices
    {
        get
        {
            if (_fillPatternChoices is not null) return _fillPatternChoices;
            var choices = new List<string>(Working.FillPatterns.Count + 1) { LayerRowViewModel.NoFillPattern };
            foreach (var p in Working.FillPatterns)
                if (p.Name is { Length: > 0 }) choices.Add(p.Name);
            return _fillPatternChoices = choices;
        }
    }

    /// <summary>Lowest layer number strictly greater than every existing layer's number, datatype 0 —
    /// guaranteed not to collide with anything already present (never a duplicate by construction).</summary>
    private LayerKey NextFreeLayerKey()
    {
        int maxLayer = 0;
        foreach (var l in Working.Layers)
            if (l.Key.Layer > maxLayer) maxLayer = l.Key.Layer;
        return new LayerKey(maxLayer + 1, 0);
    }

    [RelayCommand]
    private void AddLayer()
    {
        var before = SnapshotJson();
        var key    = NextFreeLayerKey();
        Working.Layers.Add(new LayerDef
        {
            Key         = key,
            Name        = $"Layer {key.Layer}",
            Color       = FallbackPalette.For(key).Color,
            ZOrder      = Working.Layers.Count > 0 ? Working.Layers[^1].ZOrder + 1 : 0,
            Purpose     = "drawing",
        });
        CommitEdit(before, "Add layer");
    }

    internal void DuplicateLayer(LayerRowViewModel row)
    {
        var before = SnapshotJson();
        var key    = NextFreeLayerKey();
        var src    = row.Layer;
        int index  = Working.Layers.IndexOf(src);
        var clone  = new LayerDef
        {
            Key         = key,
            Name        = $"{src.Name} copy",
            Color       = src.Color,
            FillOpacity = src.FillOpacity,
            ZOrder      = src.ZOrder,
            Visible     = src.Visible,
            Selectable  = src.Selectable,
            Purpose     = src.Purpose,
        };
        if (index >= 0) Working.Layers.Insert(index + 1, clone);
        else Working.Layers.Add(clone);
        CommitEdit(before, $"Duplicate {src.Name}");
    }

    internal void RemoveLayer(LayerRowViewModel row)
    {
        var before = SnapshotJson();
        Working.Layers.Remove(row.Layer);
        CommitEdit(before, $"Remove layer {row.Layer.Name}");
    }

    /// <summary>Moves a layer one slot within <see cref="Technology.Layers"/> and swaps its
    /// Z-order value with the layer it swapped past, so the numeric field stays meaningful for
    /// browsing even though sorting the grid itself never touches persisted order (see class
    /// header and §2's "Sorting is display-only").</summary>
    internal void MoveLayer(LayerRowViewModel row, int direction)
    {
        int index = Working.Layers.IndexOf(row.Layer);
        int other = index + direction;
        if (index < 0 || other < 0 || other >= Working.Layers.Count) return;

        var before = SnapshotJson();
        (Working.Layers[index], Working.Layers[other]) = (Working.Layers[other], Working.Layers[index]);
        (Working.Layers[index].ZOrder, Working.Layers[other].ZOrder) =
            (Working.Layers[other].ZOrder, Working.Layers[index].ZOrder);
        CommitEdit(before, direction < 0 ? $"Move {row.Layer.Name} up" : $"Move {row.Layer.Name} down");
    }

    // ── Stackup ────────────────────────────────────────────────────────────────

    private void RebuildStackup()
    {
        _suppressBoundaryCommit = true;
        StackupTop    = Working.Stackup.Top;
        StackupBottom = Working.Stackup.Bottom;
        _suppressBoundaryCommit = false;

        StackupLayers.Clear();
        foreach (var sl in Working.Stackup.Layers)
            StackupLayers.Add(new StackupLayerRowViewModel(sl, this));
    }

    [RelayCommand]
    private void AddDielectricLayer() => AddStackupLayer(StackupKind.Dielectric);

    [RelayCommand]
    private void AddConductorLayer() => AddStackupLayer(StackupKind.Conductor);

    [RelayCommand]
    private void AddViaLayer() => AddStackupLayer(StackupKind.Via);

    private void AddStackupLayer(StackupKind kind)
    {
        var before = SnapshotJson();
        Working.Stackup.Layers.Add(new StackupLayer
        {
            Kind         = kind,
            Name         = $"New {kind}",
            ThicknessDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Um, LayoutUnits.DefaultDbuPerMicron),
        });
        CommitEdit(before, $"Add {kind} stackup layer");
    }

    internal void RemoveStackupLayer(StackupLayerRowViewModel row)
    {
        var before = SnapshotJson();
        Working.Stackup.Layers.Remove(row.Layer);
        CommitEdit(before, $"Remove stackup layer {row.Layer.Name}");
    }

    internal void MoveStackupLayer(StackupLayerRowViewModel row, int direction)
    {
        int index = Working.Stackup.Layers.IndexOf(row.Layer);
        int other = index + direction;
        if (index < 0 || other < 0 || other >= Working.Stackup.Layers.Count) return;

        var before = SnapshotJson();
        (Working.Stackup.Layers[index], Working.Stackup.Layers[other]) =
            (Working.Stackup.Layers[other], Working.Stackup.Layers[index]);
        CommitEdit(before, direction < 0 ? $"Move {row.Layer.Name} up" : $"Move {row.Layer.Name} down");
    }

    // ── DRC rules ──────────────────────────────────────────────────────────────

    private void RebuildDrcRules()
    {
        DrcRules.Clear();
        foreach (var r in Working.DrcRules)
            DrcRules.Add(new DrcRuleRowViewModel(r, this));
    }

    [RelayCommand]
    private void AddDrcRule()
    {
        var before = SnapshotJson();
        var layer  = Working.Layers.Count > 0 ? Working.Layers[0].Key : default;
        Working.DrcRules.Add(new DrcRule
        {
            Name  = NextFreeDrcRuleName(),
            Kind  = DrcRuleKind.MinWidth,
            Layer = layer,
        });
        CommitEdit(before, "Add DRC rule");
    }

    private string NextFreeDrcRuleName()
    {
        var existing = new HashSet<string>(Working.DrcRules.Select(r => r.Name), StringComparer.Ordinal);
        for (int i = 1; ; i++)
        {
            var candidate = $"Rule{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    internal void RemoveDrcRule(DrcRuleRowViewModel row)
    {
        var before = SnapshotJson();
        Working.DrcRules.Remove(row.Rule);
        CommitEdit(before, $"Remove DRC rule {row.Rule.Name}");
    }

    /// <summary>
    /// Merges another technology's chosen sections into this one as ONE undoable edit.
    ///
    /// <para>One snapshot, not one per item: a user who imports a layer table and does not like the
    /// result wants Ctrl+Z to undo "the import", not to walk back out of it three hundred times.
    /// The coarse-snapshot undo this editor already uses makes that free.</para>
    /// </summary>
    public TechMergeReport MergeFrom(
        Technology source, TechSection sections, TechMergeMode mode,
        IReadOnlySet<string>? replaceKeys = null)
    {
        var before = SnapshotJson();
        var report = TechnologyMerge.Merge(Working, source, sections, mode, replaceKeys);

        if (report.ChangedNothing) return report;

        CommitEdit(before, "Import from technology");
        RebuildAll();
        return report;
    }
}
