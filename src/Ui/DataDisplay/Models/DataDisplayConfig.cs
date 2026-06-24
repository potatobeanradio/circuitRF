// ================================================================
//  DataDisplayConfig.cs  —  JSON-serialisable snapshot of the canvas layout
//
//  Ported from splotRF/src/Models/DataDisplayConfig.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.
//
//  FORMAT VERSIONS
//  v2 (single-source): adds SelectedDataSource; trace SourcePath stores logical SourceRef.
//      v1 files are rejected (alpha no-back-compat). FormatVersion default stays 1 so
//      the clipboard copy/paste path (which omits this field) continues to work unchanged.
//      Multi-tab is detected by Tabs.Count > 0 (not by FormatVersion) and still applies here.
//  v1 (original): single or multi tab, absolute/relative SourcePath in traces.
// ================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;
using RfCore;
using RfCore.Loadpull;

namespace CircuitRF.Ui.DataDisplay;

// Per-tab snapshot — zoom, offset, and all plot containers for one tab.
public sealed class TabConfig
{
    public string Name        { get; set; } = "Tab 1";
    public double ZoomLevel   { get; set; } = 1.0;
    public double ViewOffsetX { get; set; } = 0.0;
    public double ViewOffsetY { get; set; } = 0.0;
    public List<PlotContainerConfig> Plots { get; set; } = new();
}

public sealed class DataDisplayConfig
{
    public const int CurrentFormatVersion = 2;

    // Written on every save; rejected on mismatch (alpha no-back-compat policy).
    // Default = 1 so clipboard JSON (which omits this field) passes the check
    // without triggering the version mismatch guard in the paste path.
    public int FormatVersion { get; set; } = 1;

    // Logical datasource id selected at the document level (drives the toolbar combo).
    // "run.npy" or null = sentinel (most-recent run); "<schematic>/run.npy" = specific sim;
    // abs path = explicit Touchstone file.
    public string? SelectedDataSource { get; set; }

    // v2: multi-tab layout.  Non-empty list takes precedence over legacy fields.
    public List<TabConfig> Tabs { get; set; } = new();

    // Index of the tab that was active when the file was saved.  Zero-based.
    // Clamped to the actual tab count on load so old/edited files are safe.
    public int ActiveTabIndex { get; set; } = 0;

    // Window geometry — logical pixels for Width/Height, physical pixels for Left/Top.
    // Zero values mean "not saved"; window is not repositioned.
    public double WindowWidth  { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft   { get; set; }
    public double WindowTop    { get; set; }

    // v1 legacy fields — single-tab layout without a TabConfig wrapper.
    // Populated by PlotExporter for clipboard copy/paste (format stays as v1 there).
    public List<PlotContainerConfig> Plots { get; set; } = new();
    public double ZoomLevel   { get; set; } = 1.0;
    public double ViewOffsetX { get; set; } = 0.0;
    public double ViewOffsetY { get; set; } = 0.0;
}

public sealed class PlotContainerConfig
{
    public double   Left   { get; set; }
    public double   Top    { get; set; }
    public double   Width  { get; set; }
    public double   Height { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlotType PlotType { get; set; } = PlotType.Smith;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FreqUnit FreqUnit { get; set; } = FreqUnit.GHz;

    // Custom axis labels.  Empty string + false = "not set" (use auto-generated label).
    // Absent in older .splot files — defaults here match Plot's own defaults so
    // round-tripping an uncustomised plot produces no visible change.
    public string CustomTitle     { get; set; } = "";
    public bool   CustomTitleOn   { get; set; }
    public string CustomXLabel    { get; set; } = "";
    public bool   CustomXLabelOn  { get; set; }
    public string CustomYLabel    { get; set; } = "";
    public bool   CustomYLabelOn  { get; set; }
    public string CustomY2Label   { get; set; } = "";
    public bool   CustomY2LabelOn { get; set; }

    // Table-view settings (ignored for non-Table plot types).
    public bool   TableViewAscendingSortOrder { get; set; } = true;
    public int    TableViewScrollIndex        { get; set; } = 0;
    public double FontSize                    { get; set; } = 12;
    public double FreqColumnWidth             { get; set; } = 115;

    // Summary-table state (Phase 7.5). Defaults match Plot so a non-summary Table round-trips unchanged.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TableOptimum  TableOptimum     { get; set; } = TableOptimum.Mxp;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TableReadMode TableReadMode    { get; set; } = TableReadMode.Interp;
    public double        TableCompression { get; set; } = 3.0;
    public string?       SummaryLoadpullGroup { get; set; }

    public List<TraceConfig> Traces { get; set; } = new();

    // Null on older .splot files — load code defaults to full autoscale when absent.
    public AxesConfig? Axes { get; set; }
}

/// <summary>
/// Serialised axis window extents and autoscale flags for one plot.
/// Absent in older .splot files — null means full autoscale on load,
/// matching the behaviour of files written before this field existed.
/// </summary>
public sealed class AxesConfig
{
    // Autoscale flags.  Default true so a null AxesConfig (old files) behaves
    // identically to the old code: everything is autoscaled on every load.
    public bool AutoscaleX      { get; set; } = true;
    public bool AutoscaleY      { get; set; } = true;
    public bool AutoscaleRightY { get; set; } = true;
    public bool AutoscaleMag    { get; set; } = true;

