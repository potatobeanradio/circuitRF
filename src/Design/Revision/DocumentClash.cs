using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>Which of the two versions of a clashing document.</summary>
public enum ClashSide
{
    /// <summary>The one already in this workspace — the designer's own.</summary>
    Mine,

    /// <summary>The one that arrived from the other copy.</summary>
    Theirs,
}

/// <summary>
/// One side of a clash, as a designer reads it before choosing.
/// </summary>
/// <param name="Side">Which one this is.</param>
/// <param name="Who">Who wrote it. Blank when the version it came from cannot be identified, which is
/// honest rather than fabricated.</param>
/// <param name="WhenUtc">When they did.</param>
/// <param name="Title">The title of the version it came from, when there is one.</param>
/// <param name="Changed">
/// The documents that version changed. <b>Enough to choose, and no invitation to merge by hand</b>
/// (R-rc7-13) — a designer picking between two versions of a layout needs to know what else moved with
/// it, and needs nothing that would let them assemble a third.
/// </param>
/// <param name="BlobId">The exact content this side holds. An address; never shown.</param>
public sealed record ClashOption(
    ClashSide                      Side,
    string                         Who,
    DateTimeOffset                 WhenUtc,
    string                         Title,
    IReadOnlyList<DocumentChange>  Changed,
    string                         BlobId);

/// <summary>One document changed in two places at once.</summary>
/// <param name="RelativePath">Where it is in the workspace.</param>
/// <param name="Mine">The version already here.</param>
/// <param name="Theirs">The one that arrived.</param>
public sealed record DocumentClash(string RelativePath, ClashOption Mine, ClashOption Theirs);

/// <summary>
/// <b>Two versions of one document, and a choice between them — never a diff to reconcile</b>
/// (<c>docs/design/revision-control.md</c> §6.1, §6.2; RC-7 R-rc7-12, R-rc7-13, R-rc7-14).
///
/// <para><b>There is no merge for a layout and circuitRF must not pretend there is.</b> A three-way
/// text merge of a polygon's vertex list can produce geometry that is invalid, or valid and wrong,
/// while remaining perfectly well-formed JSON that opens without complaint. The same is true of a
/// schematic's connectivity. A merged design that is silently wrong is worse than a clash, because the
/// clash is at least visible. RC-3's generated <c>.gitattributes</c> marks the five document types
/// <c>-merge</c>, so git will not attempt one; <b>this is what the designer sees when it does
/// not</b>.</para>
///
/// <para><b>And this is not conservatism.</b> Large analog/RF design-management architectures are
/// centralised and <b>pessimistic</b> — check-out/check-in locking so two people cannot edit one
/// cellview concurrently, versioning at the granularity of the cellview rather than the file, and a
/// separate configuration object binding which version of which cell a hierarchy resolves to.
/// <b>The locking is the direct consequence of no-merge</b>: when merge is impossible, preventing
/// divergence is the only remaining strategy. circuitRF builds no lock server, but that is precisely
/// why whole-file resolution here is correct rather than lazy, why RC-2's read-only default for a
/// referenced workspace is the cheapest available approximation of a lock, and why RC-9's pins are the
/// one genuinely enterprise-grade item on the list.</para></para>
///
/// <para><b>Nothing in this type writes a merged file, and there is no parameter that would let it.</b>
/// <see cref="Keep"/> takes a side, reads that side's content whole, and writes it. There is no third
/// content to produce and no code path that could produce one — which is what gate 7 asserts.</para>
/// </summary>
public static class DocumentClashes
{
    /// <summary>
    /// Every document currently changed in two places at once, or an empty list.
    ///
    /// <para><b>Read from the index's unmerged entries</b>, which is where git records exactly this
    /// and nowhere else — stage 2 is the version already here, stage 3 the one that arrived. Reading
    /// it from the working tree instead would find conflict markers, which for an unmergeable document
    /// do not exist, so it would find nothing and report all-clear.</para>
    /// </summary>
    public static IReadOnlyList<DocumentClash> Find(GitCommand git)
    {
        var listed = git.Run(["ls-files", "-u", "-z"], new GitRunOptions(ReadOnly: true));
        if (!listed.Ok) return [];

        // `<mode> <object> <stage>\t<path>`, NUL-terminated. Two or three records per path.
        Dictionary<string, Dictionary<int, string>> stages = new(StringComparer.Ordinal);

        foreach (string record in listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = record.IndexOf('\t');
            if (tab <= 0) continue;

            string[] parts = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !int.TryParse(parts[2], out int stage)) continue;

            string path = record[(tab + 1)..];
            if (!stages.TryGetValue(path, out var byStage))
                stages[path] = byStage = [];
            byStage[stage] = parts[1];
        }

        if (stages.Count == 0) return [];

        var mine   = Describe(git, ClashSide.Mine,   "HEAD");
        var theirs = Describe(git, ClashSide.Theirs, IncomingVersion(git));

        List<DocumentClash> clashes = [];
        foreach (var (path, byStage) in stages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // Stage 2 is what is here, stage 3 is what arrived. A document only one side has is still
            // a choice — keep it, or do not — so a missing stage is an empty blob rather than a
            // reason to drop the row.
            clashes.Add(new DocumentClash(
                path,
                mine   with { BlobId = byStage.GetValueOrDefault(2, "") },
                theirs with { BlobId = byStage.GetValueOrDefault(3, "") }));
        }

