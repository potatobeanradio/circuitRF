// Test-only measurement methodology helper (docs/sonnet-briefs/brief-L2a-performance-harness.md §4,
// R-L2a-4/R-L2a-6). Plain xUnit + Stopwatch — no benchmarking package (R-L2a-6: ask before adding a
// dependency; a benchmarking framework is not needed to answer the questions in §3).

using System.Diagnostics;

namespace CircuitRF.Ui.Tests.LayoutPerf;

public static class BenchmarkHarness
{
    /// <summary>Median and p95 wall-clock time (ms) over <see cref="Iterations"/> MEASURED samples —
    /// warm-up iterations are run first and discarded entirely, never mixed into these numbers
    /// (R-L2a-4: "warm up, then report median and p95 — never the mean," since JIT/first-frame path
    /// construction and a single GC pause both turn a mean into noise that reads like signal).</summary>
    public readonly record struct Timing(double MedianMs, double P95Ms, double MinMs, double MaxMs, int Iterations)
    {
        public override string ToString() => $"median={MedianMs:F3}ms p95={P95Ms:F3}ms min={MinMs:F3}ms max={MaxMs:F3}ms (n={Iterations})";
    }

    public static Timing Measure(int warmupIterations, int iterations, Action action)
    {
        for (int i = 0; i < warmupIterations; i++) action();

        var samples = new double[iterations];
        var sw = new Stopwatch();
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        return Summarize(samples);
    }

    /// <summary>Same warm-up-then-report discipline as <see cref="Measure"/>, but for a SEQUENCE of
    /// distinct frames (e.g. one per pan/zoom step) rather than one repeated action — each element of
    /// <paramref name="frames"/> is its own timed sample, so the reported median/p95 describe the
    /// distribution across the sweep, not repeats of a single frame.</summary>
    public static Timing MeasureFrames(int warmupIterations, IReadOnlyList<Action> frames)
    {
        for (int i = 0; i < warmupIterations && frames.Count > 0; i++) frames[i % frames.Count]();

        var samples = new double[frames.Count];
        var sw = new Stopwatch();
        for (int i = 0; i < frames.Count; i++)
        {
            sw.Restart();
            frames[i]();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        return Summarize(samples);
    }

    private static Timing Summarize(double[] samples)
    {
        Array.Sort(samples);
        double median = samples[samples.Length / 2];
        int p95Index = Math.Clamp((int)Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1);
        return new Timing(median, samples[p95Index], samples[0], samples[^1], samples.Length);
    }

    /// <summary>Bytes allocated on the current thread by a single (post-warm-up) call to
    /// <paramref name="action"/> — the "steady-state pan loop should approach zero allocation" signal
    /// (§4). A forced GC before the measured call keeps a prior generation's garbage from skewing the
    /// delta; <c>GetAllocatedBytesForCurrentThread</c> itself does not trigger a collection, so the
    /// measured call's own allocations are not disturbed by it.</summary>
    public static long MeasureAllocatedBytes(int warmupIterations, Action action)
    {
        for (int i = 0; i < warmupIterations; i++) action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
