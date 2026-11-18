// PWL-3 — a layout's 3D setup takes Ports3D Kind Wall: the generator builds the ports as the wave ports they are drawn as (their
// face, its zero padding, the line), and each is turned into the wall port it stands for in the solved problem. No solver runs.

using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class LayoutWallPortTests
{
    private const int Dbu = 1000;
    private static readonly LayerKey L1 = new(1, 0), L2 = new(2, 0);
    private static long Mm(double v) => (long)Math.Round(v * 1000 * Dbu);

    private static Technology Tech()
    {
        var t = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        foreach (var l in t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor))
            l.IsGroundReference = l.Name == "Inner 1 (Ground Plane)";
        return t;
    }

    private static LabelShape Port(string text, double xMm, LayoutRotation direction) => new()
    {
        Layer = L1, PortLayer = L1, X = Mm(xMm), Y = 0, Text = text, Height = Mm(0.2), IsPort = true,
        PortKind = PlanarPortKind.Edge, PortDirection = direction,
    };

    /// <summary>A 0.3 mm top-copper line over the inner ground plane, both 10 mm long and flush at the ends, ports at each end.</summary>
    private static Em3dGenerationResult Generate(Action<EmSetup> edit)
    {
        var tech = Tech();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(
        [
            new RectShape { Layer = L1, X1 = 0, Y1 = Mm(-0.15), X2 = Mm(10), Y2 = Mm(0.15) },
            new RectShape { Layer = L2, X1 = 0, Y1 = Mm(-3), X2 = Mm(10), Y2 = Mm(3) },
            Port("P1", 0, LayoutRotation.R0),
            Port("P2", 10, LayoutRotation.R180),
        ]);
        var setup = new EmSetup
        {
            Name = "wall", LayoutRef = "w/layout/w.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
        };
        edit(setup);
        return Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/w.clay", view, tech, Dbu), tech);
    }

    [Fact]
    public void ALayoutsPorts3DWall_AreWallPorts_OnPecFaces_WithTheLineCutBack()
    {
        var g = Generate(s => s.Ports3D = [new EmPort3D(1, Em3dPortKind.Wall), new EmPort3D(2, Em3dPortKind.Wall)]);
        Assert.True(g.Ok, g.Refusal);
        var p = g.Problem!;
        Assert.Equal(Em3dBoundaryKind.Pec, p.Boundary.Faces.XMin);
        Assert.Equal(Em3dBoundaryKind.Pec, p.Boundary.Faces.XMax);
        foreach (var port in p.Ports)
        {
            Assert.Equal(Em3dPortKind.Lumped, port.Kind);
            var w = Assert.IsType<Em3dWallPort>(port.Wall);
            Assert.Equal(300e-6, w.WidthM, 9);
            Assert.True(w.HeightM > 0);
            Assert.Equal(0.2 * Math.Min(w.WidthM, w.HeightM), w.GapM, 12);
            Assert.StartsWith("airbox/x", port.NegativeObject);
        }
        // The line starts one gap in from the xmin face.
        var w1 = p.Ports.Single(q => q.Number == 1).Wall!;
        double lineStart = p.Sheets.Where(s => s.Name == w1.Conductor).Select(s => s.Outline.Min(q => q.X))
                           .Concat(p.Solids.Where(s => s.Name == w1.Conductor).Select(s => Em3dProblem.Bounds(s.Primitive).X0)).Single();
        Assert.Equal(p.Boundary.Min.X + w1.GapM, lineStart, 9);
        Assert.Contains(g.Notes, n => n.Contains("calibrated out by line standards", StringComparison.Ordinal));
    }

    [Fact]
    public void ALayoutsWallPortOnEMerge_IsRefusedNamingPalace()
    {
        var g = Generate(s =>
        {
            s.Solver3D = Em3dSolverId.Emerge;
            s.Ports3D = [new EmPort3D(1, Em3dPortKind.Wall), new EmPort3D(2, Em3dPortKind.Wall)];
        });
        Assert.False(g.Ok);
        Assert.Contains("run on Palace in this version", g.Refusal);
    }
}
