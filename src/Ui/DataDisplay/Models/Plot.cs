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
using Avalonia;

namespace CircuitRF.Ui.DataDisplay
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

    public enum PlotType { Smith, Polar, Rect, Table }

    public static class PlotTypeExtensions
    {
        public static string Description(this PlotType t) => t switch
        {
            PlotType.Smith => "Smith Chart",
            PlotType.Polar => "Polar Plot",
            PlotType.Rect  => "Rectangular Plot",
            PlotType.Table  => "Table",
            _              => t.ToString()
        };
        public static bool IsRect   (this PlotType t) => t == PlotType.Rect;
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

        public bool AutoscaleEnforceUnityMinimum { get; set; } = true;

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

        public Rect   Viewport     => Axes.Viewport;
        public double AspectRatio  => Axes.Viewport.Width / Axes.Viewport.Height;

        // ---- Constructors -----------------------------------------------

        public Plot()
        {
            PlotType = PlotType.Smith;
            _traces.CollectionChanged += OnTracesChanged;
        }

        public Plot(PlotType plotType, FreqUnit freqUnits)
        {
            PlotType   = plotType;
            _freqUnits = freqUnits;
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

        // ---- Axes limit setters -----------------------------------------

        public void SetAxesWindow(Rect window, bool secondary = false)
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

            var w = new Rect(Math.Min(xMin, xMax), Math.Min(yMin, yMax),
                             Math.Abs(xMax - xMin), Math.Abs(yMax - yMin));
            if (useSecondary) Axes.WindowSecondary = w;
            else              Axes.Window          = w;
            Axes.WindowState          = Axes.Window;
            Axes.WindowSecondaryState = Axes.WindowSecondary;
        }

        public void SetAxesToUnity(string which = "both")
        {
            var unity = new Rect(-1, -1, 2, 2);
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
                    Axes.Viewport = new Rect(0.5 / w - 0.5, 0.5 / h - 0.5, w, h);
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
                    Axes.Viewport = new Rect(left, top, 1.0 - left - right, 1.0 - top - bot);
                    break;
                }
                case PlotType.Table:
                {
                    double w = 1.0, h = w / TableAspect;
                    Axes.Viewport = new Rect(0.5 / w - 0.5 / TableAspect, 0.5 / h - 0.5, w, h);
                    break;
                }
            }
        }

        // ---- Autoscale --------------------------------------------------

        public void Autoscale(bool force = false)
        {
            if (PlotType == PlotType.Table) return;

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
            Rect window, Rect windowSecondary)
        {
            _autoscaleX      = autoscaleX;
            _autoscaleY      = autoscaleY;
            _autoscaleRightY = autoscaleRightY;
            _autoscaleMag    = autoscaleMag;

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

            var primary   = default(Rect);
            var secondary = default(Rect);
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
                ? new Rect(-1, -1, 2, 2)
                : new Rect( 0,  0, 2, 2);

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
                    primary = new Rect(0, primary.Y, primary.Right, primary.Height);
                if (LeftAxisTraces.Count > 0)
                    secondary = new Rect(primary.X, secondary.Y, primary.Width, secondary.Height);
                else if (xIsFrequency && secondary.X < 0)
                    secondary = new Rect(0, secondary.Y, secondary.Right, secondary.Height);
            }
            else
            {
                if (AutoscaleEnforceUnityMinimum)
                {
                    var unity = new Rect(-1, -1, 2, 2);
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

                primary   = SquareCentredOnOrigin(primary);
                secondary = SquareCentredOnOrigin(secondary);
            }

            double xTick  = Axes.CalcInterval(primary.Width);
            double yTick  = Axes.CalcInterval(primary.Height);
            double y2Tick = Axes.CalcInterval(secondary.Height);

            switch (axes.ToLowerInvariant())
            {
                case "x":
                    Axes.Window          = new Rect(primary.X,   Axes.Window.Y,          primary.Width,   Axes.Window.Height);
                    Axes.WindowSecondary = new Rect(secondary.X, Axes.WindowSecondary.Y, secondary.Width, Axes.WindowSecondary.Height);
                    break;
                case "y":
                    Axes.Window = new Rect(Axes.Window.X, RoundTo(primary.Y, yTick),   Axes.Window.Width, RoundTo(primary.Height, yTick));
                    break;
                case "righty":
                    Axes.WindowSecondary = new Rect(Axes.WindowSecondary.X, RoundTo(secondary.Y, y2Tick), Axes.WindowSecondary.Width, RoundTo(secondary.Height, y2Tick));
                    break;
                case "left":
                    Axes.Window = new Rect(primary.X, RoundTo(Axes.Window.Y, yTick), primary.Width, RoundTo(Axes.Window.Height, yTick));
                    break;
                case "right":
                    Axes.WindowSecondary = new Rect(secondary.X, RoundTo(secondary.Y, y2Tick), secondary.Width, RoundTo(secondary.Height, y2Tick));
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

        private static Rect InflateRect(Rect r, double dx, double dy) =>
            new Rect(r.X - dx, r.Y - dy, r.Width + dx * 2, r.Height + dy * 2);

        private static Rect EnsureMinExtent(Rect r)
        {
            double w = r.Width  < 1e-6 ? 0.5 : r.Width;
            double h = r.Height < 1e-6 ? 0.5 : r.Height;
            double x = r.Width  < 1e-6 ? r.X - 0.25 : r.X;
            double y = r.Height < 1e-6 ? r.Y - 0.25 : r.Y;
            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// The smallest square, centred at the origin, that contains <paramref name="r"/>. Single
        /// source of truth for "square window" on a complex (Smith/Polar) plot type — used by
        /// <see cref="Autoscale"/> and, since brief-dd-plot-type-integrity.md §3, by the manual
        /// axis-limits dialog (<c>AxesLimitsViewModel</c>) — so a manual edit followed by an
        /// autoscale can never jump between two different notions of "square".
        /// </summary>
        internal static Rect SquareCentredOnOrigin(Rect r)
        {
            double xMin = new[] { r.Left, r.Right, -r.Left, -r.Right }.Min();
            double yMin = new[] { r.Top,  r.Bottom, -r.Top, -r.Bottom }.Min();
            double w    = 2 * Math.Abs(xMin);
            double h    = 2 * Math.Abs(yMin);
            double s    = Math.Max(w, h);
            if (w > h) yMin -= (w - h) / 2;
            else       xMin -= (h - w) / 2;
            return new Rect(xMin, yMin, s, s);
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

            if (_axesStorage.TryGetValue(newType, out var saved))
                Axes = saved;
            else
            {
                Axes = new Axes();
                _axesStorage[newType] = Axes;
            }

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
