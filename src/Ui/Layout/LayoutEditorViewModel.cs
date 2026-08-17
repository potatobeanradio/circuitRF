using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>One row of the current-layer picker: the layer's <see cref="LayerKey"/>, display name,
/// and swatch color (mirrors whichever of <see cref="Technology.Layers"/> or
/// <see cref="FallbackPalette"/> resolved it). A top-level type (not nested in the view model) so
/// XAML can reference it directly as a DataTemplate's <c>x:DataType</c>.</summary>
public sealed record LayerPickerItem(LayerKey Key, string Name, Rgba Color)
{
    /// <summary>Avalonia <c>Color</c> for the swatch <c>Border.Background</c> binding — never bind
    /// the raw <see cref="Rgba"/> directly (it has no implicit Brush conversion).</summary>
    public Avalonia.Media.Color SwatchColor => Avalonia.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B);
}

/// <summary>
/// ViewModel for the Layout Editor. Owns the document's own <see cref="UndoRedoStack"/> — every
/// drawn shape is a fine-grained <see cref="IUiCommand"/> (<see cref="AddShapeCommand"/>), NOT a
/// whole-model snapshot. This is a deliberate departure from the <c>.ctech</c> editor
/// (<c>TechEditorViewModel</c>), which snapshots a whole <c>Technology</c> per edit because a
/// technology is tens of layers and a handful of stackup entries — cheap to clone. A layout can hold
/// 10³–10⁶ shapes (docs/design/layout-view.md §5.1), so cloning the whole model per edit is exactly
/// what that budget forbids; only <see cref="AddShapeCommand"/> exists in L1b, but the plumbing
/// (<see cref="Execute"/>, per-document stack, restore-at-original-index) is built for the dozen
/// more commands L1c/L1d add on top.
/// </summary>
public sealed partial class LayoutEditorViewModel : ObservableObject
{
    private readonly IMessageSink? _messageSink;

    /// <summary>The L0a container this document edits.</summary>
    public LayoutView Model { get; }

    // ── Undo (L1b) ───────────────────────────────────────────────────────────

