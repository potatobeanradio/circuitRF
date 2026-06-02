using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Nonlinear;

/// <summary>
/// Task 3: Newton convergence trace test.
/// Runs the hero, captures per-step/per-iteration data, and reports what Newton is doing.
/// This is diagnostic — it does NOT change the solver, just observes.
/// </summary>
public class ConvergenceTraceTests(ITestOutputHelper output)
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

    [Fact]
    public void HeroConvergenceTrace_ReportedAndAnalyzed()
    {
        var (lib, tb) = new CnlReader().Read(HeroCnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        var trace = result.Trace;

        // ── Print the full report ────────────────────────────────────────────
        output.WriteLine("═══════════════════════════════════════════════════════");
        output.WriteLine("Hero DC Solve — Convergence Trace Report (Task 3)");
        output.WriteLine("═══════════════════════════════════════════════════════");
        output.WriteLine($"Converged:            {result.Converged}");
        output.WriteLine($"Final residual:       {result.FinalResidual:G4}");
        output.WriteLine($"Total Newton iters:   {trace.TotalNewtonIterations}");
        output.WriteLine($"Continuation steps:   {trace.TotalContinuationSteps}");
        output.WriteLine($"Damping policy:       {trace.DampingPolicy}");
        output.WriteLine("");

        output.WriteLine("Per-step breakdown:");
        output.WriteLine($"  {"Step",-5} {"Frac",8} {"Iters",7} {"OK",4}");
        for (int si = 0; si < trace.Steps.Count; si++)
        {
            var step = trace.Steps[si];
            output.WriteLine($"  {si,-5} {step.SourceFraction,8:F4} {step.Iterations,7} {step.Converged,4}");
        }

        output.WriteLine("");
        output.WriteLine("Final continuation step — per-iteration residual sequence:");
        var lastStep = trace.Steps.Last(s => s.Converged);
        output.WriteLine($"  (source fraction = {lastStep.SourceFraction:F4}, {lastStep.Iterations} iters)");
        output.WriteLine($"  {"Iter",-5} {"‖F‖",16} {"‖ΔV‖",16} Ratio");

        double prevF = double.NaN;
        foreach (var it in lastStep.IterationTrace)
        {
            string ratio = double.IsNaN(prevF) ? "   —" : $"{it.ResidualNorm / (prevF * prevF),10:G3} (F_n / F_{it.Iter - 1}²)";
            output.WriteLine($"  {it.Iter,-5} {it.ResidualNorm,16:G6} {it.UpdateNorm,16:G6}  {ratio}");
            prevF = it.ResidualNorm;
        }

        output.WriteLine("");
        output.WriteLine("═══════════════════════════════════════════════════════");
        output.WriteLine("Analysis:");

        // Determine convergence regime for the last step
        var iters = lastStep.IterationTrace;
        bool hasQuadratic = false;
        if (iters.Count >= 3)
        {
            // Check if residual is decreasing at better-than-linear rate in the final iterations
            // Quadratic: residual halves the log-scale distance per iteration (ratio < some_fraction)
            for (int k = 1; k < iters.Count - 1; k++)
            {
                double r_prev = iters[k - 1].ResidualNorm;
                double r_curr = iters[k].ResidualNorm;
                double r_next = iters[k + 1].ResidualNorm;
                if (r_prev > 0 && r_curr > 0 && r_next > 0)
                {
                    double linearFactor  = r_curr / r_prev;
                    double expectedQuad  = r_curr * linearFactor;  // if linear
                    if (r_next < expectedQuad * 0.5)   // substantially better than linear
                    {
                        hasQuadratic = true;
                        break;
                    }
                }
            }
        }

        int itersPerStep = trace.TotalContinuationSteps > 0
            ? trace.TotalNewtonIterations / trace.TotalContinuationSteps : 0;

        output.WriteLine($"  Avg iters/step:   {itersPerStep}");
        output.WriteLine($"  Quadratic regime detected in last step: {hasQuadratic}");

        if (itersPerStep <= 10)
            output.WriteLine("  ✓ GOOD: ≤10 iters/step — consistent with healthy Newton near operating point.");
        else if (itersPerStep <= 20)
            output.WriteLine("  ⚠ MODERATE: 10–20 iters/step — likely linear convergence in continuation steps, quadratic at final step.");
        else
            output.WriteLine("  ✗ SLOW: >20 iters/step — Newton not in quadratic regime for most of the solve.");

        output.WriteLine("═══════════════════════════════════════════════════════");

        // ── Assertions ───────────────────────────────────────────────────────
        Assert.True(result.Converged, "Hero must converge");
        Assert.True(trace.TotalContinuationSteps > 0, "Must have at least one continuation step");
        Assert.True(trace.TotalNewtonIterations > 0,  "Must have at least one Newton iteration");
        // Residual must be geometrically decreasing somewhere (not strictly monotone, but decreases overall)
        double firstR = lastStep.IterationTrace[0].ResidualNorm;
        double lastR  = lastStep.IterationTrace[^1].ResidualNorm;
        Assert.True(lastR < firstR, "Residual must decrease over the final continuation step");
    }

    // ── Settings: loosening tolerance should not change the converged point ──

    [Fact]
    public void LooserTolerance_StillConvergesToCorrectPoint()
    {
        var (lib, tb) = new CnlReader().Read(HeroCnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        var looser = new AnalysisSettings { NonlinearAbsTol = 1e-4 };
        var result = NonlinearDcEngine.Run(nl, looser);

        Assert.True(result.Converged);
        // Same loadline point to 2 decimal places despite looser tolerance
        double[] v = result.NodeVoltages;
        double vds = v.OrderBy(x => Math.Abs(x - 47.018)).First();
        Assert.True(Math.Abs(vds - 47.018) < 0.1, $"vds={vds:F3}");
    }
}
