// L3a — instance (SREF) and array (AREF) rendering (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md).
// Partial-class extension of LayoutRenderer, kept in its own file per this codebase's convention for a
// large concern that deserves its own home (mirrors LayoutEditorViewModel's per-concern partial files).
//
// R-L3a-3 — the phase's headline requirement: "a sub-cell's geometry is built once and drawn once per
// placement under a matrix." A resolved sub-cell is compiled EXACTLY ONCE, per layer, into a GRID OF
// CHUNKS each holding one aggregate path (reusing BuildShapePath so PathsConstructed still counts real
// path construction), cached by the LayoutView INSTANCE CellLayoutResolver's own (path, mtime) cache
// returns — a ConditionalWeakTable keyed on that reference means the compile cache and the resolver
// cache invalidate TOGETHER for free (a file change produces a NEW LayoutView instance on the next
// resolve, which is simply a cache miss here; the old compiled entry becomes unreachable and its
// SKPaths are reclaimed via SKObject's own finalizer — no separate invalidation call needed).
//
// This composes with L2c's shape-local path cache for exactly the reason the brief calls out: R-L2c-3
// cached shape paths in SHAPE-LOCAL space specifically so a pan (which moves the per-frame path-space
// origin) never invalidates them. The SAME property is what makes a COMPILED CELL reusable across every
// placement of every instance referencing it: the compiled paths live in CELL-LOCAL path space (origin
// at the sub-cell's own (0,0), never the per-frame viewport-anchored one), so the per-placement SKMatrix
// is the ONLY thing that varies frame to frame or placement to placement. Had L2c cached in path space
// instead of shape-local space, this reuse would not be possible — the second time that decision has
// paid off.
//
// The per-layer geometry was ONE path until L2e, and that is what made a dense PCell slow. The compile
// cache does its job — a cell is built once — but Skia rasterizes by walking every segment of the path
// it is handed, so cost stayed proportional to the cell's TOTAL geometry no matter how little of it was
// on screen. On a real design whose MIM capacitor carries a 158x158 field of 0.42um vias, that
// meant ~35 ms/frame at Zoom-to-Fit AND ~16 ms/frame zoomed 256x in with 640 vias visible — zooming in
// bought almost nothing, which is the signature of missing culling rather than of too much geometry.
// Two changes address it, and LayoutInstanceChunkCullingTests holds both:
//
//   1. CHUNKING — the layer is a grid of chunks, each with its own bounds, and DrawInstances maps the
//      viewport back through each placement's matrix to skip the ones off screen. Pixel-identical by
//      construction: a chunk's bounds are the union of what it draws.
//   2. STROKE ELISION — a chunk whose largest primitive is under DefaultStrokeElisionDevicePixels draws
//      as one solid grown fill instead of a fill pass plus an outline pass. Stroking was where the time
//      actually went (82 ms of a 102 ms layer, tessellating outlines for ~100k segments) and at that
//      size the outline IS the shape. For an opaque axis-aligned rect the substitution is EXACT, which
//      is why a via field loses nothing; where it is not exact the geometry is too small to resolve.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

public static partial class LayoutRenderer
{
    /// <summary>One spatial chunk of one layer's compiled geometry (L2e stage 1). A compiled cell used
    /// to be ONE aggregate path per layer, which made path CONSTRUCTION a once-per-cell cost (the point
    /// of the compile cache) but left RASTERIZATION proportional to the cell's total geometry at every
    /// zoom — Skia walks every segment of a path it is handed, so a 24,964-via MIM cap cost the same
    /// 43 ms zoomed 256x in, with 640 vias on screen, as it did at full extent. Splitting the layer into
    /// a grid of chunks, each carrying its own bounds, restores the culling that
    /// <see cref="LayoutSpatialIndex"/> already performs one level up: the same idea, one level down.
    ///
    /// <para><see cref="PrimitiveBounds"/> is the per-primitive bbox list the stroke-elision tier draws
    /// from (see <see cref="DefaultStrokeElisionDevicePixels"/>) — kept because at the few-device-pixel sizes
    /// that tier engages at, a polygon and its bbox are indistinguishable, which is the same
    /// equivalence <see cref="AddMinimalRect"/> already relies on one level up.</para>
    /// </summary>
    private sealed class CompiledChunk
    {
        public SKRect Bounds;
        public readonly SKPath Geometry = new();
        public SKRect[] PrimitiveBounds = [];
        /// <summary>Largest single-primitive extent in this chunk, in cell-local path space (microns) —
        /// what the stroke-elision decision is taken against, so one oversized primitive in an otherwise
        /// tiny cluster keeps the whole chunk on the exact tier rather than silently coarsening it.</summary>
        public float MaxExtent;

        /// <summary>The stroke-elision tier's grown-bounds path, and the grow amount it was built at.
        /// The grow amount is half a DEVICE pixel expressed in path space, so it is a function of zoom
        /// ALONE — which is precisely why caching it is worth the memory: a pan holds zoom fixed, so
        /// every frame of the gesture this whole change exists to make smooth is a hit, and only a zoom
        /// step rebuilds.
        ///
        /// <para><b>Published as one immutable snapshot, never mutated in place, and that is
        /// load-bearing.</b> Avalonia runs <c>ICustomDrawOperation.Render</c> OFF the UI thread (see
        /// LayoutRenderThreadSafetyTests for the crash that taught this codebase so), and a compiled
        /// cell is shared by every placement on every canvas — so a rewind-in-place cache here would be
        /// two threads writing one <see cref="SKPath"/>. Instead a miss builds a fresh path and swaps
        /// the whole record in with a single reference assignment, which is atomic: a reader sees
        /// either the complete old snapshot or the complete new one, and the path it is drawing from
        /// can never be rewritten under it. Two threads racing a miss both build, one wins, and the
        /// loser's path is simply garbage — wasted work on a zoom step, never corruption.</para>
        /// </summary>
        public ElidedGeometry? Elided;

        /// <summary>Sum of the primitives' own areas, of their (width + height), and their count —
        /// the three coefficients that turn the elision tier's grow amount into the fraction of
        /// <see cref="Bounds"/> the grown primitives cover, without touching
        /// <see cref="PrimitiveBounds"/> again. See <see cref="CoverageAt"/>.</summary>
        public double AreaSum, SemiPerimeterSum;

        public double BoundsArea;