        return clashes;
    }

    /// <summary>
    /// <b>Keeps one side, whole</b> (R-rc7-13).
    ///
    /// <para>The chosen side's content is written to the file exactly as that side holds it, and the
    /// document is recorded as settled. <b>Nothing is combined</b>: there is no third content, and the
    /// only inputs are a path and which of two objects to copy.</para>
    ///
    /// <para>A side that holds nothing means that version did not have the document, so keeping it
    /// removes the file — which is a choice a designer can legitimately make and would otherwise be
    /// unexpressible.</para>
    /// </summary>
    public static RevisionOutcome Keep(GitCommand git, DocumentClash clash, ClashSide side)
    {
        var chosen = side == ClashSide.Mine ? clash.Mine : clash.Theirs;
        string full = Path.Combine(git.WorkspaceRoot,
                                   clash.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (chosen.BlobId.Length == 0)
        {
            try
            {
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return RevisionOutcome.Failed(GitFailures.Unrecognised(
                    HistoryMessages.ChoosingAVersion, $"'{clash.RelativePath}' could not be removed"));
            }
        }
        else
        {
            // The blob's own bytes, written whole. `cat-file blob` streams the object exactly as it
            // is; nothing here decodes, re-encodes or reformats it, so a design document arrives byte
            // for byte as the version that holds it wrote it.
            var content = git.Run(["cat-file", "blob", chosen.BlobId], new GitRunOptions(ReadOnly: true));
            if (!content.Ok)
                return RevisionOutcome.Failed(GitFailures.Translate(
                    content, HistoryMessages.ChoosingAVersion, git.WorkspaceRoot));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                Cells.AtomicFile.RewriteText(full, content.StdOut);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return RevisionOutcome.Failed(GitFailures.Unrecognised(
                    HistoryMessages.ChoosingAVersion, $"'{clash.RelativePath}' could not be written"));
            }
        }

        // The index's unmerged entries for this path go, so it stops being reported as a clash. Adding
        // the file back is what does that, and removing it is what does it for the empty side.
        var settled = chosen.BlobId.Length == 0
                    ? git.Run(["rm", "-f", "--cached", "--", clash.RelativePath])
                    : git.Run(["add", "--", clash.RelativePath]);

        return settled.Ok
            ? RevisionOutcome.Success
            : RevisionOutcome.Failed(GitFailures.Translate(
                settled, HistoryMessages.ChoosingAVersion, git.WorkspaceRoot));
    }

    /// <summary>Which side a designer chose, named for the report. Not "ours" and "theirs".</summary>
    public static string Describe(ClashSide side)
        => side == ClashSide.Mine ? "your" : "the other copy's";

    /// <summary>
    /// The version that arrived, when git recorded one. <c>MERGE_HEAD</c> is where it puts that and
    /// the only place it does; absent, there is nothing incoming and the clash — if there is one —
    /// came from somewhere this feature did not create.
    /// </summary>
    private static string IncomingVersion(GitCommand git)
    {
        var r = git.Run(["rev-parse", "--verify", "--quiet", "MERGE_HEAD"],
                        new GitRunOptions(ReadOnly: true));
        return r.Ok && r.Line.Length > 0 ? r.Line : "";
    }

    /// <summary>
    /// One side's identity — who wrote it, when, what they called it, and what else it changed.
    ///
    /// <para><b>An unidentifiable side is reported blank rather than guessed at.</b> "Somebody, at
    /// some point" is a true statement a designer can act on; a fabricated name is one they cannot
    /// tell from a real one.</para>
    /// </summary>
    private static ClashOption Describe(GitCommand git, ClashSide side, string version)
    {
        if (version.Length == 0)
            return new ClashOption(side, "", DateTimeOffset.UnixEpoch, "", [], "");

        var found = HistoryBrowser.Versions(git, 0)
                                  .FirstOrDefault(v => v.CommitId.StartsWith(version, StringComparison.Ordinal))
                 ?? HistoryBrowser.Versions(git, 1).FirstOrDefault();

        if (found is null) return new ClashOption(side, "", DateTimeOffset.UnixEpoch, "", [], "");

        // What that version changed, against the one before it — which is the answer to "what am I
        // choosing", rather than a comparison against the other side that would read as a merge to
        // reconcile.
        var against = ParentOf(git, found.CommitId);
        var changed = against is { } parent ? HistoryBrowser.Compare(git, parent, found.CommitId) : [];

        return new ClashOption(side, found.Who, found.WhenUtc, found.Title, changed, "");
    }

    private static string? ParentOf(GitCommand git, string commitId)
    {
        var r = git.Run(["rev-parse", "--verify", "--quiet", commitId + "^"],
                        new GitRunOptions(ReadOnly: true));
        return r.Ok && r.Line.Length > 0 ? r.Line : null;
    }

    /// <summary>What the panel says once every clash has been settled.</summary>
    public static Diagnostic AllSettled(int count) => Diagnostic.Create(
        "revision.clash.all-settled",
        DiagnosticSeverity.Info,
        "{count} document(s) settled. Each one is whole — the version you chose, exactly as it was "
      + "written.",
        ("count", count));
}
