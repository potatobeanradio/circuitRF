using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class PerfBenchmarkTests
{
    /// <summary>
    /// <b>Opt-in (2026-08-16): this is a wall-clock gate and a full-solution run is not a place to
    /// measure wall clock.</b>
    ///
    /// <para>It had already been hardened once — best-of-5 rather than the mean, threshold widened to
    /// 500 ms — for exactly this reason, and it flaked again under a full <c>dotnet test</c> while
    /// passing in isolation. That is the same case root <c>CLAUDE.md</c> records for
    /// <c>RfCore.Tests</c>' <c>Rbf2DPerfTests</c>: fast, but wall-clock-sensitive, and therefore
    /// unable to survive the parallel-start burst whatever statistic it gates on. <b>Do not untag it
    /// on the grounds that it runs quickly</b> — it is tagged for the purpose the mechanism serves.
    /// Run it with <c>dotnet test --settings circuitrf.benchmark.runsettings</c>, and whenever the
    /// schematic connectivity or render-model build is touched.</para>
    ///
    /// <para>(The name says 50 ms; the gate is 500 ms and has been since the best-of-5 rewrite. Kept
    /// as-is rather than renamed, so the history greps.)</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")] // fast, but a WALL-CLOCK gate — meaningless under full-suite load
    public void BuildRenderModel_10k_Under50ms()
    {
        var renderModel = SchematicModelBuilder.GenerateStressTest(10_000);
        var editModel   = SchematicEditModel.FromRenderModel(renderModel);

        Console.WriteLine($"Components: {editModel.Components.Count}, Wires: {editModel.Wires.Count}");

        // Warm up
        editModel.BuildRenderModel();

        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            editModel.BuildRenderModel();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            Console.WriteLine($"  Run {i+1}: {sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        double best = times.Min();
        Console.WriteLine($"  Best: {best:F1} ms  Avg: {times.Average():F1} ms  Max: {times.Max():F1} ms");

        // Gate on the BEST run, not the mean. This is a "did the algorithm regress" check, and the
        // fastest observed run is the statistic that answers it: a genuine regression (the O(N²)
        // connectivity this replaced took ~1500 ms) is slow in EVERY run, while the mean is hostage
        // to one descheduled sample when the rest of the ~3900-test suite is saturating the CPU —
        // which is precisely how this test flaked in full-suite runs while passing in isolation.
        // Also brings it in line with this repo's own measurement convention (R-L2a-4: median/p95,
        // never the mean).
        // Baseline: ~37 ms Release, ~90 ms Debug.
        Assert.True(best < 500, $"BuildRenderModel 10k regressed: best of {times.Count} was {best:F1} ms (was ~37 ms release)");
    }
}
