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
            double dmx = XTick * MajorX;
            double dmy = YTick * MajorY;
            if (dmx <= 0) dmx = 0.05;
            if (dmy <= 0) dmy = 0.05;

            int i = Window.Left < 0
                ? (int)(Window.Left / dmx)
                : (int)(Window.Left / dmx + 1);
            int j = Window.Top < 0
                ? (int)(Window.Top / dmy)
                : (int)(Window.Top / dmy + 1);

            var majorXTicks = StrideTo(i * dmx, Window.Right,  dmx);
            var majorYTicks = StrideTo(j * dmy, Window.Bottom, dmy);

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
                double dmy2 = Y2Tick * MajorY;
                if (dmy2 <= 0) dmy2 = 0.05;
                int j2 = WindowSecondary.Top < 0
                    ? (int)(WindowSecondary.Top / dmy2)
                    : (int)(WindowSecondary.Top / dmy2 + 1);
                majorY2Ticks = StrideTo(j2 * dmy2, WindowSecondary.Bottom, dmy2);
            }

            // Ensure equal-length lists before Zip
            while (majorY2Ticks.Count < majorYTicks.Count)  majorY2Ticks.Add(double.NaN);
            while (majorYTicks.Count  < majorY2Ticks.Count) majorYTicks.Add(double.NaN);

            var minorX  = new List<double>();
            var minorY  = new List<double>();
            var minorY2 = new List<double>();

            if (minorTicks)
            {
                if (XTick != 0)
                {
                    int ii = Window.Left < 0
                        ? (int)(Window.Left / XTick)
                        : (int)(Window.Left / XTick + 1);
                    var majorSet = new HashSet<double>(majorXTicks);
                    foreach (var v in StrideTo(ii * XTick, Window.Right, XTick))
                        if (!majorSet.Contains(v)) minorX.Add(v);
                    minorX.Sort();
                }

                if (YTick != 0)
                {
                    int jj = Window.Top < 0
                        ? (int)(Window.Top / YTick)
                        : (int)(Window.Top / YTick + 1);
                    var majorSet = new HashSet<double>(majorYTicks);
                    foreach (var v in StrideTo(jj * YTick, Window.Bottom, YTick))
                        if (!majorSet.Contains(v)) minorY.Add(v);
                    minorY.Sort();
                }

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
                        int jj = WindowSecondary.Top < 0
                            ? (int)(WindowSecondary.Top / Y2Tick)
                            : (int)(WindowSecondary.Top / Y2Tick + 1);
                        var majorSet = new HashSet<double>(majorY2Ticks);
                        foreach (var v in StrideTo(jj * Y2Tick, WindowSecondary.Bottom, Y2Tick))
                            if (!majorSet.Contains(v)) minorY2.Add(v);
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

        private static List<double> StrideTo(double from, double to, double by)
        {
            var list = new List<double>();
            if (by <= 0) return list;
            double eps = 1e-10 * Math.Abs(to == 0 ? 1 : to);
            for (double v = from; v <= to + eps; v += by)
                list.Add(v);
            return list;
        }
    }
}
