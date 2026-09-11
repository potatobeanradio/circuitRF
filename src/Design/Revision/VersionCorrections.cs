using System.Globalization;
using System.Text;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>
/// <b>What §5.11 case (c)'s annotation holds</b> — a corrected title and, since §5.12, a corrected
/// note (RC-12 R-rc12-6).
///
/// <para><b>It is shaped like a message because it stands in for one.</b> The first line is the title
/// the row shows in place of the original; anything after it is the longer note, shown where the
/// original's would have been. A single-line annotation — every one written before notes existed — is
/// a title and no note, which is what it always meant.</para>
/// </summary>
/// <param name="Title">The corrected line. Never empty on an annotation that exists.</param>
/// <param name="Note">The corrected note, or an empty string.</param>
public sealed record VersionCorrection(string Title, string Note = "")
{
    /// <summary>Reads one back out of the object git handed over.</summary>
    public static VersionCorrection Of(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string title = lines.Length > 0 ? lines[0].Trim() : "";
        string note  = lines.Length > 1 ? string.Join('\n', lines[1..]).Trim() : "";
        return new VersionCorrection(title, note);
    }

    /// <summary>What gets written. Empty when there is nothing to correct, which REMOVES the
    /// annotation and puts the original in front again.</summary>
    public string Text
    {
        get
        {
            string title = Title.ReplaceLineEndings(" ").Trim();
            string note  = MessageNotes.Text(Note);

            if (title.Length == 0 && note.Length == 0) return "";
            if (note.Length == 0)                      return title;
            return title + "\n\n" + note;
        }
    }

    /// <summary>Whether this annotation says anything at all.</summary>
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>What one correction did, or why it was refused.</summary>
/// <param name="Ok">Whether anything changed.</param>
/// <param name="Version">The entry as it now reads, when a title was corrected.</param>
/// <param name="Diagnostic">The refusal, or the report. Never null.</param>
public sealed record CorrectionOutcome(bool Ok, HistoryVersion? Version, Diagnostic Diagnostic)
{
    public static CorrectionOutcome Refused(Diagnostic d) => new(false, null, d);
}

/// <summary>
/// <b>Correcting what a person wrote about a version, and annotating one they can no longer
/// correct</b> (<c>docs/design/revision-control.md</c> §5.11 cases (b) and (c); RC-11 R-rc11-7 …
/// R-rc11-14).
///
/// <para><b>The line everything here follows from</b> (R-rc11-1): what was recorded is never altered;
/// what a person wrote <i>about</i> it may be corrected by that person, until it has been shared —
/// after which it can only be annotated. Content, trees, times and the sequence are immutable with no
/// exception anywhere; a title is commentary, and commentary is correctable by its author. The line is
/// drawn at sharing because sharing is the only event that creates a second reader, and the second
/// reader is the entire reason git's own rule exists.</para>
///
/// <para><b>Only the SUBJECT LINE is replaced, and the rest of the message is carried through
/// verbatim</b> (R-rc11-4's rule, applied to a version). Rebuilding the message from
/// <see cref="CommitMessage.Build"/> would be correct for a commit circuitRF wrote and a lie about
/// anything else that reached this line of work — somebody's own <c>git commit</c>, an import, a
/// script — because it would add circuitRF's own "you asked for this" line and its origin trailer to
/// a commit that never carried them. Replacing one line preserves every trailer, including
/// R-rc7-6's restored-from pair, without this file having to know which of them exist.</para>
///
/// <para><b>A correction does not restamp the version</b> (R-rc11-10). The author, the committer, both
/// times and the tree are the ones already recorded, re-supplied to <c>commit-tree</c> through git's
/// own date and identity variables. A title correction that quietly moved a version's date would make
/// §5.6 rule 2's ordering argument false in the one list a designer reads it from — and it is the
/// default behaviour of the obvious alternative, which is why the obvious alternative is not used
/// here.</para>
///
/// <para><b><c>--amend</c> is deliberately not what runs</b> (R-rc11-9, §12 Q33). The brief permits it
/// in exactly one file; what it turned out to buy was nothing, because <c>git commit --amend</c> needs
/// the shared index and restamps the committer date, and both of those are the two things this
/// operation must not do. <c>commit-tree</c> over the recorded tree with the recorded identity is
/// strictly narrower: no index is touched, no hook can run, and nothing but the one line moves. The
/// gate that holds §8.3 shut therefore still forbids <c>--amend</c> everywhere, and this file is the
/// one place permitted to carry it — a permission it does not spend.</para>
///
/// <para><b>§5.2a's checkpoint references are independent of the line of work</b> (R-rc11-11), so a
/// corrected version leaves every restore point exactly where it was. That is asserted rather than
/// reasoned about: the failure is invisible until somebody restores.</para>
/// </summary>
public static class VersionCorrections
{
    // ── Case (b): the newest unshared version's title (R-rc11-7) ──────────────────────────────────

