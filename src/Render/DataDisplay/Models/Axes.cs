// ================================================================
//  Axes.cs  —  Axis state and tick calculation  (pure model, no drawing)
//
//  Ported from splotRF/src/Models/Axes.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    // ============================================================
    //  AxesColor  —  per-axis color overrides
    // ============================================================

    public class AxesColor
    {
        public SKColor YAxisColor  { get; set; } = TraceProperties.ColorLUT[0];   // Black
        public SKColor Y2AxisColor { get; set; } = TraceProperties.ColorLUT[0];   // Black

        public void SetColor(int index, SKColor color)
        {
            if      (index == 0) YAxisColor  = color;
            else if (index == 1) Y2AxisColor = color;
        }
    }

    // ============================================================
    //  AxisScale  —  how an axis maps world to canvas
    // ============================================================

    /// <summary>
    /// The X axis's mapping. <see cref="Linear"/> is the affine map the display has always had;
    /// <see cref="Log"/> is base-10 logarithmic, for the decade-spanning sweeps circuitRF's own EM
    /// kernel writes by default (a <c>.cem</c> whose <c>Frequency.Kind</c> is <c>Log</c>).
    ///
    /// <para><b>It is an AXIS property, never a trace one.</b> Two traces on one axis cannot
    /// disagree about its scale.</para>
    ///
    /// <para><b>X only, and Rect only.</b> The Y axis is already logarithmic where it matters
    /// (<c>YAxis: Db</c>), and a Smith or Polar X carries Re(Γ), which is signed —
    /// <c>PlotRenderer.BuildTransforms</c> engages the log map on Rect alone, so no other plot kind
    /// can reach it even if an <see cref="Axes"/> were handed the mode directly.</para>
    /// </summary>
    public enum AxisScale { Linear, Log }

    // ============================================================
    //  TickSet  —  pre-calculated tick positions in world coords
    // ============================================================

    public struct TickSet
    {
        public List<double>                             MajorX;
        public List<(double Primary, double Secondary)> MajorY;
        public List<double>                             MinorX;
        public List<double>                             MinorY;
        public List<double>                             MinorY2;
    }

    // ============================================================
    //  Axes
    // ============================================================

    public class Axes
    {
        // ---- Minor tick spacing -----------------------------------------

        private double _xTick = 6;
        public double XTick
        {
            get => _xTick;
            set => _xTick = value == 0 ? _xTick : value;
        }

        private double _yTick = 6;
        public double YTick
        {
            get => _yTick;
            set => _yTick = value == 0 ? _yTick : value;
        }

        private double _y2Tick = 10;
        public double Y2Tick
        {
            get => _y2Tick;
            set => _y2Tick = value == 0 ? _y2Tick : value;
        }

        // ---- Major-tick multipliers -------------------------------------

        /// <summary>One major vertical grid line per MajorX minor ticks.</summary>
        public double MajorX { get; set; } = 2;

        /// <summary>One major horizontal grid line per MajorY minor ticks.</summary>
        public double MajorY { get; set; } = 2;

        // ---- Panning ----------------------------------------------------

        public bool LockedPanning { get; set; } = false;

        // ---- Equal scale ------------------------------------------------

        /// <summary>
        /// A rectangular plot draws one world unit as the same number of pixels along X and Y (brief NZ-8): the plot box is
        /// narrowed, centred, in whichever direction has the more pixels per unit, so a circle of error is a circle on a
        /// constellation. The window is untouched — the ticks are the ones the window gives. Off by default; ignored on a
        /// log axis, where X and Y are not in the same units.
        /// </summary>
        public bool EqualAspect { get; set; }

        // ---- Appearance scalars (read by renderers) ----------------------

        public double Ticksize                { get; set; } = 0.015;
        public double MinorTransparencyScale  { get; set; } = 0.5;
        public double TickThicknessFactor     { get; set; } = 0.5;
        public double GridThicknessFactor     { get; set; } = 0.5;
        public bool   Box                     { get; set; } = false;

        // ---- Label formatting (read by renderers) -----------------------

        public double FontSizeTicks   { get; set; } = 6;
        public double FontSizeLabel   { get; set; } = 8;
        public int    NumDigitsLeftY  { get; set; } = 3;
        public int    NumDigitsRightY { get; set; } = 3;
        public int    NumDigitsXAxis  { get; set; } = 5;

        // ---- Derived tick lengths in world coordinates ------------------

        public double TickLengthX => Ticksize * Window.Width;
        public double TickLengthY => Ticksize * Window.Height;

        // ---- Drag-start snapshots for panning ---------------------------

        public PlotRect WindowState          { get; set; } = default;
        public PlotRect WindowSecondaryState { get; set; } = default;

        // ---- X axis scale ------------------------------------------------

        private AxisScale _xScale = AxisScale.Linear;

        /// <summary>
        /// Linear (the default, and every axis this display has ever drawn) or base-10 Log.
        /// Assigning it re-runs the window through its own setter, because the two modes disagree
        /// about what a legal window is — see <see cref="Window"/>.
        /// </summary>
        public AxisScale XScale
        {
            get => _xScale;
            set
            {
                if (_xScale == value) return;
                _xScale = value;
                Window          = _window;            // re-runs the mode-dependent repair below
                WindowSecondary = _windowSecondary;
            }
        }

        // ---- Left-Y axis scale --------------------------------------------

        private AxisScale _yScale = AxisScale.Linear;

        /// <summary>
        /// Linear (the default) or base-10 Log for the PRIMARY (left) Y axis — a probability that spans
        /// decades (a CCDF) is unreadable on a linear one. The secondary Y axis stays linear, and a log
        /// left axis does not drive it through a shared grid. Not persisted: it is set by code that
        /// builds a plot for one purpose, not yet by the Axes Limits flyout.
        /// </summary>
        public AxisScale YScale
        {
            get => _yScale;
            set
            {
                if (_yScale == value) return;
                _yScale = value;
                Window = _window;                    // re-runs the mode-dependent repair below
            }
        }

        /// <summary>The Y analogue of <see cref="RepairLogWindow"/>: a positive bottom edge four decades
        /// below the top when the window carries none.</summary>
        internal static PlotRect RepairLogWindowY(PlotRect w)
        {
            if (w.Y > 0 && w.Height > 0) return w;
            double top = w.Y + w.Height;
            if (!(top > 0) || !double.IsFinite(top)) return new PlotRect(w.X, 1, w.Width, 9);
            double bottom = top * 1e-4;
            return new PlotRect(w.X, bottom, w.Width, top - bottom);
        }

        // ---- Primary window (world coordinates) -------------------------

        private PlotRect _window = new PlotRect(-50, -50, 150, 150);

        /// <summary>
        /// Primary axis world-coordinate window (freq × left-Y).
        /// Recalculates tick intervals on every assignment.
        ///
        /// <para><b>The two modes repair a left edge differently, and the linear arithmetic is
        /// untouched.</b> On a LINEAR axis a left edge of exactly 0 is nudged to −1e-6 so that the
        /// zero grid line renders correctly — a cosmetic nudge, and the behaviour every existing
        /// document depends on. On a LOG axis that nudge produces the one point the map has no
        /// answer for, and it arrives by DEFAULT: an autoscaled frequency window literally begins
        /// at −1e-6. So the log path never reaches it; see <see cref="RepairLogWindow"/>.</para>
        /// </summary>
        public PlotRect Window
        {
            get => _window;
            set
            {
                if (_xScale == AxisScale.Log)
                    value = RepairLogWindow(value);
                else if (value.X == 0)
                    value = new PlotRect(-1e-6, value.Y, value.Width + 1e-6, value.Height);
                if (_yScale == AxisScale.Log)
                    value = RepairLogWindowY(value);
                _window = value;
                SetTicks();
            }
        }

        // ---- Secondary (right Y) window ---------------------------------

        private PlotRect _windowSecondary = new PlotRect(0, 0, 150, 150);

        public PlotRect WindowSecondary
        {
            get => _windowSecondary;
            set
            {
                if (_xScale == AxisScale.Log)
                    value = RepairLogWindow(value);
                else if (value.X == 0)
                    value = new PlotRect(-1e-6, value.Y, value.Width + 1e-6, value.Height);
                _windowSecondary = value;
            }
        }

        /// <summary>
        /// A window whose X extent a base-10 log map can actually draw.
        ///
        /// <para>This is a BACKSTOP, not the normal path: a log autoscale frames enclosing decades,
        /// a log pan and a log zoom are multiplicative and cannot walk a positive edge down to zero,
        /// and the Axes Limits flyout refuses a non-positive minimum rather than coercing one. What
        /// reaches it is a window authored while the axis was LINEAR — the moment the user flips the
        /// mode — plus any caller that assigns a window directly.</para>
        ///
        /// <para>The RIGHT edge is kept and the left is placed four decades below it, because the
        /// right edge is the half a linear frequency window always has right; when the right edge is
        /// not positive either the window carries no positive extent at all and a single decade
        /// 1…10 is substituted, which is drawable and visibly wrong rather than silently blank.</para>
        /// </summary>
        internal static PlotRect RepairLogWindow(PlotRect w)
        {
            if (w.X > 0 && w.Width > 0) return w;
            double right = w.X + w.Width;
            if (!(right > 0) || !double.IsFinite(right)) return new PlotRect(1, w.Y, 9, w.Height);
            double left = right * 1e-4;
            return new PlotRect(left, w.Y, right - left, w.Height);
        }

        /// <summary>
        /// The world X that sits <paramref name="factor"/> tick-lengths from <paramref name="x"/>
        /// along the axis — the Y-axis tick nubs' inboard ends, which are the one place the grid
        /// renderer measures a distance ALONG X rather than mapping a point.
        ///
        /// <para>On a linear axis this is exactly <c>x + TickLengthX * factor</c>, written that way
        /// so the existing expressions keep their bit pattern (multiplying by 1.0 and by 0.5, and
        /// adding a negated value, are all exact). On a log axis a tick nub is a fraction of the
        /// axis's LENGTH IN DECADES, which is a ratio, not a difference.</para>
        /// </summary>
        public double XPlusTickLength(double x, double factor)
        {
            if (_xScale != AxisScale.Log) return x + TickLengthX * factor;
            double right = Window.Right;
            if (!(x > 0) || !(Window.Left > 0) || !(right > Window.Left)) return x;
            double decades = Math.Log10(right) - Math.Log10(Window.Left);
            return x * Math.Pow(10.0, Ticksize * decades * factor);
        }

        /// <summary>The world Y that sits <paramref name="factor"/> tick-lengths from <paramref name="y"/> —
        /// <see cref="XPlusTickLength"/>'s twin for the X ticks' nubs. Linear: exactly the old expression.</summary>
        public double YPlusTickLength(double y, double factor)
        {
            if (_yScale != AxisScale.Log) return y + TickLengthY * factor;
            double top = Window.Bottom;
            if (!(y > 0) || !(Window.Top > 0) || !(top > Window.Top)) return y;
            double decades = Math.Log10(top) - Math.Log10(Window.Top);
            return y * Math.Pow(10.0, Ticksize * decades * factor);
        }

        public bool SecondaryShareGrid { get; set; } = true;
        public bool ShowSecondary      { get; set; } = false;

        // ---- Viewport (fractional sub-region of the canvas) -------------

        public PlotRect Viewport { get; set; } = new PlotRect(0.15, 0.05, 0.80, 0.88);

        // ---- Constructors -----------------------------------------------

        public Axes() { }

        /// <summary>Deep-copy constructor.</summary>
        public Axes(Axes src)
        {
            // FIRST, and it has to be: Window's setter repairs its value differently in each mode,
            // so a copy that took the windows before the mode would run a log window through the
            // linear zero-nudge.
            _xScale                = src._xScale;
            _yScale                = src._yScale;
            Box                    = src.Box;
            FontSizeLabel          = src.FontSizeLabel;
            FontSizeTicks          = src.FontSizeTicks;
            NumDigitsLeftY         = src.NumDigitsLeftY;
            NumDigitsRightY        = src.NumDigitsRightY;
            NumDigitsXAxis         = src.NumDigitsXAxis;
            Window                 = src.Window;
            WindowSecondary        = src.WindowSecondary;
            Viewport               = src.Viewport;
            WindowState            = src.WindowState;
            WindowSecondaryState   = src.WindowSecondaryState;
            LockedPanning          = src.LockedPanning;
            EqualAspect            = src.EqualAspect;
            XTick                  = src.XTick;
            YTick                  = src.YTick;
            Y2Tick                 = src.Y2Tick;
            ShowSecondary          = src.ShowSecondary;
            SecondaryShareGrid     = src.SecondaryShareGrid;
            GridThicknessFactor    = src.GridThicknessFactor;
            TickThicknessFactor    = src.TickThicknessFactor;
            MinorTransparencyScale = src.MinorTransparencyScale;
            Ticksize               = src.Ticksize;
            MajorX                 = src.MajorX;
            MajorY                 = src.MajorY;
        }

        // ---- Panning helpers --------------------------------------------

        /// <summary>
        /// Pans the primary window by a world delta.
        ///
        /// <para><b>On a log axis <paramref name="dx"/> is a number of DECADES, not a frequency
        /// difference</b>, and the window slides multiplicatively. That is not a special case bolted
        /// on: the only caller that produces a world dx is
        /// <see cref="TranslateFromPointer"/>, which divides a pixel delta by the axis's own
        /// <c>XScale</c> — and in log mode that scale is px per decade, so the quotient is already
        /// in the right units. A difference applied to a log axis walks the left edge through zero
        /// and out the other side.</para>
        /// </summary>
        public void Translate(double dx, double dy)
        {
            if (LockedPanning) return;
            if (_xScale == AxisScale.Log)
            {
                double f = Math.Pow(10.0, -dx);
                Window = new PlotRect(WindowState.X * f, WindowState.Y - dy,
                                      WindowState.Width * f, Window.Height);
                return;
            }
            Window = new PlotRect(WindowState.X - dx, WindowState.Y - dy,
                              Window.Width, Window.Height);
        }

        public void TranslateSecondary(double dx, double dy)
        {
            if (LockedPanning) return;
            if (_xScale == AxisScale.Log)
            {
                double f = Math.Pow(10.0, -dx);
                WindowSecondary = new PlotRect(
                    WindowSecondaryState.X * f,
                    WindowSecondaryState.Y - dy,
                    WindowSecondaryState.Width * f,
                    WindowSecondary.Height);
                return;
            }
            WindowSecondary = new PlotRect(
                WindowSecondaryState.X - dx,
                WindowSecondaryState.Y - dy,
                WindowSecondary.Width,
                WindowSecondary.Height);
        }

        /// <summary>
        /// Pans BOTH windows by a pointer delta measured in canvas pixels, converting that delta
        /// through EACH axis's own world→canvas scale. Call once per pointer-move with the deltas
        /// accumulated since <see cref="WindowState"/>/<see cref="WindowSecondaryState"/> were
        /// snapshotted; a no-op while <see cref="LockedPanning"/> is set.
        /// </summary>
        /// <remarks>
        /// <b>The two axes do not share a scale.</b> A drag of N pixels is a different world
        /// distance on the left axis than on the right one whenever their windows differ — which is
        /// the normal case, not an edge case: Match Designer's magnitude plot puts |S11| and |S21|
        /// on separate dB ranges, and its phase plot puts degrees against group delay. Converting
        /// the pointer delta once, with the PRIMARY scale, and applying that world number to the
        /// secondary window is what made right-axis traces shear away from the pointer and off the
        /// plot as soon as the user turned Lock Axes Panning off (owner, 2026-08-21). Both
        /// conversions live here so a caller cannot pass one axis's scale to the other again.
        ///
        /// <para><b>The delta is quantized to whole canvas pixels, and that is load-bearing</b>
        /// (owner, 2026-08-21: <i>"as I pan left or right in an axis, the y-axis and right y-axis
        /// numbers and the ticks wiggle/glitch slightly … same with x-axis when I pan up or
        /// down"</i>). A pointer delta is a fractional number of pixels, and nobody drags along an
        /// exact axis: a horizontal drag still carries a few tenths of a pixel of Y. Unrounded,
        /// that repainted the entire Y tick column at a new sub-pixel phase on EVERY pointer event
        /// — measured at ~700 changed pixels on the left axis and ~900 on the right for jitter
        /// under half a pixel — which is the shimmer. Rounding fixes both halves at once:</para>
        /// <list type="bullet">
        /// <item>the orthogonal axis stops moving at all until the pointer has crossed a whole
        /// pixel, so an axis-aligned drag leaves it pixel-identical; and</item>
        /// <item>the axis being panned translates by an EXACT integer — a tick's canvas position
        /// works out to <c>before + delta</c> exactly, since the offset term absorbs the shift —
        /// so every glyph keeps its sub-pixel phase and simply slides.</item>
        /// </list>
        /// <para>The right axis was the worse of the two because <see cref="SecondaryShareGrid"/>
        /// derives its tick VALUES from <c>(y − Window.Top) / Window.Height</c>: a sub-pixel change
        /// in <c>Window.Top</c> re-numbers it, rather than merely re-placing it.</para>
        /// <para>Rounding the accumulated drag-start delta (not a per-event increment) is what
        /// keeps this a clean staircase with no accumulated drift.</para>
        /// </remarks>
        public void TranslateFromPointer(
            double dxPx,           double dyPx,
            double primaryXScale,  double primaryYScale,
            double secondaryXScale, double secondaryYScale)
        {
            dxPx = Math.Round(dxPx);
            dyPx = Math.Round(dyPx);

            Translate(dxPx / primaryXScale, dyPx / primaryYScale);
            if (ShowSecondary)
                TranslateSecondary(dxPx / secondaryXScale, dyPx / secondaryYScale);
        }

        /// <summary>
        /// The right-button drag, which pans the SECONDARY Y axis alone. Same whole-pixel
        /// quantization as <see cref="TranslateFromPointer"/>, for the same reason — the right
        /// axis's own numbers shimmer under a sub-pixel delta just as readily.
        /// </summary>
        public void TranslateSecondaryFromPointer(double dyPx, double secondaryYScale)
            => TranslateSecondary(0, Math.Round(dyPx) / secondaryYScale);

        // ---- Tick interval calculation ----------------------------------

        /// <summary>
        /// Returns a nice minor-tick interval for the given data range,
        /// targeting roughly 10–20 ticks across the range.
        /// </summary>
        public double CalcInterval(double range)
        {
            if (range <= 0) return 1;
            double x = Math.Pow(10.0, Math.Floor(Math.Log10(range)));
            if (range / (x / 2.0) >= 10) return x / 2.0;
            if (range / (x / 5.0) >= 10) return x / 5.0;
            return x / 10.0;
        }

        private void SetTicks()
        {
            XTick  = CalcInterval(Window.Width);
            YTick  = CalcInterval(Window.Height);
            Y2Tick = CalcInterval(WindowSecondary.Height);

            double numXTicks = Math.Round(Window.Width / XTick);
            int    wInt      = (int)Window.Width;

            MajorX = wInt switch
            {
                0 => numXTicks < 15 ? 2 : 5,
                1 => 2,
                2 => 2,
                3 => 5,
                4 => numXTicks < 15 ? 2 : 5,
                5 => numXTicks < 15 ? 2 : 5,
                _ => 2
            };
        }

        // ---- Tick position calculation ----------------------------------

        /// <summary>
        /// Returns all tick positions in world coordinates for both axes.
        /// </summary>
        public TickSet Ticks(bool minorTicks)
        {
            int majX = MajorStep(MajorX);
            int majY = MajorStep(MajorY);

            double dmx = XTick * majX;
            double dmy = YTick * majY;
            if (dmx <= 0) dmx = 0.05;
            if (dmy <= 0) dmy = 0.05;

            var majorXTicks = _xScale == AxisScale.Log
                ? LogLattice(Window.Left, Window.Right, major: true)
                : Lattice(Window.Left, Window.Right,  dmx);
            var majorYTicks = _yScale == AxisScale.Log
                ? LogLattice(Window.Top, Window.Bottom, major: true)
                : Lattice(Window.Top,  Window.Bottom, dmy);

            // Secondary Y major ticks
            var majorY2Ticks = new List<double>();
            if (SecondaryShareGrid)
            {
                foreach (var y in majorYTicks)
                {
                    double frac = (y - Window.Top) / Window.Height;
                    majorY2Ticks.Add(WindowSecondary.Top + frac * WindowSecondary.Height);
                }
            }
            else
            {
                double dmy2 = Y2Tick * majY;
                if (dmy2 <= 0) dmy2 = 0.05;
                majorY2Ticks = Lattice(WindowSecondary.Top, WindowSecondary.Bottom, dmy2);
            }

            // Ensure equal-length lists before Zip
            while (majorY2Ticks.Count < majorYTicks.Count)  majorY2Ticks.Add(double.NaN);
            while (majorYTicks.Count  < majorY2Ticks.Count) majorYTicks.Add(double.NaN);

            var minorX  = new List<double>();
            var minorY  = new List<double>();
            var minorY2 = new List<double>();

            if (minorTicks)
            {
                minorX = _xScale == AxisScale.Log
                    ? LogLattice(Window.Left, Window.Right, major: false)
                    : Lattice(Window.Left, Window.Right,  XTick, skipEvery: majX);
                minorY = _yScale == AxisScale.Log
                    ? LogLattice(Window.Top, Window.Bottom, major: false)
                    : Lattice(Window.Top,  Window.Bottom, YTick, skipEvery: majY);

                if (ShowSecondary)
                {
                    if (SecondaryShareGrid)
                    {
                        foreach (var y in minorY)
                        {
                            double frac = (y - Window.Top) / Window.Height;
                            minorY2.Add(WindowSecondary.Top + frac * WindowSecondary.Height);
                        }
                    }
                    else
                    {
                        minorY2 = Lattice(WindowSecondary.Top, WindowSecondary.Bottom,
                                          Y2Tick, skipEvery: majY);
                    }
                }
            }

            return new TickSet
            {
                MajorX  = majorXTicks,
                MajorY  = majorYTicks.Zip(majorY2Ticks,
                              (p, s) => (Primary: p, Secondary: s)).ToList(),
                MinorX  = minorX,
                MinorY  = minorY,
                MinorY2 = minorY2
            };
        }

        // ---- Private helpers --------------------------------------------

        /// <summary>Hard cap on how many ticks one axis may generate, so a degenerate interval
        /// (a hand-set tick spacing orders of magnitude below the range) cannot allocate without
        /// bound. Well above what <see cref="CalcInterval"/> ever produces — it targets 10–20.</summary>
        private const int MaxTicksPerAxis = 5000;

        /// <summary>The major-tick multiplier as the whole number it always is (2 or 5 from
        /// <see cref="SetTicks"/>), floored at 1 so a zeroed value cannot divide by nothing.</summary>
        private static int MajorStep(double major)
        {
            int m = (int)Math.Round(major);
            return m < 1 ? 1 : m;
        }

        /// <summary>
        /// The ticks of the lattice <c>{n·step}</c> lying within <c>[from, to]</c>, in ascending
        /// order. With <paramref name="skipEvery"/> &gt; 0, every <paramref name="skipEvery"/>-th
        /// lattice point is omitted — which is exactly the set of MAJOR ticks when the caller
        /// passes the minor spacing and the major multiplier, so this one method yields both the
        /// major ticks and the minor ticks that are not also major.
        /// </summary>
        /// <remarks>
        /// <b>Two things here are load-bearing, both learned from a real panning artefact
        /// (owner, 2026-08-21: "still glitchy … I even see some ticks leave the world space and
        /// render outside the rect plot's box").</b>
        ///
        /// <para><b>1. Values are <c>n · step</c>, never an accumulated <c>v += step</c>.</b> The
        /// old code walked the axis by repeated addition, so a tick's value depended on how many
        /// additions it took to get there — and that count changes as you pan, since the starting
        /// index moves. Multiplying by the index makes a given grid line's value identical at every
        /// pan offset, which is what a grid line is supposed to be.</para>
        ///
        /// <para><b>2. Minor-vs-major is separated by INDEX, not by comparing the values.</b> The
        /// old code built a <c>HashSet&lt;double&gt;</c> of the major values and dropped any minor
        /// that matched exactly — and with a tick spacing of 0.2 (which <see cref="CalcInterval"/>
        /// returns constantly, and which has no exact binary representation) five accumulated 0.2s
        /// are NOT bit-equal to one accumulated 1.0, so the dedup silently failed. The minor grid
        /// line was then painted over the major one — same pixel, lighter paint, drawn second—-so
        /// three of every four major gridlines rendered in the WRONG SHADE. Worse, which ones
        /// failed changed as the window moved (measured over a 400 px pan: `.XXX` → `..XX` →
        /// `X.XX` → `.X.X` → `XXXX`), so the gridlines visibly flickered between shades while
        /// dragging. A major tick is the lattice point whose index is a multiple of the major
        /// multiplier; that is an integer fact and cannot round off.</para>
        ///
        /// <para>The Y axis never showed it, which is why it went unnoticed: its spacings came out
        /// as 2 and 4, and doubling a power of two IS exact.</para>
        /// </remarks>
        /// <summary>
        /// The X ticks of a base-10 log axis: the MAJOR set (which carries the labels) or the MINOR
        /// set, which is everything in the sub-decade lattice that is not major.
        ///
        /// <para><b>Three tiers, because a decade lattice stops existing as you zoom in.</b> The
        /// brief's rule — majors are the decades, minors the 2…9 within each — is right for the
        /// span this feature exists for and produces NO tick at all on a window of 1…2 GHz. An
        /// unlabelled axis is a worse defect than the one being fixed, so:</para>
        /// <list type="number">
        /// <item>two decades or more in view: majors are 10ⁿ, minors are {2…9}·10ⁿ;</item>
        /// <item>less than that: majors are {1,2,5}·10ⁿ, minors the remaining {3,4,6,7,8,9}·10ⁿ —
        /// the sub-decade marks a reader already looks for;</item>
        /// <item>narrower than even that lattice can populate (fewer than three majors — two is the
        /// window's own two edges and leaves the interior unlabelled), the axis is
        /// effectively linear over the window and is ticked linearly, by the SAME
        /// <see cref="CalcInterval"/>/<see cref="Lattice"/> pair the linear axis uses. The mapping
        /// stays logarithmic; only the choice of where to put the marks changes.</item>
        /// </list>
        ///
        /// <para>Values are <c>m · 10ⁿ</c> computed from the integers, never accumulated — the same
        /// property <see cref="Lattice"/>'s own remarks are about, and the reason a decade line's
        /// value is identical at every pan offset.</para>
        /// </summary>
        private List<double> LogLattice(double lo, double hi, bool major)
        {
            var list = new List<double>();
            if (!(lo > 0) || !(hi > lo) || !double.IsFinite(hi)) return list;

            double lgLo = Math.Log10(lo), lgHi = Math.Log10(hi);
            if (lgHi - lgLo > MaxLogDecades) return list;

            int[] mant = lgHi - lgLo >= 2.0
                ? (major ? [1] : [2, 3, 4, 5, 6, 7, 8, 9])
                : (major ? [1, 2, 5] : [3, 4, 6, 7, 8, 9]);

            int e0 = (int)Math.Floor(lgLo), e1 = (int)Math.Floor(lgHi);
            double eps = 1e-10;
            for (int e = e0; e <= e1; e++)
            {
                double decade = Math.Pow(10.0, e);
                foreach (int m in mant)
                {
                    double v = m * decade;
                    if (v >= lo * (1 - eps) && v <= hi * (1 + eps)) list.Add(v);
                }
            }
            list.Sort();

            // Tier 3: the sub-decade lattice did not populate the window either. Fall back to the
            // linear tick lattice over the same window — at this zoom the log map differs from an
            // affine one by less than a pixel, and a labelled axis is the point.
            if (list.Count < 3 && lgHi - lgLo < 2.0)
            {
                double step = CalcInterval(hi - lo);
                int    majX = MajorStep(MajorX);
                return major ? Lattice(lo, hi, step * majX)
                             : Lattice(lo, hi, step, skipEvery: majX);
            }
            return list;
        }

        /// <summary>Hard cap on a log axis's span, so a window whose left edge has collapsed toward
        /// zero cannot ask for an unbounded number of decades. Twenty-four decades is wider than
        /// any physical frequency axis and still finite.</summary>
        private const int MaxLogDecades = 24;

        private static List<double> Lattice(double from, double to, double step, int skipEvery = 0)
        {
            var list = new List<double>();
            if (!(step > 0) || !(to >= from)) return list;

            double eps = 1e-10 * Math.Abs(to == 0 ? 1 : to);
            double qLo = from / step;
            double qHi = (to + eps) / step;
            if (double.IsNaN(qLo) || double.IsNaN(qHi)) return list;
            if (qHi - qLo > MaxTicksPerAxis) return list;

            long first = (long)Math.Ceiling(qLo);
            long last  = (long)Math.Floor(qHi);
            for (long n = first; n <= last; n++)
            {
                if (skipEvery > 0 && n % skipEvery == 0) continue;
                list.Add(n * step);
            }
            return list;
        }
    }
}
