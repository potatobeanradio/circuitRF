using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Regression for the loadpull/pursuit Zin bug: Zin / Zsource / Pin_delivered must divide by the
/// TRUE current the source delivers into the DUT input node (which includes any passives the user
/// wired at the gate), not by the device's INl[gate] alone.
///
/// Before the fix, a gate carrying an SDD with `I[1,0]=_v1/5000` (a 5000 Ω intrinsic gate) reported
/// Zin ≈ 5000 Ω regardless of what else was attached — because it used INl[gate] = V/5000. With a
/// shunt at the gate the source actually delivers INl[gate] + V/Rshunt, so the impedance the source
/// sees is much lower. The engine now recovers that via the source tuner's Z_Port and choke branch
/// currents (HB linear back-solver) → ISrcIn = I_Zport − I_choke.
/// </summary>
public class LoadpullZinPassivesTests(ITestOutputHelper output)
{
    // The user's MyFET SDD (5000 Ω intrinsic gate via I[1,0]=_v1/5000), plus a SourceTuner/LoadTuner
    // and — optionally — a shunt resistor Rg from the gate node to ground. A loadpull_pursuit directive
    // (CreateLoadpullResult=false: we drive RunOneTermination directly, no follow-on).
    private static string BuildCnl(bool withGateShunt, double rgOhm)
    {
        string shunt = withGateShunt ? $"R:Rg1  n1  0  R={rgOhm.ToString(System.Globalization.CultureInfo.InvariantCulture)} Ohm\n" : "";
        return
            "define MyFET (gate drain)\n" +
            "  parameters Periphery_mm=1\n" +
            "  Sv = -0.837\n  Sc = 0.71\n  TV0 = 4.268\n  TC = 1.507\n  th = 0.001\n" +
            "  a = 0.176\n  g = 0.089\n  lam = 0.0012\n  B = 1130\n" +
            "  SDD:X1  gate  0  drain  0  I[1,0]=_v1/5000  " +
            "I[2,0]=Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*" +
            "ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2\n" +
            "end MyFET\n\n" +
            "RFfreq = 2.2 GHz\n" +
            "Vgs = -3.05\n" +
            "VDD = 48\n\n" +
            "MyFET:X1  n1  n2  Periphery_mm=1\n" +
            "Tuner:SourceTuner1  n1  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=Vgs  Z[1]=50\n" +
            "Tuner:LoadTuner1  n2  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VDD  Z[1]=50\n" +
            shunt +
            "analysis LPP1 type=loadpull_pursuit Tone=\"RFfreq\" ToneUnit=GHz MaxHarm=3 " +
            "LoadTuner=LoadTuner1 SourceTuner=SourceTuner1 Sweep=Load TuneHarm=1 Compression=3 " +
            "GainType=Gt PinStart=-5 PinStep=2.5 PinMax=10 MaxIter=100 Tol=1e-7 " +
            "SearchMethod=IteratedQuadratic CreateLoadpullResult=false\n";
    }

