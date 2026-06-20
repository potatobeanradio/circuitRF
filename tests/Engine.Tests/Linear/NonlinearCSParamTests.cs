using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gate test for the NonlinearC full pipeline (brief-nonlinearc-symbol):
/// constant-C NonlinearC must yield the same S-parameters as a linear capacitor.
/// Exercises: CNL elaboration → DC pre-pass (0 V, sparam-zero-bias note) →
/// StampLinearized at jω·C(0)=jω·C0 → S-matrix comparison.
/// </summary>
public class NonlinearCSParamTests
{
    private static (ElaboratedNetlist Netlist, DataSet Result) Run(string cnl, double[] freqsHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, freqsHz);
        return (nl, ds);
    }

    private static Complex S(DataSet ds, int r, int c, int fi = 0) =>
        (Complex)ds["S"][fi, r, c];

    // ── T1: constant-C NonlinearC (C0=1 pF only) ≡ linear C = 1 pF ─────────────
    // Shunt capacitor between port node and ground.
    // No DC source → DC pre-pass finds 0 V bias → sparam-zero-bias note emitted.
    // StampLinearized stamps jω·C(0) = jω·C0 → identical to linear C stamp.

    [Fact]
    public void T1_ConstantC_NonlinearC_MatchesLinearCapacitor()
    {
        const string cnlNlc = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            NonlinearC:C1  n1 0  C0=1e-12
            """;
        const string cnlRef = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            C:C1     n1 0  C=1e-12
            """;

        double[] freqs = [1e9, 2e9, 5e9, 10e9];

        var (nlNlc, dsNlc) = Run(cnlNlc, freqs);
        var (_, dsRef)     = Run(cnlRef, freqs);

        // sparam-zero-bias note must be emitted for the NonlinearC circuit (nonlinear device, no DC).
        Assert.Contains(nlNlc.Warnings,
            w => w.Contains("No DC bias", StringComparison.OrdinalIgnoreCase));

        // S11 must match the linear reference at every frequency (tight tolerance).
        const double tol = 1e-9;
        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var sNlc = S(dsNlc, 0, 0, fi);
            var sRef = S(dsRef, 0, 0, fi);
            Assert.True((sNlc - sRef).Magnitude < tol,
                $"S11 mismatch at fi={fi} ({freqs[fi] / 1e9:G3} GHz): NLC={sNlc:G6}, ref={sRef:G6}");
        }
    }
}
