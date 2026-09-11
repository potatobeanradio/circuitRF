// ================================================================
//  Plot.cs  —  Plot model  (pure data + logic, no drawing)
//
//  Ported from splotRF/src/Models/Plot.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.  AppSettings.GoldenAspectRatio is
//  inlined as the golden ratio constant (≈ 1.618033988749895).
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;

namespace CircuitRF.Render.DataDisplay
{
    // ============================================================
    //  FreqUnit
    // ============================================================

    public enum FreqUnit { Hz, kHz, MHz, GHz, THz, ZHz, YHz }

    public static class FreqUnitExtensions
    {
        private static readonly Dictionary<FreqUnit, double> ScaleMap = new()
        {
            { FreqUnit.Hz,  1      },
            { FreqUnit.kHz, 1e-3   },
            { FreqUnit.MHz, 1e-6   },
            { FreqUnit.GHz, 1e-9   },
            { FreqUnit.THz, 1e-12  },
            { FreqUnit.ZHz, 1e-21  },
            { FreqUnit.YHz, 1e-24  }
        };

        /// <summary>Multiply a frequency in Hz by Scale to get the display value.</summary>
        public static double Scale(this FreqUnit u) =>
            ScaleMap.TryGetValue(u, out var s) ? s : 1.0;

        public static string Description(this FreqUnit u) => u.ToString();
    }

    // ============================================================
    //  PlotType
    // ============================================================

    /// <summary>
    /// <b>Four kinds, and the fourth is why the fifth fits.</b> <see cref="Table"/> has no x axis, no
    /// y axis and no <see cref="Axes.Window"/> in the sense the other three use one, and it has
    /// worked that way since before ANT-10 asked whether a non-2D kind belonged here at all
    /// (brief-antenna-10-pattern-3d.md §2). Every seam a plot kind passes through is an early return
    /// or an additive <c>switch</c> case, and <see cref="PlotTypeExtensions.IsRect"/> and
    /// <see cref="PlotTypeExtensions.IsComplex"/> are POSITIVE predicates over named members, so a
    /// new kind is false for both and needs no third predicate threaded anywhere.
    /// </summary>
    public enum PlotType
    {
        Smith, Polar, Rect, Table,

        /// <summary>ANT-10: a pattern r(θ, φ) in dB over a floor, drawn as a rotatable surface.
        /// <b>Not the default pattern view</b> — the principal-plane cuts of ANT-7 are, and they tell
        /// an engineer more about an antenna than a 3D lobe does.</summary>
        Surface3D,
    }

    public static class PlotTypeExtensions
    {
        public static string Description(this PlotType t) => t switch
        {
            PlotType.Smith => "Smith Chart",
            PlotType.Polar => "Polar Plot",
            PlotType.Rect  => "Rectangular Plot",
            PlotType.Table  => "Table",
            PlotType.Surface3D => "3D Pattern",
            _              => t.ToString()
        };
        public static bool IsRect   (this PlotType t) => t == PlotType.Rect;

        /// <summary>The 3D pattern surface. Its own predicate rather than an <c>IsPlanar</c> over
        /// the other four, because every call site that needs it is asking "is this the surface",
        /// never "is this planar".</summary>
        public static bool IsSurface(this PlotType t) => t == PlotType.Surface3D;
        public static bool IsComplex(this PlotType t) => t == PlotType.Smith || t == PlotType.Polar;
    }

    // ============================================================
    //  Plot
    // ============================================================

    public class Plot
    {
        // ---- Static layout constants ------------------------------------

        public static double SmithAspect = 1.0;
        // Golden ratio — inlined from AppSettings.GoldenAspectRatio (not ported in 7.1b)
        public static double RectAspect  = 1.618033988749895;
        public static double TableAspect = 1.0;

        // ---- Identity ---------------------------------------------------

        public Guid Id { get; set; } = Guid.NewGuid();

        // ---- Plot type --------------------------------------------------

        public PlotType PlotType { get; private set; } = PlotType.Smith;

        // ---- Frequency units --------------------------------------------

        private FreqUnit _freqUnits = FreqUnit.GHz;
        public FreqUnit FreqUnits
        {
            get => _freqUnits;
            set
            {
                _freqUnits = value;
                foreach (var t in Traces) t.BuildPath(PlotType, FreqUnits);
                Autoscale();
            }
        }

        // ---- Axes -------------------------------------------------------

        public Axes Axes { get; set; } = new Axes();

        private Dictionary<PlotType, Axes> _axesStorage = new();

        // ---- Traces -----------------------------------------------------

        private readonly ObservableCollection<Trace> _traces = new();
        public ObservableCollection<Trace> Traces => _traces;

        private void OnTracesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Axes.ShowSecondary = NeedsSecondary;
            SetAxesViewport();
            Autoscale();
        }

        public bool          NeedsSecondary   => Traces.Any(t => t.UseSecondaryAxis);
        public List<Trace>   LeftAxisTraces   => Traces.Where(t => !t.UseSecondaryAxis).ToList();
        public List<Trace>   RightAxisTraces  => Traces.Where(t =>  t.UseSecondaryAxis).ToList();
        public bool          SupportsComplex  => PlotType != PlotType.Rect;

        // ---- Autoscale flags --------------------------------------------

        private bool _autoscaleX = true;
        public bool AutoscaleX
        {
            get => _autoscaleX;
            set { _autoscaleX = value; if (value) RunAutoscale("x"); }
        }

        private bool _autoscaleY = true;
        public bool AutoscaleY
        {
            get => _autoscaleY;
            set { _autoscaleY = value; if (value) RunAutoscale("y"); }
        }

        private bool _autoscaleRightY = true;
        public bool AutoscaleRightY
        {
            get => _autoscaleRightY;
            set { _autoscaleRightY = value; if (value) RunAutoscale("rightY"); }
        }

        private bool _autoscaleMag = true;
        public bool AutoscaleMag
        {
            get => _autoscaleMag;
            set { _autoscaleMag = value; if (value) RunAutoscale("both"); }
        }

