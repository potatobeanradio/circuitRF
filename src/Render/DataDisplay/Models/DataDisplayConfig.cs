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

namespace CircuitRF.Render.DataDisplay;

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
    // "run.npy" or null = sentinel (most-recent run); "<name>.npy" (flat results/, R-res-1) = a
    // specific run/baseline; abs path = explicit Touchstone file.
    public string? SelectedDataSource { get; set; }

    /// <summary>
    /// R-res-4 — every data source this display references, keyed the same way a trace's own
    /// SourcePath/SourceRef is (relative to the results root when the file lives under it, else the
    /// absolute path), mapped to its user-facing alias. Loaded BEFORE tab/trace restoration so a
    /// dataset with no current trace still reloads (or shows broken, R-res-5) and carries its alias;
    /// aliases are stored here rather than re-derived at load time — see DataSourceEntryViewModel.Alias.
    /// </summary>
    public Dictionary<string, string> SourceAliases { get; set; } = new();

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

    // ---- The dB radial mode (ANT-7 §2) --------------------------------------
    //
    //  Defaults match Plot's own, so a plot that never touched the mode round-trips byte for byte
    //  and every `.cdd` written before ANT-7 loads as the Linear polar plot it was.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PolarRadialMode      PolarRadial           { get; set; } = PolarRadialMode.Linear;
    public double               PolarDbFloor          { get; set; } = -40.0;
    public double               PolarDbRingStep       { get; set; } = 10.0;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PolarDbReferenceMode PolarDbReference      { get; set; } = PolarDbReferenceMode.Peak;
    public double               PolarDbReferenceValue { get; set; }
    public string               PolarDbUnit           { get; set; } = "";

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

    /// <summary>Start of a kept sub-range on a KeepAsX axis — the "10" of a typed <c>S[10..50, 1, 1]</c>.</summary>
    public int RangeStart { get; set; }

    /// <summary>
    /// End (exclusive) of that sub-range. <b>Defaults to −1, and must:</b> −1 means "the whole axis",
    /// which is <see cref="AxisSlice"/>'s own default and therefore what a <c>.cdd</c> written before
    /// this field existed has to read back as. Letting it default to 0 would be far worse than the
    /// bug it fixes — 0 is a legal EMPTY range, so every restored trace would load with no points.
    /// </summary>
    public int RangeEndExclusive { get; set; } = -1;

    /// <summary>Net-name label for a pinned axis, so a restored spec reads <c>V[:, "X1.drain"]</c>
    /// rather than falling back to a bare index.</summary>
    public string Label { get; set; } = "";

    // The two halves of the .cdd mapping, kept adjacent on purpose. AxisSlice has six fields and this
    // DTO carried three, written and read by two hand-maintained field lists a couple of hundred
    // lines apart in DataDisplayViewModel — so a narrowed X range and a net-name label were dropped
    // on every save/load. The range loss was invisible rather than merely wrong: the trace card went
    // on showing the typed spec while the plot drew the whole axis, because a trace with both
    // CubeName and Slice set resolves through the SLICE, not through Expression.

    public static AxisSliceConfig From(AxisSlice s) => new()
    {
        AxisName          = s.AxisName,
        Role              = s.Role,
        Index             = s.Index,
        RangeStart        = s.RangeStart,
        RangeEndExclusive = s.RangeEndExclusive,
        Label             = s.Label ?? "",
    };

    public AxisSlice ToSlice() =>
        new(AxisName, Role, Index, RangeStart, RangeEndExclusive, Label ?? "");
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

    // Ordered port selection for network metrics (R-stb-3a). Defaults match Trace's own, so a
    // pre-existing .cdd written before these existed loads as input=1/output=2 — exactly the
    // 2-port behaviour it had when saved.
    public int  InputPort             { get; set; } = 1;
    public int  OutputPort            { get; set; } = 2;
    public bool PassivityWholeNetwork { get; set; } = true;

    /// <summary>
    /// The fixture a passive-readout trace reads its DUT impedance out of. Defaults to
    /// shunt-through, matching <c>Trace</c>'s own default — a `.cdd` written before this existed
    /// loads as shunt-through, which is the reading a 2-port vendor part file wants and the only
    /// possible reading of a 1-port one.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RfCore.Data.PassiveExtraction PassiveExtraction { get; set; }
        = RfCore.Data.PassiveExtraction.ShuntThrough;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DependentVarFormat YAxis  { get; set; } = DependentVarFormat.Db;

    public bool   UseSecondaryAxis   { get; set; }
    public string Z0                 { get; set; } = "50";

    /// <summary>Whether the trace card's Z0 "Override" checkbox is on. Absent in files written
    /// before the override gate existed, and false is the right reading there: no override means
    /// the data is shown at the source's own per-port references, un-renormalized.</summary>
    public bool   Z0Override         { get; set; }

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

    // "Plot versus" (Y vs X). Null = X comes from the cube's swept axis, exactly as before this
    // existed, so every .cdd written earlier loads unchanged.
    public string?               XSpec         { get; set; }

    /// <summary>Absolute path of the X side's data source; null = the same source as the Y side.</summary>
    public string?               XSourcePath   { get; set; }

    public TracePropertiesConfig   Properties { get; set; } = new();
    public List<MarkerConfig>      Markers    { get; set; } = new();

    /// <summary>Non-null when this trace draws a WSProbe metric (WSP-4, R-wsp4-11). The trace is
    /// still cube-bound — <see cref="CubeName"/> names the run's own <c>…wsp</c> matrix and
    /// <see cref="CubeSlice"/> is authored against the METRIC's axes — so a reader that does not
    /// know about this field still finds a well-formed cube trace. Absent in every `.cdd` written
    /// before WSP-4, which loads unchanged.</summary>
    public WspTraceConfig? WsProbe { get; set; }

    /// <summary>Non-null when this trace is a loadpull contour trace (7.4e).
    /// When present, the standard network/cube-bound fields are ignored.</summary>
    public ContourTraceConfig? ContourTrace { get; set; }

    /// <summary>Non-null when this trace is a summary-table column (7.5). Mutually exclusive with ContourTrace.</summary>
    public SummaryColumnConfig? SummaryColumn { get; set; }
}

