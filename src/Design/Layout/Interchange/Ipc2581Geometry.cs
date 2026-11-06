// The small geometry the IPC-2581 reader works in between a parsed element and a LayoutShape: pieces in
// local coordinates, already in DBU but still doubles, and the transform that places them. Rounding to
// the integer grid happens once, when a piece becomes a shape, so an inch file and a millimetre file of
// one board land on the same DBU however many transforms sit between a dictionary entry and the board.

namespace CircuitRF.Design.Layout.Interchange;

internal readonly record struct Ipc2581Point(double X, double Y);

/// <summary>A run of vertices with an optional bulge on the edge leaving each one, in
/// <see cref="LayoutEdge.Bulge"/>'s convention (positive is an increasing-angle sweep in a Y-up frame).</summary>
internal sealed record Ipc2581Contour(Ipc2581Point[] Points, double[]? Bulges)
{
    public bool HasArcs => Bulges is not null && Array.Exists(Bulges, b => b != 0);

    /// <summary>A whole circle as two half-circle arcs, because a single edge cannot close on itself.</summary>
    public static Ipc2581Contour Circle(double cx, double cy, double r) =>
        new([new(cx + r, cy), new(cx - r, cy)], [1.0, 1.0]);

    public static Ipc2581Contour Box(double x1, double y1, double x2, double y2) =>
        new([new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2)], null);
}

/// <summary>Something a feature draws. <see cref="Erase"/> is a fill the format calls VOID: it clears what the
/// same set already drew and draws nothing itself.</summary>
internal abstract record Ipc2581Piece(bool Erase);

internal sealed record Ipc2581Disc(double Cx, double Cy, double R, bool Erase = false) : Ipc2581Piece(Erase);

internal sealed record Ipc2581Region(Ipc2581Contour Outer, IReadOnlyList<Ipc2581Contour> Holes, bool Erase = false)
    : Ipc2581Piece(Erase);

internal sealed record Ipc2581Stroke(Ipc2581Contour Line, double Width, PathEndStyle End, bool Closed, bool Erase = false)
    : Ipc2581Piece(Erase);

/// <summary>A placement: a uniform scale, a rotation and possibly one reflection, then a translation.</summary>
internal readonly record struct Ipc2581Transform(double A, double B, double C, double D, double Tx, double Ty, double Scale, bool Mirrored)
{
    public static readonly Ipc2581Transform Identity = new(1, 0, 0, 1, 0, 0, 1, false);

    /// <summary>
    /// A local placement in the format's stated order: the origin moves to the offset, the shape rotates
    /// counter-clockwise about it, x is negated if mirrored, the result is scaled, and the location places it.
    /// The order matters only for a shape that is both rotated and mirrored and is not symmetric, so a wrong
    /// one looks right on most of a board.
    /// </summary>
    public static Ipc2581Transform Of(
        double xOffset, double yOffset, double rotationDegrees, bool mirror, double scale, double x, double y)
    {
        var (cos, sin) = CosSin(rotationDegrees);
        double a = scale * (mirror ? -cos : cos);
        double b = scale * (mirror ? sin : -sin);
        double c = scale * sin;
        double d = scale * cos;
        return new Ipc2581Transform(a, b, c, d,
            x - (a * xOffset + b * yOffset),
            y - (c * xOffset + d * yOffset),
            scale, mirror);
    }

    /// <summary>This placement applied after <paramref name="inner"/>.</summary>
    public Ipc2581Transform After(Ipc2581Transform inner) => new(
        A * inner.A + B * inner.C, A * inner.B + B * inner.D,
        C * inner.A + D * inner.C, C * inner.B + D * inner.D,
        A * inner.Tx + B * inner.Ty + Tx,
        C * inner.Tx + D * inner.Ty + Ty,
        Scale * inner.Scale,
        Mirrored != inner.Mirrored);

    public Ipc2581Point Apply(Ipc2581Point p) => new(A * p.X + B * p.Y + Tx, C * p.X + D * p.Y + Ty);

    /// <summary>Exact at the right angles, so a quarter-turned rectangle stays on the grid it started on.</summary>
    private static (double Cos, double Sin) CosSin(double degrees)
    {
        double turned = degrees % 360.0;
        if (turned < 0) turned += 360.0;
        return turned switch
        {
            0.0 => (1.0, 0.0),
            90.0 => (0.0, 1.0),
            180.0 => (-1.0, 0.0),
            270.0 => (0.0, -1.0),
            _ => (Math.Cos(turned * Math.PI / 180.0), Math.Sin(turned * Math.PI / 180.0)),
        };
    }
}

internal static class Ipc2581Shapes
{
    private static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>The bulge of an arc from a centre and a direction, or two arcs when the sweep is more than half a
    /// turn: an edge's bulge grows without limit as its sweep approaches a whole turn, and a start equal to its
    /// end is a whole circle that one edge cannot state.</summary>
    public static void AppendArc(
        List<Ipc2581Point> points, List<double> bulges, Ipc2581Point start, Ipc2581Point end, Ipc2581Point center, bool clockwise)
    {
        double r = Math.Sqrt(Math.Pow(start.X - center.X, 2) + Math.Pow(start.Y - center.Y, 2));
        if (r == 0)
        {
            bulges.Add(0);
            points.Add(end);
            return;
        }

        double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
        double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
        double sweep = a1 - a0;
        if (clockwise) { while (sweep >= 0) sweep -= 2 * Math.PI; }
        else { while (sweep <= 0) sweep += 2 * Math.PI; }

        if (Math.Abs(sweep) > Math.PI)
        {
            double mid = a0 + sweep / 2;
            bulges.Add(LayoutArc.ToBulge(sweep / 2));
            points.Add(new Ipc2581Point(center.X + r * Math.Cos(mid), center.Y + r * Math.Sin(mid)));
            bulges.Add(LayoutArc.ToBulge(sweep / 2));
            points.Add(end);
            return;
        }
        bulges.Add(LayoutArc.ToBulge(sweep));
        points.Add(end);
    }

