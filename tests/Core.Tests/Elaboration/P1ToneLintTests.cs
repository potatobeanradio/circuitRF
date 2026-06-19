using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// Gate tests for brief-schematic-housecleaning Item 2:
/// LintTopLevelTerms now includes P1ToneModel so P1Tone satisfies S-param port numbering.
/// </summary>
public class P1ToneLintTests
{
    private static ElaboratedNetlist Parse(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static bool HasMissingPortWarning(ElaboratedNetlist nl)
        => nl.Warnings.Any(w => w.Contains("is missing") || w.Contains("port Num="));

    // T1 — P1Tone at Num=1 + Term at Num=2: no "port 1 missing" warning
    [Fact]
    public void P1Tone_Num1_PlusTerm_Num2_NoMissingWarning()
    {
        var nl = Parse(@"
P1Tone:P1  n1 0  Num=1 Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
Term:T2    n2 0  Num=2 Z=50 Ohm
R:R1  n1 n2  R=100 Ohm
");
        Assert.False(HasMissingPortWarning(nl),
            $"Expected no missing-port warning; got: {string.Join("; ", nl.Warnings)}");
    }

    // T2 — two Terms with no gap: no warning
    [Fact]
    public void TwoTerms_Sequential_NoWarning()
    {
        var nl = Parse(@"
Term:T1  n1 0  Num=1 Z=50 Ohm
Term:T2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 n2  R=100 Ohm
");
        Assert.False(HasMissingPortWarning(nl),
            $"Expected no warning; got: {string.Join("; ", nl.Warnings)}");
    }

    // T3 — only a P1Tone at Num=1 (1-port): no "port 1 missing" warning
    [Fact]
    public void P1Tone_Only_Num1_NoMissingWarning()
    {
        var nl = Parse(@"
P1Tone:P1  n1 0  Num=1 Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
R:R1  n1 0  R=100 Ohm
");
        Assert.False(HasMissingPortWarning(nl),
            $"Expected no missing-port warning; got: {string.Join("; ", nl.Warnings)}");
    }

    // T4 — P1Tone at Num=2 without Num=1: should warn about missing port 1
    [Fact]
    public void P1Tone_GapAtNum1_HasMissingWarning()
    {
        var nl = Parse(@"
P1Tone:P1  n1 0  Num=2 Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
Term:T2    n2 0  Num=3 Z=50 Ohm
R:R1  n1 n2  R=100 Ohm
");
        Assert.True(HasMissingPortWarning(nl),
            "Expected a missing-port warning when Num=1 is absent");
    }
}
