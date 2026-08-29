// P12 (brief-em-p12-aim-bordered-vias) — the bordered accelerator's own gates.
//
// The brief asks for four things and they are four different KINDS of measurement, which is why they
// are separate methods rather than one table:
//
//   M1 — a MULTI-LEVEL mesh with no vias, against the dense multi-level solve. What is new there is
//        the grid kernel per (level, level) pairing and the per-level scatter/gather; there is no
//        border at all.
//   M2 — THE BORDER, against the dense via solve on two fixtures that exercise the two vertical
//        basis families the mesher can produce: an interior via between two meshed levels
//        (`ViaPhysicsTests`' MMIC fixture) and a GROUND ATTACHMENT (`InternalPortTests`' backside
//        via), whose span runs from the plane to the metal. Plus the de-embedded S.
//   M3 — G_A^zz's validated range still governs. It is a statement about the KERNEL and is exactly
//        as true of an accelerated run as of a dense one.
//   M4 — the ceiling ladder: grow the fixture by LENGTH and record what a via-bearing accelerated
//        point actually costs.
//
// ── THE ONE THING TO READ BEFORE TRUSTING A NUMBER HERE ──────────────────────────────────────────
//
// Every |ΔI| below is reported beside a control, because on its own it says nothing about P12. Two
// controls do the work:
//
//   • THE WIDE-RADIUS RUNG. Widen `NearRadiusFactor` until every pair is in the near set and the
//     projection has nothing left to approximate. The bordered operator then has to reproduce the
//     dense multi-level matrix to ROUND-OFF — and it does, at 1e-15 entry-wise. That is what says
//     the border and the multi-level near assembly ARE `PlanarFill.FillMultiLevel`'s arithmetic
//     rather than a second reading of it, with no tolerance anywhere in the claim.
//
//   • THE SINGLE-LEVEL CONTROL. Whatever is left at the shipped radius is the PROJECTION's error,
//     and the projection is M5's, untouched. Run the SAME mesh character through the shipped
//     single-level `PlanarAimOperator` against the shipped single-level dense fill and the number
//     is the same order (measured: 2.7e-5 on a cells/λ = 40 un-edge-meshed FR-4 line, against
//     4.9e-7 on the shipping mesh — a 55× spread from the MESH alone, on a path P12 does not
//     touch). The brief's own 8.7e-7 is `AimAccuracyTests`' figure for the 32 mm FR-4 hero AT THE
//     SHIPPING MESH; it is a property of that fixture, and quoting it at a different one would be
//     grading P12 on somebody else's mesh.
//
// Everything numeric here is Category=Benchmark: a multi-level fill needs `Dcim.FitAtHeights` at
// ~0.1-0.3 s per (component, height pairing) — 13 of them on the two-level fixture — so there is no
// such thing as a sub-second multi-level accuracy test. The routine tier's contribution is the
// structural block at the top, which is milliseconds.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarP12BorderedAimTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Fixtures
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    /// <summary>
    /// The MMIC two-level shape L9 exists for — `ViaPhysicsTests.TwoLevelWithVia`'s own geometry,
    /// parameterised by length and by whether the via is there.
    ///
    /// <para><b>The via footprint is FIXED in metres rather than a fraction of the line</b>, which
    /// matters only for M4's ladder and matters a lot there: a board gets longer and its vias do
    /// not, so a fixture whose N_z grows with N measures a construction nobody builds. Both are
    /// measured in M4; this is the healthy one.</para>
    /// </summary>
    private static PlanarProblem TwoLevel(double fHz, double lengthM, bool via, bool upperEmpty = false)
    {
        const double w = 100e-6, viaSide = 40e-6;
        var stack = LayerStacks.MmicTwoLevel;
        var lower = new PlanarConductorLayer("M1", [Rect(0, 0, lengthM, w)], 4.1e7, 2e-6, stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("M2", upperEmpty ? [] : [Rect(0, 0, lengthM, w)],
                                             4.1e7, 3e-6, stack.TopZ);
        double xc = 0.5 * lengthM;
        var vias = via && !upperEmpty
            ? new[] { new PlanarVia(0, 1, [Rect(xc - 0.5 * viaSide, 0.3 * w,
                                                xc + 0.5 * viaSide, 0.7 * w)], 4.1e7) }
            : [];
        return new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, fHz, null, stack, vias);
    }

    /// <summary>§10.7's FR-4 hero with a backside via at its centre — `InternalPortTests`' own
    /// fixture, and the one whose vertical bases are GROUND ATTACHMENTS (a half basis spanning the
    /// plane to the metal) rather than interior vias.</summary>
    private static PlanarProblem GroundAttachment(double lengthM, double fHz, double viaSideM = 1.2e-3)
    {
        var problem = PlanarLineFixtures.Fr4Line(lengthM, fHz);
        var (x0, y0, x1, y1) = problem.Bounds();
        double xc = 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);
        var via = new PlanarVia(PlanarVia.GroundTerminal, 0,
                                [PlanarLineFixtures.Rect(xc - 0.5 * viaSideM, yc - 0.5 * viaSideM,
                                                         xc + 0.5 * viaSideM, yc + 0.5 * viaSideM)],
                                5.8e7);
        return problem with { Vias = [via] };
    }

    private static PlanarPortResolution[] EndPortsOn(PlanarMesh mesh, PlanarProblem problem, int? layer)
    {
        var (x0, y0, x1, y1) = problem.Bounds();
        double yc = 0.5 * (y0 + y1);
        return [.. PlanarPorts.ResolveAll(mesh,
        [
            new PlanarPort(1, new EmPoint(x0, yc), PlanarPortSide.MinX, 50.0, layer),
            new PlanarPort(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, 50.0, layer),
        ])];
    }

    private static double RelNorm(Vec<Complex> a, Vec<Complex> b)
    {
        double num = 0, den = 0;
        for (int i = 0; i < a.Count; i++)
        {
            num += (a[i] - b[i]).Magnitude * (a[i] - b[i]).Magnitude;
            den += a[i].Magnitude * a[i].Magnitude;
        }
        return Math.Sqrt(num / den);
    }

    private sealed record Rung(int N, int Nz, double NearPerRow, double NearFraction,
                               double WorstEntry, int Iterations, double CurrentError,
                               double DenseSeconds, double AimSeconds, long ResidentBytes,
                               long BorderBytes);

    /// <summary>
    /// One fixture, one mesh, one frequency: the dense multi-level solve against the bordered
    /// accelerator. <c>WorstEntry</c> is the largest entry-wise deviation of the accelerated PRODUCT
    /// from the dense matrix, scaled by that matrix's own largest entry — the accelerator forms no
    /// matrix, so the entries are recovered by probing it with unit vectors, which is the operator
    /// under test rather than a second path into it. <c>CurrentError</c> is the solved current
    /// vector's relative deviation, i.e. the quantity an s-parameter is read from.
    /// </summary>
    private static Rung Measure(PlanarProblem problem, PlanarMeshSettings mesh, double fHz,
                                int? portLayer, PlanarAimSettings? aim = null)
    {
        var report = SurfaceMesher.Mesh(problem, mesh, accelerated: true);
        var m      = report.Mesh;
        var levels = PlanarLevels.From(problem);
        double omega = 2.0 * Math.PI * fHz;
        int n = m.Bases.Count;

        var denseCores = PlanarFill.BuildCores(m);
        var geomCores  = PlanarFill.BuildGeometryOnlyCores(m);
        // ONE kernel set, shared: the fits are what a multi-level fill costs, and paying for them
        // twice would make the two wall clocks below meaningless as a comparison.
        var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, fHz));

        var swD = Stopwatch.StartNew();
        var z = PlanarFill.FillMultiLevel(denseCores, set.For(denseCores), levels, omega);
        // P7 — the factorisation consumes its matrix in place, and z is the reference. The copy is
        // the test's to take.
        var system = PlanarSystem.Wrap(z.Copy());
        var ports  = EndPortsOn(m, problem, portLayer);
        var rhs    = PlanarExcitation.RightHandSide(n, ports[0]);
        var exact  = system.Solve(rhs);
        swD.Stop();

        var g = PlanarAimGeometry.Build(geomCores, problem.Slab.HeightM, aim);
        var swA = Stopwatch.StartNew();
        var op  = PlanarBorderedAimOperator.Build(g, set.For(geomCores), levels, omega);
        swA.Stop();

        double norm = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) norm = Math.Max(norm, z[i, j].Magnitude);

        double worst = 0;
        var e = new Complex[n];
        for (int j = 0; j < n; j++)
        {
            Array.Clear(e);
            e[j] = Complex.One;
            var col = op.Multiply(e);
            for (int i = 0; i < n; i++) worst = Math.Max(worst, (col[i] - z[i, j]).Magnitude);
        }

        var got = op.Solve(rhs);
        var r = op.Report;
        return new Rung(n, r.VerticalCount, r.NearEntriesPerRow, r.NearFillFraction, worst / norm,
                        op.LastIterations, RelNorm(exact, got),
                        swD.Elapsed.TotalSeconds, swA.Elapsed.TotalSeconds,
                        r.ResidentBytes, r.BorderBytes);
    }

    private void Print(string label, Rung r) =>
        _out.WriteLine($"  {label,-34} {r.N,6} {r.Nz,5}  {r.NearPerRow,8:F0}  {r.NearFraction * 100,5:F1}%  " +
                       $"{r.WorstEntry,10:E2}  {r.Iterations,5}  {r.CurrentError,10:E2}  " +
                       $"{r.DenseSeconds,7:F2}  {r.AimSeconds,6:F2}  {r.ResidentBytes / 1048576.0,6:F1}");

    private void Header() =>
        _out.WriteLine("  fixture                                 N    N_z  near/row   near%   worst |ΔZ|  " +
                       "iters       |ΔI|  dense s  aim s      MB");

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Structural — the routine tier
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P12_1_TheGeometrySplitsAViaMeshAtR_via_5sOwnPrefix()
    {
        // The whole design rests on the horizontal unknowns being a contiguous PREFIX: the
        // projection, the near set and every stencil are indexed by basis, and the border starts at
        // N_h. R-via-5 guarantees it and three tests already assert it of the mesher; this asserts
        // that the accelerator's own split agrees with the mesh rather than with a comment.
        var problem = TwoLevel(10e9, 400e-6, via: true);
        var report  = SurfaceMesher.Mesh(problem);
        var mesh    = report.Mesh;
        Assert.True(report.ViaUnknownCount > 0, "the fixture must actually carry vertical unknowns");

        var g = PlanarAimGeometry.Build(PlanarFill.BuildGeometryOnlyCores(mesh), problem.Slab.HeightM);

        Assert.Equal(mesh.Bases.Count, g.TotalUnknowns);
        Assert.Equal(report.ViaUnknownCount, g.VerticalCount);
        Assert.Equal(mesh.Bases.Count - report.ViaUnknownCount, g.HorizontalCount);
        Assert.Equal(g.HorizontalCount, g.UnknownCount);          // what AIM actually projects

        for (int i = 0; i < g.HorizontalCount; i++)
            Assert.NotEqual(PlanarBasisDirection.Z, mesh.Bases[i].Direction);
        for (int i = g.HorizontalCount; i < mesh.Bases.Count; i++)
            Assert.Equal(PlanarBasisDirection.Z, mesh.Bases[i].Direction);

        // …and the stencils are the horizontal block's, not the mesh's — a projection sized from
        // TotalUnknowns would be indexed one basis past its own array on the last via row.
        Assert.Equal(g.HorizontalCount, g.UnknownCount);
        _out.WriteLine($"N = {g.TotalUnknowns} splits as {g.HorizontalCount} projected horizontal " +
                       $"rooftops + {g.VerticalCount} bordered vertical unknowns; the geometry holds " +
                       $"{g.NearEntries:N0} near entries over the horizontal block alone.");
    }

    [Fact]
    public void P12_2_TheSingleLevelOperatorRefusesAViaMeshBY_NAME_AndNamesTheBorderedOne()
    {
        // The refusal MOVED rather than disappearing, and where it moved to is the useful half of
        // the message: PlanarAimOperator holds ONE grid kernel pair, which is a statement that every
        // source and observer sits at one height. Building it on a via mesh would use the wrong
        // kernel for every cross-level pair and produce a complete, plausible, wrong answer.
        var problem = TwoLevel(10e9, 400e-6, via: true);
        var mesh    = SurfaceMesher.Mesh(problem).Mesh;
        var g       = PlanarAimGeometry.Build(PlanarFill.BuildGeometryOnlyCores(mesh), problem.Slab.HeightM);
        var k       = PlanarLineFixtures.Kernel(GroundedSlab.GaAsStarter, 10e9);

        var ex = Assert.Throws<NotSupportedException>(
            () => PlanarAimOperator.Build(g, k.VectorPotential, k.Scalar, 2 * Math.PI * 10e9));
        Assert.Contains("PlanarBorderedAimOperator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dense", ex.Message, StringComparison.OrdinalIgnoreCase);

        // …and the neighbour: the SAME call on a mesh with no vias is accepted, so the refusal is
        // about the ẑ family and not about the geometry object.
        var flat  = TwoLevel(10e9, 400e-6, via: false, upperEmpty: true);
        var gFlat = PlanarAimGeometry.Build(
            PlanarFill.BuildGeometryOnlyCores(SurfaceMesher.Mesh(flat).Mesh), flat.Slab.HeightM);
        Assert.Equal(0, gFlat.VerticalCount);
        _ = PlanarAimOperator.Build(gFlat, k.VectorPotential, k.Scalar, 2 * Math.PI * 10e9);

        _out.WriteLine(ex.Message);
    }

    [Fact]
    public void P12_3_G_A_zzsValidatedRangeSTILLGoverns_WithTheAcceleratorOn()
    {
        // Milestone 3. `Dcim.ValidatedRhoOverLambdaAtHeights` = 0.1 is a statement about the interior
        // FIT — how far apart two via footprints may be before G_A^zz stops being validated — and it
        // is exactly as true of an accelerated solve as of a dense one. P12 removed a SOLVER
        // refusal; it must not have removed this one, and the way to say so is to run the fixture
        // the dense path refuses, with Aim set, and get the same sentence.
        const double f = 10e9;
        var fr4   = new EmMaterial(4.4, 0.02);
        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(1.5e-3, fr4), new MediumLayer(0.1e-3, fr4)], Termination.Air);
        const double w = 2.9e-3, len = 20e-3;

        PlanarProblem WithVias(params double[] centres) => new(
            [new PlanarConductorLayer("L1", [Rect(0, 0, len, w)], 5.8e7, 35e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("L2", [Rect(0, 0, len, w)], 5.8e7, 35e-6, stack.TopZ)],
            new GroundedSlab(1.6e-3, fr4), f, null, stack,
            [.. centres.Select(cx => new PlanarVia(0, 1,
                [Rect(cx - 0.25e-3, 0.5 * w - 0.25e-3, cx + 0.25e-3, 0.5 * w + 0.25e-3)], 5.8e7))]);

        var aimFill = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default };

        // Two vias 18 mm apart: the fit really IS asked about that ρ, and it is refused.
        var far     = WithVias(1.0e-3, 19.0e-3);
        var meshFar = SurfaceMesher.Mesh(far, accelerated: true).Mesh;
        var vFar    = PlanarSolve.VerticalRangeVerdict(far, meshFar, f, aimFill);
        Assert.False(vFar.Verdict.Ok);
        Assert.Contains("ρ/λ", vFar.Verdict.Reason, StringComparison.Ordinal);

        // …and its legitimate neighbour, one via mid-board, still runs — narrowing the question was
        // never widened by turning the accelerator on.
        var near     = WithVias(10.0e-3);
        var meshNear = SurfaceMesher.Mesh(near, accelerated: true).Mesh;
        Assert.True(PlanarSolve.VerticalRangeVerdict(near, meshNear, f, aimFill).Verdict.Ok);

        _out.WriteLine(vFar.Verdict.Reason);
    }

    [Fact]
    public void P12_3b_ThePreSolveVERDICTAndTheRUNAgreeOnWhichCeilingAViaMeshGets()
    {
        // A defect P12 turned up rather than caused, and the reason the question is now one
        // function. `PlanarKernel`'s two mesh calls and the EM panel passed `Aim is not null` with
        // no level condition, while `PlanarSolveContext`'s constructor asked
        // `Aim is not null && levels is null` — so the report a user reads BEFORE pressing Simulate
        // judged a via-bearing accelerated mesh against 12,000 and the run then refused it at 5,000,
        // quoting the dense ceiling. Whichever way the owner settles the ceiling itself, the two
        // must not answer differently, and that is what this asserts.
        Assert.True(SurfaceMesher.UsesAcceleratedCeiling(aimOn: true, multiLevel: false));
        Assert.False(SurfaceMesher.UsesAcceleratedCeiling(aimOn: false, multiLevel: false));
        Assert.False(SurfaceMesher.UsesAcceleratedCeiling(aimOn: true, multiLevel: true));

        // …and on a real mesh, through the two call paths rather than through the function alone.
        // 24 mm at cells/λ 80 is N ≈ 5,000: between the two ceilings, which is the only band where
        // the two answers could ever have differed.
        var problem = TwoLevel(10e9, 24e-3, via: true);
        var ms      = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 80, EdgeMesh: false);
        var aimFill = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default };
        bool accel  = SurfaceMesher.UsesAcceleratedCeiling(true, problem.RequiresGeneralKernel);

        var report = SurfaceMesher.Mesh(problem, ms, accelerated: accel);
        Assert.True(report.Mesh.Bases.Count is > SurfaceMesher.UnknownCeiling
                                            and < SurfaceMesher.AcceleratedUnknownCeiling,
            $"the fixture must land BETWEEN the two ceilings to measure anything; N = " +
            $"{report.Mesh.Bases.Count}");

        // The report says it cannot solve; so must the run, and for the same reason rather than a
        // different one.
        Assert.False(report.CanSolve);
        var ex = Assert.Throws<InvalidOperationException>(() => new PlanarSolveContext(
            report.Mesh, EndPortsOn(report.Mesh, problem, 0), aimFill,
            PlanarLevels.From(problem), problem.Slab.HeightM));
        Assert.Contains($"{SurfaceMesher.UnknownCeiling:N0}", ex.Message, StringComparison.Ordinal);

        _out.WriteLine($"N = {report.Mesh.Bases.Count} (via-bearing, Aim on): the pre-solve verdict " +
                       $"and the run both judge it against {SurfaceMesher.UnknownCeiling:N0}. Before " +
                       "P12 the verdict used 12,000 and the run used 5,000.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M1 — the multi-level block, no border at all
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P12_4_M1_MultiLevelWithoutVias_AgainstTheDenseMultiLevelSolve()
    {
        _out.WriteLine("");
        _out.WriteLine("M1 — two conductor levels, NO vias: one grid kernel table per (level, level) " +
                       "pairing over ONE shared auxiliary grid, and a per-level scatter/gather.");
        _out.WriteLine("");
        Header();

        // The shipping mesh on this fixture is small enough that every pair is near, so the second
        // rung deliberately refines PAST it to make a far field exist at all — otherwise the table
        // compares the dense matrix with itself.
        var problem = TwoLevel(10e9, 400e-6, via: false);
        var shipping = Measure(problem, PlanarMeshSettings.Default, 10e9, portLayer: 0);
        Print("shipping mesh (all pairs near)", shipping);

        var far = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 80, EdgeMesh: false);
        var wide = Measure(TwoLevel(10e9, 2e-3, via: false), far, 10e9, portLayer: 0);
        Print("2 mm, cells/λ 80 (far field)", wide);

        _out.WriteLine("");
        _out.WriteLine($"  Both rungs carry {3} level pairings on one grid. The first has no far " +
                       "field, so it is the EXACTNESS statement: the accelerated product reproduces " +
                       "PlanarFill.FillMultiLevel's matrix to round-off. The second has one, so it " +
                       "is the projection's.");

        Assert.True(shipping.WorstEntry < 1e-13,
            $"with every pair in the near set the bordered product IS the dense multi-level matrix, " +
            $"and it reads {shipping.WorstEntry:E2} — the multi-level near assembly is not the dense " +
            "fill's arithmetic");
        Assert.True(wide.CurrentError < 1e-5,
            $"the multi-level projection is {wide.CurrentError:E2} from the dense solve");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M2 — the border
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P12_5_M2_TheBorder_AgainstTheDenseViaSolve_OnBothVerticalBasisFamilies()
    {
        _out.WriteLine("");
        _out.WriteLine("M2 — the dense border (Z_hz, Z_zz), against the dense via solve. Two fixtures " +
                       "because the mesher makes two kinds of vertical basis: an INTERIOR via between " +
                       "two meshed levels, and a GROUND ATTACHMENT spanning the plane to the metal.");
        _out.WriteLine("");
        Header();

        var shipping = PlanarMeshSettings.Default;
        var far      = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 80, EdgeMesh: false);
        var wideNear = PlanarAimSettings.Default with { NearRadiusFactor = 12.0 };

        // ── the interior via ─────────────────────────────────────────────────────────────────
        var mmicShipping = Measure(TwoLevel(10e9, 400e-6, via: true), shipping, 10e9, 0);
        Print("MMIC via, shipping (all near)", mmicShipping);

        var mmicFar  = Measure(TwoLevel(10e9, 2e-3, via: true), far, 10e9, 0);
        var mmicCtrl = Measure(TwoLevel(10e9, 2e-3, via: true), far, 10e9, 0, wideNear);
        Print("MMIC via, cells/λ 80", mmicFar);
        Print("  …same, near radius 12 supports", mmicCtrl);

        // ── the ground attachment ────────────────────────────────────────────────────────────
        var groundShipping = Measure(GroundAttachment(16e-3, 6e9), shipping, 6e9, null);
        Print("FR-4 ground via, shipping mesh", groundShipping);

        _out.WriteLine("");
        _out.WriteLine("  The WIDE-RADIUS rung is the control that matters: widen the near set until " +
                       "nothing is left for the projection to approximate and the bordered operator " +
                       "must reproduce the dense via matrix to round-off. What survives at the " +
                       "shipped radius is the PROJECTION's error, and the projection is M5's — see " +
                       "P12_7 for the same mesh character with no via in it at all.");

        // The exactness claims — no tolerance in the argument, only round-off.
        Assert.True(mmicShipping.WorstEntry < 1e-13,
            $"all pairs near, and the product still differs from the dense matrix by " +
            $"{mmicShipping.WorstEntry:E2}: the border is not FillMultiLevel's arithmetic");
        Assert.True(mmicCtrl.NearFraction > 0.999,
            "the control rung must actually put every pair in the near set");
        Assert.True(mmicCtrl.WorstEntry < 1e-13, $"control rung reads {mmicCtrl.WorstEntry:E2}");
        Assert.True(mmicCtrl.CurrentError < 1e-9, $"control rung reads {mmicCtrl.CurrentError:E2}");

        // The projection claims, each against what its own mesh supports (see the file header).
        Assert.True(groundShipping.CurrentError < 8.7e-7,
            $"the ground-attachment fixture at the SHIPPING mesh is {groundShipping.CurrentError:E2} " +
            "from the dense solve, past AimAccuracyTests' own 8.7e-7");
        Assert.True(mmicFar.CurrentError < 1e-5,
            $"the two-level via projection is {mmicFar.CurrentError:E2} from the dense solve");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P12_6_M2_TheDeEmbeddedS_ThroughTheWHOLESweepDriver()
    {
        // The brief's S gate, and it is deliberately taken through `PlanarSolve.Run` rather than
        // through the operator: what it tests that P12_5 does not is the WIRING — that
        // PlanarSolveContext reaches the bordered operator on the general kernel, that the
        // calibration standards (single-level, their own meshes) still take the single-level
        // accelerated path beside it, and that the de-embedding algebra sees the same solution
        // either way.
        double f = 10e9, len = 2e-3;
        var problem = TwoLevel(f, len, via: true);
        var ms   = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);
        var mesh = SurfaceMesher.Mesh(problem, ms, accelerated: true).Mesh;
        var ports = EndPortsOn(mesh, problem, 0);
        double[] freqs = [5e9, 10e9];

        var swD = Stopwatch.StartNew();
        var dense = PlanarSolve.Run(problem, mesh, ports, freqs);
        swD.Stop();

        var st = PlanarSolveSettings.Default with
        {
            Fill = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default },
        };
        var swA = Stopwatch.StartNew();
        var aim = PlanarSolve.Run(problem, mesh, ports, freqs, st);
        swA.Stop();

        double worstDe = 0, worstRaw = 0;
        for (int p = 0; p < freqs.Length; p++)
            for (int a = 0; a < 2; a++)
                for (int b = 0; b < 2; b++)
                {
                    worstDe  = Math.Max(worstDe,  (dense.Points[p].S[a, b]    - aim.Points[p].S[a, b]).Magnitude);
                    worstRaw = Math.Max(worstRaw, (dense.Points[p].RawS[a, b] - aim.Points[p].RawS[a, b]).Magnitude);
                }

        _out.WriteLine("");
        _out.WriteLine($"M2 — de-embedded S through PlanarSolve.Run: N = {mesh.Bases.Count}, " +
                       $"{dense.StandardCount} calibration standard(s), {freqs.Length} frequencies.");
        _out.WriteLine($"  worst |ΔS| de-embedded {worstDe:E2}, raw {worstRaw:E2}; " +
                       $"dense {swD.Elapsed.TotalSeconds:F1} s, accelerated {swA.Elapsed.TotalSeconds:F1} s.");
        _out.WriteLine($"  S21(10 GHz) = {dense.Points[1].S[1, 0]}");

        Assert.True(worstDe < 1e-6,
            $"the accelerated de-embedded S is {worstDe:E2} from the dense one, past the brief's 1e-6");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P12_7_TheSingleLevelCONTROL_TheAccuracyIsTheMESHsAndNotP12s()
    {
        // The second control from the file header, and the reason every |ΔI| above is quoted with
        // its mesh attached. This runs no P12 code at all: it is the SHIPPED single-level
        // accelerator against the SHIPPED single-level dense fill, on the two mesh characters used
        // above. If the spread here is the same as the spread there, the accuracy is the
        // projection's and the mesh's — which is a path P12 does not touch.
        double f = 6e9, len = 16e-3;
        _out.WriteLine("");
        _out.WriteLine("  single-level control (no P12 code) — same fixture, same two mesh characters");
        _out.WriteLine("");
        _out.WriteLine("  mesh                       N   near/row  near%   worst |ΔZ|  iters       |ΔI|");

        foreach (var ms in new[]
        {
            PlanarMeshSettings.Default,
            new PlanarMeshSettings(Auto: false, CellsPerWavelength: 40, EdgeMesh: false),
        })
        {
            var problem = PlanarLineFixtures.Fr4Line(len, f);
            var mesh    = SurfaceMesher.Mesh(problem, ms).Mesh;
            var ports   = PlanarPorts.ResolveAll(mesh, PlanarLineFixtures.EndPorts(problem));
            var dense   = PlanarFill.BuildCores(mesh);
            var geom    = PlanarFill.BuildGeometryOnlyCores(mesh);
            var k       = PlanarLineFixtures.Kernel(problem.Slab, f);
            double omega = 2 * Math.PI * f;

            var z   = PlanarFill.Fill(dense, k.VectorPotential, k.Scalar, omega);
            var sys = PlanarSystem.Wrap(z.Copy());
            int n   = mesh.Bases.Count;
            var rhs = PlanarExcitation.RightHandSide(n, ports[0]);
            var exact = sys.Solve(rhs);

            var op = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, omega, problem.Slab.HeightM);
            double norm = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) norm = Math.Max(norm, z[i, j].Magnitude);
            double worst = 0;
            var e = new Complex[n];
            for (int j = 0; j < n; j++)
            {
                Array.Clear(e);
                e[j] = Complex.One;
                var col = op.Multiply(e);
                for (int i = 0; i < n; i++) worst = Math.Max(worst, (col[i] - z[i, j]).Magnitude);
            }
            var got = op.Solve(rhs);

            _out.WriteLine($"  cells/λ {ms.CellsPerWavelength,3} edge {ms.EdgeMesh,-5} {n,6}  " +
                           $"{op.Report.NearEntriesPerRow,8:F0}  {op.Report.NearFillFraction * 100,5:F1}%  " +
                           $"{worst / norm,10:E2}  {op.LastIterations,5}  {RelNorm(exact, got),10:E2}");
        }

        _out.WriteLine("");
        _out.WriteLine("  Read this table beside P12_5's. The spread between the two rows is the " +
                       "MESH's, on a code path P12 does not touch, and it is the same spread the " +
                       "bordered operator shows on the same two mesh characters.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M4 — the ceiling ladder
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P12_8_M4_TheCeilingLadder_GrowTheFixtureByLENGTH()
    {
        // Milestone 4, and it is `brief-em-aim-ceiling.md`'s own healthy construction: grow the part
        // by LENGTH at a fixed mesh resolution, which is how a real board gets big. The ladder that
        // BROKE there was the other one — refining the resolution at a fixed footprint — and P8's
        // near-radius floor is what fixed that; neither is re-run here.
        //
        // No dense reference: past N ≈ 5,000 there is nothing to compare against, which is the whole
        // point of the exercise. What is recorded is what the accelerated point COSTS and whether it
        // stays healthy.
        const double f = 10e9;
        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 80, EdgeMesh: false);

        _out.WriteLine("");
        _out.WriteLine("M4 — a two-level line with ONE fixed 40 µm via, grown by length at cells/λ = 80.");
        _out.WriteLine("");
        _out.WriteLine("   L(mm)      N   N_z  near/row  near%   geom s  grid  near  border  precond  " +
                       "solve  iters  resid     MB   dense MB  border MB");

        int worstIterations = 0;
        double worstNearPerRow = 0, firstNearPerRow = 0;
        foreach (double len in new[] { 2e-3, 8e-3, 24e-3, 48e-3, 72e-3 })
        {
            var problem = TwoLevel(f, len, via: true);
            var report  = SurfaceMesher.Mesh(problem, ms, accelerated: true);
            var mesh    = report.Mesh;
            var geom    = PlanarFill.BuildGeometryOnlyCores(mesh);
            var set     = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f));
            var levels  = PlanarLevels.From(problem);

            var g  = PlanarAimGeometry.Build(geom, problem.Slab.HeightM);
            var op = PlanarBorderedAimOperator.Build(g, set.For(geom), levels, 2 * Math.PI * f);

            var rhs = PlanarExcitation.RightHandSide(mesh.Bases.Count, EndPortsOn(mesh, problem, 0)[0]);
            var sw  = Stopwatch.StartNew();
            op.Solve(rhs);
            sw.Stop();

            var r = op.Report;
            worstIterations = Math.Max(worstIterations, op.LastIterations);
            worstNearPerRow = Math.Max(worstNearPerRow, r.NearEntriesPerRow);
            if (firstNearPerRow == 0) firstNearPerRow = r.NearEntriesPerRow;

            _out.WriteLine($"  {len * 1e3,6:F0} {r.UnknownCount,7} {r.VerticalCount,5}  " +
                           $"{r.NearEntriesPerRow,8:F0}  {r.NearFillFraction * 100,5:F1}%  " +
                           $"{g.TotalMs / 1000,7:F2}  {r.GridKernelMs / 1000,4:F2}  {r.NearFillMs / 1000,4:F2}  " +
                           $"{r.BorderMs / 1000,6:F2}  {r.PreconditionerMs / 1000,7:F2}  " +
                           $"{sw.Elapsed.TotalSeconds,5:F2}  {op.LastIterations,5}  {op.LastResidual,7:E1}  " +
                           $"{r.ResidentBytes / 1048576.0,6:F1}  {PlanarSystem.ResidentBytes(r.UnknownCount, mesh.Cells.Count) / 1048576.0,9:F0}  " +
                           $"{r.BorderBytes / 1048576.0,9:F1}");
        }

        _out.WriteLine("");
        _out.WriteLine("  The 'dense MB' column is PlanarSystem.ResidentBytes — what a dense point of " +
                       "the same N actually holds, not 16N².");
        _out.WriteLine("  The BORDER's own bytes stay under a megabyte across the whole ladder, " +
                       "because N_z is set by the vias and not by N. Its TIME does not scale with N " +
                       "either: the mixed block is N_h × N_z graded quadratures, and N_z is fixed.");
        _out.WriteLine("  A ladder whose via footprint grows WITH the line tells the opposite story " +
                       "— see the RESOLVED.md write-up, where N_z reached 140 and the border became " +
                       "28.6 s of a 34 s point. That is the construction the brief's 'cheap when " +
                       "N_z ≪ N_h' is a statement about.");

        // §11's finding, on a via-bearing operator: the near field is O(N) and the iteration count
        // does not walk. Both are asserted rather than merely printed, because either failing is
        // what would say the ceiling cannot be widened to this class of mesh.
        Assert.True(worstNearPerRow < 1.5 * firstNearPerRow,
            $"near entries per row went {firstNearPerRow:F0} -> {worstNearPerRow:F0}: the near field " +
            "is not O(N) on a via-bearing mesh and the ladder's cost claim does not hold");
        Assert.True(worstIterations <= 12,
            $"GMRES reached {worstIterations} iterations; §11's flat-iteration finding does not " +
            "survive the bordered preconditioner");
    }
}
