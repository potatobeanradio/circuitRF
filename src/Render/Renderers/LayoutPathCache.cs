// Per-shape SKPath cache (L2c §3, docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md, R-L2c-3/4).
// Lives here, in src/Ui/Renderers/ (Skia), never in src/Ui/Layout/ (framework-free) — caching SKPath
// instances is exactly the kind of state the Layout/Renderer firewall exists to keep out of the model.

using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// Caches each shape's fill path in SHAPE-LOCAL space — relative to the shape's own DBU bbox minimum,
/// not the per-frame path-space origin L1a's <see cref="LayoutRenderer.PathSpace"/> uses.
///
/// <b>R-L2c-3 — the trap this avoids.</b> Path space is anchored near the viewport CENTRE and re-quantized
/// on essentially every pan (<see cref="LayoutRenderer.ComputeOrigin"/>) — a path cached directly in path
/// space would be wrong (or at best, stale-and-about-to-be-invalidated) after almost every frame, doing net
/// harm instead of good. Building each path once relative to the shape's OWN bbox min sidesteps this
/// entirely: that reference point never changes unless the shape's geometry itself changes, so the cached
/// path survives any number of pans/zooms untouched. Reusing <see cref="LayoutRenderer.PathSpace"/> with the
/// shape's own bbox min as the origin (instead of the per-frame one) is what makes this a few-line reuse
/// rather than a second geometry pipeline — the same affine-in-DBU math that makes path space itself work
/// (<c>ps.X(dbu) = (dbu - origin) * dbuToUm</c>) means <c>ps.X(dbu) == local.X(dbu) + ps.X(refX)</c> for ANY
/// reference point, so drawing the cached local path under a translate by <c>(ps.X(refX), ps.Y(refY))</c>
/// reproduces the exact current-frame path-space position — see <see cref="LayoutRenderer.DrawLayer"/>.
///
/// <b>R-L2c-4 — bounded, LRU over the recently-drawn set.</b> An unbounded cache of up to 500k native
/// <c>SKPath</c> objects is a real memory cost, not a free lunch; evicting the least-recently-drawn entries
/// keeps steady-state memory bounded regardless of total document size, at the cost of re-building paths for
/// shapes that scroll in and out of the cached "working set" (e.g. panning across a design larger than the
/// cache). <see cref="SKPath"/> is <c>IDisposable</c> (owns native Skia buffers) — every eviction path
/// disposes it explicitly; never rely on the finalizer for this.
/// </summary>
public sealed class LayoutPathCache
{
    private sealed class Entry(SKPath? localPath, long refX, long refY)
    {
        /// <summary>The shape's own fill path, or null when only the widened one has been asked for.
        /// Null is not a placeholder for "not cached yet" at the ENTRY level — the entry exists and its
        /// reference point is fixed; it means this shape has so far only been drawn through the
        /// hairline tier, which never touches the un-widened outline. Building it anyway cost ~42,000
        /// unused <c>GetFillPath</c>+<c>Simplify</c> pairs on the first frame of an imported Gerber
        /// panel, and held their native buffers for as long as the document stayed open.</summary>
        public SKPath? LocalPath = localPath;
        public readonly long RefX = refX, RefY = refY;

        /// <summary>The vertex-decimation tolerance <see cref="LocalPath"/> was built at
        /// (<see cref="LayoutRenderDetail"/>), or -1 while nothing is built. Mirrors
        /// <see cref="WidenedAtDbu"/>'s contract exactly, and for the same reason: the tolerance is a
        /// function of ZOOM alone, so a pan is all hits and only a zoom OCTAVE rebuilds. Without it a
        /// cached path outlives the zoom level it was thinned for — which shows up as a shape that
        /// stays coarse after zooming in, the one failure mode a decimation tier can have.</summary>
        public long LocalDetailDbu = -1;

