using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// What an S-parameter run of an ideal mixer reports — the port matches and the leakage paths, and
/// no conversion at all.
///
/// <para>That is an ANSWER rather than an omission, and it is worth a file of its own because it
/// looks exactly like a missing feature: a user who sweeps a mixer and reads S21 = 0 will conclude
/// the device is not stamped. The arithmetic is in <c>ComponentModel.StampLinearized</c>, which
/// linearises a nonlinear device at its DC operating point — and the mixer's RF-to-IF small-signal
/// gain is proportional to the LO VOLTAGE, which at DC is zero. The tests below pin both halves:
/// the conversion path really is absent, and everything an S-parameter measurement CAN see about a
/// mixer really is there.</para>
/// </summary>
public class MixerSParamTests(ITestOutputHelper output)
{
    // Ports 1/2/3 on RF/LO/IF. The default mixer is ideal: 50 Ω at every port, no leakage.
    private const string Ideal = """
        Port:P1  rf 0  Num=1  Z=50 Ohm
        Port:P2  lo 0  Num=2  Z=50 Ohm
        Port:P3  if 0  Num=3  Z=50 Ohm
        Mixer:X1  rf 0  lo 0  if 0  ConvGain=-7  Plo=7
        """;

    private static DataSet Run(string cnl, double fHz = 1e9)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, [fHz]);
    }

    private static Complex S(DataSet ds, int r, int c) => (Complex)ds["S"][0, r, c];

    [Fact]
    public void EveryPortIsMatched_BecauseEachOneIsItsOwnImpedance()
    {
        var ds = Run(Ideal);
        for (int p = 0; p < 3; p++)
            Assert.True(S(ds, p, p).Magnitude < 1e-9,
                $"S{p + 1}{p + 1} = {S(ds, p, p)} (a 50 Ω port into a 50 Ω reference reflects nothing)");
    }

    [Fact]
    public void NothingTransmits_BecauseConversionIsNotWhatSparametersMeasure()
    {
        // The whole point of the file. S31 is zero not because the mixer is missing from the matrix
        // but because ∂i_if/∂v_rf = −K·v_lo/Zif and v_lo = 0 at the DC operating point. An ideal
        // mixer with no LO on it genuinely does not transmit.
        var ds = Run(Ideal);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                if (r != c)
                    Assert.True(S(ds, r, c).Magnitude < 1e-9, $"S{r + 1}{c + 1} = {S(ds, r, c)}");
    }

    [Fact]
    public void Mismatch_IsVisible_SoTheDeviceIsDemonstrablyInTheMatrix()
    {
        // The companion to the test above: "S21 = 0" would mean nothing if the mixer were simply
        // absent from the assembly. Detune one port and its reflection moves to the value a plain
        // resistor of that value would give — Γ = (25 − 50)/(25 + 50) = −1/3.
        var ds = Run(Ideal.Replace("ConvGain=-7", "ConvGain=-7  Zrf=25"));
        Assert.Equal(-1.0 / 3.0, S(ds, 0, 0).Real, 9);
        Assert.Equal(0.0, S(ds, 0, 0).Imaginary, 9);
    }

    [Fact]
    public void TheLeakagePaths_AreExactlyWhatAnSparamRunCanSee()
    {
        // 30 dB of RF-IF isolation is 30 dB of S31, and it is unilateral — nothing comes back.
        var ds = Run(Ideal.Replace("Plo=7", "Plo=7  IsoRF_IF=30"));
        double s31 = 20.0 * Math.Log10(S(ds, 2, 0).Magnitude);
        output.WriteLine($"S31 = {s31:F4} dB");
        Assert.Equal(-30.0, s31, 6);
        Assert.True(S(ds, 0, 2).Magnitude < 1e-9, "leakage is one-directional; S13 must stay zero");
    }

    [Fact]
    public void LoToRfLeakage_LandsOnTheRfPort_NotTheIfPort()
    {
        // Each isolation names one path, and getting two of them the same way round is the kind of
        // mistake that survives every other test in the suite.
        var ds = Run(Ideal.Replace("Plo=7", "Plo=7  IsoLO_RF=20"));
        Assert.Equal(-20.0, 20.0 * Math.Log10(S(ds, 0, 1).Magnitude), 6);   // LO port → RF port
        Assert.True(S(ds, 2, 1).Magnitude < 1e-9, "IsoLO_RF must not put anything at the IF port");
    }

    [Fact]
    public void TheIdealDeviceIsFrequencyFlat_BecauseItStoresNoCharge()
    {
        var lo = Run(Ideal.Replace("Plo=7", "Plo=7  IsoRF_IF=30"), 1e8);
        var hi = Run(Ideal.Replace("Plo=7", "Plo=7  IsoRF_IF=30"), 4e10);
        Assert.Equal(S(lo, 2, 0).Real, S(hi, 2, 0).Real, 12);
        Assert.Equal(S(lo, 2, 0).Imaginary, S(hi, 2, 0).Imaginary, 12);
    }
}
