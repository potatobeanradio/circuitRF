using System;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// Rewrites one <see cref="MessageEntry"/> in place while a long operation runs.
///
/// <para><b>The thread marshaller is injected rather than reached for.</b> Progress arrives from
/// whatever background thread the engine is running on, so every mutation has to land on the UI
/// thread — but taking <c>Dispatcher.UIThread</c> directly would make this class untestable without
/// one, and a headless test run does not reliably own the dispatcher thread (it is created by
/// whichever test touches it first, which under a parallel run is some other class). Handing in the
/// marshaller lets <see cref="MessagesTool"/> supply the dispatcher and a test supply "just run it".</para>
/// </summary>
internal sealed class LiveProgressMessage(MessageEntry entry, Action<Action> marshal) : IProgressMessage
{
    /// <summary>The row being rewritten — the handle's own view of it, independent of whether the
    /// owning collection has been mutated yet.</summary>
    internal MessageEntry Entry { get; } = entry;

    public void Update(string text, string? counter = null, double? percentComplete = null, bool indeterminate = false)
        => marshal(() =>
        {
            Entry.Text = text;
            Entry.ProgressText = counter;
            Entry.ProgressIndeterminate = indeterminate;
            if (percentComplete is { } pct)
                Entry.ProgressPercent = Math.Clamp(pct, 0, 100);
        });

    public void Finish(MessageLevel level, string outcome, bool keepBar = true)
        => marshal(() =>
        {
            Entry.Level = level;

            // Onto the END of the row: after the counter when there is one, so the outcome reads as
            // the tail of the sentence rather than landing mid-row before the bar.
            if (Entry.HasProgressText) Entry.ProgressText = $"{Entry.ProgressText} - {outcome}";
            else                       Entry.Text         = $"{Entry.Text} - {outcome}";

            if (keepBar)
            {
                // The bar stays, pinned full if it was indeterminate: a finished row still showing an
                // animating bar reads as a run that never stopped.
                if (Entry.ProgressIndeterminate) Entry.ProgressPercent = 100;
                Entry.ProgressIndeterminate = false;
            }
            else
            {
                // Owner request, 2026-08-14: "the simulation progress bar glyph should be removed
                // from the Messages window... after the simulation is complete. The text that says
                // simulation is complete should remain." ProgressPercent null hides only the
                // ProgressBar (its IsVisible is bound to HasProgress) — ProgressText/Text, already
                // appended above, keep rendering as plain text on the same row.
                Entry.ProgressIndeterminate = false;
                Entry.ProgressPercent       = null;
            }
        });

    public void Complete(MessageLevel level, string text)
        => marshal(() =>
        {
            Entry.Level = level;
            Entry.Text  = text;
            Entry.ProgressText          = null;
            Entry.ProgressIndeterminate = false;
            Entry.ProgressPercent       = null;
        });
}