    /// <summary>Every shape a piece places, for a piece that draws (see <see cref="Ipc2581Piece.Erase"/> for one that
    /// clears). A shape whose holes are not provably valid goes through the same check a loaded document does.</summary>
    public static IEnumerable<LayoutShape> Place(Ipc2581Piece piece, Ipc2581Transform xf, long flattenTolDbu)
    {
        switch (piece)
        {
            case Ipc2581Disc disc:
                var c = xf.Apply(new Ipc2581Point(disc.Cx, disc.Cy));
                return [new CircleShape { Cx = Round(c.X), Cy = Round(c.Y), R = Round(disc.R * xf.Scale) }];
            case Ipc2581Stroke stroke:
                return PlaceStroke(stroke, xf);
            case Ipc2581Region region:
                return PlaceRegion(region, xf, flattenTolDbu);
            default:
                return [];
        }
    }

    private static IEnumerable<LayoutShape> PlaceStroke(Ipc2581Stroke stroke, Ipc2581Transform xf)
    {
        var line = stroke.Line;
        long width = Round(stroke.Width * xf.Scale);
        var xy = Flat(line.Points, xf);
        int n = line.Points.Length;

        // A line that starts where it ends is a flash of its end shape. A path of two equal points has no
        // direction to build a square end from and no outline to hand a boolean.
        if (n == 2 && !line.HasArcs && xy[0] == xy[2] && xy[1] == xy[3])
        {
            long half = width / 2;
            return stroke.End switch
            {
                PathEndStyle.Round => [new CircleShape { Cx = xy[0], Cy = xy[1], R = half }],
                PathEndStyle.Square => [new RectShape { X1 = xy[0] - half, Y1 = xy[1] - half, X2 = xy[0] + half, Y2 = xy[1] + half }],
                _ => [],
            };
        }

        if (stroke.Closed)
        {
            Array.Resize(ref xy, xy.Length + 2);
            xy[^2] = xy[0];
            xy[^1] = xy[1];
        }
        int edges = stroke.Closed ? n : n - 1;
        return [new PathShape { Xy = xy, Edges = Edges(line, edges, xf), Width = width, End = stroke.End }];
    }

    private static IEnumerable<LayoutShape> PlaceRegion(Ipc2581Region region, Ipc2581Transform xf, long flattenTolDbu)
    {
        var outer = region.Outer;
        var xy = Flat(outer.Points, xf);
        List<long[]>? holes = null;
        foreach (var hole in region.Holes)
            (holes ??= []).Add(FlatRing(hole, xf, flattenTolDbu));

        if (outer.HasArcs)
        {
            var curve = new CurveShape { Xy = xy, Edges = Edges(outer, outer.Points.Length, xf), Holes = holes };
            return holes is null ? [curve] : LayoutClipper.EnsureValidHoles(curve);
        }

        if (holes is null && xy.Length == 8 && IsBox(xy))
            return [new RectShape
            {
                X1 = Math.Min(xy[0], xy[4]), Y1 = Math.Min(xy[1], xy[5]),
                X2 = Math.Max(xy[0], xy[4]), Y2 = Math.Max(xy[1], xy[5]),
            }];

        var polygon = new PolygonShape { Xy = xy, Holes = holes };
        return holes is null ? [polygon] : LayoutClipper.EnsureValidHoles(polygon);
    }

    private static bool IsBox(long[] xy) =>
        (xy[0] == xy[6] && xy[2] == xy[4] && xy[1] == xy[3] && xy[5] == xy[7]) ||
        (xy[1] == xy[7] && xy[3] == xy[5] && xy[0] == xy[2] && xy[4] == xy[6]);

    private static long[] Flat(Ipc2581Point[] points, Ipc2581Transform xf)
    {
        var xy = new long[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            var p = xf.Apply(points[i]);
            xy[2 * i] = Round(p.X);
            xy[2 * i + 1] = Round(p.Y);
        }
        return xy;
    }

    /// <summary>A hole's ring is always a flat vertex list (<see cref="CurveShape.Holes"/>), so an arc in a cutout
    /// flattens here at the import's tolerance.</summary>
    private static long[] FlatRing(Ipc2581Contour ring, Ipc2581Transform xf, long flattenTolDbu)
    {
        var xy = Flat(ring.Points, xf);
        if (!ring.HasArcs) return xy;
        var curve = new CurveShape { Xy = xy, Edges = Edges(ring, ring.Points.Length, xf) };
        return LayoutFlattener.Flatten(curve, flattenTolDbu)[0];
    }

    /// <summary>A reflection reverses an arc's direction; a rotation and a uniform scale leave its bulge alone.</summary>
    private static List<LayoutEdge>? Edges(Ipc2581Contour contour, int count, Ipc2581Transform xf)
    {
        if (!contour.HasArcs) return null;
        var edges = new List<LayoutEdge>(count);
        for (int i = 0; i < count; i++)
        {
            double bulge = contour.Bulges![i] * (xf.Mirrored ? -1 : 1);
            edges.Add(bulge == 0
                ? new LayoutEdge()
                : new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge });
        }
        return edges;
    }
}
