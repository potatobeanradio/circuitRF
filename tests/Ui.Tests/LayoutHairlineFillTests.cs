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
