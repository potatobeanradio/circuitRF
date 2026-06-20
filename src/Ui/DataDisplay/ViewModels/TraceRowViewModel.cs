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
using RfCore.Loadpull;
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

    // Standard (line/marker/table) trace body; hidden for contour traces.
    public bool IsStandardTrace => !IsContourTrace;

    // ---- Contour trace (Phase 7.4e) -----------------------------------------

    public bool IsContourTrace => _trace.IsContourTrace;

    // VM-side LoadpullSurface (owned here; Trace must not hold a DataSet per firewall).
    private LoadpullSurface? _loadpullSurface;
    private string?          _surfaceSourcePath;

    // Suppresses On…Changed callbacks during SyncContourVmFromData.
    private bool _suppressContourCallback;

    public ObservableCollection<string> AvailableMetrics     { get; } = new();
    public ObservableCollection<string> AvailableFrequencies { get; } = new();

    [ObservableProperty] private string?          _contourMetricName;
    [ObservableProperty] private ConstraintKind   _contourConstraintKind = ConstraintKind.Compression;
    [ObservableProperty] private string           _contourConstraintMetric = "";
    [ObservableProperty] private double           _contourConstraintValue = 3.0;
    [ObservableProperty] private int              _contourFreqIndex;
    [ObservableProperty] private ContourLevelMode _contourLevelMode;
    [ObservableProperty] private double           _contourLevelStart;
    [ObservableProperty] private double           _contourLevelStep;
    [ObservableProperty] private double           _contourLevelStop;
    [ObservableProperty] private int              _contourLevelCount = 10;
    [ObservableProperty] private bool             _contourShowIsoLines = true;
    [ObservableProperty] private bool             _contourShowFill;
    [ObservableProperty] private bool             _contourShowLabels = true;
    [ObservableProperty] private ContourFillKind  _contourSelectedFillKind;
    [ObservableProperty] private ContourColorMap  _contourColorMap;
    [ObservableProperty] private double           _contourLabelSpacing = 1.0;
    [ObservableProperty] private bool             _contourOptionsExpanded;

    public bool IsCompressionConstraint    => ContourConstraintKind == ConstraintKind.Compression;
    public bool IsConstantMetricConstraint => ContourConstraintKind == ConstraintKind.ConstantMetric;
    public bool IsRangeLevelMode           => ContourLevelMode == ContourLevelMode.Range;
    public bool IsCountLevelMode           => ContourLevelMode == ContourLevelMode.Count;
    public bool ShowContourFreqPicker      => IsContourTrace && AvailableFrequencies.Count > 1;

    partial void OnContourMetricNameChanged(string? value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.MetricName = value ?? "";
        var (s, step, stop) = ContourDefaults.LevelRange(cd.MetricName);
        cd.LevelStart = s; cd.LevelStep = step; cd.LevelStop = stop;
        SyncContourVmFromData(cd);
        RebuildContour();
    }

    partial void OnContourConstraintKindChanged(ConstraintKind value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ContourConstraintKind = value;
        OnPropertyChanged(nameof(IsCompressionConstraint));
        OnPropertyChanged(nameof(IsConstantMetricConstraint));
        RebuildContour();
    }

    partial void OnContourConstraintMetricChanged(string value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ConstraintMetricName = value;
        RebuildContour();
    }

    partial void OnContourConstraintValueChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ConstraintValue = value;
        RebuildContour();
    }

    partial void OnContourFreqIndexChanged(int value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.FreqIndex = value;
        RebuildContour();
    }

    partial void OnContourLevelModeChanged(ContourLevelMode value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LevelMode = value;
        OnPropertyChanged(nameof(IsRangeLevelMode));
        OnPropertyChanged(nameof(IsCountLevelMode));
        RebuildContour();
    }

    partial void OnContourLevelStartChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LevelStart = value;
        RebuildContour();
    }

    partial void OnContourLevelStepChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LevelStep = value;
        RebuildContour();
    }

    partial void OnContourLevelStopChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LevelStop = value;
        RebuildContour();
    }

    partial void OnContourLevelCountChanged(int value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LevelCount = value;
        RebuildContour();
    }

    partial void OnContourShowIsoLinesChanged(bool value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ShowIsoLines = value;
        _parent.Notify();
    }

    partial void OnContourShowFillChanged(bool value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ShowFill = value;
        _parent.Notify();
    }

    partial void OnContourShowLabelsChanged(bool value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.DrawLabels = value;
        _parent.Notify();
    }

    partial void OnContourSelectedFillKindChanged(ContourFillKind value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.SelectedFillKind = value;
        OnPropertyChanged(nameof(IsTopoMapFill));
        OnPropertyChanged(nameof(IsHeatMapFill));
        _parent.Notify();
    }

    partial void OnContourColorMapChanged(ContourColorMap value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ColorMap = value;
        // DEFERRED: renderer ignores ColorMap until colormap ramp mapping is implemented.
        _parent.Notify();
    }

    partial void OnContourLabelSpacingChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LabelSpacing = value;
        // DEFERRED: richer label render with spacing is future work.
        _parent.Notify();
    }

    private void RebuildContour()
    {
        var cd = _trace.ContourData;
        if (cd is null) return;

        if (!EnsureLoadpullSurface()) { ClearContourGrid(cd); return; }

        var surface = _loadpullSurface!;
        int freqIdx = Math.Clamp(cd.FreqIndex, 0, Math.Max(0, surface.Frequencies.Count - 1));

        ConstraintSpec constraint = cd.ContourConstraintKind == ConstraintKind.Compression
            ? ConstraintSpec.AtCompression(cd.ConstraintValue)
            : ConstraintSpec.AtConstantMetric(cd.ConstraintMetricName, cd.ConstraintValue);

        var plane = (_parent.PlotType is PlotType.Smith or PlotType.Polar)
            ? SurfacePlane.Gamma
            : SurfacePlane.Z;

        var fit = surface.Fit(freqIdx, cd.MetricName, constraint, plane);
        if (fit is null) { ClearContourGrid(cd); return; }

        var grid    = surface.Resample(fit);
        var scatter = surface.Reduce(freqIdx, cd.MetricName, constraint, plane);

        ContourLevelSet levels;
        if (cd.LevelMode == ContourLevelMode.Range)
        {
            double step = cd.LevelStep > 0 ? cd.LevelStep : 0.5;
            var    raw  = ContourExtractor.LevelsByStep(grid, step, cd.LevelStart);
            double lo   = Math.Min(cd.LevelStart, cd.LevelStop);
            double hi   = Math.Max(cd.LevelStart, cd.LevelStop);
            double[] filtered = Array.FindAll(raw.Levels, l => l >= lo && l <= hi);
            levels = new ContourLevelSet(filtered);
        }
        else
        {
            levels = ContourExtractor.LevelsBetween(grid, Math.Max(1, cd.LevelCount));
        }

        cd.Grid    = grid;
        cd.Scatter = scatter;
        cd.Levels  = levels;

        _parent.Notify();
    }

    private static void ClearContourGrid(ContourData cd)
    {
        cd.Grid    = null;
        cd.Scatter = null;
        cd.Levels  = new ContourLevelSet(Array.Empty<double>());
    }

    private bool EnsureLoadpullSurface()
    {
        DataSourceEntryViewModel? entry = null;
        string? tracePath = _trace.SourcePath;

        if (tracePath is not null)
        {
            var sel = _parent.Library?.SelectedEntry;
            if (sel is not null &&
                string.Equals(sel.FilePath, tracePath, StringComparison.OrdinalIgnoreCase))
                entry = sel;
            else
                entry = _parent.LibraryEntries.FirstOrDefault(e =>
                    string.Equals(e.FilePath, tracePath, StringComparison.OrdinalIgnoreCase));
        }

        entry ??= _parent.Library?.SelectedEntry;

        if (entry?.Data is not { } ds) return false;

        if (_loadpullSurface is null ||
            !string.Equals(entry.FilePath, _surfaceSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _loadpullSurface   = new LoadpullSurface(ds);
                _surfaceSourcePath = entry.FilePath;
            }
            catch
            {
                _loadpullSurface = null;
                return false;
            }
            RebuildMetricList();
            RebuildFrequencyList();
        }
        return _loadpullSurface.Frequencies.Count > 0;
    }

    private void RebuildMetricList()
    {
        AvailableMetrics.Clear();
        var entry = _parent.Library?.SelectedEntry;
        if (entry?.Data is not { } ds) return;
        foreach (var group in ds.Groups)
            foreach (var kvp in ds.CubesIn(group))
            {
                if (kvp.Key == "GammaLoad" || kvp.Key.StartsWith("__", StringComparison.Ordinal)) continue;
                if (kvp.Value.Axes.Any(a => a.Name == "gridPoint") && !AvailableMetrics.Contains(kvp.Key))
                    AvailableMetrics.Add(kvp.Key);
            }
    }

    private void RebuildFrequencyList()
    {
        AvailableFrequencies.Clear();
        if (_loadpullSurface is null) return;
        foreach (var f in _loadpullSurface.Frequencies)
            AvailableFrequencies.Add($"{f / 1e9:G4} GHz");
        OnPropertyChanged(nameof(ShowContourFreqPicker));
    }

    private void SyncContourVmFromData(ContourData cd)
    {
        _suppressContourCallback = true;
        ContourLevelStart = cd.LevelStart;
        ContourLevelStep  = cd.LevelStep;
        ContourLevelStop  = cd.LevelStop;
        _suppressContourCallback = false;
    }

    // ---- Cube-bound discriminators (Phase 7.2c-a) --------------------------

    /// <summary>True when the trace is in cube-bound mode (not SNP/matrix).</summary>
    public bool IsCubeBoundTrace => _trace.IsCubeBound;

    /// <summary>YAxis combo is shown for network-bound Rect/Table; hidden for cube-bound.</summary>
    public bool ShowYAxisCombo => IsRectOrTablePlot && !IsCubeBoundTrace;

    /// <summary>MatrixType (S/Z/Y) combo visible for S-parameter network sources only.</summary>
    public bool ShowMatrixTypeCombo => !_trace.IsCubeBound && _trace.Data is { } d && !d.IsEmpty;

    // ---- Combined data picker (replaces SNP source + Row + Col) ----------
    //
    //  One item per (SNP × matrix-element) plus derived-parameter items for
    //  2-port SNPs.  Rebuilt when MatrixType or the library contents change.
    //  Selection revert is avoided by NOT calling RebuildSignals from
    //  RefreshDescription (which is called from RebuildAndNotify).

    // Full unfiltered signal set (rebuilt by RebuildSignals); AvailableSignals is the slice for SelectedGroup.
    private readonly List<TraceDataItem> _allSignals = new();

    /// <summary>Group headers for the left picker combo (distinct, in build order).</summary>
    public ObservableCollection<string> AvailableGroups { get; } = new();

    [ObservableProperty]
    private string? _selectedGroup;

    partial void OnSelectedGroupChanged(string? value)
    {
        if (_suppressDataCallback) return;
        _suppressDataCallback = true;
        FilterSignalsToGroup(value);
        _suppressDataCallback = false;
        SelectedSignal = AvailableSignals.FirstOrDefault();
    }

    private void FilterSignalsToGroup(string? group)
    {
        AvailableSignals.Clear();
        if (group is null) return;
        foreach (var s in _allSignals)
            if (s.Group == group) AvailableSignals.Add(s);
        OnPropertyChanged(nameof(IsViSelector));
    }

    public ObservableCollection<TraceDataItem> AvailableSignals { get; } = new();

    /// <summary>True when the item list is the analysis V/I pair (render as an IconSelectButton).</summary>
    public bool IsViSelector =>
        AvailableSignals.Count > 0 &&
        AvailableSignals.All(s => s.IsCubeBound && (s.Label == "V" || s.Label == "I"));

    // ---- Axis-role editor (Phase 7.3a) ------------------------------------
    //
    //  One row per DataCube axis.  Rebuilt when CubeName changes.
    //  Edits write back to Trace.Slice (name-keyed) and trigger RebuildAndNotify.

    public ObservableCollection<AxisRoleRowViewModel> AxisRoles { get; } = new();

    // ---- Unified node/branch visibility toggle ----------------------------------
    //
    //  ShowAll = false (default) → both node-axis filter and branch-list filter are ON.
    //  true → reveals unlabeled nodes AND device-port branch currents.
    //  Absent __LabeledNodes (hand-written netlist) defaults ShowAll=true.

    [ObservableProperty]
    private bool _showAll;

    private bool _rebuildingAxisRoles;

    partial void OnShowAllChanged(bool value)
    {
        if (_rebuildingAxisRoles) return;
        RebuildSignals();
    }

    // True when the current cube has a filterable label axis (node or branch) — controls toggle visibility.
    private bool _hasNodeAxis;

    /// <summary>True when the "Show all" toggle is relevant (cube has a filterable label axis).</summary>
    public bool ShowAllNodesToggleVisible => IsCubeBoundTrace && _hasNodeAxis;

    /// <summary>True when the unified "Show all" toggle is relevant.</summary>
    public bool ShowAllToggleVisible => IsCubeBoundTrace && _hasNodeAxis;

    [ObservableProperty]
    private TraceDataItem? _selectedSignal;

    partial void OnSelectedSignalChanged(TraceDataItem? value)
    {
        if (_suppressDataCallback || value == null) return;

        if (value.IsAbsent)
        {
            _trace.CubeName = value.CubeName;
            _trace.Slice    = Array.Empty<AxisSlice>();
            AxisRoles.Clear();
            _parent.RebuildAndNotify();
            OnPropertyChanged(nameof(ShowEmptyQuantity));
            OnPropertyChanged(nameof(EmptyQuantityMessage));
            OnPropertyChanged(nameof(ShowAxisRoles));
            return;
        }

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
        OnPropertyChanged(nameof(ShowZ0Row));
        OnPropertyChanged(nameof(ShowMatrixTypeCombo));
        OnPropertyChanged(nameof(IsMultiPortNormalization));
        OnPropertyChanged(nameof(IsZ0Editable));
        OnPropertyChanged(nameof(Z0DisabledReason));
        if (alreadyApplied) return;

        // All picker signals come from SelectedEntry, so stamp the sentinel ref.
        _trace.SourceRef  = DataSourceRef.Selected;
        _trace.SourcePath = value.Entry.FilePath;

        if (value.IsCubeBound)
        {
            // Carry over as much of the prior slice as the new cube allows (match by axis name),
            // then re-derive Expression so the spec adapts to the new data source.
            var oldSlice = _trace.Slice;          // may be from a different cube (e.g. V → I)
            _trace.CubeName        = value.CubeName;

            // Rank-0 (scalar) cube: empty slice, bare-name Expression — do not index Axes[0].
            var cubeForRank = (value.Entry.Data is not null && value.CubeName is not null
                && value.Entry.Data.Contains(value.CubeName))
                ? value.Entry.Data[value.CubeName] : null;
            if (cubeForRank?.Rank == 0)
            {
                _trace.Slice = Array.Empty<AxisSlice>();
            }
            else
            {
                _trace.Slice = BuildCarriedSlice(value, oldSlice);
            }
            _trace.InvalidSpecText = null;
            _trace.ExpressionError = null;
            _trace.Expression      = _trace.BuildPickerExpression();

            // Cube-bound traces have no per-port Z0 from the S matrix.
            _trace.SourceZ0PerPort   = null;
            _trace.SourceZ0IsUnusual = false;
            RebuildAxisRoles();
        }
        else
        {
            // Switching to network-bound: clear ALL cube identity, INCLUDING the expression —
            // otherwise IsCubeBound (CubeName is not null || Expression is not null) stays true
            // and the card keeps showing HB fields.
            _trace.CubeName        = null;
            _trace.Slice           = null;
            _trace.Expression      = null;
            _trace.InvalidSpecText = null;
            _trace.ExpressionError = null;
            _trace.Transform       = CubeTransform.None;
            AxisRoles.Clear();
            _trace.Data = value.Entry.Snp!;

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

        // Source kind may have flipped (cube ↔ network) — refresh every card-visibility discriminator
        // so the right fields show without reopening the inspector.
        OnPropertyChanged(nameof(IsCubeBoundTrace));
        OnPropertyChanged(nameof(ShowAllToggleVisible));
        RefreshDescription();   // raises ShowMatrixTypeCombo, ShowZ0Row/Control, ShowYAxisCombo, TraceTransformItems, Spec*, etc.
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

    // ---- Unified transform combo (§3+4) ------------------------------------
    //
    //  One combo drives both cube-bound and network traces.
    //  For network traces, CubeTransform is mapped to/from DependentVarFormat.
    //  TraceTransformItems returns AllCubeTransforms or AllTransformsForNetwork
    //  depending on the trace type; SelectedTransformItem is kept in sync with
    //  the trace's actual YAxis/Transform in SyncTransformItem().
    //
    //  Implemented manually (not via [ObservableProperty]) so SyncTransformItem()
    //  can set the backing field directly without triggering the rebuild callback.

    private CubeTransformItem? _selectedTransformItem;
    private bool _suppressTransformCallback;

    public CubeTransformItem? SelectedTransformItem
    {
        get => _selectedTransformItem;
        set
        {
            if (ReferenceEquals(_selectedTransformItem, value)) return;
            _selectedTransformItem = value;
            OnPropertyChanged();
            if (!_suppressTransformCallback)
                ApplySelectedTransform(value);
        }
    }

    private void ApplySelectedTransform(CubeTransformItem? value)
    {
        if (value is null || !value.Enabled) return;
        if (_trace.IsCubeBound)
        {
            _trace.Transform = value.Transform;
            // Recompute the expression from the updated picker state (Transform changed).
            _trace.Expression = null;
            _trace.Expression = _trace.BuildPickerExpression();
        }
        else
        {
            _trace.YAxis = CubeTransformToYAxis(value.Transform);
        }
        _parent.RebuildAndNotify();
    }

    /// <summary>Returns the per-trace transform list: all-enabled for cube, network-filtered otherwise.</summary>
    public IReadOnlyList<CubeTransformItem> TraceTransformItems =>
        _trace.IsCubeBound
            ? PlotInspectorViewModel.AllCubeTransforms
            : PlotInspectorViewModel.AllTransformsForNetwork;

    /// <summary>
    /// Syncs SelectedTransformItem to the trace's current YAxis/Transform without triggering
    /// the rebuild callback. Called from RefreshDescription so the unified combo stays in step
    /// after the source signal or plot type changes.
    /// </summary>
    private void SyncTransformItem()
    {
        CubeTransformItem? item;
        if (_trace.IsCubeBound)
        {
            item = PlotInspectorViewModel.AllCubeTransforms
                .FirstOrDefault(t => t.Transform == _trace.Transform);
        }
        else
        {
            var ct = YAxisToCubeTransform(_trace.YAxis);
            item = PlotInspectorViewModel.AllTransformsForNetwork
                .FirstOrDefault(t => t.Transform == ct);
        }
        if (!ReferenceEquals(_selectedTransformItem, item))
        {
            _suppressTransformCallback = true;
            SelectedTransformItem = item;
            _suppressTransformCallback = false;
        }
    }

    private static CubeTransform YAxisToCubeTransform(DependentVarFormat f) => f switch
    {
        DependentVarFormat.Db        => CubeTransform.dB20,
        DependentVarFormat.Mag       => CubeTransform.Mag,
        DependentVarFormat.Phase     => CubeTransform.Phase,
        DependentVarFormat.Real      => CubeTransform.Real,
        DependentVarFormat.Imaginary => CubeTransform.Imag,
        _                            => CubeTransform.None,   // Complex → None
    };

    private static DependentVarFormat CubeTransformToYAxis(CubeTransform ct) => ct switch
    {
        CubeTransform.dB20  => DependentVarFormat.Db,
        CubeTransform.Mag   => DependentVarFormat.Mag,
        CubeTransform.Phase => DependentVarFormat.Phase,
        CubeTransform.Real  => DependentVarFormat.Real,
        CubeTransform.Imag  => DependentVarFormat.Imaginary,
        _                   => DependentVarFormat.Complex,    // None/dB10/dB/Conj → Complex
    };

    /// <summary>
    /// Called by PlotInspectorViewModel when the plot Freq unit changes so harmonic
    /// axis-role pin labels are rebuilt with the new unit.
    /// </summary>
    internal void OnFreqUnitChanged()
    {
        RebuildAxisRoles();
        OnPropertyChanged(nameof(AxisRoles));
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

    public IRelayCommand ToggleShowAllCommand { get; }

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

    /// <summary>Gates the entire Z0 row — only S-param (non-cube, non-derived) traces show it.</summary>
    public bool ShowZ0Row => IsScatteringTrace;

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
        OnPropertyChanged(nameof(SelectedLineColor));
    }

    public ColorItem? SelectedLineColor
    {
        get => PlotInspectorViewModel.ColorItems.FirstOrDefault(c => c.Index == LineColorIndex)
               ?? PlotInspectorViewModel.ColorItems[0];
        set { if (value is not null && value.Index != LineColorIndex) LineColorIndex = value.Index; }
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
        OnPropertyChanged(nameof(SelectedMarkerColor));
    }

    public ColorItem? SelectedMarkerColor
    {
        get => PlotInspectorViewModel.ColorItems.FirstOrDefault(c => c.Index == MarkerColorIndex)
               ?? PlotInspectorViewModel.ColorItems[0];
        set { if (value is not null && value.Index != MarkerColorIndex) MarkerColorIndex = value.Index; }
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
        if (_parent.Library is { } lib)
            lib.SelectedDataSourceChanged -= OnSelectedDataSourceChanged;
    }

    // ---- Command ------------------------------------------------------------

    public IRelayCommand RemoveCommand { get; }

    // ---- Contour commands (Phase 7.4e) ----------------------------------------

    public IRelayCommand SetConstraintCompressionCommand  { get; private set; } = null!;
    public IRelayCommand SetConstraintConstantCommand     { get; private set; } = null!;
    public IRelayCommand SetRangeLevelModeCommand         { get; private set; } = null!;
    public IRelayCommand SetCountLevelModeCommand         { get; private set; } = null!;
    public IRelayCommand ToggleShowIsoLinesCommand        { get; private set; } = null!;
    public IRelayCommand ToggleShowFillCommand            { get; private set; } = null!;
    public IRelayCommand ToggleShowLabelsCommand          { get; private set; } = null!;
    public IRelayCommand SetTopoMapFillCommand            { get; private set; } = null!;
    public IRelayCommand SetHeatMapFillCommand            { get; private set; } = null!;
    public IRelayCommand ToggleOptionsCommand             { get; private set; } = null!;

    public bool IsTopoMapFill => ContourSelectedFillKind == ContourFillKind.TopoMap;
    public bool IsHeatMapFill => ContourSelectedFillKind == ContourFillKind.HeatMap;

    public static IReadOnlyList<ContourColorMap> AllContourColorMaps { get; } =
        Enum.GetValues<ContourColorMap>().ToList();

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

        // Unified transform item — cube or mapped-from-YAxis.
        if (trace.IsCubeBound)
        {
            _selectedTransformItem = PlotInspectorViewModel.AllCubeTransforms
                .FirstOrDefault(t => t.Transform == trace.Transform);
        }
        else
        {
            var ct = YAxisToCubeTransform(trace.YAxis);
            _selectedTransformItem = PlotInspectorViewModel.AllTransformsForNetwork
                .FirstOrDefault(t => t.Transform == ct);
        }

        RemoveCommand              = new RelayCommand(() => _parent.RemoveTrace(this));
        ToggleSecondaryAxisCommand = new RelayCommand(() => UseSecondaryAxis = !UseSecondaryAxis);
        ToggleShowAllCommand       = new RelayCommand(() => ShowAll = !ShowAll);

        SetConstraintCompressionCommand  = new RelayCommand(() => ContourConstraintKind = ConstraintKind.Compression);
        SetConstraintConstantCommand     = new RelayCommand(() => ContourConstraintKind = ConstraintKind.ConstantMetric);
        SetRangeLevelModeCommand         = new RelayCommand(() => ContourLevelMode      = ContourLevelMode.Range);
        SetCountLevelModeCommand         = new RelayCommand(() => ContourLevelMode      = ContourLevelMode.Count);
        ToggleShowIsoLinesCommand        = new RelayCommand(() => ContourShowIsoLines   = !ContourShowIsoLines);
        ToggleShowFillCommand            = new RelayCommand(() => ContourShowFill       = !ContourShowFill);
        ToggleShowLabelsCommand          = new RelayCommand(() => ContourShowLabels     = !ContourShowLabels);
        SetTopoMapFillCommand            = new RelayCommand(() => ContourSelectedFillKind = ContourFillKind.TopoMap);
        SetHeatMapFillCommand            = new RelayCommand(() => ContourSelectedFillKind = ContourFillKind.HeatMap);
        ToggleOptionsCommand             = new RelayCommand(() => ContourOptionsExpanded  = !ContourOptionsExpanded);

        // Initialize contour VM fields from ContourData (if this is a contour trace).
        if (trace.ContourData is { } cd)
        {
            _contourMetricName       = cd.MetricName;
            _contourConstraintKind   = cd.ContourConstraintKind;
            _contourConstraintMetric = cd.ConstraintMetricName;
            _contourConstraintValue  = cd.ConstraintValue;
            _contourFreqIndex        = cd.FreqIndex;
            _contourLevelMode        = cd.LevelMode;
            _contourLevelStart       = cd.LevelStart;
            _contourLevelStep        = cd.LevelStep;
            _contourLevelStop        = cd.LevelStop;
            _contourLevelCount       = cd.LevelCount;
            _contourShowIsoLines     = cd.ShowIsoLines;
            _contourShowFill         = cd.ShowFill;
            _contourShowLabels       = cd.DrawLabels;
            _contourSelectedFillKind = cd.SelectedFillKind;
            _contourColorMap         = cd.ColorMap;
            _contourLabelSpacing     = cd.LabelSpacing;
            // Build surface and populate metric/frequency lists; trigger initial fit.
            if (EnsureLoadpullSurface())
                RebuildContour();
        }

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

        // Refresh signals when the selected datasource changes.
        if (_parent.Library is { } lib)
            lib.SelectedDataSourceChanged += OnSelectedDataSourceChanged;
    }

    // ---- Signal list management ---------------------------------------------

    private void OnLibraryEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildSignals();

    private void OnSelectedDataSourceChanged(object? s, EventArgs e)
        => RebuildSignals();

    private void RebuildSignals()
    {
        _allSignals.Clear();
        AvailableGroups.Clear();
        AvailableSignals.Clear();

        bool isComplexPlot = _parent.PlotType is PlotType.Smith or PlotType.Polar;
        // Picker shows only the selected datasource (single-source brief).
        var selectedEntry = _parent.Library?.SelectedEntry;
        bool singleSource = true;  // always single-source now

        // ---- Network-bound signals (matrix / derived) -----------------------
        foreach (var entry in selectedEntry is null
            ? System.Linq.Enumerable.Empty<DataSourceEntryViewModel>()
            : new[] { selectedEntry })
        {
            if (entry.Snp is null) continue;
            var snp = entry.Snp;

            string netGroup = (singleSource ? "" : $"{System.IO.Path.GetFileNameWithoutExtension(entry.DisplayName)}..")
                            + "S-Parameters";

            if (snp.IsEmpty)
            {
                bool isSource = _trace.Data == snp;
                int row = isSource ? _trace.Row : 0;
                int col = isSource ? _trace.Col : 0;
                _allSignals.Add(new TraceDataItem(entry, MatrixType, row, col,
                    omitFilePrefix: true, isBroken: true) { Group = netGroup });
                continue;
            }

            int ports = snp.Ports;

            if (_trace.Data == snp
                && _trace.Derived == DerivedParameters.None
                && (_trace.Row >= ports || _trace.Col >= ports))
            {
                _allSignals.Add(new TraceDataItem(entry, MatrixType,
                    _trace.Row, _trace.Col, omitFilePrefix: true, isBroken: true) { Group = netGroup });
            }

            for (int r = 0; r < ports; r++)
                for (int c = 0; c < ports; c++)
                    _allSignals.Add(new TraceDataItem(entry, MatrixType, r, c, omitFilePrefix: true) { Group = netGroup });

            if (ports == 2)
            {
                _allSignals.Add(new TraceDataItem(entry, DerivedParameters.SourceStabilityCircle, _parent.PlotType, omitFilePrefix: true) { Group = netGroup });
                _allSignals.Add(new TraceDataItem(entry, DerivedParameters.LoadStabilityCircle,   _parent.PlotType, omitFilePrefix: true) { Group = netGroup });
                _allSignals.Add(new TraceDataItem(entry, DerivedParameters.MuPrime, _parent.PlotType, omitFilePrefix: true) { Group = netGroup });
                _allSignals.Add(new TraceDataItem(entry, DerivedParameters.Mu,      _parent.PlotType, omitFilePrefix: true) { Group = netGroup });
                _allSignals.Add(new TraceDataItem(entry, DerivedParameters.MaxGain, _parent.PlotType, omitFilePrefix: true) { Group = netGroup });
            }
        }

        // ---- Cube-bound signals (Phase 7.3a: one item per cube, axis roles via editor) ------
        //
        // One ComboBox entry per plottable cube (rank ≥1, skip S/Z0).
        // Default slice: first axis as X, remainder pinned at index 0.
        // The axis-role editor below the combo lets users reassign roles without
        // generating a combinatorial explosion of signal items.
        foreach (var entry in selectedEntry is null
            ? System.Linq.Enumerable.Empty<DataSourceEntryViewModel>()
            : new[] { selectedEntry })
        {
            var ds = entry.Data;
            if (ds is null) continue;

            string filePrefix = singleSource
                ? ""
                : $"{System.IO.Path.GetFileNameWithoutExtension(entry.DisplayName)}..";

            foreach (var group in ds.Groups)
            {
                string groupDisplay = group == DataSet.DefaultGroup      ? "Signals"
                                    : group == DataSet.MeasurementsGroup ? "Measurements"
                                    :                                       group;   // "HB1", "DC1", "SP1"
                string cubeGroup = filePrefix + groupDisplay;

                foreach (var (bareName, cube) in ds.CubesIn(group))
                {
                    if (bareName == "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
                    // Default-group S belongs to the network/SNP path (Touchstone). S in a named analysis
                    // group is a simulated S cube (no SNP) — offer it as a first-class cube.
                    if (bareName == "S" && group == DataSet.DefaultGroup) continue;
                    // Solver diagnostics — not offered in the picker; advanced users type them in the spec field.
                    if (bareName.EndsWith("Converged", StringComparison.Ordinal) ||
                        bareName.EndsWith("Residual",  StringComparison.Ordinal)) continue;
                    // Skip node-indexed current cubes (internal diagnostic / legacy guard).
                    bool isNodeIndexedCurrent =
                        (bareName == "I" || bareName == "INl")
                        && cube.Axes.Any(a => a.Name == "node");
                    if (isNodeIndexedCurrent) continue;
                    int rank = cube.Rank;
                    if (rank == 0 && !_parent.IsTablePlot) continue;   // scalars are Table-only

                    bool isEnabled = !isComplexPlot || cube.DataKind == DataKind.Complex;
                    // Default- and measurements-group cubes are bare-resolvable — emit their bare
                    // name so the picker yields `PDC`/`V`, matching typed input. Analysis cubes
                    // must stay qualified (bare `V` would resolve to the wrong group).
                    string qualified =
                        (group == DataSet.DefaultGroup || group == DataSet.MeasurementsGroup)
                            ? bareName
                            : $"{group}.{bareName}";

                    // freq → X when present (parameter / freq-swept cubes), else first non-label axis;
                    // all other axes pinned at index 0.
                    AxisSlice[] defaultSlice = BuildDefaultSlice(cube);

                    _allSignals.Add(new TraceDataItem(entry, qualified, defaultSlice, bareName, isEnabled)
                                    { Group = cubeGroup });
                }
            }
        }

        // ---- Ensure each analysis group offers both V and I (absent placeholder when cube missing) ----
        foreach (var grpName in _allSignals.Select(s => s.Group).Distinct().ToList())
        {
            var items = _allSignals.Where(s => s.Group == grpName).ToList();
            var vItem = items.FirstOrDefault(s => s.IsCubeBound && s.Label == "V" && (s.CubeName?.Contains('.') ?? false));
            var iItem = items.FirstOrDefault(s => s.IsCubeBound && s.Label == "I" && (s.CubeName?.Contains('.') ?? false));
            if (vItem is null && iItem is null) continue;          // not an analysis group
            var sample = (vItem ?? iItem)!;
            string cubeName = sample.CubeName!;
            string prefix = cubeName[..(cubeName.IndexOf('.') + 1)];   // e.g. "HB1."
            if (vItem is null)
                _allSignals.Add(new TraceDataItem(sample.Entry, prefix + "V", Array.Empty<AxisSlice>(), "V")
                                { Group = grpName, IsAbsent = true });
            if (iItem is null)
                _allSignals.Add(new TraceDataItem(sample.Entry, prefix + "I", Array.Empty<AxisSlice>(), "I")
                                { Group = grpName, IsAbsent = true });
        }

        // ---- Select the item matching the current trace state ---------------
        TraceDataItem? match = null;

        if (_trace.IsCubeBound)
        {
            // Match by source + cube only; slice is managed via the axis-role editor.
            match = _allSignals.FirstOrDefault(s =>
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
                    match = _allSignals.FirstOrDefault(s => s.IsBroken && s.Entry == matchEntry);
                }
                else if (_trace.Derived != DerivedParameters.None)
                {
                    match = _allSignals.FirstOrDefault(s => s.Entry == matchEntry
                        && s.Derived == _trace.Derived);
                }
                else
                {
                    match = _allSignals.FirstOrDefault(s => s.Entry == matchEntry
                        && s.Row == _trace.Row && s.Col == _trace.Col
                        && s.Derived == DerivedParameters.None && !s.IsBroken);
                }
            }
        }

        foreach (var s in _allSignals)
            if (!AvailableGroups.Contains(s.Group)) AvailableGroups.Add(s.Group);

        _suppressDataCallback = true;
        SelectedGroup  = match?.Group ?? AvailableGroups.FirstOrDefault();
        FilterSignalsToGroup(SelectedGroup);
        SelectedSignal = match ?? AvailableSignals.FirstOrDefault();
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
        OnPropertyChanged(nameof(ShowZ0Row));
        OnPropertyChanged(nameof(ShowMatrixTypeCombo));
        OnPropertyChanged(nameof(IsMultiPortNormalization));
        OnPropertyChanged(nameof(IsZ0Editable));
        OnPropertyChanged(nameof(Z0DisabledReason));

        // Rebuild axis-role rows for the currently selected cube (Phase 7.3a).
        // Track ShowAll before/after: RebuildAxisRolesCore may auto-set ShowAll=true for
        // hand-written netlists (absent __LabeledNodes) while the _rebuildingAxisRoles guard
        // suppresses the usual OnShowAllChanged→RebuildSignals callback. If ShowAll changed,
        // re-run so AvailableSignals reflects the new (all-show) state.
        bool showAllSnapshot = ShowAll;
        RebuildAxisRoles();
        if (ShowAll != showAllSnapshot)
            RebuildSignals();
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
        _rebuildingAxisRoles = true;
        try { RebuildAxisRolesCore(); }
        finally { _rebuildingAxisRoles = false; }
    }

    private static string SiblingCubeName(string cubeName, string sibling)
    {
        int dot = cubeName.IndexOf('.');
        return dot > 0 ? string.Concat(cubeName.AsSpan(0, dot), ".", sibling) : sibling;
    }

    private void RebuildAxisRolesCore()
    {
        AxisRoles.Clear();

        if (!_trace.IsCubeBound || _trace.CubeName is null) return;

        // For sentinel traces, look in SelectedEntry; for cross-schematic, scan all Entries.
        var entry = _parent.Library?.SelectedEntry is { } sel &&
                    string.Equals(sel.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase)
            ? sel
            : _parent.LibraryEntries.FirstOrDefault(e =>
                  string.Equals(e.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase));
        var ds = entry?.Data;
        if (ds is null || !ds.Contains(_trace.CubeName)) return;

        var cube  = ds[_trace.CubeName];
        var slice = _trace.Slice;

        // Which axis (if any) is the filterable label axis, and its provenance side-cube.
        string? filterAxisName = null, provenanceCube = null;
        foreach (var ax in cube.Axes)
        {
            if (ax.Name == "node")   { filterAxisName = "node";   provenanceCube = "__LabeledNodes";  break; }
            if (ax.Name == "branch") { filterAxisName = "branch"; provenanceCube = "__ProbeBranches"; break; }
        }

        HashSet<string>? labeledSet = null;
        if (provenanceCube is not null)
        {
            string sib = SiblingCubeName(_trace.CubeName, provenanceCube);
            if (ds.Contains(sib))
            {
                labeledSet = new HashSet<string>(StringComparer.Ordinal);
                var lblCube   = ds[sib];
                var labelAxis = lblCube.Axes.FirstOrDefault(a => a.Labels is not null);   // "label" or "probe"
                if (labelAxis?.Labels is { } lbls) foreach (var l in lbls) labeledSet.Add(l);
            }
        }

        bool hasFilterAxis = filterAxisName is not null;
        if (_hasNodeAxis != hasFilterAxis)            // _hasNodeAxis now means "has a filterable label axis"
        {
            _hasNodeAxis = hasFilterAxis;
            OnPropertyChanged(nameof(ShowAllNodesToggleVisible));
            OnPropertyChanged(nameof(ShowAllToggleVisible));
        }

        if (labeledSet is null && !ShowAll) ShowAll = true;   // no provenance ⇒ show all (unchanged)
        bool showAll = ShowAll;

        for (int d = 0; d < cube.Rank; d++)
        {
            var axis = cube.Axes[d];

            // Find matching slice entry by axis name.
            bool isX      = false;
            bool isFamily = false;
            int  savedTrueIdx = 0;
            if (slice is not null)
            {
                foreach (var s in slice)
                {
                    if (s.AxisName == axis.Name)
                    {
                        isX          = s.Role == AxisRole.KeepAsX;
                        isFamily     = s.Role == AxisRole.FamilyIterate;
                        savedTrueIdx = s.Index;
                        break;
                    }
                }
            }

            // Build label list for the pin combo.
            // Frequency axes (unit == "Hz") are formatted in the plot's display FreqUnit.
            bool axisIsFreq = IsFreqUnit(axis.Unit);
            FreqUnit plotFreqUnit = _parent.FreqUnit;

            if (axis.Name == filterAxisName && !showAll && labeledSet is not null)
            {
                // Filtered: only show options that are in the labeled set.
                var filteredOpts    = new List<string>();
                var filteredIndices = new List<int>();

                for (int k = 0; k < axis.Length; k++)
                {
                    string label = axis.Labels is not null && k < axis.Labels.Length
                        ? axis.Labels[k]
                        : axis.Values[k].ToString("G3");
                    if (labeledSet.Contains(label))
                    {
                        filteredOpts.Add(label);
                        filteredIndices.Add(k);
                    }
                }

                // Restore display index from the saved true axis index.
                int displayIdx = filteredIndices.IndexOf(savedTrueIdx);
                if (displayIdx < 0) displayIdx = 0;

                AxisRoles.Add(new AxisRoleRowViewModel(this, axis.Name, axis.Unit,
                    filteredOpts, isX, displayIdx, filteredIndices, optionsAreLabels: true, isFamily: isFamily,
                    isFilterableLabelAxis: true));
            }
            else
            {
                // Unfiltered (all options).
                var opts = new List<string>(axis.Length);
                bool hasLabels = axis.Labels is not null && axis.Labels.Length > 0;
                for (int k = 0; k < axis.Length; k++)
                {
                    if (hasLabels && k < axis.Labels!.Length)
                        opts.Add(axis.Labels[k]);
                    else if (axisIsFreq)
                        opts.Add($"{(axis.Values[k] * plotFreqUnit.Scale()).ToString("G4")} {plotFreqUnit.Description()}");
                    else
                        opts.Add(axis.Values[k].ToString("G3"));
                }
                int pinIdx = Math.Clamp(savedTrueIdx, 0, Math.Max(0, axis.Length - 1));
                AxisRoles.Add(new AxisRoleRowViewModel(this, axis.Name, axis.Unit, opts, isX, pinIdx,
                    optionsAreLabels: hasLabels, isFamily: isFamily,
                    isFilterableLabelAxis: axis.Name == filterAxisName));
            }
        }

        // Guard: if no X axis, promote the first non-family, non-label row.
        // A null fallback means only label/family axes exist → no X → scalar (valid for no-sweep DC).
        if (!AxisRoles.Any(r => r.IsX))
        {
            var fallback = AxisRoles.FirstOrDefault(r =>
                !r.IsFamily && r.AxisName is not "node" and not "branch");
            fallback?.SetIsXSilent(true);
        }
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
    /// Called by AxisRoleRowViewModel when a row is set to Family.
    /// Silently demotes any other Family row back to Pinned (≤1 family constraint).
    /// </summary>
    internal void OnAxisSetToFamily(AxisRoleRowViewModel newFamily)
    {
        foreach (var row in AxisRoles)
            if (!ReferenceEquals(row, newFamily) && row.IsFamily)
                row.SetIsFamilySilent(false);
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
            AxisRole role = r.IsX ? AxisRole.KeepAsX
                          : r.IsFamily ? AxisRole.FamilyIterate
                          : AxisRole.PinToIndex;
            string lbl = (role == AxisRole.PinToIndex && r.OptionsAreLabels && r.PinOptions.Count > 0)
                ? r.PinOptions[Math.Clamp(r.PinIndex, 0, r.PinOptions.Count - 1)]
                : "";
            slice[i] = new AxisSlice(r.AxisName, role, r.TruePinIndex, Label: lbl);
        }

        // Guard: if no X survived, promote the first non-family, non-label axis.
        // If none exists (only label/family axes), leave no X → scalar (valid for no-sweep DC).
        bool hasX = Array.Exists(slice, s => s.Role == AxisRole.KeepAsX);
        if (!hasX && slice.Length > 0)
        {
            int fb = Array.FindIndex(slice, s =>
                s.Role != AxisRole.FamilyIterate && s.AxisName is not "node" and not "branch");
            if (fb >= 0)
            {
                slice[fb] = new AxisSlice(slice[fb].AxisName, AxisRole.KeepAsX, 0);
                AxisRoles[fb].SetIsXSilent(true);
            }
            // else: only label/family axes → no X → scalar. Leave as-is.
        }

        _trace.Slice = slice;
        // Sync the expression text field to the picker-authored shorthand.
        _trace.Expression = null;
        _trace.Expression = _trace.BuildPickerExpression();
        _parent.RebuildAndNotify();
    }

    /// <summary>
    /// Index of the default X axis for a cube: the "freq" axis when present (S/Y/Z parameter cubes and
    /// any freq-swept cube), else the first non-label (node/branch) axis. Returns -1 when only label
    /// axes exist (→ no X → scalar, valid for no-sweep DC).
    /// </summary>
    internal static int DefaultXAxis(RfCore.Data.DataCube cube)
    {
        for (int d = 0; d < cube.Rank; d++)
            if (cube.Axes[d].Name == "freq") return d;
        for (int d = 0; d < cube.Rank; d++)
            if (cube.Axes[d].Name is not "node" and not "branch") return d;
        return -1;
    }

    /// <summary>
    /// Default slice for a cube: <see cref="DefaultXAxis"/> → KeepAsX, every other axis pinned at index 0
    /// (carrying its first label for quoted net names). Rank-0 → empty slice. For an S cube
    /// [freq, i, j] (+ optional swept prefix) this is S(1,1) over frequency with i/j and the sweep pinned.
    /// </summary>
    internal static AxisSlice[] BuildDefaultSlice(RfCore.Data.DataCube cube)
    {
        int rank = cube.Rank;
        if (rank == 0) return Array.Empty<AxisSlice>();
        int xIdx = DefaultXAxis(cube);
        var slice = new AxisSlice[rank];
        for (int d = 0; d < rank; d++)
        {
            var ax = cube.Axes[d];
            if (d == xIdx)
                slice[d] = new AxisSlice(ax.Name, AxisRole.KeepAsX, 0);
            else
            {
                string lbl = (ax.Labels is { Length: > 0 }) ? ax.Labels[0] : "";
                slice[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, 0, Label: lbl);
            }
        }
        return slice;
    }

    /// <summary>True for an S/Y/Z parameter cube (axes "freq", "i", "j") — used to pick the dB20
    /// first-add transform on Rect.</summary>
    internal static bool IsParameterCube(RfCore.Data.DataCube cube)
        => cube.Axes.Any(a => a.Name == "freq")
        && cube.Axes.Any(a => a.Name == "i")
        && cube.Axes.Any(a => a.Name == "j");

    private AxisSlice[] BuildCarriedSlice(TraceDataItem value, AxisSlice[]? oldSlice)
    {
        var ds   = value.Entry.Data;
        var cube = (ds is not null && value.CubeName is not null && ds.Contains(value.CubeName))
            ? ds[value.CubeName] : null;
        if (cube is null) return value.Slice?.ToArray() ?? Array.Empty<AxisSlice>();
        return BuildCarriedSliceFromCube(cube, oldSlice);
    }

    /// <summary>
    /// Builds a slice for <paramref name="cube"/>, preserving as many parameters from
    /// <paramref name="oldSlice"/> as possible (matched by axis NAME): same role (X vs pinned)
    /// and the same pin index when in range, else clamped to 0. Axes absent from the old slice
    /// default to PinToIndex/0. Exactly one axis ends up as X. Label is set from the cube's
    /// axis labels so node slots render as quoted net names (e.g. "Vout2").
    /// </summary>
    internal static AxisSlice[] BuildCarriedSliceFromCube(RfCore.Data.DataCube cube, AxisSlice[]? oldSlice)
    {
        int rank   = cube.Rank;
        var result = new AxisSlice[rank];

        var old = new System.Collections.Generic.Dictionary<string, AxisSlice>(StringComparer.Ordinal);
        if (oldSlice is not null)
            foreach (var s in oldSlice) old[s.AxisName] = s;

        bool anyX = false;
        for (int d = 0; d < rank; d++)
        {
            var ax  = cube.Axes[d];
            int len = ax.Length;

            if (old.TryGetValue(ax.Name, out var prev))
            {
                int idx  = Math.Clamp(prev.Index, 0, Math.Max(0, len - 1));
                var role = prev.Role;
                string lbl = (role == AxisRole.PinToIndex && ax.Labels is { Length: > 0 } && idx < ax.Labels.Length)
                    ? ax.Labels[idx] : "";
                result[d] = new AxisSlice(ax.Name, role, idx, Label: lbl);
                if (role == AxisRole.KeepAsX) anyX = true;
            }
            else
            {
                string lbl = (ax.Labels is { Length: > 0 }) ? ax.Labels[0] : "";
                result[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, 0, Label: lbl);
            }
        }

        if (!anyX && rank > 0)
        {
            int fb = DefaultXAxis(cube);
            if (fb >= 0) result[fb] = result[fb] with { Role = AxisRole.KeepAsX, Label = "" };
            // else: all label axes → no X → scalar.
        }
        else
        {
            bool seenX = false;
            for (int d = 0; d < rank; d++)
                if (result[d].Role == AxisRole.KeepAsX)
                {
                    if (seenX) result[d] = result[d] with { Role = AxisRole.PinToIndex };
                    seenX = true;
                }
        }

        return result;
    }

    // ---- Called by PlotInspectorViewModel after trace paths are rebuilt ----
    //
    //  Deliberately does NOT call RebuildSignals() — calling it here would
    //  clear the AvailableSignals collection in the middle of a callback chain
    //  that originated from OnSelectedSignalChanged, causing Avalonia's ComboBox
    //  to reset its SelectedItem to null (the revert bug).

    public void RefreshDescription()
    {
        OnPropertyChanged(nameof(IsContourTrace));
        OnPropertyChanged(nameof(IsStandardTrace));
        OnPropertyChanged(nameof(IsRectPlot));
        OnPropertyChanged(nameof(IsRectOrTablePlot));
        OnPropertyChanged(nameof(IsTablePlot));
        OnPropertyChanged(nameof(IsNotTablePlot));
        OnPropertyChanged(nameof(IsCubeBoundTrace));
        OnPropertyChanged(nameof(ShowAllToggleVisible));
        OnPropertyChanged(nameof(ShowYAxisCombo));
        OnPropertyChanged(nameof(ShowZ0Badge));
        OnPropertyChanged(nameof(Z0BadgeTooltip));
        OnPropertyChanged(nameof(ShowZ0Control));
        OnPropertyChanged(nameof(ShowZ0Row));
        OnPropertyChanged(nameof(ShowMatrixTypeCombo));
        OnPropertyChanged(nameof(IsMultiPortNormalization));
        OnPropertyChanged(nameof(IsZ0Editable));
        OnPropertyChanged(nameof(Z0DisabledReason));
        OnPropertyChanged(nameof(SpecShorthand));
        OnPropertyChanged(nameof(SpecError));
        OnPropertyChanged(nameof(HasSpecError));
        OnPropertyChanged(nameof(ShowEmptyQuantity));
        OnPropertyChanged(nameof(EmptyQuantityMessage));
        OnPropertyChanged(nameof(ShowAxisRoles));
        // Unified transform combo: rebuild list and re-sync selection to trace state.
        OnPropertyChanged(nameof(TraceTransformItems));
        SyncTransformItem();
    }

    // ---- Inline spec editor (#4) -------------------------------------------

    /// <summary>
    /// Raw editable text for the spec TextBox: the user's last raw input when
    /// invalid, or the canonical shorthand when valid. No " &lt;invalid&gt;" suffix
    /// (that's for the Table column header only).
    /// </summary>
    public string SpecShorthand => _trace.IsCubeBound
        ? (_trace.InvalidSpecText ?? _trace.CubeShorthand)
        : "";

    public bool   ShowEmptyQuantity   => SelectedSignal?.IsAbsent == true;
    public string EmptyQuantityMessage =>
        SelectedSignal?.IsAbsent == true
            ? (SelectedSignal.Label == "I" ? "No branch currents" : "No node voltages")
            : "";
    public bool   ShowAxisRoles       => IsCubeBoundTrace && !ShowEmptyQuantity;

    /// <summary>Human-readable parse/eval error for the spec hint; empty when valid.</summary>
    public string SpecError => _trace.ExpressionError ?? "";

    public bool HasSpecError => !string.IsNullOrEmpty(SpecError);

    /// <summary>
    /// Called from code-behind on Enter / LostFocus to parse and apply the typed spec.
    /// On success, applies (CubeName, Slice, Transform) and rebuilds the plot.
    /// On failure, stores the raw text as InvalidSpecText and shows SpecError.
    /// </summary>
    public void CommitSpec(string text)
    {
        if (!_trace.IsCubeBound) return;

        _trace.Expression      = text;
        _trace.InvalidSpecText = null;
        _trace.ExpressionError = null;

        // Re-derive the picker state from the typed text so the card's comboboxes (harmonic/node pin,
        // transform, axis-role rows) track the edit. A single-cube spec like `V[:, "Vout2", 1]` or
        // `mag(V[:, "Vout", 1])` parses to (CubeName, Slice, Transform); a multi-cube expression does not,
        // in which case we drop the single-cube identity and present it as a free expression.
        string? normalizedSource = _trace.SourcePath is string sp ? System.IO.Path.GetFullPath(sp) : null;
        var ds = (_parent.Library?.SelectedEntry is { } sel2 &&
                  string.Equals(sel2.FilePath, normalizedSource, StringComparison.OrdinalIgnoreCase)
            ? sel2
            : _parent.LibraryEntries.FirstOrDefault(e =>
                  string.Equals(e.FilePath, normalizedSource, StringComparison.OrdinalIgnoreCase)))
            ?.Data;
        if (ds is not null &&
            CubeTraceSpecParser.TryParse(text, ds, out var cubeName, out var slice, out var transform, out _))
        {
            _trace.CubeName  = cubeName;
            _trace.Slice     = slice;
            _trace.Transform = transform;
        }
        else
        {
            // Not a single-cube picker spec (e.g. multi-cube expression) — keep Expression as the source
            // of truth and drop the single-cube identity so the axis-role editor shows nothing stale.
            _trace.CubeName = null;
            _trace.Slice    = null;
        }

        _parent.RebuildAndNotify();

        if (_trace.CubeName is not null)   // valid single-cube spec
            RebuildSignals();              // re-syncs group + item combos AND axis-role rows to the new cube
        else
            RebuildAxisRoles();            // best-effort: clear stale rows; combos unchanged
        OnPropertyChanged(nameof(IsCubeBoundTrace));
        OnPropertyChanged(nameof(ShowAllToggleVisible));
    }

    private static bool IsFreqUnit(string? unit) =>
        unit is "Hz" or "kHz" or "MHz" or "GHz";
}