        /// <summary>The stroke-elision tier's widened outline for this shape, and the widening it was
        /// built at (DBU). Mirrors <c>LayoutRenderer.CompiledChunk.Elided</c> exactly: the widening is
        /// a device-pixel allowance expressed in DBU and BUCKETED to a rung of
        /// <c>LayoutRenderDetail.WidenDbu</c>'s ladder, so it is a coarse function of ZOOM alone — which
        /// is what makes caching it worthwhile. A pan holds zoom fixed, so every frame of the gesture
        /// this exists to make smooth is a hit; a zoom rebuilds only when it crosses a rung, not on every
        /// frame of the gesture. Null until a frame actually asks, so a document that never engages the
        /// tier pays nothing for it.</summary>
        public SKPath? WidenedPath;
        public long WidenedAtDbu = -1;
        public long WidenedDetailDbu = -1;

        /// <summary>The hairline tier's stroke route (see <see cref="GetOrBuildCentreline"/>), keyed on
        /// the decimation tolerance ALONE — no widening, so a zoom gesture does not invalidate it.</summary>
        public SKPath? CentrelinePath;
        public long CentrelineDetailDbu = -1;

        public void DisposeAll()
        {
            LocalPath?.Dispose();
            WidenedPath?.Dispose();
            CentrelinePath?.Dispose();
        }
    }

    private readonly Dictionary<int, LinkedListNode<(int Index, Entry Entry)>> _map = new();
    private readonly LinkedList<(int Index, Entry Entry)> _lru = new(); // most-recently-used at the front

    public int Capacity { get; }

    public LayoutPathCache(int capacity = 50_000) => Capacity = System.Math.Max(1, capacity);

    public int Count => _map.Count;

    // ── Test/diagnostic hooks (internal, InternalsVisibleTo CircuitRF.Ui.Tests) ──────────────────
    internal int HitCount { get; private set; }
    internal int MissCount { get; private set; }
    internal int EvictionCount { get; private set; }

    /// <summary>Returns the shape's path in local space plus the DBU point it is relative to. Builds
    /// and caches on a miss (incrementing <paramref name="counters"/>'s <c>PathsConstructed</c> exactly
    /// as a fresh, uncached build would — a cache MISS still allocates real <c>SKPath</c> objects this
    /// frame; a HIT allocates none); moves the entry to the front of the LRU list on either outcome.</summary>
    internal (SKPath LocalPath, long RefX, long RefY) GetOrBuild(int index, LayoutShape shape, double dbuToUm, long detailDbu, LayoutFrameCounters? counters, out bool wasHit)
    {
        var entry = Touch(index, shape);
        if (entry.LocalPath is { } cached && entry.LocalDetailDbu == detailDbu)
        {
            HitCount++;
            wasHit = true;
            return (cached, entry.RefX, entry.RefY);
        }

        MissCount++;
        wasHit = false;

        entry.LocalPath?.Dispose();
        var localPs = new LayoutRenderer.PathSpace(entry.RefX, entry.RefY, dbuToUm);
        entry.LocalPath = LayoutRenderer.BuildShapePath(shape, localPs, counters, detailDbu) ?? new SKPath();
        entry.LocalDetailDbu = detailDbu;
        return (entry.LocalPath, entry.RefX, entry.RefY);
    }

