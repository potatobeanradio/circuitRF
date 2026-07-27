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
    /// gesture is one call to Execute — however many vertices/points it took to build the shape.</summary>
    public void Execute(IUiCommand cmd) => _undoRedo.Execute(cmd);

    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// True after a DisplayUnit/SnapDbu edit that has not yet been saved. Kept separate from
    /// <see cref="UndoRedoStack.IsModified"/> because those two preferences deliberately carry NO
    /// undo entry (§1.3/§1.5) — <see cref="IsDirty"/> is the OR of this flag and the undo stack's
    /// modified state, so either kind of unsaved change dirties the document, and
    /// <see cref="MarkSaved"/> clears both together.
    /// </summary>
    private bool _prefsDirty;

    private void RefreshDirty() => IsDirty = _prefsDirty || _undoRedo.IsModified;

    /// <summary>Records the current state (undo position + preference edits) as the clean,
    /// just-saved baseline. Call after the document has been written to disk.</summary>
    public void MarkSaved()
    {
        _prefsDirty = false;
        _undoRedo.MarkSaved();
        RefreshDirty();
    }

    /// <summary>
    /// Absolute on-disk path of the .clay file, or null for a not-yet-saved (scratch) document.
    /// Mirrors <c>SymbolEditorViewModel.CurrentSymbolPath</c> — the document reflects this.
    /// </summary>
    [ObservableProperty] private string? _currentLayoutPath;

    [ObservableProperty] private LayoutUnit _displayUnit;
    [ObservableProperty] private long _snapDbu;

    // ── Technology (L0c) ───────────────────────────────────────────────────────

    /// <summary>The resolved technology, or null when unresolved (missing/corrupt/no default) —
    /// the layout still opens and edits either way (§2.4 "never block on it").</summary>
    [ObservableProperty] private Technology? _technology;

    /// <summary>Absolute path of the .ctech <see cref="Technology"/> was resolved from, or null.
    /// Lets the workspace know which open documents to refresh when that file changes.</summary>
    internal string? ResolvedTechPath { get; private set; }

    public string TechNameText => Technology?.Name ?? "No technology";

    public string LayerCountText => Technology is null ? "fallback colors" : $"{Technology.Layers.Count} layers";

    /// <summary>Combined metadata-bar readout, e.g. "PCB 2-Layer · 8 layers" or
    /// "No technology · fallback colors".</summary>
    public string TechSummaryText => $"{TechNameText} · {LayerCountText}";

    partial void OnTechnologyChanged(Technology? value)
    {
        OnPropertyChanged(nameof(TechNameText));
        OnPropertyChanged(nameof(LayerCountText));
        OnPropertyChanged(nameof(TechSummaryText));
        RebuildAvailableLayers();
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
    }

    partial void OnSnapDbuChanged(long value)
    {
        Model.SnapDbu = value;
        _prefsDirty = true;
        RefreshDirty();
        OnPropertyChanged(nameof(SnapText));
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

        SelectAllCommand = new RelayCommand(() =>
        {
            SetSelection(Enumerable.Range(0, Model.Shapes.Count));
            _cycleCache = null;
        });
        DeselectAllCommand = new RelayCommand(() =>
        {
            SetSelection([]);
            _cycleCache = null;
        });

        InitBooleanCommands();   // L1e — src/Ui/Layout/LayoutEditorViewModel.Booleans.cs
        InitClipboardCommands(); // L1f — src/Ui/Layout/LayoutEditorViewModel.Clipboard.cs
        InitScaleCommands();     // L1h — src/Ui/Layout/LayoutEditorViewModel.Scale.cs

        _pathWidthText     = LayoutUnits.Format(_pathWidthDbu, DisplayUnit, Model.DbuPerMicron);
        _cornerRadiusText  = LayoutUnits.Format(_cornerRadiusDbu, DisplayUnit, Model.DbuPerMicron);
        _labelHeightText   = LayoutUnits.Format(_labelHeightDbu, DisplayUnit, Model.DbuPerMicron);

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

            // Any model mutation (draw, move, delete, undo, redo — every one of them calls
            // NotifyChanged) invalidates the overlap-cycling cache (R-L1c-2). Selected indices may
            // also have shifted or been removed by the mutation, so drop any that are now stale.
            // The picked-vertex index (L1d) is unconditionally cleared too — a ReplaceShapeCommand
            // can change the shape's vertex count/order at the very index that's still selected.
            _cycleCache = null;
            _pickedVertexIndex = null;
            if (_selectedIndices.RemoveAll(i => i < 0 || i >= Model.Shapes.Count) > 0)
            {
                SelectionStatusText = ComputeGenericSelectionStatus();
                RebuildOverlay();
            }
        };
    }

    // ── Canvas viewport readouts (L1a) ─────────────────────────────────────────

    /// <summary>True when the layout has no geometry at all — the view keeps L0b's centered
    /// placeholder text in this state instead of showing an empty canvas/grid.</summary>
    public bool IsEmpty => Model.Shapes.Count == 0 && Model.Instances.Count == 0;

    [ObservableProperty] private string _cursorXText = "—";
    [ObservableProperty] private string _cursorYText = "—";

    /// <summary>Called by the view on every pointer-move/exit over the canvas — §1 R6's "live
    /// physical readout." Null clears the readout (pointer left the canvas).</summary>
    public void SetCursorWorld(double? worldX, double? worldY)
    {
        if (worldX is null || worldY is null)
        {
            CursorXText = "—";
            CursorYText = "—";
            return;
        }
        CursorXText = LayoutUnits.Format((long)Math.Round(worldX.Value), DisplayUnit, Model.DbuPerMicron) + " " + UnitSuffix(DisplayUnit);
        CursorYText = LayoutUnits.Format((long)Math.Round(worldY.Value), DisplayUnit, Model.DbuPerMicron) + " " + UnitSuffix(DisplayUnit);
    }

    /// <summary>Posts a Messages summary (docs/sonnet-briefs/brief-L1g-technology-retarget.md §5 —
    /// "report what happened"). Used after a technology retarget and after a cross-tech paste, both
    /// of which are bulk changes to the user's geometry that deserve a readable record once the
    /// dialog is gone.</summary>
    public void ReportMessage(string text) => _messageSink?.Success(text);

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
    public enum Tool { Select, Rect, RoundedRect, Circle, Polygon, Path, Label }

    [ObservableProperty] private Tool _activeTool = Tool.Select;

    public IRelayCommand<string> SetActiveToolCommand { get; private set; } = null!;

    partial void OnActiveToolChanged(Tool value) => CancelDrawOp();

    private static bool IsTwoPointDragTool(Tool t) => t is Tool.Rect or Tool.RoundedRect or Tool.Circle;
    private static bool IsMultiPointTool(Tool t)   => t is Tool.Polygon or Tool.Path;

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

    private void SetSelection(IEnumerable<int> indices)
    {
        var distinct = new List<int>();
        foreach (var i in indices)
            if (i >= 0 && i < Model.Shapes.Count && !distinct.Contains(i))
                distinct.Add(i);

        _selectedIndices.Clear();
        _selectedIndices.AddRange(distinct);
        _pickedVertexIndex = null;
        SelectionStatusText = ComputeGenericSelectionStatus();
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
    // (ClickX, ClickY) is the world point (rounded to DBU) of the press that built this cache;
    // Stack is the ordered hit list from that press; Index is which stack entry is CURRENTLY
    // selected. Invalidated by: pointer movement beyond the tolerance threshold (HandleSelectMove),
    // any model mutation (the Model.Changed subscription in the constructor), and any selection
    // change originating elsewhere (SelectAll/DeselectAll/marquee/delete below).
    private (long ClickX, long ClickY, IReadOnlyList<int> Stack, int Index)? _cycleCache;

    // ── Select-tool gesture state ─────────────────────────────────────────────

    private enum SelectDragKind { None, Move, Marquee }
    private SelectDragKind _selectDragKind = SelectDragKind.None;

    private long _selectPressWX, _selectPressWY;   // world press point (rounded to DBU)
    private long _marqueeCurX, _marqueeCurY;       // live marquee far corner
    private bool _marqueeAdd, _marqueeToggle;      // Shift / Ctrl captured at press
    private List<int> _marqueeBaseSelection = [];

    // ── Live marquee preview (L1i, docs/sonnet-briefs/brief-L1i-live-marquee-selection.md) ────────
    // R-L1i-2: _marqueePreview is a SEPARATE list from _selectedIndices — it is what the highlight
    // renders while a marquee drag is active, and it is NEVER written into the real selection until
    // commit (HandleSelectRelease -> CommitMarquee -> SetSelection). Both are reused scratch buffers
    // (cleared and refilled in place, never reallocated per pointer move) per the brief's perf note.
    private readonly List<int> _marqueeHitsScratch = [];
    private readonly List<int> _marqueePreview = [];
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

    private void HandleSelectPress(double wx, double wy, KeyModifiers mods, long tolDbu)
    {
        bool shift = (mods & KeyModifiers.Shift) != 0;
        bool ctrl  = (mods & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool alt   = (mods & KeyModifiers.Alt) != 0;

        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);
        _selectPressWX = px; _selectPressWY = py;

        // L1h: bbox scale handles take priority over everything else when they're showing (R-L1h-5) —
        // a 2+ selection always has them; a single selection has them only while Scale mode is toggled
        // on, in which case they TEMPORARILY REPLACE L1d's handles rather than coexist with them.
        if (TryBeginScaleDrag(px, py, alt, tolDbu))
            return;

        // L1d: handles (and the edge-line fallback below them) take absolute priority over L1c's
        // selection/cycling logic when exactly one shape is selected — "a press on a handle must not
        // disturb the selection or the overlap-cycling cache" (§2). Only a single-shape selection
        // shows handles at all (§2: "multi-selection shows no handles — it is a move/delete selection").
        if (_selectedIndices.Count == 1 && !ScaleModeActive && TryHandleSelectPressOnHandles(_selectedIndices[0], px, py, ctrl, alt, tolDbu))
            return;
        _pickedVertexIndex = null;

        long thresh = Math.Max(tolDbu, 1);
        bool cacheUsable = _cycleCache is { } c0 && c0.Stack.Count > 0
            && (alt || (Math.Abs(c0.ClickX - px) <= thresh && Math.Abs(c0.ClickY - py) <= thresh));

        IReadOnlyList<int> stack;
        int stackIndex;

        if (cacheUsable)
        {
            var c = _cycleCache!.Value;
            stack = c.Stack;
            stackIndex = (c.Index + 1) % stack.Count;
        }
        else
        {
            stack = LayoutHitTest.HitStack(Model, Technology, px, py, tolDbu);
            stackIndex = 0;
        }

        if (stack.Count == 0)
        {
            _cycleCache = null;
            if (!shift && !ctrl) SetSelection([]);
            BeginMarquee(px, py, shift, ctrl);
            return;
        }

        _cycleCache = (px, py, stack, stackIndex);
        int hitIndex = stack[stackIndex];
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
    /// </summary>
    private LayoutShape? BuildHandleDragPreview(long px, long py, bool suspendSnap)
    {
        if (_handleDragOriginal is null) return null;
        var original = _handleDragOriginal;

        switch (_handleDragKind)
        {
            case HandleDragKind.Vertex:
            {
                var (sx, sy) = LayoutSnapping.SnapPoint(px, py, Model.SnapDbu, suspendSnap);
                return LayoutShapeEditing.SetVertex(original, _handleDragIndex, sx, sy);
            }

            case HandleDragKind.EdgeMidpoint:
            {
                var (dx, dy) = ComputeEdgePerpendicularOffset(original, _handleDragIndex, px, py, suspendSnap);
                return LayoutShapeEditing.TranslateEdgeEndpoints(original, _handleDragIndex, dx, dy);
            }

            case HandleDragKind.RectEdge:
            {
                long delta = ComputeRectEdgePerpendicularOffset(_handleDragIndex, px, py, suspendSnap);
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
                var (sx, sy) = LayoutSnapping.SnapPoint(px, py, Model.SnapDbu, suspendSnap);
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

        double totalDx = px - _handleDragAnchorX, totalDy = py - _handleDragAnchorY;
        double offset = totalDx * nx + totalDy * ny; // scalar projection onto the perpendicular
        long snapped = LayoutSnapping.SnapValue(offset, Model.SnapDbu, suspendSnap);

        return ((long)Math.Round(snapped * nx), (long)Math.Round(snapped * ny));
    }

    /// <summary>Same "snap the perpendicular offset" rule as <see cref="ComputeEdgePerpendicularOffset"/>,
    /// simplified for a Rect/RoundedRect's always-axis-aligned edges: edges 0/2 (bottom/top) are
    /// horizontal, so their perpendicular is vertical (project the drag's Y delta); edges 1/3
    /// (right/left) are vertical, so their perpendicular is horizontal (project the drag's X delta).
    /// No vector math needed — the axis is fixed by which edge it is.</summary>
    private long ComputeRectEdgePerpendicularOffset(int edgeIndex, long px, long py, bool suspendSnap)
    {
        double totalDx = px - _handleDragAnchorX, totalDy = py - _handleDragAnchorY;
        double raw = edgeIndex is 0 or 2 ? totalDy : totalDx;
        return LayoutSnapping.SnapValue(raw, Model.SnapDbu, suspendSnap);
    }

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
        if (_selectedIndices.Count != 1) return null;
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
        if (_selectedIndices.Count != 1) return null;
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
                : _selectedIndices.Append(hitIndex));
        }
        else if (shift)
        {
            SetSelection(_selectedIndices.Contains(hitIndex)
                ? _selectedIndices
                : _selectedIndices.Append(hitIndex));
        }
        else
        {
            // A plain click on a shape that is already part of a MULTI-selection preserves the
            // whole selection — this is what makes "drag from inside any selected shape translates
            // the whole selection" true rather than collapsing the group to just the clicked member
            // before the drag even starts. A click on anything else (an unselected shape, or the
            // sole member of a single-shape selection — which still needs to replace-with-itself so
            // cycling continues to work) replaces the selection with just the hit.
            if (!(_selectedIndices.Count > 1 && _selectedIndices.Contains(hitIndex)))
                SetSelection([hitIndex]);
        }
    }

    private void UpdateSelectionStatusFromCycle()
    {
        if (_selectedIndices.Count == 1 && _cycleCache is { } cache && cache.Stack.Count > 1)
        {
            int idx = _selectedIndices[0];
            int pos = -1;
            for (int i = 0; i < cache.Stack.Count; i++) if (cache.Stack[i] == idx) { pos = i; break; }
            if (pos >= 0 && idx >= 0 && idx < Model.Shapes.Count)
            {
                var shape = Model.Shapes[idx];
                SelectionStatusText = $"{ShapeTypeName(shape)} · {LayerDisplayName(shape.Layer)} · {pos + 1} of {cache.Stack.Count}";
                return;
            }
        }
        SelectionStatusText = ComputeGenericSelectionStatus();
    }

    private void BeginMoveDrag(long px, long py)
    {
        if (_selectedIndices.Count == 0) return;
        _selectDragKind = SelectDragKind.Move;
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
        _marqueeLastComputedCorner = null;
        ComputeMarqueeSelection(px, py);
        _marqueeLastComputedCorner = (px, py);
        UpdateMarqueeSelectionStatus();
        RebuildOverlay();
    }

    /// <summary>R-L1i-1: the ONE hit computation shared by the live preview (called every qualifying
    /// pointer move) and the commit (called once at release, via <see cref="CommitMarquee"/>) — if the
    /// preview computed hits differently from the commit, the highlight would lie about the outcome.
    /// Folds in the Shift (add) / Ctrl (toggle) semantics against <see cref="_marqueeBaseSelection"/>,
    /// so the result IS the prospective final selection (R-L1i-3) — a Ctrl-drag crossing an
    /// already-selected shape visibly un-highlights it with no separate code path. Mutates and returns
    /// the reused <see cref="_marqueePreview"/> scratch buffer (never <c>_selectedIndices</c> — that is
    /// R-L1i-2, enforced structurally: nothing in this method touches that field).</summary>
    private List<int> ComputeMarqueeSelection(long curX, long curY)
    {
        MarqueeRecomputeCount++;

        bool leftToRight = curX >= _selectPressWX;
        long minX = Math.Min(_selectPressWX, curX), maxX = Math.Max(_selectPressWX, curX);
        long minY = Math.Min(_selectPressWY, curY), maxY = Math.Max(_selectPressWY, curY);
        var marqueeBb = new Bbox(minX, minY, maxX, maxY);

        _marqueeHitsScratch.Clear();
        for (int i = 0; i < Model.Shapes.Count; i++)
        {
            var shape = Model.Shapes[i];
            var def = ResolveLayerDef(shape.Layer);
            if (!def.Visible || !def.Selectable) continue; // gate 8: hidden/non-selectable never previewed

            // L2: query the spatial index instead of scanning all shapes
            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;

            bool matches = leftToRight
                ? bb.MinX >= marqueeBb.MinX && bb.MaxX <= marqueeBb.MaxX && bb.MinY >= marqueeBb.MinY && bb.MaxY <= marqueeBb.MaxY
                : bb.Intersects(marqueeBb);
            if (matches) _marqueeHitsScratch.Add(i);
        }

        _marqueePreview.Clear();
        if (_marqueeToggle)
        {
            _marqueePreview.AddRange(_marqueeBaseSelection);
            foreach (var h in _marqueeHitsScratch)
            {
                int existing = _marqueePreview.IndexOf(h);
                if (existing >= 0) _marqueePreview.RemoveAt(existing);
                else _marqueePreview.Add(h);
            }
        }
        else if (_marqueeAdd)
        {
            _marqueePreview.AddRange(_marqueeBaseSelection);
            foreach (var h in _marqueeHitsScratch)
                if (!_marqueePreview.Contains(h)) _marqueePreview.Add(h);
        }
        else
        {
            _marqueePreview.AddRange(_marqueeHitsScratch);
        }

        return _marqueePreview;
    }

    private void UpdateMarqueeSelectionStatus()
    {
        SelectionStatusText = _marqueePreview.Count switch
        {
            0 => "",
            1 => "1 shape",
            var n => $"{n} shapes",
        };
    }

    /// <summary>Escape (or any other abandonment of an in-progress marquee, e.g. the pointer button
    /// being released off-canvas) must clear the preview and restore the status readout WITHOUT
    /// touching <c>_selectedIndices</c> — that field was never written during the drag (R-L1i-2), so
    /// simply recomputing the generic status from it is correct.</summary>
    private void CancelMarqueeIfActive()
    {
        if (_selectDragKind != SelectDragKind.Marquee) return;
        _marqueePreview.Clear();
        _marqueeLastComputedCorner = null;
        SelectionStatusText = ComputeGenericSelectionStatus();
    }

    private void HandleSelectMove(double wx, double wy, bool leftDown, KeyModifiers mods, long tolDbu, long pixelDbu = 0)
    {
        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);

        if (_cycleCache is { } cache)
        {
            long thresh = Math.Max(tolDbu, 1);
            if (Math.Abs(cache.ClickX - px) > thresh || Math.Abs(cache.ClickY - py) > thresh)
                _cycleCache = null;
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
                RebuildOverlay();
            }
            return;
        }

        switch (_selectDragKind)
        {
            case SelectDragKind.Move:
            {
                bool suspend = (mods & KeyModifiers.Alt) != 0;
                long dx = LayoutSnapping.SnapValue(px - _moveAnchorX, Model.SnapDbu, suspend);
                long dy = LayoutSnapping.SnapValue(py - _moveAnchorY, Model.SnapDbu, suspend);
                if (dx != _moveLiveDx || dy != _moveLiveDy) _moveHasMoved = true;
                _moveLiveDx = dx; _moveLiveDy = dy;
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

    private void CommitMoveDrag()
    {
        if (_moveHasMoved && (_moveLiveDx != 0 || _moveLiveDy != 0) && _selectedIndices.Count > 0)
        {
            var shapes = MovableSelectedIndices(_selectedIndices).Select(i => Model.Shapes[i]).ToList();
            if (shapes.Count > 0)
                Execute(new Commands.Layout.MoveShapesCommand(Model, shapes, _moveLiveDx, _moveLiveDy));
        }
        _moveLiveDx = 0; _moveLiveDy = 0; _moveHasMoved = false;
    }

    /// <summary>R-L1i-1: commit is just "settle on whatever the shared compute says," via the exact
    /// same function the live preview calls — so the preview can never lie about the outcome (gate 3).
    /// <see cref="ComputeMarqueeSelection"/> returns the reused <c>_marqueePreview</c> buffer; passing
    /// it straight to <see cref="SetSelection"/> is safe because that method copies into
    /// <c>_selectedIndices</c> before this method goes on to clear it below.</summary>
    private void CommitMarquee(long releaseX, long releaseY)
    {
        SetSelection(ComputeMarqueeSelection(releaseX, releaseY));
        _cycleCache = null;
        _marqueePreview.Clear();
        _marqueeLastComputedCorner = null;
    }

    private void DeleteSelection()
    {
        var indices = _selectedIndices.ToList();
        if (indices.Count == 0) return;
        Execute(new Commands.Layout.DeleteShapesCommand(Model, indices));
        SetSelection([]);
        _cycleCache = null;
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

    private void NudgeSelection(Key key, KeyModifiers mods)
    {
        if (_selectedIndices.Count == 0) return;
        long step = OneSnapStepDbu;
        if ((mods & KeyModifiers.Shift) != 0) step *= 10;

        long dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
        long dy = key switch { Key.Up => step, Key.Down => -step, _ => 0 };
        if (dx == 0 && dy == 0) return;

        var shapes = MovableSelectedIndices(_selectedIndices).Select(i => Model.Shapes[i]).ToList();
        if (shapes.Count == 0) return;

        Execute(new Commands.Layout.MoveShapesCommand(Model, shapes, dx, dy));
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
    /// smaller than the current zoom can show — 0 (the default) skips that note.</summary>
    public void OnPointerPressed(double wx, double wy, KeyModifiers mods, int clickCount = 1, long hitTolDbu = 0, double zoomPxPerDbu = 0)
    {
        // L1f: a paste placement in progress takes priority over every other gesture — a click
        // commits it, regardless of the currently active drawing tool.
        if (_pastePlacementShapes is not null) { CommitPastePlacement(); return; }

        if (ActiveTool == Tool.Select) { HandleSelectPress(wx, wy, mods, Math.Max(hitTolDbu, 0)); return; }

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
    /// to skip the O(shapes) recompute for sub-pixel pointer moves, per L1i's perf note).</summary>
    public void OnPointerMoved(double wx, double wy, bool leftDown, KeyModifiers mods, long hitTolDbu = 0, long pixelDbu = 0)
    {
        if (_pastePlacementShapes is not null)
        {
            bool pasteSuspend = (mods & KeyModifiers.Alt) != 0;
            UpdatePastePlacementCursor(wx, wy, pasteSuspend);
            return;
        }

        if (ActiveTool == Tool.Select) { HandleSelectMove(wx, wy, leftDown, mods, Math.Max(hitTolDbu, 0), Math.Max(pixelDbu, 0)); return; }

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
            else { SetSelection([]); _cycleCache = null; }
            return;
        }

        if (ActiveTool == Tool.Select && _selectDragKind == SelectDragKind.None && _handleDragKind == HandleDragKind.None)
        {
            bool ctrlOrMeta = (mods & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (ctrlOrMeta && key == Key.A) { SelectAllCommand.Execute(null); return; }
            if (key == Key.Delete || key == Key.Back)
            {
                // §3 "Delete on a selected vertex" takes priority over whole-shape delete when a
                // vertex handle was the last thing clicked (no drag) on the single selection.
                if (_selectedIndices.Count == 1 && _pickedVertexIndex is { } vIdx) { DeleteVertex(_selectedIndices[0], vIdx); return; }
                if (_selectedIndices.Count > 0) { DeleteSelection(); return; }
            }
            if (key is Key.Left or Key.Right or Key.Up or Key.Down) { NudgeSelection(key, mods); return; }
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
        CancelMarqueeIfActive(); // gate 7: leaves _selectedIndices untouched, clears the preview
        _selectDragKind  = SelectDragKind.None;
        _moveLiveDx = 0; _moveLiveDy = 0; _moveHasMoved = false;
        ResetHandleDragState();
        ResetScaleDragState();
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
        else
        {
            DrawReadoutText = "";
        }

        LayoutMarquee? marquee = _selectDragKind == SelectDragKind.Marquee
            ? new LayoutMarquee(_selectPressWX, _selectPressWY, _marqueeCurX, _marqueeCurY)
            : null;

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
        if (_pastePlacementShapes is { Count: > 0 } pasteShapes)
        {
            long dx = _pasteCursorX - _pastePlacementAnchorX;
            long dy = _pasteCursorY - _pastePlacementAnchorY;
            pastePreview = LayoutFragment.Translate(pasteShapes, dx, dy);
        }

        // L1i: one highlight path, not a committed one and a preview one — the marquee preview IS the
        // prospective outcome (R-L1i-3), so it renders with the exact same accent as a settled
        // selection while a marquee drag is active, and _selectedIndices otherwise.
        IReadOnlyList<int> effectiveHighlight = _selectDragKind == SelectDragKind.Marquee
            ? _marqueePreview
            : _selectedIndices;

        Overlay = new LayoutOverlay
        {
            InProgressPrimitive = inProgress,
            SelectedIndices = effectiveHighlight.ToArray(),
            Marquee = marquee,
            DragOverrides = dragOverrides,
            PastePreview = pastePreview,
            ShowScaleHandles = ShowScaleHandles,
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
