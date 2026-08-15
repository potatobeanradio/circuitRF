// brief-em-deembed-ceiling-closeout.md — C2 and C3. This closes what
// brief-em-aim-ceiling.md's own §13 subsection ("The limitation this surfaced") left open: the
// de-embedding-only defects, not the accelerator itself. See src/Engine/Mom/CLAUDE.md and
// HISTORY.md's AIM-ceiling closing subsection for the parent finding this builds on.
//
// C2 — PlanarFill.BuildCores' own shared GuardCeiling asks about mesh.Bases.Count and quotes an
// n×n dense COMPLEX MATRIX, which is what its OTHER callers (PlanarFill.Fill / PlanarSystem.Build)
// go on to allocate. PlanarDeembed.StaticCapacitance never does — it allocates TWO m×m complex
// matrices over CELLS instead. This file measures m against n on a real calibration standard (not
// assumed), and gates the corrected guard/message PlanarDeembed.GuardCapacitanceCeiling now
// carries at that one call site.
//
// C3 — is the ω → 0 static scalar system genuinely REAL (so StaticCapacitance could move to
// Mat<double> + a symmetric factorisation, halving its memory)? Measured here, not assumed:
// PlanarKernelTerms.StaticScalar's own Inverse/Constant come from a complex image ratio
// k = (1 − εᵣ*)/(1 + εᵣ*), and εᵣ* = εᵣ(1 − j·tanδ) is complex on every lossy substrate this
// repository ships a starter for. The result is a NEGATIVE finding, recorded rather than acted on.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class EmDeembedCeilingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // C2 — the guard at StaticCapacitance's call site
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REAL calibration standard, not a synthetic square grid — the shape D4 actually builds
    /// (long in the direction of propagation, few cells across the port's own width), so the
    /// measured m/n ratio is the one a de-embedded run actually sees.
    /// </summary>
    [Fact]
    public void C2_MVsN_OnARealCalibrationStandard_IsMeasuredNotAssumed()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        int endRun = PlanarCalibration.EndRunCellsFor(ports[0], problem.Slab);
        var std = PlanarCalibration.BuildLine(ports[0], 4e-3, endRun);

        int n = std.Mesh.Bases.Count, m = std.Mesh.Cells.Count;
        double ratio = (double)m / n;
        _out.WriteLine($"real standard: m = {m} cells, n = {n} bases, m/n = {ratio:F3}");

        // The claim CLAUDE.md's §L8b note makes generally ("N counts basis functions... ~2x
        // cells"), pinned on an ACTUAL standard rather than left as a round number: m is always
        // strictly under n for any grid with more than one cell on an axis, and nowhere near it.
        Assert.True(m < n, $"a mesh's cell count must be under its basis count; got m={m}, n={n}");
        Assert.InRange(ratio, 0.45, 0.95);
    }

    /// <summary>Cheap: a uniform grid built directly (no metal, no fill) so the guard's OWN
    /// message can be exercised without paying for an O(N²) fill that would never complete.</summary>
    private static PlanarMesh BuildUniformGrid(int nx, int ny, double cellSize)
    {
        var gx = new double[nx + 1];
        var gy = new double[ny + 1];
        for (int i = 0; i <= nx; i++) gx[i] = i * cellSize;
        for (int i = 0; i <= ny; i++) gy[i] = i * cellSize;

        var cells = new List<PlanarCell>(nx * ny);
        var at = new int[nx * ny];
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                at[iy * nx + ix] = cells.Count;
                cells.Add(new PlanarCell(0, ix, iy, gx[ix], gy[iy], gx[ix + 1], gy[iy + 1]));
            }

        var bases = new List<PlanarBasis>();
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                if (ix + 1 < nx)
                    bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[iy * nx + ix + 1], PlanarBasisDirection.X));
                if (iy + 1 < ny)
                    bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[(iy + 1) * nx + ix], PlanarBasisDirection.Y));
            }

        return new PlanarMesh(cells, bases, ["Metal"], gx, gy);
    }

    [Fact]
    public void C2_TheGuardAtThisCallSite_QuotesCellsAndTheRealMegabytes()
    {
        // Chosen so m sits UNDER the dense ceiling while n sits well past it — the exact
        // mismatch C2 exists to fix. The refusal decision is unchanged (it still asks about n,
        // per the brief's own instruction not to change the shared threshold); only the message
        // a caller reaching it through StaticCapacitance sees is different.
        var mesh = BuildUniformGrid(100, 45, 1e-4);
        int n = mesh.Bases.Count, m = mesh.Cells.Count;
        Assert.True(n > SurfaceMesher.UnknownCeiling, $"fixture must exceed the ceiling; got n={n}");
        Assert.True(m < SurfaceMesher.UnknownCeiling, $"fixture's point needs m under ceiling; got m={m}");

        var terms = PlanarKernelTerms.StaticScalar(GroundedSlab.Fr4Starter);
        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanarDeembed.StaticCapacitance(mesh, terms));

        double realMb = 2.0 * m * (double)m * 16.0 / (1024 * 1024);
        double sharedGuardMb = (double)n * n * 16.0 / (1024 * 1024);
        _out.WriteLine($"m = {m:N0}, n = {n:N0} — this call site's real MB = {realMb:F1}, " +
                       $"the shared n×n guard's own MB would have been {sharedGuardMb:F1}");

        Assert.Contains($"{m:N0} cells", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{realMb:N0} MB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("two m×m complex matrices", ex.Message, StringComparison.Ordinal);
        Assert.True(realMb < sharedGuardMb,
            "the whole point: this call site's real working set is smaller than the shared " +
            "guard's n×n proxy would claim");
    }

    /// <summary>The megabytes formula corroborated against an actual measured allocation, not
    /// left as arithmetic alone — the same "counted, not profiled" standard this area already
    /// uses (AimCeilingTests' own header comment).</summary>
    [Fact]
    public void C2_TheFormula_IsCorroboratedByAMeasuredAllocation()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        int endRun = PlanarCalibration.EndRunCellsFor(ports[0], problem.Slab);
        var std = PlanarCalibration.BuildLine(ports[0], 4e-3, endRun);
        var terms = PlanarKernelTerms.StaticScalar(problem.Slab);

        GC.Collect();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        double c = PlanarDeembed.StaticCapacitance(std.Mesh, terms);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _out.WriteLine($"m = {std.Mesh.Cells.Count}, n = {std.Mesh.Bases.Count}: measured " +
                       $"{allocated / 1048576.0:F2} MB allocated for this call (C_total = {c:E3} F)");
        Assert.True(allocated > 0, "the call must actually allocate something measurable");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // C3 — is the ω → 0 static scalar system real?
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void C3_TheStaticScalarKernel_HasAMateriallyNonzeroImaginaryPart_OnALossySlab()
    {
        // Fr4Starter: TanD = 0.02. If this were only floating-point noise, C3's representation
        // win (Mat<double> + a symmetric factorisation, halving StaticCapacitance's memory) would
        // be free. It is not: k = (1 − εᵣ*)/(1 + εᵣ*) is complex whenever εᵣ* is, and εᵣ* is
        // complex for any TanD > 0 — which both starters in this repository ship with.
        var fr4 = PlanarKernelTerms.StaticScalar(GroundedSlab.Fr4Starter);
        double fr4Ratio = Math.Abs(fr4.Inverse.Imaginary / fr4.Inverse.Real);
        _out.WriteLine($"StaticScalar(Fr4Starter).Inverse = {fr4.Inverse}, |Im/Re| = {fr4Ratio:E3}");
        Assert.True(fr4Ratio > 1e-3,
            $"expected a MATERIAL imaginary part from TanD = 0.02, not floating-point noise; got {fr4Ratio:E3}");

        // GaAs's TanD is ~10x smaller (0.002) but still nonzero — same finding, smaller magnitude.
        var gaas = PlanarKernelTerms.StaticScalar(GroundedSlab.GaAsStarter);
        double gaasRatio = Math.Abs(gaas.Inverse.Imaginary / gaas.Inverse.Real);
        _out.WriteLine($"StaticScalar(GaAsStarter).Inverse = {gaas.Inverse}, |Im/Re| = {gaasRatio:E3}");
        Assert.True(gaasRatio > 1e-5,
            $"expected a nonzero imaginary part on GaAs too, just smaller; got {gaasRatio:E3}");
    }

    /// <summary>
    /// The same finding, carried all the way through a real solve — the quantity
    /// <c>PlanarDeembed.StaticCapacitance</c> actually discards via its own <c>.Real</c>, on a
    /// real calibration standard. This duplicates that method's own arithmetic up to the final
    /// truncation (rather than changing its signature) specifically to inspect what is thrown
    /// away, which D7's algebra is out of scope to touch here.
    /// </summary>
    [Fact]
    public void C3_TheDiscardedImaginaryPart_IsMaterial_OnARealCalibrationStandard()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        int endRun = PlanarCalibration.EndRunCellsFor(ports[0], problem.Slab);
        var std = PlanarCalibration.BuildLine(ports[0], 4e-3, endRun);

        var st    = PlanarFillSettings.Default;
        var cores = PlanarFill.BuildCores(std.Mesh, st);
        var terms = PlanarKernelTerms.StaticScalar(problem.Slab);
        var p     = PlanarFill.ScalarPotentialMatrix(cores, terms.With(st.Order, cores.RhoFloorM));

        int m = std.Mesh.Cells.Count;
        var a   = new Mat<Complex>(m, m);
        var rhs = new Vec<Complex>(m);
        for (int i = 0; i < m; i++)
        {
            rhs[i] = Complex.One;
            for (int j = 0; j < m; j++) a[i, j] = p[i, j] / EmConstants.Eps0;
        }
        var q = a.Lu().Solve(rhs);
        Complex total = Complex.Zero;
        for (int i = 0; i < m; i++) total += q[i];

        double ratio = Math.Abs(total.Imaginary / total.Real);
        _out.WriteLine($"total (before StaticCapacitance's own .Real) = {total}, " +
                       $"discarded |Im/Re| = {ratio:E3}");
        Assert.True(ratio > 1e-4,
            "StaticCapacitance discards this every call; C3 needs it measured, not assumed away");
    }
}
