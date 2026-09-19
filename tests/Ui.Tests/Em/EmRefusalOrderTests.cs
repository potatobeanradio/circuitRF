// Three EM refusals that a user report (2026-09-18) showed were being SAID WRONG — not missing, and
// not incorrect physics. An experienced designer imported a two-layer board, pointed an EM setup at
// it, and was handed a dense paragraph about via basis functions. What was actually wrong with the
// run was that he had not placed a port yet.
//
// Each test below pins the sentence a user in that position must read. The physics under all three
// is untouched.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmRefusalOrderTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);
    private static readonly LayerKey Drill     = new(7, 0);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-refusal-order-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    /// <summary>
    /// A line with a ground via on it, so <c>PlanarLevels.CanRepresentVias</c> has something to
    /// refuse: PCB 2-Layer's FR-4 is 1.6 mm at εᵣ = 4.4, which puts k·ℓ past the kernel's 0.30 floor
    /// anywhere above ~4.3 GHz. Ports are added by the caller, or not.
    /// </summary>
    private static LayoutView LineWithAGroundVia()
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(4.0), Y2 = Mm(2.9) });
        v.Shapes.Add(new ViaShape
        {
            Layer = Drill, X = Mm(2.0), Y = Mm(1.45),
            DrillSize = Mm(0.3), PadSize = Mm(0.6),
        });
        return v;
    }

    private static LabelShape Port(int n, long x, long y) => new()
    {
        Layer = TopCopper, X = x, Y = y, Text = "P" + n, Height = Mm(0.4), IsPort = true,
    };

    private static EmSetup At(double fGHz) => PlanarRunTests.NewSetup(fGHz);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1 — the reported defect: no ports at all, and the kernel's verdict spoke over it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>With NO PORTS and a via the kernel also refuses, the run says the ports.</b> The user's
    /// own omission comes before a physics limit on solving a structure there is no reason to solve
    /// yet — and only one of the two refusals is ever shown.
    /// </summary>
    [Fact]
    public void NoPortLabels_IsSaidEvenWhenTheKernelWouldAlsoRefuseTheVia()
    {
        var run = EmRunService.Run(At(10.0), PlanarRunTests.Source(LineWithAGroundVia()),
                                   Path.Combine(_dir, "noports"));

        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("no port labels", run.Error!, StringComparison.OrdinalIgnoreCase);

        // And the via paragraph — correct, and not what this user needs — is not what was said.
        Assert.DoesNotContain("z-rooftop", run.Error!, StringComparison.Ordinal);
    }

    /// <summary>The other side of the same order: once the ports ARE there, the kernel's verdict is
    /// what a too-long via gets, unchanged.</summary>
    [Fact]
    public void WithPortsPlaced_TheViaVerdictIsStillTheRefusal_AndSpellsItsLengthInUnits()
    {
        var view = LineWithAGroundVia();
        view.Shapes.Add(Port(1, 0,        Mm(1.45)));
        view.Shapes.Add(Port(2, Mm(4.0),  Mm(1.45)));

        var run = EmRunService.Run(At(10.0), PlanarRunTests.Source(view),
                                   Path.Combine(_dir, "via"));

        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("z-rooftop", run.Error!, StringComparison.Ordinal);

        // The LENGTH is the one number in that paragraph a reader has to act on. It used to print
        // as a raw "{ell:G4} m" — 0.001465 m for a 1.4651 mm via — which a designer reported as a
        // units bug. It now carries an SI prefix (the layout's own display unit through the UI, SI
        // engineering notation headless), so a bare " m" after the digits is the regression.
        Assert.DoesNotContain("0.0016 m", run.Error!, StringComparison.Ordinal);
        Assert.Matches(@"is [\d.]+ ?(mm|µm|mil) long", run.Error!);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2 — two labels on one terminal: a complete, plausible, non-passive answer, silently
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Two port labels that resolve to the same cut are refused rather than solved.</b>
    ///
    /// <para>The imported board that prompted this carried P5 and P6 at bit-identical coordinates.
    /// Reproduced on the shipped Klopfenstein taper by copying its port 2 onto itself as port 3, a
    /// clean passive two-port became a three-port with 40 of 47 rows NOT PASSIVE whose own caveat
    /// says the cause "is not identified here" — so the wrong answer arrived complete, plausible and
    /// unattributable. Here it is the same defect on the cheapest geometry that shows it.</para>
    /// </summary>
    [Fact]
    public void TwoPortLabelsOnOneTerminal_AreRefused_NotSolvedIntoAThreePort()
    {
        var view = PlanarRunTests.NewLayout();
        view.Shapes.Add(Port(3, Mm(4.0), Mm(1.45)));   // exactly where P2 already is

        var run = EmRunService.Run(At(5.0), PlanarRunTests.Source(view),
                                   Path.Combine(_dir, "dup"));

        // Refused, not EngineError: it is the user's geometry, and it names both ports.
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Contains("SAME CUT", run.Error!, StringComparison.Ordinal);
        Assert.Contains("Ports 2 and 3", run.Error!, StringComparison.Ordinal);
        Assert.Null(run.SnpPath);
    }

    /// <summary>The guard is INDISTINGUISHABILITY, not overlap — the two ends of an ordinary line
    /// still run. (The coarse-mesh fixtures in <c>ModalErrorBoxTests</c> are the hard version of
    /// this: there the two ends of a very short line share their one interior rooftop and are told
    /// apart only by side and direction.)</summary>
    [Fact]
    public void TwoPortsAtOppositeEndsOfOneLine_StillRun()
    {
        var run = EmRunService.Run(At(5.0), PlanarRunTests.Source(PlanarRunTests.NewLayout()),
                                   Path.Combine(_dir, "ok"));

        Assert.Equal(EmRunStatus.Ok, run.Status);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 3 — a mesh that was REFUSED reported its counters instead of its reason
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A refused mesh report says the refusal, not "0 unknown(s) over 0 cell(s)".</b>
    /// <c>SurfaceMesher</c>'s pre-build guard returns a report whose counters are ZERO and whose
    /// reason is in <c>Refusal</c>; the Messages row read the counters, so the user saw a line that
    /// looked like a mesh which had run and found nothing. The reported board hit exactly this.
    /// </summary>
    [Fact]
    public void ARefusedMeshReport_ReportsItsReason_RatherThanZeroCounters()
    {
        var vm = new EmSetupEditorViewModel(Path.Combine(_dir, "s.cem"), PlanarRunTests.NewSetup());

        // The mesher's own pre-build refusal, reached the way a run reaches it: ask for a grid this
        // geometry cannot have. The report comes back Refused with both counters at zero.
        var view    = PlanarRunTests.NewLayout();
        var source  = PlanarRunTests.Source(view);
        var planar  = PlanarExtractor.Extract(
            view.Shapes, source.Technology!, Dbu, 5e9,
            PlanarRunTests.NewSetup().ToExtractionSettings("layout.clay"));
        Assert.True(planar.Ok);

        var report = SurfaceMesher.Mesh(
            planar.Problem! with { MaxFrequencyHz = 5e9 },
            new PlanarMeshSettings(Auto: false, CellsPerWavelength: 1_000_000, EdgeMesh: false),
            PlanarEdgeReference.LocalConductorWidth);

        Assert.Equal(PlanarBudgetVerdict.Refused, report.Verdict);
        Assert.NotNull(report.Refusal);
        Assert.Equal(0, report.CellCount);

        vm.AdoptPlanarMeshReport(report);

        Assert.True(vm.MeshWasRefused);
        Assert.Equal(report.Refusal, vm.MeshOutcomeText());
        Assert.DoesNotContain("0 unknown(s) over 0 cell(s)", vm.MeshOutcomeText(), StringComparison.Ordinal);
    }
}
