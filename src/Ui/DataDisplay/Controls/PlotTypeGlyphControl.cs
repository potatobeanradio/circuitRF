using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CircuitRF.Ui.DataDisplay.Controls;

public enum PlotTypeGlyphKind { Smith, Polar }

/// <summary>
/// Draws a simplified Smith or Polar grid glyph. All geometry lives here for owner tweaking.
/// </summary>
public class PlotTypeGlyphControl : Control
{
    public static readonly StyledProperty<PlotTypeGlyphKind> KindProperty =
        AvaloniaProperty.Register<PlotTypeGlyphControl, PlotTypeGlyphKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<PlotTypeGlyphControl, IBrush?>(nameof(Stroke));

    static PlotTypeGlyphControl()
    {
        AffectsRender<PlotTypeGlyphControl>(KindProperty, StrokeProperty);
    }

    public PlotTypeGlyphKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        const double inset = 0.75;
        double cx = b.Width  / 2.0;
        double cy = b.Height / 2.0;
        double rx = cx - inset;
        double ry = cy - inset;
        if (rx <= 0 || ry <= 0) return;

        // Unit-coord mapping: u ∈ [-1,1] → pixel; screen Y is flipped (v=+1 → top).
        double Px(double u) => cx + u * rx;
        double Py(double v) => cy - v * ry;

        var brush = Stroke ?? new SolidColorBrush(Color.FromArgb(180, 128, 128, 128));
        var pen   = new Pen(brush, 1.0);

        if (Kind == PlotTypeGlyphKind.Polar)
        {
            DrawPolar(context, pen, cx, cy, rx, ry, Px, Py);
        }
        else
        {
            DrawSmith(context, pen, cx, cy, rx, ry, Px, Py);
        }
    }

    // ── Polar ──────────────────────────────────────────────────────────────
    private static void DrawPolar(DrawingContext ctx, IPen pen,
        double cx, double cy, double rx, double ry,
        Func<double, double> Px, Func<double, double> Py)
    {
        ctx.DrawEllipse(null, pen, new Point(cx, cy), rx,       ry      ); // r = 1.0
        ctx.DrawEllipse(null, pen, new Point(cx, cy), rx * 0.5, ry * 0.5); // r = 0.5
        ctx.DrawLine(pen, new Point(Px(-1), Py(0)), new Point(Px(1), Py(0))); // horizontal axis
        ctx.DrawLine(pen, new Point(Px(0), Py(-1)), new Point(Px(0), Py(1))); // vertical axis
    }

    // ── Smith ───────────────────────────────────────────────────────────────
    // Sparse Smith grid (see brief §4): outer circle, real axis, R=1 circle, X=±1 arc circles.
    // Math mirrors AxesRenderer.DrawSmithGrid: R-circle radius=1/(1+R), centreX=1−radius;
    // X-circle centreX=1, centreY=±1/X, radius=1/X.
    private static void DrawSmith(DrawingContext ctx, IPen pen,
        double cx, double cy, double rx, double ry,
        Func<double, double> Px, Func<double, double> Py)
    {
        var clipGeom = new EllipseGeometry(new Rect(cx - rx, cy - ry, rx * 2, ry * 2));
        using (ctx.PushGeometryClip(clipGeom))
        {
            // Outer unit circle
            ctx.DrawEllipse(null, pen, new Point(cx, cy), rx, ry);

            // Real axis: (−1,0) → (1,0)
            ctx.DrawLine(pen, new Point(Px(-1), Py(0)), new Point(Px(1), Py(0)));

            // R=1 circle: radius = 1/(1+1) = 0.5, centreX = 1−0.5 = 0.5
            const double rr1 = 0.5, rc1x = 0.5;
            ctx.DrawEllipse(null, pen,
                new Point(Px(rc1x), Py(0)),
                rx * rr1, ry * rr1);

            // X=+1 arc circle: centreX=1, centreY=−1/1=−1, radius=1/1=1
            ctx.DrawEllipse(null, pen,
                new Point(Px(1.0), Py(-1.0)),
                rx * 1.0, ry * 1.0);

            // X=−1 arc circle: centreX=1, centreY=+1/1=+1, radius=1
            ctx.DrawEllipse(null, pen,
                new Point(Px(1.0), Py(1.0)),
                rx * 1.0, ry * 1.0);
        }
    }
}
