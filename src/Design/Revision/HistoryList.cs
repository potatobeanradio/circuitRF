namespace CircuitRF.Design.Revision;

/// <summary>
/// What one row of the merged history list is (<c>docs/design/revision-control.md</c> §5.10;
/// RC-10 R-rc10-2, R-rc10-11).
///
/// <para><b>Three kinds, not two.</b> §5.10's argument is about two kinds of ENTRY — a version and a
/// restore point — and the gap is neither: it is a stretch in which nothing was recorded, and it is
/// the one row in the list that describes an absence rather than a state.</para>
/// </summary>
public enum HistoryEntryKind
{
    /// <summary>A version: titled, permanent, and it travels with a copy (§5.2).</summary>
    Version,

    /// <summary>A restore point: this machine's safety net, and it does not travel (§5.2a).</summary>
    RestorePoint,

    /// <summary>An off period, with its reason (R-rc10-11).</summary>
    Gap,
}

/// <summary>
/// The five things the filter can switch (R-rc10-7).
///
/// <para><b>Three origins are deliberately not on this list and are always shown</b> —
/// <see cref="CheckpointOrigin.BeforeRestore"/>, <see cref="CheckpointOrigin.RecordingOff"/> and
/// <see cref="CheckpointOrigin.RecordingOn"/>. None of them is noise: a restore is rare and its entry
/// is the whole content of §5.8's promise that going back is not a one-way door, and an off period has
/// exactly two ends, which are what stop it rendering as a quiet fortnight. A checkbox nobody would
/// think to tick is how R-rc10-20's promise leaves the place the promise was made.</para>
/// </summary>
public enum HistoryEntryClass
{
    /// <summary>A version the designer titled.</summary>
    Version,

    /// <summary>A save-point the designer asked for.</summary>
    SavePoint,

    /// <summary>The entry taken before an assistant's batch.</summary>
    AiBatch,

    /// <summary>The entry taken because the workspace closed — the only one hidden by default.</summary>
    Automatic,

    /// <summary>Not a class of its own but a cross-cut: an entry retention thinned (RC-6 R-rc6-4).</summary>
    TidiedAway,
}

/// <summary>
/// <b>What the merged panel is showing, and the one thing that replaced a second window</b>
/// (§5.10 rules 3 and 4; R-rc10-5, R-rc10-7, R-rc10-8, R-rc10-9).
///
/// <para><b>The default is every entry somebody stated an intent for.</b> Versions, save-points and
/// before-batch checkpoints are shown; the workspace-close entry is not. That is the whole of the
/// answer to the close checkpoint being too much, and it is a display decision on purpose — the close
/// checkpoint keeps being taken (R-rc10-6), because it is the one boundary that reliably exists in
/// every session, including the ones where the designer never thought about history at all.</para>
///
/// <para><b>Per-user view state, and never a Settings row</b> (R-rc10-8). Every row on §10A's tab
/// changes what is KEPT; a filter a designer flips while hunting for something is not a preference
/// about what exists, and putting it there is the category error that tab is most exposed to.</para>
/// </summary>
/// <param name="Versions">Show the versions.</param>
/// <param name="SavePoints">Show the save-points the designer asked for.</param>
/// <param name="AiBatches">Show the entries taken before an assistant's batch.</param>
/// <param name="Automatic">Show the workspace-close entries. <b>Off by default</b>, and the only one
/// that is.</param>
/// <param name="TidiedAway">Show the entries retention thinned, marked (RC-6 R-rc6-4). On by default:
/// hiding them is a filter choice, never a new state.</param>
/// <param name="Search">What a person wrote — titles, batch intents and the author (R-rc10-9). Empty
/// means no search, which is not the same as a search that matches everything.</param>
public sealed record HistoryFilter(
    bool   Versions   = true,
    bool   SavePoints = true,
    bool   AiBatches  = true,
    bool   Automatic  = false,
    bool   TidiedAway = true,
    string Search     = "")
{
    /// <summary>What the panel and <c>history list</c> both open on (R-rc10-22). One value, so the two
    /// cannot differ silently.</summary>
    public static readonly HistoryFilter Default = new();

    /// <summary>Whether a search term is in force.</summary>
    public bool IsSearching => Search.Trim().Length > 0;

    /// <summary>Whether this class of entry is shown.</summary>
    public bool Shows(HistoryEntryClass kind) => kind switch
    {
        HistoryEntryClass.Version    => Versions,
        HistoryEntryClass.SavePoint  => SavePoints,
        HistoryEntryClass.AiBatch    => AiBatches,
        HistoryEntryClass.Automatic  => Automatic,
        HistoryEntryClass.TidiedAway => TidiedAway,
        _                            => true,
    };
}

