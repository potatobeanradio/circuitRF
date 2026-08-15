// ================================================================
//  HarmonicaTitlesTests.cs — R-h9b-4 of brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using CircuitRF.Harmonica;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

public sealed class HarmonicaTitlesTests
{
    [Theory]
    [InlineData(3.0, "P-3dB")]
    [InlineData(2.5, "P-2.5dB")]
    [InlineData(1.0, "P-1dB")]
    public void CompressionLabel_DropsTrailingZeros(double db, string expected)
        => Assert.Equal(expected, HarmonicaTitles.CompressionLabel(db));

    [Fact]
    public void MetricRow_Power_ReadsPowerDbm()
        => Assert.Equal("P-3dB Power (dBm)",
            HarmonicaTitles.MetricRow(isPowerChart: true, GridMetric.DrainEfficiency, 3.0));

    [Fact]
    public void MetricRow_Efficiency_ReadsEfficiencyPercent_ByDefault()
        => Assert.Equal("P-3dB Efficiency (%)",
            HarmonicaTitles.MetricRow(isPowerChart: false, GridMetric.DrainEfficiency, 3.0));

    [Fact]
    public void MetricRow_Pae_ReadsPaePercent()
        => Assert.Equal("P-3dB PAE (%)",
            HarmonicaTitles.MetricRow(isPowerChart: false, GridMetric.Pae, 3.0));

    [Fact]
    public void PlaneRow_Band1_ReadsFundamental()
        => Assert.Equal("Fundamental Load Plane, Z0=50Ω",
            HarmonicaTitles.PlaneRow(TerminationSide.Load, 1, 50.0));

    [Fact]
    public void PlaneRow_Band2Source_ReadsNfZero()
        => Assert.Equal("2f0 Source Plane, Z0=50Ω",
            HarmonicaTitles.PlaneRow(TerminationSide.Source, 2, 50.0));

    [Fact]
    public void PlaneRow_Band4Load_ReadsNfZero()
        => Assert.Equal("4f0 Load Plane, Z0=50Ω",
            HarmonicaTitles.PlaneRow(TerminationSide.Load, 4, 50.0));

    [Fact]
    public void PlaneRow_NonIntegerZ0_IsNotForcedToAnInteger()
        => Assert.Equal("Fundamental Load Plane, Z0=37.5Ω",
            HarmonicaTitles.PlaneRow(TerminationSide.Load, 1, 37.5));

    [Fact]
    public void PlaneRow_IntegerZ0_HasNoDecimalPoint()
    {
        var row = HarmonicaTitles.PlaneRow(TerminationSide.Load, 1, 75.0);
        Assert.DoesNotContain(".", row);
    }

    // ── R8C §1.5 — MxHeaderRow carries the optimum's impedance ───────────────

    [Fact]
    public void MxHeaderRow_Load_WithZText_CarriesZL()
        => Assert.Equal("MXP 1f0 ZL1=12.500-j3.200 Ω",
            HarmonicaTitles.MxHeaderRow("MXP", TerminationSide.Load, 1, "12.500-j3.200 Ω"));

    [Fact]
    public void MxHeaderRow_Source_WithZText_CarriesZS()
        => Assert.Equal("MXE 2f0 ZS2=8.100+j45.000 Ω",
            HarmonicaTitles.MxHeaderRow("MXE", TerminationSide.Source, 2, "8.100+j45.000 Ω"));

    [Fact]
    public void MxHeaderRow_NullZText_KeepsTheOldPlaneOnlyHeader()
        => Assert.Equal("MXP 1f0 Load", HarmonicaTitles.MxHeaderRow("MXP", TerminationSide.Load, 1, zText: null));

    [Fact]
    public void MxHeaderRow_EmptyZText_KeepsTheOldPlaneOnlyHeader()
        => Assert.Equal("MXE 3f0 Source", HarmonicaTitles.MxHeaderRow("MXE", TerminationSide.Source, 3, zText: ""));
}
