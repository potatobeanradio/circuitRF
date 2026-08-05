using CircuitRF.Core;
using CircuitRF.Core.Devices;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Pins the temperature contract every component model shares. These are cheap and they exist
/// because a temperature mistake is the silent kind: it converges, the numbers are finite, and the
/// answer is wrong by an amount that looks like a modelling difference.
/// </summary>
public sealed class TemperatureTests
{
    /// <summary>
    /// The nominal is 26.85 °C precisely because it is 300 K EXACTLY, and "exactly" is a claim about
    /// IEEE-754 doubles rather than about arithmetic on paper. If this ever fails, the nominal has
    /// been "tidied" to 27 °C and every device that states no temperature has acquired a small
    /// residual ΔT it is not supposed to have.
    /// </summary>
    [Fact]
    public void NominalIsExactlyThreeHundredKelvin()
    {
        Assert.Equal(300.0, Temperature.NominalK);              // bit-exact, not approximate
        Assert.Equal(300.0, Temperature.ToKelvin(Temperature.NominalC));
    }

    [Fact]
    public void CelsiusKelvinRoundTrips()
    {
        foreach (double c in new[] { -273.15, -40.0, 0.0, 26.85, 27.0, 85.0, 125.0 })
            Assert.Equal(c, Temperature.ToCelsius(Temperature.ToKelvin(c)), 12);
    }

    /// <summary>
    /// A temperature DIFFERENCE is the same number on both scales — which is the whole reason every
    /// published relation is written in ΔT rather than in two absolute temperatures. Asserting it
    /// keeps anyone from "fixing" DeltaT to convert something.
    /// </summary>
    [Fact]
    public void DeltaTIsScaleFree()
    {
        const double tempC = 85.0, tnomC = 27.0;
        double inCelsius = Temperature.DeltaT(tempC, tnomC);
        double inKelvin  = Temperature.ToKelvin(tempC) - Temperature.ToKelvin(tnomC);

        Assert.Equal(58.0, inCelsius, 12);
        Assert.Equal(inCelsius, inKelvin, 12);
    }

    [Fact]
    public void ThermalVoltageAtNominalIsTheTextbookValue()
    {
        // kT/q at 300 K = 25.852 mV. Four significant figures is all this is worth asserting to.
        Assert.Equal(0.025852, Temperature.ThermalVoltage(Temperature.NominalK), 6);
    }

    /// <summary>
    /// The forwarding alias still resolves to the same constant. Both spellings are in use — the
    /// factory and the FET tests use the old name — and a divergence would put two different
    /// nominals in one build with nothing to signal it.
    /// </summary>
    [Fact]
    public void FetBaseAliasStillMatchesTheDefinition()
        => Assert.Equal(Temperature.NominalC, CircuitRF.Core.Devices.Fet.FetModelBase.NominalTemperatureC);

    /// <summary>
    /// The load-bearing one: a DEFAULT-constructed diode is still evaluated at exactly the nominal
    /// after the constant moved. Vt is private, so it is recovered from behaviour instead of read —
    /// with Is = 1 and N = 1 the conduction current is exp(V/Vt) − 1, so Vt = V / ln(I + 1).
    ///
    /// This is what makes the extraction provably numeric-identity rather than probably harmless.
    /// </summary>
    [Fact]
    public void DefaultDiodeStillSitsAtTheNominalTemperature()
    {
        var d = new DiodeModel(saturationCurrent: 1.0, emissionCoefficient: 1.0);

        const double v = 0.01;                       // well inside the exponential region
        var r = d.Evaluate(new PortVoltages([v]));
        double recoveredVt = v / System.Math.Log(r.I[0] + 1.0);

        Assert.Equal(Temperature.ThermalVoltage(Temperature.NominalK), recoveredVt, 12);
    }
}