    private static (LoadpullEngine eng, LoadpullAnalysisParams lpp) Setup(string cnl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"zin_passives_{Guid.NewGuid():N}.cnl");
        File.WriteAllText(path, cnl);
        try
        {
            var (lib, tb) = CnlReader.ReadFile(path);
            var netlist   = new Elaborator(lib).Elaborate(tb);
            var lpa = tb.Analyses.OfType<CircuitRF.Core.Design.LoadpullPursuitAnalysis>().First();
            var pp  = LoadpullPursuitEngine.Resolve(lpa, netlist.ResolvedGlobals);
            return (new LoadpullEngine(netlist, tb), pp.LpParams);
        }
        finally { try { File.Delete(path); } catch { /* ignore */ } }
    }

    private static PinStepResult? PickStep(GridPointResult gpr) =>
        gpr.PinSteps.Where(s => s.Converged && !s.IsTickle)
                    .OrderByDescending(s => s.PavlDbm)   // highest drive → largest, least-noisy currents
                    .FirstOrDefault();

    // ── The fix: with a shunt at the gate, ISrcIn obeys KCL (INl[gate] + V/Rg) and Zin drops ──
    [Fact]
    public void Loadpull_GateShunt_SourceCurrentObeysKcl_AndZinDropsBelowIntrinsicGate()
    {
        const double Rg = 200.0;
        var (eng, lpp) = Setup(BuildCnl(withGateShunt: true, Rg));
        var ctx = eng.PrepareContext(lpp);
        var gpr = eng.RunOneTermination(lpp, ctx, new Complex(50, 0), gridIndex: 0);

        var step = PickStep(gpr);
        Assert.NotNull(step);
        int src = ctx.SrcIfIdx;

        Complex vG    = step!.V[src, 1];
        Complex iNlG  = step.INl[src, 1];
        Complex iSrc  = step.ISrcIn[1];

        // KCL at the gate node: the source-delivered current = device gate current + shunt current.
        Complex iExpected = iNlG + vG / Rg;
        double  resid     = (iSrc - iExpected).Magnitude;
        output.WriteLine($"INl[gate,1]={iNlG}  V[gate,1]={vG}  ISrcIn[1]={iSrc}");
        output.WriteLine($"expected (INl+V/Rg)={iExpected}  residual={resid:E3}");
        Assert.True(resid < 1e-3 * iSrc.Magnitude + 1e-12,
            $"ISrcIn must equal INl[gate]+V/Rg by KCL; residual {resid:E3} too large.");

        // Zin from the corrected current is much lower than the bare 5000 Ω intrinsic gate.
        Complex zinFixed   = vG / iSrc;     // correct: source-delivered current
        Complex zinOldBug  = vG / iNlG;     // old: INl[gate] only → ≈ 5000 Ω
        output.WriteLine($"Zin(fixed)={zinFixed.Real:F1}{zinFixed.Imaginary:+0.0;-0.0}j Ω   " +
                         $"Zin(old bug)={zinOldBug.Real:F1} Ω");
        Assert.True(zinFixed.Real > 0, "Zin must have positive real part.");
        Assert.True(zinOldBug.Real > 3000, "Sanity: the old formula reports ≈ the 5000 Ω intrinsic gate.");
        Assert.True(zinFixed.Real < 500,
            $"Zin must reflect the gate shunt (5000∥200 ≈ 190 Ω), not the bare gate; got {zinFixed.Real:F1} Ω.");

        ctx.SweptModel.ClearHarmonicOverride();
    }

    // ── Backward compatibility: with nothing but the tuner + FET on the gate, ISrcIn == INl[gate] ──
    [Fact]
    public void Loadpull_CanonicalGate_SourceCurrentEqualsDeviceGateCurrent()
    {
        var (eng, lpp) = Setup(BuildCnl(withGateShunt: false, 0));
        var ctx = eng.PrepareContext(lpp);
        var gpr = eng.RunOneTermination(lpp, ctx, new Complex(50, 0), gridIndex: 0);

        // The engine captures the source impedance presented at the fundamental (the IRL reference).
        // Here the SourceTuner declares Z[1]=50, so the input return loss is referenced to 50 Ω.
        Assert.Equal(50.0, gpr.SourceZFund.Real, precision: 1);
        Assert.Equal(0.0,  gpr.SourceZFund.Imaginary, precision: 1);

        var step = PickStep(gpr);
        Assert.NotNull(step);
        int src = ctx.SrcIfIdx;

        Complex iNlG = step!.INl[src, 1];
        Complex iSrc = step.ISrcIn[1];
        double resid = (iSrc - iNlG).Magnitude;
        output.WriteLine($"INl[gate,1]={iNlG}  ISrcIn[1]={iSrc}  residual={resid:E3}");

        // No passives at the gate → the source delivers exactly the device gate current
        // → Zin reverts to V/INl[gate] (Hero references unchanged).
        Assert.True(resid < 1e-3 * iSrc.Magnitude + 1e-12,
            $"Canonical gate: ISrcIn must equal INl[gate]; residual {resid:E3} too large.");

        ctx.SweptModel.ClearHarmonicOverride();
    }
}
