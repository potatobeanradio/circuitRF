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
/// Gate tests for brief-sdd-nonlinearc-equivalence-test:
/// a nonlinear capacitor written as SDD I[1,1]=Q(V) (w=1 charge weighting, H[1]=jω)
/// must produce the same results as the dedicated NonlinearC device in both the
/// S-parameter (small-signal) and HB (large-signal) engines.
///
/// This is the regression anchor for the SDD weighting-function generalization.
/// No production-code changes — validates the existing I[p,1]→charge path.
///
/// Shared quadratic C(V):
///   C(V) = 10e-12 − 1.5e-12·V + 0.1e-12·V²
///   Q(V) = ∫₀ᵛ C dv = 10e-12·V − 0.75e-12·V² + (0.1e-12/3)·V³
/// </summary>
public class SddNonlinearCEquivalenceTests(ITestOutputHelper output)
{
    // Shared coefficient literals — single source of truth for both device forms.
    private const string NlcParams = "C0=10e-12 C1=-1.5e-12 C2=0.1e-12";

    // Q(V) written with explicit multiplication (no ^ operator) per brief.
    private const string SddChargeExpr =
        "10e-12*_v1 - 0.75e-12*_v1*_v1 + (0.1e-12/3)*_v1*_v1*_v1";

    // ── S-parameter helpers ───────────────────────────────────────────────────

