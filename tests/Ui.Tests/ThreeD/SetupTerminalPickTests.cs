// ================================================================
//  SetupTerminalPickTests.cs — an electrostatic setup's Pick, through the 3D editor that owns the panel. The workspace
//  builds the panel (RestoreActiveSetup) before the view's first elaboration lands; the panel then had no conductors to
//  match, so every pick in the view matched nothing and the Pick stayed armed. The GPU is a recording fake.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class SetupTerminalPickTests : IDisposable
{
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pick-" + Guid.NewGuid().ToString("N")[..12]);
    private C3dEditorViewModel? _vm;

    public void Dispose()
    {
        _vm?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void APanelBuiltBeforeTheFirstElaboration_ListsTheConductors_AndAPickFillsTheRow_OrSaysWhyNot()
    {
        var vm = Open();
        var setup = vm.SetupEditor!.ViewModel;
        setup.ListBodiesAsync().GetAwaiter().GetResult();          // what the setup window's open does
        Assert.Equal("2 piece(s) of metal.", setup.BodiesStatus);

        var row = setup.TerminalRows[0];
        setup.PickTerminalCommand.Execute(row);
        Select(vm, "sub");                                          // a dielectric: refused, said, still armed
        Assert.Same(row, setup.PickingRow);
        Assert.StartsWith("\"sub\" is not a conductor.", setup.StaticStatus);

        Select(vm, "line");
        Assert.Null(setup.PickingRow);
        Assert.Equal("line", row.Net);
    }

    private static void Select(C3dEditorViewModel vm, string name)
        => vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.Viewer.Scene.Objects.Single(o => o.Name == name).Id)]);

    private C3dEditorViewModel Open()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 4.4 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        var setup = new EmSetup
        {
            Name = "ES", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Electrostatic,
            Terminals3D = [new EmTerminal3D("pad", "", Objects: ["pad"])],
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(400 * Um, 400 * Um, 100 * Um) },
                new C3dBox { Name = "pad", Material = "Copper", Min = new C3dPoint3(0, 0, 100 * Um), Size = new C3dPoint3(100 * Um, 100 * Um, 10 * Um) },
                new C3dBox { Name = "line", Material = "Copper", Min = new C3dPoint3(200 * Um, 0, 100 * Um), Size = new C3dPoint3(100 * Um, 400 * Um, 10 * Um) },
            ],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = _vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        vm.RestoreActiveSetup(null);                                // before Start, as WorkspaceViewModel.OpenC3dEditor does
        Assert.NotNull(vm.SetupEditor);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)), "the scene never settled");
        return vm;
    }
}
