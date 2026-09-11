using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>What one boundary produced.</summary>
/// <param name="Recorded">False when the tree already matched (R-rc5-5a) or nothing could be written.</param>
/// <param name="Point">The entry, when one was written.</param>
/// <param name="LeftOut">Files left out at an unattended boundary (R-rc5-15a).</param>
/// <param name="Diagnostics">Everything worth reporting. <b>Success posts nothing</b> (R-rc5-11); the
/// only Info here is "nothing had changed", which a caller that asked is entitled to see and an
/// automatic caller drops.</param>
/// <param name="TreeId">The state this boundary saw — <b>present even when nothing was written</b>,
/// because the tree test (R-rc5-5a) succeeding is the case where a caller most needs to know what the
/// workspace currently holds. A restore uses it to decide what may safely be taken away.</param>
public sealed record CheckpointOutcome(
    bool                      Recorded,
    RestorePoint?             Point,
    IReadOnlyList<string>     LeftOut,
    IReadOnlyList<Diagnostic> Diagnostics,
    string?                   TreeId = null)
{
    public static CheckpointOutcome Failed(Diagnostic d) => new(false, null, [], [d]);
}

/// <summary>
/// <b>A boundary, taken</b> — the layer between <see cref="GitCheckpoint"/>'s commit primitive and
/// everything that decides WHEN one happens (<c>docs/design/revision-control.md</c> §5.3, §5.5,
/// §8.2b; R-rc5-4 … R-rc5-9a, R-rc5-15a).
///
/// <para><b>Three boundaries and no others</b> (R-rc5-4): before an agent's batch, an explicit
/// save-point, and workspace close. <b>Not on every save</b>, and the reason is not cost — §2 measured
/// a recording per save at single-digit KB and it is affordable. It is wrong because a save is not a
/// boundary: one logical design action writes a layout, the workspace file and a schematic, so a
/// recording per file save keeps fragments of an edit, some of which are not internally consistent.
/// And because a history nobody can read is not a history.</para>
///
/// <para><b>A simulation run and an idle timeout are excluded, and the reasons are recorded here so
/// they are not revisited casually</b> (R-rc5-5). A run is attractive as a "this is the state I
/// measured" marker, but runs are frequent, often unchanged from the last one, a parameter sweep would
/// generate dozens of near-identical entries, and it silently couples the history to the analysis
/// engine — which the staging is specifically arranged to avoid. An idle timeout produces an entry the
/// user cannot predict, at a moment meaningful to nobody, labelled with a time rather than an intent;
/// and "idle" during a long simulation is not idle at all.</para>
///
/// <para><b>Nothing in this type knows what a window is.</b> §1.2's agent is out of process, so every
/// boundary has to be reachable with no display — which is why the git type went below the firewall in
/// the first place.</para>
/// </summary>
public static class WorkspaceCheckpoints
{
    /// <summary>
    /// Records the workspace at a boundary.
    /// </summary>
    /// <param name="attended">
    /// <b>Whether there is anybody to answer a question</b> (R-rc5-15a, §8.2b). A close is at the
    /// moment the designer asked to leave and a batch is headless — at both, the recording proceeds
    /// and an unexpectedly large new file is LEFT OUT, because including is irreversible and leaving
    /// out is not. An attended boundary asks first, and the caller passes the answers as
    /// <paramref name="exclusions"/>.
    /// </param>
    /// <param name="exclusions">Workspace-relative paths the caller has already decided to leave out.</param>
    /// <param name="kept">Whether retention may never thin this one. A save-point and the two
    /// recording transitions are always kept (R-rc5-1f, R-rc6-5a) whatever the caller says.</param>
    /// <param name="forceRecord">
    /// <b>Record even when the tree is unchanged</b> — for the two recording transitions only
    /// (R-rc6-14a). Everywhere else the tree test is exactly right: a boundary whose state is already
    /// in the history adds nothing. A transition is not a fact about CONTENT, though; it is the fact
    /// that recording stopped or started, and suppressing it would leave the gap with one end, which
    /// is what makes it render as a quiet interval rather than as a gap.
    /// </param>
    /// <param name="note">§5.12's longer note, or null — what the label has no room for. Only the two
    /// attended boundaries ever have one: nobody is at a close or a batch to write a paragraph.</param>
    public static CheckpointOutcome Take(
        GitCommand             git,
        CheckpointOrigin       origin,
        string?                label,
        bool                   attended    = true,
        IReadOnlyList<string>? exclusions  = null,
        bool                   kept        = false,
        bool                   forceRecord   = false,
        IReadOnlySet<string>?  alsoHeldTrees = null,
        string?                note          = null)
    {
        var newest       = RestorePoints.Newest(git);
        string? previous = forceRecord ? null : newest?.TreeId;

        List<string> leaveOut   = [.. exclusions ?? []];
        List<Diagnostic> notes  = [];

        // §8.2b. Only at a boundary nobody is at: an attended one has already asked, and asking twice
        // would leave a file out that the designer just said to include.
        if (!attended)
        {
            var large = LargeFileGuard.Find(git, previous);
            if (large.Count > 0)
            {
                leaveOut.AddRange(LargeFileGuard.ExclusionsFor(large).Except(leaveOut, StringComparer.Ordinal));
                notes.Add(RestorePointMessages.LeftOutUnattended(
                    string.Join(", ", large.Select(f => f.RelativePath)), large.Count));
            }
        }

        bool   alwaysKept = IsAlwaysKept(origin);
        long   sequence   = CheckpointReferences.NextSequence(git);
        string reference  = CheckpointReferences.NameFor(sequence);
        string message    = CheckpointMessage.Build(
            origin, label, sequence,
            kept: kept || alwaysKept,
            leftOut: leaveOut,
            note: note);

        var result = GitCheckpoint.Record(git, reference, message, previous, leaveOut, alsoHeldTrees);

        foreach (string nested in result.ExcludedRepositories)
            notes.Add(GitFailures.NestedRepositoryExcluded(nested));

        if (result.Diagnostic is { Severity: DiagnosticSeverity.Error } failure)
            return new CheckpointOutcome(false, null, leaveOut, [.. notes, failure], result.TreeId);

        if (!result.Recorded)
            return new CheckpointOutcome(false, null, leaveOut,
                                         [.. notes, result.Diagnostic ?? GitFailures.NothingToRecord()],
                                         result.TreeId);

        var point = new RestorePoint(
            reference, result.CommitId!, result.TreeId!, sequence, DateTimeOffset.UtcNow,
            origin, CheckpointMessage.SubjectFor(origin, label), label?.Trim(),
            kept || alwaysKept, leaveOut) { Note = MessageNotes.Text(note) };

        return new CheckpointOutcome(true, point, leaveOut, notes, result.TreeId);
    }

