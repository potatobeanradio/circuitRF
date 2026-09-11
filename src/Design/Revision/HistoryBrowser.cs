using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Revision;

/// <summary>
/// One version a designer kept, as the browser reads it.
/// </summary>
/// <param name="CommitId">Its identity. <b>The one identifier in this feature a designer is ever
/// shown</b> (R-rc7-4, R-rc7-7), and only because an explicit action produced it.</param>
/// <param name="TreeId">The state it holds — what a comparison and a restore both act on.</param>
/// <param name="WhenUtc">When it was kept.</param>
/// <param name="Title">The line the designer wrote.</param>
/// <param name="Explicit">Whether circuitRF wrote it as R-rc7-1's deliberate commit. False for
/// anything else that reached this line of work, which is listed anyway.</param>
/// <param name="RestoredFrom">What the tree had been restored from when this was kept, or null
/// (R-rc7-6). <b>Present on exactly the versions that need it</b>: without the line, two consecutive
/// versions where the second reverts the first read as a change of mind.</param>
/// <param name="Who">Who kept it. Shown on a clash, where "which of these two" is the whole
/// question.</param>
public sealed record HistoryVersion(
    string          CommitId,
    string          TreeId,
    DateTimeOffset  WhenUtc,
    string          Title,
    bool            Explicit,
    RestoredFrom?   RestoredFrom,
    string          Who)
{
    /// <summary>
    /// <b>This version is on the copy this workspace came from and is not here yet</b> (R-rc9-6).
    ///
    /// <para>A fetch updates the remote-tracking reference and moves nothing else, so what it brings in
    /// is on the machine, complete, and invisible to anything that reads <c>HEAD</c>. Listing those
    /// versions is what makes Pull Changes an operation with a result rather than a silent download —
    /// and marking them is what stops the list claiming they are part of this workspace's own history,
    /// which they are not until somebody chooses one.</para>
    ///
    /// <para>Not a positional member: every construction of this record predates it and means false.
    /// The same reasoning as <see cref="RestorePoint.Thinned"/>, and for the same reason.</para>
    /// </summary>
    public bool OnTheOtherCopy { get; init; }

    /// <summary>
    /// <b>The longer note the designer wrote about this version</b> (§5.12), or an empty string.
    ///
    /// <para>Empty on anything circuitRF did not write, because <see cref="MessageNotes"/> reads a
    /// COUNTED extent: an unrecognised body is somebody else's structure, and claiming it as a note
    /// would mean offering to replace it.</para>
    ///
    /// <para>Not a positional member, for <see cref="OnTheOtherCopy"/>'s reason.</para>
    /// </summary>
    public string Note { get; init; } = "";
}

/// <summary>What changed between two versions, at the granularity of documents (R-rc7-11).</summary>
public enum DocumentChangeKind { Added, Changed, Removed, Renamed }

/// <summary>One document that differs between two versions.</summary>
/// <param name="RelativePath">Where it is in the workspace.</param>
/// <param name="Kind">What happened to it.</param>
/// <param name="PreviousPath">Where it used to be, on a rename. Null otherwise.</param>
public sealed record DocumentChange(
    string             RelativePath,
    DocumentChangeKind Kind,
    string?            PreviousPath = null);

/// <summary>
/// One row of the browser: <b>either a version, or a stretch in which nothing was recorded</b>
/// (R-rc7-10).
/// </summary>
/// <param name="Version">The version, on a version row.</param>
/// <param name="Gap">The off period, on a gap row.</param>
public sealed record HistoryRow(HistoryVersion? Version, RevisionGap? Gap)
{
    public bool IsGap => Gap is not null;
}