        /// <summary>
        /// Whether autoscale on a complex-plane plot never shows less than the unit circle.
        ///
        /// <para><b>True for a Smith chart and false for a Polar plot, and that difference is the
        /// point.</b> A Smith chart's grid IS the unit disc — a Γ plane whose |Γ| = 1 circle is the
        /// chart, so shrinking inside it would draw a chart with no boundary. A Polar plot carries
        /// whatever the trace is: an impedance in ohms, an admittance in siemens, a loop gain. The
        /// unity floor made every one of those unreadable in one direction — a 1/H0 locus of a few
        /// tens of millisiemens collapsed to a dot at the origin, which is indistinguishable from a
        /// trace that is not being drawn at all.</para>
        /// </summary>
        public bool AutoscaleEnforceUnityMinimum { get; set; } = true;

        /// <summary>The effective floor: the property above, and never on a Polar plot.</summary>
        private bool UnityMinimumApplies
            => AutoscaleEnforceUnityMinimum && PlotType != PlotType.Polar;

        // ---- The dB radial mode (ANT-7 §2) ------------------------------

        /// <summary>
        /// How this plot's RADIUS is read. <see cref="PolarRadialMode.Linear"/> is every polar plot
        /// that existed before ANT-7 and stays the default; <see cref="PolarRadialMode.Db"/> makes it
        /// a pattern plot. Ignored on every other plot type.
        /// </summary>
        public PolarRadialMode PolarRadial { get; set; } = PolarRadialMode.Linear;

        /// <summary>
        /// Where the centre of a Db-radial plot is, RELATIVE TO THE OUTER RING — so −40 means the
        /// centre is 40 dB below whatever the outer ring turned out to be, and the control means the
        /// same thing whether the reference is the peak or an absolute value.
        ///
        /// <para>It is a control rather than a constant because a high-directivity pattern and a
        /// near-isotropic one want different ones: 40 dB of disc on a pattern whose whole variation
        /// is 3 dB is a dot at the rim.</para>
        /// </summary>
        public double PolarDbFloor { get; set; } = -40.0;

        /// <summary>Ring spacing in dB. 10 is what a pattern is read on.</summary>
        public double PolarDbRingStep { get; set; } = 10.0;

        /// <summary>Whether the outer ring is the data's own peak or a value the author named.</summary>
        public PolarDbReferenceMode PolarDbReference { get; set; } = PolarDbReferenceMode.Peak;

        /// <summary>The outer-ring value when <see cref="PolarDbReference"/> is
        /// <see cref="PolarDbReferenceMode.Absolute"/>. Ignored otherwise.</summary>
        public double PolarDbReferenceValue { get; set; } = 0.0;

        /// <summary>
        /// The unit the radial numbers are in — "dBi" for a gain pattern, "dB(W/sr)" for radiation
        /// intensity. Empty means "take it from the cube, and fall back to dB": a cube VALUE unit is
        /// not something every source carries, and a bare "dB" is honest where "dBi" would be a claim.
        /// </summary>
        public string PolarDbUnit { get; set; } = "";

        /// <summary>True when this plot is drawing a pattern rather than a locus.</summary>
        public bool IsPolarPattern => PlotType == PlotType.Polar && PolarRadial == PolarRadialMode.Db;

        /// <summary>
        /// True when this plot carries a <see cref="PatternScale"/> at all — the polar cut in its dB
        /// radial mode, or ANT-10's surface.
        ///
        /// <para><b>The surface has no mode to switch.</b> A 3D pattern IS a radius in dB above a
        /// floor (ANT-10 §1); there is no linear reading of it that means anything, so
        /// <see cref="PolarRadialMode"/> does not apply and is not consulted.</para>
        /// </summary>
        public bool IsPatternPlot => IsPolarPattern || PlotType == PlotType.Surface3D;

        // ---- ANT-10: the 3D pattern surface ------------------------------

        /// <summary>Where the eye is. Persisted with the plot, so a `.cdd` reopens on the view it was
        /// saved from and <c>circuitrf render</c> draws that same view.</summary>
        public PatternCamera SurfaceCamera { get; set; } = PatternCamera.Default;

        /// <summary>
        /// The ramp the surface is coloured by. <b>The contour renderer's own enum</b>, sampled by
        /// its own <c>ContourColormaps.Sample</c> — ANT-10 §7 forbids a third colour ramp and this
        /// is the second, not a new one.
        ///
        /// <para><b>The DEFAULT is not the contour's own <c>Bone</c>, and it was measured rather
        /// than chosen.</b> A contour is drawn INSIDE a framed chart, so a ramp that ends at white
        /// or at black is bounded by the axes whatever the theme. A surface is not: it is a free
        /// shape on the page, and a facet the colour of the background has no edge at all. Of the
        /// thirteen ramps, exactly two — <c>Cool</c> and <c>Winter</c> — have NEITHER end at white
        /// nor near-black, and are therefore legible on both themes; <c>Cool</c> is the one whose
        /// floor (cyan) is also bright enough to read on the dark one. Every other ramp remains
        /// available and is persisted, so an author who wants the conventional black-to-white heat
        /// map has it.</para>
        /// </summary>
        public ContourColorMap SurfaceColorMap { get; set; } = ContourColorMap.Cool;

        /// <summary>
        /// Whether the ground plane is drawn as a disc at θ = 90°. On by default and §4 says why: a
        /// hemisphere floating above nothing reads as a complete, very good antenna.
        /// </summary>
        public bool SurfaceShowGroundDisc { get; set; } = true;

        /// <summary>Whether the scene's x/y/z axes are drawn. On by default — "a pattern with no axes
        /// is a shape" (§3).</summary>
        public bool SurfaceShowAxes { get; set; } = true;

        /// <summary>The resolved radial scale, or null when this is not a pattern plot. Rebuilt by
        /// <see cref="RefreshPolarPattern"/>; never set from outside.</summary>
        public PolarPatternScale? PatternScale { get; private set; }

        /// <summary>
        /// Resolves the radial scale from EVERY trace on the plot at once and pushes it onto each of
        /// them, rebuilding their paths when it changed.
        ///
        /// <para><b>Why the plot owns this and a trace cannot.</b> A trace builds its own path the
        /// moment its data is set (<c>Trace.SetCubeData</c>), which is before the plot knows what
        /// else is on it — and a normalised reference is a property of the whole plot, not of one
        /// curve. So the scale is resolved here, at the two points every caller already passes
        /// through after resolving traces: <see cref="Autoscale"/> and
        /// <see cref="RestoreAxesFromConfig"/>.</para>
        /// </summary>
        public void RefreshPolarPattern()
        {
            PolarPatternScale? scale = IsPatternPlot ? BuildPatternScale() : null;

            bool changed = !Equals(scale, PatternScale);
            PatternScale = scale;

            foreach (var t in Traces)
            {
                bool traceChanged = !Equals(t.PatternScale, scale);
                t.PatternScale = scale;
                if (changed || traceChanged) t.BuildPath(PlotType, FreqUnits);
            }
        }

