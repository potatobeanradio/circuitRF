using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>
/// brief-hzn-12 (follow-up) — the 3D view's incident plane waves: Add Plane Wave on the canvas, Onto Face on a flat face, one named row
/// each under Excitations with at most one enabled, the Inspector's name, Enabled box and values, the wave's arrows picked as its own
/// kind, the card's RCS badge, a switch to Palace, and Delete. Read off
/// the view model, the document and the scene (the canvas cannot draw from this machine's shell).
/// </summary>
[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class PlaneWaveEditorTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn12ui-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public PlaneWaveEditorTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>Add Plane Wave on a port-driven setup: one undo entry, the wave falling from above, its row, the
    /// card's RCS badge, and the glyph drawn as the wave's own pickable kind. Undo takes it all back.</summary>
    [Fact]
    public void AddPlaneWave_DrivesTheActiveSetup_AsOneEntry_WithItsRowBadgeAndArrows_AndUndoTakesItBack()
    {
        var vm = Open();
        int entries = vm.UndoEntries;
        vm.AddPlaneWave();
        Settle(vm);

        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(new Point3(0, 0, -1), vm.ActivePlaneWave!.Direction);
        Assert.Single(vm.Tree.SelectMany(g => g.Items), i => i.Kind == C3dEditorViewModel.PlaneWaveKind && i.DisplayName == "Plane wave 1");
        Assert.Equal("RCS", vm.SetupItems.Single(i => i.Name == "S").TypeLabel);
        var arrows = vm.SceneObject(Scene3DBuilder.PlaneWaveName);
        Assert.NotNull(arrows);
        Assert.Equal(Scene3DKind.Excitation, arrows!.Kind);

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Null(vm.ActivePlaneWave);
        Assert.Empty(vm.Document.PlaneWaves);
        Assert.DoesNotContain(vm.Tree.SelectMany(g => g.Items), i => i.Kind == C3dEditorViewModel.PlaneWaveKind);
        Assert.Equal("SP", vm.SetupItems.Single(i => i.Name == "S").TypeLabel);
    }

    /// <summary>Onto Face aims the wave straight into the face, along its inward normal (the box's top: travelling −z, its side: +x),
    /// with its phase zero at the face's centre, where the view draws it arriving.</summary>
    [Theory]
    [InlineData("zmax", 0, 0, -1, 10, 10, 20)]
    [InlineData("xmin", 1, 0, 0, 0, 10, 10)]
    public void AddPlaneWaveOntoFace_TravelsIntoThatFace_FromItsCentre(string face, double kx, double ky, double kz, double ox, double oy, double oz)
    {
        var vm = Open();
        var block = vm.SceneObject("block")!;
        Assert.Null(vm.AddPlaneWaveOntoFace(block.Id, block.FaceNames.ToList().IndexOf(face)));
        Settle(vm);
        var k = vm.ActivePlaneWave!.Direction;
        Assert.Equal((kx, ky, kz), (k.X, k.Y, k.Z));
        var e = vm.ActivePlaneWave.EField;
        Assert.Equal(0, k.X * e.X + k.Y * e.Y + k.Z * e.Z, 12);
        var o = vm.ActivePlaneWave.PhaseOriginUm!.Value;
        Assert.Equal((ox, oy, oz), (Math.Round(o.X, 3), Math.Round(o.Y, 3), Math.Round(o.Z, 3)));
    }

    /// <summary>The row's Properties page writes the wave as typed, as one entry; a wave that cannot be solved is refused with its
    /// reason and leaves the setup as it was.</summary>
    [Fact]
    public void ThePropertiesPage_WritesTheWave_AndRefusesOneThatCannotBeSolved()
    {
        var vm = Open();
        vm.AddPlaneWave();
        Settle(vm);
        var p = vm.Properties;
        Assert.True(p.IsPlaneWave);
        p.PlaneWaveDirection = "1, 0, 0";
        p.PlaneWaveEField = "0, 0, 1";
        int entries = vm.UndoEntries;
        p.CommitPlaneWave();
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal((new Point3(1, 0, 0), new Point3(0, 0, 1)), (vm.ActivePlaneWave!.Direction, vm.ActivePlaneWave.EField));

        vm.Properties.Reload();
        vm.Properties.PlaneWaveEField = "1, 0, 0";
        vm.Properties.CommitPlaneWave();
        Assert.Contains("right angles", vm.Properties.Error);
        Assert.Equal(new Point3(0, 0, 1), vm.ActivePlaneWave!.EField);
    }

    /// <summary>A view holds several waves, each its own named row, and at most one is enabled: adding one disables the rest, enabling
    /// one disables the other, and the disabled rows stay (greyed) to switch back to. The enabled one is what drives the setup.</summary>
    [Fact]
    public void SeveralWaves_EachItsOwnRow_WithOnlyOneEnabled_WhichDrivesTheSetup()
    {
        var vm = Open();
        vm.AddPlaneWave();
        vm.AddPlaneWave(new Point3(1, 0, 0), new Point3(0, 0, 1));
        Settle(vm);
        Assert.Equal([("Plane wave 1", false), ("Plane wave 2", true)], vm.Document.PlaneWaves.Select(w => (w.Name, w.Enabled)));
        var rows = vm.Tree.SelectMany(g => g.Items).Where(i => i.Kind == C3dEditorViewModel.PlaneWaveKind).ToList();
        Assert.Equal([("Plane wave 1", false), ("Plane wave 2", true)], rows.Select(r => (r.DisplayName, r.IsModelled)));
        Assert.Equal(new Point3(1, 0, 0), vm.ActivePlaneWave!.Direction);

        Assert.Null(vm.SetPlaneWaveEnabled("Plane wave 1", true));
        Settle(vm);
        Assert.Equal([true, false], vm.Document.PlaneWaves.Select(w => w.Enabled));
        Assert.Equal(new Point3(0, 0, -1), vm.ActivePlaneWave!.Direction);

        Assert.Null(vm.SetPlaneWaveEnabled("Plane wave 1", false));
        Settle(vm);
        Assert.Null(vm.ActivePlaneWave);
        Assert.Equal("SP", vm.SetupItems.Single(i => i.Name == "S").TypeLabel);
    }

    /// <summary>The Inspector names the wave and says whether it is enabled: a new name is written as one entry (and a name another wave
    /// has is refused), and the Enabled box enables it.</summary>
    [Fact]
    public void TheInspector_RenamesTheWave_AndItsEnabledBoxEnablesIt()
    {
        var vm = Open();
        vm.AddPlaneWave();
        vm.AddPlaneWave();
        Settle(vm);
        var p = vm.Properties;
        Assert.Equal(("Plane wave 2", true), (p.NameText, p.PlaneWaveEnabled));
        int entries = vm.UndoEntries;
        p.NameText = "Nose on";
        p.CommitName();
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(["Plane wave 1", "Nose on"], vm.Document.PlaneWaves.Select(w => w.Name));

        vm.Properties.Reload();
        vm.Properties.NameText = "Plane wave 1";
        vm.Properties.CommitName();
        Assert.NotEmpty(vm.Properties.Error);
        Assert.Equal("Nose on", vm.Document.PlaneWaves[1].Name);

        vm.SelectPlaneWaveRow("Plane wave 1");
        Settle(vm);
        Assert.Equal(("Plane wave 1", false), (vm.Properties.NameText, vm.Properties.PlaneWaveEnabled));
        vm.Properties.PlaneWaveEnabled = true;
        Settle(vm);
        Assert.Equal([true, false], vm.Document.PlaneWaves.Select(w => w.Enabled));
    }

    /// <summary>Changing the setup's solver to Palace in its panel disables the enabled wave in the same entry, and Undo puts both back.</summary>
    [Fact]
    public void ASwitchToPalace_DisablesTheWave_InTheSameEntry()
    {
        var vm = Open();
        vm.AddPlaneWave();
        Settle(vm);
        vm.SelectedSetupItem = vm.SetupItems.Single(i => i.Name == "S");
        Settle(vm);
        int entries = vm.UndoEntries;
        var panel = vm.SetupEditor!.ViewModel;
        panel.Solver3DChoice = CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel.Solver3DChoices.Single(c => c.Value == Em3dSolverId.Palace);
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.False(vm.Document.PlaneWaves.Single().Enabled);
        Assert.Contains("disabled", vm.StatusMessage);
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.True(vm.Document.PlaneWaves.Single().Enabled);
    }

    /// <summary>Move… on the wave, on its row's menu and on its picture's, moves its phase origin by the typed distance as one undo entry,
    /// and the picture follows; a disabled wave is not drawn, so its Move… is greyed saying why.</summary>
    [Fact]
    public void MoveOnTheWave_MovesItsPhaseOrigin_AsOneEntry_AndThePictureFollows()
    {
        var vm = Open();
        vm.AddPlaneWave();
        vm.AddPlaneWave();
        Settle(vm);
        var row = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == C3dEditorViewModel.PlaneWaveRowPrefix + "Plane wave 2");
        Assert.Contains(vm.TreeMenuItems(row), m => m.Header == "Move…" && m.Enabled);
        Assert.Equal(["X", "Y", "Z"], vm.TreeMenuItems(row).Single(m => m.Header == "Move Along").Children!.Select(c => c.Header));
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject(Scene3DBuilder.PlaneWaveName)!.Id)]);   // its picture, on the canvas
        Assert.Contains(vm.Viewer.ContextMenuItems(), m => m.Header == "Move…" && m.Enabled);
        Assert.True(vm.CanRunModify("Move"));
        float before = vm.SceneObject(Scene3DBuilder.PlaneWaveName)!.Min.X;

        int entries = vm.UndoEntries;
        vm.Viewer.ContextMenuItems().Single(m => m.Header == "Move Along").Children!.Single(c => c.Header == "X").Run!();
        var move = Assert.IsType<CircuitRF.Ui.ThreeD.Operations.MoveTool>(vm.Tool);
        Assert.Equal("Plane wave 2", move.PlaneWave);
        move.SetBase(new C3dPoint3(0, 0, 0), exact: true);
        vm.OpenField(null);
        vm.FieldText = "5";
        vm.FieldEnter();
        Settle(vm);

        Assert.True(LayoutUnits.TryParse("5", vm.Document.DisplayUnit, vm.Document.DbuPerMicron, out long d));
        Assert.Equal(new C3dPoint3(d, 0, 0), vm.Document.PlaneWaves[1].PhaseOrigin);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(d * 1e-9, vm.SceneObject(Scene3DBuilder.PlaneWaveName)!.Min.X - before, 1e-3 * d * 1e-9);
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(default, vm.Document.PlaneWaves[1].PhaseOrigin);

        var disabled = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == C3dEditorViewModel.PlaneWaveRowPrefix + "Plane wave 1");
        Assert.Contains(vm.TreeMenuItems(disabled), m => m.Header == "Move…" && !m.Enabled && m.Tip!.Contains("disabled"));
    }

    /// <summary>A direction typed in the Inspector turns the drawn wave round: falling from above, then rising from below.</summary>
    [Fact]
    public void ADirectionTypedInTheInspector_TurnsTheDrawnWave()
    {
        var vm = Open();
        vm.AddPlaneWave();
        Settle(vm);
        var above = vm.SceneObject(Scene3DBuilder.PlaneWaveName)!;
        vm.Properties.PlaneWaveDirection = "0, 0, 1";
        vm.Properties.CommitPlaneWave();
        Settle(vm);
        Assert.Equal(new Point3(0, 0, 1), vm.ActivePlaneWave!.Direction);
        var below = vm.SceneObject(Scene3DBuilder.PlaneWaveName)!;
        Assert.True(below.Max.Z < above.Min.Z, $"the wave is drawn below the model now: {above.Min.Z}..{above.Max.Z} then {below.Min.Z}..{below.Max.Z}");
    }

    /// <summary>The wave's row is in its own Excitations group, not the air box's.</summary>
    [Fact]
    public void TheWavesRow_IsInItsOwnExcitationsGroup()
    {
        var vm = Open();
        vm.AddPlaneWave();
        Settle(vm);
        var group = vm.Tree.Single(g => g.Items.Any(i => i.Kind == C3dEditorViewModel.PlaneWaveKind));
        Assert.Equal(("Excitations", C3dTreeGroupRole.Excitations), (group.Header, group.Role));
        Assert.DoesNotContain(group.Items, i => i.IsAirBox);
    }

    /// <summary>On a Palace setup Add Plane Wave is refused with Palace's reason, and the setup is left driven by its ports.</summary>
    [Fact]
    public void OnAPalaceSetup_AddingAPlaneWave_IsRefusedWithTheReason()
    {
        var vm = Open(Em3dSolverId.Palace);
        int entries = vm.UndoEntries;
        vm.AddPlaneWave();
        Assert.Contains("no incident-field source", vm.StatusMessage);
        Assert.Null(vm.ActivePlaneWave);
        Assert.Equal(entries, vm.UndoEntries);
    }

    /// <summary>Delete on the wave's row removes it and returns the setup to its ports.</summary>
    [Fact]
    public void Delete_OnTheWavesRow_ReturnsTheSetupToItsPorts()
    {
        var vm = Open();
        vm.AddPlaneWave();
        Settle(vm);
        Assert.Equal(C3dEditorViewModel.PlaneWaveKind, vm.SelectedTreeItem?.Kind);
        Assert.True(vm.DeleteSelection());
        Settle(vm);
        Assert.Null(vm.ActivePlaneWave);
        Assert.Empty(vm.Document.PlaneWaves);
    }

    private C3dEditorViewModel Open(Em3dSolverId solver = Em3dSolverId.Emerge)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        var setup = new EmSetup { Name = "S", Solver3D = solver, Problem3D = Em3dProblemType.Driven };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "block", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.Equal("S", vm.ActiveSetupName);
        return vm;
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    private void Settle(C3dEditorViewModel vm) => Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, "the scene never settled");
}
