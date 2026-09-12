// RF4 / L2d — the tiled raster cache for COMMITTED TOP-LEVEL GEOMETRY.
// docs/sonnet-briefs/brief-rasterfill-4-tiled-raster-cache.md, and docs/design/layout-view.md §5.3,
// which deferred this until there was a measured shortfall to point at. There is one: see
// src/Render/RESOLVED.md.
//
// This file is the CACHE — what a tile is, how tiles are keyed, how many are kept and how they are
// disposed. What goes INTO one is LayoutRenderer.Tiles.cs; the two are deliberately separate for the
// same reason LayoutPathCache is separate from DrawLayer.

using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// One tile's identity. Two frames that agree on every field here would have rasterized the same
/// pixels into this tile, and that is the entire licence for reusing one.
///
/// <para><b>The zoom is EXACT, not bucketed</b> (R-rf4-4). The brief offers octave bucketing with a
/// scaled blit as the cheaper alternative; it was measured against re-tiling on settle and rejected —
/// see <c>src/Render/RESOLVED.md</c>. The short version is that a scaled blit cannot satisfy R-rf4-7:
/// a mid-octave frame is a resample of a differently-scaled raster and is not the frame the renderer
/// would otherwise have drawn, and on one-mil artwork that softening is exactly what the user is
/// looking at.</para>
///
/// <para><b><see cref="VisualKey"/> folds every per-frame decision that changes what a layer looks
/// like</b> — the frame's outline decision, which layers are on the merge tier, the decimation and
/// hairline-widening rungs, the technology's own layer table and the theme. Those are all functions
/// of zoom and of the document in the ordinary case, so they are constant across a pan and cost
/// nothing; when one of them does change, the key changes and the tiles rebuild, which is
/// correct-and-slower rather than a stale picture.</para>
/// </summary>
internal readonly record struct TileKey(long OriginX, long OriginY, double Zoom, int TileX, int TileY, int VisualKey);

