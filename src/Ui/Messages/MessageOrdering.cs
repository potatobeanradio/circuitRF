using System.Collections.Generic;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// <b>Where a new message goes in the log — which is not always the end.</b>
///
/// <para>Owner request, 2026-09-13: "keep the simulation progress bar at the bottom of the messages
/// list". A long run is precisely the thing that posts messages WHILE it runs — the EM sweep's own
/// notes, a mesh warning, an engine diagnostic — and appending them left the one row the user is
/// actually watching drifting upward as the run talked, with the newest text stranded below it.</para>
///
/// <para>It is a pure function of the list so it can be gated without a dispatcher: <c>MessagesTool</c>
/// marshals to the UI thread and then calls this, and a test calls it directly. The rule lives here
/// rather than in the dock view model because it is a property of the LOG, not of the panel.</para>
/// </summary>
internal static class MessageOrdering
{
    /// <summary>
    /// The index a new message takes: above the LAST still-running row, and above any of its own
    /// siblings directly before it.
    ///
    /// <para><b>Not simply "the end less any trailing live rows", and the EM run is why.</b> That run
    /// owns two rows — a sweep row carrying the bar and a stage row naming the current step — and it
    /// settles the STAGE row first, then posts its twenty-odd notes, then settles the sweep row with
    /// its summary. Under a trailing-only rule the settled stage row breaks the chain, every note
    /// lands below the live bar, and the bar the user is watching ends up in the middle of the log
    /// again. So the search is for the last LIVE row and then back over the contiguous live block it
    /// ends: a run's own rows stay together at the bottom whatever order it settles them in.</para>
    ///
    /// <para>With nothing running this is a plain append, so a log with no run in it is ordered
    /// exactly as it always was.</para>
    /// </summary>
    public static int InsertIndexFor(IList<MessageEntry> messages)
    {
        int last = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
            if (messages[i].IsLiveProgress) { last = i; break; }

        if (last < 0) return messages.Count;

        int at = last;
        while (at > 0 && messages[at - 1].IsLiveProgress) at--;
        return at;
    }

    /// <summary>
    /// Puts <paramref name="entry"/> in the log — <b>or counts it against the identical message
    /// already sitting where it would go</b>.
    /// </summary>
    /// <remarks>
    /// <b>Three identical paragraphs read as three problems</b> (field report, 2026-09-22). A
    /// designer arming footprint placement three times on a technology declaring no courtyard layer
    /// got the same 300-character warning three times in a row. The warning is correct and is
    /// reported per arming DELIBERATELY — reporting it once per generated cell was the defect the
    /// previous round fixed, because the second placement then said nothing. What was wrong is only
    /// that the log repeated it rather than counting it.
    ///
    /// <para><b>CONSECUTIVE ONLY, and never across an intervening message.</b> Two identical
    /// warnings with something else between them are two separate episodes, and where each one fell
    /// is exactly what a timestamped log is for. The comparison is on the level, the text and the
    /// file — a row whose text is still CHANGING is a live progress row and is excluded outright,
    /// since collapsing two of those would merge two running operations into one.</para>
    ///
    /// <para><b>The timestamp is left at the FIRST occurrence.</b> A repeat count answers "how many
    /// times"; the time the condition was first met is the one a reader correlates against what they
    /// were doing, and overwriting it would lose that to gain nothing.</para>
    /// </remarks>
    public static void Insert(IList<MessageEntry> messages, MessageEntry entry)
    {
        int at = InsertIndexFor(messages);

        if (at > 0 && !entry.IsLiveProgress && IsSameMessage(messages[at - 1], entry))
        {
            messages[at - 1].RepeatCount++;
            return;
        }

        messages.Insert(at, entry);
    }

    private static bool IsSameMessage(MessageEntry existing, MessageEntry entry) =>
        !existing.IsLiveProgress
        && existing.Level == entry.Level
        && string.Equals(existing.Text, entry.Text, System.StringComparison.Ordinal)
        && string.Equals(existing.FilePath ?? "", entry.FilePath ?? "", System.StringComparison.Ordinal)
        && existing.ActionLabel is null && entry.ActionLabel is null;
}
