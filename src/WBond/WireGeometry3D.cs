// The 3D predicate class (docs/design/wbond.md §8.1) — the ONE genuinely new piece of geometry WB-D
// adds. Everything else in that phase is an existing mechanism reused.
//
// ── Why a wire-to-wire clearance cannot be a 2D polygon spacing ─────────────────────────────────
//
// Two bond wires that cross in plan view may be a millimetre apart in z, and two that look far apart
// in plan may pass within a wire diameter of each other over a pad. The quantity a house states is
// the distance between the wires as PHYSICAL METAL: each wire is a chain of capsules (a cylinder of
// the wire's own radius with hemispherical ends), and the clearance is the minimum surface-to-surface
// distance between two such chains. There is no projection of that onto the layout plane that
// preserves it, which is why §8.1 calls the 3D predicate the only new code in WB-D.
//
// ── The closed form, and its three failure modes ────────────────────────────────────────────────
//
// The minimum distance between two 3D line segments is standard (Ericson, *Real-Time Collision
// Detection*, §5.1.9). Three things decide whether an implementation of it is right:
//
//   1. PARALLEL or near-parallel segments. The usual `denom = a*e - b*b` goes to zero and the
//      unclamped solution explodes. Guarded by falling back to s = 0 and solving for t, which is
//      correct for a parallel pair (any s gives the same distance).
//   2. ZERO-LENGTH segments. A degenerate point pair in a wire's polyline — legal data, and a
//      division by zero if unguarded. Both single- and double-degenerate cases are handled first.
//   3. CLAMPING. The unconstrained closest points must be clamped to [0,1] and then RE-SOLVED on the
//      other parameter, not clamped independently. Clamping both independently is the classic wrong
//      answer, and it is wrong by a bounded-but-real amount on exactly the crossing wires a DRC
//      exists to find — the case where the unconstrained solution lands off the end of one segment.
//
// This is gated against a brute-force sampled minimum rather than against itself: a 200x200 sample
// over both segments is trivially obviously correct and shares no algebra with the closed form, so
// agreement between them is evidence rather than a tautology.

namespace CircuitRF.WBond;

