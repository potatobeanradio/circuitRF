using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Match;

/// <summary>
/// The Designer's ladder preview (match.md §9.3): the network drawn with circuitRF's own symbol
/// geometry, each element labelled, absorbed elements dimmed, out-of-range values red, and the
/// transform brackets beneath the products they act on.
/// </summary>
/// <remarks>
/// <b>The symbols come from <see cref="BuiltInSymbols"/>, not from a second set drawn here.</b> An
/// inductor in this window is the same glyph as an inductor on a schematic page, which is the whole
/// reason the brief asks for the renderer's own conventions — a preview whose capacitor looks
/// different from the editor's capacitor is a preview of something else.
///
/// <para>The built-in glyphs are VERTICAL (pins at (0, ±200)), which is what a shunt arm wants;
/// a series arm rotates them 90°, exactly as placing one rotated on a page would.</para>
///
/// <para><b>Every colour here is a <c>Schematic.*</c> role</b> (owner, 2026-08-19: "all components
/// and text should respect the Schematic. colours from the Color Theme"). The two exceptions are
/// deliberate and each says something the schematic has no way to say: <c>Match.Absorbed</c> dims an
/// element the external terminations supply, and <c>Match.Negative</c> reddens an unbuildable VALUE
/// — the value only, never the glyph.</para>
/// </remarks>
public sealed class MatchLadderPreview : Control
{
    /// <summary>The layout to draw.</summary>
    public static readonly StyledProperty<MatchLadderLayout?> LayoutProperty =
        AvaloniaProperty.Register<MatchLadderPreview, MatchLadderLayout?>(nameof(Layout));

    /// <summary>Light or dark, so the theme roles resolve to the right variant.</summary>
    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<MatchLadderPreview, bool>(nameof(IsDark));

    static MatchLadderPreview() => AffectsRender<MatchLadderPreview>(LayoutProperty, IsDarkProperty);

