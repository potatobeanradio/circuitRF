// ================================================================
//  TraceRowViewModel.cs  —  Observable wrapper for a single Trace
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using RfCore;
using RfCore.Data;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class TraceRowViewModel : ViewModelBase
{
    private readonly Trace                  _trace;
    private readonly PlotInspectorViewModel _parent;

    // Prevents OnSelectedDataChanged from firing during construction and RebuildSignals.
    private bool _suppressDataCallback;

    // Stashed Z0Kind from the last ApplySourceZ0 call — lets UI distinguish NonUniform from
    // UniformComplex without changing the broader SourceZ0IsUnusual flag on the trace.
    private RfCore.Data.Z0Kind? _sourceZ0Kind;

    public Trace Trace => _trace;

    // True only for Rect — used for the →R secondary-axis checkbox.
    public bool IsRectPlot => _parent.PlotType == PlotType.Rect;

    // True for Rect or Table — used to show/hide the YAxis format combo.
    public bool IsRectOrTablePlot => _parent.PlotType is PlotType.Rect or PlotType.Table;

    // True only for Table — used to show/hide per-trace number-format controls.
    public bool IsTablePlot    => _parent.PlotType == PlotType.Table;
    public bool IsNotTablePlot => _parent.PlotType != PlotType.Table;

    // Standard (line/marker/table) trace body. 7.4 adds IsContourTrace sibling.
    public bool IsStandardTrace => true;

    // ---- Cube-bound discriminators (Phase 7.2c-a) --------------------------

    /// <summary>True when the trace is in cube-bound mode (not SNP/matrix).</summary>
    public bool IsCubeBoundTrace => _trace.IsCubeBound;

    /// <summary>YAxis combo is shown for network-bound Rect/Table; hidden for cube-bound.</summary>
    public bool ShowYAxisCombo => IsRectOrTablePlot && !IsCubeBoundTrace;

    // ---- Combined data picker (replaces SNP source + Row + Col) ----------
    //
    //  One item per (SNP × matrix-element) plus derived-parameter items for
    //  2-port SNPs.  Rebuilt when MatrixType or the library contents change.
    //  Selection revert is avoided by NOT calling RebuildSignals from
    //  RefreshDescription (which is called from RebuildAndNotify).

    public ObservableCollection<TraceDataItem> AvailableSignals { get; } = new();

    // ---- Axis-role editor (Phase 7.3a) ------------------------------------
    //
    //  One row per DataCube axis.  Rebuilt when CubeName changes.
    //  Edits write back to Trace.Slice (name-keyed) and trigger RebuildAndNotify.

    public ObservableCollection<AxisRoleRowViewModel> AxisRoles { get; } = new();

    [ObservableProperty]
    private TraceDataItem? _selectedSignal;

    partial void OnSelectedSignalChanged(TraceDataItem? value)
    {
        if (_suppressDataCallback || value == null) return;

        // Skip if the trace already matches the selection — nothing actually changed.
        bool alreadyApplied;
        if (value.IsCubeBound)
        {
            // Compare source + cube only; user may have customized Slice via the axis-role editor.
            alreadyApplied = _trace.IsCubeBound
                && string.Equals(_trace.SourcePath, value.Entry.FilePath, StringComparison.OrdinalIgnoreCase)
                && _trace.CubeName == value.CubeName;
        }
        else
        {
            alreadyApplied = value.Derived != DerivedParameters.None
                ? (_trace.Data == value.Entry.Snp && _trace.Derived == value.Derived)
                : (_trace.Data == value.Entry.Snp && _trace.Row == value.Row
                   && _trace.Col == value.Col && _trace.Derived == DerivedParameters.None);
        }
        OnPropertyChanged(nameof(ShowZ0Badge));
        OnPropertyChanged(nameof(Z0BadgeTooltip));
        OnPropertyChanged(nameof(ShowZ0Control));
        OnPropertyChanged(nameof(IsMultiPortNormalization));
        OnPropertyChanged(nameof(IsZ0Editable));
        OnPropertyChanged(nameof(Z0DisabledReason));
        if (alreadyApplied) return;

        _trace.SourcePath = value.Entry.FilePath;

        if (value.IsCubeBound)
        {
            _trace.CubeName = value.CubeName;
            _trace.Slice    = value.Slice?.ToArray();  // default slice; user edits via AxisRoles
            // Cube-bound traces have no per-port Z0 from the S matrix.
            _trace.SourceZ0PerPort   = null;
            _trace.SourceZ0IsUnusual = false;
            RebuildAxisRoles();
        }
        else
        {
            // Switching to network-bound: clear any cube identity.
            _trace.CubeName = null;
            _trace.Slice    = null;
            AxisRoles.Clear();
            _trace.Data     = value.Entry.Snp!;

            // Set per-port Z0 fields from the source entry (Phase 7.2f).
            ApplySourceZ0(value.Entry);

            if (value.Derived != DerivedParameters.None)
            {
                _trace.Derived = value.Derived;
            }
            else
            {
                _trace.Derived = DerivedParameters.None;
                _trace.Row     = value.Row;
                _trace.Col     = value.Col;
            }
        }

        _parent.RebuildAndNotify();
    }

    /// <summary>Populates SourceZ0PerPort / SourceZ0IsUnusual on the trace from the source entry,
    /// stashes the Z0Kind for per-kind UI gating, resets the Override checkbox, and seeds the
    /// displayed Z0 value from the source port-1 reference.</summary>
    internal void ApplySourceZ0(DataSourceEntryViewModel entry)
    {
        StampSourceZ0OnTrace(_trace, entry);
        _sourceZ0Kind = entry.Z0Kind;
        // Reset override without triggering the full OnZ0OverrideEnabledChanged path
        // (caller handles the subsequent rebuild).
        _applyingSource = true;
        Z0OverrideEnabled = false;
        _applyingSource = false;
        SeedZ0FromSource();
    }

    /// <summary>Stamps only the Trace-level SourceZ0 fields from the entry.  Used by
    /// PlotInspectorViewModel where no TraceRowViewModel exists yet.</summary>
    internal static void StampSourceZ0OnTrace(Trace trace, DataSourceEntryViewModel entry)
    {
        if (entry.Data is { } ds && ds.Contains("Z0"))
        {
            trace.SourceZ0PerPort   = ds["Z0"].ComplexValues;
            trace.SourceZ0IsUnusual = entry.HasUnusualZ0;
        }
        else
        {
            trace.SourceZ0PerPort   = null;
            trace.SourceZ0IsUnusual = false;
        }
    }

    private static bool SlicesEqual(AxisSlice[]? a, AxisSlice[]? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // ---- Matrix type -------------------------------------------------------

    [ObservableProperty]
    private MatrixType _matrixType;

    partial void OnMatrixTypeChanged(MatrixType value)
    {
        _trace.MatrixType = value;
        RebuildSignals();          // labels change (S→Z etc.)
        _parent.RebuildAndNotify();
    }

    // ---- Y-axis format (Rect/Text only) ------------------------------------
    //
    //  AvailableYAxes is built once in the constructor for the current PlotType.
    //  Complex is disabled (but still shown) on Rect so the user can see why
    //  it is not selectable.

    public IReadOnlyList<YAxisItem> AvailableYAxes { get; }

    [ObservableProperty]
    private YAxisItem? _selectedYAxis;

    partial void OnSelectedYAxisChanged(YAxisItem? value)
    {
        if (value == null) return;
        _trace.YAxis = value.Format;
        _parent.RebuildAndNotify();
    }

    // ---- Cube transform (Rect, cube-bound traces only) ----------------------

    [ObservableProperty]
    private CubeTransformItem? _selectedCubeTransformItem;

    partial void OnSelectedCubeTransformItemChanged(CubeTransformItem? value)
    {
        if (value is null) return;
        _trace.Transform = value.Transform;
        _parent.RebuildAndNotify();
    }

    // ---- Secondary axis -----------------------------------------------------

    [ObservableProperty]
    private bool _useSecondaryAxis;

    partial void OnUseSecondaryAxisChanged(bool value)
    {
        _trace.UseSecondaryAxis = value;
        OnPropertyChanged(nameof(SecondaryAxisIcon));
        _parent.OnTraceSecondaryAxisChanged();
    }

    /// <summary>Icon that reflects current secondary-axis state.</summary>
    public MaterialIconKind SecondaryAxisIcon =>
        UseSecondaryAxis ? MaterialIconKind.ArrowRight : MaterialIconKind.ArrowLeft;

    public IRelayCommand ToggleSecondaryAxisCommand { get; }

    // ---- Z0 text entry (reference impedance) --------------------------------

    [ObservableProperty]
    private string _z0String = "50";

    // Suppresses the rebuild side-effect in OnZ0StringChanged while SeedZ0FromSource is seeding.
    private bool _seedingZ0;

    partial void OnZ0StringChanged(string value)
    {
        if (_seedingZ0) return;
        if (ComplexStringHelper.TryParse(value, out Complex z0))
        {
            _trace.Z0 = z0;
            _parent.RebuildAndNotify();
        }
    }

    // ---- Z0 badge (Phase 7.2e) — retained for the one-time Messages warning seam ---

    /// <summary>True when this is an S-parameter trace whose source has unusual (non-uniform or complex) Z0.</summary>
    public bool ShowZ0Badge =>
        !_trace.IsCubeBound
        && _trace.MatrixType == MatrixType.S
        && (SelectedSignal?.Entry?.HasUnusualZ0 ?? false);

    /// <summary>Tooltip listing the per-port Z0 values from the source entry.</summary>
    public string Z0BadgeTooltip
    {
        get
        {
            var entry = SelectedSignal?.Entry;
            if (entry is null || !entry.HasUnusualZ0) return "";
            var kind  = entry.Z0Kind;
            var ports = entry.Z0PerPort;
            var sb    = new System.Text.StringBuilder("Reference Z0: ");
            for (int i = 0; i < ports.Count; i++)
            {
                var z    = ports[i];
                string zFmt = Math.Abs(z.Imaginary) < 1e-12
                    ? $"{z.Real:G4}Ω"
                    : $"{z.Real:G4}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary:G4}jΩ";
                if (i > 0) sb.Append(", ");
                sb.Append($"port{i + 1}={zFmt}");
            }
            sb.Append(kind == RfCore.Data.Z0Kind.NonUniform ? " (non-uniform)" : " (complex)");
            return sb.ToString();
        }
    }

    // ---- Z0 control gating (Phase 7.2f-2) ---------------------------------

    /// <summary>True when the source uses genuinely non-uniform-across-ports normalization.
    /// UniformComplex is NOT non-uniform; only NonUniform triggers multi-port mode.</summary>
    private bool SourceZ0IsNonUniform => _sourceZ0Kind == RfCore.Data.Z0Kind.NonUniform;

    /// <summary>True for a network-bound, non-derived S-matrix trace.</summary>
    public bool IsScatteringTrace =>
        !_trace.IsCubeBound
        && _trace.Derived == DerivedParameters.None
        && _trace.MatrixType == MatrixType.S;

    /// <summary>True when the source has non-uniform port normalization — the Z0 box is replaced
    /// by a "Multiple Port Normalization" label; no Override checkbox is shown.</summary>
    public bool IsMultiPortNormalization =>
        !_trace.IsCubeBound && _trace.SourceZ0IsUnusual && SourceZ0IsNonUniform;

    /// <summary>True when the Z0 control (box + Override checkbox) should be shown — i.e. the
    /// trace is scattering and NOT in multi-port normalization mode.</summary>
    public bool ShowZ0Control => !_trace.IsCubeBound && IsScatteringTrace && !IsMultiPortNormalization;

    /// <summary>Override checkbox bound in the trace card. When unchecked, the Z0 box reverts to
    /// the source port-1 reference and editing is disabled.</summary>
    [ObservableProperty]
    private bool _z0OverrideEnabled;

    // Suppresses OnZ0OverrideEnabledChanged rebuild while ApplySourceZ0 is resetting the field.
    private bool _applyingSource;

    partial void OnZ0OverrideEnabledChanged(bool value)
    {
        if (_applyingSource) return;
        if (!value)
        {
            SeedZ0FromSource();
            _parent.RebuildAndNotify();
        }
        OnPropertyChanged(nameof(IsZ0Editable));
    }

    /// <summary>Seeds _trace.Z0 and Z0String from the source's port-1 reference impedance.
    /// Does not trigger RebuildAndNotify — callers handle that.</summary>
    private void SeedZ0FromSource()
    {
        var sourcePort1Z0 = (_trace.SourceZ0PerPort is { Length: > 0 } arr)
            ? arr[0]
            : _trace.Data.Z0;
        _trace.Z0 = sourcePort1Z0;
        _seedingZ0 = true;
        Z0String = ComplexStringHelper.Format(sourcePort1Z0);
        _seedingZ0 = false;
    }

    /// <summary>Z0 box is editable only when ShowZ0Control is true and the Override checkbox is on.</summary>
    public bool IsZ0Editable => ShowZ0Control && Z0OverrideEnabled;

    /// <summary>Tooltip shown on the disabled Z0 box (legacy; kept for existing tests).</summary>
    public string Z0DisabledReason => _trace.SourceZ0IsUnusual
        ? "Source has non-uniform/complex port normalization — renormalize by re-simulating."
        : "";

    // ---- Line ---------------------------------------------------------------

    [ObservableProperty]
    private bool _lineEnabled;

    partial void OnLineEnabledChanged(bool value)
    {
        _trace.Properties.LineEnabled = value;
        _parent.Notify();
        OnPropertyChanged(nameof(SelectedLineMode));
    }

    [ObservableProperty]
    private double _lineWidth;

    partial void OnLineWidthChanged(double value)
    {
        _trace.Properties.LineWidth = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private LineType _lineType;

    partial void OnLineTypeChanged(LineType value)
    {
        _trace.Properties.LineType = value;
        _parent.Notify();
        OnPropertyChanged(nameof(SelectedLineMode));
    }

    // Merged line mode: Off or a specific LineType — drives the icon-pick.
    public LineModeItem? SelectedLineMode
    {
        get => !LineEnabled
            ? PlotInspectorViewModel.LineModes[0]
            : PlotInspectorViewModel.LineModes.FirstOrDefault(m => !m.IsOff && m.Type == LineType);
        set
        {
            if (value == null) return;
            if (value.IsOff)
                LineEnabled = false;
            else { LineEnabled = true; LineType = value.Type; }
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private int _lineColorIndex;

    partial void OnLineColorIndexChanged(int value)
    {
        _trace.Properties.LineColorStorage = null;
        _trace.Properties.LineColorIndex   = value;
        _parent.Notify();
    }

    // ---- Marker -------------------------------------------------------------

    [ObservableProperty]
    private bool _markerEnabled;

    partial void OnMarkerEnabledChanged(bool value)
    {
        _trace.Properties.MarkerEnabled = value;
        _parent.Notify();
        OnPropertyChanged(nameof(SelectedSymbolMode));
    }

    [ObservableProperty]
    private double _markerSize;

    partial void OnMarkerSizeChanged(double value)
    {
        _trace.Properties.MarkerSize = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private MarkerTypeItem? _selectedMarkerTypeItem;

    partial void OnSelectedMarkerTypeItemChanged(MarkerTypeItem? value)
    {
        if (value == null) return;
        _trace.Properties.MarkerType = value.Value;
        _parent.Notify();
        OnPropertyChanged(nameof(SelectedSymbolMode));
    }

    // Merged symbol mode: Off or a specific MarkerType — drives the icon-pick.
    public SymbolModeItem? SelectedSymbolMode
    {
        get
        {
            if (!MarkerEnabled) return PlotInspectorViewModel.SymbolModes[0];
            var shape = SelectedMarkerTypeItem?.Value ?? MarkerType.Circle;
            return PlotInspectorViewModel.SymbolModes.FirstOrDefault(m => !m.IsOff && m.Shape == shape);
        }
        set
        {
            if (value == null) return;
            if (value.IsOff)
                MarkerEnabled = false;
            else
            {
                MarkerEnabled = true;
                var item = PlotInspectorViewModel.AllMarkerTypes.FirstOrDefault(m => m.Value == value.Shape);
                if (item != null) SelectedMarkerTypeItem = item;
            }
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private int _markerColorIndex;

    partial void OnMarkerColorIndexChanged(int value)
    {
        _trace.Properties.MarkerColorStorage = null;
        _trace.Properties.MarkerColorIndex   = value;
        _parent.Notify();
    }

    // ---- Table number-format controls (Table plot only) --------------------

    [ObservableProperty]
    private PrecisionFormat _formatString;

    partial void OnFormatStringChanged(PrecisionFormat value)
    {
        _trace.FormatString = value;
        _parent.Notify();
    }

    [ObservableProperty]
    private int _maximumFractionDigits;

    partial void OnMaximumFractionDigitsChanged(int value)
    {
        _trace.MaximumFractionDigits = value;
        _parent.Notify();
    }

    // ---- Cleanup ------------------------------------------------------------

    /// <summary>
    /// Removes the CollectionChanged subscription this VM added in its constructor.
    /// Call before discarding a TraceRowViewModel that wasn't removed by the user.
    /// </summary>
    internal void UnsubscribeFromLibrary()
    {
        _parent.LibraryEntries.CollectionChanged -= OnLibraryEntriesChanged;
    }

    // ---- Command ------------------------------------------------------------

    public IRelayCommand RemoveCommand { get; }

    // ---- Constructor --------------------------------------------------------

    public TraceRowViewModel(Trace trace, PlotInspectorViewModel parent)
    {
        _trace  = trace;
        _parent = parent;

        _matrixType       = trace.MatrixType;
        _useSecondaryAxis = trace.UseSecondaryAxis;

        _lineEnabled    = trace.Properties.LineEnabled;
        _lineWidth      = trace.Properties.LineWidth;
        _lineType       = trace.Properties.LineType;
        _lineColorIndex = trace.Properties.LineColorIndex;

        _markerEnabled    = trace.Properties.MarkerEnabled;
        _markerSize       = trace.Properties.MarkerSize;
        _markerColorIndex = trace.Properties.MarkerColorIndex;

        _formatString          = trace.FormatString;
        _maximumFractionDigits = trace.MaximumFractionDigits;

        _z0String = ComplexStringHelper.Format(trace.Z0);

        // Marker type via icon wrapper
        _selectedMarkerTypeItem = PlotInspectorViewModel.AllMarkerTypes
            .FirstOrDefault(m => m.Value == trace.Properties.MarkerType);

        // Cube transform item (Phase 7.2c-a)
        _selectedCubeTransformItem = PlotInspectorViewModel.AllCubeTransforms
            .FirstOrDefault(t => t.Transform == trace.Transform);

        RemoveCommand              = new RelayCommand(() => _parent.RemoveTrace(this));
        ToggleSecondaryAxisCommand = new RelayCommand(() => UseSecondaryAxis = !UseSecondaryAxis);

        // Build YAxis items once — plot type doesn't change within a VM lifetime
        // (RebuildTraces() creates fresh VMs on plot-type switch).
        bool isRect = parent.PlotType == PlotType.Rect;
        AvailableYAxes = Enum.GetValues<DependentVarFormat>()
            .Select(f => new YAxisItem(f, enabled: !(isRect && f == DependentVarFormat.Complex)))
            .ToList();
        _selectedYAxis = AvailableYAxes.FirstOrDefault(y => y.Format == trace.YAxis);

        // Build data picker list and select the item matching the current trace state.
        RebuildSignals();

        // Keep AvailableSignals fresh when the library collection changes.
        _parent.LibraryEntries.CollectionChanged += OnLibraryEntriesChanged;
    }

    // ---- Signal list management ---------------------------------------------

    private void OnLibraryEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildSignals();

    private void RebuildSignals()
    {
        AvailableSignals.Clear();

        bool isComplexPlot = _parent.PlotType is PlotType.Smith or PlotType.Polar;
        bool singleSource  = _parent.LibraryEntries.Count == 1;

        // ---- Network-bound signals (matrix / derived) -----------------------
        foreach (var entry in _parent.LibraryEntries)
        {
            if (entry.Snp is null) continue;
            var snp = entry.Snp;
            if (snp.IsEmpty)
            {
                bool isSource = _trace.Data == snp;
                int row = isSource ? _trace.Row : 0;
                int col = isSource ? _trace.Col : 0;
                AvailableSignals.Add(new TraceDataItem(entry, MatrixType, row, col,
                    singleSource, isBroken: true));
                continue;
            }

            int ports = snp.Ports;

            if (_trace.Data == snp
                && _trace.Derived == DerivedParameters.None
                && (_trace.Row >= ports || _trace.Col >= ports))
            {
                AvailableSignals.Add(new TraceDataItem(entry, MatrixType,
                    _trace.Row, _trace.Col, singleSource, isBroken: true));
            }

            for (int r = 0; r < ports; r++)
                for (int c = 0; c < ports; c++)
                    AvailableSignals.Add(new TraceDataItem(entry, MatrixType, r, c, singleSource));

            if (ports == 2)
            {
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.SourceStabilityCircle, _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.LoadStabilityCircle,   _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.MuPrime, _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.Mu,      _parent.PlotType, singleSource));
                AvailableSignals.Add(new TraceDataItem(entry, DerivedParameters.MaxGain, _parent.PlotType, singleSource));
            }
        }

        // ---- Cube-bound signals (Phase 7.3a: one item per cube, axis roles via editor) ------
        //
        // One ComboBox entry per plottable cube (rank ≥1, skip S/Z0).
        // Default slice: first axis as X, remainder pinned at index 0.
        // The axis-role editor below the combo lets users reassign roles without
        // generating a combinatorial explosion of signal items.
        foreach (var entry in _parent.LibraryEntries)
        {
            var ds = entry.Data;
            if (ds is null) continue;

            string filePrefix = singleSource
                ? ""
                : $"{System.IO.Path.GetFileNameWithoutExtension(entry.DisplayName)}..";

            foreach (var (cubeName, cube) in ds.Cubes)
            {
                if (cubeName is "S" or "Z0") continue;
                int rank = cube.Rank;
                if (rank <= 0) continue;

                bool isEnabled = !isComplexPlot || cube.DataKind == DataKind.Complex;

                // Default slice: axis 0 → KeepAsX, axes 1..N-1 → PinToIndex at 0.
                var defaultSlice = new AxisSlice[rank];
                defaultSlice[0] = new AxisSlice(cube.Axes[0].Name, AxisRole.KeepAsX, 0);
                for (int d = 1; d < rank; d++)
                    defaultSlice[d] = new AxisSlice(cube.Axes[d].Name, AxisRole.PinToIndex, 0);

                AvailableSignals.Add(new TraceDataItem(entry, cubeName, defaultSlice,
                                                       $"{filePrefix}{cubeName}", isEnabled));
            }
        }

        // ---- Select the item matching the current trace state ---------------
        TraceDataItem? match = null;

        if (_trace.IsCubeBound)
        {
            // Match by source + cube only; slice is managed via the axis-role editor.
            match = AvailableSignals.FirstOrDefault(s =>
                s.IsCubeBound
                && string.Equals(s.Entry.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase)
                && s.CubeName == _trace.CubeName);
        }
        else if (_trace.Data != null)
        {
            var matchEntry = _parent.LibraryEntries.FirstOrDefault(e => e.Snp == _trace.Data);
            if (matchEntry != null)
            {
                if (matchEntry.Snp!.IsEmpty
                    || (_trace.Derived == DerivedParameters.None
                        && (_trace.Row >= matchEntry.Snp!.Ports
                            || _trace.Col >= matchEntry.Snp!.Ports)))
                {
                    match = AvailableSignals.FirstOrDefault(s => s.IsBroken && s.Entry == matchEntry);
                }
                else if (_trace.Derived != DerivedParameters.None)
                {
                    match = AvailableSignals.FirstOrDefault(s => s.Entry == matchEntry
                        && s.Derived == _trace.Derived);
                }
                else
                {
                    match = AvailableSignals.FirstOrDefault(s => s.Entry == matchEntry
                        && s.Row == _trace.Row && s.Col == _trace.Col
                        && s.Derived == DerivedParameters.None && !s.IsBroken);
                }
            }
        }

        _suppressDataCallback = true;
        SelectedSignal = match;
        _suppressDataCallback = false;

        // Keep per-port Z0 fields fresh when the library changes in place (e.g. auto-refresh).
        if (match is not null && !match.IsCubeBound)
            ApplySourceZ0(match.Entry);
        else if (match is null || match.IsCubeBound)
        {
            _trace.SourceZ0PerPort   = null;
            _trace.SourceZ0IsUnusual = false;
            _sourceZ0Kind = null;
        }

        OnPropertyChanged(nameof(ShowZ0Badge));
        OnPropertyChanged(nameof(Z0BadgeTooltip));
        OnPropertyChanged(nameof(ShowZ0Control));
        OnPropertyChanged(nameof(IsMultiPortNormalization));
        OnPropertyChanged(nameof(IsZ0Editable));
        OnPropertyChanged(nameof(Z0DisabledReason));

        // Rebuild axis-role rows for the currently selected cube (Phase 7.3a).
        RebuildAxisRoles();
    }

    /// <summary>
    /// Called by PlotInspectorViewModel after any library change to refresh the
    /// signal list (e.g. when a broken entry is restored in-place).
    /// </summary>
    internal void RefreshDataSources() => RebuildSignals();

    // ---- Axis-role editor (Phase 7.3a) ------------------------------------

    /// <summary>
    /// Rebuilds AxisRoles from the cube currently referenced by the trace.
    /// Reads Trace.Slice (name-keyed) to restore prior role assignments;
    /// axes missing from the slice default to PinToIndex/0.
    /// If no axis ends up as X, the first axis is promoted.
    /// </summary>
    private void RebuildAxisRoles()
    {
        AxisRoles.Clear();

        if (!_trace.IsCubeBound || _trace.CubeName is null) return;

        var entry = _parent.LibraryEntries.FirstOrDefault(e =>
            string.Equals(e.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase));
        var ds = entry?.Data;
        if (ds is null || !ds.Contains(_trace.CubeName)) return;

        var cube  = ds[_trace.CubeName];
        var slice = _trace.Slice;

        for (int d = 0; d < cube.Rank; d++)
        {
            var axis = cube.Axes[d];

            // Find matching slice entry by axis name.
            bool isX   = false;
            int pinIdx = 0;
            if (slice is not null)
            {
                foreach (var s in slice)
                {
                    if (s.AxisName == axis.Name)
                    {
                        isX    = s.Role == AxisRole.KeepAsX;
                        pinIdx = Math.Clamp(s.Index, 0, Math.Max(0, axis.Length - 1));
                        break;
                    }
                }
            }

            // Build label list for the pin combo.
            var opts = new List<string>(axis.Length);
            for (int k = 0; k < axis.Length; k++)
            {
                opts.Add(axis.Labels is not null && k < axis.Labels.Length
                    ? axis.Labels[k]
                    : axis.Values[k].ToString("G3"));
            }

            AxisRoles.Add(new AxisRoleRowViewModel(this, axis.Name, axis.Unit, opts, isX, pinIdx));
        }

        // Guard: at least one X axis.
        if (!AxisRoles.Any(r => r.IsX) && AxisRoles.Count > 0)
            AxisRoles[0].SetIsXSilent(true);
    }

    /// <summary>
    /// Called by AxisRoleRowViewModel when a row is promoted to X.
    /// Silently demotes all other rows that were X to Pinned.
    /// </summary>
    internal void OnAxisSetToX(AxisRoleRowViewModel newX)
    {
        foreach (var row in AxisRoles)
            if (!ReferenceEquals(row, newX) && row.IsX)
                row.SetIsXSilent(false);
    }

    /// <summary>
    /// Writes the current AxisRoles state back to Trace.Slice and
    /// triggers a full redraw + data resolve.
    /// </summary>
    internal void FlushSliceAndRebuild()
    {
        // Build updated slice in cube-axis order.
        var slice = new AxisSlice[AxisRoles.Count];
        for (int i = 0; i < AxisRoles.Count; i++)
        {
            var r = AxisRoles[i];
            slice[i] = new AxisSlice(r.AxisName,
                r.IsX ? AxisRole.KeepAsX : AxisRole.PinToIndex,
                r.PinIndex);
        }

        // Guard: if no X survived, fall back to the first axis.
        bool hasX = Array.Exists(slice, s => s.Role == AxisRole.KeepAsX);
        if (!hasX && slice.Length > 0)
        {
            slice[0] = new AxisSlice(slice[0].AxisName, AxisRole.KeepAsX, 0);
            AxisRoles[0].SetIsXSilent(true);
        }

        _trace.Slice = slice;
        _parent.RebuildAndNotify();
    }

    // ---- Called by PlotInspectorViewModel after trace paths are rebuilt ----
    //
    //  Deliberately does NOT call RebuildSignals() — calling it here would
    //  clear the AvailableSignals collection in the middle of a callback chain
    //  that originated from OnSelectedSignalChanged, causing Avalonia's ComboBox
    //  to reset its SelectedItem to null (the revert bug).

    public void RefreshDescription()
    {
        OnPropertyChanged(nameof(IsRectPlot));
        OnPropertyChanged(nameof(IsRectOrTablePlot));
        OnPropertyChanged(nameof(IsTablePlot));
        OnPropertyChanged(nameof(IsNotTablePlot));
        OnPropertyChanged(nameof(IsCubeBoundTrace));
        OnPropertyChanged(nameof(ShowYAxisCombo));
        OnPropertyChanged(nameof(ShowZ0Badge));
        OnPropertyChanged(nameof(Z0BadgeTooltip));
        OnPropertyChanged(nameof(ShowZ0Control));
        OnPropertyChanged(nameof(IsMultiPortNormalization));
        OnPropertyChanged(nameof(IsZ0Editable));
        OnPropertyChanged(nameof(Z0DisabledReason));
    }
}
