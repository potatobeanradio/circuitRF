using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// brief-hb-control-loop-cold-start — harmonic balance of the SDD example's bias loop (bench 6: an SDD reading the drain supply's
/// current through an IProbe holds the gate's feed at VGS0 − Vspan·tanh(Kloop·(Idd − Iset)/Vspan) through 1 mΩ, into a GaN FET's
/// gate through a 100 nH choke), at 2 GHz into 50 Ω. Its Jacobian is right; the line search judging ‖F‖ in amperes vetoed every
/// step from the DC seed, and a failed solve with control currents is now retried judging either norm (HbNewton.Solve).
/// </summary>
public class HbControlLoopColdStartTests(ITestOutputHelper output)
{
    private const string Circuit = """

VGS0 = -3.05
VDS = 48
Iset = 0.0614
Kloop = 200
dVt = 0
RFfreq = 2 GHz
Pin = 0

P1Tone:SRC  src  0  Num=1  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq
C:CBG  src  g  C=100 pF
L:LG  vg  g  L=100 nH
SDD:BIAS  vg  0  I[1,0]=(_v1-VGS0-Vspan*tanh(Kloop*(Iset-_c1)/Vspan))/Rc  C[1]=IDD  Rc=1e-3  Vspan=1
SDD:FET  g  0  d  0  Periphery_mm=1.25  Sv=-0.837  Sc=0.71  TV0=4.268+dVt  TC=1.507  th=0.001  a=0.176  g=0.089  lam=0.0012  B=1130  Isg=1e-10  Vtg=0.1  Ibk=1e-6  Vbr=150  Vbk=5  Cgsp=0.3e-12  Cgs0=1.0e-12  Kgs=1.5  Vgs0=-3  Cdsm=0.2e-12  Cds0=0.5e-12  Vds0=10  Dgs=0.3  Vgsd=20  I[1,0]=_v1/50+Periphery_mm*Isg*(if(_v1/Vtg<40,exp(_v1/Vtg),exp(40)*(1+_v1/Vtg-40))-1)-Periphery_mm*Ibk*if((_v2-_v1-Vbr)/Vbk<40,exp((_v2-_v1-Vbr)/Vbk),exp(40)*(1+(_v2-_v1-Vbr)/Vbk-40))  I[2,0]=Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0-_v1+_v2*th+Sc*ln(exp(-(Sv-_v1)/Sc)+1)))+1))*ln(exp(-(2*TV0-2*_v1+2*_v2*th+2*Sc*ln(exp(-(Sv-_v1)/Sc)+1))/TC)+1)*(_v2*lam+1))/2+Periphery_mm*Ibk*if((_v2-_v1-Vbr)/Vbk<40,exp((_v2-_v1-Vbr)/Vbk),exp(40)*(1+(_v2-_v1-Vbr)/Vbk-40))  I[1,1]=Periphery_mm*(Cgsp*_v1+Cgs0/2*(_v1+ln(cosh(Kgs*(_v1-Vgs0)))/Kgs)*(1+Dgs*tanh(-_v2/Vgsd)))  I[2,1]=Periphery_mm*(Cdsm*_v2+(Cds0-Cdsm)*Vds0*(1-exp(-_v2/Vds0)))
SDD:CGD  g  d  Periphery_mm=1.25  Cgdm=0.04e-12  Cgd0=0.15e-12  Vgd0=8  I[1,1]=Periphery_mm*(Cgdm*_v1-(Cgd0-Cgdm)*Vgd0*(1-exp(_v1/Vgd0)))
Vdc:VDD  vdd  0  Vdc=VDS V
IProbe:IDD  vdd  vds
L:LD  vds  d  L=100 nH
C:CBD  d  o  C=100 pF
R:RL  o  0  R=50 Ohm
""";

    private static (ElaboratedNetlist Nl, TestBench Tb, HbAnalysisParams P, HbEngine Engine) Make(double kloop, double pin, string keys = "")
    {
        string cnl = Circuit.Replace("Kloop = 200", $"Kloop = {kloop}").Replace("Pin = 0", $"Pin = {pin}")
                     + $"\nanalysis HB1 type=hb Tone=RFfreq MaxHarm=5 DriveStepping=Never {keys}\n";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        return (nl, tb, HbEngine.Resolve(hba, nl.ResolvedGlobals), new HbEngine(nl, tb));
    }

