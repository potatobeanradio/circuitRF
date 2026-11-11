// The lateral shape of a slab that the stackup states no outline for — a dielectric, a body drawn on no
// layer. A layout placed in a .c3d, and a planar (MoM) setup previewed in 3D, cut every such slab to the board
// outline, else to ONE rectangle: the box round what the layout draws (Em3dLayoutSolids.Bounded). Nothing
// reaches past the artwork; a board that is bigger than its copper draws its outline.
//
// The preview's slabs are also clipped to the air box it is drawn in. Coordinates are integer nanometres for
// Clipper, so the result is deterministic.

using Clipper2Lib;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em3d;

public static class SlabLateralBound
{
    private const double NmPerMetre = 1e9;

    /// <summary>
    /// <paramref name="polys"/> clipped to the rectangle — a planar preview's slab never reaches past the air box
    /// it is drawn in, whatever padding the setup states. Outer rings only.
    /// </summary>
    public static IReadOnlyList<PlanarPolygon> ClipToRect(IReadOnlyList<PlanarPolygon> polys, double x0, double y0, double x1, double y1)
    {
        var subject = new Paths64(polys.Select(p => new Path64(p.Outer.Select(q =>
            new Point64((long)Math.Round(q.X * NmPerMetre), (long)Math.Round(q.Y * NmPerMetre))))));
        var rect = new Rect64((long)Math.Round(x0 * NmPerMetre), (long)Math.Round(y0 * NmPerMetre),
                              (long)Math.Round(x1 * NmPerMetre), (long)Math.Round(y1 * NmPerMetre));
        var clipped = Clipper.RectClip(rect, subject);
        return [.. clipped.Where(r => r.Count >= 3)
                          .Select(r => new PlanarPolygon([.. r.Select(q => new EmPoint(q.X / NmPerMetre, q.Y / NmPerMetre))]))];
    }

    /// <summary>
    /// PSV-2 — <paramref name="slab"/> less <paramref name="cuts"/>: a dielectric that exists everywhere except where its
    /// opening mask is drawn. An opening inside the slab becomes a hole ring and one crossing its edge a notch; the result
    /// is one honest solid per region rather than an air solid that relies on a backend's overlap order. Holes the slab
    /// already had (a board outline's cut-outs) are kept. Integer nanometres, as <see cref="ClipToRect"/>.
    /// </summary>
    public static IReadOnlyList<PlanarPolygon> Subtract(IReadOnlyList<PlanarPolygon> slab, IReadOnlyList<PlanarPolygon> cuts)
        => Boolean(ClipType.Difference, slab, cuts);

    /// <summary>PSV-4 — the region <paramref name="a"/> and <paramref name="b"/> share, as outer rings with holes.</summary>
    public static IReadOnlyList<PlanarPolygon> Intersect(IReadOnlyList<PlanarPolygon> a, IReadOnlyList<PlanarPolygon> b)
        => a.Count == 0 || b.Count == 0 ? [] : Boolean(ClipType.Intersection, a, b);

    /// <summary>
    /// PSV-4 — <paramref name="polys"/> grown outward by <paramref name="delta"/> metres with square corners: the footprint a
    /// conformal coat of that thickness takes round the metal. A mitred corner sets the wall at the coat's thickness on
    /// both faces, which is the coat a straight-walled process leaves; a rounded one would add facets the mesh pays for.
    /// </summary>
    public static IReadOnlyList<PlanarPolygon> Inflate(IReadOnlyList<PlanarPolygon> polys, double delta)
    {
        if (polys.Count == 0) return [];
        var normal = Clipper.Union(Paths(polys), FillRule.EvenOdd);
        var grown  = Clipper.InflatePaths(normal, delta * NmPerMetre, JoinType.Miter, EndType.Polygon, miterLimit: 2.0);
        return Boolean(ClipType.Union, Tree(grown), []);
    }

    private static IReadOnlyList<PlanarPolygon> Tree(Paths64 paths)
    {
        var result = new List<PlanarPolygon>();
        foreach (var path in paths)
            if (path.Count >= 3) result.Add(new PlanarPolygon(Ring(path)));
        return result;
    }

    private static IReadOnlyList<PlanarPolygon> Boolean(ClipType op, IReadOnlyList<PlanarPolygon> a, IReadOnlyList<PlanarPolygon> b)
    {
        var tree = new PolyTree64();
        // A union of rings that came out of an offset is read non-zero, so a hole ring the offset kept stays a hole.
        Clipper.BooleanOp(op, Paths(a), Paths(b), tree, op == ClipType.Union ? FillRule.NonZero : FillRule.EvenOdd);
        var result = new List<PlanarPolygon>();
        void Walk(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var outer = node[i];
                var holes = new List<IReadOnlyList<EmPoint>>();
                for (int j = 0; j < outer.Count; j++)
                {
                    if (outer[j].Polygon is { Count: >= 3 } hole) holes.Add(Ring(hole));
                    Walk(outer[j]);
                }
                if (outer.Polygon is { Count: >= 3 } ring)
                    result.Add(new PlanarPolygon(Ring(ring), holes.Count > 0 ? holes : null));
            }
        }
        Walk(tree);
        return result;
    }

    /// <summary>PSV-2 — the area (m²) <paramref name="a"/> and <paramref name="b"/> share, each read even-odd.</summary>
    public static double IntersectionArea(IReadOnlyList<PlanarPolygon> a, IReadOnlyList<PlanarPolygon> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var both = Clipper.BooleanOp(ClipType.Intersection, Paths(a), Paths(b), FillRule.EvenOdd);
        return Math.Abs(both.Sum(p => Clipper.Area(p))) / (NmPerMetre * NmPerMetre);
    }

    private static Paths64 Paths(IReadOnlyList<PlanarPolygon> polys)
    {
        var paths = new Paths64();
        foreach (var p in polys)
            foreach (var ring in new[] { p.Outer }.Concat(p.HoleRings))
                paths.Add(new Path64(ring.Select(q => new Point64((long)Math.Round(q.X * NmPerMetre), (long)Math.Round(q.Y * NmPerMetre)))));
        return paths;
    }

    private static IReadOnlyList<EmPoint> Ring(Path64 path)
        => [.. path.Select(q => new EmPoint(q.X / NmPerMetre, q.Y / NmPerMetre))];
}