/// <summary>
/// One row of the merged list — a version, a restore point, or a gap.
/// </summary>
/// <param name="Kind">Which of the three this is.</param>
/// <param name="Version">The version, on a version row.</param>
/// <param name="Point">The restore point, on a restore-point row.</param>
/// <param name="Gap">The off period, on a gap row.</param>
/// <param name="WhenUtc">The one term the three orderings share — see <see cref="HistoryList"/>.</param>
/// <param name="Shared">
/// Whether this entry has left the machine (§5.10's expander column, and the hinge of §5.11).
///
/// <para><b>Always false for a restore point</b>, and that is not an omission: §5.2a says a checkpoint
/// does not clone, so "local-only" is a property of the kind rather than of the entry.</para>
/// </param>
/// <param name="Correction">
/// §5.11 case (c)'s annotation, when the author wrote one (RC-11 R-rc11-13; RC-12 R-rc12-6 added its
/// note half). <b>Shown in place of the original, with the original one click away</b> — never instead
/// of it, because the original is still in the file and the dialog that wrote this said so.
/// </param>
public sealed record HistoryEntry(
    HistoryEntryKind Kind,
    HistoryVersion?  Version,
    RestorePoint?    Point,
    RevisionGap?     Gap,
    DateTimeOffset   WhenUtc,
    bool             Shared = false,
    VersionCorrection? Correction = null)
{
    public bool IsVersion => Kind == HistoryEntryKind.Version;
    public bool IsPoint   => Kind == HistoryEntryKind.RestorePoint;
    public bool IsGap     => Kind == HistoryEntryKind.Gap;

    /// <summary>Which filter class this row belongs to, or null for a gap — a gap answers to no
    /// checkbox, because an off period is not an entry somebody chose to make.</summary>
    public HistoryEntryClass? Class => Kind switch
    {
        HistoryEntryKind.Version      => HistoryEntryClass.Version,
        HistoryEntryKind.RestorePoint => Point!.Origin switch
        {
            CheckpointOrigin.SavePoint       => HistoryEntryClass.SavePoint,
            CheckpointOrigin.BeforeBatch     => HistoryEntryClass.AiBatch,
            CheckpointOrigin.WorkspaceClosed => HistoryEntryClass.Automatic,
            _                                => null,
        },
        _ => null,
    };

    /// <summary>Retention tidied this one away (RC-6 R-rc6-4). Never true of a version — §5.6 rule 5
    /// puts human-written commits outside retention's scope entirely.</summary>
    public bool Thinned => Point is { Thinned: true };

    /// <summary>
    /// The title, or the origin sentence for an entry nobody titled (R-rc10-12) — <b>and the
    /// correction in place of either, when the author wrote one</b> (R-rc11-13).
    /// </summary>
    public string Title => Correction?.Title is { Length: > 0 } corrected ? corrected : OriginalTitle;

    /// <summary>
    /// What was actually written down. <b>One click away, never gone</b> (R-rc11-14): the original
    /// wording stays in the history and can still be read by anyone holding the workspace, and the
    /// dialog that offered the correction said so in a sentence that may not be softened.
    /// </summary>
    public string OriginalTitle => Kind switch
    {
        HistoryEntryKind.Version      => Version!.Title,
        HistoryEntryKind.RestorePoint => LabelOf(Point!),
        _                             => "",
    };

    /// <summary>
    /// <b>What a person wrote is read back; what circuitRF generated is rendered afresh</b> (owner,
    /// 2026-09-07).
    ///
    /// <para><see cref="RestorePoint.Intent"/> is exactly "the words a person supplied" — a save-point
    /// they named, a batch's declared intent, and a rename, which writes the corrected words to BOTH
    /// halves precisely so this distinction keeps working. Empty means the label on disk is circuitRF's
    /// own wording for the origin, and there is no reason to show a workspace recorded last month
    /// vocabulary this release no longer uses. Rendering it from the current wording is what lets the
    /// reword of "before going back" reach the entries a designer already has, rather than only the
    /// ones they make from now on.</para>
    /// </summary>
    private static string LabelOf(RestorePoint point)
        => point.Intent is { Length: > 0 }
         ? point.Label
         : CheckpointMessage.SubjectFor(point.Origin, null);

    /// <summary>Whether §5.11 case (c)'s annotation is what the row is showing.</summary>
    public bool IsCorrected => Correction?.Title is { Length: > 0 } corrected
                            && !string.Equals(corrected, OriginalTitle, StringComparison.Ordinal);

    /// <summary>
    /// <b>The longer note on this entry</b> (§5.12) — the corrected one where the author wrote one,
    /// and otherwise what they wrote when they recorded it. Empty is the ordinary answer.
    /// </summary>
    public string Note => Correction?.Note is { Length: > 0 } corrected ? corrected : OriginalNote;

    /// <summary>What was actually recorded, for <see cref="OriginalTitle"/>'s reason: a correction
    /// never erases, and the expander shows both.</summary>
    public string OriginalNote => Kind switch
    {
        HistoryEntryKind.Version      => Version!.Note,
        HistoryEntryKind.RestorePoint => Point!.Note,
        _                             => "",
    };

    /// <summary>Whether §5.11 case (c)'s annotation is what the note being shown came from.</summary>
    public bool IsNoteCorrected => Correction?.Note is { Length: > 0 } corrected
                                && !string.Equals(corrected, OriginalNote, StringComparison.Ordinal);

    /// <summary>Whether this entry carries a note at all.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>Who kept it. Blank on a restore point, which nobody else ever sees.</summary>
    public string Who => Version?.Who ?? "";

    /// <summary>What the designer or the agent supplied before any shaping — a batch's own stated
    /// intent, which is what R-rc10-9's search is over as well as the title.</summary>
    public string Intent => Point?.Intent ?? "";

    /// <summary>RC-5's ordering number (§5.6 rule 2), on the entries that have one.</summary>
    public long? Sequence => Point?.Sequence;

    /// <summary>The identity, shown only in an expander and only because opening one is the explicit
    /// action R-rc10-15 permits it under.</summary>
    public string Identity => Version?.CommitId ?? Point?.CommitId ?? "";

    /// <summary>
    /// <b>The CONTENT this entry holds</b>, which is what decides whether going back to it would change
    /// anything (owner, 2026-09-08). Two entries over one tree are two identities and one state, so an
    /// identity comparison answers the wrong question — the same reasoning
    /// <see cref="WayForward.LeadsSomewhereElse"/> already states one type over.
    /// </summary>
    public string TreeId => Version?.TreeId ?? Point?.TreeId ?? "";
}

