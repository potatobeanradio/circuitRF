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
    /// workspace-scoped configuration; unlike a layout there is no scratch/unsaved-floating state.
    /// It moves in exactly one place, <see cref="SaveAs"/>, mirroring
    /// <c>EmSetupEditorViewModel.FilePath</c>.</summary>
    public string FilePath { get; private set; }

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

    /// <summary>Every problem the technology has, whichever tab owns it. The banner does NOT bind to
    /// this — see <see cref="ActiveTabIssues"/>.</summary>
    [ObservableProperty] private IReadOnlyList<TechProblem> _validationProblems = [];

    /// <summary>Every problem's message, in order — what a caller outside the editor asks for.</summary>
    public IReadOnlyList<string> ValidationIssues => [.. ValidationProblems.Select(p => p.Message)];

    /// <summary>
    /// Which tab is showing (0 Layers, 1 Stackup, 2 DRC Rules, 3 Interchange), bound to the
    /// TabControl's own SelectedIndex.
    ///
    /// <para>The validation banner shows <see cref="ActiveTabIssues"/> — this tab's problems only. A
    /// technology imported from a Gerber set can carry a couple of dozen problems, essentially all of
    /// them about interchange aliases, and a banner that recites them over the layer table every time
    /// the file is opened is noise at exactly the moment the user is doing something else. Nothing is
    /// hidden: <see cref="LayersTabHeader"/> and its three siblings carry the count of what is on the
    /// tabs that are not showing.</para>
    /// </summary>
    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>The problems the CURRENTLY SHOWING tab owns — what the banner lists.</summary>
    public IReadOnlyList<string> ActiveTabIssues =>
        [.. ValidationProblems.Where(p => p.Area == AreaOfTab(SelectedTabIndex)).Select(p => p.Message)];

    /// <summary>Whether the technology has ANY problem, on any tab — the plain "is something wrong"
    /// question. The banner asks <see cref="HasActiveTabIssues"/> instead.</summary>
    public bool HasValidationIssues => ValidationProblems.Count > 0;

    /// <summary>Whether the tab currently showing has problems — what makes the banner visible.</summary>
    public bool HasActiveTabIssues => ActiveTabIssues.Count > 0;

    /// <summary>
    /// The one-step repairs this tab's problems offer — each a button under the banner's messages
    /// (field report, 2026-09-22: "Conductor GND claims no drawing layer" was answered with "I have
    /// a gnd layer", because the conductor card opens on its four field rows and the picker that
    /// joins the two sits below them).
    /// </summary>
    public IReadOnlyList<TechFix> ActiveTabFixes =>
        [.. ValidationProblems.Where(p => p.Area == AreaOfTab(SelectedTabIndex) && p.Fix is not null)
                              .Select(p => p.Fix!)];

    /// <summary>Whether there is at least one.</summary>
    public bool HasActiveTabFixes => ActiveTabFixes.Count > 0;

    /// <summary>
    /// Applies a <see cref="TechFix"/> — one undoable edit, through the same commit the drawing-layer
    /// checkbox makes.
    /// </summary>
    [RelayCommand]
    private void ApplyTechFix(TechFix? fix)
    {
        if (fix is null) return;
        var conductor = Working.Stackup.Layers.FirstOrDefault(
            l => l.Kind == StackupKind.Conductor && string.Equals(l.Name, fix.ConductorName, StringComparison.Ordinal));
        if (conductor is null || conductor.DrawingLayers.Contains(fix.Layer)) return;

        var before = SnapshotJson();
        conductor.DrawingLayers.Add(fix.Layer);
        CommitEdit(before, fix.Label);
    }

    public string LayersTabHeader      => TabHeader("Layers",    TechProblemArea.Layers);
    public string StackupTabHeader     => TabHeader("Stackup",   TechProblemArea.Stackup);
    public string DrcTabHeader         => TabHeader("DRC Rules", TechProblemArea.Drc);
    public string InterchangeTabHeader => TabHeader("Interchange", TechProblemArea.Interchange);

    private static TechProblemArea AreaOfTab(int index) => index switch
    {
        1 => TechProblemArea.Stackup,
        2 => TechProblemArea.Drc,
        3 => TechProblemArea.Interchange,
        _ => TechProblemArea.Layers,
    };

    /// <summary>
    /// "Interchange (2)" whenever that tab has problems, plain "Interchange" when it has none.
    ///
    /// <para><b>The count does NOT depend on which tab is showing</b>, and that costs a little
    /// redundancy on purpose. Dropping it from the selected tab — mildly tidier, since the banner
    /// below is already listing those problems in full — changed the header's WIDTH on every click,
    /// which slid the headers to its right sideways under the pointer that had just clicked one.
    /// Same defect as the banner moving the header row vertically, on the other axis. A tab strip
    /// whose labels are constant is worth more than the duplication is worth removing.</para>
    /// </summary>
    private string TabHeader(string title, TechProblemArea area)
    {
        int n = ValidationProblems.Count(p => p.Area == area);
        return n > 0 ? $"{title} ({n})" : title;
    }

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

    /// <summary>
    /// GI3 R-gi3-9. The substrate entries in <see cref="Stackup.Layers"/>' own order, then the vias as
    /// their own group below them.
    ///
    /// <para><b>The MODEL's order is untouched.</b> <c>Stackup.Layers</c> is documented "Ordered TOP to
    /// BOTTOM" and that order IS z — L4d's R-L4d-5, restated by <c>GerberStackupMapping</c>'s
    /// R-L4g-10: a reversed stack simulates cleanly and answers a different question. This is a
    /// PRESENTATION change and nothing here writes to the list. What it fixes is that a via entry, which
    /// has no position in z at all, used to render as a row in the middle of the stack — and a via added
    /// before the conductors rendered above the top copper, reading as a layer sitting over the board.</para>
    /// </summary>
    private void ApplyStackupFilter()
    {
        // R-stk3-7: emptying the collection a ListBox is bound to makes it drop its own SelectedItem
        // and push the null back. The selection is the view model's and is held by NAME; a projection
        // being rebuilt says nothing about it. See SelectedStackupLayerRow's setter.
        bool priorSuppress = _suppressSelectionSync;
        _suppressSelectionSync = true;
        try
        {
            ApplyStackupFilterCore();
        }
        finally { _suppressSelectionSync = priorSuppress; }
    }

    private void ApplyStackupFilterCore()
    {
        var q = StackupFilter.Trim();
        FilteredStackupLayers.Clear();

        // Cleared on EVERY row first, not just the listed ones: a header left set on a row the filter
        // has excluded reappears the moment the filter is cleared, in the wrong place.
        foreach (var r in StackupLayers) r.ShowsViaGroupHeader = false;

        foreach (var r in StackupLayers)
            if (r.Layer.Kind != StackupKind.Via && Matches(r.Layer.Name, q)) FilteredStackupLayers.Add(r);

        bool firstVia = true;
        foreach (var r in StackupLayers)
        {
            if (r.Layer.Kind != StackupKind.Via || !Matches(r.Layer.Name, q)) continue;
            r.ShowsViaGroupHeader = firstVia;
            firstVia = false;
            FilteredStackupLayers.Add(r);
        }

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

    /// <summary>
    /// Whether the Stackup tab's cross-section pane is expanded, and whether its card pane is —
    /// the two small expanders at the top left of each (owner, 2026-09-13), so a user can give the
    /// window to whichever half they are editing.
    ///
    /// <para><b>They persist in the <c>.ctech</c></b> (<c>Stackup.DrawingPaneExpanded</c> /
    /// <c>CardPaneExpanded</c>), so they go through <see cref="CommitEdit"/> like every other edit in
    /// this editor: the choice dirties the document, undoes and saves. That is
    /// <see cref="SetViaDrawLane"/>'s precedent, and the same sentence applies — a cosmetic value is
    /// still a value in the file.</para>
    ///
    /// <para><b>Both true is the default and neither can be set from a file to "both collapsed by
    /// accident"</b>: null reads back as expanded, which is what every technology written before this
    /// means.</para>
    /// </summary>
    [ObservableProperty] private bool _stackupDrawingExpanded = true;

    /// <inheritdoc cref="StackupDrawingExpanded"/>
    [ObservableProperty] private bool _stackupCardsExpanded = true;

    partial void OnStackupDrawingExpandedChanged(bool value)
        => CommitPaneState(() => Working.Stackup.DrawingPaneExpanded = value,
                           value ? "Expand the cross-section" : "Collapse the cross-section");

    partial void OnStackupCardsExpandedChanged(bool value)
        => CommitPaneState(() => Working.Stackup.CardPaneExpanded = value,
                           value ? "Expand the stackup cards" : "Collapse the stackup cards");

    private void CommitPaneState(Action write, string description)
    {
        if (_suppressPaneCommit) return;
        var before = SnapshotJson();
        write();
        CommitEdit(before, description);
    }

    private bool _suppressPaneCommit;

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

    /// <summary>
    /// Raised after every committed edit, undo and redo — the stackup drawing's cue to rebuild its
    /// scene. Fired from <see cref="ApplySnapshot"/> for the same reason
    /// <see cref="TechLiveChanged"/> is: it is the one place <see cref="Working"/> is replaced.
    ///
    /// <para>That single wire is what makes the drawing live for EVERY edit path at once — a card's
    /// text box, a card's combo, an Add button, an undo, a redo, and every edit briefs 4-6 add. There
    /// is no second place to remember (R-stk2-3).</para>
    ///
    /// <para>No payload, deliberately: the subscriber reads <see cref="Working"/>, which this event
    /// is the announcement of. A <c>Technology</c> argument would be a second clone per edit for a
    /// reader that is already looking at the first.</para>
    /// </summary>
    public event Action? StackupChanged;

    /// <summary>Fired after a successful save with the absolute path — the workspace's cue to
    /// call <c>TechnologyCache.Invalidate(path)</c>, which is what fires L0c's live-refresh seam.</summary>
    public event Action<string>? TechSaved;

    /// <summary>Fired after a successful <see cref="SaveAs"/>, with the OLD path and the NEW one.
    /// Separate from <see cref="TechSaved"/> because the workspace has to re-key its open-document
    /// map and invalidate the cache entry for BOTH paths — the old one because this editor is no
    /// longer the live view of it, the new one because there was no entry for it at all.</summary>
    public event Action<string, string>? TechSavedAs;

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

    /// <summary>
    /// Write this technology to a DIFFERENT <c>.ctech</c> and follow it from then on — the tab menu's
    /// Save As… (owner request, 2026-09-09). The original file is left exactly as it was on disk;
    /// Save As is not a move.
    ///
    /// <para><b>What it does NOT do, and what the caller has to say out loud.</b> A design resolves
    /// its technology through an explicit reference — a layout's own <c>TechRef</c>, or the
    /// workspace's <c>DefaultTechRef</c> (<c>TechnologyResolver</c>) — so every open layout goes on
    /// resolving the ORIGINAL path until something repoints it. That is the same thing a schematic's
    /// Save As does to the cells that instantiate it, and it is reported rather than guessed at:
    /// silently repointing a whole workspace's designs at a file the user has only just made would be
    /// a much larger edit than the one they asked for.</para>
    ///
    /// <para>The picker lives on the caller for the reason the rest of this file does: everything
    /// under <c>src/Ui/Layout/</c> is framework-free, so this takes a resolved path and does the I/O.
    /// </para>
    /// </summary>
    public void SaveAs(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return;
        try
        {
            TechPersistence.SaveToFile(newPath, Working);
        }
        catch (Exception ex)
        {
            SaveError?.Invoke($"Couldn't save technology to '{newPath}': {ex.Message}");
            return;
        }

        string oldPath = FilePath;
        FilePath = newPath;
        UndoRedo.MarkSaved();
        TechSavedAs?.Invoke(oldPath, newPath);
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

        // Last, after Working has been replaced and every row collection rebuilt: the drawing reads
        // Working directly, so a cue raised any earlier would rebuild the scene from the technology
        // this edit replaced.
        StackupChanged?.Invoke();
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

        // LAST, and after ApplyFilters: the selection is held by NAME precisely because every row VM
        // above was just destroyed and rebuilt (R-stk3-1), and the ListBox's SelectedItem has to be an
        // item the filtered projection now holds — so the projection has to exist first.
        SyncStackupSelection();

        Revalidate();
    }

    private void Revalidate() => ValidationProblems = TechValidation.Analyze(Working);

    partial void OnValidationProblemsChanged(IReadOnlyList<TechProblem> value) => RaiseValidationViews();

    /// <summary>Changing tab changes only what the banner shows — the headers now read the same
    /// whichever tab is selected, so they are deliberately not re-raised here.</summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ActiveTabIssues));
        OnPropertyChanged(nameof(HasActiveTabIssues));
        OnPropertyChanged(nameof(ActiveTabFixes));
        OnPropertyChanged(nameof(HasActiveTabFixes));
        OnPropertyChanged(nameof(HelpTip));
    }

    // ── Help ──────────────────────────────────────────────────────────────────────────────────────
    //
    // One window edits four unrelated things, and no single documentation page covers all four. So the
    // Help button follows the visible tab rather than opening one chapter and hoping: the stackup has a
    // chapter of its own, and the other three tabs are sections of the Layout Editor chapter.
    //
    // The mapping lives HERE, not in the code-behind, so it can be asserted without a UI platform —
    // and every destination it can produce is listed in DocAnchors.TechEditorLinks, which is what makes
    // the docs build fail if one of those sections stops being emitted.

    /// <summary>The page (relative to <c>docs/user/</c>) and anchor the Help button opens for a tab
    /// index — 0 Layers, 1 Stackup, 2 DRC Rules, 3 Interchange.</summary>
    public static (string Page, string Anchor) HelpDestinationFor(int tabIndex) => tabIndex switch
    {
        1 => ("reference/stackup.html",       ""),
        2 => ("reference/layout-editor.html", "drc"),
        3 => ("reference/layout-editor.html", "interchange"),
        _ => ("reference/layout-editor.html", "technology"),
    };

    /// <summary>Where Help goes from the tab that is showing now.</summary>
    public (string Page, string Anchor) HelpDestination => HelpDestinationFor(SelectedTabIndex);

    /// <summary>What the Help button's tooltip says — it names the tab, because the button's
    /// destination changes with it and a bare "Help" would not say that.</summary>
    public string HelpTip => SelectedTabIndex switch
    {
        1 => "Open the documentation for the Stackup",
        2 => "Open the documentation for design-rule checking",
        3 => "Open the documentation for interchange mappings",
        _ => "Open the documentation for the technology and its layer table",
    };

    private void RaiseValidationViews()
    {
        OnPropertyChanged(nameof(ValidationIssues));
        OnPropertyChanged(nameof(ActiveTabIssues));
        OnPropertyChanged(nameof(HasValidationIssues));
        OnPropertyChanged(nameof(HasActiveTabIssues));
        OnPropertyChanged(nameof(ActiveTabFixes));
        OnPropertyChanged(nameof(HasActiveTabFixes));
        OnPropertyChanged(nameof(LayersTabHeader));
        OnPropertyChanged(nameof(StackupTabHeader));
        OnPropertyChanged(nameof(DrcTabHeader));
        OnPropertyChanged(nameof(InterchangeTabHeader));
    }

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

    // ── Selection: the drawing and the cards point at one thing (brief 3) ─────────────────────────

    /// <summary>
    /// The stackup entry the drawing and the card list are both pointing at, by
    /// <c>StackupLayer.Name</c> — null when nothing is selected.
    ///
    /// <para><b>By NAME, and not by a row VM or a <c>StackupLayer</c> reference.</b>
    /// <see cref="ApplySnapshot"/> assigns <see cref="Working"/> a freshly deserialized instance and
    /// <see cref="RebuildStackup"/> then clears and rebuilds every
    /// <see cref="StackupLayerRowViewModel"/>, so EVERY object identity in the stackup is destroyed
    /// on every committed edit, undo and redo. A held reference survives none of those, and a
    /// selection that silently evaporates on the first edit is worse than no selection at all
    /// (R-stk3-1).</para>
    ///
    /// <para>A rename is the one case a name does not survive, and it is handled where the rename
    /// happens — <see cref="StackupLayerRowViewModel.CommitName"/> re-points this when it renamed the
    /// selected entry (R-stk3-8). Names are unique by construction for new entries
    /// (<see cref="NextFreeStackupName"/>) and a duplicate is a validation problem the editor already
    /// reports; on a duplicate the selection resolves to the FIRST match, stated rather than guarded
    /// against.</para>
    ///
    /// <para>Both surfaces read this one property; neither owns it.</para>
    /// </summary>
    [ObservableProperty] private string? _selectedStackupLayerName;

    /// <summary>
    /// The card list's own <c>SelectedItem</c>, two-way (R-stk3-7).
    ///
    /// <para><c>StackupList</c> is <c>SelectionMode="Single"</c> and two selection models on one list
    /// is a fight, so the <c>ListBox</c>'s selection is DRIVEN from
    /// <see cref="SelectedStackupLayerName"/> and drives it back — clicking a card selects its band
    /// and clicking a band highlights its card. <see cref="_suppressSelectionSync"/> breaks the
    /// re-entrancy, on the same pattern <see cref="_suppressBoundaryCommit"/> already uses here.</para>
    /// </summary>
    public StackupLayerRowViewModel? SelectedStackupLayerRow
    {
        get => _selectedStackupLayerRow;
        set
        {
            // A CONTROL'S WRITE-BACK IS NOT A SELECTION. Everything below <see cref="_suppressSelectionSync"/>
            // guards — rebuilding FilteredStackupLayers, and this property being re-pointed by
            // SyncStackupSelection — makes a bound ListBox null its own SelectedItem and push that
            // null back through the two-way binding. Taken at face value it would clear the selection
            // on every committed edit, every undo and every keystroke in the filter box: exactly the
            // evaporating selection holding the name rather than a reference exists to prevent.
            if (_suppressSelectionSync) return;

            if (!SetProperty(ref _selectedStackupLayerRow, value)) return;
            SelectedStackupLayerName = value?.Layer.Name;
        }
    }

    private StackupLayerRowViewModel? _selectedStackupLayerRow;
    private bool _suppressSelectionSync;

    partial void OnSelectedStackupLayerNameChanged(string? value)
    {
        // R-stk3-5 — A SELECTION THE FILTER WOULD HIDE CLEARS THE FILTER.
        //
        // ApplyStackupFilter builds FilteredStackupLayers from StackupFilter, so a click on a band
        // whose name the filter excludes would scroll to nothing at all, silently — which is the
        // outcome that makes the feature feel broken rather than limited. The entire purpose of the
        // click is to land on that entry's fields. Clear it; do not narrow it and do not warn about
        // it. Assigning "" re-runs ApplyStackupFilter through OnStackupFilterChanged.
        //
        // Here rather than in the code-behind, so it holds for brief 6's context-menu selections too.
        if (value is { Length: > 0 } && !Matches(value, StackupFilter.Trim()))
            StackupFilter = "";

        SyncStackupSelection();
    }

    /// <summary>
    /// Re-points <see cref="SelectedStackupLayerRow"/> and every row's
    /// <see cref="StackupLayerRowViewModel.IsSelected"/> at the row VMs that exist NOW.
    ///
    /// <para>Called from <see cref="OnSelectedStackupLayerNameChanged"/> and from
    /// <see cref="RebuildAll"/> — the second is what makes R-stk3-1 true, because a rebuild has just
    /// thrown away every row VM the selection was pointing at.</para>
    /// </summary>
    private void SyncStackupSelection()
    {
        var name = SelectedStackupLayerName;
        StackupLayerRowViewModel? match = null;

        foreach (var r in StackupLayers)
        {
            bool selected = match is null && name is not null &&
                            string.Equals(r.Layer.Name, name, System.StringComparison.Ordinal);
            r.IsSelected = selected;
            if (selected) match = r;
        }

        // The BACKING FIELD, under the guard — never the public setter, which exists for the
        // ListBox's half of the two-way link and would write the name straight back at us.
        bool prior = _suppressSelectionSync;
        _suppressSelectionSync = true;
        try
        {
            if (!ReferenceEquals(_selectedStackupLayerRow, match))
                SetProperty(ref _selectedStackupLayerRow, match, nameof(SelectedStackupLayerRow));
        }
        finally { _suppressSelectionSync = prior; }
    }

    /// <summary>R-stk3-9. <c>Esc</c> clears the outline, the card shading and the <c>ListBox</c>
    /// selection together — and deliberately does NOT put back a filter this selection cleared.
    /// Undoing that on <c>Esc</c> would make <c>Esc</c> a second undo, which it is not.</summary>
    public void ClearStackupSelection() => SelectedStackupLayerName = null;

    // ── Stackup ────────────────────────────────────────────────────────────────

    private void RebuildStackup()
    {
        _suppressBoundaryCommit = true;
        StackupTop    = Working.Stackup.Top;
        StackupBottom = Working.Stackup.Bottom;
        _suppressBoundaryCommit = false;

        // Null is EXPANDED — see Stackup.DrawingPaneExpanded. Suppressed for the same reason the two
        // above are: this is the model being projected onto the view model, not an edit.
        _suppressPaneCommit = true;
        StackupDrawingExpanded = Working.Stackup.DrawingPaneExpanded ?? true;
        StackupCardsExpanded   = Working.Stackup.CardPaneExpanded   ?? true;
        _suppressPaneCommit = false;

        StackupLayers.Clear();
        foreach (var sl in Working.Stackup.Layers)
            StackupLayers.Add(new StackupLayerRowViewModel(sl, this));

        RaiseStackHeightViews();
    }

    // ── GI3 R-gi3-6 / R-gi3-7 — does the stackup add up? ──────────────────────────────────────────
    //
    // The cheapest possible check on a hand-entered stackup, and it catches the common error directly:
    // a stack transcribed one row at a time and never added up does not match the board it came from,
    // and nothing in the application said so at any point before a run gave a wrong answer.
    //
    // Recomputed from Working on every rebuild, which is after every committed edit — so it follows a
    // thickness the moment that field commits, and follows an undo and a redo for free.

    /// <summary>The sum of the Conductor and Dielectric thicknesses, in the editor's display unit.
    /// <b>Vias are excluded</b> — a via has no z band of its own, which is why
    /// <c>PlanarExtractor.BuildStack</c> skips them and why the validator asks no thickness of one.</summary>
    public string StackTotalText =>
        LayoutUnits.Format(Working.Stackup.TotalThicknessDbu, Working.DefaultDisplayUnit,
                           LayoutUnits.DefaultDbuPerMicron) + " " +
        LayoutUnits.Suffix(Working.DefaultDisplayUnit);

    // ── What the stack is MADE OF, beside how tall it is ──────────────────────────────────────────
    //
    // Three counts on the same row as the height, because the height alone does not answer the
    // question a reader of a stackup actually opens it with — "is this the four-layer board I think
    // it is?". A 4/3/2 reads as a four-layer board with two via kinds at a glance; scrolling the
    // cards and counting them by eye is the version of this that gets it wrong.
    //
    // DELIBERATELY TERSE ("conductors: 4", not "Number of conductors: 4"): these sit beside the
    // height in a row that also has to hold the board-thickness comparison, and the readouts wrap
    // rather than truncate when the window narrows.

    /// <summary>How many Conductor entries the stackup holds.</summary>
    public string ConductorCountText  => "conductors: "  + CountOf(StackupKind.Conductor);

    /// <summary>How many Dielectric entries the stackup holds.</summary>
    public string DielectricCountText => "dielectrics: " + CountOf(StackupKind.Dielectric);

    /// <summary>How many Via entries the stackup holds. <b>Entries, not holes</b> — a via entry is a
    /// kind of connection between two named conductors, and one entry covers every via drawn on its
    /// drawing layer.</summary>
    public string ViaCountText        => "vias: "        + CountOf(StackupKind.Via);

    private int CountOf(StackupKind kind) => Working.Stackup.Layers.Count(l => l.Kind == kind);

    /// <summary>Whether another document stated an overall board thickness for this stackup to be
    /// compared against — a Gerber job file's <c>BoardThickness</c>, today. False for every
    /// hand-authored technology, which is most of them.</summary>
    public bool HasBoardThickness => Working.Stackup.BoardThicknessDbu is > 0;

    public string BoardThicknessText =>
        Working.Stackup.BoardThicknessDbu is { } b
            ? LayoutUnits.Format(b, Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron) + " " +
              LayoutUnits.Suffix(Working.DefaultDisplayUnit)
            : "";

    /// <summary>How far the two numbers may differ before the editor says so: <b>1 % of the stated
    /// board thickness, or 1 µm, whichever is larger</b>. The percentage is what makes it useful across
    /// a 100 µm die and a 1.6 mm board; the floor is what stops a rounding difference on a thin stack
    /// reading as a discrepancy. This is a TRANSCRIPTION check, not a fabrication-tolerance one — a
    /// real board's thickness tolerance is far wider, and a stackup within it still adds up.</summary>
    private long StackHeightTolerance(long boardDbu) =>
        Math.Max(LayoutUnits.ToDbu(1m, LayoutUnit.Um, LayoutUnits.DefaultDbuPerMicron),
                 (long)Math.Round(boardDbu * 0.01));

    public bool HasStackHeightMismatch =>
        Working.Stackup.BoardThicknessDbu is { } b && b > 0 &&
        Math.Abs(Working.Stackup.TotalThicknessDbu - b) > StackHeightTolerance(b);

    /// <summary><b>States the disagreement and corrects nothing.</b> Which of the two numbers is wrong
    /// is not something the application knows: the entered layers may be incomplete, or the stated
    /// board thickness may exclude plating and mask, or the file may simply be wrong. Saying so is the
    /// whole value; silently reconciling them would destroy it.</summary>
    public string StackHeightMismatchText
    {
        get
        {
            if (Working.Stackup.BoardThicknessDbu is not { } b || b <= 0) return "";
            long diff = Working.Stackup.TotalThicknessDbu - b;
            string sign = diff > 0 ? "thicker than" : "thinner than";
            string mag = LayoutUnits.Format(Math.Abs(diff), Working.DefaultDisplayUnit,
                                            LayoutUnits.DefaultDbuPerMicron) + " " +
                         LayoutUnits.Suffix(Working.DefaultDisplayUnit);
            return $"The layers entered here add up to {mag} {sign} the board thickness the imported " +
                   "job file states. Nothing has been corrected — which of the two is right is not " +
                   "something circuitRF can know.";
        }
    }

    private void RaiseStackHeightViews()
    {
        OnPropertyChanged(nameof(StackTotalText));
        OnPropertyChanged(nameof(ConductorCountText));
        OnPropertyChanged(nameof(DielectricCountText));
        OnPropertyChanged(nameof(ViaCountText));
        OnPropertyChanged(nameof(HasBoardThickness));
        OnPropertyChanged(nameof(BoardThicknessText));
        OnPropertyChanged(nameof(HasStackHeightMismatch));
        OnPropertyChanged(nameof(StackHeightMismatchText));
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
        Working.Stackup.Layers.Add(NewStackupLayer(kind));
        CommitEdit(before, $"Add {kind} stackup layer");
    }

    /// <summary>
    /// The entry the "＋ Conductor" / "＋ Dielectric" / "＋ Via" buttons add, unattached.
    ///
    /// <para>Factored out for R-stk6-4, which requires that a via added from the drawing's context
    /// menu and a via added from the button be <b>the same entry</b> but for its name and its span.
    /// That is a promise about <see cref="StackupLayer.Plated"/>, <see cref="StackupLayer.Fill"/>,
    /// <see cref="StackupLayer.WallThicknessDbu"/> and the starting thickness all at once, and the
    /// only version of it that cannot drift is one constructor both paths call — a second initializer
    /// listing the same fields agrees right up until one of them gains another.</para>
    /// </summary>
    private StackupLayer NewStackupLayer(StackupKind kind) => new()
    {
        Kind         = kind,
        Name         = NextFreeStackupName(kind),
        ThicknessDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Um, LayoutUnits.DefaultDbuPerMicron),
    };

    /// <summary>
    /// GI3 R-gi3-8's other half. The flat <c>$"New {kind}"</c> this used to write meant that clicking
    /// "＋ Conductor" twice produced two entries with one name — which is now a reported problem, and
    /// would have been one the editor inflicted on itself. Numbered exactly as
    /// <see cref="NextFreeDrcRuleName"/> already numbers a new rule.
    /// </summary>
    private string NextFreeStackupName(StackupKind kind)
    {
        var existing = new HashSet<string>(Working.Stackup.Layers.Select(l => l.Name), StringComparer.Ordinal);
        string bare = $"New {kind}";
        if (!existing.Contains(bare)) return bare;
        for (int i = 2; ; i++)
        {
            var candidate = $"{bare} {i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    // ── R-stk6-4 — Add Via, from the dielectric that was right-clicked ────────────────────────────

    /// <summary>
    /// The conductors immediately above and below <paramref name="dielectric"/> in the z order —
    /// the span a via added at that dielectric gets.
    ///
    /// <para><b>The walk skips dielectrics AND via entries.</b> A run of two dielectrics with no
    /// metal between them is ordinary (prepreg on core), so "the layer above" is not "the previous
    /// list entry"; and a via entry has no z band of its own and sits outside the order entirely
    /// (R-stk5-1), so a via lying between two bands in list order must not stop either walk.</para>
    ///
    /// <para>Either end may be null — a dielectric above the top metal has nothing to span up to, a
    /// solder mask below the bottom metal nothing to span down to — which is what
    /// <see cref="AddViaAroundRefusal"/> turns into a disabled menu item rather than a via with one
    /// end unset.</para>
    /// </summary>
    internal (string? Above, string? Below) ConductorsAround(StackupLayer dielectric)
    {
        ArgumentNullException.ThrowIfNull(dielectric);
        var layers = Working.Stackup.Layers;
        int at = layers.IndexOf(dielectric);
        if (at < 0) return (null, null);

        string? Walk(int step)
        {
            for (int i = at + step; i >= 0 && i < layers.Count; i += step)
                if (layers[i].Kind == StackupKind.Conductor) return layers[i].Name;
            return null;
        }

        return (Walk(-1), Walk(+1));
    }

    /// <summary>
    /// Why "Add Via" is not offered on <paramref name="row"/>, or null when it is.
    ///
    /// <para><b>Disabled with a reason, never enabled then refused.</b> The reason names the SIDE
    /// that has no metal, because that is the half the user can act on — moving the dielectric, or
    /// adding the conductor it is missing.</para>
    /// </summary>
    internal string? AddViaAroundRefusal(StackupLayerRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsDielectric) return "Add Via places a via across a dielectric; this entry is not one.";

        var (above, below) = ConductorsAround(row.Layer);
        return (above, below) switch
        {
            (null, null) => $"There is no conductor above or below \"{row.Layer.Name}\" for a via to span.",
            (null, _)    => $"There is no conductor above \"{row.Layer.Name}\" for a via to span up to.",
            (_, null)    => $"There is no conductor below \"{row.Layer.Name}\" for a via to span down to.",
            _            => null,
        };
    }

    /// <summary>
    /// R-stk6-4. Adds a via spanning the two conductors <paramref name="dielectricRow"/> lies
    /// between, and SELECTS it — which scrolls the card list to its card, where the drawing layer
    /// and the wall thickness the new entry still needs are.
    ///
    /// <para>One <see cref="CommitEdit"/>, so one Ctrl-Z removes it. No-ops when
    /// <see cref="AddViaAroundRefusal"/> has something to say; the menu item is disabled in that case
    /// and this is the belt to its braces.</para>
    /// </summary>
    internal void AddViaSpanningAround(StackupLayerRowViewModel dielectricRow)
    {
        ArgumentNullException.ThrowIfNull(dielectricRow);
        if (AddViaAroundRefusal(dielectricRow) is not null) return;

        var (above, below) = ConductorsAround(dielectricRow.Layer);

        var before = SnapshotJson();
        var via = NewStackupLayer(StackupKind.Via);
        via.SpanFromLayer = above;
        via.SpanToLayer   = below;
        Working.Stackup.Layers.Add(via);
        CommitEdit(before, $"Add via spanning {above} to {below}");

        // AFTER the commit, and by NAME: the commit rebuilt every row VM, so `via` itself is already
        // a dead reference by the time this line runs (R-stk3-1).
        SelectedStackupLayerName = via.Name;
    }

    /// <summary>
    /// Removes one stackup entry. <b>The card's ✕, the drawing's context menu and the
    /// <c>Delete</c> keystroke all land here</b> — one snapshot, one description, one undo entry,
    /// and no second deletion path to drift from this one.
    /// </summary>
    internal void RemoveStackupLayer(StackupLayerRowViewModel row)
    {
        var before = SnapshotJson();
        bool wasSelected = string.Equals(SelectedStackupLayerName, row.Layer.Name,
                                         System.StringComparison.Ordinal);
        Working.Stackup.Layers.Remove(row.Layer);
        CommitEdit(before, $"Remove stackup layer {row.Layer.Name}");

        // The selection is held BY NAME (R-stk3-1) and that name has just stopped naming anything:
        // left standing it outlines nothing on the drawing and shades no card, so the editor would
        // claim to have something selected while showing no sign of it — and a second Delete would
        // do nothing with no visible reason. Cleared AFTER the commit, so the undo entry above
        // restores the entry rather than the selection that pointed at it.
        if (wasSelected) ClearStackupSelection();
    }

    /// <summary>
    /// Deletes whatever the stackup selection names, and says whether there was anything to delete —
    /// which is what tells the keystroke's handler whether to mark the key handled, rather than
    /// swallowing a <c>Delete</c> that did nothing.
    ///
    /// <para>Resolved BY NAME against the rows that exist NOW, for R-stk3-1's reason: every row VM
    /// is destroyed on every committed edit, so a name is the only handle that survives one. Against
    /// <see cref="StackupLayers"/> and not the filtered view, because a selection the filter would
    /// hide has already cleared the filter (<see cref="OnSelectedStackupLayerNameChanged"/>) and
    /// because what is selected is what gets deleted either way.</para>
    ///
    /// <para><b>No confirmation, exactly as the context menu's Delete has none</b>: undo is the
    /// confirmation and it is already there. A conductor a via's span names is deleted the same way
    /// too — the via is left alone and <c>TechValidation</c> reports it, per R-stk6-3.</para>
    /// </summary>
    internal bool DeleteSelectedStackupLayer()
    {
        if (SelectedStackupLayerName is not { Length: > 0 } name) return false;

        var row = StackupLayers.FirstOrDefault(
            r => string.Equals(r.Layer.Name, name, System.StringComparison.Ordinal));
        if (row is null) return false;

        RemoveStackupLayer(row);
        return true;
    }

    /// <summary>
    /// Moves a Conductor or Dielectric entry one place within the z order.
    ///
    /// <para>GI3 R-gi3-10: <b>a Via row is refused outright</b> (its list position means nothing, so
    /// the swap changed the reading order and no physical fact), and a substrate row swaps with the
    /// next SUBSTRATE entry, stepping over any via entries lying between them in the list. Both follow
    /// from the same premise as R-gi3-9's grouping: the z order is the order of the non-via entries,
    /// and a via sits outside it. Swapping across an intervening via leaves that via's own index
    /// untouched, which is correct precisely because its index carries no meaning.</para>
    /// </summary>
    internal void MoveStackupLayer(StackupLayerRowViewModel row, int direction)
    {
        if (!row.CanMove) return;
        int band = BandIndexOf(row.Layer);
        if (band < 0) return;

        MoveStackupLayerTo(
            row, band + Math.Sign(direction),
            direction < 0 ? $"Move {row.Layer.Name} up" : $"Move {row.Layer.Name} down");
    }

    /// <summary>
    /// R-stk5-2. Moves a Conductor or Dielectric entry to <paramref name="targetBandIndex"/> in the
    /// z order — <b>the whole move, as ONE undo entry</b>.
    ///
    /// <para>This is what <see cref="MoveStackupLayer"/> is now written in terms of, and the reason
    /// it exists is brief 5's drag: dragging the top copper of a nine-entry stack to the bottom
    /// crosses four bands, and four single-step calls would push four undo entries so that one
    /// Ctrl-Z put it back one place. That reads as a broken undo. One snapshot, one description,
    /// one entry.</para>
    ///
    /// <para><b>The index counts BANDS, not list slots</b> (<see cref="BandIndexOf"/>), which is what
    /// makes it obey R-stk5-1's two rules by construction rather than by repeating them: a via row is
    /// refused outright because its list position is not z and never was, and the band VALUES rotate
    /// through the band slots while every via entry stays exactly where it is in the list. Rotating
    /// the values rather than moving the entry is precisely what a run of adjacent
    /// <see cref="MoveStackupLayer"/> swaps does, which is why the two agree for every single-step
    /// case and why a multi-step drag lands where the equivalent run of button clicks would.</para>
    /// </summary>
    internal void MoveStackupLayerTo(
        StackupLayerRowViewModel row, int targetBandIndex, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanMove) return;

        var layers = Working.Stackup.Layers;
        var slots  = BandSlots();
        int from   = slots.IndexOf(layers.IndexOf(row.Layer));
        if (from < 0) return;

        int to = Math.Clamp(targetBandIndex, 0, slots.Count - 1);
        if (to == from) return;

        var before = SnapshotJson();

        var bands = new List<StackupLayer>(slots.Count);
        foreach (int slot in slots) bands.Add(layers[slot]);
        bands.RemoveAt(from);
        bands.Insert(to, row.Layer);
        for (int i = 0; i < slots.Count; i++) layers[slots[i]] = bands[i];

        CommitEdit(before, description ?? $"Move {row.Layer.Name} to position {to + 1}");
    }

    /// <summary>The raw list indices of the non-via entries, in order. The z order IS this sequence;
    /// a via sits outside it (R-stk5-1).</summary>
    private List<int> BandSlots()
    {
        var layers = Working.Stackup.Layers;
        var slots  = new List<int>(layers.Count);
        for (int i = 0; i < layers.Count; i++)
            if (layers[i].Kind != StackupKind.Via) slots.Add(i);
        return slots;
    }

    /// <summary>Where <paramref name="layer"/> sits in the z order, counting bands only, or -1 for a
    /// via and for an entry that is not in this stackup.</summary>
    internal int BandIndexOf(StackupLayer layer)
    {
        if (layer is null || layer.Kind == StackupKind.Via) return -1;
        int band = 0;
        foreach (var l in Working.Stackup.Layers)
        {
            if (ReferenceEquals(l, layer)) return band;
            if (l.Kind != StackupKind.Via) band++;
        }
        return -1;
    }

    /// <summary>
    /// R-stk5-10's write, and the one edit in this editor that changes nothing about the technology
    /// except the picture of it. Null clears the lane back to the drawing's own spread.
    ///
    /// <para>It goes through <see cref="CommitEdit"/> like every other edit here — a cosmetic value
    /// is still a value in the <c>.ctech</c>, so it dirties the editor, it undoes, and it saves. What
    /// it must never do is reach anything downstream of the drawing, which is R-stk5-9's gate rather
    /// than this comment.</para>
    /// </summary>
    internal void SetViaDrawLane(StackupLayerRowViewModel row, double? fraction)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsVia) return;

        double? v = fraction is { } f && !double.IsNaN(f) ? Math.Clamp(f, 0d, 1d) : null;
        if (Nullable.Equals(row.Layer.DrawLaneFraction, v)) return;

        var before = SnapshotJson();
        row.Layer.DrawLaneFraction = v;
        CommitEdit(before, $"Move {row.Layer.Name} laterally");
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
