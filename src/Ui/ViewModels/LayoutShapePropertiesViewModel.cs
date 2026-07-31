using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Properties panel for the Layout Editor's current shape selection (docs/sonnet-briefs/
/// brief-L1c-selection-and-properties.md §7, restructured by brief-L1j-properties-inspector.md).
/// Mirrors <see cref="SymbolPrimitiveInspectorViewModel"/>'s shape (staged text fields committed on
/// focus-loss/Enter via an explicit Commit* method, combo selections committed immediately via a
/// partial change handler) but for <see cref="LayoutShape"/>:
///
/// - Common to every shape: layer (a combo showing swatch + name, exactly the drawing toolbar's) and
///   net (free text).
/// - Type-specific groups are shown only when EVERY selected shape is that one type; a mixed-type
///   multi-selection shows only the common fields. Within a homogeneous-type multi-selection, a
///   staged text/combo field shows the shared value, or blank when the shapes' values differ — and
///   committing it applies to every one of them as ONE undo entry (<see cref="ApplyToEach{T}"/>
///   folds a <see cref="SetShapeFieldCommand{T}"/> per shape into a single <see cref="CompositeCommand"/>
///   chain, the same pattern <c>CompositeCommand</c> already supports elsewhere).
/// - Dimension fields parse through <see cref="LayoutUnits"/>.
///
/// <b>R-L1j-1 (liveness):</b> <c>_selected</c> is built from <see cref="LayoutEditorViewModel.
/// EffectiveSelectedShapes"/> — the live drag-override clone for an index when a drag preview exists,
/// otherwise the committed model shape — never <c>Model.Shapes</c> directly. This one change is what
/// makes every field (old and new) update on every pointer move of a drag, not just on commit; the
/// refresh TRIGGER (<c>Model.Changed</c> / <c>Overlay</c> PropertyChanged, both already subscribed
/// below) needed no change at all.
///
/// <b>R-L1j-2 (read-only mid-drag):</b> <see cref="IsEditingEnabled"/> is false whenever
/// <c>Overlay.DragOverrides</c> is non-empty — during a drag, <c>_selected</c> holds throwaway preview
/// clones, and a commit would write into geometry that is about to be discarded. Every Commit* method
/// also independently re-checks this at the moment it runs (<see cref="DragBlocksEdits"/>), so a
/// commit is refused even if something bypassed the view's <c>IsEnabled</c> binding.
///
/// <b>R-L1j-3 (never clobber focus):</b> <see cref="SetFocusedField"/> is called by the view's
/// GotFocus/LostFocus handlers; every write this class makes to a text field during
/// <see cref="RefreshFromVm"/> is routed through <see cref="SetTextIfNotFocused"/>, which skips the
/// one field currently under the caret. Escape (<see cref="RevertField"/>) is the one deliberate
/// bypass — it forces a refresh of that field specifically, discarding whatever was typed.
/// </summary>
public sealed partial class LayoutShapePropertiesViewModel : ObservableObject
{
    private LayoutEditorViewModel? _vm;
    private List<LayoutShape> _selected = [];
    private bool _isRefreshing;

    /// <summary>The bound editor VM, exposed so the view's code-behind can open the instance
    /// cell-picker dialog (Re-target…) — it needs <see cref="LayoutEditorViewModel.WorkspaceRootDir"/>/
    /// <see cref="LayoutEditorViewModel.InstanceBaseDir"/> to build it, exactly like the Instance
    /// tool's own placement picker in <c>LayoutEditorView.axaml.cs</c> already does.</summary>
    public LayoutEditorViewModel? EditorVm => _vm;

    /// <summary>The field key (see the Commit*/Error property names below, or a vertex row's
    /// <c>FieldKeyX</c>/<c>FieldKeyY</c>) currently under the caret, or null. Set by the view's
    /// GotFocus/LostFocus handlers — R-L1j-3.</summary>
    private string? _focusedField;

    public static PathEndStyle[]   PathEndStyleOptions { get; } = System.Enum.GetValues<PathEndStyle>();
    public static LayoutRotation[] RotationOptions     { get; } = System.Enum.GetValues<LayoutRotation>();
    public static LabelFontStyle[] LabelStyleOptions   { get; } = System.Enum.GetValues<LabelFontStyle>();

    // ── Empty state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isEmptyState = true;
    [ObservableProperty] private string _emptyMessage = "Select a shape or instance to inspect.";
    public bool IsNotEmptyState => !IsEmptyState;
    partial void OnIsEmptyStateChanged(bool oldValue, bool newValue) => OnPropertyChanged(nameof(IsNotEmptyState));

    [ObservableProperty] private string _selectionSummaryText = "";

    /// <summary>R-L1j-2: false while any selected shape has a live drag-preview override. Bound to
    /// the panel's editable region's <c>IsEnabled</c> — fields stay visible (so the user still sees
    /// live values) but cannot be typed into until the drag commits.</summary>
    [ObservableProperty] private bool _isEditingEnabled = true;

    private bool DragBlocksEdits() => _vm is not null && _vm.Overlay.DragOverrides.Count > 0;

    // ── Generic field dispatch (view code-behind is Tag-keyed, mirrors TechEditorView's
    // CommitField/OnComboSelectionChanged dispatcher pattern) ──────────────────────────────────────

    /// <summary>Called by the view on GotFocus/LostFocus for every text field (static or a vertex
    /// row) — <c>null</c> means nothing is currently focused.</summary>
    public void SetFocusedField(string? fieldKey) => _focusedField = fieldKey;

    /// <summary>Routes a LostFocus/Enter commit from the view to the specific field's own Commit*
    /// method, keyed by the control's <c>Tag</c>. The individual Commit* methods remain public with
    /// their original names/signatures — this is an additive entry point, not a replacement.</summary>
    public void CommitField(string fieldKey, string text)
    {
        switch (fieldKey)
        {
            case "Net":         CommitNetText(text); break;
            case "CornerRadius": CommitCornerRadiusText(text); break;
            case "Radius":       CommitRadiusText(text); break;
            case "ViaPadSize":   CommitViaPadSizeText(text); break;
            case "ViaDrillSize": CommitViaDrillSizeText(text); break;
            case "ViaX":         CommitViaXText(text); break;
            case "ViaY":         CommitViaYText(text); break;
            case "PathWidth":    CommitPathWidthText(text); break;
            case "LabelText":    CommitLabelText(text); break;
            case "LabelHeight":  CommitLabelHeightText(text); break;
            case "LabelX":       CommitLabelXText(text); break;
            case "LabelY":       CommitLabelYText(text); break;
            case "FlattenTol":   CommitFlattenTolText(text); break;
            case "RectWidth":    CommitRectWidthText(text); break;
            case "RectHeight":   CommitRectHeightText(text); break;
            case "RectX":        CommitRectXText(text); break;
            case "RectY":        CommitRectYText(text); break;
            case "BitmapPath":    CommitBitmapPathText(text); break;
            case "BitmapWidth":   CommitBitmapWidthText(text); break;
            case "BitmapHeight":  CommitBitmapHeightText(text); break;
            case "BitmapOpacity": CommitBitmapOpacityText(text); break;
            case "InstanceCellRef": CommitInstanceCellRefText(text); break;
            case "InstanceX":       CommitInstanceXText(text); break;
            case "InstanceY":       CommitInstanceYText(text); break;
            case "InstanceMag":     CommitInstanceMagText(text); break;
            case "InstanceRows":    CommitInstanceRowsText(text); break;
            case "InstanceCols":    CommitInstanceColsText(text); break;
            case "InstancePitchX":  CommitInstancePitchXText(text); break;
            case "InstancePitchY":  CommitInstancePitchYText(text); break;
        }
    }

    /// <summary>Escape — reverts ONE field to its canonical value and clears its error, bypassing the
    /// focus guard deliberately (Escape IS the explicit revert action). Reuses the full
    /// <see cref="RefreshFromVm"/> pass rather than a per-field recompute — simple, and correct since
    /// nothing else is mid-edit while Escape is pressed in a single-focus UI.</summary>
    public void RevertField(string fieldKey)
    {
        string? saved = _focusedField;
        _focusedField = null;
        RefreshFromVm();
        _focusedField = saved;
        switch (fieldKey)
        {
            case "CornerRadius": CornerRadiusError = null; break;
            case "Radius":       RadiusError = null; break;
            case "ViaPadSize":   ViaPadSizeError = null; break;
            case "ViaDrillSize": ViaDrillSizeError = null; break;
            case "ViaX":         ViaXError = null; break;
            case "ViaY":         ViaYError = null; break;
            case "PathWidth":    PathWidthError = null; break;
            case "LabelHeight":  LabelHeightError = null; break;
            case "LabelX":       LabelXError = null; break;
            case "LabelY":       LabelYError = null; break;
            case "FlattenTol":   FlattenTolError = null; break;
            case "RectWidth":    RectWidthError = null; break;
            case "RectHeight":   RectHeightError = null; break;
            case "RectX":        RectXError = null; break;
            case "RectY":        RectYError = null; break;
            case "BitmapWidth":   BitmapWidthError = null; break;
            case "BitmapHeight":  BitmapHeightError = null; break;
            case "BitmapOpacity": BitmapOpacityError = null; break;
            case "InstanceX":       InstanceXError = null; break;
            case "InstanceY":       InstanceYError = null; break;
            case "InstanceMag":     InstanceMagError = null; break;
            case "InstanceRows":    InstanceRowsError = null; break;
            case "InstanceCols":    InstanceColsError = null; break;
            case "InstancePitchX":  InstancePitchXError = null; break;
            case "InstancePitchY":  InstancePitchYError = null; break;
        }
    }

    /// <summary>Writes <paramref name="value"/> into the field named <paramref name="fieldKey"/>
    /// UNLESS that field currently has focus (R-L1j-3) — the single choke point every refresh-time
    /// text write in this class goes through.</summary>
    private void SetTextIfNotFocused(string fieldKey, string value, System.Func<string> getter, System.Action<string> setter)
    {
        if (_focusedField == fieldKey) return;
        if (getter() != value) setter(value);
    }

    // ── Layer / Net (common) ──────────────────────────────────────────────────

    public ObservableCollection<LayerPickerItem> AvailableLayers => _vm?.AvailableLayers ?? _emptyLayers;
    private static readonly ObservableCollection<LayerPickerItem> _emptyLayers = [];

    [ObservableProperty] private LayerPickerItem? _selectedLayerItem;
    [ObservableProperty] private string _netText = "";

    /// <summary>False for an all-bitmap selection (a bitmap is not electrical) AND false for an
    /// instance selection — brief-L3a-followups.md §3: <see cref="LayoutInstance"/> carries no
    /// <c>Net</c> field at all (never did — this was a display bug, not a model gap). Nets attach to
    /// conductor geometry and pins, not to a placement; the sub-cell's own port labels are what will
    /// carry nets once L5 lands. Nothing is lost by hiding this row for an instance.</summary>
    public bool ShowNet => !ShowBitmap && !IsInstanceContext;

