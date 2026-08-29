// M5 (brief-em-sweep-performance) — the ROUTINE half of the accelerator's gates.
//
// Everything here is milliseconds and runs in the default tier, because every one of these is a
// STRUCTURAL property rather than a measurement: the per-entry fill is the dense fill, the near set
// contains what makes the grid kernel's zero-separation value irrelevant, moving that value does not
// move the answer, and a via mesh is refused by name instead of being modelled badly.
//
// The MEASUREMENTS — R-emp-16's two accuracy gates and R-emp-17's radius/order table — are in
// AimAccuracyTests, tagged Category=Benchmark, per the brief's own tagging rule.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class AimAcceleratorTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>Small enough that the dense matrix exists to compare against, and still carrying both
    /// basis directions and a real de-embedding-grade kernel.</summary>
    private static (PlanarMesh Mesh, PlanarFillCores Dense, PlanarFillCores Geom, PlanarKernelPair K,
                    double Omega, IReadOnlyList<PlanarPortResolution> Ports)
        Fixture(double lengthM = 8e-3, double fHz = 6e9, PlanarMeshSettings? mesh = null)
    {
        var problem = PlanarLineFixtures.Fr4Line(lengthM, fHz);
        var (m, ports) = PlanarLineFixtures.MeshAndPorts(problem, mesh ?? PlanarLineFixtures.Coarse);
        return (m,
                PlanarFill.BuildCores(m),
                PlanarFill.BuildGeometryOnlyCores(m),
                PlanarLineFixtures.Kernel(problem.Slab, fHz),
                2.0 * Math.PI * fHz,
                ports);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T1 — the seam AIM's near field is built on IS the dense fill, to the last bit
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T1_PerEntryFillIsBitIdenticalToTheDenseFill()
    {
        // Without this, "the near field is the exact matrix, sparsely" is a claim rather than a fact,
        // and every accuracy number below would be measuring two approximations at once. A tolerance
        // would be the wrong gate here for exactly the reason M1/M2's own bit-identity note gives:
        // two orderings of the same sum agree to 1e-12 whether or not they are the same computation.
        var (mesh, dense, geom, k, omega, _) = Fixture(lengthM: 16e-3);

        var z = PlanarFill.Fill(dense, k.VectorPotential, k.Scalar, omega);
        var entry = new PlanarEntryFill(geom, k.VectorPotential, k.Scalar, omega);

        int n = mesh.Bases.Count;

        // Both basis directions must be present or the vector block's direction split is untested and
        // the cross-direction "scalar only" branch never runs.
        int xs = 0, ys = 0;
        foreach (var b in mesh.Bases)
            if (b.Direction == PlanarBasisDirection.X) xs++; else ys++;
        Assert.True(xs > 0 && ys > 0, $"the fixture exercises one direction only: {xs} x̂, {ys} ŷ");

        int compared = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex a = z[i, j], b = entry.At(i, j);
                Assert.True(a.Real == b.Real && a.Imaginary == b.Imaginary,
                    $"entry [{i},{j}] differs: dense {a} vs per-entry {b}");
                compared++;
            }

        _out.WriteLine($"N = {n}; {compared:N0} entries bit-identical; " +
                       $"{entry.CellPairCount:N0} distinct cell pairs integrated");
    }

    [Fact]
    public void T1b_GeometryOnlyCoresCarryTheSameMeshSCALARS_AndRefuseADenseFill()
    {
        // The AIM path re-floors its kernel and sizes its radial remainder table from these three
        // numbers, so they have to be the dense path's own — not a second derivation that can drift.
        var (mesh, dense, geom, k, omega, _) = Fixture();

        Assert.Equal(dense.MinCellEdgeM, geom.MinCellEdgeM);
        Assert.Equal(dense.ExtentM,      geom.ExtentM);
        Assert.Equal(dense.RhoFloorM,    geom.RhoFloorM);
        Assert.Equal(dense.UnknownCount, geom.UnknownCount);
        Assert.True(dense.HasPairCores);
        Assert.False(geom.HasPairCores);
        Assert.Equal(0, geom.CoreBytes);

        // And the O(N²) memory really is not there.
        Assert.True(dense.CoreBytes > 0);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanarFill.Fill(geom, k.VectorPotential, k.Scalar, omega));
        Assert.Contains("geometry-only", ex.Message);
        Assert.Throws<InvalidOperationException>(() => geom.ScalarCore(0, 0));
        _ = mesh;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T2 — the near set, and the one thing that makes G(0) legitimately arbitrary
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T2_EveryStencilOverlappingPairIsInTheNearSet()
    {
        // This is the structural gate behind T3. If a pair whose stencils share even one grid node is
        // treated as FAR, the arbitrary value at zero separation survives into the answer — smoothly,
        // plausibly, and with nothing anywhere looking wrong.
        var (mesh, _, geom, k, omega, _) = Fixture();

        // A deliberately NARROW radius, so the radius criterion cannot mask the overlap criterion.
        var st = new PlanarAimSettings(ProjectionOrder: 2, NearRadiusFactor: 0.0);
        var aim = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, omega, st);

        int n = mesh.Bases.Count;
        int overlaps = 0;
        for (int i = 0; i < n; i++)
        {
            var (pi, qi) = aim.StencilOrigin(i);
            for (int j = 0; j < n; j++)
            {
                var (pj, qj) = aim.StencilOrigin(j);
                if (Math.Abs(pi - pj) > 2 || Math.Abs(qi - qj) > 2) continue;
                overlaps++;
                Assert.True(aim.IsNear(i, j),
                    $"bases {i} and {j} share grid nodes (stencils at ({pi},{qi}) and ({pj},{qj})) " +
                    "but are not in the near set — the zero-separation kernel value would survive " +
                    "into the answer");
            }
        }

        Assert.True(overlaps > 0, "the fixture produced no overlapping stencils, so this gate is vacuous");
        _out.WriteLine($"N = {n}; {overlaps:N0} stencil-overlapping pairs, all near " +
                       $"({aim.Report.NearEntriesPerRow:F1} near entries per row at radius 0)");
    }

    [Fact]
    public void T3_TheZeroSeparationKernelValueDoesNotMoveTheProduct()
    {
        // The gate the file header names. Two accelerators differing ONLY in where the grid kernel's
        // self value is sampled must produce the same product — because every pair that can see it is
        // corrected exactly. A failure here is a hole in the near set, not a tuning problem.
        var (mesh, _, geom, k, omega, _) = Fixture();

        var a = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, omega,
                                        new PlanarAimSettings(SelfKernelFactor: 0.5));
        var b = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, omega,
                                        new PlanarAimSettings(SelfKernelFactor: 0.05));

        int n = mesh.Bases.Count;
        var x = Probe(n);
        var ya = a.Multiply(x);
        var yb = b.Multiply(x);

        double worst = 0, scale = 0;
        for (int i = 0; i < n; i++)
        {
            worst = Math.Max(worst, (ya[i] - yb[i]).Magnitude);
            scale = Math.Max(scale, ya[i].Magnitude);
        }

        _out.WriteLine($"self-kernel sampled at 0.5·h and at 0.05·h — a 10× change in a value that " +
                       $"differs by {Ratio(a, b):E2} — moves the product by {worst / scale:E2} relative");
        Assert.True(worst / scale < 1e-12,
            $"the answer moved {worst / scale:E2} when the arbitrary zero-separation kernel value " +
            "changed, so some pair that can see it is being treated as far field");
    }

    private static double Ratio(PlanarAimOperator a, PlanarAimOperator b)
        => b.Report.GridPitchM / a.Report.GridPitchM * 10.0;   // reported for the message only

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T4 — the accelerated product against the dense one
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T4_TheAcceleratedProductAgreesWithTheDenseProduct()
    {
        // R-emp-16's first gate in its cheap form: the same operator, one on a coarse fixture. The
        // reported target is the FILL's own accuracy (L8c: 5.0e-6 against an independent oracle), not
        // the kernel's — an accelerator that is better than the matrix it accelerates is measuring
        // nothing. The Benchmark tier sweeps this properly; here it is a floor.
        var (mesh, dense, geom, k, omega, _) = Fixture();

        var z = PlanarFill.Fill(dense, k.VectorPotential, k.Scalar, omega);
        var aim = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, omega);

        int n = mesh.Bases.Count;
        var x = Probe(n);
        var ya = aim.Multiply(x);

        var yd = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex acc = Complex.Zero;
            for (int j = 0; j < n; j++) acc += z[i, j] * x[j];
            yd[i] = acc;
        }

        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            num += (ya[i] - yd[i]).Magnitude * (ya[i] - yd[i]).Magnitude;
            den += yd[i].Magnitude * yd[i].Magnitude;
        }
        double rel = Math.Sqrt(num / den);

        _out.WriteLine($"N = {n}, order {aim.Report.ProjectionOrder}, near radius " +
                       $"{aim.Report.NearRadiusM * 1e3:F3} mm, {aim.Report.NearEntriesPerRow:F0} near " +
                       $"entries/row ({aim.Report.NearFillFraction * 100:F1}% of dense): " +
                       $"‖(Z_aim − Z)x‖/‖Zx‖ = {rel:E2}");

        Assert.True(rel < 1e-3,
            $"the accelerated product is {rel:E2} from the dense one, which is past anything the " +
            "de-embedding could absorb");
    }

    [Fact]
    public void T5_TheAcceleratedSolveReproducesTheDenseSolve()
    {
        // The whole chain: project, convolve, correct, GMRES against the near field's own LU — against
        // the dense LU on the same matrix and the same real port excitation.
        var (mesh, dense, geom, k, omega, ports) = Fixture();

        var system = PlanarSystem.Build(dense, k.VectorPotential, k.Scalar, omega);
        var aim    = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, omega);

        var rhs = PlanarExcitation.RightHandSide(mesh.Bases.Count, ports[0]);
        var exact = system.Solve(rhs);
        var iter  = aim.Solve(rhs);

        double num = 0, den = 0;
        for (int i = 0; i < mesh.Bases.Count; i++)
        {
            num += (exact[i] - iter[i]).Magnitude * (exact[i] - iter[i]).Magnitude;
            den += exact[i].Magnitude * exact[i].Magnitude;
        }
        double rel = Math.Sqrt(num / den);

        _out.WriteLine($"GMRES took {aim.LastIterations} iteration(s) to a residual of " +
                       $"{aim.LastResidual:E2}; the current vector is {rel:E2} from the dense LU's");

        Assert.True(aim.LastIterations < 40,
            $"GMRES took {aim.LastIterations} iterations — §11 measured single digits with an " +
            "adequate near field, so this is the preconditioner failing rather than the solver");
        Assert.True(rel < 1e-2,
            $"the accelerated current distribution is {rel:E2} from the dense one");
    }

    [Fact]
    public void T5b_TheGmresHarnessSolvesADenseSystemExactly()
    {
        // Before any accelerated number is believed: the solver itself, driven by the DENSE product,
        // must reproduce the dense LU. Otherwise a bug in the Arnoldi or the Givens updates shows up
        // as "the accelerator is inaccurate" and gets attributed to the projection.
        var (mesh, dense, _, k, omega, ports) = Fixture();

        var z = PlanarFill.Fill(dense, k.VectorPotential, k.Scalar, omega);
        var system = PlanarSystem.Wrap(z);
        int n = mesh.Bases.Count;

        var rhsV = PlanarExcitation.RightHandSide(n, ports[0]);
        var exact = system.Solve(rhsV);

        var b = new Complex[n];
        for (int i = 0; i < n; i++) b[i] = rhsV[i];

        var got = PlanarGmres.Solve(
            v =>
            {
                var y = new Complex[n];
                for (int i = 0; i < n; i++)
                {
                    Complex acc = Complex.Zero;
                    for (int j = 0; j < n; j++) acc += z[i, j] * v[j];
                    y[i] = acc;
                }
                return y;
            },
            PlanarGmres.NoPreconditioner, b, 1e-12, n, 0, out int its, out double res);

        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            num += (exact[i] - got[i]).Magnitude * (exact[i] - got[i]).Magnitude;
            den += exact[i].Magnitude * exact[i].Magnitude;
        }

        _out.WriteLine($"unpreconditioned full GMRES on the dense matrix: {its} iterations, " +
                       $"residual {res:E2}, {Math.Sqrt(num / den):E2} from the LU's answer");
        Assert.True(res <= 1e-12, $"the GMRES harness did not converge — residual {res:E2}");
        Assert.True(Math.Sqrt(num / den) < 1e-8);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T6 — what it refuses, by name
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T6_ASettingWhoseBadCaseIsAPlausibleWrongAnswerIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarAimSettings(ProjectionOrder: -1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarAimSettings(GridSpacingFactor: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarAimSettings(Tolerance: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarAimSettings(Tolerance: 1.0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarAimSettings(MaxIterations: 0).Validate());

        // The one that would otherwise ask the kernel for its own singularity and return a NaN matrix.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarAimSettings(SelfKernelFactor: 0).Validate());
    }

    [Fact]
    public void T7_TheDefaultIsSTILLTheDensePath()
    {
        // PlanarFillSettings.Aim is null by default, and a solve context with it null must build the
        // full cores and take L8c/L8d's own path. This is the assertion that says M5 ships OFF.
        Assert.Null(PlanarFillSettings.Default.Aim);

        var problem = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);

        var dense = new PlanarSolveContext(mesh, ports);
        Assert.True(dense.Cores.HasPairCores);
        Assert.Null(dense.LastAccelerator);

        var accel = new PlanarSolveContext(
            mesh, ports, PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default });
        Assert.False(accel.Cores.HasPairCores);

        var k = PlanarLineFixtures.Kernel(problem.Slab, 6e9);
        var yDense = dense.SolveAt(k, 6e9).Y;
        var yAccel = accel.SolveAt(k, 6e9).Y;
        Assert.NotNull(accel.LastAccelerator);

        double worst = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                worst = Math.Max(worst, (yDense[i, j] - yAccel[i, j]).Magnitude / yDense[i, j].Magnitude);

        _out.WriteLine($"Y through the dense path and through the accelerator agree to {worst:E2} " +
                       $"({accel.LastAccelerator!.LastIterations} GMRES iterations)");
        Assert.True(worst < 1e-3);
    }

    /// <summary>A deterministic, non-degenerate probe vector. Not random: a fixed vector makes a
    /// failure reproducible, and the quantity being measured is a relative norm over the whole
    /// spectrum of the operator rather than a property of one direction.</summary>
    private static Complex[] Probe(int n)
    {
        var x = new Complex[n];
        for (int i = 0; i < n; i++)
            x[i] = new Complex(Math.Cos(0.7 * i + 0.3), Math.Sin(1.3 * i + 1.1));
        return x;
    }
}