    /// <inheritdoc cref="LayoutProperty"/>
    public MatchLadderLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <inheritdoc cref="IsDarkProperty"/>
    public bool IsDark
    {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    /// <summary>
    /// World units of air to the left and right of the drawing. Wide enough for the interface pins,
    /// which reach a further <see cref="PinBodyLength"/> beyond each port.
    /// </summary>
    private const double AirX = 340.0;

    /// <summary>
    /// World units of air above and below. <b>Larger than <see cref="AirX"/>, and it has to be</b>: a
    /// series element's three-line label stacks UPWARD from the spine and reaches ~420 units above
    /// it, so an equal margin would clip the component-type line off the top of the pane.
    /// </summary>
    private const double AirY = 500.0;

    /// <summary>
    /// How far a built-in Pin glyph reaches back from its own terminal — the hexagon's far vertex is
    /// at local (−100, 0) and its terminal at (+100, 0) (<c>BuiltInSymbols.BuildPin</c>), so the body
    /// occupies 200 world units behind the point it connects at.
    /// </summary>
    public const double PinBodyLength = 200.0;

    /// <summary>Half the length of a symbol's own lead — the built-ins put their pins at ±200.</summary>
    public const double LeadHalf = 200.0;

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        var layout = Layout;
        if (layout is null || layout.Elements.Count == 0) return;
        var b = Bounds;
        if (b.Width <= 4 || b.Height <= 4) return;

        double worldW = layout.PortRightX - layout.PortLeftX + 2 * AirX;
        int bracketRows = layout.Brackets.Count == 0 ? 0 : layout.Brackets.Max(r => r.Row) + 1;
        double worldH = MatchLadderLayout.BracketY
                        + bracketRows * MatchLadderLayout.BracketRowPitch + 2 * AirY;

        double scale = Math.Min(b.Width / worldW, b.Height / worldH);
        double ox = (b.Width - worldW * scale) / 2.0 - (layout.PortLeftX - AirX) * scale;
        double oy = (b.Height - worldH * scale) / 2.0 + AirY * scale;

        Point P(double wx, double wy) => new(ox + wx * scale, oy + wy * scale);

        var variant = IsDark ? ColorVariant.Dark : ColorVariant.Light;
        Color Role(string role)
        {
            var c = ThemeService.Active.Resolve(role, variant);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        // The pane is a schematic, so it is painted like one — Schematic.Background, not the
        // window's own chrome colour (owner, 2026-08-19). Painted before anything else, over the
        // WHOLE control rather than the drawing's extent, so there is no band of host colour around
        // the edge of a network that does not fill its pane.
        ctx.FillRectangle(new SolidColorBrush(Role(ColorRole.SchematicBackground)), b);

        var wirePen = new Pen(new SolidColorBrush(Role(ColorRole.SchematicWire)), Math.Max(1.0, 12 * scale));
        double fontSize = Math.Max(8.0, 90 * scale);

        DrawWiring(ctx, layout, P, wirePen);

        // The four interface pins (§9.3, owner 2026-08-19): the two signal ports, plus the two
        // ground references an external circuit would tie to the rail. Drawn in the schematic's own
        // symbol colour, from the schematic's own Pin glyph, at the same size relative to a
        // component as the editor draws it — same world units, same symbol, no second geometry.
        var pinBrush = new SolidColorBrush(Role(ColorRole.SchematicSymbolLine));
        var pinPen = new Pen(pinBrush, Math.Max(1.0, 14 * scale));
        var dotBrush = new SolidColorBrush(Role(ColorRole.SchematicConnectedPin));
        DrawInterfacePins(ctx, layout, P, pinPen, pinBrush);

        foreach (var e in layout.Elements)
        {
            var brush = new SolidColorBrush(Role(e.ColorRoleKey));
            var pen = new Pen(brush, Math.Max(1.0, 14 * scale));
            DrawSymbol(ctx, e, P, pen, brush);
            DrawConnectedPinDots(ctx, e, P, scale, dotBrush);

            // Label: THREE lines, in the schematic's own three text roles (owner, 2026-08-19) —
            // component type, instance name, parameter value, exactly as a placed component labels
            // itself on a page. Three draws rather than one three-line string, because each line
            // carries a different role and only the value can be the thing that is wrong.
            var typeText = Text(e.Type == ElementType.L ? "L" : "C",
                                new SolidColorBrush(Role(ColorRole.SchematicComponentNameText)), fontSize);
            var name = Text(e.Name, new SolidColorBrush(Role(e.NameColorRoleKey)), fontSize);
            var value = Text(e.ValueText, new SolidColorBrush(Role(e.ValueColorRoleKey)), fontSize);

            if (e.IsShunt)
            {
                // A shunt arm labels to its right, where the drop leaves room; left-aligned, the way
                // the schematic editor stacks a component's own label block.
                var at = P(e.X + 130, e.Y - 200);
                ctx.DrawText(typeText, at);
                ctx.DrawText(name, new Point(at.X, at.Y + typeText.Height));
                ctx.DrawText(value, new Point(at.X, at.Y + typeText.Height + name.Height));
            }
            else
            {
                // A series arm labels above the spine, centred on its own column.
                var centre = P(e.X, e.Y - 420);
                ctx.DrawText(typeText, new Point(centre.X - typeText.Width / 2, centre.Y));
                ctx.DrawText(name, new Point(centre.X - name.Width / 2, centre.Y + typeText.Height));
                ctx.DrawText(value, new Point(centre.X - value.Width / 2,
                                              centre.Y + typeText.Height + name.Height));
            }
        }

        DrawBrackets(ctx, layout, P, scale, Role, fontSize);
    }

    /// <summary>
    /// The spine, the shunt drops and the ground rail.
    /// </summary>
    /// <remarks>
    /// <b>The spine is drawn in the GAPS between series elements, never straight through them</b>
    /// (owner-reported, 2026-08-19: "there are wires rendering underneath the series components").
    /// A built-in glyph carries its OWN leads out to ±200, so a full-width port-to-port line lays a
    /// second, thicker wire across every series body — visible as a bar through the capacitor's gap
    /// and along the inductor's coils. A schematic wire stops at the pin it connects to, and so does
    /// this one.
    /// </remarks>
    private static void DrawWiring(
        DrawingContext ctx, MatchLadderLayout layout, Func<double, double, Point> P, IPen wire)
    {
        double y = MatchLadderLayout.SpineY;

        double cursor = layout.PortLeftX;
        foreach (var e in layout.Elements.Where(e => !e.IsShunt).OrderBy(e => e.X))
        {
            double left = e.X - LeadHalf;
            if (left > cursor) ctx.DrawLine(wire, P(cursor, y), P(left, y));
            cursor = Math.Max(cursor, e.X + LeadHalf);
        }
        if (layout.PortRightX > cursor) ctx.DrawLine(wire, P(cursor, y), P(layout.PortRightX, y));

        var shunts = layout.Elements.Where(e => e.IsShunt).ToList();
        if (shunts.Count == 0) return;

        // The rail spans the WHOLE port range rather than just the shunt columns, so the two ground
        // pins sit directly under the two signal pins — which is what makes the drawing read as a
        // two-port with a common reference rather than as a floating stub.
        double gy = MatchLadderLayout.GroundY;
        ctx.DrawLine(wire, P(layout.PortLeftX, gy), P(layout.PortRightX, gy));
        foreach (var s in shunts)
        {
            ctx.DrawLine(wire, P(s.X, y), P(s.X, s.Y - LeadHalf));
            ctx.DrawLine(wire, P(s.X, s.Y + LeadHalf), P(s.X, gy));
        }
    }

    /// <summary>
    /// The four interface pins: port 1 and port 2 on the spine, and their two ground references at
    /// the ends of the rail — "where ground would be placed in an external circuit" (owner).
    /// </summary>
    /// <remarks>
    /// The ground pair is drawn only when the network HAS a ground rail. A ladder of nothing but
    /// series elements has no shunt arm, no rail, and therefore no reference terminal to mark —
    /// drawing two pins onto a rail that is not there would be a picture of a different circuit.
    /// </remarks>
    private static void DrawInterfacePins(
        DrawingContext ctx, MatchLadderLayout layout, Func<double, double, Point> P,
        IPen pen, IBrush brush)
    {
        DrawPin(ctx, P, pen, brush, layout.PortLeftX, MatchLadderLayout.SpineY, pointsRight: true);
        DrawPin(ctx, P, pen, brush, layout.PortRightX, MatchLadderLayout.SpineY, pointsRight: false);

        if (!layout.Elements.Any(e => e.IsShunt)) return;
        DrawPin(ctx, P, pen, brush, layout.PortLeftX, MatchLadderLayout.GroundY, pointsRight: true);
        DrawPin(ctx, P, pen, brush, layout.PortRightX, MatchLadderLayout.GroundY, pointsRight: false);
    }

    /// <summary>
    /// One built-in Pin glyph, its terminal landing exactly on (<paramref name="tipX"/>,
    /// <paramref name="tipY"/>) and its body reaching away from the network.
    /// </summary>
    private static void DrawPin(
        DrawingContext ctx, Func<double, double, Point> P, IPen pen, IBrush brush,
        double tipX, double tipY, bool pointsRight)
    {
        // Pin's own terminal is at local (+100, 0), so the glyph origin sits 100 behind the tip —
        // mirrored in x for the right-hand pin, exactly as placing one mirrored on a page would.
        double sx = pointsRight ? 1.0 : -1.0;
        double originX = tipX - sx * 100.0;

        DrawPrimitives(ctx, BuiltInSymbols.Primitives(SymbolKind.Pin), pen, brush,
                       (lx, ly) => P(originX + sx * lx, tipY + ly));
    }

    /// <summary>
    /// The filled marker on each element terminal, in <c>Schematic.ConnectedPin</c> — the same square
    /// dot the schematic editor puts on a connected port (<c>SchematicRenderer.DrawPortMarkers</c>),
    /// and for the same reason: a terminal with nothing on it reads as unconnected.
    /// </summary>
    private static void DrawConnectedPinDots(
        DrawingContext ctx, MatchLadderElement e, Func<double, double, Point> P,
        double scale, IBrush dot)
    {
        // Proportional to the drawing, with the same "never smaller than a couple of pixels" floor
        // the editor's own marker uses — at this zoom the editor's literal 4-world-unit half would
        // land under one pixel.
        double half = Math.Max(2.0, 16 * scale);

        foreach (var (wx, wy) in e.IsShunt
            ? new[] { (e.X, e.Y - LeadHalf), (e.X, e.Y + LeadHalf) }
            : new[] { (e.X - LeadHalf, e.Y), (e.X + LeadHalf, e.Y) })
        {
            var c = P(wx, wy);
            ctx.FillRectangle(dot, new Rect(c.X - half, c.Y - half, half * 2, half * 2));
        }
    }

    private static void DrawSymbol(
        DrawingContext ctx, MatchLadderElement e, Func<double, double, Point> P, IPen pen, IBrush brush)
    {
        var symbol = BuiltInSymbols.Primitives(e.Type == ElementType.L ? SymbolKind.Inductor : SymbolKind.Capacitor);
        bool rotate = !e.IsShunt;   // the built-ins are vertical; a series arm lies along the spine

        Point T(double lx, double ly)
        {
            double x = rotate ? -ly : lx;
            double y = rotate ?  lx : ly;
            return P(e.X + x, e.Y + y);
        }

        DrawPrimitives(ctx, symbol, pen, brush, T);
    }

    /// <summary>
    /// Walks one symbol's primitives through an arbitrary local→pixel map. Shared by the elements and
    /// by the interface pins so there is exactly one place that knows how each primitive kind draws.
    /// </summary>
    private static void DrawPrimitives(
        DrawingContext ctx, Symbol symbol, IPen pen, IBrush brush,
        Func<double, double, Point> T)
    {
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

                // A POLYGON is a closed polyline, and leaving it out is exactly why the four
                // interface pins drew as nothing (owner-reported, 2026-08-19): the Pin glyph is a
                // hexagon polygon plus one stem line, and the stem lands on the wire it connects to
                // — so the visible result of the missing case was no pin at all rather than half of
                // one. Nothing else this preview draws is a polygon, which is how it went unnoticed.
                case PolygonPrimitive pg:
                    for (int i = 0; i < pg.Points.Count; i++)
                    {
                        var q = pg.Points[(i + 1) % pg.Points.Count];
                        ctx.DrawLine(pen, T(pg.Points[i][0], pg.Points[i][1]), T(q[0], q[1]));
                    }
                    break;

                case ArcPrimitive a:
                    DrawPolyArc(ctx, pen, a, T);
                    break;

                case QuadCurvePrimitive q:
                    DrawPolyQuad(ctx, pen, q, T);
                    break;

                case CirclePrimitive c:
                    // The inductor's polarity dot. Radius is isotropic, so a rotation does not touch
                    // it; only its centre moves.
                    var centre = T(c.Cx, c.Cy);
                    double r = Math.Max(1.0, Math.Abs((T(c.Cx + c.R, c.Cy) - centre).X));
                    ctx.DrawEllipse(c.Filled ? brush : Brushes.Transparent, pen, centre, r, r);
                    break;
            }
        }
    }

    // Arcs and quadratics are flattened rather than converted to Avalonia geometry: the segment count
    // is fixed and small, the preview is never zoomed past a few hundred pixels per symbol, and a
    // flattened path rotates with the same T() every other primitive uses instead of needing its own
    // transform rule.
    private static void DrawPolyArc(
        DrawingContext ctx, IPen pen, ArcPrimitive a, Func<double, double, Point> T)
    {
        const int steps = 16;
        Point prev = default;
        for (int i = 0; i <= steps; i++)
        {
            double deg = a.StartDeg + a.SweepDeg * i / steps;
            double rad = deg * Math.PI / 180.0;
            var p = T(a.Cx + a.R * Math.Cos(rad), a.Cy + a.R * Math.Sin(rad));
            if (i > 0) ctx.DrawLine(pen, prev, p);
            prev = p;
        }
    }

    private static void DrawPolyQuad(
        DrawingContext ctx, IPen pen, QuadCurvePrimitive q, Func<double, double, Point> T)
    {
        const int steps = 16;
        Point prev = default;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps, u = 1 - t;
            double x = u * u * q.P0X + 2 * u * t * q.CtrlX + t * t * q.P2X;
            double y = u * u * q.P0Y + 2 * u * t * q.CtrlY + t * t * q.P2Y;
            var p = T(x, y);
            if (i > 0) ctx.DrawLine(pen, prev, p);
            prev = p;
        }
    }

    private static void DrawBrackets(
        DrawingContext ctx, MatchLadderLayout layout, Func<double, double, Point> P,
        double scale, Func<string, Color> role, double fontSize)
    {
        if (layout.Brackets.Count == 0) return;
        var brush = new SolidColorBrush(role(ColorRole.MatchBracket));
        var pen = new Pen(brush, Math.Max(1.0, 12 * scale));

        foreach (var br in layout.Brackets)
        {
            double y = MatchLadderLayout.BracketY + br.Row * MatchLadderLayout.BracketRowPitch;
            ctx.DrawLine(pen, P(br.X0, y), P(br.X1, y));
            ctx.DrawLine(pen, P(br.X0, y), P(br.X0, y - 60));
            ctx.DrawLine(pen, P(br.X1, y), P(br.X1, y - 60));
            var label = Text(br.Label, brush, fontSize);
            ctx.DrawText(label, new Point(P((br.X0 + br.X1) / 2, y + 20).X - label.Width / 2,
                                          P((br.X0 + br.X1) / 2, y + 20).Y));
        }
    }

    private static FormattedText Text(string text, IBrush brush, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
}
