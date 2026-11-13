using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Matching;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

public class FilterLadderDrawingTests
{
    [Fact]
    public void AnEllipticLowpassStubSharesOneColumn()
    {
        var built = FilterLadder.Synthesize(
            FilterResponse.Elliptic, NetworkForm.Lowpass, 3,
            1e9, 0, 0, 0.1, 50, FilterFirstElement.Series, 40);
        Assert.True(built.Ok, built.Refusal?.Message);

        var layout = FilterLadderDrawing.Build(
            built.Realization!.Elements, 50, 50,
            (ind, v) => ind ? $"L={v}" : $"C={v}");

        // Series L, stub (L+C stacked), series L → three columns, four placed symbols.
        Assert.Equal(4, layout.Elements.Count);
        var stub = layout.Elements.Where(e => e.IsShunt).ToList();
        Assert.Equal(2, stub.Count);
        Assert.Equal(stub[0].X, stub[1].X);
        Assert.True(stub[1].Y > stub[0].Y);
    }

    [Fact]
    public void AParallelTankSharesOneSeriesColumn()
    {
        var built = FilterLadder.Synthesize(
            FilterResponse.Elliptic, NetworkForm.Lowpass, 5,
            1e9, 0, 0, 0.1, 50, FilterFirstElement.Parallel, 40);
        Assert.True(built.Ok, built.Refusal?.Message);

        var layout = FilterLadderDrawing.Build(
            built.Realization!.Elements, 50, 50,
            (ind, v) => ind ? $"L={v}" : $"C={v}");

        var series = layout.Elements.Where(e => !e.IsShunt).GroupBy(e => e.X).ToList();
        Assert.Contains(series, g => g.Count() == 2); // L∥C in one column
    }
}
