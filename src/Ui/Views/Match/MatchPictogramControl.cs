using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Match;

/// <summary>
/// The specification pane's termination pictogram (match.md §9.2): an R with its reactive element in
/// the chosen arrangement — R in series with C, R in parallel with L, and so on. <c>None</c> draws
/// the resistor alone.
/// </summary>
/// <remarks>
/// It is the fastest way to show series-versus-parallel, which is the one thing about a termination
/// that a pair of radio buttons states and does not show. The glyphs are circuitRF's own
/// (<see cref="BuiltInSymbols"/>), so the R here is the R on the page.
/// </remarks>
public sealed class MatchPictogramControl : Control
{
    /// <summary>What to draw.</summary>
    public static readonly StyledProperty<MatchPictogram> PictogramProperty =
        AvaloniaProperty.Register<MatchPictogramControl, MatchPictogram>(nameof(Pictogram));

    /// <summary>Line colour.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<MatchPictogramControl, IBrush?>(nameof(Stroke));

    static MatchPictogramControl() =>
        AffectsRender<MatchPictogramControl>(PictogramProperty, StrokeProperty);

    /// <inheritdoc cref="PictogramProperty"/>
    public MatchPictogram Pictogram
    {
        get => GetValue(PictogramProperty);
        set => SetValue(PictogramProperty, value);
    }

    /// <inheritdoc cref="StrokeProperty"/>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    // The drawing's own world, in the same 100-units-per-grid-square the symbols use.
    private const double WorldW = 1200.0;
    private const double WorldH = 900.0;

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width <= 2 || b.Height <= 2) return;

        double scale = Math.Min(b.Width / WorldW, b.Height / WorldH);
        double ox = b.Width / 2.0, oy = b.Height / 2.0;
        Point P(double x, double y) => new(ox + x * scale, oy + y * scale);

        var brush = Stroke ?? Brushes.Gray;
        var pen = new Pen(brush, Math.Max(1.0, 16 * scale));

        var p = Pictogram;
        var reactive = p.Kind == ReactanceKind.L ? SymbolKind.Inductor : SymbolKind.Capacitor;

        if (p.Kind == ReactanceKind.None)
        {
            Glyph(ctx, pen, brush, SymbolKind.Resistor, 0, 0, P);
            ctx.DrawLine(pen, P(-560, 0), P(-200, 0));
            ctx.DrawLine(pen, P(200, 0), P(560, 0));
            return;
        }

        if (p.Topology == TerminationTopology.Series)
        {
            Glyph(ctx, pen, brush, SymbolKind.Resistor, -280, 0, P);
            Glyph(ctx, pen, brush, reactive, 280, 0, P);
            ctx.DrawLine(pen, P(-560, 0), P(-480, 0));
            ctx.DrawLine(pen, P(-80, 0), P(80, 0));
            ctx.DrawLine(pen, P(480, 0), P(560, 0));
            return;
        }

        // Parallel: two branches between one pair of nodes.
        Glyph(ctx, pen, brush, SymbolKind.Resistor, 0, -220, P);
        Glyph(ctx, pen, brush, reactive, 0, 220, P);
        ctx.DrawLine(pen, P(-380, -220), P(-200, -220));
        ctx.DrawLine(pen, P(200, -220), P(380, -220));
        ctx.DrawLine(pen, P(-380, 220), P(-200, 220));
        ctx.DrawLine(pen, P(200, 220), P(380, 220));
        ctx.DrawLine(pen, P(-380, -220), P(-380, 220));
        ctx.DrawLine(pen, P(380, -220), P(380, 220));
        ctx.DrawLine(pen, P(-560, 0), P(-380, 0));
        ctx.DrawLine(pen, P(380, 0), P(560, 0));
    }

    /// <summary>Draws one built-in glyph, rotated to lie horizontally, centred at (cx, cy).</summary>
    private static void Glyph(
        DrawingContext ctx, IPen pen, IBrush brush, SymbolKind kind,
        double cx, double cy, Func<double, double, Point> P)
    {
        var symbol = BuiltInSymbols.Primitives(kind);
        Point T(double lx, double ly) => P(cx - ly, cy + lx);

        foreach (var prim in symbol.Primitives)
        {
            switch (prim)
            {
                case LinePrimitive l:
                    ctx.DrawLine(pen, T(l.X1, l.Y1), T(l.X2, l.Y2));
                    break;
                case PolylinePrimitive pl:
                    for (int i = 1; i < pl.Points.Count; i++)
                        ctx.DrawLine(pen, T(pl.Points[i - 1][0], pl.Points[i - 1][1]),
                                          T(pl.Points[i][0], pl.Points[i][1]));
                    break;
                case ArcPrimitive a:
                {
                    const int steps = 14;
                    Point prev = default;
                    for (int i = 0; i <= steps; i++)
                    {
                        double rad = (a.StartDeg + a.SweepDeg * i / steps) * Math.PI / 180.0;
                        var q = T(a.Cx + a.R * Math.Cos(rad), a.Cy + a.R * Math.Sin(rad));
                        if (i > 0) ctx.DrawLine(pen, prev, q);
                        prev = q;
                    }
                    break;
                }
                case QuadCurvePrimitive qc:
                {
                    const int steps = 14;
                    Point prev = default;
                    for (int i = 0; i <= steps; i++)
                    {
                        double t = i / (double)steps, u = 1 - t;
                        var q = T(u * u * qc.P0X + 2 * u * t * qc.CtrlX + t * t * qc.P2X,
                                  u * u * qc.P0Y + 2 * u * t * qc.CtrlY + t * t * qc.P2Y);
                        if (i > 0) ctx.DrawLine(pen, prev, q);
                        prev = q;
                    }
                    break;
                }
                case CirclePrimitive c:
                {
                    var centre = T(c.Cx, c.Cy);
                    double r = Math.Max(1.0, Math.Abs((T(c.Cx, c.Cy + c.R) - centre).X));
                    ctx.DrawEllipse(c.Filled ? brush : Brushes.Transparent, pen, centre, r, r);
                    break;
                }
            }
        }
    }
}
