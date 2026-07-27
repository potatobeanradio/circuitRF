using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Properties panel for the Layout Editor's current shape selection (docs/sonnet-briefs/
/// brief-L1c-selection-and-properties.md §7). Mirrors <see cref="SymbolPrimitiveInspectorViewModel"/>'s
/// shape (staged text fields committed on focus-loss/Enter via an explicit Commit* method, combo
/// selections committed immediately via a partial change handler) but for <see cref="LayoutShape"/>:
///
/// - Common to every shape: layer (a combo showing swatch + name, exactly the drawing toolbar's) and
///   net (free text).
/// - Type-specific groups are shown only when EVERY selected shape is that one type; a mixed-type
///   multi-selection shows only the common fields. Within a homogeneous-type multi-selection, a
///   staged text/combo field shows the shared value, or blank when the shapes' values differ — and
///   committing it applies to every one of them as ONE undo entry (<see cref="ApplyToEach{T}"/>
///   folds a <see cref="SetShapeFieldCommand{T}"/> per shape into a single <see cref="CompositeCommand"/>
///   chain, the same pattern <c>CompositeCommand</c> already supports elsewhere).
/// - Dimension fields parse/format through <see cref="LayoutUnits"/>; invalid text reverts to the
///   current (canonical-formatted) value and never throws.
/// </summary>
public sealed partial class LayoutShapePropertiesViewModel : ObservableObject
{
    private LayoutEditorViewModel? _vm;
    private List<LayoutShape> _selected = [];
    private bool _isRefreshing;

    public static PathEndStyle[]  PathEndStyleOptions { get; } = System.Enum.GetValues<PathEndStyle>();
    public static LayoutRotation[] RotationOptions    { get; } = System.Enum.GetValues<LayoutRotation>();

    // ── Empty state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isEmptyState = true;
    [ObservableProperty] private string _emptyMessage = "Select a shape to inspect.";
    public bool IsNotEmptyState => !IsEmptyState;
    partial void OnIsEmptyStateChanged(bool oldValue, bool newValue) => OnPropertyChanged(nameof(IsNotEmptyState));

    [ObservableProperty] private string _selectionSummaryText = "";

    // ── Layer / Net (common) ──────────────────────────────────────────────────

    public ObservableCollection<LayerPickerItem> AvailableLayers => _vm?.AvailableLayers ?? _emptyLayers;
    private static readonly ObservableCollection<LayerPickerItem> _emptyLayers = [];

    [ObservableProperty] private LayerPickerItem? _selectedLayerItem;
    [ObservableProperty] private string _netText = "";

    partial void OnSelectedLayerItemChanged(LayerPickerItem? value)
    {
        if (_isRefreshing || value is null) return;
        CommitLayer(value.Key);
        RefreshFromVm();
    }

    public void CommitNetText(string text)
    {
        string? newNet = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        ApplyToEach<string?>("Net", s => s.Net, (s, v) => s.Net = v, newNet);
        RefreshFromVm();
    }

    // ── RoundedRect ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showRoundedRect;
    [ObservableProperty] private string _cornerRadiusText = "";