/// <summary>One rasterized tile plus the per-frame side products the pass that built it produced.</summary>
/// <remarks>
/// <b>The side products are here because a blit frame never runs the layer loop</b>, and three things
/// that loop produces are not pixels: the ports it defers to the frame's own top pass
/// (<c>DrawPortGlyphs</c>), the layer keys the technology did not define, and the cell references that
/// did not resolve. Recomputing them on a blit frame would mean walking the candidates again — the
/// work this tier exists to avoid — and dropping them would make a port glyph flicker out the moment
/// the cache warmed. They are per TILE, so a frame gathers exactly the ones its own tiles carry.
/// </remarks>
internal sealed class LayoutTile(
    SKImage image, int sidePx,
    List<LayoutRenderer.DeferredPort> ports, List<LayerKey> unknownLayers, List<string> missingCellRefs,
    HashSet<int> shapeIndices)
{
    public SKImage Image { get; } = image;
    public int SidePx { get; } = sidePx;

    /// <summary>Ports collected while this tile was built. Drawn LIVE over the blit every frame,
    /// never baked in — a port glyph is a fixed device size and R-rf4-2 keeps it out of the raster.</summary>
    public List<LayoutRenderer.DeferredPort> Ports { get; } = ports;

    public List<LayerKey> UnknownLayers { get; } = unknownLayers;
    public List<string> MissingCellRefs { get; } = missingCellRefs;

    /// <summary>
    /// The shape indices this tile rasterized — its padded candidate set.
    ///
    /// <para><b>It is here because a move cannot be invalidated from the new position alone</b>
    /// (R-rf4-9). <see cref="LayoutChangeInfo"/> carries INDICES, not geometry, and by the time a
    /// subscriber is told, the shape has already moved — so the tile the shape came FROM can only be
    /// found by asking which tiles drew that index. Invalidating the destination and not the source is
    /// precisely the ghost-left-behind defect <see cref="LayoutPathCache"/>'s own comment documents,
    /// one level up and inside an image where it is harder to see.</para>
    ///
    /// <para>Costs about 12 bytes per candidate per tile and is counted against the cache's cap along
    /// with the pixels, so it cannot quietly become the thing that blows the budget.</para>
    /// </summary>
    public HashSet<int> ShapeIndices { get; } = shapeIndices;

    /// <summary>Bytes of native and managed memory this tile holds, for the cap in
    /// <see cref="LayoutTileCache"/>.</summary>
    public long Bytes => (long)SidePx * SidePx * 4 + ShapeIndices.Count * 12L;

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Bounded, LRU, explicitly disposed cache of rasterized document tiles (R-rf4-5).
///
/// <para>Owned by the CALLER for the lifetime of one open document, exactly as
/// <see cref="LayoutPathCache"/> is and for the same reason: <see cref="LayoutRenderer.Draw"/> is a
/// stateless static method and a cache that does not survive a frame does nothing at all. A caller
/// that supplies none — every export, every one-shot render, every existing test — gets today's
/// renderer with no tier in the way, which is what makes R-rf4-8 and gate 7 structural rather than a
/// flag someone has to remember.</para>
///
/// <para><b>An <see cref="SKImage"/> owns native pixels and is disposed on eviction</b>, never left to
/// a finalizer — the rule <see cref="LayoutPathCache"/> already states for <c>SKPath</c>. The cap is on
/// BYTES rather than on a tile count because a tile's size follows the tile pitch and the device
/// scale, so counting tiles would cap two different amounts of memory on two different displays.</para>
/// </summary>
public sealed class LayoutTileCache : IDisposable
{
    /// <summary>
    /// The tile pitch, in DEVICE pixels.
    ///
    /// <para>512 is measured, not assumed — see <c>src/Render/RESOLVED.md</c>. Smaller tiles make a
    /// pan cheaper at the leading edge (less is re-rasterized when new document comes into view) and
    /// a full repaint dearer (every tile pays the per-tile candidate filter and a surface of its own);
    /// larger tiles the reverse. At 512 a 1600x1000 viewport is covered by 4x3 tiles and a 3200x2000
    /// one by 7x5.</para>
    /// </summary>
    internal const int TileDevicePixels = 512;

    /// <summary>
    /// How far OUTSIDE its own rectangle a tile rasterizes before it is cropped back, in device pixels.
    ///
    /// <para><b>This is the seam</b> (R-rf4-7). A tile rasterized with a CLIP at its own edge
    /// antialiases every shape against that edge, and two such tiles composited source-over leave a
    /// lighter line down the join — <c>(1-a)(1-b) != 0</c> — which is the defect the brief calls the
    /// hard part of this work. Rasterizing a padded region and blitting only the core instead means
    /// the pixels at the core's edge were produced with the neighbouring geometry present, exactly as
    /// a full-frame render produces them. Skia's coverage for a pixel depends only on path edges
    /// within about a pixel of it, so the padding only has to exceed the widest thing that can paint
    /// across the boundary: the 2-device-pixel geometry stroke, plus antialiasing. 8 is that with room
    /// to spare, and it costs (1 + 2*8/512)^2 = 6.3% more rasterized area.</para>
    /// </summary>
    internal const int TilePaddingDevicePixels = 8;

    /// <summary>
    /// Total tile memory held, in bytes.
    ///
    /// <para>96 MB is 48 tiles at 512+2*8 square in RGBA8888 — four viewports' worth at 1600x1000 and
    /// about two at 3200x2000, so a pan has somewhere to pan back TO and a zoom that returns to a
    /// previous level finds it still there. The owner asked directly, during the instance-raster work,
    /// whether that tier's problem was memory; it was not, and this one must not become the change
    /// that makes the answer yes. The steady-state figure a real board actually reaches is in
    /// <c>src/Render/RESOLVED.md</c> next to this cap.</para>
    /// </summary>
    public const long DefaultMaxBytes = 96L * 1024 * 1024;

    private readonly Dictionary<TileKey, LinkedListNode<(TileKey Key, LayoutTile Tile)>> _map = [];
    private readonly LinkedList<(TileKey Key, LayoutTile Tile)> _lru = new();   // most-recently-used at the front
    private long _bytes;

    public LayoutTileCache(long maxBytes = DefaultMaxBytes) => MaxBytes = System.Math.Max(1, maxBytes);

    public long MaxBytes { get; }
    public long BytesHeld => _bytes;
    public int Count => _map.Count;

    // ── Test/diagnostic hooks ────────────────────────────────────────────────────────────────────
    internal int EvictionCount { get; private set; }

    /// <summary>
    /// The zoom the last frame that consulted this cache was drawn at, and the one before it.
    ///
    /// <para><b>This is how a zoom gesture stays out of the cache without any gesture plumbing</b>
    /// (R-rf4-4). A tile is built only on a frame whose zoom equals the previous frame's — so every
    /// frame of a continuous pinch, each at a new zoom, draws live exactly as it does today, and the
    /// first frame after the gesture stops is the one that tiles. No canvas has to tell the renderer
    /// that a gesture ended, which matters because there are several ways to zoom (wheel, pinch, a
    /// zoom box, a keyboard step) and only some of them have a gesture at all.</para>
    /// </summary>
    internal double LastZoom { get; private set; } = double.NaN;
    internal bool ZoomSettled { get; private set; }

    internal void NoteFrameZoom(double zoom)
    {
        ZoomSettled = zoom == LastZoom;
        LastZoom = zoom;
    }

    internal LayoutTile? Get(TileKey key)
    {
        if (!_map.TryGetValue(key, out var node)) return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Tile;
    }

    internal void Put(TileKey key, LayoutTile tile)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _bytes -= existing.Value.Tile.Bytes;
            existing.Value.Tile.Dispose();
            _lru.Remove(existing);
            _map.Remove(key);
        }

        var node = _lru.AddFirst((key, tile));
        _map[key] = node;
        _bytes += tile.Bytes;

        // Evict from the back — least recently drawn — until the cap holds. Never evict the tile just
        // added, even if it alone exceeds the cap: the alternative is a tier that stores nothing and
        // rebuilds every frame, which is strictly worse than not having it.
        while (_bytes > MaxBytes && _lru.Count > 1)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            _bytes -= last.Value.Tile.Bytes;
            last.Value.Tile.Dispose();
            EvictionCount++;
        }
    }

    /// <summary>
    /// R-rf4-6 — invalidation rides <see cref="LayoutChangeInfo"/>, the notification that already
    /// drives <see cref="LayoutPathCache.Apply"/> and the spatial index. A second notification path is
    /// a second thing to get out of step.
    ///
    /// <para><b>Both ends of a move, and that is the whole subtlety.</b> A changed shape has to drop
    /// the tiles it USED to paint into and the tiles it paints into NOW. The first comes from each
    /// tile's own record of the indices it rasterized (<see cref="LayoutTile.ShapeIndices"/>) — the
    /// only thing that still knows where the shape was, since the notification carries indices and the
    /// model has already moved on. The second comes from the shape's current bbox against each tile's
    /// document bounds. Doing only the second leaves a ghost; doing only the first leaves a hole.</para>
    ///
    /// <para>An index-shifting change — anything that is not an append, a trailing removal or an
    /// in-place update — clears, because every tile's recorded index set would otherwise name
    /// different shapes than it did. So does a change this cannot classify at all, which is the honest
    /// answer when the notification says "everything".</para>
    /// </summary>
    public void Apply(LayoutChangeInfo info, LayoutView view)
    {
        if (_map.Count == 0) return;

        switch (info.Kind)
        {
            case LayoutChangeKind.InstancesChanged:
                // The tier does not engage on a document with instances at all (see
                // LayoutRenderer.TryDrawTiled), so nothing held here can be showing one — but a
                // document that has just GAINED its first instance must stop serving tiles that were
                // built without it, and clearing is both correct and free.
                Clear();
                return;

            case LayoutChangeKind.Appended:
                InvalidateByBbox(view, info.StartIndex, info.Count);
                return;

            case LayoutChangeKind.RemovedTrailing:
                InvalidateByIndex(info.StartIndex, info.Count);
                return;

            case LayoutChangeKind.Updated when info.Indices is { Count: > 0 } indices:
                InvalidateByIndex(indices);
                InvalidateByBbox(view, indices);
                return;

            default:
                Clear();
                return;
        }
    }

    private void InvalidateByIndex(int start, int count) =>
        DropWhere(tile =>
        {
            for (int i = start; i < start + count; i++) if (tile.ShapeIndices.Contains(i)) return true;
            return false;
        });

    private void InvalidateByIndex(IReadOnlyList<int> indices) =>
        DropWhere(tile =>
        {
            foreach (var i in indices) if (tile.ShapeIndices.Contains(i)) return true;
            return false;
        });

    private void InvalidateByBbox(LayoutView view, int start, int count)
    {
        var boxes = BoxesOf(view, start, count);
        if (boxes.Count > 0) DropWhereKey(key => Reaches(key, boxes));
    }

    private void InvalidateByBbox(LayoutView view, IReadOnlyList<int> indices)
    {
        var boxes = BoxesOf(view, indices);
        if (boxes.Count > 0) DropWhereKey(key => Reaches(key, boxes));
    }

    private static List<Bbox> BoxesOf(LayoutView view, int start, int count)
    {
        var boxes = new List<Bbox>(System.Math.Max(0, count));
        var shapes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(view.Shapes);
        for (int i = start; i < start + count; i++)
        {
            if ((uint)i >= (uint)shapes.Length || shapes[i] is not { } s) continue;
            var b = LayoutGeometry.BboxOf(s);
            if (!b.IsEmpty) boxes.Add(b);
        }
        return boxes;
    }

    private static List<Bbox> BoxesOf(LayoutView view, IReadOnlyList<int> indices)
    {
        var boxes = new List<Bbox>(indices.Count);
        var shapes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(view.Shapes);
        foreach (var i in indices)
        {
            if ((uint)i >= (uint)shapes.Length || shapes[i] is not { } s) continue;
            var b = LayoutGeometry.BboxOf(s);
            if (!b.IsEmpty) boxes.Add(b);
        }
        return boxes;
    }

    private static bool Reaches(TileKey key, List<Bbox> boxes)
    {
        var (minX, minY, maxX, maxY) = LayoutRenderer.TileDocumentBounds(key);
        foreach (var b in boxes)
            if (b.MaxX >= minX && b.MinX <= maxX && b.MaxY >= minY && b.MinY <= maxY) return true;
        return false;
    }

    private void DropWhere(System.Func<LayoutTile, bool> predicate)
    {
        List<TileKey>? doomed = null;
        foreach (var (key, node) in _map)
            if (predicate(node.Value.Tile)) (doomed ??= []).Add(key);
        if (doomed is not null) foreach (var key in doomed) Remove(key);
    }

    private void DropWhereKey(System.Func<TileKey, bool> predicate)
    {
        List<TileKey>? doomed = null;
        foreach (var key in _map.Keys)
            if (predicate(key)) (doomed ??= []).Add(key);
        if (doomed is not null) foreach (var key in doomed) Remove(key);
    }

    private void Remove(TileKey key)
    {
        if (!_map.TryGetValue(key, out var node)) return;
        _lru.Remove(node);
        _map.Remove(key);
        _bytes -= node.Value.Tile.Bytes;
        node.Value.Tile.Dispose();
        InvalidationCount++;
    }

    /// <summary>Tiles dropped by <see cref="Apply"/>. Gate 9 is an assertion on this: an edit that
    /// invalidated every tile would turn the cache off without anything looking wrong.</summary>
    internal int InvalidationCount { get; private set; }

    /// <summary>Drops every tile. The answer to a change this cache cannot localise, and to a layer
    /// visibility / technology / theme change, which the caller signals directly.</summary>
    public void Clear()
    {
        foreach (var node in _lru) node.Tile.Dispose();
        _lru.Clear();
        _map.Clear();
        _bytes = 0;
    }

    public void Dispose() => Clear();
}
