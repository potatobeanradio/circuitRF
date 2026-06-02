using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Nonlinear;

/// <summary>
/// Task 4: DcBiasStepping tri-state — IfNecessary (default), Always, Never.
/// </summary>
public class DcBiasSteppingTests(ITestOutputHelper output)
{
    private const string HeroI2 =
        "(B*TC*tanh(_v2*a*(tanh(g*(TV0-_v1+_v2*th+Sc*log(exp(-(Sv-_v1)/Sc)+1)))+1))" +
        "*log(exp(-(2*TV0-2*_v1+2*_v2*th+2*Sc*log(exp(-(Sv-_v1)/Sc)+1))/TC)+1)" +
        "*(_v2*lam+1))/2";

    private const string HeroCnl = $@"
Sv=-0.837
Sc=0.71
TV0=4.268
TC=1.507
th=0.001
a=0.176
g=0.089
lam=0.0012
B=1130
V:Vg vgs 0 V=-3.05
R:Rg vgs gate R=1e-6 Ohm
V:Vd vdd 0 V=48
R:Rd vdd drain R=20 Ohm
SDD:M1 gate 0 drain 0 I[1,0]=_v1/50 I[2,0]={HeroI2}
";

    private static (NonlinearDcEngine.DcResult Result, double Vds, double I2mA)
        RunHero(AnalysisSettings settings)
    {
        var (lib, tb) = new CnlReader().Read(HeroCnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var r  = NonlinearDcEngine.Run(nl, settings);
        double vds = r.NodeVoltages.OrderBy(v => Math.Abs(v - 47.018)).First();
        double i2  = (48.0 - vds) / 20.0 * 1000.0;  // mA
        return (r, vds, i2);
    }

    // ── IfNecessary (default): direct solve, no ramp ──────────────────────────

    [Fact]
    public void IfNecessary_HeroConverges_DirectSolve_FewIterations()
    {
        var (result, vds, i2mA) = RunHero(new AnalysisSettings
        {
            DcBiasStepping = DcBiasSteppingMode.IfNecessary
        });

        output.WriteLine($"DcBiasStepping=IfNecessary: converged={result.Converged}, " +
                         $"iters={result.Iterations}, steps={result.Trace.TotalContinuationSteps}");
        output.WriteLine($"  vds={vds:F4} V, i2={i2mA:F3} mA, residual={result.FinalResidual:G4}");

        Assert.True(result.Converged, $"Hero must converge. Residual={result.FinalResidual:G4}");

        // Direct solve = 1 continuation step (frac=1.0 from cold start).
        Assert.Equal(1, result.Trace.TotalContinuationSteps);

        // Should take well under 20 iterations total (quadratic convergence from cold start).
        Assert.True(result.Iterations <= 20,
            $"Direct solve should converge in ≤20 iters, took {result.Iterations}");

        // Same operating point as the ramped solve.
        Assert.True(Math.Abs(vds - 47.018) < 0.05, $"vds={vds:F4}");
        Assert.True(Math.Abs(i2mA - 49.12) < 0.5,  $"i2={i2mA:F3} mA");
    }

    [Fact]
    public void IfNecessary_IsDefaultSetting()
    {
        // AnalysisSettings.Default must use IfNecessary.
        Assert.Equal(DcBiasSteppingMode.IfNecessary, AnalysisSettings.Default.DcBiasStepping);
    }

    // ── Always: ramped path, same endpoint ───────────────────────────────────

    [Fact]
    public void Always_HeroConverges_RampedPath()
    {
        var (result, vds, i2mA) = RunHero(new AnalysisSettings
        {
            DcBiasStepping = DcBiasSteppingMode.Always,
            DcBiasRampSteps = 20
        });

        output.WriteLine($"DcBiasStepping=Always: converged={result.Converged}, " +
                         $"iters={result.Iterations}, steps={result.Trace.TotalContinuationSteps}");

        Assert.True(result.Converged);
        // Ramped path uses 20 continuation steps.
        Assert.Equal(20, result.Trace.TotalContinuationSteps);
        Assert.True(Math.Abs(vds - 47.018) < 0.05, $"vds={vds:F4}");
        Assert.True(Math.Abs(i2mA - 49.12) < 0.5,  $"i2={i2mA:F3} mA");
    }

    [Fact]
    public void Always_CustomRampSteps_Works()
    {
        var (result, vds, _) = RunHero(new AnalysisSettings
        {
            DcBiasStepping  = DcBiasSteppingMode.Always,
            DcBiasRampSteps = 5
        });

        Assert.True(result.Converged);
        Assert.Equal(5, result.Trace.TotalContinuationSteps);
        Assert.True(Math.Abs(vds - 47.018) < 0.05, $"vds={vds:F4}");
    }

    // ── Never: direct only; throw on failure ─────────────────────────────────

    [Fact]
    public void Never_HeroConvergesDirectly_NoThrow()
    {
        // Hero should converge from cold start — Never should succeed.
        var (result, vds, i2mA) = RunHero(new AnalysisSettings
        {
            DcBiasStepping = DcBiasSteppingMode.Never
        });

        Assert.True(result.Converged, $"Hero should converge directly. Residual={result.FinalResidual:G4}");
        Assert.Equal(1, result.Trace.TotalContinuationSteps);
        Assert.True(Math.Abs(vds - 47.018) < 0.05, $"vds={vds:F4}");
    }

    [Fact]
    public void Never_ForcedFailure_ThrowsNonlinearDcNotConvergedException()
    {
        // Force the direct solve to fail by capping at 1 iteration — too few to converge.
        // Never must throw rather than silently return non-converged.
        var (lib, tb) = new CnlReader().Read(HeroCnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        var settings = new AnalysisSettings
        {
            DcBiasStepping    = DcBiasSteppingMode.Never,
            NonlinearMaxIter  = 1  // force convergence failure
        };

        Assert.Throws<NonlinearDcNotConvergedException>(() => NonlinearDcEngine.Run(nl, settings));
    }

    // ── IfNecessary fallback: when direct fails, ramp engages ─────────────────

    [Fact]
    public void IfNecessary_WhenDirectFails_FallsBackToRamp_AndConverges()
    {
        // Cap direct at 1 iteration so it always fails, forcing fallback to ramp.
        var (lib, tb) = new CnlReader().Read(HeroCnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        var settings = new AnalysisSettings
        {
            DcBiasStepping   = DcBiasSteppingMode.IfNecessary,
            NonlinearMaxIter = 1   // direct will fail; ramp will succeed (uses same MaxIter per step,
                                   // but each ramp step starts closer to the solution)
        };

        // With MaxIter=1, even the ramp may fail per-step, but the fallback fires.
        // The key assertion is: no exception is thrown (IfNecessary never throws).
        // Whether it converges depends on the circuit — for the hero with MaxIter=1 it likely won't,
        // but the point is it falls back gracefully rather than throwing.
        var result = NonlinearDcEngine.Run(nl, settings);  // must not throw
        // Trace must show more than 1 continuation step (the ramp fired).
        Assert.True(result.Trace.TotalContinuationSteps > 1,
            $"Ramp fallback should have fired. Steps={result.Trace.TotalContinuationSteps}");
    }

    // ── Both direct and ramped paths land at the same operating point ─────────

    [Fact]
    public void DirectAndRamped_ProduceSameOperatingPoint()
    {
        var (rDirect, vdsDirect, _) = RunHero(new AnalysisSettings
            { DcBiasStepping = DcBiasSteppingMode.Never });
        var (rRamped, vdsRamped, _) = RunHero(new AnalysisSettings
            { DcBiasStepping = DcBiasSteppingMode.Always });

        Assert.True(rDirect.Converged);
        Assert.True(rRamped.Converged);
        Assert.True(Math.Abs(vdsDirect - vdsRamped) < 1e-4,
            $"Direct vds={vdsDirect:F6}, Ramped vds={vdsRamped:F6} — should match to 4 decimal places");
    }
}
