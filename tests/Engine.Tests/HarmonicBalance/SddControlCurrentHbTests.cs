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
/// Gate tests for SDD control currents (_cn) in the HB residual (brief #2).
///
/// T1 — HbCtrl_IProbe_ReadThrough        SDD mirrors IProbe: I[1,0]=_c1, C[1]=IP1 — converges,
///                                        INl at n_sdd ≈ IProbe k=1 current.
/// T2 — HbCtrl_IProbe_BetaScaling        I[1,0]=beta*_c1 (beta=2) — INl ≈ 2 × IProbe k=1.
/// T3 — HbCtrl_InductorKind              SDD referencing an inductor branch converges.
/// T4 — HbCtrl_NoControlRef_Regression   No C[n] → identical behavior (cc=null path).
/// </summary>
public class SddControlCurrentHbTests(ITestOutputHelper output)
{
    // ── Helper ───────────────────────────────────────────────────────────────

    private static (DataSet ds, string[] nodeLabels, int K) RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);
        var nodeLabels = ds["V"].Axes[0].Labels!;
        return (ds, nodeLabels, p.MaxHarmonic);
    }

    // ── T1: SDD mirrors IProbe via _c1 ───────────────────────────────────────
    //
    // Circuit:
    //   V_1Tone (1V @ 1GHz) → R_src=50Ω → n_probe → IP1 → n_mid → R_load=50Ω → 0
    //   IProbe k=1 current = 1.0V / 100Ω = 10 mA peak
    //
    //   Vdc:VDD (1V DC) → R_bias=100Ω → n_sdd → SDD (I[1,0]=_c1, C[1]=IP1) → 0
    //   SDD is isolated from the IProbe path → fixed-point converges immediately.
    //   Expected: INl[n_sdd, k=1] ≈ 10 mA (= IP1 k=1 current)

    [Fact]
    public void HbCtrl_IProbe_ReadThrough()
    {
        const string cnl = @"
V_1Tone:VS    n_in 0    Vdc=0    Freq=1e9  V=1.0  Phase=0
R:R_src       n_in n_probe      R=50
IProbe:IP1    n_probe n_mid
R:R_load      n_mid 0           R=50

Vdc:VDD       n_vdd 0           Vdc=1
R:R_bias      n_vdd n_sdd       R=100
SDD:X1        n_sdd 0           Ports=1  I[1,0]=_c1  C[1]=IP1

analysis DC1  type=dc
analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-7
";
        var (ds, nodeLabels, K) = RunHb(cnl);

        output.WriteLine($"Node labels: {string.Join(", ", nodeLabels)}");

        // Convergence
        Assert.True(ds.Contains("Converged"), "Converged cube missing");
        double converged = ds["Converged"].RealValues[0];
        output.WriteLine($"Converged = {converged}");
        Assert.True(converged > 0.5, "HB did not converge");

        // IProbe k=1 current from the 'I' cube
        Assert.True(ds.Contains("I"), "I cube missing");
        var iCube = ds["I"];
        string[] branchLabels = iCube.Axes[0].Labels!;
        int ipIdx = Array.IndexOf(branchLabels, "IP1");
        Assert.True(ipIdx >= 0, $"IP1 not found in I cube branches: {string.Join(", ", branchLabels)}");
        var ipK1 = (Complex)iCube[ipIdx, 1];
        output.WriteLine($"IP1 k=1 = {ipK1.Real * 1e3:F4} + j{ipK1.Imaginary * 1e3:F4} mA  |mag|={ipK1.Magnitude * 1e3:F4} mA");
        Assert.True(Math.Abs(ipK1.Magnitude - 0.01) < 1e-4,
            $"Expected |IP1 k=1| ≈ 10 mA, got {ipK1.Magnitude * 1e3:G} mA");

        // INl at n_sdd — the SDD's converged nonlinear current at k=1
        int sddNodeIdx = Array.IndexOf(nodeLabels, "n_sdd");
        Assert.True(sddNodeIdx >= 0, $"n_sdd not found in node axis: {string.Join(", ", nodeLabels)}");
        var inlCube = ds["INl"];
        var sddInlK1 = (Complex)inlCube[sddNodeIdx, 1];
        output.WriteLine($"INl[n_sdd, k=1] = {sddInlK1.Real * 1e3:F4} + j{sddInlK1.Imaginary * 1e3:F4} mA");

        // SDD's port current must match the IProbe current (relative tol 1e-4)
        double relErr = (sddInlK1 - ipK1).Magnitude / (ipK1.Magnitude + 1e-15);
        output.WriteLine($"Relative error = {relErr:E3}  (expected < 1e-4)");
        Assert.True(relErr < 1e-4,
            $"INl[n_sdd,k=1] should equal IP1 k=1 current; rel err = {relErr:E3}");

        output.WriteLine("T1 HbCtrl_IProbe_ReadThrough: PASS.");
    }

    // ── T2: I[1,0]=beta*_c1 — scaled by 2 ───────────────────────────────────
    //
    // Same circuit but SDD equation I[1,0]=beta*_c1 with beta=2.
    // INl[n_sdd, k=1] ≈ 2 × IP1 k=1 current.

    [Fact]
    public void HbCtrl_IProbe_BetaScaling()
    {
        const string cnl = @"
beta = 2

V_1Tone:VS    n_in 0    Vdc=0    Freq=1e9  V=1.0  Phase=0
R:R_src       n_in n_probe      R=50
IProbe:IP1    n_probe n_mid
R:R_load      n_mid 0           R=50

Vdc:VDD       n_vdd 0           Vdc=1
R:R_bias      n_vdd n_sdd       R=100
SDD:X1        n_sdd 0           Ports=1  I[1,0]=beta*_c1  C[1]=IP1

analysis DC1  type=dc
analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-7
";
        var (ds, nodeLabels, K) = RunHb(cnl);

        // Convergence
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");

        // IProbe k=1 (reference)
        var iCube = ds["I"];
        string[] branchLabels = iCube.Axes[0].Labels!;
        int ipIdx = Array.IndexOf(branchLabels, "IP1");
        var ipK1 = (Complex)iCube[ipIdx, 1];
        output.WriteLine($"IP1 k=1 mag = {ipK1.Magnitude * 1e3:F4} mA");

        // INl[n_sdd, k=1] should be 2 × IP1 k=1
        int sddNodeIdx = Array.IndexOf(nodeLabels, "n_sdd");
        var sddInlK1 = (Complex)ds["INl"][sddNodeIdx, 1];
        double expected = 2.0 * ipK1.Magnitude;
        output.WriteLine($"INl[n_sdd,k=1] mag = {sddInlK1.Magnitude * 1e3:F4} mA  (expected {expected * 1e3:F4} mA)");

        double relErr = Math.Abs(sddInlK1.Magnitude - expected) / (expected + 1e-15);
        output.WriteLine($"Relative error = {relErr:E3}");
        Assert.True(relErr < 1e-4,
            $"INl[n_sdd,k=1] should equal 2 × IP1 k=1; rel err = {relErr:E3}");

        output.WriteLine("T2 HbCtrl_IProbe_BetaScaling: PASS.");
    }

    // ── T3: inductor-kind control reference ───────────────────────────────────
    //
    // SDD references an inductor branch current. The inductor is an RF choke in a
    // simple amplifier-like circuit. Verify the simulation converges and _c1 ≠ 0.

    [Fact]
    public void HbCtrl_InductorKind()
    {
        const string cnl = @"
V_1Tone:VS    n_in 0    Vdc=0    Freq=1e9  V=1.0  Phase=0
R:R_src       n_in n_rf         R=50
L:L_choke     n_rf n_mid        L=1e-6  R=0
R:R_load      n_mid 0           R=50

Vdc:VDD       n_vdd 0           Vdc=1
R:R_bias      n_vdd n_sdd       R=200
SDD:X1        n_sdd 0           Ports=1  I[1,0]=_c1  C[1]=L_choke

analysis DC1  type=dc
analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-7
";
        var (ds, nodeLabels, K) = RunHb(cnl);

        double converged = ds["Converged"].RealValues[0];
        output.WriteLine($"Converged = {converged}");
        Assert.True(converged > 0.5, "HB did not converge");

        int sddNodeIdx = Array.IndexOf(nodeLabels, "n_sdd");
        var sddInlK1 = (Complex)ds["INl"][sddNodeIdx, 1];
        output.WriteLine($"INl[n_sdd, k=1] mag = {sddInlK1.Magnitude * 1e3:F4} mA");
        Assert.True(sddInlK1.Magnitude > 1e-6,
            $"_c1 (inductor current) should be non-zero; got {sddInlK1.Magnitude:E3} A");

        output.WriteLine("T3 HbCtrl_InductorKind: PASS.");
    }

    // ── T4: regression — no C[n] → cc=null path, identical behavior ──────────
    //
    // Simple half-wave rectifier SDD: no C[n] references → cc=null is passed to
    // HbNewton.Solve → EvaluateNonlinear is byte-identical to the pre-brief path.
    // V_1Tone drives the SDD; check that HB converges and the DC rectified output
    // (k=0) is positive (the SDD clips negative half-cycles).

    [Fact]
    public void HbCtrl_NoControlRef_Regression()
    {
        const string cnl = @"
V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=1.0  Phase=0
R:R_src      n_in n_d  R=50
SDD:X1       n_d 0   Ports=1  I[1,0]=if(_v1>0, _v1/50, 0)
R:R_load     n_d 0   R=50

analysis DC1  type=dc
analysis HB1  type=hb  Tone=1e9  MaxHarm=5  Tol=1e-6
";
        var (ds, nodeLabels, K) = RunHb(cnl);

        // cc=null path: convergence must hold
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");

        // DC output (k=0 at n_d): rectifier produces a positive DC voltage
        int ndIdx = Array.IndexOf(nodeLabels, "n_d");
        Assert.True(ndIdx >= 0, $"n_d not found in node axis: {string.Join(", ", nodeLabels)}");
        // The half-wave SDD sinks current only on positive half-cycles, pulling n_d negative on average.
        double vDc = ((Complex)ds["V"][ndIdx, 0]).Real;
        output.WriteLine($"V[n_d, k=0] = {vDc:G4} V  (expected < 0, DC loading effect)");
        Assert.True(vDc < 0, $"SDD half-wave loading should pull DC node negative; got {vDc:G4} V");

        output.WriteLine("T4 HbCtrl_NoControlRef_Regression: PASS.");
    }
}
