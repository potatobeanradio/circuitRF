using System.Numerics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using SkiaSharp;

namespace CircuitRF.Ui.Filters;

/// <summary>One coloured mask line on the magnitude plot.</summary>
internal readonly record struct FilterMaskLine(double Value, SKColor Color, bool Vertical);

/// <summary>
/// Dashed mask lines on the Filter Designer's magnitude plot: vertical edges (Fp/Fs) and horizontal
/// floors (−Ap / −As). Colours match the legend strokes next to the Edges / Ap / As labels.
/// Drawn as overlay chrome so Autoscale stays on the real S21 traces.
/// </summary>
internal sealed class FilterSpecMaskOverlay : IPlotOverlay
{
    private readonly Plot _plot;

    /// <summary>Mask lines to draw this frame.</summary>
    public IReadOnlyList<FilterMaskLine> Lines { get; set; } = [];

    public FilterSpecMaskOverlay(Plot plot) => _plot = plot;

    /// <inheritdoc/>
    public void Draw(SKCanvas canvas, TransformSet tf, RenderTheme theme)
    {
        var win = _plot.Axes.Window;
        if (!(win.Width > 0) || win.Height == 0) return;

        double yLo = Math.Min(win.Top, win.Bottom);
        double yHi = Math.Max(win.Top, win.Bottom);

        double toX(double hz) => _plot.FreqUnits switch
        {
            FreqUnit.Hz  => hz,
            FreqUnit.kHz => hz / 1e3,
            FreqUnit.MHz => hz / 1e6,
            _            => hz / 1e9,
        };

        foreach (var line in Lines)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.4f,
                Color = line.Color,
                PathEffect = SKPathEffect.CreateDash([5f, 4f], 0),
            };

            if (line.Vertical)
            {
                if (!(line.Value > 0) || !double.IsFinite(line.Value)) continue;
                double x = toX(line.Value);
                if (x < win.Left || x > win.Right) continue;
                canvas.DrawLine(tf.PrimaryToCanvas(x, win.Top), tf.PrimaryToCanvas(x, win.Bottom), paint);
            }
            else
            {
                if (!double.IsFinite(line.Value)) continue;
                if (line.Value < yLo || line.Value > yHi) continue;
                canvas.DrawLine(
                    tf.PrimaryToCanvas(win.Left, line.Value),
                    tf.PrimaryToCanvas(win.Right, line.Value),
                    paint);
            }
        }
    }

    /// <inheritdoc/>
    public object? HitTest(double canvasX, double canvasY, TransformSet tf) => null;

    /// <inheritdoc/>
    public void DragBegin(object handle) { }

    /// <inheritdoc/>
    public void DragTo(Complex gammaWorld) { }

    /// <inheritdoc/>
    public void DragEnd(bool cancelled) { }
}

/// <summary>Stable colours shared by the Edges legends and the plot mask lines.</summary>
internal static class FilterMaskColors
{
    public static readonly SKColor EdgeA = new(230, 126, 34);   // orange  — Fs1 / first edge
    public static readonly SKColor EdgeB = new(41, 128, 185);   // blue    — Fp1 / second
    public static readonly SKColor EdgeC = new(39, 174, 96);    // green   — Fp2 / third
    public static readonly SKColor EdgeD = new(142, 68, 173);   // purple  — Fs2 / fourth
    public static readonly SKColor Ap    = new(192, 57, 43);    // red     — −Ap
    public static readonly SKColor As    = new(127, 140, 141);  // grey    — −As

    public const string EdgeABrush = "#E67E22";
    public const string EdgeBBrush = "#2980B9";
    public const string EdgeCBrush = "#27AE60";
    public const string EdgeDBrush = "#8E44AD";
    public const string ApBrush    = "#C0392B";
    public const string AsBrush    = "#7F8C8D";
}
