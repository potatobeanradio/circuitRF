// Owner report, 2026-09-25: a layout with two ports, and the 3D setup's port table listed one. The
// table was built from the .cem's Ports3D entries alone, and a lumped port at its defaults is written
// as nothing — so a port nobody had changed had no row. The rows are now the layout's own port labels
// merged with any Ports3D entry, and each carries the port's reference impedance, because for a 3D
// setup this table is the only place ports are configured (the Ports group is hidden).

using System.Linq;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class Em3dPortTableTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static LabelShape Port(string text, double xMm, double yMm, LayoutRotation dir) =>
        new()
        {
            Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = text,
            Height = Mm(0.5), IsPort = true, PortDirection = dir,
        };

    /// <summary>A tee, so Auto lands on the planar kernel, whose ports come from the layout's labels.</summary>
    private static EmSetupEditorViewModel PalacePanelOverTwoPorts(params EmPort3D[] ports3D)
    {
        var view = new LayoutView
        {
            DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um,
            SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
        };
        view.Shapes.Add(new PolygonShape
        {
            Layer = TopCopper,
            Xy =
            [
                Mm(9), Mm(0),  Mm(20), Mm(0),  Mm(20), Mm(2.9),  Mm(0), Mm(2.9),
                Mm(0), Mm(0),  Mm(6),  Mm(0),  Mm(6),  Mm(-12),  Mm(9), Mm(-12),
            ],
        });
        view.Shapes.Add(Port("P1", 0,  1.45, LayoutRotation.R0));
        view.Shapes.Add(Port("P2", 20, 1.45, LayoutRotation.R180));

        const string clay = "/tmp/em3d-port-table.clay";
        var setup = new EmSetup
        {
            LayoutRef = clay, AnalysisKind = EmAnalysisKind.Auto,
            Solver3D = Em3dSolver.Palace, Ports3D = [.. ports3D],
        };
        var vm = new EmSetupEditorViewModel(clay + ".cem", setup)
        {
            ResolveLayout = _ => new EmLayoutSource(clay, view, StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    [Fact]
    public void EveryLayoutPort_HasARow_AndOnlyALeftoverEntryCanBeRemoved()
    {
        var vm = PalacePanelOverTwoPorts(new EmPort3D(1, Em3dPortKind.Wave), new EmPort3D(5, Em3dPortKind.Wave));

        Assert.True(vm.IsPortKindSetup);
        Assert.Equal(["1", "2", "5"], vm.Port3DRows.Select(r => r.Port));
        Assert.Equal([Em3dPortKind.Wave, Em3dPortKind.Lumped, Em3dPortKind.Wave], vm.Port3DRows.Select(r => r.Kind));
        Assert.Equal([true, true, false], vm.Port3DRows.Select(r => r.InLayout));
    }

    [Fact]
    public void ARowsImpedance_WritesThePortsZ0Slot_AndTheDefaultRowIsNotWritten()
    {
        var vm = PalacePanelOverTwoPorts();
        var row2 = vm.Port3DRows.Single(r => r.Port == "2");
        Assert.Equal("50", row2.Z0Text);

        row2.Z0Text = "25";
        vm.CommitPort3DZ0(row2);

        Assert.Null(row2.Z0Error);
        Assert.Equal(new Complex(25, 0), vm.Working.ResolvePortZ0(1));
        Assert.Equal("25", vm.Port3DRows.Single(r => r.Port == "2").Z0Text);   // survives the rebuild
        Assert.Empty(vm.Working.Ports3D);                                      // lumped at defaults: nothing
    }
}
