// ================================================================
//  TraceRowViewModel.cs  —  Observable wrapper for a single Trace
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;
using SkiaSharp;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

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

    // Standard (line/marker/table) trace body; hidden for contour and summary traces.
    public bool IsStandardTrace => !IsContourTrace && !IsSummaryColumn;

    // ---- Contour trace (Phase 7.4e) -----------------------------------------

    public bool IsContourTrace   => _trace.IsContourTrace;
    public bool IsSummaryColumn  => _trace.IsSummaryColumn;

    // VM-side LoadpullSurface (owned here; Trace must not hold a DataSet per firewall).
    private LoadpullSurface? _loadpullSurface;
    private string?          _surfaceSourcePath;
    // Loadpull group the cached surface was built from ("" = top level / flat .spl). Part of the
    // staleness key so re-binding to a different group (e.g. LP1 → LP2) rebuilds the surface.
    private string?          _surfaceGroup;

    // Suppresses On…Changed callbacks during SyncContourVmFromData.
    private bool _suppressContourCallback;

    public ObservableCollection<string> AvailableMetrics        { get; } = new();
    public ObservableCollection<string> AvailableFrequencies    { get; } = new();
    public ObservableCollection<string> AvailableLoadpullGroups { get; } = new();

    // §7 — wrapper items for the const-metric combo so disabled items can grey out.
    public record ConstraintMetricItem(string Name, bool IsEnabled);
    public ObservableCollection<ConstraintMetricItem> ConstraintMetricOptions { get; } = new();

    [ObservableProperty]
    private ConstraintMetricItem? _selectedConstraintMetricItem;

    partial void OnSelectedConstraintMetricItemChanged(ConstraintMetricItem? value)
    {
        if (_suppressContourCallback || value is null || _trace.ContourData is not { } cd) return;
        cd.ConstraintMetricName = value.Name;
        _suppressContourCallback = true;
        ContourConstraintMetric = value.Name;
        _suppressContourCallback = false;
        RebuildContour();
    }

    private void RebuildConstraintMetricOptions()
    {
        // §6: use alias-group comparison so Gain/Gt/Gp and DE/PAE/Efficiency are treated
        // as the same concept — a constraint metric that aliases to the primary is disabled.
        string primaryGroup = MetricAliasGroup(ContourMetricName);
        ConstraintMetricOptions.Clear();
        foreach (var m in AvailableMetrics)
            ConstraintMetricOptions.Add(new ConstraintMetricItem(m, MetricAliasGroup(m) != primaryGroup));
        // Sync current selection — pick first enabled if current is absent or aliases to primary.
        var match = ConstraintMetricOptions.FirstOrDefault(i => i.Name == ContourConstraintMetric);
        var select = (match?.IsEnabled ?? false) ? match
                   : ConstraintMetricOptions.FirstOrDefault(i => i.IsEnabled);
        _suppressContourCallback = true;
        SelectedConstraintMetricItem = select;
        _suppressContourCallback = false;
        // Always update cd when the constraint metric needs to change (was empty, or now conflicts).
        bool constraintCollides = string.IsNullOrEmpty(ContourConstraintMetric)
            || MetricAliasGroup(ContourConstraintMetric) == primaryGroup;
        if (select is not null && constraintCollides)
        {
            ContourConstraintMetric = select.Name;
            if (_trace.ContourData is { } cd) cd.ConstraintMetricName = select.Name;
        }
    }

    private static string MetricAliasGroup(string? metric) => metric switch
    {
        "Gain" or "Gt" or "Gp" or "Gt_dB" or "Gp_dB" => "Gain",
        "DE" or "PAE" or "Efficiency"  => "Efficiency",
        _                              => metric ?? "",
    };

    [ObservableProperty] private string?          _contourMetricName;
    [ObservableProperty] private ConstraintKind   _contourConstraintKind = ConstraintKind.Compression;
    [ObservableProperty] private string           _contourConstraintMetric = "";
    [ObservableProperty] private double           _contourConstraintValue = 3.0;
    [ObservableProperty] private int              _contourFreqIndex;
    [ObservableProperty] private string?          _selectedLoadpullGroup;
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
    [ObservableProperty] private double           _contourLabelSpacing = 30.0;
    [ObservableProperty] private bool             _contourOptionsExpanded;

    // ---- New fields (7.4h-1b) -----------------------------------------------
    [ObservableProperty] private bool      _contourDisplayMxp;
    [ObservableProperty] private bool      _contourDisplayMxe;
    [ObservableProperty] private bool      _contourDisplayGridPoints;
    [ObservableProperty] private SKColor   _contourGridPointColor   = SKColors.Black;
    [ObservableProperty] private SKColor   _contourLabelBackground  = SKColors.White;
    [ObservableProperty] private SKColor   _contourLabelForeground  = SKColors.Black;
    [ObservableProperty] private RbfKernel _contourInterpKernel     = ContourDefaults.Kernel;
    [ObservableProperty] private double    _contourSmoothing        = ContourDefaults.Smoothing;
    [ObservableProperty] private double?   _contourEpsilon          = ContourDefaults.Epsilon;

    // ---- New fields (7.4h-3) -----------------------------------------------
    [ObservableProperty] private double  _contourGridPointSize = 3.0;
    [ObservableProperty] private double  _contourLevelFontSize = 9.0;
    [ObservableProperty] private SKColor _contourLineColor     = new SKColor(255, 255, 255, 220);
    [ObservableProperty] private bool    _contourFadeLineOpacity;

    // ---- New fields (7.4h-4) -----------------------------------------------
    [ObservableProperty] private double _contourStrokeWidth = 1.5;

    public bool IsCompressionConstraint    => ContourConstraintKind == ConstraintKind.Compression;
    public bool IsConstantMetricConstraint => ContourConstraintKind == ConstraintKind.ConstantMetric;
    public bool IsRangeLevelMode           => ContourLevelMode == ContourLevelMode.Range;
    public bool IsCountLevelMode           => ContourLevelMode == ContourLevelMode.Count;
    public bool ShowContourFreqPicker      => IsContourTrace && AvailableFrequencies.Count > 1;
    /// <summary>Label for the contour slice-axis picker — "Freq" for a frequency-swept loadpull, or the
    /// swept-variable name (e.g. "RFfreq", "Vds") for a parametric-swept loadpull/pursuit.</summary>
    public string ContourLeadingAxisLabel
    {
        get
        {
            var n = _loadpullSurface?.LeadingAxisName;
            return string.IsNullOrEmpty(n) || string.Equals(n, "freq", System.StringComparison.OrdinalIgnoreCase)
                ? "Freq" : n;
        }
    }
    /// <summary>Show the loadpull-group picker only when the source carries more than one loadpull view
    /// (e.g. a run.npy with both a standalone Loadpull and a Loadpull-Pursuit follow-on).</summary>
    public bool ShowContourGroupPicker     => IsContourTrace && AvailableLoadpullGroups.Count > 1;

    /// <summary>The iso-line color actually rendered: the user's override, or the high-contrast color
    /// auto-derived from the colormap when not overridden. The trace card's color swatch binds to THIS
    /// (not the raw stored LineColor) so the indicator matches the plotted lines.</summary>
    public SKColor ContourLineColorEffective =>
        _trace.ContourData is { } cd
            ? ContourRenderer.ResolveBaseLineColor(cd.LineColor, cd.LineColorOverridden, cd.ColorMap)
            : ContourLineColor;

    // §16 — units label for the constraint value box.
    public string ConstraintUnits => ContourConstraintKind == ConstraintKind.Compression
        ? "dB"
        : ConstraintMetricUnit(ContourConstraintMetric);

    private static string ConstraintMetricUnit(string? metric) => metric switch
    {
        "Pout_dBm" or "Pout"           => "dBm",
        "Pout_W" or "Pdc_W"            => "W",
        "Gain" or "Gt" or "Gp" or "Gt_dB" or "Gp_dB" => "dB",
        "DE" or "PAE" or "Efficiency"  => "%",
        "AMPM_deg"                     => "deg",
        "IRL_dB"                       => "dB",
        _                              => "",
    };

    partial void OnContourMetricNameChanged(string? value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.MetricName = value ?? "";
        var (s, step, stop) = ContourDefaults.LevelRange(cd.MetricName);
        cd.LevelStart = s; cd.LevelStep = step; cd.LevelStop = stop;
        SyncContourVmFromData(cd);
        RebuildConstraintMetricOptions();  // §7: refresh enabled flags when primary metric changes
        RebuildContour();
    }

    partial void OnContourConstraintKindChanged(ConstraintKind value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ContourConstraintKind = value;
        OnPropertyChanged(nameof(IsCompressionConstraint));
        OnPropertyChanged(nameof(IsConstantMetricConstraint));
        OnPropertyChanged(nameof(ConstraintUnits));
        // §8: when switching to ConstantMetric ensure a valid metric is selected.
        if (value == ConstraintKind.ConstantMetric)
        {
            var first = ConstraintMetricOptions.FirstOrDefault(m => m.IsEnabled);
            if (first is not null &&
                (string.IsNullOrEmpty(cd.ConstraintMetricName) || cd.ConstraintMetricName == cd.MetricName))
            {
                _suppressContourCallback = true;
                ContourConstraintMetric = first.Name;
                SelectedConstraintMetricItem = first;
                cd.ConstraintMetricName = first.Name;
                _suppressContourCallback = false;
            }
        }
        RebuildContour();
    }

    partial void OnContourConstraintMetricChanged(string value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ConstraintMetricName = value;
        OnPropertyChanged(nameof(ConstraintUnits));
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
        // The RecommendedBox (MXP/MXE region) is per-frequency, so re-zoom to the new frequency's box —
        // a forced re-frame, exactly as AddContourTrace does on first add (the contour plot otherwise
        // keeps autoscale off for a sticky view, so a plain Autoscale() would no-op).
        _parent.ForceRescaleAndNotify();
    }

    partial void OnSelectedLoadpullGroupChanged(string? value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LoadpullGroup = value;
        _loadpullSurface = null;          // force rebuild against the newly-chosen loadpull analysis
        RebuildContourForCurrentSurface();
    }

    /// <summary>
    /// Drops the cached loadpull surface and rebuilds the contour from the CURRENT data source. Called
    /// after a re-run: the run.npy is overwritten at the same path, so the path-keyed surface cache (and
    /// the metric/frequency/analysis pickers built from it) would otherwise keep serving the PREVIOUS
    /// run's analyses. No-op for non-contour traces.
    /// </summary>
    internal void RefreshContourAfterReload()
    {
        if (!IsContourTrace || _trace.ContourData is null) return;
        _loadpullSurface   = null;
        _surfaceSourcePath = null;
        _surfaceGroup      = null;
        RebuildContourForCurrentSurface();
    }

    // Rebuild the contour against a freshly-resolved loadpull surface (after the analysis picker changed
    // or a re-run reloaded the data). Always shows data: keeps the metric if the new surface still offers
    // it, else falls back to the first available; resets the level set only when the metric had to change.
    // Picker ComboBox churn is muted (else its transient Clear/Add nulls the bound selection → empty plot),
    // then one rebuild + re-frame to the new RecommendedBox.
    private void RebuildContourForCurrentSurface()
    {
        if (_trace.ContourData is not { } cd) return;
        string prevMetric = cd.MetricName;
        int    prevFreq   = cd.FreqIndex;

        _suppressContourCallback = true;
        EnsureLoadpullSurface();   // rebuilds surface + metric/freq/analysis lists from the current data

        string metric = AvailableMetrics.Contains(prevMetric)
            ? prevMetric
            : (AvailableMetrics.FirstOrDefault() ?? prevMetric);
        bool metricChanged = !string.Equals(metric, prevMetric, StringComparison.Ordinal);

        ContourMetricName = metric;
        int freq = AvailableFrequencies.Count > 0
            ? Math.Clamp(prevFreq, 0, AvailableFrequencies.Count - 1) : 0;
        ContourFreqIndex  = freq;
        cd.FreqIndex      = freq;
        cd.MetricName     = metric;
        if (metricChanged)
        {
            var (s, step, stop) = ContourDefaults.LevelRange(metric);
            cd.LevelStart = s; cd.LevelStep = step; cd.LevelStop = stop;
            SyncContourVmFromData(cd);
            RebuildConstraintMetricOptions();
        }
        _suppressContourCallback = false;

        RebuildContour();
        _parent.ForceRescaleAndNotify();
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
        OnPropertyChanged(nameof(SelectedContourFill));
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
        OnPropertyChanged(nameof(SelectedContourFill));
        _parent.Notify();
    }

    partial void OnContourColorMapChanged(ContourColorMap value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.ColorMap = value;
        cd.LineColorOverridden = false;
        OnPropertyChanged(nameof(ContourLineColorEffective));   // swatch follows the new auto-derived color
        _parent.Notify();
    }

    partial void OnContourLabelSpacingChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LabelSpacing = value;
        _parent.Notify();
    }

    // ---- New display-toggle handlers (7.4h-1b) — no re-fit, just redraw ----

    partial void OnContourDisplayMxpChanged(bool value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.DisplayMxp = value;
        _parent.Notify();
    }

    partial void OnContourDisplayMxeChanged(bool value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.DisplayMxe = value;
        _parent.Notify();
    }

    partial void OnContourDisplayGridPointsChanged(bool value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.DisplayGridPoints = value;
        _parent.Notify();
    }

    partial void OnContourGridPointColorChanged(SKColor value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.GridPointColor = value;
        _parent.Notify();
    }

    partial void OnContourLabelBackgroundChanged(SKColor value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LabelBackground = value;
        _parent.Notify();
    }

    partial void OnContourLabelForegroundChanged(SKColor value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LabelForeground = value;
        _parent.Notify();
    }

    // ---- New display handlers (7.4h-3) — redraw only, no re-fit -----------

    partial void OnContourGridPointSizeChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.GridPointSize = value;
        _parent.Notify();
    }

    partial void OnContourLevelFontSizeChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LevelFontSize = value;
        _parent.Notify();
    }

    partial void OnContourLineColorChanged(SKColor value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.LineColor = value;
        cd.LineColorOverridden = true;
        OnPropertyChanged(nameof(ContourLineColorEffective));
        _parent.Notify();
    }

    partial void OnContourFadeLineOpacityChanged(bool value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.FadeLineOpacity = value;
        _parent.Notify();
    }

    partial void OnContourStrokeWidthChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.StrokeWidth = (float)value;
        _parent.Notify();
    }

    // ---- Engine-param handlers — re-fit on change --------------------------

    partial void OnContourInterpKernelChanged(RbfKernel value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.InterpKernel = value;
        RebuildContour();
    }

    partial void OnContourSmoothingChanged(double value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.Smoothing = value;
        RebuildContour();
    }

    partial void OnContourEpsilonChanged(double? value)
    {
        if (_suppressContourCallback || _trace.ContourData is not { } cd) return;
        cd.Epsilon = value;
        RebuildContour();
    }

    // ---- SelectedContourFill (VM-only; drives ShowFill + SelectedFillKind) --

    public ContourFillSelection SelectedContourFill
    {
        get => ContourShowFill
            ? (ContourSelectedFillKind == ContourFillKind.TopoMap
                ? ContourFillSelection.Topography
                : ContourFillSelection.Heatmap)
            : ContourFillSelection.None;
        set
        {
            ContourShowFill = value != ContourFillSelection.None;
            if (value == ContourFillSelection.Topography) ContourSelectedFillKind = ContourFillKind.TopoMap;
            else if (value == ContourFillSelection.Heatmap) ContourSelectedFillKind = ContourFillKind.HeatMap;
            OnPropertyChanged();
        }
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

        // brief-dd-z0-renormalization.md §5: Γ plane only — a Z-plane contour has no reference
        // impedance concept (the impedance grid does not move), so z0 stays null there and cannot
        // leak into that fit even if the trace's Z0 field holds a stale override from a prior
        // Smith/Polar view.
        System.Numerics.Complex? z0 = plane == SurfacePlane.Gamma ? _trace.Z0 : (System.Numerics.Complex?)null;

        var fit = surface.Fit(freqIdx, cd.MetricName, constraint, plane, z0,
            kernel: cd.InterpKernel, smooth: cd.Smoothing, epsilon: cd.Epsilon);
        if (fit is null) { ClearContourGrid(cd); return; }

        var grid    = surface.Resample(fit);
        // §1: for Smith/Polar compute a disk-covering fill grid over [-1,1]×[-1,1]
        // at higher resolution so the TopoMap fill reaches the circular-clip edge.
        var fillGrid = (plane == SurfacePlane.Gamma)
            ? surface.Resample(fit, new ViewBox(-1.0, 1.0, -1.0, 1.0), 80)
            : null;
        var scatter = surface.Reduce(freqIdx, cd.MetricName, constraint, plane, z0);

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

        cd.Grid       = grid;
        cd.FillGrid   = fillGrid;
        cd.Scatter    = scatter;
        cd.Levels     = levels;
        cd.GammaPlane = plane == SurfacePlane.Gamma;

        // Cache MXP / MXE for the renderer (surface stays out of renderer path).
        // MXP/MXE markers are the compression-based recommended terminations — independent of this
        // contour's metric/constraint (so they stay put when plotting e.g. Efficiency at Constant Pout).
        var (mxpR, mxeR) = surface.RecommendedMxx(fit);
        cd.MxpCoord = mxpR?.Measured;
        cd.MxeCoord = mxeR?.Measured;

        // Marker surface-evaluation hooks — capture locals so the closures are stable.
        var      evalSurface = surface;
        int      evalFreq    = freqIdx;
        string   evalMetric  = cd.MetricName;
        var      evalConstr  = constraint;
        var      evalPlane   = plane;
        var      evalZ0      = z0;
        RbfKernel evalKernel = cd.InterpKernel;
        double   evalSmooth  = cd.Smoothing;
        double?  evalEps     = cd.Epsilon;

        cd.EvaluateMetric = (coord, snapped) =>
            evalSurface.MetricAtCoord(evalFreq, evalMetric, coord, evalConstr, evalPlane, evalZ0,
                nearest: snapped, kernel: evalKernel, smooth: evalSmooth, epsilon: evalEps);

        var nodeCoords = scatter.Coords;
        cd.NearestNode = coord =>
        {
            if (nodeCoords is null || nodeCoords.Length == 0) return coord;
            int best = 0; double bestD2 = double.PositiveInfinity;
            for (int i = 0; i < nodeCoords.Length; i++)
            {
                double dx = nodeCoords[i].Real - coord.Real;
                double dy = nodeCoords[i].Imaginary - coord.Imaginary;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return nodeCoords[best];
        };

        _parent.Notify();
    }

    private static void ClearContourGrid(ContourData cd)
    {
        cd.Grid           = null;
        cd.FillGrid       = null;
        cd.Scatter        = null;
        cd.Levels         = new ContourLevelSet(Array.Empty<double>());
        cd.MxpCoord       = null;
        cd.MxeCoord       = null;
        cd.EvaluateMetric = null;
        cd.NearestNode    = null;
        cd.GammaPlane     = false;
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

        // Loadpull cubes may live at top level (flat .spl/.lpcwave) or under an analysis-name group
        // (simulated LP run.npy, e.g. "LP1"). Resolve the group shape-based (brief 08) and build the
        // surface from it. When the source carries more than one loadpull view (e.g. a standalone
        // Loadpull "LP1" + a Loadpull-Pursuit follow-on "LPP1"), honour the user's chosen group
        // (persisted on ContourData.LoadpullGroup); otherwise default to the first view. "" = top level.
        var views = LoadpullRecognition.FindLoadpullViews(ds);
        string? wanted = _trace.ContourData?.LoadpullGroup;
        string group =
            (!string.IsNullOrEmpty(wanted) && views.Any(v => (v.Group ?? "") == wanted)) ? wanted!
            : views.Count > 0 ? (views[0].Group ?? "") : "";

        if (_loadpullSurface is null
            || !string.Equals(entry.FilePath, _surfaceSourcePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(group, _surfaceGroup, StringComparison.Ordinal))
        {
            try
            {
                _loadpullSurface   = new LoadpullSurface(ds, group);
                _surfaceSourcePath = entry.FilePath;
                _surfaceGroup      = group;
            }
            catch
            {
                _loadpullSurface = null;
                return false;
            }
            RebuildMetricList();
            RebuildFrequencyList();
        }
        RebuildLoadpullGroupList(views, group);
        return _loadpullSurface.Frequencies.Count > 0;
    }

    // Keep the loadpull-group picker (AvailableLoadpullGroups + SelectedLoadpullGroup) in sync with the
    // recognized views and the group actually in use. Only mutates the collection when it changes, and
    // syncs SelectedLoadpullGroup under the suppress guard so it never re-triggers a rebuild.
    private void RebuildLoadpullGroupList(IReadOnlyList<LoadpullRecognition.LoadpullView> views, string activeGroup)
    {
        var groups = views.Select(v => v.Group ?? "").ToList();

        // Suppress the selection callback across the ENTIRE mutation: the live ComboBox sets its
        // SelectedItem to null while the ItemsSource is being Cleared, which would otherwise re-enter
        // OnSelectedLoadpullGroupChanged → rebuild → RebuildLoadpullGroupList mid-loop and double-add
        // every analysis. (Save/restore so an outer suppress is honored.)
        bool prevSuppress = _suppressContourCallback;
        _suppressContourCallback = true;
        try
        {
            if (!groups.SequenceEqual(AvailableLoadpullGroups, StringComparer.Ordinal))
            {
                AvailableLoadpullGroups.Clear();
                foreach (var g in groups) AvailableLoadpullGroups.Add(g);
                OnPropertyChanged(nameof(ShowContourGroupPicker));
            }
            SelectedLoadpullGroup = activeGroup;
        }
        finally { _suppressContourCallback = prevSuppress; }
    }

    // Simulation-only bookkeeping cubes that share the {gridPoint[,pinStep]} shape but are NOT
    // figures of merit — they must never appear in the contour metric picker. GammaLoad is the
    // termination coordinate (the Γ/Z plane itself), not a metric over it. (Measured .spl/.lpcwave
    // sources don't carry these, which is why the leak only showed on simulated LP runs.)
    private static readonly HashSet<string> NonMetricCubes = new(StringComparer.Ordinal)
    {
        "GammaLoad", "Converged", "IsTickle", "StopCode", "PavlDbm",
    };

    // Headline FOMs always offered as contour metrics even when flat — a user expects Pout / gain /
    // efficiency to be selectable regardless of dynamic range (e.g. DE/PAE are 0 with no bias-tee;
    // Pout may be near-constant). Other cubes (ZLoad, Pdc, Bias*, custom measurements) are still
    // gated by the §10 "must vary" rule so genuinely flat extras stay out of the way.
    private static readonly HashSet<string> KnownFomCubes = new(StringComparer.Ordinal)
    {
        // Engine core FOMs + post-processor derived FOMs (loadpull-postprocessor.md) — always offered.
        "Pout_dBm", "Pout_W", "Gt_dB", "Gp_dB", "Efficiency", "PAE", "Pdc_W",
        "Zin_real", "Zin_imag", "IRL_dB", "AMPM_deg",
    };

    private void RebuildMetricList()
    {
        AvailableMetrics.Clear();
        var entry = _parent.Library?.SelectedEntry;
        if (entry?.Data is not { } ds) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(string Key, int Priority)>();

        foreach (var group in ds.Groups)
            foreach (var kvp in ds.CubesIn(group))
            {
                string key = kvp.Key;
                if (NonMetricCubes.Contains(key) || key.StartsWith("__", StringComparison.Ordinal)) continue;
                if (!kvp.Value.Axes.Any(a => a.Name == "gridPoint")) continue;
                // A contour metric is a scalar field over {gridPoint[, pinStep]}; the interface spectra
                // (V/INl) carry node/harmonic axes and are not selectable metrics — exclude them.
                if (kvp.Value.Axes.Any(a => a.Name is "node" or "harmonic")) continue;
                if (!seen.Add(key)) continue;
                // §10 — skip fields that don't vary, EXCEPT the headline FOMs (always offered).
                if (!KnownFomCubes.Contains(key) && !DataCube.CubeVaries(kvp.Value)) continue;
                candidates.Add((key, MetricPriority(key)));
            }

        // §9 — priority sort, then alphabetical within the same priority bucket.
        candidates.Sort((a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var (key, _) in candidates)
            AvailableMetrics.Add(key);

        RebuildConstraintMetricOptions();  // §7: refresh disabled-items list
    }

    // §9 — alias-normalization + priority table.
    // Respects vendor labels in the displayed text; only controls sort order.
    private static int MetricPriority(string key)
    {
        string norm = key.ToLowerInvariant().Trim();
        if      (norm.EndsWith("_dbm", StringComparison.Ordinal)) norm = norm[..^4];
        else if (norm.EndsWith("_db",  StringComparison.Ordinal)) norm = norm[..^3];
        else if (norm.EndsWith("_deg", StringComparison.Ordinal)) norm = norm[..^4];

        // 1. Pout
        if (norm == "pout") return 1;
        // 2. Efficiency / Drain Efficiency
        if (norm is "efficiency" or "de" or "deff" or "eff" or "drainedf" or "draineff") return 2;
        // 3. Gain (Gt) — match exact stem "gt" not "gp"
        if (norm is "gt" or "gain") return 3;
        // 4. AM/PM
        if (norm is "ampm" or "trans_phase" or "transmission phase" or "transphase") return 4;
        // 5. (deferred — curvilinear angle; reserved slot, treated as low priority)
        // 6. PAE
        if (norm == "pae") return 6;
        // 7. Gp (Power Gain) — exact stem "gp" only
        if (norm is "gp") return 7;
        // 8. Zin_real
        if (norm is "zin_real") return 8;
        // 9. Zin_imag
        if (norm is "zin_imag") return 9;
        // 10. Everything else — alphabetical (use high priority number + string sort handles rest)
        return 100;
    }

    private void RebuildFrequencyList()
    {
        AvailableFrequencies.Clear();
        if (_loadpullSurface is null) return;
        // A frequency leading axis is shown in GHz; any other swept variable (e.g. Vds) in its own unit.
        bool   isFreq = IsFrequencyUnit(_loadpullSurface.LeadingAxisUnit);
        string unit   = _loadpullSurface.LeadingAxisUnit;
        foreach (var f in _loadpullSurface.Frequencies)
            AvailableFrequencies.Add(
                isFreq                       ? $"{f / 1e9:G4} GHz"
              : string.IsNullOrEmpty(unit)   ? $"{f:G4}"
                                             : $"{f:G4} {unit}");
        OnPropertyChanged(nameof(ShowContourFreqPicker));
        OnPropertyChanged(nameof(ContourLeadingAxisLabel));
    }

    private static bool IsFrequencyUnit(string? u) =>
        u is "Hz" or "kHz" or "MHz" or "GHz" or "THz";

    private void SyncContourVmFromData(ContourData cd)
    {
        _suppressContourCallback = true;
        ContourLevelStart = cd.LevelStart;
        ContourLevelStep  = cd.LevelStep;
        ContourLevelStop  = cd.LevelStop;
        _suppressContourCallback = false;
    }

    private static Rgba SkColorToRgba(SKColor c) => new Rgba(c.Red, c.Green, c.Blue, c.Alpha);
    private static SKColor RgbaToSkColor(Rgba r)  => new SKColor(r.R, r.G, r.B, r.A);

    private async Task<Rgba?> ShowColorPickerAsync(Rgba initial)
    {
        var owner = _parent.GetOwnerWindow?.Invoke();
        if (owner is null) return null;
        _parent.RaiseColorPickStarted();
        try
        {
            return await new ColorPickerDialog(initial).ShowDialog<Rgba?>(owner);
        }
        finally
        {
            _parent.RaiseColorPickEnded();
        }
    }

    private async Task PickGridPointColorAsync()
    {
        var result = await ShowColorPickerAsync(SkColorToRgba(ContourGridPointColor));
        if (result is { } rgba) ContourGridPointColor = RgbaToSkColor(rgba);
    }

    private async Task PickLabelBgColorAsync()
    {
        var result = await ShowColorPickerAsync(SkColorToRgba(ContourLabelBackground));
        if (result is { } rgba) ContourLabelBackground = RgbaToSkColor(rgba);
    }

    private async Task PickLabelFgColorAsync()
    {
        var result = await ShowColorPickerAsync(SkColorToRgba(ContourLabelForeground));
        if (result is { } rgba) ContourLabelForeground = RgbaToSkColor(rgba);
    }

    private async Task PickLineColorAsync()
    {
        // Start from the color actually shown (the auto-derived one when not overridden) so the picker
        // opens on what the user sees, not the unused stored default.
        var result = await ShowColorPickerAsync(SkColorToRgba(ContourLineColorEffective));
        if (result is { } rgba) ContourLineColor = RgbaToSkColor(rgba);
    }

    // ---- Summary column authoring (Phase 7.5d) ---------------------------------

    private bool _suppressSummaryCallback;

    public ObservableCollection<string> SummaryMetricOptions { get; } = new();

    [ObservableProperty] private string? _summaryMetricSelection;

    partial void OnSummaryMetricSelectionChanged(string? value)
    {
        if (_suppressSummaryCallback || _trace.SummaryColumn is not { } sc || value is null) return;
        ApplySummaryMetric(sc, value);
        _parent.RebuildSummary();
        OnPropertyChanged(nameof(SummaryUnitLabel));
    }

    private void ApplySummaryMetric(SummaryColumnData sc, string selection)
    {
        switch (selection)
        {
            case "Zload":   sc.Kind = SummaryColumnKind.Zload;          sc.MetricName = ""; break;
            case "Zsource": sc.Kind = SummaryColumnKind.Zsource;        sc.MetricName = ""; break;
            case "Zin":     sc.Kind = SummaryColumnKind.Zin;            sc.MetricName = ""; break;
            case "VDD":     sc.Kind = SummaryColumnKind.OperatingPoint; sc.MetricName = "BiasVLoad"; break;
            case "Idq":     sc.Kind = SummaryColumnKind.OperatingPoint; sc.MetricName = "BiasILoad"; break;
            default:        sc.Kind = SummaryColumnKind.Metric;         sc.MetricName = selection; break;
        }
        sc.Header = "";
    }

    private void RebuildSummaryMetricOptions()
    {
        SummaryMetricOptions.Clear();
        foreach (var m in new[] { "Pout_dBm", "Efficiency", "Gt_dB", "Gp_dB", "PAE", "AMPM_deg", "IRL_dB", "Pdc_W" })
            if (AvailableMetrics.Contains(m)) SummaryMetricOptions.Add(m);
        SummaryMetricOptions.Add("Zload");
        // Zin is offered when the Zin_real cube EXISTS in the dataset — not gated on AvailableMetrics,
        // which drops low-variance cubes (input impedance is often near-constant across the load grid,
        // so Zin_real would be filtered out and Zin would wrongly never appear). Presence is the right test.
        if (SummaryDataHasCube("Zin_real")) SummaryMetricOptions.Add("Zin");
        SummaryMetricOptions.Add("Zsource");
        SummaryMetricOptions.Add("VDD");
        SummaryMetricOptions.Add("Idq");
        _suppressSummaryCallback = true;
        SummaryMetricSelection = SummaryMetricForColumn(_trace.SummaryColumn);
        _suppressSummaryCallback = false;
    }

    /// <summary>True when the selected datasource contains a cube with the given canonical name
    /// (presence test, independent of whether it varies across the grid).</summary>
    private bool SummaryDataHasCube(string name)
    {
        var ds = _parent.Library?.SelectedEntry?.Data;
        if (ds is null) return false;
        // Group-aware: a simulated LP run nests cubes under an analysis group (e.g. "LP1"); a flat
        // .spl is top level. ds.Contains(bare) only resolves the default/measurements group.
        if (ds.Contains(name)) return true;
        foreach (var g in ds.Groups)
            if (ds.CubesIn(g).ContainsKey(name)) return true;
        return false;
    }

    private static string? SummaryMetricForColumn(SummaryColumnData? sc) => sc?.Kind switch
    {
        null                              => null,
        SummaryColumnKind.Zload           => "Zload",
        SummaryColumnKind.Zsource         => "Zsource",
        SummaryColumnKind.Zin             => "Zin",
        SummaryColumnKind.OperatingPoint  => sc.MetricName == "BiasILoad" ? "Idq" : "VDD",
        _                                 => sc.MetricName,
    };

    /// <summary>The table-wide compression shown (disabled) on a summary column card.</summary>
    public double SummaryCompressionDisplay => _parent.TableCompression;

    /// <summary>
    /// Display precision (decimal digits) for a summary column's cells. Bound by the summary card's
    /// Digits NUD. This must write SummaryColumnData.FractionDigits (what the cell formatter reads),
    /// NOT the trace's MaximumFractionDigits (which the standard-table number-format controls use).
    /// Editing it re-renders the table so the new precision shows immediately.
    /// </summary>
    public int SummaryFractionDigits
    {
        get => _trace.SummaryColumn?.FractionDigits ?? 1;
        set
        {
            var sc = _trace.SummaryColumn;
            if (sc is null || sc.FractionDigits == value) return;
            sc.FractionDigits = value;
            OnPropertyChanged();
            _parent.Notify();   // re-render: cells re-format at the new precision
        }
    }

    /// <summary>Always false — per-column compression box is greyed; compression is table-wide.</summary>
    public bool SummaryCompressionEditable => false;

    public string SummaryUnitLabel
    {
        get
        {
            var sc = _trace.SummaryColumn;
            if (sc is null) return "";
            return sc.Kind switch
            {
                SummaryColumnKind.Zload   => "Ω",
                SummaryColumnKind.Zsource => "Ω",
                SummaryColumnKind.Zin     => "Ω",
                SummaryColumnKind.OperatingPoint => string.IsNullOrEmpty(sc.UnitLabel)
                    ? (sc.MetricName == "BiasILoad" ? "mA" : "V")   // pre-RebuildSummary fallback
                    : sc.UnitLabel,                                  // magnitude-inferred (bug 5 option b)
                SummaryColumnKind.Metric  => sc.MetricName switch
                {
                    "Pout_dBm"        => "dBm",
                    "Pout_W" or "Pdc_W" => "W",
                    "Efficiency" or "PAE" => "%",
                    "AMPM_deg"        => "°",
                    _                 => "dB",
                },
                _ => "dB",
            };
        }
    }

    /// <summary>Raises the disabled-compression display so a summary card reflects the table-wide value.
    /// Also re-reads SummaryUnitLabel, which can change after RebuildSummary stamps a magnitude-inferred
    /// Idq/VDD unit (bug 5 option b).</summary>
    internal void RaiseSummaryCompressionChanged()
    {
        if (_trace.IsSummaryColumn)
        {
            OnPropertyChanged(nameof(SummaryCompressionDisplay));
            OnPropertyChanged(nameof(SummaryUnitLabel));
        }
    }

    // ---- Cube-bound discriminators (Phase 7.2c-a) --------------------------

    /// <summary>True when the trace is in cube-bound mode (not SNP/matrix).</summary>
    public bool IsCubeBoundTrace => _trace.IsCubeBound;

    /// <summary>YAxis combo is shown for network-bound Rect/Table; hidden for cube-bound.</summary>
    public bool ShowYAxisCombo => IsRectOrTablePlot && !IsCubeBoundTrace;

    /// <summary>
    /// MatrixType (S/Z/Y) combo — shown only for a trace that reads a network-parameter matrix
    /// ELEMENT (an S/Z/Y value at a fixed port pair). Never for a derived metric (µ, µ′, K, |Δ|,
    /// MaxGain, passivity, circles): the metric is defined on S only, and Trace.Derived's setter
    /// already force-pins MatrixType to S, so the selector could only lie there (§1).
    ///
    /// True for (a) a non-derived network (Touchstone/SNP) trace with non-empty Data, and (b) a
    /// cube-bound trace whose cube is the network-parameter cube of a group that carries S + Z0 —
    /// via <see cref="RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec"/>, the same authority §2's
    /// virtual Z/Y cubes are built from.
    /// </summary>
    public bool ShowMatrixTypeCombo
    {
        get
        {
            if (_trace.IsDerived) return false;
            if (!_trace.IsCubeBound) return _trace.Data is { } d && !d.IsEmpty;
            return _trace.CubeName is { } cubeName
                && ResolveTraceSourceEntry()?.Data is { } ds
                && RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, cubeName);
        }
    }

    // ---- Combined data picker (replaces SNP source + Row + Col) ----------
    //
    //  One item per (SNP × matrix-element) plus derived-parameter items for
    //  2-port SNPs.  Rebuilt when MatrixType or the library contents change.
    //  Selection revert is avoided by NOT calling RebuildSignals from
    //  RefreshDescription (which is called from RebuildAndNotify).

    // Full unfiltered signal set (rebuilt by RebuildSignals); AvailableSignals is the slice for SelectedGroup.
    private readonly List<TraceDataItem> _allSignals = new();

    // ---- Source selector (R-dd-2) — visible only once a second dataset is loaded --------
    //
    //  With one dataset, the picker is completely unaffected — RebuildSignals reads
    //  _parent.Library.SelectedEntry exactly as before (R-dd-1's structural guarantee).
    //  With 2+ datasets, this row's OWN choice of source (independent of the toolbar's
    //  globally-selected entry) governs which file's traces the group/item combos show,
    //  ending in an "Add from file…" sentinel so pulling in a new dataset and picking a
    //  trace from it is one gesture (R-dd-2).

    private DataSourceEntryViewModel? _pickerSourceEntry;
    private bool _suppressSourceCallback;

    // True only for the brief window of a DELIBERATE user source switch — between the combo pick
    // and CommitCurrentSelection re-binding the trace to it. Outside that window the trace's own
    // binding is authoritative (see RebuildSourceEntries).
    private bool _sourceSwitchInProgress;

    public ObservableCollection<PickerSourceItem> AvailableSourceEntries { get; } = new();

    [ObservableProperty]
    private PickerSourceItem? _selectedSourceItem;

    /// <summary>True once a second dataset exists — the one gate that keeps a single-dataset
    /// display's picker byte-identical to before this selector existed.</summary>
    public bool SourceSelectorVisible => _parent.LibraryEntries.Count > 1;

    partial void OnSelectedSourceItemChanged(PickerSourceItem? value)
    {
        if (_suppressSourceCallback || value is null) return;

        if (value.IsAddFromFile)
        {
            _ = HandleAddSourceFromFileAsync();
            return;
        }

        if (ReferenceEquals(value.Entry, _pickerSourceEntry)) return;
        _pickerSourceEntry = value.Entry;
        _sourceSwitchInProgress = true;
        try
        {
            RebuildSignals();
            CommitCurrentSelection();
        }
        finally { _sourceSwitchInProgress = false; }
    }

    private async Task HandleAddSourceFromFileAsync()
    {
        var lib = _parent.Library;
        var request = lib?.AddSourceFileRequested;
        string? path = request is null ? null : await request();

        bool added = false;
        if (!string.IsNullOrEmpty(path))
        {
            await lib!.LoadFileAsync(path);
            var loaded = _parent.LibraryEntries.FirstOrDefault(e =>
                string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (loaded is not null) { _pickerSourceEntry = loaded; added = true; }
        }

        // Re-sync the combo either way: a genuine add already ran RebuildSignals via the
        // LibraryEntries.CollectionChanged handler (using whatever the default source was at
        // that moment) — this second call re-points it at the just-added file; a cancel just
        // reverts the combo off the sentinel (and must NOT claim a switch is in flight, or the
        // stale _pickerSourceEntry would win over the trace's real binding on the way out).
        _sourceSwitchInProgress = added;
        try
        {
            RebuildSignals();
            CommitCurrentSelection();
        }
        finally { _sourceSwitchInProgress = false; }
    }

    /// <summary>
    /// Applies the row's own current <see cref="SelectedSignal"/> to the trace model directly —
    /// bypassing RebuildSignals' internal suppress guard (which normally protects a mere group/
    /// item browse from overwriting the model mid-rebuild). Used whenever switching the picker's
    /// Source deliberately picks a new file to browse (R-dd-2/R-dd-3): pulling in a new dataset
    /// and picking a trace from it must be one gesture, not "switch source, then separately also
    /// click the first item."
    /// </summary>
    private void CommitCurrentSelection()
    {
        var sig = SelectedSignal;
        if (sig is null) return;
        bool saved = _suppressDataCallback;
        _suppressDataCallback = false;
        OnSelectedSignalChanged(sig);
        _suppressDataCallback = saved;
    }

    /// <summary>
    /// Rebuilds AvailableSourceEntries and keeps SelectedSourceItem/_pickerSourceEntry pointed at a
    /// real entry.
    ///
    /// **The trace's OWN current binding is the truth this card must reflect** — a sentinel-bound
    /// trace (SourceRef == DataSourceRef.Selected, which every auto-created display's traces are)
    /// is silently re-pointed to a different file whenever the toolbar's datasource combo changes
    /// (DataDisplayViewModel.OnSelectedDataSourceChanged rewrites SourcePath), and the card must
    /// follow it. A prior version preferred the row's own sticky prior choice (_pickerSourceEntry)
    /// FIRST, so once it was set at row construction it never yielded: pick source B in the
    /// toolbar and the plot correctly showed B's data while the trace card kept claiming A — for
    /// both the Source combo AND the group/item cascade, since RebuildSignals enumerates whichever
    /// entry this same field names.
    ///
    /// The one exception is a deliberate user source switch, where _pickerSourceEntry leads for the
    /// duration: at that instant the trace has NOT yet been re-bound to the new file (that is
    /// exactly what CommitCurrentSelection does immediately afterward), so deferring to the trace
    /// there would snap the combo straight back and make the switch a no-op.
    /// </summary>
    private void RebuildSourceEntries()
    {
        AvailableSourceEntries.Clear();

        if (_parent.LibraryEntries.Count > 1)
        {
            foreach (var e in _parent.LibraryEntries)
                AvailableSourceEntries.Add(new PickerSourceItem(e));
            AvailableSourceEntries.Add(PickerSourceItem.AddFromFile);
        }

        OnPropertyChanged(nameof(SourceSelectorVisible));

        var realItems = AvailableSourceEntries.Where(i => !i.IsAddFromFile).ToList();
        if (realItems.Count == 0)
        {
            _pickerSourceEntry = null;
            _suppressSourceCallback = true;
            SelectedSourceItem = null;
            _suppressSourceCallback = false;
            return;
        }

        var wanted = _sourceSwitchInProgress
            ? (_pickerSourceEntry ?? ResolveTraceSourceEntry())
            : (ResolveTraceSourceEntry() ?? _pickerSourceEntry ?? _parent.Library?.SelectedEntry);
        var match  = realItems.FirstOrDefault(i => ReferenceEquals(i.Entry, wanted)) ?? realItems[0];

        _pickerSourceEntry = match.Entry;
        _suppressSourceCallback = true;
        SelectedSourceItem = match;
        _suppressSourceCallback = false;
    }

    /// <summary>
    /// Re-points the Source combo at whichever entry the trace is CURRENTLY bound to, without
    /// rebuilding the collection (no churn, no scroll/selection reset). Called from
    /// RefreshDescription so the combo tracks a toolbar-driven source change even along paths that
    /// only refresh trace cards (DataDisplayViewModel.OnSelectedDataSourceChanged →
    /// Inspector.RebuildAndNotify) and never call RebuildSignals — i.e. so the fix does not depend
    /// on this row's own SelectedDataSourceChanged handler happening to run after the one that
    /// rewrites SourcePath.
    /// </summary>
    private void SyncSourceSelectionToTrace()
    {
        if (_sourceSwitchInProgress) return;
        if (AvailableSourceEntries.Count == 0) return;
        if (ResolveTraceSourceEntry() is not { } bound) return;
        if (ReferenceEquals(bound, _pickerSourceEntry)) return;

        var match = AvailableSourceEntries.FirstOrDefault(i => ReferenceEquals(i.Entry, bound));
        if (match is null) return;

        _pickerSourceEntry = bound;
        _suppressSourceCallback = true;
        SelectedSourceItem = match;
        _suppressSourceCallback = false;
    }

    /// <summary>Best-effort: which loaded entry this trace is currently bound to, by source
    /// path (cube-bound / network) or by SNP identity (legacy network match).</summary>
    private DataSourceEntryViewModel? ResolveTraceSourceEntry()
    {
        if (_trace.SourcePath is not null)
        {
            var byPath = _parent.LibraryEntries.FirstOrDefault(e =>
                string.Equals(e.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null) return byPath;
        }
        if (_trace.Data is not null)
            return _parent.LibraryEntries.FirstOrDefault(e => e.Snp == _trace.Data);
        return null;
    }

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
            // EXCEPT for a network-parameter element item (§4): N² of them share one CubeName
            // ("SP1.S(1,1)".."SP1.S(4,4)" all bind CubeName=="SP1.S"), so "same CubeName" alone is
            // not "nothing changed" there — the i/j port pair must also match.
            alreadyApplied = _trace.IsCubeBound
                && string.Equals(_trace.SourcePath, value.Entry.FilePath, StringComparison.OrdinalIgnoreCase)
                && _trace.CubeName == value.CubeName
                && (!HasPortPair(value.Slice) || NetworkParamSliceMatches(value.Slice, _trace.Slice));
        }
        else
        {
            // NetworkView is Snp for a Touchstone source and the on-demand view of a simulated
            // run's S cube otherwise — the same instance the bind below assigns, so this stays a
            // true "nothing changed" test for both source kinds.
            var boundView = value.Entry.NetworkView;
            alreadyApplied = value.Derived != DerivedParameters.None
                ? (ReferenceEquals(_trace.Data, boundView) && _trace.Derived == value.Derived)
                : (ReferenceEquals(_trace.Data, boundView) && _trace.Row == value.Row
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

        // R-dd-2: a picked signal may come from ANY loaded entry now, not only the toolbar's
        // globally-selected one (the picker's own Source selector lets a row browse a specific
        // dataset independently). Stamp the "Selected" sentinel ONLY when the picked entry really
        // IS the toolbar's current selection — a trace deliberately bound to a SPECIFIC dataset
        // must persist a real ref to that dataset, or reloading the .cdd would silently reassign
        // it to whatever the toolbar happens to have selected at load time instead.
        bool pickedTheToolbarSelection = ReferenceEquals(value.Entry, _parent.Library?.SelectedEntry);
        _trace.SourceRef  = pickedTheToolbarSelection
            ? DataSourceRef.Selected
            : (value.Entry.FilePath is { } fp
                ? DataDisplayViewModel.ComputeSourceKey(fp, _parent.Library)
                : DataSourceRef.Selected);
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
                var carried = BuildCarriedSlice(value, oldSlice);
                // Network-parameter items (§4) all share one CubeName across N² port pairs —
                // BuildCarriedSlice would otherwise carry the OLD trace's i/j pin forward and
                // ignore the port pair the user actually picked. Every other axis (sweep
                // pin/Family, etc.) still carries over normally.
                if (value.Slice is not null
                    && value.Entry.Data is { } vds
                    && value.CubeName is not null
                    && RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(vds, value.CubeName))
                {
                    var pickedI = value.Slice.First(s => s.AxisName == "i");
                    var pickedJ = value.Slice.First(s => s.AxisName == "j");
                    for (int d = 0; d < carried.Length; d++)
                    {
                        if (carried[d].AxisName == "i") carried[d] = pickedI;
                        else if (carried[d].AxisName == "j") carried[d] = pickedJ;
                    }
                }
                _trace.Slice = carried;
            }
            // Re-apply the first-add nicety for the NEW signal: an auto-transform only for COMPLEX data
            // (so it shows a curve), None for REAL data (raw, no annoying "mag"). Matches the seed path.
            if (cubeForRank is not null)
                _trace.Transform = DefaultTransformFor(cubeForRank, _parent.PlotType, value.CubeName);
            _trace.InvalidSpecText = null;
            _trace.ExpressionError = null;
            _trace.Expression      = _trace.BuildPickerExpression();

            // Interim reset — an ordinary (non-network-param) cube has no per-port Z0 at all, and a
            // network-param cube trace (S/Z/Y element) gets these re-stamped from the group's own
            // Z0 cube by PlotInspectorViewModel.ResolveNetworkParamCube on the RebuildAndNotify()
            // below (brief-dd-z0-renormalization.md §1) — this reset just avoids showing a stale
            // network-trace value in between.
            _trace.SourceZ0PerPort   = null;
            _trace.SourceZ0IsUnusual = false;

            // A freshly-picked network-param cube trace (S/Z/Y element) seeds Z0 at the source's OWN
            // port-1 reference, matching the network-trace convention (ApplySourceZ0/SeedZ0FromSource)
            // — otherwise a stale default Z0=50 would silently renormalize a non-50Ω source the first
            // time it's plotted, before the user ever touched the Override checkbox.
            if (value.CubeName is not null && value.Entry.Data is { } dsForZ0
                && RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(dsForZ0, value.CubeName))
            {
                int dotZ0 = value.CubeName.LastIndexOf('.');
                string grp = dotZ0 < 0 ? "" : value.CubeName[..dotZ0];
                string z0Spec = grp.Length == 0
                    ? RfCore.Data.NetworkMetrics.Z0CubeName
                    : $"{grp}.{RfCore.Data.NetworkMetrics.Z0CubeName}";
                if (dsForZ0.Contains(z0Spec) && dsForZ0[z0Spec].ComplexValues is { Length: > 0 } z0Vals)
                {
                    _trace.Z0  = z0Vals[0];
                    _seedingZ0 = true;
                    Z0String   = ComplexStringHelper.Format(z0Vals[0]);
                    _seedingZ0 = false;
                }
                _applyingSource   = true;
                Z0OverrideEnabled = false;
                _applyingSource   = false;
            }
            RebuildAxisRoles();
        }
        else
        {
            // A cube marker's frequency lives in its POSITION, and the position is about to become
            // meaningless (the cube data is replaced below, and a derived trace re-places markers on
            // a Γ-plane locus). Read it out first, or every marker reports 0 Hz — and a 0 Hz lookup
            // against the new network reads NaN. Must run BEFORE the cube identity is cleared.
            _trace.CaptureMarkerFrequencies();

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
            _trace.Data = (value.Entry.NetworkView ?? value.Entry.Snp)!;

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

        // Committing a picked signal can change which SOURCE this trace is bound to (R-dd-2's own
        // Source selector, or simply picking an item from a different file when 2+ datasets are
        // loaded) — the plot's label strips must recompute (a plot that now spans 2 sources needs
        // alias-qualified labels; reverting to 1 must drop the qualifier again). RebuildAndNotify's
        // own PlotNeedsRedraw does not do this (it's the redraw-only path); PlotStructureChanged is
        // what PlotContainerViewModel.UpdateLabelStrips is wired to.
        _parent.NotifyStructureChanged();

        // Source kind may have flipped (cube ↔ network) — refresh every card-visibility discriminator
        // so the right fields show without reopening the inspector.
        OnPropertyChanged(nameof(IsCubeBoundTrace));
        OnPropertyChanged(nameof(ShowAllToggleVisible));
        RefreshDescription();   // raises ShowMatrixTypeCombo, ShowZ0Row/Control, ShowYAxisCombo, TraceTransformItems, Spec*, etc.
    }

    /// <summary>
    /// Refreshes the source-derived Z0 fields when the LIBRARY changed under a trace that is still
    /// pointed at the same signal — and <b>leaves the user's override alone</b>.
    ///
    /// <para><b>This is the workspace-reopen bug</b> (owner, 2026-08-18: <i>"the Z0 override is not
    /// respected when closing and reopening a workspace. I suspect the .cdd file is not persisting
    /// the override or its value."</i>). <b>The <c>.cdd</c> persists both correctly and always
    /// did.</b> What destroyed them was the restore ORDER: the config is applied first, then the data
    /// sources finish loading, then <see cref="RebuildSignals"/> runs — and it called
    /// <see cref="ApplySourceZ0"/>, which unconditionally clears the Override checkbox and reseeds the
    /// Z0 box from the source. The correct value was loaded and thrown away a moment later, which is
    /// exactly what makes it look like a persistence fault.</para>
    ///
    /// <para><b>Resetting is right on a SOURCE change and wrong on a library refresh</b>, and that is
    /// the whole distinction. Picking a different signal is the user saying "plot this other thing",
    /// and the new thing's own reference impedance is the honest starting point. A library refresh is
    /// the same signal re-read from disk; the user's override outlives it, the same way their axis
    /// limits and trace colour do. So the per-port fields and the Z0Kind are refreshed either way, and
    /// only the reseed is conditional.</para>
    /// </summary>
    private void RefreshSourceZ0PreservingOverride(DataSourceEntryViewModel entry)
    {
        StampSourceZ0OnTrace(_trace, entry);
        _sourceZ0Kind = entry.Z0Kind;

        // With an override in force the box shows the USER's number; reseeding would overwrite it
        // with the source's, which is the bug this method exists to not have.
        if (Z0OverrideEnabled) return;

        SeedZ0FromSource();
    }

    /// <summary>Populates SourceZ0PerPort / SourceZ0IsUnusual on the trace from the source entry,
    /// stashes the Z0Kind for per-kind UI gating, resets the Override checkbox, and seeds the
    /// displayed Z0 value from the source port-1 reference.
    ///
    /// <para><b>For an explicit SOURCE change only.</b> A library refresh that leaves the trace on the
    /// same signal must go through <see cref="RefreshSourceZ0PreservingOverride"/> instead — see its
    /// note for why.</para></summary>
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
        // Read the entry's ALREADY-classified vector rather than re-resolving "Z0" here — a second
        // lookup is a second chance to get the group qualification wrong, and did: a bare
        // ds.Contains("Z0") is false for a simulated source, whose Z0 lives at "SP1.Z0".
        if (entry.Z0PerPort is { Count: > 0 } z0PerPort)
        {
            trace.SourceZ0PerPort   = z0PerPort as System.Numerics.Complex[] ?? z0PerPort.ToArray();
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

    // ---- Network-metric card (brief-stability-passivity-touchstone.md §3) -----------------
    //
    //  Stability's 2-port formulas need to know WHICH 2-port. A 4-port holding two FETs has
    //  several candidate pairings and only the user knows which is meaningful — which is why an
    //  automatic "compute stability when the data is 2-port" rule was rejected in favour of an
    //  explicit, ordered selection (R-stb-3).

    /// <summary>True for a derived network-metric trace (μ, μ′, K, |Δ|, MaxGain, passivity, circles).</summary>
    public bool IsNetworkMetricTrace => _trace.IsDerived;

    /// <summary>Port count of this trace's source network; 0 when it has none.</summary>
    private int SourcePortCount => _trace.Data is { IsEmpty: false } d ? d.Ports : 0;

    /// <summary>1-based port numbers for the two selectors.</summary>
    public ObservableCollection<int> AvailablePorts { get; } = new();

    /// <summary>
    /// R-stb-3: two INDEPENDENT selectors, never an enumerated pair list — the pair count grows as
    /// N(N−1)/2 (28 for an 8-port, 190 for a 20-port), which does not scale; two selectors do, for
    /// any N. Hidden at exactly 2 ports, where input=1/output=2 is the only sensible choice.
    /// </summary>
    public bool ShowPortSelectors =>
        IsNetworkMetricTrace && _trace.Derived.NeedsPortPair() && SourcePortCount > 2;

    public int SelectedInputPort
    {
        get => _trace.InputPort;
        set
        {
            if (value == _trace.InputPort || value < 1) return;
            _trace.InputPort = value;
            // Ports must differ; nudge the other selector rather than refusing the pick.
            if (_trace.OutputPort == value)
            {
                _trace.OutputPort = FirstPortOtherThan(value);
                OnPropertyChanged(nameof(SelectedOutputPort));
            }
            OnPropertyChanged(nameof(SelectedInputPort));
            OnPropertyChanged(nameof(TerminationNote));
            _parent.RebuildAndNotify();
            _parent.NotifyStructureChanged();
        }
    }

    public int SelectedOutputPort
    {
        get => _trace.OutputPort;
        set
        {
            if (value == _trace.OutputPort || value < 1) return;
            _trace.OutputPort = value;
            if (_trace.InputPort == value)
            {
                _trace.InputPort = FirstPortOtherThan(value);
                OnPropertyChanged(nameof(SelectedInputPort));
            }
            OnPropertyChanged(nameof(SelectedOutputPort));
            OnPropertyChanged(nameof(TerminationNote));
            _parent.RebuildAndNotify();
            _parent.NotifyStructureChanged();
        }
    }

    private int FirstPortOtherThan(int p)
    {
        for (int i = 1; i <= Math.Max(2, SourcePortCount); i++) if (i != p) return i;
        return p == 1 ? 2 : 1;
    }

    /// <summary>Passivity only: whole network vs the extracted pair (R-stb-6).</summary>
    public bool PassivityWholeNetwork
    {
        get => _trace.PassivityWholeNetwork;
        set
        {
            if (value == _trace.PassivityWholeNetwork) return;
            _trace.PassivityWholeNetwork = value;
            OnPropertyChanged(nameof(PassivityWholeNetwork));
            OnPropertyChanged(nameof(ShowPassivityScope));
            OnPropertyChanged(nameof(TerminationNote));
            _parent.RebuildAndNotify();
        }
    }

    /// <summary>The whole-network/pair choice is offered only for passivity, and only above 2 ports.</summary>
    public bool ShowPassivityScope =>
        IsNetworkMetricTrace && _trace.Derived == DerivedParameters.Passivity && SourcePortCount > 2;

    /// <summary>
    /// R-stb-4: extracting a 2-port sub-matrix from an N-port is valid ONLY because the other ports
    /// are assumed terminated in the reference impedance. That is standard and correct, but someone
    /// comparing against a bench measurement where port 3 saw something else gets a mismatch with no
    /// explanation — one line of text is cheap insurance. Also carries R-stb-6's warning that a
    /// sub-matrix's passivity is not the device's passivity.
    /// </summary>
    public string? TerminationNote
    {
        get
        {
            if (!IsNetworkMetricTrace) return null;
            int n = SourcePortCount;

            if (_trace.Derived == DerivedParameters.Passivity)
                return PassivityWholeNetwork || n <= 2
                    ? null
                    : $"Passivity of the extracted {_trace.InputPort}–{_trace.OutputPort} 2-port, not of the "
                    + $"whole {n}-port: a sub-matrix can test passive while the full network is not. "
                    + "Remaining ports are assumed terminated in the reference impedance.";

            if (n > 2)
                return $"Computed from the {_trace.InputPort}→{_trace.OutputPort} 2-port of this {n}-port. "
                     + "The other ports are assumed terminated in the reference impedance — results will "
                     + "differ from a bench measurement that terminated them otherwise.";
            return null;
        }
    }

    public bool ShowTerminationNote => TerminationNote is not null;

    private void RebuildPortOptions()
    {
        int n = SourcePortCount;
        if (AvailablePorts.Count == n) return;      // no churn when unchanged
        AvailablePorts.Clear();
        for (int p = 1; p <= n; p++) AvailablePorts.Add(p);
    }

    /// <summary>Re-raises every network-metric card property; called from RefreshDescription.</summary>
    private void RefreshNetworkMetricCard()
    {
        RebuildPortOptions();
        OnPropertyChanged(nameof(IsNetworkMetricTrace));
        OnPropertyChanged(nameof(ShowPortSelectors));
        OnPropertyChanged(nameof(ShowPassivityScope));
        OnPropertyChanged(nameof(PassivityWholeNetwork));
        OnPropertyChanged(nameof(SelectedInputPort));
        OnPropertyChanged(nameof(SelectedOutputPort));
        OnPropertyChanged(nameof(TerminationNote));
        OnPropertyChanged(nameof(ShowTerminationNote));
    }

    // ---- Matrix type -------------------------------------------------------

    [ObservableProperty]
    private MatrixType _matrixType;

    partial void OnMatrixTypeChanged(MatrixType value)
    {
        _trace.MatrixType = value;

        if (_trace.CubeName is { } cubeName)
        {
            int dot      = cubeName.LastIndexOf('.');
            string bare  = dot < 0 ? cubeName : cubeName[(dot + 1)..];
            string group = dot < 0 ? "" : cubeName[..dot];
            if (bare is "S" or "Z" or "Y")
            {
                // Cube-bound network-parameter trace: MatrixType has no direct effect on cube
                // rendering (unlike the network/SNP path, where it's already honoured in
                // BuildMatrixPath/DataPoint) — rewrite CubeName's bare S/Z/Y name instead, keep
                // the slice, and re-derive Expression so the picker/spec text stay in sync.
                _trace.CubeName   = group.Length == 0 ? value.ToString() : $"{group}.{value}";
                _trace.Expression = _trace.BuildPickerExpression();
            }
        }

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

        if (_trace.CubeName is not null)
        {
            // Genuine picker cube trace — rebuild the bracket expression from the updated picker state.
            _trace.Transform  = value.Transform;
            _trace.Expression = _trace.BuildPickerExpression();
        }
        else
        {
            // No CubeName → a network/SNP trace OR a typed multi-cube expression. The transform maps to
            // YAxis (network rendering); the combo records the selection. NEVER call BuildPickerExpression
            // here — with no CubeName it returns the network description string (e.g. "dB(S(1,1))"), which
            // stored as Expression would falsely mark the trace cube-bound and break it.
            // Self-heal: a network trace must have no Expression to render via its S-matrix, so clear an
            // Expression that currently FAILS to parse (a stale network description) — but PRESERVE a valid
            // typed expression (no error).
            if (_trace.ExpressionError is not null)
                _trace.Expression = null;
            // Trace owns the CubeTransform -> YAxis mapping, because for Max Gain it is not the
            // generic one (dB10, not dB20) and it refuses the entries that say nothing about a real
            // positive power gain. A refused transform writes nothing.
            if (!_trace.SetDisplayTransform(value.Transform)) return;
        }
        _parent.RebuildAndNotify();
    }

    /// <summary>False for a real multi-cube expression result — the transform lives in the expression text,
    /// so the combo is disabled (and forced to None). Enabled for complex expressions, picker/bare-cube, and
    /// network traces.</summary>
    public bool IsTransformComboEnabled => !_trace.TransformIsInert;

    /// <summary>
    /// Returns the per-trace, per-plot-type transform list — single source of truth for which
    /// entries make sense to select (brief-dd-plot-type-integrity.md §4): None/Conj are disabled on
    /// Rect for complex data (they leave a value Rect can't plot); the scalar reductions are
    /// disabled on Smith/Polar (only complex-preserving entries make sense there); everything is
    /// enabled on Table (it renders complex and scalar cells alike); Conj is disabled for real data
    /// on any plot type. The pre-existing network-only exclusion (dB10/dB/Conj are cube-only) is
    /// unaffected — this is an additional filter, not a replacement.
    /// </summary>
    public IReadOnlyList<CubeTransformItem> TraceTransformItems
    {
        get
        {
            // Cached by (isCubeBound, plotType, isComplexData) so repeated reads within one refresh
            // cycle — the ItemsSource binding AND SyncTransformItem's own lookup — return the SAME
            // CubeTransformItem instances. Without this, each read allocates a fresh list/items, and
            // SelectedTransformItem's ReferenceEquals-based "did it actually change" check (and
            // Avalonia's own ComboBox SelectedItem matching against its ItemsSource) would never see
            // two reads as equal even when nothing changed.
            var key = (_trace.IsCubeBound, _parent.PlotType,
                       isComplexData: _trace.IsCubeBound ? _trace.CubeDataIsComplex : true,
                       _trace.Derived);
            if (_cachedTransformItems is null || _cachedTransformItemsKey != key)
            {
                _cachedTransformItems    = BuildTransformItems(key.IsCubeBound, key.PlotType, key.isComplexData, key.Derived);
                _cachedTransformItemsKey = key;
            }
            return _cachedTransformItems;
        }
    }

    private IReadOnlyList<CubeTransformItem>? _cachedTransformItems;
    private (bool IsCubeBound, PlotType PlotType, bool isComplexData, DerivedParameters Derived) _cachedTransformItemsKey;

    internal static IReadOnlyList<CubeTransformItem> BuildTransformItems(
        bool isCubeBound, PlotType plotType, bool isComplexData,
        DerivedParameters derived = DerivedParameters.None) =>
        Enum.GetValues<CubeTransform>()
            .Select(t => new CubeTransformItem(t, TransformEntryEnabled(t, isCubeBound, plotType, isComplexData, derived)))
            .ToList();

    private static bool TransformEntryEnabled(
        CubeTransform t, bool isCubeBound, PlotType plotType, bool isComplexData,
        DerivedParameters derived = DerivedParameters.None)
    {
        // Max Gain is a real, positive POWER ratio, so its list is its own: None and Mag (the
        // linear ratio) and dB10 (10*log10 of it). dB20/dB would misname the arithmetic — the
        // metric has been 10*log10 since 2026-08-29 — and Real/Imag/Phase/Conj describe a complex
        // value it never has. Everything else stays keyed out and disabled rather than hidden, so
        // the combo still reads as the same control on every trace.
        if (derived == DerivedParameters.MaxGain)
            return Array.IndexOf(Trace.MaxGainTransforms, t) >= 0;

        // Pre-existing rule, unaffected by plot type: dB10/dB/Conj are cube-only.
        if (!isCubeBound && t is CubeTransform.dB10 or CubeTransform.dB or CubeTransform.Conj)
            return false;

        if (plotType == PlotType.Table) return true;   // renders complex & scalar cells alike

        bool isComplexPassthrough = t is CubeTransform.None or CubeTransform.Conj;

        if (plotType.IsComplex())   // Smith/Polar: only complex-preserving entries make sense
            return isComplexPassthrough && (isComplexData || t != CubeTransform.Conj);

        // Rect. A CUBE trace with no scalar reduction can't render at all (Trace.RectValueInvalid) —
        // disable None/Conj. A NETWORK trace has no such failure mode (it falls back to magnitude,
        // pre-existing behaviour outside this brief's scope) — unaffected, only the exclusion above
        // and the real-data Conj rule below apply to it.
        if (isCubeBound && isComplexData) return !isComplexPassthrough;
        if (!isComplexData) return t != CubeTransform.Conj;   // real data: Conj is meaningless
        return true;
    }

    /// <summary>
    /// Syncs SelectedTransformItem to the trace's current YAxis/Transform without triggering
    /// the rebuild callback. Called from RefreshDescription so the unified combo stays in step
    /// after the source signal or plot type changes.
    /// </summary>
    private void SyncTransformItem()
    {
        // A real expression result is inert — pin the combo to None (it's also disabled in the view).
        if (_trace.TransformIsInert && _trace.Transform != CubeTransform.None)
            _trace.Transform = CubeTransform.None;

        var items = TraceTransformItems;
        var item  = items.FirstOrDefault(t => t.Transform == _trace.DisplayTransform);
        if (!ReferenceEquals(_selectedTransformItem, item))
        {
            _suppressTransformCallback = true;
            SelectedTransformItem = item;
            _suppressTransformCallback = false;
        }
    }

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
        if (!ComplexStringHelper.TryParse(value, out Complex z0))
        {
            Z0ErrorText = "Invalid Z0 — expected a real or complex value (e.g. \"75\" or \"50+10j\").";
            return;
        }
        // brief-dd-z0-renormalization.md §2: the power-wave renormalization divides by √Re(Z0), so a
        // non-positive real part must be refused with a clear message rather than left to produce NaNs
        // deep in RFNetwork.SToS.
        if (z0.Real <= 0.0)
        {
            Z0ErrorText = "Z0 must have a positive real part.";
            return;
        }
        Z0ErrorText = "";
        _trace.Z0 = z0;
        RebuildAfterZ0Change();
    }

    /// <summary>Validation message for the Z0 box; empty when the current text is valid. §2.</summary>
    [ObservableProperty]
    private string _z0ErrorText = "";

    public bool HasZ0Error => !string.IsNullOrEmpty(Z0ErrorText);

    partial void OnZ0ErrorTextChanged(string value) => OnPropertyChanged(nameof(HasZ0Error));

    // ---- Z0 badge (Phase 7.2e; extended to cube network-param traces by §2) --------

    /// <summary>True when this is an S-parameter trace (network OR cube-bound network-param) whose
    /// source has unusual (non-uniform or complex) Z0 — the provenance indicator that survives now
    /// that §2 no longer disables the Z0 control for an unusual source.</summary>
    public bool ShowZ0Badge =>
        (IsScatteringTrace
            || (IsCubeNetworkParamTrace && _trace.CubeName is { } cn && BareCubeNameOf(cn) == "S"))
        && (SelectedSignal?.Entry?.HasUnusualZ0 ?? false);

    private static string BareCubeNameOf(string cubeName)
    {
        int dot = cubeName.LastIndexOf('.');
        return dot < 0 ? cubeName : cubeName[(dot + 1)..];
    }

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

    // ---- Z0 control gating (Phase 7.2f-2; extended to cube traces by brief-dd-z0-renormalization.md §1) --

    /// <summary>True when the source uses genuinely non-uniform-across-ports normalization.
    /// UniformComplex is NOT non-uniform; only NonUniform triggers multi-port mode.</summary>
    /// <summary>_sourceZ0Kind is stamped only by the network path's ApplySourceZ0; a cube-bound
    /// network-param trace has no such stash, so derive non-uniformity directly from the per-port
    /// array PlotInspectorViewModel.ResolveNetworkParamCube stamps on the trace.</summary>
    private bool SourceZ0IsNonUniform => _trace.IsCubeBound
        ? _trace.SourceZ0PerPort is { Length: > 1 } perPort && perPort.Any(z => z != perPort[0])
        : _sourceZ0Kind == RfCore.Data.Z0Kind.NonUniform;

    /// <summary>True for a network-bound, non-derived S-matrix trace.</summary>
    public bool IsScatteringTrace =>
        !_trace.IsCubeBound
        && _trace.Derived == DerivedParameters.None
        && _trace.MatrixType == MatrixType.S;

    /// <summary>True for a cube-bound trace whose cube is the network-parameter cube (S/Z/Y element)
    /// of a group that carries S + Z0 — the same authority <see cref="ShowMatrixTypeCombo"/> uses.
    /// Unlike the network path this is NOT restricted to MatrixType.S: Z/Y are reference-independent
    /// (§1's "renormalize S first, then convert" invariant), so showing the same Z0 control on a Z/Y
    /// cube trace is correct — the numbers just don't move when it's edited.</summary>
    public bool IsCubeNetworkParamTrace =>
        _trace.IsCubeBound
        && _trace.CubeName is { } cubeName
        && ResolveTraceSourceEntry()?.Data is { } ds
        && RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, cubeName);

    /// <summary>Gates the entire Z0 row — S-param (network) traces and network-param cube traces.
    /// A contour trace's Z0 control is a SEPARATE row in its own card section, gated by
    /// <see cref="ShowContourZ0Control"/> — see that property's doc.</summary>
    public bool ShowZ0Row => IsScatteringTrace || IsCubeNetworkParamTrace;

    /// <summary>True when a Γ-plane loadpull contour's Z0 control should be shown
    /// (brief-dd-z0-renormalization.md §5). The Z plane has no reference-impedance concept — the
    /// impedance grid does not move — so this is false there even if the trace carries a stale Z0
    /// override from a prior Smith/Polar view.</summary>
    public bool ShowContourZ0Control =>
        _trace.IsContourTrace && (_parent.PlotType is PlotType.Smith or PlotType.Polar);

    /// <summary>
    /// True when the source has non-uniform-across-ports or complex port normalization. §2
    /// reconsidered the old "renorm disabled" reading of this flag — it no longer replaces the Z0
    /// box with a static label; it now only drives the "source was unusual" badge
    /// (<see cref="ShowZ0Badge"/>/<see cref="Z0BadgeTooltip"/>). Renormalizing a per-port/complex
    /// source to a uniform user Z0 is exactly what <c>RFNetwork.SToS</c>/<c>RenormalizeSCube</c>
    /// already do natively — there was no concrete correctness reason found to keep the old block.
    /// </summary>
    public bool IsMultiPortNormalization =>
        (!_trace.IsCubeBound || IsCubeNetworkParamTrace) && _trace.SourceZ0IsUnusual && SourceZ0IsNonUniform;

    /// <summary>True when the Z0 control (box + Override checkbox) should be shown — a scattering
    /// network trace or a network-param cube trace. No longer suppressed by
    /// <see cref="IsMultiPortNormalization"/> — see that property's doc for why.</summary>
    public bool ShowZ0Control => IsScatteringTrace || IsCubeNetworkParamTrace;

    /// <summary>Override checkbox bound in the trace card. When unchecked, the Z0 box reverts to
    /// the source port-1 reference and editing is disabled.</summary>
    [ObservableProperty]
    private bool _z0OverrideEnabled;

    // Suppresses OnZ0OverrideEnabledChanged rebuild while ApplySourceZ0 is resetting the field.
    private bool _applyingSource;

    partial void OnZ0OverrideEnabledChanged(bool value)
    {
        // The model flag is the single gate on every renormalization path (Trace.Z0OverrideEnabled)
        // — it must track the checkbox even when the change is a source-swap reset.
        _trace.Z0OverrideEnabled = value;
        if (_applyingSource) return;
        if (!value) SeedZ0FromSource();
        // Both directions change what is rendered: turning Override OFF drops any renormalization
        // and returns the source's own data; turning it ON renormalizes every port to the box's Z0
        // (which for a non-uniform source moves the curve even before the box is edited).
        RebuildAfterZ0Change();
        OnPropertyChanged(nameof(IsZ0Editable));
    }

    /// <summary>Seeds _trace.Z0 and Z0String from the source's port-1 reference impedance. A contour
    /// trace has no "source" port-1 — it seeds to the assumed 50 Ω the Γ grid is itself referenced to
    /// (RfCore.Loadpull.LoadpullSurface.AssumedSourceZ0). Does not trigger a rebuild — callers handle
    /// that (RebuildAfterZ0Change).</summary>
    private void SeedZ0FromSource()
    {
        var sourcePort1Z0 = _trace.IsContourTrace
            ? new Complex(50, 0)
            : (_trace.SourceZ0PerPort is { Length: > 0 } arr) ? arr[0] : _trace.Data.Z0;
        _trace.Z0 = sourcePort1Z0;
        _seedingZ0 = true;
        Z0String = ComplexStringHelper.Format(sourcePort1Z0);
        _seedingZ0 = false;
    }

    /// <summary>A contour trace's Γ-grid is not rebuilt by the ordinary RebuildAndNotify/BuildPath
    /// sweep (Trace.BuildPath falls through to the network path for a contour trace — contour
    /// rendering is deliberately driven only by explicit RebuildContour calls, same as every other
    /// OnContourXxxChanged handler in this file). Route the Z0 box's rebuild the same way.</summary>
    private void RebuildAfterZ0Change()
    {
        if (_trace.IsContourTrace) RebuildContour();
        else _parent.RebuildAndNotify();
    }

    /// <summary>Z0 box is editable only when its control is shown and the Override checkbox is on.</summary>
    public bool IsZ0Editable => (ShowZ0Control || ShowContourZ0Control) && Z0OverrideEnabled;

    /// <summary>Tooltip shown on the disabled Z0 box (legacy; kept for existing tests).</summary>
    public string Z0DisabledReason => _trace.SourceZ0IsUnusual
        ? "Source has non-uniform/complex port normalization — check Override to renormalize to a uniform Z0."
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

    public IRelayCommand ToggleMxpCommand            { get; private set; } = null!;
    public IRelayCommand ToggleMxeCommand            { get; private set; } = null!;
    public IRelayCommand ToggleDisplayGridPtsCommand { get; private set; } = null!;

    public IAsyncRelayCommand PickGridPointColorCommand { get; private set; } = null!;
    public IAsyncRelayCommand PickLabelBgColorCommand   { get; private set; } = null!;
    public IAsyncRelayCommand PickLabelFgColorCommand   { get; private set; } = null!;
    public IAsyncRelayCommand PickLineColorCommand       { get; private set; } = null!;
    public IRelayCommand      ToggleFadeLineOpacityCommand { get; private set; } = null!;

    public bool IsTopoMapFill => ContourSelectedFillKind == ContourFillKind.TopoMap;
    public bool IsHeatMapFill => ContourSelectedFillKind == ContourFillKind.HeatMap;

    public static IReadOnlyList<ContourColorMap>      AllContourColorMaps { get; } =
        Enum.GetValues<ContourColorMap>().ToList();

    public static IReadOnlyList<RbfKernel>            AllRbfKernels { get; } =
        Enum.GetValues<RbfKernel>().ToList();

    // brief-dd-loadpull-contour-ux-round8 §3: Heatmap is experimental and withheld from the UI —
    // the picker offers only None/Topography. ContourFillSelection.Heatmap, ContourFillKind.HeatMap,
    // ContourFillType.HeatMap, ContourData.Scatter, and the renderer's heatmap branch stay intact so
    // a saved .cdd with Heatmap selected still loads and renders, and the experiment can be
    // re-enabled by restoring this list to Enum.GetValues<ContourFillSelection>().
    public static IReadOnlyList<ContourFillSelection> ContourFillOptions { get; } =
        new[] { ContourFillSelection.None, ContourFillSelection.Topography };

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
        // Mirror the model's override state into the checkbox (a trace restored from .cdd, or a
        // copied trace, may already have it on) — via the backing field, so the change handler's
        // reseed/rebuild does not fire while the row is still being constructed.
        _z0OverrideEnabled = trace.Z0OverrideEnabled;

        // Marker type via icon wrapper
        _selectedMarkerTypeItem = PlotInspectorViewModel.AllMarkerTypes
            .FirstOrDefault(m => m.Value == trace.Properties.MarkerType);

        // Cube transform item (Phase 7.2c-a)
        _selectedCubeTransformItem = PlotInspectorViewModel.AllCubeTransforms
            .FirstOrDefault(t => t.Transform == trace.Transform);

        // Unified transform item — cube or mapped-from-YAxis, from the per-plot-type list (§4).
        // TraceTransformItems needs _trace/_parent, both already assigned above.
        _selectedTransformItem = TraceTransformItems
            .FirstOrDefault(t => t.Transform == trace.DisplayTransform);

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

        ToggleMxpCommand            = new RelayCommand(() => ContourDisplayMxp        = !ContourDisplayMxp);
        ToggleMxeCommand            = new RelayCommand(() => ContourDisplayMxe        = !ContourDisplayMxe);
        ToggleDisplayGridPtsCommand = new RelayCommand(() => ContourDisplayGridPoints = !ContourDisplayGridPoints);

        PickGridPointColorCommand    = new AsyncRelayCommand(PickGridPointColorAsync);
        PickLabelBgColorCommand      = new AsyncRelayCommand(PickLabelBgColorAsync);
        PickLabelFgColorCommand      = new AsyncRelayCommand(PickLabelFgColorAsync);
        PickLineColorCommand         = new AsyncRelayCommand(PickLineColorAsync);
        ToggleFadeLineOpacityCommand = new RelayCommand(() => ContourFadeLineOpacity = !ContourFadeLineOpacity);

        // Initialize summary column fields (if this is a summary trace).
        if (trace.IsSummaryColumn)
        {
            if (EnsureLoadpullSurface())
            {
                RebuildMetricList();
                RebuildSummaryMetricOptions();
            }
        }

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
            _contourDisplayMxp        = cd.DisplayMxp;
            _contourDisplayMxe        = cd.DisplayMxe;
            _contourDisplayGridPoints = cd.DisplayGridPoints;
            _contourGridPointColor    = cd.GridPointColor;
            _contourLabelBackground   = cd.LabelBackground;
            _contourLabelForeground   = cd.LabelForeground;
            _contourInterpKernel      = cd.InterpKernel;
            _contourSmoothing         = cd.Smoothing;
            _contourEpsilon           = cd.Epsilon;
            _contourGridPointSize     = cd.GridPointSize;
            _contourLevelFontSize     = cd.LevelFontSize;
            _contourLineColor         = cd.LineColor;
            _contourFadeLineOpacity   = cd.FadeLineOpacity;
            _contourStrokeWidth       = cd.StrokeWidth;
            // Build surface and populate metric/frequency lists; trigger initial fit.
            if (EnsureLoadpullSurface())
            {
                // A brand-new contour (or one whose saved metric is absent — e.g. after a cube rename)
                // defaults to the first available metric (priority order → Pout_dBm) so the +Contour
                // button immediately renders something. AddContourTrace then autoscales (Rect).
                if (string.IsNullOrEmpty(cd.MetricName) || !AvailableMetrics.Contains(cd.MetricName))
                {
                    string firstMetric = AvailableMetrics.FirstOrDefault() ?? "";
                    if (firstMetric.Length > 0)
                    {
                        cd.MetricName      = firstMetric;
                        _contourMetricName = firstMetric;
                        var (s, step, stop) = ContourDefaults.LevelRange(firstMetric);
                        cd.LevelStart = s; cd.LevelStep = step; cd.LevelStop = stop;
                        _contourLevelStart = s; _contourLevelStep = step; _contourLevelStop = stop;
                    }
                }
                RebuildContour();
            }
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

        // AvailablePorts (and the other network-metric card properties) are otherwise populated
        // only by RefreshDescription -> RefreshNetworkMetricCard, which the constructor never
        // calls — so a freshly-built card rendered the In/Out row with two empty combos until the
        // user re-picked the signal on the live VM. Populate it up front instead.
        RefreshNetworkMetricCard();

        // The vs-X row has exactly the same problem, and for the same reason: its state is synced by
        // RefreshDescription, which the constructor does not call. A trace that ALREADY plots versus
        // — pasted, undone/redone, or restored from a .cdd — therefore came up with the checkbox
        // clear and an empty picker, while the trace itself went on plotting against its X quantity.
        SyncVersusFromTrace();

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

        // R-dd-2: with one dataset loaded, the picker browses the toolbar's globally-selected
        // entry exactly as before (structurally guarantees R-dd-1's single-dataset case is
        // untouched). With 2+ datasets, it browses whichever source THIS row's own selector
        // points at.
        bool multiSource = _parent.LibraryEntries.Count > 1;
        RebuildSourceEntries();
        var selectedEntry = multiSource ? _pickerSourceEntry : _parent.Library?.SelectedEntry;
        bool singleSource = !multiSource;

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

            // R-stb-3b/R-stb-9: every N ≥ 2 offers the full 2-port metric set — the ordered port
            // selectors on the card decide WHICH 2-port, so nothing here is specific to N = 2.
            // Passivity is offered from N ≥ 1, since it is not a 2-port formula (R-stb-6).
            if (ports >= 2)
            {
                foreach (var d in new[]
                {
                    DerivedParameters.SourceStabilityCircle,
                    DerivedParameters.LoadStabilityCircle,
                    DerivedParameters.MuPrime,
                    DerivedParameters.Mu,
                    DerivedParameters.MaxGain,
                    DerivedParameters.K,
                    DerivedParameters.DeltaMag,
                    DerivedParameters.GroupDelay,
                })
                    _allSignals.Add(new TraceDataItem(entry, d, _parent.PlotType, omitFilePrefix: true) { Group = netGroup });
            }
            if (ports >= 1)
                _allSignals.Add(new TraceDataItem(entry, DerivedParameters.Passivity, _parent.PlotType, omitFilePrefix: true) { Group = netGroup });
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
                    if (bareName == "Z0" || bareName == "ToneFreqs" || bareName == "MetaMixOrder" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
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

                    // Network-parameter matrix cube (S/Z/Y in a NAMED group carrying S+Z0): never a
                    // bare quantity item — one item per ordered (i,j) port pair instead (§4), and
                    // only for the currently selected matrix type (see AddNetworkParamElementItems).
                    if (group != DataSet.DefaultGroup && bareName is "S" or "Z" or "Y"
                        && RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, $"{group}.{bareName}"))
                    {
                        if (bareName == MatrixType.ToString())
                            AddNetworkParamElementItems(entry, group, bareName, cube, cubeGroup, isComplexPlot);
                        continue;
                    }

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

        // ---- Network metrics for a SIMULATED S-parameter run (R-stb-1) ---------------------
        //
        // A grouped run's S cube has no SNP by design (it goes through the cube path above, which
        // can carry a swept axis an SNP cannot). The 2-port metric formulae need only matrices, so
        // they are offered here against the entry's narrow NetworkView, in the analysis group's own
        // section beside its S cube. Gated on `entry.Snp is null` so a Touchstone source keeps
        // offering these exactly once, from the network block above.
        foreach (var entry in selectedEntry is null
            ? System.Linq.Enumerable.Empty<DataSourceEntryViewModel>()
            : new[] { selectedEntry })
        {
            if (entry.Snp is not null) continue;                  // Touchstone — already offered
            if (entry.Data is not { } mds) continue;
            if (entry.NetworkView is not { IsEmpty: false } view) continue;
            if (RfCore.Data.NetworkMetrics.FindSCubeSpec(mds) is not { } sSpec) continue;

            int dotAt = sSpec.IndexOf('.');
            string sGroup = dotAt < 0 ? DataSet.DefaultGroup : sSpec[..dotAt];
            string metricGroup = (singleSource ? "" : $"{System.IO.Path.GetFileNameWithoutExtension(entry.DisplayName)}..")
                               + (sGroup == DataSet.DefaultGroup ? "Signals" : sGroup);

            int mPorts = view.Ports;
            if (mPorts >= 2)
            {
                foreach (var d in new[]
                {
                    DerivedParameters.SourceStabilityCircle,
                    DerivedParameters.LoadStabilityCircle,
                    DerivedParameters.MuPrime,
                    DerivedParameters.Mu,
                    DerivedParameters.MaxGain,
                    DerivedParameters.K,
                    DerivedParameters.DeltaMag,
                    DerivedParameters.GroupDelay,
                })
                    _allSignals.Add(new TraceDataItem(entry, d, _parent.PlotType, omitFilePrefix: true)
                                    { Group = metricGroup });
            }
            if (mPorts >= 1)
                _allSignals.Add(new TraceDataItem(entry, DerivedParameters.Passivity, _parent.PlotType, omitFilePrefix: true)
                                { Group = metricGroup });
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
            // Match by source + cube; slice is normally managed via the axis-role editor and
            // ignored here. But a network-parameter cube (§4) now offers N² items that all share
            // the SAME CubeName ("SP1.S(1,1)".."SP1.S(4,4)" all bind CubeName=="SP1.S") — those
            // must disambiguate on the i/j port pair, or every S(i,j)/typed spec would always
            // resolve to the first item (S(1,1)).
            var candidates = _allSignals.Where(s =>
                s.IsCubeBound
                && string.Equals(s.Entry.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase)
                && s.CubeName == _trace.CubeName).ToList();

            match = candidates.Count <= 1
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(s => NetworkParamSliceMatches(s.Slice, _trace.Slice))
                  ?? candidates.FirstOrDefault();
        }
        else if (_trace.Data != null)
        {
            // The trace's Data is whichever view the bind in OnSelectedSignalChanged handed it:
            // `Entry.NetworkView ?? Entry.Snp`. So finding its entry again has to ask about BOTH.
            // Matching on Snp alone found nothing for a SIMULATED source — which has no Snp by
            // design — and the fallback below then re-pointed the card at the first signal in the
            // group. That is the "my Max Gain trace turns into S(1,1) when I press Run" report: the
            // trace was no longer being deleted, but its card silently re-selected S(1,1) on the
            // rebuild that every re-run triggers.
            var matchEntry = _parent.LibraryEntries.FirstOrDefault(
                e => ReferenceEquals(e.Snp, _trace.Data) || ReferenceEquals(e.NetworkView, _trace.Data));
            // Same "?? " order as the bind, so this is the very object the trace holds. (For a
            // Touchstone entry NetworkView IS Snp — including a BROKEN one, so the IsEmpty branch
            // below still fires for a missing file exactly as before.)
            var matchView = matchEntry?.NetworkView ?? matchEntry?.Snp;
            if (matchEntry != null && matchView != null)
            {
                if (matchView.IsEmpty
                    || (_trace.Derived == DerivedParameters.None
                        && (_trace.Row >= matchView.Ports
                            || _trace.Col >= matchView.Ports)))
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

        // Keep per-port Z0 fields fresh when the library changes in place (e.g. auto-refresh) —
        // WITHOUT clearing an override the user set. See RefreshSourceZ0PreservingOverride.
        //
        // A cube-bound NETWORK-PARAM trace (S/Z/Y element) is RE-STAMPED from its own group's Z0
        // cube, never cleared. This method runs AFTER PlotInspectorViewModel.TrySetCubeData on both
        // the .cdd load path (the row VM's constructor calls it) and the post-run library refresh
        // (OnLibraryChanged stamps, then calls RefreshDataSources), so clearing here threw away the
        // only faithful record of the run's port references and left every reflection readout
        // against the trace's default 50 Ω — a Term at 5+j100 into 5−j100 plotted at the Smith
        // centre and reported "impedance=50+j0 Ω". Anything else (an ordinary cube, or no match at
        // all) genuinely has no per-port Z0 and is still cleared.
        if (match is not null && !match.IsCubeBound)
        {
            RefreshSourceZ0PreservingOverride(match.Entry);
        }
        else if (match?.Entry.Data is { } matchDs && match.CubeName is { } matchCube
                 && PlotInspectorViewModel.StampSourceZ0FromCube(matchDs, _trace, matchCube))
        {
            _sourceZ0Kind = null;   // network-path stash only; the cube path reads SourceZ0PerPort
            // With Override off, Trace.Z0 is documented as a read-only MIRROR of the source's port-1
            // reference — so it has to follow the array we just stamped. A .cdd persists whatever Z0
            // the box last held (50 for any display authored before the port was complex), and
            // nothing on the load path re-seeded it, so the card showed 50 Ω for a 5+j100 port and
            // ticking Override would then renormalize to a value the user never typed.
            if (!Z0OverrideEnabled) SeedZ0FromSource();
        }
        else
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

        // A network-parameter matrix element (S/Z/Y): the port indices i/j are never an X axis
        // and are picked via the item combo (S(1,1), S(1,2), …), not the axis-role editor — so
        // suppress their rows here (§4). Every other axis (a parametric sweep, freq) stays.
        bool isNetworkParamCube = RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, _trace.CubeName);

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

            if (isNetworkParamCube && (axis.Name == "i" || axis.Name == "j")) continue;

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

            // The axis-role row label shows the *display* unit: a frequency axis carries the
            // base SI unit ("Hz") but its values are rendered in the plot's FreqUnit, so the
            // label must match (e.g. "RFfreq (GHz)", not "RFfreq (Hz)").
            string? displayUnit = axisIsFreq ? plotFreqUnit.Description() : axis.Unit;

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

                AxisRoles.Add(new AxisRoleRowViewModel(this, axis.Name, displayUnit,
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
                AxisRoles.Add(new AxisRoleRowViewModel(this, axis.Name, displayUnit, opts, isX, pinIdx,
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
        // A "plot versus" X side mirrors these roles, so it has to be re-derived from the NEW slice
        // before the expression text is composed — otherwise an explicit X spec keeps the roles it was
        // written with and the two halves disagree about which axis is the family.
        RegenerateXSpecForYRoleChange();
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

    /// <summary>
    /// Adds one picker item per ordered (i,j) port pair — S(1,1), S(1,2), S(2,1), … in row-major
    /// order — instead of a single bare quantity item, for a network-parameter matrix cube (§4).
    /// The label prefix is <paramref name="bareName"/> (the CURRENTLY selected matrix type — the
    /// caller only invokes this for that one), so flipping the S/Z/Y selector relabels the same
    /// N² items rather than tripling the list.
    /// </summary>
    private void AddNetworkParamElementItems(DataSourceEntryViewModel entry, string group,
        string bareName, RfCore.Data.DataCube cube, string cubeGroup, bool isComplexPlot)
    {
        int iDim = -1, jDim = -1;
        for (int d = 0; d < cube.Rank; d++)
        {
            if (cube.Axes[d].Name == "i") iDim = d;
            else if (cube.Axes[d].Name == "j") jDim = d;
        }
        if (iDim < 0 || jDim < 0) return;   // malformed — refuse rather than guess

        int nPorts = cube.Axes[iDim].Length;
        string qualified = $"{group}.{bareName}";
        bool isEnabled = !isComplexPlot || cube.DataKind == DataKind.Complex;
        AxisSlice[] baseSlice = BuildDefaultSlice(cube);

        for (int i = 0; i < nPorts; i++)
        for (int j = 0; j < nPorts; j++)
        {
            var slice = (AxisSlice[])baseSlice.Clone();
            slice[iDim] = new AxisSlice("i", AxisRole.PinToIndex, i);
            slice[jDim] = new AxisSlice("j", AxisRole.PinToIndex, j);

            string label = $"{bareName}({i + 1},{j + 1})";
            _allSignals.Add(new TraceDataItem(entry, qualified, slice, label, isEnabled)
                            { Group = cubeGroup });
        }
    }

    /// <summary>True when both slices pin the SAME "i" and "j" axis index — the disambiguator for
    /// network-parameter picker items, which all share one CubeName across N² port pairs.</summary>
    private static bool NetworkParamSliceMatches(AxisSlice[]? itemSlice, AxisSlice[]? traceSlice)
    {
        if (itemSlice is null || traceSlice is null) return false;
        int? itemI = null, itemJ = null, traceI = null, traceJ = null;
        foreach (var s in itemSlice)
        {
            if (s.AxisName == "i") itemI = s.Index;
            else if (s.AxisName == "j") itemJ = s.Index;
        }
        foreach (var s in traceSlice)
        {
            if (s.AxisName == "i") traceI = s.Index;
            else if (s.AxisName == "j") traceJ = s.Index;
        }
        return itemI is not null && itemI == traceI && itemJ is not null && itemJ == traceJ;
    }

    /// <summary>True when a slice pins both an "i" and a "j" axis — i.e. it identifies a network-
    /// parameter element item, one of the N² sharing a single CubeName (§4).</summary>
    private static bool HasPortPair(AxisSlice[]? slice) =>
        slice is not null && slice.Any(s => s.AxisName == "i") && slice.Any(s => s.AxisName == "j");

    /// <summary>True for an S/Y/Z parameter cube (axes "freq", "i", "j") — used to pick the dB20
    /// first-add transform on Rect.</summary>
    internal static bool IsParameterCube(RfCore.Data.DataCube cube)
        => cube.Axes.Any(a => a.Name == "freq")
        && cube.Axes.Any(a => a.Name == "i")
        && cube.Axes.Any(a => a.Name == "j");

    /// <summary>The auto-applied "first-add nicety" transform for a cube on a given plot type. Only
    /// COMPLEX data on a Rect plot gets a transform so the user sees a curve instead of
    /// <c>&lt;invalid&gt;</c>; REAL data is shown raw (None) — no annoying "mag".
    ///
    /// <para>S gets dB20 (the conventional S-parameter view); Z and Y prefer Mag — dB20 of an
    /// impedance/admittance is defensible but odd. S/Z/Y share the exact same axis shape (freq, i,
    /// j — <see cref="IsParameterCube"/> cannot tell them apart), so the distinction is by cube
    /// NAME; <paramref name="cubeName"/> omitted (e.g. harmonicaRF, which is never S/Z/Y-shaped in
    /// practice) keeps the previous dB20-for-any-parameter-cube default.</para>
    /// </summary>
    internal static CubeTransform DefaultTransformFor(
        RfCore.Data.DataCube cube, PlotType plotType, string? cubeName = null)
    {
        if (plotType != PlotType.Rect) return CubeTransform.None;
        return Trace.DefaultRectTransform(
            cube.DataKind == RfCore.Data.DataKind.Complex, IsParameterCube(cube), cubeName);
    }

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
        OnPropertyChanged(nameof(IsSummaryColumn));
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
        // Unified transform combo: rebuild list, re-sync selection, refresh enabled state (inert for a
        // real expression result). SyncTransformItem pins Transform→None when inert, so notify after it.
        OnPropertyChanged(nameof(TraceTransformItems));
        SyncTransformItem();
        OnPropertyChanged(nameof(IsTransformComboEnabled));
        // Keep the Source combo honest about which file this trace actually reads from — a
        // toolbar-driven datasource change re-points a sentinel trace and only refreshes cards.
        SyncSourceSelectionToTrace();
        SyncVersusFromTrace();
        RefreshNetworkMetricCard();
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

        // ── "Y vs X": split first, because the separator binds looser than anything inside either
        //    half. Only the Y half is parsed as the trace's own cube identity, which is what keeps
        //    the axis-role editor alive on a versus trace.
        string ySpecText = text;
        if (VersusSpec.TrySplit(text, out var ySide, out var xSide, out var versusErr))
        {
            ySpecText = ySide;
            if (!TryBindXSide(xSide, out var xErr))
            {
                // TryBindXSide has already parked the raw X text on the trace so the resolve that
                // follows reports the same message — do NOT clear it here.
                _trace.InvalidSpecText = text;
                _trace.ExpressionError = xErr;
                _parent.RebuildAndNotify();
                SyncVersusFromTrace();
                return;
            }
        }
        else if (!string.IsNullOrEmpty(versusErr))
        {
            _trace.XSpec = null;
            _trace.XSourcePath = null;
            _trace.XSourceAlias = null;
            _trace.InvalidSpecText = text;
            _trace.ExpressionError = versusErr;
            _parent.RebuildAndNotify();
            SyncVersusFromTrace();
            return;
        }
        else
        {
            _trace.XSpec        = null;
            _trace.XSourcePath  = null;
            _trace.XSourceAlias = null;
        }

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
            CubeTraceSpecParser.TryParse(ySpecText, ds, out var cubeName, out var slice, out var transform, out _))
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
        SyncVersusFromTrace();             // the vs row tracks the typed text like every other control
        OnPropertyChanged(nameof(IsCubeBoundTrace));
        OnPropertyChanged(nameof(ShowAllToggleVisible));
    }

    /// <summary>
    /// Binds the typed X half. An <c>alias::Cube</c> prefix names a DIFFERENT loaded dataset — the
    /// same alias the label strips and the source combo show. The alias is resolved to a path here
    /// and never stored, because an alias is renamable and a path is not.
    /// </summary>
    private bool TryBindXSide(string xSide, out string error)
    {
        error = "";
        int sep = xSide.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0)
        {
            _trace.XSourcePath  = null;
            _trace.XSourceAlias = null;
            _trace.XSpec        = xSide;
            return true;
        }

        string alias = xSide[..sep].Trim();
        string bare  = xSide[(sep + 2)..].Trim();
        if (bare.Length == 0)
        {
            error = $"'{alias}::' names a source but no quantity.";
            return false;
        }

        var entry = _parent.LibraryEntries.FirstOrDefault(e =>
            string.Equals(e.Alias, alias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileNameWithoutExtension(e.DisplayName), alias,
                             StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            // Keep the raw "alias::Cube" on the trace: the resolver reports the unmatched alias, so
            // the message survives the resolve that follows this edit instead of being replaced.
            _trace.XSourcePath  = null;
            _trace.XSourceAlias = null;
            _trace.XSpec        = xSide;
            error = $"No loaded data source named '{alias}'.";
            return false;
        }

        _trace.XSourcePath = string.Equals(entry.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase)
            ? null
            : entry.FilePath;
        _trace.XSpec = bare;
        return true;
    }

    private static bool IsFreqUnit(string? unit) =>
        unit is "Hz" or "kHz" or "MHz" or "GHz";
}
