// Gate for docs/sonnet-briefs/brief-rasterfill-3-coalesce-raster-fill-on-import.md §3.
//
// EVERY FIXTURE IS SYNTHETIC (§4). A painted rectangle, a painted rectangle with a cutout, a painted
// layer with a clear slot across it, and one ordinary traces-and-pads layer — hand-authored Gerber,
// following GerberImportTests' own precedent. It is also the only way gates 1-3 can assert an EXACT
// expected area: a real board's pour has no closed form.
//
// COUNTERS AND GEOMETRY ONLY. There is no wall-clock assertion anywhere in this file.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Theming;
using CircuitRF.Ui.Renderers;
using Clipper2Lib;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

public class RasterFillCoalesceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rasterfill-coalesce-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int Dbu = 1000;                  // DBU per micron — one DBU is one nanometre
    private const long Mm = 1_000_000;             // DBU per millimetre
    private const long Mil = 25_400;               // one mil, in DBU
    private static readonly long Tol = LayoutRasterFillCoalesce.DefaultToleranceDbu(Dbu);
    private static readonly LayerKey M1 = new(1, 0);

    // ── Geometry fixtures (gates 1, 2) ───────────────────────────────────────────────────────────

    private static PathShape Scanline(long x0, long x1, long y, long width, PathEndStyle end) =>
        new() { Layer = M1, Xy = [x0, y, x1, y], Width = width, End = end };

    private static double Area(IReadOnlyList<LayoutShape> shapes)
    {
        double a = 0;
        foreach (var s in shapes)
        {
            if (s is not PolygonShape p) continue;
            a += Math.Abs(Clipper.Area(Ring(p.Xy)));
            if (p.Holes is { } holes) foreach (var h in holes) a -= Math.Abs(Clipper.Area(Ring(h)));
        }
        return a;
    }

    private static Path64 Ring(long[] xy)
    {
        var path = new Path64(xy.Length / 2);
        for (int i = 0; i + 1 < xy.Length; i += 2) path.Add(new Point64(xy[i], xy[i + 1]));
        return path;
    }

    /// <summary>
    /// Gate 1: a rectangle PAINTED as N abutting strokes coalesces to a region of exactly the
    /// rectangle's area, at every overlap from just-touching to half.
    ///
    /// <para>Butt caps, so the expected area has a closed form and the assertion is EXACT rather than
    /// tolerance-bounded — the arc flattening R-rf3-5 names is the only approximation in any of this,
    /// and a butt-capped scanline has no arc in it at all. The round-capped case is asserted
    /// separately below, where the tolerance is the point.</para>
    /// </summary>
    [Theory]
    [InlineData(0.00)]
    [InlineData(0.10)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    public void APaintedRectangle_CoalescesToARegionOfTheSameArea(double overlap)
    {
        long width = Mil, pitch = (long)Math.Round(Mil * (1 - overlap));
        const long spanX = 5 * Mm;
        const int n = 300;

        var strokes = new List<LayoutShape>(n);
        for (int i = 0; i < n; i++)
            strokes.Add(Scanline(0, spanX, i * pitch, width, PathEndStyle.Flush));

        var regions = LayoutRasterFillCoalesce.Union(strokes, Tol, M1, null);

        Assert.Single(regions);
        double expected = (double)spanX * (width + (n - 1) * pitch);
        Assert.Equal(expected, Area(regions), expected * 1e-9);
    }

    /// <summary>
    /// R-rf3-5 stated as a measurement rather than a claim: with ROUND caps the union is exact in
    /// principle and approximate only in the cap's arc flattening, so the same pour unioned at the
    /// stated tolerance and at one DBU must agree to within that tolerance across the boundary it
    /// applies to. A gate on the absolute area would assert nothing about the flattening at all.
    /// </summary>
    [Fact]
    public void ARoundCappedPour_AgreesWithAFarFinerFlattening_ToTheStatedTolerance()
    {
        var strokes = new List<LayoutShape>();
        for (int i = 0; i < 200; i++)
            strokes.Add(Scanline(0, 2 * Mm, i * (long)(Mil * 0.9), Mil, PathEndStyle.Round));

        double coarse = Area(LayoutRasterFillCoalesce.Union(strokes, Tol, M1, null));
        double fine = Area(LayoutRasterFillCoalesce.Union(strokes, 1, M1, null));

        // Both cap columns are scalloped by one half-circle per stroke; the flattening can lose at
        // most the tolerance over the length of that boundary.
        double boundary = 2.0 * 200 * Math.PI * (Mil / 2.0);
        Assert.True(Math.Abs(coarse - fine) <= Tol * boundary,
            $"flattening error {Math.Abs(coarse - fine):N0} DBU² exceeded {Tol * boundary:N0} DBU²");
        Assert.True(coarse <= fine, "a coarser flattening of a convex cap can only cut the corner, never add area");
    }

    /// <summary>Gate 2: a pour painted AROUND a cutout produces a polygon with a hole of the right
    /// area — not a solid one. Written before the union code, per the brief.</summary>
    [Fact]
    public void APourPaintedAroundACutout_KeepsTheCutoutAsAHole()
    {
        long width = Mil, pitch = (long)(Mil * 0.9);
        const long spanX = 6 * Mm, spanY = 6 * Mm;
        const long holeLo = 2 * Mm, holeHi = 4 * Mm;

        var strokes = new List<LayoutShape>();
        for (long y = 0; y <= spanY; y += pitch)
        {
            if (y > holeLo && y < holeHi)
            {
                strokes.Add(Scanline(0, holeLo, y, width, PathEndStyle.Flush));
                strokes.Add(Scanline(holeHi, spanX, y, width, PathEndStyle.Flush));
            }
            else strokes.Add(Scanline(0, spanX, y, width, PathEndStyle.Flush));
        }

        var regions = LayoutRasterFillCoalesce.Union(strokes, Tol, M1, null);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(regions));
        var hole = Assert.Single(poly.Holes!);
        double holeArea = Math.Abs(Clipper.Area(Ring(hole)));

        // The hole is what the scanlines did not paint: (holeHi - holeLo) wide, and as tall as the
        // gap between the last scanline below it and the first one above.
        double expectedWide = holeHi - holeLo;
        Assert.InRange(holeArea, expectedWide * (holeHi - holeLo - 2.0 * pitch), expectedWide * (holeHi - holeLo));
    }

    /// <summary>Gate 6, at the geometry level: one stroke is one stroke. The decision, not the union,
    /// is what has to say so — the union of a single outline is a perfectly good polygon.</summary>
    [Fact]
    public void ASingleTrace_IsNeverCoalesced()
    {
        var one = new PathShape { Layer = M1, Xy = [0, 0, 10 * Mm, 0], Width = 6 * Mil, End = PathEndStyle.Round };
        var plan = LayoutRasterFillCoalesce.Build(
            [new LayoutRasterFillCoalesce.Candidate(one, "k", "M1")], Tol);
        Assert.True(plan.IsEmpty);
    }

    /// <summary>R-rf3-4's counter-example, stated directly: thirty thousand traces that do not overlap
    /// each other collapse to nothing and are left alone, however many of them there are.</summary>
    [Fact]
    public void ManyNonOverlappingTraces_AreNeverCoalesced()
    {
        var strokes = new List<LayoutRasterFillCoalesce.Candidate>();
        for (int i = 0; i < 3_000; i++)
            strokes.Add(new LayoutRasterFillCoalesce.Candidate(
                new PathShape { Layer = M1, Xy = [0, i * 20 * Mil, 2 * Mm, i * 20 * Mil], Width = 6 * Mil, End = PathEndStyle.Round },
                "k", "M1"));

        Assert.True(LayoutRasterFillCoalesce.Build(strokes, Tol).IsEmpty);
    }

    /// <summary>R-rf3-3's other half: two nets painting the same layer are two pours, never one.</summary>
    [Fact]
    public void TwoNetsOnOneLayer_CoalesceSeparately()
    {
        var candidates = new List<LayoutRasterFillCoalesce.Candidate>();
        for (int i = 0; i < 400; i++)
        {
            string net = i < 200 ? "GND" : "VCC";
            long y = i < 200 ? i * (long)(Mil * 0.9) : 5 * Mm + (i - 200) * (long)(Mil * 0.9);
            var s = new PathShape { Layer = M1, Net = net, Xy = [0, y, 2 * Mm, y], Width = Mil, End = PathEndStyle.Flush };
            candidates.Add(new LayoutRasterFillCoalesce.Candidate(s, net, "M1"));
        }

        var plan = LayoutRasterFillCoalesce.Build(candidates, Tol);
        Assert.Equal(2, plan.Replacements.Count);
        Assert.Equal(["GND", "VCC"], plan.Replacements.Select(r => r.Regions[0].Net).Order());
        Assert.All(plan.Replacements, r => Assert.Equal(200, r.Replaced.Count));
    }

    // ── Gerber fixtures (gates 3, 5-8) ───────────────────────────────────────────────────────────

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    /// <summary>A pour painted the way a CAM tool's raster fill paints one: N abutting one-mil
    /// scanlines, drawn rather than filled.</summary>
    private static string PaintedPour(int lines = 220, double widthMm = 5.0, bool clearSlot = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(MmHeader).Append("%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,0.025400*%\nD10*\n");
        long pitch = 22_860;                       // 0.9 x the aperture: abutting, with 10% overlap
        long span = (long)Math.Round(widthMm * Mm);
        for (int i = 0; i < lines; i++)
        {
            long y = i * pitch;
            sb.Append($"X0Y{y}D02*\n").Append($"X{span}Y{y}D01*\n");
        }
        if (clearSlot)
        {
            // A CLEAR stroke straight across the finished pour: it REMOVES copper, and unioning it
            // with the dark strokes would paint the slot solid.
            long mid = lines / 2 * pitch;
            sb.Append("%ADD11C,0.200000*%\n%LPC*%\nD11*\n")
              .Append($"X0Y{mid}D02*\n").Append($"X{span}Y{mid}D01*\n");
        }
        return sb.Append("M02*\n").ToString();
    }

    /// <summary>An ordinary traces-and-pads layer: sixty traces far enough apart that none of them
    /// touches another, plus a few pads. Deliberately MORE than
    /// <see cref="LayoutRasterFillCoalesce.MinStrokesPerRegion"/> strokes, so the union is actually
    /// computed and the DECISION is what rejects it — a fixture under the floor would prove only that
    /// the cheap pre-filter works.</summary>
    private static string OrdinaryTraces()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(MmHeader).Append("%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,0.152400*%\nD10*\n");
        for (int i = 0; i < 60; i++)
        {
            long y = i * 20 * Mil;
            sb.Append($"X0Y{y}D02*\n").Append($"X{3 * Mm}Y{y}D01*\n");
        }
        sb.Append("%ADD11C,0.600000*%\nD11*\n");
        for (int i = 0; i < 5; i++) sb.Append($"X{(i + 1) * 500_000}Y{-2 * Mm}D03*\n");
        return sb.Append("M02*\n").ToString();
    }

    private string Folder(string name, string fileName, string content)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    /// <summary>
    /// One import into its own parent directory, under a FIXED import name.
    ///
    /// <para>The name is fixed because the written <c>.clay</c> carries a relative <c>TechRef</c> built
    /// from it, so importing the same set twice under two names produces two different files for a
    /// reason that has nothing to do with coalescing — which would make gate 5's byte comparison
    /// assert the name rather than the geometry.</para>
    /// </summary>
    private GerberImport.ImportResult Import(string sourceDir, string into, bool coalesce)
    {
        string parent = Path.Combine(_root, into);
        Directory.CreateDirectory(parent);
        return GerberImport.Import(
            [.. Directory.EnumerateFiles(sourceDir).OrderBy(p => p, StringComparer.Ordinal)],
            parent, "board", null, Dbu, coalesceRasterFill: coalesce);
    }

    private static string ClayPath(GerberImport.ImportResult result) =>
        Directory.EnumerateFiles(CellFolder.SubFolderPath(result.CellDir!, ViewType.Layout), "*.clay").Single();

    private static LayoutView LoadCell(GerberImport.ImportResult result) =>
        LayoutPersistence.LoadFromFile(ClayPath(result));

    /// <summary>Gate 7: the shape count collapses on the real class of input. A counter, not a clock —
    /// and the gate that proves the feature did anything at all.</summary>
    [Fact]
    public void ARasterFilledLayer_ArrivesAsRegions_NotAsThousandsOfStrokes()
    {
        var dir = Folder("pour", "pour.gtl", PaintedPour());

        var coalesced = LoadCell(Import(dir, "on", coalesce: true));
        var asAuthored = LoadCell(Import(dir, "off", coalesce: false));

        Assert.Equal(220, asAuthored.Shapes.Count);
        Assert.All(asAuthored.Shapes, s => Assert.IsType<PathShape>(s));

        Assert.True(coalesced.Shapes.Count <= 4,
            $"expected the pour to arrive as a handful of regions, got {coalesced.Shapes.Count} shape(s)");
        Assert.All(coalesced.Shapes, s => Assert.IsType<PolygonShape>(s));
    }

    /// <summary>Gate 8: the import SAYS what it did, per layer, with both counts. A silent structural
    /// change to somebody's artwork is not acceptable even when it is an improvement.</summary>
    [Fact]
    public void TheImport_ReportsEveryLayerItCoalesced_WithBothCounts()
    {
        var dir = Folder("pour-note", "pour.gtl", PaintedPour());
        var result = Import(dir, "on", coalesce: true);

        string note = Assert.Single(result.Messages.Where(m => m.Contains("coalesced into", StringComparison.Ordinal)));
        Assert.Contains("220", note, StringComparison.Ordinal);          // the strokes it consumed
        Assert.Contains("1 filled region", note, StringComparison.Ordinal);
        Assert.Contains("0.1 µm", note, StringComparison.Ordinal);        // R-rf3-5's stated tolerance

        // The old "select them and use Merge" advice must not also be given: the Merge it asks for has
        // already happened.
        Assert.DoesNotContain(result.Messages, m => m.Contains("editor's Merge action", StringComparison.Ordinal));

        // ...and it is still given when coalescing is off, which is what keeps that advice reachable.
        var off = Import(dir, "off", coalesce: false);
        Assert.Contains(off.Messages, m => m.Contains("editor's Merge action", StringComparison.Ordinal));
        Assert.DoesNotContain(off.Messages, m => m.Contains("coalesced into", StringComparison.Ordinal));
    }

    /// <summary>Gate 5: a board with no painted fill is BIT-FOR-BIT unchanged. The file, not the
    /// picture — R-rf3-4's counting rule is what makes this true, and nothing else does.</summary>
    [Fact]
    public void AnOrdinaryBoard_ImportsToTheSameBytes_WithCoalescingOnOrOff()
    {
        var dir = Folder("ordinary", "board.gtl", OrdinaryTraces());

        byte[] on = File.ReadAllBytes(ClayPath(Import(dir, "on", coalesce: true)));
        byte[] off = File.ReadAllBytes(ClayPath(Import(dir, "off", coalesce: false)));

        Assert.Equal(off, on);
    }

    /// <summary>Gate 6, through the import: the degenerate case of gate 5, called out because it is the
    /// one a wrong threshold breaks.</summary>
    [Fact]
    public void ASingleTrace_StaysAPathShape_ThroughTheImport()
    {
        string one = MmHeader + "%ADD10C,0.152400*%\nD10*\nX0Y0D02*\nX3000000Y0D01*\nM02*\n";
        var dir = Folder("one-trace", "trace.gtl", one);

        var view = LoadCell(Import(dir, "on", coalesce: true));
        Assert.IsType<PathShape>(Assert.Single(view.Shapes));
    }

    /// <summary>
    /// Gate 3: a dark pour with a CLEAR stroke across it never coalesces to a solid.
    ///
    /// <para>The mechanism is R-rf3-3's: a layer that paints in clear polarity is COMPOSITED by the
    /// reader, and a composited read is excluded from coalescing wholesale — which is what makes
    /// "never union across polarity" true by construction rather than by a filter that could be
    /// forgotten.</para>
    /// </summary>
    [Fact]
    public void ADarkPourWithAClearStrokeAcrossIt_KeepsTheSlot()
    {
        var dir = Folder("slot", "pour.gtl", PaintedPour(clearSlot: true));
        var view = LoadCell(Import(dir, "on", coalesce: true));

        // The slot's centre is where the clear stroke ran; nothing may cover it.
        long midY = 110 * 22_860;
        long probeX = 2_500_000;
        Assert.False(CoveredAt(view.Shapes, probeX, midY),
            "the clear stroke's slot was filled in — a clear object was unioned as if it were dark");

        // ...and the copper on either side of it is still there, so the fixture is testing a slot
        // rather than an empty layer.
        Assert.True(CoveredAt(view.Shapes, probeX, 10 * 22_860));
        Assert.True(CoveredAt(view.Shapes, probeX, 210 * 22_860));
    }

    private static bool CoveredAt(IEnumerable<LayoutShape> shapes, long x, long y)
    {
        var pt = new Point64(x, y);
        foreach (var s in shapes)
        {
            var paths = LayoutClipper.ToClipperPaths(s, Tol);
            int winding = 0;
            foreach (var path in paths)
            {
                if (Clipper.PointInPolygon(pt, path) == PointInPolygonResult.IsOutside) continue;
                winding += Clipper.IsPositive(path) ? 1 : -1;
            }
            if (winding != 0) return true;
        }
        return false;
    }

    // ── Gate 4: the picture is unchanged ─────────────────────────────────────────────────────────

    private static Technology RenderTech() => new()
    {
        Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = M1, Name = "M1", Color = new Rgba(255, 128, 96),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static SKBitmap Render(LayoutView view, Technology tech, LayoutViewport vp)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false });
        using var img = surface.Snapshot();
        return SKBitmap.FromImage(img);
    }

    private static (int Differing, int Painted) Compare(SKBitmap a, SKBitmap b, SKColor background)
    {
        int differing = 0, painted = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var ca = a.GetPixel(x, y);
                var cb = b.GetPixel(x, y);
                if (Far(ca, background) || Far(cb, background)) painted++;
                if (Far(ca, cb)) differing++;
            }
        return (differing, painted);

        static bool Far(SKColor p, SKColor q) =>
            Math.Abs(p.Red - q.Red) > 6 || Math.Abs(p.Green - q.Green) > 6 || Math.Abs(p.Blue - q.Blue) > 6;
    }

    /// <summary>
    /// Gate 4: the coalesced document paints the same pixels as the un-coalesced one, at two zooms —
    /// the whole pour in view, and zoomed into the scalloped boundary where the flattening is
    /// resolvable. R-rf3-7's flag is what gives the comparison both sides.
    ///
    /// <para>The two bitmaps are compared against EACH OTHER rather than each against a background
    /// count. A lit-pixel count off a probed corner says nothing at the zoom that matters: at the
    /// close zoom every pixel in the frame is inside the pour, so the corner probe reads copper and
    /// the count collapses to zero for both. Pixel-for-pixel difference is the oracle the density
    /// gate uses and it is the one that survives being zoomed in.</para>
    /// </summary>
    [Theory]
    [InlineData(0.00006, 0.0)]        // the whole pour in view
    [InlineData(0.00400, -4_900_000)] // hard against its right-hand scalloped edge
    public void TheCoalescedDocument_PaintsTheSamePixels(double zoom, double panX)
    {
        var dir = Folder("render", "pour.gtl", PaintedPour());
        var tech = RenderTech();

        var coalesced = LoadCell(Import(dir, "on", coalesce: true));
        var asAuthored = LoadCell(Import(dir, "off", coalesce: false));
        foreach (var s in coalesced.Shapes) s.Layer = M1;
        foreach (var s in asAuthored.Shapes) s.Layer = M1;

        var vp = new LayoutViewport(panX, 0, zoom, 400, 400);
        using var before = Render(asAuthored, tech, vp);
        using var after = Render(coalesced, tech, vp);

        var background = new SKColor(0, 0, 0, 0);
        var (differing, painted) = Compare(before, after, background);

        Assert.True(painted > 10_000, $"the fixture painted almost nothing ({painted} px) at zoom {zoom}");
        Assert.True(differing <= painted / 50,
            $"{differing} of {painted} painted pixels differ — more than the 2% the flattening can explain");
    }

    // ── Gate 9: DRC agrees ───────────────────────────────────────────────────────────────────────

    private static Technology DrcTech(long minSpacingDbu) => new()
    {
        Name = "T",
        Layers = [new LayerDef { Key = M1, Name = "M1", Color = new Rgba(200, 200, 200, 255) }],
        DrcRules =
        [
            new DrcRule
            {
                Name = "M1 min spacing", Kind = DrcRuleKind.MinSpacing, Layer = M1,
                ValueDbu = minSpacingDbu, NetScope = DrcNetScope.DifferentNet,
            },
        ],
    };

    /// <summary>A painted pour plus one trace a stated distance from its edge.</summary>
    private static List<LayoutShape> PourAndTrace(long gapDbu)
    {
        var shapes = new List<LayoutShape>();
        long width = Mil, pitch = (long)(Mil * 0.9);
        const int lines = 120;
        for (int i = 0; i < lines; i++)
            shapes.Add(Scanline(0, 2 * Mm, i * pitch, width, PathEndStyle.Flush));

        long top = (lines - 1) * pitch + width / 2;
        shapes.Add(new PathShape
        {
            Layer = M1,
            Xy = [0, top + gapDbu + 3 * Mil, 2 * Mm, top + gapDbu + 3 * Mil],
            Width = 6 * Mil,
            End = PathEndStyle.Flush,
        });
        return shapes;
    }

    /// <summary>
    /// Gate 9: a clearance the DRC engine reports on the un-coalesced document is reported identically
    /// on the coalesced one — and a clearance it passes still passes. This is the assertion that makes
    /// R-rf3-5's tolerance claim real rather than stated.
    /// </summary>
    [Theory]
    [InlineData(4 * Mil, 6 * Mil, true)]    // the trace is too close: both must report it
    [InlineData(8 * Mil, 6 * Mil, false)]   // the trace is clear: neither may report anything
    public void DrcReportsTheSameClearances_CoalescedOrNot(long gap, long rule, bool expectViolation)
    {
        var strokes = PourAndTrace(gap);
        var tech = DrcTech(rule);

        var plan = LayoutRasterFillCoalesce.Build(
            [.. strokes.Select(s => new LayoutRasterFillCoalesce.Candidate(s, "M1", "M1"))], Tol);
        Assert.False(plan.IsEmpty, "the fixture's pour did not coalesce — the comparison would be vacuous");
        var coalesced = plan.Apply(strokes);

        var before = DrcEngine.Run(strokes, tech);
        var after = DrcEngine.Run(coalesced, tech);

        Assert.Equal(expectViolation, before.ErrorCount + before.WarningCount > 0);
        Assert.Equal(before.ErrorCount, after.ErrorCount);
        Assert.Equal(before.WarningCount, after.WarningCount);
    }
}
