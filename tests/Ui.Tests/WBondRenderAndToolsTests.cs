using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's 2026-08-16 rendering-and-tools round: round segment joins at true diameter, vertex
/// dots that keep growing with zoom, the vertex accent colour, the envelope's outline, the Wire tool
/// in the profile view, the T key, and the selection gate on the toolbar's five selection commands.
///
/// <para>The rendering half is asserted with PIXEL oracles wherever one exists — a round join and a
/// butt join differ at a nameable pixel, and asserting that is worth far more than asserting that a
/// <c>StrokeCap</c> property was set. Only the things a pixel cannot see (which handler a key runs,
/// which binding gates a button) fall back to a source scan.</para>
/// </summary>
public class WBondRenderAndToolsTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    // ════════════════════════════════════════════════════════ round joins at true diameter

    /// <summary>
    /// <b>The outer corner of a bend is FILLED</b> (owner: "when wires are rendered at true diameter,
    /// the segments look joined badly").
    ///
    /// <para>The oracle is the one pixel the two join styles disagree about. A wire is drawn segment
    /// by segment — it has to be, since a single segment can be selected and recoloured on its own —
    /// so with butt caps the two rectangles meet at the vertex and leave a wedge of background on the
    /// OUTSIDE of the turn. A round cap is a disc of radius w/2 centred on the vertex, which covers
    /// it. This probes a point 0.9 × w/2 from the vertex along the outward bisector: inside the disc,
    /// outside both rectangles.</para>
    /// </summary>
    [Fact]
    public void AtTrueDiameter_TheOuterCornerOfABendIsFilled()
    {
        // An L: east along +x, then north along +y, with a fat wire so the join is many pixels wide.
        var design = LShapedWire(diameterMils: 20.0);

        var viewport = Framed();
        const int size = CanvasSize;
        double zoom = viewport.Zoom;

        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(SKColors.Black);

        WBondRenderer.Draw(surface.Canvas, design, viewport, WBondRenderTheme.Fallback,
                           thickness: WireThicknessMode.TrueDiameter);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        // The bend's vertex, in world nanometres, and the wire's half-width in pixels.
        var bendNm = design.AllWires().First().Points[1];
        float vx = (float)viewport.WorldToScreenX(bendNm.X);
        float vy = (float)viewport.WorldToScreenY(bendNm.Y);
        double halfWidthPx = WBondUnits.ToNm(20.0, WBondUnit.Mil) * zoom / 2.0;

        Assert.True(halfWidthPx > 6, $"The fixture must be many pixels wide to have an outside corner; it is {halfWidthPx:0.0}.");

        // Screen y grows DOWNWARD, so "outside the turn" — world (+x, −y) — is screen (+x, +y).
        //
        // 0.95 × the half-width along the bisector: inside the round cap's disc, outside BOTH
        // rectangles, and — deliberately — outside the vertex dot as well, whose radius is
        // VertexToWireDiameterRatio × the half-width and therefore 0.6 of it. That last clearance is
        // what makes this a test of the JOIN rather than of the bead sitting on it.
        double reach = 0.95 * halfWidthPx / Math.Sqrt(2.0);
        Assert.True(0.95 > WBondRenderer.VertexToWireDiameterRatio,
                    "The vertex dot now reaches past the probe; this test would be measuring the dot.");

        var corner = bitmap.GetPixel((int)Math.Round(vx + reach), (int)Math.Round(vy + reach));

        Assert.True(corner.Alpha > 0 && (corner.Red + corner.Green + corner.Blue) > 60,
                    $"The outer corner of the bend is background ({corner}) — the join is not rounded.");

        // …and the sanity half: a point comfortably OUTSIDE the round cap is still background, so the
        // test above cannot be passing because the whole canvas is painted.
        double beyond = 1.5 * halfWidthPx / Math.Sqrt(2.0);
        var outside = bitmap.GetPixel((int)Math.Round(vx + beyond), (int)Math.Round(vy + beyond));
        Assert.True((outside.Red + outside.Green + outside.Blue) < 60,
                    $"A point well outside the wire is painted ({outside}) — the probe proves nothing.");
    }

    private const int CanvasSize = 400;

    /// <summary>
    /// A viewport that actually FRAMES <see cref="LShapedWire"/> — the L runs 0..100 mil on both
    /// axes, so at 1,000 DBU/µm the world is 2.54e6 units across and the zoom has to be ~1e-4 px per
    /// unit. A plausible-looking round number puts the bend thousands of pixels off canvas, where
    /// every probe reads transparent and every pixel oracle passes or fails for the wrong reason.
    /// </summary>
    private static LayoutViewport Framed()
    {
        double extent = WBondUnits.ToNm(100.0, WBondUnit.Mil);
        double zoom = CanvasSize / (2.1 * extent);
        double pan = extent / 2.0 - CanvasSize / (2.0 * zoom);

        return new LayoutViewport(pan, pan, zoom, CanvasSize, CanvasSize);
    }

    private static WBondDesign LShapedWire(double diameterMils)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };

        array.Wires.Add(new Wire
        {
            DiameterNm = WBondUnits.ToNm(diameterMils, WBondUnit.Mil),
            Material = "Gold",
            Points =
            {
                Point3.Mils(0, 0, 0),
                Point3.Mils(100, 0, 0),
                Point3.Mils(100, 100, 0),
            },
        });

        design.Arrays.Add(array);
        return design;
    }

    // ════════════════════════════════════════════════════════ segment width and vertex dots

    /// <summary>
    /// <b>The segment grows with zoom, with no upper clamp</b> (owner, 2026-08-16: "also want the wire
    /// segment to render larger when zoomed in, just like what you did with the wire vertex").
    /// </summary>
    [Fact]
    public void AWireSegment_KeepsGrowingWithZoom()
    {
        var theme = WBondRenderTheme.Fallback;
        long oneMil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        float previous = 0f;
        foreach (double pixelsPerNm in new[] { 1e-4, 1e-3, 1e-2, 1e-1, 1.0, 10.0 })
        {
            float w = WBondRenderer.StrokeWidthPx(oneMil, pixelsPerNm, theme, WireThicknessMode.TrueDiameter);
            Assert.True(w > previous, $"The segment stopped growing at {pixelsPerNm} px/nm: {w} <= {previous}.");
            previous = w;
        }

        Assert.True(WBondRenderer.StrokeWidthPx(oneMil, 1e4, theme, WireThicknessMode.TrueDiameter) > 1e5);
    }

    /// <summary>
    /// <b>A vertex dot keeps growing with it</b> (owner: "if I zoom in very far, the vertex stops
    /// changing size"), and it does so because it is measured against the segment — the two cannot
    /// come apart.
    /// </summary>
    [Fact]
    public void AVertexDot_KeepsGrowingWithTheSegment()
    {
        var theme = WBondRenderTheme.Fallback;
        long oneMil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        float previous = 0f;
        foreach (double pixelsPerNm in new[] { 1e-3, 1e-2, 1e-1, 1.0, 10.0 })
        {
            float stroke = WBondRenderer.StrokeWidthPx(oneMil, pixelsPerNm, theme, WireThicknessMode.TrueDiameter);
            float r = WBondRenderer.VertexRadiusPx(stroke, WireThicknessMode.TrueDiameter);

            Assert.True(r > previous, $"The dot stopped growing at {pixelsPerNm} px/nm: {r} <= {previous}.");
            previous = r;
        }

        // It really is unbounded — the old defect was a constant, and a clamp would be the same
        // defect with a larger constant.
        Assert.True(WBondRenderer.VertexRadiusPx(1e6f, WireThicknessMode.TrueDiameter) > 1e5);
    }

    /// <summary>
    /// <b>The dot and the segment keep the SAME relative size at every zoom</b> (owner, 2026-08-16:
    /// "the relative sizes between the wire vertex and wire segment change as I zoom in or out —
    /// their relative size should be independent of zoom level").
    ///
    /// <para>The cause was two independent floors: the segment's, and a separate one on the dot. They
    /// bind at different zooms, so below the crossover the dot sat still while the line went on
    /// shrinking and the ratio ran away. The sweep below crosses the segment's floor deliberately —
    /// it starts where the wire is sub-pixel and ends where it is thousands of pixels wide.</para>
    /// </summary>
    [Theory]
    [InlineData(WireThicknessMode.Thin)]
    [InlineData(WireThicknessMode.TrueDiameter)]
    public void TheDotToSegmentRatio_IsTheSameAtEveryZoom(WireThicknessMode mode)
    {
        var theme = WBondRenderTheme.Fallback;
        long diameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        double? first = null;
        foreach (double pixelsPerNm in new[] { 1e-9, 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 1e-1, 1.0, 100.0 })
        {
            float stroke = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, mode);
            float radius = WBondRenderer.VertexRadiusPx(stroke, mode);

            double ratio = 2.0 * radius / stroke;
            first ??= ratio;

            Assert.Equal(first.Value, ratio, 5);
        }

        // The sweep really did cross the floor, or it proves nothing about the case that was broken:
        // at the low end the wire is sub-pixel and pinned, at the high end it is thousands wide.
        float floored = WBondRenderer.StrokeWidthPx(diameterNm, 1e-9, theme, mode);
        float wide = WBondRenderer.StrokeWidthPx(diameterNm, 100.0, theme, mode);

        Assert.Equal(floored, WBondRenderer.StrokeWidthPx(diameterNm, 1e-12, theme, mode));
        Assert.True(wide > 1000f * floored);
    }

    /// <summary>
    /// <b>The dot is WIDER than a thin segment, and narrower than a true-diameter one</b> (owner,
    /// 2026-08-16: "vertex width should be wider than segment at all zoom levels — unless segments are
    /// at their true diameter").
    ///
    /// <para>Both fall out of one rule: the dot is three fifths of the wire's APPARENT diameter in
    /// either mode. Thin draws the line at a third of that, so the dot is 1.8× it; true diameter draws
    /// the line at all of it, so the dot is a bead inside the wire — which it has to be there, or it
    /// would cover the segment join at that very vertex.</para>
    /// </summary>
    [Theory]
    [InlineData(1e-9)]   // floored
    [InlineData(1e-4)]
    [InlineData(1e-2)]
    [InlineData(1.0)]
    public void TheDot_IsWiderThanAThinSegmentAndInsideATrueDiameterOne(double pixelsPerNm)
    {
        var theme = WBondRenderTheme.Fallback;
        long diameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        float thin = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, WireThicknessMode.Thin);
        float real = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, WireThicknessMode.TrueDiameter);

        Assert.True(2.0 * WBondRenderer.VertexRadiusPx(thin, WireThicknessMode.Thin) > thin,
                    "The dot is not wider than the thin segment it marks.");
        Assert.True(2.0 * WBondRenderer.VertexRadiusPx(real, WireThicknessMode.TrueDiameter) < real,
                    "The dot covers the join on a true-diameter wire.");
    }

    /// <summary>
    /// The dot is the SAME SIZE in both modes — three fifths of the apparent diameter either way — so
    /// toggling Ø changes how fat the wire looks without moving the handle the user is aiming at.
    /// It is also what lets the mode-free hit test match what is drawn in both.
    /// </summary>
    [Fact]
    public void TheDot_IsTheSameSizeInBothModes()
    {
        var theme = WBondRenderTheme.Fallback;
        long diameterNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        const double pixelsPerNm = 1e-3;

        float thin = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, WireThicknessMode.Thin);
        float real = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, WireThicknessMode.TrueDiameter);

        Assert.Equal(WBondRenderer.VertexRadiusPx(real, WireThicknessMode.TrueDiameter),
                     WBondRenderer.VertexRadiusPx(thin, WireThicknessMode.Thin), 3);
    }

    /// <summary>
    /// Zoomed OUT the SEGMENT collapses to its floor rather than to nothing — the one thing the old
    /// constant got right. The dot has no floor of its own any more; it inherits this one, which is
    /// exactly what keeps the ratio constant.
    /// </summary>
    [Fact]
    public void TheSegmentNeverFallsBelowItsFloor()
    {
        var theme = WBondRenderTheme.Fallback;
        long oneMil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        Assert.Equal(1.0f, WBondRenderer.StrokeWidthPx(oneMil, 0.0, theme, WireThicknessMode.TrueDiameter));
        Assert.Equal(theme.LineWidthPx, WBondRenderer.StrokeWidthPx(oneMil, 0.0, theme, WireThicknessMode.Thin));

        Assert.Equal(0f, WBondRenderer.VertexRadiusPx(0f, WireThicknessMode.Thin));
    }

    /// <summary>
    /// <b>Both modes grow with zoom — the segment AND the dot on it</b> (owner, 2026-08-16: "as I zoom
    /// in, the wire segment and wire vertex is supposed to render bigger").
    ///
    /// <para>Thin mode used to be a fixed screen width, so zooming in enlarged everything on the
    /// canvas except the wires. What the two modes differ in is WIDTH — a third of the real diameter
    /// against the real diameter — not whether they scale.</para>
    /// </summary>
    [Theory]
    [InlineData(WireThicknessMode.Thin)]
    [InlineData(WireThicknessMode.TrueDiameter)]
    public void InEitherMode_BothTheSegmentAndTheDotGrowWithZoom(WireThicknessMode mode)
    {
        var theme = WBondRenderTheme.Fallback;
        long diameterNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);

        float lastStroke = 0f, lastRadius = 0f;
        foreach (double pixelsPerNm in new[] { 1e-4, 1e-3, 1e-2, 1e-1, 1.0 })
        {
            float stroke = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, mode);
            float radius = WBondRenderer.VertexRadiusPx(stroke, mode);

            Assert.True(stroke > lastStroke, $"{mode}: the segment stopped growing at {pixelsPerNm} px/nm.");
            Assert.True(radius > lastRadius, $"{mode}: the dot stopped growing at {pixelsPerNm} px/nm.");

            lastStroke = stroke;
            lastRadius = radius;
        }
    }

    /// <summary>
    /// …and the two modes are still genuinely different: thin is a fixed FRACTION of the real
    /// diameter, so Ø continues to mean "actual size" and clearance stays judgeable by eye (WB22a).
    /// </summary>
    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    public void ThinMode_IsAFractionOfTheRealDiameter(double pixelsPerNm)
    {
        var theme = WBondRenderTheme.Fallback;
        long diameterNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);

        float thin = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, WireThicknessMode.Thin);
        float real = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, WireThicknessMode.TrueDiameter);

        Assert.True(thin < real, "Thin mode draws as wide as the real diameter — the Ø toggle says nothing.");

        // Compared as a RATIO: these are floats and at a mil-per-pixel zoom the widths run to six
        // figures, where an absolute tolerance is measuring float mantissa rather than the rule.
        Assert.Equal(WBondRenderer.ThinStrokeFraction, thin / (double)real, 5);
        Assert.InRange(WBondRenderer.ThinStrokeFraction, 0.15, 0.6);
    }

    /// <summary>
    /// <b>The dot sits INSIDE the wire, not over it.</b> A dot as wide as the wire — or wider — covers
    /// the segment join at that very vertex, which would hide the rounded joins asked for in the same
    /// breath and would draw a true-diameter wire as a chain of beads. Visibility is the CONTRAST's
    /// job, not the size's.
    /// </summary>
    [Theory]
    [InlineData(1e-3)]
    [InlineData(1e-2)]
    [InlineData(1.0)]
    public void AVertexDot_SitsInsideItsWireRatherThanOverIt(double pixelsPerNm)
    {
        var theme = WBondRenderTheme.Fallback;
        long diameterNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);   // fat enough to be past the floor

        float stroke = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, WireThicknessMode.TrueDiameter);
        float radius = WBondRenderer.VertexRadiusPx(stroke, WireThicknessMode.TrueDiameter);
        double wireHalfWidth = stroke / 2.0;

        Assert.True(radius < wireHalfWidth, $"A {radius} px dot covers the join on a {wireHalfWidth} px half-width wire.");

        // …and big enough to see: over half the wire's width.
        Assert.True(radius > wireHalfWidth / 2.0);
        Assert.InRange(WBondRenderer.VertexToWireDiameterRatio, 0.4, 0.95);
    }

    /// <summary>
    /// <b>The profile view honours the thickness mode</b> — it used to draw every wire at the constant
    /// hairline whatever Ø said, because the canvas's own <c>Thickness</c> was never passed through.
    /// That is the report: a wire that grew with zoom in the layout view stayed 1.5 px in the profile
    /// view, with a vertex dot that scaled anyway.
    /// </summary>
    [Fact]
    public void TheProfileView_DrawsAtTrueDiameterWhenAsked()
    {
        var theme = WBondRenderTheme.Fallback;
        var design = BandDesign();

        int hairline = ProfileWirePixels(design, theme, WireThicknessMode.Thin);
        int fat = ProfileWirePixels(design, theme, WireThicknessMode.TrueDiameter);

        Assert.True(fat > hairline * 2,
                    $"True diameter drew {fat} px against the hairline's {hairline} — the mode is not reaching the profile view.");

        // …and the canvas passes its own property through, so the toolbar's Ø actually arrives.
        Assert.Contains("thickness: _thickness", Read("src/Ui/Controls/WBondProfileCanvas.cs"), StringComparison.Ordinal);
    }

    /// <summary>Counts wire-coloured pixels in one profile render.</summary>
    private static int ProfileWirePixels(WBondDesign design, WBondRenderTheme theme, WireThicknessMode thickness)
    {
        const int size = 600;
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(SKColors.Black);

        WBondRenderer.DrawProfile(
            surface.Canvas, design, theme,
            s => (float)(s / 4000.0), z => (float)(size - z / 2000.0),
            pixelsPerNm: 1.0 / 4000.0, thickness: thickness);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        int lit = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (Math.Abs(px.Red - theme.Wire.Red) < 40
                    && Math.Abs(px.Green - theme.Wire.Green) < 40
                    && Math.Abs(px.Blue - theme.Wire.Blue) < 40) lit++;
            }

        return lit;
    }

    // ════════════════════════════════════════════════════════ the vertex colour

    /// <summary>
    /// <c>wBond.WireVertex</c> is a real role in both variants, and it is an ACCENT — not the wire's
    /// own colour, which is what made the dots invisible on the wire.
    /// </summary>
    [Theory]
    [InlineData(ColorVariant.Light)]
    [InlineData(ColorVariant.Dark)]
    public void TheVertexRole_IsADistinctAccentInBothVariants(ColorVariant variant)
    {
        var theme = WBondRenderTheme.FromTheme(ColorTheme.BuiltIn, variant);

        Assert.NotEqual(theme.Wire, theme.Vertex);
        Assert.NotEqual(theme.InputEnd, theme.Vertex);

        // "Distinct" as a number, not as an inequality: a vertex one shade off the wire would satisfy
        // NotEqual and be exactly as invisible as before.
        int distance = Math.Abs(theme.Wire.Red - theme.Vertex.Red)
                     + Math.Abs(theme.Wire.Green - theme.Vertex.Green)
                     + Math.Abs(theme.Wire.Blue - theme.Vertex.Blue);

        Assert.True(distance > 150, $"The vertex accent is only {distance} away from the wire colour.");
    }

    /// <summary>The role is in the shared vocabulary, so the theme editor and every `.ccolor` see it.</summary>
    [Fact]
    public void TheVertexRole_IsInTheSharedRoleList()
    {
        Assert.Contains(ColorRole.WBondWireVertex, ColorRole.All);
        Assert.Equal("wBond.WireVertex", ColorRole.WBondWireVertex);
    }

    /// <summary>
    /// The layout overlay actually PAINTS the accent — the role existing and the renderer using it
    /// are two different claims, and only the second one is visible to a user.
    /// </summary>
    [Fact]
    public void TheOverlay_PaintsItsVerticesInTheAccent()
    {
        var design = LShapedWire(diameterMils: 4.0);
        var theme = WBondRenderTheme.Fallback;

        var viewport = Framed();
        const int size = CanvasSize;

        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(SKColors.Black);
        WBondRenderer.Draw(surface.Canvas, design, viewport, theme);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        int accent = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (Math.Abs(px.Red - theme.Vertex.Red) < 20
                    && Math.Abs(px.Green - theme.Vertex.Green) < 20
                    && Math.Abs(px.Blue - theme.Vertex.Blue) < 20) accent++;
            }

        // Two of the three points: the input foot keeps its own colour (WB3).
        Assert.True(accent > 0, "No vertex was drawn in the accent colour.");
    }

    // ════════════════════════════════════════════════════════ the envelope's outline

    /// <summary>
    /// <b>The envelope has a visible edge</b> (owner: "add a thin border around the envelope
    /// rendering that has same color but less transparency").
    ///
    /// <para>The oracle is that the band's boundary is a DIFFERENT pixel from its interior — with no
    /// outline the fill is uniform, so the two are identical. Both are sampled from the same band, so
    /// this cannot pass by accident of one of them landing on a wire.</para>
    /// </summary>
    [Fact]
    public void TheEnvelope_IsDrawnWithAnOutlineDistinctFromItsFill()
    {
        var theme = WBondRenderTheme.Fallback;

        // Five bound wires at three loop heights, so the band has real thickness to have an edge on.
        var design = BandDesign();

        const int size = 600;
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(SKColors.Black);

        float SpanToScreen(double s) => (float)(s / 4000.0);
        float ZToScreen(double z) => (float)(size - z / 2000.0);

        WBondRenderer.DrawProfile(surface.Canvas, design, theme, SpanToScreen, ZToScreen);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        // Walk a column down through the band and find its topmost painted row. The outline is 1.25 px
        // wide and antialiased, so the FIRST painted row is its outer fringe — a partial coverage that
        // is darker than the fill, not brighter. The outline's core is the brightest of the first few
        // rows; the fill is sampled well clear of it.
        int column = size / 2;
        int top = -1;
        for (int y = 0; y < size && top < 0; y++)
            if (Lit(bitmap.GetPixel(column, y))) top = y;

        Assert.True(top >= 0 && top + 20 < size, "The envelope was not drawn at all.");

        int edge = Enumerable.Range(top, 4).Max(y => (int)bitmap.GetPixel(column, y).Green);
        int fill = bitmap.GetPixel(column, top + 20).Green;

        Assert.True(fill > 0, "There is no band under the edge to compare against.");
        Assert.True(edge > fill,
                    $"The band's edge (green {edge}) is no more opaque than its fill (green {fill}) — there is no outline.");

        static bool Lit(SKColor px) => px.Red + px.Green + px.Blue > 8;
    }

    /// <summary>
    /// <b>A wire outside the band is not a glitch</b> (owner: "some wires in the profile view are
    /// rendered outside the envelope rendering").
    ///
    /// <para>The band spans an array's PROFILE-BOUND members only. A wire detached from the profile
    /// is drawn individually and is legitimately outside it — which is what the new outline makes
    /// visible. This pins the rule so nobody later "fixes" the band by including free wires in it and
    /// silently changes what it claims.</para>
    /// </summary>
    [Fact]
    public void TheEnvelope_CoversTheProfileBoundMembersOnly()
    {
        var design = BandDesign();
        var array = design.Arrays[0];

        var before = ProfileEnvelope.Build(array);
        Assert.Equal(array.Wires.Count, before.BoundWires.Count);
        Assert.Empty(before.FreeWires);

        // Detaching one wire moves it OUT of the envelope's own membership — it is still drawn, just
        // no longer described by the band.
        array.Wires[0].ProfileBinding = null;

        var after = ProfileEnvelope.Build(array);
        Assert.Equal(array.Wires.Count - 1, after.BoundWires.Count);
        Assert.Equal([0], after.FreeWires);
    }

    private static WBondDesign BandDesign()
    {
        var design = new WBondDesign();
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        design.Profiles.Add(profile);

        var array = new WireArray { Name = "G1", Profile = profile.Name };
        for (int w = 0; w < 5; w++)
        {
            var wire = profile.CreateWire(Point3.Mils(0, w * 6, 4), Point3.Mils(100, w * 6, 1),
                                          WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold");

            // Vary the height so the band is not degenerate — a zero-thickness band has no edge.
            for (int i = 1; i < wire.Points.Count - 1; i++)
            {
                var p = wire.Points[i];
                wire.Points[i] = new Point3(p.X, p.Y, p.Z + WBondUnits.ToNm(w * 2.0, WBondUnit.Mil));
            }

            array.Wires.Add(wire);
        }

        design.Arrays.Add(array);
        return design;
    }

    // ════════════════════════════════════════════════════════ the Wire tool in the profile view

    /// <summary>
    /// <b>A fixed plane has an exact inverse</b> — the profile view's span is <c>x·cos θ + y·sin θ</c>,
    /// so a point placed on the view direction projects back to the span it was clicked at. That
    /// round trip is what makes drawing a wire from this view mean anything.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(10.0)]
    [InlineData(-37.5)]
    public void UnprojectingAProfileClick_RoundTripsThroughTheProjection(double degrees)
    {
        double azimuth = degrees * Math.PI / 180.0;

        long spanNm = WBondUnits.ToNm(73.0, WBondUnit.Mil);
        long zNm = WBondUnits.ToNm(11.0, WBondUnit.Mil);

        var start = ProfileProjection.Unproject(spanNm, zNm, ProfileProjection.SpanMode.Absolute, azimuth);
        var end = ProfileProjection.Unproject(spanNm + 100_000, zNm, ProfileProjection.SpanMode.Absolute, azimuth);

        Assert.NotNull(start);
        Assert.NotNull(end);

        // Project the placed point back through the SAME projection the canvas draws with.
        var wire = new Wire { Points = { start!.Value, end!.Value } };
        var back = ProfileProjection.Project(wire, 0, ProfileProjection.SpanMode.Absolute, azimuth);

        Assert.Equal(spanNm, back.Span, 0);
        Assert.Equal(zNm, back.Z, 0);
    }

    /// <summary>
    /// <b>AUTO and Normalised have no inverse, and the refusal is the honest answer.</b> Under AUTO
    /// each wire is drawn on its own chord, so a span names a different direction for every wire and
    /// none at all for one that does not exist yet.
    /// </summary>
    [Fact]
    public void UnprojectingUnderAutoOrNormalisedSpan_IsRefused()
    {
        Assert.Null(ProfileProjection.Unproject(1000, 2000, ProfileProjection.SpanMode.Absolute, null));
        Assert.Null(ProfileProjection.Unproject(1000, 2000, ProfileProjection.SpanMode.Normalised, 0.0));
        Assert.Null(ProfileProjection.Unproject(1000, 2000, ProfileProjection.SpanMode.Normalised, null));
    }

    /// <summary>
    /// The profile canvas arms the Wire tool and places through the shared <c>AddWire</c> — it had no
    /// draw path at all, which is the reported bug, and a second placement path would be a second
    /// chance to get the profile binding and the undo entry wrong.
    /// </summary>
    [Fact]
    public void TheProfileCanvas_HasTheWireTool()
    {
        var canvas = Read("src/Ui/Controls/WBondProfileCanvas.cs");

        Assert.Contains("public bool WireDrawArmed", canvas, StringComparison.Ordinal);
        Assert.Contains("ProfileProjection.Unproject", canvas, StringComparison.Ordinal);
        Assert.Contains("_viewModel.AddWire(", canvas, StringComparison.Ordinal);
        Assert.Contains("ReportRefusal", canvas, StringComparison.Ordinal);

        // …and the view pushes the active tool into it, through the profile view's own pass-through
        // (WB39a/M3 made that canvas a control of its own, hosted twice). Without this the property is
        // dead code and the bug is unchanged.
        Assert.Contains("ProfileCanvas.WireDrawArmed",
                        Read("src/Ui/Views/WBond/WBondProfileView.axaml.cs"), StringComparison.Ordinal);
        Assert.Contains("ProfileView.WireDrawArmed = _bound?.ActiveTool == WBondTool.DrawWire",
                        Read("src/Ui/Views/WBond/WBondEditorView.axaml.cs"), StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════ the selection gate

    /// <summary>
    /// <b>Nothing selected means the selection commands are disabled</b> (owner: Straighten and
    /// Transform were live and silently did nothing).
    /// </summary>
    [Fact]
    public void TheSelectionGate_FollowsTheSelection()
    {
        var vm = new WBondViewModel(SelectableDesign());
        var document = new WBondDocumentViewModel(vm);

        Assert.False(document.HasSelection);

        int changes = 0;
        document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WBondDocumentViewModel.HasSelection)) changes++;
        };

        vm.SelectAllWires();
        Assert.True(document.HasSelection);
        Assert.True(changes > 0, "Nothing told the toolbar the selection changed, so the buttons never re-enable.");

        vm.ClearSelection();
        Assert.False(document.HasSelection);
    }

    /// <summary>A partial selection — some vertices — still counts: those commands act on it.</summary>
    [Fact]
    public void APartialSelection_CountsAsASelection()
    {
        var vm = new WBondViewModel(SelectableDesign());
        var document = new WBondDocumentViewModel(vm);

        vm.Selection = new WireSelection { Points = { new PointRef(0, 1) } };
        Assert.True(document.HasSelection);
    }

    /// <summary>All five selection commands are gated, and dim their icon while disabled.</summary>
    [Fact]
    public void AllFiveSelectionCommands_AreGatedAndDimmed()
    {
        var xaml = Read("src/Ui/Views/WBond/WBondEditorView.axaml");

        foreach (var name in new[] { "ReverseBtn", "StraightenBtn", "ReapplyProfileBtn",
                                     "TransformBtn", "DetachBtn" })
        {
            int at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at >= 0, $"{name} is gone from the toolbar.");

            string element = xaml[at..xaml.IndexOf("</Button>", at, StringComparison.Ordinal)];
            Assert.Contains("IsEnabled=\"{Binding ViewModel.HasSelection}\"", element, StringComparison.Ordinal);
            Assert.Contains("Classes=\"SelectionBtn\"", element, StringComparison.Ordinal);
        }

        Assert.Contains("Button.SelectionBtn:disabled", xaml, StringComparison.Ordinal);
    }

    /// <summary>T opens the Transform dialog, and is gated exactly as its button is.</summary>
    [Fact]
    public void TheTKey_RunsTransformAndIsGatedLikeItsButton()
    {
        var code = Read("src/Ui/Views/WBond/WBondEditorView.axaml.cs");

        Assert.Contains("case Key.T:", code, StringComparison.Ordinal);
        Assert.Contains("if (_bound.HasSelection) OnTransform(", code, StringComparison.Ordinal);
        Assert.Contains("(T)", Read("src/Ui/Views/WBond/WBondEditorView.axaml"), StringComparison.Ordinal);
    }

    private static WBondDesign SelectableDesign()
    {
        var design = new WBondDesign();
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        design.Profiles.Add(profile);

        var array = new WireArray { Name = "G1", Profile = profile.Name };
        array.Wires.Add(profile.CreateWire(Point3.Mils(0, 0, 4), Point3.Mils(100, 0, 1),
                                           WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        design.Arrays.Add(array);
        return design;
    }

    // ════════════════════════════════════════════════════════ the vertex hitbox

    /// <summary>
    /// <b>A vertex is clickable everywhere it is DRAWN</b> (owner, 2026-08-16: "the hitbox of the wire
    /// vertex does not match the vertex size").
    ///
    /// <para>The dot grows with zoom because it is tied to the wire's real diameter; the hit
    /// tolerance is a few screen pixels, so past the crossover the dot is larger than its own hitbox
    /// and clicking anywhere but its centre misses. The fixture is a fat wire and a tolerance far
    /// smaller than its dot — the zoomed-in case exactly.</para>
    /// </summary>
    [Fact]
    public void AClickAnywhereOnAVertexDot_HitsThatVertex()
    {
        long diameterNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var mesh = WireMesh.Build(FatWireDesign(diameterNm));

        double drawnRadius = WireHitTest.VertexRadiusNm(diameterNm);
        Assert.True(drawnRadius > 0);

        var foot = mesh.Wires[0].Points[0];

        // A screen-derived tolerance a hundredth of the drawn dot — the zoomed-right-in case.
        double tinyTolerance = drawnRadius / 100.0;

        // Just inside the drawn dot's edge, PERPENDICULAR to the wire: on the dot, and clear of the
        // segment — probing along the wire would sit on the segment at distance zero and measure the
        // point-versus-segment preference instead of the vertex's own reach.
        var hit = WireHitTest.HitTestLayout(
            mesh, foot.X, foot.Y + (long)(drawnRadius * 0.9), tinyTolerance);

        Assert.True(hit.Found, "A click on the visible dot missed it.");
        Assert.False(hit.IsSegment);
        Assert.Equal(0, hit.Point);
    }

    /// <summary>
    /// …and NOT beyond it. The hitbox matching the dot means matching it on both sides — a floor that
    /// grew without bound would make every click anywhere select a vertex.
    /// </summary>
    [Fact]
    public void AClickOutsideTheVertexDot_DoesNotHitTheVertex()
    {
        long diameterNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var mesh = WireMesh.Build(FatWireDesign(diameterNm));

        double drawnRadius = WireHitTest.VertexRadiusNm(diameterNm);
        var foot = mesh.Wires[0].Points[0];

        // Well past the dot, and perpendicular to the wire so the SEGMENT is not hit either.
        var hit = WireHitTest.HitTestLayout(
            mesh, foot.X, foot.Y + (long)(drawnRadius * 4.0), drawnRadius / 100.0);

        Assert.False(hit.Found);
    }

    /// <summary>
    /// The caller's own tolerance still wins when it is the larger of the two — zoomed OUT, a click
    /// several pixels from a sub-pixel wire must still land on it.
    /// </summary>
    [Fact]
    public void TheScreenTolerance_StillAppliesWhenItIsTheLargerOne()
    {
        long diameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        var mesh = WireMesh.Build(FatWireDesign(diameterNm));

        double drawnRadius = WireHitTest.VertexRadiusNm(diameterNm);
        double screenTolerance = drawnRadius * 20.0;
        var foot = mesh.Wires[0].Points[0];

        var hit = WireHitTest.HitTestLayout(
            mesh, foot.X, foot.Y + (long)(drawnRadius * 5.0), screenTolerance);

        Assert.True(hit.Found);
        Assert.False(hit.IsSegment);
    }

    /// <summary>
    /// The renderer and the hit test read <b>one</b> constant. Two would be the bug: a dot drawn at
    /// one size and clickable at another.
    /// </summary>
    [Fact]
    public void TheDrawnRadiusAndTheHitRadius_AreTheSameNumber()
    {
        Assert.Equal(WireHitTest.VertexToWireDiameterRatio, WBondRenderer.VertexToWireDiameterRatio);

        // …and the renderer's pixel radius IS the hit test's world radius — in BOTH modes, because the
        // dot is the same fraction of the wire's apparent diameter either way. That is what lets the
        // hit test be mode-free without ever being smaller than the circle on screen.
        var theme = WBondRenderTheme.Fallback;
        long diameterNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        const double pixelsPerNm = 1e-3;

        foreach (var mode in new[] { WireThicknessMode.Thin, WireThicknessMode.TrueDiameter })
        {
            float stroke = WBondRenderer.StrokeWidthPx(diameterNm, pixelsPerNm, theme, mode);
            double drawnPx = WBondRenderer.VertexRadiusPx(stroke, mode);

            Assert.True(drawnPx > theme.LineWidthPx, "The fixture is at the floor; it proves nothing.");
            Assert.Equal(WireHitTest.VertexRadiusNm(diameterNm) * pixelsPerNm, drawnPx, 3);
        }
    }

    private static WBondDesign FatWireDesign(long diameterNm)
    {
        var design = new WBondDesign();
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        design.Profiles.Add(profile);

        var array = new WireArray { Name = "G1", Profile = profile.Name };
        array.Wires.Add(profile.CreateWire(Point3.Mils(0, 0, 4), Point3.Mils(100, 0, 1),
                                           diameterNm, "Gold"));
        design.Arrays.Add(array);
        return design;
    }

    // ════════════════════════════════════════════════════════ the envelope's extra line

    /// <summary>
    /// <b>The band is sampled at the wires' own vertices</b> (owner, 2026-08-16: "in profile view
    /// there is an additional line rendered where two segments join").
    ///
    /// <para>A member's height is piecewise linear between its own points, so a uniform sample ladder
    /// that steps OVER a vertex joins the samples on either side with a straight line and cuts the
    /// corner. Invisible as a translucent fill; as an outline it is a second line diverging from the
    /// wire at exactly the place two segments join. The oracle is that every wire vertex now has a
    /// band sample at its own span, so the band's edge cannot cut it.</para>
    /// </summary>
    [Fact]
    public void TheEnvelopesSamples_LandOnEveryWireVertex()
    {
        var design = BandDesign();
        var array = design.Arrays[0];
        var envelope = ProfileEnvelope.Build(array);

        foreach (var wire in array.Wires)
        {
            var start = wire.Points[0];
            var end = wire.Points[^1];

            for (int i = 1; i < wire.Points.Count - 1; i++)
            {
                double s = WireEdits.ChordParameter(start, end, wire.Points[i]);
                Assert.Contains(envelope.Bands, b => Math.Abs(b.Span - s) < 2e-3);
            }
        }
    }

    /// <summary>
    /// <b>A band with no thickness is not drawn at all.</b> The ordinary array — every member the same
    /// shape — has min == max at every sample, so the band is a zero-area sliver. As a fill that was
    /// invisible; the moment it gained an outline it became a second line lying on the array's own
    /// curve. The oracle is that a uniform array renders exactly the pixels it did before the
    /// envelope had an edge at all.
    /// </summary>
    [Fact]
    public void AZeroThicknessBand_DrawsNothing()
    {
        // Five wires, all one shape, all bound: min == max everywhere.
        var uniform = UniformBandDesign();
        var envelope = ProfileEnvelope.Build(uniform.Arrays[0]);

        Assert.Equal(0.0, envelope.Bands.Max(b => b.MaxHeightNm - b.MinHeightNm));

        // Measured the way the renderer measures it — in device pixels, at a zoom that would frame
        // the array comfortably. Zero is below the threshold at every zoom, which is the point: a
        // band this array cannot be shown at ANY magnification.
        double thickness = WBondRenderer.BandThicknessPx(
            uniform.Arrays[0].Wires[0], envelope, z => (float)(600 - z / 2000.0));

        Assert.Equal(0.0, thickness);
        Assert.True(thickness < WBondRenderer.MinimumVisibleBandPx);
    }

    /// <summary>A band with real thickness IS still drawn — the fix must not delete the feature.</summary>
    [Fact]
    public void ABandWithRealThickness_IsStillDrawn()
    {
        var design = BandDesign();
        var envelope = ProfileEnvelope.Build(design.Arrays[0]);

        double thickness = WBondRenderer.BandThicknessPx(
            design.Arrays[0].Wires[0], envelope, z => (float)(600 - z / 2000.0));

        Assert.True(thickness > WBondRenderer.MinimumVisibleBandPx,
                    $"A real envelope measured {thickness:0.0} px and would be suppressed.");
    }

    /// <summary>
    /// The threshold is a SCREEN measure, so the same array crosses it by zooming: a band that is a
    /// hairline at package zoom is a ribbon zoomed in. Measuring in nanometres would pick one
    /// magnification and be wrong at every other.
    /// </summary>
    [Fact]
    public void TheBandThreshold_IsMeasuredInPixelsAndSoFollowsTheZoom()
    {
        var design = BandDesign();
        var envelope = ProfileEnvelope.Build(design.Arrays[0]);
        var reference = design.Arrays[0].Wires[0];

        double zoomedIn = WBondRenderer.BandThicknessPx(reference, envelope, z => (float)(-z / 200.0));
        double zoomedOut = WBondRenderer.BandThicknessPx(reference, envelope, z => (float)(-z / 2_000_000.0));

        Assert.True(zoomedIn > WBondRenderer.MinimumVisibleBandPx);
        Assert.True(zoomedOut < WBondRenderer.MinimumVisibleBandPx);
    }

    /// <summary>Both designs render without throwing — the suppression path is exercised end to end.</summary>
    [Fact]
    public void BothBandCases_Render()
    {
        foreach (var design in new[] { UniformBandDesign(), BandDesign() })
        {
            using var surface = SKSurface.Create(new SKImageInfo(600, 600));
            surface.Canvas.Clear(SKColors.Black);

            var result = WBondRenderer.DrawProfile(
                surface.Canvas, design, WBondRenderTheme.Fallback,
                s => (float)(s / 4000.0), z => (float)(600 - z / 2000.0));

            Assert.True(result.WiresDrawn > 0);
        }
    }

    private static WBondDesign UniformBandDesign()
    {
        var design = new WBondDesign();
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        design.Profiles.Add(profile);

        var array = new WireArray { Name = "G1", Profile = profile.Name };
        for (int w = 0; w < 5; w++)
            array.Wires.Add(profile.CreateWire(Point3.Mils(0, w * 6, 4), Point3.Mils(100, w * 6, 1),
                                               WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));

        design.Arrays.Add(array);
        return design;
    }

    // ════════════════════════════════════════════════════════ deleting the last wire

    /// <summary>
    /// <b>Deleting the only wire leaves an EMPTY design</b>, and that is a valid one (owner,
    /// 2026-08-16: "I get an error 'wBond array G1 has no wire. An empty array makes the mapping
    /// matrix…'" and then "make it support 0 wires").
    ///
    /// <para>The delete always succeeded; the MODEL refused the result, because the last array was
    /// deliberately kept alive and an array with no wires makes the array-basis inductance singular.
    /// The two states look alike and are not: an empty ARRAY is a named terminal with nothing behind
    /// it, an empty DESIGN is a document nobody has drawn in yet. The last group is now pruned with
    /// the rest, so what is left is no wires AND no groups.</para>
    /// </summary>
    [Fact]
    public void DeletingEveryWire_LeavesAValidEmptyDesign()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 1, perGroup: 1));

        string? refusal = null;
        vm.EditRefused += m => refusal = m;

        vm.SelectAllWires();
        Assert.Equal(1, vm.DeleteSelectedWires());

        Assert.Null(refusal);
        Assert.Equal(0, vm.Design.WireCount);
        Assert.Empty(vm.Design.Arrays);

        // The design still validates, and the panel it publishes is honestly empty rather than stale.
        vm.Design.Validate();
        Assert.Empty(vm.Readout.Rows);
        Assert.Equal(0, vm.Mesh.WireCount);
    }

    /// <summary>The same across several groups — every one of them is pruned.</summary>
    [Fact]
    public void DeletingEveryWireAcrossGroups_EmptiesTheDesign()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 3, perGroup: 2));

        vm.SelectAllWires();
        Assert.Equal(6, vm.DeleteSelectedWires());

        Assert.Equal(0, vm.Design.WireCount);
        Assert.Empty(vm.Design.Arrays);
    }

    /// <summary>An emptied design is still EDITABLE — a wire drawn into it comes back as group one.</summary>
    [Fact]
    public void AnEmptiedDesign_TakesANewWire()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 2, perGroup: 1));

        vm.SelectAllWires();
        vm.DeleteSelectedWires();
        Assert.Empty(vm.Design.Arrays);

        vm.AddWire(Point3.Mils(0, 0, 4), Point3.Mils(100, 0, 1),
                   WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold");

        Assert.Equal(1, vm.Design.WireCount);
        Assert.Single(vm.Design.Arrays);
        Assert.Single(vm.Readout.Rows);
    }

    /// <summary>Undo brings the wires back — an empty design is a state, not a dead end.</summary>
    [Fact]
    public void EmptyingADesign_IsUndoable()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 2, perGroup: 2));

        vm.SelectAllWires();
        vm.DeleteSelectedWires();
        Assert.Equal(0, vm.Design.WireCount);

        vm.Undo();

        Assert.Equal(4, vm.Design.WireCount);
        Assert.Equal(2, vm.Design.Arrays.Count);
    }

    /// <summary>
    /// <b>An empty design still refuses to be a placed COMPONENT.</b> Its pins are its array names, so
    /// a part with no arrays has nothing to connect — and the refusal has to say that, not "the
    /// mapping matrix is rank-deficient".
    /// </summary>
    [Fact]
    public void AnEmptyDesign_IsNotAPlaceableComponent()
    {
        var empty = new WBondDesign();
        empty.Validate();   // valid as a DESIGN…

        var ex = Assert.Throws<InvalidOperationException>(
            () => new CircuitRF.Core.Devices.WBondModel(empty, "empty.wBond"));

        Assert.Contains("no pins", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rank", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An array that exists but holds nothing is STILL refused — that one really is singular.</summary>
    [Fact]
    public void AnEmptyArray_IsStillRefused()
    {
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray { Name = "G1" });

        var ex = Assert.Throws<InvalidOperationException>(design.Validate);
        Assert.Contains("no wires", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Emptying ONE group out of several is fine and prunes that group — the refusal must not have
    /// been written as "never leave a group empty", which would break the ordinary case.
    /// </summary>
    [Fact]
    public void EmptyingOneGroupOfSeveral_SucceedsAndPrunesIt()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 3, perGroup: 2));

        vm.Selection = new WireSelection { Wires = { 0, 1 } };   // the whole of G1
        Assert.Equal(2, vm.DeleteSelectedWires());

        Assert.Equal(4, vm.Design.WireCount);
        Assert.Equal(2, vm.Design.Arrays.Count);
        Assert.DoesNotContain(vm.Design.Arrays, a => a.Name == "G1");
    }

    // ════════════════════════════════════════════════════════ paste selects what it pasted

    /// <summary>
    /// <b>A multi-group paste selects the wires it actually created</b> (owner, 2026-08-16: "pasting
    /// many wires with different groups and position clusters results in the wrong selection after
    /// paste").
    ///
    /// <para>A paste is not an append: each wire rejoins an array of its own NAME, so pasting into
    /// anything but the last array inserts into the middle of the flat order and shifts every index
    /// above it. Selecting "the last N" therefore named some of the ORIGINALS and none of the copies.
    /// The fixture has three groups so the paste lands in all three at once, which is the only
    /// arrangement where the two answers differ.</para>
    /// </summary>
    [Fact]
    public void PastingAcrossGroups_SelectsTheWiresItActuallyPasted()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 3, perGroup: 2));

        vm.SelectAllWires();
        string? clip = vm.CopySelection();
        Assert.NotNull(clip);

        var before = Snapshot(vm);
        int pasted = vm.PasteWires(clip, WBondUnits.ToNm(50.0, WBondUnit.Mil), 0);

        Assert.Equal(6, pasted);
        Assert.Equal(12, vm.Design.WireCount);

        // Every selected index must name a wire that was NOT there before. The old "last N" answer
        // selects 6..11, which on this design is the whole of G3 plus the pasted G3 wires — three of
        // them originals.
        var after = Snapshot(vm);
        var selected = vm.Selection.Wires.OrderBy(i => i).ToList();

        Assert.Equal(6, selected.Count);
        foreach (int i in selected)
            Assert.DoesNotContain(after[i], before);

        // …and nothing that WAS there is selected, which is the other half of "wrong selection".
        for (int i = 0; i < after.Count; i++)
            if (before.Contains(after[i])) Assert.DoesNotContain(i, selected);
    }

    /// <summary>A single-group paste is unchanged — the case that always worked must keep working.</summary>
    [Fact]
    public void PastingWithinOneGroup_StillSelectsTheCopies()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 1, perGroup: 3));

        vm.SelectAllWires();
        string? clip = vm.CopySelection();

        int pasted = vm.PasteWires(clip, WBondUnits.ToNm(50.0, WBondUnit.Mil), 0);

        Assert.Equal(3, pasted);
        Assert.Equal([3, 4, 5], vm.Selection.Wires.OrderBy(i => i));
    }

    /// <summary>The selection is exactly the wires, with no stray points or segments left behind.</summary>
    [Fact]
    public void APastedSelection_IsWholeWiresOnly()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 2, perGroup: 2));

        vm.SelectAllWires();
        vm.PasteWires(vm.CopySelection(), WBondUnits.ToNm(50.0, WBondUnit.Mil), 0);

        Assert.Empty(vm.Selection.Points);
        Assert.Empty(vm.Selection.Segments);
        Assert.Equal(4, vm.Selection.Wires.Count);
    }

    /// <summary>The design's wires, by identity, in flat order.</summary>
    private static List<Wire> Snapshot(WBondViewModel vm) => [.. vm.Design.AllWires()];

    private static WBondDesign MultiGroupDesign(int groups, int perGroup)
    {
        var design = new WBondDesign();
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        design.Profiles.Add(profile);

        for (int g = 0; g < groups; g++)
        {
            var array = new WireArray { Name = $"G{g + 1}", Profile = profile.Name };
            for (int w = 0; w < perGroup; w++)
                array.Wires.Add(profile.CreateWire(
                    Point3.Mils(g * 400, w * 6, 4), Point3.Mils(g * 400 + 100, w * 6, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));

            design.Arrays.Add(array);
        }

        return design;
    }

    // ════════════════════════════════════════════════════════ the refusal message

    /// <summary>
    /// <b>A refusal is cleared by the next successful edit</b> (owner, 2026-08-16: "the 'inductance
    /// matrix is not positive definite' message does not ever go away once it is displayed").
    ///
    /// <para>The clear lives on <c>OnReadoutChanged</c> — the one event that means an edit went
    /// through — and NOT on <c>RefreshQualityText</c>, which runs on every overlay repaint including
    /// ones that changed no geometry. The old guard read "keep it unless the readout is PROVISIONAL",
    /// which is inverted against its own stated intent and so never fired on an ordinary frame.</para>
    /// </summary>
    [Fact]
    public void TheRefusalMessage_IsClearedByTheNextSuccessfulEdit()
    {
        var code = Read("src/Ui/Views/WBond/WBondEditorView.axaml.cs");

        int readout = code.IndexOf("private void OnReadoutChanged()", StringComparison.Ordinal);
        Assert.True(readout >= 0);
        Assert.Contains("_refusalShowing = false;", code[readout..], StringComparison.Ordinal);

        // …and the inverted guard is gone from RefreshQualityText.
        int refresh = code.IndexOf("private void RefreshQualityText()", StringComparison.Ordinal);
        Assert.True(refresh > readout);
        string body = code[refresh..code.IndexOf("private bool _refusalShowing", StringComparison.Ordinal)];
        Assert.DoesNotContain("ReadoutIsProvisional) return", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ORDERING the fix depends on: a refusal restores its snapshot — which republishes, and so
    /// clears the message — <b>before</b> it raises <c>EditRefused</c>. Reverse them and a genuine
    /// refusal would erase its own message.
    /// </summary>
    [Fact]
    public void ARefusal_RepublishesBeforeItReportsTheReason()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 1, perGroup: 2));

        var order = new List<string>();
        vm.ReadoutChanged += () => order.Add("readout");
        vm.EditRefused += _ => order.Add("refused");

        // Two wires on identical geometry make the matrix singular — the reachable case the refusal
        // path exists for (a paste onto an occupied position, before the free-pitch search existed).
        vm.Selection = new WireSelection { Wires = { 1 } };
        var wires = vm.Design.AllWires().ToList();
        for (int i = 0; i < wires[1].Points.Count; i++)
            vm.SetWirePoint(1, i, wires[0].Points[i]);

        if (order.Contains("refused"))
            Assert.True(order.IndexOf("readout") < order.LastIndexOf("refused"),
                        "The reason was reported before the republish that clears it — the message would erase itself.");
    }

    /// <summary>An out-of-band refusal reaches the same strip, with nothing to roll back.</summary>
    [Fact]
    public void AnOutOfBandRefusal_IsReported()
    {
        var vm = new WBondViewModel(MultiGroupDesign(groups: 1, perGroup: 1));

        string? seen = null;
        vm.EditRefused += m => seen = m;

        vm.ReportRefusal("no fixed plane");
        Assert.Equal("no fixed plane", seen);
    }

    // ════════════════════════════════════════════════════════ the drag capture

    /// <summary>
    /// <b>An overlay drag captures the pointer</b> (owner: "when I drag many wires around in the
    /// layout view, the dragging appears to glitch"). Without it a drag that outruns the canvas — over
    /// a ruler strip, the panel, another window — stops receiving moves and freezes under a moving
    /// cursor, and its release lands somewhere else entirely.
    /// </summary>
    [Fact]
    public void AnOverlayDrag_CapturesThePointerAndReleasesIt()
    {
        var canvas = Read("src/Ui/Controls/LayoutCanvas.cs");

        int press = canvas.IndexOf("_canvasOverlay?.OnPointerPressed(", StringComparison.Ordinal);
        Assert.True(press >= 0);
        Assert.Contains("e.Pointer.Capture(this);",
                        canvas[press..canvas.IndexOf("_viewModel.OnPointerPressed(", press, StringComparison.Ordinal)],
                        StringComparison.Ordinal);

        // The release is UNCONDITIONAL — a press consumed as a plain click captures too, and a
        // capture that outlives its gesture swallows every later click on the panel beside it.
        int release = canvas.IndexOf("private void OnPointerReleased(", StringComparison.Ordinal);
        Assert.True(release >= 0);

        string body = canvas[release..canvas.IndexOf("private void OnPointerWheel(", release, StringComparison.Ordinal)];
        Assert.Contains("e.Pointer.Capture(null);", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("e.Pointer.Capture(null);", StringComparison.Ordinal)
                    < body.IndexOf("_canvasOverlay?.OnPointerReleased(", StringComparison.Ordinal),
                    "The capture is released only when the overlay claims the release — a click would keep it.");
    }

    // ════════════════════════════════════════════════════════ dialog and panel wording

    /// <summary>The Group Wires As dialog lost the terminal explainer and the word "selected".</summary>
    [Fact]
    public void TheGroupWiresDialog_IsJustTheCountAndThePicker()
    {
        var xaml = Read("src/Ui/Views/Dialogs/WBondGroupWiresDialog.axaml");
        Assert.DoesNotContain("named terminal", xaml, StringComparison.Ordinal);

        var code = Read("src/Ui/Views/Dialogs/WBondGroupWiresDialog.axaml.cs");
        Assert.DoesNotContain("wires selected", code, StringComparison.Ordinal);
        Assert.Contains("\"1 wire\"", code, StringComparison.Ordinal);
        Assert.Contains("{wireCount} wires\"", code, StringComparison.Ordinal);
    }

    /// <summary>The Transform dialog's count reads the same way — one phrase, two dialogs.</summary>
    [Fact]
    public void TheTransformDialogsCount_ReadsTheSameWay()
    {
        var code = Read("src/Ui/Views/Dialogs/WBondTransformDialog.axaml.cs");

        Assert.DoesNotContain("wires selected", code, StringComparison.Ordinal);
        Assert.DoesNotContain("1 wire selected", code, StringComparison.Ordinal);
        Assert.Contains("\"Nothing selected.\"", code, StringComparison.Ordinal);   // this one keeps its sentence
    }

    /// <summary>The Properties Inspector's resting state says what to do, not what is missing.</summary>
    [Fact]
    public void ThePropertiesInspector_SaysSelectObjectsToInspect()
    {
        Assert.Equal("Select objects to inspect.", WBondWirePropertiesViewModel.NothingSelectedMessage);

        var panel = new WBondWirePropertiesViewModel();
        Assert.True(panel.IsEmptyState);
        Assert.Equal(WBondWirePropertiesViewModel.NothingSelectedMessage, panel.EmptyMessage);

        // …and it is what an editor with nothing selected actually reports, not just the default.
        var vm = new WBondViewModel(SelectableDesign());
        panel.SetContext(vm);
        vm.ClearSelection();

        Assert.True(panel.IsEmptyState);
        Assert.Equal(WBondWirePropertiesViewModel.NothingSelectedMessage, panel.EmptyMessage);
    }

    /// <summary>
    /// The unit hint sits BELOW the field, and the field is narrow — nothing typed into it is more
    /// than ten characters (owner).
    /// </summary>
    [Fact]
    public void TheValuePrompt_PutsItsHintUnderANarrowField()
    {
        var xaml = Read("src/Ui/Views/Dialogs/WBondValuePromptDialog.axaml");

        int box = xaml.IndexOf("x:Name=\"ValueBox\"", StringComparison.Ordinal);
        int hint = xaml.IndexOf("x:Name=\"SubText\"", StringComparison.Ordinal);
        int buttons = xaml.IndexOf("Content=\"Cancel\"", StringComparison.Ordinal);

        Assert.True(box >= 0 && hint >= 0 && buttons >= 0);
        Assert.True(box < hint, "The unit hint is still above the field.");
        Assert.True(hint < buttons, "The unit hint is below the buttons rather than above them.");

        string field = xaml[box..xaml.IndexOf("/>", box, StringComparison.Ordinal)];
        Assert.Contains("Width=\"120\"", field, StringComparison.Ordinal);

        // A StackPanel stretches its children, so Width alone would be a MINIMUM. The alignment is
        // what actually makes the box narrow.
        Assert.Contains("HorizontalAlignment=\"Left\"", field, StringComparison.Ordinal);
    }
}