    /// <summary>
    /// Corrects the title of a version that has not been shared.
    ///
    /// <para><b>The newest unshared version is the requirement and the tail is a follow-up</b>
    /// (R-rc11-8). An older one is a rewrite of every version after it — safe for the same reason, and
    /// materially more work over a workspace with thousands of files. Nearly every bad title is
    /// noticed within a minute of being typed, which is the case this buys; anything else is offered
    /// <see cref="Annotate"/> by name, which is available for any version at all.</para>
    /// </summary>
    /// <param name="version">The entry as the panel read it.</param>
    /// <param name="title">What it should say. Blank is a refusal — a correction to nothing is not a
    /// correction, and the entry would list as <see cref="CommitMessage.UntitledVersion"/>, which is
    /// not what anybody opening this dialog meant.</param>
    /// <param name="note">
    /// §5.12's longer note (R-rc12-5). <b>Null leaves the one already there alone</b> — what every
    /// caller that predates notes means — and a value replaces it, an empty one removing it. Written
    /// into the message that is being rewritten anyway, so correcting both costs exactly what
    /// correcting one costs.
    /// </param>
    public static CorrectionOutcome CorrectTitle(GitCommand git, HistoryVersion version, string? title,
                                                 string? note = null)
    {
        string wanted = OneLine(title);
        if (wanted.Length == 0)
            return CorrectionOutcome.Refused(HistoryMessages.ACorrectionNeedsWords());

        // R-rc11-2. Computed, never assumed — and the one state where it cannot be computed answers
        // "shared", which is what sends the designer to the annotation instead.
        var shared = VersionSharing.Compute(git);
        if (shared.Contains(version.CommitId))
            return CorrectionOutcome.Refused(HistoryMessages.TitleHasBeenShared(version.Title));

        // R-rc11-8. The tail is not built, so the refusal NAMES why and offers case (c) — which is the
        // sentence most designers who meet this feature will actually read.
        string? head = WorkspaceCommit.CurrentVersionId(git);
        if (head is null || !string.Equals(head, version.CommitId, StringComparison.Ordinal))
            return CorrectionOutcome.Refused(HistoryMessages.OnlyTheNewestTitleCorrects(version.Title));

        if (ReadCommit(git, version.CommitId) is not { } raw)
            return CorrectionOutcome.Refused(HistoryMessages.EntryCouldNotBeRead());

        var original = Parse(raw);
        string message = ReplaceSubject(original.Message, wanted);

        // R-rc11-4's rule, applied to the note: ONE thing moves and the rest of the message — the
        // explanation line, every trailer, R-rc7-6's restored-from pair — is carried through verbatim.
        if (note is not null) message = MessageNotes.Replace(message, note);

        List<string> write = ["commit-tree", original.TreeId];
        foreach (string parent in original.Parents) { write.Add("-p"); write.Add(parent); }

        var written = git.Run(write, new GitRunOptions(
            StandardInput: message,
            Environment:   RecordedIdentity(original)));

        if (!written.Ok || written.Line.Length == 0)
            return CorrectionOutcome.Refused(
                GitFailures.Translate(written, HistoryMessages.CorrectingATitle, git.WorkspaceRoot));

        // Named old value: another process that moved the line of work between the read and the write
        // is left alone by git rather than overwritten.
        var moved = git.Run(["update-ref", "HEAD", written.Line, version.CommitId]);
        if (!moved.Ok)
            return CorrectionOutcome.Refused(
                GitFailures.Translate(moved, HistoryMessages.CorrectingATitle, git.WorkspaceRoot));

        // R-rc11-7 is "the old wording is gone", and it is not gone while a COPY of it is sitting in
        // the pending restore provenance waiting to be written into the next commit (owner,
        // 2026-09-07). This is case (b) — the version was never shared — so there is nothing on
        // anybody else's disk to stay in step with, and the deleted wording must not reappear.
        RestoreProvenance.Retitle(git.WorkspaceRoot, version.CommitId, written.Line, wanted);

        var corrected = version with { CommitId = written.Line, Title = wanted };
        if (note is not null) corrected = corrected with { Note = MessageNotes.Text(note) };
        return new CorrectionOutcome(true, corrected, HistoryMessages.TitleCorrected(wanted));
    }