    // Primary axis window (world / data coordinates).
    // Rect: X = freq min, Y = data min (left-Y), Width = freq span, Height = left-Y span.
    // Smith/Polar: X = Re min, Y = Im min, Width/Height = extents.
    public double WindowX      { get; set; } = -1;
    public double WindowY      { get; set; } = -1;
    public double WindowWidth  { get; set; } =  2;
    public double WindowHeight { get; set; } =  2;

    // Secondary (right Y) axis window.
    public double WindowSecondaryX      { get; set; } = -1;
    public double WindowSecondaryY      { get; set; } = -1;
    public double WindowSecondaryWidth  { get; set; } =  2;
    public double WindowSecondaryHeight { get; set; } =  2;
}

// ---- Cube-bound persistence (Phase 7.2c-a) ----------------------------------

/// <summary>
/// Serialisable form of one AxisSlice.  Null CubeName in TraceConfig means
/// network-bound (old .cdd files load unchanged — no migration required).
/// </summary>
public sealed class AxisSliceConfig
{
    public string AxisName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AxisRole Role { get; set; } = AxisRole.PinToIndex;

    public int Index { get; set; }
}

public sealed class TraceConfig
{
    public string?  SourcePath       { get; set; }
    public int      Row              { get; set; }
    public int      Col              { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MatrixType MatrixType { get; set; } = MatrixType.S;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DerivedParameters Derived { get; set; } = DerivedParameters.None;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DependentVarFormat YAxis  { get; set; } = DependentVarFormat.Db;

    public bool   UseSecondaryAxis   { get; set; }
    public string Z0                 { get; set; } = "50";

    // Table-view per-trace settings.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MatrixFormat    MatrixFormat          { get; set; } = MatrixFormat.MA;
    public double          ColumnWidth           { get; set; } = 115;
    public double          XColumnWidth          { get; set; } = 0;
    public Dictionary<int, double> FamilyColumnWidths { get; set; } = new();
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PrecisionFormat FormatString          { get; set; } = PrecisionFormat.F;
    public int             MaximumFractionDigits { get; set; } = 3;

    // Cube-bound fields (Phase 7.2c-a). Null = network-bound; loads as before.
    public string?               CubeName      { get; set; }
    public List<AxisSliceConfig> CubeSlice     { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CubeTransform         CubeTransform { get; set; } = CubeTransform.None;

    // Expression-mode field. When non-null, supersedes CubeName/CubeSlice/CubeTransform for value production.
    public string?               Expression    { get; set; }

    public TracePropertiesConfig   Properties { get; set; } = new();
    public List<MarkerConfig>      Markers    { get; set; } = new();

    /// <summary>Non-null when this trace is a loadpull contour trace (7.4e).
    /// When present, the standard network/cube-bound fields are ignored.</summary>
    public ContourTraceConfig? ContourTrace { get; set; }

    /// <summary>Non-null when this trace is a summary-table column (7.5). Mutually exclusive with ContourTrace.</summary>
    public SummaryColumnConfig? SummaryColumn { get; set; }
}

/// <summary>Persisted authoring state for one summary-table column (Phase 7.5).
/// When present, the trace is a summary column; standard network/cube fields are ignored.</summary>
public sealed class SummaryColumnConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SummaryColumnKind Kind { get; set; } = SummaryColumnKind.Metric;
    public string MetricName     { get; set; } = "Pout";
    public string Header         { get; set; } = "";
    public int    FractionDigits { get; set; } = 1;
    public double ColumnWidth    { get; set; } = 0;
}

public sealed class MarkerConfig
{
    public string  Name                   { get; set; } = "m0";
    public int     Index                  { get; set; }
    public double  Freq                   { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FreqUnit FreqUnits { get; set; } = FreqUnit.GHz;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MatrixFormat MatrixFormat { get; set; } = MatrixFormat.MA;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MarkerStyle Style { get; set; } = MarkerStyle.Medium;

    public bool   UseNormalizedImpedance  { get; set; } = true;
    public int    MaximumFractionDigits   { get; set; } = 4;

    // Info-box position in logical pixels relative to the PlotContainerView.
    public double InfoBoxX { get; set; }
    public double InfoBoxY { get; set; }

    // Multi-marker / delta mode flags.
    public bool IsMulti { get; set; }
    public bool IsDelta { get; set; }

    // For stability-circle markers: snapped world position.
    public float PositionStaticX { get; set; }
    public float PositionStaticY { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MarkerKind MarkerKind { get; set; } = MarkerKind.Polyline;

    public bool   ShowInfoBox    { get; set; } = true;
    public bool   ContourSnapped { get; set; }
    public bool   VswrEnabled    { get; set; }
    public double VswrValue      { get; set; } = 2.0;
}

/// <summary>Persisted authoring state for one loadpull contour trace (7.4e).
/// The Grid/Scatter/Levels are not persisted — they are re-derived at load time by RebuildContour.</summary>
public sealed class ContourTraceConfig
{
    public string MetricName { get; set; } = "Pout";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConstraintKind ConstraintKind { get; set; } = ConstraintKind.Compression;

    public string ConstraintMetricName { get; set; } = "";
    public double ConstraintValue      { get; set; } = 3.0;
    public int    FreqIndex            { get; set; } = 0;
    public string? LoadpullGroup       { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContourLevelMode LevelMode  { get; set; } = ContourLevelMode.Range;

    public double LevelStart { get; set; } = -30.0;
    public double LevelStep  { get; set; } = 0.5;
    public double LevelStop  { get; set; } = 60.0;
    public int    LevelCount { get; set; } = 10;

    public bool ShowIsoLines { get; set; } = true;
    public bool ShowFill     { get; set; }
    public bool DrawLabels   { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContourFillKind SelectedFillKind { get; set; } = ContourFillKind.TopoMap;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContourColorMap ColorMap { get; set; } = ContourColorMap.Hot;

    public double LabelSpacing { get; set; } = 1.0;

    // ---- Overlay display toggles -------------------------------------
    public bool    DisplayMxp        { get; set; }
    public bool    DisplayMxe        { get; set; }
    public bool    DisplayGridPoints { get; set; }
    public uint    GridPointColor    { get; set; } = 0xFF000000u; // SKColors.Black ARGB
    public uint    LabelForeground   { get; set; } = 0xFFFFFFFFu; // SKColors.White ARGB

    // ---- Iso-line style ----------------------------------------------
    public uint   LineColor          { get; set; } = 0xDCFFFFFFu; // white, 220 alpha
    public float  StrokeWidth        { get; set; } = 1.5f;
    public bool   LineColorOverridden { get; set; }
    public uint   LabelBackground    { get; set; } = 0x8C000000u; // black, 140 alpha
    public double GridPointSize      { get; set; } = 3.0;
    public double LevelFontSize      { get; set; } = 9.0;
    public bool   FadeLineOpacity    { get; set; }

    // ---- Interp engine params -----------------------------------------
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RbfKernel InterpKernel { get; set; } = RbfKernel.Multiquadric;
    public double    Smoothing    { get; set; } = 1e-3;
    public double?   Epsilon      { get; set; } = null;
}

public sealed class TracePropertiesConfig
{
    public bool   LineEnabled    { get; set; } = true;
    public double LineWidth      { get; set; } = 1.0;
    public int    LineColorIndex { get; set; } = 12;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LineType LineType     { get; set; } = LineType.Solid;

    public bool   MarkerEnabled    { get; set; }
    public double MarkerSize       { get; set; } = 1.5;
    public int    MarkerColorIndex { get; set; } = 12;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MarkerType MarkerType { get; set; } = MarkerType.Circle;
}
