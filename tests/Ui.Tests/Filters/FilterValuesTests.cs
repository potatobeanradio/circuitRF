using CircuitRF.Design.Filters;
using CircuitRF.Design.Matching;
using CircuitRF.Design.VendorLibraries;
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

    [Fact]
    public void ToleranceOf_ReadsTolHeaderPercentAndEiaSlashField()
    {
        var fromTol = VendorLibraryHeader.Parse(
            ["* Property : C = 10[pF]", "* Tolerance : ±5%"], out _);
        Assert.Contains("5", FilterValues.ToleranceOf(fromTol));

        var fromEia = VendorLibraryHeader.Parse(
            ["* Description : 1005M(0402)/C0G/10[pF]/J/50[V]", "* Property : C = 10[pF]"], out _);
        Assert.Contains("5", FilterValues.ToleranceOf(fromEia));

        var fromDesc = VendorLibraryHeader.Parse(
            ["* Description : 0402/X7R/10nF/±10%/16V", "* Property : C = 10[nF]"], out _);
        Assert.Contains("10", FilterValues.ToleranceOf(fromDesc));

        var none = VendorLibraryHeader.Parse(
            ["* Description : 1005M(0402)/C0G/10[pF]/50[V]", "* Property : C = 10[pF]"], out _);
        Assert.Equal("", FilterValues.ToleranceOf(none));
    }

    [Fact]
    public void ToleranceOf_ReadsEiaLetterAfterValueCodeInPartName()
    {
        // Static SPICE editions put no ±% in the header; the EIA letter sits after the value code.
        var cap = VendorLibraryHeader.Parse(
            ["* Description : 0402M(01005)/C0G/3.00e-06[uF]/16[V]",
             "* Property : C = 3.00e-06[uF]"], out _);
        string capTol = FilterValues.ToleranceOf(cap, "TSTCxx3R0B01");
        Assert.Contains("0.1", capTol);
        Assert.Contains("pF", capTol);

        var ind = VendorLibraryHeader.Parse(
            ["* Property : L = 4.3[nH]"], out _);
        string indTol = FilterValues.ToleranceOf(ind, "TSTLxx4N3D00");
        Assert.Contains("0.5", indTol);
        Assert.Contains("nH", indTol);

        var pct = VendorLibraryHeader.Parse(
            ["* Property : C = 100[pF]"], out _);
        Assert.Contains("5", FilterValues.ToleranceOf(pct, "TSTCxx101JA01"));

        // Integer nH uses 15N (not only 15N0); sub-pF uses leading R80.
        var ind15 = VendorLibraryHeader.Parse(["* Property : L = 15[nH]"], out _);
        Assert.Contains("5", FilterValues.ToleranceOf(ind15, "TSTLxx15NJ1Z"));

        var capSub = VendorLibraryHeader.Parse(["* Property : C = 0.8[pF]"], out _);
        Assert.Contains("0.1", FilterValues.ToleranceOf(capSub, "TSTCxxR80BB01"));
    }

    [Fact]
    public void IecSeriesTolerance_IsTheConventionalPercent()
    {
        Assert.Contains("10", FilterValues.ToleranceOf(FilterSeriesKind.E12));
        Assert.Equal("", FilterValues.ToleranceOf(FilterSeriesKind.Exact));
    }
}
