namespace CircuitRF.WBond;

/// <summary>
/// Reports mutual coupling that the model does <b>not</b> capture, because it falls between two
/// separate wBond components (wbond.md §7, WB30 / WB30a; R-wbb-7).
///
/// <h3>Why this is load-bearing rather than advisory</h3>
/// <para>A wBond reduces only its own wires, so coupling to a <i>different</i> wBond is silently
/// zero. <c>CouplingDomain</c> — which would gather several instances into one matrix — is v2, so in
/// v1 <b>this audit is the entire safety mechanism</b>, and the only remedy it can offer is manual:
/// merge the wires into a single wBond. That is acceptable because the one-wBond-per-cell convention
/// means most designs never produce a second one, but it means the audit must not be treated as a
/// nicety.</para>
///
/// <h3>It reports; it never refuses</h3>
/// <para>Two wBonds that genuinely do not interact are a legitimate design — input-side and
/// output-side bonds two inches apart couple negligibly, and forcing them into one component would
/// make one N = 600 matrix where two N = 300 matrices are both faster and no less accurate. So the
/// audit informs and names the fix.</para>
///
/// <h3>The threshold is physical, not a round number</h3>
/// <para>Two parallel wires of length ℓ at height <i>h</i> over a ground plane, separated laterally
/// by <i>d</i>, have mutual <c>M ≈ (μ₀ℓ/4π)·ln(1 + (2h/d)²)</c> against a self inductance
/// <c>L ≈ (μ₀ℓ/2π)·ln(2h/a)</c>. So the coupling coefficient is</para>
/// <code>
/// k ≈ ln(1 + (2h/d)²) / (2·ln(2h/a))
/// </code>
/// <para>which is what this estimates — and reporting an estimated <b>k</b> is far more actionable
/// than reporting a distance, because it is the number that decides whether the omission matters.
/// The estimate is deliberately cheap (chord geometry, not a Grover fill): an exact cross-fill of two
/// 600-wire designs is ~26 M filament pairs and this runs on every solve.</para>
/// </summary>
public static class CouplingAudit
{
    /// <summary>Coupling coefficients above this are reported. 1 % is where it starts to matter.</summary>
    public const double DefaultThreshold = 0.01;

    /// <summary>One unmodelled coupling between two wBond instances.</summary>
    /// <param name="InstanceA">The first instance's name or path.</param>
    /// <param name="InstanceB">The second instance's name or path.</param>
    /// <param name="EstimatedK">Estimated worst-case coupling coefficient between any two of their wires.</param>
    /// <param name="ClosestApproachMetres">Lateral separation of the closest wire pair.</param>
    /// <param name="Message">What to tell the user, including the remedy.</param>
    public readonly record struct Finding(
        string InstanceA,
        string InstanceB,
        double EstimatedK,
        double ClosestApproachMetres,
        string Message);

    /// <summary>
    /// Audits every pair of the supplied wBond instances.
    /// </summary>
    /// <param name="instances">Named designs, already in a common coordinate frame.</param>
    /// <param name="threshold">Report pairs whose estimated coupling exceeds this. Default 1 %.</param>
    public static IReadOnlyList<Finding> Audit(
        IReadOnlyList<(string Name, WBondDesign Design)> instances,
        double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(instances);

        var findings = new List<Finding>();

        for (int a = 0; a < instances.Count; a++)
        {
            for (int b = a + 1; b < instances.Count; b++)
            {
                var finding = Compare(instances[a], instances[b], threshold);
                if (finding is not null) findings.Add(finding.Value);
            }
        }

        // Worst first — a user acting on one report should act on the one that matters most.
        findings.Sort((x, y) => y.EstimatedK.CompareTo(x.EstimatedK));
        return findings;
    }

