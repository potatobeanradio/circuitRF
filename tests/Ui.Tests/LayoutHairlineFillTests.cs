// The hairline tier and the merge tier's path cache — src/Ui/RESOLVED.md, "An imported Gerber that
// strokes every trace segment". Both were found on a 2014 Gerber panel whose copper pours are painted
// as 41,824 one-mil raster strokes, and neither can be provoked by anything authored in this editor,
// which is why they are gated here rather than left to the existing LOD/merge tests.
//
// Every test below asserts a PROPERTY (pixels identical, holes survive, paths not rebuilt), never a
// duration — the win these gate is measured in src/Ui/RESOLVED.md and is not re-measured here.

using System.Linq;
using CircuitRF.Design.Theming;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

public class LayoutHairlineFillTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new Rgba(0, 90, 180), FillOpacity = 0.35, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private const int W = 400, H = 200;

    /// <summary>A zoom at which a one-mil (25,400 DBU) trace is ~0.8 device pixels wide — the regime
    /// the tier engages in, and the one the owner's file sits in at Zoom-to-Fit.</summary>
    private const double HairlineZoom = 3.246e-5;

    private static LayoutRenderOptions Opts(double hairline, LayoutPathCache? cache = null) => new()
    {
        Theme = LayoutRenderTheme.Light, ShowGrid = false,
        PathCache = cache ?? new LayoutPathCache(),
        HairlineFillPixelThreshold = hairline,
    };

    private static byte[] RenderBytes(LayoutView view, Technology tech, LayoutViewport vp, LayoutRenderOptions opts)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    private static int PixelsDiffering(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) n++;
        return n;
    }

    // ── The substitution is exact, not an approximation ─────────────────────────────────────────

    /// <summary>
    /// A <c>PathShape</c>'s fill IS its centreline stroked at <c>Width</c>, so filling it at
    /// <c>Width + the pen</c> covers exactly what fill-plus-outline covers. That claim is the entire
    /// licence for the tier, so it is asserted as PIXEL IDENTITY rather than as a tolerance.
    ///
    /// <para><b>Identity holds because this fixture's zoom sits where the widening bucket is nearly
    /// exact, not because the widening is exact everywhere.</b> Since 2026-09-12 the allowance is
    /// bucketed up an eighth-octave ladder so it can be a cache key, which makes the footprint a
    /// bounded OVER-cover rather than an equality — the bound is what
    /// <see cref="TheOverCoverAtARung_StaysWithinTheDocumentedBound"/> gates. Here it is 1.06x the pen,
    /// i.e. a fourteenth of a device pixel, so the two rasterizations still agree pixel for pixel.</para>
    /// </summary>
    [Fact]
    public void AHairlinePath_RendersIdentically_WithTheTierOnAndOff()
    {
        var view = MakeView();
        view.Shapes.Add(new PathShape
        {
            Layer = LayerA, Xy = [200_000, 3_000_000, 9_800_000, 3_000_000],
            Width = 25_400, End = PathEndStyle.Round,
        });
        var tech = MakeTech();
        var vp = new LayoutViewport(0, 0, HairlineZoom, W, H);

        var off = RenderBytes(view, tech, vp, Opts(hairline: -1));
        var on = RenderBytes(view, tech, vp, Opts(hairline: 0));

        // Antialiasing at the two round end caps is the only place the two rasterizations may disagree,
        // and only by a handful of pixels out of 80,000 — a whole-image tolerance would hide a real
        // regression, so the budget is stated as an absolute count.
        Assert.True(PixelsDiffering(off, on) <= 32,
            $"hairline fill must reproduce fill-plus-outline; {PixelsDiffering(off, on)} pixels differed");
    }

    /// <summary>
    /// The threshold is one device pixel and must stay there. The tier's footprint substitution is
    /// exact at ANY width, but its alpha is not: the widened fill is solid throughout where the real
    /// pair paints a solid rim around an interior at the layer's own fill opacity. Once the interior is
    /// resolvable the difference is plainly visible — that is what flooded the clearances on the
    /// owner's board when the instance tier's 4.0 was borrowed.
    ///
    /// <para>The fixture is a path ~2 device pixels wide: below the borrowed 4.0 and above the correct
    /// 1.0, so it fails the moment the threshold is widened, which is the only way this test earns its
    /// place.</para>
    /// </summary>
    [Fact]
    public void APathTwoPixelsWide_IsUntouchedByTheTier()
    {
        var view = MakeView();
        long twoPixelsDbu = (long)(2.0 / HairlineZoom);
        view.Shapes.Add(new PathShape
        {
            Layer = LayerA, Xy = [200_000, 3_000_000, 9_800_000, 3_000_000],
            Width = twoPixelsDbu, End = PathEndStyle.Round,
        });
        var tech = MakeTech();
        var vp = new LayoutViewport(0, 0, HairlineZoom, W, H);

        Assert.Equal(RenderBytes(view, tech, vp, Opts(hairline: -1)),
                     RenderBytes(view, tech, vp, Opts(hairline: 0)));
    }

    // ── A closed centreline strokes to a RING, and the hole must survive ────────────────────────

    /// <summary>
    /// The board-outline bug: every board outline in the owner's panel is a closed 5-point path one mil
    /// wide, and batching them into the shared fill turned each board into a solid filled rectangle
    /// covering everything inside it (199 such paths on the fabrication-drawing layer alone). A closed
    /// centreline must stay on the ordinary fill-plus-outline route, so its interior stays empty.
    ///
    /// <para><b>The fixture is two NESTED rings of OPPOSITE winding, and every part of that is load
    /// bearing.</b> The mechanism is not "a batch loses holes" — it is narrower than that, and the
    /// narrower statement is the one worth gating. A ring carries its own outer contour and its own
    /// hole, correctly paired, and drawn on its own it is immune; batched, the shared path is filled
    /// NonZero, so contour ORIENTATION across independently built shapes starts to matter, and one
    /// shape's hole is cancelled by another's oppositely-wound contour. Measured directly against the
    /// hairline tier with the guard removed: one ring alone, two nested rings wound the same way, two
    /// coincident rings, and three nested rings all render correctly; two nested rings of opposite
    /// winding do not. A Gerber traces each outline in whatever direction the source tool emitted, so
    /// mixed winding is the normal case in an imported file, not a contrived one.</para>
    /// </summary>
    [Fact]
    public void NestedClosedHairlinePaths_KeepTheirInteriors_WhateverTheirWinding()
    {
        // Same square, traced in the two opposite directions.
        static long[] Ring(long cx, long cy, long half, bool ccw) => ccw
            ? [cx - half, cy - half, cx + half, cy - half, cx + half, cy + half, cx - half, cy + half, cx - half, cy - half]
            : [cx - half, cy - half, cx - half, cy + half, cx + half, cy + half, cx + half, cy - half, cx - half, cy - half];

        const long cx = 5_000_000, cy = 3_000_000;
        var view = MakeView();
        view.Shapes.Add(new PathShape { Layer = LayerA, Xy = Ring(cx, cy, 2_000_000, ccw: true), Width = 25_400, End = PathEndStyle.Round });
        view.Shapes.Add(new PathShape { Layer = LayerA, Xy = Ring(cx, cy, 4_000_000, ccw: false), Width = 25_400, End = PathEndStyle.Round });

        var tech = MakeTech();
        var vp = new LayoutViewport(0, 0, HairlineZoom, W, H);

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, Opts(hairline: 0));
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        var centre = bmp.GetPixel((int)(cx * HairlineZoom), (int)(H - cy * HairlineZoom));
        var background = bmp.GetPixel(2, 2);
        Assert.True(centre == background,
            $"nested hairline outlines must stay rings — the interior painted {centre} against background {background}");
    }

    // ── The merge tier reuses the path cache ───────────────────────────────────────────────────

    /// <summary>
    /// R-L2c-3 wired the path cache into the individual tier only. A layer past
    /// <c>MergeShapeCountThreshold</c> whose shapes are long (so never sub-pixel by bbox) therefore
    /// rebuilt every outline every frame — 219,556 <c>SKPath</c>s per frame on the owner's file. The
    /// property is that a SECOND frame of an unchanged document constructs no paths at all.
    /// </summary>
    [Fact]
    public void ADenseLayerOfLongShapes_BuildsNoPathsOnASecondFrame()
    {
        var view = MakeView();
        const int count = 3_000;   // past DefaultMergeShapeCountThreshold (2,000)
        for (int i = 0; i < count; i++)
        {
            long y = 1_000_000 + i * 2_000L;
            // Wide enough NOT to reach the hairline tier, long enough not to reach the LOD tier, so
            // these land squarely in the merge tier — which is the path under test.
            view.Shapes.Add(new PathShape
            {
                Layer = LayerA, Xy = [500_000, y, 9_500_000, y],
                Width = 1_000_000, End = PathEndStyle.Round,
            });
        }
        var tech = MakeTech();
        var vp = new LayoutViewport(0, 0, HairlineZoom, W, H);
        var cache = new LayoutPathCache(capacity: count * 2);
        var opts = Opts(hairline: 0, cache);

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var first = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        var second = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.True(first.ShapesDrawn > count / 2, $"the fixture must actually reach the merge tier; drew {first.ShapesDrawn}");
        Assert.True(first.PathsConstructed > 0, "the first frame must build the paths it caches");
        Assert.Equal(0, second.PathsConstructed);
    }

    // ── The widening is BUCKETED, so a continuous zoom is not a continuous cache miss ──────────
    //
    // brief-rasterfill-2-widen-key-bucketing.md. The widening is the cache key for the widened
    // outline, and it was computed straight from the zoom — so every frame of a trackpad pinch carried
    // a new key and rebuilt every hairline path on every visible layer. Measured on the reported board
    // at board fit: a 0.1% zoom change, a visible set that is for practical purposes identical,
    // rebuilt 192,680 paths and cost 17% on top of the frame.
    //
    // The rungs below are LayoutRenderDetail.WidenDbu's own ladder (eighth-octaves) written out, so a
    // change to BucketsPerOctave fails these rather than silently re-tuning them.

    /// <summary>Rung 2^16 of the widening ladder, and the rung below it — the pair every gate here is
    /// positioned against. A widening of <c>w</c> DBU is asked for by the zoom <c>2 / w</c>, since the
    /// allowance is <see cref="LayoutRenderer"/>'s 2-device-pixel pen converted to DBU.</summary>
    private const long Rung16 = 65_536, RungBelow16 = 60_097;

    private static LayoutView RoutedHairlines(int n = 12)
    {
        // Separated one-mil traces, not an abutting pour: the widening changes each trace's drawn
        // width outright here, where in a pour the strokes overlap and only the pour's rim moves. This
        // is the fixture that can actually see an over-wide substitution.
        var view = MakeView();
        for (int i = 0; i < n; i++)
        {
            long y = 2_000_000 + i * 350_000L;
            view.Shapes.Add(new PathShape
            {
                Layer = LayerA, Xy = [1_000_000, y, 9_000_000, y], Width = 25_400, End = PathEndStyle.Round,
            });
        }
        return view;
    }

    /// <summary>Mean ink coverage of a frame (1 - luminance), the same oracle the detail tier's own
    /// density work uses — a pixel count alone cannot tell a shifted edge from a fatter shape.</summary>
    private static double Ink(byte[] px)
    {
        double acc = 0;
        for (int i = 0; i < px.Length; i += 4) acc += 1.0 - (px[i] + px[i + 1] + px[i + 2]) / 765.0;
        return acc / (px.Length / 4);
    }

    private static LayoutRenderResult Frame(
        LayoutView view, Technology tech, double zoom, LayoutPathCache cache, long originY = 1_800_000)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        return LayoutRenderer.Draw(surface.Canvas, view, tech, new LayoutViewport(0, originY, zoom, W, H),
            Opts(hairline: 0, cache));
    }

    /// <summary>Gate 1 — the key is constant across a whole bucket of zoom, which is the entire point:
    /// a gesture moves through the bucket before anything is rebuilt.</summary>
    [Fact]
    public void TheWidening_IsConstantAcrossABucketOfZoom_AndStepsAtTheRung()
    {
        const double pen = 2.0;

        // Every zoom whose raw allowance lands in (RungBelow16, Rung16] must give the SAME answer…
        Assert.Equal(Rung16, LayoutRenderDetail.WidenDbu(pen, pen / Rung16));
        Assert.Equal(Rung16, LayoutRenderDetail.WidenDbu(pen, pen / 63_000.0));
        Assert.Equal(Rung16, LayoutRenderDetail.WidenDbu(pen, pen / (RungBelow16 + 1.0)));

        // …and the zoom that reaches the rung below must step to it, not to something arbitrary.
        Assert.Equal(RungBelow16, LayoutRenderDetail.WidenDbu(pen, pen / RungBelow16));
    }

    /// <summary>Gate 4 — the bucket may only ever WIDEN. Bucketing down would make the widened fill
    /// narrower than the pen it stands in for, which draws hairline artwork thinner than the frame
    /// would have drawn it and is the one failure this tier exists to prevent. (It is the asymmetry
    /// with <c>ToleranceDbu</c>, which buckets down safely because its error bound only tightens.)</summary>
    [Fact]
    public void TheWidening_IsNeverNarrowerThanTheRawAllowance_AtAnyZoom()
    {
        const double pen = 2.0;
        for (double zoom = 1e-8; zoom < 1e3; zoom *= 1.037)
        {
            long raw = (long)System.Math.Ceiling(pen / zoom);
            long bucketed = LayoutRenderDetail.WidenDbu(pen, zoom);
            Assert.True(bucketed >= raw, $"zoom {zoom:E3}: bucketed {bucketed} is narrower than the raw {raw}");
            Assert.True(bucketed <= System.Math.Max(1, raw) * 1.1,
                $"zoom {zoom:E3}: bucketed {bucketed} over-covers the raw {raw} by more than one eighth-octave");
        }

        Assert.Equal(0, LayoutRenderDetail.WidenDbu(2.0, 0));        // degenerate zoom: the tier cannot run
        Assert.Equal(0, LayoutRenderDetail.WidenDbu(0, 1e-4));       // no pen to stand in for
        Assert.Equal(1, LayoutRenderDetail.WidenDbu(2.0, 10.0));     // finer than one DBU: one DBU, as the bare ceiling gave
    }

    /// <summary>Gate 2 — the brief in one assertion. A frame that changes zoom by 0.1% is a frame a
    /// pinch gesture produces dozens of, and it must rebuild nothing.</summary>
    [Fact]
    public void AMicroZoomFrame_RebuildsNothing()
    {
        var view = RoutedHairlines();
        var tech = MakeTech();
        var cache = new LayoutPathCache(capacity: 1_000);

        double zoom = 2.0 / 68_000.0;                 // comfortably inside a bucket
        var first = Frame(view, tech, zoom, cache);
        Assert.True(first.PathsConstructed > 0, "the first frame must build what it caches");
        Assert.True(first.ShapesDrawn >= 12, $"the fixture must reach the hairline tier; drew {first.ShapesDrawn}");

        Assert.Equal(0, Frame(view, tech, zoom * 1.001, cache).PathsConstructed);
    }

    /// <summary>Gate 3 — crossing a rung rebuilds the working set, and the frame after it does not.
    /// A rebuild per rung is the price; a rebuild per frame was the defect.</summary>
    [Fact]
    public void CrossingARung_RebuildsOnce_AndOnlyOnce()
    {
        var view = RoutedHairlines();
        var tech = MakeTech();
        var cache = new LayoutPathCache(capacity: 1_000);

        double justAbove = 2.0 / (Rung16 + 64.0);     // one rung up from Rung16
        double justBelow = 2.0 / (Rung16 - 67.0);     // the same bucket as Rung16 — a 0.2% zoom step
        Assert.NotEqual(LayoutRenderDetail.WidenDbu(2.0, justAbove), LayoutRenderDetail.WidenDbu(2.0, justBelow));

        Assert.True(Frame(view, tech, justAbove, cache).PathsConstructed > 0);
        Assert.True(Frame(view, tech, justBelow, cache).PathsConstructed > 0, "crossing a rung must rebuild");
        Assert.Equal(0, Frame(view, tech, justBelow * 1.001, cache).PathsConstructed);
    }

    /// <summary>Gate 6 — a pan holds zoom fixed, so it was all hits before this change and must stay
    /// all hits after it.</summary>
    [Fact]
    public void APan_RebuildsNothing()
    {
        var view = RoutedHairlines();
        var tech = MakeTech();
        var cache = new LayoutPathCache(capacity: 1_000);

        double zoom = 2.0 / 68_000.0;
        Assert.True(Frame(view, tech, zoom, cache).PathsConstructed > 0);
        Assert.Equal(0, Frame(view, tech, zoom, cache, originY: 1_900_000).PathsConstructed);
    }

    /// <summary>
    /// Gate 5 — what the over-cover actually looks like, measured rather than argued.
    ///
    /// <para>Bucketing UP means the widened fill is no longer exactly the pen it replaces: it is up to
    /// one eighth-octave wider, so the substitution over-covers. The bound below is the thing a viewer
    /// would see as a POP while zooming — the frames either side of a rung — and it is stated as ink
    /// coverage, because a pixel count cannot tell a shifted antialiased edge from a fatter shape.</para>
    ///
    /// <para><b>Measured on this fixture, at the worst zoom in the bucket: 4.3%</b> — a 0.18
    /// device-pixel step in drawn width. The bound is 6% because that is what separates the ladder this
    /// uses from the next coarser one: the same measurement is 8.6% at quarter-octaves, 19.0% at
    /// half-octaves and 45.8% at whole octaves, and the last of those is what the brief that asked for
    /// this expected to be imperceptible. None of them may creep back in.</para>
    /// </summary>
    [Fact]
    public void TheOverCoverAtARung_StaysWithinTheDocumentedBound()
    {
        var view = RoutedHairlines();
        var tech = MakeTech();

        static double OverCover(LayoutView view, Technology tech, double zoom)
        {
            var vp = new LayoutViewport(0, 1_800_000, zoom, W, H);
            double off = Ink(RenderBytes(view, tech, vp, Opts(hairline: -1)));   // the real fill + pen
            double on = Ink(RenderBytes(view, tech, vp, Opts(hairline: 0)));     // the substitution
            return (on - off) / off;
        }

        double above = OverCover(view, tech, 2.0 / (Rung16 + 64.0));
        double below = OverCover(view, tech, 2.0 / (Rung16 - 67.0));

        Assert.True(System.Math.Abs(above) <= 0.06, $"over-cover above the rung was {above:P1}");
        Assert.True(System.Math.Abs(below) <= 0.06, $"over-cover below the rung was {below:P1}");
        Assert.True(System.Math.Abs(above - below) <= 0.06,
            $"the step across the rung was {System.Math.Abs(above - below):P1} — that is the pop a zoom gesture shows");
    }

    /// <summary>
    /// The capacity half of the same bug: an LRU smaller than what one frame touches evicts its whole
    /// working set every frame, so the cache is not merely less effective — it is inert, and every
    /// frame rebuilds everything. <c>LayoutCanvas</c> sizes the cache to the document for this reason.
    /// </summary>
    [Fact]
    public void ACacheSmallerThanOneFrame_RebuildsEverythingEveryFrame()
    {
        var view = MakeView();
        const int count = 3_000;
        for (int i = 0; i < count; i++)
        {
            long y = 1_000_000 + i * 2_000L;
            view.Shapes.Add(new PathShape
            {
                Layer = LayerA, Xy = [500_000, y, 9_500_000, y],
                Width = 1_000_000, End = PathEndStyle.Round,
            });
        }
        var tech = MakeTech();
        var vp = new LayoutViewport(0, 0, HairlineZoom, W, H);
        var opts = Opts(hairline: 0, new LayoutPathCache(capacity: count / 3));

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        var second = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.True(second.PathsConstructed > 0,
            "an undersized cache is expected to thrash — if this ever reads 0 the sizing rule changed and " +
            "LayoutCanvas.PathCacheCapacityFor's reason for existing should be re-checked");
    }
}
