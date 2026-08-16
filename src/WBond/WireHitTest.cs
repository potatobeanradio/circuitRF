namespace CircuitRF.WBond;

/// <summary>
/// The profile view's projection: 3D wire points onto a (span, z) plane with <b>z always up</b>
/// (wbond.md §6.2).
///
/// <para>The horizontal axis is <b>normalised span along the XY chord</b>, which is what makes wire
/// angle and wire length stop being profile differences at all — a wire at 37° and a wire at 90°,
/// 60 mil and 140 mil long, plot on top of each other if they have the same loop shape. That is
/// exactly what a packaging engineer means by "the same loop".</para>
///
/// <para>An absolute-span mode is also offered, because a user comparing two wires of the same length
/// wants to see their true geometry rather than a normalisation that hides a difference.</para>
/// </summary>
public static class ProfileProjection
{
    /// <summary>How the profile view's horizontal axis is scaled.</summary>
    public enum SpanMode
    {
        /// <summary>True geometry: wires of different length terminate at different x.</summary>
        Absolute,

        /// <summary>0..1 for every wire, so shapes overlay directly.</summary>
        Normalised,
    }

    /// <summary>A projected point: horizontal position and height, both in nanometres unless normalised.</summary>
    public readonly record struct Projected(double Span, double Z);