/// <summary>An axis-aligned 3D bounding box in nanometres. Doubles, because it is always compared
/// against a clearance computed in doubles and an integer box would need a rounding rule of its own.</summary>
public readonly record struct Bbox3(
    double MinX, double MinY, double MinZ,
    double MaxX, double MaxY, double MaxZ)
{
    public static readonly Bbox3 Empty = new(
        double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
        double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX;

    public Bbox3 Expand(double by) => new(
        MinX - by, MinY - by, MinZ - by,
        MaxX + by, MaxY + by, MaxZ + by);

    public Bbox3 Union(in Bbox3 o) => new(
        Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Min(MinZ, o.MinZ),
        Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY), Math.Max(MaxZ, o.MaxZ));

    public static Bbox3 Of(in Point3 a, in Point3 b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>True when the two boxes come within <paramref name="gap"/> of each other. The
    /// broad-phase test — cheap, conservative, and never rejects a pair that could be closer.</summary>
    public bool WithinOf(in Bbox3 o, double gap) =>
        MinX - gap <= o.MaxX && o.MinX - gap <= MaxX &&
        MinY - gap <= o.MaxY && o.MinY - gap <= MaxY &&
        MinZ - gap <= o.MaxZ && o.MinZ - gap <= MaxZ;

    /// <summary>
    /// A lower bound on the distance between any point of this box and any point of
    /// <paramref name="o"/>. Used to skip a segment pair whose boxes are already further apart than
    /// the best clearance found so far — the inner-loop half of the acceleration.
    /// </summary>
    public double SeparationFrom(in Bbox3 o)
    {
        double dx = Math.Max(0.0, Math.Max(MinX - o.MaxX, o.MinX - MaxX));
        double dy = Math.Max(0.0, Math.Max(MinY - o.MaxY, o.MinY - MaxY));
        double dz = Math.Max(0.0, Math.Max(MinZ - o.MaxZ, o.MinZ - MaxZ));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// Segment, capsule and wire distance in 3D. Framework-free, allocation-free on the hot path, and
/// stateless — every method is a pure function of its arguments.
/// </summary>
public static class WireGeometry3D
{
    /// <summary>
    /// Below this, a squared length is treated as zero and the segment as a point. Lengths are in
    /// nanometres, so this is a hundredth of a nanometre squared — far below any real wire feature
    /// and far above the rounding of a double at these magnitudes.
    /// </summary>
    private const double Epsilon = 1e-4;

    /// <summary>
    /// Minimum distance between two 3D line segments, and the parameters at which it occurs.
    /// See this file's header for the three cases that decide whether this is right.
    /// </summary>
    public static double SegmentDistance(
        in Point3 p1, in Point3 q1, in Point3 p2, in Point3 q2, out double s, out double t)
    {
        double d1x = q1.X - p1.X, d1y = q1.Y - p1.Y, d1z = q1.Z - p1.Z;
        double d2x = q2.X - p2.X, d2y = q2.Y - p2.Y, d2z = q2.Z - p2.Z;
        double rx  = p1.X - p2.X, ry  = p1.Y - p2.Y, rz  = p1.Z - p2.Z;

        double a = d1x * d1x + d1y * d1y + d1z * d1z;   // |d1|²
        double e = d2x * d2x + d2y * d2y + d2z * d2z;   // |d2|²
        double f = d2x * rx  + d2y * ry  + d2z * rz;

        // Case 2 — both degenerate: two points.
        if (a <= Epsilon && e <= Epsilon)
        {
            s = t = 0.0;
            return Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }

        if (a <= Epsilon)
        {
            // First segment is a point.
            s = 0.0;
            t = Clamp01(f / e);
        }
        else
        {
            double c = d1x * rx + d1y * ry + d1z * rz;

            if (e <= Epsilon)
            {
                // Second segment is a point.
                t = 0.0;
                s = Clamp01(-c / a);
            }
            else
            {
                double b = d1x * d2x + d1y * d2y + d1z * d2z;
                double denom = a * e - b * b;

                // Case 1 — parallel or near-parallel: denom is zero (or numerically indistinguishable
                // from it) and the unclamped solution is unbounded. Any s gives the same distance for
                // a truly parallel pair, so pick the start of segment one and solve for t.
                s = denom > Epsilon ? Clamp01((b * f - c * e) / denom) : 0.0;

                // Case 3 — clamp, then RE-SOLVE the other parameter. Clamping t independently here
                // is the classic wrong answer: it returns the distance to a point that is not the
                // closest point on segment one.
                t = (b * s + f) / e;
                if (t < 0.0)      { t = 0.0; s = Clamp01(-c / a); }
                else if (t > 1.0) { t = 1.0; s = Clamp01((b - c) / a); }
            }
        }

        double cx = (p1.X + d1x * s) - (p2.X + d2x * t);
        double cy = (p1.Y + d1y * s) - (p2.Y + d2y * t);
        double cz = (p1.Z + d1z * s) - (p2.Z + d2z * t);
        return Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    /// <inheritdoc cref="SegmentDistance(in Point3, in Point3, in Point3, in Point3, out double, out double)"/>
    public static double SegmentDistance(in Point3 p1, in Point3 q1, in Point3 p2, in Point3 q2) =>
        SegmentDistance(p1, q1, p2, q2, out _, out _);

    /// <summary>
    /// Surface-to-surface distance between two capsules — the centreline distance less both radii.
    ///
    /// <para><b>Two capsules that touch return exactly zero, and overlapping ones return a negative
    /// number.</b> Neither is clamped to zero: a negative clearance is the amount by which the metal
    /// interpenetrates, which is the number that tells a user how far a wire has to move. Clamping it
    /// would make "just touching" and "buried a diameter deep" report identically.</para>
    /// </summary>
    public static double CapsuleClearance(
        in Point3 p1, in Point3 q1, double radius1,
        in Point3 p2, in Point3 q2, double radius2) =>
        SegmentDistance(p1, q1, p2, q2) - radius1 - radius2;

    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;

    // ── Whole wires ─────────────────────────────────────────────────────────────

    /// <summary>The wire's polyline bounding box, NOT including its radius.</summary>
    public static Bbox3 BboxOf(Wire wire)
    {
        var box = Bbox3.Empty;
        foreach (var p in wire.Points)
            box = box.Union(new Bbox3(p.X, p.Y, p.Z, p.X, p.Y, p.Z));
        return box;
    }

    /// <summary>The wire's bounding box grown by its own radius — the extent of its actual metal.</summary>
    public static Bbox3 MetalBboxOf(Wire wire) => BboxOf(wire).Expand(wire.DiameterNm / 2.0);

    /// <summary>
    /// Minimum surface-to-surface clearance between two whole wires, in nanometres. Negative when
    /// their metal interpenetrates.
    ///
    /// <para><b>The segment sweep is bbox-pruned, and that is what makes it affordable.</b> A wire is
    /// 6-7 points, so a naive pair costs 25-36 segment-distance evaluations; at the owner's stated
    /// worst case of 600 wires that is ~5 million even before the broad phase. Skipping a segment
    /// pair whose boxes are already further apart than the best clearance found so far removes almost
    /// all of them on real geometry, because two wires that come close do so over one span each.</para>
    /// </summary>
    public static double Clearance(Wire a, Wire b)
    {
        double ra = a.DiameterNm / 2.0;
        double rb = b.DiameterNm / 2.0;
        double best = double.PositiveInfinity;

        var ap = a.Points;
        var bp = b.Points;
        if (ap.Count < 2 || bp.Count < 2) return double.PositiveInfinity;

        for (int i = 1; i < ap.Count; i++)
        {
            var boxA = Bbox3.Of(ap[i - 1], ap[i]);

            for (int j = 1; j < bp.Count; j++)
            {
                var boxB = Bbox3.Of(bp[j - 1], bp[j]);

                // The boxes are already this far apart at minimum, so the centrelines cannot be
                // closer; subtracting the radii gives a lower bound on the clearance.
                if (boxA.SeparationFrom(boxB) - ra - rb >= best) continue;

                double d = SegmentDistance(ap[i - 1], ap[i], bp[j - 1], bp[j]) - ra - rb;
                if (d < best) best = d;
            }
        }

        return best;
    }

    /// <summary>
    /// The two points at which the wires come closest, on their CENTRELINES. Used to place a
    /// violation's marker at the offending spot rather than over both wires end to end — a marker the
    /// size of two whole wires says "somewhere in here", which is exactly what §9A.1 rejects.
    /// </summary>
    public static double ClosestApproach(Wire a, Wire b, out Point3 pointOnA, out Point3 pointOnB)
    {
        pointOnA = a.Points.Count > 0 ? a.Points[0] : default;
        pointOnB = b.Points.Count > 0 ? b.Points[0] : default;

        var ap = a.Points;
        var bp = b.Points;
        if (ap.Count < 2 || bp.Count < 2) return double.PositiveInfinity;

        double best = double.PositiveInfinity;
        for (int i = 1; i < ap.Count; i++)
        {
            var boxA = Bbox3.Of(ap[i - 1], ap[i]);
            for (int j = 1; j < bp.Count; j++)
            {
                var boxB = Bbox3.Of(bp[j - 1], bp[j]);
                if (boxA.SeparationFrom(boxB) >= best) continue;

                double d = SegmentDistance(ap[i - 1], ap[i], bp[j - 1], bp[j], out double s, out double t);
                if (d >= best) continue;

                best = d;
                pointOnA = Lerp(ap[i - 1], ap[i], s);
                pointOnB = Lerp(bp[j - 1], bp[j], t);
            }
        }
        return best;
    }

    private static Point3 Lerp(in Point3 a, in Point3 b, double t) => new(
        (long)Math.Round(a.X + (b.X - a.X) * t),
        (long)Math.Round(a.Y + (b.Y - a.Y) * t),
        (long)Math.Round(a.Z + (b.Z - a.Z) * t));

    /// <summary>
    /// Minimum foot-to-foot distance in the LAYOUT PLANE between two wires — §8's "minimum wire
    /// pitch", which is a different quantity from <see cref="Clearance"/>.
    ///
    /// <para>Both feet of each wire are considered, so a reversed wire measures the same. z is
    /// deliberately excluded: pitch is a bond-pad spacing, and two pads at different heights on the
    /// same footprint are still the same pitch apart as far as the bonder's placement is concerned.</para>
    /// </summary>
    public static double FootPitch(Wire a, Wire b)
    {
        if (a.Points.Count < 2 || b.Points.Count < 2) return double.PositiveInfinity;

        double best = double.PositiveInfinity;
        foreach (var pa in new[] { a.Points[0], a.Points[^1] })
        foreach (var pb in new[] { b.Points[0], b.Points[^1] })
        {
            double dx = pa.X - pb.X, dy = pa.Y - pb.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// The largest turn angle, in degrees, between consecutive segments of the polyline — §8's
    /// "maximum wire angle change". Zero for a dead-straight wire and for a wire of fewer than three
    /// points, which has no turn to measure.
    /// </summary>
    public static double MaxAngleChangeDegrees(Wire wire)
    {
        var pts = wire.Points;
        if (pts.Count < 3) return 0.0;

        double worst = 0.0;
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            double ux = pts[i].X - pts[i - 1].X, uy = pts[i].Y - pts[i - 1].Y, uz = pts[i].Z - pts[i - 1].Z;
            double vx = pts[i + 1].X - pts[i].X, vy = pts[i + 1].Y - pts[i].Y, vz = pts[i + 1].Z - pts[i].Z;

            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            double vl = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (ul <= 0.0 || vl <= 0.0) continue;   // a repeated point turns through no angle

            double cos = (ux * vx + uy * vy + uz * vz) / (ul * vl);
            double deg = Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI;
            if (deg > worst) worst = deg;
        }
        return worst;
    }

    /// <summary>
    /// Minimum distance from a wire's METAL SURFACE to a vertical plane-bounded 2D segment lying at a
    /// stated height — the primitive behind wire-to-layer and wire-to-edge clearance.
    ///
    /// <para>The layout segment is treated as a zero-radius segment at <paramref name="zNm"/>, so the
    /// answer is the wire's surface to the artwork's own edge. Artwork has thickness, but a conductor
    /// is microns thick against a loop that clears it by tens of microns, and pretending otherwise
    /// would need a per-layer thickness the check has no way to attribute to a flattened polygon
    /// boundary.</para>
    /// </summary>
    public static double DistanceToPlanarSegment(
        Wire wire, long x0, long y0, long x1, long y1, long zNm)
    {
        var pts = wire.Points;
        if (pts.Count < 2) return double.PositiveInfinity;

        var a = new Point3(x0, y0, zNm);
        var b = new Point3(x1, y1, zNm);
        double r = wire.DiameterNm / 2.0;
        var segBox = Bbox3.Of(a, b);

        double best = double.PositiveInfinity;
        for (int i = 1; i < pts.Count; i++)
        {
            var box = Bbox3.Of(pts[i - 1], pts[i]);
            if (box.SeparationFrom(segBox) - r >= best) continue;

            double d = SegmentDistance(pts[i - 1], pts[i], a, b) - r;
            if (d < best) best = d;
        }
        return best;
    }
}