        private PolarPatternScale? BuildPatternScale()
        {
            double step = PolarDbRingStep > 0 ? PolarDbRingStep : 10.0;
            // A floor at or above the outer ring is not a disc. Clamped to one ring rather than
            // refused: the control is a spinner and a user dragging it past zero wants the smallest
            // legible scale, not an empty plot.
            double depth = PolarDbFloor < 0 ? -PolarDbFloor : step;

            double peak = double.NegativeInfinity;
            string unit = PolarDbUnit;
            foreach (var t in Traces)
            {
                if (t.IsContourTrace || t.IsSummaryColumn) continue;
                double p = t.PatternPeakDb();
                if (double.IsFinite(p) && p > peak) peak = p;
                if (unit.Length == 0 && t.PatternDbUnit is { Length: > 0 } u) unit = u;
            }

            bool normalised = PolarDbReference == PolarDbReferenceMode.Peak;
            double reference = normalised ? peak : PolarDbReferenceValue;

            // No finite sample anywhere — every trace is empty or unresolved. A plot with no data
            // still needs a grid to say what it WOULD show, so the scale is built on the author's own
            // numbers with the peak standing in at 0.
            if (!double.IsFinite(reference)) reference = 0.0;

            int above = 0;
            if (!normalised)
                foreach (var t in Traces)
                {
                    if (t.IsContourTrace || t.IsSummaryColumn) continue;
                    above += t.PatternCountAbove(reference);
                }

            return new PolarPatternScale
            {
                ReferenceDb         = reference,
                FloorDb             = reference - depth,
                RingStepDb          = step,
                Normalised          = normalised,
                Unit                = unit.Length > 0 ? unit : "dB",
                AboveReferenceCount = above,
            };
        }

        // ---- Display options --------------------------------------------

        public bool   ShowWatermark      { get; set; } = false;
        public string CustomTitle    { get; set; } = "";
        public bool   CustomTitleOn  { get; set; } = false;

        /// <summary>Renders <see cref="Title"/> in bold. Defaults false — every existing plot keeps
        /// its regular-weight title; harmonicaRF's Loadline/Power Sweep/Time Domain plots opt in
        /// (<c>HarmonicaPanelRenderer</c>'s own three <c>Build*Plot</c> methods).</summary>
        public bool   CustomTitleBold { get; set; } = false;
        public string CustomXLabel   { get; set; } = "";
        public bool   CustomXLabelOn { get; set; } = false;
        public string CustomYLabel   { get; set; } = "";
        public bool   CustomYLabelOn { get; set; } = false;
        public string CustomY2Label  { get; set; } = "";
        public bool   CustomY2LabelOn{ get; set; } = false;

        public string Title
        {
            get
            {
                if (CustomTitleOn) return CustomTitle;
                var parts = Traces
                    .Where(t => t.IsContourTrace)
                    .Select(t => t.ContourData!.TitleString())
                    .ToList();
                return parts.Count > 0 ? string.Join(" / ", parts) : "";
            }
        }

        public string YLabel
        {
            get
            {
                if (CustomYLabelOn) return CustomYLabel;
                if (PlotType == PlotType.Rect && Traces.Any(t => t.IsContourTrace))
                    return "Imaginary (Ω)";
                return "";
            }
        }

        public string Y2Label => CustomY2LabelOn ? CustomY2Label : "";

        public string XLabel
        {
            get
            {
                if (CustomXLabelOn) return CustomXLabel;

                // Smith/Polar contour: no freq label.
                if (PlotType.IsComplex() && Traces.Any(t => t.IsContourTrace)) return "";

                // Rect contour (Z-plane): impedance axis labels.
                if (PlotType == PlotType.Rect && Traces.Any(t => t.IsContourTrace))
                    return "Real (Ω)";

                if (Traces.Count > 0 && Traces[0].IsCubeBound)
                    return XLabelFor(Traces[0]);

                // Network/SNP behavior (unchanged).
                string u = FreqUnits.Description();
                if (Traces.Count == 0 || !SupportsComplex)
                    return $"freq ({u})";
                string min = (FreqUnits.Scale() * Traces[0].MinFreq).ToString($"G{Axes.NumDigitsXAxis}");
                string max = (FreqUnits.Scale() * Traces[0].MaxFreq).ToString($"G{Axes.NumDigitsXAxis}");
                return $"freq ({min} to {max} {u})";
            }
        }

        /// <summary>
        /// The X-axis label ONE trace would carry — the cube's X-axis name and unit, or the X spec
        /// text for a "plot versus" trace (whose X data is a quantity, not an axis, and therefore
        /// carries no unit: cube VALUES have no unit anywhere in the data model, only axes do).
        /// <para>Per-trace because a plot may legitimately hold traces with DIFFERENT X quantities
        /// once "vs" exists (Gain vs Pout beside Gain vs Pin) — see <see cref="XLabelsDiffer"/>.</para>
        /// </summary>
        public string XLabelFor(Trace t)
        {
            if (!t.IsCubeBound)
            {
                string u = FreqUnits.Description();
                if (!SupportsComplex) return $"freq ({u})";
                string mn = (FreqUnits.Scale() * t.MinFreq).ToString($"G{Axes.NumDigitsXAxis}");
                string mx = (FreqUnits.Scale() * t.MaxFreq).ToString($"G{Axes.NumDigitsXAxis}");
                return $"freq ({mn} to {mx} {u})";
            }

            string axisName = t.CubeXAxisName;
            string? unit    = t.CubeXUnit;
            if (string.IsNullOrEmpty(axisName)) axisName = "x";
            bool isFreq = unit is "Hz" or "kHz" or "MHz" or "GHz";
            bool isHarmonicAxis = string.Equals(axisName, Trace.HarmonicAxisName, StringComparison.Ordinal);
            if (isFreq || isHarmonicAxis)
                return $"freq ({FreqUnits.Description()})";
            return string.IsNullOrEmpty(unit) ? axisName : $"{axisName} ({unit})";
        }

