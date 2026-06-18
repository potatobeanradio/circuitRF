using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// Gate tests for Enabled semantics on parametric-sweep chains (brief-sweep-revamp-2-dispatch).
/// Uses a simple 2-level DC sweep: SW_Vgs(Inner=SW_Vds) → SW_Vds(Inner=DC1) → DC1.
/// Circuit: V:Vgate + V:Vdrain pin two nodes; sweep verifies node-voltage cube axes.
/// </summary>
public class ParametricSweepEnabledTests(ITestOutputHelper output)
{
    // Simple two-variable DC circuit — nodes n_gate and n_drain pinned by voltage sources.
    private const string BaseCnl = @"
Vgs = -3.0
Vds = 5.0

Vdc:Vgate   n_gate  0  Vdc=Vgs
Vdc:Vdrain  n_drain 0  Vdc=Vds

analysis DC1     type=dc
analysis SW_Vds  type=parametric_sweep  Var=Vds  Values=0,5,10  Inner=DC1
analysis SW_Vgs  type=parametric_sweep  Var=Vgs  Values=-3,-3.5  Inner=SW_Vds
";

    private static (Library lib, TestBench tb, ParametricSweepAnalysis swVgs)
        ParseAndGetRoot(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var swVgs = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW_Vgs");
        return (lib, tb, swVgs);
    }

    // ── 1. Both sweeps enabled → V[Vgs, Vds, node] ───────────────────────────

    [Fact]
    public void BothEnabled_V_HasBothSweepAxes()
    {
        var (lib, tb, swVgs) = ParseAndGetRoot(BaseCnl);
        var ds = ParametricSweepEngine.Run(swVgs, lib, tb);

        var vCube = ds["V"];
        output.WriteLine($"V axes: [{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        Assert.Equal(3, vCube.Rank);
        Assert.Equal("Vgs", vCube.Axes[0].Name);
        Assert.Equal(2,     vCube.Axes[0].Length);
        Assert.Equal("Vds", vCube.Axes[1].Name);
        Assert.Equal(3,     vCube.Axes[1].Length);
        Assert.Equal("node", vCube.Axes[2].Name);
    }

    // ── 2. Inner (SW_Vds) disabled → V[Vgs, node] only ──────────────────────

    [Fact]
    public void InnerDisabled_V_HasOnlyOuterAxis()
    {
        var (lib, tb, swVgs) = ParseAndGetRoot(BaseCnl);

        // Disable SW_Vds — its axis should collapse out of the result.
        tb.Analyses.First(a => a.Name == "SW_Vds").Enabled = false;

        var ds = ParametricSweepEngine.Run(swVgs, lib, tb);

        var vCube = ds["V"];
        output.WriteLine($"V axes: [{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // Only the Vgs axis; Vds dropped.
        Assert.Equal(2, vCube.Rank);
        Assert.Equal("Vgs",  vCube.Axes[0].Name);
        Assert.Equal(2,      vCube.Axes[0].Length);
        Assert.Equal("node", vCube.Axes[1].Name);

        // Spot-check: at each Vgs, V(n_drain) should equal the global Vds=5 (default).
        int nodeIdx = vCube.Axes[1].Labels!.ToList().IndexOf("n_drain");
        Assert.True(nodeIdx >= 0, "n_drain not found in node axis");
        for (int gi = 0; gi < 2; gi++)
        {
            double vDrain = (double)vCube[gi, nodeIdx];
            output.WriteLine($"V(n_drain) at Vgs[{gi}] = {vDrain:F4} V (expected ≈ 5)");
            Assert.True(Math.Abs(vDrain - 5.0) < 1e-6,
                $"Expected V(n_drain)≈5 V (global Vds=5), got {vDrain:G}");
        }
    }

    // ── 3. Both disabled → AnalysisChain resolves to DC1; base runs directly ─

    [Fact]
    public void BothDisabled_DispatcherResolves_ToBase()
    {
        var (lib, tb, swVgs) = ParseAndGetRoot(BaseCnl);

        tb.Analyses.First(a => a.Name == "SW_Vds").Enabled = false;
        tb.Analyses.First(a => a.Name == "SW_Vgs").Enabled = false;

        // Dispatcher logic: resolve effective top, verify it's DC1.
        var top = AnalysisChain.ResolveEffectiveTop(swVgs, tb);
        Assert.NotNull(top);
        Assert.IsType<DcAnalysis>(top);
        Assert.Equal("DC1", top!.Name);
        Assert.True(AnalysisChain.IsChainRunnable(top, tb));

        // Run DC1 directly (simulating what the dispatcher does) — result has no sweep axes.
        var dc1 = (DcAnalysis)top;
        var nl  = new Elaborator(lib).Elaborate(tb);
        var dcResult = NonlinearDcEngine.Run(nl);
        var ds = DcResultPacker.Pack(dcResult, nl);

        var vCube = ds["V"];
        output.WriteLine($"V axes: [{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // No sweep axes — just the node axis.
        Assert.Equal(1, vCube.Rank);
        Assert.Equal("node", vCube.Axes[0].Name);

        // V(n_drain) ≈ 5 V (global Vds=5).
        int nodeIdx = vCube.Axes[0].Labels!.ToList().IndexOf("n_drain");
        Assert.True(nodeIdx >= 0, "n_drain not found in node axis");
        double vDrain = (double)vCube[nodeIdx];
        output.WriteLine($"V(n_drain) at DC = {vDrain:F4} V (expected ≈ 5)");
        Assert.True(Math.Abs(vDrain - 5.0) < 1e-6, $"Expected V(n_drain)≈5 V, got {vDrain:G}");
    }

    // ── 4. DC disabled → IsChainRunnable false (dispatcher skips entire chain) ─

    [Fact]
    public void BaseDisabled_IsChainNotRunnable()
    {
        var (_, tb, swVgs) = ParseAndGetRoot(BaseCnl);
        tb.Analyses.First(a => a.Name == "DC1").Enabled = false;

        // Both sweeps are enabled but the base is dead → chain is inert.
        Assert.False(AnalysisChain.IsChainRunnable(swVgs, tb));

        // Also verify the outer sweep itself still "resolves" to swVgs (it's enabled).
        var top = AnalysisChain.ResolveEffectiveTop(swVgs, tb);
        Assert.Same(swVgs, top);

        // But IsChainRunnable(top) is still false → dispatcher must skip it.
        Assert.False(AnalysisChain.IsChainRunnable(top!, tb));
    }
}