    /// <summary>
    /// Whether <see cref="CorrectTitle"/> would do anything for this entry — <b>what an affordance
    /// asks before offering itself</b>, so a designer is not handed a dialog that can only refuse.
    /// </summary>
    /// <param name="shared">The one computed answer (R-rc11-2), passed in rather than recomputed:
    /// a menu asking per row would start a process per row.</param>
    public static bool CanCorrectTitle(GitCommand git, HistoryVersion version, SharedVersions shared)
        => !version.OnTheOtherCopy
        && !shared.Contains(version.CommitId)
        && string.Equals(WorkspaceCommit.CurrentVersionId(git), version.CommitId, StringComparison.Ordinal);

    // ── Case (c): the annotation (R-rc11-12, R-rc11-13) ───────────────────────────────────────────

    /// <summary>
    /// <b>circuitRF's own notes reference</b> — not git's default <c>refs/notes/commits</c>.
    ///
    /// <para>The same rule the checkpoint namespace follows (§5.2a): circuitRF writes in a namespace of
    /// its own and never treads on state the designer keeps for their own purposes. A designer with
    /// their own notes on the default reference would otherwise find circuitRF replacing them, silently
    /// and with no way back that is not <c>git fsck</c>.</para>
    ///
    /// <para>The escape hatch of §4.1 still reads it —
    /// <c>git log --notes=circuitrf</c> — and that spelling is in the user chapter rather than
    /// inferred.</para>
    /// </summary>
    public const string NotesRef = "refs/notes/circuitrf";

    /// <summary>
    /// The refspec that carries the annotations with the versions they annotate (R-rc11-13, §9).
    /// <b>Named in one place</b>, because a fetch and a send that spelled it differently would produce
    /// a correction the sender believes they made and the recipient never sees.
    /// </summary>
    public const string NotesRefspec = "+" + NotesRef + ":" + NotesRef;

    /// <summary>
    /// <b>A correction that leaves the original in place</b> (R-rc11-13) — an annotation git attaches
    /// to a commit without altering it, shown by the panel in place of the original with the original
    /// one click away.
    ///
    /// <para><b>It is written and re-written freely.</b> It is the one mutable thing in this whole
    /// document, and that is exactly what makes it the right home for commentary: nothing depends on
    /// its identity, no clone is invalidated by it, and a designer who gets the correction wrong can
    /// correct the correction.</para>
    ///
    /// <para><b>It is offered for any version at all</b>, shared or not. Case (b) is the better answer
    /// where it applies; this one always applies, which is why the refusals above can name it.</para>
    ///
    /// <para>An empty correction REMOVES the annotation, which is how a designer takes back a
    /// correction they did not mean — and is not a way to erase anything, because the original was
    /// never altered.</para>
    /// </summary>
    /// <param name="note">§5.12's longer note, corrected alongside the title and carried in the same
    /// object (R-rc12-6). It travels with the version exactly as the corrected title does.</param>
    public static CorrectionOutcome Annotate(GitCommand git, HistoryVersion version, string? correction,
                                             string? note = null)
    {
        git.Identity ??= RevisionIdentity.Resolve(git);
        if (git.Identity is null) return CorrectionOutcome.Refused(GitFailures.NoIdentity());

        // A correction to the NOTE ALONE is a real request — `history correct --note …` with no
        // --text — and it must not produce an annotation whose first line is blank, because that first
        // line is what stands in for the title wherever this version is listed. The title it already
        // has is what it should go on saying.
        string title = (correction ?? "").Trim();
        if (title.Length == 0 && MessageNotes.Text(note).Length > 0) title = version.Title;

        string text = new VersionCorrection(title, note ?? "").Text;

        if (text.Length == 0)
        {
            var removed = git.Run(["notes", "--ref", NotesRef, "remove", "--ignore-missing",
                                   "--", version.CommitId]);
            return removed.Ok
                ? new CorrectionOutcome(true, version, HistoryMessages.CorrectionRemoved(version.Title))
                : CorrectionOutcome.Refused(GitFailures.Translate(
                      removed, HistoryMessages.CorrectingATitle, git.WorkspaceRoot));
        }

        // Through stdin, never a command line: a correction is free text a designer typed, and a
        // message assembled into an argument list is the one place a quote character changes meaning.
        var added = git.Run(["notes", "--ref", NotesRef, "add", "--force", "-F", "-",
                             "--", version.CommitId],
                            new GitRunOptions(StandardInput: text));

        // Case (c). §8.3 keeps the original in the HISTORY, because it is on somebody else's disk and
        // pretending otherwise would be a lie — but a commit not yet written is not history, and the
        // row already shows the correction in place of the original (R-rc11-13). So what the next
        // commit says it came back from is the correction too, rather than a sentence contradicting
        // the row directly above it. The identity is unchanged: a note leaves the commit alone.
        if (added.Ok)
            RestoreProvenance.Retitle(git.WorkspaceRoot, version.CommitId, version.CommitId,
                                      VersionCorrection.Of(text).Title);

        return added.Ok
            ? new CorrectionOutcome(true, version, HistoryMessages.CorrectionAdded(
                  VersionCorrection.Of(text).Title))
            : CorrectionOutcome.Refused(GitFailures.Translate(
                  added, HistoryMessages.CorrectingATitle, git.WorkspaceRoot));
    }

