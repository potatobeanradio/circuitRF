using System;
using System.Diagnostics;
using System.Threading;

namespace CircuitRF.Engine;

/// <summary>
/// One progress observation from a running analysis: which stage is running, how many work units it
/// has finished, and how many there are in total.
/// <para/>
/// <see cref="Total"/> of 0 means INDETERMINATE — a stage whose work is not a countable sequence of
/// points. A single harmonic-balance solve is one Newton loop, not N steps, and reporting a fake
/// denominator for it would be worse than admitting there isn't one.
/// </summary>
public sealed record RunProgress(string Stage, long Completed, long Total);

/// <summary>
/// Cancellation and progress for an engine run — the ONE object every engine takes, so a caller
/// wires both concerns once rather than threading two parameters through every signature.
///
/// <para><b>Cancellation is at a POINT BOUNDARY, never inside a solve.</b> Every engine here checks
/// the token between the units it iterates over — a parametric-sweep point, an s-parameter
/// frequency, a loadpull grid termination — and none of them checks inside a single matrix
/// factorisation or a Newton loop. So Stop is answered within one point, not instantly: a sweep of
/// 20,301 points stops in the time one point takes, while a lone HB solve runs to completion. That
/// is the honest granularity and it is what makes cancellation cheap enough to be always-on; a
/// finer one would mean a token check in the inner numerical loops, which is exactly where this
/// engine cannot afford one.</para>
///
/// <para><b>Cancelling abandons the run — it does not produce a partial result.</b> A sweep's
/// per-point DataSets are stacked along a new axis, so a half-finished sweep has no shape to be
/// published in: the axis would carry N labels against fewer than N slices. Callers catch
/// <see cref="OperationCanceledException"/> and report a cancelled run rather than writing anything.</para>
///
/// <para><b>Progress counts LEAF work units against one total for the whole run.</b> Only the
/// innermost countable loop calls <see cref="Tick"/>; every enclosing level hands its inner analysis
/// a <see cref="Child"/> (same token, no progress) so a nested sweep's frequency loop cannot also
/// count and double the numerator. The one exception is a nested PARAMETRIC sweep, which is passed
/// the full control precisely so the innermost sweep is the one doing the counting.</para>
/// </summary>
public sealed class RunControl
{
    private long _completed;
    private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();
    private string _stage = "";

    /// <summary>Cancellation token. Default is <see cref="CancellationToken.None"/>, so a
    /// <c>RunControl</c> created purely for progress never cancels.</summary>
    public CancellationToken Token { get; init; } = CancellationToken.None;

    /// <summary>Where progress observations go. Null makes <see cref="Tick"/> a cancellation check
    /// and nothing else — which is exactly what <see cref="Child"/> produces.</summary>
    public IProgress<RunProgress>? Progress { get; init; }

    /// <summary>Total leaf work units for the WHOLE run, across every analysis. 0 = indeterminate.</summary>
    public long Total { get; init; }

    /// <summary>
    /// Floor on how often an observation is actually delivered. A 20,000-point sweep completing in
    /// under a minute ticks several hundred times a second, and every delivered observation is a
    /// post onto the UI thread — so unthrottled progress reporting costs more than the arithmetic it
    /// is reporting on. The final tick of a known total is always delivered regardless, so the bar
    /// cannot be left short of the end by the throttle.
    /// </summary>
    public double MinReportIntervalMs { get; init; } = 40;

    /// <summary>Which analysis is running. Setting it delivers an observation immediately — a stage
    /// change is the one event a user is always waiting to see.</summary>
    public string Stage
    {
        get => _stage;
        set { _stage = value ?? ""; ReportNow(); }
    }

    /// <summary>Units finished so far, across every analysis in the run.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    public void ThrowIfCancellationRequested() => Token.ThrowIfCancellationRequested();

    /// <summary>
    /// One leaf work unit finished: checks cancellation, then advances the shared counter and
    /// (subject to <see cref="MinReportIntervalMs"/>) delivers an observation.
    /// </summary>
    public void Tick(long units = 1)
    {
        Token.ThrowIfCancellationRequested();
        long done = Interlocked.Add(ref _completed, units);
        if (Progress is null) return;

        // Always deliver the last unit of a known total: the throttle must never leave the bar short.
        if (Total > 0 && done >= Total) { ReportNow(done); return; }
        if (_sinceLastReport.Elapsed.TotalMilliseconds < MinReportIntervalMs) return;
        ReportNow(done);
    }

    /// <summary>
    /// A control that shares this one's cancellation but reports NO progress. An enclosing level
    /// hands this to an inner analysis whose own loop would otherwise count work units that the
    /// enclosing level is already counting — see the class remark on leaf counting.
    /// </summary>
    public RunControl Child() => new() { Token = Token, Total = Total };

    private void ReportNow() => ReportNow(Completed);

    private void ReportNow(long done)
    {
        _sinceLastReport.Restart();
        Progress?.Report(new RunProgress(_stage, done, Total));
    }
}
