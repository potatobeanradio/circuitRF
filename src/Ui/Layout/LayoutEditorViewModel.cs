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

    private long _labelHeightDbu = 5_000;   // arbitrary reasonable default (5 um at 1000 dbu/um)
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

    /// <summary>True while any drawing gesture is in progress — the view uses this (or checks
    /// <see cref="ActiveTool"/>) to decide whether the live W/H fields should be enabled.</summary>
    public bool IsDrawingRect => _isDrawingTwoPoint && ActiveTool == Tool.Rect;

    // ── Pointer handlers — filled from LayoutCanvas (L1a's marked seam) ─────────────────────────

    public void OnPointerPressed(double wx, double wy, KeyModifiers mods, int clickCount = 1)
    {
        if (ActiveTool == Tool.Select) return;   // inert in L1b — hit-testing arrives in L1c

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
            RebuildOverlay();
        }
    }

    public void OnPointerMoved(double wx, double wy, bool leftDown, KeyModifiers mods)
    {
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
        if (_isTypingLabel)
        {
            if (key == Key.Escape) { CancelDrawOp(); return; }
            if (key == Key.Enter || key == Key.Return) { CommitLabel(); return; }
            if (key == Key.Back && _labelBuffer.Length > 0) { _labelBuffer = _labelBuffer[..^1]; RebuildOverlay(); }
            return;
        }

        if (key == Key.Escape) { CancelDrawOp(); return; }

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

    /// <summary>Escape — leaves the model untouched and clears the overlay (gate 10).</summary>
    private void CancelDrawOp()
    {
        _isDrawingTwoPoint = false;
        _drawPoints.Clear();
        _isTypingLabel   = false;
        _labelBuffer     = "";
        _typedWidthDbu   = null;
        _typedHeightDbu  = null;
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
            var shape = new LabelShape
            {
                Layer    = CurrentLayerKey,
                X        = _labelAnchorX,
                Y        = _labelAnchorY,
                Text     = _labelBuffer,
                Height   = _labelHeightDbu,
                Rotation = LayoutRotation.R0,
                IsPort   = false,   // port placement belongs with the EM work, not here
            };
            Execute(new AddShapeCommand(Model, shape));
        }
        _isTypingLabel = false;
        _labelBuffer   = "";
        RebuildOverlay();
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
            DrawReadoutText = "";
        }
        else
        {
            DrawReadoutText = "";
        }

        Overlay = new LayoutOverlay { InProgressPrimitive = inProgress };
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
