using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Messages;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>What a background wBond computation produced, and how it ended.</summary>
/// <typeparam name="T">The computation's own result type.</typeparam>
/// <param name="Value">The result, or <c>default</c> when the run did not finish.</param>
/// <param name="Cancelled">The user stopped it. Nothing was written and nothing is to be reported as
/// an error — a stop is an outcome, not a fault.</param>
/// <param name="Error">The refusal or exception message, or null. Already posted to the Messages
/// panel by <see cref="WBondBackgroundRun.ExecuteAsync"/>; returned so a caller with a status line of
/// its own can repeat it there.</param>
internal readonly record struct WBondRunOutcome<T>(T? Value, bool Cancelled, string? Error)
{
    public bool Succeeded => !Cancelled && Error is null;
}

/// <summary>
/// Runs one long wirebond computation off the UI thread and reports it in the Messages panel as two
/// live rows with progress bars.
///
/// <h3>This is the EM run's own mechanism, not a second one</h3>
/// <para>The planar EM kernel already answers exactly this problem — a solve that costs tens of
/// seconds to minutes, run from a button, whose user must be able to see it is alive. The pieces are
/// <see cref="IMessageSink.BeginProgress"/>, <see cref="IProgressMessage"/> and
/// <c>WorkspaceViewModel.ReportEmProgress</c>'s two-row split, and all three are reused here
/// unchanged. The only thing this file adds is the adapter from
/// <see cref="WBondProgress"/> to those rows — see <see cref="WBondRunControl"/> for why the wirebond
/// kernel reports through a type of its own rather than through <c>CircuitRF.Engine.RunControl</c>.</para>
///
/// <h3>Two rows, because there are two questions</h3>
/// <para><b>The sweep row</b> answers "how far through the run" and counts frequency points.
/// <b>The stage row</b> answers "what is it doing right now" and moves within a single point — or,
/// far more importantly here, through the whole of the frequency-INDEPENDENT setup, which at
/// N_s = 4,800 is 34.5 s during which the point counter cannot honestly move at all. One bar cannot
/// carry both, and a bar that sits still for half a minute is indistinguishable from a hung run,
/// which is the exact complaint the EM split was built to answer.</para>
///
/// <h3>Everything that changes goes AFTER the bar</h3>
/// <para>The counter is passed as <see cref="IProgressMessage.Update"/>'s <c>counter</c> argument
/// rather than being interpolated into the text: the bar is drawn immediately after the text, so
/// anything that grows to its left shoves it sideways on every observation.</para>
/// </summary>
internal static class WBondBackgroundRun
{
    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread with a live pair of Messages rows, and settles
    /// them on whatever way it ends.
    /// </summary>
    /// <param name="messages">
    /// Where the rows go. <b>Null is a supported host, not a bug</b> — the standalone <c>wBond</c>
    /// binary has no Messages panel at all (it is one window around one editor, with no Dock), and the
    /// work still has to leave the UI thread there. With no sink the run is silent and the caller's own
    /// status line reports the outcome.
    /// </param>
    /// <param name="title">The constant left-hand text of both rows. Must not change during the run.</param>
    /// <param name="startText">
    /// Posted BEFORE anything long begins, so the first thing a user sees after pressing the button is
    /// what is starting and roughly what it will cost — not an empty bar. Null skips it.
    /// </param>
    /// <param name="points">The sweep row's denominator. 0 makes it indeterminate.</param>
    /// <param name="work">The computation, given a control to tick and to check for cancellation.</param>
    /// <param name="summary">
    /// The outcome APPENDED to the end of the finished sweep row — so the settled line still says what
    /// ran and how many points it got through, rather than collapsing to a bare "complete".
    /// </param>
    /// <param name="cancel">The caller's own cancellation (a dialog closing, a Cancel button).</param>
    /// <param name="mirror">
    /// An extra consumer of every observation, on the UI thread, or null.
    /// <b>A MODAL dialog needs one</b>: <c>Compare Distributed Model…</c> is shown with
    /// <c>ShowDialog</c>, so the Messages panel is behind it and unreadable for the whole run. The panel
    /// rows are still posted — they are the record afterwards — but the thing the user can actually see
    /// while waiting has to be inside the dialog, fed from these same observations rather than from a
    /// second progress path.
    /// </param>
    /// <param name="cancellation">
    /// The stop, bound to BOTH progress rows so the user can cancel by right-clicking either bar
    /// (owner, 2026-08-19).
    ///
    /// <para><b>This is the only stop a Touchstone export has.</b> It runs in the background from a
    /// menu item — there is no dialog left on screen to put a Cancel button on — so before this the
    /// 3-D wire kernel could not be stopped from the UI at all, and a mis-sized run had to be waited
    /// out or the application killed. The caller owns the token source; this only binds the rows and
    /// settles the handle when the run ends.</para>
    /// </param>
    internal static async Task<WBondRunOutcome<T>> ExecuteAsync<T>(
        IMessageSink? messages,
        string title,
        string? startText,
        long points,
        Func<WBondRunControl, T> work,
        Func<T, string> summary,
        CancellationToken cancel,
        Action<WBondProgress>? mirror = null,
        RunCancellation? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(summary);

        if (startText is not null) messages?.Info(startText);

        var sweepLive = messages?.BeginProgress(title);
        var stageLive = messages?.BeginProgress($"{title} — starting");

        // ONE handle on BOTH rows: they are two views of one computation (see the class remarks), so
        // either bar's Cancel has to stop all of it. RunCancellation refuses a second ask, so
        // right-clicking both in turn is one request, not two.
        if (cancellation is not null)
        {
            sweepLive?.BindCancellation(cancellation);
            stageLive?.BindCancellation(cancellation);
        }

        // Progress<T> captures the UI SynchronizationContext at construction, so every observation
        // lands back on the UI thread without the kernel knowing anything about threading. This method
        // is called from the UI thread; a caller that is not on it gets a sink that marshals anyway
        // (MessagesTool posts through the dispatcher), so the rows are correct either way.
        var control = new WBondRunControl
        {
            Token    = cancel,
            Total    = points,
            Progress = sweepLive is null && stageLive is null && mirror is null
                ? null
                : new Progress<WBondProgress>(p =>
                {
                    Report(sweepLive, stageLive, title, p);
                    mirror?.Invoke(p);
                }),
        };

        T value;
        try
        {
            value = await Task.Run(() => work(control), cancel).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            cancellation?.Finish();

            // Two rows both carry a bar, so both have to be resolved — and they must say DIFFERENT
            // things, or the panel reads as one message duplicated. Same split the EM run uses: the
            // stage row says it stopped, the sweep row keeps its point count and says nothing was
            // written. keepBar: false — a settled row should read as text, not keep a stalled bar.
            stageLive?.Complete(MessageLevel.Info, $"{title} — stopped");
            sweepLive?.Finish(MessageLevel.Warning, "stopped — nothing was written", keepBar: false);
            return new WBondRunOutcome<T>(default, Cancelled: true, Error: null);
        }
        catch (Exception ex)
        {
            cancellation?.Finish();

            // THE ERROR IS THE LAST LINE. Both rows were posted before the run, so they sit above
            // whatever follows; finishing the sweep row WITH the error would put it above the notes
            // rather than after them. So the rows settle quietly and the error is posted on its own,
            // last — the same ordering the EM run was corrected to (owner, 2026-08-11).
            stageLive?.Complete(MessageLevel.Info, $"{title} — stopped");
            sweepLive?.Complete(MessageLevel.Info, $"{title} — see the error below");
            messages?.Error(ex.Message);
            return new WBondRunOutcome<T>(default, Cancelled: false, Error: ex.Message);
        }

        cancellation?.Finish();
        stageLive?.Complete(MessageLevel.Info, $"{title} — finished");
        sweepLive?.Finish(MessageLevel.Success, summary(value), keepBar: false);

        return new WBondRunOutcome<T>(value, Cancelled: false, Error: null);
    }

