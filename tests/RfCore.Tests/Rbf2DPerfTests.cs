// ================================================================
//  Rbf2DPerfTests.cs — benchmark assertions for Rbf2D
//
//  These guard against regression to the wrong order of magnitude,
//  not micro-tuning.  Thresholds are generous (CI-safe) but tight
//  enough to catch accidental O(N²) eval or per-call allocations.
//
//  Owner: tune thresholds at bring-up to the dev machine.
//  Filter from fast CI via:  dotnet test --filter "Category=Perf"
// ================================================================

using System;
using System.Diagnostics;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

[Trait("Category", "Perf")]
public class Rbf2DPerfTests
{
    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------
    private static (double[] re, double[] im, double[] val) MakeGrid(int n, ulong seed = 42)
    {
        var re  = new double[n];
        var im  = new double[n];
        var val = new double[n];
        // Deterministic pseudo-random via LCG (no DateTime/Random — reproducible)
        ulong s = seed;
        for (int i = 0; i < n; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            re[i] = ((double)(s >> 32) / uint.MaxValue) * 2.0 - 1.0;
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            im[i] = ((double)(s >> 32) / uint.MaxValue) * 2.0 - 1.0;
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            val[i] = ((double)(s >> 32) / uint.MaxValue) * 100.0;
        }
        return (re, im, val);
    }

    private static double MedianMs(double[] samples)
    {
        Array.Sort(samples);
        int n = samples.Length;
        return n % 2 == 1 ? samples[n / 2] : (samples[n / 2 - 1] + samples[n / 2]) / 2.0;
    }

    // ----------------------------------------------------------------
    // Test 1: Fit @ N=20  < 0.2 ms median over 100 runs
    // ----------------------------------------------------------------
    [Fact]
    public void FitN20_Under0p2ms_Median()
    {
        const int Runs = 100;
        const double ThresholdMs = 0.2;
        var (re, im, val) = MakeGrid(20);

        // Warmup
        for (int i = 0; i < 5; i++) _ = new Rbf2D(re, im, val);

        var times = new double[Runs];
        for (int r = 0; r < Runs; r++)
        {
            var sw = Stopwatch.StartNew();
            _ = new Rbf2D(re, im, val);
            sw.Stop();
            times[r] = sw.Elapsed.TotalMilliseconds;
        }

        double median = MedianMs(times);
        Assert.True(median < ThresholdMs,
            $"Fit N=20 median={median:F4}ms, threshold={ThresholdMs}ms");
    }

    // ----------------------------------------------------------------
    // Test 2: Fit @ N=200  < 5 ms median over 20 runs
    // ----------------------------------------------------------------
    [Fact]
    public void FitN200_Under5ms_Median()
    {
        const int Runs = 20;
        const double ThresholdMs = 5.0;
        var (re, im, val) = MakeGrid(200);

        // Warmup
        for (int i = 0; i < 3; i++) _ = new Rbf2D(re, im, val);

        var times = new double[Runs];
        for (int r = 0; r < Runs; r++)
        {
            var sw = Stopwatch.StartNew();
            _ = new Rbf2D(re, im, val);
            sw.Stop();
            times[r] = sw.Elapsed.TotalMilliseconds;
        }

        double median = MedianMs(times);
        Assert.True(median < ThresholdMs,
            $"Fit N=200 median={median:F2}ms, threshold={ThresholdMs}ms");
    }

    // ----------------------------------------------------------------
    // Test 3: Evaluate 50×50 grid (2500 pts) @ N=200  < 5 ms median
    // ----------------------------------------------------------------
    [Fact]
    public void EvalN200_50x50Grid_Under5ms_Median()
    {
        const int Runs = 20;
        // 2500 query pts × 200 nodes = 500k multiquadric (sqrt) evals.
        // Threshold is a regression guard (an accidental O(N²) eval would be
        // 100×+ slower), generous enough not to flake on a loaded CI machine.
        const double ThresholdMs = 15.0;
        const int GridN = 50;
        var (re, im, val) = MakeGrid(200);

        var rbf = new Rbf2D(re, im, val);

        // Build a 50×50 query grid
        double[] qRe = new double[GridN * GridN];
        double[] qIm = new double[GridN * GridN];
        for (int i = 0; i < GridN; i++)
        {
            for (int j = 0; j < GridN; j++)
            {
                qRe[i * GridN + j] = -1.0 + 2.0 * i / (GridN - 1);
                qIm[i * GridN + j] = -1.0 + 2.0 * j / (GridN - 1);
            }
        }
        double[] res = new double[GridN * GridN];

        // Warmup
        for (int i = 0; i < 3; i++) rbf.Evaluate(qRe, qIm, res);

        var times = new double[Runs];
        for (int r = 0; r < Runs; r++)
        {
            var sw = Stopwatch.StartNew();
            rbf.Evaluate(qRe, qIm, res);
            sw.Stop();
            times[r] = sw.Elapsed.TotalMilliseconds;
        }

        double median = MedianMs(times);
        Assert.True(median < ThresholdMs,
            $"Eval N=200 50×50 median={median:F2}ms, threshold={ThresholdMs}ms");
    }

    // ----------------------------------------------------------------
    // Test 4: Full surface (fit N=200 + eval 2500)  < 10 ms median
    // ----------------------------------------------------------------
    [Fact]
    public void FullSurface_FitPlusEval_Under10ms_Median()
    {
        const int Runs = 20;
        // fit (O(N³) LDLᵀ, N=200) + 500k-eval. Regression guard, CI-safe.
        const double ThresholdMs = 25.0;
        const int GridN = 50;
        var (re, im, val) = MakeGrid(200);

        double[] qRe = new double[GridN * GridN];
        double[] qIm = new double[GridN * GridN];
        for (int i = 0; i < GridN; i++)
            for (int j = 0; j < GridN; j++)
            {
                qRe[i * GridN + j] = -1.0 + 2.0 * i / (GridN - 1);
                qIm[i * GridN + j] = -1.0 + 2.0 * j / (GridN - 1);
            }
        double[] res = new double[GridN * GridN];

        // Warmup
        for (int i = 0; i < 3; i++)
        {
            var warm = new Rbf2D(re, im, val);
            warm.Evaluate(qRe, qIm, res);
        }

        var times = new double[Runs];
        for (int r = 0; r < Runs; r++)
        {
            var sw = Stopwatch.StartNew();
            var rbf = new Rbf2D(re, im, val);
            rbf.Evaluate(qRe, qIm, res);
            sw.Stop();
            times[r] = sw.Elapsed.TotalMilliseconds;
        }

        double median = MedianMs(times);
        Assert.True(median < ThresholdMs,
            $"Full surface N=200+50×50 median={median:F2}ms, threshold={ThresholdMs}ms");
    }
}
