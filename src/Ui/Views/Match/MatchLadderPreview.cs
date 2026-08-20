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

    /// <summary>World units of air around the drawing.</summary>
    private const double Air = 260.0;

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        var layout = Layout;
        if (layout is null || layout.Elements.Count == 0) return;
        var b = Bounds;
        if (b.Width <= 4 || b.Height <= 4) return;

        double worldW = layout.PortRightX - layout.PortLeftX + 2 * Air;
        int bracketRows = layout.Brackets.Count == 0 ? 0 : layout.Brackets.Max(r => r.Row) + 1;
        double worldH = MatchLadderLayout.BracketY
                        + bracketRows * MatchLadderLayout.BracketRowPitch + 2 * Air;

        double scale = Math.Min(b.Width / worldW, b.Height / worldH);
        double ox = (b.Width - worldW * scale) / 2.0 - (layout.PortLeftX - Air) * scale;
        double oy = (b.Height - worldH * scale) / 2.0 + Air * scale;

        Point P(double wx, double wy) => new(ox + wx * scale, oy + wy * scale);

        var variant = IsDark ? ColorVariant.Dark : ColorVariant.Light;
        Color Role(string role)
        {
            var c = ThemeService.Active.Resolve(role, variant);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        var wirePen = new Pen(new SolidColorBrush(Role(ColorRole.SchematicWire)), Math.Max(1.0, 12 * scale));
        var textBrush = new SolidColorBrush(Role(ColorRole.SchematicInstanceNameText));
        double fontSize = Math.Max(8.0, 90 * scale);

        DrawWiring(ctx, layout, P, wirePen);

        foreach (var e in layout.Elements)
        {
            var brush = new SolidColorBrush(Role(e.ColorRoleKey));
            var pen = new Pen(brush, Math.Max(1.0, 14 * scale));
            DrawSymbol(ctx, e, P, pen, brush);

            // Label: the instance-relative name over the value, beside the element. A series element
            // labels above the spine; a shunt one labels to its right, where the drop leaves room.
            double lx = e.IsShunt ? e.X + 110 : e.X - 150;
            double ly = e.IsShunt ? e.Y - 120 : e.Y - 300;
            DrawText(ctx, $"{e.Name}\n{e.ValueText}", P(lx, ly), textBrush, fontSize);
        }

        DrawBrackets(ctx, layout, P, scale, Role, fontSize);
    }

    private static void DrawWiring(
        DrawingContext ctx, MatchLadderLayout layout, Func<double, double, Point> P, IPen wire)
    {
        // The spine, port to port, and one drop per shunt element down to the ground rail.
        ctx.DrawLine(wire, P(layout.PortLeftX, MatchLadderLayout.SpineY),
                           P(layout.PortRightX, MatchLadderLayout.SpineY));

        var shunts = layout.Elements.Where(e => e.IsShunt).ToList();
        if (shunts.Count > 0)
        {
            double gy = MatchLadderLayout.GroundY;
            ctx.DrawLine(wire, P(shunts.Min(s => s.X) - 120, gy), P(shunts.Max(s => s.X) + 120, gy));
            foreach (var s in shunts)
            {
                ctx.DrawLine(wire, P(s.X, MatchLadderLayout.SpineY), P(s.X, s.Y - 200));
                ctx.DrawLine(wire, P(s.X, s.Y + 200), P(s.X, gy));
            }
        }

        // Port stubs.
        foreach (var (x, label) in new[] { (layout.PortLeftX, "1"), (layout.PortRightX, "2") })
            ctx.DrawEllipse(Brushes.Transparent, wire, P(x, MatchLadderLayout.SpineY), 3, 3);
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
                    DrawPolyArc(ctx, pen, a, T, rotate);
                    break;

                case QuadCurvePrimitive q:
                    DrawPolyQuad(ctx, pen, q, T);
                    break;

                case CirclePrimitive c:
                    // The inductor's polarity dot. Radius is isotropic, so the rotation does not
                    // touch it; only its centre moves.
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
        DrawingContext ctx, IPen pen, ArcPrimitive a, Func<double, double, Point> T, bool rotate)
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
            DrawText(ctx, br.Label, P((br.X0 + br.X1) / 2 - 60, y + 20), brush, fontSize);
        }
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        var ft = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, brush);
        ctx.DrawText(ft, at);
    }
}
