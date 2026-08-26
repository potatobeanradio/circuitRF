using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.TechImport;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A layer's fill is painted through the stipple its process declares.
///
/// <para><b>Why this matters more than it sounds.</b> A real process layer table runs to hundreds of
/// rows over a few dozen colours — measured on one open vendor kit: 377 layers, 38 distinct fill
/// colours, so 373 of them collide with something. What separates them on screen is the repeating
/// mask, not the hue. Reading the colour and discarding the mask renders a whole process as a few
/// dozen indistinguishable washes, and two different layers of metal look like one.</para>
/// </summary>
public sealed class LayerStipplePatternTests
{
    private static readonly LayerKey Key = new(1, 0);

    /// <summary>Half the texels set, in a coarse checker — dense enough that a solid fill and a
    /// stippled one cannot be confused, sparse enough that the gaps are unmistakably gaps.</summary>
    private static FillPattern Checker(string name = "checker", int size = 4)
    {
        var rows = new List<string>();
        for (int y = 0; y < size; y++)
        {
            var row = new char[size];
            for (int x = 0; x < size; x++) row[x] = ((x + y) % 2 == 0) ? '*' : '.';
            rows.Add(new string(row));
        }
        return new FillPattern { Name = name, Rows = rows };
    }

    private static Technology MakeTech(string? patternName, FillPattern? pattern, double fillOpacity = 1.0)
    {
        var tech = new Technology { Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000 };
        if (pattern is not null) tech.FillPatterns.Add(pattern);
        tech.Layers.Add(new LayerDef
        {
            Key = Key,
            Name = "L1",
            Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
            FillOpacity = fillOpacity,
            FillPattern = patternName,
            ZOrder = 0,
            Visible = true,
            Selectable = true,
        });
        return tech;
    }

