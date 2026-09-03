// Polygonal builders for the aperture shapes that cannot be a CircleShape or a RectShape
// (docs/sonnet-briefs/brief-L4e-gerber-import-reader.md R-L4e-7/R-L4e-8): obround and regular-polygon
// standard apertures, every aperture-macro primitive, and the hole rings any of them may carry.
//
// These are Clipper2 Paths64 because compositing an aperture INTERNALLY (a macro's exposure-0
// primitive erases within the aperture, before the flash is ever placed) is a boolean, and because a
// holed aperture is a boolean too. They are not used for a plain C or R flash, which R-L4e-9 requires
// to come back as the CircleShape / RectShape the writer emitted — a 64-sided polygon would render
// identically and quietly destroy the round trip.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

internal static class GerberPrimitives
{
    /// <summary>Chord tolerance for turning a circular boundary into a polygon: 0.1 micron at the
    /// default DbuPerMicron=1000. Everything that reaches this file is already a shape circuitRF
    /// cannot express with a real arc, so this is the accuracy floor for the polygonal stand-in, not a
    /// choice about how curves are stored generally.</summary>
    internal const long CircleTolDbu = 100;

    private const int MinCircleSegments = 12;
    private const int MaxCircleSegments = 256;

    internal static int CircleSegments(double radius)
    {
        if (radius <= CircleTolDbu) return MinCircleSegments;
        double theta = 2.0 * Math.Acos(1.0 - CircleTolDbu / radius);
        int n = (int)Math.Ceiling(2.0 * Math.PI / theta);
        return Math.Clamp(n, MinCircleSegments, MaxCircleSegments);
    }

    internal static Path64 Circle(double cx, double cy, double radius)
    {
        int n = CircleSegments(radius);
        var path = new Path64(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2.0 * Math.PI * i / n;
            path.Add(Point(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
        }
        return path;
    }

    internal static Path64 Rect(double cx, double cy, double w, double h)
    {
        double hw = w / 2.0, hh = h / 2.0;
        return
        [
            Point(cx - hw, cy - hh), Point(cx + hw, cy - hh),
            Point(cx + hw, cy + hh), Point(cx - hw, cy + hh),
        ];
    }

    /// <summary>An obround (stadium) pad: a rectangle capped by a semicircle on its shorter pair of
    /// sides. Degenerates to a circle when the two dimensions are equal, which is the correct reading
    /// of <c>O,dXd</c> and not a special case worth branching on.</summary>
    internal static Path64 Obround(double cx, double cy, double w, double h)
    {
        double r = Math.Min(w, h) / 2.0;
        if (r <= 0) return Rect(cx, cy, Math.Max(w, 1), Math.Max(h, 1));

        // Distance between the two cap centres, along the longer axis.
        bool horizontal = w >= h;
        double span = (horizontal ? w - h : h - w) / 2.0;
        double c1x = horizontal ? cx - span : cx, c1y = horizontal ? cy : cy - span;
        double c2x = horizontal ? cx + span : cx, c2y = horizontal ? cy : cy + span;

        int n = CircleSegments(r);
        if (n % 2 != 0) n++;                       // an even count puts a vertex exactly on each cap seam
        var path = new Path64(n + 2);

        // Right/top cap, then left/bottom cap — half a circle each, so the straight flanks fall out.
        double baseAngle = horizontal ? -Math.PI / 2.0 : 0.0;
        for (int i = 0; i <= n / 2; i++)
        {
            double a = baseAngle + Math.PI * i / (n / 2.0);
            path.Add(Point(c2x + r * Math.Cos(a), c2y + r * Math.Sin(a)));
        }
        for (int i = 0; i <= n / 2; i++)
        {
            double a = baseAngle + Math.PI + Math.PI * i / (n / 2.0);
            path.Add(Point(c1x + r * Math.Cos(a), c1y + r * Math.Sin(a)));
        }
        return path;
    }

    /// <summary>A regular polygon inscribed in <paramref name="diameter"/>, first vertex at
    /// <paramref name="rotationDeg"/> from the +X axis — the format's own definition for both the
    /// standard <c>P</c> aperture and macro primitive 5.</summary>
    internal static Path64 RegularPolygon(double cx, double cy, double diameter, int vertices, double rotationDeg)
    {
        vertices = Math.Clamp(vertices, 3, 12);
        double r = diameter / 2.0;
        double phi = rotationDeg * Math.PI / 180.0;
        var path = new Path64(vertices);
        for (int i = 0; i < vertices; i++)
        {
            double a = phi + 2.0 * Math.PI * i / vertices;
            path.Add(Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return path;
    }

    /// <summary>Macro primitives 2/20 (vector line): a rectangle of <paramref name="width"/> swept
    /// from one point to another, with butt ends (the format's own definition — no round caps).</summary>
    internal static Path64 VectorLine(double x1, double y1, double x2, double y2, double width)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return Rect(x1, y1, width, width);
        double ux = dx / len, uy = dy / len;
        double nx = -uy * width / 2.0, ny = ux * width / 2.0;
        return
        [
            Point(x1 + nx, y1 + ny), Point(x2 + nx, y2 + ny),
            Point(x2 - nx, y2 - ny), Point(x1 - nx, y1 - ny),
        ];
    }

    /// <summary>Macro primitive 7 (thermal): the annulus between two diameters, cut by a horizontal
    /// and a vertical gap of <paramref name="gap"/> — four disconnected spokes, which is why a thermal
    /// flash legitimately produces more than one shape.</summary>
    internal static Paths64 Thermal(double cx, double cy, double outerDia, double innerDia, double gap)
    {
        double ro = outerDia / 2.0, ri = innerDia / 2.0;
        var ring = Clipper.Difference(new Paths64 { Circle(cx, cy, ro) }, new Paths64 { Circle(cx, cy, ri) },
            LayoutClipper.Rule);
        if (gap <= 0) return ring;

        var cuts = new Paths64
        {
            Rect(cx, cy, outerDia * 2, gap),   // horizontal cut, deliberately over-long
            Rect(cx, cy, gap, outerDia * 2),   // vertical cut
        };
        return Clipper.Difference(ring, cuts, LayoutClipper.Rule);
    }

    /// <summary>Rotates about the MACRO ORIGIN (0,0), which is what every macro primitive's rotation
    /// modifier means — not about the primitive's own centre.</summary>
    internal static Paths64 Rotate(Paths64 paths, double degrees)
    {
        if (degrees == 0) return paths;
        double a = degrees * Math.PI / 180.0;
        double ca = Math.Cos(a), sa = Math.Sin(a);
        var result = new Paths64(paths.Count);
        foreach (var p in paths)
        {
            var q = new Path64(p.Count);
            foreach (var pt in p) q.Add(Point(pt.X * ca - pt.Y * sa, pt.X * sa + pt.Y * ca));
            result.Add(q);
        }
        return result;
    }

    internal static Point64 Point(double x, double y) =>
        new((long)Math.Round(x, MidpointRounding.AwayFromZero), (long)Math.Round(y, MidpointRounding.AwayFromZero));
}
