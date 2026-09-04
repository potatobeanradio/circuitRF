// Render-tolerance vertex decimation — the LOD tier for geometry that is DENSER than the screen,
// as opposed to SMALLER than the screen (which LayoutRenderer's sub-pixel/merge tiers already
// handle). See the class comment below for the measurement this exists because of.

using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Drops vertices that land on the same device pixel as the one before them.
///
/// <para><b>The gap this fills.</b> Every existing LOD tier in <see cref="LayoutRenderer"/> keys off
/// how BIG a shape is on screen — a sub-pixel bbox becomes a minimal rect, a hairline-width path
/// becomes a widened fill, a crowded layer merges into one batched fill. None of them can see the
/// case that actually dominates an imported board: ONE shape, large on screen, carrying orders of
/// magnitude more vertices than the screen has pixels to show them on. An imported Gerber's drill
/// symbols and its composited copper pours are exactly that — arcs flattened at the file's own
/// micrometre tolerance, which is the right tolerance to STORE and 100x more than any zoom level
/// can draw.</para>
///
/// <para><b>Measured, on a real 6-layer board (3,284 shapes, 764,110 vertices, 20 layers, whole
/// board in view at 1600x1000):</b> 244 ms a frame. 99.4% of those vertices belong to 1,928
/// polygons, and Skia's cost — both the fill and the batched outline stroke — is essentially linear
/// in the edge count fed to it. Decimating at half a device pixel leaves 100,421 vertices and 45 ms;
/// at one device pixel, 64,057 and 32 ms. The error is bounded by the tolerance itself, so at half a
/// pixel the result is not "an approximation of the board" — it is the same rasterization.</para>
///
/// <para><b>Why the tolerance is bucketed to a power of two</b> (<see cref="ToleranceDbu"/>): a
/// decimated contour is cached (<see cref="LayoutPathCache"/>) and must be rebuilt whenever the
/// tolerance changes. Taken straight from the zoom, that is every frame of a zoom gesture — the
/// rebuild would cost more than the saving. Bucketed, a zoom moves through an entire octave before
/// anything is rebuilt, and the effective tolerance stays inside [half, one] x the requested pixel
/// budget, so the error bound is unchanged. This is the same trick <c>LayoutRenderer.ComputeOrigin</c>
/// already uses to keep the per-frame path-space anchor from moving on every pan.</para>
/// </summary>
internal static class LayoutRenderDetail
{
    /// <summary>Contours at or below this many vertices are never decimated. Below it the walk costs
    /// more than the vertices it could remove, and — the reason this is a hard floor rather than a
    /// tuning knob — it keeps every ordinary authored primitive (a rectangle, a five-point outline, a
    /// hand-drawn polygon) bit-for-bit what it was. The tier can then only ever engage on the machine-
    /// generated contours it was built for.</summary>
    internal const int MinVerticesToDecimate = 16;

    /// <summary>
    /// The decimation tolerance in DBU for a frame drawn at <paramref name="devicePxPerDbu"/>, or 0
    /// for "do not decimate". Bucketed DOWN to a power of two, so the answer is stable across a zoom
    /// octave and never exceeds the requested pixel budget.
    /// </summary>
    internal static long ToleranceDbu(double detailPixels, double devicePxPerDbu)
    {
        if (detailPixels <= 0 || devicePxPerDbu <= 0 || double.IsNaN(devicePxPerDbu)) return 0;

        double raw = detailPixels / devicePxPerDbu;
        if (!(raw > 1.0) || double.IsInfinity(raw)) return 0;   // finer than one DBU: nothing to drop

        int octave = (int)Math.Floor(Math.Log2(raw));
        if (octave < 0) return 0;
        if (octave > 62) octave = 62;
        return 1L << octave;
    }