/// <summary>
/// Persisted authoring state for one WSProbe trace (WSP-4, R-wsp4-11) — which probe (or pair, or
/// ordered set) the metric is taken at, which metric, and the two options some of them read.
///
/// <para><b>Probes are named, never indexed.</b> The document's <c>idx</c> is assigned at
/// elaboration in flattened netlist order, so a design that gains a probe renumbers the ones after
/// it — a saved display carrying an index would come back drawing a different node with nothing
/// said. The label is the stable identity, and a label this run does not have is reported by name
/// with the run's own list beside it.</para>
///
/// <para><see cref="Metric"/> is written by NAME (<c>JsonStringEnumConverter</c>), and
/// <see cref="WspMetric"/> is append-only for the reason
/// <see cref="DerivedParameters"/> is.</para>
/// </summary>
public sealed class WspTraceConfig
{
    public string       Probe { get; set; } = "";
    public string       With  { get; set; } = "";
    public List<string> Set   { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WspMetric Metric { get; set; } = WspMetric.None;

    /// <summary>The circulator/pair/Ohtomo reference, in the trace card's own complex spelling.
    /// "0" means "take the source group's own port-1 Re(Z0), else 50 Ohm" — resolved at draw time,
    /// so a display authored against one run reads the next run's reference.</summary>
    public string Z0 { get; set; } = "0";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RfCore.Stability.WspSide ActiveSide { get; set; } = RfCore.Stability.WspSide.G;

    public int SetIndex { get; set; } = 1;

    // ── The Envelope sub-card (R-wsp4-9) ─────────────────────────────────────
    //
    //  Absent in a `.cdd` written before it, and every default below is the card's own "off" —
    //  so an older file loads with an envelope that is not being asked for, rather than one
    //  asking for a grid nobody authored.

    /// <summary>The probe whose G-side termination the source ladder replaces. Empty = unpulled.</summary>
    public string SourceProbe { get; set; } = "";

    /// <summary>The probe whose L-side termination the load ladder replaces. Empty = unpulled.</summary>
    public string LoadProbe { get; set; } = "";

    /// <summary>The source ladder, one |ΓS| per rung. Empty is the side's off state.</summary>
    public List<double> GammaSMags { get; set; } = new();

    /// <inheritdoc cref="GammaSMags"/>
    public List<double> GammaLMags { get; set; } = new();

    /// <summary>The angular step of both Γ grids, in degrees.</summary>
    public double ThetaStepDeg { get; set; } = 15.0;

    /// <summary>The passivated run <c>NDFenc</c> is taken against — a cube spec in the same source.
    /// Empty reads the <c>wsp_passive</c> beside this group's own <c>wsp</c>.</summary>
    public string PassiveSource { get; set; } = "";
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
    public RbfKernel InterpKernel { get; set; } = ContourDefaults.Kernel;
    public double    Smoothing    { get; set; } = ContourDefaults.Smoothing;
    public double?   Epsilon      { get; set; } = ContourDefaults.Epsilon;
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
