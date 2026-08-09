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

    /// <summary>
    /// Settles the line: raises it to <paramref name="level"/> and APPENDS the outcome to the END of
    /// the row — after the counter when there is one — keeping its bar.
    ///
    /// <para>Appending rather than replacing is what keeps the finished row worth reading — it still
    /// names the analysis and the point count the run got through, with the outcome on the end,
    /// instead of collapsing to a bare "complete" that says nothing about what was done. The bar
    /// stays for the same reason, and an indeterminate one is pinned full so a finished row never
    /// shows a still-animating bar.</para>
    /// </summary>
    void Finish(MessageLevel level, string outcome);

    /// <summary>Settles the line by REPLACING its text and dropping its bar — for an outcome that
    /// makes the progress so far irrelevant rather than context for it.</summary>
    void Complete(MessageLevel level, string text);
}

/// <summary>
/// Fallback for an <see cref="IMessageSink"/> that has no live-message support: the opening line is
/// already posted by <see cref="IMessageSink.BeginProgress"/>, updates are dropped, and completion
/// posts an ordinary message. Never silently swallows the outcome.
/// </summary>
internal sealed class PostOnlyProgressMessage(IMessageSink sink) : IProgressMessage
{
    public void Update(string text, string? counter = null, double? percentComplete = null, bool indeterminate = false) { }

    public void Finish(MessageLevel level, string outcome) => sink.Post(level, outcome);

    public void Complete(MessageLevel level, string text) => sink.Post(level, text);
}
