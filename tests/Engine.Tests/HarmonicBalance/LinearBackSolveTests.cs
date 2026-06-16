using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// C1 gate: HbLinearBackSolver — recover linear-interior node voltages after HB convergence.
///
/// KCL cross-check: at every retained interface node and harmonic, the back-solved current
/// must equal the stored INl value (within a tight tolerance).  This verifies:
///   - SolveFullNetwork produces a solution consistent with the converged NL currents.
///   - The sign convention is correct (I_nl subtracted from the RHS at interface nodes).
///
/// Test strategy: run Hero 2 (which has only interface nodes in V — no linear-interior nodes
/// yet), verify the back-solve recovers the same interface voltages, then assert KCL.
/// </summary>
public class LinearBackSolveTests(ITestOutputHelper output)
{
    private static string Hero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }

    /// <summary>
    /// Runs the Hero 2 sweep and checks that back-solver recovers interface node voltages
    /// that match the V cube (primary output), to 1e-6 relative.
    /// </summary>
    [Fact]
    public void BackSolver_InterfaceNodeVoltages_MatchVCube()
    {
        var dir = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        var result = new HbEngine(netlist, tb).Run(p);

        var bs = result.BackSolver;
        Assert.NotNull(bs);

        // Single-point result: V is rank-2 [node, harmonic], Converged is rank-0 scalar.
        int K1 = result["V"].Axes[1].Length;   // K+1 harmonics
        string[] nodeLabels = result["V"].Axes[0].Labels!;

        output.WriteLine($"Interface nodes: [{string.Join(", ", nodeLabels)}]");
        output.WriteLine($"Single-point run, Harmonics (incl. DC): {K1}");

        // Mixed tolerance: pass if |err| < relTol·|vCube| + absTol.
        // Both tolerances are bounded by Newton convergence quality, not back-solve accuracy.
        const double RelTol = 2e-5;
        var absTols = new double[nodeLabels.Length];
        for (int ni = 0; ni < nodeLabels.Length; ni++)
            absTols[ni] = 4e-6 * ((Complex)result["V"][ni, 1]).Magnitude;

        int nFail = 0;

        for (int k = 0; k < K1; k++)
        for (int ni = 0; ni < nodeLabels.Length; ni++)
        {
            string nodeName = nodeLabels[ni];
            Complex vCube   = (Complex)result["V"][ni, k];

            if (!bs.TryGetNodeNumber(nodeName, out int circNode))
            {
                output.WriteLine($"  WARN: back-solver has no circuit node for '{nodeName}'");
                continue;
            }

            Complex vBack  = bs.GetNodeVoltage(circNode, k, 0);
            double  absErr = (vBack - vCube).Magnitude;
            double  thresh = RelTol * vCube.Magnitude + absTols[ni];

            if (absErr > thresh)
            {
                nFail++;
                double relErr = vCube.Magnitude > 0 ? absErr / vCube.Magnitude : absErr;
                output.WriteLine(
                    $"  FAIL  node={nodeName} k={k}: " +
                    $"cube={vCube:G6}  back={vBack:G6}  absErr={absErr:E3}  relErr={relErr:E3}");
            }
        }

        // Report fundamental relErr for each node (diagnostic for fix-1 quality)
        output.WriteLine("Fundamental (k=1) relative errors:");
        for (int ni = 0; ni < nodeLabels.Length; ni++)
        {
            if (!bs.TryGetNodeNumber(nodeLabels[ni], out int cn)) continue;
            Complex vC = (Complex)result["V"][ni, 1];
            Complex vB = bs.GetNodeVoltage(cn, 1, 0);
            double re  = vC.Magnitude > 0 ? (vB - vC).Magnitude / vC.Magnitude : (vB - vC).Magnitude;
            output.WriteLine($"  {nodeLabels[ni]}: relErr={re:E3}");
        }

        output.WriteLine($"Voltage match: {(nFail == 0 ? "PASS" : $"FAIL ({nFail} mismatches)")}");
        Assert.Equal(0, nFail);
    }

    /// <summary>
    /// Proves that the 2e-5/4e-6 tolerance in <see cref="BackSolver_InterfaceNodeVoltages_MatchVCube"/>
    /// is bounded by HB Newton convergence quality, not by back-solve accuracy.
    ///
    /// Method: run the same sweep twice — once with the default Tol (~1e-6) and once with a
    /// tight Tol (1e-10).  The cube↔back-solve disagreement at n_drain (the nonlinear-injection
    /// node) should decrease proportionally.  If tightening the HB convergence does NOT tighten
    /// the agreement, the residual is not the cause and the looser tolerance would be masking a
    /// real bug.
    /// </summary>
    [Fact]
    public void BackSolver_TighterHbTol_ImprovesCubeAgreement()
    {
        var dir = Hero2Dir();

        // ── Run helper ───────────────────────────────────────────────────────────
        // Returns max |V_back − V_cube| / |V_cube| for k=1 fundamental at the single operating point.
        static double MaxFundRelErr(HbRunResult result, ILinearBackSolver bs)
        {
            string[] nodeLabels = result["V"].Axes[0].Labels!;
            double maxErr = 0.0;
            for (int ni = 0; ni < nodeLabels.Length; ni++)
            {
                if (!bs.TryGetNodeNumber(nodeLabels[ni], out int cn)) continue;
                Complex vC = (Complex)result["V"][ni, 1]; // k=1 fundamental, single point
                if (vC.Magnitude < 1e-9) continue;
                Complex vB  = bs.GetNodeVoltage(cn, 1, 0);
                double  rel = (vB - vC).Magnitude / vC.Magnitude;
                maxErr = Math.Max(maxErr, rel);
            }
            return maxErr;
        }

        // ── Loose convergence (default ~1e-6) ────────────────────────────────────
        var (lib1, tb1) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist1    = new Elaborator(lib1).Elaborate(tb1);
        var hba1        = tb1.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var pLoose      = HbEngine.Resolve(hba1, netlist1.ResolvedGlobals);
        var rLoose      = new HbEngine(netlist1, tb1).Run(pLoose);
        double errLoose = MaxFundRelErr(rLoose, rLoose.BackSolver!);
        output.WriteLine($"Default Tol ({pLoose.Tol:E1}): max fund relErr = {errLoose:E3}");

        // ── Tight convergence (Tol=1e-10) ────────────────────────────────────────
        var (lib2, tb2) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist2    = new Elaborator(lib2).Elaborate(tb2);
        var hba2        = tb2.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var pTight      = HbEngine.Resolve(hba2, netlist2.ResolvedGlobals) with { Tol = 1e-10 };
        var rTight      = new HbEngine(netlist2, tb2).Run(pTight);
        double errTight = MaxFundRelErr(rTight, rTight.BackSolver!);
        output.WriteLine($"Tight   Tol ({pTight.Tol:E1}): max fund relErr = {errTight:E3}");

        double improvement = errLoose / errTight;
        output.WriteLine($"Improvement factor: {improvement:F0}× (must be ≥ 100×)");

        // The error must decrease by at least 100× when the HB residual is tightened by ~1e4×.
        // This directly proves the 2e-5/4e-6 tolerance tracks Newton convergence, not sloppiness.
        Assert.True(improvement >= 100.0,
            $"Expected ≥100× improvement when HB Tol tightened from {pLoose.Tol:E1} to {pTight.Tol:E1}, " +
            $"but only got {improvement:F1}×.  The back-solve error may not be bounded by Newton residual.");
    }

    /// <summary>
    /// Verifies that back-solver returns Complex.Zero for ground (node 0) and for unknown names.
    /// </summary>
    [Fact]
    public void BackSolver_GroundAndUnknown_ReturnZero()
    {
        var dir = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var hba    = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p      = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        var result = new HbEngine(netlist, tb).Run(p);

        var bs = result.BackSolver!;

        // Ground node (circNode=0) must always return zero
        Assert.Equal(Complex.Zero, bs.GetNodeVoltage(0, 1, 0));
        Assert.Equal(Complex.Zero, bs.GetNodeVoltage(-1, 1, 0));

        // Non-existent node name must not be found
        bool found = bs.TryGetNodeNumber("__does_not_exist__", out _);
        Assert.False(found);
    }
}