    /// <summary>
    /// Returns <paramref name="xy"/> with every vertex that falls within <paramref name="tolDbu"/>
    /// (Chebyshev) of the last KEPT vertex removed, or <paramref name="xy"/> itself when nothing
    /// would be dropped.
    ///
    /// <para><b>The first and last vertices are always kept.</b> Not tidiness — correctness. A closed
    /// contour is stored as an implicitly-closed vertex list, so dropping the last vertex because it
    /// sits near the one before it moves the closing edge and reshapes the contour. That is not a
    /// theoretical worry: dropped this way, the round glyphs on an imported board's drill chart
    /// (every D, O, 0 and colon — the closed ones, and only those) vanished from the frame.</para>
    ///
    /// <para>A contour that decimates below <paramref name="minKeep"/> vertices is returned
    /// unchanged rather than emitted degenerate — a whole small ring collapsing to a line is the one
    /// way this tier could DELETE geometry instead of simplifying it.</para>
    /// </summary>
    internal static long[] Decimate(long[] xy, long tolDbu, int minKeep)
    {
        int n = xy.Length / 2;
        if (tolDbu <= 0 || n <= MinVerticesToDecimate) return xy;

        // First pass: count survivors, so the common "nothing to drop" case allocates nothing.
        int kept = CountKept(xy, n, tolDbu);
        if (kept == n) return xy;
        if (kept < minKeep) return xy;

        var outp = new long[kept * 2];
        int w = 0;
        long lx = xy[0], ly = xy[1];
        outp[w++] = lx; outp[w++] = ly;
        for (int i = 1; i < n - 1; i++)
        {
            long x = xy[2 * i], y = xy[2 * i + 1];
            if (Math.Abs(x - lx) < tolDbu && Math.Abs(y - ly) < tolDbu) continue;
            outp[w++] = x; outp[w++] = y;
            lx = x; ly = y;
        }
        outp[w++] = xy[2 * (n - 1)];
        outp[w] = xy[2 * (n - 1) + 1];
        return outp;
    }

    /// <summary>
    /// Whether this frame can afford to outline its geometry — ONE answer for the whole frame, never
    /// per shape.
    ///
    /// <para><b>Why this is not a per-shape decision, which is what it was first built as.</b> Owner,
    /// 2026-09-04: with a single layer showing, zooming in and out made different shapes gain their
    /// outline at different zoom levels, and the editor looked like it was malfunctioning. It was
    /// not — each shape's own vertex density crossed the threshold at its own zoom — but that is
    /// exactly the point. An outline is not a per-shape property the way a fill colour is; it is a
    /// VISUAL LANGUAGE the whole view speaks or does not. Deciding it shape by shape is
    /// indistinguishable from the renderer flaking out, however defensible each individual decision
    /// is. Vertex DECIMATION stays per shape — it is bounded by half a pixel and nobody can see it —
    /// but the outline is categorical and has to be uniform.</para>
    ///
    /// <para><b>Why the estimate deliberately ignores WHERE the viewport is.</b> The honest measure of
    /// the outline pass's cost is the vertex count actually on screen, and using it would make the
    /// answer change as the user pans across a dense region — trading per-shape popping for
    /// per-pan popping, which is the same complaint. So the estimate is a function of the visible
    /// LAYERS and the ZOOM only: how much geometry the visible layers hold, scaled by the fraction of
    /// the design the viewport covers. Panning cannot change it; only zooming or toggling a layer can.
    /// It is an estimate, and it is allowed to be: being slightly wrong sets a threshold slightly
    /// early or late, and neither outcome flickers.</para>
    ///
    /// <para>This is also what makes the owner's own rule fall out rather than needing to be written:
    /// with one or two layers showing, the visible total is small enough that outlines are on at every
    /// zoom — no special case for it anywhere.</para>
    /// </summary>
    /// <summary>
    /// How concentrated the geometry in a drawing is assumed to be, as a fraction of its bounding box
    /// — the correction that stops a zoomed-in frame being estimated as if the design were spread
    /// evenly over its own extent, which no real one is.
    ///
    /// <para>Measured on the imported board rather than picked: its copper occupies 2,795 mm² of a
    /// 32,500 mm² drawing, the rest being the two drill charts and the empty space between them, so
    /// the geometry sits in about a ninth of the extent. Without the correction a viewport at 4x —
    /// which covers a sixteenth of the extent but lands squarely ON the board and therefore sees
    /// nearly all of it — was estimated at 48,000 vertices when it was showing closer to 700,000, and
    /// the frame it turned outlines back on for cost 45 ms.</para>
    ///
    /// <para>It only ever makes a zoomed-in frame more conservative: at full extent the viewport
    /// covers the whole drawing, the fraction clamps to 1, and the estimate is the exact visible
    /// vertex count with no assumption in it at all. So the budget below means precisely "how much
    /// geometry may be outlined with the whole design in view", which is the number worth reasoning
    /// about.</para>
    /// </summary>
    private const double AssumedGeometryConcentration = 8.0;

