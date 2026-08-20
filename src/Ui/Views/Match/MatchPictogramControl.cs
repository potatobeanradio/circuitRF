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
///
/// <para><b>Drawn VERTICALLY — rotated 90° from the original</b> (owner, 2026-08-19). A termination
/// hangs between a node and ground, which is what a vertical drawing shows and a horizontal one does
/// not; the built-in glyphs are vertical to begin with, so this orientation is also the one that
/// needs no rotation of its own. <see cref="ResistorOnLeft"/> then puts termination 1's R on the LEFT
/// branch of a parallel pair and termination 2's on the RIGHT, so the two pictograms mirror each
/// other the way the two ends of the network do.</para>
/// </remarks>
public sealed class MatchPictogramControl : Control
{
    /// <summary>What to draw.</summary>
    public static readonly StyledProperty<MatchPictogram> PictogramProperty =
        AvaloniaProperty.Register<MatchPictogramControl, MatchPictogram>(nameof(Pictogram));

    /// <summary>Line colour.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<MatchPictogramControl, IBrush?>(nameof(Stroke));

    /// <summary>
    /// Which branch of a PARALLEL pair the resistor takes — true = left (termination 1), false =
    /// right (termination 2). Ignored by the series and resistor-only arrangements, which have only
    /// one branch to draw.
    /// </summary>
    public static readonly StyledProperty<bool> ResistorOnLeftProperty =
        AvaloniaProperty.Register<MatchPictogramControl, bool>(nameof(ResistorOnLeft), true);

    static MatchPictogramControl() =>
        AffectsRender<MatchPictogramControl>(
            PictogramProperty, StrokeProperty, ResistorOnLeftProperty);

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

    /// <inheritdoc cref="ResistorOnLeftProperty"/>
    public bool ResistorOnLeft
    {
        get => GetValue(ResistorOnLeftProperty);
        set => SetValue(ResistorOnLeftProperty, value);
    }

    // The drawing's own world, in the same 100-units-per-grid-square the symbols use. Portrait, since
    // the arrangement is now vertical.
    private const double WorldW = 900.0;
    private const double WorldH = 1200.0;

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
            ctx.DrawLine(pen, P(0, -560), P(0, -200));
            ctx.DrawLine(pen, P(0, 200), P(0, 560));
            return;
        }

        if (p.Topology == TerminationTopology.Series)
        {
            Glyph(ctx, pen, brush, SymbolKind.Resistor, 0, -280, P);
            Glyph(ctx, pen, brush, reactive, 0, 280, P);
            ctx.DrawLine(pen, P(0, -560), P(0, -480));
            ctx.DrawLine(pen, P(0, -80), P(0, 80));
            ctx.DrawLine(pen, P(0, 480), P(0, 560));
            return;
        }

        // Parallel: two vertical branches between one pair of nodes. The resistor takes the side this
        // end is drawn on, so termination 1 and termination 2 read as mirror images.
        double rx = ResistorOnLeft ? -220 : 220;
        double xx = -rx;
        Glyph(ctx, pen, brush, SymbolKind.Resistor, rx, 0, P);
        Glyph(ctx, pen, brush, reactive, xx, 0, P);

        foreach (double x in new[] { rx, xx })
        {
            ctx.DrawLine(pen, P(x, -380), P(x, -200));
            ctx.DrawLine(pen, P(x, 200), P(x, 380));
        }
        ctx.DrawLine(pen, P(rx, -380), P(xx, -380));
        ctx.DrawLine(pen, P(rx, 380), P(xx, 380));
        ctx.DrawLine(pen, P(0, -560), P(0, -380));
        ctx.DrawLine(pen, P(0, 380), P(0, 560));
    }

    /// <summary>
    /// Draws one built-in glyph in its own natural (vertical) orientation, centred at (cx, cy).
    /// </summary>
    private static void Glyph(
        DrawingContext ctx, IPen pen, IBrush brush, SymbolKind kind,
        double cx, double cy, Func<double, double, Point> P)
    {
        var symbol = BuiltInSymbols.Primitives(kind);
        Point T(double lx, double ly) => P(cx + lx, cy + ly);

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
                    double r = Math.Max(1.0, Math.Abs((T(c.Cx + c.R, c.Cy) - centre).X));
                    ctx.DrawEllipse(c.Filled ? brush : Brushes.Transparent, pen, centre, r, r);
                    break;
                }
            }
        }
    }
}
