// Owner report, 2026-08-09: "I pressed Mesh button for my EM Setup (full wave) but nothing happened
// and no messages were displayed."
//
// The header Mesh button was bound to BuildMeshCommand — the CROSS-SECTION mesher — whose second line
// is `if (Problem is null) return;`. On a full-wave setup Problem is null by construction (the planar
// problem lives in PlanarProblem), so the most prominent button in the editor returned silently.
// Meanwhile the planar mesher sat on a SECOND button, also labelled "Mesh", inside the Surface mesh
// group: two identical labels, one of them inert in the mode the user was in.
//
// The header button is now BuildActiveMeshCommand, which refreshes and then dispatches to whichever
// kernel the registry actually chose.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmMeshButtonDispatchTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>§10.7's own hero footprint: a 2.9 × 20 mm run on the PCB starter's Top Copper.</summary>
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

    private static EmSetupEditorViewModel Vm(EmAnalysisKind kind, out Technology tech)
    {
        tech = StarterTechnologies.Pcb2Layer();
        var view = LineLayout(tech);
        var t = tech;
        return new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-meshbtn.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = kind })
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(Path.GetTempPath(), "a.clay"), view, t, Dbu),
        };
    }

    [Fact]
    public void OnAFullWaveSetup_TheHeaderMeshButton_ProducesASurfaceMesh()
    {
        var vm = Vm(EmAnalysisKind.Planar, out _);
        vm.Refresh();
        Assert.True(vm.IsPlanarAnalysis, "fixture must actually select the full-wave kernel");
        Assert.Null(vm.PlanarMeshReport);

        vm.BuildActiveMeshCommand.Execute(null);

        Assert.NotNull(vm.PlanarMeshReport);
        Assert.NotEmpty(vm.PlanarMeshNotes);
    }

    [Fact]
    public void TheOldBinding_WouldHaveDoneNothing_WhichIsWhyItReadAsADeadButton()
    {
        // The negative control, and the whole point of the fix: BuildMesh alone is the cross-section
        // mesher, and on a full-wave setup it has no problem to mesh. Without this the test above
        // could pass against a header button that simply called both.
        var vm = Vm(EmAnalysisKind.Planar, out _);
        vm.Refresh();

        vm.BuildMeshCommand.Execute(null);

        Assert.Null(vm.MeshReport);
        Assert.Null(vm.PlanarMeshReport);
        // …but it is no longer SILENT about it.
        Assert.NotEmpty(vm.MeshNotes);
    }

    [Fact]
    public void OnACrossSectionSetup_TheHeaderMeshButton_StillProducesTheCrossSectionMesh()
    {
        var vm = Vm(EmAnalysisKind.CrossSection, out _);
        vm.Refresh();
        Assert.False(vm.IsPlanarAnalysis);

        vm.BuildActiveMeshCommand.Execute(null);

        Assert.NotNull(vm.MeshReport);
        Assert.Null(vm.PlanarMeshReport);
    }
}
