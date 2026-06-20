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
/// End-to-end gate test for brief-sdd-weighting-parser (brief #3):
/// an SDD nonlinear capacitor written via the user-defined weighting path —
///   I[1,2]=Q(_v1)  H[2]=j*2*pi*freq
/// must produce the same HB spectrum (and S-param) as the same device via the
/// built-in charge weighting:
///   I[1,1]=Q(_v1)
///
/// This is the payoff: a user-defined H[w]≡jω reproduces the built-in w=1 charge path,
/// proving the full I[p,w≥2] + H[w]=expr pipeline from netlist → engine.
/// </summary>
public class SddWeightingParserE2eTests(ITestOutputHelper output)
{
    // Q(V) = ∫₀ᵛ C dV where C(V) = 10e-12 − 1.5e-12·V + 0.1e-12·V²
    private const string QExpr =
        "10e-12*_v1 - 0.75e-12*_v1*_v1 + (0.1e-12/3)*_v1*_v1*_v1";

    // Built-in w=1 (charge) path: I[1,1]=Q(_v1)
    private const string CnlW1 = $"""
        P1Tone:P1  n1 0  Pavl=15 dBm  Z=50 Ohm  Freq=1e9  Phase=0 deg
        SDD:X1  n1 0  I[1,1]={QExpr}
        analysis HB1 type=hb Tone=1e9 MaxHarm=5 Tol=1e-6
        """;

    // User-weight w=2 path: I[1,2]=Q(_v1)  H[2]=j*2*pi*freq  — must match w=1
    private const string CnlW2 = $"""
        P1Tone:P1  n1 0  Pavl=15 dBm  Z=50 Ohm  Freq=1e9  Phase=0 deg
        SDD:X1  n1 0  I[1,2]={QExpr}  H[2]=j*2*pi*freq
        analysis HB1 type=hb Tone=1e9 MaxHarm=5 Tol=1e-6
        """;

    // ── HB helper ─────────────────────────────────────────────────────────────

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

    // ── S-param helper ────────────────────────────────────────────────────────

    private static DataSet RunSparam(string cnl, double[] freqs)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqs);
    }

    // Trim off the analysis line so S-param can parse the netlist (no HB analysis needed).
    private static string ToSparamCnl(string cnl) =>
        string.Join('\n', cnl.Split('\n').Where(l => !l.TrimStart().StartsWith("analysis")));

    // ── Test: HB spectrum equivalence (w=2 user-H ≡ w=1 built-in) ───────────

    [Fact]
    public void Hb_UserWeightH2_JomegaEqualsBuiltinCharge()
    {
        var dsW1 = RunHb(CnlW1);
        var dsW2 = RunHb(CnlW2);

        var vW1 = dsW1["V"];
        var vW2 = dsW2["V"];

        int n1W1 = NodeIdx(vW1, "n1");
        int n1W2 = NodeIdx(vW2, "n1");

        Assert.True(n1W1 >= 0, "n1 must appear in w=1 V cube");
        Assert.True(n1W2 >= 0, "n1 must appear in w=2 V cube");

        int numHarm = vW1.Axes[1].Length;
        output.WriteLine($"Per-harmonic comparison ({numHarm} harmonics, node n1):");
        output.WriteLine("  k   |V_w1|          |V_w2|          |err|            tol");

        for (int k = 0; k < numHarm; k++)
        {
            var vK1 = (Complex)vW1[n1W1, k];
            var vK2 = (Complex)vW2[n1W2, k];
            double err = (vK2 - vK1).Magnitude;
            double tol = Math.Max(1e-9, 1e-6 * vK1.Magnitude);

            output.WriteLine(
                $"  {k,2}  {vK1.Magnitude,14:G6}  {vK2.Magnitude,14:G6}  {err,14:E3}  {tol:E3}");

            Assert.True(err < tol,
                $"V[n1, k={k}]: w=1={vK1:G6} w=2={vK2:G6} err={err:E3} > tol={tol:E3}");
        }

        // Sanity: test must actually exercise nonlinearity.
        var v2 = (Complex)vW1[n1W1, 2];
        var v3 = (Complex)vW1[n1W1, 3];
        Assert.True(v2.Magnitude > 1e-7, $"2nd harmonic must be non-trivial (got {v2.Magnitude:G4} V)");
        Assert.True(v3.Magnitude > 1e-7, $"3rd harmonic must be non-trivial (got {v3.Magnitude:G4} V)");

        output.WriteLine("HB equivalence PASS.");
    }

    // ── Test: S-param equivalence (w=2 user-H ≡ w=1 built-in at small signal) ─

    [Fact]
    public void Sparam_UserWeightH2_JomegaEqualsBuiltinCharge()
    {
        double[] freqs = [1e9, 2e9, 5e9, 10e9];

        // S-param netlist — no HB analysis directive.
        string sparamW1 = $"""
            Port:P1  n1 0  Num=1  Z=50 Ohm
            SDD:X1   n1 0  I[1,1]={QExpr}
            """;
        string sparamW2 = $"""
            Port:P1  n1 0  Num=1  Z=50 Ohm
            SDD:X1   n1 0  I[1,2]={QExpr}  H[2]=j*2*pi*freq
            """;

        var dsW1 = RunSparam(sparamW1, freqs);
        var dsW2 = RunSparam(sparamW2, freqs);

        output.WriteLine("S11 comparison (w=1 vs w=2):");
        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var s1 = (Complex)dsW1["S"][fi, 0, 0];
            var s2 = (Complex)dsW2["S"][fi, 0, 0];
            double err = (s2 - s1).Magnitude;

            output.WriteLine($"  {freqs[fi] / 1e9:G3} GHz  w=1={s1:G6}  w=2={s2:G6}  err={err:E3}");
            Assert.True(err < 1e-9,
                $"S11 mismatch at {freqs[fi] / 1e9:G3} GHz: w=1={s1:G6} w=2={s2:G6} err={err:E3}");
        }

        output.WriteLine("S-param equivalence PASS.");
    }
}
