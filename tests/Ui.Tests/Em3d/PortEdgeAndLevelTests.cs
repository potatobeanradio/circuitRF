using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// Designer feedback round 38, on the shipped four-layer board (Top, Inner 1 ground, Inner 2, Bottom marked as ground):
// a stepped end's ports keep their own edges and its copper, two ports on one sheet are refused by number, and a port
// committed to a trace on a layer marked as ground drives that trace.
public sealed class PortEdgeAndLevelTests
{
    private const int Dbu = 1000;
    private static readonly LayerKey Top = new(1, 0), Inner1 = new(2, 0), Inner2 = new(3, 0), Bottom = new(4, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static LabelShape Port(string text, LayerKey layer, double x, double y, LayoutRotation direction, double? widthUm = null) => new()
    {
        Layer = layer, PortLayer = layer, X = Um(x), Y = Um(y), Text = text, Height = Um(200), IsPort = true,
        PortKind = PlanarPortKind.Edge, PortDirection = direction, PortWidth = widthUm is { } w ? Um(w) : null,
    };

    private static RectShape Box(LayerKey layer, double x1, double y1, double x2, double y2) => new()
    {
        Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2),
    };

    private static Em3dGenerationResult Generate(IEnumerable<LayoutShape> shapes, bool portsEach = false)
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        var face = new EmAirBoxFace(1000, Em3dBoundaryKind.Absorbing);
        var setup = new EmSetup
        {
            Name = "pl", LayoutRef = "pl/layout/pl.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            Palace = new CemPalace { Quality = PalaceQuality.Draft },
            AirBox = new EmAirBox(face, face, face, face, face, face),
        };
        return Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/pl.clay", view, tech, Dbu), tech, portsEach: portsEach);
    }

    /// <summary>A pad end stepped at x = 100, 200 and 45 µm, as two component lands joined by a neck.</summary>
    private static PolygonShape SteppedEnd() => new()
    {
        Layer = Top,
        Xy = [Um(100), 0, Um(2000), 0, Um(2000), Um(1520), Um(45), Um(1520), Um(45), Um(960), Um(200), Um(960), Um(200), Um(620), Um(100), Um(620)],
    };

    [Fact]
    public void PortsOnASteppedEnd_DriveTheirOwnEdges_AndCutNoCopper()
    {
        // Port 1 at the neck's corner, which is what used to take the whole 1520 µm and cut port 2's land away.
        var g = Generate([Box(Inner1, -500, -500, 2500, 2000), SteppedEnd(),
                          Port("1", Top, 200, 960, LayoutRotation.R0), Port("2", Top, 100, 310, LayoutRotation.R0)]);
        Assert.True(g.Ok, g.Refusal);
        Assert.DoesNotContain(g.Notes, n => n.Contains("curved edge", StringComparison.Ordinal));
        var one = g.Problem!.Ports.Single(p => p.Number == 1);
        var two = g.Problem.Ports.Single(p => p.Number == 2);
        Assert.Equal(340e-6, one.Max.Y - one.Min.Y, 9);
        Assert.Equal(200e-6, one.Min.X, 9);
        Assert.Equal(620e-6, two.Max.Y - two.Min.Y, 9);
        Assert.Equal(100e-6, two.Min.X, 9);
    }

    [Fact]
    public void TwoPortsOnOneEdge_AreRefusedByNumber_UntilTheirWidthsKeepThemApart()
    {
        List<LayoutShape> Shapes(double? width) =>
        [
            Box(Inner1, -500, -500, 2500, 1500), Box(Top, 0, 0, 2000, 1000),
            Port("1", Top, 0, 250, LayoutRotation.R0, width), Port("2", Top, 0, 750, LayoutRotation.R0, width),
        ];

        var refused = Generate(Shapes(null));
        Assert.False(refused.Ok);
        Assert.Contains("Ports 1 and 2 lie on one sheet", refused.Refusal);

        // Brought into a 3D view, the second is left out alone and the first is still built.
        var each = Generate(Shapes(null), portsEach: true);
        Assert.Equal(1, Assert.Single(each.Problem!.Ports).Number);
        Assert.Equal(2, Assert.Single(each.PortsLeftOut).Port);

        Assert.True(Generate(Shapes(300)).Ok);
    }

    /// <summary>A rounded board corner read from a Gerber outline, points 0.5 µm apart and bending by far less than the
    /// tolerance: the laminate's side has to lose them, since four on one face made a flat element.</summary>
    [Fact]
    public void RobustMeshing_CleansTheBoardOutline()
    {
        var corner = new List<long> { Um(-500), Um(-500), Um(2500), Um(-500), Um(2500), Um(1000) };
        for (int i = 0; i <= 40; i++) corner.AddRange([Um(1000 - i * 0.5), Um(1000 + 1e-5 * i * i)]);
        corner.AddRange([Um(-500), Um(1000)]);
        var outline = new PolygonShape { Layer = new LayerKey(11, 0), Xy = [.. corner] };
        var g = Generate([outline, Box(Inner1, -500, -500, 2500, 1000), Box(Top, 0, 0, 2000, 400),
                          Port("1", Top, 2000, 200, LayoutRotation.R180)]);
        Assert.True(g.Ok, g.Refusal);
        var laminate = (Em3dExtrudedPolygon)g.Problem!.Solids.First(s => s.Role == Em3dRole.Dielectric).Primitive;
        Assert.InRange(laminate.Outline.Count, 4, 8);
    }

    [Fact]
    public void APortOnATraceInABottomPour_DrivesIt_AndReturnsThroughTheMetalAbove()
    {
        var g = Generate([Box(Inner2, -500, -500, 5500, 4500), Box(Bottom, 0, 2000, 5000, 4000), Box(Bottom, 0, 0, 2000, 400),
                          Port("1", Bottom, 2000, 200, LayoutRotation.R180)]);
        Assert.True(g.Ok, g.Refusal);
        var port = Assert.Single(g.Problem!.Ports);
        Assert.StartsWith("Bottom Copper", port.PositiveObject, StringComparison.Ordinal);
        Assert.StartsWith("Inner 2", port.NegativeObject, StringComparison.Ordinal);
        Assert.Equal(400e-6, port.Max.Y - port.Min.Y, 9);
    }

    [Fact]
    public void APortOnThePourItself_IsRefused_NamingTheLayerItIsOn()
    {
        var g = Generate([Box(Top, -500, -500, 5500, 4500), Box(Inner2, -500, -500, 5500, 4500),
                          Box(Bottom, 0, 2000, 5000, 4000), Box(Bottom, 0, 0, 2000, 400),
                          Port("1", Bottom, 5000, 3000, LayoutRotation.R180)]);
        Assert.False(g.Ok);
        Assert.Contains("committed to 'Bottom Copper", g.Refusal);
        Assert.DoesNotContain("sits on metal", g.Refusal);
    }
}
