using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// Gate tests for brief-schematic-housecleaning Item 2:
/// LintTopLevelTerms now includes P1ToneModel so P1Tone satisfies S-param port numbering.
///
/// The lint is gated on a runnable S-parameter analysis (the Num parameter is meaningful only there),
/// so every test that asserts the warning fires must declare an S-parameter analysis. The final test
/// gates the inverse: no S-parameter analysis ⇒ no Num warning, even with a missing Num.
/// </summary>
public class P1ToneLintTests
{
    private const string Sparam = "analysis SP type=sparam start=1 GHz stop=3 GHz step=1 GHz";

    private static ElaboratedNetlist Parse(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static bool HasMissingPortWarning(ElaboratedNetlist nl)
        => nl.Warnings.Any(w => w.Contains("is missing") || w.Contains("port Num="));

    private static bool HasNumWarning(ElaboratedNetlist nl)
        => nl.Warnings.Any(w => w.Contains("has no Num parameter"));

    // T1 — P1Tone at Num=1 + Term at Num=2: no "port 1 missing" warning
    [Fact]
    public void P1Tone_Num1_PlusTerm_Num2_NoMissingWarning()
    {
        var nl = Parse(@"
P1Tone:P1  n1 0  Num=1 Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
Term:T2    n2 0  Num=2 Z=50 Ohm
R:R1  n1 n2  R=100 Ohm
" + Sparam + "\n");
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
" + Sparam + "\n");
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
" + Sparam + "\n");
        Assert.False(HasMissingPortWarning(nl),
            $"Expected no missing-port warning; got: {string.Join("; ", nl.Warnings)}");
    }

    // T4 — P1Tone at Num=2 without Num=1, with S-param analysis: warns about missing port 1
    [Fact]
    public void P1Tone_GapAtNum1_HasMissingWarning()
    {
        var nl = Parse(@"
P1Tone:P1  n1 0  Num=2 Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
Term:T2    n2 0  Num=3 Z=50 Ohm
R:R1  n1 n2  R=100 Ohm
" + Sparam + "\n");
        Assert.True(HasMissingPortWarning(nl),
            "Expected a missing-port warning when Num=1 is absent");
    }

    // T5 — missing-Num Term, with an S-param analysis: warns (the lint has teeth when S-param runs)
    [Fact]
    public void TermNoNum_WithSparam_Warns()
    {
        var nl = Parse(@"
P1Tone:P1  n1 0  Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
R:R1  n1 0  R=100 Ohm
" + Sparam + "\n");
        Assert.True(HasNumWarning(nl),
            "Expected a no-Num warning when an S-parameter analysis is present");
    }

    // T6 — missing-Num Term, NO S-param analysis (HB only): no Num warning.
    // This is the regression guard: a Term/P1Tone without Num must NOT warn on a bench that runs
    // only harmonic balance — the Num parameter is irrelevant to HB.
    [Fact]
    public void TermNoNum_NoSparam_DoesNotWarn()
    {
        var nl = Parse(@"
P1Tone:P1  n1 0  Pavl=0 dBm Z=50 Ohm Freq=1 GHz Phase=0 deg
R:R1  n1 0  R=100 Ohm
analysis HB type=hb fund=1 GHz harmonics=5
");
        Assert.False(HasNumWarning(nl),
            $"Expected no Num warning without an S-parameter analysis; got: {string.Join("; ", nl.Warnings)}");
    }
}
