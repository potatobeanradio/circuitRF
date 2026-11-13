using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>Issue 66 §3: a labelled net that only nonlinear devices touch is recorded from the step's accepted Newton
/// unknown, so the seam check reads the circuit and not Newton's residual through a huge driving-point impedance. Here
/// the net between two amplifiers' outputs and a third's input read EnvSeamError 20.6 and a "segments disagree" note,
/// against 0.0025 unlabelled, every output identical.</summary>
public sealed class EnvelopeDeviceOnlyNetTests
{
    private static string Bench(string labelled) => $"""
        MOD_Source:S1  a  0  Fc=3.5 GHz  Pavg=-10  Z=50  Record=0.5e-3  Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1"
        Filter:F1  a  0  f  0  Response=Butterworth  Form=Bandpass  Order=5  F1=3.4875 GHz  F2=3.5125 GHz
        Amp:A1  f  0  n2  0  Gain=-7 dB  IP3=30  IP3Ref=Output
        Amp:A2  f  0  n2  0  Gain=-7 dB  IP3=30  IP3Ref=Output
        Amp:PA  n2  0  out  0  Gain=20 dB  IP3=55  IP3Ref=Output
        IProbe:Iout  out  load
        R:RL  load  0  R=50
        analysis ENV1 type=env Stop=0.1e-3 Step=auto Accuracy=standard MaxHarm=1 Segments=8 Warmup=auto Start=0 Tol=1e-6 SaveWaveforms=no
        labelednets {labelled}
        """;

    private static (DataSet ds, List<string> said) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var first = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), first.ResolvedGlobals);
        bool used = false;
        var gate = new object();
        ElaboratedNetlist Elab() { lock (gate) { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return first; } }
        var said = new List<string>();
        var ds = EnvelopeRunner.Run(plan, Elab, note: said.Add);
        said.AddRange(first.Notes);
        said.AddRange(first.Warnings);
        return (ds, said);
    }

    [Fact]
    public void ADeviceOnlyNet_IsRecordedAsTheSolveSolvedIt_AndRaisesNoSeamAlarm()
    {
        var (with, saidWith) = Run(Bench("a f n2 out load"));
        var (without, _) = Run(Bench("a f out load"));

        double seamWith = with["__EnvSeamError"].RealValues[0], seamWithout = without["__EnvSeamError"].RealValues[0];
        Assert.True(seamWith <= 2 * seamWithout, $"EnvSeamError {seamWith} with n2 labelled against {seamWithout} without");
        Assert.DoesNotContain(saidWith, s => s.Contains("disagree at their seams", StringComparison.Ordinal));
        Assert.Equal(without["EVM_dB"].RealValues, with["EVM_dB"].RealValues);
        Assert.Equal(without["Pin_avg_dBm"].RealValues, with["Pin_avg_dBm"].RealValues);
    }
}
