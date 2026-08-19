using System;
using System.Diagnostics;
using System.Threading;

namespace CircuitRF.WBond;

/// <summary>
/// One progress observation from a running wBond computation: which stage is running, how many work
/// units it has finished, and how many there are in total.
/// <para/>
/// <see cref="Total"/> of 0 means INDETERMINATE — a stage whose work is not a countable sequence.
/// </summary>
/// <param name="Stage">Which unit of work is running right now.</param>
/// <param name="Completed">Leaf units finished across the whole run.</param>
/// <param name="Total">Leaf units in the whole run; 0 = indeterminate.</param>
/// <param name="StageCompleted">Sub-units finished WITHIN the current stage.</param>
/// <param name="StageTotal">Sub-units in the current stage; 0 = the stage has no honest denominator.</param>
public sealed record WBondProgress(
    string Stage, long Completed, long Total, long StageCompleted = 0, long StageTotal = 0);

/// <summary>
/// Cancellation and progress for a wBond computation — the ONE object the MoM kernel and the
/// Touchstone export take, so a caller wires both concerns once rather than threading two parameters
/// through every signature.
///
/// <h3>Why this is a near-copy of <c>CircuitRF.Engine.RunControl</c> and must stay one</h3>
/// <para><b>This project has no project references, deliberately, and cannot acquire one.</b>
/// <c>src/Core</c> references <c>src/WBond</c> (that is how the wBond <c>ComponentModel</c> reaches the
/// physics without a cycle) and <c>src/Engine</c> references <c>src/Core</c> — so a reference from here
/// to <c>Engine</c>, where <c>RunControl</c> lives, closes the loop <c>WBond → Engine → Core → WBond</c>
/// and does not compile. Hoisting <c>RunControl</c> into a new shared leaf project would buy one shared
/// 100-line type at the cost of a project every consumer has to know about.</para>
///
/// <para>So the shape is copied on purpose, field for field, <b>so the UI's reporter reads identically
/// for an EM run and a wirebond run</b> — <c>WorkspaceViewModel.ReportEmProgress</c> and
/// <c>WBondMomProgress.Report</c> are the same six lines against the same five fields. If
/// <c>RunControl</c> gains a concept the wirebond kernel needs, add it here too rather than trying to
/// share the type.</para>
///
/// <h3>Cancellation is at a WORK boundary, never inside a factorisation</h3>
/// <para>The token is checked between meshing, each fill, each Cholesky, each K̃ row and each frequency
/// point — never inside a triangular solve. So Stop is answered within one row or one point rather than
/// instantly, which is the granularity that keeps the check off the inner numerical loops.</para>
///
/// <h3>Reporting is throttled, and thread-safe under <c>Parallel.For</c></h3>
/// <para>The two setup fills tick once per matrix ROW from every worker thread at once — hundreds of
/// thousands of ticks on a large design — and every delivered observation is a post onto the UI thread.
/// The throttle is a compare-and-swap on one timestamp rather than a <see cref="Stopwatch"/> restart,
/// so exactly one thread wins each interval and the rest return without reporting.</para>
/// </summary>
public sealed class WBondRunControl
{
    private long _completed;
    private long _stageCompleted;
    private long _stageTotal;
    private long _lastReportTicks = Stopwatch.GetTimestamp();
    private string _stage = "";

    /// <summary>Cancellation token. Default is <see cref="CancellationToken.None"/>, so a control
    /// created purely for progress never cancels.</summary>
    public CancellationToken Token { get; init; } = CancellationToken.None;

    /// <summary>Where progress observations go. Null makes every tick a cancellation check and nothing
    /// else — which is what a caller that only wants Stop passes.</summary>
    public IProgress<WBondProgress>? Progress { get; init; }

    /// <summary>Total leaf work units for the WHOLE run — frequency points, for a sweep. 0 =
    /// indeterminate.</summary>
    public long Total { get; init; }

    /// <summary>Floor on how often an observation is actually delivered. The final tick of a known
    /// total is always delivered regardless, so a bar is never left short of its own end.</summary>
    public double MinReportIntervalMs { get; init; } = 40;

