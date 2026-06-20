using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Nonlinear;

/// <summary>
/// Gate tests for adding V_1Tone / V_nTone (ToneSourceModel) to the SDD control-current
/// referenceable set (brief #5). A tone source is an independent voltage source — its branch
/// current is a solved unknown, identical in structure to Vdc. Covers all three engines (DC, HB,
/// S-param), the shared V_nTone path, and the two-terminal Cport rejection.
/// </summary>
public class SddControlCurrentToneSourceTests(ITestOutputHelper output)
{
    // ── DC read-through: SDD mirrors a tone source's DC branch current ────────────────────────
    //
    //   V_1Tone:Vt1 (Vdc=2, AC inert at DC): n1 → 0 ;  R1=1kΩ n1→0  ⟹ i(Vt1) = 2mA (magnitude).
    //   Vdc:VDD (5V) → R2=500Ω → n3 → SDD(I[1,0]=_c1, C[1]=Vt1) → 0.
    //   The SDD sources i(Vt1) into n3, shifting V(n3) off 5 V by 500Ω·2mA = 1 V → V(n3)=6 V.
    [Fact]
    public void T1_Dc_ReadThrough_MirrorsToneSourceBranchCurrent()
    {
        const string cnl = @"
V_1Tone:Vt1  n1 0   Vdc=2  Freq=1e9  V=0.5  Phase=0
R:R1         n1 0   R=1000 Ohm

Vdc:VDD      n2 0   Vdc=5 V
R:R2         n2 n3  R=500 Ohm
SDD:X1       n3 0   Ports=1  I[1,0]=_c1  C[1]=Vt1

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"Did not converge. Residual={result.FinalResidual:G}");
        output.WriteLine($"NodeVoltages = [{string.Join(", ", result.NodeVoltages.Select(v => v.ToString("G6")))}]");