    private static Finding? Compare(
        (string Name, WBondDesign Design) a,
        (string Name, WBondDesign Design) b,
        double threshold)
    {
        double worstK = 0.0;
        double closest = double.MaxValue;

        foreach (var wireA in a.Design.AllWires())
        {
            if (wireA.Points.Count < 2) continue;

            foreach (var wireB in b.Design.AllWires())
            {
                if (wireB.Points.Count < 2) continue;

                double d = LateralSeparation(wireA, wireB);
                double h = 0.5 * (MeanHeight(wireA) + MeanHeight(wireB));
                double radius = 0.5 * (wireA.RadiusMetres + wireB.RadiusMetres);

                closest = Math.Min(closest, d);

                double k = EstimateCoupling(d, h, radius);
                if (k > worstK) worstK = k;
            }
        }

        if (worstK < threshold) return null;

        string message =
            $"wBond '{a.Name}' and wBond '{b.Name}' each model their own wires only, and have wires " +
            $"within {closest * 1e3 / 25.4:F1} mil of each other — an estimated coupling coefficient of " +
            $"{worstK:P1}. Their mutual coupling is NOT modelled.\n" +
            "To capture it, move the wires into a single wBond component; separate components are " +
            "reduced independently and cannot see each other.";

        return new Finding(a.Name, b.Name, worstK, closest, message);
    }

    /// <summary>
    /// <c>k ≈ ln(1 + (2h/d)²) / (2·ln(2h/a))</c> — see the class remarks for where it comes from.
    /// Clamped to [0, 1]: the closed form is an approximation and loses meaning once d approaches a.
    /// </summary>
    public static double EstimateCoupling(double separationMetres, double heightMetres, double radiusMetres)
    {
        if (heightMetres <= 0.0 || radiusMetres <= 0.0) return 0.0;

        // Below one radius the two wires are the same conductor; the estimate says nothing there.
        double d = Math.Max(separationMetres, radiusMetres);

        double selfTerm = Math.Log(2.0 * heightMetres / radiusMetres);
        if (selfTerm <= 0.0) return 0.0;   // a wire lower than its own radius — not a wire over a plane

        double ratio = 2.0 * heightMetres / d;
        double k = Math.Log(1.0 + ratio * ratio) / (2.0 * selfTerm);

        return Math.Clamp(k, 0.0, 1.0);
    }

    /// <summary>Mean height of a wire's points above the ground plane, metres.</summary>
    private static double MeanHeight(Wire wire)
    {
        double sum = 0.0;
        foreach (var p in wire.Points) sum += WBondUnits.ToMetres(p.Z);
        return sum / wire.Points.Count;
    }

    /// <summary>
    /// Shortest distance between the two wires' <b>XY chords</b>, metres.
    ///
    /// <para>Chords rather than full polylines: the audit is an estimate whose whole job is to be
    /// cheap enough to run on every solve, and a wire's loop does not move its footprint much.</para>
    /// </summary>
    private static double LateralSeparation(Wire a, Wire b)
    {
        (double ax1, double ay1) = Xy(a.Points[0]);
        (double ax2, double ay2) = Xy(a.Points[^1]);
        (double bx1, double by1) = Xy(b.Points[0]);
        (double bx2, double by2) = Xy(b.Points[^1]);

        return SegmentDistance(ax1, ay1, ax2, ay2, bx1, by1, bx2, by2);
    }

    private static (double X, double Y) Xy(Point3 p) =>
        (WBondUnits.ToMetres(p.X), WBondUnits.ToMetres(p.Y));

    /// <summary>Shortest distance between two 2D segments.</summary>
    private static double SegmentDistance(
        double ax1, double ay1, double ax2, double ay2,
        double bx1, double by1, double bx2, double by2)
    {
        double best = Math.Min(
            Math.Min(PointToSegment(ax1, ay1, bx1, by1, bx2, by2),
                     PointToSegment(ax2, ay2, bx1, by1, bx2, by2)),
            Math.Min(PointToSegment(bx1, by1, ax1, ay1, ax2, ay2),
                     PointToSegment(bx2, by2, ax1, ay1, ax2, ay2)));

        // Crossing segments are at zero distance, which the endpoint tests above cannot detect.
        if (Intersects(ax1, ay1, ax2, ay2, bx1, by1, bx2, by2)) return 0.0;
        return best;
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

    private static bool Intersects(
        double ax1, double ay1, double ax2, double ay2,
        double bx1, double by1, double bx2, double by2)
    {
        static double Cross(double ox, double oy, double px, double py, double qx, double qy) =>
            (px - ox) * (qy - oy) - (py - oy) * (qx - ox);

        double d1 = Cross(bx1, by1, bx2, by2, ax1, ay1);
        double d2 = Cross(bx1, by1, bx2, by2, ax2, ay2);
        double d3 = Cross(ax1, ay1, ax2, ay2, bx1, by1);
        double d4 = Cross(ax1, ay1, ax2, ay2, bx2, by2);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
