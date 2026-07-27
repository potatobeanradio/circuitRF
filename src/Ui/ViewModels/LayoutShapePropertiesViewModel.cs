using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;

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

    /// <summary>The field key (see the Commit*/Error property names below, or a vertex row's
    /// <c>FieldKeyX</c>/<c>FieldKeyY</c>) currently under the caret, or null. Set by the view's
    /// GotFocus/LostFocus handlers — R-L1j-3.</summary>
    private string? _focusedField;

    public static PathEndStyle[]   PathEndStyleOptions { get; } = System.Enum.GetValues<PathEndStyle>();
    public static LayoutRotation[] RotationOptions     { get; } = System.Enum.GetValues<LayoutRotation>();
    public static LabelFontStyle[] LabelStyleOptions   { get; } = System.Enum.GetValues<LabelFontStyle>();

    // ── Empty state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isEmptyState = true;
    [ObservableProperty] private string _emptyMessage = "Select a shape to inspect.";
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

    /// <summary>False for an all-bitmap selection — a bitmap is not electrical, so Net is meaningless
    /// for it (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md, properties panel §).</summary>
    public bool ShowNet => !ShowBitmap;

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
            OnPropertyChanged(nameof(AvailableLayers));
    }

    private void OnModelChanged(object? sender, System.EventArgs e) => RefreshFromVm();

    // ── Refresh ────────────────────────────────────────────────────────────────

    private void RefreshFromVm()
    {
        if (_vm is null) { SetEmpty("No active layout."); return; }

        _selected = _vm.EffectiveSelectedShapes().ToList(); // R-L1j-1: drag-override-aware

        if (_selected.Count == 0) { SetEmpty("Select a shape to inspect."); return; }

        _isRefreshing = true;
        IsEmptyState = false;
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
        ShowRoundedRect = ShowCircle = ShowPath = ShowLabel = ShowFlattenTol = ShowRectSize = ShowVertexList = ShowBitmap = false;
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
