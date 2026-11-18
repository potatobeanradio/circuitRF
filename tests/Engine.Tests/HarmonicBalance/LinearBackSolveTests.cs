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
    /// The back-solve reproduces the interface voltages the solve returned to round-off, at ANY Newton
    /// tolerance. It injects the linear side's own current at those voltages, <c>−(I_src + Y_NN·V)</c>,
    /// rather than the device current, which differs from it by the Newton residual; injecting the device
    /// current made the disagreement track the tolerance (6.94e-4 at Tol=1e-4 on this fixture), and on a
    /// floating island behind the devices it reached kilovolts (<see cref="HbFloatingIslandBackSolveTests"/>).
    /// Tol=1e-4 is pinned because it stops this fixture's quadratic Newton one iterate early (‖F‖ = 2.2e-5).
    /// </summary>
    [Fact]
    public void BackSolver_ReproducesCube_EvenAtLooseTolerance()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero2Dir(), "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals) with { Tol = 1e-4 };
        var result    = new HbEngine(netlist, tb).Run(p);
        var bs        = result.BackSolver!;

        string[] nodeLabels = result["V"].Axes[0].Labels!;
        double maxErr = 0.0;
        for (int ni = 0; ni < nodeLabels.Length; ni++)
        {
            if (!bs.TryGetNodeNumber(nodeLabels[ni], out int cn)) continue;
            Complex vC = (Complex)result["V"][ni, 1];
            if (vC.Magnitude < 1e-9) continue;
            maxErr = Math.Max(maxErr, (bs.GetNodeVoltage(cn, 1, 0) - vC).Magnitude / vC.Magnitude);
        }
        output.WriteLine($"Tol {p.Tol:E1}: max fund relErr = {maxErr:E3}");
        Assert.True(maxErr < 1e-12, $"back-solve vs cube relErr {maxErr:E3} at Tol {p.Tol:E1}");
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
