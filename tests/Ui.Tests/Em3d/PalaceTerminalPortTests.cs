using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using NumFlat;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-115 §9 — Palace terminal ports on a shared face.
//
//  Gates 1–4 replay brief 124's committed runs (testdata/em3d/terminal/palace-modal/, its README names each) through
//  PalaceRun's readers, PalaceTerminalS and ModalTerminalTransform: nothing is solved, so a Palace version bump re-checks
//  the transform by re-running 124's harness and replaying its new output here. Entry order in every run: 1 line-1 near,
//  2 line-2 near, 3 line-1 far, 4 line-2 far; the comparisons are in 124's order (1 line-1 near, 2 line-1 far, 3 line-2
//  near, 4 line-2 far), all at 50 Ω.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class PalaceTerminalPortTests(ITestOutputHelper output)
{
    private const double Z0 = 50, C0 = 299_792_458.0, Ell = 15e-3;
    private static readonly int[] ToRefOrder = [0, 2, 1, 3];
    private static readonly IReadOnlyList<IReadOnlyList<int>> SharedFaces = [[0, 1], [2, 3]];

    // ── gate 1: the symmetric air pair ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_SharedFaceEqualsRouteBOnTheSameMesh()
    {
        var (s, r) = Terminal("a-shared-r2e0.02");
        double dS = MaxDiff(s, RouteB("a-halfPMC-r2e0.02", "a-halfPEC-r2e0.02"));
        output.WriteLine($"max|ΔS| vs route B {dS:G3}; g {r.Faces[0].G.Real:F4} / {r.Faces[1].G.Real:F4}");
        Assert.True(dS <= 0.0005, $"max|ΔS| {dS}");
        Assert.Equal(-0.501, r.Faces[0].G.Real, 1e-3);
        Assert.Equal(+0.153, r.Faces[1].G.Real, 1e-3);
    }

    [Fact]
    public void Gate1_TwoModeMixturesGiveOneTerminalS()
    {
        var (a, _) = Terminal("a-shared-r2-default");
        var (b, _) = Terminal(Path.Combine("..", "palace", "pair-a-shared-face"));
        double dS = MaxDiff(a, b);
        output.WriteLine($"max|ΔS| between 124's and 113-a's mixtures {dS:G3}");
        Assert.True(dS <= 0.0005, $"max|ΔS| {dS}");
    }

    // ── gate 2: the asymmetric air pair against the exact multiconductor line ──────────────────

    /// <summary>124's 2D characteristic-impedance matrix (asym.py, fv2d.py) of the air pair with line 2 10 % wider (1.32 mm).</summary>
    private static readonly double[,] AsymZc = { { 86.271514068699, 14.727192459527 }, { 14.727192459527, 81.819393868652 } };

    [Fact]
    public void Gate2_AsymmetricPairMatchesTheExactLine()
    {
        var (s, _) = Terminal("asym10-shared-r2e0.01");
        double dS = MaxDiff(s, CoupledLine(AsymZc, 5e9));
        output.WriteLine($"max|ΔS| vs the exact line {dS:G3}");
        Assert.True(dS <= 0.003, $"max|ΔS| {dS}");
    }

    // ── gate 3: the microstrip pair, lossless and lossy; the one-entry face ─────────────────────

    [Theory]
    [InlineData(2, 1.0793)]
    [InlineData(4, 1.0823)]
    [InlineData(6, 1.0863)]
    public void Gate3_MicrostripPairIsLosslessWithTheRobinCorrection(int fGHz, double k)
    {
        var (_, r) = Terminal($"basym-shared-r2e-f{fGHz}");
        output.WriteLine($"power {r.PortPower.Min():F5}–{r.PortPower.Max():F5}, K {r.K[1]:F4}");
        Assert.All(r.PortPower, p => Assert.InRange(p, 0.998, 1.002));
        Assert.Equal(k, r.K[1], 4e-4);
        Assert.Equal(k, r.K[3], 4e-4);
        Assert.False(r.Faces[0].Degenerate);
    }

    [Fact]
    public void Gate3_WhatLossDoesAgreesWithOpenEms()
    {
        double dS = MaxDiff(LossDifferential(ModalRobinForm.RealParts), OpenEmsLossDifferential());
        output.WriteLine($"max|ΔS| of the loss differential {dS:G3}");
        Assert.True(dS <= 0.001, $"max|ΔS| {dS}");
    }

    /// <summary>113-a's coax run (two ports, each its own face, three frequencies): every port a one-entry face. brief-em3d-141:
    /// the ungrouped path refers each port to a mode whose voltage is real before renormalising, and so does the transform.</summary>
    [Fact]
    public void Gate3_AOneEntryFaceIsTodaysRenormalisation()
    {
        string name = Path.Combine("..", "palace", "coax");
        var samples = Read(name, [1, 2]);
        var ports = new[] { Wave(1), Wave(2) };
        var s = PalaceRun.ReadPortS(Path.Combine(Fixture(name), PalaceRun.PortSFile), [1, 2], out _);
        var z = PalaceRun.ReadPortZ(Path.Combine(Fixture(name), PalaceRun.PortZFile), [1, 2], out _, out _)!;
        var v = PalaceRun.ReadWavePortV(Path.Combine(Fixture(name), PalaceRun.PortVFile), [1, 2], out _)!.Value.V;
        var theta = WavePortReferences.FitModeVoltages(s.S, [0, 1], v).Voltage.Select(r => r.Select(x => x.Phase).ToArray()).ToList();
        var today = WavePortReferences.Renormalise(s with { S = WavePortReferences.Rotate(s.S, [0, 1], theta) }, ports, ports, z);
        var mine = PalaceTerminalS.Convert(samples, ports, out string? error);
        Assert.True(mine is not null, error);
        Assert.Equal(3, today.S.Length);
        for (int k = 0; k < today.S.Length; k++)
            Assert.True(MaxDiff(mine.S.S[k], today.S[k]) <= 1e-12, $"row {k}: max|ΔS| {MaxDiff(mine.S.S[k], today.S[k])}");
    }

    // ── §3: the adaptive sweep with interpolated kₙ is the Point sweep ───────────────────────────

    /// <summary>brief 115 §3 — 124's asymmetric microstrip mesh solved 2–6 GHz in 9 points twice: an adaptive sweep (which logs
    /// no kₙ) with kₙ interpolated from 124's own 2, 4 and 6 GHz runs on that mesh, and Point samples with every frequency's
    /// kₙ logged. Their terminal S agree to 0.001 at every frequency, the bar §3 decides by.</summary>
    [Fact]
    public void Section3_AdaptiveWithInterpolatedKnIsThePointSweep()
    {
        var knots = new[] { 2, 4, 6 }.SelectMany(g => PalaceRun.ReadWaveModes(File.ReadLines(Path.Combine(Fixture($"basym-shared-r2e-f{g}"),
                                                                                                         PalaceRun.PalaceLogFile)))).ToList();
        var adaptive = PalaceTerminalS.Read(Fixture("s3-adaptive"), knots, [1, 2, 3, 4], out string? error);
        Assert.True(adaptive is not null, error);
        var point = Read("s3-point", [1, 2, 3, 4]);
        Assert.Equal(9, point.FrequenciesHz.Length);
        double worst = 0;
        for (int k = 0; k < 9; k++)
        {
            var a = ModalTerminalTransform.Solve(adaptive.Samples[k], SharedFaces, [Z0, Z0, Z0, Z0]);
            var b = ModalTerminalTransform.Solve(point.Samples[k], SharedFaces, [Z0, Z0, Z0, Z0]);
            worst = Math.Max(worst, MaxDiff(a.S!, b.S!));
        }
        output.WriteLine($"max|ΔS| adaptive (interpolated kₙ) vs Point {worst:G3}");
        Assert.True(worst <= 0.001, $"max|ΔS| {worst}");
    }

    /// <summary>Palace's 2D mode solve of a face (Problem BoundaryMode) logs n_eff; kₙ = n_eff·ω/c. Lines from a run on 124's
    /// microstrip mesh at 3 GHz, whose driven run logged 1.063e+02 and 9.840e+01.</summary>
    [Fact]
    public void Section3_AFaceModeSolvesLogGivesItsWavenumbers()
    {
        string[] log =
        [
            " eig 0: kn = 9.038506e-01+7.645619e-14i, n_eff = 1.691209e+00+1.430584e-13i",
            " eig 1: kn = 8.364012e-01+3.247261e-13i, n_eff = 1.565003e+00+6.076001e-13i",
            " eig 2: kn = 1.903835e-07+6.259305e+00i, n_eff = 3.562295e-07+1.171188e+01i",
        ];
        var kn = PalaceRun.ParseFaceModes(log, 3e9);
        Assert.Equal(3, kn.Count);
        Assert.Equal(106.3, kn[0].Real, 0.05);
        Assert.Equal(98.40, kn[1].Real, 0.005);
        Assert.Equal([2e9, 4e9, 6e9, 8e9, 10e9], PalaceTerminalS.KnotFrequenciesHz([.. Enumerable.Range(0, 9).Select(k => (2 + k) * 1e9)]));
    }

    // ── gate 4: the wrong forms fail ────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_WithoutTheGramFitPowerIsLost()
    {
        var samples = Read("a-shared-r2e0.02", [1, 2, 3, 4]);
        var r = ModalTerminalTransform.Solve(samples.Samples[0], SharedFaces, [Z0, Z0, Z0, Z0], gram: ModalGramForm.Identity);
        double off = r.PortPower.Max(p => Math.Abs(1 - p));
        output.WriteLine($"G = 1: per-port power off 1 by {off:G3}");
        Assert.True(off > 0.01, $"off {off}");
    }

    [Fact]
    public void Gate4_WithoutTheRobinCorrectionSigmaMinFalls()
    {
        var samples = Read("basym-shared-r2e-f6", [1, 2, 3, 4]);
        var r = ModalTerminalTransform.Solve(samples.Samples[0], SharedFaces, [Z0, Z0, Z0, Z0], robin: ModalRobinForm.None);
        output.WriteLine($"K = 1: σ_min {r.SigmaMin:F4}");
        Assert.True(r.SigmaMin < 0.9, $"σ_min {r.SigmaMin}");
    }

    [Fact]
    public void Gate4_AComplexRobinMissesWhatLossDoes()
    {
        double dS = MaxDiff(LossDifferential(ModalRobinForm.Complex), OpenEmsLossDifferential());
        output.WriteLine($"complex K: max|ΔS| of the loss differential {dS:G3}");
        Assert.True(dS > 0.005, $"max|ΔS| {dS}");
    }

    // ── gate 6: what Palace still refuses ───────────────────────────────────────────────────────
    // Three terminals through a whole run: TerminalWavePortTests.Gate7_AThreeTerminalSetup_IsRefusedBeforeGmsh. A .c3d port has
    // no reference-plane shift (brief 114 lowers every terminal at the face), so the shifted plane is the problem-level backstop.

    [Fact]
    public void Gate6_AShiftedReferencePlaneIsRefusedNamingOpenEms()
    {
        string? why = Em3dRunService.TerminalPortRefusal(Em3dSolverId.Palace, [("Left", 2)], shifted: ("Left", 0.5e-3));
        Assert.Equal("Port 'Left' has its reference plane moved 0.5 mm off its face; Palace runs terminal ports with the reference " +
                     "plane on the port face in this version (the terminal voltages it writes are measured there). Set the setup's " +
                     "solver to openEMS.", why);
        Assert.NotNull(Em3dRunService.TerminalPortsSkipPalace([("Left", 2)], shifted: ("Left", 0.5e-3)));
    }

    [Fact]
    public void Gate6_TwoTerminalsRunOnPalace_AndOnBothRunBothSolvers()
    {
        Assert.Null(Em3dRunService.TerminalPortRefusal(Em3dSolverId.Palace, [("Left", 2), ("Right", 2)]));
        Assert.Null(Em3dRunService.TerminalPortsSkipPalace([("Left", 2), ("Right", 2)]));
        Assert.Contains("so this setup ran on openEMS and Palace was skipped for it",
                        Em3dRunService.TerminalPortsSkipPalace([("Left", 3)]));
    }

    // ── gate 8: the run's checks ────────────────────────────────────────────────────────────────

    private static readonly Em3dPort[] PairPorts =
    [
        Wave(1) with { FaceGroup = 1, FaceGroupLabel = "Left" }, Wave(2) with { FaceGroup = 1, FaceGroupLabel = "Left" },
        Wave(3) with { FaceGroup = 3, FaceGroupLabel = "Right" }, Wave(4) with { FaceGroup = 3, FaceGroupLabel = "Right" },
    ];

    [Fact]
    public void Gate8_APassiveLosslessPairWarnsNothing_AndSaysItsRoute()
    {
        var outcome = PalaceTerminalS.Convert(Read("a-shared-r2e0.02", [1, 2, 3, 4]), PairPorts, out string? error);
        Assert.True(outcome is not null, error);
        var (notes, warnings) = PalaceTerminalS.Checks(PairPorts, outcome, lossless: true);
        Assert.Empty(warnings);
        var route = PalaceTerminalS.RouteNotes(PairPorts, outcome).ToList();
        output.WriteLine(string.Join("\n", route.Concat(notes)));
        Assert.Equal("Port 'Left': terminals P1, P2 as modes 1 and 2 of one Palace wave port, converted to terminal S (degenerate " +
                     "face: Gram fit g = −0.501, residual 2e-6).", route[0]);
    }

    [Fact]
    public void Gate8_AGramResidualOf1e2Warns()
    {
        var samples = Read("a-shared-r2e0.02", [1, 2, 3, 4]);
        var x = samples.Samples[0];
        double[] z = [.. x.ZPv];
        z[0] *= 1.01;                       // a Z_PV no real g can meet
        var doctored = samples with { Samples = [x with { ZPv = z }] };
        var outcome = PalaceTerminalS.Convert(doctored, PairPorts, out _)!;
        output.WriteLine($"residual {outcome.PerFrequency[0].Faces[0].Residual:G3}");
        Assert.InRange(outcome.PerFrequency[0].Faces[0].Residual, 5e-3, 2e-2);
        Assert.Contains(PalaceTerminalS.Checks(PairPorts, outcome, lossless: true).Warnings, w => w.Contains("Gram fit"));
    }

    [Fact]
    public void Gate8_ALosslessProblemLosingHalfAPercentWarns()
    {
        var outcome = PalaceTerminalS.Convert(Read("a-shared-r2e0.02", [1, 2, 3, 4]), PairPorts, out _)!;
        var r = outcome.PerFrequency[0];
        var lossy = outcome with { PerFrequency = [r with { PortPower = [.. r.PortPower.Select(p => p * 0.995)], SigmaMin = r.SigmaMin * 0.9975 }] };
        var warnings = PalaceTerminalS.Checks(PairPorts, lossy, lossless: true).Warnings;
        Assert.Contains(warnings, w => w.Contains("no loss mechanism"));
        Assert.Empty(PalaceTerminalS.Checks(PairPorts, lossy, lossless: false).Warnings);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string Fixture(string name)
        => Path.GetFullPath(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "terminal", "palace-modal", name));

    /// <summary>A run's one frequency, read the way the run service reads it (kₙ from its log).</summary>
    private static WavePortModalSamples Read(string name, int[] ports)
    {
        string dir = Fixture(name);
        var modes = PalaceRun.ReadWaveModes(File.ReadLines(Path.Combine(dir, PalaceRun.PalaceLogFile)));
        var samples = PalaceTerminalS.Read(dir, modes, ports, out string? error);
        Assert.True(samples is not null, error);
        return samples;
    }

    private static Em3dPort Wave(int n)
        => new(n, $"P{n}", "s", "g", default, default, default, Z0, new Em3dReferencePlane(default, default, 0)) { Kind = Em3dPortKind.Wave };

    /// <summary>A shared-face run's terminal S in 124's order, and the transform's result.</summary>
    private static (Complex[,] S, ModalTerminalResult R) Terminal(string name, ModalRobinForm robin = ModalRobinForm.RealParts)
    {
        var samples = Read(name, [1, 2, 3, 4]);
        var r = ModalTerminalTransform.Solve(samples.Samples[0], SharedFaces, [Z0, Z0, Z0, Z0], robin: robin);
        Assert.Null(r.Error);
        return (Permute(r.S!, ToRefOrder), r);
    }

    private static Complex[,] LossDifferential(ModalRobinForm robin)
        => Sub(Terminal("basymloss-shared-r2e-f4", robin).S, Terminal("basym-shared-r2e-f4", robin).S);

    private static Complex[,] OpenEmsLossDifferential()
    {
        Complex[,] At4(string run)
        {
            var snp = TouchstoneIO.ReadFile(Fixture(Path.Combine("openems", run, "PairB.s4p")));
            int k = Array.FindIndex(snp.Frequencies, f => Math.Abs(f - 4e9) < 1);
            Assert.True(k >= 0);
            var m = new Complex[4, 4];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) m[i, j] = snp.Matrices[k][i, j];
            return m;
        }
        return Sub(At4("asym-lossy"), At4("asym-lossless"));
    }

    /// <summary>119's route B: two half-model runs, each a 2-port renormalised from its own Z_PV to 50 Ω, combined even/odd.</summary>
    private static Complex[,] RouteB(string even, string odd)
    {
        // A half-model run excites port 1 only (port 2 by the end-to-end mirror), so its file has S11 and S21 alone.
        Complex[,] Half(string run)
        {
            var lines = File.ReadAllLines(Path.Combine(Fixture(run), PalaceRun.PortSFile));
            var head = lines[0].Split(',').Select(h => h.Trim()).ToList();
            var row = lines[1].Split(',').Select(c => double.Parse(c, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Complex Col(int i) => Complex.FromPolarCoordinates(Math.Pow(10, row[head.IndexOf($"|S[{i}][1]| (dB)")] / 20),
                                                               row[head.IndexOf($"arg(S[{i}][1]) (deg.)")] * Math.PI / 180);
            var z = PalaceRun.ReadPortZ(Path.Combine(Fixture(run), PalaceRun.PortZFile), [1], out _, out _)!;
            var m = new Mat<Complex>(2, 2);
            (m[0, 0], m[1, 0], m[0, 1], m[1, 1]) = (Col(1), Col(2), Col(2), Col(1));
            var r = RFNetwork.SToS(m, [z[0][0], z[0][0]], [Z0, Z0]);
            return new Complex[,] { { r[0, 0], r[0, 1] }, { r[1, 0], r[1, 1] } };
        }
        var (se, so) = (Half(even), Half(odd));
        var S = new Complex[4, 4];
        (int, int)[] line = [(0, 2), (1, 3)];
        for (int a = 0; a < 2; a++)
            for (int b = 0; b < 2; b++)
            {
                var (p1, p3) = line[a];
                var (q1, q3) = line[b];
                Complex same = (se[a, b] + so[a, b]) / 2, cross = (se[a, b] - so[a, b]) / 2;
                S[p1, q1] = S[p3, q3] = same;
                S[p1, q3] = S[p3, q1] = cross;
            }
        return S;
    }

    /// <summary>A lossless air multiconductor line of characteristic-impedance matrix <paramref name="zc"/>, 124's order.</summary>
    private static Complex[,] CoupledLine(double[,] zc, double fHz)
    {
        double th = 2 * Math.PI * fHz / C0 * Ell;
        var yc = new Mat<Complex>(2, 2);
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) yc[i, j] = zc[i, j];
        yc = yc.Inverse();
        var y = new Mat<Complex>(4, 4);
        Complex near = -Complex.ImaginaryOne / Math.Tan(th), far = Complex.ImaginaryOne / Math.Sin(th);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                y[i, j] = y[i + 2, j + 2] = near * yc[i, j];
                y[i, j + 2] = y[i + 2, j] = far * yc[i, j];
            }
        var one = new Mat<Complex>(4, 4);
        for (int i = 0; i < 4; i++) one[i, i] = 1;
        var s = (one - y * Z0) * (one + y * Z0).Inverse();      // order near1, near2, far1, far2
        var m = new Complex[4, 4];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) m[i, j] = s[i, j];
        return Permute(m, ToRefOrder);
    }

    private static Complex[,] Permute(Complex[,] s, int[] p)
    {
        var r = new Complex[4, 4];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) r[i, j] = s[p[i], p[j]];
        return r;
    }

    private static Complex[,] Sub(Complex[,] a, Complex[,] b)
    {
        var r = new Complex[a.GetLength(0), a.GetLength(1)];
        for (int i = 0; i < r.GetLength(0); i++) for (int j = 0; j < r.GetLength(1); j++) r[i, j] = a[i, j] - b[i, j];
        return r;
    }

    private static double MaxDiff(Complex[,] a, Complex[,] b)
    {
        double m = 0;
        for (int i = 0; i < a.GetLength(0); i++) for (int j = 0; j < a.GetLength(1); j++) m = Math.Max(m, (a[i, j] - b[i, j]).Magnitude);
        return m;
    }
}