    /// <summary>
    /// Projects one point of a wire.
    /// </summary>
    /// <param name="mode">
    /// <see cref="SpanMode.Normalised"/> returns span in 0..1; <see cref="SpanMode.Absolute"/> returns
    /// nanometres along the chord.
    /// </param>
    /// <param name="azimuthRadians">
    /// <b>The plane the view is looking at</b>, measured from +x — 0 for XZ, π/2 for YZ. Null is
    /// AUTO: each wire is projected onto its OWN chord, which is §6.2's parameterisation and the
    /// reason wire angle and wire length stop being profile differences at all.
    ///
    /// <para>A FIXED azimuth is the other thing a user sometimes wants, and the two are genuinely
    /// different pictures: auto answers "do these wires have the same loop shape", a fixed plane
    /// answers "what does this array look like from the south". Under a fixed azimuth a wire running
    /// perpendicular to the view is foreshortened to nothing, which is what looking down a wire
    /// actually looks like and is why auto is still the default.</para>
    /// </param>
    public static Projected Project(Wire wire, int pointIndex, SpanMode mode = SpanMode.Absolute,
                                    double? azimuthRadians = null)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 2) return new Projected(0.0, wire.Points.Count == 0 ? 0.0 : wire.Points[0].Z);

        var start = wire.Points[0];
        var end = wire.Points[^1];
        var p = wire.Points[pointIndex];

        if (azimuthRadians is { } azimuth) return ProjectOntoPlane(start, end, p, mode, azimuth);

        if (mode == SpanMode.Normalised)
            return new Projected(WireEdits.ChordParameter(start, end, p), p.Z);

        // ABSOLUTE span has a FIXED origin — the world's, not the wire's own input foot.
        //
        // Measuring from Points[0] pinned that point at span 0 permanently: it could not move in this
        // view whatever happened to it in the world, and any motion of it was rendered as motion of
        // everything ELSE. Both of the owner's 2026-08-16 reports are that one fact — an alt-drag
        // anchored on the output foot DREW the output foot moving (while the layout view drew the
        // truth, so the two views disagreed about the same gesture), and a plain drag of the start
        // point left it glued in place while the rest of the curve slid out from under the cursor.
        //
        // The overlay property §6.2 wants — wires of different angle and length lying on top of each
        // other — is what SpanMode.Normalised is for, and it still has it. Absolute is the mode whose
        // whole purpose is "true geometry", and a true picture cannot re-origin itself on the point
        // being dragged.
        var (ux, uy) = WireEdits.ChordDirectionXY(wire);
        return new Projected(p.X * ux + p.Y * uy, p.Z);
    }

    /// <summary>
    /// The fixed-plane projection.
    ///
    /// <para><b>Absolute is measured from the WORLD origin</b>, for the reason the auto branch above
    /// gives at length: a view that re-origins on the wire's own input foot cannot show that foot
    /// move. Normalised stays foot-relative by definition — 0 at the input foot, 1 at the output —
    /// which is what makes it the shape-comparison mode.</para>
    /// </summary>
    private static Projected ProjectOntoPlane(Point3 start, Point3 end, Point3 p, SpanMode mode,
                                              double azimuth)
    {
        double cos = Math.Cos(azimuth), sin = Math.Sin(azimuth);

        if (mode == SpanMode.Absolute) return new Projected(p.X * cos + p.Y * sin, p.Z);

        double span = (p.X - start.X) * cos + (p.Y - start.Y) * sin;
        double chord = (end.X - start.X) * cos + (end.Y - start.Y) * sin;

        // A wire seen end-on has no extent in this plane and therefore no normalised coordinate.
        // Zero, rather than a division that would blow up or a chord-based fallback that would
        // silently disagree with the curve drawn beside it.
        return new Projected(Math.Abs(chord) < 1.0 ? 0.0 : span / chord, p.Z);
    }

    /// <summary>
    /// The direction, in XY, that "horizontal" means in the profile view for a given wire — a unit
    /// vector. Under a fixed azimuth that is the view direction; under AUTO it is the wire's own
    /// chord.
    ///
    /// <para>One definition, because the same answer has to serve the projection above, the
    /// horizontal drag (<see cref="WireEdits.Translate"/>) and the arrow-key nudge. Two would let a
    /// point render in one place and move in another.</para>
    /// </summary>
    public static (double X, double Y) HorizontalDirection(Wire wire, double? azimuthRadians)
    {
        ArgumentNullException.ThrowIfNull(wire);

        return azimuthRadians is { } azimuth
            ? (Math.Cos(azimuth), Math.Sin(azimuth))
            : WireEdits.ChordDirectionXY(wire);
    }

    /// <summary>
    /// The default profile-view azimuth: the <b>mean chord azimuth</b> of the displayed wires.
    ///
    /// <para>Without it an array of wires running at 37° renders foreshortened, which reads as a
    /// shorter loop than it is. Averaging the directions makes the array present side-on, which is
    /// what a user drawing a profile expects to see.</para>
    ///
    /// <para>Directions are averaged as <b>vectors</b>, not as angles: averaging 350° and 10° as
    /// numbers gives 180°, pointing the view exactly backwards.</para>
    /// </summary>
    public static double MeanChordAzimuthRadians(IEnumerable<Wire> wires)
    {
        ArgumentNullException.ThrowIfNull(wires);

        double sx = 0.0, sy = 0.0;
        foreach (var wire in wires)
        {
            if (wire.Points.Count < 2) continue;
            double dx = wire.Points[^1].X - wire.Points[0].X;
            double dy = wire.Points[^1].Y - wire.Points[0].Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length == 0.0) continue;

            sx += dx / length;
            sy += dy / length;
        }

        return sx == 0.0 && sy == 0.0 ? 0.0 : Math.Atan2(sy, sx);
    }

    /// <summary>
    /// Whether an array's members share a span closely enough that an absolute axis is the better
    /// default — the rule §6.2 states for choosing between the two modes.
    /// </summary>
    public static SpanMode PreferredMode(IReadOnlyList<Wire> wires, double tolerance = 0.05)
    {
        ArgumentNullException.ThrowIfNull(wires);
        if (wires.Count < 2) return SpanMode.Absolute;

        double min = double.MaxValue, max = 0.0;
        foreach (var wire in wires)
        {
            double chord = wire.ChordLengthMetres();
            if (chord <= 0.0) continue;
            min = Math.Min(min, chord);
            max = Math.Max(max, chord);
        }

        if (min == double.MaxValue || max == 0.0) return SpanMode.Absolute;
        return (max - min) / max <= tolerance ? SpanMode.Absolute : SpanMode.Normalised;
    }
}

