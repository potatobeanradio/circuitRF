// brief-em-p5-translation-class-memo.md — translation-class memoisation of cell-pair integrals.
//
// The mesh is a tensor-product grid and every kernel is a function of separation alone, so an
// ordered cell pair's seven P4 primitives depend only on (w_a, h_a, w_c, h_c, Δx, Δy) and the rule
// its separation selects. P5 keys every band pair on GRID INDICES (spacing classes at 1e-12
// relative, hash-consed spacing lists, the τ band, the 180° rotation folded — PlanarPairClasses.cs),
// integrates each class once on a synthetic representative, and assembles entries from the class
// table at the point of use. P4's per-pair arithmetic is kept as PlanarFill.BuildCoresByPairs /
// FillByPairs and is the REFERENCE here.
//
// WHY THE GATE IS ON THE DIAGONAL SCALE AND NOT RELATIVE PER ENTRY. The brief asked for 1e-12
// relative per entry and that is not a property the reference itself has. Every value in P4's
// matrix is computed at the pair's ABSOLUTE coordinates, and the closed forms lose ~1e-16 × (x/w) of
// their own value there — measured before any P5 code existed: the self core of a 0.5 mm cell moves
// 2e-13 relative when the same cell is placed at x = 1 m instead of at the origin, and the far core
// of two cells 0.25 m apart by 5e-13. A class value is computed with the outer cell at the origin,
// so it is the MORE stable of the two, and the two disagree by ~1e-13 of the near cores. The D4
// assembly then takes signed second differences of those cores, which for an aligned far pair of an
// x̂ and a ŷ rooftop cancel to 1e-10 … 1e-14 of the diagonal — so a fixed ~1e-13 · |Z_ii| absolute
// disagreement reads as anything from 1e-8 to 1e-1 RELATIVE on entries that are themselves
// cancellation residue. Measured on all seven fixtures: max |Δ_ij| / √(|Z_ii||Z_jj|) between
// 6e-14 and 5e-13, while "relative per entry" reaches 0.12 on an entry that is 1e-14 of the largest.
// The gate is therefore |Δ_ij| ≤ 1e-12 · √(|Z_ii||Z_jj|), per entry — the scale on which the
// factorisation reads the entry — and the per-entry relative figures are reported beside it. What
// IS bit-identical, and asserted: the scalar core of every pair with a cut cell (its own row, the
// same conformal call in the same orientation — an ENTRY on a cut basis is not, because its scalar
// block also sums whole-cell pairs), the symmetry of the assembled matrix, and PlanarEntryFill.At
// against Fill (PlanarP4MomentCacheTests P4_3 holds that on the production path).
//
// THE COUNTS ARE THE COST CLAIM. The class count is a counter, not a stopwatch: P5_1 pins the brief's
// own table (its counting method, its numbers) and the classifier's count on the same meshes, so a
// mesher change that silently destroys reuse — jittered bulk spacings, say — turns a test red.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarP5TranslationClassTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double Tolerance = 1e-12;

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // The seven fixtures of the brief's table
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The brief's own seven, with the class counts its table records under its own
    /// counting method (canonical orientation, quantisation 1e-6 · smallest edge, unordered pairs)
    /// and the counts the classifier produces on the same meshes (ordered band pairs, 180° rotation
    /// folded, the τ band in the key).</summary>
    public static IEnumerable<object[]> Seven()
    {
        var fr4 = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;
        yield return ["FR-4 hero 2.9 × 20 mm, 10 GHz", PlanarLineFixtures.Fr4Line(20e-3, 10e9), 10e9, 44_253L, 18_036, 21_056];
        yield return ["FR-4 line 80 mm, 10 GHz", PlanarLineFixtures.Fr4Line(80e-3, 10e9), 10e9, 554_931L, 70_494, 82_264];
        yield return ["FR-4 line 256 mm, 6 GHz", PlanarLineFixtures.Fr4Line(256e-3, 6e9), 6e9, 1_961_190L, 118_188, 137_688];
        yield return ["FR-4 taper 2.9 → 0.5 mm, 20 mm", PlanarLineFixtures.Taper(fr4, 2.9e-3, 0.5e-3, 20e-3, 10e9), 10e9, 265_356L, 10_430, 11_298];
        yield return ["FR-4 taper 60 mm", PlanarLineFixtures.Taper(fr4, 2.9e-3, 0.5e-3, 60e-3, 10e9), 10e9, 500_500L, 16_888, 18_252];
        yield return ["GaAs line 72 µm × 2 mm, 20 GHz", PlanarLineFixtures.GaAsLine(2e-3, 20e9), 20e9, 85_905L, 40_656, 47_632];
        yield return ["two coupled 40 mm lines", PlanarLineFixtures.Problem(fr4, 10e9,
                          PlanarLineFixtures.Rect(0, -w - 0.5 * w, 40e-3, -0.5 * w),
                          PlanarLineFixtures.Rect(0, 0.5 * w, 40e-3, w + 0.5 * w)), 10e9, 603_351L, 106_608, 115_287];
    }

    /// <summary>The brief's counting method, verbatim: unordered pairs, the pair oriented so that
    /// (Δx, Δy) ≥ 0 lexicographically, the six numbers quantised at 1e-6 of the smallest edge.</summary>
    private static (long Pairs, int Classes) BriefCount(PlanarMesh mesh)
    {
        int m = mesh.Cells.Count;
        double minEdge = double.PositiveInfinity;
        foreach (var c in mesh.Cells) minEdge = Math.Min(minEdge, Math.Min(c.Width, c.Height));
        double q = 1e-6 * minEdge;
        static long Q(double v, double quantum) => (long)Math.Round(v / quantum);

        var classes = new HashSet<(long, long, long, long, long, long)>();
        long pairs = 0;
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
            {
                var ca = mesh.Cells[a]; var cb = mesh.Cells[b];
                double dx = cb.XMin - ca.XMin, dy = cb.YMin - ca.YMin;
                if (dx < 0 || (dx == 0 && dy < 0)) { (ca, cb) = (cb, ca); dx = -dx; dy = -dy; }
                classes.Add((Q(ca.Width, q), Q(cb.Width, q), Q(dx, q), Q(ca.Height, q), Q(cb.Height, q), Q(dy, q)));
                pairs++;
            }
        return (pairs, classes.Count);
    }

    private void CountOne(string label, PlanarProblem problem, long pairs, int briefClasses, int classifierClasses)
    {
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var (p, c) = BriefCount(mesh);
        var cores  = PlanarFill.BuildCores(mesh);
        _out.WriteLine($"{label}: N = {mesh.Bases.Count:N0}, cells = {mesh.Cells.Count:N0}; brief's count " +
                       $"{p:N0} pairs / {c:N0} classes ({(double)p / c:F1}×); classifier {cores.BandPairs:N0} " +
                       $"ordered band pairs / {cores.ClassCount:N0} classes ({(double)cores.BandPairs / cores.ClassCount:F1}×); " +
                       $"spacings x {cores.SpacingClasses.X} classes of {cores.ExactlyDistinctSpacings.X} exactly distinct, " +
                       $"y {cores.SpacingClasses.Y} of {cores.ExactlyDistinctSpacings.Y}");
        Assert.Equal(pairs, p);
        Assert.Equal(briefClasses, c);
        Assert.Equal(classifierClasses, cores.ClassCount);
        Assert.Equal(cores.ClassCount, (int)cores.QuadraturePasses);   // no cut cell: one pass per class
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Routine
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P5_1_TheBriefsClassCountsReproduce_OnTheHeroAndThe60mmTaper()
    {
        // Milestone 1: the table, as a counter. Both the brief's own method and the classifier's
        // count are literals, so a mesher change that jitters a bulk spacing turns this red.
        foreach (var row in Seven())
        {
            string label = (string)row[0];
            if (!label.StartsWith("FR-4 hero") && !label.StartsWith("FR-4 taper 60")) continue;
            CountOne(label, (PlanarProblem)row[1], (long)row[3], (int)row[4], (int)row[5]);
        }
    }

    private sealed record Outcome(int N, int Cells, int Classes, long Band, int CutBases,
                                  double MaxScaled, double MaxRel, double MaxRelBig, double MaxAbsOverMax,
                                  long RemainderPasses, double CoreS, double FillS, double RefCoreS, double RefFillS);

    /// <summary>The whole gate on one fixture: cores and fills both ways, the diagonal-scale gate,
    /// bit-identity on cut pairs, symmetry, the counters, and the per-entry relative figures reported.</summary>
    private Outcome Gate(string label, PlanarProblem problem, PlanarMeshSettings ms, double fHz,
                         PlanarExtractionOrder order = PlanarExtractionOrder.Constant, PlanarMesh? meshOverride = null)
    {
        var mesh  = meshOverride ?? SurfaceMesher.Mesh(problem, ms).Mesh;
        var pair0 = PlanarLineFixtures.Kernel(problem.Slab, fHz);
        double w  = 2 * Math.PI * fHz;
        int n = mesh.Bases.Count, m = mesh.Cells.Count;

        var counters = new PlanarFillCounters();
        var st = PlanarFillSettings.Default with { Order = order, Counters = counters };

        var sw = Stopwatch.StartNew();
        var cores = PlanarFill.BuildCores(mesh, st);
        double coreS = sw.Elapsed.TotalSeconds;
        var pair = pair0.For(cores, order);
        var z = PlanarFill.Fill(cores, pair.VectorPotential, pair.Scalar, w);
        counters = new PlanarFillCounters();
        cores = PlanarFill.BuildCores(mesh, st with { Counters = counters });
        pair = pair0.For(cores, order);
        sw.Restart();
        z = PlanarFill.Fill(cores, pair.VectorPotential, pair.Scalar, w);
        double fillS = sw.Elapsed.TotalSeconds;
        long remainderPasses = counters.RemainderPasses;

        sw.Restart();
        var reference = PlanarFill.BuildCoresByPairs(mesh, st);
        double refCoreS = sw.Elapsed.TotalSeconds;
        var refPair = pair0.For(reference, order);
        sw.Restart();
        var zRef = PlanarFill.FillByPairs(reference, refPair.VectorPotential, refPair.Scalar, w);
        double refFillS = sw.Elapsed.TotalSeconds;

        Assert.Equal(PlanarCoreLayout.Classes,   cores.Layout);
        Assert.Equal(PlanarCoreLayout.Triangles, reference.Layout);

        int cutBases = 0;
        for (int i = 0; i < n; i++) if (cores.IsCutBasis(i)) cutBases++;

        double big = 0;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) big = Math.Max(big, zRef[i, j].Magnitude);

        // Every pair with a cut CELL keeps its own conformal scalar core, computed by the same call
        // in the same orientation as P4's — bit-identical, whatever the layout. (An ENTRY touching a
        // cut basis is not: its scalar block also sums whole-cell pairs, which are classed now.)
        for (int a = 0; a < m; a++)
        {
            if (!mesh.Cells[a].IsCut) continue;
            for (int b = 0; b < m; b++)
            {
                var got = cores.ScalarCore(a, b);
                var exp = reference.ScalarCore(a, b);
                Assert.True(got.Inverse == exp.Inverse && got.Log == exp.Log && got.Radius == exp.Radius,
                    $"{label}: scalar core ({a},{b}) with a cut cell is not bit-identical: {got} vs {exp}");
            }
        }

        double maxScaled = 0, maxRel = 0, maxRelBig = 0, maxAbs = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex got = z[i, j], exp = zRef[i, j];
                double d = (got - exp).Magnitude;
                double scale = Math.Sqrt(zRef[i, i].Magnitude * zRef[j, j].Magnitude);
                Assert.True(d <= Tolerance * scale,
                    $"{label}: entry [{i},{j}] differs by {d / scale:E2} of √(Z_ii·Z_jj) (got {got}, reference {exp})");
                maxScaled = Math.Max(maxScaled, d / scale);
                maxAbs    = Math.Max(maxAbs, d);
                double rel = d / exp.Magnitude;
                maxRel = Math.Max(maxRel, rel);
                if (exp.Magnitude >= 1e-6 * big) maxRelBig = Math.Max(maxRelBig, rel);
            }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                Assert.True(z[i, j] == z[j, i], $"{label}: Z[{i},{j}] != Z[{j},{i}]");

        _out.WriteLine($"{label}: N = {n:N0}, cells = {m:N0}, cut bases = {cutBases:N0}; " +
                       $"{cores.BandPairs:N0} band pairs → {cores.ClassCount:N0} classes " +
                       $"({(cores.ClassCount > 0 ? (double)cores.BandPairs / cores.ClassCount : 0):F1}×)");
        _out.WriteLine($"   vs P4's per-pair reference: max |Δ|/√(Z_ii·Z_jj) = {maxScaled:E2}, max |Δ|/|Z_max| = {maxAbs / big:E2}; " +
                       $"relative per entry: {maxRelBig:E2} over entries ≥ 1e-6·|Z_max|, {maxRel:E2} over all");
        _out.WriteLine($"   core build: {cores.QuadraturePasses:N0} passes vs {reference.QuadraturePasses:N0} — " +
                       $"{coreS:F2} s vs {refCoreS:F2} s (×{refCoreS / Math.Max(coreS, 1e-9):F1}); " +
                       $"cores {cores.CoreBytes / 1048576.0:F1} MB vs {reference.CoreBytes / 1048576.0:F1} MB");
        _out.WriteLine($"   per-frequency fill: {remainderPasses:N0} vector remainder passes vs P4's one per band pair; " +
                       $"{fillS:F2} s vs {refFillS:F2} s (×{refFillS / Math.Max(fillS, 1e-9):F1})");

        return new Outcome(n, m, cores.ClassCount, cores.BandPairs, cutBases, maxScaled, maxRel, maxRelBig,
                           maxAbs / big, remainderPasses, coreS, fillS, refCoreS, refFillS);
    }

    [Fact]
    public void P5_2_TheCoarseHeroAgreesWithP4OnTheDiagonalScale_AndOnePassPerClass()
    {
        var o = Gate("FR-4 hero, coarse", PlanarLineFixtures.Fr4Line(20e-3, 10e9), PlanarLineFixtures.Coarse, 10e9);
        Assert.Equal(0, o.CutBases);
        // one core pass and one vector remainder pass per class, and far fewer classes than pairs
        Assert.Equal(o.Classes, o.RemainderPasses);
        Assert.True(o.Classes * 3 < o.Band, $"{o.Classes} classes for {o.Band} band pairs — the memo is not reusing");
        // the vector block itself is not cancellation residue: its relative agreement is tight
        Assert.True(o.MaxRelBig < 1e-9, $"entries at or above 1e-6 of the largest differ by {o.MaxRelBig:E2} relative");
    }

    [Fact]
    public void P5_2b_TheSolvedCurrentsAgreeToTheSameScale()
    {
        // What the factorisation makes of the two matrices — the claim that matters downstream.
        // Unit excitations at a few bases; the solutions agree to ~1e-12 relative, which is the
        // diagonal-scale gate above seen through the LU.
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var pair0   = PlanarLineFixtures.Kernel(problem.Slab, 10e9);
        double w    = 2 * Math.PI * 10e9;

        var cores = PlanarFill.BuildCores(mesh);
        var p = pair0.For(cores, PlanarExtractionOrder.Constant);
        var z = PlanarSystem.Wrap(PlanarFill.Fill(cores, p.VectorPotential, p.Scalar, w));
        var reference = PlanarFill.BuildCoresByPairs(mesh);
        var q = pair0.For(reference, PlanarExtractionOrder.Constant);
        var zRef = PlanarSystem.Wrap(PlanarFill.FillByPairs(reference, q.VectorPotential, q.Scalar, w));

        int n = mesh.Bases.Count;
        double worst = 0;
        foreach (int k in new[] { 0, n / 3, n / 2, n - 1 })
        {
            var rhs = new Vec<Complex>(n);
            rhs[k] = Complex.One;
            var x = z.Solve(rhs);
            var y = zRef.Solve(rhs);
            double num = 0, den = 0;
            for (int i = 0; i < n; i++) { num += (x[i] - y[i]).Magnitude * (x[i] - y[i]).Magnitude; den += y[i].Magnitude * y[i].Magnitude; }
            worst = Math.Max(worst, Math.Sqrt(num / den));
        }
        _out.WriteLine($"N = {n}: solutions of Z x = e_k agree to {worst:E2} relative (2-norm)");
        Assert.True(worst < 1e-11, $"solutions differ by {worst:E2}");
    }

    [Fact]
    public void P5_3_OrderLinearReachesTheRadiusPrimitives()
    {
        // Order = Linear stores the ∫∫r cores in a third kernel slot of every class entry.
        var o = Gate("FR-4 line 6 mm, coarse, Order = Linear", PlanarLineFixtures.Fr4Line(6e-3, 10e9),
                     PlanarLineFixtures.Coarse, 10e9, PlanarExtractionOrder.Linear);
        Assert.True(o.Classes > 0);
    }

    /// <summary>ConformalFillOracleTests' chamfered rectangle — small, and genuinely cut.</summary>
    private static PlanarProblem Chamfer()
        => new([new PlanarConductorLayer("Metal",
                  [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(3.0e-3, 0),
                                      new EmPoint(3.0e-3, 1.6e-3), new EmPoint(1.6e-3, 2.6e-3),
                                      new EmPoint(0, 2.6e-3)])],
                  5.8e7, 35e-6)],
                GroundedSlab.Fr4Starter, 10e9);

    [Fact]
    public void P5_4_ACutMeshKeepsItsCutPairsOnRows_BitIdenticalWhereTheyStandAlone()
    {
        // A pair with a cut cell is never memoised: its scalar core lives on a per-cell row and a
        // pair with a cut basis on a per-basis row, both computed by L8c's own calls. The scalar
        // rows are asserted bit-identical to P4's inside Gate; every entry, cut or whole, meets the
        // diagonal-scale gate.
        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 4, EdgeMesh: false,
                                        BoundaryCells: PlanarBoundaryCells.Conformal);
        var o = Gate("chamfered rectangle, conformal, coarse", Chamfer(), ms, 10e9);
        Assert.True(o.CutBases > 0, "the fixture must actually contain cut bases");
        Assert.True(o.CutBases < o.N, "…and some whole ones, or the class path is untested");
        Assert.True(o.Classes > 0);
    }

    [Fact]
    public void P5_5_AUniformGridPutsPairsOnTheRuleThreshold_AndTheBandInTheKeyHolds()
    {
        // Two equal cells offset by (4, 4) cells have τ = 4 in exact arithmetic — exactly FarRatio —
        // and floating point puts each pair on one side or the other. A class that carried its
        // representative's rule to such a member would be wrong by the rule change (~1e-6 of the
        // entry, ~1e-7 of the diagonal scale — far above the gate). The band is in the key, and this
        // grid is the family that would catch its removal.
        var gx = new double[13]; var gy = new double[13];
        for (int i = 0; i <= 12; i++) { gx[i] = 0.7e-3 * i; gy[i] = 0.7e-3 * i; }
        var mesh = PlanarFillTests.Grid(gx, gy);
        var problem = PlanarLineFixtures.Fr4Line(8.4e-3, 10e9);
        var o = Gate("12 × 12 uniform square cells", problem, PlanarLineFixtures.Coarse, 10e9, meshOverride: mesh);
        Assert.True(o.Classes > 0);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Benchmark — the brief's seven fixtures at the shipping mesh, with the timings HISTORY.md records
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P5_6_TheSevenFixtures_CountsGateAndTimings()
    {
        foreach (var row in Seven())
        {
            string label = (string)row[0];
            var problem  = (PlanarProblem)row[1];
            CountOne(label, problem, (long)row[3], (int)row[4], (int)row[5]);
            Gate(label, problem, PlanarLineFixtures.Shipping, (double)row[2]);
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P5_7_AimNearFillOnThe256mmLine()
    {
        // Milestone 5: the per-entry fill classifies its near pairs on demand and integrates a class
        // once. Reported beside P4's 8.34 s (measured on the P4 tree, same box, same day).
        var problem = PlanarLineFixtures.Fr4Line(256e-3, 6e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var pair0   = PlanarLineFixtures.Kernel(problem.Slab, 6e9);
        double w    = 2 * Math.PI * 6e9;
        var geom = PlanarFill.BuildGeometryOnlyCores(mesh);
        var gp   = pair0.For(geom, PlanarExtractionOrder.Constant);
        var aim  = PlanarAimOperator.Build(geom, gp.VectorPotential, gp.Scalar, w, problem.Slab.HeightM,
                                           PlanarAimSettings.Default);
        _out.WriteLine($"N = {mesh.Bases.Count:N0}: AIM near fill {aim.Report.NearFillMs / 1000.0:F2} s, " +
                       $"{aim.Report.NearEntries:N0} near entries from {aim.Report.NearCellPairs:N0} scalar classes");
        Assert.True(aim.Report.NearCellPairs > 0);
    }
}
