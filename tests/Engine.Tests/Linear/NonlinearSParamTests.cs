using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gate tests for the nonlinear-device small-signal seam in SParameterEngine (design §3).
///
///   T1 — purely-linear netlist: no DC pre-pass, byte-identical S-params, no bias warnings.
///   T2 — resistive SDD at 0 V bias matches a linear resistor; sparam-zero-bias note emitted.
///   T3 — bias-dependent linearization: DC operating point feeds G(V₀) into S-params.
///   T4 — DC non-convergence fallback: run completes, sparam-dc-nonconverged warning emitted.
/// </summary>
public class NonlinearSParamTests
{
    private static (ElaboratedNetlist Netlist, DataSet Result) Run(
        string cnl, double[] freqsHz, AnalysisSettings? settings = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, freqsHz, settings);
        return (nl, ds);
    }

    private static Complex S(DataSet ds, int r, int c, int fi = 0) =>
        (Complex)ds["S"][fi, r, c];

    // ── T1: purely-linear netlist — no DC pre-pass, no bias warnings ──────────

    [Fact]
    public void T1_PurelyLinear_NoDcPrePass_SParamsUnchanged()
    {
        // Matched 50 Ω shunt → S11 = 0 exactly.
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1     n1 0  R=50 Ohm
            """;

        var (nl, ds) = Run(cnl, [1e9]);

        // S11 matches the analytic value (matched load → reflection = 0).
        var s11 = S(ds, 0, 0);
        Assert.True(s11.Magnitude < 1e-8, $"S11={s11} (expected ≈ 0)");

        // No bias warnings: DC engine was never invoked.
        Assert.DoesNotContain(nl.Warnings,
            w => w.Contains("sparam-zero-bias",     StringComparison.OrdinalIgnoreCase) ||
                 w.Contains("sparam-dc-nonconverged", StringComparison.OrdinalIgnoreCase) ||
                 w.Contains("No DC bias",            StringComparison.OrdinalIgnoreCase) ||
                 w.Contains("did not converge",      StringComparison.OrdinalIgnoreCase));
    }

    // ── T2: resistive SDD at 0 V bias — electrically visible + sparam-zero-bias ─

    [Fact]
    public void T2_ResistiveSdd_ZeroBias_MatchesLinearResistor()
    {
        // 1-port SDD: I[1] = _v1/75  →  G = 1/75 S  →  75 Ω shunt to ground.
        // No DC source → DC solves to 0 V → sparam-zero-bias note must be emitted.
        // S11 must match the same topology with a linear 75 Ω resistor.
        const string cnlSdd = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            SDD:D1   n1 0  I[1]=_v1/75
            """;
        const string cnlRef = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1     n1 0  R=75 Ohm
            """;

        var (nlSdd, dsSdd) = Run(cnlSdd, [1e9, 2e9]);
        var (_, dsRef)     = Run(cnlRef, [1e9, 2e9]);

        // sparam-zero-bias informational note must be emitted for the SDD circuit.
        Assert.Contains(nlSdd.Warnings,
            w => w.Contains("No DC bias", StringComparison.OrdinalIgnoreCase));

        // S11 from SDD matches the linear-resistor reference at both frequencies.
        const double tol = 1e-6;
        for (int fi = 0; fi < 2; fi++)
        {
            var sSdd = S(dsSdd, 0, 0, fi);
            var sRef  = S(dsRef, 0, 0, fi);
            Assert.True((sSdd - sRef).Magnitude < tol,
                $"S11 mismatch at fi={fi}: SDD={sSdd:G6}, ref={sRef:G6}");
        }

        // Sanity: expected S11 for 75 Ω shunt in a 50 Ω system = 0.2 (real, freq-independent).
        var s11 = S(dsSdd, 0, 0, 0);
        Assert.True(Math.Abs(s11.Real - 0.2) < 1e-5, $"S11.Real={s11.Real} (expected 0.2)");
        Assert.True(Math.Abs(s11.Imaginary) < 1e-5, $"S11.Imag={s11.Imaginary} (expected 0)");
    }

    // ── T3: bias-dependent linearization — G(V₀) used in S-params ────────────

    [Fact]
    public void T3_BiasedSdd_SmallSignalConductanceMatchesDcOperatingPoint()
    {
        // SDD: I[1] = g0·_v1 + g1·_v1²  →  G(V₀) = g0 + 2·g1·V₀
        //   g0 = 0.02 S, g1 = 0.01 S/V, V₀ = 3 V
        //   → G(3) = 0.02 + 0.06 = 0.08 S  →  Z = 12.5 Ω
        //   → S11 = (12.5 − 50)/(12.5 + 50) = −37.5/62.5 = −0.6
        //
        // DC bias: L:Lchoke (1 GH, ideal DC short / RF open) connects n1 to the Vdc node.
        // Vdc:Vbias forces V(n_dc)=3V at DC; L makes V(n1)=3V at DC.
        // At RF (1 GHz), Z_L = jωL ≈ j·6.28×10¹⁸ Ω → negligible admittance → Y_ext ≈ G_ss.
        const string cnl = """
            Port:P1   n1   0     Num=1  Z=50 Ohm
            L:Lchoke  n1   n_dc  L=1e9
            Vdc:Vbias n_dc 0     Vdc=3.0
            SDD:D1    n1   0     I[1]=0.02*_v1+0.01*_v1^2
            """;

        const double g0 = 0.02, g1 = 0.01, v0 = 3.0;
        double gSs    = g0 + 2.0 * g1 * v0;    // 0.08 S
        double zEff   = 1.0 / gSs;              // 12.5 Ω
        double s11Exp = (zEff - 50.0) / (zEff + 50.0);  // −0.6

        var (_, ds) = Run(cnl, [1e9]);
        var s11 = S(ds, 0, 0, 0);

        // Real part matches the bias-corrected prediction; imaginary part is negligible
        // (pure-resistive SDD has Dc=0 so the linearized stamp is real at all frequencies).
        Assert.True(Math.Abs(s11.Real - s11Exp) < 1e-3,
            $"S11.Real={s11.Real:G6} expected≈{s11Exp:G6} (G_ss={gSs} S at V₀={v0} V)");
        Assert.True(Math.Abs(s11.Imaginary) < 1e-3,
            $"S11.Imag={s11.Imaginary:G6} expected≈0 (no reactive term in this SDD)");
    }

    // ── T4: DC non-convergence fallback — run completes, warning emitted ───────

    [Fact]
    public void T4_DcNonConvergence_FallsBackToZeroBiasAndWarns()
    {
        // Quadratic SDD: I = v²/75.  At V=0, dI/dV = 0, so the Newton Jacobian starts
        // near-singular (only gmin on the n1 diagonal).  After step 1 (V→1 from Vdc branch
        // constraint), step 2 has a 0.0133 S update in the branch unknown (dxNorm ≈ 0.0133 >>
        // VTol=1e-9), so Newton does NOT declare convergence within MaxIter=2 steps.
        // DcBiasStepping=Never → SolveDirect(throwOnFailure=true) → throws after 2 steps.
        // SParameterEngine catches NonlinearDcNotConvergedException, emits sparam-dc-nonconverged,
        // and falls back to 0 V linearization.  The Vdc acts as a short at RF → S11 = −1.
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            SDD:D1   n1 0  I[1]=_v1^2/75
            Vdc:Vs   n1 0  Vdc=1.0
            """;

        var settings = new AnalysisSettings
        {
            DcBiasStepping   = DcBiasSteppingMode.Never,
            NonlinearMaxIter = 2,    // 2 Newton steps; quadratic SDD needs 3+ → throws
        };

        // Run must not throw despite DC non-convergence.
        var (nl, ds) = Run(cnl, [1e9], settings);

        // sparam-dc-nonconverged warning must be emitted.
        Assert.Contains(nl.Warnings,
            w => w.Contains("S-parameters may be inaccurate", StringComparison.OrdinalIgnoreCase));

        // At 0 V bias: Dg(0) = 0 (quadratic SDD is an open), and Vdc shorts n1 at RF → S11 = −1.
        var s11 = S(ds, 0, 0, 0);
        Assert.True(Math.Abs(s11.Real + 1.0) < 1e-6 && Math.Abs(s11.Imaginary) < 1e-6,
            $"S11={s11} (expected −1 due to Vdc short at RF)");
    }
}