/// <summary>
/// <b>What a restore left the designer looking at, and the way forward from it</b> (R-rc10-18, §5.8,
/// §12 Q35).
///
/// <para><b>It creates nothing.</b> The entry it names already exists — §5.8's restore takes it before
/// it writes a single file — and reaching it is an ordinary restore with an ordinary checkpoint of its
/// own. All this record does is carry the two names to the panel, so the reassurance is offered in the
/// place the designer is standing rather than in the panel they did not open.</para>
///
/// <para><b>It lives for the session and is written nowhere.</b> A restore is a moment, not a state:
/// recording it on disk would put a sentence about one afternoon into a workspace that outlives it,
/// and R-rc10-4 forbids this brief from changing what is stored in any case.</para>
/// </summary>
/// <param name="WentBackTo">The entry the workspace was put into, as the designer read it when they
/// chose it.</param>
/// <param name="KeptAs">The entry the replaced state was kept as — <b>the one the way forward goes
/// to</b>, and the one R-rc10-20 stops retention thinning.</param>
/// <param name="WentBackTo">The entry the workspace was put back to, as its title read at the time.</param>
/// <param name="KeptAs">The entry holding what was replaced — the way forward itself.</param>
/// <param name="WentBackToId">
/// <b>The identity of the state the workspace was put into</b> — which is what the sentence NAMES it by
/// since 2026-09-08, a generated label having turned out to name every second entry identically.
///
/// <para>It is still tracked across a correction for the reason it originally was: both correction
/// paths that rewrite a commit change its identity, and a sentence left pointing at the old one would
/// quietly stop matching the row it is about. Empty means unknown, which matches nothing and is left
/// alone.</para>
/// </param>
/// <param name="WentBackToUtc">When that state was taken, for the sentence to say so beside the
/// identity. An identity alone answers <i>which</i> and never <i>when</i>, and <i>when</i> is what a
/// designer is actually navigating by.</param>
/// <param name="WentBackToTreeId">The CONTENT the workspace was put into, for
/// <see cref="LeadsSomewhereElse"/> to compare against. The identity would not do on its own: two
/// entries holding identical content are two different identities, and a way back to one of them is
/// still a way back to where the designer already is.</param>
public sealed record WayForward(
    string          WentBackTo,
    RestorePoint    KeptAs,
    string          WentBackToId    = "",
    DateTimeOffset? WentBackToUtc   = null,
    string          WentBackToTreeId = "")
{
    /// <summary>
    /// <b>Whether the way back goes anywhere</b> (owner, 2026-09-08: the panel said <i>Now at
    /// f15b942</i> over a button reading <i>Go back to f15b942</i>).
    ///
    /// <para>A restore that replaced content identical to what it wrote replaced nothing, and
    /// <see cref="WorkspaceRestore"/> then resolves the entry holding the state being replaced to an
    /// entry holding the target's own content — correctly, because that IS where the workspace was.
    /// The way back is what stops being meaningful, not the restore: there is no earlier state to
    /// return to, so the action is withdrawn and the line says only where the workspace is.</para>
    ///
    /// <para><b>Content first, identity as the backstop.</b> The tree is the fact that matters; the
    /// identity catches the same defect on a record built before the tree was carried, and costs one
    /// comparison.</para>
    /// </summary>
    public bool LeadsSomewhereElse
        => !string.Equals(WentBackToId, KeptAs.CommitId, StringComparison.Ordinal)
        && !(WentBackToTreeId.Length > 0
             && string.Equals(WentBackToTreeId, KeptAs.TreeId, StringComparison.Ordinal));

    /// <summary>
    /// This sentence with the corrected wording in it, when the correction is to the entry this names.
    /// Returns the same instance otherwise — matched on IDENTITY, never on the text, because titles
    /// collide constantly and rewriting the wrong sentence is worse than leaving a stale one.
    /// </summary>
    public WayForward Retitled(string commitId, string title)
        => WentBackToId.Length > 0 && string.Equals(WentBackToId, commitId, StringComparison.Ordinal)
         ? this with { WentBackTo = title.ReplaceLineEndings(" ").Trim() }
         : this;
}

