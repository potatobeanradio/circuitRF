// 3D editor bugs round 7 — the setup panel's solver list, the setup cards, the toolbar (owner's list, 2026-09-28).
//
// Thermal was offered as a PROBLEM, beside Driven and Eigenmode, when it is who solves: it is the Solver list's last row
// now. A double-click on a card's solver-note line opened nothing. The mesh toggle had a row of its own under the toolbar
// with the stale-fields sentence, and was enabled with no field to plot.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public class EditorRound7Tests
{
    [Fact]
    public void Thermal_IsASolverChoice_NotAProblem_AndTheFileKeepsItsSpelling()
    {
        Assert.DoesNotContain(EmSetupEditorViewModel.Problem3DChoices, c => c.Value == Em3dProblemType.Thermal);

        var vm = new EmSetupEditorViewModel("model.c3d", new EmSetup { Name = "S1", Solver3D = Em3dSolverId.Palace }, embedded: true);
        var thermal = vm.Solver3DChoiceList[^1];
        Assert.Equal("Thermal", thermal.Label);
        Assert.True(thermal.IsThermal);
        Assert.True(vm.ShowProblem3D);

        vm.Solver3DChoice = thermal;
        Assert.Equal(Em3dProblemType.Thermal, vm.Working.Problem3D);
        Assert.Equal(Em3dSolverChoice.None, vm.Working.Solver3D);
        Assert.NotNull(vm.Working.Thermal);
        Assert.True(vm.IsThermalSetup);
        Assert.False(vm.ShowProblem3D);                   // no Problem picker for a thermal setup
        Assert.False(vm.IsPalaceSetup);

        vm.Solver3DChoice = vm.Solver3DChoiceList.First(c => c.Value == Em3dSolverId.OpenEms);
        Assert.Equal(Em3dProblemType.Driven, vm.Working.Problem3D);
        Assert.Equal(Em3dSolverId.OpenEms, vm.Working.Solver3D);
        Assert.True(vm.ShowProblem3D);

        // A thermal setup opens on the Thermal row; a .cem offers none (R-em3d75-3 D1).
        var opened = new EmSetupEditorViewModel("model.c3d",
            new EmSetup { Name = "Hot", Solver3D = Em3dSolverChoice.None, Problem3D = Em3dProblemType.Thermal, Thermal = new CemThermal() }, embedded: true);
        Assert.Same(EmSetupEditorViewModel.ThermalSolverChoice, opened.Solver3DChoice);
        Assert.Contains("thermal solver", opened.Solver3DDescription, StringComparison.Ordinal);
        var cem = new EmSetupEditorViewModel("panel.cem", new EmSetup { Name = "panel" });
        Assert.DoesNotContain(cem.Solver3DChoiceList, c => c.IsThermal);
    }

    [Fact]
    public void SetupCard_IsFoundByItsListItem_SoASolverNoteLineOpensItToo()
    {
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/ThreeD/C3dSetupAnalysesView.axaml.cs"));
        Assert.Contains("FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as C3dSetupItem", code, StringComparison.Ordinal);
        Assert.DoesNotContain("source.DataContext is not C3dSetupItem", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MeshToggle_IsATool_EnabledByAMesh_AndTheStaleSentenceIsTheInspectors()
    {
        using var viewer = new Viewer3DViewModel("model.c3d", "model", () => null, (_, _, _) => throw new InvalidOperationException(),
                                                 () => new RecordingBackend(), () => null, a => a());
        var raised = new List<string?>();
        viewer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(viewer.CanShowMesh);
        Assert.StartsWith("No mesh yet", viewer.MeshTip, StringComparison.Ordinal);
        viewer.MeshAvailable = true;
        Assert.False(viewer.FieldsAvailable);
        Assert.True(viewer.CanShowMesh);
        Assert.Contains(nameof(Viewer3DViewModel.CanShowMesh), raised);

        string root = RepoRoot();
        string editor = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(root, "src/Ui/Views/ThreeD/C3dEditorView.axaml")), "<!--.*?-->", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(editor, "Viewer.ShowMesh"));
        Assert.Contains("IsEnabled=\"{Binding ViewModel.Viewer.CanShowMesh}\"", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("FieldsStaleText", editor, StringComparison.Ordinal);
        Assert.Contains("{Binding PlotStale}", File.ReadAllText(Path.Combine(root, "src/Ui/Views/ThreeD/C3dPropertiesView.axaml")), StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
