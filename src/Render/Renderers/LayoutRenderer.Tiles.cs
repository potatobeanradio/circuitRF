// RF4 / L2d — the tiled raster cache, renderer side.
// docs/sonnet-briefs/brief-rasterfill-4-tiled-raster-cache.md. LayoutTileCache.cs is the cache; this
// file is what goes into a tile, where a tile sits, and when the tier is allowed to run at all.
//
// ── WHAT THIS EXISTS BECAUSE OF ───────────────────────────────────────────────────────────────────
// An imported Gerber board whose copper pours are stored as tens of thousands of abutting one-mil
// scanline strokes pans at 3.3 FPS with every existing tier already fully engaged: PathsConstructed is
// 0 on every frame, a 29,274-shape layer issues 4 draw calls, outlines are already off, and 77% of the
// frame is Skia rasterizing painted area — which is invariant to all of the above. RF3 removes that
// geometry at the SOURCE, but only on import; a board already sitting in a workspace still has it, and
// that is the board this tier is for.
//
// ── WHY IT IS THE FOURTH TIER AND NOT A FIFTH ────────────────────────────────────────────────────
// The instance raster tier (DefaultInstanceRasterMaxDevicePixels) already rasterizes once and blits
// per placement, and it was built for exactly this shape of problem. It is keyed on PLACEMENTS, and an
// imported board has none — InstanceRastersBuilt is 0 on every frame it will ever draw. This is the
// same idea given the input it was always the right answer for: top-level geometry.