/// <summary>
/// <b>What one read of a workspace's history holds, before any filter is applied</b> (RC-11).
///
/// <para>The panel re-reads this when the history CHANGES — a boundary, a workspace switch — and
/// filters it when a designer flips a checkbox. That is the whole of the separation, and it is the
/// difference between a filter that responds to the pointer and one that starts a dozen
/// subprocesses.</para>
///
/// <para><b>It is a value, not a cache.</b> Nothing holds one between operations and nothing
/// invalidates one: the caller reads a fresh one at every boundary exactly as it always did, and a
/// list that outlived its repository would offer a designer a way back to a state that is no longer
/// there.</para>
/// </summary>
/// <param name="Versions">This workspace's own line of work, newest first.</param>
/// <param name="Points">The safety net, including the entries retention thinned.</param>
/// <param name="Incoming">What a Pull brought in and nobody has chosen yet (R-rc9-6).</param>
/// <param name="Shared">What has left this machine (R-rc11-2).</param>
/// <param name="Corrections">§5.11 case (c)'s annotations, keyed by identity.</param>
/// <param name="AutomaticCount">
/// How many workspace-close entries there are — R-rc10-5's empty-line arithmetic, counted from the
/// list already read rather than by reading it a second time.
/// </param>
public sealed record HistorySources(
    IReadOnlyList<HistoryVersion>                  Versions,
    IReadOnlyList<RestorePoint>                    Points,
    IReadOnlyList<HistoryVersion>                  Incoming,
    SharedVersions                                 Shared,
    IReadOnlyDictionary<string, VersionCorrection> Corrections,
    int                                            AutomaticCount)
{
    /// <summary>A workspace with no history, or none circuitRF can read — the ordinary empty answer.</summary>
    public static readonly HistorySources Nothing = new(
        [], [], [], SharedVersions.Local,
        new Dictionary<string, VersionCorrection>(StringComparer.Ordinal), 0);

    /// <summary>
    /// <b>The most recently recorded entry, over both lists</b> — and therefore the content the
    /// workspace on disk holds, for as long as nothing has been written into it since (owner,
    /// 2026-09-08).
    ///
    /// <para>That pairing is the whole point: <i>has anything been written since the last entry</i> is
    /// answerable with no git at all, and combined with this it says what the workspace CONTAINS
    /// without hashing a single file. It is what withdraws the offer to go back to the state the
    /// workspace is already in, and what greys the two keep actions rather than letting them take a
    /// title and then record nothing.</para>
    ///
    /// <para><b>Thinned entries count.</b> Retention tidying one away does not change what it holds, and
    /// this is a statement about content rather than about what the list shows. Null when there is no
    /// history at all — the case where a keep always records.</para>
    ///
    /// <para><b>The clock is the only term the two lists share</b>, which is
    /// <see cref="HistoryList.Build"/>'s own caveat: under a clock fault this can name the wrong one of
    /// two entries recorded seconds apart. The consequence is a button offered or withheld, never a
    /// state altered, and each row still carries its own date.</para>
    /// </summary>
    public HistoryEntry? Newest
    {
        get
        {
            HistoryEntry? newest = null;

            if (Versions.Count > 0)
                newest = new HistoryEntry(HistoryEntryKind.Version, Versions[0], null, null,
                                          Versions[0].WhenUtc);

            if (Points.Count > 0 && (newest is null || Points[0].TakenUtc > newest.WhenUtc))
                newest = new HistoryEntry(HistoryEntryKind.RestorePoint, null, Points[0], null,
                                          Points[0].TakenUtc);

            return newest;
        }
    }

    /// <summary>
    /// <b>The same sources with one entry's tidied-away mark set the other way</b> — the local half of
    /// tidying an entry away and of bringing one back (owner, 2026-09-07).
    ///
    /// <para><b>Why this exists rather than a re-read.</b> Both operations are ONE reference write —
    /// 18 ms measured — and both were followed by <see cref="ReadSources"/>, which is 180 ms on a
    /// sixty-entry workspace and rises with it. That 180 ms is on the UI thread, and it re-reads the
    /// versions, the incoming set, the sharing set and the annotations, none of which either operation
    /// can have changed. The one thing that DID change is a boolean this record already holds.</para>
    ///
    /// <para><b>It is a transition, not a guess.</b> The caller applies it only after the write it
    /// mirrors reported success; a write that failed refreshes for real, because then the panel and the
    /// repository genuinely disagree and the panel is the one that is wrong. That is the same rule
    /// RC-11 established for the filter checkboxes — redraw from what is in hand, and never let the
    /// list drift from what a restore would actually do.</para>
    ///
    /// <para>Matched on identity. An entry this does not name is returned untouched, and so is the
    /// whole record when nothing matches, so a caller cannot silently redraw a list it did not
    /// change.</para>
    /// </summary>
    public HistorySources WithThinned(string commitId, bool thinned)
    {
        if (commitId.Length == 0) return this;

        RestorePoint[]? next = null;

        for (int i = 0; i < Points.Count; i++)
        {
            if (!string.Equals(Points[i].CommitId, commitId, StringComparison.Ordinal)) continue;
            if (Points[i].Thinned == thinned) return this;

            next    = [.. Points];
            next[i] = Points[i] with { Thinned = thinned };
            break;
        }

        return next is null ? this : this with { Points = next };
    }

    /// <summary>
    /// The rows one filter shows. <b>Pure</b> — no process starts, nothing is read from disk — which
    /// is what makes a checkbox a checkbox.
    ///
    /// <para><b>Anything a Pull brought in goes on top, never sorted in</b> (R-rc9-6). An incoming
    /// version is not part of this workspace's history until somebody chooses it, and time ordering
    /// would put it on top almost always and occasionally not — leaving one stranded mid-list under a
    /// mark nobody would look for there.</para>
    /// </summary>
    public HistoryList.Result Under(HistoryFilter filter)
    {
        var own = HistoryList.Build(Versions, Points, Shared, filter, Corrections);
        if (Incoming.Count == 0) return own;

        var waiting = HistoryList.Build(Incoming, [], SharedVersions.Local, filter, Corrections);
        return new HistoryList.Result([.. waiting.Rows, .. own.Rows], own.ThinnedMatchesNotShown);
    }
}

