// L8e Tier 6 — THE PHASE GATE. §11's L8 row is three sentences and this file is all three:
//
//   "A quarter-wave open stub resonates at the right frequency;
//    a bend's s-parameters are physically sane;
//    A and B agree on a uniform line."
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHY THIS LIVES IN Ui.Tests RATHER THAN Engine.Tests, against the brief's own file map
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// The brief indicates tests/Engine.Tests/Mom/L8PhaseGateTests.cs, and every NUMBER here is the
// engine's. But what this slice ADDS to L8d's own measurements is the PRODUCT PATH — drawn artwork →
// extractor → registry → kernel → DataSet → .snp — and three of those five live in src/Ui behind the
// firewall. Running the gate through a hand-built PlanarProblem would re-measure what L8d already
// measured and would prove nothing about the path a user actually takes; "A and B come out of the
// SAME product path, from ONE layout, selected by the registry" is a different claim from "the two
// engines agree", and it is the one §11's row is about now. So the file is here, and the deviation
// is recorded rather than silent.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// THE TIERING, AND WHY IT IS WHAT IT IS
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// A de-embedded frequency point costs 7.66 s on §10.7's own hero (L8d Tier 7) and the calibration
// standards are 78% of it. The whole gate written naively is ~12 minutes, which is not a routine gate
// and is barely a tolerable opt-in one. Two levers, both applied:
//
//   • The frequency counts come from what each CLAIM needs, not from habit. Locating a notch is a
//     coarse scan plus one refine; a bend's sanity checks are three points; A-vs-B is two.
//   • The routine tier keeps ONE representative case per starter — the A-vs-B uniform line, the
//     cheapest of the three and the one that exercises the whole product path — and the stub and the
//     bend are Category=Benchmark.
//
// CALIBRATION IS NOT SHARED ACROSS DIFFERENT DUT GEOMETRIES anywhere here. L8b derives grid spacing
// from the whole problem's narrowness per axis, so a stub and a bend do not get the same port cells
// even at the same feed width; L8d measured a supposedly invariant answer moving by 1.8e-1 when a
// calibration was reused across geometries. PlanarSolve already shares one calibration between two
// ports only when PlanarPortCalibrator.SameCrossSection says it may.

