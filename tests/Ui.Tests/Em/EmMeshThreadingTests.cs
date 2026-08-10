// Owner report, 2026-08-09: "I pressed the mesh button but got: Meshing 'MLin' failed: The calling
// thread cannot access this object because a different thread owns it."
//
// Moving the Mesh button off the UI thread was right; moving ALL of BuildActiveMesh off it was not.
// That method writes observable view-model properties — which raise PropertyChanged straight into
// bound Avalonia controls — and fires AnalysisRefreshed, which the workspace turns into opening a
// layout document. Only SurfaceMesher.Mesh is poolable.
//
// A cross-thread violation cannot be reproduced headlessly (this suite owns no dispatcher), so these
// gates pin the two things that CAN be checked: the split composes to the same answer as the one-shot
// path, and the host offloads only the pure half.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmMeshThreadingTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static LayoutView LineLayout(Technology tech)
    {
        var signal = tech.Stackup.Layers
            .First(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference);
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Mil, SnapDbu = 0 };
        view.Shapes.Add(new RectShape
        {
            Layer = signal.DrawingLayers[0],
            X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu,
        });
        return view;
    }

    private static EmSetupEditorViewModel Vm()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var view = LineLayout(tech);
        return new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-meshthread.cem"),
            new EmSetup { Name = "MLin", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar })
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(Path.GetTempPath(), "a.clay"), view, tech, Dbu),
        };
    }

    [Fact]
    public void PrepareThenComputeThenAdopt_ProducesTheSameResultAsTheOneShotPath()
    {
        // The property the split has to preserve. If these ever diverge, the button and every
        // headless caller stop meshing the same thing.
        var oneShot = Vm();
        oneShot.Refresh();
        oneShot.BuildPlanarMesh();

        var split = Vm();
        split.Refresh();
        var problem = split.PreparePlanarMesh();
        Assert.NotNull(problem);
        split.AdoptPlanarMeshReport(split.ComputePlanarMesh(problem!, null));

        Assert.Equal(oneShot.PlanarMeshReport!.UnknownCount, split.PlanarMeshReport!.UnknownCount);
        Assert.Equal(oneShot.PlanarMeshReport.CellCount,     split.PlanarMeshReport.CellCount);
        Assert.Equal(oneShot.PlanarMeshNotes,                split.PlanarMeshNotes);
    }

    [Fact]
    public void Prepare_DoesTheStateWrites_SoComputeHasNothingToTouch()
    {
        // The boundary itself: after Prepare, everything the UI binds to is already written except
        // the report — which is exactly what Adopt puts back on the UI thread.
        var vm = Vm();
        vm.Refresh();

        var problem = vm.PreparePlanarMesh();

        Assert.NotNull(problem);
        Assert.NotNull(vm.PlanarProblem);
        Assert.Null(vm.PlanarMeshReport);              // not meshed yet
    }

    [Fact]
    public void PrepareReturnsNull_WhenThereIsNothingToMesh_AndSaysWhy()
    {
        // Null is the "do not offload anything" signal, and the reason is already on screen.
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-nolayout.cem"),
            new EmSetup { Name = "MLin", LayoutRef = "", AnalysisKind = EmAnalysisKind.Planar });
        vm.Refresh();

        Assert.Null(vm.PreparePlanarMesh());
        Assert.NotNull(vm.PlanarExtractionRefusal);
    }

    [Fact]
    public void ComputeIsPure_RunningItTwiceChangesNoViewModelState()
    {
        // "Poolable" means it touches nothing shared. Running it repeatedly must leave the view model
        // exactly as Prepare left it.
        var vm = Vm();
        vm.Refresh();
        var problem = vm.PreparePlanarMesh()!;

        var a = vm.ComputePlanarMesh(problem, null);
        var b = vm.ComputePlanarMesh(problem, null);

        Assert.Null(vm.PlanarMeshReport);              // still nothing adopted
        Assert.Equal(a.UnknownCount, b.UnknownCount);
        Assert.Equal(a.CellCount, b.CellCount);
    }

    [Fact]
    public void TheHost_OffloadsOnlyTheMesher()
    {
        // The regression guard for the reported crash. WorkspaceViewModel cannot be constructed
        // headlessly, so this reads the call site — the same fallback this codebase already uses for
        // view/menu wiring it cannot instantiate.
        string src = File.ReadAllText(RepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        int at = src.IndexOf("private async Task MeshEmSetupAsync", StringComparison.Ordinal);
        Assert.True(at > 0, "MeshEmSetupAsync not found");
        string body = src[at..(at + 4000)];

        Assert.Contains("Task.Run(() => vm.ComputePlanarMesh(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(() => vm.BuildActiveMesh", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(() => vm.BuildPlanarMesh", body, StringComparison.Ordinal);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