/// <summary>
/// <b>The one merged history, read once and shown in one panel</b>
/// (<c>docs/design/revision-control.md</c> §5.10; RC-10 R-rc10-1 … R-rc10-11, R-rc10-21).
///
/// <para><b>This is a presentation type and it may never become a storage one</b> (§5.10 rule 6,
/// R-rc10-4). Checkpoints stay on §5.2a's per-checkpoint references and still do not clone; versions
/// stay ordinary commits on the line of work. The moment the two are stored alike, §5.2a's travel
/// table stops being true and §5.6's retention has a human-written commit in its scope — so nothing
/// here writes anything, and the merge exists entirely in the ordering below.</para>
///
/// <para><b>It lives below the firewall so the panel and <c>circuitrf history list</c> read the same
/// list</b> (R-rc10-21, R-rc10-22). A verb that cannot express what the window shows means an agent
/// and a designer are reading two different histories, and two defaults that differ silently is the
/// defect R-rc10-22 exists to prevent.</para>
///
/// <para><b>The clock is the only term the three orderings share</b>, which is inherited from
/// <see cref="HistoryBrowser.Rows"/> and is stated there in full: versions are ordered by the line of
/// work, restore points by RC-5's own sequence, and a gap by the pair of entries that bracket it.
/// Under a clock fault a row can therefore float to the wrong place — visibly, since every row carries
/// its own date, which is the failure worth having.</para>
/// </summary>
public static class HistoryList
{
    /// <summary>
    /// The rows the panel shows, newest first, together with what the filter left out that a designer
    /// asked for by name.
    /// </summary>
    /// <param name="Rows">What is shown.</param>
    /// <param name="ThinnedMatchesNotShown">
    /// R-rc10-10. <b>How many entries retention tidied away also match the search and are not in
    /// <paramref name="Rows"/>.</b>
    ///
    /// <para>Zero unless a search is in force AND the tidied-away class is switched off — which is the
    /// only combination in which the answer is incomplete. <i>Three tidied-away entries also match</i>
    /// is the difference between an incomplete answer and a wrong one; a silently shorter list is the
    /// wrong one.</para>
    /// </param>
    public sealed record Result(
        IReadOnlyList<HistoryEntry> Rows,
        int                         ThinnedMatchesNotShown);