    /// <summary>Gate 1 (R-hbcl-1) — the analytic Jacobian, control-current block included, agrees with the FD oracle at the DC seed
    /// and at the solution, each entry against its own row. The bias node's row holds ForcedDc's ~1e6 S, so the oracle's relative
    /// metric reports its roundoff instead (measured 4e-2 at ε = 1e-6, 3e-3 at 1e-5, 3.6e-1 at 1e-7: 1/ε). Without J_cc the same
    /// metric sees the missing block.</summary>
    [Fact]
    public void TheJacobian_AgreesWithFiniteDifference_AtTheDcSeedAndAtTheSolution()
    {
        var (nl, _, p, engine) = Make(200, 10);
        var ext = new HbLinearExtractor(nl, AnalysisSettings.Default);
        int N = ext.InterfaceCount, K = p.MaxHarmonic;
        var dc = NonlinearDcEngine.Run(nl);
        var seed = new Complex[N, K + 1];
        for (int n = 0; n < N; n++)
        {
            int c = ext.InterfaceNodes[n];
            seed[n, 0] = c > 0 ? dc.NodeVoltages[c - 1] : 0;
            for (int k = 1; k <= K; k++) seed[n, k] = new Complex(1e-3, 1e-3);   // HbEngine's cold seed
        }
        var run = Make(200, 10).Engine.Run(p);
        Assert.True(run.Converged);

        var atSeed = engine.RunJacobianDiagnostic(p, seed, 0.0);
        var atSolution = engine.RunJacobianDiagnostic(p, run.InterfaceV!, 0.0);
        var withoutJcc = engine.RunJacobianDiagnostic(p, run.InterfaceV!, 0.0, useControlJacobian: false);
        output.WriteLine($"row-scaled: seed {atSeed.MaxRowScaledError:E2}, solution {atSolution.MaxRowScaledError:E2}, " +
                         $"solution without J_cc {withoutJcc.MaxRowScaledError:E2} (relative, floored: {atSolution.MaxRelError:E2})");
        Assert.True(atSeed.MaxRowScaledError < 1e-5, $"at the seed {atSeed.MaxRowScaledError:E2}");
        Assert.True(atSolution.MaxRowScaledError < 1e-5, $"at the solution {atSolution.MaxRowScaledError:E2}");
        Assert.True(withoutJcc.MaxRowScaledError > 1e-3, $"without J_cc {withoutJcc.MaxRowScaledError:E2}");
    }

    /// <summary>Gate 2 (R-hbcl-2) — from cold at loop gains 50, 200 and 1,000 V/A and 10, 20 and 30 dBm, each equal to the solution
    /// reached by raising Kloop from 0 with each point warm-started from the last.</summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(30.0)]
    public void TheBiasLoop_ConvergesFromCold_AndEqualsTheWarmStartedSolution(double pin)
    {
        Complex[,]? warm = null;
        var warmAt = new Dictionary<double, Complex[,]>();
        foreach (double kloop in new[] { 0.0, 5, 20, 50, 100, 200, 400, 700, 1000 })
        {
            var (_, _, p, engine) = Make(kloop, pin);
            var r = engine.Run(p, warm);
            Assert.True(r.Converged, $"warm ramp at Kloop {kloop}");
            warm = warmAt[kloop] = r.InterfaceV!;
        }
        foreach (double kloop in new[] { 50.0, 200, 1000 })
        {
            var (_, _, p, engine) = Make(kloop, pin);
            var r = engine.Run(p);
            Assert.True(r.Converged, $"cold at Kloop {kloop}, {pin} dBm");
            double worst = 0;
            var w = warmAt[kloop];
            for (int n = 0; n < w.GetLength(0); n++)
                for (int k = 0; k < w.GetLength(1); k++) worst = Math.Max(worst, (w[n, k] - r.InterfaceV![n, k]).Magnitude);
            output.WriteLine($"{pin} dBm, Kloop {kloop}: {r.Trace!.TotalIterations} iterations, max |dV| {worst:E2} V, vg {r.InterfaceV![1, 0].Real:F4} V");
            Assert.True(worst < 1e-4, $"cold and warm differ by {worst:E2} V at Kloop {kloop}, {pin} dBm");
        }
    }

    /// <summary>Gate 4 (R-hbcl-3) — a sweep point that did not converge (MaxIter=3 at 30 dBm) keeps Converged 0 and its residual,
    /// and its spectra and every measurement read from them are NaN; a converged point beside it is a number.</summary>
    [Fact]
    public void AnUnconvergedSweepPoint_ReadsNaNInItsMeasurements()
    {
        string cnl = Circuit.Replace("Kloop = 200", "Kloop = 0")
                     + "\nanalysis HB1 type=hb Tone=RFfreq MaxHarm=5 DriveStepping=Never MaxIter=3\n"
                     + "analysis DRIVE type=parametric_sweep Var=Pin Values=-100,30 Inner=HB1\n"
                     + "measure Vd1 = mag(HB1.V(\"d\", 1))\n";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb);
        var meas = new DataSet();
        Assert.Empty(new MeasurementEvaluator(tb, nl, new Dictionary<string, DataSet> { ["HB1"] = ds }).EvaluateInto(meas));
        Assert.Equal([1.0, 0.0], ds["Converged"].RealValues);
        Assert.True(double.IsFinite(ds["Residual"].RealValues[1]) && ds["Residual"].RealValues[1] > 0);
        var vd1 = meas["Vd1"].RealValues;
        output.WriteLine($"Vd1: {vd1[0]:G6}, {vd1[1]:G6}");
        Assert.True(double.IsFinite(vd1[0]));
        Assert.True(double.IsNaN(vd1[1]));
    }
}
