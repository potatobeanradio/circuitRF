using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gate tests for the engine-diagnostics channel (ElaboratedNetlist.AddWarning /
/// AddWarningOnce). Verifies that:
///   T1 — S-param floating node emits a regularization warning naming the node.
///   T2 — HB non-convergence emits a single sweep-point summary warning.
/// </summary>
public class EngineDiagnosticsChannelTests
{
    // ── T1: S-param floating node → regularization warning with node name ─────

    [Fact]
    public void SParam_FloatingNodeViaBuriedTerm_EmitsRegularizationWarningWithNodeName()
    {
        // A Sub cell containing a Term.  When elaborated the buried Term is in
        // netlist.Components but StampAll skips it (InstancePath.Contains('.') ).
        // n_float has no admittance stamps → all-zero KCL row → SingularMatrixException
        // → IfNecessary retry → AddWarningOnce("sparam-regularization", "... n_float ...").
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1  n1 0  R=50 Ohm
            define Sub(A)
              Term:T_buried  A 0  Num=1  Z=50 Ohm
            end Sub
            Sub:X1  n_float
            """;

        var (lib, tb) = new CnlReader().Read(cnl);
        var nl        = new Elaborator(lib).Elaborate(tb);
        _            = SParameterEngine.Run(nl, [1e9]);

        var regWarn = nl.Warnings.FirstOrDefault(w =>
            w.Contains("regularization", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(regWarn);
        Assert.Contains("n_float", regWarn);
    }

    // ── T2: HB MaxIter=1 → one convergence-summary warning ───────────────────

    [Fact]
    public void Hb_MaxIterOne_EmitsOneConvergenceSummaryWarning()
    {
        // Saturating SDD driven at 5 V peak: tanh(10 * 2.5) ≈ 1 (full saturation).
        // One Newton step from V=0 cannot satisfy the 1e-6 tolerance → ncCount=1.
        // After the HB loop AddWarning("HB did not converge at 1 of 1 sweep point(s) ...").
        const string cnl = """
            V_1Tone:Vs  n_src 0  Freq=1e9  V=5.0  Phase=0  Vdc=0
            R:Rs  n_src n_dev  R=50 Ohm
            SDD:D1  n_dev 0  I[1,0]=1e-3*tanh(10.0*_v1)
            R:Rl  n_dev 0  R=50 Ohm
            analysis HB1  type=hb  Tone=1e9  MaxHarm=3  MaxIter=1
            """;

        var (lib, tb) = new CnlReader().Read(cnl);
        var nl        = new Elaborator(lib).Elaborate(tb);

        var hba    = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p      = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var engine = new HbEngine(nl, tb);
        _          = engine.Run(p);

        var ncWarn = nl.Warnings.FirstOrDefault(w =>
            w.Contains("did not converge", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ncWarn);
        Assert.Contains("sweep point(s)", ncWarn);
    }
}