    public void CommitCornerRadiusText(string text)
    {
        if (_vm is null || !LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu) || dbu < 0)
        { RefreshFromVm(); return; }
        ApplyToEach<long>("Corner Radius", s => ((RoundedRectShape)s).CornerRadius,
            (s, v) => ((RoundedRectShape)s).CornerRadius = v, dbu, s => s is RoundedRectShape);
        RefreshFromVm();
    }

    // ── Circle ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showCircle;
    [ObservableProperty] private string _radiusText = "";

    public void CommitRadiusText(string text)
    {
        if (_vm is null || !LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu) || dbu <= 0)
        { RefreshFromVm(); return; }
        ApplyToEach<long>("Radius", s => ((CircleShape)s).R, (s, v) => ((CircleShape)s).R = v, dbu, s => s is CircleShape);
        RefreshFromVm();
    }

    // ── Path ───────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showPath;
    [ObservableProperty] private string _pathWidthText = "";
    [ObservableProperty] private PathEndStyle? _pathEndStyleValue;

    public void CommitPathWidthText(string text)
    {
        if (_vm is null || !LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu) || dbu <= 0)
        { RefreshFromVm(); return; }
        ApplyToEach<long>("Width", s => ((PathShape)s).Width, (s, v) => ((PathShape)s).Width = v, dbu, s => s is PathShape);
        RefreshFromVm();
    }

    partial void OnPathEndStyleValueChanged(PathEndStyle? oldValue, PathEndStyle? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue) return;
        ApplyToEach<PathEndStyle>("End Style", s => ((PathShape)s).End,
            (s, v) => ((PathShape)s).End = v, newValue.Value, s => s is PathShape);
        RefreshFromVm();
    }

    // ── Label ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showLabel;
    [ObservableProperty] private string _labelText = "";
    [ObservableProperty] private string _labelHeightText = "";
    [ObservableProperty] private LayoutRotation? _labelRotationValue;

    public void CommitLabelText(string text)
    {
        ApplyToEach<string>("Text", s => ((LabelShape)s).Text, (s, v) => ((LabelShape)s).Text = v, text ?? "", s => s is LabelShape);
        RefreshFromVm();
    }

    public void CommitLabelHeightText(string text)
    {
        if (_vm is null || !LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu) || dbu <= 0)
        { RefreshFromVm(); return; }
        ApplyToEach<long>("Height", s => ((LabelShape)s).Height, (s, v) => ((LabelShape)s).Height = v, dbu, s => s is LabelShape);
        RefreshFromVm();
    }

    partial void OnLabelRotationValueChanged(LayoutRotation? oldValue, LayoutRotation? newValue)
    {
        if (_isRefreshing || newValue is null || oldValue == newValue) return;
        ApplyToEach<LayoutRotation>("Rotation", s => ((LabelShape)s).Rotation,
            (s, v) => ((LabelShape)s).Rotation = v, newValue.Value, s => s is LabelShape);
        RefreshFromVm();
    }

    // ── Flatten tolerance (Curve / Path — blank = inherit) ────────────────────

    [ObservableProperty] private bool _showFlattenTol;
    [ObservableProperty] private string _flattenTolText = "";

    public void CommitFlattenTolText(string text)
    {
        if (_vm is null) { RefreshFromVm(); return; }

        long? newTol;
        if (string.IsNullOrWhiteSpace(text)) newTol = null;
        else if (LayoutUnits.TryParse(text, _vm.DisplayUnit, _vm.Model.DbuPerMicron, out var dbu) && dbu > 0) newTol = dbu;
        else { RefreshFromVm(); return; }

        ApplyToEach<long?>("Flatten Tolerance",
            s => s switch { CurveShape c => c.FlattenTolDbu, PathShape p => p.FlattenTolDbu, _ => null },
            (s, v) => { if (s is CurveShape c) c.FlattenTolDbu = v; else if (s is PathShape p) p.FlattenTolDbu = v; },
            newTol,
            s => s is CurveShape or PathShape);
        RefreshFromVm();
    }

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

        _selected = _vm.SelectedIndices
            .Where(i => i >= 0 && i < _vm.Model.Shapes.Count)
            .Select(i => _vm.Model.Shapes[i])
            .ToList();

        if (_selected.Count == 0) { SetEmpty("Select a shape to inspect."); return; }

        _isRefreshing = true;
        IsEmptyState = false;

        SelectionSummaryText = _selected.Count == 1
            ? ShapeTypeName(_selected[0])
            : $"{_selected.Count} shapes selected";

        var sharedLayer = _selected[0].Layer;
        bool layerSame = _selected.All(s => s.Layer == sharedLayer);
        SelectedLayerItem = layerSame ? AvailableLayers.FirstOrDefault(l => l.Key == sharedLayer) : null;

        var sharedNet = _selected[0].Net;
        bool netSame = _selected.All(s => s.Net == sharedNet);
        NetText = netSame ? (sharedNet ?? "") : "";

        ShowRoundedRect = _selected.All(s => s is RoundedRectShape);
        if (ShowRoundedRect)
            CornerRadiusText = FormatSharedDbu(_selected.Cast<RoundedRectShape>().Select(s => (long?)s.CornerRadius));

        ShowCircle = _selected.All(s => s is CircleShape);
        if (ShowCircle)
            RadiusText = FormatSharedDbu(_selected.Cast<CircleShape>().Select(s => (long?)s.R));

        ShowPath = _selected.All(s => s is PathShape);
        if (ShowPath)
        {
            var paths = _selected.Cast<PathShape>().ToList();
            PathWidthText = FormatSharedDbu(paths.Select(p => (long?)p.Width));
            var ends = paths.Select(p => p.End).Distinct().ToList();
            PathEndStyleValue = ends.Count == 1 ? ends[0] : null;
        }

        ShowLabel = _selected.All(s => s is LabelShape);
        if (ShowLabel)
        {
            var labels = _selected.Cast<LabelShape>().ToList();
            var texts = labels.Select(l => l.Text).Distinct().ToList();
            LabelText = texts.Count == 1 ? texts[0] : "";
            LabelHeightText = FormatSharedDbu(labels.Select(l => (long?)l.Height));
            var rots = labels.Select(l => l.Rotation).Distinct().ToList();
            LabelRotationValue = rots.Count == 1 ? rots[0] : null;
        }

        ShowFlattenTol = _selected.All(s => s is CurveShape or PathShape);
        if (ShowFlattenTol)
        {
            var tols = _selected.Select(s => s switch
            {
                CurveShape c => c.FlattenTolDbu,
                PathShape p  => p.FlattenTolDbu,
                _            => null,
            }).Distinct().ToList();
            FlattenTolText = tols.Count == 1 && tols[0] is { } t
                ? LayoutUnits.Format(t, _vm.DisplayUnit, _vm.Model.DbuPerMicron)
                : "";
        }

        _isRefreshing = false;
    }

    private void SetEmpty(string message)
    {
        _selected = [];
        _isRefreshing = true;
        IsEmptyState = true;
        EmptyMessage = message;
        SelectionSummaryText = "";
        ShowRoundedRect = ShowCircle = ShowPath = ShowLabel = ShowFlattenTol = false;
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
        _                 => shape.GetType().Name,
    };

    // ── Command dispatch helper ────────────────────────────────────────────────

    private void CommitLayer(LayerKey key) => ApplyToEach<LayerKey>("Layer", s => s.Layer, (s, v) => s.Layer = v, key);

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
