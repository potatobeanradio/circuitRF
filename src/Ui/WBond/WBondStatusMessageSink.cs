using System;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// An <see cref="IMessageSink"/> over a single status line — the standalone <c>wBond</c> binary's
/// answer to circuitRF's Messages panel.
///
/// <h3>Why the standalone binary needs one at all</h3>
/// <para>The shell is one plain window around one editor: no Dock, no workspace, and <b>no Messages
/// region</b> (see <c>WBondShellWindow</c>). Until a wirebond MoM run existed that was fine — nothing
/// it could do took longer than a repaint. A distributed export is minutes, and with no sink at all
/// <see cref="WBondBackgroundRun"/> is silent for the whole of it: the window stays responsive, which
/// is the important half, but nothing anywhere says why.</para>
///
/// <h3>One line, and the last write wins — which lands on the right row by itself</h3>
/// <para>Two live rows cannot both be shown on one line, and no rule is needed to choose between them:
/// <see cref="WBondBackgroundRun.Report"/> updates the sweep row and then the stage row inside one
/// synchronous callback, so the stage row is simply what the line holds when the frame is drawn. That
/// is also the more useful of the two here — the stage text is the answer to "what is it doing", and
/// the point counter it overwrites is the half a user with no panel can least act on.</para>
///
/// <para>There is no bar. A status line is text, and inventing a glyph for a percentage would be a
/// second progress widget to keep in step with the panel's real one.</para>
/// </summary>
internal sealed class WBondStatusMessageSink(Action<string, bool> show) : IMessageSink
{
    private readonly Action<string, bool> _show = show ?? throw new ArgumentNullException(nameof(show));

    public void Post(MessageLevel level, string text, string? filePath = null)
        => _show(text, level is MessageLevel.Warning or MessageLevel.Error);

    public IProgressMessage BeginProgress(string text)
    {
        _show(text, false);
        return new StatusProgress(_show);
    }

    /// <summary>Nothing to clear — the line holds one message and the next one replaces it.</summary>
    public void Clear() { }

    private sealed class StatusProgress(Action<string, bool> show) : IProgressMessage
    {
        public void Update(string text, string? counter = null, double? percentComplete = null,
                           bool indeterminate = false)
            => show(counter is null ? text : $"{text}  {counter}", false);

        public void Finish(MessageLevel level, string outcome, bool keepBar = true)
            => show(outcome, level is MessageLevel.Warning or MessageLevel.Error);

        public void Complete(MessageLevel level, string text)
            => show(text, level is MessageLevel.Warning or MessageLevel.Error);
    }
}
