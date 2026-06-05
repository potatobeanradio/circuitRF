using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Phase 4b code-review diagnostic — Pass A (Phase4b_CodeReview_Brief.md §A2).
///
/// Runs Hero 3 at a KNOWN operating point and prints V, INl, all FOMs, and Zin/Zsource
/// WITH UNITS AND SIGNS so the owner can verify against independent hand calculations.
///
/// The convention being tested (HbEngine.cs and HarmonicBalance/CLAUDE.md):
///   INl[n,k] = current FROM node n INTO the nonlinear device (passive sign convention).
///
/// Expected physics (from circuit analysis):
///   At RF (k=1), choke is open-circuit.
///   KCL n_drain: I_into_load = −INl[drain,1]
///   KCL n_gate:  I_from_source = +INl[gate,1]
///
///   ⟹ Pout = −½Re(V[drain]·INl[drain]*) > 0  for an amplifier
///   ⟹ Pin  = +½Re(V[gate]·INl[gate]*)  > 0  for power flowing in
///   ⟹ Zin  = V[gate,1] / INl[gate,1]  (no negation — Zin is passive, Re(Zin) > 0)
///   ⟹ Zsource = conj(Zin)
///
/// Run with:  dotnet test --filter SignConvention_Diagnostic
/// </summary>
public class SignConventionDiagnosticTests(ITestOutputHelper output)
{
    private static string Hero3Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero3");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero3 not found");
    }

    [Fact]
    public void SignConvention_Diagnostic()
    {
        var dir    = Hero3Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3_at_compression.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var lpa       = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var p         = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);

        var engine = new LoadpullEngine(netlist, tb);
        var ctx    = engine.PrepareContext(p);

        // --- Pick a diagnostic operating point ---
        // Use grid point 0 (Z_load=50Ω, the simplest case) at Pin=0 dBm.
        // At this point the FET should be moderately compressed (Hero 3 at_compression with PinMax=30).
        // The exact Pin doesn't matter — we report what the code produces so the owner can hand-check.

        // Find the grid point with Z ≈ 50+0j (grid 0 per the .gam file header).
        var gp0 = p.Grid.Points[0];
        var z0  = gp0.Z;
        output.WriteLine($"Diagnostic termination: Z={z0.Real:F2}+j{z0.Imaginary:F2} Ω  " +
                         $"Γ={gp0.Gamma.Real:F4}+j{gp0.Gamma.Imaginary:F4}");
        output.WriteLine($"f0 = {p.ToneHz/1e9:F3} GHz  K = {p.MaxHarmonic}");
        output.WriteLine($"Bias: Vdd=48V, Vgg=-3.05V");
        output.WriteLine("─────────────────────────────────────────────────────────────────");

        // Run the full inner Pin sweep at this termination.
        var gpr = engine.RunOneTermination(p, ctx, z0, gridIndex: 0);

        // Print every converged step.
        output.WriteLine("Pin[dBm]  Pout[dBm]  Gt[dB]  Gp[dB]  DE[%]  PAE[%]");
        foreach (var s in gpr.PinSteps.Where(s => s.Converged && !s.IsTickle))
        {
            output.WriteLine(
                $"  {s.PavlDbm,5:F1}  {10*Math.Log10(s.PoutW*1e3),8:F2}  " +
                $"{s.GtDb,6:F2}  {s.GpDb,6:F2}  {s.De*100,5:F1}  {s.Pae*100,5:F1}");
        }

        output.WriteLine("─────────────────────────────────────────────────────────────────");

        // Pick the step closest to 10 dBm for the detailed diagnostic.
        var diagStep = gpr.PinSteps
            .Where(s => s.Converged && !s.IsTickle)
            .OrderBy(s => Math.Abs(s.PavlDbm - 10.0))
            .FirstOrDefault();

        if (diagStep is null)
        {
            output.WriteLine("No converged non-tickle steps available.");
            return;
        }

        output.WriteLine($"Detailed diagnostic at Pin≈{diagStep.PavlDbm:F1} dBm:");
        output.WriteLine("");

        // ── Raw V and INl at k=0 (DC) and k=1 (f0) ───────────────────────────
        int loadIdx = ctx.LoadIfIdx;
        int srcIdx  = ctx.SrcIfIdx;

        output.WriteLine("Node indices: loadIdx=" + loadIdx + " (drain), srcIdx=" + srcIdx + " (gate)");
        output.WriteLine("");

        // DC values (k=0).
        double vDrainDC   = diagStep.V[loadIdx, 0].Real;
        double iNlDrainDC = diagStep.INl[loadIdx, 0].Real;
        double vGateDC    = diagStep.V[srcIdx, 0].Real;
        double iNlGateDC  = diagStep.INl[srcIdx, 0].Real;

        output.WriteLine("── DC (k=0) — verifying INl convention and Pdc ──");
        output.WriteLine($"  V[drain,0] = {vDrainDC:F6} V    (expected ≈ Vdd = 48 V)");
        output.WriteLine($"  INl[drain,0] = {iNlDrainDC*1e3:F3} mA  " +
                         "(expected +Idd > 0: drain current leaving n_drain into FET)");
        output.WriteLine($"  V[gate,0]  = {vGateDC:F6} V    (expected ≈ Vgg = −3.05 V)");
        output.WriteLine($"  INl[gate,0] = {iNlGateDC*1e3:F3} mA  " +
                         "(expected: small, sign depends on SDD gate conductance convention)");
        double pdcDrain = vDrainDC * iNlDrainDC;
        double pdcGate  = vGateDC  * iNlGateDC;
        double pdcTotal = pdcDrain + pdcGate;
        output.WriteLine($"  Pdc_drain = V[drain]·INl[drain] = {pdcDrain:F3} W  " +
                         "(expected > 0: supply provides power)");
        output.WriteLine($"  Pdc_gate  = V[gate]·INl[gate]  = {pdcGate:F3} W  " +
                         "(typically small; may be positive or negative for gate supply)");
        output.WriteLine($"  Pdc_total = {pdcTotal:F3} W");
        output.WriteLine($"  Stored PdcW = {diagStep.PdcW:F3} W  " +
                         "(should equal Pdc_total above)");
        output.WriteLine("");

        // RF values (k=1).
        Complex vDrainRF   = diagStep.V[loadIdx, 1];
        Complex iNlDrainRF = diagStep.INl[loadIdx, 1];
        Complex vGateRF    = diagStep.V[srcIdx, 1];
        Complex iNlGateRF  = diagStep.INl[srcIdx, 1];

        output.WriteLine("── RF (k=1) — verifying Pout, Pin, Zin ──");
        output.WriteLine($"  V[drain,1]   = {vDrainRF.Real:F4}+j{vDrainRF.Imaginary:F4} V " +
                         $" |V|={vDrainRF.Magnitude:F4}");
        output.WriteLine($"  INl[drain,1] = {iNlDrainRF.Real:F6}+j{iNlDrainRF.Imaginary:F6} A " +
                         $" |I|={iNlDrainRF.Magnitude:F6}");
        output.WriteLine($"  V[gate,1]    = {vGateRF.Real:F4}+j{vGateRF.Imaginary:F4} V " +
                         $" |V|={vGateRF.Magnitude:F4}");
        output.WriteLine($"  INl[gate,1]  = {iNlGateRF.Real:F6}+j{iNlGateRF.Imaginary:F6} A " +
                         $" |I|={iNlGateRF.Magnitude:F6}");
        output.WriteLine("");

        // Pout.
        double poutCode    = -0.5 * (vDrainRF * Complex.Conjugate(iNlDrainRF)).Real;
        double poutCodeDbm = 10 * Math.Log10(poutCode * 1e3);
        output.WriteLine($"  Pout (code: −½Re(V[drain]·INl[drain]*)) = {poutCode*1e3:F2} mW  = {poutCodeDbm:F2} dBm");
        output.WriteLine($"  Stored PoutW = {diagStep.PoutW*1e3:F2} mW  (should match above)");
        output.WriteLine($"  Pout > 0? {poutCode > 0}  (must be true for a PA)");
        output.WriteLine("");

        // Pin.
        double pinCode = 0.5 * (vGateRF * Complex.Conjugate(iNlGateRF)).Real;
        output.WriteLine($"  Pin_delivered (code: +½Re(V[gate]·INl[gate]*)) = {pinCode*1e3:F2} mW");
        output.WriteLine($"  Stored PinDelivW = {diagStep.PinDeliveredW*1e3:F2} mW  (should match)");
        output.WriteLine($"  Pin > 0? {pinCode > 0}  (must be true: source delivers power)");
        output.WriteLine("");

        // Gt and Gp.
        double gtDb = 10 * Math.Log10(poutCode / diagStep.PavlW);
        double gpDb = pinCode > 1e-30 ? 10 * Math.Log10(poutCode / pinCode) : double.NaN;
        output.WriteLine($"  Gt = Pout/Pavl  = {gtDb:F2} dB  (stored: {diagStep.GtDb:F2} dB)");
        output.WriteLine($"  Gp = Pout/Pin   = {gpDb:F2} dB  (stored: {diagStep.GpDb:F2} dB)");
        output.WriteLine("");

        // DE and PAE.
        double de  = pdcTotal > 1e-6 ? poutCode / pdcTotal : double.NaN;
        double pae = pdcTotal > 1e-6 ? (poutCode - pinCode) / pdcTotal : double.NaN;
        output.WriteLine($"  DE  = Pout/Pdc = {de*100:F1}%  (stored: {diagStep.De*100:F1}%)");
        output.WriteLine($"  PAE = (Pout-Pin)/Pdc = {pae*100:F1}%  (stored: {diagStep.Pae*100:F1}%)");
        output.WriteLine("");

        // Zin — two ways: code formula and correct formula from convention.
        Complex zinCode    = vGateRF / (-iNlGateRF);   // current code (negated)
        Complex zinCorrect = vGateRF / iNlGateRF;       // correct from convention
        output.WriteLine($"  Zin (code: V/−INl)   = {zinCode.Real:F3}+j{zinCode.Imaginary:F3} Ω  " +
                         $"Re={zinCode.Real:F3}  (expected: negative Re ← BUG if so)");
        output.WriteLine($"  Zin (correct: V/INl) = {zinCorrect.Real:F3}+j{zinCorrect.Imaginary:F3} Ω  " +
                         $"Re={zinCorrect.Real:F3}  (expected: positive Re for passive input)");
        output.WriteLine($"  Zsource_code    = {Complex.Conjugate(zinCode).Real:F3}+j{Complex.Conjugate(zinCode).Imaginary:F3} Ω");
        output.WriteLine($"  Zsource_correct = {Complex.Conjugate(zinCorrect).Real:F3}+j{Complex.Conjugate(zinCorrect).Imaginary:F3} Ω");
        output.WriteLine("");

        // Assertions that MUST hold by physics.
        output.WriteLine("── Assertion checks (physics) ──");
        Assert.True(poutCode > 0,       "Pout must be positive for an amplifier.");
        Assert.True(pinCode  > 0,       "Pin_delivered must be positive (source delivers power).");
        Assert.True(pdcTotal > 0,       "Pdc must be positive (supply provides power).");
        Assert.True(de  > 0 && de  < 1, $"DE must be in (0,1); got {de*100:F1}%");
        Assert.True(pae >= 0,           $"PAE must be ≥ 0; got {pae*100:F1}%");

        // Report Zin sign — this is the known bug; assert the CORRECT formula gives Re(Zin)>0.
        output.WriteLine($"  Re(Zin_correct) = {zinCorrect.Real:F3} — should be > 0 (passive input)");
        output.WriteLine($"  Re(Zin_code)    = {zinCode.Real:F3}    — should be < 0 (the sign bug)");
        Assert.True(zinCorrect.Real > 0,
            $"Correct Zin = V[gate]/INl[gate] must have positive real part; got {zinCorrect.Real:F3}");
        // Also verify the code's formula gives wrong sign (documents the bug).
        output.WriteLine($"  Code bug confirmed: Re(Zin_code) {(zinCode.Real < 0 ? "<" : "≥")} 0" +
                         $" (should be < 0 to confirm the bug)");

        output.WriteLine("");
        output.WriteLine("── Summary for owner hand-verification ──");
        output.WriteLine($"  At Z_load={z0.Real:F0}+j{z0.Imaginary:F0}Ω, Pin={diagStep.PavlDbm:F1} dBm:");
        output.WriteLine($"    Pavl     = {diagStep.PavlW*1e3:F3} mW = {diagStep.PavlDbm:F1} dBm");
        output.WriteLine($"    Pout     = {diagStep.PoutW*1e3:F3} mW = {10*Math.Log10(diagStep.PoutW*1e3):F2} dBm");
        output.WriteLine($"    Pin_del  = {diagStep.PinDeliveredW*1e3:F3} mW");
        output.WriteLine($"    Gt       = {diagStep.GtDb:F2} dB");
        output.WriteLine($"    Gp       = {diagStep.GpDb:F2} dB");
        output.WriteLine($"    Pdc      = {diagStep.PdcW*1e3:F3} mW");
        output.WriteLine($"    DE       = {diagStep.De*100:F2}%");
        output.WriteLine($"    PAE      = {diagStep.Pae*100:F2}%");
        output.WriteLine($"    Vdrain   = {vDrainRF.Real:F4}+j{vDrainRF.Imaginary:F4} V  (phasor at f0)");
        output.WriteLine($"    INl_drain= {iNlDrainRF.Real:F5}+j{iNlDrainRF.Imaginary:F5} A");
        output.WriteLine($"    Vgate    = {vGateRF.Real:F6}+j{vGateRF.Imaginary:F6} V");
        output.WriteLine($"    INl_gate = {iNlGateRF.Real:F6}+j{iNlGateRF.Imaginary:F6} A");
        output.WriteLine($"    Zin(correct) = {zinCorrect.Real:F3}+j{zinCorrect.Imaginary:F3} Ω");
        output.WriteLine($"    Zsource(correct) = {Complex.Conjugate(zinCorrect).Real:F3}+j{Complex.Conjugate(zinCorrect).Imaginary:F3} Ω");
        output.WriteLine($"    Zin(code — buggy) = {zinCode.Real:F3}+j{zinCode.Imaginary:F3} Ω");

        // Clean up tuner state.
        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);
    }
}
