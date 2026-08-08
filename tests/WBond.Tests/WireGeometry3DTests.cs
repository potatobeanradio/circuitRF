using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// Gates for WB-D's 3D predicate class (brief-wbond-wbd §M3).
///
/// <para><b>The closed form is gated against an INDEPENDENT oracle, not against itself.</b> A
/// brute-force sampled minimum over both segments shares no algebra with the analytic solution — it
/// is a double loop over 201x201 parameter values — so agreement between them is evidence. A test
/// that compared the closed form against a second derivation of the same formula would pass whatever
/// sign error both copies shared.</para>
/// </summary>
public sealed class WireGeometry3DTests(ITestOutputHelper output)
{
    private const int OracleSamples = 200;

    /// <summary>
    /// Brute-force minimum distance between two segments: sample both parameters on a fine grid and
    /// take the smallest distance. Trivially obviously correct, and deliberately written without
    /// reference to the closed form.
    /// </summary>
    private static double OracleDistance(Point3 p1, Point3 q1, Point3 p2, Point3 q2)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i <= OracleSamples; i++)
        {
            double s = (double)i / OracleSamples;
            double ax = p1.X + (q1.X - p1.X) * s;
            double ay = p1.Y + (q1.Y - p1.Y) * s;
            double az = p1.Z + (q1.Z - p1.Z) * s;

            for (int j = 0; j <= OracleSamples; j++)
            {
                double t = (double)j / OracleSamples;
                double bx = p2.X + (q2.X - p2.X) * t;
                double by = p2.Y + (q2.Y - p2.Y) * t;
                double bz = p2.Z + (q2.Z - p2.Z) * t;

                double dx = ax - bx, dy = ay - by, dz = az - bz;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < best) best = d;
            }
        }
        return best;
    }

    /// <summary>
    /// The classic WRONG answer: solve unconstrained, then clamp BOTH parameters independently
    /// instead of clamping one and re-solving the other. Present only so a test can show the closed
    /// form is not doing this — see <see cref="ClampingIndependently_IsWrong_OnACrossingPair"/>.
    /// </summary>
    private static double NaiveIndependentClamp(Point3 p1, Point3 q1, Point3 p2, Point3 q2)
    {
        double d1x = q1.X - p1.X, d1y = q1.Y - p1.Y, d1z = q1.Z - p1.Z;
        double d2x = q2.X - p2.X, d2y = q2.Y - p2.Y, d2z = q2.Z - p2.Z;
        double rx = p1.X - p2.X, ry = p1.Y - p2.Y, rz = p1.Z - p2.Z;

        double a = d1x * d1x + d1y * d1y + d1z * d1z;
        double e = d2x * d2x + d2y * d2y + d2z * d2z;
        double b = d1x * d2x + d1y * d2y + d1z * d2z;
        double c = d1x * rx + d1y * ry + d1z * rz;
        double f = d2x * rx + d2y * ry + d2z * rz;

        double denom = a * e - b * b;
        double s = denom == 0 ? 0 : Math.Clamp((b * f - c * e) / denom, 0, 1);
        double t = e == 0 ? 0 : Math.Clamp((a * f - b * c) / denom, 0, 1);

        double cx = (p1.X + d1x * s) - (p2.X + d2x * t);
        double cy = (p1.Y + d1y * s) - (p2.Y + d2y * t);
        double cz = (p1.Z + d1z * s) - (p2.Z + d2z * t);
        return Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    // ── Gate: the closed form agrees with the oracle over a randomised corpus ────────────────────

    [Fact]
    public void SegmentDistance_MatchesBruteForceOracle_OverARandomisedCorpus()
    {
        // Deterministic seed: a randomised corpus that cannot be reproduced is a corpus nobody can
        // debug when it fails.
        var rng = new Random(20260807);

        // A wire radius is half a mil = 12,700 nm. The tolerance below is three orders of magnitude
        // under that, so an error this test lets through cannot change any clearance verdict.
        const double toleranceNm = 12.0;

        // Sampling the oracle at 1/200 of each segment leaves a chord error that grows with segment
        // length, so the corpus is built at a realistic bond-wire scale rather than an arbitrary one.
        const long scale = 200_000;   // 200 um

        int cases = 0;
        double worst = 0.0;

        for (int i = 0; i < 400; i++)
        {
            var p1 = Rand(rng, scale);
            var q1 = Rand(rng, scale);
            var p2 = Rand(rng, scale);
            var q2 = Rand(rng, scale);

            double closed = WireGeometry3D.SegmentDistance(p1, q1, p2, q2);
            double oracle = OracleDistance(p1, q1, p2, q2);

            // The oracle is an upper bound on the true minimum (it samples), so the closed form must
            // never exceed it, and must not be far below it either.
            Assert.True(closed <= oracle + toleranceNm,
                $"closed form {closed} exceeded the sampled minimum {oracle}");
            worst = Math.Max(worst, oracle - closed);
            cases++;
        }

        output.WriteLine($"random corpus: {cases} pairs, worst (oracle - closed) = {worst:0.###} nm");
        Assert.True(worst < 2_000.0, $"closed form is {worst} nm below the sampled minimum — too far");
    }

    [Theory]
    // Every degenerate shape the header calls out, each checked against the same oracle.
    [InlineData("parallel, offset",       0, 0, 0,  1000, 0, 0,   0, 500, 0,  1000, 500, 0)]
    [InlineData("parallel, collinear",    0, 0, 0,  1000, 0, 0,   2000, 0, 0, 3000, 0, 0)]
    [InlineData("parallel, overlapping",  0, 0, 0,  1000, 0, 0,   500, 0, 0,  1500, 0, 0)]
    [InlineData("coincident",             0, 0, 0,  1000, 0, 0,   0, 0, 0,    1000, 0, 0)]
    [InlineData("touching at a point",    0, 0, 0,  1000, 0, 0,   1000, 0, 0, 1000, 1000, 0)]
    [InlineData("crossing in plan, apart in z", 0, -500, 0, 0, 500, 0,  -500, 0, 300, 500, 0, 300)]
    [InlineData("zero length vs segment", 500, 500, 0, 500, 500, 0,  0, 0, 0, 1000, 0, 0)]
    [InlineData("both zero length",       100, 200, 300, 100, 200, 300,  400, 200, 300, 400, 200, 300)]
    [InlineData("skew",                   0, 0, 0, 1000, 0, 0,  500, -500, 700, 500, 500, 700)]
    public void SegmentDistance_MatchesOracle_ForEveryDegenerateCase(
        string label,
        long p1x, long p1y, long p1z, long q1x, long q1y, long q1z,
        long p2x, long p2y, long p2z, long q2x, long q2y, long q2z)
    {
        var p1 = new Point3(p1x, p1y, p1z);
        var q1 = new Point3(q1x, q1y, q1z);
        var p2 = new Point3(p2x, p2y, p2z);
        var q2 = new Point3(q2x, q2y, q2z);

        double closed = WireGeometry3D.SegmentDistance(p1, q1, p2, q2);
        double oracle = OracleDistance(p1, q1, p2, q2);

        Assert.False(double.IsNaN(closed), $"{label}: NaN");
        Assert.True(Math.Abs(closed - oracle) < 1.0,
            $"{label}: closed form {closed} vs sampled minimum {oracle}");
    }

    [Fact]
    public void ClampingIndependently_IsWrong_OnACrossingPair()
    {
        // A skew pair whose unconstrained closest point lands off the end of segment one (s = 2.5,
        // clamped to 1) while the other parameter stays INTERIOR. Re-solving t at the clamped s gives
        // t = 0.2; clamping t independently leaves it at its unconstrained 0.5, which is a point on
        // segment two that is not the closest one. The two segments must also not be perpendicular,
        // or b = 0 and the two answers coincide by accident.
        var p1 = new Point3(0, 0, 0);
        var q1 = new Point3(1000, 0, 0);
        var p2 = new Point3(2000, -1000, 100);
        var q2 = new Point3(3000, 1000, 100);

        double closed = WireGeometry3D.SegmentDistance(p1, q1, p2, q2);
        double oracle = OracleDistance(p1, q1, p2, q2);
        double naive  = NaiveIndependentClamp(p1, q1, p2, q2);

        Assert.True(Math.Abs(closed - oracle) < 1.0, $"closed {closed} vs oracle {oracle}");

        // If this ever stops differing, the fixture has stopped exercising the trap and the test is
        // no longer evidence of anything.
        Assert.True(Math.Abs(naive - oracle) > 10.0,
            $"the naive clamp agreed ({naive} vs {oracle}) — this fixture no longer exercises the trap");
        output.WriteLine($"closed {closed:0.##}  oracle {oracle:0.##}  naive-independent-clamp {naive:0.##}");
    }

    // ── Gate: touching wires report exactly zero, never negative-by-rounding and never NaN ──────

    [Fact]
    public void TwoWiresThatTouch_ReportClearanceZero()
    {
        // Two parallel wires whose centrelines are exactly one diameter apart: the surfaces meet.
        long diameter = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var a = Straight(0, 0, 0, 1_000_000, 0, 0, diameter);
        var b = Straight(0, diameter, 0, 1_000_000, diameter, 0, diameter);

        double clearance = WireGeometry3D.Clearance(a, b);

        Assert.False(double.IsNaN(clearance));
        Assert.Equal(0.0, clearance, 6);
    }

    [Fact]
    public void OverlappingWires_ReportNegativeClearance_NotZero()
    {
        long diameter = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        // Centrelines half a diameter apart: the metal interpenetrates by half a diameter.
        var a = Straight(0, 0, 0, 1_000_000, 0, 0, diameter);
        var b = Straight(0, diameter / 2, 0, 1_000_000, diameter / 2, 0, diameter);

        double clearance = WireGeometry3D.Clearance(a, b);

        // Negative, not clamped: the magnitude is how far a wire has to move, which is the number a
        // user acts on.
        Assert.True(clearance < 0, $"expected interpenetration, got {clearance}");
        Assert.Equal(-diameter / 2.0, clearance, 3);
    }

    [Fact]
    public void WiresCrossingInPlanButSeparatedInZ_AreNotClose()
    {
        long diameter = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        long z = WBondUnits.ToNm(20.0, WBondUnit.Mil);

        var a = Straight(-500_000, 0, 0, 500_000, 0, 0, diameter);
        var b = Straight(0, -500_000, z, 0, 500_000, z, diameter);

        double clearance = WireGeometry3D.Clearance(a, b);

        // This is the whole reason the predicate is 3D: in plan view these two wires cross.
        Assert.Equal(z - diameter, clearance, 3);
    }

    // ── Gate: the accelerated sweep finds exactly what an all-pairs scan finds ───────────────────

    [Fact]
    public void AcceleratedSweep_FindsExactlyWhatAnAllPairsScanFinds()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 120, arrayCount: 6);
        var wires = design.AllWires().ToList();

        double limit = WBondUnits.ToNm(8.0, WBondUnit.Mil);

        var sweep = new WirePairSweep(wires, limit);
        var accelerated = sweep.FindCloserThan(limit);

        var bruteForce = new List<WirePair>();
        for (int i = 0; i < wires.Count; i++)
        for (int j = i + 1; j < wires.Count; j++)
        {
            double d = WireGeometry3D.Clearance(wires[i], wires[j]);
            if (d < limit) bruteForce.Add(new WirePair(i, j, d));
        }

        Assert.Equal(bruteForce.Count, accelerated.Count);
        for (int k = 0; k < bruteForce.Count; k++)
        {
            Assert.Equal(bruteForce[k].A, accelerated[k].A);
            Assert.Equal(bruteForce[k].B, accelerated[k].B);
            Assert.Equal(bruteForce[k].ClearanceNm, accelerated[k].ClearanceNm, 3);
        }

        output.WriteLine($"{wires.Count} wires: {sweep.Counters.AllPairs} all pairs, " +
                         $"{sweep.Counters.CandidatePairs} candidates, {sweep.Counters.TestedPairs} tested, " +
                         $"{accelerated.Count} under {limit} nm");

        // The broad phase has to actually prune, or it is not a broad phase.
        Assert.True(sweep.Counters.CandidatePairs < sweep.Counters.AllPairs / 2,
            $"grid pruned almost nothing: {sweep.Counters.CandidatePairs} of {sweep.Counters.AllPairs}");
    }

    [Fact]
    public void IntersectingWires_AreFound_AndACleanDesignHasNone()
    {
        var clean = TestDesigns.PowerAmplifier(wireCount: 60, arrayCount: 6);
        var cleanWires = clean.AllWires().ToList();

        Assert.Empty(new WirePairSweep(cleanWires, 0).FindIntersections());

        // Now push one wire through another. A real design cannot contain this — two pieces of metal
        // cannot occupy the same space — so it is a geometry error rather than a tight clearance.
        var crossed = cleanWires.ToList();
        long diameter = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        var a = crossed[0];
        crossed.Add(Straight(
            a.Points[0].X, a.Points[0].Y - 200_000, a.Points[0].Z,
            a.Points[0].X, a.Points[0].Y + 200_000, a.Points[0].Z,
            diameter));

        var hits = new WirePairSweep(crossed, 0).FindIntersections();

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(h.ClearanceNm <= 0));
    }

    // ── Per-wire measurements ───────────────────────────────────────────────────────────────────

    [Fact]
    public void MaxAngleChange_IsZeroForAStraightWire_AndTheTurnForABend()
    {
        var straight = Straight(0, 0, 0, 1_000_000, 0, 0, 25_400);
        Assert.Equal(0.0, WireGeometry3D.MaxAngleChangeDegrees(straight), 6);

        var bent = new Wire
        {
            Points = { new Point3(0, 0, 0), new Point3(1_000_000, 0, 0), new Point3(1_000_000, 1_000_000, 0) },
            DiameterNm = 25_400,
        };
        Assert.Equal(90.0, WireGeometry3D.MaxAngleChangeDegrees(bent), 6);

        // A repeated point is legal data and turns through no angle — it must not produce NaN.
        var repeated = new Wire
        {
            Points = { new Point3(0, 0, 0), new Point3(0, 0, 0), new Point3(1_000_000, 0, 0) },
            DiameterNm = 25_400,
        };
        Assert.Equal(0.0, WireGeometry3D.MaxAngleChangeDegrees(repeated), 6);
    }

    [Fact]
    public void FootPitch_IsAPlanarFootToFootDistance_NotAClearance()
    {
        long diameter = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        long z = WBondUnits.ToNm(20.0, WBondUnit.Mil);

        // Feet 6 mil apart in the layout plane, but the loops arch away from each other in z, so the
        // 3D clearance is much larger than the pitch. §8's table needs both quantities.
        var a = Straight(0, 0, 0, 1_000_000, 0, 0, diameter);
        var b = Straight(0, WBondUnits.ToNm(6.0, WBondUnit.Mil), z,
                         1_000_000, WBondUnits.ToNm(6.0, WBondUnit.Mil), z, diameter);

        double pitch = WireGeometry3D.FootPitch(a, b);
        double clearance = WireGeometry3D.Clearance(a, b);

        Assert.Equal(WBondUnits.ToNm(6.0, WBondUnit.Mil), pitch, 3);
        Assert.True(clearance > pitch,
            $"fixture no longer separates the two quantities (pitch {pitch}, clearance {clearance})");
    }

    [Fact]
    public void DistanceToPlanarSegment_MeasuresSurfaceToArtworkEdge()
    {
        long diameter = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        long z = WBondUnits.ToNm(10.0, WBondUnit.Mil);

        // A wire held flat at z, directly over an artwork edge lying at z = 0.
        var wire = Straight(0, 0, z, 1_000_000, 0, z, diameter);
        double d = WireGeometry3D.DistanceToPlanarSegment(wire, 500_000, -100_000, 500_000, 100_000, 0);

        Assert.Equal(z - diameter / 2.0, d, 3);
    }

    // ── Cost, measured rather than claimed (R-wbd-4) ────────────────────────────────────────────

    [Theory]
    [InlineData(100)]
    [InlineData(600)]
    [Trait("Category", "Benchmark")]
    public void ClearanceSweepCost_IsReported_At100And600Wires(int wireCount)
    {
        var design = TestDesigns.PowerAmplifier(wireCount, arrayCount: wireCount >= 600 ? 12 : 5);
        var wires = design.AllWires().ToList();

        // The design's wires sit on a 6 mil pitch, so an 8 mil limit puts every adjacent pair in
        // range. That matters: a limit nothing violates measures only the broad phase, and the
        // narrow phase is the half that could be slow.
        double limit = WBondUnits.ToNm(8.0, WBondUnit.Mil);

        // Warm up the JIT before measuring — the first call through a cold path measures the runtime,
        // not the algorithm.
        _ = new WirePairSweep(wires, limit).FindCloserThan(limit);

        var samples = new List<double>();
        WireSweepCounters counters = default;
        int hitCount = 0;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var sweep = new WirePairSweep(wires, limit);
            var hits = sweep.FindCloserThan(limit);
            sw.Stop();
            counters = sweep.Counters;
            hitCount = hits.Count;
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();

        // The same answer without a broad phase, so the acceleration is reported as a RATIO against
        // a measured baseline rather than as an unquantified claim.
        var naive = Stopwatch.StartNew();
        int naiveHits = 0;
        for (int i = 0; i < wires.Count; i++)
        for (int j = i + 1; j < wires.Count; j++)
            if (WireGeometry3D.Clearance(wires[i], wires[j]) < limit) naiveHits++;
        naive.Stop();

        output.WriteLine(
            $"{wires.Count} wires ({wires.Sum(w => w.Points.Count - 1)} segments): " +
            $"accelerated min {samples[0]:0.###} ms, median {samples[samples.Count / 2]:0.###} ms  |  " +
            $"{counters.AllPairs} all pairs -> {counters.CandidatePairs} candidates -> " +
            $"{counters.TestedPairs} measured -> {hitCount} under {WBondUnits.FromNm((long)limit, WBondUnit.Mil)} mil  |  " +
            $"naive all-pairs {naive.Elapsed.TotalMilliseconds:0.###} ms " +
            $"({naive.Elapsed.TotalMilliseconds / Math.Max(samples[0], 1e-6):0.#}x)");

        Assert.Equal(naiveHits, hitCount);

        // A loose catastrophe guard, not a budget: this must stay far below the ~5 s a user would
        // notice, and it is measured on shared CI hardware.
        Assert.True(samples[0] < 2_000.0, $"clearance sweep took {samples[0]} ms at {wires.Count} wires");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private static Point3 Rand(Random rng, long scale) => new(
        (long)((rng.NextDouble() - 0.5) * 2 * scale),
        (long)((rng.NextDouble() - 0.5) * 2 * scale),
        (long)(rng.NextDouble() * scale));

    private static Wire Straight(long x0, long y0, long z0, long x1, long y1, long z1, long diameterNm) => new()
    {
        Points = { new Point3(x0, y0, z0), new Point3(x1, y1, z1) },
        DiameterNm = diameterNm,
    };
}