    private readonly UndoRedoStack _undoRedo = new();
    public UndoRedoStack UndoRedo => _undoRedo;

    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }

    /// <summary>Executes a mutation and pushes it onto this document's own undo stack. One user
    /// gesture is one call to Execute — however many vertices/points it took to build the shape.
    /// R-pc-13: refused (reported, never a silent no-op — R13a) while <see cref="IsPCellReadOnly"/>;
    /// <see cref="DetachFromPCell"/> is the escape hatch.</summary>
    public void Execute(IUiCommand cmd)
    {
        if (IsPCellReadOnly)
        {
            _messageSink?.Warning(PCellReadOnlyReason);
            return;
        }
        _undoRedo.Execute(cmd);
    }

    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// True after a DisplayUnit/SnapDbu edit that has not yet been saved. Kept separate from
    /// <see cref="UndoRedoStack.IsModified"/> because those two preferences deliberately carry NO
    /// undo entry (§1.3/§1.5) — <see cref="IsDirty"/> is the OR of this flag and the undo stack's
    /// modified state, so either kind of unsaved change dirties the document, and
    /// <see cref="MarkSaved"/> clears both together.
    /// </summary>
    private bool _prefsDirty;

    private void RefreshDirty() => IsDirty = _prefsDirty || _wireDirty || _undoRedo.IsModified;

    /// <summary>Records the current state (undo position + preference edits) as the clean,
    /// just-saved baseline. Call after the document has been written to disk.</summary>
    public void MarkSaved()
    {
        _prefsDirty = false;
        _undoRedo.MarkSaved();
        // WB40: a wirebond cell's wires live in a `.wBond` beside its `.clay` and travel with it.
        // Here rather than in PerformSave because the workspace writes sub-cell sessions with a bare
        // LayoutPersistence.SaveToFile — this method is the one call every save path shares.
        SaveWireDesignIfDirty();
        RefreshDirty();
    }

    /// <summary>
    /// Absolute on-disk path of the .clay file, or null for a not-yet-saved (scratch) document.
    /// Mirrors <c>SymbolEditorViewModel.CurrentSymbolPath</c> — the document reflects this.
    /// </summary>
    [ObservableProperty] private string? _currentLayoutPath;

    [ObservableProperty] private LayoutUnit _displayUnit;
    [ObservableProperty] private long _snapDbu;

    /// <summary>brief-L5-followups-2.md §6 (R-L5g-15): view toggle for the PCell pin overlay —
    /// default ON. Session-local UI preference, not model state: never persisted in <c>.clay</c>,
    /// never on any undo stack (the same "view-preference edit, not a geometry mutation" rule
    /// <see cref="DisplayUnit"/>/<see cref="SnapDbu"/> already follow), never touches <see cref="IsDirty"/>.</summary>
    [ObservableProperty] private bool _showPCellPins = true;

    /// <summary>brief-L6-L7-em-ui.md R-em-15: view toggle for the EM mesh overlay — same
    /// session-local, never-persisted, never-undoable contract as <see cref="ShowPCellPins"/>, and
    /// the same "the toggle default lives at the VM layer, not in LayoutRenderOptions" rule. Default
    /// ON so pressing Mesh shows the mesh without a second gesture; the overlay still draws nothing
    /// until <see cref="EmMeshReport"/> is non-null.</summary>
    [ObservableProperty] private bool _showEmMesh = true;

    /// <summary>The mesh to draw, pushed here by the <c>.cem</c> editor that produced it. R-em-17:
    /// this is CLEARED when the layout is edited — the overlay survives an edit by being
    /// invalidated, never by going stale.</summary>
    [ObservableProperty] private Engine.Mom.EmMeshReport? _emMeshReport;

    /// <summary>brief-L8b D5: view toggle for the PLAN-VIEW surface-mesh overlay. Same contract as
    /// <see cref="ShowEmMesh"/> in every respect. The two are independent and both may be on — which
    /// overlay a document actually shows follows from which mesh was computed, not from a mode, so
    /// there is no third "which kernel am I looking at" toggle to keep in step.</summary>
    [ObservableProperty] private bool _showPlanarMesh = true;

    /// <summary>The surface mesh to draw, pushed here by the <c>.cem</c> editor that produced it.
    /// R-em-17 applies MORE strongly here than to the cross-section inset: a plan-view mesh drawn
    /// over EDITED artwork is worse than no mesh, because it looks like it still matches.</summary>
    [ObservableProperty] private Engine.Mom.PlanarMeshReport? _planarMeshReport;

    /// <summary>L8e/D5 — the per-cell current-density map to shade the surface mesh with, pushed
    /// here by the <c>.cem</c> editor whose run produced it. Null takes the plain cell-boundary
    /// path, which is exactly what L8b's own provision was shaped for. R-em-17 applies and matters
    /// MORE than for the mesh itself: a current map over edited artwork looks like it still
    /// matches.</summary>
    [ObservableProperty] private Engine.Mom.PlanarCurrentDensityMap? _planarCurrentDensity;

    /// <summary>§10.6's "show the de-embedding reference plane in the layout". <b>A drawing job over
    /// a location the ENGINE reports</b> (<c>PlanarPortResolution.ReferencePlaneM</c>) — L8d's D2
    /// fixes the plane one cell in from the drawn metal end and deliberately offers no offset, so
    /// there is nothing here to compute and nothing for a user to move.</summary>
    [ObservableProperty] private IReadOnlyList<Engine.Mom.PlanarPortResolution> _planarReferencePlanes = [];

    // ── Technology (L0c) ───────────────────────────────────────────────────────

    /// <summary>The resolved technology, or null when unresolved (missing/corrupt/no default) —
    /// the layout still opens and edits either way (§2.4 "never block on it").</summary>
    [ObservableProperty] private Technology? _technology;

    /// <summary>Absolute path of the .ctech <see cref="Technology"/> was resolved from, or null.
    /// Lets the workspace know which open documents to refresh when that file changes.</summary>
    internal string? ResolvedTechPath { get; private set; }

    public string TechNameText => Technology?.Name ?? "No technology";

    public string LayerCountText => Technology is null ? "fallback colors" : $"{Technology.Layers.Count} layers";

    /// <summary>
    /// Metadata-bar readout: just the technology's own name, e.g. "PCB 2-Layer RO4350B (20mil, 1oz)".
    ///
    /// <para><b>The layer COUNT was deliberately removed</b> (owner, 2026-07-30). It read
    /// "PCB 2-Layer … · 8 layers", which is self-contradictory to anyone in the industry: "2-layer"
    /// is the board's physical METAL count (top and bottom copper), while 8 is the number of drawing
    /// layers in the .ctech — a different thing wearing the same word. The count is not load-bearing
    /// information at a glance, and Edit shows it precisely. <see cref="LayerCountText"/> is kept for
    /// any caller that genuinely wants the count.</para>
    ///
    /// <para>The no-technology case still says what it falls back to, because THAT is worth knowing:
    /// geometry is drawn with generated placeholder colours, not the process's own.</para>
    /// </summary>
    public string TechSummaryText =>
        Technology is null ? $"{TechNameText} · {LayerCountText}" : TechNameText;

    partial void OnTechnologyChanged(Technology? value)
    {
        OnPropertyChanged(nameof(TechNameText));
        OnPropertyChanged(nameof(LayerCountText));
        OnPropertyChanged(nameof(TechSummaryText));
        OnPropertyChanged(nameof(ViaToolAvailability));
        OnPropertyChanged(nameof(ViaToolTipText));
        RebuildAvailableLayers();
        RebuildSnapLadderOptions();
    }

    /// <summary>Applies a resolution from <see cref="TechnologyResolver"/> — called by the workspace
    /// after New Layout, after opening a .clay, and whenever the live-refresh seam fires. Does NOT
    /// touch DisplayUnit/SnapDbu: those are the document's own state once open, and silently
    /// re-seeding them from a changed technology would discard a user's choice.</summary>
    internal void ApplyTechResolution(TechResolution resolution)
    {
        ResolvedTechPath = resolution.ResolvedPath;
        Technology        = resolution.Tech;

        // R-lbl-1 (docs/sonnet-briefs/brief-layout-label-fix-and-text-flatten.md): seed the label-
        // height default from the technology exactly once — the first time a technology resolves for
        // this document (this method always runs synchronously right after construction at every
        // `new LayoutEditorViewModel(...)` call site, so "once" here is equivalent to "at construction,
        // like DisplayUnit/SnapDbu"). A LATER technology change (retarget, live .ctech edit) must NOT
        // re-seed — that would silently discard whatever the user has since typed into the Label
        // toolbar field, the same reason DisplayUnit/SnapDbu are never touched here at all.
        if (!_labelHeightSeededFromTech)
        {
            _labelHeightSeededFromTech = true;
            if (resolution.Tech is { DefaultLabelHeightDbu: > 0 } tech)
            {
                _labelHeightDbu = tech.DefaultLabelHeightDbu;
                LabelHeightText = LayoutUnits.Format(_labelHeightDbu, DisplayUnit, Model.DbuPerMicron);
            }
        }
    }

    // ── Metadata bar (read-only, derived) ─────────────────────────────────────

    /// <summary>Database resolution, e.g. "1 DBU = 1 nm". NOT shown in the metadata bar any more
    /// (owner, 2026-07-30) — it is fixed per document and set at creation, so it earned no permanent
    /// pixels. Kept for any surface that genuinely needs to state the resolution.</summary>
    public string ResolutionText => $"1 DBU = {LayoutUnits.Format(1, LayoutUnit.Nm, Model.DbuPerMicron)} nm";

    public string SnapText => $"{LayoutUnits.Format(SnapDbu, DisplayUnit, Model.DbuPerMicron)} {UnitSuffix(DisplayUnit)}";

    public string ShapeCountText => Model.Shapes.Count.ToString();

    public string InstanceCountText => Model.Instances.Count.ToString();

    /// <summary>Bbox of all shapes, unioned, formatted in the current display unit. "—" when empty.</summary>
    public string ExtentText
    {
        get
        {
            var bb = Bbox.Empty;
            foreach (var shape in Model.Shapes)
                bb = bb.Union(LayoutGeometry.BboxOf(shape));
            if (bb.IsEmpty) return "—";

            var w = LayoutUnits.Format(bb.MaxX - bb.MinX, DisplayUnit, Model.DbuPerMicron);
            var h = LayoutUnits.Format(bb.MaxY - bb.MinY, DisplayUnit, Model.DbuPerMicron);
            return $"{w} × {h} {UnitSuffix(DisplayUnit)}";
        }
    }

    /// <summary>ComboBox item source for the display-unit picker.</summary>
    public static IReadOnlyList<LayoutUnit> AllUnits { get; } = Enum.GetValues<LayoutUnit>();

    private static string UnitSuffix(LayoutUnit unit) => LayoutUnits.Suffix(unit);

    // ── Display unit / snap grid — document preferences, not geometry (§1.3/§1.5) ────
    // They dirty the document (persisted in .clay) but never touch an undo stack: a unit
    // change "needs no undo entry beyond a view-preference change", and a snap change never
    // touches existing geometry.

    partial void OnDisplayUnitChanged(LayoutUnit value)
    {
        Model.DisplayUnit = value;
        _prefsDirty = true;
        RefreshDirty();
        OnPropertyChanged(nameof(SnapText));
        OnPropertyChanged(nameof(ExtentText));
        RefreshTypedFieldDisplays();
        RefreshSnapDistanceDisplay();
        RebuildSnapLadderOptions();
        RefreshCursorReadout();   // the X:/Y: readout is unit-formatted too — relabel it in place
    }

    partial void OnSnapDbuChanged(long value)
    {
        Model.SnapDbu = value;
        _prefsDirty = true;
        RefreshDirty();
        OnPropertyChanged(nameof(SnapText));
        RefreshSnapDistanceDisplay();
        // docs/sonnet-briefs/brief-snap-ladder-crash.md R-crash-1: deliberately NEVER
        // RebuildSnapLadderOptions() here. Selecting a ladder entry sets SnapDbu (via
        // CommitSnapLadderSelection -> CommitSnapDistanceText), which means THIS method runs from
        // inside Avalonia's own SelectionChanged notification — mutating the ObservableCollection the
        // active SelectionModel is still reading crashes with ArgumentOutOfRangeException, reliably,
        // on every single selection. "Never blank" is satisfied by RefreshSnapDistanceDisplay() alone
        // (the combobox is editable and bound via Text, which can show any value regardless of ladder
        // membership) — the ladder itself is a pure function of Technology/DisplayUnit and must stay
        // one; see SnapLadderOptions's own doc comment for the general rule this is an instance of.
    }

    // ── Construction ───────────────────────────────────────────────────────────

    public LayoutEditorViewModel(LayoutView model, string? currentLayoutPath = null, IMessageSink? messageSink = null)
    {
        Model = model;
        _messageSink = messageSink;

        // Seed backing fields directly — bypassing the property setters so construction
        // never marks the document dirty or double-writes the model it was built from.
        _displayUnit       = model.DisplayUnit;
        _snapDbu           = model.SnapDbu;
        _currentLayoutPath = currentLayoutPath;

        SaveLayoutCommand   = new AsyncRelayCommand<Window?>(SaveLayoutAsync);
        SaveLayoutAsCommand = new AsyncRelayCommand<Window?>(SaveLayoutAsAsync);

        UndoCommand = new RelayCommand(() => _undoRedo.Undo(), () => _undoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => _undoRedo.Redo(), () => _undoRedo.CanRedo);
        _undoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.IsModified)) RefreshDirty();
        };

        SetActiveToolCommand = new RelayCommand<string>(name =>
        {
            if (name is not null && Enum.TryParse<Tool>(name, out var t)) ActiveTool = t;
        });

        // Bug (owner-reported, post-L5): this used to call SetSelection(shapes only), so instances —
        // including PCells — were silently excluded from Select All despite SetSelection's own doc
        // comment listing "SelectAll" as a REPLACE caller that sets the WHOLE new selection. Select
        // All must select everything: every shape (bitmaps included — BitmapShape is a LayoutShape,
        // already covered) AND every instance (ordinary AND PCell — an instance is an instance, R-L5-1's
        // whole point being that a PCell instance is not a special case anywhere else either).
        SelectAllCommand = new RelayCommand(() =>
        {
            ReplaceMixedSelection(Enumerable.Range(0, Model.Shapes.Count), Enumerable.Range(0, Model.Instances.Count));
            _cycleCache.Clear();
        });
        DeselectAllCommand = new RelayCommand(() =>
        {
            SetSelection([]);
            _cycleCache.Clear();
        });

        InitBooleanCommands();   // L1e — src/Ui/Layout/LayoutEditorViewModel.Booleans.cs
        InitClipboardCommands(); // L1f — src/Ui/Layout/LayoutEditorViewModel.Clipboard.cs
        InitScaleCommands();     // L1h — src/Ui/Layout/LayoutEditorViewModel.Scale.cs

        _pathWidthText     = LayoutUnits.Format(_pathWidthDbu, DisplayUnit, Model.DbuPerMicron);
        _cornerRadiusText  = LayoutUnits.Format(_cornerRadiusDbu, DisplayUnit, Model.DbuPerMicron);
        _labelHeightText   = LayoutUnits.Format(_labelHeightDbu, DisplayUnit, Model.DbuPerMicron);

        // brief-snap-combobox-and-consistency.md R-cmb-1/2: _snapDbu/_displayUnit were just seeded onto
        // their backing fields directly, above — which bypasses OnSnapDbuChanged/OnDisplayUnitChanged
        // entirely, so neither SnapDistanceText nor SnapLadderOptions would otherwise be populated
        // until something LATER changes one of those properties (typically the workspace's own
        // ApplyTechResolution call, right after construction — but a document with no technology, or
        // one opened before that call runs, would show a genuinely blank combobox in the meantime).
        // Seeding both explicitly here closes that gap regardless of when/whether a technology ever
        // resolves.
        _snapDistanceText = SnapText;
        RebuildSnapLadderOptions();

        RebuildAvailableLayers();

        // Every AddShapeCommand (and future L1c/L1d mutations) fires Model.Changed; the metadata-bar
        // readouts and the empty-layout placeholder are all computed from Model.Shapes/Instances, so
        // they need an explicit INPC nudge here — nothing else raises it for them. Without this the
        // placeholder Border (drawn on top of LayoutCanvas) never hides after the first shape is
        // drawn, even though the shape really is in the model and really is being rendered underneath.
        Model.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShapeCountText));
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(ExtentText));

            // R-em-17: the mesh overlay survives the layout being edited underneath it by being
            // INVALIDATED, not by being stale. An edited .clay clears the displayed mesh; it does
            // not keep drawing the old one, which would be a picture of a cross-section that is no
            // longer there.
            EmMeshReport     = null;
            PlanarMeshReport = null;
            PlanarCurrentDensity  = null;
            PlanarReferencePlanes = [];

            // Any model mutation (draw, move, delete, undo, redo — every one of them calls
            // NotifyChanged) invalidates the overlap-cycling cache (R-L1c-2). Selected indices may
            // also have shifted or been removed by the mutation, so drop any that are now stale.
            // The picked-vertex index (L1d) is unconditionally cleared too — a ReplaceShapeCommand
            // can change the shape's vertex count/order at the very index that's still selected.
            _cycleCache.Clear();
            _pickedVertexIndex = null;

            // brief-snap-distance-and-geometry-snap.md R-snp-12's second invalidation hook: the
            // ACTIVELY EDITED top-level document's own shape edits never route through
            // WorkspaceViewModel.OnCellLayoutLiveViewChanged (that seam only fires for a cell reached
            // through an instance) — this Model.Changed subscription is the seam for THIS document's
            // own intrinsic feature cache. Always invalidates (rather than checking info.Kind) since a
            // vertex/handle edit keeps the shape COUNT unchanged but moves its features.
            LayoutSnapFeatureIndex.Invalidate(Model);

            // L5b: a DRC result describes geometry that has now changed. Markers drawn over moved
            // artwork, and a violation count that no longer matches it, are both worse than showing
            // nothing — so the result is dropped rather than refreshed (R16b: checking is on demand).
            ClearDrcResultOnEdit();

            bool shapesChanged = _selectedIndices.RemoveAll(i => i < 0 || i >= Model.Shapes.Count) > 0;
            bool instancesChanged = _selectedInstanceIndices.RemoveAll(i => i < 0 || i >= Model.Instances.Count) > 0;
            if (shapesChanged || instancesChanged) SelectionStatusText = ComputeGenericSelectionStatus();

            // Unconditionally, not only when the selection LIST changed. A PCell parameter committed
            // from the Properties Inspector re-points the selected instance at a different generated
            // cell (R-L5-2's copy-on-write) at the SAME index — the index list is untouched, so the
            // old guard skipped the rebuild and the grips went on being drawn for the cell the
            // instance no longer references. Every other overlay this method feeds (handles, scale
            // handles, DRC markers) is derived from the model in the same way and has the same
            // exposure; a rebuild is cheap (the grips resolve through PCellGeometryCache) and being
            // stale is not.
            RebuildOverlay();
        };
    }

    // ── Canvas viewport readouts (L1a) ─────────────────────────────────────────

    /// <summary>True when the layout has no geometry at all — the view keeps L0b's centered
    /// placeholder text in this state instead of showing an empty canvas/grid.</summary>
    public bool IsEmpty => Model.Shapes.Count == 0 && Model.Instances.Count == 0;

    [ObservableProperty] private string _cursorXText = "—";
    [ObservableProperty] private string _cursorYText = "—";

    /// <summary>The raw pointer position the view last reported, kept so the readout can be recomputed
    /// without a pointer move — after the display unit changes, and (the reason it exists) after the
    /// geometry-snap query for THIS tick has resolved.</summary>
    private double? _cursorRawWorldX;
    private double? _cursorRawWorldY;

    /// <summary>Called by the view on every pointer-move/exit over the canvas — §1 R6's "live
    /// physical readout." Null clears the readout (pointer left the canvas).</summary>
    public void SetCursorWorld(double? worldX, double? worldY)
    {
        _cursorRawWorldX = worldX;
        _cursorRawWorldY = worldY;
        RefreshCursorReadout();
    }

    /// <summary>Owner request: the X:/Y: readout traced the raw mouse position even with geometry snap
    /// on, so it disagreed with where a click would actually land. With snap on and a REAL candidate
    /// in range it now reports the snapped point instead.
    /// <para/>
    /// Gated on <c>_snapCandidateIsRealTarget</c>, not merely on a non-null candidate: during a
    /// marker-initiated drag <c>UpdateSnapMarker</c> keeps a SYNTHETIC candidate alive that just
    /// tracks the cursor, and reading that would dress the raw position up as a snapped one — the
    /// exact claim this is meant to stop making. That flag is also what the committed-position path
    /// (<c>RecomputeMoveDelta</c>) gates on, so the readout and the drag agree by construction.
    /// <para/>
    /// Grid snap (<see cref="SnapDbu"/>) is deliberately NOT folded in here. It applies to a
    /// gesture's committed point, not to hovering, and a readout that quantised on hover would report
    /// a coordinate no feature is at.</summary>
    private void RefreshCursorReadout()
    {
        if (_cursorRawWorldX is not { } rawX || _cursorRawWorldY is not { } rawY)
        {
            CursorXText = "—";
            CursorYText = "—";
            return;
        }

        long x = (long)Math.Round(rawX);
        long y = (long)Math.Round(rawY);
        if (GeometrySnapEnabled && _snapCandidateIsRealTarget && _currentSnapCandidate is { } snap)
        {
            x = snap.X;
            y = snap.Y;
        }

        CursorXText = LayoutUnits.Format(x, DisplayUnit, Model.DbuPerMicron) + " " + UnitSuffix(DisplayUnit);
        CursorYText = LayoutUnits.Format(y, DisplayUnit, Model.DbuPerMicron) + " " + UnitSuffix(DisplayUnit);
    }

    /// <summary>Test/diagnostic-only — true when the readout is currently reporting a snapped feature
    /// rather than the raw pointer position. A test asserting only on the formatted text cannot tell a
    /// snapped value from a raw one that happens to round the same way.</summary>
    internal bool CursorReadoutIsSnapped =>
        _cursorRawWorldX is not null && GeometrySnapEnabled && _snapCandidateIsRealTarget && _currentSnapCandidate is not null;

    /// <summary>Posts a Messages summary (docs/sonnet-briefs/brief-L1g-technology-retarget.md §5 —
    /// "report what happened"). Used after a technology retarget and after a cross-tech paste, both
    /// of which are bulk changes to the user's geometry that deserve a readable record once the
    /// dialog is gone. <paramref name="filePath"/> (optional) renders as a clickable "reveal in file
    /// manager" link (<see cref="MessageEntry.FilePath"/>) — e.g. the file/folder an export just wrote to.</summary>
    public void ReportMessage(string text, string? filePath = null) => _messageSink?.Success(text, filePath);

    /// <summary>Posts a Messages error — used when a user-chosen <c>.ctech</c> (Change Technology's
    /// "each .ctech in tech/" or "Browse…" options) fails to load.</summary>
    public void ReportError(string text) => _messageSink?.Error(text);

    /// <summary>Posts a Messages warning — e.g. the L1j Properties Inspector's "corner radius was
    /// clamped to fit the new size" notice.</summary>
    public void ReportWarning(string text) => _messageSink?.Warning(text);

    // ── Effective (drag-override-aware) geometry — L1j, docs/sonnet-briefs/brief-L1j-properties-
    // inspector.md R-L1j-1 ──────────────────────────────────────────────────────────────────────
    // A drag (move/handle/scale) deliberately never mutates Model.Shapes — one gesture is one undo
    // entry, pushed at release; the pending geometry lives only in Overlay.DragOverrides. Any UI that
    // wants to show what the user is CURRENTLY seeing (not what is merely committed) must read
    // through these, not Model.Shapes directly — this is the single source the Properties Inspector
    // reads so it updates live during a drag without a second code path.

    /// <summary>The shape currently rendered at <paramref name="index"/> — the live drag-preview clone
    /// when one exists for this index, otherwise the committed shape. Never mutate the result when it
    /// came from a preview: it is a throwaway clone, about to be discarded or superseded next frame.</summary>
    public LayoutShape EffectiveShapeAt(int index) =>
        Overlay.DragOverrides.TryGetValue(index, out var preview) ? preview : Model.Shapes[index];

    /// <summary>Every currently selected shape's EFFECTIVE geometry, in <see cref="SelectedIndices"/>
    /// order. Out-of-range indices (a stale selection during an in-flight undo) are skipped, mirroring
    /// every other selection-reading loop in this class.</summary>
    public IReadOnlyList<LayoutShape> EffectiveSelectedShapes() =>
        SelectedIndices.Where(i => i >= 0 && i < Model.Shapes.Count).Select(EffectiveShapeAt).ToList();

    // ── Unknown-layer warning — once per layer per load (L0c's deliberately-unwired seam) ───────

    private readonly HashSet<LayerKey> _warnedUnknownLayers = [];

    /// <summary>Called by the canvas after each frame with any layer keys a resolved
    /// <see cref="Technology"/> did not define. Posts a Messages warning the first time each key is
    /// seen for this open document — never once per shape, never inside the render loop.</summary>
    public void ReportUnknownLayers(IReadOnlyList<LayerKey> keys)
    {
        foreach (var key in keys)
        {
            if (!_warnedUnknownLayers.Add(key)) continue;
            _messageSink?.Warning($"Layer {key.Layer}/{key.Datatype} is not defined in '{TechNameText}' — using a generated fallback color.");
        }
    }

    // ── Tools (L1b) ────────────────────────────────────────────────────────────

    /// <summary><c>Select</c> is a registered tool that does nothing in L1b — hit-testing,
    /// selection, and editing are L1c. <c>Curve</c> is deliberately absent: see the promotion-rule
    /// note at the top of <c>LayoutModel.cs</c>.</summary>
    /// <summary><c>Instance</c> (L3a, docs/sonnet-briefs/brief-L3a-instances-and-arrays.md §6) places a
    /// cell reference — pick via <see cref="BeginInstancePlacement"/> (from a cell-picker dialog the
    /// view owns), then click-to-place with a live ghost, mirroring L1f's paste-placement gesture.</summary>
    /// <summary><c>Via</c> (docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.1) is the simplest
    /// tool in this list: a single click commits a <see cref="ViaShape"/> immediately at the snapped
    /// point, technology-default pad/drill, no drag and no ghost-then-click two-step — see
    /// <see cref="ViaToolAvailability"/> for why it is not always enabled.</summary>
    public enum Tool { Select, Rect, RoundedRect, Circle, Polygon, Path, Label, Instance, Via, Port }

    [ObservableProperty] private Tool _activeTool = Tool.Select;

    public IRelayCommand<string> SetActiveToolCommand { get; private set; } = null!;

    partial void OnActiveToolChanged(Tool value) => CancelDrawOp();

    private static bool IsTwoPointDragTool(Tool t) => t is Tool.Rect or Tool.RoundedRect or Tool.Circle;
    private static bool IsMultiPointTool(Tool t)   => t is Tool.Polygon or Tool.Path;

    // ── Via tool (docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.1) ───────────────────────

    /// <summary>R13a: "tool prominence follows the technology — needed where the stackup has a via
    /// layer with a drill function; on a technology without one it is redundant, since a via there is
    /// ordinary geometry (§1)." A via layer is any <see cref="StackupKind.Via"/> stackup entry, exactly
    /// how R-via-4 completed both starter stackups — no separate "has a drill function" flag exists or
    /// is needed, since <c>StackupKind.Via</c> IS that function.</summary>
    public LayoutCommandAvailability ViaToolAvailability =>
        Technology is { } tech && tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Via)
            ? LayoutCommandAvailability.Enabled
            : LayoutCommandAvailability.Disabled(
                "Via: this technology's stackup has no via layer — draw geometry on a via/drill layer directly instead.");

    /// <summary>The toolbar tooltip text — the base description when enabled, the R13a reason when
    /// not (bound directly rather than left to code-behind, since a toolbar button has no natural
    /// "open a menu" moment to compute it in, unlike the context-menu items <c>LayoutCanvas</c> already
    /// surfaces <see cref="LayoutCommandAvailability.DisabledReason"/> for).</summary>
    public string ViaToolTipText => ViaToolAvailability.CanExecute
        ? "Via (place at snapped point)"
        : ViaToolAvailability.DisabledReason!;

    /// <summary>§4.1: "single click places a ViaShape at the snapped point... pad and drill default
    /// from the technology." One <see cref="AddShapeCommand"/>, exactly like every other L1b drawing
    /// tool's commit (<see cref="FinishTwoPointDraw"/>/<see cref="FinishMultiPointDraw"/>) — no drag,
    /// no ghost-then-click two-step, since there is nothing to size or shape: PadSize/DrillSize are
    /// fixed technology defaults, only editable afterward via the Properties Inspector (L1j).</summary>
    private void CommitViaPlacement(double wx, double wy, KeyModifiers mods)
    {
        if (!ViaToolAvailability.CanExecute) return;
        bool suspend = (mods & KeyModifiers.Alt) != 0;
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend);

        long pad   = Technology is { DefaultViaPadDbu: > 0 }   t1 ? t1.DefaultViaPadDbu   : 500_000; // 0.5 mm
        long drill = Technology is { DefaultViaDrillDbu: > 0 } t2 ? t2.DefaultViaDrillDbu : 300_000; // 0.3 mm

        var via = new ViaShape { Layer = CurrentLayerKey, X = sx, Y = sy, PadSize = pad, DrillSize = drill };
        Execute(new AddShapeCommand(Model, via));
        RebuildOverlay();
    }

    // ── Port tool (L8e D3) ──────────────────────────────────────────────────────────────────────

    /// <summary>The live Port-tool ghost: exactly what a click at the current cursor would place,
    /// or null when the cursor is off every conductor (where a click places nothing). Built by
    /// <see cref="TryBuildPortPlacement"/> — the same method the commit uses — so the ghost's
    /// snapped position, inferred direction and resolved width are the ones that will land.</summary>
    private LabelShape? _portGhost;

    /// <summary>
    /// §10.6's "click an edge, get P1", and <b>the tool sets the port flag and nothing else</b>.
    ///
    /// <para>A port is an ordinary <see cref="LabelShape"/> with <see cref="LabelShape.IsPort"/> set
    /// — the provision <c>LayoutModel.cs</c> has carried since L0a, spent here. There is no new
    /// shape type, no change to <c>.clay</c>'s schema, and a layout full of port labels round-trips
    /// exactly as it already did.</para>
    ///
    /// <para><b>The reference impedance is deliberately NOT here.</b> It lives in the <c>.cem</c>
    /// (<c>EmSetup.PortZ0s</c>, already per-port and already additive, R-cpl-6). Putting it on the
    /// shape would give one quantity two homes, and the layout is the wrong one: the same artwork can
    /// legitimately be analysed by two EM setups at two reference impedances.</para>
    ///
    /// <para><b>The DIRECTION is seeded here and is then the user's</b> (owner report, 2026-08-09).
    /// It was previously inferred at extraction time and nowhere else, so nothing on screen said
    /// which way a port faced and there was nothing to rotate. The tool now infers it once, at
    /// placement, from the artwork under the click, and stores it in
    /// <see cref="LabelShape.PortDirection"/>; Rotate advances it from there. A port placed on bare
    /// dielectric gets no direction at all rather than a guessed one — null still means "infer it",
    /// which is exactly what every pre-existing <c>.clay</c> carries.</para>
    /// </summary>
    private void CommitPortPlacement(double wx, double wy, KeyModifiers mods, double zoomPxPerDbu, long snapTolDbu)
    {
        if (TryBuildPortPlacement(wx, wy, mods, snapTolDbu, zoomPxPerDbu) is not { } label)
        {
            // Owner report, 2026-08-09: "in place-port mode, when I clicked away from the metal, a
            // port was created." A port names an END OF A CONDUCTOR — off the metal there is no end
            // to name, no direction to face and no width to be. Refused with a reason rather than
            // placed as a label that looks like a port and is refused much later, at Simulate.
            ReportWarning("Port: click on a conductor — a port names the end of a piece of metal, " +
                          "so there is nothing to place one on here.");
            return;
        }

        _portGhost = null;
        Execute(new AddShapeCommand(Model, label));
        RebuildOverlay();
    }

    /// <summary>
    /// The ONE place a Port-tool placement is decided — snap point, conductor, direction, name — used
    /// by BOTH the hover ghost and the click that commits, so <b>what the ghost shows is what lands</b>
    /// rather than two independently-computed answers that can disagree (owner request, 2026-08-09:
    /// "the port needs a ghost rendering… the ghost's snapping and sizes also need to render live").
    /// Returns null exactly when the point is off every conductor — which is what makes the ghost
    /// VANISH there, saying "clicking here creates nothing" before the click rather than after it.
    ///
    /// <para>R-snpf-4's own rule, applied to the Port tool: geometry snap overrides grid snap only
    /// when it genuinely has a REAL feature to offer. A port wants to land exactly on a conductor's
    /// corner or edge midpoint far more often than on the grid, and the alternative — zoom in and
    /// eyeball it — is what the marker is for.</para>
    /// </summary>
    private LabelShape? TryBuildPortPlacement(double wx, double wy, KeyModifiers mods, long snapTolDbu, double zoomPxPerDbu)
    {
        bool suspend = (mods & KeyModifiers.Alt) != 0;

        UpdateSnapMarker((long)Math.Round(wx), (long)Math.Round(wy), mods, Math.Max(snapTolDbu, 0), 1);
        var (sx, sy) = _snapCandidateIsRealTarget && _currentSnapCandidate is { } target
            ? (target.X, target.Y)
            : LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend);

        var conductorAt = LayoutPortDirection.LookupFor(Model, Technology, InstanceBaseDir);
        if (conductorAt(sx, sy) is not { } conductor) return null;

        // The same visibility floor an ordinary committed label gets — a port marker that renders
        // sub-pixel is a port the user cannot see they placed (the L1-fix default-zoom lesson).
        long height = zoomPxPerDbu > 0
            ? Renderers.LayoutRenderer.EffectiveVisibleLabelHeightDbu(_labelHeightDbu, zoomPxPerDbu)
            : _labelHeightDbu;

        return new LabelShape
        {
            Layer         = CurrentLayerKey,
            X             = sx,
            Y             = sy,
            Text          = NextPortName(),
            Height        = height,
            Rotation      = LayoutRotation.R0,
            IsPort        = true,
            // The SAME answer Resolve would infer — a pin's own inward direction when the point
            // names one, the box's nearest-side inference otherwise. Re-deriving it here from the
            // box alone (as this did) is what let a port on a tapered PCell be stamped with a
            // direction its own marker then disagreed with.
            PortDirection = LayoutPortDirection.DirectionAt(conductor, sx, sy),
        };
    }

    /// <summary>
    /// D3's auto-numbering: the lowest port number this layout does not already use. Reads the
    /// EXISTING port labels through <c>EmPortExtraction.TryParseNumber</c> — the same parser the
    /// extractor uses — so the tool and the extractor can never disagree about what "P3" means.
    /// </summary>
    internal string NextPortName()
    {
        var used = new HashSet<int>();
        foreach (var s in Model.Shapes)
            if (s is LabelShape { IsPort: true } l && Em.EmPortExtraction.TryParseNumber(l.Text, out int n))
                used.Add(n);

        int next = 1;
        while (used.Contains(next)) next++;
        return $"P{next}";
    }

    // ── Selection (L1c) ───────────────────────────────────────────────────────
    // docs/design/layout-view.md §6.2 R13 (overlap cycling), §1.5 R5 / R-L1c-3 (snap the delta).

    private readonly List<int> _selectedIndices = [];

    /// <summary>Currently selected shape indices, in the order they were added to the selection.
    /// Mirrors <c>Overlay.SelectedIndices</c> (which is what actually drives rendering) — read this
    /// property, and watch for <see cref="Overlay"/> to change via <c>PropertyChanged</c>, exactly
    /// as <c>SymbolPrimitiveInspectorViewModel</c> watches <c>SymbolEditorViewModel.Overlay</c>.</summary>
    public IReadOnlyList<int> SelectedIndices => _selectedIndices;

    [ObservableProperty] private string _selectionStatusText = "";

    public IRelayCommand SelectAllCommand { get; private set; } = null!;
    public IRelayCommand DeselectAllCommand { get; private set; } = null!;

    /// <summary>brief-L3a-followups.md §2/R-fix-2: shapes and instances may now be selected together.
    /// <paramref name="clearOtherKind"/> distinguishes the two kinds of caller — a REPLACE (plain
    /// click, SelectAll/DeselectAll, marquee with no modifier: "this is the whole new selection," so
    /// the other kind must be cleared alongside it) from an ADD/TOGGLE (Shift/Ctrl-click, or the
    /// marquee's own Shift/Ctrl combination: "extend what's already selected," so the other kind's
    /// selection must survive untouched). Default true matches every pre-existing call site, which was
    /// always a replace.</summary>
    private void SetSelection(IEnumerable<int> indices, bool clearOtherKind = true)
    {
        var distinct = new List<int>();
        foreach (var i in indices)
            if (i >= 0 && i < Model.Shapes.Count && !distinct.Contains(i))
                distinct.Add(i);

        _selectedIndices.Clear();
        _selectedIndices.AddRange(distinct);
        _pickedVertexIndex = null;

        // Guarded on Count>0 so a replace against an already-empty instance selection is a no-op (no
        // spurious overlay rebuild) — the overwhelmingly common case.
        if (clearOtherKind && _selectedInstanceIndices.Count > 0) _selectedInstanceIndices.Clear();

        SelectionStatusText = ComputeSelectionStatus();
        RebuildOverlay();
    }

    private string ComputeGenericSelectionStatus()
    {
        if (_selectedIndices.Count == 0) return "";
        if (_selectedIndices.Count == 1)
        {
            int idx = _selectedIndices[0];
            if (idx < 0 || idx >= Model.Shapes.Count) return "";
            var shape = Model.Shapes[idx];
            return $"{ShapeTypeName(shape)} · {LayerDisplayName(shape.Layer)}";
        }
        return $"{_selectedIndices.Count} selected";
    }

    /// <summary>The one status-text computation for EITHER kind alone (unchanged single-kind detail,
    /// via <see cref="ComputeGenericSelectionStatus"/>/<see cref="ComputeInstanceSelectionStatus"/>)
    /// OR both together (brief-L3a-followups.md §2 — a mixed selection has no single "type · layer"
    /// detail to show, so it reports the count of each kind instead).</summary>
    private string ComputeSelectionStatus()
    {
        bool hasShapes = _selectedIndices.Count > 0;
        bool hasInstances = _selectedInstanceIndices.Count > 0;
        if (hasShapes && hasInstances)
        {
            string shapeWord = _selectedIndices.Count == 1 ? "shape" : "shapes";
            string instWord = _selectedInstanceIndices.Count == 1 ? "instance" : "instances";
            return $"{_selectedIndices.Count} {shapeWord} + {_selectedInstanceIndices.Count} {instWord}";
        }
        if (hasInstances) return ComputeInstanceSelectionStatus();
        return ComputeGenericSelectionStatus();
    }

    private static string ShapeTypeName(LayoutShape shape) => shape switch
    {
        RectShape         => "Rect",
        PolygonShape      => "Polygon",
        RoundedRectShape  => "RoundedRect",
        CircleShape       => "Circle",
        CurveShape        => "Curve",
        PathShape         => "Path",
        ViaShape          => "Via",
        LabelShape        => "Label",
        _                 => shape.GetType().Name,
    };

    private LayerDef ResolveLayerDef(LayerKey key)
    {
        if (Technology is { } tech)
            foreach (var l in tech.Layers)
                if (l.Key == key) return l;
        return FallbackPalette.For(key);
    }

    private string LayerDisplayName(LayerKey key) => ResolveLayerDef(key).Name;

    // ── Overlap cycling cache (R-L1c-2) ────────────────────────────────────────
    // ClickCycleCache<int>.ClickX/ClickY is the world point (rounded to DBU) of the press that built
    // this cache; Stack is the ordered hit list from that press; Index is which stack entry is
    // CURRENTLY selected. Invalidated by: pointer movement beyond the tolerance threshold
    // (HandleSelectMove), any model mutation (the Model.Changed subscription in the constructor), and
    // any selection change originating elsewhere (SelectAll/DeselectAll/marquee/delete below). The
    // generic ClickCycleCache<T> (docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md R-snp-9)
    // is the SAME cycling algorithm geometry-snap candidate cycling reuses — do not fork a second one.
    private readonly ClickCycleCache<int> _cycleCache = new();

    // ── Select-tool gesture state ─────────────────────────────────────────────

    // MoveInstance is gone (brief-L3a-followups.md §2/R-fix-2) — Move now covers whichever of
    // shapes/instances are selected, together, since a mixed selection can contain both.
    private enum SelectDragKind { None, Move, Marquee }
    private SelectDragKind _selectDragKind = SelectDragKind.None;

    private long _selectPressWX, _selectPressWY;   // world press point (rounded to DBU)
    private long _marqueeCurX, _marqueeCurY;       // live marquee far corner
    private bool _marqueeAdd, _marqueeToggle;      // Shift / Ctrl captured at press
    private List<int> _marqueeBaseSelection = [];
    private List<int> _marqueeBaseInstanceSelection = [];

    // ── Live marquee preview (L1i, docs/sonnet-briefs/brief-L1i-live-marquee-selection.md; extended
    // to instances by brief-L3a-followups.md §2/R-fix-3) ────────────────────────────────────────────
    // R-L1i-2: _marqueePreview/_marqueeInstancePreview are SEPARATE lists from _selectedIndices/
    // _selectedInstanceIndices — they are what the highlight renders while a marquee drag is active,
    // and are NEVER written into the real selection until commit (HandleSelectRelease -> CommitMarquee
    // -> ReplaceMixedSelection). All four are reused scratch buffers (cleared and refilled in place,
    // never reallocated per pointer move) per the brief's perf note.
    private readonly List<int> _marqueeHitsScratch = [];
    private readonly List<int> _marqueePreview = [];
    private readonly List<int> _marqueeInstanceHitsScratch = [];
    private readonly List<int> _marqueeInstancePreview = [];
    private (long X, long Y)? _marqueeLastComputedCorner; // null = "not computed yet this drag"

    /// <summary>How many times <see cref="ComputeMarqueeSelection"/> actually ran (as opposed to being
    /// skipped by the &lt;1-device-pixel-movement guard). Test-only instrumentation for gate 9 —
    /// internal, exposed to <c>CircuitRF.Ui.Tests</c> via <c>InternalsVisibleTo</c>.</summary>
    internal int MarqueeRecomputeCount { get; private set; }

    private long _moveAnchorX, _moveAnchorY;       // press point the move delta is measured from
    private long _moveLiveDx, _moveLiveDy;         // current snapped delta (0,0) until the drag moves
    private bool _moveHasMoved;                    // true once the live delta has been non-zero at least once

    private static readonly IReadOnlyDictionary<int, LayoutShape> EmptyDragOverrides = new Dictionary<int, LayoutShape>();

    // ── Handle drag state (L1d) ────────────────────────────────────────────────
    // docs/design/layout-view.md §6.3 R14, L1d brief. Independent of _selectDragKind — a handle drag
    // pre-empts move/marquee entirely and is checked first in every gesture handler.

    private enum HandleDragKind { None, Vertex, EdgeMidpoint, RectEdge, Bulge, CubicControl, Radius, CornerRadius, RectCorner }
    private HandleDragKind _handleDragKind = HandleDragKind.None;
    private int _handleDragShapeIndex;
    private int _handleDragIndex;      // vertex/edge/corner index
    private int _handleDragSubIndex;   // cubic control point: 0 = C1, 1 = C2
    private LayoutShape? _handleDragOriginal; // shape BEFORE the drag — also the Escape-restore/undo "before"
    private LayoutShape? _handleDragPreview;  // current live preview, rendered via Overlay.DragOverrides
    private long _handleDragAnchorX, _handleDragAnchorY; // press point — edge-drag/bulge project against this
    private bool _handleDragMoved;

    /// <summary>The last VERTEX handle clicked (press+release, no drag) on the single selection, or
    /// null. Delete/Backspace removes this vertex instead of the whole shape when set (§3 "Delete on
    /// a selected vertex"). Cleared on any selection change, model mutation, or Escape.</summary>
    private int? _pickedVertexIndex;

    private void HandleSelectPress(double wx, double wy, KeyModifiers mods, long tolDbu, long snapTolDbu = 0)
    {
        bool shift = (mods & KeyModifiers.Shift) != 0;
        bool ctrl  = (mods & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool alt   = (mods & KeyModifiers.Alt) != 0;

        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);
        _selectPressWX = px; _selectPressWY = py;

        // pcell-parameter-handles.md R-pch-8: a PCell instance's PARAMETER grips are tested FIRST, and
        // specifically before the instance-body move drag further down — otherwise grabbing a grip
        // would move the whole instance instead, which is the one interaction failure here that a
        // user cannot work around. There is no conflict with L1d's own handles below: a SHAPE shows
        // geometry handles, an INSTANCE shows parameter handles, and an instance has never had
        // geometry handles at all.
        if (TryBeginPCellHandleDrag(px, py, tolDbu))
            return;

        // L1h: bbox scale handles take priority over everything else when they're showing (R-L1h-5) —
        // a 2+ selection always has them; a single selection has them only while Scale mode is toggled
        // on, in which case they TEMPORARILY REPLACE L1d's handles rather than coexist with them.
        if (TryBeginScaleDrag(px, py, alt, tolDbu))
            return;

        // L1d: handles (and the edge-line fallback below them) take absolute priority over L1c's
        // selection/cycling logic when exactly one shape is selected — "a press on a handle must not
        // disturb the selection or the overlap-cycling cache" (§2). Only a single-shape selection
        // shows handles at all (§2: "multi-selection shows no handles — it is a move/delete selection").
        // brief-L3a-followups.md §2: an instance mixed into the selection also suppresses handles —
        // "Vertex/edge/bulge/control-point handles: No — an instance has no vertices" — a click near a
        // would-be handle position on the one selected shape must fall through to ordinary
        // selection/move when instances are ALSO selected, not silently reach shape-only geometry
        // editing.
        if (_selectedIndices.Count == 1 && _selectedInstanceIndices.Count == 0 && !ScaleModeActive
            && TryHandleSelectPressOnHandles(_selectedIndices[0], px, py, ctrl, alt, tolDbu))
            return;
        _pickedVertexIndex = null;

        // brief-snap-distance-and-geometry-snap.md R-snp-8/R-snp-10: geometry snap sits between L1d's
        // handle check (above — handles still win within their own radius on a selected shape) and
        // the ordinary hit-test/cycling fallback below. A marker showing at the click point consumes
        // the click for its OWNING shape/instance even when the click itself misses that shape's own
        // hit-test — this is the feature's headline behaviour.
        if (TryBeginSnapMarkerDrag(px, py, shift, ctrl, alt, snapTolDbu))
            return;

        if (_cycleCache.Matches(px, py, tolDbu, alt))
        {
            int advanced = _cycleCache.Advance(px, py);
            ApplyClickSelection(advanced, shift, ctrl);
            UpdateSelectionStatusFromCycle();
            if (!shift && !ctrl) BeginMoveDrag(px, py);
            return;
        }

        var stack = LayoutHitTest.HitStack(Model, Technology, px, py, tolDbu);
        if (stack.Count == 0)
        {
            _cycleCache.Clear();

            // L3a (R-L3a-5): no shape under the click — try an instance before falling back to a
            // marquee. Instances get their own (simpler, no cycling) click-select: a fresh press
            // always re-hit-tests, since instances are typically few enough that "which one is on
            // top" rarely needs the shape overlap-cycling machinery.
            var instStack = LayoutHitTest.HitInstanceStack(Model, Technology, InstanceBaseDir, px, py, tolDbu);
            if (instStack.Count > 0)
            {
                ApplyInstanceClickSelection(instStack[0], shift, ctrl);
                if (!shift && !ctrl) BeginMoveDrag(px, py);
                return;
            }

            if (!shift && !ctrl) SetSelection([]); // clearOtherKind:true (default) clears instances too
            BeginMarquee(px, py, shift, ctrl);
            return;
        }

        int hitIndex = _cycleCache.Rebuild(px, py, stack);
        ApplyClickSelection(hitIndex, shift, ctrl);
        UpdateSelectionStatusFromCycle();

        if (!shift && !ctrl) BeginMoveDrag(px, py);
    }

    /// <summary>Tests Ctrl/Cmd+click-insert FIRST, then handles (R-L1d-2 priority order), then the
    /// plain-click edge-drag fallback, for the single selected shape. Returns true if the press was
    /// consumed by one of these — the caller must return immediately without touching selection/cycling
    /// state.</summary>
    private bool TryHandleSelectPressOnHandles(int shapeIndex, long px, long py, bool ctrl, bool alt, long tolDbu)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return false;
        var shape = Model.Shapes[shapeIndex];

        if (ctrl && LayoutShapeEditing.IsVertexListShape(shape))
        {
            // Ctrl/Cmd+click an edge -> insert a vertex there (one command, not a drag). Only applies
            // to a true vertex-list shape (Polygon/Curve/Path) — a Rect/RoundedRect has no vertex list
            // to insert into (its 4 edges are each a single X1/Y1/X2/Y2 field), so it's excluded here
            // rather than crashing inside InsertVertexOnEdge. Checked BEFORE the handle hit-test,
            // deliberately: every straight edge already carries an EdgeMidpoint handle sitting exactly
            // at the point a user most naturally clicks to "click the edge" — without this ordering, a
            // Ctrl+click landing on (or near) that handle would silently begin an edge-DRAG instead of
            // inserting, since handles otherwise take absolute priority. Ctrl declares unambiguous
            // intent ("insert here"), which must win regardless of what handle occupies the same pixel.
            int? ctrlEdgeHit = LayoutShapeEditing.FindEdgeLineHit(shape, px, py, tolDbu);
            if (ctrlEdgeHit is { } ctrlEdgeIndex)
            {
                var after = LayoutShapeEditing.InsertVertexOnEdge(shape, ctrlEdgeIndex, px, py, Model.SnapDbu, alt);
                Execute(new Commands.Layout.ReplaceShapeCommand(Model, shapeIndex, shape, after));
                _pickedVertexIndex = null;
                return true;
            }
            // No edge under the click even with Ctrl held (e.g. Ctrl+click on empty space, or on a
            // non-edge handle like Radius/CornerRadius) -- fall through to the normal handle test below.
        }

        var handles = LayoutHandles.Build(shape);
        var hit = LayoutHandleHitTest.HitTest(handles, px, py, tolDbu);
        if (hit is { } handle)
        {
            BeginHandleDrag(shapeIndex, shape, handle, px, py);
            // "Picked vertex" bookkeeping (for the Delete key, §3 "Delete on a selected vertex") only
            // applies to true vertex-list shapes (Polygon/Curve/Path). A Rect/RoundedRect corner is
            // ALSO reported as a Vertex-kind handle (it maps to HandleDragKind.RectCorner, a resize —
            // not a removable vertex) — without this guard, clicking a Rect's corner would silently
            // make the next Delete keypress a no-op (LayoutShapeEditing.RemoveVertex correctly refuses
            // a non-vertex-list shape) instead of falling through to deleting the whole shape.
            _pickedVertexIndex = handle.Kind == LayoutHandleKind.Vertex && LayoutShapeEditing.IsVertexListShape(shape)
                ? handle.Index : null;
            return true;
        }

        int? edgeHit = LayoutShapeEditing.FindEdgeLineHit(shape, px, py, tolDbu);
        if (edgeHit is not { } edgeIndex) return false;

        if (LayoutShapeEditing.IsStraightEdge(shape, edgeIndex))
        {
            // A plain click on a straight edge's LINE (not exactly its midpoint handle) begins the
            // same perpendicular edge-drag the midpoint handle would. Curved (Arc/Cubic) edges have
            // no line-drag gesture of their own — only their Bulge/CubicControl handles reshape them
            // (handled above); clicking their curve body falls through to the shape's normal
            // body/move-drag instead.
            BeginHandleDrag(shapeIndex, shape, new LayoutHandle(LayoutHandleKind.EdgeMidpoint, 0, 0, edgeIndex), px, py);
            _pickedVertexIndex = null;
            return true;
        }

        return false;
    }

    private void BeginHandleDrag(int shapeIndex, LayoutShape shape, LayoutHandle handle, long px, long py)
    {
        _handleDragKind = handle.Kind switch
        {
            LayoutHandleKind.Vertex when shape is RectShape or RoundedRectShape => HandleDragKind.RectCorner,
            LayoutHandleKind.Vertex       => HandleDragKind.Vertex,
            LayoutHandleKind.EdgeMidpoint when shape is RectShape or RoundedRectShape => HandleDragKind.RectEdge,
            LayoutHandleKind.EdgeMidpoint => HandleDragKind.EdgeMidpoint,
            LayoutHandleKind.Bulge        => HandleDragKind.Bulge,
            LayoutHandleKind.CubicControl => HandleDragKind.CubicControl,
            LayoutHandleKind.Radius       => HandleDragKind.Radius,
            LayoutHandleKind.CornerRadius => HandleDragKind.CornerRadius,
            _ => HandleDragKind.None,
        };
        _handleDragShapeIndex = shapeIndex;
        _handleDragOriginal = shape;
        _handleDragPreview = null;
        _handleDragIndex = handle.Index;
        _handleDragSubIndex = handle.SubIndex;
        _handleDragAnchorX = px; _handleDragAnchorY = py;
        _handleDragMoved = false;
        RebuildOverlay();
    }

    /// <summary>
    /// R-L1d — three snapping rules, deliberately written next to each other so they read as one
    /// considered system rather than an inconsistency (the brief's own framing):
    /// <list type="bullet">
    /// <item><b>Vertex drag snaps the resulting POSITION.</b> The user is placing a single point; the
    /// other vertices are untouched, so snapping this one to the grid mangles nothing.</item>
    /// <item><b>Edge drag snaps the perpendicular OFFSET</b> (a scalar), then applies the identical
    /// snapped delta to both endpoints — a rigid translation of just those two vertices, which is
    /// why it (like a whole-shape move, R-L1c-3) must snap the delta and not each vertex
    /// independently: rounding each endpoint on its own could turn a 45° edge into something else.</item>
    /// <item><b>Bulge / cubic-control / radius / corner-radius / Rect-corner drags snap their own
    /// scalar result</b> (the bulge value is unbounded and geometric rather than a coordinate, so it
    /// is intentionally NOT grid-snapped; radius and corner-radius ARE snapped, being lengths on the
    /// same grid as everything else).</item>
    /// </list>
    /// brief-geometry-snap-followups.md R-snpf-1/2/3 layers geometry snap ON TOP of the grid-snap rules
    /// above, for exactly the two position-shaped cases: Vertex/RectCorner (R-snpf-2's "Vertex" row —
    /// the vertex lands ON the candidate) and EdgeMidpoint/RectEdge (R-snpf-2's "Edge" row — the
    /// candidate is PROJECTED onto the edge's own perpendicular axis, since an edge drag moves a whole
    /// line, not a point). <see cref="_currentSnapCandidate"/> is already resolved for THIS tick by
    /// <c>UpdateSnapMarker</c>, called ahead of this method in <c>HandleSelectMove</c> — a candidate in
    /// range overrides grid snap (R-snpf-3); Alt-suspend already nulls the candidate upstream, so
    /// <paramref name="suspendSnap"/> is checked here too only as an explicit, self-documenting guard.
    /// Bulge/CubicControl/Radius/CornerRadius/Scale are deliberately UNCHANGED — R-snpf-2 explicitly
    /// scopes them out (a curvature or length control has no "position" a candidate could relocate to,
    /// and a Scale drag has no single grab point) — <c>UpdateSnapMarker</c> never even queries for
    /// those, so <see cref="_currentSnapCandidate"/> is always null while one is in progress.
    /// </summary>
    private LayoutShape? BuildHandleDragPreview(long px, long py, bool suspendSnap)
    {
        if (_handleDragOriginal is null) return null;
        var original = _handleDragOriginal;

        switch (_handleDragKind)
        {
            case HandleDragKind.Vertex:
            {
                var (sx, sy) = ResolveHandlePositionWithSnap(px, py, suspendSnap);
                return LayoutShapeEditing.SetVertex(original, _handleDragIndex, sx, sy);
            }

            case HandleDragKind.EdgeMidpoint:
            {
                var (dx, dy) = ComputeEdgePerpendicularOffset(original, _handleDragIndex, px, py, suspendSnap);
                return LayoutShapeEditing.TranslateEdgeEndpoints(original, _handleDragIndex, dx, dy);
            }

            case HandleDragKind.RectEdge:
            {
                long delta = ComputeRectEdgePerpendicularOffset(original, _handleDragIndex, px, py, suspendSnap);
                return original switch
                {
                    RectShape r         => LayoutShapeEditing.TranslateRectEdge(r, _handleDragIndex, delta),
                    RoundedRectShape rr => LayoutShapeEditing.TranslateRoundedRectEdge(rr, _handleDragIndex, delta),
                    _ => null,
                };
            }

            case HandleDragKind.Bulge:
            {
                double bulge = ComputeBulgeFromDrag(original, _handleDragIndex, px, py);
                return LayoutShapeEditing.SetBulge(original, _handleDragIndex, bulge);
            }

            case HandleDragKind.CubicControl:
            {
                var (sx, sy) = LayoutSnapping.SnapPoint(px, py, Model.SnapDbu, suspendSnap);
                return LayoutShapeEditing.SetCubicControl(original, _handleDragIndex, _handleDragSubIndex, sx, sy);
            }

            case HandleDragKind.Radius when original is CircleShape c:
            {
                double dx = px - c.Cx, dy = py - c.Cy;
                long rawR = (long)Math.Round(Math.Sqrt(dx * dx + dy * dy));
                long snappedR = LayoutSnapping.SnapValue(rawR, Model.SnapDbu, suspendSnap);
                return LayoutShapeEditing.SetRadius(c, snappedR);
            }

            case HandleDragKind.CornerRadius when original is RoundedRectShape rr:
            {
                long x1 = Math.Min(rr.X1, rr.X2);
                long rawR = px - x1;
                long snappedR = LayoutSnapping.SnapValue(rawR, Model.SnapDbu, suspendSnap);
                return LayoutShapeEditing.SetCornerRadius(rr, snappedR);
            }

            case HandleDragKind.RectCorner:
            {
                var (sx, sy) = ResolveHandlePositionWithSnap(px, py, suspendSnap);
                return original switch
                {
                    RectShape r        => LayoutShapeEditing.ResizeRectCorner(r, _handleDragIndex, sx, sy),
                    RoundedRectShape rr => LayoutShapeEditing.ResizeRoundedRectCorner(rr, _handleDragIndex, sx, sy),
                    _ => null,
                };
            }

            default:
                return null;
        }
    }

    /// <summary>R-snpf-2/3 for the two POSITION-shaped handle kinds (Vertex, RectCorner): a geometry-
    /// snap candidate already resolved for this tick (<see cref="_currentSnapCandidate"/>) wins outright
    /// over grid snap; otherwise falls back to the ordinary grid-snapped point.</summary>
    private (long X, long Y) ResolveHandlePositionWithSnap(long px, long py, bool suspendSnap)
    {
        if (!suspendSnap && _currentSnapCandidate is { } candidate) return (candidate.X, candidate.Y);
        return LayoutSnapping.SnapPoint(px, py, Model.SnapDbu, suspendSnap);
    }

    private (long Dx, long Dy) ComputeEdgePerpendicularOffset(LayoutShape original, int edgeIndex, long px, long py, bool suspendSnap)
    {
        var xy = LayoutShapeEditing.XyOf(original);
        int n = xy.Length / 2;
        bool closed = LayoutShapeEditing.IsClosed(original);
        int j = closed ? (edgeIndex + 1) % n : edgeIndex + 1;
        long x0 = xy[2 * edgeIndex], y0 = xy[2 * edgeIndex + 1];
        long x1 = xy[2 * j], y1 = xy[2 * j + 1];

        double ex = x1 - x0, ey = y1 - y0;
        double len = Math.Sqrt(ex * ex + ey * ey);
        if (len < 1e-9) return (0, 0);
        double nx = -ey / len, ny = ex / len; // unit perpendicular to the edge

        long snapped;
        if (!suspendSnap && _currentSnapCandidate is { } candidate)
        {
            // R-snpf-2/3: a candidate is projected onto THIS edge's own perpendicular axis (the edge
            // is a whole line, so only the candidate's position ALONG that axis matters) — the edge
            // then lands exactly where that projection is, overriding grid snap outright.
            double edgeProj = x0 * nx + y0 * ny;
            double candidateProj = candidate.X * nx + candidate.Y * ny;
            snapped = (long)Math.Round(candidateProj - edgeProj);
        }
        else
        {
            double totalDx = px - _handleDragAnchorX, totalDy = py - _handleDragAnchorY;
            double offset = totalDx * nx + totalDy * ny; // scalar projection onto the perpendicular
            snapped = LayoutSnapping.SnapValue(offset, Model.SnapDbu, suspendSnap);
        }

        return ((long)Math.Round(snapped * nx), (long)Math.Round(snapped * ny));
    }

    /// <summary>Same "snap the perpendicular offset" rule as <see cref="ComputeEdgePerpendicularOffset"/>,
    /// simplified for a Rect/RoundedRect's always-axis-aligned edges: edges 0/2 (bottom/top) are
    /// horizontal, so their perpendicular is vertical (project the drag's Y delta); edges 1/3
    /// (right/left) are vertical, so their perpendicular is horizontal (project the drag's X delta).
    /// No vector math needed — the axis is fixed by which edge it is. R-snpf-2/3: a candidate is
    /// projected onto that same fixed axis (Y for 0/2, X for 1/3) against the edge's OWN pre-drag
    /// coordinate on that axis (<paramref name="original"/>, read via <see cref="RectEdgeCoordinate"/>)
    /// — overriding grid snap outright — exactly mirroring the general vertex-list case above.</summary>
    private long ComputeRectEdgePerpendicularOffset(LayoutShape original, int edgeIndex, long px, long py, bool suspendSnap)
    {
        if (!suspendSnap && _currentSnapCandidate is { } candidate && RectEdgeCoordinate(original, edgeIndex) is { } originalCoord)
        {
            long targetCoord = edgeIndex is 0 or 2 ? candidate.Y : candidate.X;
            return targetCoord - originalCoord;
        }

        double totalDx = px - _handleDragAnchorX, totalDy = py - _handleDragAnchorY;
        double raw = edgeIndex is 0 or 2 ? totalDy : totalDx;
        return LayoutSnapping.SnapValue(raw, Model.SnapDbu, suspendSnap);
    }

    /// <summary>The pre-drag coordinate of one Rect/RoundedRect edge, in the SAME 0=bottom(Y1)/
    /// 1=right(X2)/2=top(Y2)/3=left(X1) convention <see cref="LayoutShapeEditing.TranslateRectEdge"/>
    /// already uses — null for any other shape kind (a RectEdge drag never targets one).</summary>
    private static long? RectEdgeCoordinate(LayoutShape shape, int edgeIndex) => (shape, edgeIndex) switch
    {
        (RectShape r, 0) => r.Y1, (RectShape r, 1) => r.X2, (RectShape r, 2) => r.Y2, (RectShape r, 3) => r.X1,
        (RoundedRectShape rr, 0) => rr.Y1, (RoundedRectShape rr, 1) => rr.X2, (RoundedRectShape rr, 2) => rr.Y2, (RoundedRectShape rr, 3) => rr.X1,
        _ => null,
    };

    private double ComputeBulgeFromDrag(LayoutShape original, int edgeIndex, long px, long py)
    {
        var xy = LayoutShapeEditing.XyOf(original);
        int n = xy.Length / 2;
        bool closed = LayoutShapeEditing.IsClosed(original);
        int j = closed ? (edgeIndex + 1) % n : edgeIndex + 1;
        long x0 = xy[2 * edgeIndex], y0 = xy[2 * edgeIndex + 1];
        long x1 = xy[2 * j], y1 = xy[2 * j + 1];

        double dx = x1 - x0, dy = y1 - y0;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-9) return 0;
        double ux = dx / d, uy = dy / d;
        double nx = uy, ny = -ux; // SAME right-perpendicular convention as LayoutArc.FromBulge, so the
                                  // drag position and the resulting rendered arc agree on which side bulges.

        double mx = (x0 + x1) / 2.0, my = (y0 + y1) / 2.0;
        double h = (px - mx) * nx + (py - my) * ny; // signed distance of the drag point from the chord midpoint
        double bulge = 2.0 * h / d;                  // inverts LayoutArc.FromBulge's h = bulge*d/2

        // Dragging past the chord (h changes sign) flips the sweep sign by construction — no special
        // casing needed. Clamp only to guard against a runaway value very close to a full circle.
        return Math.Clamp(bulge, -50.0, 50.0);
    }

    private void CommitHandleDrag()
    {
        if (_handleDragMoved && _handleDragPreview is not null && _handleDragOriginal is not null)
        {
            var finalShape = FinalizeHandleDragShape(_handleDragPreview);
            Execute(new Commands.Layout.ReplaceShapeCommand(Model, _handleDragShapeIndex, _handleDragOriginal, finalShape));
            WarnIfSelfIntersecting(finalShape);
        }
        ResetHandleDragState();
    }

    private LayoutShape FinalizeHandleDragShape(LayoutShape preview)
    {
        // Rect/RoundedRect corner AND edge drags normalize (X1<X2, Y1<Y2) only NOW, at commit —
        // keeping the corner/edge-index-to-position mapping stable and simple throughout the whole
        // live preview (an edge dragged past its opposite edge is a well-defined "inside-out" rect
        // mid-drag, exactly like a corner drag).
        return (preview, _handleDragKind) switch
        {
            (RectShape r, HandleDragKind.RectCorner or HandleDragKind.RectEdge)         => LayoutShapeEditing.NormalizeRect(r),
            (RoundedRectShape rr, HandleDragKind.RectCorner or HandleDragKind.RectEdge) => LayoutShapeEditing.NormalizeRoundedRect(rr),
            _ => preview,
        };
    }

    private void WarnIfSelfIntersecting(LayoutShape shape)
    {
        // §5: allow freely during the drag; on release, flag (never block, never auto-repair).
        if (LayoutSelfIntersection.Test(shape, Technology))
            _messageSink?.Warning($"{ShapeTypeName(shape)} on layer {LayerDisplayName(shape.Layer)} self-intersects after this edit.");
    }

    private void ResetHandleDragState()
    {
        _handleDragKind = HandleDragKind.None;
        _handleDragOriginal = null;
        _handleDragPreview = null;
        _handleDragMoved = false;
    }

    // ── Edge-kind conversion (§4) — called from the canvas's right-click context menu ──────────

    /// <summary>Finds the edge nearest (wx,wy) on the single selected shape, within
    /// <paramref name="tolDbu"/> — the canvas's right-click handler uses this to decide whether/what
    /// conversion menu to show. Returns null when there is no single-shape selection, the shape has
    /// no edges, or nothing is within tolerance.</summary>
    public (int ShapeIndex, int EdgeIndex, EdgeKind CurrentKind)? FindEdgeForContextMenu(double wx, double wy, long tolDbu)
    {
        if (_selectedIndices.Count != 1 || _selectedInstanceIndices.Count != 0) return null;
        int shapeIndex = _selectedIndices[0];
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return null;
        var shape = Model.Shapes[shapeIndex];
        if (!LayoutShapeEditing.IsVertexListShape(shape)) return null;

        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);
        int? edgeIndex = LayoutShapeEditing.FindEdgeLineHit(shape, px, py, tolDbu);
        if (edgeIndex is not { } ei) return null;

        var edges = LayoutShapeEditing.EdgesOf(shape);
        var kind = edges is not null && ei < edges.Count ? edges[ei].Kind : EdgeKind.Line;
        return (shapeIndex, ei, kind);
    }

    /// <summary>Converts one edge to Line/Arc/Cubic (§4) — one undo entry. Handles the Polygon→Curve
    /// promotion rule (R-L1d-3) internally via <see cref="LayoutShapeEditing.ConvertEdge"/>.</summary>
    public void ConvertEdge(int shapeIndex, int edgeIndex, EdgeKind newKind)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return;
        var shape = Model.Shapes[shapeIndex];
        var after = LayoutShapeEditing.ConvertEdge(shape, edgeIndex, newKind);
        Execute(new Commands.Layout.ReplaceShapeCommand(Model, shapeIndex, shape, after));
    }

    /// <summary>Finds the vertex nearest (wx,wy) on the single selected shape, within
    /// <paramref name="tolDbu"/> — the canvas's right-click handler uses this to offer a "Delete
    /// Vertex" menu item as an explicit, discoverable alternative to click-to-pick-then-press-Delete.
    /// Only a true vertex-list shape (Polygon/Curve/Path) has a removable vertex — a Rect/RoundedRect
    /// corner is a resize handle, not a vertex, so it is excluded here just like the Ctrl+click-insert
    /// gesture. Reuses the exact same handle hit-test/priority a drag would, so "the vertex you can
    /// right-click to delete" is always the same one a left-click-drag would grab.</summary>
    public (int ShapeIndex, int VertexIndex)? FindVertexForContextMenu(double wx, double wy, long tolDbu)
    {
        if (_selectedIndices.Count != 1 || _selectedInstanceIndices.Count != 0) return null;
        int shapeIndex = _selectedIndices[0];
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return null;
        var shape = Model.Shapes[shapeIndex];
        if (!LayoutShapeEditing.IsVertexListShape(shape)) return null;

        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);
        var handles = LayoutHandles.Build(shape);
        var hit = LayoutHandleHitTest.HitTest(handles, px, py, tolDbu);
        return hit is { Kind: LayoutHandleKind.Vertex } handle ? (shapeIndex, handle.Index) : null;
    }

    private void ApplyClickSelection(int hitIndex, bool shift, bool ctrl)
    {
        if (ctrl)
        {
            SetSelection(_selectedIndices.Contains(hitIndex)
                ? _selectedIndices.Where(i => i != hitIndex)
                : _selectedIndices.Append(hitIndex), clearOtherKind: false);
        }
        else if (shift)
        {
            SetSelection(_selectedIndices.Contains(hitIndex)
                ? _selectedIndices
                : _selectedIndices.Append(hitIndex), clearOtherKind: false);
        }
        else
        {
            // A plain click on a shape that is already part of a MULTI-selection preserves the
            // whole selection — this is what makes "drag from inside any selected shape translates
            // the whole selection" true rather than collapsing the group to just the clicked member
            // before the drag even starts. brief-L3a-followups.md §2: "multi" now counts BOTH kinds
            // together — a shape that is part of a MIXED shape+instance selection also survives a
            // plain click on it, so a drag started there moves the whole mixed group. A click on
            // anything else (an unselected shape, or the sole member of a single-shape selection —
            // which still needs to replace-with-itself so cycling continues to work) replaces the
            // WHOLE selection (both kinds) with just the hit.
            bool totalMulti = _selectedIndices.Count + _selectedInstanceIndices.Count > 1;
            if (!(totalMulti && _selectedIndices.Contains(hitIndex)))
                SetSelection([hitIndex]);
        }
    }

    private void UpdateSelectionStatusFromCycle()
    {
        if (_selectedIndices.Count == 1 && _cycleCache.HasStack && _cycleCache.Stack.Count > 1)
        {
            int idx = _selectedIndices[0];
            int pos = -1;
            for (int i = 0; i < _cycleCache.Stack.Count; i++) if (_cycleCache.Stack[i] == idx) { pos = i; break; }
            if (pos >= 0 && idx >= 0 && idx < Model.Shapes.Count)
            {
                var shape = Model.Shapes[idx];
                SelectionStatusText = $"{ShapeTypeName(shape)} · {LayerDisplayName(shape.Layer)} · {pos + 1} of {_cycleCache.Stack.Count}";
                return;
            }
        }
        SelectionStatusText = ComputeGenericSelectionStatus();
    }

    /// <summary>True while the current move drag is a lone POINT-LIKE shape — today, an EM port —
    /// which geometry snap may attract to an absolute target the same way a grab-role drag can.
    ///
    /// <para><b>Owner report, 2026-08-09: "the port won't snap until it's over metal. I'd like it to
    /// snap to the spot even while dragging over white space, if it's within the threshold."</b>
    /// Target attraction was gated on <see cref="_snapDragActive"/>, which is set only when the PRESS
    /// itself landed on a snap marker — and a <c>LabelShape</c> contributes no snap features, so a
    /// port pressed over empty space began an ordinary body drag and geometry snap never engaged for
    /// the rest of the gesture. Over metal the press happened to find the CONDUCTOR's own feature, so
    /// snap worked there and nowhere else: exactly the reported asymmetry.</para>
    ///
    /// <para><b>Why a port may do this when an ordinary shape may not.</b> R-cmb-4/5's gate exists so
    /// a body drag keeps snapping the DELTA — an off-grid shape must not have its internal geometry
    /// re-quantised by being moved (R-L1c-3). A port is a single anchor point: it HAS no internal
    /// geometry to preserve, and landing exactly on a conductor's corner or edge midpoint is the
    /// entire reason it is being dragged. So the absolute branch is not a relaxation of that rule, it
    /// is the same rule with nothing left for it to protect.</para></summary>
    private bool _pointSnapDragActive;

    private void BeginMoveDrag(long px, long py)
    {
        if (_selectedIndices.Count == 0 && _selectedInstanceIndices.Count == 0) return;
        _selectDragKind = SelectDragKind.Move;

        // A lone port anchors the drag at its OWN anchor rather than at the raw click, so what lands
        // on the snap target is the port itself — not wherever within its pick region the user
        // happened to press. (The pick region is deliberately generous; see LayoutHitTest.)
        _pointSnapDragActive = false;
        if (!_snapDragActive && _selectedInstanceIndices.Count == 0 && _selectedIndices.Count == 1)
        {
            int i = _selectedIndices[0];
            if (i >= 0 && i < Model.Shapes.Count && Model.Shapes[i] is LabelShape { IsPort: true } port)
            {
                _pointSnapDragActive = true;
                px = port.X; py = port.Y;
            }
        }

        _moveAnchorX = px; _moveAnchorY = py;
        _moveLiveDx = 0; _moveLiveDy = 0;
        _moveHasMoved = false;
    }

    private void BeginMarquee(long px, long py, bool shift, bool ctrl)
    {
        _selectDragKind = SelectDragKind.Marquee;
        _selectPressWX = px; _selectPressWY = py;
        _marqueeCurX = px; _marqueeCurY = py;
        _marqueeAdd = shift;
        _marqueeToggle = ctrl;
        _marqueeBaseSelection = _selectedIndices.ToList();
        _marqueeBaseInstanceSelection = _selectedInstanceIndices.ToList();
        _marqueeLastComputedCorner = null;
        ComputeMarqueeSelection(px, py);
        _marqueeLastComputedCorner = (px, py);
        UpdateMarqueeSelectionStatus();
        RebuildOverlay();
    }

    /// <summary>R-L1i-1, extended to instances by brief-L3a-followups.md §2/R-fix-3: the ONE hit
    /// computation shared by the live preview (called every qualifying pointer move) and the commit
    /// (called once at release, via <see cref="CommitMarquee"/>) — if the preview computed hits
    /// differently from the commit, the highlight would lie about the outcome. Shapes and instances
    /// are candidates from the SAME combined L2b/R-L3a-4 tree query (one query, not two — R-fix-3);
    /// each kind's own Shift(add)/Ctrl(toggle)/plain(replace) combination against its own base
    /// selection is identical to L1i's original rule, just run once per kind (<see
    /// cref="CombineMarqueePreview"/>) so a Ctrl-drag un-highlights an already-selected INSTANCE
    /// exactly like it already un-highlights an already-selected shape. "Arrays are one object": an
    /// instance candidate's bbox is <see cref="CellHierarchy.InstanceBbox"/> — the WHOLE array-expanded
    /// extent, never a per-placement one — so a marquee touching ANY cell of a 50×50 array selects the
    /// array as a unit. Mutates <see cref="_marqueePreview"/>/<see cref="_marqueeInstancePreview"/>
    /// (never <c>_selectedIndices</c>/<c>_selectedInstanceIndices</c> — R-L1i-2). Returns the shape
    /// preview list for callers that only need that half (kept for source-compat with L1i's own
    /// shape-only call sites); read <see cref="_marqueeInstancePreview"/> directly for the other.</summary>
    private List<int> ComputeMarqueeSelection(long curX, long curY)
    {
        MarqueeRecomputeCount++;

        bool leftToRight = curX >= _selectPressWX;
        long minX = Math.Min(_selectPressWX, curX), maxX = Math.Max(_selectPressWX, curX);
        long minY = Math.Min(_selectPressWY, curY), maxY = Math.Max(_selectPressWY, curY);
        var marqueeBb = new Bbox(minX, minY, maxX, maxY);

        _marqueeHitsScratch.Clear();
        _marqueeInstanceHitsScratch.Clear();

        // L2b/R-L3a-4: the R-tree query is an INTERSECT test against a possibly-larger-than-exact
        // conservative bbox — a safe superset for BOTH enclose and crossing mode, since containment
        // implies intersection. The candidates below still get the EXACT SAME predicate applied
        // afterward (LayoutGeometry.BboxOf for a shape, CellHierarchy.InstanceBbox for an instance), so
        // the index only changes which candidates are CONSIDERED, never the decision (R-L2b-3) — a
        // candidate whose conservative bbox intersects but whose real bbox does not simply fails the
        // check below, exactly as it would have failed a linear scan.
        Bbox InstanceBboxFor(LayoutInstance inst) => CellHierarchy.InstanceBbox(inst, InstanceBaseDir);
        foreach (var entry in Model.SpatialIndex.QueryIntersecting(
            Model.Shapes, Model.Instances, InstanceBboxFor, CellLayoutResolver.Generation, marqueeBb))
        {
            if (entry.Kind == SpatialEntryKind.Shape)
            {
                var shape = Model.Shapes[entry.Index];
                var def = ResolveLayerDef(shape.Layer);
                if (!def.Visible || !def.Selectable) continue; // gate 8: hidden/non-selectable never previewed

                var bb = LayoutGeometry.BboxOf(shape);
                if (bb.IsEmpty) continue;

                bool matches = leftToRight
                    ? bb.MinX >= marqueeBb.MinX && bb.MaxX <= marqueeBb.MaxX && bb.MinY >= marqueeBb.MinY && bb.MaxY <= marqueeBb.MaxY
                    : bb.Intersects(marqueeBb);
                if (matches) _marqueeHitsScratch.Add(entry.Index);
            }
            else
            {
                if (entry.Index < 0 || entry.Index >= Model.Instances.Count) continue;
                var bb = InstanceBboxFor(Model.Instances[entry.Index]);
                if (bb.IsEmpty) continue;

                bool matches = leftToRight
                    ? bb.MinX >= marqueeBb.MinX && bb.MaxX <= marqueeBb.MaxX && bb.MinY >= marqueeBb.MinY && bb.MaxY <= marqueeBb.MaxY
                    : bb.Intersects(marqueeBb);
                if (matches) _marqueeInstanceHitsScratch.Add(entry.Index);
            }
        }

        CombineMarqueePreview(_marqueePreview, _marqueeBaseSelection, _marqueeHitsScratch);
        CombineMarqueePreview(_marqueeInstancePreview, _marqueeBaseInstanceSelection, _marqueeInstanceHitsScratch);

        return _marqueePreview;
    }

    /// <summary>The Shift(add)/Ctrl(toggle)/plain(replace) combination against a base selection —
    /// factored out of the old single-kind <see cref="ComputeMarqueeSelection"/> body so it runs
    /// IDENTICALLY for shapes and instances (R-fix-3: "the … Shift/Ctrl combination against the base
    /// selection are unchanged"). Writes into <paramref name="preview"/> in place.</summary>
    private void CombineMarqueePreview(List<int> preview, List<int> baseSelection, List<int> hits)
    {
        preview.Clear();
        if (_marqueeToggle)
        {
            preview.AddRange(baseSelection);
            foreach (var h in hits)
            {
                int existing = preview.IndexOf(h);
                if (existing >= 0) preview.RemoveAt(existing);
                else preview.Add(h);
            }
        }
        else if (_marqueeAdd)
        {
            preview.AddRange(baseSelection);
            foreach (var h in hits)
                if (!preview.Contains(h)) preview.Add(h);
        }
        else
        {
            preview.AddRange(hits);
        }
    }

    private void UpdateMarqueeSelectionStatus()
    {
        int shapeCount = _marqueePreview.Count, instCount = _marqueeInstancePreview.Count;
        SelectionStatusText = (shapeCount, instCount) switch
        {
            (0, 0) => "",
            ( > 0, 0) => shapeCount == 1 ? "1 shape" : $"{shapeCount} shapes",
            (0, > 0) => instCount == 1 ? "1 instance" : $"{instCount} instances",
            _ => $"{shapeCount} shape{(shapeCount == 1 ? "" : "s")} + {instCount} instance{(instCount == 1 ? "" : "s")}",
        };
    }

    /// <summary>Escape (or any other abandonment of an in-progress marquee, e.g. the pointer button
    /// being released off-canvas) must clear both previews and restore the status readout WITHOUT
    /// touching <c>_selectedIndices</c>/<c>_selectedInstanceIndices</c> — neither field was ever
    /// written during the drag (R-L1i-2), so simply recomputing the combined status from them is
    /// correct.</summary>
    private void CancelMarqueeIfActive()
    {
        if (_selectDragKind != SelectDragKind.Marquee) return;
        _marqueePreview.Clear();
        _marqueeInstancePreview.Clear();
        _marqueeLastComputedCorner = null;
        SelectionStatusText = ComputeSelectionStatus();
    }

    private void HandleSelectMove(double wx, double wy, bool leftDown, KeyModifiers mods, long tolDbu, long pixelDbu = 0, long snapTolDbu = 0)
    {
        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);

        if (_cycleCache.HasStack)
        {
            long thresh = Math.Max(tolDbu, 1);
            if (Math.Abs(_cycleCache.ClickX - px) > thresh || Math.Abs(_cycleCache.ClickY - py) > thresh)
                _cycleCache.Clear();
        }

        if (_snapCycleCache.HasStack)
        {
            long snapThresh = Math.Max(snapTolDbu, 1);
            if (Math.Abs(_snapCycleCache.ClickX - px) > snapThresh || Math.Abs(_snapCycleCache.ClickY - py) > snapThresh)
                _snapCycleCache.Clear();
        }

        // brief-snap-distance-and-geometry-snap.md: recomputes the marker/target candidate for THIS
        // tick — during an idle hover this is the click-through marker (R-snp-8); during an active
        // Move drag with a snap grab in progress, it's the live TARGET the grab point is currently
        // attracted to (§2.3's "target" role), searched near the raw cursor so the grab point tracks
        // it exactly once found.
        UpdateSnapMarker(px, py, mods, snapTolDbu, pixelDbu);

        if (_pcellHandleDrag is not null)
        {
            if (!leftDown) { ResetPCellHandleDragState(); RebuildOverlay(); return; }
            UpdatePCellHandleDrag(px, py, (mods & KeyModifiers.Alt) != 0);
            return;
        }

        if (_scaleDragKind != ScaleDragKind.None)
        {
            if (!leftDown) { ResetScaleDragState(); RebuildOverlay(); return; }
            UpdateScaleDragPreview(px, py);
            return;
        }

        if (_handleDragKind != HandleDragKind.None)
        {
            if (!leftDown) { ResetHandleDragState(); RebuildOverlay(); return; }
            bool suspend = (mods & KeyModifiers.Alt) != 0;
            var preview = BuildHandleDragPreview(px, py, suspend);
            if (preview is not null) { _handleDragPreview = preview; _handleDragMoved = true; }
            RebuildOverlay();
            return;
        }

        if (!leftDown)
        {
            if (_selectDragKind != SelectDragKind.None)
            {
                CancelMarqueeIfActive();
                _selectDragKind = SelectDragKind.None;
                _moveLiveDx = 0; _moveLiveDy = 0; _moveHasMoved = false;
                _snapDragActive = false;
                _pointSnapDragActive = false;
            }
            // brief-geometry-snap-followups.md R-snpf-7/8: this is the plain-hover path (no button
            // down, no drag). UpdateSnapMarker above already ran the query and refreshed
            // _currentSnapCandidate for THIS cursor position — but RebuildOverlay() is what pushes it
            // into Overlay.SnapMarker for the renderer to actually draw. The ORIGINAL code only called
            // it inside the "a drag just ended" branch above, so a plain hover-only move recomputed the
            // candidate and then silently discarded it — the query ran (SnapQueryRunCount incremented)
            // but nothing was ever drawn. Call unconditionally so hover shows a marker exactly like
            // every other per-tick recompute in this method already does.
            RebuildOverlay();
            return;
        }

        switch (_selectDragKind)
        {
            case SelectDragKind.Move:
            {
                bool suspend = (mods & KeyModifiers.Alt) != 0;
                RecomputeMoveDelta(px, py, suspend);
                RebuildOverlay();
                break;
            }

            case SelectDragKind.Marquee:
            {
                _marqueeCurX = px; _marqueeCurY = py;

                // Perf: skip the O(shapes) recompute when the rectangle has not moved by at least one
                // device pixel — pointer moves arrive far faster than the rectangle meaningfully
                // changes (gate 9). pixelDbu <= 0 (the default, e.g. any caller/test that doesn't pass
                // it) always recomputes — a safe, conservative fallback.
                long thresh = Math.Max(pixelDbu, 0);
                bool moved = _marqueeLastComputedCorner is not { } last
                    || Math.Abs(last.X - px) >= thresh || Math.Abs(last.Y - py) >= thresh;

                if (moved)
                {
                    ComputeMarqueeSelection(px, py);
                    _marqueeLastComputedCorner = (px, py);
                    UpdateMarqueeSelectionStatus();
                }
                RebuildOverlay();
                break;
            }
        }
    }

    private void HandleSelectRelease(double wx, double wy)
    {
        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);

        if (_pcellHandleDrag is not null)
        {
            CommitPCellHandleDrag();
            RebuildOverlay();
            return;
        }

        if (_scaleDragKind != ScaleDragKind.None)
        {
            CommitScaleDragFromPointer();
            RebuildOverlay();
            return;
        }

        if (_handleDragKind != HandleDragKind.None)
        {
            CommitHandleDrag();
            RebuildOverlay();
            return;
        }

        switch (_selectDragKind)
        {
            case SelectDragKind.Move:
                CommitMoveDrag();
                break;
            case SelectDragKind.Marquee:
                CommitMarquee(px, py);
                break;
        }

        _selectDragKind = SelectDragKind.None;
        RebuildOverlay();
    }

    /// <summary>brief-snap-distance-and-geometry-snap.md §2.7/R-snp-7: the Move-drag delta computation,
    /// factored out of <c>HandleSelectMove</c>'s own switch case so <c>RecomputeSnapStateImmediate</c>
    /// (Snap.cs) can re-run it when a toggle flips mid-drag — a toggle must update the COMMITTED live
    /// delta, not just the rendered marker, or "recomputes immediately" would only be true visually.
    /// Target role (§2.3) lands the grab point exactly on the currently-attracted feature, overriding
    /// grid snap for this tick; otherwise falls back to ordinary grid-snapped delta.
    /// <para/>
    /// brief-snap-combobox-and-consistency.md R-cmb-4/5: gated on <see cref="_snapCandidateIsRealTarget"/>,
    /// NOT merely on <c>_currentSnapCandidate is not null</c> — the owner-follow-up "marker stays
    /// visible throughout a grab-role drag" fix made <see cref="_currentSnapCandidate"/> hold a
    /// SYNTHETIC echo of the originally-grabbed feature (tracking the raw, unsnapped cursor) whenever
    /// nothing real is nearby, so a candidate is non-null for the ENTIRE grab-role drag regardless of
    /// whether anything is actually in range. Using that alone here made the absolute-position branch
    /// win for the whole gesture, permanently defeating grid snap — geometry snap must override grid
    /// snap only when it genuinely has a real feature to offer, never merely because the mode is
    /// enabled. <see cref="_snapCandidateIsRealTarget"/> is true ONLY for a real
    /// <see cref="LayoutSnapQuery.FindCandidates"/> hit, never for the synthetic marker-persistence
    /// fallback, so the two concerns (what to DRAW vs. what to SNAP the position to) stay independent.</summary>
    private void RecomputeMoveDelta(long px, long py, bool suspend)
    {
        long dx, dy;
        if ((_snapDragActive || _pointSnapDragActive) && !suspend
            && _snapCandidateIsRealTarget && _currentSnapCandidate is { } target)
        {
            dx = target.X - _moveAnchorX;
            dy = target.Y - _moveAnchorY;
        }
        else
        {
            (dx, dy) = GridSnappedDrag(px, py, suspend);
        }
        if (dx != _moveLiveDx || dy != _moveLiveDy) _moveHasMoved = true;
        _moveLiveDx = dx; _moveLiveDy = dy;
    }

    /// <summary>R-L1c-3's own rule in one place: a move snaps the DELTA, never each coordinate — so an
    /// off-grid shape keeps its internal geometry. Shared by <see cref="RecomputeMoveDelta"/> (what the
    /// drag actually commits) and by the snap marker's grab-role echo (where the grabbed feature now
    /// IS), so the marker and the geometry cannot disagree about where the grab point ended up.</summary>
    private (long Dx, long Dy) GridSnappedDrag(long px, long py, bool suspend) =>
        (LayoutSnapping.SnapValue(px - _moveAnchorX, Model.SnapDbu, suspend),
         LayoutSnapping.SnapValue(py - _moveAnchorY, Model.SnapDbu, suspend));

    /// <summary>Where the feature grabbed at <see cref="_moveAnchorX"/>/<see cref="_moveAnchorY"/> has
    /// been moved to by this tick, under the same grid snap the commit will use.</summary>
    internal (long X, long Y) SnappedGrabPoint(long px, long py)
    {
        var (dx, dy) = GridSnappedDrag(px, py, suspend: false);
        return (_moveAnchorX + dx, _moveAnchorY + dy);
    }

    /// <summary>brief-L3a-followups.md §2/R-fix-2: moves whichever of shapes/instances are currently
    /// selected, TOGETHER, as one undo entry — a <see cref="CompositeCommand"/> of
    /// <see cref="Commands.Layout.MoveShapesCommand"/> and <see cref="Commands.Layout.MoveInstancesCommand"/>
    /// when both kinds are present, or just the one command when only one kind is. This replaced two
    /// separate methods (one per kind) that could never move a mixed selection as a single gesture.</summary>
    private void CommitMoveDrag()
    {
        if (_moveHasMoved && (_moveLiveDx != 0 || _moveLiveDy != 0))
        {
            IUiCommand? combined = null;
            var shapeIndices = MovableSelectedIndices(_selectedIndices);
            if (shapeIndices.Count > 0)
                combined = new Commands.Layout.MoveShapesCommand(Model, shapeIndices, _moveLiveDx, _moveLiveDy);
            if (_selectedInstanceIndices.Count > 0)
            {
                IUiCommand instCmd = new Commands.Layout.MoveInstancesCommand(Model, _selectedInstanceIndices, _moveLiveDx, _moveLiveDy);
                combined = combined is null ? instCmd : new CompositeCommand(combined, instCmd);
            }
            if (combined is not null) Execute(combined);
        }
        _moveLiveDx = 0; _moveLiveDy = 0; _moveHasMoved = false;
        _snapDragActive = false;
        _pointSnapDragActive = false;
    }

    /// <summary>R-L1i-1, extended by R-fix-3: commit is just "settle on whatever the shared compute
    /// says," via the exact same function the live preview calls — so the preview can never lie about
    /// the outcome (gate 3), now for both kinds. <see cref="ReplaceMixedSelection"/> sets shapes and
    /// instances together, atomically, so a mixed marquee result lands as ONE selection change (one
    /// overlay rebuild), not two independent ones that could each fire their own stale intermediate
    /// state.</summary>
    private void CommitMarquee(long releaseX, long releaseY)
    {
        ComputeMarqueeSelection(releaseX, releaseY);
        ReplaceMixedSelection(_marqueePreview, _marqueeInstancePreview);
        _cycleCache.Clear();
        _marqueePreview.Clear();
        _marqueeInstancePreview.Clear();
        _marqueeLastComputedCorner = null;
    }

    /// <summary>Sets BOTH selections at once — the one place besides a plain click that commits a
    /// genuinely MIXED result (brief-L3a-followups.md §2/R-fix-2). Neither list "clears the other
    /// kind" here; this method IS both kinds' new state, together, in one overlay rebuild — unlike
    /// <see cref="SetSelection"/>/<see cref="SetInstanceSelection"/>, which each only ever own one
    /// kind and treat the other as either "clear it" or "leave it alone."</summary>
    private void ReplaceMixedSelection(IEnumerable<int> shapeIndices, IEnumerable<int> instanceIndices)
    {
        var shapes = new List<int>();
        foreach (var i in shapeIndices) if (i >= 0 && i < Model.Shapes.Count && !shapes.Contains(i)) shapes.Add(i);
        var insts = new List<int>();
        foreach (var i in instanceIndices) if (i >= 0 && i < Model.Instances.Count && !insts.Contains(i)) insts.Add(i);

        _selectedIndices.Clear(); _selectedIndices.AddRange(shapes);
        _selectedInstanceIndices.Clear(); _selectedInstanceIndices.AddRange(insts);
        _pickedVertexIndex = null;

        SelectionStatusText = ComputeSelectionStatus();
        RebuildOverlay();
    }

    /// <summary>brief-L3a-followups.md §2/R-fix-2: removes whichever of shapes/instances are currently
    /// selected, TOGETHER, as one undo entry.</summary>
    /// <summary>
    /// Deletes the current shape and instance selection — the public face of <see cref="DeleteSelection"/>.
    ///
    /// <para>Exists so a HOST editor (the wBond editor, whose Cut spans wires and geometry together)
    /// can delete the geometry half through the same command and the same undo entry the Delete key
    /// uses, rather than reaching for a second removal path.</para>
    /// </summary>
    public void DeleteSelectedGeometry() => DeleteSelection();

    private void DeleteSelection()
    {
        var shapeIndices = _selectedIndices.ToList();
        var instIndices = _selectedInstanceIndices.ToList();
        if (shapeIndices.Count == 0 && instIndices.Count == 0) return;

        IUiCommand? combined = null;
        if (shapeIndices.Count > 0) combined = new Commands.Layout.DeleteShapesCommand(Model, shapeIndices);
        if (instIndices.Count > 0)
        {
            IUiCommand instCmd = new Commands.Layout.DeleteInstancesCommand(Model, instIndices);
            combined = combined is null ? instCmd : new CompositeCommand(combined, instCmd);
        }
        Execute(combined!);
        SetSelection([]);
        _cycleCache.Clear();
    }

    /// <summary>§3 "Delete on a selected vertex" — blocked below 3 vertices for a closed shape, below
    /// 2 for a Path (<see cref="LayoutShapeEditing.RemoveVertex"/> returns null in that case and this
    /// is a no-op, matching gate 7). One <see cref="Commands.Layout.ReplaceShapeCommand"/>. Public so
    /// the canvas's right-click "Delete Vertex" menu item (an explicit alternative to the invisible
    /// click-to-pick-then-press-Delete gesture) can call it directly.</summary>
    public void DeleteVertex(int shapeIndex, int vertexIndex)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return;
        var shape = Model.Shapes[shapeIndex];
        var after = LayoutShapeEditing.RemoveVertex(shape, vertexIndex);
        if (after is null) return;
        Execute(new Commands.Layout.ReplaceShapeCommand(Model, shapeIndex, shape, after));
    }

    /// <summary>brief-L3a-followups.md §2/R-fix-2: nudges whichever of shapes/instances are currently
    /// selected, TOGETHER, as one undo entry per keypress (unchanged — one keypress is still one undo
    /// entry, exactly as before; only "which kinds move together" changed).</summary>
    private void NudgeSelection(Key key, KeyModifiers mods)
    {
        if (_selectedIndices.Count == 0 && _selectedInstanceIndices.Count == 0) return;
        long step = OneSnapStepDbu;
        if ((mods & KeyModifiers.Shift) != 0) step *= 10;

        long dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
        long dy = key switch { Key.Up => step, Key.Down => -step, _ => 0 };
        if (dx == 0 && dy == 0) return;

        IUiCommand? combined = null;
        var shapeIndices = MovableSelectedIndices(_selectedIndices);
        if (shapeIndices.Count > 0) combined = new Commands.Layout.MoveShapesCommand(Model, shapeIndices, dx, dy);
        if (_selectedInstanceIndices.Count > 0)
        {
            IUiCommand instCmd = new Commands.Layout.MoveInstancesCommand(Model, _selectedInstanceIndices, dx, dy);
            combined = combined is null ? instCmd : new CompositeCommand(combined, instCmd);
        }
        if (combined is not null) Execute(combined);
    }

    // ── Current layer (session state — deliberately NOT persisted in .clay; §2 of the brief) ────

    public ObservableCollection<LayerPickerItem> AvailableLayers { get; } = [];

    [ObservableProperty] private LayerKey _currentLayerKey;

    /// <summary>Two-way bound to the layer ComboBox's SelectedItem. Setting <see cref="CurrentLayerKey"/>
    /// directly (e.g. from a test) is also fine — both stay in sync via the partial-changed hooks.</summary>
    [ObservableProperty] private LayerPickerItem? _currentLayerItem;

    partial void OnCurrentLayerItemChanged(LayerPickerItem? value)
    {
        if (value is not null) CurrentLayerKey = value.Key;
    }

    /// <summary>Repopulates <see cref="AvailableLayers"/> from <see cref="Technology"/> (ordered by
    /// ZOrder) or a small fixed fallback set (1/0 … 4/0) when there is no technology — gate 11.
    /// Called at construction and whenever <see cref="Technology"/> changes (L0c's live seam).
    /// Keeps the current selection if its key still exists; otherwise falls back to the first
    /// layer, never throwing.</summary>
    private void RebuildAvailableLayers()
    {
        var previous = CurrentLayerKey;
        AvailableLayers.Clear();

        if (Technology is { Layers.Count: > 0 } tech)
        {
            foreach (var l in tech.Layers.OrderBy(l => l.ZOrder))
                AvailableLayers.Add(new LayerPickerItem(l.Key, l.Name, l.Color));
        }
        else
        {
            for (int i = 1; i <= 4; i++)
            {
                var key = new LayerKey(i, 0);
                var fallback = FallbackPalette.For(key);
                AvailableLayers.Add(new LayerPickerItem(key, fallback.Name, fallback.Color));
            }
        }

        var match = AvailableLayers.FirstOrDefault(l => l.Key == previous) ?? AvailableLayers.FirstOrDefault();
        CurrentLayerItem = match;
        if (match is not null) CurrentLayerKey = match.Key;
    }

    // ── Toolbar fields — staged text, parsed via LayoutUnits.TryParse (§1 R6, gate 8) ───────────
    // Invalid text reverts to the current value and never throws.

    private long _pathWidthDbu = 10_000;    // arbitrary reasonable default (10 um at 1000 dbu/um)
    [ObservableProperty] private string _pathWidthText = "";

    public void CommitPathWidthText(string text)
    {
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu) && dbu > 0)
            _pathWidthDbu = dbu;
        PathWidthText = LayoutUnits.Format(_pathWidthDbu, DisplayUnit, Model.DbuPerMicron);
    }

    [ObservableProperty] private PathEndStyle _currentPathEndStyle = PathEndStyle.Flush;

    public static IReadOnlyList<PathEndStyle> AllPathEndStyles { get; } = Enum.GetValues<PathEndStyle>();

    private long _cornerRadiusDbu;          // default 0 — a plain rect until the user sets one
    [ObservableProperty] private string _cornerRadiusText = "";

    public void CommitCornerRadiusText(string text)
    {
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu) && dbu >= 0)
            _cornerRadiusDbu = dbu;
        CornerRadiusText = LayoutUnits.Format(_cornerRadiusDbu, DisplayUnit, Model.DbuPerMicron);
    }

    // R-lbl-1: this hardcoded value is ONLY the no-technology-resolved fallback now — the real default
    // comes from Technology.DefaultLabelHeightDbu, seeded once in ApplyTechResolution (see there for why
    // a sub-pixel-on-PCB constant was the actual bug, not the label pipeline itself).
    private long _labelHeightDbu = 5_000;   // fallback: 5 um at 1000 dbu/um
    private bool _labelHeightSeededFromTech;
    [ObservableProperty] private string _labelHeightText = "";

    public void CommitLabelHeightText(string text)
    {
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu) && dbu > 0)
            _labelHeightDbu = dbu;
        LabelHeightText = LayoutUnits.Format(_labelHeightDbu, DisplayUnit, Model.DbuPerMicron);
    }

    private void RefreshTypedFieldDisplays()
    {
        PathWidthText    = LayoutUnits.Format(_pathWidthDbu, DisplayUnit, Model.DbuPerMicron);
        CornerRadiusText = LayoutUnits.Format(_cornerRadiusDbu, DisplayUnit, Model.DbuPerMicron);
        LabelHeightText  = LayoutUnits.Format(_labelHeightDbu, DisplayUnit, Model.DbuPerMicron);
    }

    // ── Live rect W/H — editable during a live Rect drag; typing a value commits (gate 9) ──────

    [ObservableProperty] private string _drawWidthText = "";
    [ObservableProperty] private string _drawHeightText = "";
    [ObservableProperty] private string _drawReadoutText = "";

    private long? _typedWidthDbu;
    private long? _typedHeightDbu;

    /// <summary>Stages a typed width override for the live Rect drag. Does not finalize the
    /// shape — call <see cref="CommitTypedRect"/> (the field's Enter-key handler) to do that.</summary>
    public void CommitDrawWidthText(string text)
    {
        if (!_isDrawingTwoPoint || ActiveTool != Tool.Rect) return;
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu) && dbu > 0)
            _typedWidthDbu = dbu;
        RebuildOverlay();
    }

    public void CommitDrawHeightText(string text)
    {
        if (!_isDrawingTwoPoint || ActiveTool != Tool.Rect) return;
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu) && dbu > 0)
            _typedHeightDbu = dbu;
        RebuildOverlay();
    }

    /// <summary>Finalizes the live Rect drag at exactly the staged W/H (falling back to the live
    /// drag's current size for whichever axis was not typed) — regardless of where the pointer
    /// currently is. One undo entry, same as a normal press/drag/release.</summary>
    public void CommitTypedRect()
    {
        if (!_isDrawingTwoPoint || ActiveTool != Tool.Rect) return;
        FinishTwoPointDraw();
    }

    // ── Overlay (the in-progress draw ghost) ──────────────────────────────────

    [ObservableProperty] private LayoutOverlay _overlay = LayoutOverlay.Empty;

    // ── Gesture state ─────────────────────────────────────────────────────────

    private bool _isDrawingTwoPoint;
    private long _drawP1X, _drawP1Y, _drawP2X, _drawP2Y;

    // Unsnapped (raw, rounded-to-DBU only) press/current points — tracked alongside the snapped
    // _drawP1/_drawP2 pair purely so BuildTwoPointShape can tell "the pointer genuinely didn't move
    // on this axis" apart from "the pointer moved but the snap grid collapsed it to the same cell"
    // (docs/sonnet-briefs/brief-L1-fix-clear-and-default-zoom.md, Bug 2 item 2) — never used for
    // anything that lands in the model; only the snapped/typed coordinates are ever drawn or stored.
    private long _drawP1RawX, _drawP1RawY, _drawP2RawX, _drawP2RawY;

    /// <summary>One snap step in DBU, used as the minimum-size fallback when a real (non-degenerate)
    /// drag collapses to nothing after snapping — never returns 0 (that would be "no minimum size").</summary>
    private long OneSnapStepDbu => Model.SnapDbu > 0 ? Model.SnapDbu : 1;

    private readonly List<(long X, long Y)> _drawPoints = [];
    private long _drawCurX, _drawCurY;

    private bool _isTypingLabel;
    private long _labelAnchorX, _labelAnchorY;
    private string _labelBuffer = "";

    /// <summary>R-lbl-2: the zoom (device px per DBU) captured when Label typing started, used only to
    /// note in the typing status hint when the label is smaller than the current zoom can show. 0 means
    /// unknown (the caller didn't pass it) — the note is simply skipped, never a divide-by-zero.</summary>
    private double _labelZoomPxPerDbu;

    /// <summary>R-lbl-3 (docs/sonnet-briefs/brief-layout-label-fix-and-text-flatten.md): the canvas
    /// reads this to suspend the Space-arms-pan-modifier gesture while a label is being typed — Space
    /// is an ordinary character in label text, not a pan trigger, while <see cref="_isTypingLabel"/>
    /// is true.</summary>
    public bool IsTypingLabel => _isTypingLabel;

    /// <summary>The label height (DBU) that will be used for the NEXT committed label — test/tooling
    /// visibility into the R-lbl-1 default (technology-seeded, or the 5 µm fallback).</summary>
    public long CurrentLabelHeightDbu => _labelHeightDbu;

    /// <summary>True while any drawing gesture is in progress — the view uses this (or checks
    /// <see cref="ActiveTool"/>) to decide whether the live W/H fields should be enabled.</summary>
    public bool IsDrawingRect => _isDrawingTwoPoint && ActiveTool == Tool.Rect;

    // ── Pointer handlers — filled from LayoutCanvas (L1a's marked seam) ─────────────────────────

    /// <summary><paramref name="hitTolDbu"/> is the ~4-screen-pixel hit tolerance already converted
    /// to DBU by the caller using the CURRENT zoom (never cached, never derived from <c>SnapDbu</c> —
    /// see the brief's "Read first" section). Only meaningful when <see cref="ActiveTool"/> is
    /// <see cref="Tool.Select"/>; every drawing tool ignores it. <paramref name="zoomPxPerDbu"/> (R-lbl-2,
    /// docs/sonnet-briefs/brief-layout-label-fix-and-text-flatten.md) is the CURRENT device-pixels-
    /// per-DBU zoom, used only by the Label tool to note in the typing status hint when the label is
    /// smaller than the current zoom can show — 0 (the default) skips that note. <paramref
    /// name="snapTolDbu"/> (docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md R-snp-15) is
    /// geometry snap's own screen-pixel tolerance, already converted to DBU by the caller from the
    /// CURRENT zoom — a deliberately separate constant from <paramref name="hitTolDbu"/>, never
    /// cached, never derived from <c>SnapDbu</c>. 0 (the default) means "no geometry-snap query."</summary>
    public void OnPointerPressed(double wx, double wy, KeyModifiers mods, int clickCount = 1, long hitTolDbu = 0, double zoomPxPerDbu = 0, long snapTolDbu = 0)
    {
        // L1f: a paste placement in progress takes priority over every other gesture — a click
        // commits it, regardless of the currently active drawing tool.
        if (_pastePlacementShapes is not null) { CommitPastePlacement(); return; }

        if (ActiveTool == Tool.Select) { HandleSelectPress(wx, wy, mods, Math.Max(hitTolDbu, 0), Math.Max(snapTolDbu, 0)); return; }

        if (ActiveTool == Tool.Instance) { CommitInstancePlacement(); return; }

        if (ActiveTool == Tool.Via) { CommitViaPlacement(wx, wy, mods); return; }

        if (ActiveTool == Tool.Port) { CommitPortPlacement(wx, wy, mods, zoomPxPerDbu, Math.Max(snapTolDbu, 0)); return; }

        bool suspend = (mods & KeyModifiers.Alt) != 0;

        if (IsTwoPointDragTool(ActiveTool))
        {
            var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend);
            _isDrawingTwoPoint = true;
            _drawP1X = sx; _drawP1Y = sy;
            _drawP2X = sx; _drawP2Y = sy;
            _drawP1RawX = (long)Math.Round(wx); _drawP1RawY = (long)Math.Round(wy);
            _drawP2RawX = _drawP1RawX; _drawP2RawY = _drawP1RawY;
            _typedWidthDbu  = null;
            _typedHeightDbu = null;
            OnPropertyChanged(nameof(IsDrawingRect));
            RebuildOverlay();
            return;
        }

        if (IsMultiPointTool(ActiveTool))
        {
            if (clickCount >= 2) { FinishMultiPointDraw(); return; }

            (long X, long Y) pt = _drawPoints.Count == 0
                ? LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend)
                : LayoutSnapping.ConstrainAndSnap(_drawPoints[^1].X, _drawPoints[^1].Y, wx, wy, Model.AngleMode, Model.SnapDbu, suspend);

            _drawPoints.Add(pt);
            _drawCurX = pt.X; _drawCurY = pt.Y;
            RebuildOverlay();
            return;
        }

        if (ActiveTool == Tool.Label)
        {
            var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend);
            _isTypingLabel = true;
            _labelAnchorX  = sx;
            _labelAnchorY  = sy;
            _labelBuffer   = "";
            _labelZoomPxPerDbu = zoomPxPerDbu;
            RebuildOverlay();
        }
    }

    /// <summary><paramref name="pixelDbu"/>: the world-space size of one device pixel at the CURRENT
    /// zoom, in DBU (0 — the default — always recomputes; used only by the Select tool's marquee drag
    /// to skip the O(shapes) recompute for sub-pixel pointer moves, per L1i's perf note). <paramref
    /// name="snapTolDbu"/> is geometry snap's own screen-pixel tolerance, converted to DBU by the
    /// caller from the CURRENT zoom (R-snp-15) — 0 (the default) disables the geometry-snap marker/
    /// target query for this call.</summary>
    public void OnPointerMoved(double wx, double wy, bool leftDown, KeyModifiers mods, long hitTolDbu = 0, long pixelDbu = 0, long snapTolDbu = 0)
    {
        if (_pastePlacementShapes is not null)
        {
            bool pasteSuspend = (mods & KeyModifiers.Alt) != 0;
            UpdatePastePlacementCursor(wx, wy, pasteSuspend);
            return;
        }

        if (ActiveTool == Tool.Select) { HandleSelectMove(wx, wy, leftDown, mods, Math.Max(hitTolDbu, 0), Math.Max(pixelDbu, 0), Math.Max(snapTolDbu, 0)); return; }

        if (ActiveTool == Tool.Instance)
        {
            bool instanceSuspend = (mods & KeyModifiers.Alt) != 0;
            UpdateInstancePlacementCursor(wx, wy, instanceSuspend);
            return;
        }

        // The Port tool snaps to geometry (see CommitPortPlacement), so the marker has to be live
        // while hovering — otherwise the point the click lands on is invisible until after the fact.
        if (ActiveTool == Tool.Port)
        {
            // TryBuildPortPlacement runs UpdateSnapMarker itself (it has to — the snap candidate is
            // what decides where the port lands), so the marker stays live for free and the ghost is
            // built from the same answer. A null result means "off metal": the ghost vanishes while
            // the snap marker stays, which is the affordance for "clicking here creates nothing".
            _portGhost = TryBuildPortPlacement(wx, wy, mods, Math.Max(snapTolDbu, 0),
                                               pixelDbu > 0 ? 1.0 / pixelDbu : 0);
            RebuildOverlay();
            return;
        }

        bool suspend = (mods & KeyModifiers.Alt) != 0;

        if (_isDrawingTwoPoint)
        {
            if (!leftDown) { CancelDrawOp(); return; }
            var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend);
            _drawP2X = sx; _drawP2Y = sy;
            _drawP2RawX = (long)Math.Round(wx); _drawP2RawY = (long)Math.Round(wy);
            RebuildOverlay();
            return;
        }

        if (_drawPoints.Count > 0)
        {
            var pt = LayoutSnapping.ConstrainAndSnap(_drawPoints[^1].X, _drawPoints[^1].Y, wx, wy, Model.AngleMode, Model.SnapDbu, suspend);
            _drawCurX = pt.X; _drawCurY = pt.Y;
            RebuildOverlay();
        }
    }

    public void OnPointerReleased(double wx, double wy, KeyModifiers mods)
    {
        if (ActiveTool == Tool.Select) { HandleSelectRelease(wx, wy); return; }

        if (!_isDrawingTwoPoint) return;
        bool suspend = (mods & KeyModifiers.Alt) != 0;
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend);
        _drawP2X = sx; _drawP2Y = sy;
        _drawP2RawX = (long)Math.Round(wx); _drawP2RawY = (long)Math.Round(wy);
        FinishTwoPointDraw();
    }

    public void OnTextInput(string text)
    {
        if (!_isTypingLabel || string.IsNullOrEmpty(text)) return;
        string printable = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (printable.Length == 0) return;
        _labelBuffer += printable;
        RebuildOverlay();
    }

    public void OnKeyDown(Key key, KeyModifiers mods)
    {
        if (_pastePlacementShapes is not null)
        {
            if (key == Key.Escape) CancelPastePlacement();
            return;
        }

        if (_isTypingLabel)
        {
            if (key == Key.Escape) { CancelDrawOp(); ActiveTool = Tool.Select; return; }
            if (key == Key.Enter || key == Key.Return) { CommitLabel(); return; }
            if (key == Key.Back && _labelBuffer.Length > 0) { _labelBuffer = _labelBuffer[..^1]; RebuildOverlay(); }
            return;
        }

        // brief-snap-distance-and-geometry-snap.md — R-snp-1: F9 toggles this document's own snap
        // distance on/off (AutoCAD's grid-snap toggle). R-snp-6: F3 and 's' both toggle geometry snap
        // (confirmed free — no existing single-letter tool shortcut in this editor). None of these are
        // gated on ActiveTool — they are view toggles, not tool state.
        if (key == Key.F9) { ToggleSnapDbuEnabled(); return; }
        if (key == Key.F3 || key == Key.S) { GeometrySnapEnabled = !GeometrySnapEnabled; return; }

        // Mirrors SymbolEditorViewModel.OnKeyDown's Escape contract exactly: any in-progress
        // operation (a non-Select tool being active counts as one) cancels and switches to Select;
        // only when genuinely idle ON the Select tool does Escape clear the selection instead.
        // CancelDrawOp() must be called explicitly here (not left to OnActiveToolChanged alone) —
        // when ActiveTool is ALREADY Select but a marquee/move drag is in progress, assigning
        // `ActiveTool = Tool.Select` is a no-op (same value) and its partial change handler never
        // fires, so relying on that alone would silently leave the drag running.
        if (key == Key.Escape)
        {
            // L1h R-L1h-5: Scale mode (no drag in progress) is its own escape-able state — Escape
            // exits it and restores L1d's single-shape handles, exactly like leaving a drawing tool,
            // WITHOUT also clearing the selection (a mode toggle isn't a destructive operation).
            if (_scaleDragKind == ScaleDragKind.None && ScaleModeActive) { ScaleModeActive = false; return; }

            bool hasActiveOp = ActiveTool != Tool.Select
                             || _isDrawingTwoPoint
                             || _drawPoints.Count > 0
                             || _selectDragKind != SelectDragKind.None
                             || _handleDragKind != HandleDragKind.None
                             || _scaleDragKind != ScaleDragKind.None;
            if (hasActiveOp) { CancelDrawOp(); ActiveTool = Tool.Select; }
            else { SetSelection([]); _cycleCache.Clear(); }
            return;
        }

        if (ActiveTool == Tool.Select && _selectDragKind == SelectDragKind.None && _handleDragKind == HandleDragKind.None)
        {
            bool ctrlOrMeta = (mods & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (ctrlOrMeta && key == Key.A) { SelectAllCommand.Execute(null); return; }

            // R / Shift+R rotate the selection, same keys and same sense as the Schematic Editor.
            // Guarded on ctrlOrMeta being false so Ctrl/Cmd+R (Run) is never stolen.
            if (!ctrlOrMeta && key == Key.R)
            {
                RotateSelection(clockwise: mods.HasFlag(KeyModifiers.Shift));
                return;
            }

            // M / Shift+M mirror horizontally / vertically — again the Schematic Editor's own keys.
            if (!ctrlOrMeta && key == Key.M)
            {
                MirrorSelection(horizontal: !mods.HasFlag(KeyModifiers.Shift));
                return;
            }
            if (key == Key.Delete || key == Key.Back)
            {
                // §3 "Delete on a selected vertex" takes priority over whole-selection delete when a
                // vertex handle was the last thing clicked (no drag) on the single selection — only
                // ever set with no instance mixed in (TryHandleSelectPressOnHandles's own gate).
                if (_selectedIndices.Count == 1 && _selectedInstanceIndices.Count == 0 && _pickedVertexIndex is { } vIdx)
                { DeleteVertex(_selectedIndices[0], vIdx); return; }
                // brief-L3a-followups.md §2/R-fix-2: DeleteSelection now removes shapes AND instances
                // together as one undo entry — no more separate per-kind dispatch here.
                DeleteSelection(); return;
            }
            if (key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                NudgeSelection(key, mods); return;
            }
        }

        if (key == Key.Back && _drawPoints.Count > 0)
        {
            _drawPoints.RemoveAt(_drawPoints.Count - 1);
            if (_drawPoints.Count > 0) { _drawCurX = _drawPoints[^1].X; _drawCurY = _drawPoints[^1].Y; }
            RebuildOverlay();
            return;
        }

        if (key == Key.Enter || key == Key.Return)
        {
            if (_drawPoints.Count > 0) FinishMultiPointDraw();
        }
    }

    /// <summary>Escape — leaves the model untouched and clears the overlay (gate 10). Restores the
    /// original shape for an in-progress handle drag (gate 11: "restores the original shape and
    /// pushes no command") — the drag never mutated anything in the model, only the live preview.</summary>
    private void CancelDrawOp()
    {
        _isDrawingTwoPoint = false;
        _drawPoints.Clear();
        _isTypingLabel   = false;
        _labelBuffer     = "";
        _typedWidthDbu   = null;
        _typedHeightDbu  = null;
        _portGhost       = null;
        CancelMarqueeIfActive(); // gate 7: leaves _selectedIndices untouched, clears the preview
        _selectDragKind  = SelectDragKind.None;
        _moveLiveDx = 0; _moveLiveDy = 0; _moveHasMoved = false;
        _snapDragActive = false;
        _pointSnapDragActive = false;
        ResetHandleDragState();
        ResetScaleDragState();
        // Escape mid-drag: nothing committed, no undo entry — the parameters were never touched,
        // because a handle drag only ever writes on release.
        ResetPCellHandleDragState();
        CancelInstancePlacement();
        OnPropertyChanged(nameof(IsDrawingRect));
        RebuildOverlay();
    }

    private void FinishTwoPointDraw()
    {
        var shape = BuildTwoPointShape(_drawP1X, _drawP1Y, _drawP2X, _drawP2Y, _typedWidthDbu, _typedHeightDbu,
            _drawP2RawX - _drawP1RawX, _drawP2RawY - _drawP1RawY);
        _isDrawingTwoPoint = false;
        _typedWidthDbu  = null;
        _typedHeightDbu = null;
        if (shape is not null) Execute(new AddShapeCommand(Model, shape));
        OnPropertyChanged(nameof(IsDrawingRect));
        RebuildOverlay();
    }

    private void FinishMultiPointDraw()
    {
        int minPts = ActiveTool == Tool.Polygon ? 3 : 2;
        if (_drawPoints.Count >= minPts)
        {
            var shape = BuildMultiPointShape(_drawPoints);
            if (shape is not null) Execute(new AddShapeCommand(Model, shape));
        }
        _drawPoints.Clear();
        RebuildOverlay();
    }

    private void CommitLabel()
    {
        if (!string.IsNullOrWhiteSpace(_labelBuffer))
        {
            // Owner report: a label could still "disappear" the instant Enter was pressed — the
            // in-progress ghost was already boosted to a visible minimum (R-lbl-2), but the COMMITTED
            // shape used the raw technology/fallback default unboosted, which can still be sub-pixel at
            // a zoomed-out view even though it's a perfectly sensible size at the technology's own
            // typical zoom. The committed height now gets the SAME visibility floor as the ghost,
            // using the zoom captured when typing started — never retroactive, never touches an
            // existing label, and a no-op when the caller didn't supply a zoom (matches every prior
            // caller/test that doesn't pass one).
            long height = LayoutRenderer.EffectiveVisibleLabelHeightDbu(_labelHeightDbu, _labelZoomPxPerDbu);
            var shape = new LabelShape
            {
                Layer    = CurrentLayerKey,
                X        = _labelAnchorX,
                Y        = _labelAnchorY,
                Text     = _labelBuffer,
                Height   = height,
                Rotation = LayoutRotation.R0,
                IsPort   = false,   // port placement belongs with the EM work, not here
            };
            Execute(new AddShapeCommand(Model, shape));
        }
        _isTypingLabel = false;
        _labelBuffer   = "";
        RebuildOverlay();
    }

    /// <summary>R-lbl-2: the "Typing label…" hint shown in the toolbar's <see cref="DrawReadoutText"/>
    /// readout while <see cref="_isTypingLabel"/> — appears on the first keypress/click that arms
    /// typing and clears the instant <see cref="CommitLabel"/> or <see cref="CancelDrawOp"/> runs
    /// (both flip <c>_isTypingLabel</c> false then call <see cref="RebuildOverlay"/>, so there is no
    /// separate clear path to keep in sync). Notes when the label is smaller than the zoom captured at
    /// typing-start can show — <see cref="_labelZoomPxPerDbu"/> is 0 (skip the note) whenever the
    /// caller didn't supply it.</summary>
    private string BuildLabelTypingStatus()
    {
        const string Hint = "Typing label — Enter to commit, Esc to cancel";
        if (_labelZoomPxPerDbu <= 0) return Hint;

        double pixelHeight = _labelHeightDbu * _labelZoomPxPerDbu;
        return pixelHeight < LayoutRenderer.MinVisibleLabelDevicePixels
            ? Hint + " (smaller than the current zoom can show)"
            : Hint;
    }

    // ── Shape builders ─────────────────────────────────────────────────────────

    /// <summary>Expands a snap-collapsed rect axis to one snap step when the RAW (unsnapped) drag on
    /// that axis was non-zero — i.e. the pointer genuinely moved there, and the snap grid alone ate
    /// the extent (Bug 2 item 2: a huge snap step relative to a sane-but-small viewport made this the
    /// common case, not a rare one). Returns false (leave collapsed) when the raw axis delta really
    /// is zero — a straight-line drag or a stationary click must not fabricate a dimension the
    /// pointer never moved through.</summary>
    private bool TryExpandDegenerateAxis(ref long a1, ref long a2, long rawDelta)
    {
        if (a1 != a2) return true;         // not collapsed — nothing to do
        if (rawDelta == 0) return false;   // genuinely no movement on this axis
        a2 = a1 + Math.Sign(rawDelta) * OneSnapStepDbu;
        return true;
    }

    private LayoutShape? BuildTwoPointShape(long x1, long y1, long x2, long y2, long? typedW, long? typedH, long rawDx, long rawDy)
    {
        switch (ActiveTool)
        {
            case Tool.Rect:
            {
                long rx1, ry1, rx2, ry2;
                if (typedW is not null || typedH is not null)
                {
                    long w = typedW ?? Math.Abs(x2 - x1);
                    long h = typedH ?? Math.Abs(y2 - y1);
                    if (w <= 0 || h <= 0) return null;
                    rx1 = x1; ry1 = y1; rx2 = x1 + w; ry2 = y1 + h;
                }
                else
                {
                    long ax1 = x1, ax2 = x2, ay1 = y1, ay2 = y2;
                    if (!TryExpandDegenerateAxis(ref ax1, ref ax2, rawDx)) return null;
                    if (!TryExpandDegenerateAxis(ref ay1, ref ay2, rawDy)) return null;
                    rx1 = Math.Min(ax1, ax2); rx2 = Math.Max(ax1, ax2);
                    ry1 = Math.Min(ay1, ay2); ry2 = Math.Max(ay1, ay2);
                }
                return new RectShape { Layer = CurrentLayerKey, X1 = rx1, Y1 = ry1, X2 = rx2, Y2 = ry2 };
            }

            case Tool.RoundedRect:
            {
                long ax1 = x1, ax2 = x2, ay1 = y1, ay2 = y2;
                if (!TryExpandDegenerateAxis(ref ax1, ref ax2, rawDx)) return null;
                if (!TryExpandDegenerateAxis(ref ay1, ref ay2, rawDy)) return null;
                long rx1 = Math.Min(ax1, ax2), rx2 = Math.Max(ax1, ax2);
                long ry1 = Math.Min(ay1, ay2), ry2 = Math.Max(ay1, ay2);
                long radius = Math.Min(_cornerRadiusDbu, Math.Min(rx2 - rx1, ry2 - ry1) / 2);
                return new RoundedRectShape { Layer = CurrentLayerKey, X1 = rx1, Y1 = ry1, X2 = rx2, Y2 = ry2, CornerRadius = radius };
            }

            case Tool.Circle:
            {
                double dx = x2 - x1, dy = y2 - y1;
                long r = (long)Math.Round(Math.Sqrt(dx * dx + dy * dy));
                if (r <= 0)
                {
                    // Same idea as the rect axes: a snap-collapsed radius with a genuinely non-zero
                    // raw drag gets a minimum-size circle instead of nothing; a real click (raw
                    // distance also zero) still yields no shape.
                    if (rawDx == 0 && rawDy == 0) return null;
                    r = OneSnapStepDbu;
                }
                return new CircleShape { Layer = CurrentLayerKey, Cx = x1, Cy = y1, R = r };
            }

            default:
                return null;
        }
    }

    private LayoutShape? BuildMultiPointShape(IReadOnlyList<(long X, long Y)> points)
    {
        if (points.Count == 0) return null;
        var xy = new long[points.Count * 2];
        for (int i = 0; i < points.Count; i++) { xy[2 * i] = points[i].X; xy[2 * i + 1] = points[i].Y; }

        return ActiveTool switch
        {
            Tool.Polygon => new PolygonShape { Layer = CurrentLayerKey, Xy = xy },
            Tool.Path    => new PathShape { Layer = CurrentLayerKey, Xy = xy, Width = _pathWidthDbu, End = CurrentPathEndStyle },
            _ => null,
        };
    }

    // ── Overlay + live readout rebuild ───────────────────────────────────────────

    private void RebuildOverlay()
    {
        LayoutShape? inProgress = null;

        if (_isDrawingTwoPoint)
        {
            inProgress = BuildTwoPointShape(_drawP1X, _drawP1Y, _drawP2X, _drawP2Y, _typedWidthDbu, _typedHeightDbu,
                _drawP2RawX - _drawP1RawX, _drawP2RawY - _drawP1RawY);
            DrawReadoutText = ComputeTwoPointReadout();
            if (ActiveTool == Tool.Rect)
            {
                long w = _typedWidthDbu ?? Math.Abs(_drawP2X - _drawP1X);
                long h = _typedHeightDbu ?? Math.Abs(_drawP2Y - _drawP1Y);
                DrawWidthText  = LayoutUnits.Format(w, DisplayUnit, Model.DbuPerMicron);
                DrawHeightText = LayoutUnits.Format(h, DisplayUnit, Model.DbuPerMicron);
            }
        }
        else if (_drawPoints.Count > 0)
        {
            var preview = new List<(long X, long Y)>(_drawPoints) { (_drawCurX, _drawCurY) };
            inProgress = BuildMultiPointShape(preview);
            DrawReadoutText = ComputeMultiPointReadout();
        }
        else if (_isTypingLabel)
        {
            inProgress = new LabelShape
            {
                Layer = CurrentLayerKey, X = _labelAnchorX, Y = _labelAnchorY,
                Text  = (_labelBuffer.Length > 0 ? _labelBuffer : "") + "|", Height = _labelHeightDbu,
            };
            DrawReadoutText = BuildLabelTypingStatus();
        }
        else if (_portGhost is { } portGhost)
        {
            inProgress = portGhost;
            DrawReadoutText = $"Port {portGhost.Text} — click to place";
        }
        else
        {
            // A parameter-handle drag owns the readout for its whole gesture — it is set INSIDE the
            // rebuild rather than before it, because this method is the one place that decides what
            // the readout says and an assignment made before calling it would simply be overwritten.
            DrawReadoutText = _pcellHandleDrag?.Readout ?? "";
        }

        LayoutMarquee? marquee = _selectDragKind == SelectDragKind.Marquee
            ? new LayoutMarquee(_selectPressWX, _selectPressWY, _marqueeCurX, _marqueeCurY)
            : null;

        // brief-L3a-followups.md §2/R-fix-2: Move now covers BOTH kinds together (no more separate
        // MoveInstance drag kind) — this block and the shape one just below it are independent `if`s,
        // both gated on the SAME _selectDragKind == Move, each naturally a no-op when its own selected-
        // index list is empty.
        IReadOnlyDictionary<int, LayoutInstance> instanceDragOverrides = EmptyInstanceDragOverrides;
        if (_selectDragKind == SelectDragKind.Move && (_moveLiveDx != 0 || _moveLiveDy != 0))
        {
            var dict = new Dictionary<int, LayoutInstance>();
            foreach (var idx in _selectedInstanceIndices)
            {
                if (idx < 0 || idx >= Model.Instances.Count) continue;
                var clone = LayoutGeometry.Clone(Model.Instances[idx]);
                LayoutGeometry.TranslateBy(clone, _moveLiveDx, _moveLiveDy);
                dict[idx] = clone;
            }
            instanceDragOverrides = dict;
        }
        else if (_pcellHandleDrag is { } pinDrag && (pinDrag.PendingDx != 0 || pinDrag.PendingDy != 0))
        {
            // R-pch-4b: a pinned-anchor grip drag moves the whole instance so the anchor holds its
            // world position. Riding the EXISTING instance-drag-override channel means the renderer
            // needed no change at all — it already substitutes an overridden instance for the stored
            // one, which is exactly what this is.
            var shifted = LayoutGeometry.Clone(pinDrag.Instance);
            LayoutGeometry.TranslateBy(shifted, pinDrag.PendingDx, pinDrag.PendingDy);
            instanceDragOverrides = new Dictionary<int, LayoutInstance> { [pinDrag.InstanceIndex] = shifted };
        }

        IReadOnlyDictionary<int, LayoutShape> dragOverrides = EmptyDragOverrides;
        if (_selectDragKind == SelectDragKind.Move && (_moveLiveDx != 0 || _moveLiveDy != 0))
        {
            var dict = new Dictionary<int, LayoutShape>();
            foreach (var idx in MovableSelectedIndices(_selectedIndices))
            {
                var clone = LayoutGeometry.Clone(Model.Shapes[idx]);
                LayoutGeometry.TranslateBy(clone, _moveLiveDx, _moveLiveDy);
                dict[idx] = clone;
            }
            dragOverrides = dict;
        }
        else if (_handleDragKind != HandleDragKind.None && _handleDragPreview is not null)
        {
            // L1d: the live handle-drag preview reuses the SAME dragOverrides mechanism L1c's move
            // uses (brief §1: "render a preview through the existing dragOverrides mechanism").
            dragOverrides = new Dictionary<int, LayoutShape> { [_handleDragShapeIndex] = _handleDragPreview };
        }
        else if (_scaleDragKind != ScaleDragKind.None && _scaleDragMoved)
        {
            // L1h: the live bbox-scale preview reuses the SAME mechanism — it is already N-shape-
            // capable (L1c's move preview built it that way), so a multi-shape scale needs no new
            // preview plumbing beyond computing the scaled clones themselves.
            var (scaled, _) = BuildScaledShapes(_scaleDragOriginals, _scaleLiveFactorX, _scaleLiveFactorY, _scaleAnchorX, _scaleAnchorY);
            var dict = new Dictionary<int, LayoutShape>();
            for (int k = 0; k < _scaleDragIndices.Count; k++) dict[_scaleDragIndices[k]] = scaled[k];
            dragOverrides = dict;
            UpdateScaleReadoutText();
        }

        IReadOnlyList<LayoutShape>? pastePreview = null;
        IReadOnlyList<LayoutOverlay.GhostInstance>? pasteInstancePreview = null;
        if (_pastePlacementShapes is { } pasteShapes)
        {
            long dx = _pasteCursorX - _pastePlacementAnchorX;
            long dy = _pasteCursorY - _pastePlacementAnchorY;
            if (pasteShapes.Count > 0) pastePreview = LayoutFragment.Translate(pasteShapes, dx, dy);

            // The instance half of the ghost (owner report: the ports followed the cursor and the
            // MLIN did not). Translated the same way and by the same delta as the shapes, so the two
            // halves of one fragment can never drift apart mid-gesture.
            if (_pastePlacementInstances.Count > 0)
            {
                var moved = LayoutFragment.Translate(_pastePlacementInstances, dx, dy);
                var ghosts = new List<LayoutOverlay.GhostInstance>(moved.Count);
                for (int i = 0; i < moved.Count; i++)
                    ghosts.Add(new LayoutOverlay.GhostInstance(
                        moved[i],
                        CellHierarchy.InstanceBbox(moved[i], InstanceBaseDir),
                        i < _pastePlacementInstanceBoxOnly.Length && _pastePlacementInstanceBoxOnly[i]));
                pasteInstancePreview = ghosts;
            }
        }

        // L1i: one highlight path, not a committed one and a preview one — the marquee preview IS the
        // prospective outcome (R-L1i-3), so it renders with the exact same accent as a settled
        // selection while a marquee drag is active, and _selectedIndices otherwise. R-fix-3 extends
        // this identically to instances — an instance entering the marquee highlights live the same
        // way a shape does, and un-highlights under Ctrl the same way too, since both previews are
        // built by the exact same CombineMarqueePreview call.
        IReadOnlyList<int> effectiveHighlight = _selectDragKind == SelectDragKind.Marquee
            ? _marqueePreview
            : _selectedIndices;
        IReadOnlyList<int> effectiveInstanceHighlight = _selectDragKind == SelectDragKind.Marquee
            ? _marqueeInstancePreview
            : _selectedInstanceIndices;

        Overlay = new LayoutOverlay
        {
            InProgressPrimitive = inProgress,
            SelectedIndices = effectiveHighlight.ToArray(),
            SelectedInstanceIndices = effectiveInstanceHighlight.ToArray(),
            Marquee = marquee,
            DragOverrides = dragOverrides,
            InstanceDragOverrides = instanceDragOverrides,
            PastePreview = pastePreview,
            PastePreviewInstances = pasteInstancePreview,
            // brief-L3a-followups.md §4: the Instance tool's own ghost and the drag-and-drop ghost are
            // two independent state machines (see LayoutEditorViewModel.Instances.cs's own header) that
            // are never simultaneously active in normal use — one overlay slot serves both.
            PendingInstancePlacement = _instancePlacementPending ?? _dragInstancePlacementPending,
            PendingPCellPlacement = _paletteDragGhostView is { } ghostView && _paletteDragPoint is { } pt
                ? (ghostView, pt.X, pt.Y) : null,
            ShowScaleHandles = ShowScaleHandles,
            SnapMarker = _currentSnapCandidate,
            DrcMarkers = BuildDrcMarkers(),
            // pcell-parameter-handles.md: the selected PCell instance's parameter grips, and — while
            // one is being dragged live — the regenerated artwork to draw in that instance's place.
            PCellHandles = BuildPCellHandleMarkers(instanceDragOverrides),
            PCellHandlePreview = _pcellHandleDrag is { PreviewView: { } handleGhost } dragging
                ? (dragging.InstanceIndex, handleGhost) : null,
        };
    }

    private string ComputeTwoPointReadout()
    {
        string unit = UnitSuffix(DisplayUnit);
        if (ActiveTool == Tool.Circle)
        {
            double dx = _drawP2X - _drawP1X, dy = _drawP2Y - _drawP1Y;
            long r = (long)Math.Round(Math.Sqrt(dx * dx + dy * dy));
            return $"r = {LayoutUnits.Format(r, DisplayUnit, Model.DbuPerMicron)} {unit}";
        }
        long w = _typedWidthDbu ?? Math.Abs(_drawP2X - _drawP1X);
        long h = _typedHeightDbu ?? Math.Abs(_drawP2Y - _drawP1Y);
        return $"{LayoutUnits.Format(w, DisplayUnit, Model.DbuPerMicron)} × {LayoutUnits.Format(h, DisplayUnit, Model.DbuPerMicron)} {unit}";
    }

    private string ComputeMultiPointReadout()
    {
        if (_drawPoints.Count == 0) return "";
        string unit = UnitSuffix(DisplayUnit);

        var last = _drawPoints[^1];
        double segLen = Distance(last.X, last.Y, _drawCurX, _drawCurY);

        double total = segLen;
        for (int i = 1; i < _drawPoints.Count; i++)
            total += Distance(_drawPoints[i - 1].X, _drawPoints[i - 1].Y, _drawPoints[i].X, _drawPoints[i].Y);

        return $"seg {LayoutUnits.Format((long)Math.Round(segLen), DisplayUnit, Model.DbuPerMicron)} · " +
               $"total {LayoutUnits.Format((long)Math.Round(total), DisplayUnit, Model.DbuPerMicron)} {unit}";
    }

    private static double Distance(long x0, long y0, long x1, long y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ── Save / load ────────────────────────────────────────────────────────────

    public IAsyncRelayCommand<Window?> SaveLayoutCommand   { get; }
    public IAsyncRelayCommand<Window?> SaveLayoutAsCommand { get; }

    /// <summary>Fired after each successful save with the absolute path of the saved .clay file.</summary>
    public event Action<string>? LayoutSaved;

    /// <summary>Raised when a save fails (e.g. a read-only / unwritable location). The workspace
    /// routes it to the Messages pane. A failed save must surface an error, never crash the app.</summary>
    public event Action<string>? SaveError;

    private async Task SaveLayoutAsync(Window? owner)
    {
        if (CurrentLayoutPath is not null)
            PerformSave(CurrentLayoutPath);
        else
            await SaveLayoutAsAsync(owner);
    }

    private async Task SaveLayoutAsAsync(Window? owner)
    {
        if (owner is null) return;

        IStorageFolder? startFolder = null;
        if (CurrentLayoutPath is { Length: > 0 } p)
        {
            string? dir = Path.GetDirectoryName(p);
            if (dir is not null)
                try { startFolder = await owner.StorageProvider.TryGetFolderFromPathAsync(dir); }
                catch { }
        }

        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Save Layout",
            DefaultExtension       = "clay",
            SuggestedFileName      = Path.GetFileNameWithoutExtension(CurrentLayoutPath ?? "layout"),
            SuggestedStartLocation = startFolder,
            FileTypeChoices        =
            [
                new FilePickerFileType("circuitRF Layout") { Patterns = ["*.clay"] },
            ],
        });
        if (result is null) return;
        PerformSave(result.Path.LocalPath);
    }

    internal void PerformSave(string path)   // internal for a future save-error regression test
    {
        try
        {
            LayoutPersistence.SaveToFile(path, Model);
        }
        catch (Exception ex)
        {
            // Do NOT mark the document saved or raise LayoutSaved — the file was not written.
            SaveError?.Invoke($"Couldn't save layout to '{path}': {ex.Message}");
            return;
        }
        CurrentLayoutPath = path;
        MarkSaved();
        LayoutSaved?.Invoke(path);
    }
}