using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    /// <summary>
    /// Candidate shapes a frame must be showing before the tile tier engages
    /// (<see cref="LayoutRenderOptions.TileShapeCountThreshold"/>).
    ///
    /// <para><b>20,000, and the boards either side of it are why.</b> Measured on the three imported
    /// boards this brief's series was written from, all fitted to the board with every layer visible:
    /// a 3,284-shape board of dense polygons pans in 16 ms and has nothing to gain; a 33,283-shape
    /// raster-filled board pans in 122 ms and a 52,230-shape one in 306 ms. There is no document in
    /// between to tune against, so the value sits in the middle of a wide empty gap rather than on a
    /// knife edge — which is the honest thing to say about it, and the reason it is exposed as an
    /// option at all.</para>
    /// </summary>
    internal const int DefaultTileShapeCountThreshold = 20_000;

    internal static int EffectiveTileShapeCountThreshold(LayoutRenderOptions opts) => opts.TileShapeCountThreshold switch
    {
        < 0 => int.MaxValue,                         // the tier is switched off (exports, tests)
        0   => DefaultTileShapeCountThreshold,
        _   => opts.TileShapeCountThreshold,
    };

    /// <summary>
    /// The document rectangle a tile rasterizes, INCLUDING its padding — which is the rectangle
    /// invalidation has to ask about, since geometry inside the padding paints into the core.
    ///
    /// <para>The inverse of the tile grid below. A tile's grid is laid out over the translation-free
    /// device coordinate <c>u = (dbu - origin) * zoom</c>, so tile <c>tx</c> covers
    /// <c>u in [tx*T, (tx+1)*T)</c> — a function of the origin and the zoom alone, and therefore
    /// identical at every pan position, which is what R-rf4-1 asks for. Y is negated because path
    /// space is built Y-down while the document is Y-up.</para>
    /// </summary>
    internal static (long MinX, long MinY, long MaxX, long MaxY) TileDocumentBounds(TileKey key)
    {
        const double t = LayoutTileCache.TileDevicePixels, p = LayoutTileCache.TilePaddingDevicePixels;
        double uLo = key.TileX * t - p, uHi = (key.TileX + 1) * t + p;
        double vLo = key.TileY * t - p, vHi = (key.TileY + 1) * t + p;
        double minX = key.OriginX + uLo / key.Zoom, maxX = key.OriginX + uHi / key.Zoom;
        double maxY = key.OriginY - vLo / key.Zoom, minY = key.OriginY - vHi / key.Zoom;
        return (Clamp(minX), Clamp(minY), Clamp(maxX), Clamp(maxY));

        static long Clamp(double v) =>
            v <= long.MinValue ? long.MinValue : v >= long.MaxValue ? long.MaxValue : (long)v;
    }

    /// <summary>
    /// Whether a layer uses the merge tier inside a TILE.
    ///
    /// <para><b>It is the layer's document-wide visible shape count, not the frame's candidate
    /// count, and that difference is the whole point.</b> The merge tier's own rule is a per-frame
    /// question — how many of this layer's shapes are in the viewport — and a per-frame answer cannot
    /// key a cache that outlives the frame. Worse, it churns: at a zoomed-in view the candidate count
    /// moves on essentially every pan frame, so a key built on it changed constantly and every tile
    /// was rebuilt every frame. That is a cache doing negative work, and it also hid a correctness
    /// defect behind it (see <c>ResolveRegion</c> in <c>LayoutRenderer.Draw</c>) by rebuilding the
    /// stale tiles before anyone could see them.</para>
    ///
    /// <para>The document-wide count is stable under a pan, identical for every tile — so a layer
    /// cannot composite one way in one tile and another way in its neighbour — and changes only when
    /// the document does, which already invalidates. <b>Where it diverges from the un-tiled frame</b>
    /// is a zoom deep enough that a layer's visible count falls below the threshold while its total
    /// stays above: the un-tiled frame would darken same-layer overlap there and the tiled one does
    /// not. It is recorded in <c>src/Render/RESOLVED.md</c> rather than hidden, and it is the price of
    /// a tile being a function of the document instead of of the viewport.</para>
    /// </summary>
    private static Dictionary<LayerKey, bool> LayerMergeMap(LayoutView view, LayoutRenderOptions opts)
    {
        int threshold = EffectiveMergeShapeCountThreshold(opts);
        var counts = new Dictionary<LayerKey, int>();
        var shapes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(view.Shapes);
        for (int i = 0; i < shapes.Length; i++)
        {
            if (shapes[i] is not { } sh || sh is BitmapShape) continue;
            counts.TryGetValue(sh.Layer, out int n);
            counts[sh.Layer] = n + 1;
        }

        var map = new Dictionary<LayerKey, bool>(counts.Count);
        foreach (var (layer, n) in counts) map[layer] = opts.ForceMergeTier || n > threshold;
        return map;
    }

    /// <summary>
    /// Everything about this frame that changes what committed geometry LOOKS like and is not already
    /// in the tile key's origin and zoom. Two frames agreeing on this and on the key would have
    /// rasterized the same pixels; when one of them changes, the key changes and the tiles rebuild.
    /// </summary>
    private static int VisualKeyFor(LayoutRenderOptions opts, LayoutRenderTheme theme, bool drawOutlines,
                                    Technology? tech)
    {
        var h = new System.HashCode();
        h.Add(drawOutlines);
        h.Add(opts.DetailPixelThreshold);
        h.Add(opts.LodPixelThreshold);
        h.Add(opts.HairlineFillPixelThreshold);
        h.Add(opts.StrokeElisionPixelThreshold);
        h.Add(opts.CoarseCoverageThreshold);
        h.Add(opts.ForceMergeTier);
        h.Add(opts.MergeShapeCountThreshold);
        h.Add(opts.TransparentBackground);
        h.Add(opts.ShowGrid);
        h.Add((uint)theme.Background);
        h.Add((uint)theme.GridMinor);
        h.Add((uint)theme.GridMajor);

        // ── THE TECHNOLOGY'S OWN LAYER TABLE, NOT THE LAYERS THIS FRAME HAPPENED TO RESOLVE ──────
        // Which layers have candidates in the viewport changes as you pan, so hashing the frame's
        // resolved list made the key move under a pan and rebuilt every tile — the same defect
        // LayerMergesInTiles describes, by a second route. A layer the technology does not declare
        // falls back to FallbackPalette.For, which is a pure function of the key and needs nothing
        // here.
        if (tech is not null)
            foreach (var def in tech.Layers)
            {
                h.Add(def.Key.Layer); h.Add(def.Key.Datatype);
                h.Add(def.Visible); h.Add(def.ZOrder);
                h.Add(def.Color.R); h.Add(def.Color.G); h.Add(def.Color.B); h.Add(def.Color.A);
                h.Add(def.FillOpacity);
                h.Add(def.FillPattern);
            }

        return h.ToHashCode();
    }

    /// <summary>
    /// Draws this frame's committed geometry from the tile cache, building whatever is missing.
    /// Returns false — having drawn nothing and touched no canvas state — for every reason there is
    /// not to tile, so the caller falls through to the identical un-tiled call.
    /// </summary>
    private static bool TryDrawTiled(
        SKCanvas canvas, LayoutView view, Technology? tech, LayoutViewport vp, LayoutRenderOptions opts,
        PathSpace ps, SKMatrix matrix, double scaleUm, long originX, long originY, bool drawOutlines,
        List<(LayerDef Def, List<(int Index, LayoutShape Shape)> Shapes)> resolved,
        LayoutFrameCounters counters, List<DeferredPort> deferredPorts,
        HashSet<LayerKey> unknownLayers, HashSet<string> missingCellRefs,
        System.Action<SKCanvas, Bbox?, SKRect?, LayoutFrameCounters, List<DeferredPort>, HashSet<LayerKey>, HashSet<string>, HashSet<int>?> drawCommitted,
        out SKMatrix frameMatrix)
    {
        // The frame's own transform unless the tier engages, in which case the snapped one below.
        frameMatrix = matrix;

        if (opts.TileCache is not { } cache) return false;

        // The zoom the cache last saw, recorded WHETHER OR NOT the tier goes on to engage — otherwise
        // a frame that refused for some other reason would make the next one look like a zoom change.
        cache.NoteFrameZoom(vp.Zoom);

        if (EffectiveTileShapeCountThreshold(opts) > counters.ShapesExamined) return false;
        if (opts.DetailPixelThreshold < 0 || opts.OutlineVertexBudget < 0) return false;

        // An opaque tile carries the background, so it cannot serve a frame that was asked not to
        // paint one. Export mode is the only caller that asks, and it supplies no cache anyway — this
        // is the second lock on the same door.
        if (opts.TransparentBackground) return false;

        // ── R-rf4-3: A DRAG BYPASSES THE CACHE, exactly as every other cache here does ───────────
        // LayoutPathCache bypasses on dragOverrides and the L2b index is not churned by drags, both
        // for the same reason: a cache keyed on anything but the live dragged position paints the
        // shape where it used to be, and the selection outline — never cached — keeps tracking the
        // cursor over the top of it. One level up that defect is harder to see, not easier, because
        // the stale pixels are inside an image rather than in a path. A drag selection is always
        // small and a gesture is short, so drawing the whole frame live for its duration costs
        // nothing that matters, and the commit's own LayoutChangeInfo re-rasters what moved.
        if (opts.Overlay is { } ov
            && (ov.DragOverrides is { Count: > 0 } || ov.InstanceDragOverrides is { Count: > 0 }))
            return false;

        // ── R-rf4-4: A ZOOM DRAWS LIVE AND RE-TILES ON SETTLE ────────────────────────────────────
        // The brief offers two answers and asks for both to be measured. This is the second: tiles
        // are keyed on the EXACT zoom, so a frame at a new zoom has none and draws live exactly as it
        // does today, and the first frame that repeats a zoom is the one that tiles. It needs no
        // gesture plumbing — which matters, because there are several ways to zoom and only some of
        // them are a gesture — and it is the only one of the two that can satisfy R-rf4-7, since the
        // other blits a raster built at a different scale and is therefore a resample of a picture
        // the renderer would not otherwise have drawn. What the measurement decided, and what the
        // octave-bucketed alternative actually cost, is in src/Render/RESOLVED.md.
        bool mayBuild = cache.ZoomSettled;

        // ── SCOPED TO FLAT DOCUMENTS, AND THIS IS A LIMITATION RATHER THAN AN OVERSIGHT ──────────
        // DrawInstances paints screen-space CHROME inline with a placement's geometry — PCell pins,
        // the interface-changed mark, the moved-cell mark. R-rf4-2 says a tile holds committed layer
        // geometry and nothing else, and baking a fixed-device-size glyph into a raster is exactly
        // the defect that requirement exists to prevent. Separating them is a larger job than this
        // brief, and a document with placements already has a raster tier of its own — the one this
        // tier was modelled on. The board class RF4 was written for has no instances at all.
        if (view.Instances.Count > 0) return false;

        int visualKey = VisualKeyFor(opts, opts.Theme, drawOutlines, tech);

        const int t = LayoutTileCache.TileDevicePixels;
        const int pad = LayoutTileCache.TilePaddingDevicePixels;

        double transX = (originX - vp.PanX) * vp.Zoom;
        double transY = vp.Height - (originY - vp.PanY) * vp.Zoom;

        // ── ONE ROUNDING FOR THE WHOLE FRAME, AND WHY IT HAS TO BE ONE ───────────────────────────
        //
        // A tile is rasterized against its own position on the grid and must be reusable at every pan
        // offset, so the sub-pixel phase it was built at cannot follow the pan. It is blitted at a
        // whole device pixel instead. Doing that PER TILE would snap neighbouring tiles differently
        // and put a one-pixel step down every seam; rounding the frame's translation ONCE and
        // offsetting every tile from it by an exact multiple of the tile pitch means the whole
        // committed layer is displaced by one common sub-pixel amount, with no discontinuity anywhere
        // in it.
        //
        // <b>The displacement is under half a device pixel and does not move during a pan</b>: it is
        // frac(transX), and transX changes by a whole number of device pixels when the pan does. So a
        // drag-pan shows no jitter — the committed geometry simply sits up to half a pixel from where
        // an un-tiled frame would have put it, while the overlays drawn over it are exact. At the pan
        // offsets where transX and transY are whole numbers the two agree exactly, which is what gate
        // 4 asserts, across seams, as bit identity.
        int snapX = (int)System.Math.Round(transX);
        int snapY = (int)System.Math.Round(transY);

        // ── AND THE REST OF THE FRAME MOVES WITH IT ──────────────────────────────────────────────
        // The snap is applied to the frame's own matrix too, so the rulers, the port glyphs, the
        // selection outlines, the handles and the marquee are all placed by the same whole device
        // pixel the blitted geometry is. Leaving them on the unsnapped transform would put a
        // selection outline up to half a pixel off the shape it is around — a discrepancy a user can
        // see, in exchange for agreeing with a frame nobody is drawing. This way the only thing that
        // moves is the whole view, together, by under half a pixel, and a pan advances it in whole
        // pixels, which is what a pixel-snapped canvas does everywhere else in the world.
        //
        // Assigned to the out parameter only on the successful return below: a frame that refuses for
        // any of the reasons still ahead draws exactly what it draws today, snap included.
        var snappedMatrix = SKMatrix.CreateScaleTranslation((float)scaleUm, (float)scaleUm, snapX, snapY);

        int txLo = (int)System.Math.Floor(-transX / t), txHi = (int)System.Math.Floor((vp.Width  - 1 - transX) / t);
        int tyLo = (int)System.Math.Floor(-transY / t), tyHi = (int)System.Math.Floor((vp.Height - 1 - transY) / t);

        // A viewport this big is not a viewport; refusing is cheaper than allocating for it.
        long wanted = ((long)txHi - txLo + 1) * ((long)tyHi - tyLo + 1);
        if (wanted <= 0 || wanted > 4096) return false;

        // ── FIRST PASS: is every tile this frame needs already held? ─────────────────────────────
        // Asked BEFORE anything is drawn, because the answer decides whether this frame can be a pure
        // blit at all. A frame that may not build (the zoom is still changing) and is missing a tile
        // must draw live — a half-tiled frame would show cached geometry next to live geometry with a
        // sub-pixel step between them, which is worse than either.
        var needed = new List<(int Tx, int Ty, TileKey Key, LayoutTile? Held)>((int)wanted);
        bool complete = true;
        for (int ty = tyLo; ty <= tyHi; ty++)
            for (int tx = txLo; tx <= txHi; tx++)
            {
                var key = new TileKey(originX, originY, vp.Zoom, tx, ty, visualKey);
                var held = cache.Get(key);
                if (held is null) complete = false;
                needed.Add((tx, ty, key, held));
            }

        if (!complete && !mayBuild) return false;

        // ── SECOND PASS: build what is missing, BEFORE anything is drawn ─────────────────────────
        // Separate from the blit below so a build that fails (an offscreen surface this size failing
        // is out of memory and nothing else) can still fall through to drawing the frame live. Doing
        // it in one pass would leave half the tiles already blitted onto the canvas with no way back.
        //
        // <b>AND NOTHING IS PUT IN THE CACHE UNTIL EVERY TILE HAS BEEN BLITTED</b>, which is the
        // second reason for the split. A Put can EVICT, and an eviction disposes the tile's
        // SKImage — so putting a freshly built tile in while earlier tiles of the same frame are
        // still waiting to be drawn can free an image this frame is about to blit. It needs a cache
        // too small to hold one viewport to happen at all, which is exactly what an undersized cache
        // is, and the symptom is a use-after-free rather than a slow frame.
        var tiles = new List<(int Tx, int Ty, TileKey Key, LayoutTile Tile, bool IsNew)>(needed.Count);
        var tileCounters = new LayoutFrameCounters();
        foreach (var (tx, ty, key, held) in needed)
        {
            if (held is not null) { tiles.Add((tx, ty, key, held, false)); continue; }

            var built = BuildTile(key, tx, ty, view, vp, opts.Theme, opts.ShowGrid, ps, scaleUm,
                                  drawCommitted, tileCounters);
            if (built is null)
            {
                // Out of memory part-way. Nothing has been drawn and nothing has been stored, so the
                // frame falls through to the live path with the cache exactly as it was.
                foreach (var stored in tiles) if (stored.IsNew) stored.Tile.Dispose();
                return false;
            }
            tiles.Add((tx, ty, key, built, true));
            counters.TilesBuilt++;
        }

        // ── THIRD PASS: the blit ─────────────────────────────────────────────────────────────────
        using var blitPaint = new SKPaint { IsAntialias = false };
        // Every tile collects every port — see DeferredPort.Index for why, and for what drawing one
        // twice actually looks like.
        var seenPorts = new HashSet<int>();

        foreach (var (tx, ty, _, tile, _) in tiles)
        {
            // The core, cropped out of the padded raster and blitted at a whole device pixel.
            var src = new SKRect(pad, pad, pad + t, pad + t);
            var dst = SKRect.Create(tx * t + snapX, ty * t + snapY, t, t);
            canvas.DrawImage(tile.Image, src, dst, TileSampling, blitPaint);
            counters.TilesBlitted++;
            counters.DrawCalls++;

            // R-rf4-2's other half: the things the layer pass produces that are NOT pixels. A port
            // glyph is a fixed device size and is drawn live above every layer by the frame's own top
            // pass, so the tile carries the port and not its picture.
            foreach (var port in tile.Ports)
                if (seenPorts.Add(port.Index)) deferredPorts.Add(port);
            foreach (var k in tile.UnknownLayers) unknownLayers.Add(k);
            foreach (var r in tile.MissingCellRefs) missingCellRefs.Add(r);
        }

        // The build's WORK counters join the frame's; its canvas-issue counters do not. That is the
        // split InstanceRastersBuilt already states: DrawCalls answers "what did this frame issue to
        // the canvas" and the blits above are that answer, while a path built or a vertex emitted
        // inside a tile really was built by this frame and is what a diagnostic is looking for.
        counters.PathsConstructed += tileCounters.PathsConstructed;
        counters.VerticesEmitted += tileCounters.VerticesEmitted;
        counters.FillPaintsBuilt += tileCounters.FillPaintsBuilt;
        counters.ShapesDrawn += tileCounters.ShapesDrawn;
        counters.LayersVisited += tileCounters.LayersVisited;

        // Now that every image has been read, the new ones may join the cache — and an eviction here
        // can only reach a tile this frame is finished with.
        foreach (var (_, _, key, tile, isNew) in tiles)
            if (isNew) cache.Put(key, tile);

        frameMatrix = snappedMatrix;
        return true;
    }

    /// <summary>Nearest-neighbour, no mipmap. The blit is a whole-device-pixel translation of a raster
    /// built at this exact scale, so there is nothing to resample and saying so explicitly is what
    /// keeps it that way — a filtered default would soften one-mil artwork for no reason at all.</summary>
    private static readonly SKSamplingOptions TileSampling = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>
    /// Rasterizes one tile: its own padded region of the document, at the frame's exact scale, through
    /// the caller's own committed-geometry pass.
    /// </summary>
    private static LayoutTile? BuildTile(
        TileKey key, int tx, int ty, LayoutView view, LayoutViewport vp,
        LayoutRenderTheme theme, bool showGrid, PathSpace ps, double scaleUm,
        System.Action<SKCanvas, Bbox?, SKRect?, LayoutFrameCounters, List<DeferredPort>, HashSet<LayerKey>, HashSet<string>, HashSet<int>?> drawCommitted,
        LayoutFrameCounters tileCounters)
    {
        const int t = LayoutTileCache.TileDevicePixels;
        const int pad = LayoutTileCache.TilePaddingDevicePixels;
        const int side = t + 2 * pad;

        SKSurface? surface = null;
        try
        {
            surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;                    // out of memory: fall through to live

            var c = surface.Canvas;

            // ── THE TILE IS OPAQUE, AND THAT IS WHAT MAKES R-rf4-7 ACHIEVABLE ────────────────────
            //
            // Compositing the same fills into a TRANSPARENT tile and then compositing the tile onto
            // the background is associative in real arithmetic and is not in bytes: an 8-bit
            // premultiplied buffer stores a 35%-opacity layer's colour as round(C * 0.35), and eleven
            // such layers over one another accumulate a quantization error that the un-tiled frame —
            // which never holds a partially transparent intermediate, because it starts from an opaque
            // background — never pays. Measured on the reported board: 0.2 to 0.8% of pixels differed,
            // by up to 34 of 255 on a channel, spread evenly over the frame rather than gathered at
            // the seams. Starting each tile from the SAME opaque background the frame starts from
            // makes the arithmetic identical step for step, and the figure goes to zero.
            //
            // The background is therefore the tile's, and so is the grid: an opaque tile would
            // otherwise paint over the frame's. R-rf4-2 puts the grid outside the cache and this is
            // the one place that has to be read for what it is FOR — the requirement is about chrome
            // that describes a gesture in progress, which a stale tile would freeze. The grid is
            // nothing of the kind: its pitch is a pure function of the snap step and the zoom, both of
            // which are in the tile key, and its lines sit at document multiples of that pitch. It is
            // as cacheable as the geometry, and caching it is what buys exactness.
            c.Clear(theme.Background);
            if (showGrid)
            {
                // The frame's grid, in the tile's own pixels, at the SNAPPED position the geometry
                // below will be drawn at — so grid and artwork keep the same relationship inside the
                // tile that they have in an un-tiled frame.
                // NOTHING IN A TILE MAY REFERENCE THE PAN. The viewport handed to DrawGrid places a
                // world point at exactly the tile pixel the matrix below places it at —
                // (world - origin) * zoom - (tx*T - pad) — so the grid and the artwork share one
                // mapping and the blit's own whole-pixel offset applies to both at once. The first
                // draft folded the frame's snap in here as well, which applied it TWICE and put the
                // grid a couple of hundred pixels off the metal: 5.6% of the frame differed, which is
                // roughly what a whole grid drawn in the wrong place comes to.
                var tileVp = vp with
                {
                    Width = side,
                    Height = side,
                    PanX = key.OriginX + (tx * t - pad) / vp.Zoom,
                    PanY = key.OriginY - ((ty * t - pad) + side) / vp.Zoom,
                };
                DrawGrid(c, view, tileVp, theme);
            }

            // Path space -> this tile's pixels. Identical to the frame's own matrix in SCALE, and
            // differing only in a translation that is a function of the tile's grid position — never
            // of the pan. Every device-pixel-denominated decision inside drawCommitted (stroke widths,
            // the LOD, hairline and elision thresholds) therefore resolves exactly as it would on the
            // real canvas, which is what makes this a cache rather than a second look at the document.
            c.Concat(SKMatrix.CreateScaleTranslation(
                (float)scaleUm, (float)scaleUm, -(tx * t - pad), -(ty * t - pad)));

            var (minX, minY, maxX, maxY) = TileDocumentBounds(key);
            var tileDoc = new Bbox(minX, minY, maxX, maxY);
            var tilePathRect = NormalizedRect(ps.X(minX), ps.Y(minY), ps.X(maxX), ps.Y(maxY));

            var ports = new List<DeferredPort>();
            var unknown = new HashSet<LayerKey>();
            var missing = new HashSet<string>();
            var drawn = new HashSet<int>();
            drawCommitted(c, tileDoc, tilePathRect, tileCounters, ports, unknown, missing, drawn);

            return new LayoutTile(surface.Snapshot(), side, ports, [.. unknown], [.. missing], drawn);
        }
        catch (System.Exception)
        {
            // A surface this small failing is not worth a message, and there is a correct slower path
            // one line away — the same bargain the instance raster tier strikes.
            return null;
        }
        finally { surface?.Dispose(); }
    }
}