        // n3 = 6 V (SDD sourced the tone source's −2 mA branch current → +2 mA into n3 → +1 V).
        double vn3 = result.NodeVoltages.FirstOrDefault(v => Math.Abs(v - 6.0) < 0.1);
        Assert.True(Math.Abs(vn3 - 6.0) < 1e-6, $"Expected V(n3) ≈ 6 V (|i_Vt1|=2 mA mirrored), got {vn3:G}");
        output.WriteLine("T1_Dc_ReadThrough_MirrorsToneSourceBranchCurrent: PASS.");
    }

    // ── HB mirror: SDD drain current spectrum = beta × tone-source branch current ──────────────
    //
    //   V_1Tone:Vt1 drives a series loop with IProbe IP1 (series ⇒ i(IP1) ≡ i(Vt1) exactly).
    //   SDD I[1,0]=beta*_c1, C[1]=Vt1 in an isolated Vdc-fed branch (clean fixed point).
    //   Expect |INl[n_sdd, k=1]| ≈ beta·|I(IP1, k=1)|.
    [Fact]
    public void T2_Hb_Mirror_ScalesToneSourceBranchSpectrum()
    {
        const string cnl = @"
beta = 2

V_1Tone:Vt1   n_in 0    Vdc=0  Freq=1e9  V=1.0  Phase=0
R:R_src       n_in n_a  R=10
IProbe:IP1    n_a n_b
R:R_load      n_b 0     R=50

Vdc:VDD       n_vdd 0      Vdc=1
R:R_bias      n_vdd n_sdd  R=100
SDD:X1        n_sdd 0      Ports=1  I[1,0]=beta*_c1  C[1]=Vt1

analysis DC1  type=dc
analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-7
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);

        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");

        var iCube = ds["I"];
        string[] branchLabels = iCube.Axes[0].Labels!;
        int ipIdx = Array.IndexOf(branchLabels, "IP1");
        Assert.True(ipIdx >= 0, $"IP1 not in I cube branches: {string.Join(", ", branchLabels)}");
        var ipK1 = (Complex)iCube[ipIdx, 1];   // ≡ tone-source branch current (series)

        string[] nodeLabels = ds["V"].Axes[0].Labels!;
        int sddNodeIdx = Array.IndexOf(nodeLabels, "n_sdd");
        var sddInlK1 = (Complex)ds["INl"][sddNodeIdx, 1];

        double expected = 2.0 * ipK1.Magnitude;
        output.WriteLine($"|I(IP1) k=1| = {ipK1.Magnitude * 1e3:F4} mA, |INl[n_sdd] k=1| = {sddInlK1.Magnitude * 1e3:F4} mA (expected {expected * 1e3:F4} mA)");
        Assert.True(ipK1.Magnitude > 1e-4, "tone-source branch current should be non-trivial");
        double relErr = Math.Abs(sddInlK1.Magnitude - expected) / (expected + 1e-15);
        Assert.True(relErr < 1e-4, $"INl[n_sdd,k=1] should equal 2×i(Vt1) k=1; rel err = {relErr:E3}");
        output.WriteLine("T2_Hb_Mirror_ScalesToneSourceBranchSpectrum: PASS.");
    }

    // ── S-param: SDD referencing a tone source resolves and the run is non-singular ───────────
    //
    //   The tone source is a 0 V branch (short) at the S-param drive frequencies (E=0 off its
    //   tone), so its branch current is well-defined and referenceable. The control column couples
    //   the SDD port row to that branch.
    [Fact]
    public void T3_SParam_ToneSourceControlRef_ResolvesAndNonSingular()
    {
        const string cnl = @"
Port:P1   n1 0   Num=1  Z=50 Ohm
R:Rin     n1 nx  R=10 Ohm
V_1Tone:Vt1  nx 0  Vdc=0  Freq=5e9  V=1.0  Phase=0
SDD:D1    n1 0   I[1]=_v1/200 + 0.5*_c1  C[1]=Vt1
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [1e9, 2e9]);

        var s11 = (Complex)ds["S"][0, 0, 0];
        output.WriteLine($"S11 @1GHz = {s11:G6}");
        Assert.False(double.IsNaN(s11.Real) || double.IsNaN(s11.Imaginary), "S11 not finite");
        Assert.True(s11.Magnitude <= 1.0 + 1e-6, $"|S11|={s11.Magnitude} > 1 (non-physical)");
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("singular", StringComparison.OrdinalIgnoreCase));
        output.WriteLine("T3_SParam_ToneSourceControlRef_ResolvesAndNonSingular: PASS.");
    }

    // ── V_nTone: the shared model resolves identically (multi-frequency spelling) ─────────────
    [Fact]
    public void T4_VnTone_SharedModel_ResolvesIdentically()
    {
        // Same DC read-through as T1, but the reference is a V_nTone (two inert AC tones + Vdc=2).
        const string cnl = @"
V_nTone:Vn   n1 0   Vdc=2  NumFreqs=2  Freq[1]=1e9 V[1]=0.5 Phase[1]=0  Freq[2]=2e9 V[2]=0.25 Phase[2]=0
R:R1         n1 0   R=1000 Ohm

Vdc:VDD      n2 0   Vdc=5 V
R:R2         n2 n3  R=500 Ohm
SDD:X1       n3 0   Ports=1  I[1,0]=_c1  C[1]=Vn

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"V_nTone did not converge. Residual={result.FinalResidual:G}");
        double vn3 = result.NodeVoltages.FirstOrDefault(v => Math.Abs(v - 6.0) < 0.1);
        Assert.True(Math.Abs(vn3 - 6.0) < 1e-6, $"Expected V(n3) ≈ 6 V via V_nTone, got {vn3:G}");
        output.WriteLine("T4_VnTone_SharedModel_ResolvesIdentically: PASS.");
    }

    // ── Cport rejection: a tone source is two-terminal — Cport must be absent or 1 ────────────
    [Fact]
    public void T5_CportOnToneSource_Throws()
    {
        const string cnl = @"
V_1Tone:Vt1  n1 0   Vdc=1  Freq=1e9  V=0.5  Phase=0
R:R1         n1 0   R=100 Ohm
R:R2         n2 0   R=100 Ohm
SDD:X1       n2 0   Ports=1  I[1,0]=_c1  C[1]=Vt1  Cport[1]=2

analysis DC1  type=dc
";
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            NonlinearDcEngine.Run(nl);
        });
        output.WriteLine($"T5 exception: {ex.Message}");
        Assert.Contains("two-terminal", ex.Message);
        Assert.Contains("V_1Tone/V_nTone", ex.Message);
        output.WriteLine("T5_CportOnToneSource_Throws: PASS.");
    }
}
