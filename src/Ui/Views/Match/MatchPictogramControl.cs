using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Match;

/// <summary>
/// The specification pane's termination pictogram (match.md §9.2) — <b>one library part, drawn by
/// its own glyph</b>: an R, an SRL, an SRC, a PRL or a PRC, whichever the termination is.
/// </summary>
/// <remarks>
/// It is the fastest way to show series-versus-parallel, which is the one thing about a termination
/// that a pair of radio buttons states and does not show.
///
/// <para><b>It draws ONE symbol now, and composes nothing</b> (owner, 2026-09-20: the old drawing
/// did not match the feel of the rest of circuitRF). Until the two-element RLC parts existed there
/// was no single glyph for "R in series with C", so this control drew the standalone R and the
/// standalone C and joined them with lines of its own — a second, slightly-different copy of the
/// library's own artwork, with its own spacings and its own idea of how a parallel pair is hung
/// between two rails. <see cref="MatchPictogram.Symbol"/> now names the part and
/// <see cref="BuiltInSymbols"/> draws it, so the R here is the R on the page and stays that way
/// through any future redraw.</para>
///
/// <para><b>Nothing is mirrored any more.</b> Termination 1's resistor used to take the LEFT branch
/// of a parallel pair and termination 2's the right, so the two cards read as mirror images
/// (owner, 2026-08-19). A mirrored library glyph is not the library glyph — it would flip the
/// inductor's coils and move its polarity dot — so both cards now show the part as the schematic
/// would draw it. The two cards are already told apart by their headings, their values and their
/// position in the pane.</para>
///
/// <para><b>The world is the symbol's own bounding box</b>, fitted to the control, rather than a
/// fixed one this file declares. Each of the five glyphs is 400 units tall (they all reach both
/// pins, which is the family's pin contract) and between 60 and 210 wide, so fitting the box keeps
/// the vertical scale identical across all five — the picture changes shape between arrangements
/// without changing size, which is what makes the two cards comparable at a glance.</para>
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

    /// <summary>
    /// Breathing room around the glyph, in the symbol's own units (100 = one grid square).
    /// </summary>
    /// <remarks>
    /// On the narrow glyphs — the bare R is 60 units wide against 400 tall — the fit is decided by
    /// the HEIGHT, so this is what stops the top and bottom pins sitting hard against the control's
    /// edge. It is stated in world units rather than pixels so it scales with the drawing.
    /// </remarks>
    private const double GlyphPadding = 24.0;

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width <= 2 || b.Height <= 2) return;

        var symbol = BuiltInSymbols.Primitives(Pictogram.Symbol);
        var (minX, minY, maxX, maxY) = Extent(symbol.Primitives);
        double worldW = (maxX - minX) + 2 * GlyphPadding;
        double worldH = (maxY - minY) + 2 * GlyphPadding;
        if (worldW <= 0 || worldH <= 0) return;

        double scale = Math.Min(b.Width / worldW, b.Height / worldH);
        double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
        double ox = b.Width / 2.0, oy = b.Height / 2.0;
        Point P(double x, double y) => new(ox + (x - cx) * scale, oy + (y - cy) * scale);

        var brush = Stroke ?? Brushes.Gray;

        // Thin on purpose. The canvas's own weight would read as a blot at this size: these glyphs
        // draw about a fifth of schematic scale, and 16 world units of stroke would come out at
        // three pixels across a picture 40 wide.
        var pen = new Pen(brush, Math.Max(1.0, 8 * scale));

        Draw(ctx, pen, brush, symbol.Primitives, P);
    }

    /// <summary>Draws a symbol's primitive list through the supplied world→screen map.</summary>
    private static void Draw(
        DrawingContext ctx, IPen pen, IBrush brush,
        IEnumerable<SymbolPrimitive> primitives, Func<double, double, Point> T)
    {
        foreach (var prim in primitives)
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

    /// <summary>
    /// The box the primitives occupy. Curves are bounded by their control points — an
    /// over-estimate, which is the safe direction for a fit.
    /// </summary>
    internal static (double MinX, double MinY, double MaxX, double MaxY) Extent(
        IReadOnlyList<SymbolPrimitive> primitives)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Take(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var p in primitives)
        {
            switch (p)
            {
                case LinePrimitive l:      Take(l.X1, l.Y1); Take(l.X2, l.Y2); break;
                case PolylinePrimitive pl: foreach (var pt in pl.Points) Take(pt[0], pt[1]); break;
                case ArcPrimitive a:       Take(a.Cx - a.R, a.Cy - a.R); Take(a.Cx + a.R, a.Cy + a.R); break;
                case CirclePrimitive c:    Take(c.Cx - c.R, c.Cy - c.R); Take(c.Cx + c.R, c.Cy + c.R); break;
                case QuadCurvePrimitive q:
                    Take(q.P0X, q.P0Y); Take(q.CtrlX, q.CtrlY); Take(q.P2X, q.P2Y);
                    break;
            }
        }

        return minX > maxX ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
    }
}