    /// <summary>
    /// <b>What a boundary is called when it could not be taken</b> (R-rc5-10) — the operation named in
    /// circuitRF's words, for the message that always reports a failure.
    /// </summary>
    public static string Describe(CheckpointOrigin origin) => origin switch
    {
        CheckpointOrigin.SavePoint       => "the save-point you asked for",
        CheckpointOrigin.WorkspaceClosed => "this workspace as it was when you closed it",
        CheckpointOrigin.BeforeBatch     => "this workspace as it was before an assistant changed it",
        CheckpointOrigin.BeforeRestore   => "this workspace as it was before going back",
        CheckpointOrigin.RecordingOff    => "this workspace as it was when you turned recording off",
        CheckpointOrigin.RecordingOn     => "this workspace as it was when you turned recording back on",
        _                                => "this workspace",
    };

    /// <summary>
    /// The four origins retention may never thin, whatever the caller passes (R-rc5-1f, R-rc6-5a,
    /// <b>R-rc10-20</b>).
    ///
    /// <para>A save-point, because the user's judgement about what matters beats any heuristic and
    /// thinning it would discard exactly that judgement. And the pair that brackets an off period,
    /// because they are what gives the gap its ends — a pair retention could thin is a gap retention
    /// could erase.</para>
    ///
    /// <para><b>And the entry taken before a restore</b> (§5.6 rule 6's fourth kind, §12 Q34, added
    /// 2026-09-07). It is what makes <i>going back is never a one-way door</i> true, and the list
    /// omitted it — so the one entry whose entire purpose is to undo a destructive operation aged out
    /// of the list on its own while the designer kept working. That is not data loss (§4.5 keeps the
    /// objects and RC-6's journal brings the entry back); it is the promise leaving the place the
    /// promise was made. Restores are rare, so keeping them costs nothing measurable.</para>
    ///
    /// <para><b>This is RC-10's one storage-visible change, and it is a keep rather than a delete.</b>
    /// Everything else in that brief is presentation (§5.10 rule 6).</para>
    /// </summary>
    public static bool IsAlwaysKept(CheckpointOrigin origin)
        => origin is CheckpointOrigin.SavePoint
                  or CheckpointOrigin.BeforeRestore
                  or CheckpointOrigin.RecordingOff
                  or CheckpointOrigin.RecordingOn;
}