        /// <summary>What fraction of <see cref="Bounds"/> this chunk's primitives cover once each is
        /// grown by <paramref name="grow"/> on every side — the elision tier's own geometry, measured
        /// rather than drawn. A grown rect of w x h has area (w+2g)(h+2g) = wh + 2g(w+h) + 4g^2, so the
        /// whole chunk's grown area is a quadratic in g over the three sums above.
        ///
        /// <para><b>One or more means the grown geometry has at least as much area as the box holding
        /// it</b>, which for a REGULAR field is exactly the condition for its union to BE that box: on a
        /// uniform pitch p with grown side s, coverage is (s/p)^2, so coverage >= 1 is s >= p is
        /// "neighbours touch". That equivalence is what
        /// <see cref="DefaultCoarseCoverageThreshold"/> trades on.</para></summary>
        public double CoverageAt(float grow) =>
            BoundsArea <= 0 ? 0
            : (AreaSum + 2.0 * grow * SemiPerimeterSum + 4.0 * (double)grow * grow * PrimitiveBounds.Length)
              / BoundsArea;
    }

    /// <summary>One immutable (grow amount, path) pair — see <see cref="CompiledChunk.Elided"/> for why
    /// this is a record swapped wholesale rather than two mutable fields.</summary>
    private sealed record ElidedGeometry(float Grow, SKPath Path);

    /// <summary>A layer's chunks split by the coarse tier at one grow amount (L2f) — the ones whose
    /// grown geometry already fills its own bounds, batched into <see cref="Collapsed"/> as one rect
    /// each, and <see cref="Rest"/>, which still draw individually through the elision/exact tiers.
    /// Immutable and swapped wholesale for exactly the reason <see cref="CompiledChunk.Elided"/> is —
    /// see that field for the off-UI-thread rendering this codebase does.</summary>
    private sealed record CoarseGeometry(float Grow, SKPath? Collapsed, SKRect CollapsedBounds,
                                         int CollapsedCount, CompiledChunk[] Rest);

    private sealed class CompiledLayerGeometry
    {
        public required LayerKey Key;
        public readonly List<CompiledChunk> Chunks = [];

        /// <summary>The coarse-tier split at the last grow amount asked for. Keyed on grow ALONE and
        /// that is sufficient, not a simplification: both gates the split applies — "is this chunk on
        /// the elision tier" and "does its grown geometry fill its bounds" — are functions of the
        /// per-primitive grow amount, because grow is itself
        /// <c>GeometryStrokeDevicePixels / 2</c> device pixels expressed in cell-local path space and
        /// therefore already carries the zoom AND the placement's own magnification.</summary>
        public CoarseGeometry? Coarse;
    }

    /// <summary>One resolved cell's compiled geometry — every one of its own shapes AND every one of
    /// its own instances' geometry (recursively, up to <see cref="CellHierarchy.MaxDepth"/>), flattened
    /// into THIS cell's own local path space. <see cref="BrokenPlaceholders"/> holds one dashed marker
    /// rect (in this cell's own local path space) per NESTED broken/cyclic/too-deep instance reference —
    /// deliberately without a text label (see the file header for why a top-level broken instance gets
    /// full labeled treatment and a nested one does not) and, deliberately, only ONE mark regardless of
    /// that nested instance's own array size (a documented corner-case simplification: a broken
    /// reference nested inside an array of an array is rare enough that one representative mark, not
    /// Rows*Cols of them, is an acceptable trade for not needing per-array-cell placeholder bookkeeping
    /// at arbitrary compile depth).</summary>
    private sealed class CompiledCellGeometry
    {
        public readonly List<CompiledLayerGeometry> Layers = [];
        public readonly List<SKRect> BrokenPlaceholders = [];
    }

    /// <summary>Compiled-cell cache, keyed by LayoutView REFERENCE — see the file header for why this
    /// piggybacks on <see cref="CellLayoutResolver"/>'s own cache lifecycle instead of maintaining a
    /// second, separately-invalidated cache.</summary>
    private static readonly ConditionalWeakTable<LayoutView, CompiledCellGeometry> _cellCompileCache = new();

    /// <summary>
    /// Evicts <paramref name="view"/>'s compiled geometry, if any — brief-L3b-hierarchy-navigation.md
    /// §2/R-L3b-1's other invalidation half. A push-in session's <see cref="LayoutView"/> is mutated IN
    /// PLACE across edits (the same reference persists), unlike a fresh disk-load, which produces a
    /// NEW reference the compile cache would simply never have seen before — so an in-place-edited
    /// session's stale compiled paths need this EXPLICIT eviction; a disk-reloaded reference self-heals
    /// via <see cref="ConditionalWeakTable{TKey,TValue}"/> just going stale/unreachable. Safe to call
    /// with a view that was never compiled (no-op).
    /// </summary>
    internal static void InvalidateCompiledGeometry(LayoutView view) => _cellCompileCache.Remove(view);

    /// <summary>Target primitives per compiled chunk (L2e stage 1). Small enough that a zoomed-in
    /// viewport lands on a handful of chunks rather than a slab of the cell; large enough that a
    /// full-extent view issues tens-to-hundreds of draw calls, not thousands — Skia's per-call overhead
    /// is a few microseconds, so ~100 chunks costs well under a millisecond against the ~100 ms the
    /// unchunked path spent. Chosen by that arithmetic, not tuned: the win here is asymptotic (culling
    /// that did not exist at all), so the exact constant is not a cliff.</summary>
    private const int TargetPrimitivesPerChunk = 256;

    /// <summary>Default on-screen size, in device pixels, at or under which a chunk drops its per-primitive
    /// hairline outline and draws as one solid grown fill (L2e stage 2). Set where the outline stops
    /// carrying information: the outline is <see cref="GeometryStrokeDevicePixels"/> wide, so at four
    /// device pixels of total extent a primitive is already almost entirely outline and its fill
    /// interior is sub-pixel — which is exactly why the two tiers look the same here and would NOT at,
    /// say, twelve.</summary>
    internal const double DefaultStrokeElisionDevicePixels = 4.0;

    /// <summary>Cap on the chunk grid's per-side division count — bounds the per-chunk bookkeeping for
    /// a pathologically dense cell at 1,024 chunks.</summary>
    private const int MaxChunkGridSide = 32;