    /// <summary>
    /// A <see cref="PathShape"/>'s CENTRELINE in shape-local space, for the hairline tier's stroke
    /// route — the batch <c>DrawLayer</c> strokes at <c>Width + the pen</c> instead of filling a
    /// per-shape widened outline.
    ///
    /// <para><b>Its key is the decimation tolerance ALONE, and that is the whole point of it.</b>
    /// <see cref="GetOrBuildWidened"/> has the widening in its key, so it rebuilds whenever the zoom
    /// crosses a rung of that ladder — and a scroll-wheel click is 1.15x, which is 0.20 of an octave
    /// against a rung of 0.125, so EVERY click misses on EVERY shape. A centreline does not depend on
    /// the widening at all: the width is applied by the paint when the batch is stroked. The tolerance
    /// is bucketed to a whole octave, so a wheel zoom rebuilds roughly once in every three or four
    /// clicks rather than on all of them.</para>
    /// </summary>
    internal (SKPath? LocalPath, long RefX, long RefY) GetOrBuildCentreline(
        int index, PathShape shape, double dbuToUm, long detailDbu, LayoutFrameCounters? counters)
    {
        var entry = Touch(index, shape);
        if (entry.CentrelinePath is not null && entry.CentrelineDetailDbu == detailDbu)
        {
            HitCount++;
            return (entry.CentrelinePath, entry.RefX, entry.RefY);
        }

        entry.CentrelinePath?.Dispose();
        var localPs = new LayoutRenderer.PathSpace(entry.RefX, entry.RefY, dbuToUm);
        entry.CentrelinePath = LayoutRenderer.BuildPathCentreline(shape, localPs, counters, detailDbu);
        entry.CentrelineDetailDbu = detailDbu;
        MissCount++;
        return (entry.CentrelinePath, entry.RefX, entry.RefY);
    }

    // ── Which of the hairline tier's two routes a layer draws with ──────────────────────────────
    //
    // The two draw the SAME region — a centreline stroked at Width + the pen — one by filling a
    // per-shape outline built at that width, the other by stroking a batch of centrelines with a paint
    // of that width. They differ only in WHERE the stroker runs: once per shape into a cache, or once
    // per frame over the batch. So the frame is free to pick whichever is cheaper, and the answer flips
    // with what the user is doing.
    //
    //   * Zoom SETTLED — every widened outline is already built, the fill route rasterizes a cached
    //     path and nothing is stroked. Measured on a 16,917-shape imported panel at 3400x2000:
    //     33.5 ms a frame against 58.7 for the stroke route.
    //   * Zoom MOVING — every widened outline is stale, so the fill route must rebuild all of them:
    //     159.1 ms a frame, and 322 on the first. The stroke route rebuilds nothing and draws in 45.8.
    //
    // The rule below is therefore: stroke while anything is missing, fill once everything is there, and
    // only ever BUILD while the key is holding still. A gesture then costs no builds at all, and the
    // warm-up that follows it is spread over a few frames instead of landing in one.
    private (long Widen, long Detail) _lastElisionKey = (-1, -1);
    private bool _elisionKeyStable;
    private readonly HashSet<LayerKey> _fillRouteReady = [];

    /// <summary>
    /// Called ONCE per frame, before any layer is drawn, with the key this frame's hairline outlines
    /// would be built at.
    ///
    /// <para><b>Once per frame, not once per layer</b>, and that is not a tidiness point: asked per
    /// layer, the first layer saw the change and updated the key, so every layer after it in the same
    /// frame saw a key that already matched and went on building. A zoom gesture then paid the full
    /// rebuild on every frame anyway, which is the entire cost this exists to avoid.</para>
    /// </summary>
    internal void NoteFrameElisionKey(long widenDbu, long detailDbu)
    {
        var key = (widenDbu, detailDbu);
        _elisionKeyStable = _lastElisionKey == key;
        if (_elisionKeyStable) return;

        // The zoom moved. Nothing built at the old key is usable, and building at the new one is wasted
        // the moment the next click lands — so this frame strokes, and builds nothing.
        _lastElisionKey = key;
        _fillRouteReady.Clear();
    }

    /// <summary>Called once per layer per frame, BEFORE its shapes are walked. Returns whether this
    /// layer may use the fill route, and whether this frame may build widened outlines at all.</summary>
    internal (bool UseFillRoute, bool MayBuild) BeginElisionFrame(LayerKey layer)
        => (_elisionKeyStable && _fillRouteReady.Contains(layer), _elisionKeyStable);

