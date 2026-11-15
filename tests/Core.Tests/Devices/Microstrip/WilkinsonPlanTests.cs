using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// <see cref="WilkinsonPlan"/>'s numbers against the closed form (Wilkinson 1960; Pozar): arms of √2·Z0, a
/// quarter wave long at F in MLIN's own dispersive εeff, an isolation resistor of 2·Z0, and a ring that closes
/// across Gap.
/// </summary>
public class WilkinsonPlanTests
{
    /// <summary>20 mil RO4350B, 1 oz copper — the example workspace's board.</summary>
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private const double C0 = 2.99792458e8;

    private static MicrostripLineParameters Line(double w, double f)
        => new MicrostripLineModel(w, 1.0, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, "test").LineParameters(f);

    [Theory]
    [InlineData(50.0, 2.4e9, 1.35e-3)]
    [InlineData(50.0, 5.8e9, 1.35e-3)]
    [InlineData(35.0, 1.0e9, 2.0e-3)]
    public void TheNumbers_AreTheClosedForm(double z0, double f, double gap)
    {
        var d = WilkinsonPlan.Build(z0, f, gap, Ro4350b);

        Assert.Equal(Math.Sqrt(2.0) * z0, d.ArmZ0, 12);
        Assert.Equal(2.0 * z0, d.IsolationOhms, 12);
        // Widths are MLIN's at F, held on the 2 nm grid: within a few milliohms of the asked impedance.
        Assert.Equal(z0, Line(d.W0Meters, f).Z0, 2);
        Assert.Equal(d.ArmZ0, Line(d.ArmWMeters, f).Z0, 2);
        Assert.Equal(Line(d.ArmWMeters, f).Eeff, d.ArmEeff, 12);
        Assert.Equal(C0 / (4.0 * f * Math.Sqrt(d.ArmEeff)), d.QuarterWaveMeters, 12);

        // Each arm, reference plane to reference plane: the input tee's line, then the curve's effective length.
        var tee = Assert.IsType<PlanTee>(d.Plan.Sections[0]);
        var curve = Assert.IsType<PlanCurve>(d.Plan.Sections[1]);
        double arm = tee.L1 - tee.W3 / 2.0 + MicrostripCurve.ElectricalLength(curve.Radius, curve.W, curve.AngleDeg);
        Assert.True(Math.Abs(arm - d.QuarterWaveMeters) < 4e-9, $"arm {arm} vs lambda/4 {d.QuarterWaveMeters}");
    }

    /// <summary>The curves come back facing each other across Gap: R2 and R3 are Gap apart, and every piece
    /// starts where the one before it ends.</summary>
    [Fact]
    public void TheRing_ClosesAcrossTheGap()
    {
        var d = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b);
        var s = d.Plan.Sections;
        var top = (PlanCurve)s[1];
        var outTop = (PlanTee)s[3];
        var outBottom = (PlanTee)s[4];

        Assert.Equal(["1", "2", "3", "R2", "R3"], d.Plan.Pins);
        Assert.Equal(top.X + 2.0 * top.Radius, outTop.X, 12);                 // the curve's far end is the output tee's
        Assert.Equal(top.Y, outTop.Y + outTop.L1 + outTop.L2, 12);
        Assert.Equal(d.GapMeters, outTop.Y - (outBottom.Y + outBottom.L1 + outBottom.L2), 12);   // R2 to R3
        Assert.Equal(1.35e-3, d.GapMeters, 15);
    }

    [Theory]
    [InlineData(0.0, 2.4e9, 1.35e-3, "device.wilkinson.spec-not-positive")]
    [InlineData(50.0, -1.0, 1.35e-3, "device.wilkinson.spec-not-positive")]
    [InlineData(50.0, 40e9, 1.35e-3, "device.wilkinson.ring-too-small")]
    [InlineData(500.0, 2.4e9, 1.35e-3, "device.wilkinson.width-unreachable")]
    public void ASpecThatCannotBeBuilt_IsACodedRefusal(double z0, double f, double gap, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => WilkinsonPlan.Build(z0, f, gap, Ro4350b));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }
}
