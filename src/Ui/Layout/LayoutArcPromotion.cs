// Arc -> Cubic promotion (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md R-L1h-7). A circular
// arc scaled non-uniformly is an ellipse, which the edge-list model cannot represent (Line/Arc/Cubic
// only). Cubic Béziers ARE closed under affine transformation, so converting every Arc edge (and a
// Circle's implicit arc) to cubics BEFORE a non-uniform scale is exact, not approximate — the exact
// same "promote to the more general representation first" move as L1d's Polygon->Curve rule
// (R-L1d-3), reused here for the same underlying reason: the simpler representation cannot express
// the transformed result.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Layout;

public static class LayoutArcPromotion
{
    /// <summary>True when <paramref name="shape"/> has at least one Arc edge (Curve/Path) or is a
    /// Circle — the set of shapes a non-uniform scale cannot touch directly.</summary>
    public static bool HasArcRequiringPromotion(LayoutShape shape) => shape switch
    {
        CircleShape c    => c.R > 0,
        CurveShape curve => curve.Edges?.Any(e => e.Kind == EdgeKind.Arc) ?? false,
        PathShape path   => path.Edges?.Any(e => e.Kind == EdgeKind.Arc) ?? false,
        _                => false,
    };

    /// <summary>
    /// Promotes every Arc edge in <paramref name="shape"/> to one or more Cubic edges. A
    /// <see cref="CircleShape"/> becomes a <see cref="CurveShape"/> with 4 cubic quadrants (the
    /// standard circle-as-4-Béziers construction — accurate to a small fraction of a percent of
    /// radius, the same convention SVG/vector tools use for the same reason). A <see cref="CurveShape"/>/
    /// <see cref="PathShape"/> with Arc edges has those edges replaced in place (splitting into ≤90°
    /// cubic segments each — a full-sweep arc needs more than one cubic to stay accurate), growing the
    /// vertex/edge lists as needed; Line and existing Cubic edges pass through unchanged. A shape with
    /// no arcs is returned AS-IS (same instance — callers use reference equality to detect whether
    /// promotion actually happened, for the "report the conversion once per operation" Messages note).
    /// </summary>
    public static LayoutShape PromoteArcsToCubics(LayoutShape shape) => shape switch
    {
        CircleShape c when c.R > 0 => CircleToCurve(c),
        CurveShape curve when HasArcRequiringPromotion(curve) => PromoteEdgeListShape(curve, closed: true),
        PathShape path when HasArcRequiringPromotion(path) => PromoteEdgeListShape(path, closed: false),
        _ => shape,
    };

    /// <summary>Kappa: the standard cubic-control-point distance factor for a 90° circular arc segment
    /// of radius 1 — <c>4/3 * tan(22.5°)</c>. Four quadrants at this constant approximate a full circle
    /// to within roughly 0.03% of its radius.</summary>
    private const double QuadrantKappa = 0.5522847498307936;

    private static CurveShape CircleToCurve(CircleShape c)
    {
        long cx = c.Cx, cy = c.Cy, r = c.R;
        long k = (long)Math.Round(r * QuadrantKappa, MidpointRounding.AwayFromZero);

        long[] xy = [cx + r, cy, cx, cy + r, cx - r, cy, cx, cy - r];
        List<LayoutEdge> edges =
        [
            new() { Kind = EdgeKind.Cubic, C1X = cx + r, C1Y = cy + k, C2X = cx + k, C2Y = cy + r },
            new() { Kind = EdgeKind.Cubic, C1X = cx - k, C1Y = cy + r, C2X = cx - r, C2Y = cy + k },
            new() { Kind = EdgeKind.Cubic, C1X = cx - r, C1Y = cy - k, C2X = cx - k, C2Y = cy - r },
            new() { Kind = EdgeKind.Cubic, C1X = cx + k, C1Y = cy - r, C2X = cx + r, C2Y = cy - k },
        ];
        return new CurveShape { Layer = c.Layer, Net = c.Net, Xy = xy, Edges = edges, FlattenTolDbu = c.FlattenTolDbu, Holes = null };
    }