    /// <summary>Called once per layer per frame, AFTER its shapes are walked, with whether every
    /// hairline shape on it had a widened outline in hand. One frame of lag is deliberate and
    /// invisible: the two routes draw the same geometry, so which one a frame used cannot be seen.</summary>
    internal void EndElisionFrame(LayerKey layer, bool allWidenedCached)
    {
        if (allWidenedCached) _fillRouteReady.Add(layer);
        else _fillRouteReady.Remove(layer);
    }

    /// <summary>
    /// The stroke-elision tier's widened outline for a <see cref="PathShape"/> whose on-screen width is
    /// under a few device pixels — the same centreline stroked at <c>Width + widenDbu</c>, in the same
    /// shape-LOCAL space (and against the same reference point) <see cref="GetOrBuild"/> uses, so both
    /// are drawn under the identical translate.
    ///
    /// <para><b>Why a widened FILL stands in for the pair at all.</b> A <see cref="PathShape"/>'s fill
    /// IS its centreline stroked at <c>Width</c>; drawing that fill and then outlining it with a pen of
    /// <c>w</c> device pixels covers precisely the same region as filling the centreline stroked at
    /// <c>Width + w</c>. So one filled path replaces a fill plus an outline pass — which is what makes
    /// this worth doing at all, given the outline pass is what a stroke-per-segment Gerber makes ruinous
    /// (Skia's stroker runs over every sub-path in the batch, and there can be tens of thousands of them
    /// on one copper layer).</para>
    ///
    /// <para><b>That identity was exact until 2026-09-12 and is now a BOUNDED OVER-COVER, which is the
    /// price of the key below being cacheable.</b> <paramref name="widenDbu"/> no longer arrives as the
    /// pen converted to DBU; it arrives bucketed UP an eighth-octave ladder
    /// (<c>LayoutRenderDetail.WidenDbu</c>), so it is between 1x and 2^(1/8) = 1.0905x the pen. The
    /// substitution therefore covers the pair's region and at most 0.09 device pixels more of margin on
    /// each side — never less, which is the direction that matters, since a widening under the pen would
    /// draw hairline artwork thinner than the frame meant to. Measured as ink coverage on separated
    /// one-mil traces, the worst zoom in a bucket over-covers by 4.3% and the frames either side of a
    /// rung differ by that much; the gate is
    /// <c>LayoutHairlineFillTests.TheOverCoverAtARung_StaysWithinTheDocumentedBound</c>.</para>
    ///
    /// <para>Keyed on <paramref name="widenDbu"/>: it is a device-pixel allowance converted to DBU and
    /// then bucketed, so it changes with zoom, only with zoom, and only once per bucket of it. A pan is
    /// all hits; a continuous zoom inside one bucket rebuilds NOTHING, and crossing a rung rebuilds the
    /// working set once. (Read straight from the zoom, as it was, "once" meant once per FRAME of a pinch
    /// gesture — 192,680 paths a frame on an imported raster-fill board, and this comment saying "a zoom
    /// step" is what let that survive.) Returns null only if the shape has no buildable outline.</para>
    /// </summary>
    /// <summary>The widened outline if it is already built at this key, else null — never builds.
    /// Touches the LRU exactly as a build would, so a probe cannot evict the shape it is asking about.</summary>
    internal (SKPath? LocalPath, long RefX, long RefY) TryGetWidened(
        int index, PathShape shape, long widenDbu, long detailDbu)
    {
        var entry = Touch(index, shape);
        if (entry.WidenedPath is not null && entry.WidenedAtDbu == widenDbu && entry.WidenedDetailDbu == detailDbu)
        {
            HitCount++;
            return (entry.WidenedPath, entry.RefX, entry.RefY);
        }
        return (null, entry.RefX, entry.RefY);
    }