using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class L8PhaseGateTests(ITestOutputHelper output) : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-l8-gate-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── The two starter technologies, as the gate's own two subjects (D7) ─────────────────────

    /// <summary>Everything a gate needs to know about a starter: its technology, which drawing layer
    /// its signal conductor is on, and the hero cross-section §10.7 sizes against it.</summary>
    private sealed record Starter(
        string Name, Technology Tech, LayerKey Signal, double WidthM, double HeightM, double EpsR);

    private static Starter Pcb() => new(
        "PCB 2-Layer (FR-4)", StarterTechnologies.Pcb2Layer(), new LayerKey(1, 0),
        WidthM: 2.9e-3, HeightM: 1.6e-3, EpsR: 4.4);

    /// <summary>The MMIC counterpart: 72 µm on 100 µm GaAs. Artwork goes on <b>Metal1</b> — Metal2
    /// sits above an explicit air layer, which the planar extractor refuses by name (one grounded
    /// slab, L8a's D2).</summary>
    private static Starter Mmic() => new(
        "MMIC GaAs", StarterTechnologies.MmicGaAs(), new LayerKey(1, 0),
        WidthM: 72e-6, HeightM: 100e-6, EpsR: 12.9);

    public static TheoryData<string> Starters => new("pcb", "mmic");

    private static Starter For(string key) => key == "pcb" ? Pcb() : Mmic();

    // ── Fixture plumbing ──────────────────────────────────────────────────────────────────────

    private static long Dbus(double metres) => (long)Math.Round(metres * 1e6 * Dbu);

    /// <summary>Guided wavelength from the crude pre-solve estimate ε_eff ≈ (εᵣ+1)/2 — the same one
    /// <c>PlanarCalibration.EstimateBeta</c> uses, and the only one available before a solve.</summary>
    private static double LambdaG(Starter s, double fHz)
        => EmConstants.C0 / (fHz * Math.Sqrt(0.5 * (s.EpsR + 1.0)));

    /// <summary>An explicit frequency list. <c>Setup</c> takes the LIST, not a (start, stop, count)
    /// triple — passing a count as if it were a third frequency is a mistake this gate made once and
    /// it cost a 50-second run that ended in a framework exception rather than a refusal, because a
    /// 6 Hz point is outside anything the kernel's per-frequency tables are sized for.</summary>
    private static double[] LinSpace(double lo, double hi, int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = lo + (hi - lo) * i / (n - 1.0);
        return f;
    }

    private static LabelShape PortLabel(LayerKey layer, double xM, double yM, string name, double heightM)
        => new()
        {
            Layer = layer, X = Dbus(xM), Y = Dbus(yM), Text = name,
            Height = Dbus(heightM), IsPort = true,
        };

    private static EmSetup Setup(string name, EmAnalysisKind kind, double[] freqsHz,
                                 PlanarMeshSettings? mesh = null)
    {
        var f = new Core.Design.FrequencySpec(
            Inv(freqsHz[0] / 1e9), Inv(freqsHz[^1] / 1e9),
            freqsHz.Length, Core.Design.SweepKind.Linear, "GHz", "GHz");

        return new EmSetup
        {
            Name         = name,
            LayoutRef    = "layout.clay",
            AnalysisKind = kind,
            Frequency    = f,
            PlanarMesh   = mesh ?? PlanarMeshSettings.Default,   // D7: the SHIPPING mesh
        };

        static string Inv(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    private EmRunResult Run(EmSetup setup, Starter s, IEnumerable<LayoutShape> shapes)
    {
        Directory.CreateDirectory(_dir);
        var view = new LayoutView { DbuPerMicron = Dbu };
        foreach (var sh in shapes) view.Shapes.Add(sh);

        var source = new EmLayoutSource("layout.clay", view, s.Tech, Dbu);
        var r = EmRunService.Run(setup, source, Path.Combine(_dir, "results"));

        if (r.Status != EmRunStatus.Ok)
            Assert.Fail($"{setup.Name} on {s.Name}: {r.Error}\n  " +
                        string.Join("\n  ", r.Warnings));
        return r;
    }

    private static double[] Freq(DataSet d)
    {
        string g = d.Groups.First(x => d.CubesIn(x).ContainsKey("S"));
        return d.CubesIn(g)["S"].Axes[0].Values;
    }

    private static Complex S(DataSet d, int fi, int i, int j)
    {
        string g = d.Groups.First(x => d.CubesIn(x).ContainsKey("S"));
        var cube = d.CubesIn(g)["S"];
        int n = cube.Axes[1].Values.Length;
        return cube.ComplexValues[(fi * n + i) * n + j];
    }

    private static double[] Eeff(DataSet d, string group)
        => d.CubesIn(group)["Eeff"].RealValues;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 3 — "A and B agree on a uniform line."  ROUTINE, one case per starter.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The claim this slice adds is the PRODUCT PATH, not the agreement.</b> L8d already measured
    /// the two engines against each other on a hand-built cross-section (−0.01% on ε_eff at 1 GHz).
    /// What is new here is that both answers come out of ONE drawn layout, through the extractors and
    /// the registry, from two <c>.cem</c> setups that differ in exactly one field — which is the claim
    /// a user experiences.
    ///
    /// <para><b>The tolerance is set by a MEASURED number, per R-res-12, and it is not the radiative
    /// floor.</b> Kernel A meshes real metal of the starter's own thickness; kernel B's sheet has
    /// none, and L8d measured kernel A's own thickness sensitivity directly: ε_eff 3.3062 at 0.1 µm,
    /// 3.3169 at 1 µm, 3.2875 at 35 µm on the FR-4 hero — i.e. ~0.9% between a 1 µm strip and real
    /// 35 µm copper, in the direction that pulls A BELOW B. The gate is 4%, comfortably above that
    /// and comfortably below anything that would let a real disagreement through; the measured number
    /// is reported either way.</para>
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [MemberData(nameof(Starters))]
    public void Gate3_AAndB_AgreeOnAUniformLine_ThroughTheSameProductPath(string key)
    {
        var s = For(key);

        // ── Sizing, and two decisions in it that are measurements rather than taste ───────────
        //
        // 1. THE BAND IS PER STARTER. A 1.5 λ_g GaAs line at 2 GHz is 85 mm of 72 µm-wide metal on a
        //    100 µm slab, which meshes to 5,040 unknowns and is REFUSED by R17 — L8b's own finding
        //    about GaAs, arriving here as a real refusal rather than as a surprise. An MMIC line is
        //    designed at MMIC frequencies, so the GaAs case is swept where one actually lives.
        //
        // 2. THE LINE IS SHORT, AND THAT COSTS THIS GATE NOTHING. ε_eff comes from γ, and γ comes
        //    from the two CALIBRATION STANDARDS, not from the DUT — so a longer DUT buys no accuracy
        //    here and costs O(N²) in the fill. Long enough to be a real de-embedded through line,
        //    and no longer.
        double fLo = s.Name.StartsWith("PCB", StringComparison.Ordinal) ? 1e9  : 10e9;
        double fHi = s.Name.StartsWith("PCB", StringComparison.Ordinal) ? 2e9  : 20e9;
        double length = 0.6 * LambdaG(s, fHi);

        var line = new RectShape
        {
            Layer = s.Signal,
            X1 = 0, Y1 = 0, X2 = Dbus(length), Y2 = Dbus(s.WidthM),
        };
        var ports = new[]
        {
            PortLabel(s.Signal, 0,      0.5 * s.WidthM, "P1", 0.3 * s.WidthM),
            PortLabel(s.Signal, length, 0.5 * s.WidthM, "P2", 0.3 * s.WidthM),
        };

        // ── Kernel A: no port labels needed, the two ports ARE the line's ends (R-mom-15) ──────
        var a = Run(Setup("gate3-A", EmAnalysisKind.CrossSection, [fLo, fHi]), s, [line]);
        var aEeff = Eeff(a.Data!, "tline");

        // ── Kernel B: the SAME artwork plus the port labels, one .cem field different ──────────
        var b = Run(Setup("gate3-B", EmAnalysisKind.Planar, [fLo, fHi]), s, [line, .. ports]);
        var bEeff = Eeff(b.Data!, PlanarKernel.DiagnosticsGroup);

        var freqs = Freq(b.Data!);
        int np = b.Data!.CubesIn(PlanarKernel.DiagnosticsGroup)["Eeff"].Axes[1].Values.Length;

        double aLo = aEeff[0], bLo = bEeff[0 * np + 0];
        double aHi = aEeff[^1], bHi = bEeff[(freqs.Length - 1) * np + 0];

        double relLo = (bLo - aLo) / aLo, relHi = (bHi - aHi) / aHi;

        output.WriteLine($"GATE 3 — {s.Name}, line {length * 1e3:F2} mm × {s.WidthM * 1e6:F0} µm, " +
                         $"shipping mesh, N = {b.PlanarMesh!.UnknownCount}");
        output.WriteLine($"  {freqs[0] / 1e9,5:F2} GHz : A ε_eff {aLo:F4}   B ε_eff {bLo:F4}   " +
                         $"{relLo * 100,+7:F2}%");
        output.WriteLine($"  {freqs[^1] / 1e9,5:F2} GHz : A ε_eff {aHi:F4}   B ε_eff {bHi:F4}   " +
                         $"{relHi * 100,+7:F2}%");

        Assert.True(Math.Abs(relLo) <= 0.04,
            $"A and B must agree on a uniform line at the bottom of the band. A {aLo:F4}, B {bLo:F4}, " +
            $"{relLo * 100:F2}% apart — the gate is 4%, set from L8d's measured ~0.9% metal-thickness " +
            "sensitivity, not from what happens to pass.");

        // The divergence upward is a RESULT, not an error — it is one of the two things kernel B
        // exists to compute. Reported, never gated as a failure.
        output.WriteLine(relHi > relLo
            ? "  → B rises faster than A with frequency: microstrip dispersion, which is a RESULT."
            : "  → B did NOT rise relative to A over this band; at this ratio the effect is small.");

        // Both ran through the registry, and both said which kernel and why.
        Assert.Equal(EmAnalysisKind.CrossSection, a.Kind);
        Assert.Equal(EmAnalysisKind.Planar,       b.Kind);
        Assert.Contains(a.Warnings, w => w.Contains("explicitly", StringComparison.Ordinal));
        Assert.Contains(b.Warnings, w => w.Contains("explicitly", StringComparison.Ordinal));

        // …and the whole product path landed its artifacts.
        Assert.True(File.Exists(b.SnpPath!));
        Assert.True(File.Exists(a.SnpPath!));
    }

    /// <summary>
    /// <b>The routine half of gate 3, and the reason it exists is a MEASUREMENT that contradicted the
    /// brief's own cost assumption — recorded rather than quietly absorbed.</b>
    ///
    /// <para>The tiering rule this slice was given keeps "one representative phase-gate case per
    /// starter" in the routine tier, on the grounds that A-vs-B is the cheapest of the three gates.
    /// It is — and at the SHIPPING mesh D7 requires, it still measures <b>~74 s for the FR-4 starter
    /// alone and ~2 min 12 s for both</b> on this machine, against a stated routine budget of ~90 s
    /// for the ENTIRE repository. The cost is not the DUT: it is the two calibration standards per
    /// port, which L8d measured at 2.58× the DUT's own unknowns and 78% of a de-embedded point, and
    /// which the two ends of a plain microstrip do not share because L8b's edge grading is not exactly
    /// mirror-symmetric. Nothing about that is avoidable at the shipping mesh.</para>
    ///
    /// <para>So the ACCURACY claim is opt-in for both starters, and what stays routine is the claim
    /// most likely to break and cheapest to check: <b>the product path itself</b> — one drawn layout,
    /// two <c>.cem</c> setups differing in one field, the registry choosing, both kernels returning a
    /// house-shaped <c>DataSet</c> and landing an <c>.snp</c>. A coarse mesh tests that just as hard,
    /// because none of it is about mesh quality.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Starters))]
    public void Gate3Wiring_OneLayout_TwoSetups_BothKernelsRunThroughTheRegistry(string key)
    {
        var s = For(key);
        double fHi = key == "pcb" ? 2e9 : 20e9;
        double length = 0.35 * LambdaG(s, fHi);
        var coarse = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false);

        var line = new RectShape
        {
            Layer = s.Signal, X1 = 0, Y1 = 0, X2 = Dbus(length), Y2 = Dbus(s.WidthM),
        };
        var ports = new[]
        {
            PortLabel(s.Signal, 0,      0.5 * s.WidthM, "P1", 0.3 * s.WidthM),
            PortLabel(s.Signal, length, 0.5 * s.WidthM, "P2", 0.3 * s.WidthM),
        };

        var a = Run(Setup("wiring-A", EmAnalysisKind.CrossSection, [fHi], coarse), s, [line]);
        var b = Run(Setup("wiring-B", EmAnalysisKind.Planar,       [fHi], coarse), s, [line, .. ports]);

        double aEeff = Eeff(a.Data!, "tline")[0];
        double bEeff = Eeff(b.Data!, PlanarKernel.DiagnosticsGroup)[0];

        output.WriteLine($"GATE 3 (wiring) — {s.Name}, coarse mesh, N = {b.PlanarMesh!.UnknownCount}: " +
                         $"A ε_eff {aEeff:F4}, B ε_eff {bEeff:F4} at {fHi / 1e9:F1} GHz");

        // The registry chose, and said so, on BOTH runs.
        Assert.Equal(EmAnalysisKind.CrossSection, a.Kind);
        Assert.Equal(EmAnalysisKind.Planar,       b.Kind);
        Assert.Equal(QuasiStaticKernel.KernelName, a.KernelName);
        Assert.Equal(PlanarKernel.KernelName,      b.KernelName);

        // Both landed the house result shape and the predictable artifact.
        Assert.True(File.Exists(a.SnpPath!));
        Assert.True(File.Exists(b.SnpPath!));
        Assert.Contains("tline", a.Data!.Groups);
        Assert.Contains(PlanarKernel.DiagnosticsGroup, b.Data!.Groups);

        // Both answers are physical — between air and the substrate, which is all a COARSE mesh is
        // entitled to claim. The accuracy statement is the Benchmark gate's.
        Assert.InRange(aEeff, 1.0, s.EpsR);
        Assert.InRange(bEeff, 1.0, s.EpsR);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — "A quarter-wave open stub resonates at the right frequency."   Category=Benchmark
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// L8d already measured this once on a hand-built mesh (notch at 5.700 GHz against 5.476 GHz
    /// predicted WITH the 0.4 h open-end extension and 6.000 GHz without). What this adds is the
    /// second starter and the run through the product path.
    ///
    /// <para><b>The open-end extension stays named in the prediction.</b> A bare quarter wavelength
    /// is NOT the reference, and treating it as one would invite tuning the solver toward a formula
    /// that was never the truth: the extension is 9.6% of the stub on the FR-4 hero.</para>
    ///
    /// <para>Six points, not fourteen: a coarse scan plus one refine is what LOCATING a notch needs,
    /// and none of this claim is about a dense sweep.</para>
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [MemberData(nameof(Starters))]
    public void Gate1_AQuarterWaveOpenStub_ResonatesWhereThePredictionSaysIncludingTheOpenEnd(string key)
    {
        var s = For(key);
        double fTarget = key == "pcb" ? 6e9 : 30e9;

        // ── THE FIXTURE IS SIZED FROM KERNEL A's ε_eff, AND THE FIRST VERSION WAS NOT ────────────
        //
        // Sizing the stub from the crude pre-solve estimate ε_eff ≈ (εᵣ+1)/2 — which is what
        // PlanarCalibration has to use before it has solved anything — makes the fixture 23% too
        // long on FR-4 (2.70 against a true 3.29). The stub then resonates 20% below the frequency
        // the prediction names, and a ±15% gate passes it only because the band is wide. MEASURED,
        // on the first run of this gate: stub 7.602 mm, notch 4.800 GHz against a bare prediction of
        // 6.000 and a corrected one of 5.534.
        //
        // Kernel A's ε_eff is the right input and is not circular: it is a 2-D quasi-static solve
        // that shares no code path with kernel B, and it is the number already gated to 2% against
        // Hammerstad-Jensen (EmAcceptanceTests). Using it means the prediction and the fixture agree
        // about what wavelength they are talking about, which is the only way this gate measures the
        // OPEN END rather than measuring ε_eff twice.
        double eeffA   = KernelAEeff(s);
        double lambda  = EmConstants.C0 / (fTarget * Math.Sqrt(eeffA));
        double stub    = 0.25 * lambda;
        double openEnd = 0.4 * s.HeightM;               // the classic open-end extension
        double through = 0.5 * lambda;                  // enough uniform line to calibrate against

        var line = new RectShape
        {
            Layer = s.Signal, X1 = 0, Y1 = 0, X2 = Dbus(through), Y2 = Dbus(s.WidthM),
        };
        var arm = new RectShape
        {
            Layer = s.Signal,
            X1 = Dbus(0.5 * through - 0.5 * s.WidthM), Y1 = Dbus(s.WidthM),
            X2 = Dbus(0.5 * through + 0.5 * s.WidthM), Y2 = Dbus(s.WidthM + stub),
        };
        var ports = new[]
        {
            PortLabel(s.Signal, 0,       0.5 * s.WidthM, "P1", 0.3 * s.WidthM),
            PortLabel(s.Signal, through, 0.5 * s.WidthM, "P2", 0.3 * s.WidthM),
        };

        // Five points over 0.85…1.05 f_target. The notch is expected at 0.92…0.97, comfortably
        // interior; five rather than seven because every point is a de-embedded solve and this gate
        // is already the most expensive of the three.
        var scan = LinSpace(0.85 * fTarget, 1.05 * fTarget, 5);
        var run  = Run(Setup("gate1-stub", EmAnalysisKind.Planar, scan), s, [line, arm, .. ports]);

        var freqs = Freq(run.Data!);
        var mags  = new double[freqs.Length];
        int notch = 0;
        for (int i = 0; i < freqs.Length; i++)
        {
            mags[i] = S(run.Data!, i, 1, 0).Magnitude;
            if (mags[i] < mags[notch]) notch = i;
        }

        double bare      = EmConstants.C0 / (4 * stub * Math.Sqrt(eeffA));
        double corrected = EmConstants.C0 / (4 * (stub + openEnd) * Math.Sqrt(eeffA));

        output.WriteLine($"GATE 1 — {s.Name}: ε_eff(A) {eeffA:F4}, stub {stub * 1e3:F3} mm, " +
                         $"open-end extension {openEnd * 1e3:F3} mm ({openEnd / stub * 100:F1}% of " +
                         $"the stub), N = {run.PlanarMesh!.UnknownCount}");
        for (int i = 0; i < freqs.Length; i++)
            output.WriteLine($"    {freqs[i] / 1e9,7:F3} GHz  |S21| {mags[i]:G4}{(i == notch ? "   ← min" : "")}");

        Assert.True(notch > 0 && notch < freqs.Length - 1,
            $"the notch landed at the edge of the scanned band ({freqs[notch] / 1e9:F3} GHz) — the " +
            "scan does not bracket the resonance, so nothing has been located.");

        // The grid minimum is quantised to the scan step; a parabola through it and its two
        // neighbours removes that term, which is what makes a tolerance below the step size honest.
        double fPeak = ParabolicMinimum(freqs[notch - 1], freqs[notch], freqs[notch + 1],
                                        mags[notch - 1], mags[notch], mags[notch + 1]);

        output.WriteLine($"  |S21| minimum: grid {freqs[notch] / 1e9:F3} GHz " +
                         $"(step {(freqs[1] - freqs[0]) / 1e9:F3} GHz), interpolated " +
                         $"{fPeak / 1e9:F3} GHz");
        output.WriteLine($"  bare λ_g/4 predicts {bare / 1e9:F3} GHz; with the open-end extension " +
                         $"{corrected / 1e9:F3} GHz");
        output.WriteLine($"  → {(fPeak - corrected) / corrected * 100:+0.0;-0.0}% against the " +
                         $"corrected prediction, {(fPeak - bare) / bare * 100:+0.0;-0.0}% against the bare one");

        // ── The two claims, and neither tolerance was chosen after seeing the number ─────────────
        //
        // 1. The resonance is BELOW the bare quarter wavelength. That is the open-end extension
        //    being real, and it is the claim the gate exists to make: an uncorrected λ_g/4 is the
        //    thing someone would "fix" the solver toward.
        Assert.True(fPeak < bare,
            $"the notch at {fPeak / 1e9:F3} GHz is not below the bare λ_g/4 prediction " +
            $"{bare / 1e9:F3} GHz — the open-end extension is not showing up at all.");

        // 2. It is within 12% of the OPEN-END-CORRECTED prediction. 12% because the 0.4h extension
        //    is itself a rule of thumb — Hammerstad's own expression gives 0.387h for this
        //    cross-section, a 3% difference in the extension alone — and because a stub's loaded Q
        //    puts the |S21| minimum near, not exactly at, the unloaded resonance.
        double err = Math.Abs(fPeak - corrected) / corrected;
        Assert.True(err <= 0.12,
            $"the notch is {err:P1} from the open-end-corrected prediction " +
            $"({fPeak / 1e9:F3} GHz against {corrected / 1e9:F3} GHz), past the 12% gate.");
    }

    /// <summary>The propagation constant kernel B itself reported, at one frequency index.</summary>
    private static Complex run0Gamma(DataSet d, int i)
    {
        var cube = d.CubesIn(PlanarKernel.DiagnosticsGroup)["Gamma"];
        int np   = cube.Axes.Count > 1 ? cube.Axes[1].Values.Length : 1;
        return cube.ComplexValues![i * np];
    }

    /// <summary>Kernel A's ε_eff for a starter's own 50 Ω cross-section — the sizing input for
    /// gate 1, and the A side of gate 3.</summary>
    private static double KernelAEeff(Starter s)
    {
        var line = new RectShape
        {
            Layer = s.Signal, X1 = 0, Y1 = 0, X2 = Dbus(10 * s.WidthM), Y2 = Dbus(s.WidthM),
        };
        var ex = CrossSectionExtractor.Extract([line], s.Tech, Dbu, null);
        Assert.True(ex.Ok, ex.Refusal);
        var report = BoundaryMesher.Mesh(ex.Problem!, EmMeshSettings.Default);
        return RlgcExtractor.Extract(ex.Problem!, report).Eeff;
    }

    /// <summary>The vertex of the parabola through three equally-spaced samples, clamped to the
    /// bracket. Three lines of algebra, written out because a gate should say what it measured.</summary>
    private static double ParabolicMinimum(double xL, double xC, double xR,
                                           double yL, double yC, double yR)
    {
        double denom = yL - 2 * yC + yR;
        if (Math.Abs(denom) < 1e-30) return xC;
        double delta = 0.5 * (yL - yR) / denom;                 // in units of the sample step
        return Math.Clamp(xC + delta * (xR - xC), xL, xR);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 2 — "A bend's s-parameters are physically sane."   Category=Benchmark
    //
    // THIS IS THE ONE NOBODY HAS RUN, and "sane" has to be given a meaning before it is measured.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [Trait("Category", "Benchmark")]
    [MemberData(nameof(Starters))]
    public void Gate2_ABendIsReciprocalPassiveAndCapacitive_AndTheMitreLowersItsReflection(string key)
    {
        var s = For(key);
        double armAtGhz = key == "pcb" ? 10e9 : 40e9;
        double arm = 0.35 * LambdaG(s, armAtGhz);
        double[] freqs = [0.25 * armAtGhz, 0.55 * armAtGhz, armAtGhz];

        var unmitred = Run(Setup("gate2-square", EmAnalysisKind.Planar, freqs), s, Bend(s, arm, false));
        var mitred   = Run(Setup("gate2-mitred", EmAnalysisKind.Planar, freqs), s, Bend(s, arm, true));

        var f = Freq(unmitred.Data!);
        output.WriteLine($"GATE 2 — {s.Name}: arms {arm * 1e3:F3} mm, W {s.WidthM * 1e6:F0} µm, " +
                         $"N = {unmitred.PlanarMesh!.UnknownCount} (square) / " +
                         $"{mitred.PlanarMesh!.UnknownCount} (mitred)");

        var squareS11 = new double[f.Length];
        var mitreS11  = new double[f.Length];

        for (int i = 0; i < f.Length; i++)
        {
            var u = unmitred.Data!;
            var m = mitred.Data!;
            squareS11[i] = S(u, i, 0, 0).Magnitude;
            mitreS11[i]  = S(m, i, 0, 0).Magnitude;

            // ── Reciprocity carries over from kernel A and is structural (D1's Y = BᵀZ⁻¹B) ─────
            var s12 = S(u, i, 0, 1);
            var s21 = S(u, i, 1, 0);
            Assert.True((s12 - s21).Magnitude <= 1e-9 * Math.Max(1, s21.Magnitude),
                $"reciprocity at {f[i] / 1e9:F2} GHz: S12 {s12}, S21 {s21}");

            // ── Passivity carries over too. LOSSLESSNESS DOES NOT, and there is no test for it —
            //    an open planar structure radiates and launches surface waves, both of which carry
            //    real power away, so |S11|² + |S21|² < 1 LEGITIMATELY (L8a's forward warning).
            double power = squareS11[i] * squareS11[i] + s21.Magnitude * s21.Magnitude;
            Assert.True(power <= 1.0 + 1e-6, $"passivity at {f[i] / 1e9:F2} GHz: Σ|S|² = {power:G6}");

            output.WriteLine($"  {f[i] / 1e9,6:F2} GHz  |S11| square {squareS11[i]:G4}   " +
                             $"mitred {mitreS11[i]:G4}   Σ|S|² {power:F6}");
        }

        // ── A bend is a shunt capacitance to first order: |S11| small and RISING with frequency ──
        Assert.True(squareS11[0] < 0.25,
            $"|S11| at the bottom of the band is {squareS11[0]:G4}; a bend is a small discontinuity, " +
            "not a mismatch.");
        Assert.True(squareS11[^1] > squareS11[0],
            $"|S11| must rise with frequency for a shunt-capacitive discontinuity: " +
            $"{squareS11[0]:G4} at {f[0] / 1e9:F2} GHz, {squareS11[^1]:G4} at {f[^1] / 1e9:F2} GHz.");

        // ── THE ELECTRICAL HALF OF L8b's STAIRCASING MEASUREMENT, never made before ─────────────
        //
        // L8b measured that the staircased mitre SURVIVES meshing (2.8% cut-area error, 18 cells
        // removed, N 550 against the unmitred 586). If the two came out electrically IDENTICAL here,
        // that would contradict L8b's own measurement — a finding about the mesher, not a tolerance
        // to widen. So this is asserted, and the report says by how much.
        double ratio = mitreS11[^1] / squareS11[^1];
        output.WriteLine($"  mitred/square |S11| at {f[^1] / 1e9:F2} GHz = {ratio:F4}");

        Assert.True(mitreS11[^1] < squareS11[^1],
            $"the mitre exists to REDUCE the discontinuity, so its |S11| must be lower: mitred " +
            $"{mitreS11[^1]:G4} vs square {squareS11[^1]:G4}. If they are identical the mesh is not " +
            "resolving the mitre, which is a finding about L8b, not a tolerance to widen.");

        // ── The equivalent shunt capacitance, against a published estimate whose inputs ARE
        //    verifiable: Kirschning, Jansen & Koster's L-C-L bend model, already in this repository
        //    with its source read directly (MicrostripBendLC's own header). Tier C3's rule — never
        //    build a gate on numbers of unknown provenance — is satisfied by using a model whose
        //    provenance is on the record rather than by substituting a limiting case.
        //
        //    THE REFERENCE PLANES ARE NOT AT THE CORNER, and ignoring that is what made the first
        //    version of this comparison meaningless. De-embedding puts them one mesh cell in from the
        //    drawn metal edge — 0.35 λ_g of uniform line away from the bend — so the raw S₁₁ carries
        //    2βℓ of line phase on top of the discontinuity's own. |S₁₁| survives that rotation but
        //    the shunt-element extraction does not, because it reads the reactance from the PHASE.
        //    So S₁₁ is rotated back to the corner using the run's OWN reported γ (the "planar"
        //    group's Gamma cube), which costs nothing and is the number the kernel already published.
        var gamma = run0Gamma(unmitred.Data!, 0);
        double ellToCorner = arm - 0.5 * s.WidthM;
        var s11Corner = S(unmitred.Data!, 0, 0, 0) * Complex.Exp(2.0 * gamma * ellToCorner);
        double c = ShuntCapacitanceFrom(s11Corner, f[0], 50.0);

        output.WriteLine($"  S₁₁ at the port plane {S(unmitred.Data!, 0, 0, 0):G4}, de-rotated to the " +
                         $"corner ({ellToCorner * 1e3:F3} mm of line, γ = {gamma:G4}) {s11Corner:G4}");
        var (_, cModel, _) = MicrostripBendLC.Compute(
            s.WidthM, s.HeightM, s.EpsR, MicrostripBendMiter.None, new MicrostripValidityReporter("gate2"));

        output.WriteLine($"  extracted shunt C at {f[0] / 1e9:F2} GHz = {c * 1e15:F1} fF; " +
                         $"Kirschning-Jansen-Koster model = {cModel * 1e15:F1} fF " +
                         $"(ratio {c / cModel:F2})");

        Assert.True(c > 0,
            $"the extracted equivalent shunt element is {c * 1e15:F1} fF — a bend is CAPACITIVE to " +
            "first order, and a negative value would mean the reflection has the wrong sign.");

        // MEASURED: 0.39 on FR-4 at 2.5 GHz, 0.69 on GaAs at 10 GHz. The extraction reads LOW, and
        // the reason is structural rather than an error: Kirschning-Jansen-Koster's bend is an L-C-L
        // network, and this fits a PURE shunt element to it. The two series inductances reflect with
        // the opposite sign to the shunt capacitance, so a pure-shunt fit of an L-C-L network always
        // under-reads C, and under-reads it more the longer the bend is electrically — which is the
        // ordering seen (FR-4's bend at 2.5 GHz is the electrically larger of the two).
        //
        // The band is therefore an ORDER-OF-MAGNITUDE-AND-SIGN gate and is written as one. Fitting
        // L-C-L properly needs three complex unknowns from a two-port and is a modelling exercise,
        // not a phase gate; what this gate is for is catching a bend that comes out inductive, or out
        // by a decade, which is what a wrong port orientation or a wrong reference plane would do.
        Assert.InRange(c / cModel, 0.2, 5.0);
    }

    /// <summary>
    /// An L-shaped bend as ONE polygon, optionally with the outer corner chamfered.
    ///
    /// <para><b>Which corner gets cut is the whole test, and the first version cut the wrong one.</b>
    /// The signal enters at (0, w/2), runs +x, turns, and leaves at (arm − w/2, arm). The inner corner
    /// of that turn is (arm − w, w); the OUTER corner — the one a mitre removes — is therefore
    /// <c>(arm, 0)</c>. Chamfering (arm, arm) instead cuts the far END of the vertical arm, which is
    /// where port 2 lives, and the port then sits on a polygon vertex rather than spanning a line end.
    /// Measured consequence: |S₁₁| went to <b>0.98 on FR-4 and 0.998 on GaAs</b> — near-total
    /// reflection, and physical, because that is genuinely what a port on a tip does. It looked like a
    /// solver fault and was a fixture fault.</para>
    ///
    /// <para>Drawn as a single polygon rather than two rectangles plus a boolean, for two reasons: a
    /// mitre has to trim BOTH arms at the shared corner, which overlapping rectangles cannot express;
    /// and a gate should not depend on <c>LayoutBooleans</c>, the component sitting next to it.</para>
    ///
    /// <para>The chamfer removes <c>w/2</c> along each axis. That is close to the classical optimum for
    /// microstrip but is NOT claimed to be it — this gate's claim is only that the mitre changes the
    /// answer and lowers the reflection, not that it reproduces a published optimum.</para>
    /// </summary>
    private static IReadOnlyList<LayoutShape> Bend(Starter s, double arm, bool mitred)
    {
        double w = s.WidthM, cut = 0.5 * w;

        long[] square =
        [
            0,             0,
            Dbus(arm),     0,
            Dbus(arm),     Dbus(arm),
            Dbus(arm - w), Dbus(arm),
            Dbus(arm - w), Dbus(w),
            0,             Dbus(w),
        ];
        long[] chamfered =
        [
            0,                 0,
            Dbus(arm - cut),   0,
            Dbus(arm),         Dbus(cut),      // ← the outer corner (arm, 0), cut back
            Dbus(arm),         Dbus(arm),
            Dbus(arm - w),     Dbus(arm),
            Dbus(arm - w),     Dbus(w),
            0,                 Dbus(w),
        ];

        return
        [
            new PolygonShape { Layer = s.Signal, Xy = mitred ? chamfered : square },
            PortLabel(s.Signal, 0,             0.5 * w, "P1", 0.3 * w),
            PortLabel(s.Signal, arm - 0.5 * w, arm,     "P2", 0.3 * w),
        ];
    }

    /// <summary>
    /// The equivalent shunt susceptance of a symmetric two-port from S₁₁, in the electrically-small
    /// limit: a shunt admittance <c>Y</c> across a <c>Z₀</c> line has
    /// <c>S₁₁ = −(Y Z₀ / 2) / (1 + Y Z₀ / 2)</c>, so <c>Y = −(2/Z₀)·S₁₁/(1 + S₁₁)</c>, and the
    /// capacitance is <c>Im(Y)/ω</c>. Stated here rather than imported because it is three lines of
    /// two-port algebra, not physics — and because a gate should say exactly what it extracted.
    /// </summary>
    private static double ShuntCapacitanceFrom(Complex s11, double fHz, double z0)
    {
        var y = -(2.0 / z0) * s11 / (Complex.One + s11);
        return y.Imaginary / (2.0 * Math.PI * fHz);
    }
}