    private static DataSet RunSparam(string cnl, double[] freqsHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqsHz);
    }

    private static Complex S11(DataSet ds, int fi) =>
        (Complex)ds["S"][fi, 0, 0];

    // ── HB helpers ────────────────────────────────────────────────────────────

    private static DataSet RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        return (DataSet)new HbEngine(netlist, tb).Run(p);
    }

    private static int NodeIdx(DataCube cube, string name) =>
        Array.FindIndex(cube.Axes[0].Labels!, n =>
            n.Equals(name, StringComparison.Ordinal));

    // ── Fact 1: S-parameter equivalence (small-signal, no HB) ────────────────
    //
    // At 0 V bias both devices linearize to jω·C(0) = jω·10e-12.
    // This confirms the SDD w=1 charge path flows through the same
    // auto-DC-bias → StampLinearized seam as NonlinearC.

    [Fact]
    public void Fact1_SParam_NlcMatchesSdd()
    {
        string cnlNlc = $"""
            Port:P1  n1 0  Num=1  Z=50 Ohm
            NonlinearC:C1  n1 0  {NlcParams}
            """;

        string cnlSdd = $"""
            Port:P1  n1 0  Num=1  Z=50 Ohm
            SDD:X1   n1 0  I[1,1]={SddChargeExpr}
            """;

        // Third leg: both should also match a linear C=C0 reference at 0 bias.
        const string cnlRef = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            C:C1     n1 0  C=10e-12
            """;

        double[] freqs = [1e9, 2e9, 5e9, 10e9];

        var dsNlc = RunSparam(cnlNlc, freqs);
        var dsSdd = RunSparam(cnlSdd, freqs);
        var dsRef = RunSparam(cnlRef, freqs);

        output.WriteLine("Fact1 S11 comparison (NLC vs SDD vs linear-C reference):");

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var sNlc = S11(dsNlc, fi);
            var sSdd = S11(dsSdd, fi);
            var sRef = S11(dsRef, fi);
            double errNlcSdd = (sSdd - sNlc).Magnitude;
            double errNlcRef = (sNlc - sRef).Magnitude;

            output.WriteLine(
                $"  {freqs[fi] / 1e9:G3} GHz  NLC={sNlc:G6}  SDD={sSdd:G6}  ref={sRef:G6}" +
                $"  Δ(NLC,SDD)={errNlcSdd:E2}  Δ(NLC,ref)={errNlcRef:E2}");

            Assert.True(errNlcSdd < 1e-9,
                $"S11 NLC vs SDD mismatch at {freqs[fi] / 1e9:G3} GHz: " +
                $"NLC={sNlc:G6} SDD={sSdd:G6} err={errNlcSdd:E3}");

            Assert.True(errNlcRef < 1e-9,
                $"S11 NLC vs linear-C mismatch at {freqs[fi] / 1e9:G3} GHz: " +
                $"NLC={sNlc:G6} ref={sRef:G6} err={errNlcRef:E3}");
        }

        output.WriteLine("Fact1 PASS.");
    }

    // ── Fact 2: HB equivalence (large-signal charge nonlinearity) ─────────────
    //
    // 15 dBm drive at 1 GHz develops voltage across the cap and excites harmonics
    // through C(V) curvature.  Both devices must produce the same V[n1,k] spectra
    // across all harmonics, and 2nd/3rd harmonics must be clearly non-trivial so
    // a degenerate all-zero match cannot pass silently.

    [Fact]
    public void Fact2_Hb_NlcMatchesSdd()
    {
        const int MaxHarm = 5;

        string cnlNlc = $"""
            P1Tone:P1  n1 0  Pavl=15 dBm  Z=50 Ohm  Freq=1e9  Phase=0 deg
            NonlinearC:C1  n1 0  {NlcParams}
            analysis HB1 type=hb Tone=1e9 MaxHarm={MaxHarm} Tol=1e-6
            """;

        string cnlSdd = $"""
            P1Tone:P1  n1 0  Pavl=15 dBm  Z=50 Ohm  Freq=1e9  Phase=0 deg
            SDD:X1  n1 0  I[1,1]={SddChargeExpr}
            analysis HB1 type=hb Tone=1e9 MaxHarm={MaxHarm} Tol=1e-6
            """;

        var dsNlc = RunHb(cnlNlc);
        var dsSdd = RunHb(cnlSdd);

        var vNlc = dsNlc["V"];
        var vSdd = dsSdd["V"];

        int n1Nlc = NodeIdx(vNlc, "n1");
        int n1Sdd = NodeIdx(vSdd, "n1");

        Assert.True(n1Nlc >= 0, "n1 must appear in NonlinearC V cube node axis");
        Assert.True(n1Sdd >= 0, "n1 must appear in SDD V cube node axis");

        int numHarm = vNlc.Axes[1].Length;
        output.WriteLine($"Fact2 per-harmonic comparison ({numHarm} harmonics, node n1):");
        output.WriteLine($"  k   |V_nlc|          |V_sdd|          |err|            tol");

        for (int k = 0; k < numHarm; k++)
        {
            var vNlcK = (Complex)vNlc[n1Nlc, k];
            var vSddK = (Complex)vSdd[n1Sdd, k];
            double err = (vSddK - vNlcK).Magnitude;
            double tol = Math.Max(1e-9, 1e-6 * vNlcK.Magnitude);

            output.WriteLine(
                $"  {k,2}  {vNlcK.Magnitude,14:G6}  {vSddK.Magnitude,14:G6}  {err,14:E3}  {tol:E3}");

            Assert.True(err < tol,
                $"V[n1, k={k}] mismatch: NLC={vNlcK:G6} SDD={vSddK:G6} " +
                $"err={err:E3} > tol={tol:E3}");
        }

        // Sanity: the test must actually exercise nonlinearity.
        var v2 = (Complex)vNlc[n1Nlc, 2];
        var v3 = (Complex)vNlc[n1Nlc, 3];
        output.WriteLine($"  |V[n1,2]|={v2.Magnitude:G4} V  |V[n1,3]|={v3.Magnitude:G4} V");
        Assert.True(v2.Magnitude > 1e-7,
            $"2nd harmonic must be non-trivial to confirm nonlinearity (got {v2.Magnitude:G4} V)");
        Assert.True(v3.Magnitude > 1e-7,
            $"3rd harmonic must be non-trivial to confirm nonlinearity (got {v3.Magnitude:G4} V)");

        output.WriteLine("Fact2 PASS.");
    }
}