    /// <summary>
    /// <b>Everything the panel shows, read from one repository in one place</b> (R-rc10-21,
    /// R-rc10-22).
    ///
    /// <para>The window and <c>circuitrf history list</c> both come through here, so the two cannot
    /// answer differently and cannot drift apart later. Two defaults that differ silently is the defect
    /// R-rc10-22 exists to prevent, and one function is the only way to be sure of it.</para>
    ///
    /// <para><b>Anything a Pull brought in goes on top, never sorted in</b> (R-rc9-6). An incoming
    /// version is not part of this workspace's history until somebody chooses it, and time ordering
    /// would put it on top almost always and occasionally not — leaving one stranded mid-list under a
    /// mark nobody would look for there.</para>
    /// </summary>
    public static Result Read(GitCommand git, HistoryFilter filter) => ReadSources(git).Under(filter);

    /// <summary>
    /// <b>Everything the panel needs from the repository, with the filter left out of it</b> (RC-11).
    ///
    /// <para>The split exists because <b>a filter toggle is not a question about the repository</b>.
    /// Before it, every checkbox in §5.10's flyout re-read the versions, the restore points, the
    /// thinning journal, the sharing set, the corrections and the incoming versions — a dozen git
    /// subprocesses, on the UI thread, to decide which of the rows already in hand to draw. The read
    /// belongs to a boundary; <see cref="HistorySources.Under"/> is pure and belongs to the
    /// checkbox.</para>
    ///
    /// <para>Every read here takes no optional locks, so none of it waits on a writer (R-rc3-1b), and
    /// the whole thing is a fixed handful of processes whatever the length of the history.</para>
    /// </summary>
    public static HistorySources ReadSources(GitCommand git)
    {
        var points = RestorePoints.ListIncludingThinned(git);

        return new HistorySources(
            HistoryBrowser.Versions(git),
            points,
            HistoryBrowser.Incoming(git),
            // Computed ONCE and handed to every row, never asked per row (R-rc11-2).
            HistoryBrowser.Shared(git),
            VersionCorrections.Annotations(git),
            points.Count(p => p.Origin == CheckpointOrigin.WorkspaceClosed));
    }

