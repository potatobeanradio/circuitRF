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
/// <param name="Stage">Which unit of work is running right now.</param>
/// <param name="Completed">Leaf units finished across the whole run.</param>
/// <param name="Total">Leaf units in the whole run; 0 = indeterminate.</param>
/// <param name="StageCompleted">Sub-units finished WITHIN the current stage.</param>
/// <param name="StageTotal">Sub-units in the current stage; 0 = the stage has no honest denominator,
/// which is also what every caller that does not use stage progress leaves it at.</param>
/// <param name="StageUnit">What ONE sub-unit of the current stage is, as a noun a reader can put
/// after the counter — "pattern(s)", "port(s)", "point(s)". Empty is the default and renders the
/// counter bare, exactly as every caller that does not set it always did.
/// <para><b>It exists because a bare "71 / 101" is not readable</b> (owner report, 2026-09-11). An
/// EM run reported a sweep row saying "101 point(s) solved" and, directly beneath it, a stage row
/// ending "71 / 101" — two different 101s, one of them the requested frequency grid and the other
/// the far-field pattern count that happens to equal it, with nothing on the second row saying
/// which. Naming the denominator's unit where the stage declares it is the fix: the stage knows
/// what it is counting, and the row that renders the counter does not have to guess.</para></param>
public sealed record RunProgress(
    string Stage, long Completed, long Total, long StageCompleted = 0, long StageTotal = 0,
    string StageUnit = "");

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
    private long _stageCompleted;
    private long _stageTotal;
    private string _stageUnit = "";
    private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();
    private string _stage = "";

    /// <summary>Cancellation token. Default is <see cref="CancellationToken.None"/>, so a
    /// <c>RunControl</c> created purely for progress never cancels.</summary>
    public CancellationToken Token { get; init; } = CancellationToken.None;

    private int _stop;

    /// <summary>
    /// <b>STOP is not CANCEL, and the difference is what happens to the work already done</b>
    /// (owner request, 2026-09-11).
    ///
    /// <para>Cancel abandons the run: the class remark above says a half-finished sweep has no shape
    /// to be published in, callers catch <see cref="OperationCanceledException"/> and nothing is
    /// written. That is right for "I did not mean to start this". It is the wrong answer for "this
    /// has found what I needed and I do not want to wait for the rest", which on an EM run — where a
    /// resonance search can keep adding points for a long time after the interesting part is solved
    /// — is the common case. So: STOP asks the engine to take no more work and FINISH, and every
    /// result the run has is packaged and returned exactly as a completed run's is.</para>
    ///
    /// <para><b>It is advisory, and an engine that ignores it is correct</b> — nothing here throws
    /// and nothing here is checked automatically. An engine that supports stopping reads this at the
    /// same boundaries it reads the token at, stops taking new work, and SAYS SO in its own notes;
    /// one that does not simply runs to completion. That is what makes it safe to hang on the one
    /// control object every engine already takes.</para>
    ///
    /// <para>One-way and idempotent, like cancellation: once asked, it stays asked.</para>
    /// </summary>
    public bool StopRequested => Volatile.Read(ref _stop) != 0;

    /// <summary>Asks the run to finish early and keep what it has. See <see cref="StopRequested"/>
    /// for how that differs from cancelling. Idempotent; safe from any thread.</summary>
    public void RequestStop() => Interlocked.Exchange(ref _stop, 1);

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

    /// <summary>
    /// Starts a new stage: names it, and declares how many sub-units it will do.
    ///
    /// <para><b>Why a SECOND counter exists at all.</b> The outer one answers "how far through the
    /// run", which is the right question when a leaf unit is small — a sweep point, an s-parameter
    /// frequency. It is the wrong question when one leaf unit is itself minutes long, which is
    /// exactly a full-wave EM frequency point (L8d/L9d measured 48 s and 71.9 s per de-embedded
    /// point at the shipping mesh). A bar that advances once a minute is indistinguishable from a
    /// hung run, so the stage counter carries the movement WITHIN a point while the outer one still
    /// answers where the run is overall. Two questions, two counters, one control object.</para>
    ///
    /// <para>Reports immediately: a stage change is the one event a user is always waiting to see.
    /// <paramref name="stageTotal"/> of 0 leaves the stage indeterminate.</para>
    /// </summary>
    /// <param name="unit">What one sub-unit IS — see <see cref="RunProgress.StageUnit"/>. It is set
    /// here and nowhere else, and it survives every <see cref="TickStage"/> relabel within the
    /// stage, because the unit is a property of what is being counted rather than of the label that
    /// happens to be showing.</param>
    public void BeginStage(string name, long stageTotal = 0, string unit = "")
    {
        Interlocked.Exchange(ref _stageCompleted, 0);
        Interlocked.Exchange(ref _stageTotal, Math.Max(stageTotal, 0));
        _stageUnit = unit ?? "";
        _stage = name ?? "";
        ReportNow();
    }

    /// <summary>
    /// One sub-unit of the current stage finished. Subject to the SAME throttle as
    /// <see cref="Tick"/> — and, like it, the last sub-unit of a known stage total is always
    /// delivered, so a stage bar is never left short of its own end.
    /// </summary>
    /// <summary>Renames the current stage WITHOUT touching either counter — for a sub-step whose own
    /// completion is counted when it finishes rather than when it starts, so the label says what is
    /// running now and the bar still only moves on real progress.</summary>
    public void SetStageLabel(string name)
    {
        _stage = name ?? "";
        ReportNow();
    }

    /// <param name="units">Sub-units finished.</param>
    /// <param name="nextLabel">What the stage is about to do, if it changed. Renaming through the
    /// tick rather than through <see cref="BeginStage"/> is what keeps a stage bar MONOTONE: begin
    /// resets the sub-counter to zero, so calling it mid-stage would send the bar backwards every
    /// time the label changed.</param>
    public void TickStage(long units = 1, string? nextLabel = null)
    {
        Token.ThrowIfCancellationRequested();
        long done = Interlocked.Add(ref _stageCompleted, units);
        if (nextLabel is not null) _stage = nextLabel;
        if (Progress is null) return;

        long total = Interlocked.Read(ref _stageTotal);
        if (nextLabel is not null) { ReportNow(); return; }      // a label change is always worth showing
        if (total > 0 && done >= total) { ReportNow(); return; }
        if (_sinceLastReport.Elapsed.TotalMilliseconds < MinReportIntervalMs) return;
        ReportNow();
    }

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
    /// <para><b>A child does NOT share the stop.</b> Stopping is answered by the engine that owns
    /// the loop, and a child is handed to an INNER analysis whose own early finish would leave the
    /// enclosing sweep with a point that is not the point it asked for — a shorter axis inside a
    /// longer one. The outer loop reads the stop and stops adding POINTS; the inner one always runs
    /// the point it was given to completion.</para>
    public RunControl Child() => new() { Token = Token, Total = Total };

    private void ReportNow() => ReportNow(Completed);

    private void ReportNow(long done)
    {
        _sinceLastReport.Restart();
        Progress?.Report(new RunProgress(
            _stage, done, Total,
            Interlocked.Read(ref _stageCompleted), Interlocked.Read(ref _stageTotal), _stageUnit));
    }
}
