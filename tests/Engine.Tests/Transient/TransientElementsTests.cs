using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Transient;
using Xunit;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>
/// brief-trn-1 R-trn1-9 — which elements a transient run steps. The lumped models below are the list the affine measure
/// accepts (a model added to it changes this test, on purpose); a line's delay, a controlled source's delay and the
/// elements refused by what they are each come back as one sentence naming the instance.
/// </summary>
public sealed class TransientElementsTests
{
    private static List<string> ProblemsOf(string body)
    {
        var (lib, tb) = new CnlReader().Read(body + "\nanalysis TR1 type=tran Stop=1e-6\n");
        return TransientElements.Problems(new Elaborator(lib).Elaborate(tb));
    }

    /// <summary>Every lumped model the measure classifies as affine in jω.</summary>
    [Fact]
    public void LumpedModels_AreAccepted()
    {
        var problems = ProblemsOf("""
            Vdc:V1 a 0 Vdc=1
            R:R1 a b R=50 Ohm
            C:C1 b 0 C=1 pF
            L:L1 b c L=1 nH R=0.1 Ohm
            L:L2 c 0 L=2 nH
            Mutual:K1 Inductor1="L1" Inductor2="L2" M=0.5 nH
            R:Rx c d R=2 Ohm
            PRLC:X2 d 0 R=300 Ohm L=5 nH C=2 pF
            VCCS:G1 e 0 b 0 G=10 mS
            VCVS:E1 f 0 b 0 E=2
            Transformer:T1 f 0 g 0 N=2
            Port:P1 g 0 Num=1
            Term:T2 e 0 Num=2 Z=75 Ohm
            IProbe:I1 d h
            R:R2 h 0 R=10 Ohm
            V_1Tone:Vs s 0 Freq=1e9 V=1 Phase=0 Vdc=0
            I_1Tone:Is s2 0 Freq=1e9 I=1e-3
            R:R3 s2 0 R=50 Ohm
            P1Tone:SRC s3 0 Num=3 Freq=1e9 Pavl=-30 dBm Z=50 Ohm
            Short:S1 s3 s
            """);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>brief-trn-4 — a lossless line is stepped as its delay and a series RLC (its C in series on one branch: 1/jωC) by a
    /// rational fit of its stamp; a controlled source delayed by 100 periods of the band's top is no rational function and is
    /// refused by name, its fit's error said.</summary>
    [Fact]
    public void ALineAndASeriesRlc_AreRealized_ALongDelayIsRefusedByName()
    {
        var problems = ProblemsOf("""
            Vdc:V1 a 0 Vdc=1
            TLIN:TL1 a b Z=50 E=20 deg F=1e9
            R:R1 b 0 R=50
            VCVS:E1 c 0 b 0 E=2 T=1e-8
            R:R2 c 0 R=50
            SRLC:X1 c 0 R=2 Ohm L=3 nH C=4 pF
            """);
        var only = Assert.Single(problems);
        Assert.StartsWith("E1 (VCVS) is not supported in transient", only);
        Assert.Contains("rational fit", only);
    }

    /// <summary>brief MTR-1 — a modulated source is no longer refused: the native transient plays it.</summary>
    [Fact]
    public void AModulatedSource_IsPlayed()
    {
        var problems = ProblemsOf("""
            MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=50 Record=0.5e-3 Carrier[1]="CW;Off=0;P=0"
            R:R1 s 0 R=50 Ohm
            """);
        Assert.Empty(problems);
    }
}