    /// <summary>False for an instance selection — brief-L3a-followups.md §3: an instance paints on
    /// whatever layers its SUB-CELL uses (it has no <c>Layer</c> field of its own, and never did),
    /// exactly like GDSII's <c>SREF</c> carries no layer either. Hiding layer M1 already hides M1
    /// geometry INSIDE an instance via the sub-cell's own resolved technology — showing a Layer combo
    /// on the instance itself would imply a control that does nothing.</summary>
    public bool ShowLayer => !IsInstanceContext;

    partial void OnSelectedLayerItemChanged(LayerPickerItem? value)
    {
        if (_isRefreshing || value is null) return;
        CommitLayer(value.Key);
        RefreshFromVm();
    }

    public void CommitNetText(string text)
    {
        if (DragBlocksEdits()) return;
        string? newNet = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        ApplyToEach<string?>("Net", s => s.Net, (s, v) => s.Net = v, newNet);
        RefreshFromVm();
    }

    // ── Rect / RoundedRect size + position (L1j §2) ─────────────────────────────
    // R-L1j-4: editing Width/Height keeps the minimum corner (X1,Y1) FIXED and moves the far edge
    // (X2/Y2) — matches how the shape was drawn. Editing X/Y instead TRANSLATES the shape (width and
    // height are preserved) — a different, complementary semantic; see ApplyRectResize vs.
    // ApplyRectPosition below. Both are shown together, above the RoundedRect corner-radius field.

    [ObservableProperty] private bool _showRectSize;
    [ObservableProperty] private string _rectWidthText = "";
    [ObservableProperty] private string? _rectWidthError;
    public bool HasRectWidthError => RectWidthError is not null;

    [ObservableProperty] private string _rectHeightText = "";
    [ObservableProperty] private string? _rectHeightError;
    public bool HasRectHeightError => RectHeightError is not null;

    [ObservableProperty] private string _rectXText = "";
    [ObservableProperty] private string? _rectXError;
    public bool HasRectXError => RectXError is not null;

    [ObservableProperty] private string _rectYText = "";
    [ObservableProperty] private string? _rectYError;
    public bool HasRectYError => RectYError is not null;