    /// <summary>
    /// Drives BOTH rows from one observation — the wirebond twin of
    /// <c>WorkspaceViewModel.ReportEmProgress</c>, deliberately line for line the same so the two runs
    /// read the same in the panel.
    ///
    /// <para>The stage row is the one place changing text sits to the LEFT of the bar, and that is
    /// deliberate: its text IS the answer to "what is it doing", so it is the text that has to change
    /// and its counter that stays put.</para>
    /// </summary>
    internal static void Report(IProgressMessage? sweepLive, IProgressMessage? stageLive,
                                string title, WBondProgress p)
    {
        ArgumentNullException.ThrowIfNull(p);

        if (p.Total > 0)
            sweepLive?.Update(title, FormatCounter(p.Completed, p.Total), 100.0 * p.Completed / p.Total);
        else
            sweepLive?.Update(title, indeterminate: true);

        string what = string.IsNullOrEmpty(p.Stage) ? "starting" : p.Stage;
        if (p.StageTotal > 0)
            stageLive?.Update($"{title} — {what}",
                              FormatCounter(p.StageCompleted, p.StageTotal),
                              100.0 * p.StageCompleted / p.StageTotal);
        else
            stageLive?.Update($"{title} — {what}", indeterminate: true);
    }

    /// <summary>The "1,194 / 2,525" counter, in the current culture — the same form the EM and
    /// Analysis rows use, so the panel does not have two spellings of one idea.</summary>
    internal static string FormatCounter(long completed, long total)
        => $"{completed.ToString("N0", CultureInfo.CurrentCulture)} / {total.ToString("N0", CultureInfo.CurrentCulture)}";
}
