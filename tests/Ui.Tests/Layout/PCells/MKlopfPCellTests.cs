using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>Gates for brief-mtaper-mklopf.md §2-3's artwork: the Klopfenstein-taper outline
/// (straight and offset), R-klp-8's perpendicular width, R-klp-4a's SmoothSteps blend, and
/// PCellRegistry wiring.</summary>
public class MKlopfPCellTests
{
    private static readonly Technology Pcb = StarterTechnologies.Pcb2Layer();

    private static Dictionary<string, double> BaseParams(double offset = 0.0, double smoothSteps = 1.0) =>
        new()
        {
            ["Z1"] = 50.0,
            ["Z2"] = 100.0,
            ["GammaMax"] = 0.05,
            ["L"] = 0.02,
            ["Offset"] = offset,
            ["SmoothSteps"] = smoothSteps,
        };

    [Fact]
    public void Generate_ReturnsOneOutlineShapeAndTwoPins()
    {
        var result = MKlopfPCell.Generate(BaseParams(), Pcb, PCellLayerSelection.Default);

        Assert.Single(result.Shapes);
        Assert.Equal(2, result.Pins.Count);
        Assert.Equal("1", result.Pins[0].Name);
        Assert.Equal(0, result.Pins[0].X);
        Assert.Equal(0, result.Pins[0].Y);
        Assert.Equal("2", result.Pins[1].Name);
        Assert.True(result.Pins[1].X > 0);
    }

    [Fact]
    public void Generate_StraightCase_Pin2Y_IsZero()
    {
        var result = MKlopfPCell.Generate(BaseParams(offset: 0.0), Pcb, PCellLayerSelection.Default);
        Assert.Equal(0, result.Pins[1].Y);
    }

    [Fact]
    public void Generate_OffsetCase_Pin2Y_MatchesTheOffset()
    {
        double offset = 3e-3;
        var result = MKlopfPCell.Generate(BaseParams(offset: offset), Pcb, PCellLayerSelection.Default);
        long expected = PCellUnits.MetresToDbu(offset, LayoutUnits.DefaultDbuPerMicron);
        Assert.Equal(expected, result.Pins[1].Y);
    }

    [Fact]
    public void Generate_PinWidths_LowerImpedanceIsWider()
    {
        // Z1=50 (wider) -> Z2=100 (narrower)
        var result = MKlopfPCell.Generate(BaseParams(), Pcb, PCellLayerSelection.Default);
        Assert.True(result.Pins[0].WidthDbu > result.Pins[1].WidthDbu);
    }

    [Fact]
    public void Generate_IsPure_SameParametersProduceIdenticalOutput()
    {
        var p = BaseParams();
        var r1 = MKlopfPCell.Generate(p, Pcb, PCellLayerSelection.Default);
        var r2 = MKlopfPCell.Generate(p, Pcb, PCellLayerSelection.Default);
        Assert.Equal(r1.Pins[1].X, r2.Pins[1].X);
        Assert.Equal(((PolygonShape)r1.Shapes[0]).Xy, ((PolygonShape)r2.Shapes[0]).Xy);
    }

    [Fact]
    public void Generate_SmoothStepsOnOrOff_BothProduceAValidOutline()
    {
        var withBlend = MKlopfPCell.Generate(BaseParams(smoothSteps: 1.0), Pcb, PCellLayerSelection.Default);
        var withoutBlend = MKlopfPCell.Generate(BaseParams(smoothSteps: 0.0), Pcb, PCellLayerSelection.Default);

        var poly1 = (PolygonShape)withBlend.Shapes[0];
        var poly2 = (PolygonShape)withoutBlend.Shapes[0];
        Assert.True(poly1.Xy.Length > 0);
        Assert.True(poly2.Xy.Length > 0);
        // The blend changes near-endpoint widths, so the two outlines should differ somewhere.
        Assert.NotEqual(poly1.Xy, poly2.Xy);
    }

    [Fact]
    public void Generate_NoTechnology_StillGeneratesGeometry()
    {
        var result = MKlopfPCell.Generate(BaseParams(), technology: null, PCellLayerSelection.Default);
        Assert.Single(result.Shapes);
        Assert.Equal(2, result.Pins.Count);
    }

    [Fact]
    public void PCellRegistry_KnowsMKlopf()
    {
        Assert.True(PCellRegistry.TryGet("MKLOPF", out var generator));
        Assert.NotNull(generator);
    }
}