    public void CommitRectWidthText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var w))
        { RectWidthError = "Invalid value"; return; }
        if (w <= 0) { RectWidthError = "Width must be greater than 0"; return; }
        RectWidthError = null;
        ApplyRectResize(newWidthDbu: w, newHeightDbu: null);
        RefreshFromVm();
    }

    public void CommitRectHeightText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var h))
        { RectHeightError = "Invalid value"; return; }
        if (h <= 0) { RectHeightError = "Height must be greater than 0"; return; }
        RectHeightError = null;
        ApplyRectResize(newWidthDbu: null, newHeightDbu: h);
        RefreshFromVm();
    }

    public void CommitRectXText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var x))
        { RectXError = "Invalid value"; return; }
        RectXError = null;
        ApplyRectPosition(newX1Dbu: x, newY1Dbu: null);
        RefreshFromVm();
    }

    public void CommitRectYText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var y))
        { RectYError = "Invalid value"; return; }
        RectYError = null;
        ApplyRectPosition(newX1Dbu: null, newY1Dbu: y);
        RefreshFromVm();
    }

    private static (long X1, long Y1, long X2, long Y2) RectBoundsOf(LayoutShape s) => s switch
    {
        RectShape r         => (r.X1, r.Y1, r.X2, r.Y2),
        RoundedRectShape rr => (rr.X1, rr.Y1, rr.X2, rr.Y2),
        _ => throw new System.InvalidOperationException("not a Rect/RoundedRect"),
    };

    private static void SetX1(LayoutShape s, long v) { if (s is RectShape r) r.X1 = v; else if (s is RoundedRectShape rr) rr.X1 = v; }
    private static void SetY1(LayoutShape s, long v) { if (s is RectShape r) r.Y1 = v; else if (s is RoundedRectShape rr) rr.Y1 = v; }
    private static void SetX2(LayoutShape s, long v) { if (s is RectShape r) r.X2 = v; else if (s is RoundedRectShape rr) rr.X2 = v; }
    private static void SetY2(LayoutShape s, long v) { if (s is RectShape r) r.Y2 = v; else if (s is RoundedRectShape rr) rr.Y2 = v; }

    /// <summary>R-L1j-4: keeps (X1,Y1) fixed, moves the far edge. A RoundedRect's CornerRadius is
    /// clamped to half the (possibly new) shorter side and the clamp is reported via Messages —
    /// otherwise shrinking the width/height below twice the radius would leave a geometrically
    /// invalid shape.</summary>
    private void ApplyRectResize(long? newWidthDbu, long? newHeightDbu)
    {
        if (_vm is null || _selected.Count == 0) return;

        IUiCommand? combined = null;
        bool anyClamped = false;
        foreach (var shape in _selected)
        {
            if (shape is not (RectShape or RoundedRectShape)) continue;
            var (x1, y1, x2, y2) = RectBoundsOf(shape);
            long targetX2 = newWidthDbu is { } w ? x1 + w : x2;
            long targetY2 = newHeightDbu is { } h ? y1 + h : y2;

            if (targetX2 != x2)
            {
                long oldX2 = x2, newX2 = targetX2; var captured = shape;
                IUiCommand cmd = new SetShapeFieldCommand<long>(_vm.Model, "Width", oldX2, newX2, v => SetX2(captured, v));
                combined = combined is null ? cmd : new CompositeCommand(combined, cmd);
            }
            if (targetY2 != y2)
            {
                long oldY2 = y2, newY2 = targetY2; var captured = shape;
                IUiCommand cmd = new SetShapeFieldCommand<long>(_vm.Model, "Height", oldY2, newY2, v => SetY2(captured, v));
                combined = combined is null ? cmd : new CompositeCommand(combined, cmd);
            }

            if (shape is RoundedRectShape rr)
            {
                long width = targetX2 - x1, height = targetY2 - y1;
                long maxR = System.Math.Max(0, System.Math.Min(width, height) / 2);
                if (rr.CornerRadius > maxR)
                {
                    long oldR = rr.CornerRadius; var capturedRR = rr;
                    IUiCommand cmd = new SetShapeFieldCommand<long>(_vm.Model, "Corner Radius", oldR, maxR, v => capturedRR.CornerRadius = v);
                    combined = combined is null ? cmd : new CompositeCommand(combined, cmd);
                    anyClamped = true;
                }
            }
        }

        if (combined is not null) _vm.Execute(combined);
        if (anyClamped) _vm.ReportWarning("Corner radius clamped to fit the new size.");
    }

    /// <summary>Editing X/Y TRANSLATES the shape along that axis — width/height are preserved, unlike
    /// <see cref="ApplyRectResize"/>'s fixed-min-corner semantics.</summary>
    private void ApplyRectPosition(long? newX1Dbu, long? newY1Dbu)
    {
        if (_vm is null || _selected.Count == 0) return;

        IUiCommand? combined = null;
        foreach (var shape in _selected)
        {
            if (shape is not (RectShape or RoundedRectShape)) continue;
            var (x1, y1, x2, y2) = RectBoundsOf(shape);
            long width = x2 - x1, height = y2 - y1;

            if (newX1Dbu is { } nx1 && nx1 != x1)
            {
                long oldX1 = x1, newX1v = nx1, oldX2 = x2, newX2v = nx1 + width; var captured = shape;
                IUiCommand c1 = new SetShapeFieldCommand<long>(_vm.Model, "X", oldX1, newX1v, v => SetX1(captured, v));
                IUiCommand c2 = new SetShapeFieldCommand<long>(_vm.Model, "X", oldX2, newX2v, v => SetX2(captured, v));
                var pair = new CompositeCommand(c1, c2);
                combined = combined is null ? pair : new CompositeCommand(combined, pair);
            }
            if (newY1Dbu is { } ny1 && ny1 != y1)
            {
                long oldY1 = y1, newY1v = ny1, oldY2 = y2, newY2v = ny1 + height; var captured = shape;
                IUiCommand c1 = new SetShapeFieldCommand<long>(_vm.Model, "Y", oldY1, newY1v, v => SetY1(captured, v));
                IUiCommand c2 = new SetShapeFieldCommand<long>(_vm.Model, "Y", oldY2, newY2v, v => SetY2(captured, v));
                var pair = new CompositeCommand(c1, c2);
                combined = combined is null ? pair : new CompositeCommand(combined, pair);
            }
        }

        if (combined is not null) _vm.Execute(combined);
    }

    // ── RoundedRect ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showRoundedRect;
    [ObservableProperty] private string _cornerRadiusText = "";
    [ObservableProperty] private string? _cornerRadiusError;
    public bool HasCornerRadiusError => CornerRadiusError is not null;

    public void CommitCornerRadiusText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { CornerRadiusError = "Invalid value"; return; }
        if (dbu < 0) { CornerRadiusError = "Corner radius cannot be negative"; return; }
        CornerRadiusError = null;
        ApplyToEach<long>("Corner Radius", s => ((RoundedRectShape)s).CornerRadius,
            (s, v) => ((RoundedRectShape)s).CornerRadius = v, dbu, s => s is RoundedRectShape);
        RefreshFromVm();
    }

    // ── Circle ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showCircle;
    [ObservableProperty] private string _radiusText = "";
    [ObservableProperty] private string? _radiusError;
    public bool HasRadiusError => RadiusError is not null;

    public void CommitRadiusText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { RadiusError = "Invalid value"; return; }
        if (dbu <= 0) { RadiusError = "Radius must be greater than 0"; return; }
        RadiusError = null;
        ApplyToEach<long>("Radius", s => ((CircleShape)s).R, (s, v) => ((CircleShape)s).R = v, dbu, s => s is CircleShape);
        RefreshFromVm();
    }

    // ── Via (docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.1: "pad and drill are editable
    // in the Properties Inspector") ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showVia;
    [ObservableProperty] private string _viaPadSizeText = "";
    [ObservableProperty] private string? _viaPadSizeError;
    public bool HasViaPadSizeError => ViaPadSizeError is not null;
    [ObservableProperty] private string _viaDrillSizeText = "";
    [ObservableProperty] private string? _viaDrillSizeError;
    public bool HasViaDrillSizeError => ViaDrillSizeError is not null;

    // Position: X/Y is the via's own center — both the pad and drill circles are centered there in the
    // model and the renderer, so this is a plain anchor point (mirrors LabelShape's X/Y exactly: a
    // straight translate of that one point, no separate resize semantic to disambiguate).
    [ObservableProperty] private string _viaXText = "";
    [ObservableProperty] private string? _viaXError;
    public bool HasViaXError => ViaXError is not null;
    [ObservableProperty] private string _viaYText = "";
    [ObservableProperty] private string? _viaYError;
    public bool HasViaYError => ViaYError is not null;

    public void CommitViaXText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { ViaXError = "Invalid value"; return; }
        ViaXError = null;
        ApplyToEach<long>("X", s => ((ViaShape)s).X, (s, v) => ((ViaShape)s).X = v, dbu, s => s is ViaShape);
        RefreshFromVm();
    }

    public void CommitViaYText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { ViaYError = "Invalid value"; return; }
        ViaYError = null;
        ApplyToEach<long>("Y", s => ((ViaShape)s).Y, (s, v) => ((ViaShape)s).Y = v, dbu, s => s is ViaShape);
        RefreshFromVm();
    }

    public void CommitViaPadSizeText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { ViaPadSizeError = "Invalid value"; return; }
        if (dbu <= 0) { ViaPadSizeError = "Pad must be greater than 0"; return; }
        ViaPadSizeError = null;
        ApplyToEach<long>("Pad", s => ((ViaShape)s).PadSize, (s, v) => ((ViaShape)s).PadSize = v, dbu, s => s is ViaShape);
        RefreshFromVm();
    }

    public void CommitViaDrillSizeText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { ViaDrillSizeError = "Invalid value"; return; }
        if (dbu <= 0) { ViaDrillSizeError = "Drill must be greater than 0"; return; }
        ViaDrillSizeError = null;
        ApplyToEach<long>("Drill", s => ((ViaShape)s).DrillSize, (s, v) => ((ViaShape)s).DrillSize = v, dbu, s => s is ViaShape);
        RefreshFromVm();
    }

    // ── Path ───────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showPath;
    [ObservableProperty] private string _pathWidthText = "";
    [ObservableProperty] private string? _pathWidthError;
    public bool HasPathWidthError => PathWidthError is not null;
    [ObservableProperty] private PathEndStyle? _pathEndStyleValue;

    public void CommitPathWidthText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { PathWidthError = "Invalid value"; return; }
        if (dbu <= 0) { PathWidthError = "Width must be greater than 0"; return; }
        PathWidthError = null;
        ApplyToEach<long>("Width", s => ((PathShape)s).Width, (s, v) => ((PathShape)s).Width = v, dbu, s => s is PathShape);
        RefreshFromVm();
    }

    partial void OnPathEndStyleValueChanged(PathEndStyle? oldValue, PathEndStyle? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue) return;
        if (DragBlocksEdits()) return;
        ApplyToEach<PathEndStyle>("End Style", s => ((PathShape)s).End,
            (s, v) => ((PathShape)s).End = v, newValue.Value, s => s is PathShape);
        RefreshFromVm();
    }

    // ── Label ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showLabel;
    [ObservableProperty] private string _labelText = "";
    [ObservableProperty] private string _labelHeightText = "";
    [ObservableProperty] private string? _labelHeightError;
    public bool HasLabelHeightError => LabelHeightError is not null;
    [ObservableProperty] private LayoutRotation? _labelRotationValue;
    [ObservableProperty] private LabelFontStyle? _labelStyleValue;

    // Position (owner-requested addition): a label's X/Y is a plain anchor point, not a
    // min-corner-and-size pair like Rect — editing it is a straight translate of that one point,
    // no separate "resize" semantic to disambiguate (unlike ApplyRectResize vs. ApplyRectPosition).
    [ObservableProperty] private string _labelXText = "";
    [ObservableProperty] private string? _labelXError;
    public bool HasLabelXError => LabelXError is not null;
    [ObservableProperty] private string _labelYText = "";
    [ObservableProperty] private string? _labelYError;
    public bool HasLabelYError => LabelYError is not null;

    public void CommitLabelText(string text)
    {
        if (DragBlocksEdits()) return;
        ApplyToEach<string>("Text", s => ((LabelShape)s).Text, (s, v) => ((LabelShape)s).Text = v, text ?? "", s => s is LabelShape);
        RefreshFromVm();
    }

    public void CommitLabelHeightText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { LabelHeightError = "Invalid value"; return; }
        if (dbu <= 0) { LabelHeightError = "Height must be greater than 0"; return; }
        LabelHeightError = null;
        ApplyToEach<long>("Height", s => ((LabelShape)s).Height, (s, v) => ((LabelShape)s).Height = v, dbu, s => s is LabelShape);
        RefreshFromVm();
    }

    public void CommitLabelXText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { LabelXError = "Invalid value"; return; }
        LabelXError = null;
        ApplyToEach<long>("X", s => ((LabelShape)s).X, (s, v) => ((LabelShape)s).X = v, dbu, s => s is LabelShape);
        RefreshFromVm();
    }

    public void CommitLabelYText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu))
        { LabelYError = "Invalid value"; return; }
        LabelYError = null;
        ApplyToEach<long>("Y", s => ((LabelShape)s).Y, (s, v) => ((LabelShape)s).Y = v, dbu, s => s is LabelShape);
        RefreshFromVm();
    }

    partial void OnLabelRotationValueChanged(LayoutRotation? oldValue, LayoutRotation? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue) return;
        if (DragBlocksEdits()) return;
        ApplyToEach<LayoutRotation>("Rotation", s => ((LabelShape)s).Rotation,
            (s, v) => ((LabelShape)s).Rotation = v, newValue.Value, s => s is LabelShape);
        RefreshFromVm();
    }

    partial void OnLabelStyleValueChanged(LabelFontStyle? oldValue, LabelFontStyle? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue) return;
        if (DragBlocksEdits()) return;
        ApplyToEach<LabelFontStyle>("Style", s => ((LabelShape)s).Style,
            (s, v) => ((LabelShape)s).Style = v, newValue.Value, s => s is LabelShape);
        RefreshFromVm();
    }

    // ── Bitmap (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md) ──────────────────
    // R-bmp-3: not geometry, so no flatten-tolerance/vertex-list section. Path is a free-text field
    // (Browse… is code-behind, per the UI firewall — this VM only ever sees the resulting string);
    // W/H reuse the same LayoutUnits dimension parsing every other field here uses; Opacity is a plain
    // 0-100% text field (no LayoutUnits dimension — it isn't a length); Locked is a tri-state checkbox
    // (null = differs across a multi-bitmap selection) committed immediately, like a combo selection.

    [ObservableProperty] private bool _showBitmap;
    partial void OnShowBitmapChanged(bool value) => OnPropertyChanged(nameof(ShowNet));

    [ObservableProperty] private string _bitmapPathText = "";
    [ObservableProperty] private bool _bitmapIsBroken;

    [ObservableProperty] private string _bitmapWidthText = "";
    [ObservableProperty] private string? _bitmapWidthError;
    public bool HasBitmapWidthError => BitmapWidthError is not null;

    [ObservableProperty] private string _bitmapHeightText = "";
    [ObservableProperty] private string? _bitmapHeightError;
    public bool HasBitmapHeightError => BitmapHeightError is not null;

    [ObservableProperty] private string _bitmapOpacityText = "";
    [ObservableProperty] private string? _bitmapOpacityError;
    public bool HasBitmapOpacityError => BitmapOpacityError is not null;

    [ObservableProperty] private bool? _bitmapLockedValue;

    public void CommitBitmapPathText(string text)
    {
        if (DragBlocksEdits()) return;
        string newPath = (text ?? "").Trim();
        foreach (var oldPath in _selected.OfType<BitmapShape>().Select(b => b.ImagePathRef).Distinct())
            BitmapCache.Invalidate(oldPath);
        ApplyToEach<string>("Image Path", s => ((BitmapShape)s).ImagePathRef, (s, v) => ((BitmapShape)s).ImagePathRef = v, newPath, s => s is BitmapShape);
        RefreshFromVm();
    }

    public void CommitBitmapWidthText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var w))
        { BitmapWidthError = "Invalid value"; return; }
        if (w <= 0) { BitmapWidthError = "Width must be greater than 0"; return; }
        BitmapWidthError = null;
        ApplyToEach<long>("Width", s => ((BitmapShape)s).W, (s, v) => ((BitmapShape)s).W = v, w, s => s is BitmapShape);
        RefreshFromVm();
    }

    public void CommitBitmapHeightText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var h))
        { BitmapHeightError = "Invalid value"; return; }
        if (h <= 0) { BitmapHeightError = "Height must be greater than 0"; return; }
        BitmapHeightError = null;
        ApplyToEach<long>("Height", s => ((BitmapShape)s).H, (s, v) => ((BitmapShape)s).H = v, h, s => s is BitmapShape);
        RefreshFromVm();
    }

    public void CommitBitmapOpacityText(string text)
    {
        if (DragBlocksEdits()) return;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
        { BitmapOpacityError = "Invalid value"; return; }
        double frac = pct / 100.0;
        if (frac < 0 || frac > 1) { BitmapOpacityError = "Opacity must be between 0 and 100%"; return; }
        BitmapOpacityError = null;
        ApplyToEach<double>("Opacity", s => ((BitmapShape)s).Opacity, (s, v) => ((BitmapShape)s).Opacity = v, frac, s => s is BitmapShape);
        RefreshFromVm();
    }

    partial void OnBitmapLockedValueChanged(bool? oldValue, bool? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue) return;
        if (DragBlocksEdits()) return;
        ApplyToEach<bool>("Locked", s => ((BitmapShape)s).Locked, (s, v) => ((BitmapShape)s).Locked = v, newValue.Value, s => s is BitmapShape);
        RefreshFromVm();
    }

    private static string FormatOpacityPercent(double opacity) =>
        System.Math.Round(opacity * 100.0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ── Instance (L3a §6, wired up per the owner follow-up — brief-L3a-instances-and-arrays.md's own
    // completion note named this a deferred gap). A SEPARATE top-level context, not a branch inside the
    // shape sections above: an instance has none of the shape concepts (Layer/Net/vertex list/flatten
    // tolerance) and gets its own selection list in the VM (LayoutEditorViewModel.SelectedInstanceIndices,
    // mutually exclusive with shape selection — see LayoutEditorViewModel.Instances.cs's own header).
    // Single-instance editing only, matching every existing VM-level instance-property method
    // (SetSelectedInstanceRotation, CommitSelectedInstanceArray, …) — a multi-instance selection shows a
    // summary only, mirroring the mixed-shape-type fallback elsewhere in this panel.

    [ObservableProperty] private bool _isInstanceContext;
    partial void OnIsInstanceContextChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNet));
        OnPropertyChanged(nameof(ShowLayer));
    }

    [ObservableProperty] private bool _isSingleInstanceSelected;

    /// <summary>docs/sonnet-briefs/brief-L5-followups.md §4/R-L5f-7: true when the single selected
    /// instance's resolved cell is PCell-generated — the Cell-reference field and Re-target… button
    /// hide for it (its generated cell is an implementation detail of the parameter mechanism, not
    /// something to see or repoint; §5's own parameter list is how it's edited instead). False (both
    /// fields shown) for an ordinary instance, an unresolved one, or when nothing/multiple are
    /// selected.</summary>
    [ObservableProperty] private bool _isSelectedInstancePCell;

    [ObservableProperty] private string _instanceCellRefText = "";

    [ObservableProperty] private string _instanceXText = "";
    [ObservableProperty] private string? _instanceXError;
    public bool HasInstanceXError => InstanceXError is not null;

    [ObservableProperty] private string _instanceYText = "";
    [ObservableProperty] private string? _instanceYError;
    public bool HasInstanceYError => InstanceYError is not null;

    [ObservableProperty] private LayoutRotation? _instanceRotationValue;
    [ObservableProperty] private bool? _instanceMirrorXValue;

    [ObservableProperty] private string _instanceMagText = "";
    [ObservableProperty] private string? _instanceMagError;
    public bool HasInstanceMagError => InstanceMagError is not null;

    [ObservableProperty] private string _instanceRowsText = "";
    [ObservableProperty] private string? _instanceRowsError;
    public bool HasInstanceRowsError => InstanceRowsError is not null;

    [ObservableProperty] private string _instanceColsText = "";
    [ObservableProperty] private string? _instanceColsError;
    public bool HasInstanceColsError => InstanceColsError is not null;

    [ObservableProperty] private string _instancePitchXText = "";
    [ObservableProperty] private string? _instancePitchXError;
    public bool HasInstancePitchXError => InstancePitchXError is not null;

    [ObservableProperty] private string _instancePitchYText = "";
    [ObservableProperty] private string? _instancePitchYError;
    public bool HasInstancePitchYError => InstancePitchYError is not null;

    /// <summary>Live "rows × cols = N placements" readout — blank for a plain (non-arrayed) instance,
    /// so the row is unobtrusive for the overwhelmingly common single-placement case.</summary>
    [ObservableProperty] private string _instanceArrayCountText = "";
    public bool HasInstanceArrayCount => InstanceArrayCountText.Length > 0;
    partial void OnInstanceArrayCountTextChanged(string value) => OnPropertyChanged(nameof(HasInstanceArrayCount));

    private LayoutInstance? SingleSelectedInstance => _vm?.SingleSelectedInstance;

    /// <summary>Free-text CellRef edit (LostFocus/Enter) — the companion "Re-target…" button in the
    /// view's code-behind opens the same cell-picker dialog the Instance tool uses and calls
    /// <see cref="LayoutEditorViewModel.RetargetSelectedInstance"/> directly, then this method's own
    /// <see cref="RefreshFromVm"/> call picks up the result. A refused retarget (cycle detected —
    /// reported via Messages by the VM) simply leaves the text showing the unchanged original CellRef.</summary>
    public void CommitInstanceCellRefText(string text)
    {
        if (_vm is null) return;
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) { RefreshFromVm(); return; }
        _vm.RetargetSelectedInstance(trimmed);
        RefreshFromVm();
    }

    public void CommitInstanceXText(string text)
    {
        if (_vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var x))
        { InstanceXError = "Invalid value"; return; }
        InstanceXError = null;
        _vm.CommitSelectedInstancePosition(x, null);
        RefreshFromVm();
    }

    public void CommitInstanceYText(string text)
    {
        if (_vm is null) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var y))
        { InstanceYError = "Invalid value"; return; }
        InstanceYError = null;
        _vm.CommitSelectedInstancePosition(null, y);
        RefreshFromVm();
    }

    partial void OnInstanceRotationValueChanged(LayoutRotation? oldValue, LayoutRotation? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue || _vm is null) return;
        _vm.SetSelectedInstanceRotation(newValue.Value);
        RefreshFromVm();
    }

    partial void OnInstanceMirrorXValueChanged(bool? oldValue, bool? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue || _vm is null) return;
        _vm.SetSelectedInstanceMirrorX(newValue.Value);
        RefreshFromVm();
    }

    public void CommitInstanceMagText(string text)
    {
        if (_vm is null) return;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mag) || mag <= 0)
        { InstanceMagError = "Magnification must be a positive number"; return; }
        InstanceMagError = null;
        _vm.CommitSelectedInstanceMagText(text);
        RefreshFromVm();
    }

    public void CommitInstanceRowsText(string text)
    {
        if (_vm is null || SingleSelectedInstance is not { } inst) return;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rows) || rows < 1)
        { InstanceRowsError = "Rows must be a positive integer"; return; }
        InstanceRowsError = null;
        _vm.CommitSelectedInstanceArray(rows, inst.Cols, inst.PitchX, inst.PitchY);
        RefreshFromVm();
    }

    public void CommitInstanceColsText(string text)
    {
        if (_vm is null || SingleSelectedInstance is not { } inst) return;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cols) || cols < 1)
        { InstanceColsError = "Columns must be a positive integer"; return; }
        InstanceColsError = null;
        _vm.CommitSelectedInstanceArray(inst.Rows, cols, inst.PitchX, inst.PitchY);
        RefreshFromVm();
    }

    public void CommitInstancePitchXText(string text)
    {
        if (_vm is null || SingleSelectedInstance is not { } inst) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var px))
        { InstancePitchXError = "Invalid value"; return; }
        InstancePitchXError = null;
        _vm.CommitSelectedInstanceArray(inst.Rows, inst.Cols, px, inst.PitchY);
        RefreshFromVm();
    }

    public void CommitInstancePitchYText(string text)
    {
        if (_vm is null || SingleSelectedInstance is not { } inst) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var py))
        { InstancePitchYError = "Invalid value"; return; }
        InstancePitchYError = null;
        _vm.CommitSelectedInstanceArray(inst.Rows, inst.Cols, inst.PitchX, py);
        RefreshFromVm();
    }

    private void RefreshInstanceContext()
    {
        _selected = [];
        _isRefreshing = true;
        IsEmptyState = false;
        IsInstanceContext = true;
        IsEditingEnabled = !DragBlocksEdits();

        var indices = _vm!.SelectedInstanceIndices;
        IsSingleInstanceSelected = indices.Count == 1;
        if (IsSingleInstanceSelected)
        {
            var inst = _vm.EffectiveInstanceAt(indices[0]);
            var resolution = CellLayoutResolver.Resolve(inst.CellRef, _vm.InstanceBaseDir);
            SelectionSummaryText = InstanceSummary(inst, resolution);
            IsSelectedInstancePCell = resolution is
                { State: CellLayoutState.Resolved, View.PCellOrigin: not null };
            ShowPCellParameterList = IsSelectedInstancePCell;
            // A genuinely NEW selection resets the entry-mode toggle back to canonical Z1/Z2/L — the
            // toggle is session-local display state (see its own doc comment), not something that
            // should silently carry over from whatever instance was selected before. Keyed on the
            // SELECTION INDEX, not the resolved CellRef: committing a W1/W2/F3db edit forks the
            // instance onto a NEW generated cell (R-L5-2's copy-on-write) at the SAME index — that is
            // an edit to the still-selected instance, not a new selection, and must not silently
            // revert the toggle the user is actively looking at mid-edit.
            if (_pcellEntryModeSelectionIndex != indices[0]) { MklopfUsesWidthEntry = false; MklopfUsesF3dbEntry = false; }
            _pcellEntryModeSelectionIndex = indices[0];
            IsMklopfTarget = ResolveSelectedInstancePCellComponentName() == SymbolKind.Mklopf;
            MklopfEntryModeAvailable = IsMklopfTarget && TryResolveMklopfSubstrate(out _, out _, out _);
            OnPropertyChanged(nameof(MklopfImpedanceToggleLabel));
            OnPropertyChanged(nameof(MklopfLengthToggleLabel));
            OnPropertyChanged(nameof(MklopfEntryModeDisabledReason));
            OnPropertyChanged(nameof(MklopfImpedanceToggleTip));
            OnPropertyChanged(nameof(MklopfLengthToggleTip));
            ToggleMklopfImpedanceEntryCommand.NotifyCanExecuteChanged();
            ToggleMklopfLengthEntryCommand.NotifyCanExecuteChanged();
            if (ShowPCellParameterList) RebuildOrRefreshPCellParamRows(inst);
            else { PCellParamRows = null; _pcellParamGeneratedCellDir = null; }
            SetTextIfNotFocused("InstanceCellRef", inst.CellRef ?? "", () => InstanceCellRefText, v => InstanceCellRefText = v);
            SetTextIfNotFocused("InstanceX", LayoutUnits.Format(inst.X, _vm.DisplayUnit, _vm.Model.DbuPerMicron), () => InstanceXText, v => InstanceXText = v);
            SetTextIfNotFocused("InstanceY", LayoutUnits.Format(inst.Y, _vm.DisplayUnit, _vm.Model.DbuPerMicron), () => InstanceYText, v => InstanceYText = v);
            InstanceRotationValue = inst.Rot;
            InstanceMirrorXValue = inst.MirrorX;
            SetTextIfNotFocused("InstanceMag", inst.Mag.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), () => InstanceMagText, v => InstanceMagText = v);
            SetTextIfNotFocused("InstanceRows", inst.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture), () => InstanceRowsText, v => InstanceRowsText = v);
            SetTextIfNotFocused("InstanceCols", inst.Cols.ToString(System.Globalization.CultureInfo.InvariantCulture), () => InstanceColsText, v => InstanceColsText = v);
            SetTextIfNotFocused("InstancePitchX", LayoutUnits.Format(inst.PitchX, _vm.DisplayUnit, _vm.Model.DbuPerMicron), () => InstancePitchXText, v => InstancePitchXText = v);
            SetTextIfNotFocused("InstancePitchY", LayoutUnits.Format(inst.PitchY, _vm.DisplayUnit, _vm.Model.DbuPerMicron), () => InstancePitchYText, v => InstancePitchYText = v);

            int rows = System.Math.Max(1, inst.Rows), cols = System.Math.Max(1, inst.Cols);
            long count = (long)rows * cols;
            InstanceArrayCountText = count > 1 ? $"{rows} × {cols} = {count:N0} placements" : "";
        }
        else
        {
            SelectionSummaryText = $"{indices.Count} instances selected";
            IsSelectedInstancePCell = false;
            ShowPCellParameterList = false;
            PCellParamRows = null; _pcellParamGeneratedCellDir = null;
            IsMklopfTarget = false; MklopfEntryModeAvailable = false;
            MklopfUsesWidthEntry = false; MklopfUsesF3dbEntry = false;
            _pcellEntryModeSelectionIndex = null;
            ToggleMklopfImpedanceEntryCommand.NotifyCanExecuteChanged();
            ToggleMklopfLengthEntryCommand.NotifyCanExecuteChanged();
            InstanceCellRefText = ""; InstanceXText = ""; InstanceYText = "";
            InstanceRotationValue = null; InstanceMirrorXValue = null;
            InstanceMagText = ""; InstanceRowsText = ""; InstanceColsText = "";
            InstancePitchXText = ""; InstancePitchYText = ""; InstanceArrayCountText = "";
        }

        _isRefreshing = false;
    }

    // ── Flatten tolerance (Curve / Path / Circle / RoundedRect — blank = inherit) ──────────────
    // §1.3.0 of brief-L1h-scale-and-context-menu.md: R9b says EVERY curved primitive carries a
    // flatten tolerance; L0a's table only gave it to the two edge-list types. Circle/RoundedRect
    // gained the field (LayoutModel.cs); this panel is widened to match, everywhere the predicate
    // gates on shape type.

    [ObservableProperty] private bool _showFlattenTol;
    [ObservableProperty] private string _flattenTolText = "";
    [ObservableProperty] private string? _flattenTolError;
    public bool HasFlattenTolError => FlattenTolError is not null;

    /// <summary>Shown as the tolerance box's placeholder when blank (gate 6a) — the resolved
    /// technology-inherited value, not a generic "inherit" string, so the user can see the number
    /// they're actually getting without having to type anything.</summary>
    [ObservableProperty] private string _flattenTolPlaceholder = "";

    private static bool IsCurvedPrimitive(LayoutShape s) => s is CurveShape or PathShape or CircleShape or RoundedRectShape;

    private static long? GetOwnFlattenTol(LayoutShape s) => s switch
    {
        CurveShape c       => c.FlattenTolDbu,
        PathShape p        => p.FlattenTolDbu,
        CircleShape c      => c.FlattenTolDbu,
        RoundedRectShape r => r.FlattenTolDbu,
        _                  => null,
    };

    private static void SetOwnFlattenTol(LayoutShape s, long? v)
    {
        switch (s)
        {
            case CurveShape c:       c.FlattenTolDbu = v; break;
            case PathShape p:        p.FlattenTolDbu = v; break;
            case CircleShape c:      c.FlattenTolDbu = v; break;
            case RoundedRectShape r: r.FlattenTolDbu = v; break;
        }
    }

    public void CommitFlattenTolText(string text)
    {
        if (DragBlocksEdits() || _vm is null) return;

        long? newTol;
        if (string.IsNullOrWhiteSpace(text)) newTol = null;
        else if (LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu) && dbu > 0) newTol = dbu;
        else
        {
            FlattenTolError = LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out _)
                ? "Flatten tolerance must be greater than 0"
                : "Invalid value";
            return;
        }

        FlattenTolError = null;
        ApplyToEach<long?>("Flatten Tolerance", GetOwnFlattenTol, SetOwnFlattenTol, newTol, IsCurvedPrimitive);
        RefreshFromVm();
    }

    // ── Vertex list (L1j §3) — shown for exactly one Polygon/Curve/Path, last in the panel ────────

    [ObservableProperty] private bool _showVertexList;

    private sealed record RingInfo(int Ring, string HeaderText, int VertexCount);
    private List<RingInfo>? _vertexRingPlan;
    private LazyIndexedList<object>? _vertexRowsBacking;

    /// <summary>Index-addressed, lazily-materializing row sequence: a <see cref="RingHeaderRow"/> per
    /// ring ("Outer (12)", "Hole 1 (8)", outer first — §3.1a) followed by that ring's
    /// <see cref="VertexRowViewModel"/>s. R-L1j-6: constructs a row on first access only; R-L1j-6's
    /// refresh rule (below) never replaces this instance for an unchanged ring structure.</summary>
    public LazyIndexedList<object>? VertexRows
    {
        get => _vertexRowsBacking;
        private set { if (!ReferenceEquals(_vertexRowsBacking, value)) { _vertexRowsBacking = value; OnPropertyChanged(); } }
    }

    private void RebuildOrRefreshVertexRows(LayoutShape shape)
    {
        var newPlan = BuildRingPlan(shape);
        bool structureChanged = _vertexRingPlan is null || !RingPlanMatches(_vertexRingPlan, newPlan);
        if (structureChanged)
        {
            _vertexRingPlan = newPlan;
            int totalRows = 0;
            foreach (var r in newPlan) totalRows += 1 + r.VertexCount;
            var plan = newPlan;
            VertexRows = new LazyIndexedList<object>(totalRows, i => BuildVertexRow(plan, i));
        }
        else if (_vertexRowsBacking is not null)
        {
            // R-L1j-6: same ring structure — never rebuild the collection (would thrash the panel and
            // lose scroll position/focus). Push fresh values into only the REALIZED rows.
            foreach (var idx in _vertexRowsBacking.MaterializedIndices.ToList())
                if (_vertexRowsBacking[idx] is VertexRowViewModel row) row.RefreshFromShape();
        }
    }

    private static List<RingInfo> BuildRingPlan(LayoutShape shape)
    {
        var plan = new List<RingInfo>();
        int outerCount = LayoutShapeEditing.XyOf(shape).Length / 2;
        plan.Add(new RingInfo(-1, $"Outer ({outerCount})", outerCount));
        var holes = LayoutShapeEditing.HolesOf(shape);
        if (holes is not null)
            for (int h = 0; h < holes.Count; h++)
                plan.Add(new RingInfo(h, $"Hole {h + 1} ({holes[h].Length / 2})", holes[h].Length / 2));
        return plan;
    }

    private static bool RingPlanMatches(List<RingInfo> a, List<RingInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Ring != b[i].Ring || a[i].VertexCount != b[i].VertexCount) return false;
        return true;
    }

    private object BuildVertexRow(List<RingInfo> plan, int globalIndex)
    {
        int cursor = 0;
        foreach (var ring in plan)
        {
            if (globalIndex == cursor) return new RingHeaderRow(ring.HeaderText);
            int vertexStart = cursor + 1;
            if (globalIndex < vertexStart + ring.VertexCount)
                return new VertexRowViewModel(this, ring.Ring, globalIndex - vertexStart);
            cursor = vertexStart + ring.VertexCount;
        }
        throw new System.ArgumentOutOfRangeException(nameof(globalIndex));
    }

    /// <summary>Re-reads one vertex's position/edge kind from the CURRENT single selection's
    /// effective shape (R-L1j-1) — called for a full rebuild and, live, for every already-realized
    /// row during a drag or an unrelated model change (R-L1j-6).</summary>
    internal void PopulateVertexRow(VertexRowViewModel row)
    {
        if (_vm is null || _selected.Count != 1) return;
        var shape = _selected[0];
        long[]? xy = row.Ring < 0 ? LayoutShapeEditing.XyOf(shape) : LayoutShapeEditing.HolesOf(shape)?.ElementAtOrDefault(row.Ring);
        if (xy is null || row.VertexIndex * 2 + 1 >= xy.Length) return; // stale row past a structural change

        long x = xy[2 * row.VertexIndex], y = xy[2 * row.VertexIndex + 1];
        if (_focusedField != row.FieldKeyX) row.XText = LayoutUnits.Format(x, _vm.DisplayUnit, _vm.Model.DbuPerMicron);
        if (_focusedField != row.FieldKeyY) row.YText = LayoutUnits.Format(y, _vm.DisplayUnit, _vm.Model.DbuPerMicron);
        row.EdgeText = ComputeEdgeText(shape, row);
    }

    private static string ComputeEdgeText(LayoutShape shape, VertexRowViewModel row)
    {
        if (row.Ring >= 0) return "Line"; // §3.1a: holes are always plain polygons, never their own edge list
        var edges = LayoutShapeEditing.EdgesOf(shape);
        bool closed = LayoutShapeEditing.IsClosed(shape);
        int vertexCount = LayoutShapeEditing.XyOf(shape).Length / 2;
        if (!closed && row.VertexIndex == vertexCount - 1) return ""; // open Path's last vertex has no outgoing edge
        if (edges is null) return "Line";
        return row.VertexIndex < edges.Count ? edges[row.VertexIndex].Kind.ToString() : "Line";
    }

    /// <summary>Committing a vertex edit is ONE <see cref="ReplaceShapeCommand"/> (L1d's single
    /// geometry-edit command) — mirrors exactly what a canvas vertex-drag commits, so undo restores
    /// the shape at its original index and the Polygon→Curve promotion rule keeps working unchanged.</summary>
    internal void CommitVertexField(VertexRowViewModel row, bool isX, string text)
    {
        if (DragBlocksEdits() || _vm is null || _selected.Count != 1) return;
        if (!LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var v))
        { row.Error = "Invalid value"; return; }
        row.Error = null;

        var shape = _selected[0];
        long[]? xy = row.Ring < 0 ? LayoutShapeEditing.XyOf(shape) : LayoutShapeEditing.HolesOf(shape)?.ElementAtOrDefault(row.Ring);
        if (xy is null || row.VertexIndex * 2 + 1 >= xy.Length) return;

        long curX = xy[2 * row.VertexIndex], curY = xy[2 * row.VertexIndex + 1];
        long newX = isX ? v : curX, newY = isX ? curY : v;
        if (newX == curX && newY == curY) return;

        int shapeIndex = SingleSelectedIndex();
        LayoutShape after = row.Ring < 0
            ? LayoutShapeEditing.SetVertex(shape, row.VertexIndex, newX, newY)
            : LayoutShapeEditing.SetHoleVertex(shape, row.Ring, row.VertexIndex, newX, newY);
        _vm.Execute(new ReplaceShapeCommand(_vm.Model, shapeIndex, shape, after));
        RefreshFromVm();
    }

    /// <summary>Escape on a vertex row's X or Y — reverts just that field, bypassing the focus guard.
    /// Cheaper than <see cref="RevertField"/>: only this one row needs to re-read, not the whole panel.</summary>
    internal void RevertVertexField(VertexRowViewModel row, bool isY)
    {
        string? saved = _focusedField;
        _focusedField = null;
        row.RefreshFromShape();
        _focusedField = saved;
        row.Error = null;
    }

    // ── PCell parameter list (brief-L5-followups.md §5/R-L5f-8) — shown for exactly one PCell
    // instance, in the SAME bounded region the vertex list uses (mutually exclusive: a selection is
    // either shape context or instance context, never both — see the shared Grid.Row="1" in the view).

    [ObservableProperty] private bool _showPCellParameterList;

    // ── MKlopf entry-mode toggle (brief-L5-followups-2.md §1/R-L5g-1) ──────────────────────────────
    // Unlike the schematic side (ParameterEditorViewModel), the generated cell's own PCellOrigin.
    // Parameters ALWAYS holds the CANONICAL Z1/Z2/L set (see OrderedParamNames' own doc comment) — a
    // layout instance has no separate "which route is currently declared" state to persist. So the
    // toggle here is PURELY a session-local DISPLAY choice, reset on every new selection: it decides
    // whether the row list shows Z1/Z2/L or W1/W2/F3db, and committing an edit in the alternate route
    // converts back to canonical Z1/Z2/L before calling EditInstancePCellParameters — there is nothing
    // to undo for the toggle itself (only an actual value edit is undoable, exactly as for any other
    // row). "Last-edited field is authoritative and never written back from the other" (the Scale-
    // dialog rule) falls out for free: the alternate-route value is always FRESHLY RE-DERIVED from
    // whatever the canonical value currently is, every time a row repopulates — nothing is cached
    // across edits that could go stale or disagree.
    [ObservableProperty] private bool _mklopfUsesWidthEntry;
    [ObservableProperty] private bool _mklopfUsesF3dbEntry;

    /// <summary>True only when the single selected instance resolves to an MKLOPF-generated cell —
    /// gates the toggle buttons themselves (they have no meaning for any other PCell).</summary>
    public bool IsMklopfTarget { get; private set; }

    /// <summary>R-L5g-1's own "disable with a reason" requirement: the Z1/Z2⇄W1/W2 and L⇄F3db
    /// conversions both need a resolved substrate (H/T/Er) — <see cref="TryResolveMklopfSubstrate"/>
    /// is the ONE place that resolution happens, the SAME <see cref="SubstrateResolver.ResolveElectrical"/>
    /// call <c>MKlopfPCell.Generate</c> itself uses (against <see cref="PCellLayerSelection.Default"/> —
    /// a per-instance layer override is a narrow, named simplification: the common case, per this
    /// codebase's own "empty means follow the technology" convention, is that no override is set).</summary>
    public bool MklopfEntryModeAvailable { get; private set; }

    public string MklopfImpedanceToggleLabel => MklopfUsesWidthEntry ? "Use Z1/Z2" : "Use W1/W2";
    public string MklopfLengthToggleLabel     => MklopfUsesF3dbEntry  ? "Use L"     : "Use F3db";

    /// <summary>brief-L5-followups-3.md §4 (R-L5h-9): "a disabled control must say why" — non-null
    /// only when the toggle genuinely cannot act right now, so a plain click on a technology-less
    /// document never reads as "pressed it and nothing happened."</summary>
    public string? MklopfEntryModeDisabledReason =>
        IsMklopfTarget && !MklopfEntryModeAvailable
            ? "No technology resolves for this document — can't convert Z1/Z2 ⇄ W1/W2 or L ⇄ F3db." : null;

    public string MklopfImpedanceToggleTip => MklopfEntryModeDisabledReason ?? "Switch between Z1/Z2 and W1/W2 entry";
    public string MklopfLengthToggleTip     => MklopfEntryModeDisabledReason ?? "Switch between L and F3db entry";

    [RelayCommand(CanExecute = nameof(CanToggleMklopfEntry))]
    private void ToggleMklopfImpedanceEntry()
    {
        MklopfUsesWidthEntry = !MklopfUsesWidthEntry;
        if (SingleSelectedInstance is { } inst) RebuildOrRefreshPCellParamRows(inst, forceRebuild: true);
    }

    [RelayCommand(CanExecute = nameof(CanToggleMklopfEntry))]
    private void ToggleMklopfLengthEntry()
    {
        MklopfUsesF3dbEntry = !MklopfUsesF3dbEntry;
        if (SingleSelectedInstance is { } inst) RebuildOrRefreshPCellParamRows(inst, forceRebuild: true);
    }

    private bool CanToggleMklopfEntry() => IsMklopfTarget && MklopfEntryModeAvailable;

    /// <summary>See <see cref="MklopfEntryModeAvailable"/>'s own doc comment. Returns false (and
    /// leaves h/t/er at 0) when no technology resolves — the caller must never show a conversion
    /// computed from these in that case.</summary>
    private bool TryResolveMklopfSubstrate(out double h, out double t, out double er)
    {
        h = t = er = 0;
        if (_vm?.Technology is not { } tech) return false;
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        if (substrate is null) return false;
        h = substrate.HeightMeters; t = substrate.ThicknessMeters; er = substrate.RelativePermittivity;
        return true;
    }

    private string? _pcellParamGeneratedCellDir; // which generated cell the current row set was built from
    private int? _pcellEntryModeSelectionIndex; // which SelectedInstanceIndices[0] the entry-mode toggle belongs to
    private LazyIndexedList<PCellParamRowViewModel>? _pcellParamRowsBacking;

    /// <summary>Index-addressed, lazily-materializing row sequence — R-L1j-6's pattern (this codebase's
    /// established fix for "Avalonia virtualizes containers, not items"), reused verbatim rather than
    /// reinvented for a second list type.</summary>
    public LazyIndexedList<PCellParamRowViewModel>? PCellParamRows
    {
        get => _pcellParamRowsBacking;
        private set { if (!ReferenceEquals(_pcellParamRowsBacking, value)) { _pcellParamRowsBacking = value; OnPropertyChanged(); } }
    }

    /// <summary>Ordered parameter names for <paramref name="origin"/> — <c>ComponentTypeRegistry.
    /// DefaultParameters</c>' own declared order (matching the schematic's own symbol) filtered to the
    /// names actually present on this generated cell (a content-addressed cell's <c>PCellOrigin.
    /// Parameters</c> always holds the CANONICAL set — e.g. MKlopf's Z1/Z2/L, never the alternate
    /// W1/W2/F3db entry routes, which are converted away before the cell is ever created — so no
    /// "which entry route" filtering is needed for the underlying storage, unlike §1's schematic-side
    /// resolution). R-L5g-1: when the entry-mode toggle is active for an MKLOPF target, "Z1"/"Z2" are
    /// swapped for the PSEUDO-names "W1"/"W2" at the SAME list positions (and "L" for "F3db") — these
    /// never exist in <paramref name="origin"/>.Parameters itself; <see cref="PopulatePCellParamRow"/>/
    /// <see cref="CommitPCellParamField"/> both special-case them, converting on the way in and out.</summary>
    private List<string> OrderedParamNames(PCellOrigin origin)
    {
        var ordered = new List<string>();
        if (LayoutToSchematicGenerator.TryGetSymbolKind(origin.GeneratorId, out var kind))
            foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
                if (origin.Parameters.ContainsKey(dp.Name) && !ordered.Contains(dp.Name))
                    ordered.Add(dp.Name);
        foreach (var name in origin.Parameters.Keys) // defensive: any name the registry didn't name (shouldn't happen)
            if (!ordered.Contains(name)) ordered.Add(name);

        if (IsMklopfTarget)
        {
            if (MklopfUsesWidthEntry)
            {
                int i1 = ordered.IndexOf("Z1"); if (i1 >= 0) ordered[i1] = "W1";
                int i2 = ordered.IndexOf("Z2"); if (i2 >= 0) ordered[i2] = "W2";
            }
            if (MklopfUsesF3dbEntry)
            {
                int iL = ordered.IndexOf("L"); if (iL >= 0) ordered[iL] = "F3db";
            }
        }
        return ordered;
    }

    private void RebuildOrRefreshPCellParamRows(LayoutInstance inst, bool forceRebuild = false)
    {
        var res = CellLayoutResolver.Resolve(inst.CellRef, _vm!.InstanceBaseDir);
        if (res.State != CellLayoutState.Resolved || res.View!.PCellOrigin is not { } origin)
        { PCellParamRows = null; _pcellParamGeneratedCellDir = null; return; }

        string cellKey = inst.CellRef; // content-addressed: a different value set is always a different CellRef
        if (forceRebuild || _pcellParamRowsBacking is null || _pcellParamGeneratedCellDir != cellKey)
        {
            _pcellParamGeneratedCellDir = cellKey;
            var names = OrderedParamNames(origin);
            PCellParamRows = new LazyIndexedList<PCellParamRowViewModel>(names.Count, i => BuildPCellParamRow(names, i));
        }
        else
        {
            foreach (var idx in _pcellParamRowsBacking.MaterializedIndices.ToList())
                _pcellParamRowsBacking[idx].RefreshFromInstance();
        }
    }

    /// <summary>The entry-mode pseudo-names' own units — hardcoded, mirroring exactly what the
    /// schematic side's own toggle hardcodes (<c>ParameterEditorViewModel.MklopfParam</c> calls):
    /// W1/W2 are lengths ("mm", routed through the layout's own display unit like any other length
    /// row), F3db is a frequency ("GHz" — no workspace-technology convention of its own, same reason
    /// the schematic side never varies it either).</summary>
    private static string? MklopfPseudoParamUnit(string name) => name switch
    {
        "W1" or "W2" => "mm",
        "F3db"        => "GHz",
        _             => null,
    };

    private PCellParamRowViewModel BuildPCellParamRow(List<string> names, int index)
    {
        string name = names[index];
        if (MklopfPseudoParamUnit(name) is { } pseudoUnit)
            return new PCellParamRowViewModel(this, name, pseudoUnit);

        var comp = ResolveSelectedInstancePCellComponentName(); // just for the DefaultParameters unit lookup
        string unit = "";
        if (comp is { } kind)
        {
            var dps = ComponentTypeRegistry.DefaultParameters(kind, 0);
            foreach (var dp in dps)
                if (dp.Name == name) { unit = dp.Unit; break; }
        }
        return new PCellParamRowViewModel(this, name, unit);
    }

    private SymbolKind? ResolveSelectedInstancePCellComponentName()
    {
        if (_vm is null || SingleSelectedInstance is not { } inst) return null;
        var res = CellLayoutResolver.Resolve(inst.CellRef, _vm.InstanceBaseDir);
        if (res.State == CellLayoutState.Resolved && res.View!.PCellOrigin is { } origin &&
            LayoutToSchematicGenerator.TryGetSymbolKind(origin.GeneratorId, out var kind))
            return kind;
        return null;
    }

    /// <summary>Re-reads one parameter's current SI value from the selected instance's resolved
    /// generated cell and formats it — length ("mm") parameters through the LAYOUT's own display unit
    /// (R-L5f-8: "like every other dimension field"), everything else (Ω, deg, dimensionless) through
    /// its own natural unit, SI underneath either way (R-pc-6).</summary>
    internal void PopulatePCellParamRow(PCellParamRowViewModel row)
    {
        if (_vm is null || SingleSelectedInstance is not { } inst || _focusedField == row.FieldKey) return;
        var res = CellLayoutResolver.Resolve(inst.CellRef, _vm.InstanceBaseDir);
        if (res.State != CellLayoutState.Resolved || res.View!.PCellOrigin is not { } origin) return;

        if (row.Name is "W1" or "W2" or "F3db")
        {
            if (!TryResolveMklopfSubstrate(out double h, out double t, out double er))
            { row.ValueText = ""; row.Error = "No technology resolves — can't convert."; return; }
            double z1 = origin.Parameters.GetValueOrDefault("Z1", 50.0);
            double z2 = origin.Parameters.GetValueOrDefault("Z2", 50.0);
            var reporter = new MicrostripValidityReporter("(layout entry-mode display)");
            double converted;
            if (row.Name is "W1" or "W2")
            {
                var (w1, w2) = MicrostripKlopfEntryConversion.ImpedanceToWidth(z1, z2, h, t, er, reporter);
                converted = row.Name == "W1" ? w1 : w2;
            }
            else
            {
                double gammaMax = origin.Parameters.GetValueOrDefault("GammaMax", 0.05);
                double l = origin.Parameters.GetValueOrDefault("L", 0.02);
                converted = MicrostripKlopfEntryConversion.LengthToF3db(z1, z2, gammaMax, l, h, t, er, reporter);
            }
            row.Error = null;
            row.ValueText = FormatPCellParamValue(row.Unit, converted);
            return;
        }

        if (!origin.Parameters.TryGetValue(row.Name, out double siValue)) return;

        row.ValueText = FormatPCellParamValue(row.Unit, siValue);
    }

    private string FormatPCellParamValue(string unit, double siValue)
    {
        if (string.Equals(unit, "mm", System.StringComparison.Ordinal))
        {
            long dbu = PCellUnits.MetresToDbu(siValue, _vm!.Model.DbuPerMicron);
            return LayoutUnits.Format(dbu, _vm.DisplayUnit, _vm.Model.DbuPerMicron);
        }
        string display = SchematicToLayoutGenerator.Fmt(SchematicToLayoutGenerator.ToDisplayValue(unit, siValue));
        return string.IsNullOrEmpty(unit) ? display : $"{display} {unit}";
    }

    private bool TryParsePCellParamValue(string unit, string text, out double siValue)
    {
        siValue = 0;
        string trimmed = text.Trim();
        if (string.Equals(unit, "mm", System.StringComparison.Ordinal))
        {
            if (!LayoutUnits.TryParse(trimmed, _vm!.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu)) return false;
            decimal mm = LayoutUnits.FromDbu(dbu, LayoutUnit.Mm, _vm.Model.DbuPerMicron);
            siValue = (double)mm / 1000.0; // mm -> metres
            return true;
        }

        // Strip a trailing unit suffix the display itself would have appended (e.g. "50 Ω", "90 deg").
        if (!string.IsNullOrEmpty(unit) && trimmed.EndsWith(unit, System.StringComparison.Ordinal))
            trimmed = trimmed[..^unit.Length].TrimEnd();
        if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double raw))
            return false;
        // "deg"/Ω/dimensionless pass through unchanged (Units.Scale is 1.0 for Ω and undefined for a
        // blank unit; "deg" is EXCLUDED deliberately — see ToDisplayValue's own doc comment: degrees
        // are already the literal storage unit for an Angle-dimensioned PCell parameter). Any OTHER
        // unit (fixed R-L5g-1: previously this branch never scaled at all, silently wrong for a unit
        // like "GHz" whose scale isn't 1 — latent until F3db's entry-mode row exposed it, since every
        // pre-existing PCell param unit here happened to have scale 1) is the exact inverse of
        // ToDisplayValue's own division — multiply back by the same Units.Scale.
        if (!string.IsNullOrEmpty(unit) && !string.Equals(unit, "deg", System.StringComparison.Ordinal))
        {
            double? scale = CircuitRF.Core.Expressions.Units.Scale(unit);
            if (scale is > 0) raw *= scale.Value;
        }
        siValue = raw;
        return true;
    }

    /// <summary>R-L5f-9: copy-on-write — routes through <see cref="LayoutEditorViewModel.
    /// EditInstancePCellParameters"/>, the SAME repoint-to-whatever-cell-the-new-values-hash-to
    /// mechanism the Properties Inspector's own instance CellRef re-target uses internally; a sibling
    /// instance referencing the pre-edit cell is untouched by construction (nothing here mutates the
    /// generated cell in place).</summary>
    internal void CommitPCellParamField(PCellParamRowViewModel row, string text)
    {
        if (DragBlocksEdits() || _vm is null) return;
        var indices = _vm.SelectedInstanceIndices;
        if (indices.Count != 1) return;

        if (row.Name is "W1" or "W2" or "F3db")
        {
            CommitMklopfPseudoParamField(row, text, indices[0]);
            return;
        }

        if (!TryParsePCellParamValue(row.Unit, text, out var siValue))
        { row.Error = "Invalid value"; return; }
        row.Error = null;

        _vm.EditInstancePCellParameters(indices[0], new Dictionary<string, double> { [row.Name] = siValue });
        RefreshFromVm();
    }

    /// <summary>
    /// R-L5g-1: commits a W1/W2/F3db entry-mode edit by converting it BACK to the canonical Z1/Z2/L
    /// keys <c>EditInstancePCellParameters</c> (and the generator itself) actually understand — the
    /// generated cell's own storage never carries these pseudo-names (see <see cref="OrderedParamNames"/>'s
    /// own doc comment). "Last-edited field is authoritative" (the Scale-dialog rule): the OTHER width
    /// in a W1/W2 pair is re-derived from whatever the CURRENT canonical Z1/Z2 resolves to — never a
    /// stale cached value — so a single-field edit never silently overwrites its sibling with anything
    /// but that sibling's own current, real value.
    /// </summary>
    private void CommitMklopfPseudoParamField(PCellParamRowViewModel row, string text, int instanceIndex)
    {
        if (!TryParsePCellParamValue(row.Unit, text, out var newValueSi))
        { row.Error = "Invalid value"; return; }

        var inst = _vm!.Model.Instances[instanceIndex];
        var res = CellLayoutResolver.Resolve(inst.CellRef, _vm.InstanceBaseDir);
        if (res.State != CellLayoutState.Resolved || res.View!.PCellOrigin is not { } origin)
        { row.Error = "Instance no longer resolves"; return; }
        if (!TryResolveMklopfSubstrate(out double h, out double t, out double er))
        { row.Error = "No technology resolves — can't convert."; return; }

        double z1cur = origin.Parameters.GetValueOrDefault("Z1", 50.0);
        double z2cur = origin.Parameters.GetValueOrDefault("Z2", 50.0);
        var reporter = new MicrostripValidityReporter("(layout entry-mode edit)");

        if (row.Name is "W1" or "W2")
        {
            var (w1cur, w2cur) = MicrostripKlopfEntryConversion.ImpedanceToWidth(z1cur, z2cur, h, t, er, reporter);
            double w1 = row.Name == "W1" ? newValueSi : w1cur;
            double w2 = row.Name == "W2" ? newValueSi : w2cur;
            var (z1, z2) = MicrostripKlopfEntryConversion.WidthToImpedance(w1, w2, h, t, er, reporter);
            row.Error = null;
            _vm.EditInstancePCellParameters(instanceIndex, new Dictionary<string, double> { ["Z1"] = z1, ["Z2"] = z2 });
        }
        else // "F3db"
        {
            double gammaMax = origin.Parameters.GetValueOrDefault("GammaMax", 0.05);
            double l = MicrostripKlopfEntryConversion.F3dbToLength(z1cur, z2cur, gammaMax, newValueSi, h, t, er, reporter);
            row.Error = null;
            _vm.EditInstancePCellParameters(instanceIndex, new Dictionary<string, double> { ["L"] = l });
        }
        RefreshFromVm();
    }

    /// <summary>The single valid model index behind <c>_selected[0]</c> — matches
    /// <see cref="LayoutEditorViewModel.EffectiveSelectedShapes"/>'s own filtering exactly, so this is
    /// never off-by-one even if <c>SelectedIndices</c> happens to carry a stale out-of-range entry
    /// alongside the one valid selection.</summary>
    private int SingleSelectedIndex() =>
        _vm!.SelectedIndices.First(i => i >= 0 && i < _vm.Model.Shapes.Count);

    // ── Context binding ────────────────────────────────────────────────────────

    public void SetContext(LayoutEditorViewModel? vm)
    {
        if (_vm is not null) { _vm.PropertyChanged -= OnVmPropertyChanged; _vm.Model.Changed -= OnModelChanged; }
        _vm = vm;
        if (_vm is not null) { _vm.PropertyChanged += OnVmPropertyChanged; _vm.Model.Changed += OnModelChanged; }
        OnPropertyChanged(nameof(AvailableLayers));
        RefreshFromVm();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutEditorViewModel.Overlay))
            RefreshFromVm();
        else if (e.PropertyName is nameof(LayoutEditorViewModel.Technology))
        {
            OnPropertyChanged(nameof(AvailableLayers));
            // brief-L5-followups-3.md §4 (R-L5h-8): the root cause of "the MKlopf entry-mode toggles
            // are always disabled" — Technology resolves CORRECTLY (confirmed directly: the same
            // SubstrateResolver.ResolveElectrical call the PCell generator itself uses, against the
            // SAME vm.Technology this panel already reads), but a resolution that lands AFTER an MKlopf
            // instance is already selected — e.g. the orphan-technology prompt resolving asynchronously
            // post-open, or any later live .ctech change/retarget — never got picked up: only
            // AvailableLayers was re-raised here, never a re-evaluation of MklopfEntryModeAvailable (or
            // any other Technology-dependent field this panel shows). A snapshot taken WHILE
            // Technology was still null/stale therefore stayed disabled forever, even after Technology
            // resolved — exactly the "pressed it and nothing happened" symptom. RefreshFromVm() is the
            // same call SetContext/instance-selection already trigger, so this is not a new code path,
            // only a missing subscription to an event that already fires.
            RefreshFromVm();
        }
    }

    private void OnModelChanged(object? sender, System.EventArgs e) => RefreshFromVm();

    // ── Refresh ────────────────────────────────────────────────────────────────

    private void RefreshFromVm()
    {
        if (_vm is null) { SetEmpty("No active layout."); return; }

        if (_vm.SelectedInstanceIndices.Count > 0) { RefreshInstanceContext(); return; }

        _selected = _vm.EffectiveSelectedShapes().ToList(); // R-L1j-1: drag-override-aware

        if (_selected.Count == 0) { SetEmpty("Select a shape or instance to inspect."); return; }

        _isRefreshing = true;
        IsEmptyState = false;
        IsInstanceContext = false;
        ShowPCellParameterList = false;
        PCellParamRows = null; _pcellParamGeneratedCellDir = null;
        _pcellEntryModeSelectionIndex = null;
        IsEditingEnabled = !DragBlocksEdits(); // R-L1j-2

        SelectionSummaryText = _selected.Count == 1
            ? ShapeTypeName(_selected[0])
            : $"{_selected.Count} shapes selected";

        var sharedLayer = _selected[0].Layer;
        bool layerSame = _selected.All(s => s.Layer == sharedLayer);
        SelectedLayerItem = layerSame ? AvailableLayers.FirstOrDefault(l => l.Key == sharedLayer) : null;

        var sharedNet = _selected[0].Net;
        bool netSame = _selected.All(s => s.Net == sharedNet);
        SetTextIfNotFocused("Net", netSame ? (sharedNet ?? "") : "", () => NetText, v => NetText = v);

        ShowRectSize = _selected.All(s => s is RectShape or RoundedRectShape);
        if (ShowRectSize)
        {
            var bounds = _selected.Select(RectBoundsOf).ToList();
            SetTextIfNotFocused("RectWidth",  FormatSharedDbu(bounds.Select(b => (long?)(b.X2 - b.X1))), () => RectWidthText,  v => RectWidthText = v);
            SetTextIfNotFocused("RectHeight", FormatSharedDbu(bounds.Select(b => (long?)(b.Y2 - b.Y1))), () => RectHeightText, v => RectHeightText = v);
            SetTextIfNotFocused("RectX",      FormatSharedDbu(bounds.Select(b => (long?)b.X1)),          () => RectXText,      v => RectXText = v);
            SetTextIfNotFocused("RectY",      FormatSharedDbu(bounds.Select(b => (long?)b.Y1)),          () => RectYText,      v => RectYText = v);
        }

        ShowRoundedRect = _selected.All(s => s is RoundedRectShape);
        if (ShowRoundedRect)
            SetTextIfNotFocused("CornerRadius", FormatSharedDbu(_selected.Cast<RoundedRectShape>().Select(s => (long?)s.CornerRadius)),
                () => CornerRadiusText, v => CornerRadiusText = v);

        ShowCircle = _selected.All(s => s is CircleShape);
        if (ShowCircle)
            SetTextIfNotFocused("Radius", FormatSharedDbu(_selected.Cast<CircleShape>().Select(s => (long?)s.R)),
                () => RadiusText, v => RadiusText = v);

        ShowVia = _selected.All(s => s is ViaShape);
        if (ShowVia)
        {
            var vias = _selected.Cast<ViaShape>().ToList();
            SetTextIfNotFocused("ViaPadSize", FormatSharedDbu(vias.Select(v => (long?)v.PadSize)), () => ViaPadSizeText, v => ViaPadSizeText = v);
            SetTextIfNotFocused("ViaDrillSize", FormatSharedDbu(vias.Select(v => (long?)v.DrillSize)), () => ViaDrillSizeText, v => ViaDrillSizeText = v);
            SetTextIfNotFocused("ViaX", FormatSharedDbu(vias.Select(v => (long?)v.X)), () => ViaXText, v => ViaXText = v);
            SetTextIfNotFocused("ViaY", FormatSharedDbu(vias.Select(v => (long?)v.Y)), () => ViaYText, v => ViaYText = v);
        }

        ShowPath = _selected.All(s => s is PathShape);
        if (ShowPath)
        {
            var paths = _selected.Cast<PathShape>().ToList();
            SetTextIfNotFocused("PathWidth", FormatSharedDbu(paths.Select(p => (long?)p.Width)), () => PathWidthText, v => PathWidthText = v);
            var ends = paths.Select(p => p.End).Distinct().ToList();
            PathEndStyleValue = ends.Count == 1 ? ends[0] : null;
        }

        ShowLabel = _selected.All(s => s is LabelShape);
        if (ShowLabel)
        {
            var labels = _selected.Cast<LabelShape>().ToList();
            var texts = labels.Select(l => l.Text).Distinct().ToList();
            SetTextIfNotFocused("LabelText", texts.Count == 1 ? texts[0] : "", () => LabelText, v => LabelText = v);
            SetTextIfNotFocused("LabelHeight", FormatSharedDbu(labels.Select(l => (long?)l.Height)), () => LabelHeightText, v => LabelHeightText = v);
            var rots = labels.Select(l => l.Rotation).Distinct().ToList();
            LabelRotationValue = rots.Count == 1 ? rots[0] : null;
            var styles = labels.Select(l => l.Style).Distinct().ToList();
            LabelStyleValue = styles.Count == 1 ? styles[0] : null;
            SetTextIfNotFocused("LabelX", FormatSharedDbu(labels.Select(l => (long?)l.X)), () => LabelXText, v => LabelXText = v);
            SetTextIfNotFocused("LabelY", FormatSharedDbu(labels.Select(l => (long?)l.Y)), () => LabelYText, v => LabelYText = v);
        }

        ShowBitmap = _selected.All(s => s is BitmapShape);
        if (ShowBitmap)
        {
            var bmps = _selected.Cast<BitmapShape>().ToList();
            var paths = bmps.Select(b => b.ImagePathRef).Distinct().ToList();
            SetTextIfNotFocused("BitmapPath", paths.Count == 1 ? paths[0] : "", () => BitmapPathText, v => BitmapPathText = v);
            BitmapIsBroken = bmps.Count == 1
                && (string.IsNullOrEmpty(bmps[0].ImagePathRef) || !System.IO.File.Exists(bmps[0].ImagePathRef));

            SetTextIfNotFocused("BitmapWidth", FormatSharedDbu(bmps.Select(b => (long?)b.W)), () => BitmapWidthText, v => BitmapWidthText = v);
            SetTextIfNotFocused("BitmapHeight", FormatSharedDbu(bmps.Select(b => (long?)b.H)), () => BitmapHeightText, v => BitmapHeightText = v);

            var opacities = bmps.Select(b => b.Opacity).Distinct().ToList();
            SetTextIfNotFocused("BitmapOpacity", opacities.Count == 1 ? FormatOpacityPercent(opacities[0]) : "",
                () => BitmapOpacityText, v => BitmapOpacityText = v);

            var lockedVals = bmps.Select(b => b.Locked).Distinct().ToList();
            BitmapLockedValue = lockedVals.Count == 1 ? lockedVals[0] : null;
        }

        ShowFlattenTol = _selected.All(IsCurvedPrimitive);
        if (ShowFlattenTol)
        {
            var tols = _selected.Select(GetOwnFlattenTol).Distinct().ToList();
            string tolText = tols.Count == 1 && tols[0] is { } t
                ? LayoutUnits.Format(t, _vm.DisplayUnit, _vm.Model.DbuPerMicron)
                : "";
            SetTextIfNotFocused("FlattenTol", tolText, () => FlattenTolText, v => FlattenTolText = v);
            // The placeholder always shows what a BLANK field resolves to — the technology default
            // (or the hardcoded fallback with none) — never a shape's own override, which is what
            // ResolveTolDbu would return if the representative shape happened to have one set.
            long inherited = _vm.Technology is { DefaultFlattenTolDbu: > 0 } tech
                ? tech.DefaultFlattenTolDbu : LayoutFlattener.DefaultTolDbu;
            FlattenTolPlaceholder = LayoutUnits.Format(inherited, _vm.DisplayUnit, _vm.Model.DbuPerMicron) + " (from technology)";
        }

        ShowVertexList = _selected.Count == 1 && LayoutShapeEditing.IsVertexListShape(_selected[0]);
        if (ShowVertexList) RebuildOrRefreshVertexRows(_selected[0]);
        else { VertexRows = null; _vertexRingPlan = null; }

        _isRefreshing = false;
    }

    private void SetEmpty(string message)
    {
        _selected = [];
        _isRefreshing = true;
        IsEmptyState = true;
        EmptyMessage = message;
        SelectionSummaryText = "";
        IsEditingEnabled = true;
        IsInstanceContext = false;
        ShowRoundedRect = ShowCircle = ShowVia = ShowPath = ShowLabel = ShowFlattenTol = ShowRectSize = ShowVertexList = ShowBitmap = false;
        ShowPCellParameterList = false;
        PCellParamRows = null; _pcellParamGeneratedCellDir = null;
        _pcellEntryModeSelectionIndex = null;
        FlattenTolPlaceholder = "";
        BitmapIsBroken = false;
        BitmapLockedValue = null;
        VertexRows = null;
        _vertexRingPlan = null;
        _isRefreshing = false;
    }

    private string FormatSharedDbu(IEnumerable<long?> values)
    {
        var distinct = values.Distinct().ToList();
        return distinct.Count == 1 && distinct[0] is { } v && _vm is not null
            ? LayoutUnits.Format(v, _vm.DisplayUnit, _vm.Model.DbuPerMicron)
            : "";
    }

    /// <summary>
    /// Header line for a single selected instance: WHAT it is, plus the schematic instance it came
    /// from when there is one — e.g. <c>"MLIN · ML1"</c>, <c>"OutputMatch · X3"</c>, or a
    /// bare <c>"OutputMatch"</c> for a layout-authored instance with no schematic counterpart.
    ///
    /// <para>Replaces a bare <c>"Instance"</c>, which said nothing a user could act on — every
    /// instance looked identical in the inspector regardless of what it actually was. Plain shapes
    /// have always named their own type here (<see cref="ShapeTypeName"/>); instances now do too.</para>
    ///
    /// <para><b>A PCell is named by its GENERATOR, not its cell folder.</b> A generated cell's folder
    /// name is a content-addressed hash (<c>MLIN_a1b2c3…</c>, see <c>GeneratedCellStore</c>) — an
    /// implementation detail of parameter de-duplication, and meaningless to read. The generator id
    /// is the thing the user recognises, and it is shown BARE: a "(PCell)" tag was tried and removed
    /// at the owner's request — "MLIN" already tells an RF engineer what it is, so the tag was noise.</para>
    /// </summary>
    private static string InstanceSummary(LayoutInstance inst, CellLayoutResolution resolution)
    {
        string what =
            resolution is { State: CellLayoutState.Resolved, View.PCellOrigin: { } origin }
                ? origin.GeneratorId
                : CellNameOf(inst.CellRef);

        // SchematicId is the schematic component's InstanceName (R-L5's idempotency key), so it is
        // exactly the name shown on the schematic — present only for a schematic-generated instance.
        return string.IsNullOrWhiteSpace(inst.SchematicId) ? what : $"{what} · {inst.SchematicId}";
    }

    /// <summary>
    /// The cell's own name from a <c>CellRef</c> — its last path segment, since a CellRef is a
    /// relative path to the cell FOLDER (e.g. <c>../../OutputMatch</c>). Falls back to a plain
    /// "Instance" for an empty/unset ref, so the header never renders blank.
    /// </summary>
    private static string CellNameOf(string? cellRef)
    {
        if (string.IsNullOrWhiteSpace(cellRef)) return "Instance";
        var trimmed = cellRef.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? "Instance" : name;
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
        BitmapShape       => "Bitmap",
        _                 => shape.GetType().Name,
    };

    // ── Command dispatch helper ────────────────────────────────────────────────

    private void CommitLayer(LayerKey key)
    {
        if (DragBlocksEdits()) return;
        ApplyToEach<LayerKey>("Layer", s => s.Layer, (s, v) => s.Layer = v, key);
    }

    /// <summary>Folds one <see cref="SetShapeFieldCommand{T}"/> per applicable, actually-changing
    /// shape into a single <see cref="CompositeCommand"/> chain — one undo entry for the whole
    /// multi-selection edit, per the brief's §7 requirement.</summary>
    private void ApplyToEach<T>(string description, System.Func<LayoutShape, T> getter,
        System.Action<LayoutShape, T> setter, T newValue, System.Func<LayoutShape, bool>? filter = null)
    {
        if (_vm is null || _selected.Count == 0) return;

        IUiCommand? combined = null;
        foreach (var shape in _selected)
        {
            if (filter is not null && !filter(shape)) continue;
            var old = getter(shape);
            if (Equals(old, newValue)) continue;

            var captured = shape;
            IUiCommand cmd = new SetShapeFieldCommand<T>(_vm.Model, description, old, newValue, v => setter(captured, v));
            combined = combined is null ? cmd : new CompositeCommand(combined, cmd);
        }

        if (combined is not null) _vm.Execute(combined);
    }
}