/// <summary>
/// <b>Reading the narrative — the sparse, deliberate history a designer created on purpose</b>
/// (<c>docs/design/revision-control.md</c> §5.2, §5.7; RC-7 R-rc7-9, R-rc7-10, R-rc7-11).
///
/// <para><b>This is not the restore-point list and the two are never merged</b> (R-rc7-9). They may
/// sit side by side and must not interleave: the narrative is what a designer wrote down on purpose
/// and what gets shared; the safety net is dense, automatic and local. §5's whole argument is that
/// conflating them produces a log no human will read — which then makes the safety net useless too,
/// because nobody looks at it. So a checkpoint never appears here and a version never appears there,
/// and the two live in separate references for exactly that reason.</para>
///
/// <para><b>Everything is read in two processes, whatever the length</b>, for
/// <see cref="RestorePoints"/>' reason: one walk of the line of work, and one batched object read. A
/// process per entry is a third of a second on a list of fifty, paid on every refresh.</para>
///
/// <para><b>An off period renders as a gap, with its reason</b> (R-rc7-10). The data is RC-6's — the
/// pair of transition entries <see cref="RevisionSwitch"/> writes, which is why both carry the kept
/// mark. <b>A gap is placed among the versions by the CLOCK, and that is the one thing here that
/// is</b>: versions are ordered by the line of work itself and restore points by RC-5's sequence, and
/// the two orderings have no common term but the timestamp. Under a clock fault a gap can therefore
/// float to the wrong place in the list — visibly, since it still carries its own dates, which is the
/// failure worth having.</para>
/// </summary>
public static class HistoryBrowser
{
    /// <summary>
    /// The versions on this workspace's line of work, <b>newest first</b>. An empty list is the
    /// ordinary state of a workspace nobody has kept a version of yet, and never an error.
    /// </summary>
    /// <param name="limit">At most this many, or 0 for all of them.</param>
    public static IReadOnlyList<HistoryVersion> Versions(GitCommand git, int limit = 0)
        => Walk(git, ["HEAD"], limit, onTheOtherCopy: false);

    /// <summary>
    /// <b>The versions a Pull brought in that are not here yet</b> (R-rc9-6), newest first, or an empty
    /// list — which is the ordinary answer for a workspace with no other copy, and for one that is
    /// already level with it.
    ///
    /// <para><c>--not HEAD</c> is the whole of it: what is wanted is the versions on the other copy and
    /// not on this one. Listing the remote reference outright would repeat every version the two copies
    /// share, which is most of them, each appearing twice in a list whose value is that it is short.</para>
    ///
    /// <para><b>Nothing here reaches a network</b> (R-rc9-6). It reads what a fetch already put on this
    /// machine, so the panel answers the same before and after an alt-tab and a Pull is the only thing
    /// that changes it.</para>
    /// </summary>
    public static IReadOnlyList<HistoryVersion> Incoming(GitCommand git, int limit = 0)
        => WorkspaceRemotes.IncomingRef(git) is { } reference
            ? Walk(git, [reference, "--not", "HEAD"], limit, onTheOtherCopy: true)
            : [];

    /// <summary>
    /// <b>Which versions have left this machine</b> (RC-10 R-rc10-14) — the identities reachable from
    /// the copy this workspace exchanges with.
    ///
    /// <para>The expander says <i>local-only</i> or <i>shared</i>, and the distinction is the one
    /// §5.11 turns on: a title nobody else has seen is the author's to correct, and one that has left
    /// the machine is not.</para>
    ///
    /// <para><b>RC-11 moved the computation into <see cref="VersionSharing"/> and left this here as
    /// the one caller's spelling of it</b> (R-rc11-2). The predicate the whole of §5.11 turns on is a
    /// named function with its own tests rather than a condition three call sites each decide for
    /// themselves — and one of the three states it distinguishes is invisible from a set alone: a
    /// remote configured and never fetched answers <b>shared</b>, not <i>nothing is shared</i>, which
    /// is what a bare empty set here used to say.</para>
    /// </summary>
    public static SharedVersions Shared(GitCommand git) => VersionSharing.Compute(git);