    /// <summary>Units finished so far, across the whole run.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>Which stage is running.</summary>
    public string Stage => _stage;

    /// <summary>
    /// Starts a new stage: names it, and declares how many sub-units it will do.
    ///
    /// <para><b>Why a SECOND counter exists.</b> The outer one answers "how far through the sweep",
    /// which is the right question once the sweep starts — and the wrong question for the ~35 s of
    /// frequency-INDEPENDENT setup before it, during which the outer counter cannot honestly move at
    /// all. The stage counter carries that stretch (the L fill's rows, the P fill's rows, each Cholesky's
    /// columns, K̃'s rows) so a run that has not solved a point yet is still visibly alive.</para>
    ///
    /// <para>Reports immediately: a stage change is the one event a user is always waiting to see.
    /// <paramref name="stageTotal"/> of 0 leaves the stage indeterminate.</para>
    /// </summary>
    public void BeginStage(string name, long stageTotal = 0)
    {
        Interlocked.Exchange(ref _stageCompleted, 0);
        Interlocked.Exchange(ref _stageTotal, Math.Max(stageTotal, 0));
        _stage = name ?? "";
        ReportNow();
    }

    /// <summary>Renames the current stage WITHOUT touching either counter — for a sub-step whose own
    /// completion is counted when it finishes rather than when it starts.</summary>
    public void SetStageLabel(string name)
    {
        _stage = name ?? "";
        ReportNow();
    }

    /// <summary>
    /// One sub-unit of the current stage finished. Subject to the same throttle as <see cref="Tick"/>,
    /// and like it the last sub-unit of a known stage total is always delivered.
    /// </summary>
    /// <param name="units">Sub-units finished.</param>
    /// <param name="nextLabel">What the stage is about to do, if it changed. Renaming through the tick
    /// rather than through <see cref="BeginStage"/> is what keeps a stage bar MONOTONE: begin resets the
    /// sub-counter to zero, so calling it mid-stage would send the bar backwards on every rename.</param>
    public void TickStage(long units = 1, string? nextLabel = null)
    {
        Token.ThrowIfCancellationRequested();
        long done = Interlocked.Add(ref _stageCompleted, units);
        if (nextLabel is not null) _stage = nextLabel;
        if (Progress is null) return;

        long total = Interlocked.Read(ref _stageTotal);
        if (nextLabel is not null) { ReportNow(); return; }          // a label change is always worth showing
        if (total > 0 && done >= total) { ReportNow(); return; }
        if (!ClaimReportSlot()) return;
        ReportNow();
    }

    /// <summary>
    /// One leaf work unit finished — one frequency point. Checks cancellation, then advances the shared
    /// counter and (subject to <see cref="MinReportIntervalMs"/>) delivers an observation.
    /// </summary>
    public void Tick(long units = 1)
    {
        Token.ThrowIfCancellationRequested();
        long done = Interlocked.Add(ref _completed, units);
        if (Progress is null) return;

        // Always deliver the last unit of a known total: the throttle must never leave the bar short.
        if (Total > 0 && done >= Total) { ReportNow(); return; }
        if (!ClaimReportSlot()) return;
        ReportNow();
    }

    public void ThrowIfCancellationRequested() => Token.ThrowIfCancellationRequested();

    /// <summary>
    /// Whether THIS caller owns the next report. One compare-and-swap per interval, so a fill ticking
    /// from ten threads at once delivers one observation rather than ten.
    /// </summary>
    private bool ClaimReportSlot()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastReportTicks);

        double elapsedMs = (now - last) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < MinReportIntervalMs) return false;

        return Interlocked.CompareExchange(ref _lastReportTicks, now, last) == last;
    }

    private void ReportNow()
    {
        Interlocked.Exchange(ref _lastReportTicks, Stopwatch.GetTimestamp());
        Progress?.Report(new WBondProgress(
            _stage, Interlocked.Read(ref _completed), Total,
            Interlocked.Read(ref _stageCompleted), Interlocked.Read(ref _stageTotal)));
    }
}
