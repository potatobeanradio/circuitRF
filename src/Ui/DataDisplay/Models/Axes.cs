// ================================================================
//  Axes.cs  —  Axis state and tick calculation  (pure model, no drawing)
//
//  Ported from splotRF/src/Models/Axes.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace CircuitRF.Ui.DataDisplay
{
    // ============================================================
    //  AxesColor  —  per-axis color overrides
    // ============================================================

    public class AxesColor
    {
        public Color YAxisColor  { get; set; } = Colors.Black;
        public Color Y2AxisColor { get; set; } = Colors.Black;

        public void SetColor(int index, Color color)
        {
            if      (index == 0) YAxisColor  = color;
            else if (index == 1) Y2AxisColor = color;
        }
    }

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

        public Rect WindowState          { get; set; } = default;
        public Rect WindowSecondaryState { get; set; } = default;

        // ---- Primary window (world coordinates) -------------------------

        private Rect _window = new Rect(-50, -50, 150, 150);

        /// <summary>
        /// Primary axis world-coordinate window (freq × left-Y).
        /// Recalculates tick intervals on every assignment.
        /// A left edge of exactly 0 is nudged slightly so that the
        /// zero grid line renders correctly.
        /// </summary>
        public Rect Window
        {
            get => _window;
            set
            {
                if (value.X == 0)
                    value = new Rect(-1e-6, value.Y, value.Width + 1e-6, value.Height);
                _window = value;
                SetTicks();
            }
        }

        // ---- Secondary (right Y) window ---------------------------------

        private Rect _windowSecondary = new Rect(0, 0, 150, 150);

        public Rect WindowSecondary
        {
            get => _windowSecondary;
            set
            {
                if (value.X == 0)
                    value = new Rect(-1e-6, value.Y, value.Width + 1e-6, value.Height);
                _windowSecondary = value;
            }
        }

        public bool SecondaryShareGrid { get; set; } = true;
        public bool ShowSecondary      { get; set; } = false;

        // ---- Viewport (fractional sub-region of the canvas) -------------

        public Rect Viewport { get; set; } = new Rect(0.15, 0.05, 0.80, 0.88);

        // ---- Constructors -----------------------------------------------

        public Axes() { }

        /// <summary>Deep-copy constructor.</summary>
        public Axes(Axes src)
        {
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

        public void Translate(double dx, double dy)
        {
            if (!LockedPanning)
                Window = new Rect(WindowState.X - dx, WindowState.Y - dy,
                                  Window.Width, Window.Height);
        }

        public void TranslateSecondary(double dx, double dy)
        {
            if (!LockedPanning)
                WindowSecondary = new Rect(
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

            var majorXTicks = Lattice(Window.Left, Window.Right,  dmx);
            var majorYTicks = Lattice(Window.Top,  Window.Bottom, dmy);

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
                minorX = Lattice(Window.Left, Window.Right,  XTick, skipEvery: majX);
                minorY = Lattice(Window.Top,  Window.Bottom, YTick, skipEvery: majY);

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
