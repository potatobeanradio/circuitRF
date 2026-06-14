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
        public string CustomXLabel   { get; set; } = "";
        public bool   CustomXLabelOn { get; set; } = false;
        public string CustomYLabel   { get; set; } = "";
        public bool   CustomYLabelOn { get; set; } = false;
        public string CustomY2Label  { get; set; } = "";
        public bool   CustomY2LabelOn{ get; set; } = false;

        public string Title   => CustomTitleOn  ? CustomTitle  : "";
        public string YLabel  => CustomYLabelOn  ? CustomYLabel  : "";
        public string Y2Label => CustomY2LabelOn ? CustomY2Label : "";

        public string XLabel
        {
            get
            {
                if (CustomXLabelOn) return CustomXLabel;

                string unit = FreqUnits.Description();

                if (Traces.Count == 0 || !SupportsComplex)
                    return $"freq ({unit})";
                string min = (FreqUnits.Scale() * Traces[0].MinFreq)
                    .ToString($"G{Axes.NumDigitsXAxis}");
                string max = (FreqUnits.Scale() * Traces[0].MaxFreq)
                    .ToString($"G{Axes.NumDigitsXAxis}");
                return $"freq ({min} to {max} {unit})";
            }
        }

        // ---- Table view -------------------------------------------------

        public double         ColumnWidth                 { get; set; } = 115;
        public bool           TableViewAscendingSortOrder { get; set; } = true;
        public int            TableViewScrollIndex        { get; set; } = 0;
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

            Autoscale();

            Axes.WindowState          = Axes.Window;
            Axes.WindowSecondaryState = Axes.WindowSecondary;
        }

        private void RunAutoscale(string axes) => AutoscaleCore(axes);

        private void AutoscaleCore(string axes)
        {
            const double paddingComplex = 0.02;
            const double paddingRect    = 0.10;
            double padX = SupportsComplex ? paddingComplex : paddingRect;
            double padY = SupportsComplex ? paddingComplex : paddingRect;

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

            if (!primarySet)   primary   = defaultWindow;
            if (!secondarySet) secondary = defaultWindow;

            if (!SupportsComplex)
            {
                primary   = EnsureMinExtent(primary);
                secondary = EnsureMinExtent(secondary);
                primary   = InflateRect(primary,   primary.Width   * padX, primary.Height   * padY);
                secondary = InflateRect(secondary, secondary.Width * padX, secondary.Height * padY);

                if (primary.X < 0)
                    primary = new Rect(0, primary.Y, primary.Right, primary.Height);
                if (LeftAxisTraces.Count > 0)
                    secondary = new Rect(primary.X, secondary.Y, primary.Width, secondary.Height);
                else if (secondary.X < 0)
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

        private static Rect SquareCentredOnOrigin(Rect r)
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

            switch (newType)
            {
                case PlotType.Smith:
                case PlotType.Polar:
                    if (!oldType.IsComplex())
                    {
                        for (int i = _traces.Count - 1; i >= 0; i--)
                        {
                            var t = _traces[i];
                            if (t.YAxis == DependentVarFormat.Phase     ||
                                t.YAxis == DependentVarFormat.Real      ||
                                t.YAxis == DependentVarFormat.Imaginary ||
                                t.Derived == DerivedParameters.MaxGain)
                                _traces.RemoveAt(i);
                        }

                        foreach (var t in _traces)
                        {
                            if (!t.IsDerived) t.YAxis = DependentVarFormat.Complex;
                            if (t.Derived == DerivedParameters.MuPrime) t.Derived = DerivedParameters.SourceStabilityCircle;
                            if (t.Derived == DerivedParameters.Mu) t.Derived = DerivedParameters.LoadStabilityCircle;
                            t.UseSecondaryAxis = false;
                        }
                    }

                    foreach (var t in _traces)
                        foreach (var mk in t.Markers)
                        { mk.IsMulti = false; mk.IsDelta = false; }
                    break;

                case PlotType.Rect:
                    if (oldType.IsComplex())
                    {
                        var originals = _traces.ToList();
                        int added = 0;
                        for (int idx = 0; idx < originals.Count; idx++)
                        {
                            var t = originals[idx];
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

                case PlotType.Table:
                    break;
            }

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
