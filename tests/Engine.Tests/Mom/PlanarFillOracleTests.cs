// L8c — Tier 2: matrix entries against the SOMMERFELD oracle.
//
// R-fil-6 — every entry is validated against SommerfeldIntegral, never against Dcim: DCIM is the
// production path, and validating it against itself proves nothing. This is the tier that decides
// whether the fill is right, and it measures TWO things at once that must be reported separately:
//
//   • the FILL's own quadrature error, which Tier 3 already bounded at ~1e-6 by comparing two paths
//     that share a kernel;
//   • the KERNEL's error, which L8a measured at ≤ 6e-3 as a fraction of the free-space kernel and
//     which this tier inherits rather than improves.
//
// So the number below is expected to land near 6e-3, NOT near 1e-6, and if it landed at 1e-6 that
// would mean the oracle was not independent. R-fil-2's own report asks for the two side by side.
//
// THE ERROR MEASURE IS L8a's SCALED ONE, not a strict relative error, and for L8a's own reason: G_q
// has deep cancellation zones — a few substrate heights out, charge plus its ground image is a
// DIPOLE — and a relative error against a quantity that is nearly zero says more about the zero than
// about the method. The entries are therefore compared against the free-space entry at the same
// geometry, which is exactly what "an entry perturbed by ε·(1/4πρ) perturbs the linear system by ε"
// means.
//
// TAGGING. The full 2 substrates x 3 frequencies x 4 separations sweep is Category=Benchmark,
// following L8a's precedent: not because any one case is slow by the ~5 s rule, but because a
// phase's own reporting sweep has no business spending Hero1BTests' wall-clock headroom. ONE
// representative case per starter technology stays in the routine gate.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarFillOracleTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>A small mesh at each substrate's own natural cell size (λ_g/20 at 10 GHz, roughly).</summary>
    private static PlanarMesh MeshFor(GroundedSlab slab)
    {
        double c = slab.HeightM > 1e-4 ? 0.7e-3 : 20e-6;      // FR-4 1.6 mm vs GaAs 100 µm
        return PlanarFillTests.Grid([0, c, 2.2 * c, 3.0 * c], [0, 0.9 * c, 1.8 * c]);
    }

    /// <summary>The four separations R-fil-6 names, as CELL pairs of that mesh.</summary>
    private static (int A, int B, string Name)[] Pairs =>
        [(0, 0, "self"), (0, 1, "nearest"), (0, 2, "next-nearest"), (0, 5, "far")];

    private static SommerfeldRadialTable OracleTable(SpectralGreens g, GreensKernel k, PlanarMesh mesh,
                                                     int pointsPerDecade)
    {
        double minEdge = double.PositiveInfinity, ext = 0;
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = 0, y1 = 0;
        foreach (var c in mesh.Cells)
        {
            minEdge = Math.Min(minEdge, Math.Min(c.Width, c.Height));
            x0 = Math.Min(x0, c.XMin); y0 = Math.Min(y0, c.YMin);
            x1 = Math.Max(x1, c.XMax); y1 = Math.Max(y1, c.YMax);
        }
        ext = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

        // THE MARGIN IS LOAD-BEARING, and this is the second time in this phase an ORACLE rather than
        // the method turned out to be the thing that was wrong. Built to exactly the mesh's extent,
        // the table's last Catmull-Rom stencil has to clamp its forward sample and degrades to
        // something near linear — measured at 2.1e-3 scaled error on FR-4 at 20 GHz, against a DCIM
        // error of 4.2e-6 at the same ρ. That reads exactly like a kernel failure and is nothing of
        // the sort. Building three times as far keeps every query strictly interior.
        return SommerfeldRadialTable.Build(g, k, 1e-8 * minEdge, 3.0 * ext, pointsPerDecade);
    }

    /// <summary>
    /// The comparison itself, for one substrate and frequency: the production fill's scalar entry
    /// P[a,b] and its vector counterpart, each against the correlation oracle driven by the direct
    /// Sommerfeld integral.
    /// </summary>
    private string Compare(GroundedSlab slab, double freqHz, int pointsPerDecade, double tolerance)
    {
        var mesh   = MeshFor(slab);
        var greens = new SpectralGreens(slab, freqHz);
        double k0  = greens.K0;
        double omega = 2.0 * Math.PI * freqHz;

        var tabQ = OracleTable(greens, GreensKernel.ScalarPotential,  mesh, pointsPerDecade);
        var tabA = OracleTable(greens, GreensKernel.VectorPotential, mesh, pointsPerDecade);

        var st    = PlanarFillSettings.Default;
        var cores = PlanarFill.BuildCores(mesh, st);
        var termsQ = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.ScalarPotential), st.Order, cores.RhoFloorM);
        var termsA = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.VectorPotential), st.Order, cores.RhoFloorM);

        var p = PlanarFill.ScalarPotentialMatrix(cores, termsQ);
        var z = PlanarFill.Fill(cores, termsA, termsQ, omega);

        var report = new System.Text.StringBuilder();
        double worst = 0;

        foreach (var (a, b, name) in Pairs)
        {
            // ── G_q, directly: the per-cell potential entry ──────────────────────────────────
            Complex got  = p[a, b];
            Complex want = PlanarPairOracle.Pair(mesh.Cells[a], mesh.Cells[b], false, 0, false, 0, true,
                                                 tabQ.Evaluate);

            // L8a's scaled measure: the same entry with the FREE-SPACE kernel is the yardstick.
            double scale = PlanarPairOracle.Pair(mesh.Cells[a], mesh.Cells[b], false, 0, false, 0, true,
                                                 rho => SommerfeldIntegral.FreeSpace(k0, rho)).Magnitude;
            double eq = (got - want).Magnitude / scale;
            worst = Math.Max(worst, eq);
            report.Append($"  G_q {name,-12} |ΔP|/free-space = {eq:E2}\n");
        }

        // ── G_A, through the assembled matrix: a same-direction basis pair, with the scalar block
        //    subtracted using the SAME P the production path used, so what is left is purely vector.
        for (int m = 0; m < mesh.Bases.Count; m++)
            for (int n = m; n < mesh.Bases.Count; n++)
            {
                if (mesh.Bases[m].Direction != mesh.Bases[n].Direction) continue;
                if (m != n && n != m + 2) continue;                        // self and one neighbour

                var (ma, mb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[m]);
                var (na, nb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[n]);
                Complex scalar = ma.Sign * na.Sign * p[ma.CellIndex, na.CellIndex]
                               + ma.Sign * nb.Sign * p[ma.CellIndex, nb.CellIndex]
                               + mb.Sign * na.Sign * p[mb.CellIndex, na.CellIndex]
                               + mb.Sign * nb.Sign * p[mb.CellIndex, nb.CellIndex];
                Complex vectorGot = (z[m, n] - scalar / (Complex.ImaginaryOne * omega * EmConstants.Eps0))
                                  / (Complex.ImaginaryOne * omega * EmConstants.Mu0);

                bool alongX = mesh.Bases[m].Direction == PlanarBasisDirection.X;
                Complex vectorWant = Complex.Zero, freeScale = Complex.Zero;
                foreach (var hm in new[] { ma, mb })
                    foreach (var hn in new[] { na, nb })
                    {
                        vectorWant += PlanarPairOracle.Pair(mesh.Cells[hm.CellIndex], mesh.Cells[hn.CellIndex],
                                                            true, hm.OuterEdge, true, hn.OuterEdge, alongX,
                                                            tabA.Evaluate);
                        freeScale += PlanarPairOracle.Pair(mesh.Cells[hm.CellIndex], mesh.Cells[hn.CellIndex],
                                                           true, hm.OuterEdge, true, hn.OuterEdge, alongX,
                                                           rho => SommerfeldIntegral.FreeSpace(k0, rho));
                    }

                double ea = (vectorGot - vectorWant).Magnitude / freeScale.Magnitude;
                worst = Math.Max(worst, ea);
                report.Append($"  G_A basis({m},{n}){(m == n ? " self" : " neighbour")} " +
                              $"|ΔZ_A|/free-space = {ea:E2}\n");
            }

        _out.WriteLine($"=== εᵣ = {slab.Material.EpsR:G3}, h = {slab.HeightM * 1e3:G3} mm, " +
                       $"{freqHz / 1e9:G3} GHz — worst scaled error {worst:E2}");
        _out.WriteLine(report.ToString().TrimEnd());

        Assert.True(worst < tolerance,
            $"{slab.Material.EpsR:G3} slab at {freqHz / 1e9:G3} GHz — worst scaled error {worst:E2}:\n{report}");
        return report.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The routine gate — one representative case per starter technology
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T2_1_Fr4At10GHz_MatchesTheSommerfeldOracle()
        => Compare(GroundedSlab.Fr4Starter, 10e9, pointsPerDecade: 16, tolerance: 1e-3);

    [Fact]
    public void T2_2_GaAsAt10GHz_MatchesTheSommerfeldOracle()
        => Compare(GroundedSlab.GaAsStarter, 10e9, pointsPerDecade: 16, tolerance: 1e-3);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The reporting sweep — Category=Benchmark, for another test's budget rather than its own
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData(false, 2e9)]
    [InlineData(false, 10e9)]
    [InlineData(false, 20e9)]
    [InlineData(true, 2e9)]
    [InlineData(true, 10e9)]
    [InlineData(true, 20e9)]
    public void T2_3_TheFullBandOnBothStarters(bool gaAs, double freqHz)
        => Compare(gaAs ? GroundedSlab.GaAsStarter : GroundedSlab.Fr4Starter, freqHz,
                   pointsPerDecade: 24, tolerance: 8e-3);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Check the ORACLE first — M4's own instruction, and this area's record justifies it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T2_4_RefiningTheOracleDoesNotMoveIt()
    {
        // "If Tier 2 disagrees with the oracle, check the ORACLE first. Refine the oracle's own
        // integrator and see whether the discrepancy moves; if it does not move, it is not
        // convergence." This tier records the refinement so a future disagreement starts from a
        // known-good oracle rather than from a guess.
        var slab   = GroundedSlab.Fr4Starter;
        var mesh   = MeshFor(slab);
        var greens = new SpectralGreens(slab, 10e9);

        var coarse = OracleTable(greens, GreensKernel.ScalarPotential, mesh, 16);
        var fine   = OracleTable(greens, GreensKernel.ScalarPotential, mesh, 48);

        double worst = 0;
        foreach (var (a, b, _) in Pairs)
        {
            Complex c = PlanarPairOracle.Pair(mesh.Cells[a], mesh.Cells[b], false, 0, false, 0, true, coarse.Evaluate);
            Complex f = PlanarPairOracle.Pair(mesh.Cells[a], mesh.Cells[b], false, 0, false, 0, true, fine.Evaluate);
            // The SCALED measure again, for L8a's reason: G_q's far entries sit in a cancellation
            // zone, and a relative error against a near-zero says more about the zero than about the
            // oracle.
            double scale = PlanarPairOracle.Pair(mesh.Cells[a], mesh.Cells[b], false, 0, false, 0, true,
                                                 rho => SommerfeldIntegral.FreeSpace(greens.K0, rho)).Magnitude;
            worst = Math.Max(worst, (c - f).Magnitude / scale);
        }

        _out.WriteLine($"oracle sampling 16 → 48 points/decade moves the entry by {worst:E2} (scaled)");
        Assert.True(worst < 1e-5,
            $"tripling the oracle's sampling density moved it by {worst:E2} scaled — it is not converged, so " +
            "nothing may be concluded from a disagreement with it");
    }
}
