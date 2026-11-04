using CircuitRF.Design.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// brief-sdl-9 D2: MGAP's artwork is its two pins and no copper — pin 1 at the origin facing −X, pin 2 at
/// <c>(S, 0)</c> facing +X, each <c>W</c> wide — so the lines placed on them end exactly <c>S</c> apart.
/// </summary>
public class MGapPCellTests
{
    [Fact]
    public void Gap_IsTwoOutwardPinsSApart_AndNoCopper()
    {
        Assert.True(PCellRegistry.TryGet(MGapPCell.GeneratorId, out var generate));
        var art = generate(PCellParameters.FromReals(new Dictionary<string, double> { ["W"] = 1.1e-3, ["S"] = 0.2e-3 }), null, PCellLayerSelection.Default);

        Assert.Empty(art.Shapes);
        Assert.Collection(art.Pins,
            p => { Assert.Equal(("1", 0L, 0L, 1_100_000L, 180.0), (p.Name, p.X, p.Y, p.WidthDbu, p.OutwardDirectionDeg)); },
            p => { Assert.Equal(("2", 200_000L, 0L, 1_100_000L, 0.0), (p.Name, p.X, p.Y, p.WidthDbu, p.OutwardDirectionDeg)); });
        Assert.Null(art.Diagnostics);
    }

    [Fact]
    public void ZeroGap_IsReported()
    {
        Assert.True(PCellRegistry.TryGet(MGapPCell.GeneratorId, out var generate));
        var art = generate(PCellParameters.FromReals(new Dictionary<string, double> { ["W"] = 1.1e-3, ["S"] = 0.0 }), null, PCellLayerSelection.Default);
        Assert.Contains(art.Diagnostics!, d => d.Contains("S must be greater than zero", StringComparison.Ordinal));
    }
}