/// <summary>
/// Finds the wire point or segment under a cursor, in either view (§6.3).
///
/// <para>Framework-free on purpose: which thing a click selects is a rule that can be wrong, and it
/// is testable against coordinates rather than through a canvas (brief-wbond-wbc §0.2).</para>
/// </summary>
public static class WireHitTest
{
    /// <summary>What a hit test found.</summary>
    /// <param name="Wire">Index of the wire hit, or −1 for nothing.</param>
    /// <param name="Point">
    /// The point index. For a segment hit this is the segment's FIRST point, matching
    /// <see cref="SegmentRef"/>.
    /// </param>
    /// <param name="IsSegment">True when a segment was hit rather than a vertex.</param>
    /// <param name="DistanceNm">How far the cursor was from it.</param>
    public readonly record struct Hit(int Wire, int Point, bool IsSegment, double DistanceNm)
    {
        public bool Found => Wire >= 0;

        public static Hit None => new(-1, -1, false, double.MaxValue);
    }

    /// <summary>
    /// Hit-tests the layout view (X-Y).
    ///
    /// <para><b>Points win over segments within the same tolerance</b>, because a vertex is the
    /// smaller and more precise target — a user aiming at one is not aiming at the line through it.
    /// The <paramref name="pointBias"/> is what implements that preference.</para>
    /// </summary>
    public static Hit HitTestLayout(WireMesh mesh, long x, long y, double toleranceNm,
                                    double pointBias = 2.0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return HitTest(mesh, toleranceNm, pointBias,
            (wire, index) => (mesh.Wires[wire].Points[index].X, mesh.Wires[wire].Points[index].Y),
            x, y);
    }

    /// <summary>
    /// Hit-tests the profile view (span, z).
    /// </summary>
    public static Hit HitTestProfile(WireMesh mesh, double span, long z, double toleranceNm,
                                     ProfileProjection.SpanMode mode = ProfileProjection.SpanMode.Absolute,
                                     double pointBias = 2.0,
                                     double? azimuthRadians = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return HitTest(mesh, toleranceNm, pointBias,
            (wire, index) =>
            {
                var projected = ProfileProjection.Project(mesh.Wires[wire], index, mode, azimuthRadians);
                return (projected.Span, projected.Z);
            },
            span, z);
    }

    private static Hit HitTest(WireMesh mesh, double toleranceNm, double pointBias,
                               Func<int, int, (double A, double B)> project,
                               double cursorA, double cursorB)
    {
        var best = Hit.None;

        for (int w = 0; w < mesh.WireCount; w++)
        {
            var points = mesh.Wires[w].Points;

            for (int i = 0; i < points.Count; i++)
            {
                var (a, b) = project(w, i);
                double distance = Math.Sqrt((a - cursorA) * (a - cursorA) + (b - cursorB) * (b - cursorB));

                // Vertices are preferred within the same tolerance, so the comparison is made on a
                // biased distance while the REPORTED distance stays true.
                if (distance <= toleranceNm && distance / pointBias < Bias(best, pointBias))
                    best = new Hit(w, i, false, distance);
            }

            for (int i = 0; i + 1 < points.Count; i++)
            {
                var (a1, b1) = project(w, i);
                var (a2, b2) = project(w, i + 1);
                double distance = PointToSegment(cursorA, cursorB, a1, b1, a2, b2);

                if (distance <= toleranceNm && distance < Bias(best, pointBias))
                    best = new Hit(w, i, true, distance);
            }
        }

        return best;

        static double Bias(Hit hit, double pointBias) =>
            !hit.Found ? double.MaxValue
            : hit.IsSegment ? hit.DistanceNm
            : hit.DistanceNm / pointBias;
    }

    private static double PointToSegment(double px, double py, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double lengthSquared = dx * dx + dy * dy;

        double t = lengthSquared == 0.0 ? 0.0
                 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSquared, 0.0, 1.0);

        double cx = x1 + t * dx, cy = y1 + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }
}
