// ANT-3 — the sheet intent, Ui side: everything the ENGINE cannot see.
//
// The mesher half (does it actually mesh a patch, is it bounded above by the per-axis rule, does the
// note say what it did) is in tests/Engine.Tests/Mom/SheetMeshTests.cs. What lives here is the .cem
// round trip, the migration off the legacy boolean, the REFUSAL when a file states the setting
// twice, Clone, the staleness hash, and the panel's own commit.
//
// THE REFUSAL IS THE LOAD-BEARING ONE, and it is a decision rather than a convenience. The obvious
// alternative to refusing a file that carries both keys is a precedence rule ("the new key wins"),
// and that is the wrong answer for a reason this area has already paid for once: a precedence rule
// means a user who set one control watched the other one silently win, which is the exact defect the
// transmission-line mesh was built to fix.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Tests.Em;

public class SheetMeshUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static LayoutView PatchLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        {
            Layer = new(1, 0),
            X1 = 0, Y1 = 0, X2 = 41_300_000, Y2 = 49_400_000,
        });
        return view;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-sheet-" + Guid.NewGuid().ToString("N")[..8]);
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
                Path.Combine(dir, "a.clay"), PatchLayout(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The .cem round trip, and the omit-at-default rule
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACemThatNeverTouchedTheControl_GainsNoByte()
    {
        // §M3: "omit-at-default still applies, so an existing .cem gains no byte and its hash is
        // unchanged." An asserted property of this format rather than a nicety — a plain
        // (non-nullable) DTO field would be written unconditionally and would change EVERY .cem
        // already on disk.
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("CurrentModel", before, StringComparison.Ordinal);
        Assert.DoesNotContain("TransmissionLineMesh", before, StringComparison.Ordinal);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.Equal(PlanarCurrentModel.None, reloaded.PlanarMesh.CurrentModel);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Theory]
    [InlineData(PlanarCurrentModel.TransmissionLine)]
    [InlineData(PlanarCurrentModel.Sheet)]
    public void SettingIt_RoundTrips_SurvivesClone_AndSurvivesAutosCollapse(PlanarCurrentModel model)
    {
        var setup = new EmSetup
        {
            Name         = "planar",
            LayoutRef    = "Amp/layout/Amp.clay",
            AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh   = PlanarMeshSettings.Default with { CurrentModel = model },
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains($"\"CurrentModel\": \"{model}\"", json, StringComparison.Ordinal);
        Assert.Equal(model, EmSetupPersistence.Deserialize(json).PlanarMesh.CurrentModel);

        // Clone drives the editor's UNDO snapshots. A field missing from it is silently lost on the
        // next unrelated edit — assert it rather than assume it.
        Assert.Equal(model, setup.Clone().PlanarMesh.CurrentModel);

        // …and Resolved's Auto collapse keeps it, unlike cells/λ and edge cells. Auto decides a
        // RESOLUTION; this says what the resolutions are FOR.
        Assert.Equal(model, setup.PlanarMesh.Resolved.CurrentModel);
        Assert.Equal(model, (PlanarMeshSettings.Default with { Auto = true, CurrentModel = model })
                            .Resolved.CurrentModel);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The migration off the legacy boolean, and the refusal
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheLegacyBoolean_ReadsAsTransmissionLine_AndIsRewrittenOnTheNewKey()
    {
        // A .cem written before ANT-3 must open on exactly the mesh it described — that is the whole
        // of the migration, and it is why the legacy key is still read at all.
        string legacy = """
        {
          "FormatVersion": 1,
          "Name": "old",
          "LayoutRef": "Amp/layout/Amp.clay",
          "PlanarMesh": { "Auto": true, "TransmissionLineMesh": true }
        }
        """;

        var setup = EmSetupPersistence.Deserialize(legacy);
        Assert.Equal(PlanarCurrentModel.TransmissionLine, setup.PlanarMesh.CurrentModel);

        // …and it comes back out on the NEW key only. One setting, one spelling: a file that went on
        // carrying both would be a file that can later disagree with itself, which is what the
        // refusal below exists to catch.
        string rewritten = EmSetupPersistence.Serialize(setup);
        Assert.Contains("\"CurrentModel\": \"TransmissionLine\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("TransmissionLineMesh", rewritten, StringComparison.Ordinal);

        // An explicit legacy `false` is the default, and it survives the trip as such.
        var off = EmSetupPersistence.Deserialize(
            legacy.Replace("\"TransmissionLineMesh\": true", "\"TransmissionLineMesh\": false"));
        Assert.Equal(PlanarCurrentModel.None, off.PlanarMesh.CurrentModel);
    }

    [Theory]
    [InlineData("true",  "Sheet")]
    [InlineData("true",  "None")]
    [InlineData("false", "Sheet")]
    [InlineData("false", "TransmissionLine")]
    public void AFileStatingItTwiceAndDisagreeing_IsRefused_NamingBOTHKeys(string legacy, string model)
    {
        // §M3: "a file carrying both, disagreeing, is a refusal naming both keys — not a precedence
        // rule." Both key names must appear in the message, because the user's next action is to
        // delete one line and they cannot do that without being told which two lines are in play.
        string json = $$"""
        {
          "FormatVersion": 1,
          "Name": "conflicted",
          "LayoutRef": "Amp/layout/Amp.clay",
          "PlanarMesh": { "Auto": true, "TransmissionLineMesh": {{legacy}}, "CurrentModel": "{{model}}" }
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => EmSetupPersistence.Deserialize(json));
        Assert.Contains("CurrentModel", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TransmissionLineMesh", ex.Message, StringComparison.Ordinal);
        Assert.Contains(model, ex.Message, StringComparison.Ordinal);
        Assert.Contains(legacy, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true",  "TransmissionLine")]
    [InlineData("false", "None")]
    public void AFileStatingItTwiceAndAGREEING_LoadsQuietly(string legacy, string model)
    {
        // Two spellings of one answer is not a conflict, and refusing it would turn a file the tool
        // itself could have written during the migration window into an error.
        string json = $$"""
        {
          "FormatVersion": 1,
          "Name": "agreeing",
          "LayoutRef": "Amp/layout/Amp.clay",
          "PlanarMesh": { "Auto": true, "TransmissionLineMesh": {{legacy}}, "CurrentModel": "{{model}}" }
        }
        """;

        Assert.Equal(Enum.Parse<PlanarCurrentModel>(model),
                     EmSetupPersistence.Deserialize(json).PlanarMesh.CurrentModel);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Staleness. §6: "MeshHash includes the intent."
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChangingTheIntent_MovesMeshHash_AndEveryOtherTermStillMovesIt()
    {
        var none  = PlanarMeshSettings.Default;
        var tline = none with { CurrentModel = PlanarCurrentModel.TransmissionLine };
        var sheet = none with { CurrentModel = PlanarCurrentModel.Sheet };

        string hNone = EmSnpProvenance.MeshHash(none);

        // All three are different meshes, so all three are different hashes — including the two
        // non-default ones against EACH OTHER, which is the term ANT-3 adds.
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(tline));
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(sheet));
        Assert.NotEqual(EmSnpProvenance.MeshHash(tline), EmSnpProvenance.MeshHash(sheet));

        // …and the new term did not displace an existing one.
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(none with { CellsPerWavelength = 40 }));
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(none with { EdgeMesh = !none.EdgeMesh }));
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(none with { EdgeCells = 7 }));
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(none with { MeshFrequencyHz = 10e9 }));
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(none with { DetailFloorDivisor = 100 }));
        Assert.NotEqual(hNone, EmSnpProvenance.MeshHash(
            none with { BoundaryCells = PlanarBoundaryCells.Conformal }));
    }

    [Fact]
    public void TheTransmissionLineTermKeptItsExactBYTES_SoNoOldSnpReadsAsStale()
    {
        // ANT-3 renamed a C# member, not a user's setting. An .snp stamped while the control was a
        // boolean describes exactly the mesh `CurrentModel = TransmissionLine` describes now, so it
        // must go on reading as CURRENT — a tidier "|model=TransmissionLine" would have marked every
        // one of them stale for no reason a user could see.
        //
        // Pinned against the literal the old code produced, not against another call of the same
        // function, because two wrong implementations agree with each other.
        var m = PlanarMeshSettings.Default with { CurrentModel = PlanarCurrentModel.TransmissionLine };
        string preimage =
            $"{m.Auto}|{m.CellsPerWavelength}|{m.EdgeMesh}|{m.EdgeCells}|{m.BoundaryCells}|auto" +
            $"|tline=True|detail={m.DetailFloorDivisor}";
        string expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(preimage)))[..16].ToLowerInvariant();

        Assert.Equal(expected, EmSnpProvenance.MeshHash(m));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The panel: one undo entry, invalidates the mesh, does NOT clear Auto
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChoosingAnIntent_IsOneUndoEntry_InvalidatesTheMesh_AndDoesNotClearAuto()
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

        vm.PlanarCurrentModel = PlanarCurrentModel.Sheet;

        Assert.Equal(PlanarCurrentModel.Sheet, vm.Working.PlanarMesh.CurrentModel);
        // Auto is NOT cleared: this is not a resolution, it says what the resolutions are FOR, and
        // clearing Auto would pin the cell size the instant a user chose an intent.
        Assert.True(vm.Working.PlanarMesh.Auto);
        Assert.Null(vm.PlanarMeshReport);

        int undosAfter = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosAfter++; }
        Assert.Equal(undosBefore + 1, undosAfter);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void ThePanelOffersAllThreeIntents_FromTheEnumRatherThanAHandWrittenList()
    {
        // A fourth member must not be able to fail silently to appear — the same rule
        // BoundaryCellsChoices and EmPortZ0Row.KindChoices follow.
        Assert.Equal(Enum.GetValues<PlanarCurrentModel>().Length,
                     EmSetupEditorViewModel.CurrentModelChoices.Count);
        Assert.Contains(PlanarCurrentModel.Sheet, EmSetupEditorViewModel.CurrentModelChoices);

        // …and every one of them has a label about the STRUCTURE rather than the algorithm.
        foreach (var m in EmSetupEditorViewModel.CurrentModelChoices)
            Assert.False(string.IsNullOrWhiteSpace(
                Converters.PlanarCurrentModelNameConverter.Label(m)));
        Assert.Equal("Radiating sheet",
                     Converters.PlanarCurrentModelNameConverter.Label(PlanarCurrentModel.Sheet));
        // …and the default is "Unstated" rather than "Off": nothing is switched off, the question
        // simply has not been answered, and the per-axis rule is what happens then.
        Assert.Equal("Unstated",
                     Converters.PlanarCurrentModelNameConverter.Label(PlanarCurrentModel.None));
    }
}