    private static LayoutShape PromoteEdgeListShape(LayoutShape shape, bool closed)
    {
        var xy = LayoutShapeEditing.XyOf(shape);
        var edges = LayoutShapeEditing.EdgesOf(shape) ?? [];
        int n = xy.Length / 2;
        int edgeCount = closed ? n : n - 1;

        var verts = new List<long> { xy[0], xy[1] };
        var newEdges = new List<LayoutEdge>();

        for (int i = 0; i < edgeCount; i++)
        {
            int j = closed ? (i + 1) % n : i + 1;
            long x0 = xy[2 * i], y0 = xy[2 * i + 1], x1 = xy[2 * j], y1 = xy[2 * j + 1];
            var edge = i < edges.Count ? edges[i] : null;

            if (edge?.Kind == EdgeKind.Arc)
            {
                var (segVerts, segEdges) = ArcToCubics(x0, y0, x1, y1, edge.Bulge);
                for (int s = 0; s < segEdges.Count; s++)
                {
                    newEdges.Add(segEdges[s]);
                    verts.Add(segVerts[s].X); verts.Add(segVerts[s].Y);
                }
            }
            else
            {
                newEdges.Add(edge?.Kind == EdgeKind.Cubic
                    ? new LayoutEdge { Kind = EdgeKind.Cubic, C1X = edge.C1X, C1Y = edge.C1Y, C2X = edge.C2X, C2Y = edge.C2Y }
                    : new LayoutEdge { Kind = EdgeKind.Line });
                verts.Add(x1); verts.Add(y1);
            }
        }

        // Implicit-closure convention (mirrors LayoutFlattener): the last edge of a closed shape wraps
        // back to vertex 0, so it duplicates verts[0..1] — strip it, never repeat the closing vertex.
        if (closed && verts.Count >= 4 && verts[0] == verts[^2] && verts[1] == verts[^1])
            verts.RemoveRange(verts.Count - 2, 2);

        var newXy = verts.ToArray();
        return shape switch
        {
            CurveShape curve => new CurveShape { Layer = curve.Layer, Net = curve.Net, Xy = newXy, Edges = newEdges, FlattenTolDbu = curve.FlattenTolDbu, Holes = curve.Holes },
            PathShape path   => new PathShape { Layer = path.Layer, Net = path.Net, Xy = newXy, Edges = newEdges, Width = path.Width, End = path.End, FlattenTolDbu = path.FlattenTolDbu },
            _ => shape,
        };
    }

    /// <summary>Splits one arc (chord (x0,y0)-(x1,y1), signed <paramref name="bulge"/>) into ≤90°
    /// cubic segments — the standard circular-arc-to-cubic-Bézier construction (control points at
    /// <c>P ± (4/3)tan(Δθ/4)·r·tangent</c>), which is exact in the limit and visually exact at ≤90°
    /// per segment. The final segment's endpoint is always the EXACT original (x1,y1) — never
    /// re-derived via trig — so the shape's next edge still starts from a bit-identical shared vertex.</summary>
    private static (List<(long X, long Y)> Vertices, List<LayoutEdge> Edges) ArcToCubics(
        long x0, long y0, long x1, long y1, double bulge)
    {
        if (bulge == 0)
            return ([(x1, y1)], [new LayoutEdge { Kind = EdgeKind.Line }]);

        var arc = LayoutArc.FromBulge(x0, y0, x1, y1, bulge);
        if (arc.R <= 0)
            return ([(x1, y1)], [new LayoutEdge { Kind = EdgeKind.Line }]);

        const double maxSweepPerSeg = Math.PI / 2.0;
        int segCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.Sweep) / maxSweepPerSeg));
        double step = arc.Sweep / segCount;

        var vertices = new List<(long, long)>(segCount);
        var edges = new List<LayoutEdge>(segCount);

        double prevAngle = arc.StartAngle;
        double prevX = x0, prevY = y0;

        for (int k = 1; k <= segCount; k++)
        {
            double angle = arc.StartAngle + step * k;
            double segX, segY;
            if (k == segCount) { segX = x1; segY = y1; }
            else
            {
                segX = arc.Cx + arc.R * Math.Cos(angle);
                segY = arc.Cy + arc.R * Math.Sin(angle);
            }

            double segSweep = angle - prevAngle;
            double kappa = (4.0 / 3.0) * Math.Tan(segSweep / 4.0);

            double c1x = prevX - kappa * arc.R * Math.Sin(prevAngle);
            double c1y = prevY + kappa * arc.R * Math.Cos(prevAngle);
            double c2x = segX + kappa * arc.R * Math.Sin(angle);
            double c2y = segY - kappa * arc.R * Math.Cos(angle);

            long rx = (long)Math.Round(segX, MidpointRounding.AwayFromZero);
            long ry = (long)Math.Round(segY, MidpointRounding.AwayFromZero);
            vertices.Add((rx, ry));
            edges.Add(new LayoutEdge
            {
                Kind = EdgeKind.Cubic,
                C1X = (long)Math.Round(c1x, MidpointRounding.AwayFromZero),
                C1Y = (long)Math.Round(c1y, MidpointRounding.AwayFromZero),
                C2X = (long)Math.Round(c2x, MidpointRounding.AwayFromZero),
                C2Y = (long)Math.Round(c2y, MidpointRounding.AwayFromZero),
            });

            prevAngle = angle; prevX = segX; prevY = segY;
        }

        // The final vertex must be the EXACT original endpoint, not the rounded trig result.
        vertices[^1] = (x1, y1);
        return (vertices, edges);
    }
}
