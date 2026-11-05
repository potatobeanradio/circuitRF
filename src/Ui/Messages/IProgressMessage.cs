using System;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// A handle on one message that stays LIVE while a long operation runs: its text and its progress bar
/// are rewritten in place, and it is finally settled to an ordinary message by <see cref="Complete"/>.
///
/// <para>Exists so a run reports progress on the line it already posted rather than by posting a new
/// line per observation. A sweep ticking several times a second would otherwise bury everything
/// around it in the log — and the thing a user actually wants to read (what is running, and how far
/// through it is) is a single value that changes, not a history of that value.</para>
///
/// <para><b>A sink that cannot update in place still works.</b> The default
/// <see cref="IMessageSink.BeginProgress"/> posts the opening line, ignores every update, and posts
/// the completion line — so a plain sink (a test fake, a headless driver) reports the start and the
/// end of the operation and simply has no bar in between.</para>
/// </summary>
public interface IProgressMessage
{
    /// <summary>
    /// Rewrites the live line. <paramref name="counter"/> is the CHANGING tail (a "1,194 / 2,525"
    /// count) and is rendered after the bar rather than inside <paramref name="text"/>;
    /// <paramref name="percentComplete"/> null leaves the bar as it is; <paramref name="indeterminate"/>
    /// marks work with no honest denominator.
    ///
    /// <para><b>Keep everything that changes in <paramref name="counter"/>.</b> The bar is drawn
    /// immediately after <paramref name="text"/>, so anything that grows in there moves the bar with
    /// it — which is exactly the twitching this split exists to remove.</para>
    /// </summary>
    void Update(string text, string? counter = null, double? percentComplete = null, bool indeterminate = false);

    /// <summary>Draws no bar while <paramref name="held"/>, whatever the row is told meanwhile: nothing is working, because the
    /// operation is waiting on a question. A row with no bar has nothing to hold.</summary>
    void Hold(bool held) { }

    /// <summary>
    /// Settles the line: raises it to <paramref name="level"/> and APPENDS the outcome to the END of
    /// the row — after the counter when there is one.
    ///
    /// <para>Appending rather than replacing is what keeps the finished row worth reading — it still
    /// names the analysis and the point count the run got through, with the outcome on the end,
    /// instead of collapsing to a bare "complete" that says nothing about what was done.</para>
    ///
    /// <para><paramref name="keepBar"/> (default <c>true</c>) keeps the bar visible, pinning an
    /// indeterminate one full so a finished row never shows a still-animating bar — the original
    /// behaviour, kept as the default for any future caller that wants it. Owner request,
    /// 2026-08-14, extended the same day to every existing call site (EM, circuit Analysis, and
    /// Mesh): pass <c>false</c> to drop the bar/percent entirely once the row settles, leaving only
    /// the appended text — a completed run's row should read as text, not keep showing a
    /// stalled-looking bar glyph.</para>
    /// </summary>
    void Finish(MessageLevel level, string outcome, bool keepBar = true);

    /// <summary>Settles the line by REPLACING its text and dropping its bar — for an outcome that
    /// makes the progress so far irrelevant rather than context for it.</summary>
    void Complete(MessageLevel level, string text);

    /// <summary>
    /// Settles the line like <see cref="Complete"/> and leaves an ACTION BUTTON where the bar was —
    /// the row the user was watching becomes the row they act on.
    ///
    /// <para>The one caller is the auto-update announcement (owner request, 2026-09-09): the
    /// download's bar reaches the end and turns into "Relaunch circuitRF" on that same line, rather
    /// than settling to a "- done." line with the offer arriving underneath it on a row of its own.
    /// The progress row and the thing it was leading up to are one event and read as one line.</para>
    ///
    /// <para><b>The default drops the button and keeps the sentence</b>, exactly as
    /// <see cref="IMessageSink.PostAction"/>'s does and for the same reason: a sink with no way to
    /// offer one still has to say what happened. So the text must read correctly with nothing to
    /// press.</para>
    /// </summary>
    void CompleteWithAction(MessageLevel level, string text, string actionLabel, Func<Task> action)
        => Complete(level, text);

    /// <summary>
    /// Binds the running operation's Cancel to this row, so the user can stop it by right-clicking its
    /// progress bar. Optional: a sink with no bar (a status line, a test fake) inherits the no-op.
    ///
    /// <para><b>Pass the SAME handle to every row of one operation.</b> A run that posts a sweep row
    /// and a stage row is one computation drawn twice — both bars must stop all of it, which is what
    /// sharing the handle means. See <see cref="RunCancellation"/>.</para>
    /// </summary>
    void BindCancellation(RunCancellation? cancellation) { }

    /// <summary>brief-dfr31-5 R-dfr31-5-5 — what the bar measures, as its tooltip; null for none. A row with no bar
    /// ignores it.</summary>
    void SetProgressTip(string? tip) { }
}

/// <summary>
/// Fallback for an <see cref="IMessageSink"/> that has no live-message support: the opening line is
/// already posted by <see cref="IMessageSink.BeginProgress"/>, updates are dropped, and completion
/// posts an ordinary message. Never silently swallows the outcome.
/// </summary>
internal sealed class PostOnlyProgressMessage(IMessageSink sink) : IProgressMessage
{
    public void Update(string text, string? counter = null, double? percentComplete = null, bool indeterminate = false) { }

    public void Finish(MessageLevel level, string outcome, bool keepBar = true) => sink.Post(level, outcome);

    public void Complete(MessageLevel level, string text) => sink.Post(level, text);

    /// <summary>There is no row to turn into a button, so the sink is asked for one of its own —
    /// which is what a sink with no live-message support could always have had.</summary>
    public void CompleteWithAction(MessageLevel level, string text, string actionLabel, Func<Task> action)
        => sink.PostAction(level, text, actionLabel, action);
}
