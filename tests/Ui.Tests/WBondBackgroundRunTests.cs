using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The wirebond half of what the EM run already does: the work leaves the UI thread, and the Messages
/// panel carries two live rows with progress bars.
///
/// <para><b>These assert the ROWS, not the arithmetic.</b> The physics is gated in
/// <c>WBond.Tests</c> and <c>WBondMomCompareTests</c>; what is new here is a reporting contract, and
/// the ways it can be wrong are all shapes — a bar left short of its end, an error buried above the
/// notes it should follow, two rows settling with the same sentence so the panel reads as a duplicate,
/// a cancelled run claiming a result.</para>
/// </summary>
public sealed class WBondBackgroundRunTests
{
    /// <summary>
    /// A sink that records live rows as well as plain posts. Rows are rewritten IN PLACE, exactly as
    /// <c>MessagesTool</c> rewrites a <c>MessageEntry</c> — a fake that appended instead would pass a
    /// test the real panel fails.
    /// </summary>
    private sealed class RecordingSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posts { get; } = [];
        public List<Row> Rows { get; } = [];

        public void Post(MessageLevel level, string text, string? filePath = null)
            => Posts.Add((level, text));

        public IProgressMessage BeginProgress(string text)
        {
            var row = new Row(text);
            Rows.Add(row);
            return row;
        }

        public void Clear() { Posts.Clear(); Rows.Clear(); }

        internal sealed class Row(string text) : IProgressMessage
        {
            public string  Text     { get; private set; } = text;
            public string? Counter  { get; private set; }
            public double? Percent  { get; private set; }
            public bool    Indeterminate { get; private set; } = true;
            public MessageLevel Level { get; private set; } = MessageLevel.Info;
            public int Updates { get; private set; }

            public void Update(string text, string? counter = null, double? percentComplete = null,
                               bool indeterminate = false)
            {
                Updates++;
                Text = text;
                Counter = counter;
                Indeterminate = indeterminate;
                if (percentComplete is { } pct) Percent = pct;
            }

            public void Finish(MessageLevel level, string outcome, bool keepBar = true)
            {
                Level = level;
                if (Counter is not null) Counter = $"{Counter} - {outcome}";
                else                     Text    = $"{Text} - {outcome}";
                if (!keepBar) { Percent = null; Indeterminate = false; }
            }

