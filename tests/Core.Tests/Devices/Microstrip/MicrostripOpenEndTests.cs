using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// The open end's closed form against an independent solve, and a plan's gap refusing and warning as MGAP does.
/// </summary>
public class MicrostripOpenEndTests
{
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    /// <summary>testdata/microstrip-gap/README.md: the planar engine's electrostatic solve of a zero-thickness strip,
    /// W = h = 508 µm on εr 3.66, puts 13.4 fF in one open end. The closed form gives 12.85 fF, 4 % below.</summary>
    [Fact]
    public void TheEndCapacitance_IsTheReferenceSolvesWithinFivePercent()
    {
        var p = new MicrostripLineModel(508e-6, 0.0, 508e-6, 0.0, 3.66, 5.8e7, 0.0, "test").LineParameters(0.0);
        double c = MicrostripOpenEnd.Capacitance(MicrostripOpenEnd.Extension(508e-6, 508e-6, 3.66, p.EeffStatic),
            p.EeffStatic, p.Z0Static);
        Assert.InRange(c / 13.4e-15, 0.95, 1.05);
    }

    private static PassivePlan Gap(double w, double s) => new(["1", "2"],
    [
        new PlanLine([1, 3], 0.0, 0.0, 0.0, w, 5e-3),
        new PlanGap([3, 4], 5e-3, 0.0, 0.0, w, s),
        new PlanLine([4, 2], 5e-3 + s, 0.0, 0.0, w, 5e-3),
    ], 4);

    [Fact]
    public void APlansGap_RefusesAndWarnsAsMgapDoes()
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => new MicrostripPlanModel(Gap(1e-3, 0.0), Ro4350b, "X1"));
        Assert.Contains("S must be greater than zero", ex.Message);

        var wide = new MicrostripPlanModel(Gap(1.5e-3, 0.2e-3), Ro4350b, "X1");      // W/h 2.95, past MGAP's 2
        Assert.Contains(wide.DrainWarnings(), w => w.Key == "MGAP:W/h");
    }
}
