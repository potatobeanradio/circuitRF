using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class LayoutUnitsTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron; // 1000

    // ── Gate 2: unit exactness ────────────────────────────────────────────────

    [Fact]
    public void ToDbu_OneMil_Is25400()
        => Assert.Equal(25_400L, LayoutUnits.ToDbu(1m, LayoutUnit.Mil, Dbu));

    [Fact]
    public void ToDbu_OneMicron_Is1000()
        => Assert.Equal(1_000L, LayoutUnits.ToDbu(1m, LayoutUnit.Um, Dbu));

    [Fact]
    public void ToDbu_OneMillimeter_Is1000000()
        => Assert.Equal(1_000_000L, LayoutUnits.ToDbu(1m, LayoutUnit.Mm, Dbu));

    [Fact]
    public void ToDbu_OneInch_Is25400000()
        => Assert.Equal(25_400_000L, LayoutUnits.ToDbu(1m, LayoutUnit.Inch, Dbu));

    [Fact]
    public void ToDbu_ThenFromDbu_RoundTripsExactly()
    {
        foreach (var unit in new[] { LayoutUnit.Nm, LayoutUnit.Um, LayoutUnit.Mm, LayoutUnit.Mil, LayoutUnit.Inch })
        {
            var dbu = LayoutUnits.ToDbu(3.0m, unit, Dbu);
            Assert.Equal(3.0m, LayoutUnits.FromDbu(dbu, unit, Dbu));
        }
    }

    // ── TryParse ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_2p9mm_Exact()
    {
        Assert.True(LayoutUnits.TryParse("2.9mm", LayoutUnit.Um, Dbu, out var dbu));
        Assert.Equal(LayoutUnits.ToDbu(2.9m, LayoutUnit.Mm, Dbu), dbu);
    }

    [Fact]
    public void TryParse_115Mil_Exact()
    {
        Assert.True(LayoutUnits.TryParse("115 mil", LayoutUnit.Um, Dbu, out var dbu));
        Assert.Equal(LayoutUnits.ToDbu(115m, LayoutUnit.Mil, Dbu), dbu);
    }

    [Fact]
    public void TryParse_50u_Exact()
    {
        Assert.True(LayoutUnits.TryParse("50u", LayoutUnit.Mil, Dbu, out var dbu));
        Assert.Equal(LayoutUnits.ToDbu(50m, LayoutUnit.Um, Dbu), dbu);
    }

    [Fact]
    public void TryParse_1e3nm_Exact()
    {
        Assert.True(LayoutUnits.TryParse("1e3nm", LayoutUnit.Um, Dbu, out var dbu));
        Assert.Equal(LayoutUnits.ToDbu(1000m, LayoutUnit.Nm, Dbu), dbu);
    }

    [Fact]
    public void TryParse_NegativeHalfMm_Exact()
    {
        Assert.True(LayoutUnits.TryParse("-0.5mm", LayoutUnit.Um, Dbu, out var dbu));
        Assert.Equal(LayoutUnits.ToDbu(-0.5m, LayoutUnit.Mm, Dbu), dbu);
    }

    [Fact]
    public void TryParse_BareNumber_UsesFallbackUnit()
    {
        Assert.True(LayoutUnits.TryParse("42", LayoutUnit.Mil, Dbu, out var dbu));
        Assert.Equal(LayoutUnits.ToDbu(42m, LayoutUnit.Mil, Dbu), dbu);
    }

    [Fact]
    public void TryParse_UnknownSuffix_Rejected()
    {
        Assert.False(LayoutUnits.TryParse("2.9 furlongs", LayoutUnit.Um, Dbu, out _));
    }

    [Fact]
    public void TryParse_EmptyOrWhitespace_Rejected()
    {
        Assert.False(LayoutUnits.TryParse("", LayoutUnit.Um, Dbu, out _));
        Assert.False(LayoutUnits.TryParse("   ", LayoutUnit.Um, Dbu, out _));
    }

    [Fact]
    public void Format_TrimsTrailingZeros()
    {
        var dbu = LayoutUnits.ToDbu(2.9m, LayoutUnit.Mm, Dbu);
        Assert.Equal("2.9", LayoutUnits.Format(dbu, LayoutUnit.Mm, Dbu));
    }
}