    internal (SKPath? LocalPath, long RefX, long RefY) GetOrBuildWidened(
        int index, PathShape shape, long widenDbu, double dbuToUm, long detailDbu, LayoutFrameCounters? counters)
    {
        // Shares the entry — and therefore the reference point — with the un-widened path, so a frame
        // that mixes the two tiers draws both under the same translate. It deliberately does NOT build
        // the un-widened path: a shape reached only through the hairline tier never draws it.
        var entry = Touch(index, shape);

        if (entry.WidenedPath is not null && entry.WidenedAtDbu == widenDbu && entry.WidenedDetailDbu == detailDbu)
        {
            HitCount++;
            return (entry.WidenedPath, entry.RefX, entry.RefY);
        }

        entry.WidenedPath?.Dispose();
        var localPs = new LayoutRenderer.PathSpace(entry.RefX, entry.RefY, dbuToUm);

        // A clone, never a mutation of the model's own shape: this runs on the render thread while the
        // UI thread owns the document, and Width is user-editable state.
        var widened = new PathShape
        {
            Layer = shape.Layer, Xy = shape.Xy, Edges = shape.Edges,
            Width = shape.Width + widenDbu, End = shape.End, FlattenTolDbu = shape.FlattenTolDbu,
        };
        entry.WidenedPath = LayoutRenderer.BuildShapePath(widened, localPs, counters, detailDbu);
        entry.WidenedAtDbu = widenDbu;
        entry.WidenedDetailDbu = detailDbu;
        MissCount++;
        return (entry.WidenedPath, entry.RefX, entry.RefY);
    }

    /// <summary>The entry for <paramref name="index"/>, created (with its shape-local reference point)
    /// and moved to the front of the LRU. Neither path is built here — the caller builds the one it
    /// actually needs.</summary>
    private Entry Touch(int index, LayoutShape shape)
    {
        if (_map.TryGetValue(index, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Entry;
        }

        var bb = LayoutGeometry.BboxOf(shape);
        var entry = new Entry(null, bb.IsEmpty ? 0 : bb.MinX, bb.IsEmpty ? 0 : bb.MinY);
        var newNode = new LinkedListNode<(int, Entry)>((index, entry));
        _lru.AddFirst(newNode);
        _map[index] = newNode;
        EvictIfOverCapacity();
        return entry;
    }

    private void EvictIfOverCapacity()
    {
        while (_map.Count > Capacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Index);
            last.Value.Entry.DisposeAll();
            EvictionCount++;
        }
    }

    /// <summary>
    /// Invalidation rides on the SAME <see cref="LayoutChangeInfo"/> payload L2b's
    /// <c>LayoutSpatialIndex.Apply</c> already consumes — no second notification path (R-L2c-3's own
    /// instruction). <see cref="LayoutChangeKind.Appended"/> needs no action (new indices have nothing
    /// cached yet, built lazily on first draw); <see cref="LayoutChangeKind.RemovedTrailing"/> and
    /// <see cref="LayoutChangeKind.Updated"/> evict exactly the affected indices;
    /// <see cref="LayoutChangeKind.Full"/> clears everything.
    /// </summary>
    public void Apply(LayoutChangeInfo info)
    {
        switch (info.Kind)
        {
            case LayoutChangeKind.Full:
                Clear();
                break;

            case LayoutChangeKind.RemovedTrailing:
                for (int i = info.StartIndex; i < info.StartIndex + info.Count; i++)
                    Remove(i);
                break;

            case LayoutChangeKind.Updated:
                foreach (var i in info.Indices!)
                    Remove(i);
                break;

            case LayoutChangeKind.Appended:
                break;
        }
    }

    private void Remove(int index)
    {
        if (!_map.TryGetValue(index, out var node)) return;
        _lru.Remove(node);
        _map.Remove(index);
        node.Value.Entry.DisposeAll();
    }

    public void Clear()
    {
        foreach (var node in _map.Values)
            node.Value.Entry.DisposeAll();
        _map.Clear();
        _lru.Clear();
    }
}
