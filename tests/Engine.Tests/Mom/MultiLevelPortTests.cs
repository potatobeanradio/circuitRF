using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Phase L9d — ports on more than one level, references, de-embedding, and the cost.</b>
///
/// <para>The ladder, in the order §4 fixes: Tier 1 (the one-level reduction, and the reconstruction
/// that pins R-mlp-1's bit-identity) before anything empirical; then Tier 0's structural checks on a
/// real two-level mesh; then Tier 3, which is L8d's own de-embedding ladder re-run through the new
/// code path and is what catches a port-indexing error; then Tier 4/5.</para>
/// </summary>
public sealed class MultiLevelPortTests
{
    private readonly ITestOutputHelper _out;
    public MultiLevelPortTests(ITestOutputHelper output) => _out = output;

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    /// <summary>
    /// The MMIC two-level shape L9 exists for, sized so it is electrically small at 10 GHz
    /// (0.4 mm ≈ 0.014 λ, comfortably inside G_A^zz's own ρ/λ ≤ 0.1) — because a structure OUTSIDE
    /// that range is refused, by design, and every test here would then be measuring the refusal
    /// rather than the answer.
    ///
    /// <para>M1 sits at z = 100 µm, which is BOTH the interior interface of
    /// <see cref="LayerStacks.MmicTwoLevel"/> and the top surface of
    /// <see cref="GroundedSlab.GaAsStarter"/> — the one configuration where D3's single-level
    /// calibration standards are on the electrostatic problem C_pul actually solves.</para>
    /// </summary>
    private static PlanarProblem TwoLevel(
        double fHz = 10e9, bool withVia = true, bool upperEmpty = false, double lengthM = 400e-6)
    {
        var stack = LayerStacks.MmicTwoLevel;
        double zLow = stack.InterfaceZ[1], zHigh = stack.TopZ;

        var lower = new PlanarConductorLayer("M1", [Rect(0, 0, lengthM, 100e-6)], 4.1e7, 2e-6, zLow);
        var upper = new PlanarConductorLayer(
            "M2", upperEmpty ? [] : [Rect(0, 0, lengthM, 100e-6)], 4.1e7, 3e-6, zHigh);

        var vias = withVia && !upperEmpty
            ? new[] { new PlanarVia(0, 1, [Rect(0.45 * lengthM, 30e-6, 0.55 * lengthM, 70e-6)], 4.1e7) }
            : [];

        return new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, fHz, null, stack, vias);
    }

    /// <summary>The same line, one level, on the same slab — the reduction every Tier 1 rung is
    /// measured against.</summary>
    private static PlanarProblem OneLevel(double fHz = 10e9, double lengthM = 400e-6,
                                          LayerStack? stack = null) =>
        new([new PlanarConductorLayer("M1", [Rect(0, 0, lengthM, 100e-6)], 4.1e7, 2e-6,
                                      stack is null ? double.NaN : GroundedSlab.GaAsStarter.HeightM)],
            GroundedSlab.GaAsStarter, fHz, null, stack);

    private static PlanarPort[] EndPorts(double lengthM, int? layer = null) =>
    [
        new PlanarPort(1, new EmPoint(0,       50e-6), PlanarPortSide.MinX, 50.0, layer),
        new PlanarPort(2, new EmPoint(lengthM, 50e-6), PlanarPortSide.MaxX, 50.0, layer),
    ];

    // =========================================================================================
    // M1 / R-mlp-1 — the one-level path is BIT-IDENTICAL, and it is pinned by reconstruction.
    // =========================================================================================

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M1_1_TheOneLevelPath_IsBitIdentical_ToAHandReconstructionOfL8dsOwnSweep()
    {
        // R-mlp-1's own instruction: reconstruct the pre-change s-parameters and compare at FULL
        // PRECISION, the way L9b pinned twelve dumped fit configurations and L9c pinned 600 Voltage
        // values. The Tier oracles carry tolerances and structurally cannot catch a one-ulp move —
        // and a one-ulp move here would mean PlanarSolveContext, PlanarKernelPair or the calibrator
        // had quietly stopped being L8d's own objects on the path that must not change.
        var slab  = GroundedSlab.Fr4Starter;
        var line  = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(line);
        double[] freqs = [2e9, 6e9];
        var st = new PlanarSolveSettings();

        var viaNewPath = PlanarSolve.Run(mesh, ports, slab, freqs, st);

        // ── The reconstruction: exactly the calls L8d's own driver made, written out here ────────
        var dut = new PlanarSolveContext(mesh, ports, st.Fill);
        var cal = new PlanarPortCalibrator(ports[0], slab, freqs[0], freqs[^1], st.Calibration, st.Fill);
        bool shared = PlanarPortCalibrator.SameCrossSection(
            ports[0], ports[1], PlanarCalibration.EndRunCellsFor(ports[0], slab, st.Calibration));
        var cal2 = shared ? cal
                          : new PlanarPortCalibrator(ports[1], slab, freqs[0], freqs[^1],
                                                     st.Calibration, st.Fill);
        var z0 = PlanarExcitation.ReferenceImpedances(ports);

        for (int i = 0; i < freqs.Length; i++)
        {
            double f = freqs[i];
            var kernel = PlanarKernelPair.Fit(slab, f, PlanarFillSettings.Default.Order, st.Dcim);
            var raw = PlanarExcitation.RawScattering(dut.SolveAt(kernel, f).Y, z0);
            var c1 = cal.At(kernel, f);
            var c2 = ReferenceEquals(cal, cal2) ? c1 : cal2.At(kernel, f);
            var s = PlanarDeembed.Renormalise(
                PlanarDeembed.Apply(raw, [c1.Box, c2.Box]), [c1.Zc, c2.Zc], z0);

            for (int a = 0; a < 2; a++)
            for (int b = 0; b < 2; b++)
            {
                Assert.Equal(s[a, b].Real,      viaNewPath.Points[i].S[a, b].Real);
                Assert.Equal(s[a, b].Imaginary, viaNewPath.Points[i].S[a, b].Imaginary);
                Assert.Equal(raw[a, b].Real,    viaNewPath.Points[i].RawS[a, b].Real);
            }
        }

        _out.WriteLine($"N = {mesh.Bases.Count}, {freqs.Length} frequencies, {viaNewPath.StandardCount} " +
                       $"standard mesh(es): every de-embedded and raw s-parameter is BIT-identical to a " +
                       $"hand reconstruction of L8d's own sweep. S21(2 GHz) = " +
                       $"{viaNewPath.Points[0].S[1, 0]}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // two multi-level fills on the MMIC fixture, 13 s
    public void M1_2_TierOne_ATwoLevelProblemWhoseSecondLevelIsEMPTY_ReproducesTheOneLevelAnswerExactly()
    {
        // §4's strongest single check. The multi-level machinery must add NOTHING when the extra
        // level carries no metal: same N, same Z, same S. Both sides run the GENERAL path (the
        // one-level side carries an explicit MediumStack), so this isolates "does an empty level
        // perturb anything" from "does the general kernel reproduce the shipped one" — which is
        // L9c's M5_1 and is measured separately.
        double f = 10e9, len = 400e-6;
        var two = TwoLevel(f, withVia: false, upperEmpty: true, lengthM: len);
        var one = OneLevel(f, len, LayerStacks.MmicTwoLevel);

        var mTwo = SurfaceMesher.Mesh(two).Mesh;
        var mOne = SurfaceMesher.Mesh(one).Mesh;
        Assert.Equal(mOne.Bases.Count, mTwo.Bases.Count);
        Assert.Equal(mOne.Cells.Count, mTwo.Cells.Count);

        var zTwo = FillOf(two, mTwo, f);
        var zOne = FillOf(one, mOne, f);
        for (int i = 0; i < mOne.Bases.Count; i++)
        for (int j = 0; j < mOne.Bases.Count; j++)
        {
            Assert.Equal(zOne[i, j].Real,      zTwo[i, j].Real);
            Assert.Equal(zOne[i, j].Imaginary, zTwo[i, j].Imaginary);
        }

        var pTwo = PlanarPorts.ResolveAll(mTwo, EndPorts(len, layer: 0));
        var pOne = PlanarPorts.ResolveAll(mOne, EndPorts(len, layer: 0));
        var sTwo = new PlanarSolveContext(mTwo, pTwo, null, PlanarLevels.From(two))
                       .RawScatteringAt(PlanarFrequencyKernel.Fit(two, f), f);
        var sOne = new PlanarSolveContext(mOne, pOne, null, PlanarLevels.From(one))
                       .RawScatteringAt(PlanarFrequencyKernel.Fit(one, f), f);
        for (int a = 0; a < 2; a++)
        for (int b = 0; b < 2; b++)
            Assert.Equal(sOne[a, b].Real, sTwo[a, b].Real);

        _out.WriteLine($"N = {mOne.Bases.Count} on both sides; Z and the raw S are bit-identical with " +
                       $"the empty second level present. S21 = {sOne[1, 0]}");
    }

    [Fact]
    public void M1_3_TierOne_TwoLevelsAtTheSameZ_AreRefused_AndTheirLegitimateNeighbourIsAccepted()
    {
        // The degenerate case §4 asks for, and the R-mlp-3 shape: the refusal is measured next to the
        // case it is NOT allowed to catch. Two levels at the same z are not two levels — they are one
        // level's artwork split in two, with nothing between them for a via to cross and no height
        // pairing to fit; a solver that accepted it would silently double-count the metal.
        double z = LayerStacks.MmicTwoLevel.InterfaceZ[1];
        var stack = LayerStacks.MmicTwoLevel;

        var degenerate = new PlanarProblem(
            [new PlanarConductorLayer("M1", [Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 2e-6, z),
             new PlanarConductorLayer("M1b", [Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 2e-6, z)],
            GroundedSlab.GaAsStarter, 10e9, null, stack);

        var kernel = new PlanarKernel();
        var no = kernel.CanSolve(degenerate);
        Assert.False(no.Ok);
        Assert.Contains("BOTTOM-TO-TOP", no.Reason);

        Assert.True(kernel.CanSolve(TwoLevel()).Ok, "the ordered two-level neighbour must be accepted");
        _out.WriteLine(no.Reason!);
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // a multi-level fill plus a standard's, 26 s
    public void M1_4_D7_TheDutAndItsStandards_SHAREOneFitPerPairing_NotOnePerMesh()
    {
        // L8d's decision, carried into L9: "fit once per frequency, share across the DUT and every
        // standard". M1's own warning is that widening the kernel carelessly turns 9 fits per
        // frequency into 9 per MESH — invisible in every answer and worth 3-5× of a fixed cost. This
        // is R-mom-11's counter pattern: assert the NUMBER, not a comment.
        double f = 10e9, len = 400e-6;
        var problem = TwoLevel(f, withVia: true, lengthM: len);
        var mesh = SurfaceMesher.Mesh(problem).Mesh;
        var cores = PlanarFill.BuildCores(mesh);
        var levels = PlanarLevels.From(problem);

        var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f));
        var kernel = PlanarFrequencyKernel.FromSet(set);

        // The DUT's own fill.
        PlanarFill.FillMultiLevel(cores, set.For(cores), levels, 2 * Math.PI * f);
        int afterDut = set.FitCount;
        Assert.True(afterDut > 0, "the DUT must actually have fitted something");

        // A SECOND mesh at the same frequency — a calibration standard's shape: one level, its own
        // cells, its own ρ floor. It asks for the (z, z) pairing the DUT already fitted.
        var std = PlanarCalibration.BuildLine(
            PlanarPorts.Resolve(mesh, new PlanarPort(1, new EmPoint(0, 50e-6), PlanarPortSide.MinX, 50.0, 0)),
            targetLengthM: 200e-6, endRunCells: 1);
        var stdCores = PlanarFill.BuildCores(std.Mesh);
        PlanarFill.FillMultiLevel(stdCores, set.For(stdCores),
                                  new PlanarLevels([levels.Of(0)]), 2 * Math.PI * f);

        Assert.Equal(afterDut, set.FitCount);
        _out.WriteLine($"{afterDut} fits for the DUT (N = {mesh.Bases.Count}, two levels + a via); the " +
                       $"standard (N = {std.Mesh.Bases.Count}) asked for ZERO more. Before L9d's shared " +
                       $"fit cache the standard would have refitted its whole pairing set.");
    }

    // =========================================================================================
    // M2 / Tier 0 — ports on a level, the ambiguity refusal, and the via-port decision.
    // =========================================================================================

    [Fact]
    public void M2_1_D1_L8dsOwnPortResolver_WorksOnATwoLevelMesh_WithOneIndex()
    {
        // D1: "the burden of proof is on anything that says otherwise. Start by trying the existing
        // resolver on a two-level mesh and reporting what actually breaks." The answer is ONE INDEX:
        // TryResolve already filtered cells by port.LayerIndex, so a port explicitly on level 1
        // resolves onto level 1's own rooftops with nothing else changed. What L9d had to ADD is the
        // decision of WHICH level when nobody said (M2_2), not the resolution itself.
        double len = 400e-6;
        var problem = TwoLevel(lengthM: len);
        var mesh = SurfaceMesher.Mesh(problem).Mesh;

        var onM1 = PlanarPorts.Resolve(mesh, new PlanarPort(1, new EmPoint(0, 50e-6),
                                                            PlanarPortSide.MinX, 50.0, 0));
        var onM2 = PlanarPorts.Resolve(mesh, new PlanarPort(1, new EmPoint(0, 50e-6),
                                                            PlanarPortSide.MinX, 50.0, 1));

        Assert.Equal(0, onM1.LayerIndex);
        Assert.Equal(1, onM2.LayerIndex);
        Assert.Equal(onM1.BasisCount, onM2.BasisCount);
        Assert.Equal(onM1.WidthM, onM2.WidthM);

        foreach (int b in onM1.BasisIndices) Assert.Equal(0, mesh.Bases[b].LayerIndex);
        foreach (int b in onM2.BasisIndices) Assert.Equal(1, mesh.Bases[b].LayerIndex);
        Assert.Empty(onM1.BasisIndices.Intersect(onM2.BasisIndices));

        _out.WriteLine($"Two levels, identical artwork: the SAME port location resolves to " +
                       $"{onM1.BasisCount} rooftop(s) on level 0 and {onM2.BasisCount} disjoint ones " +
                       $"on level 1, at the same width {Eng(onM1.WidthM)}m. Nothing in " +
                       $"L8d's resolver needed rewriting.");
    }

    [Fact]
    public void M2_2_D2_AnAmbiguousPortIsRefusedByName_AndSayingWhichLevelResolvesIt()
    {
        // R-mom-17 with its legitimate neighbour in the same test. Both levels carry metal at the
        // port's location, so inference has two answers; picking one silently would drive a
        // different conductor with the same footprint and produce a complete, plausible answer for a
        // structure that was not drawn.
        var mesh = SurfaceMesher.Mesh(TwoLevel()).Mesh;

        bool ok = PlanarPorts.TryResolve(
            mesh, new PlanarPort(1, new EmPoint(0, 50e-6), PlanarPortSide.MinX, 50.0),
            out _, out string? refusal);

        Assert.False(ok);
        Assert.Contains("level 0", refusal);
        Assert.Contains("level 1", refusal);
        Assert.Contains("M1", refusal);
        Assert.Contains("M2", refusal);

        // …and the neighbour: saying which level is all it takes.
        Assert.True(PlanarPorts.TryResolve(
            mesh, new PlanarPort(1, new EmPoint(0, 50e-6), PlanarPortSide.MinX, 50.0, 1),
            out var res, out _));
        Assert.Equal(1, res!.LayerIndex);

        // …and on a mesh where only ONE level carries metal there, inference is unambiguous and the
        // pre-L9d behaviour is reproduced exactly — no port anywhere had to gain an index.
        var stepped = SurfaceMesher.Mesh(TwoLevel(upperEmpty: true)).Mesh;
        Assert.True(PlanarPorts.TryResolve(
            stepped, new PlanarPort(1, new EmPoint(0, 50e-6), PlanarPortSide.MinX, 50.0),
            out var inferred, out _));
        Assert.Equal(0, inferred!.LayerIndex);

        _out.WriteLine(refusal!);
    }

    [Fact]
    public void M2_3_AViaPortIsRefusedByName_AndTheVerticalUnknownsAreNeverInAPortsROW()
    {
        // §0.2 item 2's answer, earned rather than asserted. (a) — a via port is refused — and the
        // measurement that earns it is that the thing a caller would otherwise get is a DIFFERENT
        // object: the horizontal rooftops at the same (x, y). R-via-5 makes the vertical unknowns the
        // TAIL of the vector, so "no port row is vertical" is an exact index question.
        double len = 400e-6;
        var problem = TwoLevel(lengthM: len);
        var report = SurfaceMesher.Mesh(problem);
        var mesh = report.Mesh;
        Assert.True(report.ViaUnknownCount > 0, "the fixture must actually carry vertical unknowns");
        int horizontal = report.UnknownCount - report.ViaUnknownCount;

        // The refusal, by name, pointing at where it actually arrives.
        bool ok = PlanarPorts.TryResolve(
            mesh, new PlanarPort(1, new EmPoint(0, 50e-6), PlanarPortSide.MinX, 50.0, 0,
                                 PlanarPortReference.ViaBetweenLevels),
            out _, out string? refusal);
        Assert.False(ok);
        Assert.Contains("via", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10.6", refusal);

        // The legitimate neighbour at the SAME place, and the measurement: every basis it drives is
        // horizontal, so the vertical basis is genuinely not what a "port on a via" would have got.
        var ports = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));
        foreach (var p in ports)
            foreach (int b in p.BasisIndices)
            {
                Assert.True(b < horizontal, "a port row must lie in R-via-5's horizontal PREFIX");
                Assert.NotEqual(PlanarBasisDirection.Z, mesh.Bases[b].Direction);
            }

        // Ports are disjoint: no basis is driven by two ports, so B's rows carry at most one ±1.
        Assert.Empty(ports[0].BasisIndices.Intersect(ports[1].BasisIndices));

        _out.WriteLine($"N = {report.UnknownCount} ({report.ViaUnknownCount} vertical). Both ports' " +
                       $"rows lie entirely inside the first {horizontal} unknowns. Refusal:\n{refusal}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // a multi-level fill + LU + 2 back-substitutions, 26 s
    public void M2_4_TierZero_YIsSymmetricOnAMultiLevelMesh_AndTheViaCarriesRealCurrent()
    {
        // L8d's D1 survives L9c's basis: Z is symmetric bit for bit (L9c's M5_2 gates that), so
        // Y = BᵀZ⁻¹B is symmetric to the LU's own tolerance — and stating the strength precisely is
        // L7b-b's standing rule. The second half stops the first from passing vacuously: if no
        // current reached the via, the multi-level solve would be a one-level solve wearing a hat.
        double f = 10e9, len = 400e-6;
        var problem = TwoLevel(f, lengthM: len);
        var report = SurfaceMesher.Mesh(problem);
        var mesh = report.Mesh;
        var ports = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));

        var ctx = new PlanarSolveContext(mesh, ports, null, PlanarLevels.From(problem));
        var sol = ctx.SolveAt(PlanarFrequencyKernel.Fit(problem, f), f);

        double asym = (sol.Y[0, 1] - sol.Y[1, 0]).Magnitude / sol.Y[0, 1].Magnitude;
        Assert.True(asym < 1e-9, $"Y must be symmetric to the LU's tolerance: {asym:E3}");

        int horizontal = report.UnknownCount - report.ViaUnknownCount;
        double biggestVia = 0;
        for (int b = horizontal; b < report.UnknownCount; b++)
            biggestVia = Math.Max(biggestVia, sol.Currents[0][b].Magnitude);
        Assert.True(biggestVia > 0, "no current reached the via — the multi-level solve is vacuous");

        _out.WriteLine($"N = {report.UnknownCount}: |Y12 − Y21|/|Y12| = {asym:E3}; the largest via " +
                       $"basis current with port 1 driven is {biggestVia:E3} A.");
    }

    // =========================================================================================
    // M3 / Tier 3 — de-embedding, through the new code path, against L8d's own numbers.
    // =========================================================================================

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M3_1_TierThree_TheGENERALPathReproducesTheShippedOne_OnAUniformSingleLevelLine()
    {
        // "This is what catches a port-indexing error, because a mis-indexed port on a ONE-LEVEL mesh
        // is still a wrong answer." Both sides de-embed the SAME uniform line with the SAME ports;
        // only the kernel differs — the shipped one-slab fit versus the general stack's interior fit
        // of the identical medium (LayerStack.FromGroundedSlab). L9c's M5_1 measured the FILL that
        // way at 6.8e-7; this measures the whole de-embedded s-parameter.
        var slab = GroundedSlab.Fr4Starter;
        var line = PlanarLineFixtures.Fr4Line(8e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(line);
        double[] freqs = [2e9, 6e9];

        var shipped = PlanarSolve.Run(mesh, ports, slab, freqs);

        // The SAME problem — same polygons, same mesh, same ports — forced onto the general path by
        // naming the medium explicitly. Deriving it from the fixture rather than restating the
        // geometry is what makes "only the kernel differs" true rather than nearly true.
        var general = line with
        {
            MediumStack = LayerStack.FromGroundedSlab(slab),
            Layers      = [line.Layers[0] with { ZM = slab.HeightM }],
        };
        Assert.True(general.RequiresGeneralKernel);
        var generalRun = PlanarSolve.Run(general, mesh, ports, freqs);

        double worst = 0;
        for (int i = 0; i < freqs.Length; i++)
        for (int a = 0; a < 2; a++)
        for (int b = 0; b < 2; b++)
            worst = Math.Max(worst,
                (shipped.Points[i].S[a, b] - generalRun.Points[i].S[a, b]).Magnitude);

        _out.WriteLine($"N = {mesh.Bases.Count}, de-embedded through BOTH paths at 2 and 6 GHz: worst " +
                       $"|ΔS| = {worst:E3}. Shipped S21(2 GHz) = {shipped.Points[0].S[1, 0]}, general " +
                       $"S21(2 GHz) = {generalRun.Points[0].S[1, 0]}.");
        Assert.True(worst < 5e-3,
            $"the general path must reproduce the shipped de-embedded answer: worst |ΔS| = {worst:E3}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // a full de-embedded two-level point, 1 m 23 s
    public void M3_2_D3_APortOnALevelThatIsNotTheSlabsTop_IsRefusedByName_AndTheNeighbourRuns()
    {
        // The refusal is on Z_c's own electrostatics rather than on "multi-level", and R-mlp-3 wants
        // its legitimate neighbour accepted in the same test. A port on M1 (which IS the slab's top
        // surface) de-embeds; the SAME structure with the ports brought out on M2 does not, because
        // C_pul would be an image series solving a different electrostatic problem and the de-embedded
        // S is REFERENCED to the Z_c it produces.
        double f = 10e9, len = 400e-6;
        var problem = TwoLevel(f, lengthM: len);
        var mesh = SurfaceMesher.Mesh(problem).Mesh;

        var onM2 = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 1));
        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanarSolve.Run(problem, mesh, onM2, [f]));
        Assert.Contains("Z_c", ex.Message);
        // L9e's refusal audit (M4) re-worded this message — it used to point at "L9c's un-run Tier 4"
        // and now names the missing OBJECT instead (a static Green's function at interior heights, and
        // the fact that LayeredStaticGreens refuses one). The assertion is UPDATED to the claim the
        // message actually makes rather than loosened: a refusal that does not say what is missing is
        // the thing R-mom-17 exists to prevent. Found by re-running the Benchmark tier, which is why
        // this was still asserting the old phrasing.
        Assert.Contains("INTERIOR heights", ex.Message);

        // …the neighbour, on the level that sits on the slab, runs to a real de-embedded answer.
        var onM1 = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));
        var run = PlanarSolve.Run(problem, mesh, onM1, [f]);
        Assert.Single(run.Points);
        Assert.True(double.IsFinite(run.Points[0].S[1, 0].Magnitude));

        _out.WriteLine($"M2 ports refused; M1 ports de-embed to S21 = {run.Points[0].S[1, 0]}.\n" +
                       ex.Message);
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // a full de-embedded two-level point, 1 m 17 s
    public void M3_3_TheStandardsAreSINGLELEVEL_AndTheRunSaysSo()
    {
        // D3 as a property of the objects rather than a promise: every standard's mesh names exactly
        // one layer and carries no vertical basis, whatever the DUT does. A standard with a via in it
        // is not a standard.
        double f = 10e9, len = 400e-6;
        var problem = TwoLevel(f, lengthM: len);
        var mesh = SurfaceMesher.Mesh(problem).Mesh;
        var ports = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));

        var cal = new PlanarPortCalibrator(ports[0], problem.Slab, f, f, null, null,
                                           standardLevelZ: problem.LevelZ(0));
        foreach (var std in cal.Standards)
        {
            Assert.Single(std.Mesh.LayerNames);
            Assert.All(std.Mesh.Cells, c => Assert.Equal(0, c.LayerIndex));
            Assert.DoesNotContain(std.Mesh.Bases, b => b.Direction == PlanarBasisDirection.Z);
        }

        var run = PlanarSolve.Run(problem, mesh, ports, [f]);
        Assert.Contains(run.Notes, n => n.Contains("SINGLE-LEVEL"));
        _out.WriteLine(run.Notes.First(n => n.Contains("SINGLE-LEVEL")));
    }

    // =========================================================================================
    // M5 — the vertical current map (D4), the G_A^zz refusal, and the capability.
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]   // a multi-level fill + solve, 25 s
    public void M5_1_D4_TheVerticalMapIsItsOwnQUANTITY_AndIsNeverFoldedIntoJ()
    {
        // D4. A via's current crosses an AREA, so what is well defined per cell is a current in
        // amperes rather than a sheet density in A/m — and |J| would then be adding two dimensions
        // and colouring the result. It is carried separately, non-zero on exactly the via's two foot
        // cells, and the horizontal map is bit-identical to what a via-free mesh would have produced
        // for the same coefficients.
        double f = 10e9, len = 400e-6;
        var problem = TwoLevel(f, lengthM: len);
        var report = SurfaceMesher.Mesh(problem);
        var mesh = report.Mesh;
        var ports = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));

        var ctx = new PlanarSolveContext(mesh, ports, null, PlanarLevels.From(problem));
        var sol = ctx.SolveAt(PlanarFrequencyKernel.Fit(problem, f), f);
        var map = PlanarCurrentDensity.Compute(mesh, sol.Currents[0], 1, f);

        Assert.True(map.HasVerticalCurrent, "the fixture must carry via current");
        Assert.True(map.MaxViaCurrent > 0);

        // Non-zero on exactly the foot cells of the vertical bases, and nowhere else.
        var feet = new HashSet<int>();
        foreach (var b in mesh.Bases)
            if (b.Direction == PlanarBasisDirection.Z) { feet.Add(b.CellA); feet.Add(b.CellB); }
        Assert.NotEmpty(feet);
        for (int c = 0; c < mesh.Cells.Count; c++)
            Assert.Equal(feet.Contains(c), map.ViaCurrent(c).Magnitude > 0);

        // …and it is NOT in |J|: recomputing |J| from Jx/Jy alone reproduces Magnitude exactly.
        for (int c = 0; c < mesh.Cells.Count; c++)
        {
            double fromXy = Math.Sqrt(map.Jx[c].Magnitude * map.Jx[c].Magnitude +
                                      map.Jy[c].Magnitude * map.Jy[c].Magnitude);
            Assert.Equal(fromXy, map.Magnitude[c]);
        }

        Assert.Contains("amperes", map.VerticalScaleCaption);
        _out.WriteLine($"{feet.Count} foot cell(s); |I_z| peaks at {Eng(map.MaxViaCurrent)}A " +
                       $"against |J| peaking at {Eng(map.MaxMagnitude)}A/m — different " +
                       $"dimensions, separately normalised.\n{map.VerticalScaleCaption}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // one two-level solve at 100 GHz, 8 s
    public void M5_2_TheGAzzRangeIsAREFUSAL_AndItBindsOnlyWhereThereIsVerticalCurrent()
    {
        // §0.2 item 4. The fill has been asking PlanarKernelSet.WithinValidatedRange since L9c and
        // nothing acted on the answer. It is a refusal rather than a note because, unlike R-prt-13's,
        // it is worded on the SCALED error a fill actually experiences and G_A^zz reaches 14× the
        // free-space kernel beyond it — a complete, smooth, plausible, wrong s-parameter set.
        //
        // Earned in R-mlp-3's sense: the SAME structure without a via is accepted at the same size,
        // so the refusal is scoped to the block it actually binds (the ẑẑ one) and not to
        // "multi-level".
        // 100 GHz rather than 10: the SAME 400 µm structure is then 0.137 λ across, past the
        // validated 0.1, while staying a handful of cells — so the refusal is exercised on a mesh
        // that costs nothing rather than on one built to be big.
        double f = 100e9;
        double len = 400e-6;
        double lambda = EmConstants.C0 / f;

        var big = TwoLevel(f, withVia: true, lengthM: len);
        var mesh = SurfaceMesher.Mesh(big).Mesh;
        var ports = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));

        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanarSolve.Run(big, mesh, ports, [f], new PlanarSolveSettings(Deembed: false)));
        Assert.Contains("0.1", ex.Message);
        Assert.Contains("VerticalVectorPotential", ex.Message);

        // The neighbour: the same size, the same medium, the same two levels — no via, so no ẑẑ block.
        var noVia = TwoLevel(f, withVia: false, lengthM: len);
        var m2 = SurfaceMesher.Mesh(noVia).Mesh;
        Assert.DoesNotContain(m2.Bases, b => b.Direction == PlanarBasisDirection.Z);
        var p2 = PlanarPorts.ResolveAll(m2, EndPorts(len, layer: 0));
        var ok = PlanarSolve.Run(noVia, m2, p2, [f], new PlanarSolveSettings(Deembed: false));
        Assert.Single(ok.Points);

        _out.WriteLine($"{Eng(len)}m at {Eng(f)}Hz is ρ/λ ≈ " +
                       $"{Math.Sqrt(len * len + 1e-8) / lambda:G3}. With a via: refused. Without one " +
                       $"(N = {m2.Bases.Count}): " +
                       $"solved, S21 = {ok.Points[0].S[1, 0]}.\n{ex.Message}");
    }

    [Fact]
    public void M5_3_TheRegistryDeclaresLayeredWithVias_AndTheKernelIsWhereItComesFrom()
    {
        // §0.2 item 5: the flag has been declared since L6 and read by nothing, and L9c deliberately
        // still did not wire it because there was no solve to be honest about. There is one now.
        // D2's rule is untouched — auto-selection still takes extractor VERDICTS, not geometry.
        Assert.True(new PlanarKernel().Capabilities.HasFlag(EmCapabilities.LayeredWithVias));
        Assert.True(EmKernelRegistry.Planar.Capabilities.HasFlag(EmCapabilities.LayeredWithVias));
        Assert.True(EmKernelRegistry.Planar.Capabilities.HasFlag(EmCapabilities.Planar));
        Assert.False(EmKernelRegistry.CrossSection.Capabilities.HasFlag(EmCapabilities.LayeredWithVias));

        // Describe() still resolves the kind through the flag, so the two cannot drift.
        Assert.Equal(PlanarKernel.KernelName, EmKernelRegistry.Describe(EmAnalysisKind.Planar).Name);
        _out.WriteLine($"Planar kernel capabilities: {EmKernelRegistry.Planar.Capabilities}");
    }

    [Fact]
    public void M5_4_AVIALONGENOUGHToNeedItsZIntegral_IsRefusedByTheKERNEL_NotJustByPlanarLevels()
    {
        // L9c measured the midpoint rule and refused a long via inside PlanarLevels; L9d is the first
        // slice with a caller, so the refusal has to reach one. R-mlp-3's neighbour: the 3 µm MMIC
        // spacer at the same frequency is accepted.
        var kernel = new PlanarKernel();
        Assert.True(kernel.CanSolve(TwoLevel()).Ok);

        var stack = new LayerStack(
            Termination.Pec,
            [new MediumLayer(100e-6, new EmMaterial(12.9, 0.002)),
             new MediumLayer(5e-3,   new EmMaterial(2.7,  0.002))],
            Termination.Air);
        double zLow = stack.InterfaceZ[1], zHigh = stack.TopZ;
        var longVia = new PlanarProblem(
            [new PlanarConductorLayer("M1", [Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 2e-6, zLow),
             new PlanarConductorLayer("M2", [Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 3e-6, zHigh)],
            GroundedSlab.GaAsStarter, 10e9, null, stack,
            [new PlanarVia(0, 1, [Rect(180e-6, 30e-6, 220e-6, 70e-6)], 4.1e7)]);

        var no = kernel.CanSolve(longVia);
        Assert.False(no.Ok);
        // UPDATED, not loosened: the refusal used to be about the MIDPOINT RULE's O((kℓ)²). The
        // z-integral is resolved now, and what k·ℓ ≤ 0.05 still bounds is L9c's BASIS — one z-rooftop
        // per gap, so a uniform current along the via.
        Assert.Contains("UNIFORM", no.Reason);
        _out.WriteLine(no.Reason!);
    }

    // =========================================================================================
    // The two MEASUREMENTS §8 asks for, whatever else happens: the de-embedding drift and the cost.
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]   // three de-embedded points on a two-level medium, ~2 min
    public void M3_4_TheDeembeddingDRIFT_OnAUniformSectionInATwoLevelMedium_AgainstL8dsOwnNumbers()
    {
        // §8 item 3, measured L8d's own way rather than argued. L8d's finding was that what limits
        // de-embedding is RADIATION, not the algebra: the de-embedded S of a uniform section is exact
        // at the two lengths the calibration was solved from and drifts away from them, as f². The
        // question here is whether de-embedding a DUT in the two-level medium against SINGLE-LEVEL
        // standards on the port's own level adds anything on top of that.
        //
        // The DUT is a uniform line on M1 in the MmicTwoLevel medium — a section that should be
        // matched — so |S11| is the whole residual, exactly as L8d's T4_5 reads it.
        double len = 400e-6;
        double[] freqs = [10e9, 20e9, 40e9];
        var problem = TwoLevel(freqs[^1], withVia: false, upperEmpty: true, lengthM: len);
        var mesh = SurfaceMesher.Mesh(problem).Mesh;
        var ports = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));

        var run = PlanarSolve.Run(problem, mesh, ports, freqs);

        _out.WriteLine($"N = {mesh.Bases.Count}, {run.StandardCount} single-level standard mesh(es).");
        _out.WriteLine("  f        |S11| de-embedded   |S21|      ε_eff     Z_c");
        double worst = 0;
        foreach (var pt in run.Points)
        {
            double s11 = pt.S[0, 0].Magnitude;
            worst = Math.Max(worst, s11);
            var c = pt.Calibrations[0];
            _out.WriteLine($"  {pt.FrequencyHz / 1e9,5:F1} GHz  {s11:E3}          " +
                           $"{pt.S[1, 0].Magnitude:F4}   {c.Gamma.EffectivePermittivity(pt.FrequencyHz):F4}   {c.Zc}");
        }

        _out.WriteLine($"\nWorst |S11| on a section that should be matched: {worst:E3}. L8d measured " +
                       $"3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz on 1.6 mm FR-4 through the SHIPPED " +
                       $"one-slab kernel, and found the mechanism to be radiation scaling as f². " +
                       $"This structure is 400 µm on 100 µm GaAs, i.e. far shorter in guided " +
                       $"wavelengths than L8d's own fixture, so the two numbers are not directly " +
                       $"comparable in magnitude — what IS comparable is the SHAPE: the residual here " +
                       $"is monotone in frequency, which is the same radiative signature and not an " +
                       $"algebraic error introduced by calibrating a two-level medium against " +
                       $"single-level standards.");

        // The gate is deliberately loose and one-sided: this is a REPORTED measurement, and turning
        // it into a tight threshold would be gating on the radiative floor L8d already showed is not
        // a convergence.
        Assert.True(worst < 0.2,
            $"a uniform section must still de-embed to something recognisably matched: |S11| = {worst:E3}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // one de-embedded two-level point with a via, ~1.5 min
    public void M5_5_D7_TheCOSTOfADeembeddedTwoLevelPoint_MeasuredRatherThanProjected()
    {
        // §8 item 5 and D7. L9c measured N (2.07× for two levels with a via) and the fit count (9)
        // but NOT the seconds, and warned that L9a's projection from a per-sample number was wrong by
        // 15-35×. L8d measured 7.66 s per de-embedded point at N = 552, 78% of it in the standards.
        //
        // TAKE THIS MEASUREMENT ALONE OR NOT AT ALL — L8d's own warning, and it applies here for the
        // same reason: run alongside the other Benchmark tests it reads more than twice as slow.
        double len = 400e-6, f = 10e9;
        var problem = TwoLevel(f, withVia: true, lengthM: len);
        var report = SurfaceMesher.Mesh(problem);
        var mesh = report.Mesh;
        var ports = PlanarPorts.ResolveAll(mesh, EndPorts(len, layer: 0));

        var sw = Stopwatch.StartNew();
        var run = PlanarSolve.Run(problem, mesh, ports, [f]);
        double total = sw.Elapsed.TotalSeconds;

        var pt = run.Points[0];
        double n = report.UnknownCount;
        _out.WriteLine($"Two levels + one via, N = {report.UnknownCount} ({report.ViaUnknownCount} " +
                       $"vertical), {run.StandardCount} single-level standard(s), {run.CoreFillCount} " +
                       $"geometric core(s):");
        _out.WriteLine($"  kernel fits   {pt.KernelFitMs / 1000.0,7:F2} s");
        _out.WriteLine($"  the DUT       {pt.DutMs / 1000.0,7:F2} s");
        _out.WriteLine($"  the standards {pt.CalibrationMs / 1000.0,7:F2} s");
        _out.WriteLine($"  cores (once)  {run.CoreBuildMs / 1000.0,7:F2} s");
        _out.WriteLine($"  TOTAL         {total,7:F2} s for ONE de-embedded point");
        _out.WriteLine($"  → a 101-point sweep projects to {(total - run.CoreBuildMs / 1000.0) * 101 / 60.0:F1} " +
                       $"minutes plus {run.CoreBuildMs / 1000.0:F2} s of cores.");
        _out.WriteLine($"\nAgainst L8d's own 7.66 s per de-embedded point at N = 552 (~780 s for 101 " +
                       $"points), of which 78% was the standards. This structure is N = {n:F0}, so the " +
                       $"comparison worth making is per-unknown rather than absolute — see the phase " +
                       $"note for what the general kernel costs over the shipped one at equal N.");

        Assert.True(total > 0);
        Assert.Single(run.Points);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static string Eng(double v) => v.ToString("G4");

    private static Mat<Complex> FillOf(PlanarProblem problem, PlanarMesh mesh, double fHz)
    {
        var cores = PlanarFill.BuildCores(mesh);
        var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, fHz))
                      .For(cores);
        return PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * fHz);
    }

    // =========================================================================================
    // M0 (brief-gazz-accuracy-ceiling) — IS THE REFUSAL ASKING THE RIGHT QUESTION?
    //
    // Dcim.ValidatedRhoOverLambdaAtHeights = 0.1 is the single limit that stops a via-bearing
    // full-wave run on ordinary board geometry. It governs G_A^zz, which is consumed in exactly one
    // place — PlanarFill's `zi && zj` arm — and was asked of the MESH DIAGONAL. Those are different
    // quantities, and on board-scale geometry they differ by more than the limit itself.
    // =========================================================================================

    /// <summary>
    /// <b>§10.7's own FR-4 hero, given a second level and a via.</b> 2.9 × 20 mm on 1.6 mm FR-4 —
    /// the geometry the design note's worked example is written on, and the one the limit refuses.
    /// The slab is split so the lower level has an interface to sit on; both halves are the same
    /// FR-4, so the medium is physically the hero's own (the same device
    /// <c>LayerStacks.AirOverGround</c> uses to exercise interior interfaces that are invisible).
    /// </summary>
    private static PlanarProblem Fr4HeroWithVias(double fHz, params double[] viaCentresM)
    {
        var fr4   = new EmMaterial(4.4, 0.02);
        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(1.5e-3, fr4), new MediumLayer(0.1e-3, fr4)], Termination.Air);

        const double w = 2.9e-3, len = 20e-3;
        var lower = new PlanarConductorLayer("L1", [Rect(0, 0, len, w)], 5.8e7, 35e-6, stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("L2", [Rect(0, 0, len, w)], 5.8e7, 35e-6, stack.TopZ);

        var vias = viaCentresM
            .Select(cx => new PlanarVia(0, 1,
                [Rect(cx - 0.25e-3, 0.5 * w - 0.25e-3, cx + 0.25e-3, 0.5 * w + 0.25e-3)], 5.8e7))
            .ToArray();

        return new PlanarProblem([lower, upper], new GroundedSlab(1.6e-3, fr4), fHz, null, stack, vias);
    }

    [Fact]
    public void M0_1_RzZ1_TheRefusalIsScopedToTheVIAS_AndTheFr4HeroWithAViaNowRUNS()
    {
        // §9 item 1 and item 6, which are the whole brief in one table.
        const double f = 10e9;
        double lambda = EmConstants.C0 / f;

        (string Label, double[] Vias)[] cases =
        [
            ("one via, mid-board",       [10.0e-3]),
            ("two vias, 1 mm apart",     [9.5e-3, 10.5e-3]),
            ("two vias, 18 mm apart",    [1.0e-3, 19.0e-3]),
        ];

        _out.WriteLine($"§10.7's FR-4 hero (2.9 × 20 mm on 1.6 mm FR-4), two levels, at {f / 1e9:F0} GHz. " +
                       $"λ₀ = {lambda * 1e3:F0} mm, and the limit is ρ/λ ≤ {Dcim.ValidatedRhoOverLambdaAtHeights}.");
        _out.WriteLine("  layout                     N     mesh diagonal   OLD ρ/λ   via extent   NEW ρ/λ   " +
                       "verdict: before → after");

        bool heroRuns = false;
        foreach (var (label, vias) in cases)
        {
            var problem = Fr4HeroWithVias(f, vias);
            var mesh    = SurfaceMesher.Mesh(problem).Mesh;

            double diag     = PlanarSolve.Diagonal(mesh);
            double vertical = PlanarSolve.VerticalExtent(mesh);
            var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f));

            bool oldOk = set.WithinValidatedRange(diag).Ok;         // what the refusal used to ask
            var  now   = PlanarSolve.VerticalRangeVerdict(problem, mesh, f);

            _out.WriteLine($"  {label,-24} {mesh.Bases.Count,-5} {diag * 1e3,10:F2} mm   " +
                           $"{diag / lambda,7:F3}   {vertical * 1e3,7:F3} mm   {vertical / lambda,7:F3}   " +
                           $"{(oldOk ? "PASS" : "REFUSED"),-8} → {(now.Verdict.Ok ? "PASS" : "REFUSED")}");

            if (label.StartsWith("one via", StringComparison.Ordinal))
            {
                // THE HEADLINE. The old question refused it on a separation the kernel is never
                // asked about; the new one asks about the via's own footprint.
                Assert.False(oldOk, "the fixture must be one the OLD question refused, or this " +
                                    "measures nothing");
                Assert.True(now.Verdict.Ok, "§10.7's FR-4 hero with a via must now run: " + now.Verdict.Reason);
                heroRuns = true;
            }

            if (label.Contains("18 mm", StringComparison.Ordinal))
            {
                // …and two vias genuinely far apart still refuse, because there the fit really IS
                // asked about that ρ. Narrowing the question is not widening the answer (D2).
                Assert.False(now.Verdict.Ok, "two vias 18 mm apart at 10 GHz must still refuse");
                Assert.Contains("between VIAS", now.Verdict.Reason!, StringComparison.Ordinal);
                Assert.Contains("NOT what is refused", now.Verdict.Reason!, StringComparison.Ordinal);
                _out.WriteLine("\n  the refusal, which must name what the separation is BETWEEN so a user " +
                               "can act on it:\n  " + now.Verdict.Reason);
            }
        }

        Assert.True(heroRuns);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M0_2_TIER1_TheFillNeverAsksGAzzAboutMoreThanTheRefusalUSED()
    {
        // Tier 1, and it is instrumented rather than read off the code — which is the whole
        // difference the scoping change turns on. If the fill ever consulted G_A^zz at a wider
        // separation than the refusal checked, the narrowing would be unsound and nothing else here
        // would notice.
        var problem = TwoLevel();
        var mesh    = SurfaceMesher.Mesh(problem).Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        double f    = 10e9;
        var set     = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);

        var diag = new PlanarFillDiagnostics();
        PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * f, diag);

        double used = PlanarSolve.VerticalExtent(mesh);
        Assert.True(diag.MaxVerticalPairRhoM > 0, "the fixture must actually exercise the ẑẑ arm");
        Assert.True(diag.MaxVerticalPairRhoM <= used * (1 + 1e-12),
            $"the fill asked G_A^zz about {diag.MaxVerticalPairRhoM:E6} m, past the " +
            $"{used:E6} m the refusal checked — the scoping is UNSOUND");

        // …and it is not merely an upper bound with slack: with every ẑẑ pair computed, the two
        // extreme via cells ARE a pair, so the number the refusal uses is the one the fill reaches.
        Assert.Equal(used, diag.MaxVerticalPairRhoM, 12);

        _out.WriteLine($"the fill's widest G_A^zz query is {diag.MaxVerticalPairRhoM * 1e6:F2} µm; " +
                       $"the refusal checked {used * 1e6:F2} µm — equal, not merely bounded. " +
                       $"(The mesh itself is {PlanarSolve.Diagonal(mesh) * 1e6:F0} µm across.)");
    }

    [Fact]
    public void M0_3_TIER2_NothingIsLeftUNGOVERNED_ByNarrowingTheQuestion()
    {
        // D2. Scoping G_A^zz to the via footprints leaves G_A^xx, G_q and the MIXED component's
        // interior pairings checked by nothing — and the mixed block couples a via to EVERY
        // horizontal basis, so its ρ genuinely spans the mesh. They do not need a refusal, and the
        // measured number is what says so rather than an assumption; the run must SAY it either way.
        const double f = 10e9;

        var small = Fr4HeroWithVias(f, 10.0e-3);
        var meshS = SurfaceMesher.Mesh(small).Mesh;
        var okS   = PlanarSolve.VerticalRangeVerdict(small, meshS, f);
        Assert.True(okS.Verdict.Ok);
        Assert.Contains(okS.Notes, n => n.Contains("G_A^xx / G_q / mixed", StringComparison.Ordinal)
                                     && n.Contains("inside", StringComparison.Ordinal));

        // …and past the measured envelope it says PAST, rather than going quiet. 20 mm at 20 GHz is
        // ρ/λ = 1.35, beyond anything L9c's Tier 5 measured for these three components.
        var big   = Fr4HeroWithVias(20e9, 10.0e-3);
        var meshB = SurfaceMesher.Mesh(big).Mesh;
        var okB   = PlanarSolve.VerticalRangeVerdict(big, meshB, 20e9);
        Assert.True(okB.Verdict.Ok, "an unmeasured range is a NOTE, not a refusal: " + okB.Verdict.Reason);
        Assert.Contains(okB.Notes, n => n.Contains("PAST", StringComparison.Ordinal)
                                     && n.Contains("unmeasured", StringComparison.Ordinal));

        Assert.Equal(1.0, Dcim.ValidatedRhoOverLambdaInteriorHorizontal);
        foreach (var n in okB.Notes) _out.WriteLine("  " + n);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M0_4_TIER4_RzZ5_NoStructureAcceptedTodayMOVED_ByOneUlp()
    {
        // D6 / R-zz-5, pinned by RECONSTRUCTION at full precision rather than by a tolerance.
        //
        // M0 changed which ρ a REFUSAL is asked about and added an optional diagnostic to the fill.
        // Neither may move a number. Two claims, and the second is the one worth stating: attaching
        // the Tier 1 instrument must not perturb the arithmetic it is measuring.
        var problem = TwoLevel();
        var mesh    = SurfaceMesher.Mesh(problem).Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        double f    = 10e9;
        var levels  = PlanarLevels.From(problem);

        var setA = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);
        var bare = PlanarFill.FillMultiLevel(cores, setA, levels, 2 * Math.PI * f);

        var setB = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);
        var instrumented = PlanarFill.FillMultiLevel(cores, setB, levels, 2 * Math.PI * f,
                                                     new PlanarFillDiagnostics());

        for (int i = 0; i < bare.RowCount; i++)
        for (int j = 0; j < bare.ColCount; j++)
            Assert.Equal(bare[i, j], instrumented[i, j]);

        // …and a structure that passed the OLD question still passes the new one, necessarily: the
        // vertical extent is a subset of the mesh, so narrowing can only ever accept MORE. Asserted
        // rather than argued, on the fixture every L9d test is measured on.
        Assert.True(PlanarSolve.VerticalExtent(mesh) <= PlanarSolve.Diagonal(mesh));
        Assert.True(PlanarSolve.VerticalRangeVerdict(problem, mesh, f).Verdict.Ok);

        _out.WriteLine($"N = {bare.RowCount}: the fill is bit-identical with and without the Tier 1 " +
                       $"instrument, and the narrowed question can only ever accept more " +
                       $"({PlanarSolve.VerticalExtent(mesh) * 1e6:F1} µm ≤ " +
                       $"{PlanarSolve.Diagonal(mesh) * 1e6:F1} µm).");
    }

    // =========================================================================================
    // M2 (brief-gazz-accuracy-ceiling) — DIRECT INTEGRATION FOR THE ẑẑ BLOCK ALONE
    //
    // NAMED "ZzM2_…" rather than "M2_…" ON PURPOSE: this file already carries L9d's own M2_1/M2_2/
    // M2_3 (its port milestone), and two briefs' M2 in one file is exactly the kind of ambiguity
    // that gets a test read as evidence for the wrong claim.
    //
    // M1's verdict was decisive and negative: three of the five DcimSettings knob groups the brief
    // names are STRUCTURALLY INERT on the interior path (Dcim.FitAtHeights never reads
    // BranchPointOrders, BranchSamples or BranchExtent — the interior sum rule is a theorem by
    // inspection, so there is no branch-point sampling to configure), and the reachable knobs give
    // 10.4× at best — 14 → 1.35 — still 71× outside the ≤ 1.9e-2 envelope, while making the error
    // 23× WORSE inside ρ/λ ≤ 0.1 where the kernel is used today. The fit is the failure, so M2
    // replaces the fit rather than tuning it.
    // =========================================================================================

    /// <summary>Two vias 18 mm apart on §10.7's own FR-4 hero — the ONE layout M0 left refused —
    /// on a deliberately coarse mesh so the ẑẑ block is cheap while ρ/λ stays at 0.617.</summary>
    private static PlanarProblem TwoFarVias(double fHz)
        => Fr4HeroWithVias(fHz, 1.0e-3, 19.0e-3);

    /// <summary>
    /// The SAME layout on a GaAs slab — the medium and pairing L9c's Tier 5 measured the interior
    /// G_A^zz fit at its WORST (14× the free-space kernel at ρ/λ = 1, low–low). Physically a die
    /// this wide is unrealistic; that is not the point. The refusal fires on ρ/λ whatever the
    /// medium, and this is the medium the number that justifies it was measured in — so it is where
    /// "does the pointwise kernel error reach the assembled block?" has to be asked.
    /// </summary>
    private static PlanarProblem TwoFarViasGaAs(double fHz)
    {
        var gaas  = new EmMaterial(12.9, 0.006);
        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(97e-6, gaas), new MediumLayer(3e-6, gaas)], Termination.Air);

        const double w = 2.9e-3, len = 20e-3;
        var lower = new PlanarConductorLayer("L1", [Rect(0, 0, len, w)], 4.1e7, 2e-6, stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("L2", [Rect(0, 0, len, w)], 4.1e7, 2e-6, stack.TopZ);

        var vias = new[] { 1.0e-3, 19.0e-3 }
            .Select(cx => new PlanarVia(0, 1,
                [Rect(cx - 0.25e-3, 0.5 * w - 0.25e-3, cx + 0.25e-3, 0.5 * w + 0.25e-3)], 4.1e7))
            .ToArray();

        return new PlanarProblem([lower, upper], new GroundedSlab(100e-6, gaas), fHz, null, stack, vias);
    }

    // ── THE GaAs CROSS-CHECK: MEASURED AS UNAFFORDABLE, WITH THE NUMBERS ─────────────────────
    //
    // ZzM2_1's conclusion — the FITTED ẑẑ block is 4.53e-7 from the direct one on the board M0 left
    // refused — is ONE layout on ONE stack, and L9c's Tier 5 says the stack matters enormously
    // (FR-4 low–low 1.1e-1 at ρ/λ = 1 against GaAs low–low **14**). Asking the same question on GaAs
    // was named as the most valuable next measurement here. It was attempted twice and it is NOT
    // affordable, and this is now a measurement rather than "it did not finish":
    //
    //   fixture: two 200 µm pads 18 mm apart on 100 µm GaAs, each pad its own via footprint, the
    //            CoarseForZz mesh — i.e. the smallest fixture that reaches ρ/λ = 0.607 at all.
    //   N = 738, 324 cells, **162 VERTICAL bases**, ρ/λ = 0.607
    //     cores                     10.5 s
    //     FITTED fill              236.8 s
    //     DIRECT fill, 32 samples  828.3 s      ← the COARSEST rung of the convergence ladder
    //
    // A comparison needs the fitted fill plus at least two direct rungs (an oracle that has not been
    // shown to have stopped moving is not an oracle — ZzM2_1 measured that conflation overstating
    // the fit's error by two decades), so the honest cost is **45+ minutes** against the Engine
    // opt-in tier's whole ~40.
    //
    // WHAT DRIVES IT, because it is not what the first attempt assumed. It is not the unknown count
    // and it is not the physical size: the SAME fixture at 100 GHz and 1.8 mm — a tenth of the
    // extent, the same ρ/λ — meshes to the identical N = 738 with the identical 162 vertical bases,
    // because the mesh is scale-free. It is the VERTICAL BASIS COUNT: the ẑẑ block is 162² entries
    // and the mixed block integrates a derivative against every horizontal basis, against L9d's own
    // measured fixture which had TWO. And a via footprint cannot be shrunk relative to its pad
    // without leaving a sliver run beside it, which drives the pitch down and the count back up.
    //
    // So the affordable version of this question needs a fixture with ~2 vertical bases at
    // ρ/λ ≈ 0.6, which needs a mesher whose pitch is not tied to the via footprint — not a smaller
    // number here. **The FR-4 conclusion therefore stands as being about FR-4**, and the refusal
    // stays where L9c measured it.

    private static readonly PlanarMeshSettings CoarseForZz =
        new(Auto: true, CellsPerWavelength: 4, EdgeMesh: false);

    /// <summary>The assembled ẑẑ submatrix — every entry between two vertical bases, which is the
    /// ONLY place G_A^zz is ever evaluated.</summary>
    private static Complex[,] ZzBlock(PlanarProblem problem, PlanarMesh mesh, PlanarFillSettings st)
    {
        double fHz  = problem.MaxFrequencyHz;
        var cores   = PlanarFill.BuildCores(mesh, st);
        var set     = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, fHz), st.Order)
                          .For(cores);
        var z       = PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * fHz);

        var idx = Enumerable.Range(0, mesh.Bases.Count)
                            .Where(k => mesh.Bases[k].Direction == PlanarBasisDirection.Z)
                            .ToArray();
        var block = new Complex[idx.Length, idx.Length];
        for (int a = 0; a < idx.Length; a++)
        for (int b = 0; b < idx.Length; b++)
            block[a, b] = z[idx[a], idx[b]];
        return block;
    }

    private static double WorstRel(Complex[,] a, Complex[,] b)
    {
        double scale = 0, worst = 0;
        for (int i = 0; i < a.GetLength(0); i++)
        for (int j = 0; j < a.GetLength(1); j++)
            scale = Math.Max(scale, a[i, j].Magnitude);
        for (int i = 0; i < a.GetLength(0); i++)
        for (int j = 0; j < a.GetLength(1); j++)
            worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude);
        return scale > 0 ? worst / scale : 0.0;
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // direct integration is 40-50 ms a point, by design
    public void ZzM2_1_RzZ3_TheDirectPath_ItsREQUIREDSampleCount_AndWhatItCosts()
    {
        const double f = 10e9;
        var fitted0 = PlanarFillSettings.Default;

        // ── (0) THE ORACLE CHECK, BEFORE ANY NUMBER BELOW IT IS BELIEVED ──────────────────────
        // This file has now had nine occasions where the "oracle" was the thing that was wrong, so
        // the direct path is checked where the FIT is known good rather than only where it is not:
        // two vias 1 mm apart is ρ/λ = 0.053, inside the 0.1 L9c measured the interior fit at
        // ≤ 2.8e-3 over. If the plumbing carried a sign or a double-subtraction, the two would
        // disagree here by far more than that.
        {
            var nearP    = Fr4HeroWithVias(f, 9.5e-3, 10.5e-3);
            var nearMesh = SurfaceMesher.Mesh(nearP, CoarseForZz).Mesh;
            double rl    = PlanarSolve.VerticalExtent(nearMesh) / (EmConstants.C0 / f);
            double agree = WorstRel(
                ZzBlock(nearP, nearMesh, fitted0),
                ZzBlock(nearP, nearMesh, fitted0 with
                        { DirectVerticalKernel = true, VerticalTableSamples = 128 }));
            _out.WriteLine($"(0) Oracle check — two vias 1 mm apart, ρ/λ = {rl:F3} (INSIDE the " +
                           $"{Dcim.ValidatedRhoOverLambdaAtHeights} limit, where L9c measured the fit " +
                           $"at ≤ 2.8e-3): direct vs fitted ẑẑ block = {agree:E2}\n");
            Assert.True(agree < 5e-2,
                        $"the direct path must reproduce the fitted one where the fit is good: {agree:E2}");
        }

        var problem = TwoFarVias(f);
        var mesh    = SurfaceMesher.Mesh(problem, CoarseForZz).Mesh;
        int vias    = mesh.Bases.Count(b => b.Direction == PlanarBasisDirection.Z);

        double lambda   = EmConstants.C0 / f;
        double vertical = PlanarSolve.VerticalExtent(mesh);
        _out.WriteLine($"§10.7's FR-4 hero, two vias 18 mm apart — the row M0 left REFUSED. " +
                       $"N = {mesh.Bases.Count} ({vias} vertical), ρ/λ = {vertical / lambda:F3} " +
                       $"against the {Dcim.ValidatedRhoOverLambdaAtHeights} limit.\n");

        var fitted = fitted0;

        // ── (a) the REQUIRED sample count, by refining until the assembled block stops moving ──
        // Deliberately NOT the DCIM table's mesh-derived spacing: that is calibrated for a function
        // that costs microseconds, and this one costs 40-50 ms a point.
        _out.WriteLine("(a) The ẑẑ block's own convergence in the table's sample count:");
        _out.WriteLine("  samples   build (s)   worst |ΔZ|/max|Z| vs the next finer");
        int[] ladder = [32, 64, 128, 256, 512];
        var blocks = new Complex[ladder.Length][,];
        var secs   = new double[ladder.Length];
        for (int k = 0; k < ladder.Length; k++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            blocks[k] = ZzBlock(problem, mesh, fitted with
                                { DirectVerticalKernel = true, VerticalTableSamples = ladder[k] });
            secs[k] = sw.Elapsed.TotalSeconds;
        }
        for (int k = 0; k < ladder.Length; k++)
        {
            string d = k + 1 < ladder.Length
                ? $"{WorstRel(blocks[k + 1], blocks[k]):E2}"
                : "(finest)";
            _out.WriteLine($"  {ladder[k],7}   {secs[k],9:F1}   {d}");
        }

        // ── (b) THE QUESTION THE REFUSAL IS ACTUALLY ABOUT ────────────────────────────────────
        //
        // L9c's Tier 5 measured the interior G_A^zz fit POINTWISE — |ΔG| as a fraction of the
        // free-space kernel at one ρ. The refusal is asked of a MESH. Those are not the same
        // question, and this is the first time anyone has asked the second one: how much of that
        // pointwise error survives into the assembled block? Measured on both stacks, because
        // L9c's own table says the answer is medium-dependent (FR-4 low–low 1.1e-1 at ρ/λ = 1,
        // GaAs low–low 14).
        _out.WriteLine("\n(b) The FITTED ẑẑ block against the DIRECT one (which IS the oracle):");
        _out.WriteLine("  stack        ρ/λ     L9c's POINTWISE fit error at ρ/λ = 1   assembled BLOCK error");

        double fitErr = 0;
        // The table is deliberately COARSE here, and the reason is what the comparison is for: it
        // has to separate "the fit is fine" (which turns out to be ~1e-6) from "the fit is broken"
        // (> 1.9e-2), and (a) above measured the block already converged to 2.2e-3 at 32 samples.
        // Precision past that buys nothing this conclusion uses, and it is not free — a GaAs
        // Sommerfeld point costs ~2 s, because L9a measured that stack's surface-wave pole at
        // 2.5e-9 of its own real part off the real axis, which is where the contour runs.
        // NOT RUN: the GaAs row. TwoFarViasGaAs below is the fixture for it and it is left in place,
        // but it was mis-sized and is not affordable as written — a 20 mm board on a 100 µm GaAs
        // slab meshes to N ≈ 1,200, and on that stack EVERY remainder evaluation is expensive (L9a
        // measured its surface-wave pole at 2.5e-9 of its own real part off the real axis), so the
        // FITTED fill is slow too and shrinking the table does not help. Measured: it did not
        // finish in 35 minutes. Asking the same question affordably needs a fixture whose MESH is
        // small while ρ/λ stays ~0.6 — a short, narrow GaAs line at a high frequency — and that is
        // a different fixture rather than a smaller number here. **So the conclusion below is about
        // FR-4 and is not evidence about GaAs**, where L9c's pointwise error is 130× larger.
        foreach (var (name, pr, pointwise, samples) in new (string, PlanarProblem, string, int)[]
                 {
                     ("FR-4 1.6 mm", problem, "1.1e-1", 512),   // = (a)'s finest, reused
                 })
        {
            var m  = SurfaceMesher.Mesh(pr, CoarseForZz).Mesh;
            var fb = ZzBlock(pr, m, fitted);
            // AGAINST THE CONVERGED DIRECT BLOCK, not a fresh coarse one. Comparing the fit against
            // a 128-sample table conflates two different errors and reports the larger: measured,
            // that reads 8.7e-5, which is (a)'s own 128-vs-256 table resolution rather than anything
            // the fit did. The finest table is what isolates the fit.
            var db = ReferenceEquals(pr, problem)
                   ? blocks[^1]
                   : ZzBlock(pr, m, fitted with
                             { DirectVerticalKernel = true, VerticalTableSamples = samples });
            double e = WorstRel(db, fb);
            if (name.StartsWith("FR-4", StringComparison.Ordinal)) fitErr = e;
            _out.WriteLine($"  {name,-12} {PlanarSolve.VerticalExtent(m) / lambda,5:F3}   " +
                           $"{pointwise,-38} {e:E2}   ({samples} samples)");
        }
        _out.WriteLine($"    …against the ≤ 1.9e-2 envelope the other three components meet at ρ/λ = 1.");

        // ── (c) the cost, per pairing per frequency and as a fraction of a de-embedded point ───
        int pairings = fitted.ViaZNodes * fitted.ViaZNodes;
        _out.WriteLine($"\n(c) Cost. n_z = {fitted.ViaZNodes} ⇒ {pairings} height pairings per via span; " +
                       $"one span here.");
        for (int k = 0; k < ladder.Length; k++)
            _out.WriteLine($"    {ladder[k],4} samples: {secs[k],6:F1} s per span per frequency" +
                           $"  =  {secs[k] / 149.9 * 100,5:F1}% of a 149.9 s de-embedded point");

        Assert.True(vias >= 2, "the fixture must carry two vertical bases");
        // The block must converge — that is what makes a sample count reportable at all.
        Assert.True(WorstRel(blocks[^1], blocks[^2]) < 1e-2,
                    $"the ẑẑ block must settle in the sample count: {WorstRel(blocks[^1], blocks[^2]):E2}");
    }

    // =========================================================================================
    // M2's guardrails — the two properties the SETTING has to have, and the settings that would
    // otherwise fail silently.
    // =========================================================================================

    [Trait("Category", "Benchmark")]
    [Fact]
    public void ZzM2_2_RzZ3_DirectVerticalKernel_ChangesNOTHING_WithoutAVerticalBasis()
    {
        // R-zz-3's own guardrail, and the same shape as R-viz-1: a setting that only ever affects
        // G_A^zz must be provably inert where G_A^zz is never evaluated. Asserted as EXACT equality
        // over every entry, on a two-level problem with no vias and on a one-level one — which
        // between them cover every calibration standard (always single-level) and every L8 path.
        const double f = 10e9;
        foreach (var (label, problem) in new (string, PlanarProblem)[]
                 {
                     ("two levels, no via", Fr4HeroWithVias(f)),
                     ("one level",          Fr4OneLevel(f)),
                 })
        {
            var mesh = SurfaceMesher.Mesh(problem, CoarseForZz).Mesh;
            Assert.DoesNotContain(mesh.Bases, b => b.Direction == PlanarBasisDirection.Z);

            var off = ZzFullFill(problem, mesh, PlanarFillSettings.Default);
            var on  = ZzFullFill(problem, mesh, PlanarFillSettings.Default with
                                 { DirectVerticalKernel = true, VerticalTableSamples = 64 });

            for (int i = 0; i < off.RowCount; i++)
            for (int j = 0; j < off.ColCount; j++)
                Assert.Equal(off[i, j], on[i, j]);   // bit-identical, not "close"
            _out.WriteLine($"{label}: {off.RowCount} unknowns, every entry bit-identical.");
        }
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void ZzM2_3_ASettingThatWouldSilentlyZeroTheViaBlock_IsRefused_NotObeyed()
    {
        // ViaZNodes = 0 gives ViaZIntegral.Nodes empty arrays, so the z-average sums nothing and the
        // ẑẑ block comes out ZERO — the vias stop conducting and NOTHING looks wrong. That is the
        // failure mode this area keeps finding, so it is a refusal rather than a silent answer.
        var mesh = SurfaceMesher.Mesh(TwoFarVias(10e9), CoarseForZz).Mesh;

        var zero = Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { ViaZNodes = 0 }));
        Assert.Contains("silently stop carrying current", zero.Message);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { ViaZStaticNodes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { RemainderNodesNear = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { VerticalTableSamples = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { TableCellFraction = 0 }));

        // …and the shipped defaults are of course accepted, or the check would be refusing everything.
        PlanarFill.BuildCores(mesh, PlanarFillSettings.Default);
        _out.WriteLine("Five settings that would silently produce a wrong answer are refused by name; " +
                       "the defaults are accepted.");
    }

    /// <summary>§10.7's hero with no vias at all — the "nothing vertical" control.</summary>
    private static PlanarProblem Fr4OneLevel(double fHz)
    {
        var fr4 = new EmMaterial(4.4, 0.02);
        return new PlanarProblem(
            [new PlanarConductorLayer("L1", [Rect(0, 0, 20e-3, 2.9e-3)], 5.8e7, 35e-6, 1.6e-3)],
            new GroundedSlab(1.6e-3, fr4), fHz);
    }

    private static Mat<Complex> ZzFullFill(PlanarProblem problem, PlanarMesh mesh, PlanarFillSettings st)
    {
        double fHz = problem.MaxFrequencyHz;
        var cores  = PlanarFill.BuildCores(mesh, st);
        var set    = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, fHz), st.Order)
                         .For(cores);
        return PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * fHz);
    }


}
