// Arcs, regions, and the polarity decision (brief-L4e §5-§6) — the last third of GerberReader.
//
// §6 is the most consequential paragraph in the brief. %LPC*% makes subsequent objects ERASE, and
// LayoutShape has no such concept: our model has polygons with holes and nothing else. Compositing is
// always CORRECT and is therefore tempting as a uniform rule — and it destroys shape identity, which
// is what the round-trip gate is defined on. So the decision is made per layer, from the file's own
// content, and reported (R-L4e-13).

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class GerberReader
{
    // ── Arcs (R-L4e-11) ───────────────────────────────────────────────────────

    /// <summary>Appends one D01 to a vertex/edge list. Arcs become BULGE EDGES, never polylines: L4c's
    /// writer emits arcs, the round trip depends on getting an arc back, and a flattened arc can never
    /// be re-exported as one.</summary>
    private void AppendSegment(List<long> xy, List<LayoutEdge> edges, long tx, long ty, long? rawI, long? rawJ)
    {
        long x0 = xy[^2], y0 = xy[^1];

        if (_mode == Interpolation.Linear)
        {
            if (x0 == tx && y0 == ty) return;   // a zero-length draw contributes nothing
            edges.Add(new LayoutEdge { Kind = EdgeKind.Line });
            xy.Add(tx); xy.Add(ty);
            return;
        }

        bool ccw = _mode == Interpolation.CounterClockwiseArc;
        long di = rawI is null ? 0 : Format.ToDbu(rawI.Value);
        long dj = rawJ is null ? 0 : Format.ToDbu(rawJ.Value);
        var (cx, cy) = ResolveArcCentre(x0, y0, tx, ty, di, dj, ccw);
        AppendArc(xy, edges, x0, y0, tx, ty, cx, cy, ccw);
        _arcs++;
    }

    /// <summary>
    /// R-L4e-11. Under G75 (multi-quadrant) I/J are SIGNED and the centre is unambiguous. Under G74
    /// (single-quadrant) they are unsigned MAGNITUDES and the centre is one of four candidates
    /// (±I, ±J) — the correct one is the candidate whose distance to BOTH endpoints agrees AND whose
    /// sweep stays within a single quadrant. Testing only the start point does not eliminate anything:
    /// all four candidates are exactly the same distance from the start.
    /// </summary>
    private (double Cx, double Cy) ResolveArcCentre(long x0, long y0, long x1, long y1, long di, long dj, bool ccw)
    {
        if (_multiQuadrant) return (x0 + (double)di, y0 + (double)dj);

        double ai = Math.Abs(di), aj = Math.Abs(dj);
        double bestScore = double.MaxValue;
        (double, double) best = (x0 + ai, y0 + aj);
        bool foundInQuadrant = false;

        foreach (double sx in new[] { 1.0, -1.0 })
        foreach (double sy in new[] { 1.0, -1.0 })
        {
            double cx = x0 + sx * ai, cy = y0 + sy * aj;
            double r0 = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
            double r1 = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
            double score = Math.Abs(r0 - r1);
            bool inQuadrant = Math.Abs(SweepOf(x0, y0, x1, y1, cx, cy, ccw)) <= Math.PI / 2.0 + 1e-6;

            // A candidate that stays inside one quadrant always beats one that does not, however good
            // its radius agreement — both constraints are part of the answer, not a tie-break.
            if (inQuadrant && !foundInQuadrant) { foundInQuadrant = true; bestScore = score; best = (cx, cy); continue; }
            if (inQuadrant == foundInQuadrant && score < bestScore) { bestScore = score; best = (cx, cy); }
        }
        return best;
    }

    private static double SweepOf(long x0, long y0, long x1, long y1, double cx, double cy, bool ccw)
    {
        double a0 = Math.Atan2(y0 - cy, x0 - cx);
        double a1 = Math.Atan2(y1 - cy, x1 - cx);
        if (x0 == x1 && y0 == y1) return ccw ? 2 * Math.PI : -2 * Math.PI;

        double sweep = a1 - a0;
        if (ccw) { while (sweep <= 0) sweep += 2 * Math.PI; }
        else { while (sweep >= 0) sweep -= 2 * Math.PI; }
        return sweep;
    }

    /// <summary>Splits the sweep into as many bulge edges as it takes. A bulge is tan(sweep/4) and so
    /// cannot express a full turn at all (tan(pi/2) is infinite) — a G75 full-circle arc, which is a
    /// perfectly ordinary way to paint a ring, must therefore become more than one edge.</summary>
    private static void AppendArc(List<long> xy, List<LayoutEdge> edges,
        long x0, long y0, long x1, long y1, double cx, double cy, bool ccw)
    {
        double sweep = SweepOf(x0, y0, x1, y1, cx, cy, ccw);
        double r = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
        if (r <= 0) { edges.Add(new LayoutEdge { Kind = EdgeKind.Line }); xy.Add(x1); xy.Add(y1); return; }

        int parts = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (1.5 * Math.PI)));
        double a0 = Math.Atan2(y0 - cy, x0 - cx);
        double part = sweep / parts;
        double bulge = LayoutArc.ToBulge(part);

        for (int k = 1; k <= parts; k++)
        {
            edges.Add(new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge });
            if (k == parts) { xy.Add(x1); xy.Add(y1); break; }   // land on the file's own integer endpoint
            double a = a0 + part * k;
            xy.Add((long)Math.Round(cx + r * Math.Cos(a), MidpointRounding.AwayFromZero));
            xy.Add((long)Math.Round(cy + r * Math.Sin(a), MidpointRounding.AwayFromZero));
        }
    }

    // ── Regions (R-L4e-12) ────────────────────────────────────────────────────

    private void BeginRegion()
    {
        FlushStroke();
        _regionMode = true;
        _contours.Clear();
        _contour = null;
    }

    private Contour NewContourAt(long x, long y)
    {
        var c = new Contour();
        c.Xy.Add(x); c.Xy.Add(y);
        return c;
    }

    private void CloseContour()
    {
        if (_contour is { Edges.Count: > 0 }) _contours.Add(_contour);
        _contour = null;
    }

    private void EndRegion()
    {
        CloseContour();
        _regionMode = false;
        if (_contours.Count == 0) return;

        var rings = new List<(long[] Xy, List<LayoutEdge>? Edges, Path64 Flat)>();
        foreach (var c in _contours)
        {
            var normalized = Normalize(c);
            if (normalized is null) { Count(_skipped, "degenerate region contour (fewer than three distinct vertices)"); continue; }
            var (xy, edges) = normalized.Value;
            rings.Add((xy, edges, FlattenClosed(xy, edges)));
        }
        _contours.Clear();
        if (rings.Count == 0) return;

        // R-L4e-12: outer contours are boundaries, inner contours cut holes. Nesting depth by
        // containment, parity deciding which is which, so an island inside a hole is an island again.
        int n = rings.Count;
        var depth = new int[n];
        var parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = -1;
            for (int j = 0; j < n; j++)
            {
                if (i == j || rings[i].Flat.Count == 0) continue;
                if (Clipper.PointInPolygon(rings[i].Flat[0], rings[j].Flat) == PointInPolygonResult.IsOutside) continue;
                depth[i]++;
            }
        }
        for (int i = 0; i < n; i++)
        {
            int bestDepth = -1;
            for (int j = 0; j < n; j++)
            {
                if (i == j || depth[j] != depth[i] - 1 || rings[i].Flat.Count == 0) continue;
                if (Clipper.PointInPolygon(rings[i].Flat[0], rings[j].Flat) == PointInPolygonResult.IsOutside) continue;
                if (depth[j] > bestDepth) { bestDepth = depth[j]; parent[i] = j; }
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (depth[i] % 2 != 0) continue;   // an odd depth is a hole; it is emitted by its parent

            List<long[]>? holes = null;
            for (int j = 0; j < n; j++)
                if (parent[j] == i)
                    (holes ??= []).Add(ToRing(rings[j].Flat));

            var (xy, edges, _) = rings[i];
            LayoutShape shape = edges is null
                ? new PolygonShape { Xy = xy, Holes = holes }
                : new CurveShape { Xy = xy, Edges = edges, Holes = holes };
            Emit(shape, null);
            _regions++;
        }
    }

    /// <summary>Drops the duplicate closing vertex a region contour always ends on — LayoutShape
    /// polygons are implicitly closed, and leaving it in produces a zero-length edge that survives into
    /// every downstream consumer and breaks byte-identity on re-export (R-L4e-12). Returns null for a
    /// degenerate contour, which the caller skips and counts.</summary>
    private static (long[] Xy, List<LayoutEdge>? Edges)? Normalize(Contour c)
    {
        var xy = new List<long>(c.Xy);
        var edges = new List<LayoutEdge>(c.Edges);

        if (xy.Count >= 4 && xy[0] == xy[^2] && xy[1] == xy[^1])
        {
            xy.RemoveRange(xy.Count - 2, 2);         // the closing vertex; its edge already wraps
        }
        else
        {
            edges.Add(new LayoutEdge { Kind = EdgeKind.Line });   // implicit closure
        }
        if (edges.Count > xy.Count / 2) edges.RemoveRange(xy.Count / 2, edges.Count - xy.Count / 2);

        if (DistinctVertexCount(xy) < 3) return null;
        bool anyArc = edges.Exists(e => e.Kind == EdgeKind.Arc);
        return ([.. xy], anyArc ? edges : null);
    }

    private static int DistinctVertexCount(List<long> xy)
    {
        var seen = new HashSet<(long, long)>();
        for (int i = 0; i + 1 < xy.Count; i += 2) seen.Add((xy[i], xy[i + 1]));
        return seen.Count;
    }

    private static Path64 FlattenClosed(long[] xy, List<LayoutEdge>? edges)
    {
        if (edges is null)
        {
            var plain = new Path64(xy.Length / 2);
            for (int i = 0; i + 1 < xy.Length; i += 2) plain.Add(new Point64(xy[i], xy[i + 1]));
            return plain;
        }
        var wrapped = new long[xy.Length + 2];
        Array.Copy(xy, wrapped, xy.Length);
        wrapped[^2] = xy[0]; wrapped[^1] = xy[1];
        var path = FlattenCentreline(wrapped, edges);
        if (path.Count > 1 && path[0] == path[^1]) path.RemoveAt(path.Count - 1);
        return path;
    }

    private static long[] ToRing(Path64 path)
    {
        var xy = new long[path.Count * 2];
        for (int i = 0; i < path.Count; i++) { xy[2 * i] = path[i].X; xy[2 * i + 1] = path[i].Y; }
        return xy;
    }

    // ── The polarity decision (R-L4e-13) ──────────────────────────────────────

    private GerberReadResult Build()
    {
        if (_refusal is not null) return new GerberReadResult { Refusal = _refusal };

        if (_objects.Count > 0 && !_formatDeclared)
            return new GerberReadResult
            {
                Refusal = "This Gerber file declares no %FS*% coordinate format, so its coordinates " +
                          "cannot be read at all. Nothing was imported.",
            };
        if (_objects.Count > 0 && _unit is null)
            return new GerberReadResult
            {
                Refusal = "This Gerber file declares no %MO*% unit (and no G70/G71), so its " +
                          "coordinates have no scale. Nothing was imported.",
            };

        var format = Format;
        bool anyClear = _objects.Exists(o => o.Clear);
        List<GerberImportedShape> shapes;
        string? compositeReason = null;
        IReadOnlyList<GerberImportedShape> compositedFlashes = [];

        if (!anyClear)
        {
            // R-L4e-13, first bullet: a layer that never paints a CLEAR object imports
            // primitive-for-primitive — a stroke stays a PathShape, a circle flash stays a CircleShape,
            // a region stays a PolygonShape. This is what makes the round trip exact, and it is what
            // keeps an imported design EDITABLE rather than one welded blob.
            shapes = [.. _objects.Select(o => new GerberImportedShape(o.Shape, o.Function, o.Component, o.Pin))];
            if (_sawClearCommand)
                _diagnostics.Add("This file sets %LPC*% but paints nothing while it is in effect, so the " +
                                 "layer kept its individual shapes rather than being composited.");
        }
        else
        {
            shapes = [.. Composite(out var copper).Select(s => new GerberImportedShape(s, null, null, null))];
            compositedFlashes = FlashesStillWhollyInCopper(copper);
            compositeReason =
                $"This layer paints {_objects.Count(o => o.Clear)} clear (%LPC*%) object(s), which " +
                "LayoutShape cannot represent directly, so the whole layer was composited through " +
                "Clipper in paint order. Its individual shape identities and per-object net names are " +
                "gone; the geometry is exact.";
            // Deliberately NOT also a diagnostic: it already leaves here as CompositeReason, and the
            // orchestrator prints both lists, so adding it twice printed it twice — once per composited
            // layer, which on a six-layer board is six duplicate paragraphs in the import log.
        }

        return new GerberReadResult
        {
            Shapes = shapes,
            Unit = format.Unit,
            IntegerDigits = format.IntegerDigits,
            DecimalDigits = format.DecimalDigits,
            Notation = format.Notation,
            CoordinatesExact = format.IsExact,
            WorstCaseRoundingErrorDbu = format.WorstCaseRoundingErrorDbu,
            FileAttributes = _fileAttributes,
            ImageName = _imageName,
            LayerName = _layerName,
            StrokeCount = _strokes,
            FlashCount = _flashes,
            RegionCount = _regions,
            ArcCount = _arcs,
            StepRepeatFactor = _stepRepeatFactor,
            Composited = anyClear,
            CompositeReason = compositeReason,
            CompositedFlashes = compositedFlashes,
            UnknownCommandCounts = _unknown,
            SkippedConstructCounts = _skipped,
            Diagnostics = _diagnostics,
        };
    }

    /// <summary>R-L4e-13, second bullet: union the dark objects and subtract the clear ones, IN PAINT
    /// ORDER. Consecutive same-polarity objects are batched into one boolean per polarity switch — the
    /// result is identical and a vector-filled layer does not turn into tens of thousands of Clipper
    /// calls.</summary>
    private IReadOnlyList<LayoutShape> Composite(out Paths64 copper)
    {
        var accumulated = new Paths64();
        int i = 0;
        while (i < _objects.Count)
        {
            bool clear = _objects[i].Clear;
            var run = new Paths64();
            while (i < _objects.Count && _objects[i].Clear == clear)
            {
                run.AddRange(LayoutClipper.ToClipperPaths(_objects[i].Shape, TolDbu));
                i++;
            }
            run = Clipper.Union(run, LayoutClipper.Rule);
            accumulated = clear
                ? Clipper.Difference(accumulated, run, LayoutClipper.Rule)
                : Clipper.Union(accumulated, run, LayoutClipper.Rule);
        }

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, accumulated, new Paths64(), tree, LayoutClipper.Rule);
        copper = accumulated;
        return LayoutClipper.FromClipperTree(tree, default, null);
    }

    // ── The pads compositing merged away, kept as pairing evidence ────────────

    /// <summary>
    /// THE CIRCULAR PADS THIS LAYER PAINTED, WHICH COMPOSITING UNIONED INTO THE COPPER AROUND THEM.
    ///
    /// <para>A drill hit and a copper flash at the same point are a via, and the pairing that rebuilds
    /// one needs a pad to measure. On a composited layer there is no pad left to find — a pour swallows
    /// every pad in it — so on any real board with a ground plane EVERY hole came back as an unpaired
    /// hole, which is not a labelling problem: <c>ViaShape</c> on a via-bound layer is what the planar
    /// extractor reads as a via, so the board simulated with no vias in it at all.</para>
    ///
    /// <para>The pads are not gone, though: compositing is the last thing this reader does, and until
    /// then every flash is still a separate painted object. That is what this returns. It is EVIDENCE,
    /// not artwork — the shapes in <see cref="GerberReadResult.Shapes"/> already contain this copper,
    /// and anything that pairs against one of these must remove it from there rather than adding it
    /// twice.</para>
    ///
    /// <para><b>Only pads that survived compositing WHOLE.</b> A dark flash a later clear object ate —
    /// an antipad on a plane layer is exactly this — is not a pad any more, and pairing a hole to it
    /// would put copper back where the artwork deliberately removed it. Tested at the centre and eight
    /// points around the rim rather than by a full boolean per flash, which is the same answer for any
    /// pad whose clearance is not a sliver and is affordable at one board's worth of flashes.</para>
    /// </summary>
    private List<GerberImportedShape> FlashesStillWhollyInCopper(Paths64 copper)
    {
        var result = new List<GerberImportedShape>();
        if (copper.Count == 0) return result;

        // Bounding box and orientation per path, once: the winding test below is asked up to nine
        // times per flash and the great majority of paths are nowhere near any given pad.
        var boxes = new (Path64 Path, long MinX, long MinY, long MaxX, long MaxY, int Wind)[copper.Count];
        for (int i = 0; i < copper.Count; i++)
        {
            var path = copper[i];
            long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
            foreach (var pt in path)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }
            boxes[i] = (path, minX, minY, maxX, maxY, Clipper.IsPositive(path) ? 1 : -1);
        }

        foreach (var obj in _objects)
        {
            if (obj.Clear || obj.Shape is not CircleShape c || c.R <= 0) continue;
            if (!InCopper(boxes, c.Cx, c.Cy)) continue;

            bool whole = true;
            for (int k = 0; k < 8 && whole; k++)
            {
                double a = k * Math.PI / 4;
                whole = InCopper(boxes,
                    c.Cx + (long)Math.Round(c.R * Math.Cos(a)),
                    c.Cy + (long)Math.Round(c.R * Math.Sin(a)));
            }
            if (!whole) continue;

            result.Add(new GerberImportedShape(c, obj.Function, obj.Component, obj.Pin));
        }
        return result;
    }

    /// <summary>The NonZero winding test the compositing itself uses: a union's outer contours are
    /// positive and its holes negative, so a point is in copper when the two do not cancel. A point on
    /// a contour counts as in — a pad rim that grazes the clearance around it is still a whole pad.</summary>
    private static bool InCopper(
        (Path64 Path, long MinX, long MinY, long MaxX, long MaxY, int Wind)[] boxes, long x, long y)
    {
        var pt = new Point64(x, y);
        int winding = 0;
        foreach (var (path, minX, minY, maxX, maxY, wind) in boxes)
        {
            if (x < minX || x > maxX || y < minY || y > maxY) continue;
            if (Clipper.PointInPolygon(pt, path) == PointInPolygonResult.IsOutside) continue;
            winding += wind;
        }
        return winding != 0;
    }
}
