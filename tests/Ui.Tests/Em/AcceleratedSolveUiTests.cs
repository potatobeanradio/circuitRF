// M5's accelerator, Ui side — its FIRST user-reachable switch (owner request, 2026-08-14).
//
// The engine half (does AIM compute the right answer, what does it cost) is M5's own gates in
// tests/Engine.Tests. What lives here is everything the engine cannot see: the .cem round trip,
// Clone, the disabled reasons, the deliberate ABSENCE from every provenance hash, and the fact that
// EmRunService actually hands the flag to the solver.
//
// The wiring test is the load-bearing one, and it is load-bearing for a reason this area has already
// paid for once: `PlanarExtractor.AnalyticAlternativeFor` was built, tested and mapped in L8b, and no
// caller in src/ ever passed it a generator id — so R-msh-8a's note was live, correct and unreachable
// by any user for months (found 2026-08-14 while diagnosing an owner report). A control the panel
// stores and nothing reads is the same failure with a checkbox on it.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class AcceleratedSolveUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static LayoutView LineLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        {
            Layer = new(1, 0),
            X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000,
        });
        return view;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-aim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static EmSetupEditorViewModel Editor(string dir, EmSetup? seed = null)
    {
        string path  = Path.Combine(dir, "panel.cem");
        var    setup = seed ?? new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), LineLayout(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The .cem round trip — omit at default, exactly like DirectVerticalKernel beside it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACemThatNeverTouchedTheControl_GainsNoByte()
    {
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("AcceleratedSolve", before, StringComparison.Ordinal);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.False(reloaded.AcceleratedSolve);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void SettingIt_RoundTrips_AndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name = "planar", LayoutRef = "Amp/layout/Amp.clay",
            AnalysisKind = EmAnalysisKind.Planar, AcceleratedSolve = true,
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("AcceleratedSolve", json, StringComparison.Ordinal);
        Assert.True(EmSetupPersistence.Deserialize(json).AcceleratedSolve);

        // Clone drives the editor's undo snapshots; a field missing from it is silently lost on the
        // next unrelated edit.
        Assert.True(setup.Clone().AcceleratedSolve);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // It enters NO provenance hash — asserted as a NEGATIVE, the R-emp-7 shape
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ItIsInNoProvenanceHash_BecauseItPicksASolverRatherThanAProblem()
    {
        // Same reasoning the core cap carries (R-emp-7/8): with the accelerator's own accuracy gates
        // passed it changes HOW the answer is computed, not WHAT it is — so an .snp produced with it
        // on is not stale for a run with it off. The arrangement (nothing hashed is aware of it) is
        // exactly what a later refactor can quietly undo, so it is asserted rather than arranged.
        var mesh  = PlanarMeshSettings.Default;
        string m0 = EmSnpProvenance.MeshHash(mesh);

        foreach (bool on in new[] { false, true })
        {
            var setup = new EmSetup
            {
                Name = "p", LayoutRef = "a.clay",
                AnalysisKind = EmAnalysisKind.Planar, AcceleratedSolve = on,
            };
            Assert.Equal(m0, EmSnpProvenance.MeshHash(setup.PlanarMesh));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The panel: one undo entry, no mesh invalidation, and the two disabled reasons
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TogglingIt_CommitsOneUndoEntry_AndDoesNOTInvalidateTheMesh()
    {
        string dir = TempDir();
        var vm = Editor(dir);

        int undosBefore = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosBefore++; }
        while (vm.UndoRedo.CanRedo) vm.UndoRedo.Redo();

        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);

        vm.AcceleratedSolve = true;

        Assert.True(vm.Working.AcceleratedSolve);
        // Unlike every mesh control in this panel: it chooses a SOLVER for a mesh, and the mesh is
        // the same one either way. Invalidating would throw away a report the user is reading for no
        // reason at all.
        Assert.NotNull(vm.PlanarMeshReport);

        // Same value again pushes nothing.
        vm.AcceleratedSolve = true;

        int undosAfter = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosAfter++; }
        Assert.Equal(undosBefore + 1, undosAfter);
        Assert.False(vm.AcceleratedSolve);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void ItIsDisabledOnTheCrossSectionKernel_ByName()
    {
        string dir = TempDir();
        var vm = Editor(dir, new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.CrossSection,
        });

        Assert.NotNull(vm.AcceleratedSolveDisabledReason);
        Assert.Contains("cross-section", vm.AcceleratedSolveDisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnAnOrdinarySingleLevelPlanarLayout_ItIsAVAILABLE()
    {
        string dir = TempDir();
        var vm = Editor(dir);
        Assert.Null(vm.AcceleratedSolveDisabledReason);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The wiring — a stored flag nothing reads is decoration
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EmRunService_HandsTheFlagToTheSolver_AndComposesWithTheVerticalKernel()
    {
        // A source scan, the same fallback this suite already uses for view-model-only plumbing:
        // the run path's own settings construction is not reachable without a full solve, and a full
        // solve is minutes. What this pins is the composition — the two fill terms must be applied to
        // ONE base, because as two independent ternaries turning the accelerator on silently discards
        // DirectVerticalKernel.
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Layout/Em/EmRunService.cs"));

        Assert.Contains("setup.AcceleratedSolve", src, StringComparison.Ordinal);
        Assert.Contains("Aim = PlanarAimSettings.Default", src, StringComparison.Ordinal);

        int vertical = src.IndexOf("if (setup.DirectVerticalKernel) fill = fill with", StringComparison.Ordinal);
        int aim      = src.IndexOf("if (setup.AcceleratedSolve)     fill = fill with", StringComparison.Ordinal);
        Assert.True(vertical > 0 && aim > vertical,
            "both fill terms must accumulate onto one `fill` local, in order — see the comment there");
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
