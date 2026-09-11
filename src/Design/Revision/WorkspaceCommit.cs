using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>What one explicit commit produced.</summary>
/// <param name="Ok">Whether a version was recorded.</param>
/// <param name="Version">The entry, when one was written.</param>
/// <param name="ExcludedRepositories">Nested repositories left out (§7A.5), workspace-relative.</param>
/// <param name="Diagnostics">Everything worth reporting — the refusal, or the report naming what was
/// recorded.</param>
public sealed record CommitResult(
    bool                      Ok,
    HistoryVersion?           Version,
    IReadOnlyList<string>     ExcludedRepositories,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static CommitResult Refused(Diagnostic d) => new(false, null, [], [d]);
}

/// <summary>
/// <b>The narrative half: an ordinary commit on the ordinary line of work, created deliberately, with
/// a title the designer wrote</b> (<c>docs/design/revision-control.md</c> §5.2, §5.5, §6.3;
/// RC-7 R-rc7-1, R-rc7-5, R-rc7-16).
///
/// <para><b>This is not a checkpoint and the two must not be conflated</b> (R-rc7-9). A checkpoint is
/// dense, automatic, machine-written and parentless, on a reference outside the line of work the
/// designer browses; this is sparse, deliberate, human-written and chained, on the line itself. §5's
/// whole argument is that one history cannot serve both motives — conflating them produces a log no
/// human will read, which then makes the safety net useless too because nobody looks at it.</para>
///
/// <para><b>Nothing branches, and nothing checks anything out</b> (R-rc7-15, R-rc7-16, R-rc0-17). The
/// commit is written through a private index and the reference is moved with <c>update-ref</c>, which
/// follows the symbolic reference the workspace is already on. There is no second line of work to
/// create after a restore, because a restore is a working-tree write that never moved anything —
/// which is also the only shape that survives §6.1 on a shared line, where a second line of work
/// would be a merge nobody can perform.</para>
///
/// <para><b>Built through a temporary index, for <see cref="GitCheckpoint"/>'s reasons</b> (§4.6,
/// §5.2b): the designer's own staged state from a shell in the same folder is untouched, and the one
/// resource git's writers actually contend on is left to them. <b>Hooks never run</b> — this is
/// plumbing, <c>git commit</c> is not the command that executes, and the invocation-level empty
/// <c>core.hooksPath</c> covers every path regardless (R-rc3-7a).</para>
///
/// <para><b>Nothing to commit is not a failure.</b> It is decided by comparing the new tree against
/// the one already on the line of work — a tree comparison, never a substring match on git's
/// "nothing to commit" (R-rc3-4).</para>
/// </summary>
public static class WorkspaceCommit
{
    /// <summary>
    /// The private index this uses. Distinct from <see cref="GitCheckpoint.PrivateIndexPath"/>'s so a
    /// commit and a checkpoint in the same process cannot tread on one another, and per-process for
    /// the same reason that one is.
    /// </summary>
    public static string PrivateIndexPath(string workspaceRoot)
        => GitCheckpoint.PrivateIndexPath(workspaceRoot) + "-commit";