    /// <summary>
    /// Merges the three sources into one ordered list and applies <paramref name="filter"/>.
    /// </summary>
    /// <param name="versions">The narrative, newest first, including anything a fetch brought in.</param>
    /// <param name="points">The safety net, including the entries retention thinned.</param>
    /// <param name="shared">What has left this machine — see <see cref="HistoryEntry.Shared"/>.
    /// <see cref="SharedVersions.Local"/> on a workspace with no other copy, which is most.</param>
    /// <param name="filter">What to show.</param>
    /// <param name="corrections">§5.11 case (c)'s annotations, keyed by identity (R-rc11-13). Empty is
    /// the ordinary answer.</param>
    public static Result Build(
        IReadOnlyList<HistoryVersion>        versions,
        IReadOnlyList<RestorePoint>          points,
        SharedVersions                       shared,
        HistoryFilter                        filter,
        IReadOnlyDictionary<string, VersionCorrection>? corrections = null)
    {
        List<HistoryEntry> all = [];

        foreach (var v in versions)
            all.Add(new HistoryEntry(HistoryEntryKind.Version, v, null, null, v.WhenUtc,
                                     shared.Contains(v.CommitId),
                                     corrections is not null
                                     && corrections.TryGetValue(v.CommitId, out var c) ? c : null));

        foreach (var p in points)
            all.Add(new HistoryEntry(HistoryEntryKind.RestorePoint, null, p, null, p.TakenUtc));

        // The gap rows come from the SAME point list the rows above do — there is no separate record
        // (RC-6 R-rc6-13). A gap that is still open sorts as though it ended now, which puts it at the
        // top where it belongs.
        foreach (var g in RevisionGaps.Find(points))
            all.Add(new HistoryEntry(HistoryEntryKind.Gap, null, null, g,
                                     g.ToUtc ?? DateTimeOffset.UtcNow));

        all.Sort((a, b) => b.WhenUtc.CompareTo(a.WhenUtc));

        string term = filter.Search.Trim();

        List<HistoryEntry> rows   = [];
        int                hidden = 0;

        foreach (var entry in all)
        {
            if (!Matches(entry, term)) continue;

            // The gap row survives every filter (R-rc10-11). It is not an entry somebody made, so
            // there is no checkbox it could answer to, and hiding it is how an off period becomes the
            // quiet interval §5.7 forbids.
            if (entry.IsGap) { rows.Add(entry); continue; }

            bool shown = entry.Class is not { } cls || filter.Shows(cls);
            if (entry.Thinned && !filter.Shows(HistoryEntryClass.TidiedAway)) shown = false;

            if (shown) rows.Add(entry);
            else if (entry.Thinned && term.Length > 0) hidden++;
        }

        return new Result(rows, hidden);
    }

