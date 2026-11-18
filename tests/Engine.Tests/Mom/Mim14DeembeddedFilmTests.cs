// brief-em-mim-14-refloor-the-full-wave-limit.md — THE DE-EMBEDDED LADDER, RE-RUN ON THE REPAIRED
// KERNEL, AND THE FIXTURE THAT CAN CARRY IT.
//
// MIM-12 measured the de-embedded two-port of a thin-film plate pair and found it correct to
// cell/separation 40, sign-INVERTED by 80 and noise at 200; PlanarLevels.FullWaveCellOverSeparation
// was set to 40 on the strength of it. MIM-12a then repaired the kernel underneath — the thin
// region's own image series is peeled out of the DCIM fit and carried in closed form — and did NOT
// re-run the ladder, because the replacement fixture it built read 6-10x low at cell/separation 2,
// where no film is involved at all. So the floor stood on a measurement of a kernel that no longer
// existed, and it was refusing a spiral-plus-capacitor at 66.7.
//
// WHAT THE FIXTURE HAD TO GET RIGHT, AND WHY IT IS MOST OF THIS FILE:
//
//  1. BOTH PORTS ON THE LOWEST LEVEL. A de-embedded edge port above the lowest analysis level is
//     MIM-4's, and MIM-4 shipped — but the topology a user actually draws routes the top plate up
//     and back down to a landing pad beside the bottom one, so both terminals abut the same metal.
//     That is what this fixture does, and it needs nothing MIM-4 added.
//
//  2. THE ELECTROSTATIC CONTROL GOES THROUGH THE RUN'S OWN FIT. Dcim.ForStackAtFrequency, which
//     PlanarFrequencyKernel.Fit calls. A PlanarKernelSet built without it is a different instrument:
//     on this very fixture it reads C/(eps0 epsR A/d) = 0.033 at 1 GHz where the run's own fit reads
//     1.006. MIM-12 step 0's whole finding, one brief on.
//
//  3. THE CAPACITANCE IS Im(-Y21)/w, NEVER -1/(w*Im(-1/Y21)). Both are "the series element read from
//     -1/Y21"; only the first is the quantity an electrostatic C12 is. -Y21 is the pi model's direct
//     branch ADMITTANCE, G + jwC; inverting first and reading the reactance gives C(1 + 1/Q^2) — 12%
//     high at the Q ~ 3 a thin-metal fixture has. T4 pins it.
//
//  4. THE FIXTURE'S OWN SERIES INDUCTANCE IS SEPARATED IN CLOSED FORM, BECAUSE IT CANNOT BE
//     SHORTENED. The calibration refuses feeds closer together than five substrate heights, so a
//     de-embedded two-port of a lumped element on a 103 um substrate necessarily carries ~600 um of
//     line and ~437 pH with it. The branch is L in series with C, so 1/Im(-Y21) = 1/(wC) - wL is
//     EXACTLY linear in w; two-parameter least squares over the three frequencies returns C free of
//     the instrument and returns L, which is a property of the pinned artwork and must come back the
//     same at every rung. Reading one frequency instead measures the fixture: at cell/separation 200
//     with a 4.35 pF plate the 3 GHz point sits on the fixture's own self-resonance and reads 3.9x.
//
// THE RESULT: no rung from cell/separation 0.5 to 200 departs from the electrostatic mutual
// capacitance of the same mesh by more than 10%, and every point is passive. The floor is 200. Both
// ladders and all three controls are tabulated in HISTORY.md §MIM-14; what is here is one test per
// claim, not one per rung.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class Mim14DeembeddedFilmTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // The shipped MMIC technology's own numbers: Metal1's sheet at 103 um and 3 um thick, the
    // capacitor dielectric 0.2 um of epsR 6.8, the MIM plate 0.25 um thick above it.
    private const double Zlow    = 103e-6;
    private const double EpsFilm = 6.80;
    private const double AirCap  = 3e-6;     // fixed air above the upper level, so only d moves
    private const double TLower  = 3e-6;
    private const double TUpper  = 0.25e-6;
    private const double Sigma   = 4.1e7;

    private static readonly double[] Band = [1e9, 2e9, 3e9];

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    private static LayerStack Stack(double d) => new(
        Termination.Pec,
        [
            new MediumLayer(Zlow, new EmMaterial(12.90, 0.0006)),
            new MediumLayer(d,    new EmMaterial(EpsFilm, 0.0010)),
            new MediumLayer(AirCap, new EmMaterial(1.0, 1e-6)),
        ],
        Termination.Air);

    /// <summary>The DCIM settings a RUN uses at this frequency — <c>PlanarFrequencyKernel.Fit</c>'s
    /// own call, and the one MIM-8's capacitance fixture never made.</summary>
    private static DcimSettings AsARunFits(double fHz, LayerStack stack) =>
        Dcim.ForStackAtFrequency(null, 2.0 * Math.PI * fHz / EmConstants.C0, stack.TopZ);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE FIXTURE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A series MIM capacitor whose BOTH feeds arrive on the lowest level. Port 1 feeds the bottom
    /// plate; the top plate straps sideways on the upper level and drops down a via onto a landing
    /// pad on the lowest one, which port 2 feeds.
    ///
    /// <para><b>The mesh does not depend on d at all</b> — d is a z quantity — so the straddling
    /// cell is PINNED across a ladder by construction and cell/separation moves only with the film.
    /// The converse ladder pins d and moves <paramref name="across"/>.</para>
    /// </summary>
    private sealed record Fixture(
        PlanarProblem Problem, PlanarMesh Mesh, IReadOnlyList<PlanarPortResolution> Ports,
        double D, double PlateM, double XSplit)
    {
        public const double FeedW = 40e-6, FeedL = 300e-6, Gap = 20e-6, Pad = 40e-6, Plate = 60e-6;

        public static Fixture Build(double d, int across, double plate = Plate)
        {
            double yc = plate / 2;
            double xa = FeedL, xb = xa + plate, xc = xb + Gap, xd = xc + Pad, len = xd + FeedL;
            var stack = Stack(d);

            var lower = new List<PlanarPolygon>
            {
                Rect(0,  yc - FeedW / 2, xa, yc + FeedW / 2),    // feed 1
                Rect(xa, 0, xb, plate),                          // bottom plate
                Rect(xc, yc - Pad / 2, xd, yc + Pad / 2),        // landing pad
                Rect(xd, yc - FeedW / 2, len, yc + FeedW / 2),   // feed 2
            };
            var upper = new List<PlanarPolygon>
            {
                Rect(xa, 0, xb, plate),                          // top plate
                Rect(xb, yc - Pad / 2, xd, yc + Pad / 2),        // strap onto the pad
            };
            var problem = new PlanarProblem(
                [
                    new PlanarConductorLayer("lower", lower, Sigma, TLower, Zlow),
                    new PlanarConductorLayer("upper", upper, Sigma, TUpper, Zlow + d),
                ],
                GroundedSlab.GaAsStarter, Band[^1], null, stack,
                [new PlanarVia(0, 1, [Rect(xc, yc - Pad / 2, xd, yc + Pad / 2)], Sigma)]);

            var mesh = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(
                Auto: false, CellsPerWavelength: 20, EdgeMesh: false, EdgeCells: 3,
                MinCellsAcrossConductor: across)).Mesh;
            var ports = PlanarPorts.ResolveAll(mesh,
            [
                new PlanarPort(1, new EmPoint(0,   yc), PlanarPortSide.MinX, 50.0, 0),
                new PlanarPort(2, new EmPoint(len, yc), PlanarPortSide.MaxX, 50.0, 0),
            ]);
            return new Fixture(problem, mesh, ports, d, plate, 0.5 * (xb + xc));
        }

        /// <summary>Which terminal a cell belongs to: 0 = port-1 net, 1 = port-2 net.</summary>
        public int NetOf(PlanarCell c) => c.LayerIndex == 0 ? (c.CenterX < XSplit ? 0 : 1) : 1;

        public double CellOverSeparation => PlanarSolve.WorstLevelPair(Problem, Mesh)!.Value.CellOverSeparation;

        /// <summary>
        /// THE CONTROL: the electrostatic mutual capacitance between the two TERMINALS, on this very
        /// mesh, through the run's own kernel — so the plate fringing, the strap, the pad and the
        /// feeds are all common with the two-port reading and cancel out of the comparison.
        /// </summary>
        public double ElectrostaticMutualF(double fHz)
        {
            var st    = PlanarFillSettings.Default;
            var cores = PlanarFill.BuildCores(Mesh, st);
            var set   = new PlanarKernelSet(
                new LayeredSpectralGreens(Problem.EffectiveStack, fHz), st.Order, 0.0,
                AsARunFits(fHz, Problem.EffectiveStack)).For(cores);
            var pm    = PlanarFill.ScalarPotentialMatrix(cores, set, PlanarLevels.From(Problem));

            int m = Mesh.Cells.Count;
            var a   = new Mat<Complex>(m, m);
            var rhs = new Vec<Complex>(m);
            for (int i = 0; i < m; i++)
            {
                rhs[i] = NetOf(Mesh.Cells[i]) == 0 ? Complex.One : Complex.Zero;
                for (int j = 0; j < m; j++) a[i, j] = pm[i, j] / EmConstants.Eps0;
            }
            var q = a.Lu().Solve(rhs);

            Complex onTwo = Complex.Zero;
            for (int i = 0; i < m; i++) if (NetOf(Mesh.Cells[i]) != 0) onTwo += q[i];
            return -onTwo.Real;
        }

        public double ClosedFormF => EmConstants.Eps0 * EpsFilm * PlateM * PlateM / D;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE TWO READINGS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The pi model's direct branch admittance, <c>−Y₂₁</c>, at one frequency.</summary>
    private static Complex Branch(Mat<Complex> s, double z0 = 50.0)
    {
        int n = s.RowCount;
        var i = new Mat<Complex>(n, n);
        for (int k = 0; k < n; k++) i[k, k] = Complex.One;
        var y = (i + s).Inverse() * (i - s);
        return -y[1, 0] / z0;
    }

    /// <summary>
    /// <b>C and L from the whole sweep, in closed form.</b> The branch is L in series with C, so
    /// <c>1/Im(−Y₂₁) = 1/(ωC) − ωL</c> exactly — linear in ω with a 1/ω term, i.e. two-parameter
    /// least squares. <c>WorstRelResidual</c> is how well that model describes the rows, and it is
    /// the number that says whether the reading may be believed at all.
    /// </summary>
    private static (double CapF, double IndH, double WorstRelResidual) FitSeriesLc(
        IReadOnlyList<(double FHz, double B)> rows)
    {
        double s11 = 0, s12 = 0, s22 = 0, t1 = 0, t2 = 0;
        foreach (var (f, b) in rows)
        {
            double w = 2 * Math.PI * f, y = 1.0 / b, x1 = 1.0 / w, x2 = w;
            s11 += x1 * x1; s12 += x1 * x2; s22 += x2 * x2; t1 += x1 * y; t2 += x2 * y;
        }
        double det  = s11 * s22 - s12 * s12;
        double c    = 1.0 / ((t1 * s22 - t2 * s12) / det);
        double l    = -(s11 * t2 - s12 * t1) / det;
        double worst = 0;
        foreach (var (f, b) in rows)
        {
            double w = 2 * Math.PI * f;
            worst = Math.Max(worst, Math.Abs(1.0 / (1.0 / (w * c) - w * l) - b) / Math.Abs(b));
        }
        return (c, l, worst);
    }

    /// <summary>One rung: solve the sweep, read the branch at every frequency, fit C and L.</summary>
    private (double CapF, double IndH, double Residual, double CStat, double WorstSigma) Rung(
        Fixture fx)
    {
        var run = PlanarSolve.Run(fx.Problem, fx.Mesh, fx.Ports, Band);
        var rows = new List<(double, double)>();
        double worstSigma = 0;
        foreach (var pt in run.Points)
        {
            rows.Add((pt.FrequencyHz, Branch(pt.S).Imaginary));
            worstSigma = Math.Max(worstSigma, RfCore.RFNetwork.Passivity(pt.S));
        }
        var (c, l, res) = FitSeriesLc(rows);
        return (c, l, res, fx.ElectrostaticMutualF(Band[0]), worstSigma);
    }

    private void Report(string label, Fixture fx,
                        (double CapF, double IndH, double Residual, double CStat, double WorstSigma) r)
        => _out.WriteLine(
            $"{label,-34} cell/sep {fx.CellOverSeparation,7:G4}  d {fx.D * 1e6,8:G4} um  " +
            $"C {r.CapF * 1e15,9:F1} fF  C_stat {r.CStat * 1e15,9:F1} fF  " +
            $"C/C_stat {r.CapF / r.CStat,6:F4}  C_stat/(e0erA/d) {r.CStat / fx.ClosedFormF,6:F4}  " +
            $"L {r.IndH * 1e12,6:F1} pH  fit {r.Residual:E1}  sigma_max {r.WorstSigma:F5}");

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T1 — THE THREE CONTROLS, ALL WITH NO FILM IN THEM. Nothing below may be read until these hold.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]   // three de-embedded sweeps on the MIM stack, ~1 min
    public void T1_TheFixtureMeasuresTheStructureAndNotItsOwnPorts()
    {
        // ── CONTROL 1: a plain through line on the same port setup is PASSIVE and MATCHED ───────
        //
        // MIM-12a measured this at 8% non-passive and stopped there. It is a two-CELL line: at two
        // cells along a 600 um conductor both reference planes land on the same gridline and the
        // de-embedded DUT has zero length. Given cells between the planes it is an ordinary matched
        // section — and MATCHED IS ASSERTED IN THE LINE'S OWN REFERENCE, not in 50 ohms, because
        // this line is 67 ohms and the published S is renormalised to the port's Z0. In 50 ohms a
        // perfectly matched 67-j2 ohm line reads |S11| = 0.07 and nothing is wrong with it.
        const double len = 600e-6, w = 40e-6;
        var line = new PlanarProblem(
            [new PlanarConductorLayer("lower", [Rect(0, 0, len, w)], Sigma, TLower, Zlow)],
            new GroundedSlab(Zlow, new EmMaterial(12.90, 0.0006)), Band[^1], null, Stack(0.2e-6), null);
        var lineMesh = SurfaceMesher.Mesh(line, new PlanarMeshSettings(
            Auto: false, CellsPerWavelength: 20, EdgeMesh: false, EdgeCells: 3,
            MinCellsAcrossConductor: 8)).Mesh;
        var lineRun = PlanarSolve.Run(line, lineMesh, PlanarPorts.ResolveAll(lineMesh,
        [
            new PlanarPort(1, new EmPoint(0,   w / 2), PlanarPortSide.MinX, 50.0, 0),
            new PlanarPort(2, new EmPoint(len, w / 2), PlanarPortSide.MaxX, 50.0, 0),
        ]), Band);

        foreach (var pt in lineRun.Points)
        {
            var zc = new[] { pt.Calibrations[0].Zc, pt.Calibrations[^1].Zc };
            var atZc = LineDeembed.Renormalise(pt.S, [50.0, 50.0], zc);
            double sigma = RfCore.RFNetwork.Passivity(pt.S);
            _out.WriteLine($"through line at {pt.FrequencyHz / 1e9:F0} GHz: Zc = {zc[0]:F2} ohm, " +
                           $"|S11| in its own reference = {atZc[0, 0].Magnitude:E3}, " +
                           $"sigma_max = {sigma:F5}");
            Assert.True(atZc[0, 0].Magnitude < 3e-2,
                $"|S11| = {atZc[0, 0].Magnitude:E3} on a section that should be matched — " +
                "PlanarDeembedTests T4_1's own gate, on this stack and these ports.");
            Assert.True(sigma <= 1.001, $"sigma_max = {sigma:F5} on a plain through line");
        }

        // ── CONTROL 2: at d = 20 um there is no thin film in it at all, and the two-port has to ──
        //    agree with the electrostatic mutual capacitance of the SAME mesh.
        var wide = Fixture.Build(20e-6, across: 2);
        var w20  = Rung(wide);
        Report("control 2 (d = 20 um)", wide, w20);
        Assert.True(Math.Abs(w20.CapF / w20.CStat - 1) < 0.10,
            $"the two-port reads {w20.CapF / w20.CStat:F4} of the electrostatic value at " +
            "cell/separation 1 — a regime with no film in it. The fixture is measuring its ports.");
        Assert.True(w20.WorstSigma <= 1.0, $"sigma_max = {w20.WorstSigma:F5}");

        // ── CONTROL 3: the reading STOPS MOVING with refinement. MIM-12a's fixture HALVED with ──
        //    every mesh (0.0068 -> 0.0045 -> 0.0027 -> 0.0014 pF); a quantity that does that is a
        //    port artefact whatever its value. Three meshes on ONE structure, the shipped film.
        var three = new List<double>();
        foreach (int across in new[] { 2, 3, 4 })
        {
            var fx = Fixture.Build(0.2e-6, across);
            var r  = Rung(fx);
            Report($"control 3 ({across} cells across)", fx, r);
            three.Add(r.CapF / r.CStat);
        }
        double spread = (three.Max() - three.Min()) / three.Min();
        _out.WriteLine($"control 3: spread over three meshes = {spread:P2}");
        Assert.True(spread < 0.05, $"the reading moved {spread:P1} over three meshes");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T2 — THE LADDER. The two rungs MIM-12 got wrong, and the one that was blocking a real design.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]   // three de-embedded sweeps, ~1 min
    public void T2_TheSeriesElementNoLongerInvertsItsSign_OutToTheNewFloor()
    {
        // MIM-12's own design: the straddling cell PINNED (four cells across the feeds gives 10 um
        // and the mesh does not depend on d at all), only the film thickness moving. Its rows were
        // C/(eps0 epsR A/d) = 1.05 at cell/separation 40, then -1.02 at 80 and -1.52 at 200 — a
        // capacitor published as an inductor. Three rungs are enough to retire that: the one a real
        // spiral-plus-capacitor sits at, the one the sign inverted at, and the new floor itself.
        // The rest of the ladder is in HISTORY.md §MIM-14.
        var wasBefore = new Dictionary<double, string>
            { [67] = "(MIM-12 had no rung here)", [80] = "MIM-12 read -1.02", [200] = "MIM-12 read -1.52" };

        double cell = Fixture.Build(0.2e-6, across: 4).CellOverSeparation * 0.2e-6;
        foreach (double ratio in new[] { 67.0, 80.0, 200.0 })
        {
            var fx = Fixture.Build(cell / ratio, across: 4);
            var r  = Rung(fx);
            Report($"rung {ratio:G3}  {wasBefore[ratio]}", fx, r);

            Assert.Equal(ratio, fx.CellOverSeparation, 0);

            // The electrostatic CONTROL has to be right before it can grade anything — it is, to 1%
            // of the closed form, at every rung. (Never the other way round: this brief's own added
            // convention is that an electrostatic measurement never gates a full-wave capability.)
            Assert.True(Math.Abs(r.CStat / fx.ClosedFormF - 1) < 0.05,
                $"the control itself reads {r.CStat / fx.ClosedFormF:F4} of eps0 epsR A/d");

            // THE GATE.
            Assert.True(r.CapF > 0,
                $"the series element came back NEGATIVE ({r.CapF * 1e15:F1} fF) at cell/separation " +
                $"{ratio:G3} — which is what MIM-12 measured and what the peel was supposed to fix.");
            Assert.True(Math.Abs(r.CapF / r.CStat - 1) < 0.10,
                $"cell/separation {ratio:G3}: the two-port reads {r.CapF / r.CStat:F4} of the " +
                "electrostatic mutual capacitance of the same mesh, outside 10%.");
            Assert.True(r.WorstSigma <= 1.0, $"sigma_max = {r.WorstSigma:F5} at {ratio:G3}");

            // …and the fixture's own inductance is the SAME at every rung, which is what says the
            // fit separated an instrument property rather than absorbing the disagreement.
            Assert.InRange(r.IndH * 1e12, 400, 470);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T3 — the constant, and where the refusal now falls. No solve; this is the routine gate.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T3_TheFloorIs200_AndWhatUsedToBeRefusedAtSixtySevenNowRuns()
    {
        Assert.Equal(200.0, PlanarLevels.FullWaveCellOverSeparation);

        // 66.7 is the ratio the spiral-plus-capacitor that produced this brief was refused at, and
        // 75 is where the mesher's DEFAULT four cells across puts a 60 um plate on the shipped film.
        // Both were past the old floor of 40 and both are inside the new one. This is asserted on
        // the real artwork through the real verdict, not on a hand-built pair, because the thing
        // that was blocking a design is the combination of the two.
        //
        // ONE CELL ACROSS IS DELIBERATELY NOT IN THIS LOOP. At one cell across the 40 um feed the
        // straddling cell IS 40 um and the film IS 0.2 um, so cell/separation is 200 exactly — the
        // bound itself, where the verdict's own > decides the answer on the last bit of a double
        // rather than on anything measured. It happens to land inside today (199.99999999999517,
        // because the two level z's are 103 um and 103.2 um and their difference is not exact), and
        // a fixture whose verdict turns on that is a coin toss: MimThinLayerTests M9_2 moved its own
        // rung OFF this ratio for the same reason. Table 5 in HISTORY.md §MIM-14 grades the rung;
        // this test grades the three that are unambiguously inside.
        foreach (int across in new[] { 2, 3, 4 })
        {
            var fx = Fixture.Build(0.2e-6, across);
            var v  = PlanarSolve.LevelSeparationVerdict(fx.Problem, fx.Mesh);
            _out.WriteLine($"{across} cell(s) across a 60 um plate: cell/separation " +
                           $"{fx.CellOverSeparation:G4}, refused = {!v.Ok}");
            Assert.True(v.Ok, $"cell/separation {fx.CellOverSeparation:G4} is inside MIM-14's " +
                              $"own ladder and must not be refused: {v.Reason}");
            Assert.True(fx.CellOverSeparation < PlanarLevels.FullWaveCellOverSeparation,
                $"cell/separation {fx.CellOverSeparation:G4} is ON the bound, not inside it — this " +
                "loop must not be the thing that decides a tie.");
        }

        // …and past it the refusal still stands, which is MIM-9's instruction and this brief's:
        // the floor moves, it does not disappear. A 50 nm film at the coarsest mesh this artwork
        // meshes to is cell/separation 800, and nothing is measured there — not the two-port, and
        // not the cross-level fill either.
        //
        // IT TAKES A THINNER FILM RATHER THAN A BIGGER PLATE, and that is worth knowing: the
        // straddling cell here is 40 um at one cell across whether the plate is 60, 200 or 400 um
        // wide, because the shared tensor grid takes its pitch from the NARROWEST metal on the two
        // levels — the 40 um feed — and not from the widest.
        var coarse = Fixture.Build(0.05e-6, across: 1);
        var verdict = PlanarSolve.LevelSeparationVerdict(coarse.Problem, coarse.Mesh);
        _out.WriteLine($"a 50 nm film at 1 cell across: cell/separation " +
                       $"{coarse.CellOverSeparation:G4}, refused = {!verdict.Ok}");
        Assert.True(coarse.CellOverSeparation > 200);
        Assert.False(verdict.Ok);
        Assert.Contains("past the 200 this solve is measured over", verdict.Reason);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T4 — the two readings of "the series element", and why only one of them is a capacitance
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T4_TheBranchCapacitanceIsTheSUSCEPTANCE_NotTheReactanceOfItsInverse()
    {
        // A known lossy branch: 1 pF with a parallel 100 uS, i.e. Q = 2*pi*1e9*1e-12/1e-4 = 62.8 at
        // 1 GHz and 6.28 at 100 MHz. Im(-Y21)/w recovers the 1 pF exactly at both; the reactance of
        // its inverse reads C(1 + 1/Q^2) and is 2.5% high at Q = 6.28.
        //
        // This is not a hypothetical. The fixture above was first built with a 0.25 um sheet on the
        // feeds as well as the plate; at the Q ~ 3 that gives, the two readings differed by 12% and
        // the impedance one would have put the floor an order of magnitude lower than it is.
        const double c = 1e-12, g = 1e-4;
        foreach (double f in new[] { 1e8, 1e9 })
        {
            double w = 2 * Math.PI * f;
            var y = new Complex(g, w * c);           // the branch admittance
            var z = Complex.One / y;
            double fromB = y.Imaginary / w;
            double fromX = -1.0 / (w * z.Imaginary);
            double q     = y.Imaginary / y.Real;
            _out.WriteLine($"{f / 1e9:G3} GHz, Q = {q:F2}: Im(-Y21)/w = {fromB * 1e12:F6} pF, " +
                           $"-1/(w Im(-1/Y21)) = {fromX * 1e12:F6} pF " +
                           $"({(fromX / c - 1):P3} high, 1/Q^2 = {1 / (q * q):P3})");
            Assert.True(Math.Abs(fromB / c - 1) < 1e-12, $"Im(-Y21)/w is not the capacitance: {fromB * 1e12:F9} pF");
            Assert.True(Math.Abs(fromX / (c * (1 + 1 / (q * q))) - 1) < 1e-12,
                        $"-1/(w Im(-1/Y21)) is not C(1 + 1/Q^2): {fromX * 1e12:F9} pF");
        }

        // And the L-C fit is exact algebra on an exact series L-C, which is what licenses reading
        // the three frequencies as one number.
        const double lTrue = 437e-12, cTrue = 1.1e-12;
        var rows = new List<(double, double)>();
        foreach (double f in Band)
        {
            double w = 2 * Math.PI * f;
            rows.Add((f, 1.0 / (1.0 / (w * cTrue) - w * lTrue)));
        }
        var (cFit, lFit, res) = FitSeriesLc(rows);
        _out.WriteLine($"fit of an exact series L-C: C = {cFit * 1e12:F6} pF, L = {lFit * 1e12:F4} pH, " +
                       $"residual {res:E2}");
        Assert.True(Math.Abs(cFit / cTrue - 1) < 1e-12, $"C = {cFit * 1e12:F9} pF");
        Assert.True(Math.Abs(lFit / lTrue - 1) < 1e-9,  $"L = {lFit * 1e12:F6} pH");
        Assert.True(res < 1e-12);
    }
}
