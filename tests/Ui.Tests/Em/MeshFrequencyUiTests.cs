// M0 — the mesh-frequency parameter, Ui side: gates 2, 3 and 5 of
// docs/sonnet-briefs/brief-em-sweep-performance.md.
//
// The engine half (does it actually size the mesh, does the report say so) is in
// tests/Engine.Tests/Mom/MeshFrequencyTests.cs. What lives here is everything the ENGINE cannot
// see: the .cem round trip, Clone, the staleness hash, and the panel's own staged-text commit.
//
// The staleness gate is the load-bearing one, exactly as it was for boundary cells: an .snp produced
// with the mesh sized at 10 GHz is not current for one sized at 20 GHz, and MeshHash is the only
// thing that can say so. R-emp-4 calls it "one line that is easy to forget" for a reason.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

public class MeshFrequencyUiTests
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
        string d = Path.Combine(Path.GetTempPath(), "crf-meshfreq-" + Guid.NewGuid().ToString("N")[..8]);
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
    // Gate 2 — the .cem round trip
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACemThatNeverTouchedTheControl_GainsNoByte()
    {
        // The omit-at-default rule, and it is an asserted property of this format rather than a
        // nicety: a plain (non-nullable) DTO field would be written unconditionally and would change
        // EVERY .cem already on disk. Same reason BoundaryCells and DirectVerticalKernel are
        // nullable beside it.
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("MeshFrequencyHz", before, StringComparison.Ordinal);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.Null(reloaded.PlanarMesh.MeshFrequencyHz);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void SettingIt_RoundTrips_SurvivesClone_AndSurvivesAutosCollapse()
    {
        var setup = new EmSetup
        {
            Name         = "planar",
            LayoutRef    = "Amp/layout/Amp.clay",
            AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh   = PlanarMeshSettings.Default with { MeshFrequencyHz = 10e9 },
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("MeshFrequencyHz", json, StringComparison.Ordinal);
        Assert.Equal(10e9, EmSetupPersistence.Deserialize(json).PlanarMesh.MeshFrequencyHz);

        // Clone drives the editor's UNDO snapshots. A field missing from it is silently lost on the
        // next unrelated edit — assert it rather than assume it.
        Assert.Equal(10e9, setup.Clone().PlanarMesh.MeshFrequencyHz);

        // …and Resolved's Auto collapse keeps it, unlike cells/λ and edge cells. Auto decides a
        // RESOLUTION; this is not one.
        Assert.Equal(10e9, setup.PlanarMesh.Resolved.MeshFrequencyHz);
        Assert.Equal(10e9, (PlanarMeshSettings.Default with { Auto = true, MeshFrequencyHz = 10e9 })
                           .Resolved.MeshFrequencyHz);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 3 — staleness. THE LOAD-BEARING ONE.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChangingIt_MovesMeshHash_AndEveryOtherTermStillMovesIt()
    {
        var unset = PlanarMeshSettings.Default;
        var at10  = unset with { MeshFrequencyHz = 10e9 };
        var at20  = unset with { MeshFrequencyHz = 20e9 };

        string hUnset = EmSnpProvenance.MeshHash(unset);

        Assert.NotEqual(hUnset, EmSnpProvenance.MeshHash(at10));
        Assert.NotEqual(EmSnpProvenance.MeshHash(at10), EmSnpProvenance.MeshHash(at20));

        // "max sweep" and "pinned to whatever the sweep's top happens to be" are DIFFERENT
        // states — the second survives a later sweep edit, the first does not — so they must hash
        // differently even when they currently produce the same mesh.
        Assert.NotEqual(hUnset, EmSnpProvenance.MeshHash(at20));

        // …and the new term did not displace an existing one.
        Assert.NotEqual(hUnset, EmSnpProvenance.MeshHash(unset with { CellsPerWavelength = 40 }));
        Assert.NotEqual(hUnset, EmSnpProvenance.MeshHash(unset with { EdgeMesh = !unset.EdgeMesh }));
        Assert.NotEqual(hUnset, EmSnpProvenance.MeshHash(unset with { EdgeCells = 7 }));
        Assert.NotEqual(hUnset, EmSnpProvenance.MeshHash(unset with { Auto = !unset.Auto }));
        Assert.NotEqual(hUnset, EmSnpProvenance.MeshHash(
            unset with { BoundaryCells = PlanarBoundaryCells.Conformal }));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-emp-5 — the panel: staged text in the sweep's own unit, one undo entry, invalidates the mesh
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheFieldIsEditedInTheSweepsOwnUnit_AndStoredInHertz()
    {
        string dir = TempDir();
        var vm = Editor(dir);

        // The sweep's own top-frequency unit — never a second unit selector of this field's own.
        Assert.Equal(vm.Frequency.StopUnit, vm.MeshFrequencyUnit);

        vm.PlanarMeshFrequencyText = "10";
        vm.CommitMeshField("MeshFrequency");

        double mult = vm.MeshFrequencyUnit switch
        {
            "kHz" => 1e3, "MHz" => 1e6, "GHz" => 1e9, _ => 1.0,
        };
        Assert.Equal(10 * mult, vm.Working.PlanarMesh.MeshFrequencyHz);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void BlankMeansFollowTheSweep_AndThePlaceholderSaysSo()
    {
        string dir = TempDir();
        var vm = Editor(dir, new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = PlanarMeshSettings.Default with { MeshFrequencyHz = 10e9 },
        });

        Assert.NotNull(vm.Working.PlanarMesh.MeshFrequencyHz);

        vm.PlanarMeshFrequencyText = "   ";
        vm.CommitMeshField("MeshFrequency");

        // Blank is a real VALUE (null = max sweep), not "leave it alone".
        Assert.Null(vm.Working.PlanarMesh.MeshFrequencyHz);
        Assert.Contains("max sweep", vm.MeshFrequencyPlaceholder, StringComparison.Ordinal);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void CommittingIt_IsOneUndoEntry_InvalidatesTheMesh_AndDoesNotClearAuto()
    {
        string dir = TempDir();
        var vm = Editor(dir);

        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);
        Assert.True(vm.Working.PlanarMesh.Auto);

        int undosBefore = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosBefore++; }
        while (vm.UndoRedo.CanRedo) vm.UndoRedo.Redo();

        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);

        vm.PlanarMeshFrequencyText = "10";
        vm.CommitMeshField("MeshFrequency");

        // The panel must not go on reporting an N produced at another mesh frequency.
        Assert.Null(vm.PlanarMeshReport);

        // …and unlike cells/λ and edge cells, this control does NOT pin the cell size by clearing
        // Auto. Auto has no opinion about WHICH frequency its own sizing is applied at.
        Assert.True(vm.Working.PlanarMesh.Auto);

        int undosAfter = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosAfter++; }
        Assert.Equal(undosBefore + 1, undosAfter);
        Assert.Null(vm.Working.PlanarMesh.MeshFrequencyHz);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void ChangingTheSweepsUnit_RerendersTheField_SoTheStoredHertzNeverDrifts()
    {
        // The field is edited in the sweep's own unit and STORED in hertz. If a sweep-unit change
        // did not re-render it, a stored 10 GHz would go on reading "10" beside an "MHz" label —
        // a factor of a thousand, reported by nothing, and committed the next time the user
        // tabbed through the field.
        string dir = TempDir();
        var vm = Editor(dir);

        vm.Frequency.StopUnit = "GHz";
        vm.PlanarMeshFrequencyText = "10";
        vm.CommitMeshField("MeshFrequency");
        Assert.Equal(10e9, vm.Working.PlanarMesh.MeshFrequencyHz);

        vm.Frequency.StopUnit = "MHz";

        Assert.Equal("MHz", vm.MeshFrequencyUnit);
        Assert.Equal(10e9, vm.Working.PlanarMesh.MeshFrequencyHz);   // the STORED value never moved
        Assert.Equal("10000", vm.PlanarMeshFrequencyText);           // …and the field now says so

        Directory.Delete(dir, true);
    }

    [Fact]
    public void CommittingTheValueItAlreadyHas_PushesNothing()
    {
        string dir = TempDir();
        var vm = Editor(dir);

        while (vm.UndoRedo.CanUndo) vm.UndoRedo.Undo();

        // Already blank; committing blank again must be a genuine no-op, not a null-to-null entry.
        vm.PlanarMeshFrequencyText = "";
        vm.CommitMeshField("MeshFrequency");
        Assert.False(vm.UndoRedo.CanUndo);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void UnparseableText_ChangesNothing_AndRevertsToTheCanonicalValue()
    {
        string dir = TempDir();
        var vm = Editor(dir, new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = PlanarMeshSettings.Default with { MeshFrequencyHz = 10e9 },
        });

        while (vm.UndoRedo.CanUndo) vm.UndoRedo.Undo();

        foreach (string bad in new[] { "not a number", "-5", "0" })
        {
            vm.PlanarMeshFrequencyText = bad;
            vm.CommitMeshField("MeshFrequency");
            Assert.Equal(10e9, vm.Working.PlanarMesh.MeshFrequencyHz);
            Assert.False(vm.UndoRedo.CanUndo);
        }

        Directory.Delete(dir, true);
    }
}
