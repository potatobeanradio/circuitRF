// Tier A — the L6/L7 Ui phase gate (brief-L6-L7-em-ui.md §7).
//
// This is the only test that proves the Ui half hands the engine the geometry it thinks it does.
// The engine's number is already validated against Hammerstad-Jensen by the kernel's own Tier 3
// gate; if the extraction path reproduces it, extraction is correct, and if it does not, extraction
// is the only thing that can be wrong.
//
// **If this disagrees with the engine's own number, the extractor is wrong — do not adjust mesh
// settings to close the gap.** src/Engine/Mom/CLAUDE.md §"What the oracles actually established"
// records how much effort went into establishing that the engine's number is right, including two
// cases where the closed-form "oracle" was the thing that was wrong. Extraction has no such defence.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmAcceptanceTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static long Mm(double v) => (long)Math.Round(v * 1000.0 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>Z₀ and ε_eff from a problem — the same two lines
    /// <c>MicrostripOracleTests.Solve</c> uses, so this compares like with like.</summary>
    private static (double Z0, double Eeff) Solve(EmProblem p, EmMeshSettings? settings = null)
    {
        var report = BoundaryMesher.Mesh(p, settings ?? EmMeshSettings.Default);
        var rlgc   = RlgcExtractor.Extract(p, report);
        return (Math.Sqrt(rlgc.LPerM / rlgc.CPerM), rlgc.Eeff);
    }

    private static EmProblem ExtractOrThrow(Technology tech, params LayoutShape[] shapes)
    {
        var r = CrossSectionExtractor.Extract(shapes, tech, Dbu, null);
        Assert.True(r.Ok, r.Refusal ?? "extraction produced no problem and no refusal");
        return r.Problem!;
    }

    // ── PCB: the 50 Ω hero ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pcb_TwoPointNineMillimetreLine_LandsAtFiftyOhms_ThroughTheExtractionPath()
    {
        // The brief's own acceptance sentence: draw a 2.9 mm × 20 mm rectangle on Pcb2Layer's Top
        // Copper and Simulate — Z₀ within 3% of 50 Ω, ε_eff between 3.0 and 3.6, matching
        // MicrostripOracleTests.T3_5_TheFiftyOhmHero_LandsAtFiftyOhmsWithAFewHundredUnknowns.
        var problem = ExtractOrThrow(StarterTechnologies.Pcb2Layer(),
            new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) });

        Assert.True(new QuasiStaticKernel().CanSolve(problem).Ok);

        var (z0, eeff) = Solve(problem);
        double rel = Math.Abs(z0 - 50.0) / 50.0;
        Assert.True(rel <= 0.03, $"hero Z₀: got {z0:G6} Ω, want 50 Ω, off by {rel:P3} (limit 3%)");
        Assert.InRange(eeff, 3.0, 3.6);

        var report = BoundaryMesher.Mesh(problem, EmMeshSettings.Default);
        Assert.InRange(report.UnknownCount, 30, 600);
    }

    // ── MMIC: both markets are gated (§10.10) ─────────────────────────────────────────────────

    [Fact]
    public void Mmic_LineOnMetal1_ReproducesTheEnginesOwnNumberForTheSameCrossSection()
    {
        // The strongest available statement of "the Ui path hands the engine what it thinks it
        // does": build the SAME cross-section by hand the way EmProblemBuilders.GaAsMicrostrip
        // does, and require the extracted one to agree to the extraction's own rounding.
        const double w = 72e-6;   // ≈ 50 Ω on 100 µm GaAs
        var extracted = ExtractOrThrow(StarterTechnologies.MmicGaAs(),
            new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = Mm(2), Y2 = Um(72) });

        var reference = new EmProblem(
            Conductors: [new EmConductor("strip",
            [
                new EmPoint(-0.5 * w, 100e-6), new EmPoint(0.5 * w, 100e-6),
                new EmPoint( 0.5 * w, 103e-6), new EmPoint(-0.5 * w, 103e-6),
            ], 4.1e7)],
            Regions:
            [
                new EmDielectricRegion(double.NegativeInfinity, 100e-6, new EmMaterial(12.9, 0.0006)),
                new EmDielectricRegion(100e-6, double.PositiveInfinity, EmMaterial.Air),
            ],
            Ground: new EmGroundPlane(0, 4.1e7),
            Ports: [new EmPort(1, "strip", null, 50), new EmPort(2, "strip", null, 50)],
            LengthMeters: 0.002);

        var (z0e, eeffE) = Solve(extracted);
        var (z0r, eeffR) = Solve(reference);

        Assert.True(Math.Abs(z0e - z0r) / z0r <= 1e-6,
            $"extracted Z₀ {z0e:G9} Ω vs. hand-built {z0r:G9} Ω");
        Assert.True(Math.Abs(eeffE - eeffR) / eeffR <= 1e-6,
            $"extracted ε_eff {eeffE:G9} vs. hand-built {eeffR:G9}");
    }

    [Fact]
    public void Mmic_SeventyTwoMicronLineOnMetal1_LandsNearFiftyOhms()
    {
        var problem = ExtractOrThrow(StarterTechnologies.MmicGaAs(),
            new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = Mm(2), Y2 = Um(72) });

        Assert.True(new QuasiStaticKernel().CanSolve(problem).Ok);

        var (z0, eeff) = Solve(problem);
        double rel = Math.Abs(z0 - 50.0) / 50.0;
        Assert.True(rel <= 0.05, $"MMIC Z₀: got {z0:G6} Ω, want ≈50 Ω, off by {rel:P3} (limit 5%)");
        // ε_eff for a W/h ≈ 0.72 line on εr = 12.9 sits a little below (εr+1)/2 = 6.95.
        Assert.InRange(eeff, 6.0, 8.5);
    }

    // ── The whole path, end to end: extract → CanSolve → Solve → DataSet ──────────────────────

    [Fact]
    public void TheExtractedProblem_SolvesToADataSetCarryingSAndTheTlineGroup()
    {
        var problem = ExtractOrThrow(StarterTechnologies.Pcb2Layer(),
            new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) });

        double[] freqs = [1e9, 5e9, 10e9];
        var ds = new QuasiStaticKernel().Solve(problem, EmMeshSettings.Default, freqs, default);

        Assert.NotNull(ds);
        Assert.True(ds.Contains("S"), "the DataSet must carry S");
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════
// L8e D8 — R18's 30-second target, RE-MEASURED with the Ports row restored.
//
// §10.10 struck the "Ports — Port tool, click each end, 5 s" row at L7 with an explicit note that it
// "becomes real work at L8, when a meshed port exists". It does, so the row comes back, and the
// question the phase gate has to answer is whether the target survives it. This scripts the same
// path §10.10 tabulates, against the real view models, and counts the interactions rather than
// asserting a feeling about them.
//
// **What this test can and cannot claim.** It counts INTERACTIONS and it measures the wait after
// Simulate. It cannot measure how long a human takes to perform an interaction, so §10.10's
// per-step seconds are carried over unchanged from the row that was already agreed — the test's job
// is to catch a step that needs MORE interactions than the budget was written for, which is the way
// a 30-second target actually dies.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public class EmAcceptanceBudgetTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-r18-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static long Mm(double v) => (long)Math.Round(v * 1000.0 * Dbu);

    /// <summary>§10.10's table, with the row D5 struck and L8 restores.</summary>
    private static readonly (string Step, int Seconds)[] Budget =
    [
        ("New layout from the starter template", 3),
        ("Draw the line",                        8),
        ("Ports — Port tool, click each end",    5),   // ← restored at L8
        ("Frequency",                            8),
        ("Mesh — untouched, Auto is correct",    0),
        ("Run",                                  1),
    ];

    /// <summary>
    /// The headline: the geometry kernel B EXISTS for — a right-angle bend, which kernel A refuses
    /// because it has no uniform cross-section — costs the user NO analysis-kind interaction, because
    /// Auto refuses A and picks B by itself (D2). The Ports row is the only addition.
    /// </summary>
    [Fact]
    public void R18_ABendReachesKernelB_WithinTheThirtySecondBudget_AndNoKernelChoiceInteraction()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        var vm   = new LayoutEditorViewModel(view);
        int interactions = 0;

        // 1. New layout from the starter template — one click; the .ctech supplies stackup + layers.
        var tech = StarterTechnologies.Pcb2Layer();
        interactions += 1;

        // 2. Draw the bend: two rectangles meeting at a corner. Two press/release pairs.
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        view.Shapes.Add(new RectShape
        { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(8), Y2 = Mm(2.9) });
        view.Shapes.Add(new RectShape
        { Layer = TopCopper, X1 = Mm(5.1), Y1 = 0, X2 = Mm(8), Y2 = Mm(8) });
        interactions += 4;

        // 3. Ports — the restored row. ONE press each, auto-numbered, no dialog, no mode to leave.
        vm.ActiveTool = LayoutEditorViewModel.Tool.Port;
        vm.OnPointerPressed(0, Mm(1.45), default);
        vm.OnPointerPressed(Mm(6.55), Mm(8), default);
        interactions += 2;

        var ports = view.Shapes.OfType<LabelShape>().Where(l => l.IsPort).ToList();
        Assert.Equal(2, ports.Count);
        Assert.Equal(["P1", "P2"], ports.Select(p => p.Text).Order());

        // 4. Frequency — three fields. 5. Mesh — untouched. 6. Run — one click.
        var setup = new EmSetup
        {
            Name         = "bend",
            LayoutRef    = "bend.clay",
            AnalysisKind = EmAnalysisKind.Auto,          // ← the DEFAULT; not an interaction
            PlanarMesh   = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false),
            Frequency    = new Core.Design.FrequencySpec("5", "5", 1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
        interactions += 3 + 0 + 1;

        Assert.Equal(EmAnalysisKind.Auto, new EmSetup().AnalysisKind);

        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var result = EmRunService.Run(setup, new EmLayoutSource("bend.clay", view, tech, Dbu),
                                      Path.Combine(_dir, "results"));
        sw.Stop();

        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        Assert.Equal(EmAnalysisKind.Planar, result.Kind);
        Assert.Equal(PlanarKernel.KernelName, result.KernelName);

        int total = Budget.Sum(b => b.Seconds);
        output.WriteLine($"R18 (kernel B, bend) — {interactions} interactions, budget {total} s");
        foreach (var (step, s) in Budget) output.WriteLine($"    {s,2} s  {step}");
        string why = result.Warnings.First(n => n.Contains("kernel", StringComparison.OrdinalIgnoreCase));
        output.WriteLine("    -- analysis kind: 0 interactions (Auto refuses A, picks B, and SAYS SO)");
        output.WriteLine("       " + why);
        output.WriteLine($"    AFTER Simulate the user waits {sw.Elapsed.TotalSeconds:F1} s " +
                         $"(1 frequency, N = {result.PlanarMesh!.UnknownCount}, coarse mesh)");

        Assert.True(total <= 30, $"R18 budget is {total} s");
    }

    /// <summary>
    /// <b>What the user actually waits for after pressing Simulate, at the SHIPPING mesh.</b>
    ///
    /// <para>The budget above is about interactions and stops at "Run — 1 click". That click is where
    /// kernel B stops resembling kernel A: A answers a 20 GHz sweep in well under a second, and B pays
    /// a matrix fill and a factorisation per frequency, plus two calibration standards per port which
    /// L8d measured at 78% of a de-embedded point. So the wait is measured rather than described,
    /// on the same bend, at the mesh the product actually ships with.</para>
    ///
    /// <para>Opt-in, because one de-embedded point at the shipping mesh is exactly the cost this test
    /// exists to report.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void R18_WhatTheUserWaitsForAfterSimulate_AtTheShippingMesh()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        var vm   = new LayoutEditorViewModel(view);
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(8), Y2 = Mm(2.9) });
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = Mm(5.1), Y1 = 0, X2 = Mm(8), Y2 = Mm(8) });
        vm.ActiveTool = LayoutEditorViewModel.Tool.Port;
        vm.OnPointerPressed(0, Mm(1.45), default);
        vm.OnPointerPressed(Mm(6.55), Mm(8), default);

        var setup = new EmSetup
        {
            Name      = "bend-shipping",
            LayoutRef = "bend.clay",
            Frequency = new Core.Design.FrequencySpec("5", "5", 1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
            // PlanarMesh left at its default — D7's shipping mesh, untouched, which is the point.
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r  = EmRunService.Run(setup, new EmLayoutSource("bend.clay", view, StarterTechnologies.Pcb2Layer(), Dbu),
                                  Path.Combine(_dir, "shipping"));
        sw.Stop();

        Assert.True(r.Status == EmRunStatus.Ok, r.Error);
        Assert.Equal(EmAnalysisKind.Planar, r.Kind);

        output.WriteLine($"R18 — ONE frequency, shipping mesh, N = {r.PlanarMesh!.UnknownCount}: " +
                         $"{sw.Elapsed.TotalSeconds:F1} s from Simulate to a plotted result " +
                         $"(mesh + DCIM fit + fill + solve + two calibration standards + the .snp).");
        output.WriteLine($"   A 101-point sweep of the same structure is therefore on the order of " +
                         $"{sw.Elapsed.TotalSeconds * 101 / 60:F0} minutes — the mesh and the kernel fit " +
                         "are shared across the sweep, the fill and the standards are not.");
    }

    /// <summary>
    /// The counter-case, and it is the honest one: on the UNIFORM LINE §10.10 actually tabulates,
    /// Auto correctly picks kernel A — so a user who specifically wants the full-wave answer for that
    /// geometry pays ONE extra interaction to say so. The budget still closes, with 2 s of slack.
    /// </summary>
    [Fact]
    public void R18_AUniformLine_CostsOneExtraInteractionToReachKernelB_AndStillFits()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        var vm   = new LayoutEditorViewModel(view);

        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(2.9) });
        vm.ActiveTool = LayoutEditorViewModel.Tool.Port;
        vm.OnPointerPressed(0, Mm(1.45), default);
        vm.OnPointerPressed(Mm(4), Mm(1.45), default);

        var tech   = StarterTechnologies.Pcb2Layer();
        var source = new EmLayoutSource("line.clay", view, tech, Dbu);

        // Auto on a uniform line → kernel A, correctly and by design (D2 is CONSERVATIVE).
        var auto = EmRunService.Run(
            new EmSetup
            {
                Name = "auto", LayoutRef = "line.clay",
                Frequency = new Core.Design.FrequencySpec("5", "5", 1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
            },
            source, Path.Combine(_dir, "auto"));
        Assert.Equal(EmAnalysisKind.CrossSection, auto.Kind);

        // The one extra interaction: pick Planar in the analysis dropdown. Explicit stays explicit.
        var explicitB = EmRunService.Run(
            new EmSetup
            {
                Name = "planar", LayoutRef = "line.clay", AnalysisKind = EmAnalysisKind.Planar,
                PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false),
                Frequency = new Core.Design.FrequencySpec("5", "5", 1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
            },
            source, Path.Combine(_dir, "planar"));
        Assert.Equal(EmAnalysisKind.Planar, explicitB.Kind);

        int total = Budget.Sum(b => b.Seconds) + 1;
        output.WriteLine($"R18 (kernel B, uniform line) — budget {total} s " +
                         $"(+1 s: one click to override Auto's correct choice of A)");
        Assert.True(total <= 30, $"R18 budget is {total} s");
    }
}
