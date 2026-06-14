// ================================================================
//  DataDisplayConfig.cs  —  JSON-serialisable snapshot of the canvas layout
//
//  Ported from splotRF/src/Models/DataDisplayConfig.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.
//
//  FORMAT VERSIONS
//  v2 (multi-tab): Tabs list is non-empty.  ZoomLevel/ViewOffsetX/Y and
//      Plots at the root level are ignored.
//  v1 (legacy single-tab): Tabs is empty; Plots at root level contains
//      the plot containers and ZoomLevel/ViewOffsetX/Y apply.
// ================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;
using RfCore;

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
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PrecisionFormat FormatString          { get; set; } = PrecisionFormat.F;
    public int             MaximumFractionDigits { get; set; } = 3;

    public TracePropertiesConfig   Properties { get; set; } = new();
    public List<MarkerConfig>      Markers    { get; set; } = new();
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
