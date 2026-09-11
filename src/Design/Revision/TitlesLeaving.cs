namespace CircuitRF.Design.Revision;

/// <summary>The three ways a history leaves this machine (<c>revision-control.md</c> §9, §9A).</summary>
public enum LeavingJourney
{
    /// <summary>Sending to the copy this workspace exchanges with (§9).</summary>
    Send,

    /// <summary>Somebody copying this workspace (§9).</summary>
    Copy,

    /// <summary>An archive with the history included (§9A).</summary>
    Archive,
}

/// <summary>One version title about to leave, as the review lists it.</summary>
/// <param name="CommitId">Its identity — carried so a correction started from the review acts on the
/// right entry, never shown in the list itself.</param>
/// <param name="Title">The line the other side will read.</param>
/// <param name="Correction">§5.11 case (c)'s annotation, when there is one. <b>Shown in place of the
/// original</b>, and the original is what <see cref="Title"/> still holds.</param>
/// <param name="WhenUtc">When it was kept, for the column that makes forty rows scannable.</param>
/// <param name="Who">Who kept it, on a workspace with more than one author.</param>
/// <param name="Sequence">
/// The restore point's own ordering number, on the archive journey's checkpoint rows; null on a
/// version. <b>It is what a correction started from this list acts on</b> (R-rc11-19), and the two
/// kinds take different corrections — a label is renamed outright, a title is corrected or annotated
/// depending on whether it has been shared.
/// </param>
public sealed record LeavingTitle(
    string         CommitId,
    string         Title,
    string?        Correction,
    DateTimeOffset WhenUtc,
    string         Who,
    long?          Sequence = null)
{
    /// <summary>Whether this row is a version. A restore point only ever appears on the archive
    /// journey, because that is the only one that carries them (§5.2a).</summary>
    public bool IsVersion => Sequence is null;

    /// <summary>What the other side reads first: the correction if there is one, the title otherwise.</summary>
    public string Shown => Correction is { Length: > 0 } c ? c : Title;

    /// <summary>Whether the original is a different string from what is shown — the row that needs both.</summary>
    public bool HasCorrection => Correction is { Length: > 0 };
}

/// <summary>
/// <b>The review in front of the three operations that let a history off this machine</b>
/// (<c>docs/design/revision-control.md</c> §5.11, §9, §9A.3; §12 Q36; RC-11 R-rc11-16 … R-rc11-19).
///
/// <para><b>It is worth more than every correction mechanism in §5.11</b> (R-rc11-17). The expensive
/// case is not a careless word: it is a customer's name or a part number in a title going to a
/// <i>different</i> customer, and nobody can recall from memory what forty titles say. §9A.3 already
/// establishes where a computation the user cannot perform belongs — in front of the operation — and
/// this is that computation.</para>
///
/// <para><b>One list, from one function, for all three journeys</b> (R-rc11-16). Three call sites each
/// working out what leaves would be three chances for one of them to be wrong about it, and the one
/// that was wrong would be the one nobody checked.</para>
///
/// <para><b>It composes with §9A.3's existing warning rather than replacing it</b> (R-rc11-18). That
/// warning is about FILES a history still holds after they were deleted from the workspace; this is
/// about TITLES. Both are true, they are about different things, and the archive dialog carries
/// both.</para>
///
/// <para><b>Restore points are deliberately absent, on two of the three journeys.</b> §5.2a's table is
/// the reference: a checkpoint does not travel with a send or a copy at all, so listing one would be
/// telling a designer to worry about a string that is not going anywhere. An archive DOES carry them —
/// it copies the directory — and their labels are listed there for that reason and only there.</para>
///
/// <para><b>Nothing here reaches a network</b>, on any journey. What a send would carry is computed
/// from the remote-tracking reference a fetch already put on this machine, which is the same
/// computation §5.11's own predicate makes (<see cref="VersionSharing"/>) read from the other
/// end.</para>
/// </summary>
public static class TitlesLeaving
{
    /// <summary>
    /// The titles this journey would take, newest first.
    /// </summary>
    /// <param name="journey">
    /// Which operation is about to happen. <b>It changes the answer, and not only the wording</b>: a
    /// send carries the versions the other copy does not have yet, while a copy and an archive carry
    /// every version there is.
    /// </param>
    public static IReadOnlyList<LeavingTitle> For(GitCommand git, LeavingJourney journey)
    {
        var corrections = VersionCorrections.Annotations(git);

        var versions = journey == LeavingJourney.Send
                     ? NotYetSent(git)
                     : HistoryBrowser.Versions(git);

        List<LeavingTitle> titles = [];
        foreach (var v in versions)
            titles.Add(new LeavingTitle(
                v.CommitId, v.Title,
                corrections.TryGetValue(v.CommitId, out var c) ? c.Title : null,
                v.WhenUtc, v.Who));

        // An archive carries the restore points too (§5.2a), and their labels are what a designer
        // wrote or an agent declared just as a version's title is. Listed after the versions rather
        // than merged among them: the two kinds do not travel alike, and this is the one dialog where
        // that difference is the point.
        if (journey == LeavingJourney.Archive)
            foreach (var p in RestorePoints.ListIncludingThinned(git))
                titles.Add(new LeavingTitle(p.CommitId, p.Label, null, p.TakenUtc, "", p.Sequence));

        return titles;
    }

    /// <summary>
    /// The versions on this workspace's line of work that the other copy does not have.
    ///
    /// <para><c>--not &lt;remote reference&gt;</c> is the whole of it, and it is
    /// <see cref="HistoryBrowser.Incoming"/>'s query read in the other direction. <b>With no other copy
    /// there is nothing to send</b>, which is the ordinary answer and is not an error — the menu item
    /// that would have led here is greyed on the same fact.</para>
    ///
    /// <para><b>A remote configured and never fetched sends EVERYTHING</b>, and that is the honest
    /// answer rather than a cautious one: this machine has never heard from the other end, so as far
    /// as it knows every version it holds is about to go. The same unknown that makes
    /// <see cref="VersionSharing"/> answer "shared" makes this answer "all of them", and the two
    /// readings are consistent — one is about what has already left, the other about what is about
    /// to.</para>
    /// </summary>
    private static IReadOnlyList<HistoryVersion> NotYetSent(GitCommand git)
    {
        if (WorkspaceRemotes.OtherCopy(git) is null) return [];
        if (WorkspaceRemotes.IncomingRef(git) is not { } reference)
            return HistoryBrowser.Versions(git);

        var listed = git.Run(["rev-list", "HEAD", "--not", reference],
                             new GitRunOptions(ReadOnly: true));
        if (!listed.Ok) return HistoryBrowser.Versions(git);

        var wanted = new HashSet<string>(
            listed.StdOut.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => s.Length > 0),
            StringComparer.Ordinal);

        return [.. HistoryBrowser.Versions(git).Where(v => wanted.Contains(v.CommitId))];
    }

    /// <summary>The journey's own wording, for <see cref="HistoryMessages.TitlesLeaving"/>.</summary>
    public static string Describe(LeavingJourney journey) => journey switch
    {
        LeavingJourney.Send    => HistoryMessages.JourneySend,
        LeavingJourney.Copy    => HistoryMessages.JourneyCopy,
        _                      => HistoryMessages.JourneyArchive,
    };

    /// <summary>One row, as a headless caller reads it — the same shape
    /// <see cref="HistoryList.Line"/> uses, so a script sorting one can sort the other.</summary>
    public static string Line(LeavingTitle title)
        => $"{title.WhenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {title.Shown}"
         + (title.HasCorrection ? $"  [corrected; originally '{title.Title}']" : "");
}
