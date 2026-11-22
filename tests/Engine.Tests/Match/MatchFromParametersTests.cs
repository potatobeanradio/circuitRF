using System;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Match;

/// <summary>
/// A <c>Match</c> line with no <c>Design</c> is synthesised from its own parameters, so a netlist written
/// by hand or by an agent places a working matching network without the Designer.
/// </summary>
public class MatchFromParametersTests
{
    private static double[] S(string cnl, double f, int i, int j)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [f]);
        return [20 * Math.Log10(((Complex)ds["S"][0, i, j]).Magnitude)];
    }

    [Theory]
    // A dual-band filter between 50-ohm ports: passes both bands, reflects the gap.
    [InlineData("Bands=2 F1=1.805 GHz F2=1.88 GHz F3=2.62 GHz F4=2.69 GHz Order=2 R1=50 Ohm R2=50 Ohm", 50, 1.84e9, 2.2e9)]
    // 50 to 10 ohms needs a Norton transform: the one the Designer's own default takes.
    [InlineData("F1=1.8 GHz F2=2.2 GHz Order=4 R1=50 Ohm R2=10 Ohm", 10, 2.0e9, 1.0e9)]
    public void WithNoDesign_TheParametersAreTheSpecification(string match, double r2, double fIn, double fOut)
    {
        string cnl = $"""
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z={r2} Ohm
            Match:M1 in out {match}
            """;
        Assert.True(S(cnl, fIn, 0, 0)[0] < -15, "matched in band");
        Assert.True(S(cnl, fOut, 1, 0)[0] < -10, "rejects out of band");
    }

    /// <summary>Form is a name, kept as one: evaluated as an expression it was dropped and a Lowpass line built a bandpass.</summary>
    [Fact]
    public void FormLowpass_PassesLowFrequencies()
    {
        const string cnl = """
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=25 Ohm
            Match:M1 in out F1=1 GHz F2=2 GHz Order=2 Form=Lowpass R1=50 Ohm R2=25 Ohm
            """;
        Assert.True(S(cnl, 10e6, 1, 0)[0] > -1, "a lowpass passes 10 MHz; a bandpass would not");
    }

    [Fact]
    public void ABareFrequency_IsRefusedNamingTheUnit()
    {
        const string cnl = """
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            Match:M1 in out F1=1.8 F2=2.2 R1=50 Ohm R2=50 Ohm
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var e = Assert.ThrowsAny<Exception>(() => SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [2e9]));
        Assert.Contains("F1=1.8 GHz", e.ToString());
    }
}
