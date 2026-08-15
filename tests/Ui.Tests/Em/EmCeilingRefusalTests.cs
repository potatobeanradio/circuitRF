// The owner report of 2026-08-14, as a gate.
//
// A user demoing the MoM engine pointed an EM setup at ONE PCell — a Klopfenstein taper, Z1 = 6.92 Ω
// into Z2 = 100 Ω on 20 mil RO4350B — and got:
//
//   "The EM solve failed: This geometry needs 7,749 unknowns at 10 cells per wavelength with a
//    3-cell edge mesh, which is past the 5,000-unknown ceiling this kernel is built for (916 MB of
//    dense complex matrix, against 381 MB at the ceiling). Lower Cells per wavelength, turn the edge
//    mesh off, or analyse a smaller region — …"
//
// Three separate defects, and this file gates all three:
//
//  1. THE ADVICE WAS WRONG FOR THE GEOMETRY. The taper is 13.1 mm of metal at one end and 299 µm at
//     the other; MinCellsAcrossConductor sets the pitch from the NARROW end (74.6 µm) and the λ_g cap
//     sits 42× coarser, so it never binds. Measured on the reported file: 7,749 unknowns at 5, 10 AND
//     20 cells/λ alike, and at mesh frequencies of 500 MHz and 5 GHz alike. The user halved the one
//     knob the message named, saw the identical number, and stopped.
//  2. IT READ AS A MEMORY LIMIT. Leading with megabytes produced "did his laptop need more RAM?" —
//     UnknownCeiling is a compile-time constant and the same file refuses identically everywhere.
//  3. THE DIAGNOSIS WAS BUILT AND THROWN AWAY. The mesh report's notes — the narrowest conductor and
//     how many cells it got, whether the edge mesh acted on anything, R-msh-8a's note about what
//     full-wave is adding on this part — were assembled and then dropped, because the refusal left
//     PlanarKernel.Solve as an exception and EmRunService reported ex.Message as an EngineError.
//
// The fixture here is the reported part's SHAPE (a very low-Z Klopfenstein taper), not its exact
// stackup: what reproduces the failure is the width RATIO, which is what a taper into a low Z1 has on
// any substrate.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Tests.Em;