    internal static bool CanAffordOutlines(
        LayoutView view, Technology? tech, LayoutViewport vp, long budget, string baseDir = "")
    {
        if (budget <= 0) return true;

        var extent = view.SpatialIndex.Extent;
        double designW = extent.IsEmpty ? 0 : (double)extent.MaxX - extent.MinX;
        double designH = extent.IsEmpty ? 0 : (double)extent.MaxY - extent.MinY;
        double designArea = designW * designH;

        double viewArea = (vp.Width / vp.Zoom) * (vp.Height / vp.Zoom);
        double onScreen = designArea > 0
            ? System.Math.Min(1.0, viewArea * AssumedGeometryConcentration / designArea)
            : 1.0;
        if (!(onScreen > 0)) return true;      // zoomed in past the point where anything is dense

        // The budget the census is allowed to reach before the answer is "no". Expressed this way
        // round so the walk below can stop the moment it is exceeded — the cost of asking is then
        // bounded by the budget itself rather than by the size of the document.
        double allowance = budget / onScreen;
        if (allowance >= long.MaxValue) return true;

        // THE SPAN, not a foreach over the list — the same rule LayoutRenderer.Draw states for the
        // candidate walk, and for the same reason. This runs on the render thread while the UI thread
        // owns the document, so enumerating the List directly throws "Collection was modified" the
        // first time an edit lands mid-frame. Taking the backing span fixes the length this frame
        // reads against; a slot vacated by RemoveAt reads back null and is skipped, which is a frame
        // of visual lag rather than an exception on a thread with nothing to catch it.
        // (Caught by LayoutRenderThreadSafetyTests, which drives exactly that race.)
        var shapes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(view.Shapes);
        var visible = VisibleLayers(tech);
        long total = 0;
        for (int i = 0; i < shapes.Length; i++)
        {
            if (shapes[i] is not { } shape) continue;
            if (visible is not null && !visible.Contains(shape.Layer)) continue;
            total += VertexCount(shape);
            if (total > allowance) return false;
        }

        // ── Placed cells count too, and leaving them out made the budget a lie exactly where it
        // mattered most ─────────────────────────────────────────────────────────────────────────
        //
        // A schematic-generated layout has NO top-level shapes at all — every piece of geometry in it
        // belongs to a placed cell. Counting only `view.Shapes` there yields zero, so the frame always
        // "affords" outlines, and the one design shape where that answer is most expensive is the one
        // where the question was never really asked. Measured: one placement of the imported board is
        // 226 ms a frame against 18 ms for the identical geometry drawn as top-level shapes.
        //
        // A cell's census is memoised per resolved LayoutView, so this is one dictionary lookup per
        // placement after the first frame — and it invalidates for free the same way the compiled
        // geometry beside it does, because a changed file produces a NEW LayoutView from the resolver.
        if (view.Instances.Count == 0) return true;
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in view.Instances)
        {
            long cells = (long)Math.Max(1, inst.Rows) * Math.Max(1, inst.Cols);
            total += cells * CellVertices(inst, baseDir, visible, visiting, 0);
            if (total > allowance) return false;
        }
        return true;
    }

    /// <summary>Vertices on visible layers inside one placement's cell, INCLUDING everything its own
    /// nested instances place, or 0 for a reference that does not resolve. Memoised per resolved
    /// <see cref="LayoutView"/> and per visible-layer set.</summary>
    private static long CellVertices(
        LayoutInstance inst, string baseDir, HashSet<LayerKey>? visible, HashSet<string> visiting, int depth)
    {
        if (depth > MaxCensusDepth) return 0;

        var step = CellHierarchy.ResolveForWalk(inst, baseDir, visiting, depth);
        if (step.State != InstanceResolutionState.Resolved || step.SubView is not { } sub) return 0;

        var census = CensusOf(sub);
        long own = 0;
        foreach (var (key, n) in census.PerLayer)
            if (visible is null || visible.Contains(key)) own += n;

        if (sub.Instances.Count == 0) return own;

        // Recursion carries the SAME cycle set the resolver uses, so a cell that (directly or through
        // a chain) places itself contributes its geometry once and then stops, exactly as the compile
        // walk beside it does — never a stack overflow on a file the user can author.
        string cellDir = step.ResolvedCellDir!;
        if (!visiting.Add(cellDir)) return own;
        try
        {
            string subBase = CellHierarchy.LayoutBaseDirOf(cellDir);
            foreach (var nested in sub.Instances)
            {
                long cells = (long)Math.Max(1, nested.Rows) * Math.Max(1, nested.Cols);
                own += cells * CellVertices(nested, subBase, visible, visiting, depth + 1);
            }
        }
        finally { visiting.Remove(cellDir); }
        return own;
    }

    private const int MaxCensusDepth = 16;

    private sealed class CellCensus
    {
        public required Dictionary<LayerKey, long> PerLayer { get; init; }
    }

    /// <summary>Keyed on the resolved <see cref="LayoutView"/> REFERENCE, exactly as
    /// <c>LayoutRenderer._cellCompileCache</c> is and for the same reason: <c>CellLayoutResolver</c>
    /// hands back a new instance when the file changes, so a stale entry simply becomes unreachable
    /// and there is no second invalidation path to keep in step. Cleared alongside the compiled
    /// geometry by <c>LayoutRenderer.InvalidateCompiledGeometry</c>.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LayoutView, CellCensus> _cellCensus = new();

    internal static void Invalidate(LayoutView view) => _cellCensus.Remove(view);

    private static CellCensus CensusOf(LayoutView sub)
    {
        if (_cellCensus.TryGetValue(sub, out var hit)) return hit;

        var perLayer = new Dictionary<LayerKey, long>();
        var shapes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sub.Shapes);
        for (int i = 0; i < shapes.Length; i++)
        {
            if (shapes[i] is not { } s || s is BitmapShape or LabelShape) continue;
            perLayer.TryGetValue(s.Layer, out var n);
            perLayer[s.Layer] = n + VertexCount(s);
        }

        var census = new CellCensus { PerLayer = perLayer };
        _cellCensus.AddOrUpdate(sub, census);
        return census;
    }

    /// <summary>Null means "no technology resolved, so nothing is hidden" — the same tolerant reading
    /// <c>LayoutRenderer.Draw</c> takes when a layer key has no <see cref="LayerDef"/>.</summary>
    private static HashSet<LayerKey>? VisibleLayers(Technology? tech)
    {
        if (tech is null) return null;
        var set = new HashSet<LayerKey>();
        foreach (var l in tech.Layers)
            if (l.Visible) set.Add(l.Key);
        return set;
    }

    internal static long VertexCount(LayoutShape shape) => shape switch
    {
        PolygonShape p => p.Xy.Length / 2 + RingVertices(p.Holes),
        CurveShape c   => c.Xy.Length / 2 + RingVertices(c.Holes),
        PathShape t    => t.Xy.Length / 2,
        _              => 4,
    };

    private static long RingVertices(List<long[]>? rings)
    {
        if (rings is not { Count: > 0 }) return 0;
        long n = 0;
        foreach (var r in rings) n += r.Length / 2;
        return n;
    }

    private static int CountKept(long[] xy, int n, long tolDbu)
    {
        int kept = 2;                       // first and last, always
        long lx = xy[0], ly = xy[1];
        for (int i = 1; i < n - 1; i++)
        {
            long x = xy[2 * i], y = xy[2 * i + 1];
            if (Math.Abs(x - lx) < tolDbu && Math.Abs(y - ly) < tolDbu) continue;
            kept++;
            lx = x; ly = y;
        }
        return kept;
    }

    /// <summary>Hole rings, decimated with the same tolerance as their outer contour. Returns the
    /// original list when no ring changed, so an unchanged shape shares its stored rings rather than
    /// copying them.</summary>
    internal static List<long[]>? DecimateRings(List<long[]>? rings, long tolDbu)
    {
        if (tolDbu <= 0 || rings is not { Count: > 0 }) return rings;

        List<long[]>? copy = null;
        for (int i = 0; i < rings.Count; i++)
        {
            var d = Decimate(rings[i], tolDbu, minKeep: 3);
            if (ReferenceEquals(d, rings[i])) continue;
            copy ??= new List<long[]>(rings);
            copy[i] = d;
        }
        return copy ?? rings;
    }
}
