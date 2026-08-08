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
    public static Projected Project(Wire wire, int pointIndex, SpanMode mode = SpanMode.Absolute)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 2) return new Projected(0.0, wire.Points.Count == 0 ? 0.0 : wire.Points[0].Z);

        var start = wire.Points[0];
        var end = wire.Points[^1];
        var p = wire.Points[pointIndex];

        double s = WireEdits.ChordParameter(start, end, p);
        if (mode == SpanMode.Normalised) return new Projected(s, p.Z);

        double dx = WBondUnits.ToMetres(end.X - start.X);
        double dy = WBondUnits.ToMetres(end.Y - start.Y);
        double chordNm = Math.Sqrt(dx * dx + dy * dy) * WBondUnits.NmPerMetre;

        return new Projected(s * chordNm, p.Z);
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
                                     double pointBias = 2.0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return HitTest(mesh, toleranceNm, pointBias,
            (wire, index) =>
            {
                var projected = ProfileProjection.Project(mesh.Wires[wire], index, mode);
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
