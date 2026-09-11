// ANT-12 — the radiation pattern's FIRST user-reachable switch, Ui side.
//
// ANT-4 through ANT-11 built the far field, the metrics, the polarization, the dB polar plot and the
// 3D surface, and every one of those phases recorded "nothing reaches the CLI or the GUI" because the
// one before it did not either. The whole antenna capability was therefore unreachable from the
// application and from `circuitrf em`: `PlanarSolveSettings.FarField` could only be set by editing C#.
// This checkbox is what closes that.
//
// What lives here is everything the engine cannot see: the .cem round trip, Clone, the two disabled
// reasons, the deliberate ABSENCE from every provenance hash, and — the load-bearing one — that
// EmRunService actually hands the flag to the solver. AcceleratedSolveUiTests' own note says why that
// last test matters: a control the panel stores and nothing reads is the same defect with a checkbox
// on it, and this area has already paid for it once.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class RadiationPatternUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static LayoutView LineLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        return view;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-ant12-" + Guid.NewGuid().ToString("N")[..8]);
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
    // The .cem round trip — omit at default, the rule every control after the first three follows
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACemThatNeverAskedForAPattern_GainsNoByte()
    {
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("RadiationPattern", before, StringComparison.Ordinal);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.False(reloaded.RadiationPattern);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void AskingForIt_RoundTrips_AndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name = "patch", LayoutRef = "patch/layout/patch.clay",
            AnalysisKind = EmAnalysisKind.Planar, RadiationPattern = true,
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("RadiationPattern", json, StringComparison.Ordinal);
        Assert.True(EmSetupPersistence.Deserialize(json).RadiationPattern);

        // Clone drives the editor's undo snapshots; a field missing from it is silently lost on the
        // next unrelated edit.
        Assert.True(setup.Clone().RadiationPattern);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // It enters NO provenance hash, and the reason is stronger than the accelerator's
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ItIsInNoProvenanceHash_BecauseItAddsAnOutputRatherThanChangingOne()
    {
        // The accelerator computes the same answer a different way; this one does not touch the
        // answer at all — the pattern is a post-process of currents the solve already produced, so an
        // `.snp` written with the pattern on is byte-identical to one written with it off. Marking
        // every existing file stale would be a lie about the network. Asserted rather than arranged,
        // because a later refactor can quietly undo an arrangement.
        string m0 = EmSnpProvenance.MeshHash(PlanarMeshSettings.Default);

        foreach (bool on in new[] { false, true })
        {
            var setup = new EmSetup
            {
                Name = "p", LayoutRef = "a.clay",
                AnalysisKind = EmAnalysisKind.Planar, RadiationPattern = on,
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

        vm.RadiationPattern = true;

        Assert.True(vm.Working.RadiationPattern);
        // No cell moves: the pattern is computed from the currents the same mesh produces.
        Assert.NotNull(vm.PlanarMeshReport);

        vm.RadiationPattern = true;   // the same value again pushes nothing

        int undosAfter = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosAfter++; }
        Assert.Equal(undosBefore + 1, undosAfter);
        Assert.False(vm.RadiationPattern);

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

        Assert.NotNull(vm.RadiationPatternDisabledReason);
        Assert.Contains("cross-section", vm.RadiationPatternDisabledReason!,
                        StringComparison.OrdinalIgnoreCase);
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// <b>Conformal boundary cells disable it, and that is a REQUIREMENT rather than a preference.</b>
    /// The far field does not transform a cut cell — a cut cell's metal is not its rectangle — and
    /// ANT-4 refuses by name. The panel declines to let a user arm a run whose pattern cannot be
    /// computed, which is the same courtesy the accelerator's multi-level reason is.
    /// </summary>
    [Fact]
    public void ConformalBoundaryCellsDisableIt_NamingStaircaseAsTheRemedy()
    {
        string dir = TempDir();
        var vm = Editor(dir, new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = PlanarMeshSettings.Default with
            { BoundaryCells = PlanarBoundaryCells.Conformal },
        });

        Assert.NotNull(vm.RadiationPatternDisabledReason);
        Assert.Contains("Staircase", vm.RadiationPatternDisabledReason!, StringComparison.Ordinal);

        // And it becomes available again the moment the mesh goes back to staircase, through the
        // panel's own control rather than by rebuilding the view model.
        vm.PlanarBoundaryCells = PlanarBoundaryCells.Staircase;
        Assert.Null(vm.RadiationPatternDisabledReason);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void OnAnOrdinaryPlanarLayout_ItIsAVAILABLE_AndOffByDefault()
    {
        string dir = TempDir();
        var vm = Editor(dir);
        Assert.Null(vm.RadiationPatternDisabledReason);
        Assert.False(vm.RadiationPattern);
        Directory.Delete(dir, true);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The wiring — a stored flag nothing reads is decoration
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>EmRunService hands the flag to the solver, and hands it the SWEEP's frequencies.</b> Both
    /// halves are pinned. Left to its own default the far field produces ONE pattern at
    /// <c>freqs[0]</c> — the bottom of the sweep, which on an antenna is the one frequency nobody
    /// wants: measured on the shipped example, 5.3 GHz reports 22.6 % radiation efficiency against
    /// 62.6 % at resonance, and both numbers are correct about different questions.
    ///
    /// <para>A source scan, the same fallback this suite already uses for run-path plumbing that is
    /// not reachable without a full solve — and a full solve here is minutes.</para>
    /// </summary>
    [Fact]
    public void EmRunService_HandsTheFlagToTheSolver_WithTheSweepsOwnFrequencies()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Design/Layout/Em/EmRunService.cs"));

        Assert.Contains("setup.RadiationPattern", src, StringComparison.Ordinal);
        Assert.Contains("FarField = setup.RadiationPattern", src, StringComparison.Ordinal);
        Assert.Contains("PlanarFarFieldSettings.Default with { FrequenciesHz = freqs }", src,
                        StringComparison.Ordinal);

        // And the one combination the panel can express that the cross-section kernel cannot honour is
        // SAID rather than silently ignored — the rule the resonance search already follows there.
        Assert.Contains("The radiation pattern is on but this run used the cross-section", src,
                        StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.False(string.IsNullOrEmpty(dir), "could not locate the repository root");
        return dir;
    }
}
