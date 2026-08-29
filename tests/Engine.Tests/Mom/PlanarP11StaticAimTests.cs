// P11 (brief-em-p11-accelerated-static-capacitance.md) — the accelerated static capacitance solve.
//
// The gate is 1e-6 RELATIVE against the dense solve, and the quantity it is asked of is C_pul —
// `(C₂ − C₁)/Δℓ` — not either total, because that is what D7 actually references the published
// s-parameters to and because the differencing amplifies whatever error is in the totals. The
// fixtures are deliberately the COARSE calibration standards: every claim here is about the
// OPERATOR agreeing with the dense operator on the same mesh, which a coarse mesh tests exactly as
// hard as a converged one and in a fraction of the time. The physical-accuracy oracles are
// PlanarStaticLimitTests' own, and this file runs the two of them that are cheap enough for the
// routine gate through the accelerated path as well.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarP11StaticAimTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>The gate the brief names: 1e-6 relative against the dense solve.</summary>
    private const double Gate = 1e-6;

    private static readonly PlanarAimSettings Aim  = PlanarAimSettings.Default;
    private static readonly PlanarFillSettings Fill = PlanarFillSettings.Default with { Aim = Aim };

    /// <summary>A uniform nx × ny mesh of a w × h rectangle centred on the origin — the same
    /// harness <c>PlanarStaticLimitTests</c> builds its oracles on.</summary>
    private static PlanarMesh Uniform(double w, double h, int nx, int ny)
    {
        var gx = new double[nx + 1];
        var gy = new double[ny + 1];
        for (int i = 0; i <= nx; i++) gx[i] = -0.5 * w + w * i / nx;
        for (int j = 0; j <= ny; j++) gy[j] = -0.5 * h + h * j / ny;
        return PlanarFillTests.Grid(gx, gy);
    }

    /// <summary>The two calibration standards a de-embedded run of a real line actually builds.</summary>
    private static (PlanarStandard Short, PlanarStandard Long, GroundedSlab Slab) Standards(
        double shortM = 12e-3, double longM = 30e-3)
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        int endRun = PlanarCalibration.EndRunCellsFor(ports[0], problem.Slab);
        return (PlanarCalibration.BuildLine(ports[0], shortM, endRun),
                PlanarCalibration.BuildLine(ports[0], longM,  endRun),
                problem.Slab);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M1 — the operator
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The accelerated product IS <c>P x</c>. Asked of the operator rather than only of the solved
    /// capacitance, because a solve can absorb a product error into a slightly different charge
    /// vector whose SUM still looks right — and the sum is the only thing the capacitance reads.
    /// </summary>
    [Fact]
    public void M1_TheAcceleratedProduct_IsTheDenseScalarPotentialMatrix()
    {
        var slab  = GroundedSlab.Fr4Starter;
        var terms = PlanarKernelTerms.StaticScalar(slab);
        var mesh  = Uniform(8e-3, 2e-3, 16, 4);

        var dense = PlanarFill.ScalarPotentialMatrix(
            PlanarFill.BuildCores(mesh, PlanarFillSettings.Default),
            terms.With(PlanarFillSettings.Default.Order,
                       PlanarFill.BuildCores(mesh, PlanarFillSettings.Default).RhoFloorM));

        var aim = PlanarStaticAim.Build(PlanarFill.BuildGeometryOnlyCores(mesh, Fill),
                                        terms, slab.HeightM, Aim);

        int m = mesh.Cells.Count;
        var rng = new Random(20260829);
        var x = new Complex[m];
        for (int i = 0; i < m; i++) x[i] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);

        var got = aim.Multiply(x);

        double num = 0, den = 0;
        for (int i = 0; i < m; i++)
        {
            Complex want = Complex.Zero;
            for (int j = 0; j < m; j++) want += dense[i, j] * x[j];
            num = Math.Max(num, (got[i] - want).Magnitude);
            den = Math.Max(den, want.Magnitude);
        }

        _out.WriteLine($"m = {m}: worst |Δ(Px)| = {num:E3} against ‖Px‖_max = {den:E3} " +
                       $"⇒ {num / den:E2} relative; near/row {aim.Report.NearEntriesPerRow:F0}, " +
                       $"fill {aim.Report.NearFillFraction:P1}");
        Assert.True(num / den < Gate,
            $"the accelerated product is {num / den:E2} from the dense matrix-vector product");
    }

    /// <summary>
    /// The grid kernel's value at zero separation is ARBITRARY, and it is only legitimate for it to
    /// be arbitrary because every pair whose stencils overlap is in the near set, where the AIM
    /// value is subtracted off exactly. Moving the sentinel must not move the answer — the same gate
    /// <c>AimAccuracyTests</c> runs on M5's own operator, asked of this one.
    /// </summary>
    [Fact]
    public void M1_TheSelfKernelSentinel_DoesNotMoveTheCapacitance()
    {
        var slab  = GroundedSlab.Fr4Starter;
        var terms = PlanarKernelTerms.StaticScalar(slab);
        var mesh  = Uniform(8e-3, 2e-3, 16, 4);

        double Run(double factor)
        {
            var st = Aim with { SelfKernelFactor = factor };
            return PlanarStaticAim.Build(
                PlanarFill.BuildGeometryOnlyCores(mesh, PlanarFillSettings.Default with { Aim = st }),
                terms, slab.HeightM, st).TotalCapacitance();
        }

        double a = Run(0.5), b = Run(0.05), c = Run(2.0);
        _out.WriteLine($"self-kernel at 0.5 / 0.05 / 2.0 pitch: {a:E12} / {b:E12} / {c:E12} F");

        Assert.True(Math.Abs(b - a) / a < 1e-12, $"sentinel 0.05 moved the answer by {Math.Abs(b - a) / a:E2}");
        Assert.True(Math.Abs(c - a) / a < 1e-12, $"sentinel 2.0 moved the answer by {Math.Abs(c - a) / a:E2}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M2 — the gate against the dense solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The brief's own gate.</b> C_pul on a real pair of calibration standards, accelerated
    /// against dense, to 1e-6 relative — and the amplification factor <c>C₂/(C₂ − C₁)</c> reported
    /// beside it, because that is what turns an error in either total into an error in the answer.
    /// </summary>
    [Fact]
    public void M2_CapacitancePerMetre_AgreesWithTheDenseSolve()
    {
        var (shortStd, longStd, slab) = Standards();

        double dense = PlanarDeembed.CapacitancePerMetre(shortStd, longStd, slab);
        double acc   = PlanarDeembed.CapacitancePerMetre(shortStd, longStd, slab, Fill);

        var terms = PlanarKernelTerms.StaticScalar(slab);
        double c1 = PlanarDeembed.StaticCapacitance(shortStd.Mesh, terms);
        double c2 = PlanarDeembed.StaticCapacitance(longStd.Mesh,  terms);

        double rel = Math.Abs(acc - dense) / Math.Abs(dense);
        _out.WriteLine($"m = {shortStd.Mesh.Cells.Count} / {longStd.Mesh.Cells.Count} cells; " +
                       $"C_pul dense {dense:E12}, accelerated {acc:E12} ⇒ {rel:E2} relative " +
                       $"(differencing amplification C₂/(C₂−C₁) = {c2 / (c2 - c1):F2})");
        Assert.True(rel < Gate, $"accelerated C_pul is {rel:E2} from the dense solve");
    }

    /// <summary>Each TOTAL separately, so a failure says which standard moved rather than only that
    /// the difference did.</summary>
    [Fact]
    public void M2_EachStandardsTotalCapacitance_AgreesWithTheDenseSolve()
    {
        var (shortStd, longStd, slab) = Standards();
        var terms = PlanarKernelTerms.StaticScalar(slab);

        foreach (var (name, std) in new[] { ("short", shortStd), ("long", longStd) })
        {
            double dense = PlanarDeembed.StaticCapacitance(std.Mesh, terms);
            double acc   = PlanarDeembed.StaticCapacitance(std.Mesh, terms, Fill,
                                                           cores: null, slabHeightM: slab.HeightM);
            double rel = Math.Abs(acc - dense) / Math.Abs(dense);
            _out.WriteLine($"{name,-5} standard (m = {std.Mesh.Cells.Count}): dense {dense:E12} F, " +
                           $"accelerated {acc:E12} F ⇒ {rel:E2}");
            Assert.True(rel < Gate, $"the {name} standard's total is {rel:E2} from the dense solve");
        }
    }

    /// <summary>
    /// <b>The GMRES tolerance ladder the brief asks for</b>, kept as a test rather than a note so the
    /// shipped default's justification is re-run rather than remembered: the differenced answer must
    /// stop moving above <see cref="PlanarAimSettings.StaticTolerance"/>'s default, which is what
    /// says the tolerance is not what limits this and the projection is.
    /// </summary>
    [Fact]
    public void M2_TheDifferencedAnswer_HasStoppedMovingAtTheShippedTolerance()
    {
        var (shortStd, longStd, slab) = Standards();
        double dense = PlanarDeembed.CapacitancePerMetre(shortStd, longStd, slab);

        var errors = new List<(double Tol, double Rel)>();
        foreach (double tol in new[] { 1e-6, 1e-8, PlanarAimSettings.Default.StaticTolerance })
        {
            var fill = PlanarFillSettings.Default with { Aim = Aim with { StaticTolerance = tol } };
            double acc = PlanarDeembed.CapacitancePerMetre(shortStd, longStd, slab, fill);
            errors.Add((tol, Math.Abs(acc - dense) / Math.Abs(dense)));
            _out.WriteLine($"StaticTolerance {tol:E0}: C_pul is {errors[^1].Rel:E3} from the dense solve");
        }

        // The last two must agree to far better than the gate — i.e. the residual is no longer what
        // is being measured. If this ever fails, the tolerance IS the limiter and the default has to
        // be re-measured rather than the assertion relaxed.
        double moved = Math.Abs(errors[^1].Rel - errors[^2].Rel);
        _out.WriteLine($"the last decade of tolerance moved the error by {moved:E2}");
        Assert.True(moved < 0.05 * Gate,
            $"tightening the tolerance a decade moved the differenced answer by {moved:E2} — the " +
            "solve residual, not the projection, is what limits this and StaticTolerance's default " +
            "needs re-measuring");
        Assert.All(errors, e => Assert.True(e.Rel < Gate, $"tolerance {e.Tol:E0} gave {e.Rel:E2}"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M2 — the static oracles, through the accelerated path
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>PlanarStaticLimitTests.T5_1</c>'s oracle — a plate over ground approaches ε₀A/h from above
    /// and the fringing excess falls with h/W — asked of the ACCELERATED solve. εᵣ = 1, so the slab
    /// contributes exactly one image and the oracle stays closed form.
    /// </summary>
    [Fact]
    public void M2_APlateOverGround_ConvergesToEpsilonZeroAOverH_ThroughTheAcceleratedPath()
    {
        const double w = 1e-3;
        double area = w * w;
        var mesh = Uniform(w, w, 10, 10);

        var ratios = new List<(double HOverW, double Ratio)>();
        foreach (double hOverW in new[] { 0.20, 0.10, 0.05 })
        {
            double h = hOverW * w;
            var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * h);
            double c = PlanarStaticAim.Build(PlanarFill.BuildGeometryOnlyCores(mesh, Fill),
                                             terms, h, Aim).TotalCapacitance();
            double parallel = EmConstants.Eps0 * area / h;
            ratios.Add((hOverW, c / parallel));
            _out.WriteLine($"h/W = {hOverW:F2}: C = {c * 1e15:F3} fF, ε₀A/h = {parallel * 1e15:F3} fF, " +
                           $"ratio = {c / parallel:F4}");
        }

        foreach (var (_, r) in ratios) Assert.True(r > 1.0, $"ratio {r:F4} is below the parallel-plate value");
        for (int i = 1; i < ratios.Count; i++)
            Assert.True(ratios[i].Ratio < ratios[i - 1].Ratio,
                $"fringing excess did not fall from h/W = {ratios[i - 1].HOverW} to {ratios[i].HOverW}");
        Assert.True(ratios[^1].Ratio < 1.35, $"h/W = 0.05 gives {ratios[^1].Ratio:F4}, too far from 1");
    }

    /// <summary>
    /// The same plate, accelerated against dense on the identical mesh at each spacing — which is the
    /// sharper form of the oracle above, because it separates "the accelerator agrees with our own
    /// dense operator" from "the operator is physically right".
    /// </summary>
    [Fact]
    public void M2_ThePlateOracle_AgreesWithItsOwnDenseSolveAtEverySpacing()
    {
        const double w = 1e-3;
        var mesh = Uniform(w, w, 10, 10);

        foreach (double hOverW in new[] { 0.20, 0.10, 0.05 })
        {
            double h = hOverW * w;
            var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * h);
            double dense = PlanarDeembed.StaticCapacitance(mesh, terms);
            double acc   = PlanarDeembed.StaticCapacitance(mesh, terms, Fill, cores: null, slabHeightM: h);
            double rel   = Math.Abs(acc - dense) / dense;
            _out.WriteLine($"h/W = {hOverW:F2}: dense {dense * 1e15:F6} fF, accelerated {acc * 1e15:F6} fF ⇒ {rel:E2}");
            Assert.True(rel < Gate, $"h/W = {hOverW} disagrees by {rel:E2}");
        }
    }

    /// <summary>
    /// <c>T6_3</c>'s separability requirement, through the accelerated path: hold the mesh fixed and
    /// refine only the QUADRATURE. The near field's exact entries are the dense path's own, so this
    /// has to behave exactly as the dense solve does — and if it did not, a quadrature error and a
    /// projection error would be indistinguishable here.
    /// </summary>
    [Fact]
    public void M2_TheAnswerConvergesUnderQuadratureOrder_ThroughTheAcceleratedPath()
    {
        const double w = 1e-3, h = 0.1e-3;
        var mesh  = Uniform(w, w, 6, 6);
        var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * h);

        var vals = new List<double>();
        foreach (int f in new[] { 0, 1, 2 })
        {
            var st = (f == 0 ? PlanarFillSettings.Default : PlanarFillSettings.Default.Finer(f)) with { Aim = Aim };
            vals.Add(PlanarStaticAim.Build(PlanarFill.BuildGeometryOnlyCores(mesh, st), terms, h, Aim)
                                    .TotalCapacitance());
            _out.WriteLine($"quadrature Finer({f}): C = {vals[^1] * 1e15:F6} fF");
        }

        double drift = Math.Abs(vals[^1] - vals[0]) / vals[0];
        _out.WriteLine($"total drift across three quadrature levels on ONE mesh: {drift:E2}");
        Assert.True(drift < 1e-4,
            $"the default quadrature is {drift:E2} away from a converged one on a fixed mesh");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M3 — the wiring, and the dense path's bit-identity
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Setting <c>Aim</c> to null is today's code, bit for bit.</b> The accelerated route is an
    /// added branch and not a rewrite of the dense one, so this is asserted as exact equality rather
    /// than to a tolerance.
    /// </summary>
    [Fact]
    public void M3_TheDensePathIsUnchanged_BitForBit()
    {
        var (shortStd, longStd, slab) = Standards();

        double a = PlanarDeembed.CapacitancePerMetre(shortStd, longStd, slab);
        double b = PlanarDeembed.CapacitancePerMetre(shortStd, longStd, slab, PlanarFillSettings.Default);
        double c = PlanarDeembed.CapacitancePerMetre(shortStd, longStd, slab,
                                                     PlanarFillSettings.Default with { Aim = null });

        _out.WriteLine($"null / Default / Aim=null: {a:E17} / {b:E17} / {c:E17}");
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    /// <summary>
    /// An accelerated call with no slab height REFUSES rather than quietly solving densely — a
    /// silent dense fallback is exactly the ceiling this phase removes, and it would reappear as a
    /// twenty-minute run that ends in the old refusal.
    /// </summary>
    [Fact]
    public void M3_AnAcceleratedCallWithoutTheSlabHeight_Refuses()
    {
        var (shortStd, _, slab) = Standards();
        var terms = PlanarKernelTerms.StaticScalar(slab);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanarDeembed.StaticCapacitance(shortStd.Mesh, terms, Fill));
        Assert.Contains("2h", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The accelerated route holds no m×m anything, so it is judged against
    /// <see cref="SurfaceMesher.AcceleratedUnknownCeiling"/> — a standard the dense route refuses by
    /// name now builds. Asserted on the GUARD rather than on a solve, because the point is which
    /// ceiling is asked, and actually solving a mesh this size is not what this test is about.
    /// </summary>
    [Fact]
    public void M3_TheCeilingAskedOfTheAcceleratedStaticSolve_IsTheAcceleratedOne()
    {
        // Chosen so n sits between the two ceilings: the dense route refuses, the accelerated one
        // does not.
        var mesh = BuildUniformGrid(120, 40, 1e-4);
        int n = mesh.Bases.Count, m = mesh.Cells.Count;
        Assert.InRange(n, SurfaceMesher.UnknownCeiling + 1, SurfaceMesher.AcceleratedUnknownCeiling);
        _out.WriteLine($"fixture: m = {m:N0} cells, n = {n:N0} bases — between the {SurfaceMesher.UnknownCeiling:N0} " +
                       $"dense and {SurfaceMesher.AcceleratedUnknownCeiling:N0} accelerated ceilings");

        var dense = Assert.Throws<InvalidOperationException>(
            () => PlanarDeembed.GuardCapacitanceCeiling(mesh, accelerated: false));
        Assert.Contains("three m×m complex matrices", dense.Message, StringComparison.Ordinal);
        _out.WriteLine("dense: " + dense.Message);

        // …and the accelerated route lets exactly this mesh through. Asked of the GUARD rather than
        // of a solve on purpose: the decision is one comparison, and running the build to observe it
        // would cost minutes to read the same bit.
        Assert.Null(Record.Exception(() => PlanarDeembed.GuardCapacitanceCeiling(mesh, accelerated: true)));
    }

    /// <summary>A bare grid with no metal and no fill, so a ceiling fixture costs nothing.</summary>
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

    /// <summary>
    /// The near field really is O(m): entries per row stay bounded while m grows, which is the
    /// structural property the accelerated route rests on. A COUNTER, not a stopwatch.
    /// </summary>
    [Fact]
    public void M3_TheNearFieldIsONotQuadratic_InEntriesPerRow()
    {
        var slab  = GroundedSlab.Fr4Starter;
        var terms = PlanarKernelTerms.StaticScalar(slab);

        var rows = new List<(int M, double PerRow, double Fraction)>();
        foreach (int nx in new[] { 16, 32, 64 })
        {
            var mesh = Uniform(nx * 0.5e-3, 2e-3, nx, 4);
            var a = PlanarStaticAim.Build(PlanarFill.BuildGeometryOnlyCores(mesh, Fill),
                                          terms, slab.HeightM, Aim);
            rows.Add((a.Size, a.Report.NearEntriesPerRow, a.Report.NearFillFraction));
            _out.WriteLine($"m = {a.Size,4}: {a.Report.NearEntriesPerRow,6:F0} near entries/row, " +
                           $"{a.Report.NearFillFraction:P1} of the dense matrix, " +
                           $"{a.Report.ResidentBytes / 1024.0:F0} kB against {a.Report.DenseBytes / 1024.0:F0} kB dense");
        }

        Assert.True(rows[^1].Fraction < rows[0].Fraction,
            "the near fill FRACTION must fall as m grows, or the near field is a smaller O(m²)");
        Assert.True(rows[^1].PerRow < 1.6 * rows[0].PerRow,
            $"near entries per row grew {rows[^1].PerRow / rows[0].PerRow:F2}× over 4× m — that is not O(m)");
    }
}
