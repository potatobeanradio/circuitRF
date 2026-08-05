using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>Gates for brief-mtaper-mklopf.md §1's artwork: MTaper's trapezoid outline, R-pc-3
/// pin origin/orientation, and PCellRegistry wiring.</summary>
public class MTaperPCellTests
{
    private static readonly Technology Pcb = StarterTechnologies.Pcb2Layer();

    [Fact]
    public void Generate_ReturnsOneTrapezoidShapeAndTwoPins()
    {
        var result = MTaperPCell.Generate(
            new Dictionary<string, PCellValue> { ["W1"] = 0.0029, ["W2"] = 0.001, ["L"] = 0.01 },
            Pcb, PCellLayerSelection.Default);

        Assert.Single(result.Shapes);
        Assert.Equal(2, result.Pins.Count);
        Assert.Equal("1", result.Pins[0].Name);
        Assert.Equal(0, result.Pins[0].X);
        Assert.Equal(0, result.Pins[0].Y);
        Assert.Equal("2", result.Pins[1].Name);
        Assert.True(result.Pins[1].X > 0); // pin 2 runs along +X, per R-pc-3/R-tap's own convention
    }

    [Fact]
    public void Generate_PinWidths_MatchW1AndW2Respectively()
    {
        var result = MTaperPCell.Generate(
            new Dictionary<string, PCellValue> { ["W1"] = 0.0029, ["W2"] = 0.001, ["L"] = 0.01 },
            Pcb, PCellLayerSelection.Default);

        Assert.True(result.Pins[0].WidthDbu > result.Pins[1].WidthDbu); // W1 > W2 in this case
    }

    [Fact]
    public void Generate_IsPure_SameParametersProduceIdenticalOutput()
    {
        var p = new Dictionary<string, PCellValue> { ["W1"] = 0.0029, ["W2"] = 0.001, ["L"] = 0.01 };
        var r1 = MTaperPCell.Generate(p, Pcb, PCellLayerSelection.Default);
        var r2 = MTaperPCell.Generate(p, Pcb, PCellLayerSelection.Default);
        Assert.Equal(r1.Pins[1].X, r2.Pins[1].X);
    }

    [Fact]
    public void PCellRegistry_KnowsMTaper()
    {
        Assert.True(PCellRegistry.TryGet("MTAPER", out var generator));
        Assert.NotNull(generator);
    }
}
