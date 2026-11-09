using System.Runtime.CompilerServices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// The envelope Newton at a modulated signal's peaks, on the Model Composer example's composed GaN FET (traps and
/// self-heating on): a full update drove the trap-state and thermal rows' residuals to 1e13-1e28 and the run stopped.
/// The step now takes harmonic balance's scaled-norm line search, a step that still fails is retaken in sub-steps, and a
/// swept point that fails anyway is reported by its value and left NaN rather than losing the whole sweep.
/// </summary>
public sealed class EnvelopeConvergenceTests
{
    private static string Bench(double pinDbm, string accuracy, double stopS) => $"""
        Pin = {pinDbm}
        MOD_Source:SRC1 n1 0 Fc=2e9 Pavg=Pin dBm Z=50 Ohm Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1" Record=0.001 Shaping=-80
        C:C1 Vin n1 C=100 pF
        L:L1 n2 Vin L=100 nH
        Vdc:V1 n2 0 Vdc=-0.8 V
        ComposedFET:X1 Vin Vout 0 File="{CmodelPath()}" N=8
        L:L2 VDD Vout L=100 nH
        Vdc:V2 VDD 0 Vdc=8 V
        C:C2 Vout n5 C=100 pF
        IProbe:Iout n5 o
        R:RL o 0 R=50 Ohm
        analysis ENV1 type=env Stop={stopS} Accuracy={accuracy} MaxHarm=5 Probes="Vin,Vout" Segments=1 Warmup=0
        """;

    private static (DataSet ds, List<string> notes) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        var notes = new List<string>();
        return (EnvelopeRunner.Run(plan, () => nl, note: notes.Add), notes);
    }

    [Fact]
    public void ADivergingFullStep_IsDampedByTheLineSearch()
    {
        // Stopped at step 77 (t = 78 ns) on the trap-state and thermal rows before the line search.
        var (ds, notes) = Run(Bench(20, "fine", 0.15e-6));

        Assert.True(double.IsFinite(ds["Pavg_dBm"].RealValues[0]));
        // no step needed splitting; a 0.15 us run holds no whole NR symbol, which the run notes (brief NZ-8)
        Assert.DoesNotContain(notes, n => !n.Contains("no signal quality", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepTheLineSearchCannotTake_IsRetakenInSubSteps()
    {
        // At 38 dBm a standard step near 0.3 µs does not converge even with the line search.
        var (ds, notes) = Run(Bench(38, "standard", 0.35e-6));

        Assert.True(double.IsFinite(ds["Pavg_dBm"].RealValues[0]));
        Assert.Contains(notes, n => n.Contains("sub-steps", StringComparison.Ordinal));
    }

    [Fact]
    public void ASweptPointThatFails_IsNaN_NamedByItsValue_AndTheOthersAreKept()
    {
        // A tolerance no solve can reach above 1 dBm: the 3 dBm point fails at its harmonic-balance start.
        var (lib, tb) = new CnlReader().Read("""
            Pin = 0
            MOD_Source:S1 n1 0 Fc=1e9 Pavg=Pin Z=50 Carrier[1]="CW;Off=0;P=0"
            IProbe:Iout n1 m
            SDD:X1 m 0 I[1,0]=_v1/50 + 1e-4*_v1^3
            analysis ENV1 type=env Stop=2e-6 Accuracy=draft Probes="n1" Segments=1 Warmup=0 Tol="if(Pin>1, 1e-300, 1e-6)"
            analysis SW1 type=parametric_sweep Var=Pin Values=0,3 Inner=ENV1
            """);
        using var diag = new Elaborator(lib).Elaborate(tb);

        var ds = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb,
                                           diagnosticsInto: diag);

        var pout = ds["Pavg_dBm"].RealValues;
        Assert.True(double.IsFinite(pout[0]));
        Assert.True(double.IsNaN(pout[1]));
        Assert.Contains(diag.Warnings, w => w.Contains("Pin = 3", StringComparison.Ordinal));
    }

    private static string CmodelPath([CallerFilePath] string here = "")
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "examples", "Model Composer", "synthetic-gan", "gan_4x125.cmodel");
    }
}