    private static LayoutView MakeViewWithBigRect()
    {
        var view = new LayoutView
        {
            DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
        };
        view.Shapes.Add(new RectShape { Layer = Key, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        return view;
    }

    /// <summary>The fraction of pixels inside the rect that are the layer's own red. A solid fill
    /// makes this ~1; a half-density stipple makes it ~0.5; no fill at all makes it 0.</summary>
    private static double PaintedFraction(Technology tech, out int distinctColours)
    {
        var view = MakeViewWithBigRect();
        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 100_000, 100_000), 200, 200, 0.1);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light });

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        // Sampled well inside the rect, so the outline stroke and its antialiasing are nowhere near.
        int painted = 0, total = 0;
        var seen = new HashSet<uint>();
        for (int y = 70; y < 130; y++)
            for (int x = 70; x < 130; x++)
            {
                var c = bmp.GetPixel(x, y);
                seen.Add((uint)c);
                total++;
                if (c.Red > c.Green + 30 && c.Red > c.Blue + 30) painted++;
            }
        distinctColours = seen.Count;
        return (double)painted / total;
    }

    [Fact]
    public void AStippledLayerPaintsOnlyItsSetTexels_AndASolidOnePaintsEverything()
    {
        double solid = PaintedFraction(MakeTech(null, null), out _);
        double stippled = PaintedFraction(MakeTech("checker", Checker()), out _);

        Assert.True(solid > 0.99, $"a solid fill should cover the interior; covered {solid:P0}");

        // Half the texels are set, so about half the interior is painted. Loose bounds on purpose —
        // the exact figure depends on where the pattern's phase lands against the sampled window,
        // and pinning it would make this a test of that alignment rather than of the stipple.
        Assert.InRange(stippled, 0.25, 0.75);
    }

    /// <summary>
    /// The point of the whole feature: two layers a process gives the same colour and different
    /// stipples must not render identically.
    /// </summary>
    [Fact]
    public void TwoLayersOfOneColour_RenderDifferentlyWhenTheirStipplesDiffer()
    {
        var sparse = new FillPattern
        {
            Name = "sparse",
            Rows = ["*...", "....", "....", "...."],
        };

        double a = PaintedFraction(MakeTech("checker", Checker()), out _);
        double b = PaintedFraction(MakeTech("sparse", sparse), out _);

        // Same colour, same opacity, same geometry — only the mask differs, and it has to show.
        Assert.True(a - b > 0.2, $"stipples of clearly different density rendered alike: {a:P0} vs {b:P0}");
    }

    /// <summary>
    /// A process states "outline only" either by declaring no fill or by declaring a mask with no
    /// set texel, and the two must reach the same place. Neither is an error, and neither should
    /// reach the shader — a fully transparent bitmap allocated to paint nothing is pure waste.
    /// </summary>
    [Fact]
    public void AnEmptyMaskAndAZeroOpacity_BothPaintNoFill()
    {
        var blank = new FillPattern { Name = "blank", Rows = ["....", "....", "....", "...."] };

        Assert.Equal(0.0, PaintedFraction(MakeTech("blank", blank), out _));
        Assert.Equal(0.0, PaintedFraction(MakeTech(null, null, fillOpacity: 0.0), out _));
    }

    /// <summary>
    /// A layer naming a stipple the technology no longer holds falls back to a solid fill rather
    /// than failing. A dangling name is recoverable and visible; a technology that cannot be opened
    /// is neither.
    /// </summary>
    [Fact]
    public void ANameThatResolvesToNothing_FillsSolid()
    {
        var tech = MakeTech("gone", null);

        Assert.Null(tech.FindFillPattern("gone"));
        Assert.True(PaintedFraction(tech, out _) > 0.99);
    }

    /// <summary>
    /// A stipple is a SCREEN-space texture. Its density must not change with zoom — that is what
    /// makes it usable as an identifier rather than as decoration, and it is the one property a
    /// world-space shader would silently get wrong.
    /// </summary>
    [Fact]
    public void TheStippleKeepsItsDensityAcrossZoom()
    {
        var tech = MakeTech("checker", Checker());
        var view = MakeViewWithBigRect();

        static double DensityAt(LayoutView view, Technology tech, Bbox window)
        {
            var vp = LayoutViewport.ZoomToFit(window, 200, 200, 0.0);
            using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
            LayoutRenderer.Draw(surface.Canvas, view, tech, vp, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light });
            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);
            int painted = 0, total = 0;
            for (int y = 60; y < 140; y++)
                for (int x = 60; x < 140; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    total++;
                    if (c.Red > c.Green + 30 && c.Red > c.Blue + 30) painted++;
                }
            return (double)painted / total;
        }

        // The whole rect, then a 10x-magnified corner of the SAME rect. World-space scaling would
        // make the second reading approach a solid fill; screen-space keeps it at the mask's density.
        double wide = DensityAt(view, tech, new Bbox(10_000, 10_000, 90_000, 90_000));
        double zoomed = DensityAt(view, tech, new Bbox(40_000, 40_000, 48_000, 48_000));

        Assert.InRange(zoomed, wide - 0.15, wide + 0.15);
    }

    /// <summary>The mask bitmap is built once per (pattern, colour), not per frame. A stipple
    /// rebuilt every frame is a performance defect that looks perfect in a screenshot.</summary>
    [Fact]
    public void TheMaskBitmapIsCachedAcrossFrames()
    {
        LayerFillPaint.Clear();
        var tech = MakeTech("checker", Checker());

        PaintedFraction(tech, out _);
        int afterFirst = LayerFillPaint.CachedBitmapCount;

        for (int i = 0; i < 5; i++) PaintedFraction(tech, out _);

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, LayerFillPaint.CachedBitmapCount);
    }

    // ── import ────────────────────────────────────────────────────────────────

    private const string TableWithStipples = """
        <layer-properties>
          <custom-dither-pattern>
            <order>0</order><name>blank</name>
            <pattern><line>....</line><line>....</line><line>....</line><line>....</line></pattern>
          </custom-dither-pattern>
          <custom-dither-pattern>
            <order>1</order><name>checker</name>
            <pattern><line>*.*.</line><line>.*.*</line><line>*.*.</line><line>.*.*</line></pattern>
          </custom-dither-pattern>
          <custom-dither-pattern>
            <order>2</order><name>unused</name>
            <pattern><line>**..</line><line>**..</line><line>....</line><line>....</line></pattern>
          </custom-dither-pattern>
          <properties>
            <fill-color>#ff0000</fill-color><dither-pattern>C1</dither-pattern>
            <visible>true</visible><name>MetalA.drawing</name><source>1/0</source>
          </properties>
          <properties>
            <fill-color>#ff0000</fill-color><dither-pattern>C0</dither-pattern>
            <visible>true</visible><name>MetalB.drawing</name><source>2/0</source>
          </properties>
          <properties>
            <fill-color>#ff0000</fill-color><dither-pattern>I1</dither-pattern>
            <visible>true</visible><name>MetalC.drawing</name><source>3/0</source>
          </properties>
          <properties>
            <fill-color>#ff0000</fill-color><dither-pattern>I0</dither-pattern>
            <visible>true</visible><name>MetalD.drawing</name><source>4/0</source>
          </properties>
          <properties>
            <fill-color>#ff0000</fill-color><dither-pattern>I7</dither-pattern>
            <visible>true</visible><name>MetalE.drawing</name><source>5/0</source>
          </properties>
        </layer-properties>
        """;

    private static Technology ImportTable(out IReadOnlyList<string> notes)
    {
        var table = LayerPropertiesReader.Read(TableWithStipples);
        var result = ProcessTechnologyBuilder.Build(
            new ProcessStackDescription("probe", [], []), table, "probe");
        notes = result.Notes;
        return result.Technology;
    }

    [Fact]
    public void ImportingALayerTable_CarriesItsStipplesAndResolvesEachReference()
    {
        var tech = ImportTable(out var notes);

        LayerDef Layer(string name) => tech.Layers.Single(l => l.Name == name);

        // A reference into the file's own pattern list.
        Assert.Equal("checker", Layer("MetalA.drawing").FillPattern);
        Assert.Equal("blank", Layer("MetalB.drawing").FillPattern);

        // Built-in 1 is hollow: no fill at all, expressed as a zero opacity rather than as a
        // synthetic empty mask, because the model already says exactly that.
        Assert.Null(Layer("MetalC.drawing").FillPattern);
        Assert.Equal(0.0, Layer("MetalC.drawing").FillOpacity);

        // Built-in 0 is solid, and so is any built-in this reader does not know — guessing at a mask
        // nobody wrote down would draw a pattern the process never specified.
        Assert.Null(Layer("MetalD.drawing").FillPattern);
        Assert.True(Layer("MetalD.drawing").FillOpacity > 0);
        Assert.Null(Layer("MetalE.drawing").FillPattern);
        Assert.True(Layer("MetalE.drawing").FillOpacity > 0);
        Assert.Contains(notes, n => n.Contains("built-in fill pattern this reader does not"));

        // A stippled layer is drawn at full opacity: the mask is already what lets the layer beneath
        // show through, and a sparse pattern behind the usual soft wash is invisible.
        Assert.Equal(1.0, Layer("MetalA.drawing").FillOpacity);

        // The pattern nothing names is not carried. A technology is what circuitRF draws with, and a
        // definition nothing points at survives edits and exports meaning nothing.
        Assert.Equal(["blank", "checker"], tech.FillPatterns.Select(p => p.Name).Order());
    }

    [Fact]
    public void AStippleSurvivesTheCtechRoundTrip()
    {
        var before = ImportTable(out _);

        var after = TechPersistence.Deserialize(TechPersistence.Serialize(before));

        Assert.Equal(before.FillPatterns.Count, after.FillPatterns.Count);
        var layer = after.Layers.Single(l => l.Name == "MetalA.drawing");
        var pattern = after.FindFillPattern(layer.FillPattern);
        Assert.NotNull(pattern);
        Assert.Equal(4, pattern!.Size);
        Assert.False(pattern.IsBlank);
        Assert.True(pattern.IsSet(0, 0));
        Assert.False(pattern.IsSet(0, 1));
    }

    /// <summary>Every technology written before stipples existed reads back with none, and every one
    /// of its layers fills solid — which is exactly what those files meant.</summary>
    [Fact]
    public void ATechnologyWithNoStipples_RoundTripsUnchanged()
    {
        var tech = MakeTech(null, null);

        string json = TechPersistence.Serialize(tech);
        var back = TechPersistence.Deserialize(json);

        Assert.DoesNotContain("FillPatterns", json);
        Assert.DoesNotContain("FillPattern", json);
        Assert.Empty(back.FillPatterns);
        Assert.Null(back.Layers[0].FillPattern);
    }

    // ── cost ──────────────────────────────────────────────────────────────────

    /// <summary>A dense via array plus its plate — the shape of a large capacitor cell, which is
    /// where a per-fill cost would actually be felt.</summary>
    private static LayoutView ViaArray(int count, out Bbox extent)
    {
        var view = new LayoutView
        {
            DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
        };
        int side = (int)Math.Sqrt(count);
        const long Pitch = 1_400, Size = 560;
        view.Shapes.Add(new RectShape { Layer = Key, X1 = 0, Y1 = 0, X2 = side * Pitch, Y2 = side * Pitch });
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                view.Shapes.Add(new RectShape
                {
                    Layer = Key, X1 = x * Pitch, Y1 = y * Pitch, X2 = x * Pitch + Size, Y2 = y * Pitch + Size,
                });
        extent = new Bbox(0, 0, side * Pitch, side * Pitch);
        return view;
    }

    private static void RenderOnce(LayoutView view, Technology tech, Bbox window)
    {
        var vp = LayoutViewport.ZoomToFit(window, 800, 600, 0.05);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light });
    }

    /// <summary>
    /// The fill paint — and with it the shader — is built ONCE PER LAYER PER FRAME, not once per
    /// shape.
    ///
    /// <para><b>This is the assertion that keeps stipples cheap, and it is a counter rather than a
    /// timing on purpose.</b> Moving the construction inside the per-shape loop is the obvious
    /// refactor for anyone who later wants a per-shape colour, and on a 20,000-via array it turns one
    /// shader into 20,000. Nothing about the rendered image changes, so a screenshot test passes and
    /// a small scene shows nothing; it surfaces only as a frame time on a file large enough that
    /// nobody bisects it quickly.</para>
    ///
    /// <para>Read off the FRAME's own result rather than a static on the paint helper. A
    /// process-wide counter reads correctly in isolation and is meaningless under a parallel suite —
    /// any other test rendering a layout lands between the reset and the assertion, which is exactly
    /// how this test flaked before it was moved.</para>
    ///
    /// <para>For why this counter is the whole cost guard and there is no timing test beside it:
    /// measured at 25k, 100k and 500k shapes, at full extent and zoomed 8x, a stippled layer renders
    /// within a few percent of a solid one and is sometimes faster — a sparse mask writes fewer
    /// pixels than a flat fill. There is no timing margin worth defending, only this structural
    /// property, and a wall-clock assertion here would measure the machine rather than the code.</para>
    /// </summary>
    [Fact]
    public void TheFillPaintIsBuiltPerLayer_NotPerShape()
    {
        var view = ViaArray(20_000, out var extent);
        var tech = MakeTech("checker", Checker());

        var vp = LayoutViewport.ZoomToFit(extent, 800, 600, 0.05);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp,
                                         new LayoutRenderOptions { Theme = LayoutRenderTheme.Light });

        // One visible layer in this scene, so one fill paint. What is being excluded is anything
        // that scales with the 20,001 shapes.
        Assert.Equal(1, result.LayersVisited);
        Assert.Equal(1, result.FillPaintsBuilt);
    }

}