public class EmCeilingRefusalTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static IReadOnlyList<LayoutShape> Klopf(Technology tech, double z1, double lengthM)
    {
        Assert.True(PCellRegistry.TryGet("MKLOPF", out var gen));
        var map = new Dictionary<string, PCellValue>(StringComparer.Ordinal)
        {
            ["Z1"] = PCellValue.Real(z1),
            ["Z2"] = PCellValue.Real(100),
            ["GammaMax"] = PCellValue.Real(0.05),
            ["L"] = PCellValue.Real(lengthM),
            ["Offset"] = PCellValue.Real(0),
            ["SmoothSteps"] = PCellValue.Real(1),
        };
        return gen(map, tech, PCellLayerSelection.Default).Shapes;
    }

    private static PlanarMeshReport MeshTaper(PlanarMeshSettings s, out int n)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var x = PlanarExtractor.Extract(Klopf(tech, 7.0, 0.030), tech, Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);
        var r = SurfaceMesher.Mesh(x.Problem!, s);
        n = r.UnknownCount;
        return r;
    }

    private static PlanarMeshSettings Planar(int cellsPerLambda, bool edge = true, double? fMesh = null)
        => new(Auto: false, CellsPerWavelength: cellsPerLambda, EdgeMesh: edge,
               EdgeCells: edge ? 3 : 0, BoundaryCells: PlanarBoundaryCells.Staircase,
               MeshFrequencyHz: fMesh);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Defect 1 — the knob the message named is INERT on this class of geometry, and it now says so
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OnAWideToNarrowTaper_CellsPerWavelength_DoesNotMoveTheUnknownCount()
    {
        // This is the measurement, kept as a test rather than as a claim in a comment. If a future
        // change makes cells/λ bind on this geometry, the refusal's conditional wording below is
        // wrong and this fails first.
        MeshTaper(Planar(5), out int at5);
        MeshTaper(Planar(10), out int at10);
        MeshTaper(Planar(20), out int at20);
        MeshTaper(Planar(10, fMesh: 500e6), out int atLowMeshFreq);

        Assert.Equal(at5, at10);
        Assert.Equal(at5, at20);
        Assert.Equal(at5, atLowMeshFreq);
        Assert.True(at5 > SurfaceMesher.UnknownCeiling,
            $"the fixture must actually refuse to be gating anything; it produced {at5} unknowns");
    }

    [Fact]
    public void TheRefusal_NamesTheNarrowestMetalAsTheCause_AndSaysCellsPerWavelengthWillNotHelp()
    {
        var r = MeshTaper(Planar(10), out _);

        Assert.Equal(PlanarBudgetVerdict.Refused, r.Verdict);
        string refusal = Assert.IsType<string>(r.Refusal);

        // The cause, in the user's own vocabulary.
        Assert.Contains("NARROWEST metal", refusal, StringComparison.Ordinal);
        Assert.Contains("tensor product", refusal, StringComparison.Ordinal);

        // …and the correction, stated outright rather than left to be discovered by experiment.
        Assert.Contains("WILL NOT REDUCE THIS COUNT", refusal, StringComparison.Ordinal);

        // A remedy list that names an inert knob is the defect. On this geometry neither of the two
        // frequency-side knobs may be offered as an action.
        string acts = refusal[refusal.IndexOf("What acts on the count here:", StringComparison.Ordinal)..];
        Assert.DoesNotContain("lower Cells per wavelength", acts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mesh frequency", acts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("edge mesh", acts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenWavelengthISTheBindingQuantity_TheRefusalOffersCellsPerWavelength()
    {
        // The other half of the conditional, so the fix is not just "never mention cells/λ". A broad
        // plate at a high frequency is the case where the λ_g cap genuinely IS what sets the pitch.
        var tech = StarterTechnologies.Pcb2Layer();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        {
            Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 40_000_000, Y2 = 40_000_000,
        });

        var x = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 60e9);
        Assert.True(x.Ok, x.Refusal);
        var r = SurfaceMesher.Mesh(x.Problem!, Planar(20));

        Assert.Equal(PlanarBudgetVerdict.Refused, r.Verdict);
        Assert.Contains("set by wavelength", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("lower Cells per wavelength", r.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("WILL NOT REDUCE THIS COUNT", r.Refusal!, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Defect 2 — it must not read as a memory limit
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheRefusal_SaysTheCeilingIsTheKERNELS_NotTheMACHINES()
    {
        var r = MeshTaper(Planar(10), out int n);
        string refusal = r.Refusal!;

        // The megabytes stay — they are real and a user sizing a problem wants them — but they are no
        // longer the lead, and the sentence that carries them says what they are NOT.
        Assert.Contains("not of your machine", refusal, StringComparison.Ordinal);
        Assert.Contains("refuses identically everywhere", refusal, StringComparison.Ordinal);
        Assert.True(refusal.IndexOf("unknowns, past the", StringComparison.Ordinal)
                  < refusal.IndexOf("MB of dense complex matrix", StringComparison.Ordinal),
            "the unknown count must precede the megabytes; leading with MB is what read as a RAM limit");

        // brief-em-aim-ceiling.md, 2026-08-14: this fixture's N sits UNDER the accelerated ceiling
        // (12,000), so — unlike the dense-only claim this test used to pin — the accelerated solve is
        // now a REAL way past this refusal, and it must be named as the first remedy rather than
        // dismissed. "does not move this ceiling" would be false here: it moved, and this mesh fits
        // under the moved one.
        Assert.True(n < SurfaceMesher.AcceleratedUnknownCeiling,
            $"this test's whole point needs the accelerated solve to actually help; got N = {n}");
        string acts = refusal[refusal.IndexOf("What acts on the count here:", StringComparison.Ordinal)..];
        Assert.StartsWith("What acts on the count here: turn on the accelerated solve",
            acts, StringComparison.Ordinal);
        Assert.Contains(SurfaceMesher.AcceleratedUnknownCeiling.ToString("N0"), refusal,
            StringComparison.Ordinal);
        Assert.DoesNotContain("does not move this ceiling", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheAcceleratedSolveWouldNotHelpEither_ItIsNotOfferedAsARemedy()
    {
        // A taper big enough to sit PAST the accelerated ceiling too (Z1 = 3.5 Ω, 60 mm -> N ≈ 13,027)
        // — naming a remedy that would still refuse is exactly the defect the owner report caught the
        // FIRST time (a knob that moves the count by zero), just for the new knob instead of the old.
        var tech = StarterTechnologies.Pcb2Layer();
        var x = PlanarExtractor.Extract(Klopf(tech, 3.5, 0.060), tech, Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);
        var r = SurfaceMesher.Mesh(x.Problem!, Planar(10), accelerated: true);

        Assert.Equal(PlanarBudgetVerdict.Refused, r.Verdict);
        Assert.True(r.UnknownCount > SurfaceMesher.AcceleratedUnknownCeiling,
            $"this test's whole point needs N past the accelerated ceiling too; got {r.UnknownCount}");
        string refusal = r.Refusal!;

        Assert.Contains($"{SurfaceMesher.AcceleratedUnknownCeiling:N0}-unknown", refusal,
            StringComparison.Ordinal);
        Assert.DoesNotContain("turn on the accelerated solve", refusal, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Defect 3 — the diagnosis reaches the user
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheRefusalCarriesItsReport_SoTheNotesSurviveTheThrow()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var shapes = new List<LayoutShape>(Klopf(tech, 7.0, 0.030));
        var x = PlanarExtractor.Extract(shapes, tech, Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);

        // Ports at the two ends, so Solve() gets far enough to mesh.
        var (x0, y0, x1, y1) = x.Problem!.Bounds();
        var ports = new[]
        {
            new PlanarPort(1, new EmPoint(x0, (y0 + y1) / 2), PlanarPortSide.MinX,
                           new System.Numerics.Complex(50, 0)),
            new PlanarPort(2, new EmPoint(x1, (y0 + y1) / 2), PlanarPortSide.MaxX,
                           new System.Numerics.Complex(50, 0)),
        };

        var ex = Assert.Throws<PlanarMeshRefusedException>(
            () => new PlanarKernel().Solve(x.Problem!, Planar(10), ports, [5e9]));

        // The message is the refusal…
        Assert.Equal(ex.Report.Refusal, ex.Message);
        // …and the report rides along, which is the whole point: these are the sentences that say
        // WHY the count is what it is, and before this they were built and dropped.
        Assert.Contains(ex.Report.Notes, n => n.Contains("Narrowest conductor dimension", StringComparison.Ordinal));
        Assert.True(ex.Report.UnknownCount > SurfaceMesher.UnknownCeiling);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-msh-8a — the note that had never once fired
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EmGeometry_CollectsThePCellGeneratorIds_FromTheSnapshotTable()
    {
        // PlanarExtractor.Extract has taken an optional generatorIds since L8b and NO CALLER IN src/
        // EVER PASSED ONE, so AnalyticAlternativeFor and its three mappings were unreachable by any
        // user. This is the collection half; the note half is below.
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = @"..\..\.generated-cells\MKLOPF_770fa9b3d56e",
        });
        view.PCellSnapshots["MKLOPF_770fa9b3d56e"] =
            new PCellSnapshot("MKLOPF", new Dictionary<string, PCellValue>(), null, null, null);

        var flat = EmGeometry.Flatten(view, Path.Combine(Path.GetTempPath(), "nothing", "a.clay"));

        // The instance does not resolve (there is no such cell on disk) and that is deliberate: the
        // id must come from the snapshot table keyed by the CellRef's last segment, never from
        // loading the cell or from parsing its folder name.
        Assert.Equal(["MKLOPF"], flat.GeneratorIds);
    }

    [Theory]
    [InlineData(@"..\..\.generated-cells\MKLOPF_770fa9b3d56e")]   // Windows-authored .clay
    [InlineData("../../.generated-cells/MKLOPF_770fa9b3d56e")]    // Unix-authored .clay
    [InlineData("MKLOPF_770fa9b3d56e")]                           // same directory
    public void TheCellRefSegment_IsFoundUnderEITHERSeparator_OnEitherPlatform(string cellRef)
    {
        // Path.GetFileName was the first implementation and it is silently wrong on Unix, where a
        // backslash is an ordinary filename character — so a Windows-authored workspace opened on
        // macOS found no generator id and the note vanished again. Which is exactly how the report
        // that started this arrived.
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Instances.Add(new LayoutInstance { CellRef = cellRef });
        view.PCellSnapshots["MKLOPF_770fa9b3d56e"] =
            new PCellSnapshot("MKLOPF", new Dictionary<string, PCellValue>(), null, null, null);

        var flat = EmGeometry.Flatten(view, Path.Combine(Path.GetTempPath(), "nothing", "a.clay"));
        Assert.Equal(["MKLOPF"], flat.GeneratorIds);
    }

    [Fact]
    public void APanelPointedAtAKlopfTaper_SaysWhatFullWaveIsADDING()
    {
        // End to end through the view model, because the defect was a missing ARGUMENT at a call
        // site: every piece of this worked in isolation and the chain was broken in one place.
        string dir = Path.Combine(Path.GetTempPath(), "crf-alt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var tech = StarterTechnologies.Pcb2Layer();
            var view = new LayoutView { DbuPerMicron = Dbu };
            foreach (var s in Klopf(tech, 50, 0.010)) view.Shapes.Add(s);
            // The panel can be pointed straight at a generated cell, which is what this marks.
            view.PCellOrigin = new PCellOrigin("MKLOPF", new Dictionary<string, PCellValue>());

            string path = Path.Combine(dir, "panel.cem");
            var setup = new EmSetup
            {
                Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            };
            EmSetupPersistence.SaveToFile(path, setup);
            var vm = new EmSetupEditorViewModel(path, setup)
            {
                ResolveLayout = _ => new EmLayoutSource(Path.Combine(dir, "a.clay"), view, tech, Dbu),
            };
            vm.Refresh();
            vm.BuildPlanarMesh();

            var note = Assert.Single(vm.PlanarMeshNotes, n => n.Contains("MKLOPF", StringComparison.Ordinal));

            // The owner's instruction of 2026-08-14: a user who has deliberately opened an EM setup
            // on this part must NOT be told the closed-form model exists as though that settled it.
            // What the note owes them is what the expensive run buys.
            Assert.Contains("What full-wave adds", note, StringComparison.Ordinal);
            Assert.DoesNotContain("effectively free", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("already has a validated", note, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // brief-em-aim-ceiling.md, gate 5 — the owner's OWN reported numbers, re-run
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §0's own table, reproduced at the owner's own numbers (Z1 = 6.92 Ω, Z2 = 100 Ω, L = 28.575 mm,
    /// conformal, cells/λ = 5, edge mesh on, mesh frequency 500 MHz — "the user's own .cem", which
    /// measured N = 7,749 there; this reconstruction from the PCell's own defaults lands at N = 6,581,
    /// close enough to be the same class of geometry and past the dense ceiling either way — the exact
    /// count was never the point, the RATIO was). Answers "what does this user do now": turn the
    /// accelerated solve on, and the run that used to throw completes.
    /// </summary>
    [Fact]
    public void TheOwnersReportedTaper_NowFitsUnderTheAcceleratedCeiling()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var x = PlanarExtractor.Extract(Klopf(tech, 6.92, 0.028575), tech, Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);

        var s = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 5, EdgeMesh: true,
                                       EdgeCells: 3, BoundaryCells: PlanarBoundaryCells.Conformal,
                                       MeshFrequencyHz: 500e6);

        var dense = SurfaceMesher.Mesh(x.Problem!, s);
        Assert.Equal(PlanarBudgetVerdict.Refused, dense.Verdict);
        Assert.True(dense.UnknownCount > SurfaceMesher.UnknownCeiling);

        var accelerated = SurfaceMesher.Mesh(x.Problem!, s, accelerated: true);
        Assert.True(accelerated.CanSolve,
            $"N = {accelerated.UnknownCount} should fit under the {SurfaceMesher.AcceleratedUnknownCeiling:N0} " +
            "accelerated ceiling; if this fails the ceiling constant or this test's fixture drifted");
        Assert.Equal(dense.UnknownCount, accelerated.UnknownCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // brief-em-deembed-ceiling-closeout.md, gate 1 — the honest refusal, at setup, in seconds
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The owner's OWN taper, de-embedding ON, accelerated ON, through the actual entry point
    /// (<see cref="PlanarSolve.Run"/>). Before this brief this call sequence appeared to succeed
    /// (mesh, DUT accelerated Z-matrix) and only threw twenty real minutes later, from inside
    /// <c>PlanarDeembed.CapacitancePerMetre</c>, once a calibration standard's own always-dense
    /// static-capacitance solve finally ran. R17's own contract has never been "there is a ceiling"
    /// — it is <i>surface the predicted N before solving, and refuse politely above it</i>. This
    /// gates that it now refuses AT SETUP, in seconds, naming the standards' own N and a remedy the
    /// user can act on, rather than succeeding past the point it can and failing later.
    /// </summary>
    [Fact]
    public void Gate1_TheOwnersReportedTaper_DeembedOn_AcceleratedOn_RefusesAtSetup_NotAfterADenseFill()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var x = PlanarExtractor.Extract(Klopf(tech, 6.92, 0.028575), tech, Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);

        var s = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 5, EdgeMesh: true,
                                       EdgeCells: 3, BoundaryCells: PlanarBoundaryCells.Conformal,
                                       MeshFrequencyHz: 500e6);

        var report = SurfaceMesher.Mesh(x.Problem!, s, accelerated: true);
        Assert.True(report.CanSolve,
            $"the DUT's own accelerated mesh must fit for this fixture to be gating the right thing; " +
            $"N = {report.UnknownCount}");

        var (x0, y0, x1, y1) = x.Problem!.Bounds();
        double yc = 0.5 * (y0 + y1);
        var ports = new[]
        {
            new PlanarPort(1, new EmPoint(x0, yc), PlanarPortSide.MinX, new System.Numerics.Complex(50, 0)),
            new PlanarPort(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, new System.Numerics.Complex(50, 0)),
        };
        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);

        var settings = PlanarSolveSettings.Default with
        {
            Deembed = true,
            Fill = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default },
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanarSolve.Run(x.Problem!, report.Mesh, resolved, [5e9], settings));
        sw.Stop();

        // "In seconds", not twenty real minutes — the whole point of C1.
        Assert.True(sw.Elapsed.TotalSeconds < 30,
            $"the refusal must fire at SETUP, before any dense fill; took {sw.Elapsed.TotalSeconds:F1} s");

        // R-dcl-2 — names WHY the accelerator does not help here, rather than reading as a
        // contradiction of the panel telling this same user to turn it on thirty seconds earlier.
        Assert.Contains("static capacitance", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will NOT help", ex.Message, StringComparison.Ordinal);

        // R-dcl-3 — a remedy a user can act on, and what turning de-embedding off costs (§10.6).
        Assert.Contains("Turn de-embedding off", ex.Message, StringComparison.Ordinal);
        Assert.Contains("port discontinuity", ex.Message, StringComparison.Ordinal);

        // §0 of the parent brief's own finding: mesh remedies are inert on this class of geometry,
        // so they must not be offered here either.
        Assert.DoesNotContain("Lower Cells per wavelength", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("edge mesh off", ex.Message, StringComparison.OrdinalIgnoreCase);

        // R-dcl-4 — both the DUT's own N and the standard's own N are named.
        Assert.Contains($"N = {report.Mesh.Bases.Count:N0}", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SurfaceMesher.UnknownCeiling.ToString("N0"), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same taper's OWN Z-matrix, actually SOLVED with the accelerator — not merely meshed.
    ///
    /// <para><b>Deliberately the DUT's raw solve, not a full de-embedded <c>PlanarKernel.Solve</c>.</b>
    /// A first attempt drove the whole panel path (mesh → calibrate → de-embed) and found a SEPARATE,
    /// genuine limit this brief does not fix: <c>PlanarDeembed.StaticCapacitance</c> computes a
    /// calibration standard's DC capacitance through <see cref="PlanarFill.BuildCores"/> — an entirely
    /// different, always-DENSE m×m cell system, not the accelerated N×N basis system — and a WIDE-port
    /// standard reproduces the DUT's own wide transverse gridlines (D4), so it can be large enough to
    /// hit <see cref="SurfaceMesher.UnknownCeiling"/> on its own. On this exact taper the short/long
    /// standard meshed at N = 6,466 and the de-embedded run used to throw from inside
    /// <c>PlanarDeembed.CapacitancePerMetre</c>, twenty real minutes into a dense fill+LU nobody asked
    /// for — closed by <c>brief-em-deembed-ceiling-closeout.md</c>'s C1, which now refuses that same
    /// run AT SETUP instead (see <see cref="Gate1_TheOwnersReportedTaper_DeembedOn_AcceleratedOn_RefusesAtSetup_NotAfterADenseFill"/>).</para>
    ///
    /// <para><b>Raw is not this user's success case, and this test's own gate must not be read as
    /// though it were.</b> §10.6 of <c>docs/design/layout-view.md</c>: "A raw port excitation includes
    /// the port discontinuity; reporting those s-parameters as the structure's response is simply
    /// wrong." This test exists to gate the DUT's own accelerated N×N solve mechanics (GMRES actually
    /// converging past the dense ceiling), not to claim a raw solve is a published answer — the
    /// published, de-embedded answer is what gate 1 covers, and on THIS exact taper it correctly
    /// refuses rather than silently reporting a raw S as the structure's response.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheOwnersReportedTaper_TheDutsOwnZMatrix_ACTUALLYSOLVES_WithTheAcceleratedSolveOn()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var x = PlanarExtractor.Extract(Klopf(tech, 6.92, 0.028575), tech, Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);

        var s = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 5, EdgeMesh: true,
                                       EdgeCells: 3, BoundaryCells: PlanarBoundaryCells.Conformal,
                                       MeshFrequencyHz: 500e6);

        var dense = SurfaceMesher.Mesh(x.Problem!, s);
        Assert.Equal(PlanarBudgetVerdict.Refused, dense.Verdict);

        var report = SurfaceMesher.Mesh(x.Problem!, s, accelerated: true);
        Assert.True(report.CanSolve);
        int n = report.UnknownCount;
        Assert.True(n > SurfaceMesher.UnknownCeiling && n <= SurfaceMesher.AcceleratedUnknownCeiling,
            $"expected an N between the two ceilings; got {n}");

        var (x0, y0, x1, y1) = x.Problem!.Bounds();
        double yc = 0.5 * (y0 + y1);
        var ports = new[]
        {
            new PlanarPort(1, new EmPoint(x0, yc), PlanarPortSide.MinX, new System.Numerics.Complex(50, 0)),
            new PlanarPort(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, new System.Numerics.Complex(50, 0)),
        };
        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);

        var ctx = new PlanarSolveContext(report.Mesh, resolved,
            PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default });
        var kernel = PlanarKernelPair.Fit(x.Problem!.Slab, 5e9);
        var raw = ctx.RawScatteringAt(kernel, 5e9);

        Assert.Equal(2, raw.RowCount);
        Assert.Equal(2, raw.ColCount);
        Assert.NotNull(ctx.LastAccelerator);
        Assert.True(ctx.LastAccelerator!.LastIterations > 0);
    }
}
