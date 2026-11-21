using System.Numerics;
using CircuitRF.Render;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using SkiaSharp;

namespace CircuitRF.Ui.Filters;

/// <summary>One coloured mask line on the magnitude plot, with an optional name drawn beside it.</summary>
internal readonly record struct FilterMaskLine(
    double Value, SKColor Color, bool Vertical, string Label = "",
    double X0 = double.NaN, double X1 = double.NaN, int Forbid = 0);

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

    /// <summary>When true, each line's <see cref="FilterMaskLine.Label"/> is drawn beside it.</summary>
    public bool ShowLabels { get; set; } = true;

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

        using var font = new SKFont(SkiaFonts.PlexRegular, 10f);
        using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        int edge = 0;
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
                var a = tf.PrimaryToCanvas(x, win.Top);
                var b = tf.PrimaryToCanvas(x, win.Bottom);
                canvas.DrawLine(a, b, paint);
                if (ShowLabels && line.Label.Length > 0)
                {
                    // Alternate rows, so two close edges (a narrow bandstop) do not print on top of
                    // each other.
                    textPaint.Color = line.Color;
                    float tx = Math.Min(a.X, b.X) + 3f;
                    float ty = Math.Min(a.Y, b.Y) + (font.Size + 2f) * (1 + (edge++ & 1));
                    canvas.DrawText(line.Label, tx, ty, SKTextAlign.Left, font, textPaint);
                }
            }
            else
            {
                if (!double.IsFinite(line.Value)) continue;
                // A limit spans its own band; an open end (NaN) runs to the plot's edge.
                double x0 = double.IsFinite(line.X0) ? Math.Max(toX(line.X0), win.Left) : win.Left;
                double x1 = double.IsFinite(line.X1) ? Math.Min(toX(line.X1), win.Right) : win.Right;
                if (!(x1 > x0)) continue;
                if (line.Forbid != 0)
                {
                    // The side a trace must not enter, lightly tinted: below −Ap in a passband,
                    // above −As in a stopband. Clipped to the window when the limit is off it.
                    double y = Math.Clamp(line.Value, yLo, yHi);
                    double far = line.Forbid > 0 ? yHi : yLo;
                    if (far != y)
                    {
                        var p0 = tf.PrimaryToCanvas(x0, y);
                        var p1 = tf.PrimaryToCanvas(x1, far);
                        using var fill = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Color = line.Color.WithAlpha(28),
                        };
                        canvas.DrawRect(SKRect.Create(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y),
                                                      Math.Abs(p1.X - p0.X), Math.Abs(p1.Y - p0.Y)), fill);
                    }
                }
                if (line.Value < yLo || line.Value > yHi) continue;
                var a = tf.PrimaryToCanvas(x0, line.Value);
                var b = tf.PrimaryToCanvas(x1, line.Value);
                canvas.DrawLine(a, b, paint);
                if (ShowLabels && line.Label.Length > 0)
                {
                    // Right-aligned at the segment's right end and just below the line: an edge
                    // label sits to the RIGHT of its own line, so the two never share a spot.
                    textPaint.Color = line.Color;
                    float tx = Math.Max(a.X, b.X) - 4f;
                    float ty = Math.Max(a.Y, b.Y) + font.Size + 1f;
                    canvas.DrawText(line.Label, tx, ty, SKTextAlign.Right, font, textPaint);
                }
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
    /// <summary>Every passband edge and the −Ap limit.</summary>
    public static readonly SKColor Pass = new(41, 128, 185);    // blue
    /// <summary>Every stopband edge and the −As limit.</summary>
    public static readonly SKColor Stop = new(192, 57, 43);     // red

    public const string PassBrush = "#2980B9";
    public const string StopBrush = "#C0392B";
}
