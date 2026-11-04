// ================================================================
//  AirBoxEditShowsTests.cs — brief DFR32-3 R-dfr32-3-1: an air-box padding typed in a .c3d's Properties is the box the
//  scene draws once it settles. A synthetic two-layer board (a substrate, a trace on top, a ground below) with one
//  embedded Palace setup; the panel's own calls are driven (the padding boxes, typed as percentages, Enter/lost focus), and the drawn box —
//  the scene's air-box edges — is compared with the box C3dProblemAssembly resolves from the document.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class AirBoxEditShowsTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-airedit-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public AirBoxEditShowsTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void OneFace_ThenAllSix_TheDrawnBoxIsTheResolvedOne()
    {
        var vm = OpenOnAirBox();
        Commit(vm, "xmin", "20");
        Settle(vm);
        AssertDrawnIsResolved(vm);
        foreach (var (face, pct) in new[] { ("xmax", "30"), ("ymin", "25"), ("ymax", "20"), ("zmin", "30"), ("zmax", "25") })
        {
            Commit(vm, face, pct);
            Settle(vm);
        }
        AssertDrawnIsResolved(vm);
        Assert.Equal(25, Stated(vm).ZMax!.PaddingPercent);
    }

    [Fact]
    public void FastEdits_WhileARebuildIsInFlight_AllReachTheDocumentAndTheDrawnBox()
    {
        var vm = OpenOnAirBox();
        foreach (var (face, pct) in new[] { ("xmin", "20"), ("xmax", "30"), ("ymin", "25"), ("ymax", "20"), ("zmin", "30"), ("zmax", "25") })
            Commit(vm, face, pct);                      // no settling between: each rebuild supersedes the one in flight
        Settle(vm);
        var stated = Stated(vm);
        Assert.Equal([20.0, 30, 25, 20, 30, 25],
                     new[] { stated.XMin, stated.XMax, stated.YMin, stated.YMax, stated.ZMin, stated.ZMax }.Select(f => f!.PaddingPercent!.Value));
        AssertDrawnIsResolved(vm);
    }

    [Fact]
    public void TypingTheNextFace_WhileThePreviousEditsSceneIsAdopted_IsNotWipedByTheReload()
    {
        var vm = OpenOnAirBox();
        Commit(vm, "xmin", "20");                       // Enter: the rebuild starts
        vm.Properties.PadXMaxPercent = "30 %";          // the next box, typed before that rebuild lands
        Settle(vm);                                     // the scene is adopted: the panel reloads
        vm.Properties.CommitAirBoxPercent("xmax");      // Enter on the box just typed in
        Settle(vm);
        Assert.Equal(30, Stated(vm).XMax?.PaddingPercent);
        Assert.Equal("30 %", vm.Properties.PadXMaxPercent);
        AssertDrawnIsResolved(vm);
    }

    [Fact]
    public void WithTheSetupPanelOpen_APanelEditAfterAPaddingEdit_KeepsThePadding()
    {
        var vm = OpenOnAirBox();
        vm.SelectedSetupItem = vm.SetupItems.Single();   // the setup's panel, an EmSetupEditorViewModel on the embedded setup
        Assert.NotNull(vm.SetupEditor);
        Commit(vm, "xmin", "20");
        Settle(vm);
        var panel = vm.SetupEditor!.ViewModel;
        string before = panel.SnapshotJson();
        panel.Working.RobustMeshing = !panel.Working.RobustMeshing;
        panel.CommitEdit(before, "Robust meshing");
        Settle(vm);
        Assert.Equal(20, Stated(vm).XMin?.PaddingPercent);
        AssertDrawnIsResolved(vm);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static void Commit(C3dEditorViewModel vm, string face, string pct)
    {
        var p = vm.Properties;
        pct += " %";                                    // brief-dfr34-2 R-dfr34-2-5 — a bare number is a length now
        switch (face)
        {
            case "xmin": p.PadXMinPercent = pct; break;
            case "xmax": p.PadXMaxPercent = pct; break;
            case "ymin": p.PadYMinPercent = pct; break;
            case "ymax": p.PadYMaxPercent = pct; break;
            case "zmin": p.PadZMinPercent = pct; break;
            default: p.PadZMaxPercent = pct; break;
        }
        p.CommitAirBoxPercent(face);
        Assert.Equal("", p.Error);
    }

    private static EmAirBox Stated(C3dEditorViewModel vm) => EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]).AirBox!;

    /// <summary>The scene's air-box edges span the box the document resolves to, and so does the box the records hold.</summary>
    private static void AssertDrawnIsResolved(C3dEditorViewModel vm)
    {
        var setup = EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]);
        var resolved = C3dProblemAssembly.AirBox(setup, vm.Elaboration!, out _)!;
        var drawn = Assert.IsType<Scene3DObject>(vm.Viewer.Scene.Objects.SingleOrDefault(o => o.Name == Scene3DBuilder.AirBoxEdgesName));
        Assert.Equal(resolved.Max.X - resolved.Min.X, drawn.Max.X - drawn.Min.X, 1e-7);
        Assert.Equal(resolved.Max.Y - resolved.Min.Y, drawn.Max.Y - drawn.Min.Y, 1e-7);
        Assert.Equal(resolved.Max.Z - resolved.Min.Z, drawn.Max.Z - drawn.Min.Z, 1e-7);
        Assert.Equal(resolved, vm.ShownAirBox);
    }

    private C3dEditorViewModel OpenOnAirBox()
    {
        var vm = Open(Doc(new EmSetup { Name = "S1", Solver3D = Em3dSolverId.Palace }));
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.IsAirBox);
        Assert.True(vm.Properties.IsAirBox);
        return vm;
    }

    private static C3dDocument Doc(params EmSetup[] setups)
    {
        C3dRect R(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };
        return new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000 * Um, 1000 * Um, 100 * Um) },
                new C3dSheet { Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = 100 * Um, Rect = R(0, 450, 1000, 100) },
                new C3dSheet { Name = "gnd", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Rect = R(0, 0, 1000, 1000) },
            ],
            Setups = [.. setups.Select(EmSetupPersistence.ToEmbedded)],
        };
    }

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 2 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
