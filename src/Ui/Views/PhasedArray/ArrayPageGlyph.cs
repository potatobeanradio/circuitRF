using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CircuitRF.Ui.PhasedArray;

namespace CircuitRF.Ui.Views.PhasedArray;

/// <summary>
/// The Phased Array tool's page buttons — one glyph per <see cref="ArrayPage"/>, which no icon set has. Drawn in the style of
/// <see cref="Controls.ImageMakeGlyphControl"/>: one <see cref="Stroke"/> brush, set from a style so a checked button can recolour it.
/// Geometry is in a 24 × 24 design box, scaled to the bounds.
/// </summary>
public sealed class ArrayPageGlyph : Control
{
    public static readonly StyledProperty<ArrayPage> PageProperty =
        AvaloniaProperty.Register<ArrayPageGlyph, ArrayPage>(nameof(Page));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ArrayPageGlyph, IBrush?>(nameof(Stroke));

    static ArrayPageGlyph() => AffectsRender<ArrayPageGlyph>(PageProperty, StrokeProperty);

    public ArrayPage Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private const double Box = 24;

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        double s = Math.Min(b.Width, b.Height) / Box;
        double ox = (b.Width - Box * s) / 2, oy = (b.Height - Box * s) / 2;
        var brush = Stroke ?? new SolidColorBrush(Color.FromArgb(200, 128, 128, 128));
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
            Draw(context, Page, brush);
    }

    /// <summary>Draws <paramref name="page"/>'s glyph in the 24 × 24 design box.</summary>
    public static void Draw(DrawingContext ctx, ArrayPage page, IBrush brush)
    {
        switch (page)
        {
            case ArrayPage.Beam: DrawBeam(ctx, brush); break;
            case ArrayPage.Channel: DrawChannel(ctx, brush); break;
            case ArrayPage.ScanMaps: DrawScanMaps(ctx, brush); break;
            case ArrayPage.TapersAndBits: DrawTapers(ctx, brush); break;
            case ArrayPage.Failures: DrawFailures(ctx, brush); break;
            case ArrayPage.Frequency: DrawFrequency(ctx, brush); break;
        }
    }

    private static Pen Line(IBrush brush, double width = 1.5, IDashStyle? dash = null)
        => new(brush, width, dash, PenLineCap.Round, PenLineJoin.Round);

    /// <summary>A radiation lobe from <paramref name="origin"/>, <paramref name="length"/> long and about
    /// <paramref name="width"/> wide, tilted <paramref name="angleDeg"/> clockwise from straight up.</summary>
    private static Geometry Lobe(Point origin, double angleDeg, double length, double width)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double w = width / 2, l = length;
            c.BeginFigure(new Point(0, 0), true);
            c.CubicBezierTo(new Point(-w * 1.3, -l * 0.45), new Point(-w, -l), new Point(0, -l));
            c.CubicBezierTo(new Point(w, -l), new Point(w * 1.3, -l * 0.45), new Point(0, 0));
            c.EndFigure(true);
        }
        g.Transform = new MatrixTransform(Matrix.CreateRotation(angleDeg * Math.PI / 180) * Matrix.CreateTranslation(origin.X, origin.Y));
        return g;
    }

    /// <summary>The array as a row of elements along the bottom.</summary>
    private static void Elements(DrawingContext ctx, IBrush brush, double y)
    {
        for (int k = 0; k < 5; k++) ctx.DrawRectangle(brush, null, new Rect(2.5 + k * 4, y, 3, 2.2), 0.6, 0.6);
    }

    // Beam: the array along the bottom and its main beam, steered off broadside, with a sidelobe either side.
    private static void DrawBeam(DrawingContext ctx, IBrush brush)
    {
        Elements(ctx, brush, 20);
        var origin = new Point(12, 18.5);
        ctx.DrawGeometry(brush, null, Lobe(origin, 20, 16.5, 6.5));
        var pen = Line(brush, 1.2);
        ctx.DrawGeometry(null, pen, Lobe(origin, -50, 6.5, 3.4));
        ctx.DrawGeometry(null, pen, Lobe(origin, 78, 5.5, 3));
    }

    // Channel: two arrays, a direct ray between them and one reflected off a scatterer above.
    private static void DrawChannel(DrawingContext ctx, IBrush brush)
    {
        for (int k = 0; k < 3; k++)
        {
            ctx.DrawRectangle(brush, null, new Rect(1.5, 12 + k * 3.5, 2.2, 2.6), 0.5, 0.5);
            ctx.DrawRectangle(brush, null, new Rect(20.3, 12 + k * 3.5, 2.2, 2.6), 0.5, 0.5);
        }
        var pen = Line(brush, 1.4);
        ctx.DrawLine(pen, new Point(5, 16.8), new Point(19, 16.8));
        ctx.DrawLine(Line(brush, 2.2), new Point(7, 4), new Point(17, 4));   // the scatterer
        var ray = Line(brush, 1.2, new DashStyle([1.6, 1.4], 0));
        ctx.DrawLine(ray, new Point(5, 13), new Point(12, 5.2));
        ctx.DrawLine(ray, new Point(12, 5.2), new Point(19, 13));
    }

    // Scan maps: the visible disc in (u, v), its rings and spokes, one cell filled.
    private static void DrawScanMaps(DrawingContext ctx, IBrush brush)
    {
        var c = new Point(12, 12);
        var pen = Line(brush, 1.3);
        var thin = Line(brush, 0.9);
        ctx.DrawEllipse(null, pen, c, 10, 10);
        ctx.DrawEllipse(null, thin, c, 6.5, 6.5);
        for (int k = 0; k < 4; k++)
        {
            double a = k * Math.PI / 4;
            ctx.DrawLine(thin, new Point(c.X + 10 * Math.Cos(a), c.Y + 10 * Math.Sin(a)), new Point(c.X - 10 * Math.Cos(a), c.Y - 10 * Math.Sin(a)));
        }
        // the cell between the outer two rings, from -90° to -45° (up and to the right)
        var cell = new StreamGeometry();
        using (var g = cell.Open())
        {
            Point P(double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
            g.BeginFigure(P(6.5, -90), true);
            g.LineTo(P(10, -90));
            g.ArcTo(P(10, -45), new Size(10, 10), 0, false, SweepDirection.Clockwise);
            g.LineTo(P(6.5, -45));
            g.ArcTo(P(6.5, -90), new Size(6.5, 6.5), 0, false, SweepDirection.CounterClockwise);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, cell);
    }

    // Tapers and bits: the elements' amplitudes as bars under a taper, each bar's top a quantised step.
    private static void DrawTapers(DrawingContext ctx, IBrush brush)
    {
        double[] h = [4, 8, 12, 14, 12, 8, 4];
        for (int k = 0; k < h.Length; k++) ctx.DrawRectangle(brush, null, new Rect(2 + k * 3, 21 - h[k], 2.2, h[k]));
        var taper = new StreamGeometry();
        using (var g = taper.Open())
        {
            g.BeginFigure(new Point(1.5, 16), false);
            g.CubicBezierTo(new Point(6, 9), new Point(9, 3.5), new Point(12, 3.5));
            g.CubicBezierTo(new Point(15, 3.5), new Point(18, 9), new Point(22.5, 16));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, Line(brush, 1.1, new DashStyle([1.5, 1.3], 0)), taper);
    }

    // Failures: the aperture as a grid of elements, two of them crossed out.
    private static void DrawFailures(DrawingContext ctx, IBrush brush)
    {
        var x = Line(brush, 1.6);
        for (int r = 0; r < 4; r++)
            for (int col = 0; col < 4; col++)
            {
                var p = new Point(4.5 + col * 5, 4.5 + r * 5);
                if ((r, col) is (1, 2) or (3, 0))
                {
                    ctx.DrawLine(x, new Point(p.X - 1.9, p.Y - 1.9), new Point(p.X + 1.9, p.Y + 1.9));
                    ctx.DrawLine(x, new Point(p.X - 1.9, p.Y + 1.9), new Point(p.X + 1.9, p.Y - 1.9));
                }
                else ctx.DrawEllipse(brush, null, p, 1.6, 1.6);
            }
    }

    // Frequency: the beam squints as the frequency moves — one beam solid, the other dashed, an arrow between their tips.
    private static void DrawFrequency(DrawingContext ctx, IBrush brush)
    {
        Elements(ctx, brush, 20);
        var origin = new Point(12, 18.5);
        ctx.DrawGeometry(null, Line(brush, 1.5), Lobe(origin, -16, 12.5, 5.5));
        ctx.DrawGeometry(null, Line(brush, 1.3, new DashStyle([1.6, 1.4], 0)), Lobe(origin, 30, 12.5, 5.5));
        // the arrow: an arc about the array's centre, outside both tips, from the solid beam's angle to the dashed one's
        const double r = 16.5, from = -14, to = 34;
        Point At(double deg) => new(origin.X + r * Math.Sin(deg * Math.PI / 180), origin.Y - r * Math.Cos(deg * Math.PI / 180));
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(At(from), false);
            g.ArcTo(At(to), new Size(r, r), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        var pen = Line(brush, 1.2);
        ctx.DrawGeometry(null, pen, arc);
        var tip = At(to);
        double t = to * Math.PI / 180, dx = Math.Cos(t), dy = Math.Sin(t);   // the clockwise tangent at the tip
        foreach (double turn in new[] { 0.6, -0.6 })
        {
            double bx = dx * Math.Cos(turn) - dy * Math.Sin(turn), by = dx * Math.Sin(turn) + dy * Math.Cos(turn);
            ctx.DrawLine(pen, tip, new Point(tip.X - 2.6 * bx, tip.Y - 2.6 * by));
        }
    }
}