        /// <summary>
        /// The unit suffix shown beside the X-axis limits — "(GHz)" for a frequency X, the axis's own
        /// unit for a unit-bearing sweep, and EMPTY when the X quantity has no unit (a versus X, or a
        /// bare sweep variable). It used to be hardcoded to the frequency unit for every Rect plot, so
        /// a Pin sweep's limits were already labelled "(GHz)" before "plot versus" existed.
        /// </summary>
        public string XAxisUnitLabel
        {
            get
            {
                if (!PlotType.IsRect()) return "";
                if (Traces.Count == 0 || !Traces[0].IsCubeBound) return $"({FreqUnits.Description()})";
                string label = XLabelFor(Traces[0]);
                int open = label.LastIndexOf('(');
                return open >= 0 && label.EndsWith(")", StringComparison.Ordinal) ? label[open..] : "";
            }
        }

        /// <summary>Non-contour traces, in plot order — the ones that own an X label row.</summary>
        public IReadOnlyList<Trace> XLabelTraces =>
            Traces.Where(t => !t.IsContourTrace && !t.IsSummaryColumn).ToList();

        /// <summary>
        /// True when the traces on this plot do NOT share one X quantity — the case "plot versus"
        /// makes reachable. The Rect renderer then draws ONE X label row per trace, in the trace's
        /// own colour, exactly as the Y labels have always been drawn; with a shared X it keeps the
        /// single centred label.
        /// </summary>
        public bool XLabelsDiffer
        {
            get
            {
                if (CustomXLabelOn) return false;
                var traces = XLabelTraces;
                if (traces.Count < 2) return false;
                string first = XLabelFor(traces[0]);
                for (int i = 1; i < traces.Count; i++)
                    if (!string.Equals(XLabelFor(traces[i]), first, StringComparison.Ordinal)) return true;
                return false;
            }
        }

        // ---- Table view -------------------------------------------------

        public double         ColumnWidth                 { get; set; } = 115;
        public bool           TableViewAscendingSortOrder { get; set; } = true;
        public int            TableViewScrollIndex        { get; set; } = 0;

        // ---- Summary-table state (Phase 7.5) ----------------------------
        // Table-wide controls: which optimum, how metrics are read, and the single shared compression.
        // Only meaningful when PlotType == Table with summary traces; ignored otherwise.
        public TableOptimum  TableOptimum     { get; set; } = TableOptimum.Mxp;
        public TableReadMode TableReadMode    { get; set; } = TableReadMode.Interp;
        public double        TableCompression { get; set; } = 3.0;
        /// <summary>Which recognized loadpull analysis group (e.g. "LP1"/"LPP1") the summary reads when the
        /// source carries more than one. Null/empty = the first loadpull view.</summary>
        public string?       SummaryLoadpullGroup { get; set; }

        /// <summary>Per-slice row axis values for a summary Table, set by the VM's RebuildSummary.
        /// Null/empty for non-summary tables. Not persisted (re-derived on load).</summary>
        public double[]? SummaryFreqs { get; set; }

        /// <summary>Name of the summary row axis — "freq" for a frequency-swept loadpull, or the swept
        /// variable name (e.g. "RFfreq", "Vds") for a parametric-swept loadpull/pursuit. Drives the
        /// summary "Freq" anchor-column header. Not persisted (re-derived on load).</summary>
        public string? SummaryAxisName { get; set; }

        /// <summary>Unit of the summary row axis — "Hz" for a frequency sweep (shown in FreqUnits), or the
        /// swept variable's unit otherwise ("V", or "" when unitless). Not persisted.</summary>
        public string? SummaryAxisUnit { get; set; }

        public PrecisionFormat FormatString               { get; set; } = PrecisionFormat.F;
        public int            MaximumFractionDigits       { get; set; } = 3;
        public double         FontSize                 { get; set; } = 12;

        // ---- Derived properties -----------------------------------------

        public PlotRect   Viewport     => Axes.Viewport;
        public double AspectRatio  => Axes.Viewport.Width / Axes.Viewport.Height;

        // ---- Constructors -----------------------------------------------

        public Plot()
        {
            PlotType = PlotType.Smith;
            _traces.CollectionChanged += OnTracesChanged;
        }

        /// <summary>
        /// A newly created plot. <b>Axis panning starts LOCKED</b> — see
        /// <see cref="Axes.LockedPanning"/> for what the two states cost each other.
        /// </summary>
        /// <remarks>
        /// <b>Owner, 2026-08-21:</b> <i>"have all new plots be placed with Lock Axes Panning turned
        /// on. So we get the same behavior as before despite these new changes."</i> A drag inside
        /// an UNLOCKED plot pans its axes, which means it can no longer move or select the plot —
        /// the two gestures are the same gesture, and only one of them can have it. Moving and
        /// selecting are what a user does constantly while arranging a display; panning is
        /// deliberate and occasional. So the default is the one that keeps the display usable, and
        /// panning is opted into per plot from its own context menu.
        ///
        /// <para>The default lives here rather than on <see cref="Axes"/> because
        /// <c>LockedPanning</c> means nothing outside the Data Display's own
        /// <c>PlotControl</c> — harmonicaRF's panels never read it — and a model-wide default of
        /// "locked" would be asserting something about axes in general that is not true.</para>
        /// </remarks>
        public Plot(PlotType plotType, FreqUnit freqUnits)
        {
            PlotType   = plotType;
            _freqUnits = freqUnits;
            Axes.LockedPanning = true;
            SetAxesViewport();
            Autoscale();
            if (PlotType != PlotType.Table)
                _axesStorage[PlotType] = Axes;
            Axes.ShowSecondary = NeedsSecondary;
            _traces.CollectionChanged += OnTracesChanged;
        }

        public Plot(Plot src, bool initNewTraces = false, bool initNewMarkers = false)
        {
            Axes             = new Axes(src.Axes);
            PlotType         = src.PlotType;
            _freqUnits       = src.FreqUnits;
            _autoscaleRightY = src.AutoscaleRightY;
            _autoscaleY      = src.AutoscaleY;
            _axesStorage     = src._axesStorage;
            ShowWatermark    = src.ShowWatermark;
            ColumnWidth      = src.ColumnWidth;

            if (initNewTraces)
            {
                foreach (var trace in src.Traces)
                {
                    var nt = new Trace(trace);
                    nt.BuildPath(PlotType, FreqUnits);
                    _traces.Add(nt);
                }
            }

            _traces.CollectionChanged += OnTracesChanged;
        }