            public void Complete(MessageLevel level, string text)
            {
                Level = level;
                Text = text;
                Counter = null;
                Percent = null;
                Indeterminate = false;
            }
        }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> that runs posted work INLINE, installed for the duration
    /// of every test here.
    ///
    /// <para><b>Without one these tests race their own subject.</b> <c>Progress&lt;T&gt;</c> captures
    /// whatever context is current when it is constructed; with none it falls back to the thread pool,
    /// so an observation posted by the last <c>Tick</c> can land AFTER the row has been settled by
    /// <c>Finish</c> and overwrite it — the assertions on a finished row would then pass or fail on
    /// timing. In the real application that cannot happen: the context is the Avalonia dispatcher, and
    /// the final report and the await's own continuation go through one ordered queue. This makes the
    /// test as ordered as production is, rather than hoping the pool happens to be.</para>
    /// </summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static async Task<WBondRunOutcome<T>> Run<T>(
        RecordingSink? sink, long points, Func<WBondRunControl, T> work,
        Func<T, string> summary, CancellationToken cancel = default)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            return await WBondBackgroundRun.ExecuteAsync(
                sink, "Exporting Touchstone", "started", points, work, summary, cancel);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    // ────────────────────────────────────────────────── the happy path

    /// <summary>
    /// TWO rows, and the start line above them. Two because there are two questions with two different
    /// answers — how far through the run, and what it is doing right now — and one bar cannot carry
    /// both. The EM run learned this the same way.
    /// </summary>
    [Fact]
    public async Task ASuccessfulRun_PostsTheStartLineThenTwoLiveRows()
    {
        var sink = new RecordingSink();

        var outcome = await Run(sink, 3, run =>
        {
            run.BeginStage("filling the inductance matrix", 2);
            run.TickStage();
            run.TickStage();
            for (int i = 0; i < 3; i++) run.Tick();
            return 42;
        }, v => $"wrote {v} things");

        Assert.True(outcome.Succeeded);
        Assert.Equal(42, outcome.Value);

        // The start line goes out BEFORE anything long begins, so the first thing on screen is what is
        // starting rather than an empty bar.
        Assert.Equal(MessageLevel.Info, sink.Posts[0].Level);
        Assert.Equal("started", sink.Posts[0].Text);

        Assert.Equal(2, sink.Rows.Count);
        var (sweep, stage) = (sink.Rows[0], sink.Rows[1]);

        // The sweep row settles SUCCESS with the summary appended to what it already said, and drops the
        // bar — a finished row should read as text, not keep showing a stalled-looking glyph.
        Assert.Equal(MessageLevel.Success, sweep.Level);
        Assert.Contains("wrote 42 things", sweep.Counter ?? sweep.Text, StringComparison.Ordinal);
        Assert.Null(sweep.Percent);

        // The stage row names a STEP, not an outcome, so it collapses to a plain line.
        Assert.Equal("Exporting Touchstone — finished", stage.Text);
        Assert.Null(stage.Percent);
    }

    /// <summary>
    /// The sweep row's bar reaches 100%. The throttle must never leave it short — a bar stuck at 99%
    /// is how a completed run reads as one that died at the end.
    /// </summary>
    [Fact]
    public async Task TheSweepBar_ReachesItsOwnEnd()
    {
        var sink = new RecordingSink();
        double? lastPercent = null;

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            await WBondBackgroundRun.ExecuteAsync(
                sink, "Exporting Touchstone", null, 4,
                run => { for (int i = 0; i < 4; i++) run.Tick(); return 0; },
                _ => "done",
                CancellationToken.None,
                mirror: p => { if (p.Total > 0) lastPercent = 100.0 * p.Completed / p.Total; });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.Equal(100.0, lastPercent);
    }

    /// <summary>
    /// Everything that CHANGES goes into the counter, after the bar. The row's text is constant for the
    /// whole run — anything that grows to the bar's left shoves it sideways on every observation, which
    /// is the twitching the counter/text split exists to remove.
    /// </summary>
    [Fact]
    public async Task TheSweepRowsText_NeverChanges_SoTheBarNeverMoves()
    {
        var sink = new RecordingSink();
        var texts = new List<string>();

        await Run(sink, 5,
            run =>
            {
                for (int i = 0; i < 5; i++)
                {
                    run.Tick();
                    texts.Add(sink.Rows[0].Text);
                }
                return 0;
            },
            _ => "done");

        Assert.All(texts, t => Assert.Equal("Exporting Touchstone", t));
    }

    // ────────────────────────────────────────────────── failure and cancellation

    /// <summary>
    /// <b>The error is the LAST line, and nothing follows it.</b> Both rows are posted BEFORE the run,
    /// so they sit above whatever comes after — finishing the sweep row WITH the error would put the
    /// error above the pile of notes rather than after it. Same correction the EM run took
    /// (owner, 2026-08-11).
    /// </summary>
    [Fact]
    public async Task AFailedRun_PostsTheErrorLast_AndSettlesBothRowsQuietly()
    {
        var sink = new RecordingSink();

        var outcome = await Run<int>(sink, 3,
            _ => throw new InvalidOperationException("the ground plane is disabled"),
            _ => "unreachable");

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Cancelled);
        Assert.Equal("the ground plane is disabled", outcome.Error);

        var (level, text) = sink.Posts[^1];
        Assert.Equal(MessageLevel.Error, level);
        Assert.Equal("the ground plane is disabled", text);

        // Neither row is left carrying an error of its own, and the two say DIFFERENT things — the same
        // sentence twice reads as one message duplicated, which is exactly what the owner reported of
        // the EM run.
        Assert.NotEqual(sink.Rows[0].Text, sink.Rows[1].Text);
        Assert.DoesNotContain("ground plane", sink.Rows[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ground plane", sink.Rows[1].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled run is a WARNING with no value, never an error and never a result. The sweep row
    /// keeps the count it reached — the one thing worth knowing about a run somebody stopped.
    /// </summary>
    [Fact]
    public async Task ACancelledRun_WarnsAndReturnsNothing()
    {
        var sink = new RecordingSink();
        using var cts = new CancellationTokenSource();

        var outcome = await Run(sink, 100, run =>
        {
            run.Tick();
            cts.Cancel();
            for (int i = 0; i < 99; i++) run.Tick();   // throws at the next boundary
            return 7;
        }, v => $"wrote {v}", cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.Null(outcome.Error);
        Assert.Equal(0, outcome.Value);               // default(int), not the 7 the work never returned

        Assert.Equal(MessageLevel.Warning, sink.Rows[0].Level);
        Assert.Contains("nothing was written", sink.Rows[0].Counter ?? sink.Rows[0].Text,
                        StringComparison.Ordinal);

        // No error anywhere: a stop is an outcome, not a fault.
        Assert.DoesNotContain(sink.Posts, p => p.Level == MessageLevel.Error);
    }

    // ────────────────────────────────────────────────── a host with no panel

    /// <summary>
    /// <b>A null sink is a supported host.</b> The standalone <c>wBond</c> binary has no Messages
    /// region at all, and the work still has to leave the UI thread there — so the run must complete
    /// normally with nowhere to report to, rather than throwing on the first observation.
    /// </summary>
    [Fact]
    public async Task WithNoSink_TheRunStillCompletes()
    {
        var outcome = await Run(null, 2, run =>
        {
            run.BeginStage("filling", 2);
            run.TickStage();
            run.Tick();
            run.Tick();
            return "ok";
        }, v => v);

        Assert.True(outcome.Succeeded);
        Assert.Equal("ok", outcome.Value);
    }

    /// <summary>
    /// The standalone binary's fallback: one status line, and the STAGE row is what it ends up holding.
    /// No rule chooses it — <see cref="WBondBackgroundRun.Report"/> writes the sweep row then the stage
    /// row inside one callback, so the stage text is simply what is there when the frame is drawn, and
    /// it is the more useful of the two for a host with no panel.
    /// </summary>
    [Fact]
    public void TheStatusLineSink_EndsEachObservationOnTheStageRow()
    {
        string line = "";
        var sink = new WBondStatusMessageSink((text, _) => line = text);

        var sweep = ((IMessageSink)sink).BeginProgress("Exporting Touchstone");
        var stage = ((IMessageSink)sink).BeginProgress("Exporting Touchstone — starting");

        WBondBackgroundRun.Report(sweep, stage, "Exporting Touchstone",
                                  new WBondProgress("filling the potential matrix", 0, 8, 300, 1200));

        Assert.Contains("filling the potential matrix", line, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────── the reporter itself

    /// <summary>
    /// The two rows read the two halves of one observation: the sweep row takes the run counters, the
    /// stage row takes the stage counters. Crossing them is the bug this pins.
    /// </summary>
    [Fact]
    public void Report_SendsTheRunCountersToTheSweepRow_AndTheStageCountersToTheStageRow()
    {
        var sweep = new RecordingSink.Row("Exporting Touchstone");
        var stage = new RecordingSink.Row("Exporting Touchstone — starting");

        WBondBackgroundRun.Report(sweep, stage, "Exporting Touchstone",
                                  new WBondProgress("assembling the segment system", 3, 12, 250, 1000));

        Assert.Equal("Exporting Touchstone", sweep.Text);
        Assert.Equal(WBondBackgroundRun.FormatCounter(3, 12), sweep.Counter);
        Assert.Equal(25.0, sweep.Percent);

        Assert.Equal("Exporting Touchstone — assembling the segment system", stage.Text);
        Assert.Equal(WBondBackgroundRun.FormatCounter(250, 1000), stage.Counter);
        Assert.Equal(25.0, stage.Percent);
    }

    /// <summary>
    /// A stage with no honest denominator is INDETERMINATE, not a bar at zero. A single Cholesky's
    /// interior is not a countable sequence, and inventing a fraction for it would be lying about the
    /// wait.
    /// </summary>
    [Fact]
    public void Report_LeavesAStageWithNoDenominatorIndeterminate()
    {
        var sweep = new RecordingSink.Row("Exporting Touchstone");
        var stage = new RecordingSink.Row("Exporting Touchstone — starting");

        WBondBackgroundRun.Report(sweep, stage, "Exporting Touchstone",
                                  new WBondProgress("reducing to the node-merged basis", 0, 0));

        Assert.True(sweep.Indeterminate);
        Assert.True(stage.Indeterminate);
        Assert.Null(stage.Counter);
    }
}
