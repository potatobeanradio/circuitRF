// Phase L2c §3 (docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md) — gates 7, 8: the path cache
// stays correct across a pan that changes path space's per-frame origin (R-L2c-3's whole point — this
// is the test that fails if the cache were keyed to path space instead of shape-local space), and
// invalidation via LayoutChangeInfo (reused from L2b, not a second notification path) is exact.

using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

public class LayoutPathCacheTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 0.5, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static byte[] RenderPixels(LayoutView view, Technology tech, LayoutViewport vp, LayoutPathCache? cache)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, PathCache = cache };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    // ── Gate 7: cached shape survives a pan that changes path space's origin ────────────────────

    [Fact]
    public void CachedShape_PixelIdentical_AfterPanThatChangesPathSpaceOrigin()
    {
        var view = MakeView();
        view.Shapes.Add(new CircleShape { Layer = LayerA, Cx = 50_000, Cy = 50_000, R = 20_000 });
        view.Shapes.Add(new PathShape
        {
            Layer = LayerA, Xy = [0, 0, 40_000, 30_000, 80_000, 0],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 10_000, C1Y = 40_000, C2X = 30_000, C2Y = 40_000 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
            Width = 5_000,
        });
        var tech = MakeTech();

        const double zoom = 0.003; // device px per DBU
        var vp1 = new LayoutViewport(0, 0, zoom, 400, 400);
        var cache = new LayoutPathCache();

        // Frame 1 populates the cache.
        _ = RenderPixels(view, tech, vp1, cache);
        Assert.True(cache.Count > 0);

        // Frame 2: pan far enough that LayoutRenderer.ComputeOrigin (internal, quantized to a
        // power-of-two step derived from the view span) picks a DIFFERENT per-frame origin — the
        // exact condition R-L2c-3 says a path-space-keyed cache would get wrong.
        var vp2 = vp1 with { PanX = vp1.PanX + 500_000, PanY = vp1.PanY + 500_000 };
        double span = System.Math.Max(vp1.Width / vp1.Zoom, vp1.Height / vp1.Zoom);
        var (origin1X, origin1Y) = LayoutRenderer.ComputeOrigin(vp1.PanX + vp1.Width / (2 * vp1.Zoom), vp1.PanY + vp1.Height / (2 * vp1.Zoom), span, span);
        var (origin2X, origin2Y) = LayoutRenderer.ComputeOrigin(vp2.PanX + vp2.Width / (2 * vp2.Zoom), vp2.PanY + vp2.Height / (2 * vp2.Zoom), span, span);
        Assert.True(origin1X != origin2X || origin1Y != origin2Y, "test setup: the pan must actually change the quantized path-space origin, or this test proves nothing");

        var withCache = RenderPixels(view, tech, vp2, cache);
        var withoutCache = RenderPixels(view, tech, vp2, null); // fresh build, no cache at all — the ground truth

        Assert.Equal(withoutCache, withCache);
    }

    [Fact]
    public void CachedShape_PixelIdentical_AcrossManySmallPans_CacheStaysWarm()
    {
        var view = MakeView();
        for (int i = 0; i < 20; i++)
            view.Shapes.Add(new RoundedRectShape { Layer = LayerA, X1 = i * 30_000, Y1 = 0, X2 = i * 30_000 + 20_000, Y2 = 15_000, CornerRadius = 4_000 });
        var tech = MakeTech();
        var cache = new LayoutPathCache();

        var vp = new LayoutViewport(-10_000, -10_000, 0.006, 400, 300);
        byte[]? lastNoCache = null;
        for (int step = 0; step < 6; step++)
        {
            vp = vp with { PanX = vp.PanX + 15_000 };
            var withCache = RenderPixels(view, tech, vp, cache);
            var withoutCache = RenderPixels(view, tech, vp, null);
            Assert.Equal(withoutCache, withCache);
            lastNoCache = withoutCache;
        }
        Assert.NotNull(lastNoCache);
        Assert.True(cache.HitCount > 0, "repeated pans over the same shapes should produce cache hits, not just misses");
    }

    // ── Gate 8: invalidation matches every LayoutChangeInfo kind ─────────────────────────────────

    [Fact]
    public void Apply_Full_ClearsEverything()
    {
        var cache = new LayoutPathCache();
        var shape = new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        cache.GetOrBuild(0, shape, 0.001, 0, null, out _);
        Assert.Equal(1, cache.Count);

        cache.Apply(LayoutChangeInfo.Full);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Apply_RemovedTrailing_EvictsOnlyTheRemovedIndices()
    {
        var cache = new LayoutPathCache();
        for (int i = 0; i < 5; i++)
            cache.GetOrBuild(i, new RectShape { Layer = LayerA, X1 = i, Y1 = 0, X2 = i + 1, Y2 = 1 }, 0.001, 0, null, out _);
        Assert.Equal(5, cache.Count);

        cache.Apply(LayoutChangeInfo.RemovedTrailing(3, 2)); // removes indices 3,4
        Assert.Equal(3, cache.Count);

        // Indices 0-2 must still be cache HITS; a fresh GetOrBuild for either must not increment MissCount.
        int missesBefore = cache.MissCount;
        for (int i = 0; i < 3; i++)
            cache.GetOrBuild(i, new RectShape { Layer = LayerA, X1 = i, Y1 = 0, X2 = i + 1, Y2 = 1 }, 0.001, 0, null, out bool wasHit);
        Assert.Equal(missesBefore, cache.MissCount);
    }

    [Fact]
    public void Apply_Updated_EvictsExactlyTheListedIndices()
    {
        var cache = new LayoutPathCache();
        for (int i = 0; i < 5; i++)
            cache.GetOrBuild(i, new RectShape { Layer = LayerA, X1 = i, Y1 = 0, X2 = i + 1, Y2 = 1 }, 0.001, 0, null, out _);

        cache.Apply(LayoutChangeInfo.Updated([1, 3]));
        Assert.Equal(3, cache.Count);

        int missesBefore = cache.MissCount;
        cache.GetOrBuild(1, new RectShape { Layer = LayerA, X1 = 1, Y1 = 0, X2 = 2, Y2 = 1 }, 0.001, 0, null, out bool wasHit1);
        Assert.False(wasHit1);
        cache.GetOrBuild(3, new RectShape { Layer = LayerA, X1 = 3, Y1 = 0, X2 = 4, Y2 = 1 }, 0.001, 0, null, out bool wasHit3);
        Assert.False(wasHit3);
        Assert.Equal(missesBefore + 2, cache.MissCount);

        // Untouched indices (0, 2, 4) are still hits.
        int missesAfter = cache.MissCount;
        cache.GetOrBuild(0, new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }, 0.001, 0, null, out bool wasHit0);
        Assert.True(wasHit0);
        Assert.Equal(missesAfter, cache.MissCount);
    }

    [Fact]
    public void Apply_Appended_NeedsNoEviction_NewIndicesSimplyMissOnFirstDraw()
    {
        var cache = new LayoutPathCache();
        cache.GetOrBuild(0, new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }, 0.001, 0, null, out _);
        int countBefore = cache.Count;

        cache.Apply(LayoutChangeInfo.Appended(1, 3)); // indices 1,2,3 are new — nothing to evict

        Assert.Equal(countBefore, cache.Count); // untouched
        cache.GetOrBuild(1, new RectShape { Layer = LayerA, X1 = 1, Y1 = 0, X2 = 2, Y2 = 1 }, 0.001, 0, null, out bool wasHit);
        Assert.False(wasHit); // genuinely new — correctly a miss, not a stale hit
    }

    [Fact]
    public void Apply_Updated_ThenRedrawnDifferentGeometry_ProducesTheNewShapeNotTheStaleOne()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 50_000 });
        var tech = MakeTech();
        var vp = LayoutViewport.ZoomToFit(new Bbox(-10_000, -10_000, 200_000, 200_000), 300, 300);
        var cache = new LayoutPathCache();

        var before = RenderPixels(view, tech, vp, cache);

        // Mutate the shape's geometry directly (as a real Updated-classified command would) and
        // invalidate via the SAME LayoutChangeInfo.Updated the command layer emits.
        ((RectShape)view.Shapes[0]).X2 = 150_000;
        cache.Apply(LayoutChangeInfo.Updated([0]));

        var afterCached = RenderPixels(view, tech, vp, cache);
        var afterFresh = RenderPixels(view, tech, vp, null);

        Assert.NotEqual(before, afterCached); // the cached render reflects the NEW geometry
        Assert.Equal(afterFresh, afterCached); // and matches an uncached fresh build exactly
    }
}