    /// <summary>
    /// Every annotation in this workspace, keyed by the identity it is attached to.
    ///
    /// <para><b>Two processes, whatever the count</b>, for <see cref="RestorePoints"/>' reason: one
    /// <c>notes list</c> for the note-to-commit mapping, one batched object read for the text. A
    /// <c>notes show</c> per row is one process per entry on a panel that refreshes at every
    /// boundary.</para>
    ///
    /// <para><b>An empty answer is the ordinary one</b> — most workspaces have never had a correction
    /// written in them — and it costs one process that fails and is not an error.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, VersionCorrection> Annotations(GitCommand git)
    {
        var empty = new Dictionary<string, VersionCorrection>(StringComparer.Ordinal);

        var listed = git.Run(["notes", "--ref", NotesRef, "list"], new GitRunOptions(ReadOnly: true));
        if (!listed.Ok) return empty;

        // "<note object> <annotated object>", one pair per line.
        Dictionary<string, string> noteOf = new(StringComparer.Ordinal);
        foreach (string line in listed.StdOut.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Trim().Split(' ');
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                noteOf[parts[1]] = parts[0];
        }

        if (noteOf.Count == 0) return empty;

        var bodies = ReadBlobs(git, noteOf.Values);

        Dictionary<string, VersionCorrection> corrections = new(StringComparer.Ordinal);
        foreach (var (commitId, noteId) in noteOf)
            if (bodies.TryGetValue(noteId, out string? text) && text.Trim().Length > 0)
                corrections[commitId] = VersionCorrection.Of(text.Trim());

        return corrections;
    }

    // ── Reading git's own objects ─────────────────────────────────────────────────────────────────

    /// <summary>One commit object, raw, or null when it could not be read.</summary>
    private static string? ReadCommit(GitCommand git, string commitId)
    {
        var r = git.Run(["cat-file", "commit", commitId], new GitRunOptions(ReadOnly: true));
        return r.Ok ? r.StdOut : null;
    }

    /// <summary>
    /// The blob bodies for a set of note objects. <c>cat-file --batch</c> is length-framed; this parses
    /// on the header lines for <see cref="HistoryBrowser"/>'s stated reason — .NET has already decoded
    /// the stream, so the announced BYTE size and the CHAR buffer disagree the moment a correction
    /// holds anything non-ASCII, which a correction written by a person very often does.
    /// </summary>
    private static Dictionary<string, string> ReadBlobs(GitCommand git, IEnumerable<string> ids)
    {
        Dictionary<string, string> bodies = new(StringComparer.Ordinal);

        string request = string.Join('\n', ids) + "\n";
        var r = git.Run(["cat-file", "--batch"],
                        new GitRunOptions(StandardInput: request, ReadOnly: true));
        if (!r.Ok) return bodies;

        string[] lines = r.StdOut.Replace("\r\n", "\n").Split('\n');

        string? currentId = null;
        var     body      = new StringBuilder();

        foreach (string line in lines)
        {
            if (IsBlobHeader(line, out string? id))
            {
                if (currentId is not null) bodies[currentId] = body.ToString();
                currentId = id;
                body.Clear();
                continue;
            }

            if (currentId is not null) body.Append(line).Append('\n');
        }

        if (currentId is not null) bodies[currentId] = body.ToString();
        return bodies;
    }