    private static IReadOnlyList<HistoryVersion> Walk(
        GitCommand git, IReadOnlyList<string> revisions, int limit, bool onTheOtherCopy)
    {
        List<string> walk = ["rev-list"];
        if (limit > 0) { walk.Add("--max-count=" + limit.ToString(CultureInfo.InvariantCulture)); }
        walk.AddRange(revisions);

        var listed = git.Run(walk, new GitRunOptions(ReadOnly: true));
        if (!listed.Ok) return [];

        var ids = listed.StdOut.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => s.Length > 0)
                                .ToList();
        if (ids.Count == 0) return [];

        var bodies = ReadObjects(git, ids);

        List<HistoryVersion> versions = [];
        foreach (string id in ids)
        {
            if (!bodies.TryGetValue(id, out string? raw)) continue;

            var (treeId, who, when, message) = SplitCommitObject(raw);
            var meta = CommitMessage.Read(message);

            versions.Add(new HistoryVersion(
                id, treeId, when, meta.Title, meta.Explicit, meta.RestoredFrom, who)
            {
                OnTheOtherCopy = onTheOtherCopy,
                Note           = meta.Note,
            });
        }

        return versions;
    }

    /// <summary>
    /// The browser's rows: the versions, with each off period placed among them as a gap
    /// (R-rc7-10). Newest first, which is how the list reads.
    /// </summary>
    /// <param name="restorePoints">RC-6's list, including thinned entries — the gaps are derived from
    /// the transition entries in it. Pass an empty list to get versions alone.</param>
    public static IReadOnlyList<HistoryRow> Rows(
        IReadOnlyList<HistoryVersion> versions,
        IReadOnlyList<RestorePoint>   restorePoints)
    {
        var gaps = RevisionGaps.Find(restorePoints);
        if (gaps.Count == 0) return [.. versions.Select(v => new HistoryRow(v, null))];

        // Newest first for both, then merged on the one term they share. A gap with no end is still
        // open, so it sorts as though it ended now — which puts it at the top, where it belongs.
        var rows = new List<(DateTimeOffset Key, HistoryRow Row)>();

        foreach (var v in versions) rows.Add((v.WhenUtc, new HistoryRow(v, null)));
        foreach (var g in gaps)     rows.Add((g.ToUtc ?? DateTimeOffset.UtcNow, new HistoryRow(null, g)));

        rows.Sort((a, b) => b.Key.CompareTo(a.Key));
        return [.. rows.Select(r => r.Row)];
    }

    /// <summary>
    /// <b>What changed between two versions, at the granularity of documents</b> (R-rc7-11) — which is
    /// the design layer's question, not the text layer's.
    ///
    /// <para><b>Naming a changed document is this brief's scope; comparing what is inside one is
    /// not.</b> Where a per-document comparison would be genuinely useful — a schematic's
    /// connectivity, a technology's layer table — that is a legitimate follow-up and needs a reader
    /// per document type, which is a different piece of work from this one.</para>
    ///
    /// <para>Read with <c>-z</c>, so a path containing a quote or a non-ASCII character is not
    /// re-encoded on the way out and then mis-split on the way in.</para>
    /// </summary>
    public static IReadOnlyList<DocumentChange> Compare(GitCommand git, string fromTreeOrCommit,
                                                       string toTreeOrCommit)
        => TryCompare(git, fromTreeOrCommit, toTreeOrCommit) ?? [];

    /// <summary>
    /// <see cref="Compare"/>, with <b>"the diff could not be produced" told apart from "nothing
    /// differs"</b> — one list and one parser, because a second one would drift.
    ///
    /// <para>The distinction is load-bearing for <see cref="WorkspaceRestore"/>, which uses the answer
    /// to decide which files to write: an empty list means write nothing, and a failure means write
    /// everything. Reading a failure as an empty list there would leave the workspace untouched and
    /// report a successful restore.</para>
    /// </summary>
    /// <param name="findRenames">
    /// Whether a delete-plus-add pair is reported as one <see cref="DocumentChangeKind.Renamed"/> row.
    /// True for the browser, which is naming documents to a reader. <b>False for a caller acting on the
    /// paths</b>: a rename IS a removal and a write, and collapsing the pair hides one of the two paths
    /// that has to be touched.
    /// </param>
    public static IReadOnlyList<DocumentChange>? TryCompare(GitCommand git, string fromTreeOrCommit,
                                                            string toTreeOrCommit,
                                                            bool findRenames = true)
    {
        List<string> arguments = ["diff-tree", "-r", "-z", "--name-status"];
        if (findRenames) arguments.Add("--find-renames");
        arguments.Add(fromTreeOrCommit);
        arguments.Add(toTreeOrCommit);

        var r = git.Run(arguments, new GitRunOptions(ReadOnly: true));
        if (!r.Ok) return null;

        var fields = r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        List<DocumentChange> changes = [];
        for (int i = 0; i < fields.Length; i++)
        {
            string status = fields[i].Trim();
            if (status.Length == 0) continue;

            // A rename is THREE fields — status, old path, new path — and reading it as two is what
            // silently shifts every remaining pair by one, turning the rest of the list into
            // nonsense that still looks like a list.
            if (status.StartsWith('R') && i + 2 < fields.Length)
            {
                changes.Add(new DocumentChange(fields[i + 2], DocumentChangeKind.Renamed, fields[i + 1]));
                i += 2;
                continue;
            }

            if (i + 1 >= fields.Length) break;
            string path = fields[++i];

            changes.Add(new DocumentChange(path, status[0] switch
            {
                'A' => DocumentChangeKind.Added,
                'D' => DocumentChangeKind.Removed,
                _   => DocumentChangeKind.Changed,
            }));
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return changes;
    }

    /// <summary>
    /// <b>What one entry holds that the workspace does not, and the other way round</b> (RC-10
    /// R-rc10-17, R-rc7-11) — the comparison the right-click menu offers, at the granularity of
    /// documents like every other one here.
    ///
    /// <para><b>It writes nothing at all</b>, which is the constraint that decided the shape.
    /// <see cref="GitCheckpoint"/>'s route to a tree of the current state is <c>add --all</c> followed
    /// by <c>write-tree</c>, and that writes a blob for every changed file — permanently, since
    /// §4.5's <c>gc.pruneExpire = never</c> forbids any pack from removing an unreachable object. A
    /// comparison a designer opens out of curiosity would then grow the repository, which is the one
    /// thing R-rc10-4 says this brief may not do.</para>
    ///
    /// <para>So the entry's tree is read into a PRIVATE index — no shared index is ever touched
    /// (§5.2b) — and <c>status</c> reports the difference against the working files. That reads the
    /// files and hashes them in memory; nothing reaches the object store. It also gets
    /// <c>.gitignore</c> right for free, so results and generated artwork are absent from the answer
    /// exactly as they are absent from the entry.</para>
    /// </summary>
    public static IReadOnlyList<DocumentChange> CompareWithWorkspace(GitCommand git, string treeOrCommit)
    {
        string index = GitCheckpoint.PrivateIndexPath(git.WorkspaceRoot) + "-compare";

        try
        {
            try { File.Delete(index); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            var options = new GitRunOptions(IndexFile: index);

            var loaded = git.Run(["read-tree", treeOrCommit], options);
            if (!loaded.Ok) return [];

            var listed = git.Run(
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames"],
                options with { ReadOnly = true });
            if (!listed.Ok) return [];

            List<DocumentChange> changes = [];
            foreach (string field in listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (field.Length < 4) continue;

                string status = field[..2];
                string path   = field[3..].Trim();
                if (path.Length == 0) continue;

                // The workspace's side is what the row is ABOUT, so the wording is from the entry's
                // point of view: a file present now and not in the entry was "added" since it.
                changes.Add(new DocumentChange(path, status switch
                {
                    "??"                       => DocumentChangeKind.Added,
                    [_, 'D'] or ['D', _]       => DocumentChangeKind.Removed,
                    [_, 'A'] or ['A', _]       => DocumentChangeKind.Added,
                    _                          => DocumentChangeKind.Changed,
                }));
            }

            changes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
            return changes;
        }
        finally
        {
            try { File.Delete(index); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// One version as a <see cref="RestorePoint"/>, so <b>going back to it uses RC-5's restore and no
    /// second implementation exists</b> (R-rc7-17).
    ///
    /// <para>The sequence is deliberately negative and derived from the clock: it is only ever used to
    /// order a list this entry never joins, and a positive one could collide with a real restore
    /// point's — which is a reference name, and colliding there would overwrite one.</para>
    /// </summary>
    public static RestorePoint AsRestorePoint(HistoryVersion version) => new(
        Reference: "",
        CommitId:  version.CommitId,
        TreeId:    version.TreeId,
        Sequence:  -version.WhenUtc.ToUnixTimeSeconds(),
        TakenUtc:  version.WhenUtc,
        Origin:    CheckpointOrigin.SavePoint,
        Label:     version.Title,
        Intent:    version.Title,
        Kept:      true,
        LeftOut:   []);

    // ── Reading objects in one process ────────────────────────────────────────────────────────────

    /// <summary>
    /// The raw bodies of a set of objects, keyed by identity. <c>cat-file --batch</c> is length-framed;
    /// this parses on the header lines for <see cref="RestorePoints"/>' stated reason — .NET has
    /// already decoded the stream, so the announced BYTE size and the CHAR buffer disagree the moment
    /// a message holds anything non-ASCII.
    /// </summary>
    private static Dictionary<string, string> ReadObjects(GitCommand git, IEnumerable<string> ids)
    {
        Dictionary<string, string> bodies = new(StringComparer.Ordinal);

        string request = string.Join('\n', ids) + "\n";
        var r = git.Run(["cat-file", "--batch"], new GitRunOptions(StandardInput: request, ReadOnly: true));
        if (!r.Ok) return bodies;

        string   text  = r.StdOut.Replace("\r\n", "\n");
        string[] lines = text.Split('\n');

        string? currentId   = null;
        var     currentBody = new StringBuilder();

        foreach (string line in lines)
        {
            if (IsObjectHeader(line, out string? id))
            {
                if (currentId is not null) bodies[currentId] = currentBody.ToString();
                currentId = id;
                currentBody.Clear();
                continue;
            }

            if (currentId is not null) currentBody.Append(line).Append('\n');
        }

        if (currentId is not null) bodies[currentId] = currentBody.ToString();
        return bodies;
    }

    private static bool IsObjectHeader(string line, out string? id)
    {
        id = null;
        string[] parts = line.Split(' ');
        if (parts.Length != 3 || parts[1] != "commit") return false;
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        if (parts[0].Length < 7 || !parts[0].All(Uri.IsHexDigit)) return false;

        id = parts[0];
        return true;
    }

    /// <summary>A raw commit object's tree, its committer's name, its committer time and its
    /// message.</summary>
    private static (string TreeId, string Who, DateTimeOffset WhenUtc, string Message)
        SplitCommitObject(string raw)
    {
        string tree = "";
        string who  = "";
        var    when = DateTimeOffset.UnixEpoch;
        int    i    = 0;

        string[] lines = raw.Split('\n');
        for (; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) { i++; break; }

            if (line.StartsWith("tree ", StringComparison.Ordinal))
                tree = line[5..].Trim();
            else if (line.StartsWith("committer ", StringComparison.Ordinal))
            {
                if (ParseTime(line) is { } t) when = t;
                who = ParseName(line[10..]);
            }
        }

        return (tree, who, when, string.Join('\n', lines.Skip(i)));
    }

    /// <summary>The epoch seconds out of a <c>committer Name &lt;email&gt; 1700000000 +0000</c>
    /// line.</summary>
    private static DateTimeOffset? ParseTime(string line)
    {
        string[] parts = line.TrimEnd().Split(' ');
        if (parts.Length < 2) return null;
        return long.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>The name out of the same line — everything before the address, which is where a name
    /// with spaces in it ends.</summary>
    private static string ParseName(string identity)
    {
        int bracket = identity.IndexOf('<');
        return bracket > 0 ? identity[..bracket].Trim() : identity.Trim();
    }
}