    /// <summary>Default coverage (see <see cref="CompiledChunk.CoverageAt"/>) at or above which a chunk
    /// on the stroke-elision tier stops drawing its primitives at all and contributes ONE rect — its own
    /// bounds — to a per-layer batch (L2f).
    ///
    /// <para><b>One, because at one the substitution is not an approximation.</b> The elision tier draws
    /// each primitive grown by half the hairline stroke on every side; on a uniform pitch p that grown
    /// side is s and the coverage is exactly (s/p)^2, so coverage >= 1 means s >= p means adjacent grown
    /// primitives TOUCH and their union is the bounding box the coarse tier substitutes for them. Zoom
    /// out on a via field and this is what the elision tier is already painting — a solid block, arrived
    /// at by tessellating and merging tens of thousands of mutually overlapping rectangles per chunk.</para>
    ///
    /// <para><b>What it costs where the field is not uniform:</b> coverage is an AREA measure, so a chunk
    /// whose primitives clump — leaving an interior hole while still summing past its own bounds — fills
    /// that hole. The bound on the error is the chunk, never more, because a chunk's stored bounds are
    /// the union of the primitive boxes actually in it (an empty margin shrinks the bounds rather than
    /// being painted), and a chunk is sized to hold
    /// <see cref="TargetPrimitivesPerChunk"/> primitives — which, at the grow amounts this tier engages
    /// at, is a few device pixels across. That is the same trade <see cref="AddMinimalRect"/> and the
    /// elision tier itself already make one and two levels up.</para></summary>
    internal const double DefaultCoarseCoverageThreshold = 1.0;

    /// <summary>One primitive queued for chunk assignment during a compile — either one of this cell's
    /// OWN shapes (built through <see cref="BuildShapePath"/> in the second pass, so the path is
    /// constructed exactly once and <c>PathsConstructed</c> still counts real work) or one already-
    /// compiled chunk of a NESTED cell, to be folded in under <see cref="Matrix"/>. Bounds are computed
    /// in the first pass so the grid can be sized before any path exists — which is what keeps peak
    /// memory at one path per chunk instead of one per primitive.</summary>
    private readonly struct CompileItem
    {
        public required SKRect Bounds { get; init; }
        public LayoutShape? Shape { get; init; }
        public CompiledChunk? Child { get; init; }
        public SKMatrix Matrix { get; init; }
        public float MaxExtent { get; init; }
    }

    private static CompiledCellGeometry CompileCell(LayoutView subView, Technology? tech, string subBaseDir,
        HashSet<string> visiting, int depth, LayoutFrameCounters? counters)
    {
        if (_cellCompileCache.TryGetValue(subView, out var cached)) return cached;

        var compiled = new CompiledCellGeometry();
        double dbuToUm = 1.0 / Math.Max(1, subView.DbuPerMicron);
        var localPs = new PathSpace(0, 0, dbuToUm);

        // ── Pass 1 — collect every primitive's BOUNDS, per layer, without building any path ────────
        var items = new Dictionary<LayerKey, List<CompileItem>>();
        List<CompileItem> ItemsFor(LayerKey key)
        {
            if (items.TryGetValue(key, out var list)) return list;
            return items[key] = [];
        }

        // Own shapes. Bitmaps (not geometry, R-bmp-3) and Labels (text, not baked into a reusable
        // path aggregate — see the file header) are not represented in compiled instance geometry;
        // both are documented gaps in the L3a completion note, not silent omissions.
        foreach (var shape in subView.Shapes)
        {
            if (shape is BitmapShape or LabelShape) continue;
            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;
            var rect = NormalizedRect(localPs.X(bb.MinX), localPs.Y(bb.MinY), localPs.X(bb.MaxX), localPs.Y(bb.MaxY));
            ItemsFor(shape.Layer).Add(new CompileItem
            {
                Bounds = rect, Shape = shape, MaxExtent = Math.Max(rect.Width, rect.Height),
            });
        }

        // Own instances — recursively compiled and flattened into THIS cell's local space, so a
        // placement of THIS cell anywhere else needs no further per-frame recursion at all.
        foreach (var nested in subView.Instances)
        {
            var step = CellHierarchy.ResolveForWalk(nested, subBaseDir, visiting, depth);
            if (step.State != InstanceResolutionState.Resolved)
            {
                var (ox0, oy0) = LayoutInstanceTransform.ArrayCellOrigin(nested, 0, 0);
                long half = CellHierarchy.PlaceholderHalfExtentDbu;
                compiled.BrokenPlaceholders.Add(NormalizedRect(
                    localPs.X(ox0 - half), localPs.Y(oy0 - half), localPs.X(ox0 + half), localPs.Y(oy0 + half)));
                continue;
            }

            visiting.Add(step.ResolvedCellDir!);
            var child = CompileCell(step.SubView!, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!), visiting, depth + 1, counters);
            visiting.Remove(step.ResolvedCellDir!);

            var (a, b, c, d) = LayoutInstanceTransform.PathSpaceLinearCoefficients(nested);
            int rows = Math.Max(1, nested.Rows), cols = Math.Max(1, nested.Cols);
            for (int r = 0; r < rows; r++)
            for (int col = 0; col < cols; col++)
            {
                var (originX, originY) = LayoutInstanceTransform.ArrayCellOrigin(nested, r, col);
                var m = new SKMatrix
                {
                    ScaleX = (float)a, SkewX = (float)b, TransX = localPs.X(originX),
                    SkewY = (float)c, ScaleY = (float)d, TransY = localPs.Y(originY),
                    Persp2 = 1f,
                };
                float childScale = LinearScaleOf(m);
                foreach (var childLayer in child.Layers)
                foreach (var childChunk in childLayer.Chunks)
                    ItemsFor(childLayer.Key).Add(new CompileItem
                    {
                        Bounds = m.MapRect(childChunk.Bounds),
                        Child = childChunk,
                        Matrix = m,
                        MaxExtent = childChunk.MaxExtent * childScale,
                    });

                foreach (var rect in child.BrokenPlaceholders)
                    compiled.BrokenPlaceholders.Add(m.MapRect(rect));
            }
        }

        // ── Pass 2 — size a grid per layer, then build each chunk's path once ──────────────────────
        foreach (var (key, list) in items)
        {
            var cl = new CompiledLayerGeometry { Key = key };
            compiled.Layers.Add(cl);
            BuildChunks(cl, list, localPs, counters);
        }