        /// <summary>
        /// A view of this plot for ONE frame, safe to hand to a draw operation: every field is
        /// shared with the live plot except <see cref="Axes"/>, which is a private copy taken at
        /// the moment of the call.
        /// </summary>
        /// <remarks>
        /// <b>A draw operation must not read live, mutable state, and this is what happens when it
        /// does</b> (owner, 2026-08-21: <i>"still glitchy … I even see some ticks leave the world
        /// space and render outside the rect plot's box … I don't see it much if I pan slowly with
        /// mouse. But if I pan fast with the mouse, it becomes way more obvious."</i>).
        ///
        /// <para><c>PlotControl.Render</c> only RECORDS an <c>ICustomDrawOperation</c>; the
        /// compositor executes it afterwards. Handing it the live <c>Plot</c> meant the drawing
        /// code read <c>Axes.Window</c> at execution time, by which point further pointer events
        /// had already moved it — and it reads the window MANY times per frame: once to build the
        /// world→canvas transform, again inside <c>Ticks()</c>, again for the tick-mark and
        /// gridline endpoints. Any pan landing between two of those reads produced a frame whose
        /// tick VALUES came from one window and whose TRANSFORM came from another. Ticks then land
        /// wherever the mismatch puts them, including outside the axes — the reported symptom
        /// exactly.</para>
        ///
        /// <para>It is speed-dependent for the same reason: the artefact is proportional to how far
        /// the window moves between two reads, so a slow drag hides it and a fast one makes it
        /// obvious. That is also why it survived quantizing the pan to whole pixels — quantization
        /// shrinks the per-event delta at low speed, which is precisely where it was already hard
        /// to see.</para>
        ///
        /// <para><c>MemberwiseClone</c> rather than a hand-written field list on purpose: the
        /// renderers read a long tail of plot state (title, custom axis labels, table layout,
        /// format strings), and a copy constructor that forgets one renders the frame WRONG rather
        /// than failing. The traces are deliberately shared — their geometry is rebuilt only on a
        /// structural change, never during a pan — and so is the trace collection, whose
        /// <c>CollectionChanged</c> subscription still belongs to the live plot, so the snapshot
        /// adds no handler and can trigger no autoscale.</para>
        /// </remarks>
        public Plot RenderSnapshot()
        {
            var snapshot = (Plot)MemberwiseClone();
            snapshot.Axes = new Axes(Axes);
            return snapshot;
        }

        // ---- Axes limit setters -----------------------------------------

        public void SetAxesWindow(PlotRect window, bool secondary = false)
        {
            if (secondary) Axes.WindowSecondary = window;
            else           Axes.Window          = window;
        }

        public void SetAxesLimits(
            bool useSecondary,
            double xMin, double xMax,
            double yMin, double yMax)
        {
            if (!double.IsFinite(xMin) || !double.IsFinite(xMax) ||
                !double.IsFinite(yMin) || !double.IsFinite(yMax)) return;

            var w = new PlotRect(Math.Min(xMin, xMax), Math.Min(yMin, yMax),
                             Math.Abs(xMax - xMin), Math.Abs(yMax - yMin));
            if (useSecondary) Axes.WindowSecondary = w;
            else              Axes.Window          = w;
            Axes.WindowState          = Axes.Window;
            Axes.WindowSecondaryState = Axes.WindowSecondary;
        }

        public void SetAxesToUnity(string which = "both")
        {
            var unity = new PlotRect(-1, -1, 2, 2);
            if (which is "left"  or "both") Axes.Window          = unity;
            if (which is "right" or "both") Axes.WindowSecondary = unity;
            Axes.WindowState          = Axes.Window;
            Axes.WindowSecondaryState = Axes.WindowSecondary;
        }

        /// <summary>Zoom by a scale factor centred on the current window midpoint.</summary>
        public void SetAxesLimits(double mag)
        {
            if (!double.IsFinite(mag) || mag <= 0) return;

            if (SupportsComplex)
            {
                double cX = Axes.Window.X + Axes.Window.Width  / 2;
                double cY = Axes.Window.Y + Axes.Window.Height / 2;
                SetAxesLimits(false, cX - mag, cX + mag, cY - mag, cY + mag);
                SetAxesLimits(true,  cX - mag, cX + mag, cY - mag, cY + mag);
            }
            else
            {
                double iX  = Axes.Window.Width           * (1 - mag) / 2;
                double iY  = Axes.Window.Height          * (1 - mag) / 2;
                double iX2 = Axes.WindowSecondary.Width  * (1 - mag) / 2;
                double iY2 = Axes.WindowSecondary.Height * (1 - mag) / 2;

                var nw  = InflateRect(Axes.Window,          -iX,  -iY);
                var nw2 = InflateRect(Axes.WindowSecondary, -iX2, -iY2);
                SetAxesLimits(false, nw.Left,  nw.Right,  nw.Top,  nw.Bottom);
                SetAxesLimits(true,  nw2.Left, nw2.Right, nw2.Top, nw2.Bottom);
            }
        }

        // ---- Viewport layout --------------------------------------------

        public void SetAxesViewport()
        {
            switch (PlotType)
            {
                case PlotType.Smith:
                case PlotType.Polar:
                {
                    double w = 0.99, h = w / SmithAspect;
                    Axes.Viewport = new PlotRect(0.5 / w - 0.5, 0.5 / h - 0.5, w, h);
                    break;
                }
                case PlotType.Rect:
                {
                    int    nl    = LeftAxisTraces.Count;
                    int    nr    = RightAxisTraces.Count;
                    double left  = Math.Min(Math.Max(0.13, 0.10 + nl * 0.05), 0.40);
                    double right = Axes.ShowSecondary
                        ? Math.Min(Math.Max(0.13, 0.10 + nr * 0.05), 0.40)
                        : 0.05;
                    if (left + right > 0.70)
                    {
                        double s = 0.70 / (left + right);
                        left *= s; right *= s;
                    }
                    double top = 0.10;
                    double bot = 0.15;
                    Axes.Viewport = new PlotRect(left, top, 1.0 - left - right, 1.0 - top - bot);
                    break;
                }
                case PlotType.Table:
                {
                    double w = 1.0, h = w / TableAspect;
                    Axes.Viewport = new PlotRect(0.5 / w - 0.5 / TableAspect, 0.5 / h - 0.5, w, h);
                    break;
                }
                case PlotType.Surface3D:
                {
                    // The whole canvas. SurfaceRenderer lays out the scene, the colour bar and the
                    // captions from the canvas size directly, exactly as TableRenderer lays out its
                    // columns — there is no world window for a viewport to be a fraction of.
                    Axes.Viewport = new PlotRect(0, 0, 1, 1);
                    break;
                }
            }
        }

