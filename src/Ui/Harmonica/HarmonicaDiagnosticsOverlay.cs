// ================================================================
//  HarmonicaDiagnosticsOverlay.cs  —  §1 of
//  brief-harmonicarf-r5-the-unmeasured-stage-and-drag-starvation
//
//  The instrument this brief exists to build FIRST — two prior briefs (R3B §1.4, R4 §4.6) both ended
//  with "ReadoutStripView.LastSetItemsMs was not read this pass... requires a live interactive
//  Avalonia session, which this session had no way to drive." A third would be a process failure, so
//  this is a real deliverable, not scaffolding.
//
//  Deliberately framework-free (no SkiaSharp/Avalonia here) — the rolling-window arithmetic is plain
//  data, and drawing it is Renderers/HarmonicaDiagnosticsOverlayRenderer's job alone (§1.2's "the
//  overlay must not distort what it measures" guardrail is about the DRAW cost, which belongs with the
//  other Skia draw calls, not with the bookkeeping).
// ================================================================

using System;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// One document's own diagnostics HUD state (§1). Owned by <see cref="HarmonicaViewModel"/>
/// (<c>Diagnostics</c>), so <c>Reset()</c> is reachable directly from a menu command with no hook back
/// into the view — the same reason <c>EditDisplay</c>/<c>ColorEditor</c> are VM-owned sub-objects
/// rather than living on the canvas.
///
/// <para><b>Costs nothing when off</b> (guardrail 6): every recording method is a plain array write —
/// no timer of its own runs unless <see cref="RecordFrame"/> is actually called, which
/// <c>HarmonicaCanvas</c>'s draw operation gates on <see cref="HarmonicaViewModel.ShowDiagnosticsOverlay"/>
/// before ever touching this class. The three small fixed-size arrays are allocated once, at
/// construction, regardless of the toggle — negligible, and not a per-frame cost either way.</para>
/// </summary>
public sealed class HarmonicaDiagnosticsOverlay
{
    /// <summary>§1.1's own rolling-window size — "over a rolling window of ~120 frames".</summary>
    public const int WindowSize = 120;

    private readonly double[] _intervalsMs  = new double[WindowSize];
    private readonly int[]    _gen0AtSample = new int[WindowSize];
    private readonly int[]    _gen1AtSample = new int[WindowSize];
    private int _count;
    private int _head;
    private double? _lastFrameMs;

    private readonly Func<double> _nowMs;

    /// <summary>Production ctor — a real wall clock.</summary>
    public HarmonicaDiagnosticsOverlay()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _nowMs = () => clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Test ctor — <c>FrameScheduler</c>'s own D1 convention: fed a clock, so this is
    /// deterministic and testable headless rather than depending on real elapsed wall time.</summary>
    public HarmonicaDiagnosticsOverlay(Func<double> nowMs) => _nowMs = nowMs;

    /// <summary>
    /// How long this overlay's own LAST draw took, in milliseconds — §1.2's "time its own draw so the
    /// overlay's cost is visible in the overlay". Written by the renderer after it finishes drawing;
    /// read (and shown) by the NEXT draw, exactly the same one-frame-behind convention
    /// <see cref="HarmonicaViewModel.LastRenderMs"/> already uses for the canvas's own render cost —
    /// stated rather than hidden, since a draw cannot report its own duration before it has finished.
    /// </summary>
    public double LastDrawMs { get; set; }

    /// <summary>
    /// Call once per actual canvas repaint — <c>HarmonicaCanvas</c>'s draw operation, gated on the
    /// toggle. Records the wall-clock gap since the previous call (the frame-interval sample §1.1
    /// asks for) and the current GC generation counts, into the rolling window.
    /// </summary>
    public void RecordFrame()
    {
        double now = _nowMs();
        if (_lastFrameMs is { } last) Push(now - last);
        _lastFrameMs = now;
    }

    private void Push(double intervalMs)
    {
        int idx = (_head + _count) % WindowSize;
        if (_count < WindowSize) _count++;
        else _head = (_head + 1) % WindowSize;

        _intervalsMs[idx]  = intervalMs;
        _gen0AtSample[idx] = GC.CollectionCount(0);
        _gen1AtSample[idx] = GC.CollectionCount(1);
    }

    /// <summary>§1.1's own reset-on-demand: clears the rolling window so the owner can do one
    /// representative drag and read a clean set, unpolluted by the app's startup or by whatever ran
    /// before the toggle was flipped on.</summary>
    public void Reset()
    {
        _count = 0;
        _head  = 0;
        _lastFrameMs = null;
        LastDrawMs = 0;
    }

    /// <summary>One computed snapshot of the rolling window — §1.1's own list, plus the GC deltas.
    /// Default (all-zero) when nothing has been recorded yet.</summary>
    public readonly record struct Stats(
        double LastMs, double MeanMs, double P95Ms, double P99Ms, double MaxMs,
        int OverBudgetCount, int SampleCount, int Gen0Delta, int Gen1Delta);

    /// <summary>
    /// Computes every statistic FRESH from the current buffer, rather than maintaining running
    /// aggregates that could drift from what the buffer actually holds — cheap (at most
    /// <see cref="WindowSize"/> doubles to sort), so there is no reason not to.
    /// </summary>
    /// <param name="overBudgetMs">§1.1's "a count of intervals over 33 ms" — a 30 fps frame budget,
    /// the same figure <c>FrameScheduler</c>'s own default target uses.</param>
    public Stats Compute(double overBudgetMs = 33.3)
    {
        if (_count == 0) return default;

        Span<double> sorted = stackalloc double[_count];
        double sum = 0, max = double.MinValue;
        int over = 0;
        for (int i = 0; i < _count; i++)
        {
            int idx = (_head + i) % WindowSize;
            double v = _intervalsMs[idx];
            sorted[i] = v;
            sum += v;
            if (v > max) max = v;
            if (v > overBudgetMs) over++;
        }
        sorted.Sort();

        int newestIdx = (_head + _count - 1) % WindowSize;
        int oldestIdx = _head;

        return new Stats(
            LastMs:          _intervalsMs[newestIdx],
            MeanMs:          sum / _count,
            P95Ms:           Percentile(sorted, 0.95),
            P99Ms:           Percentile(sorted, 0.99),
            MaxMs:           max,
            OverBudgetCount: over,
            SampleCount:     _count,
            Gen0Delta:       _gen0AtSample[newestIdx] - _gen0AtSample[oldestIdx],
            Gen1Delta:       _gen1AtSample[newestIdx] - _gen1AtSample[oldestIdx]);
    }

    /// <summary>Linear-interpolated percentile over an already-SORTED span — a plain static method
    /// rather than a local function closing over it, since a <c>stackalloc</c> span cannot be
    /// captured by a closure.</summary>
    private static double Percentile(ReadOnlySpan<double> sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        double frac = rank - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}
