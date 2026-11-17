using System.Numerics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using SkiaSharp;

namespace CircuitRF.Ui.Filters;

/// <summary>
/// Dashed mask lines on the Filter Designer's magnitude plot: vertical edges (Fp/Fs) and horizontal
/// floors (−Ap / −As). Drawn as overlay chrome so Autoscale stays on the real S21 traces.
/// </summary>
internal sealed class FilterSpecMaskOverlay : IPlotOverlay
{
    private readonly Plot _plot;

    /// <summary>Band / stop edges in Hz.</summary>
    public IReadOnlyList<double> EdgeHz { get; set; } = [];

    /// <summary>Horizontal levels in dB (negative numbers, e.g. −Ap and −As).</summary>
    public IReadOnlyList<double> LevelDb { get; set; } = [];

    public FilterSpecMaskOverlay(Plot plot) => _plot = plot;

    /// <inheritdoc/>
    public void Draw(SKCanvas canvas, TransformSet tf, RenderTheme theme)
    {
        var win = _plot.Axes.Window;
        if (!(win.Width > 0) || win.Height == 0) return;

        double yLo = Math.Min(win.Top, win.Bottom);
        double yHi = Math.Max(win.Top, win.Bottom);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            Color = new SKColor(120, 120, 120, 180),
            PathEffect = SKPathEffect.CreateDash([5f, 4f], 0),
        };

        double toX(double hz) => _plot.FreqUnits switch
        {
            FreqUnit.Hz  => hz,
            FreqUnit.kHz => hz / 1e3,
            FreqUnit.MHz => hz / 1e6,
            _            => hz / 1e9,
        };

        foreach (double hz in EdgeHz)
        {
            if (!(hz > 0) || !double.IsFinite(hz)) continue;
            double x = toX(hz);
            if (x < win.Left || x > win.Right) continue;
            var top = tf.PrimaryToCanvas(x, win.Top);
            var bot = tf.PrimaryToCanvas(x, win.Bottom);
            canvas.DrawLine(top, bot, paint);
        }

        foreach (double db in LevelDb)
        {
            if (!double.IsFinite(db)) continue;
            if (db < yLo || db > yHi) continue;
            var left = tf.PrimaryToCanvas(win.Left, db);
            var right = tf.PrimaryToCanvas(win.Right, db);
            canvas.DrawLine(left, right, paint);
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