        // ---- Autoscale --------------------------------------------------

        public void Autoscale(bool force = false)
        {
            // FIRST, and before the Table return and the per-axis flags: a pattern trace's POINTS
            // depend on the plot-wide radial scale, so they have to be rebuilt even when no axis is
            // being autoscaled at all (a pinned window is the common case on a pattern plot).
            RefreshPolarPattern();

            // Table has no axes, and neither has the 3D surface — its framing is the camera's, and
            // it is resolved per frame from the canvas rather than stored in a window. Both leave
            // AFTER RefreshPolarPattern above, which is the call the surface actually needs.
            if (PlotType is PlotType.Table or PlotType.Surface3D) return;

            if (SupportsComplex)
            {
                if (_autoscaleMag || force) RunAutoscale("both");
            }
            else
            {
                if (_autoscaleX      || force) RunAutoscale("x");
                if (_autoscaleY      || force) RunAutoscale("y");
                if (_autoscaleRightY || force) RunAutoscale("rightY");
            }
        }

        public void RestoreAxesFromConfig(
            bool autoscaleX, bool autoscaleY, bool autoscaleRightY, bool autoscaleMag,
            PlotRect window, PlotRect windowSecondary)
        {
            _autoscaleX      = autoscaleX;
            _autoscaleY      = autoscaleY;
            _autoscaleRightY = autoscaleRightY;
            _autoscaleMag    = autoscaleMag;

            RefreshPolarPattern();   // see Autoscale — the points, not just the window

            Axes.Window          = window;
            Axes.WindowSecondary = windowSecondary;

            // Only (re)autoscale an axis whose saved window is missing/degenerate — never clobber a
            // valid saved window. A valid window has positive width AND height. This preserves the
            // user's exact saved view and prevents the empty-points Rect autoscale from scrolling
            // the trace off-screen when data resolves after paste/load.
            bool windowValid          = window.Width > 0 && window.Height > 0;
            bool windowSecondaryValid = windowSecondary.Width > 0 && windowSecondary.Height > 0;

            if (SupportsComplex)
            {
                if (!windowValid && _autoscaleMag) RunAutoscale("both");
            }
            else
            {
                if (!windowValid)          { if (_autoscaleX) RunAutoscale("x"); if (_autoscaleY) RunAutoscale("y"); }
                if (!windowSecondaryValid) { if (_autoscaleRightY) RunAutoscale("rightY"); }
            }

            Axes.WindowState          = Axes.Window;
            Axes.WindowSecondaryState = Axes.WindowSecondary;
        }

        private void RunAutoscale(string axes) => AutoscaleCore(axes);

        private void AutoscaleCore(string axes)
        {
            const double paddingComplex = 0.02;
            const double paddingRect    = 0.10;
            // Rect contour autoscales tight (zero padding) so the surface fills the plot area.
            bool hasRectContour = PlotType == PlotType.Rect && Traces.Any(t => t.IsContourTrace);
            double padX = SupportsComplex ? paddingComplex : (hasRectContour ? 0.0 : paddingRect);
            double padY = SupportsComplex ? paddingComplex : (hasRectContour ? 0.0 : paddingRect);

            var primary   = default(PlotRect);
            var secondary = default(PlotRect);
            bool primarySet   = false;
            bool secondarySet = false;

            foreach (var t in Traces)
            {
                var box = t.PathBoundingRect();
                if (box.Width <= 0 && box.Height <= 0) continue;
                if (t.UseSecondaryAxis)
                {
                    secondary    = secondarySet ? secondary.Union(box) : box;
                    secondarySet = true;
                }
                else
                {
                    primary    = primarySet ? primary.Union(box) : box;
                    primarySet = true;
                }
            }

            var defaultWindow = SupportsComplex
                ? new PlotRect(-1, -1, 2, 2)
                : new PlotRect( 0,  0, 2, 2);

            // For Rect: if no trace produced a bounding box, prefer the existing window over the
            // origin fallback (0..2) so data that arrives later doesn't render off-screen.
            // Only fall back to defaultWindow when the existing window is itself degenerate.
            if (!primarySet)
            {
                bool existingValid = !SupportsComplex && Axes.Window.Width > 0 && Axes.Window.Height > 0;
                primary = existingValid ? Axes.Window : defaultWindow;
            }
            if (!secondarySet)
            {
                bool existingValid = !SupportsComplex && Axes.WindowSecondary.Width > 0 && Axes.WindowSecondary.Height > 0;
                secondary = existingValid ? Axes.WindowSecondary : defaultWindow;
            }

            if (!SupportsComplex)
            {
                // Frequency (network) X axis is always ≥ 0, so the legacy clamp pins the window to 0.
                // Cube-bound traces plot against a swept variable (Vgs, Vds, Pin, …) that can be
                // negative, so the clamp must NOT apply there or a negative-X sweep is scrolled
                // entirely off-screen (correct X-label, no visible trace).
                bool xIsFrequency = !(Traces.Count > 0 && Traces[0].IsCubeBound);

                primary   = EnsureMinExtent(primary);
                secondary = EnsureMinExtent(secondary);
                primary   = InflateRect(primary,   primary.Width   * padX, primary.Height   * padY);
                secondary = InflateRect(secondary, secondary.Width * padX, secondary.Height * padY);

                if (xIsFrequency && primary.X < 0)
                    primary = new PlotRect(0, primary.Y, primary.Right, primary.Height);
                if (LeftAxisTraces.Count > 0)
                    secondary = new PlotRect(primary.X, secondary.Y, primary.Width, secondary.Height);
                else if (xIsFrequency && secondary.X < 0)
                    secondary = new PlotRect(0, secondary.Y, secondary.Right, secondary.Height);
            }
            else
            {
                if (UnityMinimumApplies)
                {
                    var unity = new PlotRect(-1, -1, 2, 2);
                    if (primary.Width / unity.Width > 1.05)
                    {
                        primary   = primary.Union(unity);
                        secondary = secondary.Union(unity);
                    }
                    else
                    {
                        primary   = unity;
                        secondary = unity;
                    }
                }
                else
                {
                    // Without the unity floor there is nothing else holding the window open, so a
                    // locus gets the same degeneracy guard and the same breathing room a Rect trace
                    // has always had. paddingComplex existed and was never applied to anything —
                    // the floor made it unnecessary, and it is exactly what is wanted here.
                    primary   = InflateRect(EnsureMinExtent(primary),   primary.Width   * padX, primary.Height   * padY);
                    secondary = InflateRect(EnsureMinExtent(secondary), secondary.Width * padX, secondary.Height * padY);
                }

                primary   = SquareCentredOnOrigin(primary);
                secondary = SquareCentredOnOrigin(secondary);

                // A pattern plot's disc IS the scale: the outer ring is the reference and the centre
                // is the floor, both by construction at radius 1 and 0. Framing it on the data would
                // put the peak a little inside the rim on a normalised plot and the rings would stop
                // meaning what they are labelled.
                if (IsPolarPattern)
                {
                    primary   = new PlotRect(-1, -1, 2, 2);
                    secondary = primary;
                }
            }

            double xTick  = Axes.CalcInterval(primary.Width);
            double yTick  = Axes.CalcInterval(primary.Height);
            double y2Tick = Axes.CalcInterval(secondary.Height);

            switch (axes.ToLowerInvariant())
            {
                case "x":
                    Axes.Window          = new PlotRect(primary.X,   Axes.Window.Y,          primary.Width,   Axes.Window.Height);
                    Axes.WindowSecondary = new PlotRect(secondary.X, Axes.WindowSecondary.Y, secondary.Width, Axes.WindowSecondary.Height);
                    break;
                case "y":
                    Axes.Window = new PlotRect(Axes.Window.X, RoundTo(primary.Y, yTick),   Axes.Window.Width, RoundTo(primary.Height, yTick));
                    break;
                case "righty":
                    Axes.WindowSecondary = new PlotRect(Axes.WindowSecondary.X, RoundTo(secondary.Y, y2Tick), Axes.WindowSecondary.Width, RoundTo(secondary.Height, y2Tick));
                    break;
                case "left":
                    Axes.Window = new PlotRect(primary.X, RoundTo(Axes.Window.Y, yTick), primary.Width, RoundTo(Axes.Window.Height, yTick));
                    break;
                case "right":
                    Axes.WindowSecondary = new PlotRect(secondary.X, RoundTo(secondary.Y, y2Tick), secondary.Width, RoundTo(secondary.Height, y2Tick));
                    break;
                default: // "both"
                    Axes.Window          = primary;
                    Axes.WindowSecondary = secondary;
                    break;
            }

            Axes.WindowState          = Axes.Window;
            Axes.WindowSecondaryState = Axes.WindowSecondary;
        }