    private static bool IsBlobHeader(string line, out string? id)
    {
        id = null;
        string[] parts = line.Split(' ');
        if (parts.Length != 3 || parts[1] != "blob") return false;
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        if (parts[0].Length < 7 || !parts[0].All(Uri.IsHexDigit)) return false;

        id = parts[0];
        return true;
    }

    /// <summary>What one commit object holds, at the granularity this operation has to preserve.</summary>
    private sealed record RawCommit(
        string       TreeId,
        List<string> Parents,
        string       AuthorName,
        string       AuthorEmail,
        string       AuthorDate,
        string       CommitterName,
        string       CommitterEmail,
        string       CommitterDate,
        string       Message);

    private static RawCommit Parse(string raw)
    {
        string tree = "";
        List<string> parents = [];
        (string Name, string Email, string Date) author = ("", "", "");
        (string Name, string Email, string Date) committer = ("", "", "");

        string[] lines = raw.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        for (; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) { i++; break; }

            if (line.StartsWith("tree ", StringComparison.Ordinal))
                tree = line[5..].Trim();
            else if (line.StartsWith("parent ", StringComparison.Ordinal))
                parents.Add(line[7..].Trim());
            else if (line.StartsWith("author ", StringComparison.Ordinal))
                author = SplitIdentity(line[7..]);
            else if (line.StartsWith("committer ", StringComparison.Ordinal))
                committer = SplitIdentity(line[10..]);
        }

        return new RawCommit(tree, parents,
                             author.Name, author.Email, author.Date,
                             committer.Name, committer.Email, committer.Date,
                             string.Join('\n', lines.Skip(i)));
    }

    /// <summary>
    /// <c>Name &lt;address&gt; 1700000000 +0100</c> — the three fields, kept apart so they can be
    /// handed straight back. <b>The date is passed on in git's own raw spelling</b>, which git's date
    /// parser accepts verbatim: reformatting it through a <c>DateTimeOffset</c> would lose the recorded
    /// zone offset, and R-rc11-10 is about the time as it was recorded, offset included.
    /// </summary>
    private static (string Name, string Email, string Date) SplitIdentity(string identity)
    {
        int open  = identity.IndexOf('<');
        int close = identity.IndexOf('>');
        if (open < 0 || close < open) return (identity.Trim(), "", "");

        return (identity[..open].Trim(),
                identity[(open + 1)..close].Trim(),
                identity[(close + 1)..].Trim());
    }

    /// <summary>
    /// R-rc11-10's whole mechanism: the author, the committer and both times, given back to git
    /// exactly as they were recorded.
    /// </summary>
    private static Dictionary<string, string> RecordedIdentity(RawCommit c)
    {
        Dictionary<string, string> env = new(StringComparer.Ordinal);

        if (c.AuthorName.Length > 0)      env["GIT_AUTHOR_NAME"]      = c.AuthorName;
        if (c.AuthorEmail.Length > 0)     env["GIT_AUTHOR_EMAIL"]     = c.AuthorEmail;
        if (c.AuthorDate.Length > 0)      env["GIT_AUTHOR_DATE"]      = c.AuthorDate;
        if (c.CommitterName.Length > 0)   env["GIT_COMMITTER_NAME"]   = c.CommitterName;
        if (c.CommitterEmail.Length > 0)  env["GIT_COMMITTER_EMAIL"]  = c.CommitterEmail;
        if (c.CommitterDate.Length > 0)   env["GIT_COMMITTER_DATE"]   = c.CommitterDate;

        return env;
    }

    /// <summary>
    /// Replaces the subject and leaves everything else — the explanation line, the blank line, every
    /// trailer — exactly as it was. A message with no subject at all gains one, which is the shape of
    /// a commit somebody wrote with an empty first line.
    /// </summary>
    internal static string ReplaceSubject(string message, string subject)
    {
        string[] lines = (message ?? "").Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            lines[i] = subject;
            return string.Join('\n', lines);
        }

        return subject + "\n";
    }

    private static string OneLine(string? text) => text?.ReplaceLineEndings(" ").Trim() ?? "";
}
