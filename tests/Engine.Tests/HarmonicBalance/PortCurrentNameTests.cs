using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>Issue 65 §2: a block whose terminals come in pairs keys each port's current by its pair's first name, so an
/// amplifier's output current is <c>A1:out+</c>, not <c>A1:in-</c>; the input keeps <c>A1:in+</c> and the numeric
/// aliases are unchanged.</summary>
public sealed class PortCurrentNameTests
{
    [Fact]
    public void AnAmplifiersOutputCurrent_IsKeyedOut_AndItsInputKeepsItsKey()
    {
        var (lib, tb) = new CnlReader().Read("""
            P1Tone:P1 in 0 Pavl=-10 dBm Z=50 Ohm Freq=1 GHz
            Amp:A1 in 0 out 0 Gain=10 dB IP3=30 IP3Ref=Output
            R:RL out 0 R=50 Ohm
            analysis HB1 type=hb Tone="1e9" ToneUnit=Hz MaxHarm=3
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ds = (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals));

        var i = ds["I"];
        var labels = i.Axes.First(a => a.Name == "branch").Labels!;
        Assert.Contains("A1:in+", labels);
        Assert.Contains("A1:out+", labels);
        Assert.DoesNotContain("A1:in-", labels);
        Assert.Contains("A1:1", labels);

        // [branch, harmonic]: the output port's current is the load's, V(out) = 0.316 V across 50 Ω, 6.32 mA.
        int b = Array.IndexOf(labels, "A1:out+"), nk = i.Axes[1].Length;
        double magnitude = i.ComplexValues[b * nk + 1].Magnitude;
        Assert.Equal(6.32e-3, magnitude, 4e-5);
    }
}
