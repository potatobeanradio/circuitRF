// RF4 / L2d — the tiled raster cache.
// docs/sonnet-briefs/brief-rasterfill-4-tiled-raster-cache.md gates 1-9. The measurements this tier
// exists because of are in src/Render/RESOLVED.md and are deliberately NOT re-measured here: per the
// owner's call on the brief's §3, every gate below asserts a COUNTER or a PIXEL, never a duration.
// The 500k timing sweep the L2c completion note asked about stays where it already lives, in
// Category=Benchmark.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Theming;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

public class LayoutTileCacheTests
{
    private const int W = 1600, H = 1000;

    private static readonly LayerKey LayerA = new(1, 0);
    private static readonly LayerKey LayerB = new(2, 0);

    private static Technology MakeTech() => new()
    {
        Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 25_400,
        Layers =
        [
            new LayerDef { Key = LayerA, Name = "a", Color = new Rgba(200, 60, 60), FillOpacity = 0.35, ZOrder = 0, Visible = true, Selectable = true },
            new LayerDef { Key = LayerB, Name = "b", Color = new Rgba(60, 140, 200), FillOpacity = 0.35, ZOrder = 1, Visible = true, Selectable = true },
        ],
    };

    /// <summary>
    /// A document of straight-edged geometry, large enough to clear the tier's own engagement gate
    /// when the test asks for a low threshold, and laid out so that shapes straddle tile boundaries at
    /// several pan offsets. <b>No curves anywhere, and that is deliberate</b> — see
    /// <see cref="ATiledFrame_IsBitIdenticalToTheUnTiledFrame"/> for what a curve does and why.
    ///
    /// <para><b>Axis-aligned, and that is the second deliberate restriction.</b> A DIAGONAL edge is
    /// not bit-identical either, for a reason that has nothing to do with seams and that a single
    /// triangle shows on its own: Skia clips a path to the device bounds before rasterizing it, and a
    /// clipped diagonal's fixed-point slope is not quite the unclipped one, so the whole edge's
    /// coverage shifts a little — measured, one triangle, 1,185 of 1,600,000 pixels, all of them on
    /// its edges, identical at every pan offset and unchanged by more padding. An axis-aligned edge
    /// has no such freedom and comes out to the byte.</para>
    /// </summary>
    private static LayoutView MakeView(int rows = 40, int cols = 40)
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                long x = -40_000_000 + c * 2_000_000, y = -40_000_000 + r * 2_000_000;
                view.Shapes.Add(new RectShape
                {
                    Layer = ((r + c) & 1) == 0 ? LayerA : LayerB,
                    X1 = x, Y1 = y, X2 = x + 1_700_000, Y2 = y + 1_700_000,
                });
            }
        view.NotifyChanged();
        return view;
    }

    /// <summary>
    /// <b><c>ShowGrid</c> is off in every pixel comparison here, and it is not hiding anything.</b>
    /// A grid dot is drawn with a ROUND stroke cap, which is a curve, and a curve is the one thing the
    /// tiled and un-tiled frames do not agree on to the byte — see
    /// <see cref="ATiledFrame_IsBitIdenticalToTheUnTiledFrame"/>. The grid's own correctness inside a
    /// tile is gated separately and by position, in
    /// <see cref="TheGridIsInsideTheTile_AndInTheSamePlaceAsTheMetal"/>.
    /// </summary>
    private static LayoutRenderOptions Opts(LayoutTileCache? tiles, int threshold = 1, bool grid = false) => new()
    {
        Theme = LayoutRenderTheme.Light,
        ShowGrid = grid,
        PathCache = new LayoutPathCache(250_000),
        TileCache = tiles,
        TileShapeCountThreshold = threshold,
    };

    private static LayoutViewport Fit(double zoom = 1e-5) =>
        new(PanX: -W / (2.0 * zoom), PanY: -H / (2.0 * zoom), Zoom: zoom, Width: W, Height: H);

    private static byte[] Render(LayoutView view, Technology tech, LayoutViewport vp, LayoutRenderOptions opts,
                                 out LayoutRenderResult result, int frames = 1)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        result = default;
        for (int i = 0; i < frames; i++) result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
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

    // ── Gate 1 — a pan builds no tiles ───────────────────────────────────────────────────────────

    /// <summary>
    /// <b>This is the feature, in one assertion.</b> Once the ground under the viewport has been
    /// rasterized, panning back across it builds nothing and blits what is on screen. The tile grid is
    /// anchored in document space (R-rf4-1), so a pan slides across tile boundaries rather than
    /// re-anchoring the grid and invalidating everything.
    /// </summary>
    [Fact]
    public void ASteadyStatePan_BuildsNoTiles()
    {
        var view = MakeView();
        var tech = MakeTech();
        using var tiles = new LayoutTileCache();
        var opts = Opts(tiles);
        var fit = Fit();

        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));

        // Sweep the ground once — the zoom is constant throughout, so every frame after the first may
        // build, and these are the frames that do.
        var offsets = new[] { 0, 40, 80, 120, 160 };
        foreach (var dx in offsets)
            LayoutRenderer.Draw(surface.Canvas, view, tech, fit with { PanX = fit.PanX + dx / fit.Zoom }, opts);

        // Now pan back over exactly that ground.
        int built = 0, blitted = 0, frames = 0;
        foreach (var dx in offsets.Reverse().Concat(offsets))
        {
            var r = LayoutRenderer.Draw(surface.Canvas, view, tech, fit with { PanX = fit.PanX + dx / fit.Zoom }, opts);
            built += r.TilesBuilt;
            blitted += r.TilesBlitted;
            frames++;
        }

        Assert.Equal(0, built);
        Assert.True(blitted >= frames, $"every frame should have blitted the tiles on screen; got {blitted} over {frames} frames");
    }

    // ── Gate 2 — a zoom builds a bounded number, and not on every frame of the gesture ───────────

    /// <summary>
    /// R-rf4-4's chosen answer, asserted as a count rather than a clock: a continuous zoom draws LIVE
    /// (a frame at a zoom the previous frame was not at builds nothing at all), and the first frame
    /// that repeats a zoom is the one that tiles. What is NOT acceptable — and what this goes red for
    /// — is re-rasterizing every tile on every frame of a pinch.
    /// </summary>
    [Fact]
    public void AZoomGesture_BuildsNothingUntilItSettles()
    {
        var view = MakeView();
        var tech = MakeTech();
        using var tiles = new LayoutTileCache();
        var opts = Opts(tiles);

        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));

        int builtDuringGesture = 0;
        double zoom = 1e-5;
        for (int i = 0; i < 12; i++)
        {
            zoom *= 1.012;                                    // a brisk pinch, ~1.2% a frame
            var vp = Fit(zoom) with { Zoom = zoom };
            builtDuringGesture += LayoutRenderer.Draw(surface.Canvas, view, tech, Fit(zoom), opts).TilesBuilt;
            _ = vp;
        }
        Assert.Equal(0, builtDuringGesture);

        // The gesture stops. The next frame repeats the zoom, and that is the one that tiles.
        var settled = Fit(zoom);
        var onSettle = LayoutRenderer.Draw(surface.Canvas, view, tech, settled, opts);
        Assert.True(onSettle.TilesBuilt > 0, "the first frame at a repeated zoom is the one that builds");

        // And the frame after it builds nothing.
        Assert.Equal(0, LayoutRenderer.Draw(surface.Canvas, view, tech, settled, opts).TilesBuilt);
    }

    // ── Gate 3 — tier disabled, bit-identical to today's renderer ────────────────────────────────

    /// <summary>
    /// R-rf4-8, and the structural half of gate 7. The tier cannot engage without a cache the CALLER
    /// supplied, so every export, every one-shot render and every existing test is untouched by
    /// construction — there is no flag anyone has to remember to clear. Asserted both ways: no cache,
    /// and a cache with the threshold negative.
    /// </summary>
    [Fact]
    public void WithTheTierDisabled_TheFrameIsBitIdenticalToTodaysRenderer()
    {
        var view = MakeView();
        var tech = MakeTech();
        var fit = Fit();

        var reference = Render(view, tech, fit, Opts(tiles: null), out var r0);
        Assert.Equal(0, r0.TilesBuilt);
        Assert.Equal(0, r0.TilesBlitted);

        using var tiles = new LayoutTileCache();
        var offByThreshold = Render(view, tech, fit, Opts(tiles, threshold: -1), out var r1, frames: 3);
        Assert.Equal(0, r1.TilesBuilt);
        Assert.Equal(0, r1.TilesBlitted);
        Assert.Equal(0, PixelsDiffering(reference, offByThreshold));
        Assert.Equal(0, tiles.Count);

        // And under the threshold: the same document, a threshold it cannot reach.
        using var tiles2 = new LayoutTileCache();
        var underThreshold = Render(view, tech, fit, Opts(tiles2, threshold: 1_000_000), out var r2, frames: 3);
        Assert.Equal(0, r2.TilesBlitted);
        Assert.Equal(0, PixelsDiffering(reference, underThreshold));
    }

    // ── Gate 4 — tier enabled, the composed frame matches, ACROSS SEAMS ─────────────────────────

    /// <summary>
    /// R-rf4-7, which the brief calls the hard part of this work, and it is: a tile rasterized with a
    /// CLIP at its own edge antialiases every shape against that edge, and two such tiles composited
    /// leave a lighter line down the join. This tier rasterizes a PADDED region and blits only the
    /// core, so the pixels at the core's edge were produced with the neighbouring geometry present.
    ///
    /// <para>The pan offsets below are whole multiples of a device pixel and are chosen so that the
    /// fixture's rectangles land ACROSS tile boundaries — the case a clip-and-composite implementation
    /// fails. Verified to bite rather than assumed: rasterizing each tile under a clip instead makes
    /// this go red immediately, with the differing pixels lying on the seams.</para>
    ///
    /// <para><b>Why the fixture is axis-aligned rectangles.</b> Those are the shapes that DO come out
    /// to the byte, and the honest scope of this gate. A curve does not: Skia tessellates curves
    /// adaptively at the CURRENT TRANSFORM — which this renderer relies on deliberately (see
    /// <c>LayoutRenderer</c>'s own header) — and a tile's transform carries a different translation.
    /// Nor does a diagonal: Skia clips a path to the device bounds first, and a clipped diagonal's
    /// fixed-point slope is not quite the unclipped one. Both are small, both are confined to the
    /// edges themselves, and neither is a seam — which is exactly what
    /// <see cref="ACurvedOrDiagonalFrame_DiffersOnlyOnItsOwnEdges"/> gates, and what goes red if the
    /// padding here is removed. The two tests together are the whole of R-rf4-7 as it is actually
    /// attainable, and src/Render/RESOLVED.md says so in those words rather than claiming more.</para>
    /// </summary>
    [Fact]
    public void ATiledFrame_IsBitIdenticalToTheUnTiledFrame()
    {
        var view = MakeView();
        var tech = MakeTech();

        // 512 is the tile pitch. These offsets put the fixture's 1.7 mm rectangles — 17 device pixels
        // at this zoom — at, just before and just after a tile boundary.
        foreach (int dx in new[] { 0, 7, 253, 256, 509, 512, 519 })
            foreach (int dy in new[] { 0, 11, 256, 505 })
            {
                var vp = Fit() with { PanX = Fit().PanX + dx / Fit().Zoom, PanY = Fit().PanY + dy / Fit().Zoom };
                var reference = Render(view, tech, vp, Opts(tiles: null), out _);

                using var tiles = new LayoutTileCache();
                var tiled = Render(view, tech, vp, Opts(tiles), out var r, frames: 2);

                Assert.True(r.TilesBlitted > 0, $"the tier should have engaged at ({dx},{dy})");
                Assert.Equal(0, PixelsDiffering(reference, tiled));
            }
    }

    /// <summary>
    /// The bound on the two things gate 4 cannot assert as identity — a curve, whose adaptive
    /// tessellation follows the transform, and a diagonal, whose fixed-point slope follows the device
    /// clip. This is here so the difference stays SMALL and stays OFF THE SEAMS.
    ///
    /// <para><b>This is the test that gates the padding</b>, and it was verified to bite rather than
    /// assumed: with <c>TilePaddingDevicePixels</c> set to 0 — that is, each tile clipped at its own
    /// edge instead of rasterized wide and cropped — it goes red at 2.6% of pixels on the seams
    /// against 0.014% off them, which is the signature R-rf4-7 describes.</para>
    /// </summary>
    [Fact]
    public void ACurvedOrDiagonalFrame_DiffersOnlyOnItsOwnEdges()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        for (int i = 0; i < 900; i++)
        {
            long x = -40_000_000 + (i % 30) * 2_700_000, y = -40_000_000 + (i / 30) * 2_700_000;
            var layer = (i & 1) == 0 ? LayerA : LayerB;
            if ((i & 2) == 0)
                view.Shapes.Add(new CircleShape { Layer = layer, Cx = x, Cy = y, R = 1_100_000 });
            else
                view.Shapes.Add(new PolygonShape
                {
                    Layer = layer, Xy = [x, y, x + 2_200_000, y + 700_000, x + 1_100_000, y + 2_200_000],
                });
        }
        view.NotifyChanged();
        var tech = MakeTech();
        var fit = Fit();

        var reference = Render(view, tech, fit, Opts(tiles: null), out _);
        using var tiles = new LayoutTileCache();
        var tiled = Render(view, tech, fit, Opts(tiles), out var r, frames: 2);
        Assert.True(r.TilesBlitted > 0);

        int differing = PixelsDiffering(reference, tiled);
        Assert.True(differing < W * H / 200,
            $"{differing} of {W * H} pixels differ — an edge-rasterization difference is a fraction of a "
            + "percent and anything larger is a different defect");

        // And none of them is worse at a seam than away from one, which is what separates this from a
        // seam bug.
        //
        // THE SEAMS ARE NOT AT MULTIPLES OF THE TILE PITCH. A tile's core lands at
        // tx * pitch + round(transX), and transX is the frame's own path-space translation — so a band
        // computed as `x % 512` checks a set of columns that are not seams at all. The first draft did
        // exactly that and the test failed with every difference apparently "on a seam", which was the
        // band being in the wrong place rather than a defect in the tier.
        var (ox, oy) = LayoutRenderer.ComputeOrigin(
            fit.PanX + fit.Width / (2.0 * fit.Zoom), fit.PanY + fit.Height / (2.0 * fit.Zoom),
            fit.Width / fit.Zoom, fit.Height / fit.Zoom);
        int snapX = (int)System.Math.Round((ox - fit.PanX) * fit.Zoom);
        int snapY = (int)System.Math.Round(fit.Height - (oy - fit.PanY) * fit.Zoom);

        int onSeam = 0, offSeam = 0, onSeamTotal = 0, offSeamTotal = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int dx = (((x - snapX) % 512) + 512) % 512, dy = (((y - snapY) % 512) + 512) % 512;
                bool near = System.Math.Min(System.Math.Min(dx, 511 - dx), System.Math.Min(dy, 511 - dy)) <= 2;
                int i = (y * W + x) * 4;
                bool diff = reference[i] != tiled[i] || reference[i + 1] != tiled[i + 1]
                            || reference[i + 2] != tiled[i + 2] || reference[i + 3] != tiled[i + 3];
                if (near) { onSeamTotal++; if (diff) onSeam++; }
                else { offSeamTotal++; if (diff) offSeam++; }
            }

        double onRate = onSeamTotal == 0 ? 0 : (double)onSeam / onSeamTotal;
        double offRate = offSeamTotal == 0 ? 0 : (double)offSeam / offSeamTotal;
        Assert.True(onRate <= offRate * 3 + 0.001,
            $"differences concentrate at the seams ({onRate:P3} on, {offRate:P3} off) — that is a seam defect, "
            + "not adaptive flattening");
    }

    // ── Owner report, 2026-09-12 — a shape partly outside the viewport did not render at all ────

    /// <summary>
    /// <b>"When some shapes are partially outside the viewport, the entire shape doesn't render. If I
    /// pan a little more to get the full shape into view, then it will render."</b>
    ///
    /// <para>A tile's core reaches up to a full tile BEYOND the viewport, and the first version
    /// rasterized it from the FRAME's candidate set — the viewport plus an eight-pixel margin. So the
    /// part of a tile that was off-screen when the tile was built contained nothing, and a cached tile
    /// keeps that hole for as long as its key is valid: panning onto that ground showed blank. A tile
    /// is a render of a document REGION and has to ask the spatial index about that region, which is
    /// what <c>ResolveRegion</c> now does.</para>
    ///
    /// <para><b>The assertion needs BOTH halves or it proves nothing.</b> <c>TilesBuilt == 0</c> on the
    /// panned frame is what says the tiles were genuinely reused — if they had been rebuilt, the
    /// pixels would match for the wrong reason, and that is exactly what hid this defect during
    /// development: the tile key used to carry the frame's per-layer candidate COUNT, which moves on
    /// nearly every pan frame at a zoomed-in view, so every tile was rebuilt every frame and the hole
    /// was refilled before it could be seen. Verified to bite: restricting a tile's query back to the
    /// viewport's own candidates makes this go red, by 0.2% of the frame at a 4% pan rising to 2.9% at
    /// 15%.</para>
    /// </summary>
    [Fact]
    public void PanningOntoGroundACachedTileCovers_ShowsWhatIsThere()
    {
        var view = MakeView(rows: 60, cols: 60);
        var tech = MakeTech();
        var fit = Fit();
        // Zoomed in, which is the regime where a tile's core reaches well beyond the viewport.
        var start = fit.WithZoomAnchoredAt(fit.Zoom * 4, W / 2.0, H / 2.0);

        int reusedOnly = 0;
        foreach (int dx in new[] { 16, 32, 64, 128, 256, 384 })
        {
            var panned = start with { PanX = start.PanX + dx / start.Zoom };

            using var warm = new LayoutTileCache();
            using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
            LayoutRenderer.Draw(surface.Canvas, view, tech, start, Opts(warm));
            LayoutRenderer.Draw(surface.Canvas, view, tech, start, Opts(warm));   // builds the tiles here
            var afterPan = LayoutRenderer.Draw(surface.Canvas, view, tech, panned, Opts(warm));

            Assert.True(afterPan.TilesBlitted > 0);
            if (afterPan.TilesBuilt == 0) reusedOnly++;

            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);
            var reused = bmp.Bytes;

            using var cold = new LayoutTileCache();
            var fresh = Render(view, tech, panned, Opts(cold), out _, frames: 2);

            Assert.Equal(0, PixelsDiffering(reused, fresh));
        }

        // A pan far enough to need a new column of tiles legitimately builds some, and a frame that
        // rebuilt everything would match for the wrong reason. At least some of the offsets above must
        // have been served ENTIRELY from tiles that already existed, or this test proves nothing.
        Assert.True(reusedOnly >= 2, $"only {reusedOnly} of 6 pans were served from existing tiles alone");
    }

    /// <summary>
    /// The other half of the same defect, and a performance one on its own account: <b>a tile's key
    /// may not move when the viewport does.</b> The first version hashed the frame's per-layer
    /// candidate count into it (to carry the merge tier's decision) and the frame's resolved layer
    /// LIST (whose membership changes as layers scroll in and out), so at a zoomed-in view a pan
    /// invalidated every tile on nearly every frame — a cache doing negative work, and the thing that
    /// hid the defect above.
    /// </summary>
    [Fact]
    public void PanningAtAFixedZoom_DoesNotInvalidateTilesItStillNeeds()
    {
        var view = MakeView(rows: 60, cols: 60);
        var tech = MakeTech();
        var fit = Fit();
        var start = fit.WithZoomAnchoredAt(fit.Zoom * 4, W / 2.0, H / 2.0);
        double screen = W / start.Zoom;

        using var tiles = new LayoutTileCache();
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        LayoutRenderer.Draw(surface.Canvas, view, tech, start, Opts(tiles));
        int covering = LayoutRenderer.Draw(surface.Canvas, view, tech, start, Opts(tiles)).TilesBuilt;
        Assert.True(covering > 0);

        // Pan by a fraction of a tile, several times. Each step can legitimately reveal a new column of
        // tiles; none of them may invalidate the ones already held.
        for (int step = 1; step <= 6; step++)
        {
            var vp = start with { PanX = start.PanX + screen * 0.02 * step };
            int built = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, Opts(tiles)).TilesBuilt;
            Assert.True(built < covering,
                $"step {step} rebuilt {built} of {covering} tiles — a pan at a fixed zoom must not "
                + "invalidate the tiles it is still looking at");
        }
    }

    // ── Gate 5 — overlays are never cached ───────────────────────────────────────────────────────

    /// <summary>
    /// R-rf4-2. Moving a SELECTION with the document untouched must reuse every tile and must still
    /// track: a stale selection rectangle inside a cached tile is the kind of defect that reads as the
    /// editor malfunctioning. The counter half is what catches the cache being right for the wrong
    /// reason (an over-broad invalidation would also "track", by rebuilding everything).
    /// </summary>
    [Fact]
    public void MovingTheSelection_ReusesEveryTile_AndTheSelectionStillTracks()
    {
        var view = MakeView();
        var tech = MakeTech();
        using var tiles = new LayoutTileCache();
        var fit = Fit();

        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(tiles));
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(tiles));

        byte[] Frame(int selected)
        {
            var opts = Opts(tiles) with { Overlay = LayoutOverlay.Empty with { SelectedIndices = [selected] } };
            var r = LayoutRenderer.Draw(surface.Canvas, view, tech, fit, opts);
            Assert.Equal(0, r.TilesBuilt);
            Assert.True(r.TilesBlitted > 0);
            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);
            return bmp.Bytes;
        }

        var a = Frame(0);
        var b = Frame(view.Shapes.Count - 1);
        Assert.True(PixelsDiffering(a, b) > 0, "the selection outline must move with the selection");
    }

    // ── Gate 6 — a drag is live ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rf4-3. A shape being drag-previewed renders at its DRAGGED position every frame. Getting this
    /// wrong reproduces the ghost-left-behind defect <c>LayoutPathCache</c>'s own comment documents,
    /// one level up and inside an image where it is harder to see — so the tier bypasses entirely for
    /// the duration of the gesture.
    /// </summary>
    [Fact]
    public void AShapeBeingDragged_RendersAtItsDraggedPosition()
    {
        var view = MakeView();
        var tech = MakeTech();
        using var tiles = new LayoutTileCache();
        var fit = Fit();

        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(tiles));
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(tiles));
        Assert.True(tiles.Count > 0, "the tier must actually have engaged, or this proves nothing");

        byte[] DragTo(long dx)
        {
            var moved = new RectShape
            {
                Layer = LayerA,
                X1 = -40_000_000 + dx, Y1 = -40_000_000, X2 = -40_000_000 + 1_700_000 + dx, Y2 = -40_000_000 + 1_700_000,
            };
            var opts = Opts(tiles) with
            {
                Overlay = LayoutOverlay.Empty with { DragOverrides = new Dictionary<int, LayoutShape> { [0] = moved } },
            };
            var r = LayoutRenderer.Draw(surface.Canvas, view, tech, fit, opts);
            Assert.Equal(0, r.TilesBlitted);          // the whole frame is live for the gesture
            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);
            return bmp.Bytes;
        }

        Assert.True(PixelsDiffering(DragTo(0), DragTo(8_000_000)) > 0,
            "a dragged shape must move on screen during the gesture");
    }

    // ── Gate 8 — memory stays under the cap, and is disposed ─────────────────────────────────────

    /// <summary>
    /// R-rf4-5. The owner asked directly, during the instance-raster work, whether that tier's problem
    /// was memory; it was not, and this one must not become the change that makes the answer yes. A
    /// tile is a native Skia image, so the cap is on BYTES and eviction disposes rather than dropping.
    /// </summary>
    [Fact]
    public void TileMemory_StaysUnderTheCap_AndEvictsWhenItWouldNot()
    {
        var view = MakeView();
        var tech = MakeTech();

        // A cap of two tiles, against a viewport that needs a dozen.
        using var tiles = new LayoutTileCache(maxBytes: 2L * (512 + 16) * (512 + 16) * 4);
        var fit = Fit();
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int i = 0; i < 4; i++)
            LayoutRenderer.Draw(surface.Canvas, view, tech, fit with { PanX = fit.PanX + i * 300 / fit.Zoom }, Opts(tiles));

        Assert.True(tiles.BytesHeld <= tiles.MaxBytes, $"{tiles.BytesHeld} held against a {tiles.MaxBytes} cap");
        Assert.True(tiles.EvictionCount > 0, "an undersized cache must have evicted");

        // And an undersized cache still draws the right picture. A Put can evict, and an eviction
        // disposes the tile's native image — so a frame that stored a freshly built tile while earlier
        // tiles of the same frame were still waiting to be drawn would blit an image it had just freed.
        // Nothing is stored until every tile has been blitted, and this is what says so.
        var thrashed = Render(view, tech, fit, Opts(tiles), out var rt, frames: 2);
        Assert.True(rt.TilesBlitted > 0);
        using var cold = new LayoutTileCache();
        Assert.Equal(0, PixelsDiffering(thrashed, Render(view, tech, fit, Opts(cold), out _, frames: 2)));

        // At the shipped cap, one viewport's worth fits with room to spare.
        using var roomy = new LayoutTileCache();
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(roomy));
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(roomy));
        Assert.Equal(0, roomy.EvictionCount);
        Assert.True(roomy.BytesHeld < roomy.MaxBytes / 2,
            $"one viewport holds {roomy.BytesHeld / 1024 / 1024} MB of a {roomy.MaxBytes / 1024 / 1024} MB cap");
    }

    // ── Gate 9 — invalidation is exact, at BOTH ends of a move ──────────────────────────────────

    /// <summary>
    /// R-rf4-6/9. Editing one shape re-rasters the tiles it touches and no others — the assertion that
    /// catches an over-broad invalidation quietly turning the cache off, which is invisible in a
    /// screenshot.
    ///
    /// <para><b>And it must drop BOTH ends of a move.</b> <see cref="LayoutChangeInfo"/> carries
    /// indices, and by the time a subscriber is told, the shape has already moved — so the tile the
    /// shape came FROM is findable only by asking which tiles drew that index. Invalidating only the
    /// destination leaves the old pixels behind; only the source leaves a hole. Both halves are
    /// asserted here, the second by rendering.</para>
    /// </summary>
    [Fact]
    public void EditingOneShape_InvalidatesOnlyTheTilesItTouches_AtBothEnds()
    {
        var view = MakeView();
        var tech = MakeTech();
        using var tiles = new LayoutTileCache();
        var fit = Fit();

        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(tiles));
        LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(tiles));
        int before = tiles.Count;
        Assert.True(before >= 6, $"the fixture should cover several tiles; it covered {before}");

        // Move the first shape a long way — far enough that its old and new tiles are different ones.
        var original = (RectShape)view.Shapes[0];
        view.Shapes[0] = new RectShape
        {
            Layer = original.Layer,
            X1 = 30_000_000, Y1 = 30_000_000, X2 = 31_700_000, Y2 = 31_700_000,
        };
        view.NotifyChanged(LayoutChangeInfo.Updated([0]));
        tiles.Apply(LayoutChangeInfo.Updated([0]), view);

        int dropped = before - tiles.Count;
        Assert.InRange(dropped, 1, before - 1);      // some, and emphatically not all

        // The picture after the edit is the picture a cold cache produces — so neither end was missed.
        var r = LayoutRenderer.Draw(surface.Canvas, view, tech, fit, Opts(tiles));
        Assert.True(r.TilesBuilt > 0 && r.TilesBuilt <= dropped + 1);
        using var img = surface.Snapshot();
        using var afterBmp = SKBitmap.FromImage(img);
        var repaired = afterBmp.Bytes;

        using var cold = new LayoutTileCache();
        var fromScratch = Render(view, tech, fit, Opts(cold), out _, frames: 2);
        Assert.Equal(0, PixelsDiffering(repaired, fromScratch));
    }

    // ── The grid, which IS in the tile, and has to be in the right place ────────────────────────

    /// <summary>
    /// R-rf4-2 puts the grid outside the cache, and this tier reads that requirement for what it is
    /// for — chrome that describes a gesture in progress, which a stale tile would freeze. The grid is
    /// nothing of the kind: its pitch is a function of the snap step and the zoom, both of which are
    /// in the tile key. It is in the tile because the tile is OPAQUE, and the tile is opaque because
    /// that is what makes the compositing arithmetic identical (see src/Render/RESOLVED.md).
    ///
    /// <para>The first draft folded the frame's whole-pixel snap into the tile's grid viewport as well
    /// as into the blit, applying it twice and putting the grid a couple of hundred pixels off the
    /// metal. Nothing threw; 5.6% of the frame differed, which is roughly what a whole grid in the
    /// wrong place comes to. This is the gate for it.</para>
    /// </summary>
    [Fact]
    public void TheGridIsInsideTheTile_AndInTheSamePlaceAsTheMetal()
    {
        var view = MakeView();
        view.SnapDbu = 500_000;                       // a grid that actually decimates to a visible pitch
        var tech = MakeTech();
        var fit = Fit();

        var reference = Render(view, tech, fit, Opts(tiles: null, grid: true), out _);
        using var tiles = new LayoutTileCache();
        var tiled = Render(view, tech, fit, Opts(tiles, grid: true), out var r, frames: 2);
        Assert.True(r.TilesBlitted > 0);

        // A grid dot has a ROUND cap, so a handful of its pixels fall under the same adaptive-flattening
        // difference gate 4's fixture avoids. A misplaced grid is percent-scale, not per-mille.
        int differing = PixelsDiffering(reference, tiled);
        Assert.True(differing < W * H / 200,
            $"{differing} of {W * H} pixels differ with the grid on — a grid drawn in the wrong place is "
            + "percent-scale and this is the assertion that caught it");
    }

    // ── A document with placed cells is deliberately out of scope ───────────────────────────────

    /// <summary>
    /// Stated as a test rather than only as a comment, because it is a LIMITATION and a limitation
    /// nobody can see is one that gets quietly broken. <c>DrawInstances</c> paints screen-space chrome
    /// inline with a placement's geometry — PCell pins, the interface-changed mark, the moved-cell
    /// mark — and R-rf4-2 forbids baking a fixed-device-size glyph into a raster. A document with
    /// placements already has a raster tier of its own, the one this tier was modelled on.
    /// </summary>
    [Fact]
    public void ADocumentWithPlacedCells_DoesNotTile()
    {
        var view = MakeView();
        view.Instances.Add(new LayoutInstance { CellRef = "nowhere", X = 0, Y = 0 });
        view.NotifyChanged();
        var tech = MakeTech();
        using var tiles = new LayoutTileCache();

        var r = default(LayoutRenderResult);
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int i = 0; i < 3; i++) r = LayoutRenderer.Draw(surface.Canvas, view, tech, Fit(), Opts(tiles));

        Assert.Equal(0, r.TilesBuilt);
        Assert.Equal(0, r.TilesBlitted);
        Assert.Equal(0, tiles.Count);
    }

    // ── The tier holds no rendering of its own ──────────────────────────────────────────────────

    /// <summary>
    /// The rule <c>src/Cli/Authoring.cs</c> already states, applied here: a tile is rasterized through
    /// the caller's own committed-geometry pass, so the blitted pixels and the live pixels cannot
    /// become two different definitions of the same geometry — the same reason the instance raster
    /// tier runs the caller's cell-drawing closure into its offscreen surface. A second
    /// <c>DrawLayer</c> call in the tile file would be that second definition.
    /// </summary>
    [Fact]
    public void TheTileFile_DrawsNoGeometryOfItsOwn()
    {
        string path = System.IO.Path.Combine(RepoRoot(), "src", "Render", "Renderers", "LayoutRenderer.Tiles.cs");
        string source = StripComments(System.IO.File.ReadAllText(path));

        foreach (var forbidden in new[] { "DrawLayer(", "DrawInstances(", "BuildShapePath(", "DrawBitmapShapes(" })
            Assert.DoesNotContain(forbidden, source);
    }

    private static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(source[i]);
        }
        return sb.ToString();
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
