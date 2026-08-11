// Conformal boundary cells — §7 gate 5: the SLIVER THRESHOLD, measured.
//
// SurfaceMesher.DefaultSliverAreaFraction's own doc comment says the value "is a MEASUREMENT, not a
// taste — see ConformalSliverTests, which sweeps it and reports the matrix condition number and the
// answer either side". That file did not exist; this is it, and it exists because a constant
// asserted to be measured with no measurement behind it is worse than an admitted guess.
//
// WHY THE CONDITION NUMBER IS THE RIGHT INSTRUMENT, and why the answer alone is not:
//
//   The rooftop is normalised by 1/Area. A cut that leaves a vanishing fraction of a grid rectangle
//   therefore puts an enormous row into the matrix — and the failure is SILENT, because the matrix
//   stays symmetric and still factors. A capacitance read off a badly-conditioned solve can look
//   perfectly reasonable while carrying no significant figures at all. So the sweep reports BOTH:
//   κ(P) says whether the linear algebra is trustworthy, C says whether the answer moved.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class ConformalSliverTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double DiscRadiusM = 1.45e-3;
    private const int    DiscPoints  = 96;

    /// <summary>
    /// The same 96-point disc the convergence ladder uses, and for the same reason: no axis-parallel
    /// edge anywhere, so EVERY rim cell is cut and the sliver population is real rather than
    /// contrived. A Manhattan fixture produces no slivers at all and would measure nothing.
    /// </summary>
    private static PlanarProblem Disc(double fHz = 10e9)
    {
        var ring = new EmPoint[DiscPoints];
        for (int i = 0; i < DiscPoints; i++)
        {
            double a = 2.0 * Math.PI * i / DiscPoints;
            ring[i] = new EmPoint(DiscRadiusM * Math.Cos(a), DiscRadiusM * Math.Sin(a));
        }
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, fHz);
    }

    private static PlanarMeshSettings Settings(int cpw) =>
        new(Auto: false, CellsPerWavelength: cpw, EdgeMesh: false, EdgeCells: 0,
            BoundaryCells: PlanarBoundaryCells.Conformal);

    private static PlanarKernelTerms Eps1OverGround()
        => PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * GroundedSlab.Fr4Starter.HeightM);

    /// <summary>L8c's D8 static harness, and the matrix it solves — both are wanted, so the matrix is
    /// returned rather than only the capacitance.</summary>
    private static (double C, double Kappa, double MinAreaFraction) Solve(PlanarMesh mesh)
    {
        var st    = PlanarFillSettings.Default;
        var cores = PlanarFill.BuildCores(mesh, st);
        var p     = PlanarFill.ScalarPotentialMatrix(cores, Eps1OverGround().With(st.Order, cores.RhoFloorM));

        int m = mesh.Cells.Count;
        var a   = new Mat<Complex>(m, m);
        var rhs = new Vec<Complex>(m);
        for (int i = 0; i < m; i++)
        {
            rhs[i] = Complex.One;
            for (int j = 0; j < m; j++) a[i, j] = p[i, j] / EmConstants.Eps0;
        }

        // κ from the singular values of the matrix actually solved. Not an estimate — the fixture is
        // deliberately coarse so a full SVD is affordable and there is nothing to argue about.
        var s = a.Svd().S;
        double kappa = s[0] / s[^1];

        var q = a.Lu().Solve(rhs);
        Complex total = Complex.Zero;
        for (int i = 0; i < m; i++) total += q[i];

        // The worst normalisation the basis is being asked to carry: the smallest cell area as a
        // fraction of its own grid rectangle. THIS is the quantity the threshold acts on.
        double worst = 1.0;
        foreach (var c in mesh.Cells)
        {
            double rect = c.Width * c.Height;
            if (rect > 0) worst = Math.Min(worst, c.Area / rect);
        }
        return (total.Real, kappa, worst);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The sweep. Reported as a table, and gated on the two things that must be true either side.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(130)]   // sliver 4.1e-3 of a grid rectangle — a 1/Area factor of ~245
    [InlineData(250)]   // the THINNEST any density in G5c's scan produced: 4.4e-4, a factor of ~2,270
    [Trait("Category", "Benchmark")]
    public void G5_TheSliverThreshold_SweptWithConditionNumberAndAnswer(int Cpw)
    {
        var disc = Disc();

        // ── THE DENSITY MATTERS, AND THE FIRST VERSION OF THIS SWEEP PICKED THE WRONG ONE ────────
        //
        // Run at cells/λ = 70 the whole sweep measures nothing: the thinnest cut cell that density
        // produces is 1.5e-2 of its grid rectangle, so there is no sliver to absorb and the table
        // reads as "the threshold barely matters" for the wrong reason. G5c then scanned the
        // densities and found the mesher genuinely produces slivers down to 4.4e-4. Both densities
        // that actually make one are swept here, and the thinner is the decisive one.

        _out.WriteLine($"  cells/λ = {Cpw}");
        _out.WriteLine("   frac    cells   cut  merged   min area/rect        κ(P)          C (fF)");
        var rows = new List<(double Frac, int Cells, int Cut, int Merged, double MinFrac,
                             double Kappa, double C)>();

        // 0.0 is "never merge" — every sliver, however thin, is solved as its own cell. That is the
        // control the threshold exists to improve on, and it must be in the sweep or the table says
        // nothing about what merging bought.
        foreach (double frac in new[] { 0.0, 0.001, 0.005, 0.02, 0.05, 0.10 })
        {
            var r = SurfaceMesher.Mesh(disc, Settings(Cpw), sliverAreaFraction: frac);
            Assert.True(r.CanSolve, r.Refusal);

            var (c, kappa, minFrac) = Solve(r.Mesh);
            rows.Add((frac, r.Mesh.Cells.Count, r.CutCellCount, r.MergedSliverCount, minFrac, kappa, c));

            _out.WriteLine($"  {frac,6:F3}  {r.Mesh.Cells.Count,6}  {r.CutCellCount,4}  " +
                           $"{r.MergedSliverCount,6}   {minFrac:E3}   {kappa:E4}   {c * 1e15:F4}");
        }

        var never = rows[0];
        var shipped = rows.Single(x => x.Frac == SurfaceMesher.DefaultSliverAreaFraction);

        _out.WriteLine($"[VERDICT] shipped threshold {SurfaceMesher.DefaultSliverAreaFraction:F2}: " +
                       $"κ = {shipped.Kappa:E4} against {never.Kappa:E4} with merging OFF " +
                       $"({never.Kappa / shipped.Kappa:F1}x better), " +
                       $"C moved {Math.Abs(shipped.C - never.C) / Math.Abs(never.C):P4}");

        // NON-VACUITY FIRST: merging must actually have something to do on this fixture, or every
        // comparison below is between two identical meshes.
        Assert.True(shipped.Merged > 0,
            "the shipped threshold merged nothing on a 96-point disc — the fixture has no slivers " +
            "and this sweep measures nothing");

        // (1) THE THRESHOLD IS DOING ITS JOB: no cell survives below the fraction it is set at.
        Assert.True(shipped.MinFrac >= SurfaceMesher.DefaultSliverAreaFraction - 1e-12,
            $"a cell survived at {shipped.MinFrac:E3} of its grid rectangle, below the " +
            $"{SurfaceMesher.DefaultSliverAreaFraction:F2} threshold that is supposed to absorb it");

        // ── (2) CONDITIONING — AND THE MEASUREMENT CONTRADICTS R-cut-3's STATED RATIONALE ────────
        //
        // R-cut-3 (and DefaultSliverAreaFraction's own doc comment, before this test existed) says a
        // sliver "puts an enormous row in the matrix and destroys the conditioning". MEASURED, IT
        // DOES NOT: over a 245x area ratio at cells/λ = 130, κ(P) moves from 54.41 to 53.75 — about
        // one percent — and the disc's matrix is beautifully conditioned either way.
        //
        // The reason is worth keeping, because it is structural rather than a property of this
        // fixture. P is normalised 1/(A_i·A_j) on BOTH sides, so it is a symmetric diagonal scaling
        // D·P₀·D — but the self-potential of a patch grows only as the inverse of its LINEAR size,
        // not its area, so a 245x area reduction buys a ~16x diagonal entry, not a 245x one. The
        // 1/Area blow-up the rule is written against is largely cancelled by the kernel's own scaling.
        //
        // So the assertion is the honest one: merging must not make conditioning WORSE. The claim
        // that it rescues it is not made, because nothing here found a case where it needed rescuing.
        Assert.True(shipped.Kappa <= never.Kappa * 1.001,
            $"merging at {SurfaceMesher.DefaultSliverAreaFraction:F2} gave κ = {shipped.Kappa:E4} " +
            $"against {never.Kappa:E4} with merging off — it must not make conditioning worse");

        // (3) AND THE ANSWER DOES NOT MOVE. Merging is a discretisation remedy, not a modelling
        // change: R-cut-1 still holds cell-for-cell (the merged cell carries BOTH pieces, so no area
        // is lost — G5b asserts that directly), so the capacitance must be essentially unchanged.
        // If this ever fails, merging has started losing area and G5b is the gate to look at.
        double moved = Math.Abs(shipped.C - never.C) / Math.Abs(never.C);
        Assert.True(moved < 5e-3,
            $"merging moved the static capacitance by {moved:P4} — it must not change the structure " +
            "being solved");
    }

    /// <summary>
    /// <b>HOW THIN DOES A SLIVER ACTUALLY GET?</b> — the question the sweep above raised and could
    /// not answer, because one mesh density produces one sliver population.
    ///
    /// <para>Meshing is sub-millisecond, so this scans a wide range of densities with merging OFF and
    /// reports the worst area fraction any of them produced. That is the number the threshold has to
    /// be set against: if the mesher never produces a sliver below a few percent, then 0.05 is not
    /// sitting near a conditioning cliff and the constant is conservative rather than critical —
    /// which is a materially different claim from the one the shipped doc comment used to make.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void G5c_HowThinASliverTheMesherActuallyProduces_AcrossDensities()
    {
        var disc = Disc();
        double worst = 1.0;
        int worstCpw = 0;

        _out.WriteLine("  cells/λ   cells   cut    min area/rect");
        for (int cpw = 40; cpw <= 400; cpw += 10)
        {
            var r = SurfaceMesher.Mesh(disc, Settings(cpw), sliverAreaFraction: 0.0);
            if (!r.CanSolve) continue;

            double minFrac = 1.0;
            foreach (var c in r.Mesh.Cells)
            {
                double rect = c.Width * c.Height;
                if (rect > 0) minFrac = Math.Min(minFrac, c.Area / rect);
            }
            if (minFrac < worst) { worst = minFrac; worstCpw = cpw; }

            if (cpw % 50 == 0 || minFrac < 5e-3)
                _out.WriteLine($"  {cpw,7}  {r.Mesh.Cells.Count,6}  {r.CutCellCount,4}    {minFrac:E3}");
        }

        _out.WriteLine($"[VERDICT] over cells/λ = 40 … 400 with merging OFF, the thinnest cut cell any " +
                       $"density produced is {worst:E3} of its grid rectangle (at cells/λ = {worstCpw}), " +
                       $"against a threshold of {SurfaceMesher.DefaultSliverAreaFraction:F2}.");

        // No assertion on the VALUE — this is a measurement, and pinning it would pin the mesher's
        // own gridline placement, which is not what it is about. The one thing asserted is that the
        // scan genuinely found cut cells, so the number above is not the empty minimum.
        Assert.True(worst < 1.0, "no density in the scan produced a cut cell at all");
    }

    /// <summary>
    /// R-cut-1 survives the merge, asserted directly rather than inferred from the capacitance.
    /// A merged cell carries TWO pieces, so the union of the cells is still the drawn polygon.
    /// </summary>
    [Fact]
    public void G5b_MergingNeverLosesArea_AtAnyThreshold()
    {
        var disc = Disc();
        double drawn = 0.5 * DiscPoints * DiscRadiusM * DiscRadiusM * Math.Sin(2.0 * Math.PI / DiscPoints);

        foreach (double frac in new[] { 0.0, 0.05, 0.20 })
        {
            var r = SurfaceMesher.Mesh(disc, Settings(70), sliverAreaFraction: frac);
            double err = Math.Abs(r.MeshedAreaM2 - drawn) / drawn;
            _out.WriteLine($"  frac {frac:F2}: merged = {r.MergedSliverCount,3}, " +
                           $"tiling error vs the drawn artwork = {err:E3}");
            Assert.True(err < 1e-12,
                $"at threshold {frac:F2} the mesh left {err:E3} of the drawn polygon untiled — " +
                "the merge is losing area, which trades R-cut-3 against R-cut-1");
        }
    }
}