    /// <summary>
    /// <b>One row as a headless caller reads it</b> (R-rc10-21) — the ordering number, the moment, what
    /// the entry is, and the marks that change what it means.
    ///
    /// <para><b>The verb and the panel render from the same function</b>, which is what gate 12
    /// compares byte for byte. A second formatter agreeing today is how an agent and a designer end up
    /// reading two different histories later.</para>
    ///
    /// <para>The time is ISO here and relative in the window on purpose: a person scanning a list wants
    /// <i>yesterday</i>, and a script parsing one wants a date it can sort.</para>
    /// </summary>
    public static string Line(HistoryEntry entry)
    {
        if (entry.IsGap)
            return $"{"",6}  {entry.Gap!.FromUtc.ToLocalTime():yyyy-MM-dd HH:mm}  "
                 + HistoryMessages.GapBetween(entry.Gap.FromUtc, entry.Gap.ToUtc);

        string sequence = entry.Sequence is { } n
                        ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "";

        string marks = entry.IsVersion ? " [version]" : "";
        // R-rc11-13. A corrected row shows the correction and says so WITH the original, because a
        // headless caller has no expander to open and a line that silently showed one string where the
        // repository holds another would be the one place this feature could mislead.
        if (entry.IsCorrected)              marks += $" [corrected; originally '{entry.OriginalTitle}']";
        if (entry.Thinned)                  marks += " [tidied away]";
        else if (entry.Point is { Kept: true }) marks += " [kept]";
        if (entry.Point is { IsIncomplete: true } p)
            marks += $" [incomplete: {string.Join(", ", p.LeftOut)}]";
        if (entry.Version is { OnTheOtherCopy: true })
            marks += " [on the copy this came from]";

        return $"{sequence,6}  {entry.WhenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {entry.Title}{marks}";
    }

    /// <summary>
    /// R-rc10-9. <b>Over what a person wrote</b> — the title, a batch's own stated intent, and the
    /// author. Not the origin sentence circuitRF supplies, and not a date: a designer searching for
    /// <i>match network</i> is looking for their own words, and matching circuitRF's would return
    /// every automatic entry in the list for the word "workspace".
    /// </summary>
    private static bool Matches(HistoryEntry entry, string term)
    {
        if (term.Length == 0) return true;

        // A gap carries nothing a person wrote, so it cannot match — and it stays in the list anyway,
        // for R-rc10-11's reason. Dropping it would let a search hide the fact that the stretch it is
        // searching contains no recorded history at all.
        if (entry.IsGap) return true;

        // BOTH the correction and what it corrects (R-rc11-13). A designer searching for the wording
        // they replaced must still find the entry — that is very often exactly why they are searching —
        // and one searching for the wording they replaced it WITH must find it too.
        // §5.12's note is searched too (R-rc12-8), and it is the half most worth searching: a title is
        // four words somebody chose under pressure, and the paragraph under it is where they wrote down
        // the part they would later go looking for.
        return Has(entry.Title) || Has(entry.OriginalTitle) || Has(entry.Intent) || Has(entry.Who)
            || Has(entry.Note)  || Has(entry.OriginalNote);

        bool Has(string text) => text.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
