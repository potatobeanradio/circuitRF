using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// Gate 10 (brief-L5a-pcell-contract-and-microstrip.md): Hammerstad-Jensen static Z0/eeff against
/// the one independently-sourced acceptance row obtained during physics research — M. Steer,
/// "Fundamentals of Microwave and RF Design" (open-access, LibreTexts), Example 4.2/3.5.2:
/// W=600um, h=635um, er=4.1 -> eeff=2.967, Z0,air=129.7 ohm, Z0=75.3-75.4 ohm (two independent
/// fetches of the same source rounded slightly differently; both cited in the research report).
/// </summary>
public class HammerstadJensenTests
{
    private readonly MicrostripValidityReporter _reporter = new("test");

    [Fact]
    public void Compute_SteerWorkedExample_MatchesIndependentSource()
    {
        double w = 600e-6, h = 635e-6, t = 0, er = 4.1;
        var (z0, eeff) = HammerstadJensen.Compute(w, h, t, er, _reporter);

        Assert.Equal(2.967, eeff, 0.01);
        Assert.Equal(75.35, z0, 0.5); // 75.3-75.4 across the two cited fetches
    }

    [Fact]
    public void StaticZ0Air_SteerWorkedExample_Matches1297()
    {
        double u = 600.0 / 635.0;
        double z0Air = HammerstadJensen.StaticZ0Air(u);
        Assert.Equal(129.7, z0Air, 0.2);
    }

    [Fact]
    public void StaticEeff_IsAlwaysBetweenOneAndEpsR()
    {
        // eeff -> (er+1)/2 as W/h -> 0; eeff -> er as W/h -> large. Always strictly between 1 and er
        // for a real substrate (er > 1).
        foreach (var u in new[] { 0.05, 0.5, 1.0, 5.0, 50.0 })
        foreach (var er in new[] { 2.2, 4.4, 10.2, 12.9 })
        {
            double eeff = HammerstadJensen.StaticEeff(u, er);
            Assert.True(eeff > 1.0 && eeff < er, $"u={u} er={er} eeff={eeff}");
        }
    }

    [Fact]
    public void ThicknessCorrection_ZeroThickness_LeavesWidthUnchanged()
    {
        HammerstadJensen.ThicknessCorrectedWidths(1.0, 0.0, 4.4, out double u1, out double uR);
        Assert.Equal(1.0, u1);
        Assert.Equal(1.0, uR);
    }

    [Fact]
    public void ThicknessCorrection_PositiveThickness_WidensBothU1AndUr()
    {
        HammerstadJensen.ThicknessCorrectedWidths(1.0, 0.02, 4.4, out double u1, out double uR);
        Assert.True(u1 > 1.0);
        Assert.True(uR > 1.0);
    }

    [Fact]
    public void Compute_OutOfRangeWOverH_ReportsOncePerDistinctViolation()
    {
        // brief-mklopf-performance-and-messages.md R-mk-8: MicrostripValidityReporter no longer
        // writes to Console.Error at all (that was the actual bug — nothing connected it to the
        // Messages UI); its messages are queued and read via Drain().
        var reporter = new MicrostripValidityReporter("MLIN:X1");
        // W/h = 0.001, well below the 0.01 floor.
        HammerstadJensen.Compute(1e-6, 1e-3, 0, 4.4, reporter);
        HammerstadJensen.Compute(1e-6, 1e-3, 0, 4.4, reporter); // same violation again — must not re-report
        var warnings = reporter.Drain();
        Assert.Single(warnings);
        Assert.Contains("W/h", warnings[0].Message);
    }
}