    /// <summary>
    /// Records the workspace as it stands, as a version the designer titled.
    /// </summary>
    /// <param name="git">Bound to the workspace root. Its <see cref="GitCommand.Identity"/> is who the
    /// commit is by — author AND committer.</param>
    /// <param name="title">What the designer typed. Blank is allowed and lists as
    /// <see cref="CommitMessage.UntitledVersion"/>; it is never a bare time.</param>
    /// <param name="extraExclusions">Workspace-relative paths the caller is leaving out — the same
    /// large-file boundary a checkpoint honours.</param>
    /// <param name="note">§5.12's longer note, or null. Written into the body of the message this
    /// version carries; a version nobody wrote one for is byte-identical to what this produced
    /// before.</param>
    public static CommitResult Commit(
        GitCommand             git,
        string?                title,
        IReadOnlyList<string>? extraExclusions = null,
        string?                note            = null)
    {
        // §4.4's first sentence, checked BEFORE the invocation rather than recognised from git's
        // English afterwards: if nobody can be named, nothing is recorded.
        git.Identity ??= RevisionIdentity.Resolve(git);
        if (git.Identity is null) return CommitResult.Refused(GitFailures.NoIdentity());

        var nested = NestedRepositories.Find(git.WorkspaceRoot);
        string index = PrivateIndexPath(git.WorkspaceRoot);

        List<Diagnostic> notes = [];
        foreach (string path in nested) notes.Add(GitFailures.NestedRepositoryExcluded(path));

        try
        {
            Discard(index);

            var options = new GitRunOptions(IndexFile: index);

            List<string> add = ["add", "--all", "--", "."];
            add.AddRange(NestedRepositories.ExcludePathspecs(nested));
            if (extraExclusions is { Count: > 0 })
                add.AddRange(extraExclusions.Select(p => $":(exclude){p}"));

            var added = git.Run(add, options);
            if (!added.Ok) return Fail(added);

            var written = git.Run(["write-tree"], options);
            if (!written.Ok || written.Line.Length == 0) return Fail(written);

            string treeId = written.Line;
            string? parent = CurrentVersionId(git);

            // "Nothing to commit", decided structurally. The line of work already holds this exact
            // state, so a second entry saying so would be an entry a designer cannot tell from the one
            // above it — and R-rc7-5's whole purpose is that they can.
            if (parent is { } previous && TreeOfVersion(git, previous) is { } previousTree
                && string.Equals(previousTree, treeId, StringComparison.Ordinal))
                return new CommitResult(false, null, nested,
                                        [.. notes, HistoryMessages.NothingChangedSinceLastVersion()]);

            var from = RestoreProvenance.Read(git.WorkspaceRoot) is { } state
                     ? new RestoredFrom(state.Label, state.TakenUtc)
                     : null;

            string message = CommitMessage.Build(title, from, note);

            List<string> commit = ["commit-tree", treeId];
            if (parent is { } p) { commit.Add("-p"); commit.Add(p); }

            var made = git.Run(commit, options with { StandardInput = message });
            if (!made.Ok || made.Line.Length == 0) return Fail(made);

            string commitId = made.Line;

            // `update-ref HEAD` follows the symbolic reference the workspace is already on and moves
            // THAT — which is why nothing here names a reference, creates one, or needs to know what
            // the current one is called. R-rc7-3's rule is not only about what a designer is shown:
            // there is genuinely nothing here for a name to be needed for.
            var moved = parent is { } expected
                      ? git.Run(["update-ref", "HEAD", commitId, expected], options)
                      : git.Run(["update-ref", "HEAD", commitId], options);
            if (!moved.Ok) return Fail(moved);

            // ── The repository's SHARED index, brought into line — and only here ──────────────────
            //
            // A checkpoint must never touch it (§4.6, §5.2b): its reference is outside HEAD, the index
            // has nothing to do with it, and leaving it alone is what keeps two circuitRF processes off
            // the one resource git's writers contend on.
            //
            // A COMMIT is the opposite case, and the reason is the escape hatch (§4.1). The index is
            // defined RELATIVE TO HEAD, so the moment HEAD names a commit and the index is still empty,
            // git's own porcelain reads every design file as staged-for-deletion AND untracked at once
            // — `git status` in the workspace lists the whole design as deleted, and `git checkout`
            // refuses to do anything because untracked files would be overwritten. That is not "an
            // ordinary git repository, readable and repairable by every existing tool"; it is a
            // repository that looks broken to the one tool the promise is about.
            //
            // `read-tree` writes the index from the tree and does not touch the working tree — nothing
            // switches, resets or checks out. Best effort: the commit is already recorded and reported,
            // and failing it over a tidy index would trade the operation for its cosmetics.
            git.Run(["read-tree", treeId]);

            // Only now: the line has been reported, so the next commit is the designer's own work
            // rather than a state brought back, and a line repeated from here on would say nothing.
            if (from is not null) RestoreProvenance.Clear(git.WorkspaceRoot);

            var version = new HistoryVersion(
                commitId, treeId, DateTimeOffset.UtcNow,
                CommitMessage.Subject(title), Explicit: true, from,
                git.Identity?.Name ?? "");

            notes.Add(HistoryMessages.VersionRecorded(version.Title, commitId));
            return new CommitResult(true, version, nested, notes);
        }
        finally { Discard(index); }

        CommitResult Fail(GitResult r) => new(
            false, null, nested,
            [.. notes, GitFailures.Translate(r, HistoryMessages.KeepingAVersion, git.WorkspaceRoot)]);
    }

    /// <summary>
    /// The version the workspace's line of work currently ends at, or null when it holds none.
    ///
    /// <para>Null is the ordinary state of a workspace that has restore points and no explicit
    /// commits — the safety net without the narrative — and it is what makes the first commit's
    /// parentless shape correct rather than a special case.</para>
    /// </summary>
    public static string? CurrentVersionId(GitCommand git)
    {
        var r = git.Run(["rev-parse", "--verify", "--quiet", "HEAD"], new GitRunOptions(ReadOnly: true));
        return r.Ok && r.Line.Length > 0 ? r.Line : null;
    }

    /// <summary>The tree one version holds, or null. A READ — it takes no optional locks, so it never
    /// waits on a writer (R-rc3-1b).</summary>
    public static string? TreeOfVersion(GitCommand git, string commitId)
    {
        var r = git.Run(["rev-parse", "--verify", "--quiet", commitId + "^{tree}"],
                        new GitRunOptions(ReadOnly: true));
        return r.Ok && r.Line.Length > 0 ? r.Line : null;
    }

    /// <summary>
    /// Whether the workspace holds anything a commit would record — what an affordance asks before
    /// offering itself, so a designer is not handed a dialog that can only refuse.
    /// </summary>
    public static bool HasSomethingToKeep(GitCommand git)
    {
        string index = PrivateIndexPath(git.WorkspaceRoot) + "-probe";
        try
        {
            Discard(index);
            var options = new GitRunOptions(IndexFile: index);

            var nested = NestedRepositories.Find(git.WorkspaceRoot);
            List<string> add = ["add", "--all", "--", "."];
            add.AddRange(NestedRepositories.ExcludePathspecs(nested));

            if (!git.Run(add, options).Ok) return false;

            var written = git.Run(["write-tree"], options);
            if (!written.Ok || written.Line.Length == 0) return false;

            if (CurrentVersionId(git) is not { } head) return true;
            return !string.Equals(TreeOfVersion(git, head), written.Line, StringComparison.Ordinal);
        }
        finally { Discard(index); }
    }

    private static void Discard(string indexPath)
    {
        try { File.Delete(indexPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