        _cellCompileCache.AddOrUpdate(subView, compiled);
        return compiled;
    }

    /// <summary>The uniform linear scale factor of a placement matrix. Every placement transform this
    /// renderer produces is a similarity (a multiple of 90 degrees, an optional mirror, and Mag), so
    /// <c>sqrt(|det|)</c> is exact rather than an approximation — which is what lets a nested chunk's
    /// own feature size be carried up into the parent cell as a single scalar.</summary>
    private static float LinearScaleOf(in SKMatrix m) =>
        (float)Math.Sqrt(Math.Abs((double)m.ScaleX * m.ScaleY - (double)m.SkewX * m.SkewY));

    /// <summary>Buckets <paramref name="list"/> into a square grid of <see cref="CompiledChunk"/>s and
    /// builds each one's aggregate path. A primitive is assigned by its bbox CENTER and then GROWS its
    /// chunk's bounds to contain itself, so a chunk's stored bounds are always a true superset of what
    /// it draws — culling against them can never drop visible geometry, which is what makes stage 1
    /// pixel-identical to the unchunked path it replaces.</summary>
    private static void BuildChunks(CompiledLayerGeometry cl, List<CompileItem> list, PathSpace localPs,
                                    LayoutFrameCounters? counters)
    {
        if (list.Count == 0) return;

        var layerBounds = list[0].Bounds;
        foreach (var it in list) layerBounds.Union(it.Bounds);

        int gridN = Math.Clamp(
            (int)Math.Ceiling(Math.Sqrt(list.Count / (double)TargetPrimitivesPerChunk)), 1, MaxChunkGridSide);

        double cw = Math.Max(layerBounds.Width  / gridN, 1e-12);
        double ch = Math.Max(layerBounds.Height / gridN, 1e-12);

        var buckets = new List<CompileItem>?[gridN * gridN];
        foreach (var it in list)
        {
            int gx = Math.Clamp((int)(((it.Bounds.Left + it.Bounds.Right) / 2 - layerBounds.Left) / cw), 0, gridN - 1);
            int gy = Math.Clamp((int)(((it.Bounds.Top + it.Bounds.Bottom) / 2 - layerBounds.Top) / ch), 0, gridN - 1);
            (buckets[gy * gridN + gx] ??= []).Add(it);
        }

        foreach (var bucket in buckets)
        {
            if (bucket is null) continue;
            var chunk = new CompiledChunk { Bounds = bucket[0].Bounds };
            var prims = new List<SKRect>(bucket.Count);

            foreach (var it in bucket)
            {
                chunk.Bounds.Union(it.Bounds);
                if (it.MaxExtent > chunk.MaxExtent) chunk.MaxExtent = it.MaxExtent;

                if (it.Shape is { } shape)
                {
                    using var path = BuildShapePath(shape, localPs, counters);
                    if (path is null || path.IsEmpty) continue;
                    chunk.Geometry.AddPath(path);
                    prims.Add(it.Bounds);
                }
                else if (it.Child is { } child)
                {
                    var m = it.Matrix;
                    chunk.Geometry.AddPath(child.Geometry, in m);
                    foreach (var pb in child.PrimitiveBounds) prims.Add(m.MapRect(pb));
                }
            }

            if (chunk.Geometry.IsEmpty) continue;
            chunk.PrimitiveBounds = prims.ToArray();
            double areaSum = 0, semiSum = 0;
            foreach (var pb in chunk.PrimitiveBounds)
            {
                double w = pb.Width, h = pb.Height;
                areaSum += w * h;
                semiSum += w + h;
            }
            chunk.AreaSum = areaSum;
            chunk.SemiPerimeterSum = semiSum;
            chunk.BoundsArea = (double)chunk.Bounds.Width * chunk.Bounds.Height;
            cl.Chunks.Add(chunk);
        }
    }

    /// <summary>Splits one compiled layer's chunks at <paramref name="grow"/> into the ones the coarse
    /// tier collapses (batched into a single path of their own bounds) and the ones that still draw
    /// individually. Both gates are functions of grow alone — see
    /// <see cref="CompiledLayerGeometry.Coarse"/> for why that is exact and not a simplification — so the
    /// result is cacheable for the whole of a pan gesture.</summary>
    private static CoarseGeometry BuildCoarse(CompiledLayerGeometry layer, float grow,
                                              double elisionThreshold, double coarseCoverage)
    {
        // grow is GeometryStrokeDevicePixels/2 device pixels in path space, so a chunk is on the
        // elision tier when its largest primitive is under elisionThreshold DEVICE pixels — i.e. under
        // that many multiples of (2 * grow / GeometryStrokeDevicePixels) in path space.
        double pathSpacePerDevicePixel = 2.0 * grow / GeometryStrokeDevicePixels;
        double elisionExtent = elisionThreshold * pathSpacePerDevicePixel;

        SKPath? collapsed = null;
        var collapsedBounds = SKRect.Empty;
        int collapsedCount = 0;
        List<CompiledChunk>? rest = null;

        foreach (var chunk in layer.Chunks)
        {
            if (chunk.MaxExtent < elisionExtent && chunk.PrimitiveBounds.Length > 0
                && chunk.CoverageAt(grow) >= coarseCoverage)
            {
                // Grown by the same half-stroke the elision tier grows its own rects by, so the
                // collapsed block ends exactly where the elided one would have.
                var b = new SKRect(chunk.Bounds.Left - grow, chunk.Bounds.Top - grow,
                                   chunk.Bounds.Right + grow, chunk.Bounds.Bottom + grow);
                (collapsed ??= new SKPath()).AddRect(b);
                if (collapsedCount == 0) collapsedBounds = b; else collapsedBounds.Union(b);
                collapsedCount++;
                continue;
            }
            (rest ??= []).Add(chunk);
        }

        return new CoarseGeometry(grow, collapsed, collapsedBounds, collapsedCount,
                                  rest is null ? [] : rest.ToArray());
    }

    /// <summary>Draws every candidate instance placement — R-L3a §4/§5/§8 (culling already applied by
    /// the caller's spatial-index query; LOD and the missing/broken placeholder are decided here).</summary>
    private static void DrawInstances(SKCanvas canvas, LayoutView view, Technology? tech,
        IReadOnlyList<LayoutSpatialEntry> candidates, IReadOnlyDictionary<int, LayoutInstance> dragOverrides,
        LayoutRenderOptions opts, PathSpace ps, double scaleUm, SKRect visiblePathRect,
        LayoutFrameCounters counters, HashSet<string> missingCellRefs)
    {
        string baseDir = opts.BaseDir ?? "";
        double lodThreshold = opts.LodPixelThreshold > 0 ? opts.LodPixelThreshold : DefaultLodPixelThreshold;
        double elisionThreshold = opts.StrokeElisionPixelThreshold != 0
            ? opts.StrokeElisionPixelThreshold : DefaultStrokeElisionDevicePixels;
        double coarseCoverage = opts.CoarseCoverageThreshold != 0
            ? opts.CoarseCoverageThreshold : DefaultCoarseCoverageThreshold;
        double devicePxPerDbu = scaleUm * ps.DbuToUm;
        var layerMap = tech?.Layers.ToDictionary(l => l.Key);

        foreach (var entry in candidates)
        {
            if (entry.Kind != SpatialEntryKind.Instance) continue;
            if (entry.Index < 0 || entry.Index >= view.Instances.Count) continue;
            // A live move-drag renders the translated preview clone in place of the stored instance —
            // the model itself is untouched until the drag commits (mirrors dragOverrides for shapes).
            var inst = dragOverrides.TryGetValue(entry.Index, out var ov) ? ov : view.Instances[entry.Index];

            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var step = CellHierarchy.ResolveForWalk(inst, baseDir, visiting, 0);

            if (step.State != InstanceResolutionState.Resolved)
            {
                DrawBrokenInstancePlaceholder(canvas, inst, step.State, ps, scaleUm, opts.Theme, counters);
                if (!string.IsNullOrEmpty(inst.CellRef)) missingCellRefs.Add(inst.CellRef);
                continue;
            }

            // pcell-parameter-handles.md: while a parameter grip is being dragged live, the instance
            // draws the REGENERATED artwork in place of its own resolved cell. The model and the
            // generated cell on disk are untouched until the drag commits — the same rule
            // dragOverrides already follows for a shape move, one level up.
            var subView = step.SubView!;
            if (opts.Overlay?.PCellHandlePreview is { } handlePreview && handlePreview.InstanceIndex == entry.Index)
                subView = handlePreview.GhostView;

            // Deliberately the STORED cell's bbox even when a preview is substituted: this drives the
            // LOD decision only ("is this too small to draw at all"), and a grip drag never changes a
            // cell's size by orders of magnitude mid-gesture.
            var overallBbox = CellHierarchy.InstanceBbox(inst, baseDir);
            if (overallBbox.IsEmpty) continue;
            double screenW = (overallBbox.MaxX - overallBbox.MinX) * devicePxPerDbu;
            double screenH = (overallBbox.MaxY - overallBbox.MinY) * devicePxPerDbu;
            if (Math.Max(screenW, screenH) < lodThreshold)
            {
                DrawMinimalInstanceMark(canvas, overallBbox, ps, scaleUm);
                counters.InstancesDrawn++;
                continue;
            }

            visiting.Add(step.ResolvedCellDir!);
            var compiled = CompileCell(subView, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!), visiting, 1, counters);
            visiting.Remove(step.ResolvedCellDir!);

            var (a, b, c, d) = LayoutInstanceTransform.PathSpaceLinearCoefficients(inst);
            int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
            float placementScale = (float)Math.Sqrt(Math.Abs(a * d - b * c));

            // Resolved once per candidate instance, reused across every placement (R-L3a-3's "N matrix
            // draws" — not N paint allocations). Magnification is baked into the stroke width HERE
            // (gate 3): the compiled Stroke path is unscaled cell-local geometry, so the on-screen
            // width after this instance's own Mag (part of the placement matrix) must be pre-divided
            // by Mag to still land on GeometryStrokeDevicePixels device pixels.
            var layerVisuals = new List<(CompiledLayerGeometry Layer, SKPaint FillPaint, SKPaint StrokePaint, SKPaint ElidedPaint)>();
            double strokeScale = scaleUm * Math.Max(Math.Abs(inst.Mag), 1e-9);
            foreach (var layer in compiled.Layers)
            {
                LayerDef def = layerMap is not null && layerMap.TryGetValue(layer.Key, out var found)
                    ? found : FallbackPalette.For(layer.Key);
                if (!def.Visible) continue;
                var color = new SKColor(def.Color.R, def.Color.G, def.Color.B);
                layerVisuals.Add((
                    layer,
                    // The instance's own magnification is folded in, exactly as it is for the stroke
                    // width just below: a stipple inside a 10x instance must stay the same size on
                    // screen as the same layer's stipple outside it, or one cell's metal reads as a
                    // different layer from another's.
                    LayerFillPaint.Create(def, tech?.FindFillPattern(def.FillPattern), color, strokeScale, counters),
                    new SKPaint
                    {
                        IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = DevicePixelsToPathSpace(strokeScale, GeometryStrokeDevicePixels),
                        Color = color.WithAlpha(255),
                    },
                    // The stroke-elision tier's paint — the STROKE's solid alpha, not the fill's. At the
                    // few-device-pixel sizes it engages at, the outline is essentially the whole visible
                    // shape and the fill interior is sub-pixel, so carrying the fill's own (often
                    // partial) opacity across would visibly dim a dense field that today reads solid.
                    new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(255) }));
            }

            using var brokenFillPaint = compiled.BrokenPlaceholders.Count > 0
                ? new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = opts.Theme.Warning.WithAlpha(40) } : null;
            using var brokenStrokePaint = compiled.BrokenPlaceholders.Count > 0
                ? new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels), Color = opts.Theme.Warning, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0) }
                : null;

            try
            {
                for (int r = 0; r < rows; r++)
                for (int col = 0; col < cols; col++)
                {
                    var (originX, originY) = LayoutInstanceTransform.ArrayCellOrigin(inst, r, col);
                    var m = new SKMatrix
                    {
                        ScaleX = (float)a, SkewX = (float)b, TransX = ps.X(originX),
                        SkewY = (float)c, ScaleY = (float)d, TransY = ps.Y(originY),
                        Persp2 = 1f,
                    };

                    // L2e stage 1 — the viewport, mapped back into this placement's own cell-local
                    // path space, is what each chunk's bounds are tested against. Every placement
                    // transform here is a similarity (90-degree multiples, an optional mirror, Mag), so
                    // the inverse-mapped rect is EXACT, not a conservative envelope. A matrix that
                    // cannot be inverted (a degenerate Mag of 0) draws nothing on screen anyway, so
                    // falling back to "no culling" there would be work for an invisible result — but it
                    // must not SKIP the placement either, since Mag is user-editable and a zero is a
                    // transient state during a text edit; an un-invertible matrix simply keeps every
                    // chunk, and Skia's own clip discards the result.
                    bool culls = m.TryInvert(out var inverse);
                    var localVisible = culls ? inverse.MapRect(visiblePathRect) : default;

                    canvas.Save();
                    canvas.Concat(in m);
                    float chunkGrow = DevicePixelsToPathSpace(strokeScale, GeometryStrokeDevicePixels) / 2f;
                    foreach (var (layer, fillPaint, strokePaint, elidedPaint) in layerVisuals)
                    {
                        // L2f — the coarse tier. Every chunk whose grown geometry already fills its own
                        // bounds (see DefaultCoarseCoverageThreshold) is drawn as ONE rect, and all of
                        // them together as ONE path per layer rather than one draw call per chunk: at
                        // the zoom levels this engages at a whole 500um cell is a hundred device pixels
                        // wide, so per-chunk culling has nothing left to save and per-chunk draw CALLS
                        // become the cost that is left. The split is cached on the layer, keyed by the
                        // same grow amount the elision tier's own per-chunk cache is keyed by, so a pan
                        // — where this matters — never rebuilds it and a zoom step rebuilds it once.
                        var coarse = layer.Coarse;
                        if (coarseCoverage > 0 && (coarse is null || coarse.Grow != chunkGrow))
                            layer.Coarse = coarse = BuildCoarse(layer, chunkGrow, elisionThreshold, coarseCoverage);

                        IReadOnlyList<CompiledChunk> drawIndividually = layer.Chunks;
                        if (coarseCoverage > 0 && coarse is not null)
                        {
                            drawIndividually = coarse.Rest;
                            if (coarse.Collapsed is not null
                                && (!culls || localVisible.IntersectsWith(coarse.CollapsedBounds)))
                            {
                                canvas.DrawPath(coarse.Collapsed, elidedPaint);
                                counters.DrawCalls++;
                            }
                        }

                        foreach (var chunk in drawIndividually)
                        {
                            if (culls && !localVisible.IntersectsWith(chunk.Bounds)) continue;

                            // L2e stage 2 — a chunk whose largest primitive lands under a few device
                            // pixels draws as ONE solid fill of its primitives' bounding rects, grown by
                            // half the stroke width it would otherwise have been given, instead of a
                            // fill pass plus a stroke pass over the real geometry. Stroking is where the
                            // time actually went: tessellating an outline for ~100k segments measured
                            // 82 ms against the fill's 20 ms on the 24,964-via MIM cap, for an outline
                            // drawn on a 2.1-pixel square. The grown bbox covers the same pixels the
                            // stroke would have, and at this size a primitive and its bbox are
                            // indistinguishable — the same equivalence AddMinimalRect already trades on.
                            if (chunk.MaxExtent * placementScale * scaleUm < elisionThreshold
                                && chunk.PrimitiveBounds.Length > 0)
                            {
                                float grow = DevicePixelsToPathSpace(strokeScale, GeometryStrokeDevicePixels) / 2f;
                                var elided = chunk.Elided;
                                if (elided is null || elided.Grow != grow)
                                {
                                    var built = new SKPath();
                                    foreach (var pb in chunk.PrimitiveBounds)
                                        built.AddRect(new SKRect(pb.Left - grow, pb.Top - grow, pb.Right + grow, pb.Bottom + grow));
                                    chunk.Elided = elided = new ElidedGeometry(grow, built);
                                }
                                canvas.DrawPath(elided.Path, elidedPaint);
                                counters.DrawCalls++;
                                continue;
                            }

                            canvas.DrawPath(chunk.Geometry, fillPaint);
                            canvas.DrawPath(chunk.Geometry, strokePaint);
                            counters.DrawCalls += 2;
                        }
                    }
                    if (brokenFillPaint is not null && brokenStrokePaint is not null)
                        foreach (var rect in compiled.BrokenPlaceholders)
                        {
                            canvas.DrawRect(rect, brokenFillPaint);
                            canvas.DrawRect(rect, brokenStrokePaint);
                            counters.DrawCalls += 2;
                        }
                    canvas.Restore();
                    counters.InstancesDrawn++;
                }
            }
            finally
            {
                foreach (var (_, fp, sp, ep) in layerVisuals) { fp.Dispose(); sp.Dispose(); ep.Dispose(); }
            }

            // brief-L5-followups-2.md §6 (R-L5g-13/14/15): a top-level resolved instance's pins are
            // drawn as a screen-space overlay, ABOVE its own geometry — never as layer geometry
            // (never touches `compiled`/`layerVisuals`, never contributes to any counter, never
            // reachable by any exporter, which walk `LayoutView.Shapes` and never see this at all).
            // Deliberately top-level only — a cell nested inside another instance's compiled aggregate
            // has no per-instance draw call left to hook this onto (the SAME scope narrowing
            // R-L3a-3's own "nested broken instance" placeholder already uses).
            //
            // The test is "does this cell HAVE pins", not "was it generated". Gating on PCellOrigin
            // was what made an IMPORTED cell's pins invisible: it has none, so the overlay was
            // skipped before it could ever look at the cell's own pin list.
            if (opts.ShowPCellPins && (subView.Pins.Count > 0 || subView.PCellOrigin is not null))
                DrawPCellPinOverlay(canvas, inst, subView, tech, ps, scaleUm, opts.Theme, rows, cols);
        }
    }

    /// <summary>Half-side of a pin marker, in DEVICE pixels — constant on screen at any zoom.</summary>
    private const double PinMarkerHalfDevicePixels = 3.0;

    /// <summary>Draws <paramref name="subView"/>'s pins (via <see cref="Layout.CellPins"/>) at every one of
    /// <paramref name="inst"/>'s array placements — a constant-pixel-size filled SQUARE at the pin
    /// position, and nothing else.
    ///
    /// <para><b>A square, not a circle (owner request, 2026-08-09):</b> it matches the schematic
    /// editor's own port marker (<c>SchematicRenderer</c>'s <c>PortBoxHalf</c> box), so a connection
    /// point reads the same way in both editors. It also keeps a pin visually distinct from an EM
    /// PORT, which draws an arrow-and-width-bar in world space rather than a screen-space glyph.</para>
    ///
    /// <para><b>No outward-direction tick, deliberately (owner report, 2026-08-09).</b> R-L5g-13
    /// originally added a short line from the dot along the pin's own
    /// <see cref="PCellPin.OutwardDirectionDeg"/> on the reasoning that "a bare dot cannot say which
    /// way a pin faces". In practice that line reads as an EM PORT direction indicator — a genuinely
    /// different concept that now has its own rendering — so the two were being confused on screen.
    /// A cell pin is a connection point; which way it faces is carried by
    /// <see cref="LayoutPin.OutwardDeg"/> in the model and consumed by connectivity, not by this
    /// overlay. <b>Do not re-add a line here.</b></para></summary>
    private static void DrawPCellPinOverlay(SKCanvas canvas, LayoutInstance inst, LayoutView subView, Technology? tech,
        PathSpace ps, double scaleUm, LayoutRenderTheme theme, int rows, int cols)
    {
        var pins = Layout.CellPins.Resolve(subView, tech);
        if (pins.Count == 0) return;

        float half = DevicePixelsToPathSpace(scaleUm, PinMarkerHalfDevicePixels);
        using var dotPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.PCellPin };

        for (int r = 0; r < rows; r++)
        for (int col = 0; col < cols; col++)
        foreach (var pin in pins)
        {
            var (wx, wy) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, col);
            float cx = ps.X(wx), cy = ps.Y(wy);
            canvas.DrawRect(cx - half, cy - half, half * 2, half * 2, dotPaint);
        }
    }

    /// <summary>R-L3a-1 — a missing/broken TOP-LEVEL instance renders a labelled dashed placeholder at
    /// its stored extent, array-expanded (each array cell is independently a placeholder — there is no
    /// real geometry to have compressed via the array in the first place), and remains fully selectable
    /// (the caller's spatial index already indexes it via <c>CellHierarchy.PlaceholderBbox</c>).</summary>
    private static void DrawBrokenInstancePlaceholder(SKCanvas canvas, LayoutInstance inst, InstanceResolutionState state,
        PathSpace ps, double scaleUm, LayoutRenderTheme theme, LayoutFrameCounters counters)
    {
        string label = state switch
        {
            InstanceResolutionState.NotFound       => "Not Found",
            InstanceResolutionState.PrimaryMissing => "No Layout",
            InstanceResolutionState.Cyclic         => "Cyclic Ref",
            InstanceResolutionState.DepthExceeded  => "Too Deep",
            _                                       => "Broken",
        };

        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels),
            Color = theme.Warning, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };
        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Warning.WithAlpha(40) };
        // LayoutTextOutline.ResolveTypeface (not SkiaFonts.PlexRegular directly) — the same seam
        // LayoutRenderer.DrawLabelText uses, so this text ALSO honors LayoutTextOutline.
        // TestOverrideTypeface (SkiaFonts.PlexRegular cannot load without a live Avalonia app host,
        // confirmed empirically in the L1-era label work — see src/Ui/CLAUDE.md).
        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(LabelFontStyle.Regular), Math.Max(1f, DevicePixelsToPathSpace(scaleUm, 11.0)));
        using var textPaint = new SKPaint { IsAntialias = true, Color = theme.Warning };

        long half = CellHierarchy.PlaceholderHalfExtentDbu;
        int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            var (ox, oy) = LayoutInstanceTransform.ArrayCellOrigin(inst, r, c);
            var rect = NormalizedRect(ps.X(ox - half), ps.Y(oy - half), ps.X(ox + half), ps.Y(oy + half));
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);
            counters.DrawCalls += 2;

            float textWidth = font.MeasureText(label);
            if (textWidth < rect.Width * 4) // only draw the label when it's not wildly larger than the box
                canvas.DrawText(label, rect.MidX - textWidth / 2f, rect.MidY, SKTextAlign.Left, font, textPaint);
            counters.InstancesDrawn++;
        }
    }

    /// <summary>R-L3a §4 — a placement (here, the whole instance including its array, since an
    /// out-of-view-individually array cell is by definition also below threshold) whose overall screen
    /// extent falls under the LOD threshold draws as ONE minimal mark instead of compiling/descending
    /// into the sub-cell at all. Deliberately a neutral, fixed marker color rather than any of the
    /// sub-cell's own layer colors — consulting those would require exactly the descent this exists to
    /// avoid.</summary>
    private static readonly SKColor InstanceLodMarkColor = new(148, 148, 148, 200);

    private static void DrawMinimalInstanceMark(SKCanvas canvas, Bbox overallBbox, PathSpace ps, double scaleUm)
    {
        var rect = NormalizedRect(ps.X(overallBbox.MinX), ps.Y(overallBbox.MinY), ps.X(overallBbox.MaxX), ps.Y(overallBbox.MaxY));
        float halfMin = (float)(0.5 * MinimalRectDevicePixelsForInstances / Math.Max(scaleUm, 1e-12));
        float cx = (rect.Left + rect.Right) / 2f, cy = (rect.Top + rect.Bottom) / 2f;
        float w = Math.Max(rect.Width, halfMin * 2f), h = Math.Max(rect.Height, halfMin * 2f);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = InstanceLodMarkColor };
        canvas.DrawRect(new SKRect(cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f), paint);
    }

    private const double MinimalRectDevicePixelsForInstances = 1.0;

    // ── Selection outline + Instance-place ghost (§5/§6) ────────────────────────────────────────

    /// <summary>Accent outline around each selected instance's overall (array-expanded) bbox —
    /// mirrors <see cref="DrawSelectionOutlines"/> for shapes, but a simple bbox rect rather than the
    /// shape's own outline path, since R-L3a-5 selects the instance as a unit, not its contents.
    ///
    /// <para>While a PCell parameter grip is being dragged the instance is drawing REGENERATED
    /// artwork (<see cref="LayoutOverlay.PCellHandlePreview"/>), so the outline is measured from that
    /// same preview rather than from the cell still on disk — otherwise the highlight keeps the
    /// pre-drag shape's size while the artwork inside it grows or shrinks, which reads as the
    /// selection having come loose from what is selected.</para></summary>
    private static void DrawInstanceSelectionOutlines(SKCanvas canvas, LayoutView view, IReadOnlyList<int> selected,
        IReadOnlyDictionary<int, LayoutInstance> dragOverrides, LayoutRenderOptions opts, LayoutRenderTheme theme,
        PathSpace ps, double scaleUm)
    {
        string baseDir = opts.BaseDir ?? "";
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, SelectionStrokeDevicePixels),
            Color = theme.Selection,
        };
        var handlePreview = opts.Overlay?.PCellHandlePreview;
        foreach (var idx in selected)
        {
            if (idx < 0 || idx >= view.Instances.Count) continue;
            var inst = dragOverrides.TryGetValue(idx, out var ov) ? ov : view.Instances[idx];
            var bbox = handlePreview is { } preview && preview.InstanceIndex == idx
                ? CellHierarchy.InstanceBboxOfView(preview.GhostView, inst, baseDir)
                : CellHierarchy.InstanceBbox(inst, baseDir);
            if (bbox.IsEmpty) continue;
            var rect = NormalizedRect(ps.X(bbox.MinX), ps.Y(bbox.MinY), ps.X(bbox.MaxX), ps.Y(bbox.MaxY));
            canvas.DrawRect(rect, paint);
        }
    }

    /// <summary>The Instance-place tool's live ghost (§6), widened by brief-L3a-followups.md
    /// §4/R-fix-5 for the project-tree drag-and-drop entry point: when <paramref name="pending"/>'s
    /// <c>CellRef</c> RESOLVES, this draws the sub-cell's REAL compiled geometry (reusing
    /// <see cref="CompileCell"/> — the exact same per-layer aggregate paths a committed instance
    /// referencing the same cell already compiles, so this is not new per-frame cost for the common
    /// case of dragging a cell that is also placed elsewhere) under the placement matrix, at reduced
    /// opacity with a dashed accent outline so it still reads as provisional. When it does NOT resolve,
    /// this falls back to the SAME labelled dashed placeholder a committed unresolved instance gets
    /// (<see cref="DrawBrokenInstancePlaceholder"/>) — "matching R-L3a-1," per the brief — so the ghost
    /// never shows a placement it can't actually make. The Instance TOOL's own ghost (armed via the
    /// cell-picker dialog) always hits this same method — the box-only behavior it originally had is
    /// gone, not preserved as a separate code path, since a resolved cell's real geometry is strictly
    /// more informative for both entry points.</summary>
    /// <summary>
    /// A pasted instance whose resolved geometry is too large to redraw every pointer move: a dashed
    /// accent box at its array-expanded extent, in the SAME visual language as the real ghost, so the
    /// user is still aiming at something with the right size and position.
    ///
    /// <para>The owner's own rule ("if the geometry is too complicated for live rendering, then just
    /// render a box, but keep the port rendering live") — the shape half of the paste ghost is
    /// untouched by this, so ports stay live regardless of how heavy the instance beside them is.</para>
    /// </summary>
    private static void DrawGhostInstanceBox(SKCanvas canvas, Bbox bb, LayoutRenderTheme theme,
        PathSpace ps, double scaleUm, LayoutFrameCounters counters)
    {
        if (bb.IsEmpty) return;

        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels),
            Color = theme.Selection, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };
        using var fill = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection.WithAlpha(40),
        };

        float x0 = ps.X(bb.MinX), x1 = ps.X(bb.MaxX);
        float y0 = ps.Y(bb.MaxY), y1 = ps.Y(bb.MinY);   // path space is Y-down
        var rect = SKRect.Create(x0, y0, x1 - x0, y1 - y0);
        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, stroke);
        counters.DrawCalls += 2;
    }

    private static void DrawPendingInstancePlacement(SKCanvas canvas, (LayoutInstance Instance, Bbox Bbox) pending,
        Technology? tech, string baseDir, LayoutRenderTheme theme, PathSpace ps, double scaleUm, LayoutFrameCounters counters)
    {
        if (pending.Bbox.IsEmpty) return;

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = CellHierarchy.ResolveForWalk(pending.Instance, baseDir, visiting, 0);

        if (step.State != InstanceResolutionState.Resolved)
        {
            DrawBrokenInstancePlaceholder(canvas, pending.Instance, step.State, ps, scaleUm, theme, counters);
            return;
        }

        visiting.Add(step.ResolvedCellDir!);
        var compiled = CompileCell(step.SubView!, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!), visiting, 1, counters);
        visiting.Remove(step.ResolvedCellDir!);

        var (a, b, c, d) = LayoutInstanceTransform.PathSpaceLinearCoefficients(pending.Instance);
        int rows = Math.Max(1, pending.Instance.Rows), cols = Math.Max(1, pending.Instance.Cols);
        double strokeScale = scaleUm * Math.Max(Math.Abs(pending.Instance.Mag), 1e-9);

        using var ghostStroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(strokeScale, GeometryStrokeDevicePixels),
            Color = theme.Selection, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };
        using var ghostFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection.WithAlpha(60) };

        for (int r = 0; r < rows; r++)
        for (int col = 0; col < cols; col++)
        {
            var (originX, originY) = LayoutInstanceTransform.ArrayCellOrigin(pending.Instance, r, col);
            var m = new SKMatrix
            {
                ScaleX = (float)a, SkewX = (float)b, TransX = ps.X(originX),
                SkewY = (float)c, ScaleY = (float)d, TransY = ps.Y(originY),
                Persp2 = 1f,
            };
            canvas.Save();
            canvas.Concat(in m);
            foreach (var layer in compiled.Layers)
            foreach (var chunk in layer.Chunks)
            {
                canvas.DrawPath(chunk.Geometry, ghostFill);
                canvas.DrawPath(chunk.Geometry, ghostStroke);
            }
            canvas.Restore();
        }

        // The overall (array-expanded) extent outline too, so the full footprint reads clearly even
        // for a sparse sub-cell — mirrors the original box ghost's own outline, now drawn over the
        // real geometry rather than instead of it.
        var rect = NormalizedRect(ps.X(pending.Bbox.MinX), ps.Y(pending.Bbox.MinY), ps.X(pending.Bbox.MaxX), ps.Y(pending.Bbox.MaxY));
        canvas.DrawRect(rect, ghostStroke);
    }

    /// <summary>L5, R-L5-7: the palette→layout PCell drag's live ghost — draws the generator's real
    /// output (already resolved into a throwaway <see cref="LayoutView"/> by the VM, R0/no-array,
    /// translated to the current drag point) at reduced opacity with a dashed accent outline, the same
    /// visual language as <see cref="DrawPendingInstancePlacement"/>. There is no "unresolved" branch
    /// here — the VM never arms this ghost for a component that failed to resolve a generator (R-L5-8's
    /// droppability gate already refused the drag before this method is ever called).</summary>
    private static void DrawPendingPCellPlacement(SKCanvas canvas, (LayoutView GhostView, long X, long Y) pending,
        Technology? tech, LayoutRenderTheme theme, PathSpace ps, double scaleUm, LayoutFrameCounters counters)
    {
        var compiled = CompileCell(pending.GhostView, tech, "", [], 1, counters);

        using var ghostStroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels),
            Color = theme.Selection, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };
        using var ghostFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection.WithAlpha(60) };

        canvas.Save();
        canvas.Translate(ps.X(pending.X), ps.Y(pending.Y));
        foreach (var layer in compiled.Layers)
        foreach (var chunk in layer.Chunks)
        {
            canvas.DrawPath(chunk.Geometry, ghostFill);
            canvas.DrawPath(chunk.Geometry, ghostStroke);
        }
        canvas.Restore();
    }
}
