// 3D editor bugs round 6 — the setup panel and the setup cards of a .c3d (owner's list, 2026-09-28).
//
// brief-dfr36-1 — the Mesh button on a 3D setup now makes Gmsh's mesh, and greys on a solver Gmsh does not mesh for. The Stackup group could only be empty
// in a .c3d, whose geometry is the 3D model. A double-click on a setup card could land on a selectable fidelity line and
// open nothing.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.ThreeD;

public class EditorRound6SetupTests
{
    [Fact]
    public void ThreeDSetup_KeepsTheMeshButton_GreyedWithItsReasonOffGmsh_AndA_C3dSetupHidesTheStackup()
    {
        var embedded = new EmSetupEditorViewModel("model.c3d", new EmSetup { Name = "S1", Solver3D = Em3dSolverId.Palace }, embedded: true);
        Assert.True(embedded.ShowMeshButton);
        Assert.Null(embedded.MeshBlockedReason);
        Assert.True(embedded.BuildActiveMeshCommand.CanExecute(null));
        Assert.False(embedded.ShowStackup);

        // A .cem keeps its stackup (the 3D generator builds from it).
        var cem = new EmSetupEditorViewModel("panel.cem", new EmSetup { Name = "panel", AnalysisKind = EmAnalysisKind.Planar });
        Assert.True(cem.ShowMeshButton);
        Assert.True(cem.ShowStackup);
        var raised = new List<string?>();
        cem.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        cem.Solver3DChoice = EmSetupEditorViewModel.Solver3DChoices.First(c => c.Value == Em3dSolverId.Palace);
        Assert.True(cem.ShowMeshButton);
        Assert.Null(cem.MeshBlockedReason);
        Assert.True(cem.ShowStackup);
        cem.Solver3DChoice = EmSetupEditorViewModel.Solver3DChoices.First(c => c.Value == Em3dSolverId.OpenEms);
        Assert.True(cem.ShowMeshButton);
        Assert.StartsWith("Mesh makes the mesh Gmsh writes for Palace", cem.MeshBlockedReason, StringComparison.Ordinal);
        Assert.False(cem.BuildActiveMeshCommand.CanExecute(null));
        Assert.Contains(nameof(EmSetupEditorViewModel.MeshButtonTip), raised);

        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/Layout/EmSetupEditorView.axaml"));
        Assert.Contains("Name=\"MeshButton\" Margin=\"4,0,0,0\"\n                        IsVisible=\"{Binding ViewModel.ShowMeshButton}\"",
                        xaml.Replace("\r\n", "\n"), StringComparison.Ordinal);
        int stackup = xaml.IndexOf("Stackup: SHOWN", StringComparison.Ordinal);
        Assert.Contains("ViewModel.ShowStackup", xaml[stackup..(stackup + 400)], StringComparison.Ordinal);
    }

    [Fact]
    public void SetupCard_DoubleClickIsReadOffThePress_AndNoCardTextIsSelectable()
    {
        string root = RepoRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src/Ui/Views/ThreeD/C3dSetupAnalysesView.axaml"));
        string code = File.ReadAllText(Path.Combine(root, "src/Ui/Views/ThreeD/C3dSetupAnalysesView.axaml.cs"));
        Assert.DoesNotContain("<SelectableTextBlock", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleTapped", xaml, StringComparison.Ordinal);
        Assert.Contains("AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel, handledEventsToo: true)", code, StringComparison.Ordinal);
        Assert.Contains("ClickCount != 2", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
