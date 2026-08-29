// brief-em-p4-vector-block-moment-cache.md — per-cell-pair moment caching in the vector block.
//
// P5 (2026-08-29): the per-pair pass this file gates is no longer the production build — it is
// kept as PlanarFill.BuildCoresByPairs / FillByPairs, and the gates below run on those, so P4's
// claim stays pinned to P4's arithmetic. P4_3 alone runs on the production BuildCores/Fill, because
// its claim (the per-entry fill is bit-identical to the dense one) is about whatever ships.
//
// Adjacent rooftops share cells, so the pre-P4 build integrated the same CELL pair up to four
// times per basis pair with different ramp weights, and the per-frequency fill did the same with
// the remainder. The two ramps a cell can carry are linearly dependent through the pulse
// (w_B = Δ·p − w_A, PlanarFill's P4 header), so one pass per ordered cell pair yields seven
// primitives per kernel from which every (half, half) combination — both flow directions — is a
// linear map (P4.2). The build and the fill now run that pass over outer cells; the four-call
// arithmetic survives as the REFERENCE (PlanarFill.BuildCoresByHalves / FillByHalves) and as the
// production path for any pair with a cut half.
//
// WHY THE GATE IS 1e-12 AND NOT A DIGEST. Four combinations of seven primitives are not the same
// floating-point operations as four quadratures summed, so bit-identity is not on offer for the
// vector block (the brief says so); what IS on offer, and is asserted here, is (a) the assembled
// matrix agreeing with the reference to 1e-12 relative per entry, (b) the scalar cores S0/SLog
// being BIT-IDENTICAL, because the pulse×pulse primitive is accumulated with the pulse path's own
// expressions, (c) every entry touching a cut basis being bit-identical, because those pairs never
// left the four-call path, and (d) the per-entry fill being bit-identical to the dense fill, because
// both assemble from the same primitives with the same function in the same order. The cost claim
// is a COUNTER — quadrature passes per m² — not a stopwatch; wall clock is Benchmark and goes to
// HISTORY.md.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarP4MomentCacheTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double Tolerance = 1e-12;

    /// <summary>ConformalFillOracleTests' chamfered rectangle — small, and genuinely cut.</summary>
    private static PlanarProblem Chamfer()
        => new([new PlanarConductorLayer("Metal",
                  [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(3.0e-3, 0),
                                      new EmPoint(3.0e-3, 1.6e-3), new EmPoint(1.6e-3, 2.6e-3),
                                      new EmPoint(0, 2.6e-3)])],
                  5.8e7, 35e-6)],
                GroundedSlab.Fr4Starter, 10e9);

    private static PlanarMeshSettings Conformal(int cellsPerWavelength, bool edge = false, int edgeCells = 3)
        => new(Auto: false, CellsPerWavelength: cellsPerWavelength, EdgeMesh: edge, EdgeCells: edgeCells,
               BoundaryCells: PlanarBoundaryCells.Conformal);

    private sealed record Outcome(int N, int Cells, int CutBases, double MaxRel, int Compared,
                                  double CoreRatio, double RemainderRatio,
                                  double CoreS, double FillS, double RefCoreS, double RefFillS);

    /// <summary>The whole gate on one fixture: cores both ways, fills both ways, and the four
    /// assertions listed in the file header.</summary>
    private Outcome Gate(string label, PlanarProblem problem, PlanarMeshSettings ms, double fHz,
                         PlanarExtractionOrder order = PlanarExtractionOrder.Constant)
    {
        var mesh  = SurfaceMesher.Mesh(problem, ms).Mesh;
        var pair0 = PlanarLineFixtures.Kernel(problem.Slab, fHz);
        double w  = 2 * Math.PI * fHz;
        int n = mesh.Bases.Count, m = mesh.Cells.Count;

        var counters = new PlanarFillCounters();
        var st = PlanarFillSettings.Default with { Order = order, Counters = counters };

        // P5: P4's own arithmetic is the retained reference BuildCoresByPairs / FillByPairs; this
        // gate holds P4's claim (per-pair primitives against four calls) on exactly that arithmetic.
        // The class-table production path is gated against THIS one in PlanarP5TranslationClassTests.
        var sw = Stopwatch.StartNew();
        var cores = PlanarFill.BuildCoresByPairs(mesh, st);
        double coreS = sw.Elapsed.TotalSeconds;
        var pair = pair0.For(cores, order);
        sw.Restart();
        var z = PlanarFill.FillByPairs(cores, pair.VectorPotential, pair.Scalar, w);
        double fillS = sw.Elapsed.TotalSeconds;
        long remainderPasses = counters.RemainderPasses;

        sw.Restart();
        var reference = PlanarFill.BuildCoresByHalves(mesh, st);
        double refCoreS = sw.Elapsed.TotalSeconds;
        var refPair = pair0.For(reference, order);
        sw.Restart();
        var zRef = PlanarFill.FillByHalves(reference, refPair.VectorPotential, refPair.Scalar, w);
        double refFillS = sw.Elapsed.TotalSeconds;

        // (b) the scalar cores are bit-identical — the pulse path's own arithmetic
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
            {
                var got = cores.ScalarCore(a, b);
                var exp = reference.ScalarCore(a, b);
                Assert.True(got.Inverse == exp.Inverse && got.Log == exp.Log && got.Radius == exp.Radius,
                    $"{label}: scalar core ({a},{b}) is not bit-identical: {got} vs {exp}");
            }

        // (a) and (c): 1e-12 on every entry; bit-identical on every entry that touches a cut basis
        int cutBases = 0;
        for (int i = 0; i < n; i++) if (cores.IsCutBasis(i)) cutBases++;

        double maxRel = 0; int compared = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex got = z[i, j], exp = zRef[i, j];
                if (cores.IsCutBasis(i) || cores.IsCutBasis(j))
                {
                    Assert.True(got.Real == exp.Real && got.Imaginary == exp.Imaginary,
                        $"{label}: entry [{i},{j}] touches a cut basis and is not bit-identical: {got} vs {exp}");
                    continue;
                }
                double rel = (got - exp).Magnitude / exp.Magnitude;
                Assert.True(rel <= Tolerance,
                    $"{label}: entry [{i},{j}] differs by {rel:E2} relative (got {got}, reference {exp})");
                if (rel > maxRel) maxRel = rel;
                compared++;
            }

        // symmetry survives the two-triangle scatter (R-fil-2 is structural, but say so)
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                Assert.True(z[i, j] == z[j, i], $"{label}: Z[{i},{j}] != Z[{j},{i}]");

        double coreRatio = (double)cores.QuadraturePasses / reference.QuadraturePasses;
        double remRatio  = (double)remainderPasses / (4.0 * cores.VectorPairs);

        _out.WriteLine($"{label}: N = {n:N0}, cells = {m:N0}, cut bases = {cutBases:N0}");
        _out.WriteLine($"   matrix vs four-call reference: max |Δ|/|ref| = {maxRel:E2} over {compared:N0} " +
                       $"entries; every entry on a cut basis bit-identical; S0/SLog bit-identical");
        _out.WriteLine($"   core build: {cores.QuadraturePasses:N0} passes ({cores.QuadraturePasses / (double)m / m:F3} m²) " +
                       $"vs {reference.QuadraturePasses:N0} ({reference.QuadraturePasses / (double)m / m:F3} m²) — " +
                       $"×{1 / coreRatio:F2}; {coreS:F2} s vs {refCoreS:F2} s");
        _out.WriteLine($"   per-frequency vector remainder: {remainderPasses:N0} passes " +
                       $"({remainderPasses / (double)m / m:F3} m²) vs {4 * cores.VectorPairs:N0} " +
                       $"({4.0 * cores.VectorPairs / m / m:F3} m²) — ×{1 / remRatio:F2}; fill {fillS:F2} s vs {refFillS:F2} s");

        return new Outcome(n, m, cutBases, maxRel, compared, coreRatio, remRatio, coreS, fillS, refCoreS, refFillS);
    }

    // =========================================================================================
    // Routine
    // =========================================================================================

    [Fact]
    public void P4_1_TheHeroAgreesWithTheFourCallReferenceTo1e12_AndTheCountersSayOnePassPerPair()
    {
        // The coarse hero: N ≈ 90, both directions present, no cut cell — the pure (P4.2) case.
        var o = Gate("FR-4 hero, coarse", PlanarLineFixtures.Fr4Line(20e-3, 10e9), PlanarLineFixtures.Coarse, 10e9);
        Assert.Equal(0, o.CutBases);

        // The structural claim, as a counter. Pre-P4: m²/2 scalar + 4 per basis pair ≈ 4 m²
        // (VectorPairs ≈ m² because each direction has ≈ m bases). Now: one pass per ordered pair
        // in the band c ≥ a − n_x, i.e. m²/2 plus the band. Asserted as a ratio against the
        // reference's own count rather than as an absolute, so the mesher's cell count is not a
        // hidden parameter of the gate.
        Assert.True(o.CoreRatio < 0.30, $"core passes are {o.CoreRatio:P0} of the four-call count; expected under 30%");
        Assert.True(o.RemainderRatio < 0.30, $"remainder passes are {o.RemainderRatio:P0} of the four-call count; expected under 30%");
    }

    [Fact]
    public void P4_1b_OrderLinearReachesTheRadiusPrimitives()
    {
        // Order = Linear stores the ∫∫r cores and multiplies the Linear coefficient — the third
        // component of every primitive, which the Constant-order fixtures leave at zero.
        // A short line: the Linear order's extra closed forms make the four-call REFERENCE the
        // slow half of this test, and a 6 mm line (N ≈ 30) reaches the same code.
        var o = Gate("FR-4 line 6 mm, coarse, Order = Linear", PlanarLineFixtures.Fr4Line(6e-3, 10e9),
                     PlanarLineFixtures.Coarse, 10e9, PlanarExtractionOrder.Linear);
        Assert.True(o.Compared > 0);
    }

    [Fact]
    public void P4_2_ACutMeshTakesTheFourCallPathOnEveryCutPair_BitIdentically()
    {
        // Milestone 5: on a cut half the ramp is affine in both coordinates and (P4.1) does not
        // hold. Every pair touching one stays on the four-call path — asserted as bit-identity
        // inside Gate — while the whole pairs of the same mesh take the primitives at 1e-12.
        // cells/λ = 4: the cut cells' conformal quadrature is the expensive part on either side of
        // the comparison, and the coarsest mesh that still cuts cells tests the branch as hard.
        var o = Gate("chamfered rectangle, conformal, coarse", Chamfer(), Conformal(4), 10e9);
        Assert.True(o.CutBases > 0, "the fixture must actually contain cut bases or milestone 5 is untested");
        Assert.True(o.CutBases < o.N, "…and some whole ones, or the primitive path is untested");
    }

    [Fact]
    public void P4_3_ThePerEntryFillAssemblesTheSamePrimitivesAndIsBitIdenticalToTheDenseFill()
    {
        // AimAcceleratorTests.T1 holds this at 16 mm in the Benchmark tier; this is the same
        // assertion on the 8 mm coarse line so it runs routinely — both directions present, and
        // now that neither side is the four-call sum, the identity rests on At() reproducing the
        // dense pass's slot association (A half's two inner cells ascending, then B, then A + B).
        var problem = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, _) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var dense = PlanarFill.BuildCores(mesh);
        var geom  = PlanarFill.BuildGeometryOnlyCores(mesh);
        var k     = PlanarLineFixtures.Kernel(problem.Slab, 6e9);
        double w  = 2 * Math.PI * 6e9;

        var z = PlanarFill.Fill(dense, k.VectorPotential, k.Scalar, w);
        var entry = new PlanarEntryFill(geom, k.VectorPotential, k.Scalar, w);

        int n = mesh.Bases.Count, xs = 0;
        foreach (var b in mesh.Bases) if (b.Direction == PlanarBasisDirection.X) xs++;
        Assert.True(xs > 0 && xs < n, "both directions must be present");

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex a = z[i, j], b = entry.At(i, j);
                Assert.True(a.Real == b.Real && a.Imaginary == b.Imaginary,
                    $"entry [{i},{j}] differs: dense {a} vs per-entry {b}");
            }

        // One ordered cell-pair record serves every (half, half) quadrature that shares it — the
        // pre-P4 At() ran four per same-direction basis pair, cores and remainder each. On a mesh
        // this small the band of ordered pairs is most of the cell-pair square, so the honest claim
        // is "each record replaces more than two quadratures", not the ~7× a long line reaches.
        long samePairs = 0;
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
                if (mesh.Bases[i].Direction == mesh.Bases[j].Direction) samePairs++;
        _out.WriteLine($"N = {n}; {entry.VectorPairCount:N0} ordered cell pairs cached against " +
                       $"{4 * samePairs:N0} four-call quadratures the pre-P4 At() would have run");
        Assert.True(2 * entry.VectorPairCount < 4 * samePairs,
            $"{entry.VectorPairCount} cached pairs against {4 * samePairs} four-call quadratures");
    }

    // =========================================================================================
    // Benchmark — the brief's own three fixtures, with the timings HISTORY.md records
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P4_4_TheSeriesFixturesAgreeTo1e12_WithTimings()
    {
        Gate("FR-4 hero, shipping (N = 552)", PlanarLineFixtures.Fr4Line(20e-3, 10e9),
             PlanarLineFixtures.Shipping, 10e9);
        Gate("60 mm 2.9 → 0.5 mm taper, shipping (N = 1,891)",
             PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 0.5e-3, 60e-3, 10e9),
             PlanarLineFixtures.Shipping, 10e9);
        var o = Gate("16 mm 1.0 → 6.71 mm taper, conformal (N = 1,538)",
                     PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 1.0e-3, 6.71e-3, 16e-3, 6e9),
                     Conformal(20, edge: true), 6e9);
        Assert.True(o.CutBases > 0);
    }
}