        private static double RoundTo(double value, double interval) =>
            interval == 0 ? value : Math.Round(value / interval) * interval;

        private static PlotRect InflateRect(PlotRect r, double dx, double dy) =>
            new PlotRect(r.X - dx, r.Y - dy, r.Width + dx * 2, r.Height + dy * 2);

        /// <summary>
        /// Give a degenerate box enough extent to be mapped to pixels at all.
        ///
        /// <para><b>Both halves of this used to be ABSOLUTE, and that is what made an effective
        /// capacitance unplottable.</b> A 100 nF part's C_eff runs from about 1.00e-7 to 1.02e-7
        /// farads — a real 2 % variation — and a height of 2e-9 is below the old <c>1e-6</c>
        /// threshold, so it was classified as having no extent at all. The window was then replaced
        /// with a FIXED half-unit centred on the data: an axis from −0.25 to +0.25 farads with the
        /// trace as a flat line through the middle. That is exactly the reported symptom, and no
        /// amount of tick formatting could have rescued it.</para>
        ///
        /// <para><b>The degeneracy test is now relative</b>, so real variation at any scale is kept
        /// and padded normally. <b>The fallback extent stays the absolute half-unit</b> wherever
        /// that is a sensible window — which is every quantity the display drew before the passive
        /// readouts existed, so a genuinely flat decibel or magnitude trace frames exactly as it
        /// always did. Only outside the range plain notation can write at all (the same bound
        /// <see cref="EngineeringFormat.ShouldPrefix"/> uses) does it scale to the data, because
        /// ±0.25 F around a picofarad is not a window, it is a blank plot.</para>
        /// </summary>
        private static PlotRect EnsureMinExtent(PlotRect r)
        {
            var (w, x) = Extent(r.Width,  r.X, r.Right);
            var (h, y) = Extent(r.Height, r.Y, r.Bottom);
            return new PlotRect(x, y, w, h);

            static (double Size, double Origin) Extent(double size, double lo, double hi)
            {
                double scale = EngineeringFormat.AxisMagnitude(lo, hi);

                // Relative: 2 % of a hundred nanofarads is variation, not degeneracy.
                double epsilon = scale > 0 ? scale * 1e-9 : 1e-12;
                if (size > epsilon) return (size, lo);

                double fallback = EngineeringFormat.ShouldPrefix(scale) ? scale * 0.5 : 0.5;
                return (fallback, lo - fallback / 2.0);
            }
        }

        /// <summary>
        /// The smallest square, centred at the origin, that contains <paramref name="r"/>. Single
        /// source of truth for "square window" on a complex (Smith/Polar) plot type — used by
        /// <see cref="Autoscale"/> and, since brief-dd-plot-type-integrity.md §3, by the manual
        /// axis-limits dialog (<c>AxesLimitsViewModel</c>) — so a manual edit followed by an
        /// autoscale can never jump between two different notions of "square".
        /// </summary>
        internal static PlotRect SquareCentredOnOrigin(PlotRect r)
        {
            double xMin = new[] { r.Left, r.Right, -r.Left, -r.Right }.Min();
            double yMin = new[] { r.Top,  r.Bottom, -r.Top, -r.Bottom }.Min();
            double w    = 2 * Math.Abs(xMin);
            double h    = 2 * Math.Abs(yMin);
            double s    = Math.Max(w, h);
            if (w > h) yMin -= (w - h) / 2;
            else       xMin -= (h - w) / 2;
            return new PlotRect(xMin, yMin, s, s);
        }

