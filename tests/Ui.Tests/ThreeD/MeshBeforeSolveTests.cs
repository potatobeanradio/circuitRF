// brief-dfr36-1 — the mesh seen before a solve: the toggle needs a mesh and no fields, the mesh has its own plane, a Mesh click is
// refused on a setup with no Gmsh mesh, and a run's reuse test and the view's are one.

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class MeshBeforeSolveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-mesh-before-solve-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Viewer3DViewModel Viewer()
        => new("model.c3d", "model", () => null, (_, _, _) => throw new InvalidOperationException(), () => new RecordingBackend(), () => _root, a => a());

    /// <summary>A mesh on disk alone, as a mesh-only pass leaves it, for a Palace setup and for a thermal one.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMeshAloneEnablesTheToggle_WithNoFieldsAndNoRecord(bool thermal)
    {
        var setup = thermal
            ? new EmSetup { Name = "T", Problem3D = Em3dProblemType.Thermal }
            : new EmSetup { Name = "P", Solver3D = Em3dSolverId.Palace };
        string dir = thermal ? ThermalRunService.RunDirectory(_root, setup) : Em3dRunService.RunDirectory(_root, setup, Em3dSolverId.Palace);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, GmshGeoWriter.MeshFile), "");

        using var viewer = Viewer();
        viewer.ShowMeshWhenAvailable = true;
        viewer.SetRunSetup(setup);

        Assert.True(viewer.CanShowMesh);
        Assert.True(viewer.ShowMesh);
        Assert.False(viewer.FieldsAvailable);
        Assert.Contains("mesh-only pass", viewer.MeshTip, StringComparison.Ordinal);
    }

    /// <summary>Moving the clip plane never moves the mesh's plane, nor the reverse; the mesh's plane cuts the solids only while the clip
    /// plane is off, and a field's slice stays on the clip plane throughout.</summary>
    [Fact]
    public void TheMeshPlane_IsSeparateFromTheClipPlane()
    {
        using var viewer = Viewer();
        viewer.MeshPlaneAxis = ClipAxis3D.X;
        viewer.MeshPlaneEnabled = true;
        viewer.ShowMesh = true;
        var plane = viewer.MeshPlane;

        Assert.True(viewer.View.ShowMeshSection);
        Assert.False(viewer.View.ShowMesh);
        Assert.Equal(plane.Equation, viewer.View.Clip.Equation);
        Assert.False(viewer.ToolbarClip.Enabled);

        viewer.ClipEnabled = true;
        viewer.ClipPosition = 0.25;
        Assert.Equal(plane.Equation, viewer.MeshPlane.Equation);
        Assert.Equal(ClipAxis3D.Z, viewer.View.Clip.Axis);
        Assert.Equal(viewer.ToolbarClip.Equation, viewer.View.Clip.Equation);

        var clip = viewer.ToolbarClip;
        viewer.MeshPlaneAxis = ClipAxis3D.Y;
        Assert.Equal(clip.Equation, viewer.ToolbarClip.Equation);
        Assert.Equal(clip.Equation, viewer.View.Clip.Equation);
    }

    /// <summary>A click on a setup with no Gmsh mesh is refused with its reason, in the words the toolbar's tooltip uses.</summary>
    [Fact]
    public void MeshButton_RefusesWhatGmshDoesNotMesh()
    {
        Assert.StartsWith("This setup is a planar analysis", Em3dRunService.MeshButtonRefusal(new EmSetup()), StringComparison.Ordinal);
        Assert.StartsWith("Mesh makes the mesh Gmsh writes for Palace",
                          Em3dRunService.MeshButtonRefusal(new EmSetup { Solver3D = Em3dSolverId.OpenEms }), StringComparison.Ordinal);
        Assert.Null(Em3dRunService.MeshButtonRefusal(new EmSetup { Solver3D = Em3dSolverId.Palace }));
        Assert.Null(Em3dRunService.MeshButtonRefusal(new EmSetup { Problem3D = Em3dProblemType.Thermal }));
    }

    /// <summary>The planar door starts nothing: refused before discovery or any process, with no folder written.</summary>
    [Fact]
    public void APlanarSetup_RefusedAtTheRunsDoor_StartsNoProcess()
    {
        long before = PalaceRun.GmshInvocations;
        var result = EmRunService.RunThreeDView(new EmSetup { Name = "planar" }, new CircuitRF.Design.ThreeD.C3dDocument(), Path.Combine(_root, "m.c3d"), null,
                                                Path.Combine(_root, "results"), control: new CircuitRF.Engine.RunControl { MeshOnly = true });
        Assert.Equal(EmRunStatus.Refused, result.Status);
        Assert.Equal(before, PalaceRun.GmshInvocations);
        Assert.False(Directory.Exists(Path.Combine(_root, "results")));
    }

    /// <summary>The stale sentence states the mesh is not current; a current mesh says nothing.</summary>
    [Fact]
    public void AMeshThatNoLongerMatches_IsSaidToBeStale()
    {
        Assert.Null(Viewer3DViewModel.MeshStaleSentence(Em3dMeshCurrency.Current, "at 12:00"));
        Assert.Contains("has changed since", Viewer3DViewModel.MeshStaleSentence(Em3dMeshCurrency.Stale, "at 12:00"), StringComparison.Ordinal);
        Assert.Contains("when a run starts", Viewer3DViewModel.MeshStaleSentence(Em3dMeshCurrency.Unknown, "at 12:00"), StringComparison.Ordinal);
    }

    /// <summary>R-dfr36-1-1/2 — the Mesh button's own path through the shell, on real Gmsh: the view turns the mesh on with no fields, Messages
    /// says its size, and no record, result or status is written.</summary>
    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    public async Task TheMeshButtonsPath_ShowsTheMesh_AndWritesNoResult()
    {
        string example = Path.Combine(RepoRoot(), "examples", "Thermal Channel vs Surface");
        foreach (string f in Directory.EnumerateFiles(example, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(_root, Path.GetRelativePath(example, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        string cws = Path.Combine(_root, ".cws");
        string path = Path.Combine(_root, "One Finger", "3d", "One Finger.c3d");
        var shell = new WorkspaceViewModel { CurrentWorkspacePath = cws, MessagesOverride = new Sink() };
        string results = Path.Combine(_root, "results");
        using var c3d = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new RecordingBackend(), () => cws, a => a())
        {
            ResultsRootProvider = () => results,
        };
        c3d.RestoreActiveSetup(null);

        await shell.RunC3dSetupAsync(c3d, null, meshOnly: true);

        string run = Directory.GetDirectories(results).Single();
        Assert.True(File.Exists(Path.Combine(run, GmshGeoWriter.MeshFile)));
        Assert.False(File.Exists(Path.Combine(run, "status.json")));
        Assert.False(File.Exists(Path.Combine(run, ThermalRunService.TemperaturesFile)));
        Assert.Empty(Directory.GetFiles(results));
        Assert.True(c3d.Viewer.CanShowMesh);
        Assert.True(c3d.Viewer.ShowMesh);
        Assert.False(c3d.Viewer.FieldsAvailable);
    }

    /// <summary>R-dfr36-1-2/3 — the row a pass settles on: its size against the estimate, and that a current mesh was reused.</summary>
    [Fact]
    public void ThePassSaysItsSize_AndThatAMeshWasReused()
    {
        var report = new Em3dMeshOnlyReport(1000, 1200, Reused: true, "run", []);
        string said = WorkspaceViewModel.EmMeshOnlySummary(report);
        Assert.Contains("tetrahedra against", said, StringComparison.Ordinal);
        Assert.Contains("reused", said, StringComparison.Ordinal);
        Assert.DoesNotContain("reused", WorkspaceViewModel.EmMeshOnlySummary(report with { Reused = false }), StringComparison.Ordinal);
    }

    private sealed class Sink : CircuitRF.Ui.Messages.IMessageSink
    {
        public void Post(CircuitRF.Ui.Messages.MessageLevel level, string text, string? filePath = null) { }
        public void Clear() { }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
