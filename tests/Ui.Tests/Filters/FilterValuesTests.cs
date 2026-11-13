using CircuitRF.Design.Filters;
using CircuitRF.Design.Matching;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>The snap the Filter Designer and <c>circuitrf filter --series</c> share.</summary>
public class FilterValuesTests
{
    [Fact]
    public void E12SnapsByRatioNotByDifference()
    {
        // 1.09 pF is closer to 1.0 than to 1.2 on a ratio, and a linear nearest would say otherwise
        // only in a decade where the gap is small. The rule is PreferredValues' own.
        double snapped = FilterValues.Snap(inductor: false, exact: 1.09e-12, FilterSeriesKind.E12);
        Assert.Equal(1.0e-12, snapped, 15);
    }

    [Fact]
    public void ExactLeavesTheValueAlone()
    {
        Assert.Equal(1.234e-9, FilterValues.Snap(true, 1.234e-9, FilterSeriesKind.Exact));
    }

    [Fact]
    public void TheNearestPartIsTheSmallerRatio()
    {
        var parts = new[]
        {
            new FilterPart("a", 1.0e-9),
            new FilterPart("b", 1.5e-9),
            new FilterPart("c", 2.2e-9),
        };
        var chosen = FilterValues.Nearest(1.4e-9, parts);
        Assert.NotNull(chosen);
        Assert.Equal("b", chosen!.Name);
        // And the IEC ladder the snap uses is the one PreferredValues publishes.
        Assert.Contains(1.2, PreferredValues.E12);
    }
}