        // ---- Plot-type switching ----------------------------------------

        public void SetPlotType(PlotType newType)
        {
            if (newType == PlotType) return;

            PlotType oldType = PlotType;
            PlotType = newType;

            // Leaving Table: narrow deletion to what genuinely cannot exist on another plot type
            // (brief-dd-plot-type-integrity.md §1). Everything else survives and is remapped below —
            // this used to clear _plot.Traces wholesale (at the VM layer), taking ordinary traces
            // with it.
            if (oldType == PlotType.Table && newType != PlotType.Table)
            {
                for (int i = _traces.Count - 1; i >= 0; i--)
                {
                    var t = _traces[i];
                    if (t.IsSummaryColumn || (t.IsCubeBound && t.CubeIsScalar))
                        _traces.RemoveAt(i);
                }
            }

            // Network/derived-trace plot-type mutation — unchanged from before this brief except that
            // a network trace's YAxis Phase/Real/Imaginary is now remapped to Complex on entering
            // Smith/Polar instead of being deleted (§1 anchor 3), and MaxGain is no longer deleted
            // either (it joins K/|Δ|/Passivity, which already survived — none of the four have a
            // Γ-plane locus, so they go dormant rather than vanish; the reverse switch restores them).
            // Table on EITHER side is a pure no-op here ("Anything ↔ Table: no transform change at
            // all" — a Table renders complex and scalar cells alike); cube-bound traces are instead
            // handled uniformly below by Trace.RemapForPlotType, regardless of this switch.
            if (oldType != PlotType.Table && newType != PlotType.Table)
            {
                switch (newType)
                {
                    case PlotType.Smith:
                    case PlotType.Polar:
                        if (!oldType.IsComplex())
                        {
                            foreach (var t in _traces)
                            {
                                if (t.IsCubeBound) continue;
                                if (!t.IsDerived) t.YAxis = DependentVarFormat.Complex;
                                if (t.Derived == DerivedParameters.MuPrime) t.Derived = DerivedParameters.SourceStabilityCircle;
                                if (t.Derived == DerivedParameters.Mu) t.Derived = DerivedParameters.LoadStabilityCircle;
                                t.UseSecondaryAxis = false;
                            }
                        }
                        break;

                    case PlotType.Rect:
                        if (oldType.IsComplex())
                        {
                            var originals = _traces.ToList();
                            int added = 0;
                            for (int idx = 0; idx < originals.Count; idx++)
                            {
                                var t = originals[idx];
                                if (t.IsCubeBound) continue;
                                if (t.YAxis == DependentVarFormat.Complex && !t.IsDerived)
                                {
                                    var phaseTrace = new Trace(t, incrementColorBy: originals.Count, false)
                                    {
                                        YAxis            = DependentVarFormat.Phase,
                                        UseSecondaryAxis = true
                                    };
                                    t.YAxis = DependentVarFormat.Db;
                                    _traces.Insert(idx + 1 + added, phaseTrace);
                                    added++;
                                }
                                else if (t.YAxis == DependentVarFormat.Phase) t.UseSecondaryAxis = true;
                                else if (t.Derived == DerivedParameters.LoadStabilityCircle) t.Derived = DerivedParameters.Mu;
                                else if (t.Derived == DerivedParameters.SourceStabilityCircle) t.Derived = DerivedParameters.MuPrime;
                            }
                        }
                        break;
                }
            }

            if (newType is PlotType.Smith or PlotType.Polar)
                foreach (var t in _traces)
                    foreach (var mk in t.Markers)
                    { mk.IsMulti = false; mk.IsDelta = false; }

            // Cube-bound remap — single source of truth for what a plot-type change does to a cube
            // trace's Transform/Expression. No-op for network/derived/contour traces and whenever
            // Table is on either side (handled above).
            foreach (var t in _traces)
                t.RemapForPlotType(oldType, newType);

            foreach (var t in _traces) t.BuildPath(PlotType, FreqUnits);

            // Each plot type keeps its own axes, so switching type swaps the whole Axes object —
            // and a type visited for the first time gets a brand-new one. Panning locked/unlocked
            // is a property of the PLOT the user is working with, not of the type they happen to be
            // viewing it as, so it is carried across rather than silently reverting to the Axes
            // default (which would leave a locked plot suddenly undraggable after a type change).
            bool lockedPanning = Axes.LockedPanning;
            if (_axesStorage.TryGetValue(newType, out var saved))
                Axes = saved;
            else
            {
                Axes = new Axes();
                _axesStorage[newType] = Axes;
            }
            Axes.LockedPanning = lockedPanning;

            Axes.ShowSecondary = NeedsSecondary;
            SetAxesViewport();
            Autoscale(force: true);
        }

        // ---- Nearest-trace search (delegate to Trace) -------------------

        public (Trace? Trace, int FreqIndex, double Distance, System.Numerics.Vector2 NearestPoint)?
            FindNearestTraceData(System.Numerics.Vector2 queryPoint)
        {
            double best = double.PositiveInfinity;
            (Trace?, int, double, System.Numerics.Vector2)? result = null;

            foreach (var t in Traces)
            {
                var r = t.FindNearestTraceData(queryPoint);
                if (r.HasValue && r.Value.Distance < best)
                {
                    best   = r.Value.Distance;
                    result = (t, r.Value.FreqIndex, r.Value.Distance, r.Value.NearestPoint);
                }
            }
            return result;
        }

        // ---- Copy data as tab-delimited text ----------------------------

        public string CopyDataString(double[]? freq = null, string fmt = "G12")
        {
            var allFreq = freq
                ?? Traces.SelectMany(t => t.Data.Frequencies)
                         .Distinct().OrderBy(f => f).ToArray();

            var sb = new StringBuilder();
            sb.Append($"freq ({FreqUnits})");
            foreach (var t in Traces) sb.Append($"\t{t.Description}");
            sb.AppendLine();

            foreach (var f in allFreq)
            {
                sb.Append((f * FreqUnits.Scale()).ToString(fmt));
                foreach (var t in Traces)
                    sb.Append('\t' + t.DataPoint(f).ToString());
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ---- Equality ---------------------------------------------------

        public override bool Equals(object? obj) => obj is Plot p && p.Id == Id;
        public override int  GetHashCode()        => Id.GetHashCode();
    }
}
