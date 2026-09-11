using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-11's gates — <c>docs/sonnet-briefs/brief-revision-control-11-correcting-what-you-wrote.md</c>
/// §6, <c>docs/design/revision-control.md</c> §5.11.
///
/// <para><b>The line everything here holds</b> (R-rc11-1): what was recorded is never altered; what a
/// person wrote <i>about</i> it may be corrected by that person, until it has been shared — after
/// which it can only be annotated.</para>
///
/// <para><b>Every fixture path here is the SHAPE of a path, never a real one.</b> A workspace name
/// from a real machine must not reach this repository.</para>
///
/// <para>In <see cref="AppDataRootCollection"/> for the reason RC-3's, RC-5's, RC-6's, RC-7's, RC-9's
/// and RC-10's own gates are: the identity path redirects the per-user state directory, and the
/// git-environment isolation these tests install is process-wide.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class CorrectingWhatYouWroteTests
{
    // ══ 1. A renamed restore point keeps its tree and every trailer but the label ═════════════════

    /// <summary>
    /// R-rc11-3, R-rc11-4. <b>The tree is byte-identical, and the sequence, the origin, the kept mark
    /// and the left-out record all survive.</b>
    ///
    /// <para>Losing one of those is silent and shows up weeks later — as an entry retention thins that
    /// it should not have, or as a row whose origin sentence has changed to something the designer did
    /// not do. So each is asserted by name rather than by "the entry still looks right".</para>
    /// </summary>
    [GitFact]
    public void ARenamedEntryKeepsItsTreeAndEveryTrailerButTheLabel()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        // A BEFORE-BATCH entry, which is the hardest of the six: its label is built from the intent,
        // and R-rc11-6 requires the rename not to erase that a batch happened.
        ws.Write("cells/a/thing.csch", "before the assistant");
        ws.WriteBytes("cells/a/enormous.gds", 1);
        var taken = WorkspaceCheckpoints.Take(git, CheckpointOrigin.BeforeBatch,
                                              "retune the output match", attended: false,
                                              exclusions: ["cells/a/enormous.gds"], forceRecord: true);
        Assert.True(taken.Recorded, string.Join(" | ", taken.Diagnostics.Select(d => d.Render())));

        var before = RestorePoints.List(git).Single();
        Assert.Equal(CheckpointOrigin.BeforeBatch, before.Origin);
        Assert.Equal("before: retune the output match", before.Label);

        Assert.True(RestorePoints.Rename(git, before, "before the match rework").Ok);

        var after = RestorePoints.List(git).Single();

        Assert.Equal("before the match rework", after.Label);
        Assert.Equal(before.TreeId,   after.TreeId);       // the state is exactly the state
        Assert.Equal(before.Sequence, after.Sequence);     // §5.6 rule 2's ordering
        Assert.Equal(before.Origin,   after.Origin);       // R-rc11-6: still a batch
        Assert.Equal(before.Kept,     after.Kept);
        Assert.Equal(before.LeftOut,  after.LeftOut);
        Assert.True(after.IsIncomplete);

        // And the origin SENTENCE, which is what a designer actually reads in the expander.
        Assert.Equal(CheckpointMessage.Explanation(CheckpointOrigin.BeforeBatch),
                     CheckpointMessage.Explanation(after.Origin));
        Assert.Contains(CheckpointMessage.Explanation(CheckpointOrigin.BeforeBatch),
                        MessageOf(ws, after.CommitId), StringComparison.Ordinal);

        // §5.2b: still PARENTLESS. A rename that chained the new object behind the old one would make
        // §5.6's thinning free nothing, silently, and no survival gate would notice.
        Assert.Equal("", ws.Raw("rev-list", "--parents", "-1", after.CommitId).Out.Trim()
                           .Split(' ').Skip(1).FirstOrDefault() ?? "");
    }

    /// <summary>
    /// R-rc11-4's other half: <b>a rename works on the four origins whose subject ignores the
    /// label.</b>
    ///
    /// <para>A workspace-close entry is "closed" whatever anybody types, so a rename routed
    /// through the label alone would silently do nothing on exactly the entries a designer most wants
    /// to name — the automatic ones they are trying to find again.</para>
    /// </summary>
    [GitFact]
    public void AnAutomaticEntryCanBeNamedEvenThoughItsOriginWritesItsOwnSubject()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "the afternoon's work");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.WorkspaceClosed, null).Recorded);

        var closed = RestorePoints.List(git).Single();
        Assert.Equal("closed", closed.Label);

        Assert.True(RestorePoints.Rename(git, closed, "the day the match worked").Ok);

        var renamed = RestorePoints.List(git).Single();
        Assert.Equal("the day the match worked", renamed.Label);
        Assert.Equal(CheckpointOrigin.WorkspaceClosed, renamed.Origin);
        Assert.Equal(closed.TreeId, renamed.TreeId);

        // And marking it KEEP afterwards does not quietly undo the correction — which it would if the
        // rebuild went back through the origin's own wording.
        Assert.True(RestorePoints.MarkKept(git, renamed).Ok);
        Assert.Equal("the day the match worked", RestorePoints.List(git).Single().Label);
        Assert.True(RestorePoints.List(git).Single().Kept);
    }

    // ══ 2. A deleted restore point is journalled and comes back ═══════════════════════════════════

    /// <summary>
    /// R-rc11-5. <b>Letting an entry go goes THROUGH RC-6's thinning journal, not around it</b> — so
    /// it is still listed, marked, and one reference update brings it back with its exact tree.
    /// </summary>
    [GitFact]
    public void AnEntryLetGoIsJournalledAndComesBack()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "the state to let go");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "careless line").Recorded);

        var point = RestorePoints.List(git).Single();

        Assert.True(RestorePoints.Forget(git, point).Ok);

        // Gone from the live list, and the journal holds it with the reference it dropped.
        Assert.Empty(RestorePoints.List(git));
        var journalled = Assert.Single(ThinningJournal.Read(ws.Root));
        Assert.Equal(point.CommitId, journalled.CommitId);
        Assert.Equal(point.Reference, journalled.Reference);

        // Still LISTED, marked — the difference between a state tidied away and one destroyed.
        var listed = Assert.Single(RestorePoints.ListIncludingThinned(git));
        Assert.True(listed.Thinned);
        Assert.Equal(point.TreeId, listed.TreeId);

        // And it comes back, with the same tree, through RC-6's own way back.
        Assert.True(RestorePoints.RestoreThinned(git, listed).Ok);
        var back = Assert.Single(RestorePoints.List(git));
        Assert.Equal(point.TreeId, back.TreeId);
        Assert.False(back.Thinned);
        Assert.Empty(ThinningJournal.Read(ws.Root));
    }

    // ══ 3. Letting an entry go frees nothing until a reclaim ══════════════════════════════════════

    /// <summary>
    /// R-rc11-5's load-bearing half, reached from this brief's entry point: <b>a designer tidying a
    /// label must not be the one path in this document that destroys a state.</b>
    ///
    /// <para>Asserted the way RC-6's own fixture asserts it — on a SCRATCH COPY with an immediate
    /// expiry, because §4.5's <c>gc.pruneExpire = never</c> means the real repository can never show
    /// the difference between "still reachable" and "not yet collected".</para>
    /// </summary>
    [GitFact]
    public void LettingAnEntryGoFreesNothingUntilAReclaim()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "keep this one");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "the one that stays").Recorded);

        ws.Write("cells/a/thing.csch", "let this one go");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "the one that goes").Recorded);

        var points = RestorePoints.List(git).OrderBy(p => p.Sequence).ToList();
        Assert.Equal(2, points.Count);

        Assert.True(RestorePoints.Forget(git, points[1]).Ok);

        // Nothing freed here: the object is still in the repository, under circuitRF's own settings.
        Assert.Equal(0, ws.Raw("cat-file", "-e", points[1].CommitId).Code);

        // The reclaim is EXPLICIT, and it acts on the journal's own ages.
        var reclaimed = GitReclaim.Reclaim(git, ThinningJournal.Read(ws.Root),
                                           TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Null(reclaimed.Diagnostic);
        Assert.Single(reclaimed.Reclaimed);

        Assert.NotEqual(0, ws.Raw("cat-file", "-e", points[1].CommitId).Code);
        Assert.Equal(0, ws.Raw("cat-file", "-e", points[0].CommitId).Code);
        Assert.Empty(ThinningJournal.Read(ws.Root));
    }

    // ══ 4. The newest unshared version's title corrects, and nothing else moves ═══════════════════

    /// <summary>
    /// R-rc11-7, R-rc11-10. <b>The title changes; the tree, the author and the time do not.</b>
    ///
    /// <para>The time is the one that would go wrong quietly. <c>git commit --amend</c> restamps the
    /// committer date by default, and a title correction that moved a version's date would make §5.6
    /// rule 2's ordering argument false in the one list a designer reads it from — so the recorded
    /// identity and both recorded times are handed back to <c>commit-tree</c> verbatim, and this is
    /// what says so.</para>
    /// </summary>
    [GitFact]
    public void TheNewestUnsharedTitleCorrectsAndTheTreeAuthorAndTimeDoNotMove()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        // Kept a fortnight ago, so a restamp would be unmissable.
        var when = DateTimeOffset.UtcNow.AddDays(-14);
        ws.SetClock(when);
        ws.Write("cells/a/thing.csch", "the design as it stands");
        Assert.True(WorkspaceCommit.Commit(git, "Retuned the ouptut match").Ok);
        ws.SetClock(DateTimeOffset.UtcNow);

        var before = Assert.Single(HistoryBrowser.Versions(git));
        string beforeAuthor = ws.Raw("log", "-1", "--format=%an <%ae> %at %ai", before.CommitId).Out.Trim();

        var outcome = VersionCorrections.CorrectTitle(git, before, "Retuned the output match");
        Assert.True(outcome.Ok, outcome.Diagnostic.Render());

        var after = Assert.Single(HistoryBrowser.Versions(git));
        Assert.Equal("Retuned the output match", after.Title);

        // The state is exactly the state, and the identity and both times are the recorded ones.
        Assert.Equal(before.TreeId, after.TreeId);
        Assert.Equal(beforeAuthor,
                     ws.Raw("log", "-1", "--format=%an <%ae> %at %ai", after.CommitId).Out.Trim());
        Assert.Equal(when.ToUnixTimeSeconds(), after.WhenUtc.ToUnixTimeSeconds());

        // Every trailer the message carried survives — asserted through the one that is easiest to
        // lose, since rebuilding the message from scratch would have dropped it.
        Assert.Contains(CommitMessage.ExplicitLine, MessageOf(ws, after.CommitId), StringComparison.Ordinal);
        Assert.True(after.Explicit);
    }

    /// <summary>
    /// R-rc11-8. <b>An older version's title is refused, the refusal names WHY, and it offers the
    /// annotation</b> — which is available for any version at all.
    /// </summary>
    [GitFact]
    public void AnOlderVersionRefusesAndOffersTheAnnotation()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "v1");
        Assert.True(WorkspaceCommit.Commit(git, "The first one").Ok);
        ws.Write("cells/a/thing.csch", "v2");
        Assert.True(WorkspaceCommit.Commit(git, "The second one").Ok);

        var older = HistoryBrowser.Versions(git).Single(v => v.Title == "The first one");

        var refused = VersionCorrections.CorrectTitle(git, older, "Something else");
        Assert.False(refused.Ok);
        Assert.Equal("revision.correction.not-newest", refused.Diagnostic.Id);
        Assert.Contains("correction", refused.Diagnostic.Render(), StringComparison.OrdinalIgnoreCase);

        // And nothing moved.
        Assert.Equal("The first one",
                     HistoryBrowser.Versions(git).Single(v => v.CommitId == older.CommitId).Title);

        // The remedy the refusal names actually works on this entry.
        Assert.True(VersionCorrections.Annotate(git, older, "The first one — 3.5 GHz, not 3.6").Ok);
    }

    // ══ 5. A shared version refuses, and both shapes of "shared" refuse ═══════════════════════════

    /// <summary>
    /// R-rc11-2, R-rc11-12. <b>Two workspaces, and both refuse.</b>
    ///
    /// <para>The first has a remote-tracking reference covering the version, so sharing is computed
    /// exactly. The second has a remote configured and has never fetched, so it <b>cannot</b> be
    /// computed — and that is the case the safe-direction rule decides: the answer is <i>shared</i>,
    /// because a correction refused is recoverable and an erasure believed is not.</para>
    /// </summary>
    [GitFact]
    public void ASharedVersionRefusesTheCorrection_AndSoDoesOneWhoseSharingCannotBeComputed()
    {
        using var state = new AppDataRootScope();
        using var far   = Librarian();

        string near = CloneOf(far);
        try
        {
            var mine    = GitCommand.For(near)!;
            var version = Assert.Single(HistoryBrowser.Versions(mine));

            // Computed exactly: the clone's remote-tracking reference covers this version.
            var reach = VersionSharing.Compute(mine);
            Assert.Equal(SharingReach.Known, reach.Reach);
            Assert.True(reach.Contains(version.CommitId));

            var refused = VersionCorrections.CorrectTitle(mine, version, "A different line");
            Assert.False(refused.Ok);
            Assert.Equal("revision.correction.shared", refused.Diagnostic.Id);
            Assert.Contains("correction", refused.Diagnostic.Render(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(version.Title, Assert.Single(HistoryBrowser.Versions(mine)).Title);

            // And the annotation, which the refusal named, applies to it.
            Assert.True(VersionCorrections.Annotate(mine, version, "A different line").Ok);
        }
        finally { Delete(near); }

        // ── The second shape: a remote configured and never fetched ──────────────────────────────
        using var never = Librarian();
        var alone = never.Git();

        Assert.Equal(SharingReach.NoOtherCopy, VersionSharing.Compute(alone).Reach);

        // A remote is added and nothing is ever fetched. This machine now genuinely does not know what
        // is on the other end, which is the ordinary shape of a workspace somebody wired up this
        // morning.
        Assert.Equal(0, never.Raw("remote", "add", "origin",
                                  Path.Combine(never.Root + "-nowhere")).Code);

        var unknown = VersionSharing.Compute(alone);
        Assert.Equal(SharingReach.CannotBeComputed, unknown.Reach);

        var theirs = Assert.Single(HistoryBrowser.Versions(alone));
        Assert.True(unknown.Contains(theirs.CommitId));

        var alsoRefused = VersionCorrections.CorrectTitle(alone, theirs, "A different line");
        Assert.False(alsoRefused.Ok);
        Assert.Equal("revision.correction.shared", alsoRefused.Diagnostic.Id);
        Assert.Equal(theirs.Title, Assert.Single(HistoryBrowser.Versions(alone)).Title);
    }

    /// <summary>
    /// The other side of the same predicate, and it matters as much: <b>a workspace with no other copy
    /// has nothing shared, and its titles are its author's.</b> That is most workspaces, and a
    /// predicate that answered "shared" defensively there would take this whole feature away from
    /// nearly everyone who has it.
    /// </summary>
    [GitFact]
    public void AWorkspaceWithNoOtherCopyHasNothingShared()
    {
        using var state = new AppDataRootScope();
        using var ws    = Librarian();

        var reach = VersionSharing.Compute(ws.Git());
        Assert.Equal(SharingReach.NoOtherCopy, reach.Reach);
        Assert.False(reach.Contains(HistoryBrowser.Versions(ws.Git())[0].CommitId));

        Assert.True(VersionCorrections.CanCorrectTitle(
            ws.Git(), HistoryBrowser.Versions(ws.Git())[0], reach));
    }

    // ══ 6. The annotation shows in place of the original, and travels ═════════════════════════════

    /// <summary>
    /// R-rc11-13. <b>The correction is what the row shows; the original is still there and still
    /// reachable.</b>
    /// </summary>
    [GitFact]
    public void TheCorrectionShowsInPlaceOfTheOriginalAndTheOriginalIsReachable()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "the design");
        Assert.True(WorkspaceCommit.Commit(git, "Match for the Northwind board").Ok);

        var version = Assert.Single(HistoryBrowser.Versions(git));
        Assert.True(VersionCorrections.Annotate(git, version, "Output match, 3.5 GHz").Ok);

        // The list shows the correction, and carries what it corrects.
        var row = Assert.Single(HistoryList.Read(git, HistoryFilter.Default).Rows, r => r.IsVersion);
        Assert.Equal("Output match, 3.5 GHz", row.Title);
        Assert.Equal("Match for the Northwind board", row.OriginalTitle);
        Assert.True(row.IsCorrected);

        // R-rc11-1: NOTHING WAS ALTERED. The commit is the same commit, and its message still holds
        // the original wording — which is the fact the un-softened sentence tells the designer.
        Assert.Equal(version.CommitId, row.Identity);
        Assert.Contains("Match for the Northwind board", MessageOf(ws, version.CommitId),
                        StringComparison.Ordinal);

        // The panel puts the original one click away, in the expander.
        var tool = new HistoryTool();
        tool.SetSources(HistoryList.ReadSources(git), hasWorkspace: true);
        var item = Assert.Single(tool.Rows, r => r.IsVersion);
        Assert.Equal("Output match, 3.5 GHz", item.Title);
        Assert.True(item.IsCorrected);
        Assert.Contains("Match for the Northwind board", item.OriginalTitleText, StringComparison.Ordinal);

        // R-rc10-9's search finds it under BOTH wordings — a designer searching for the line they
        // replaced is very often exactly why they are searching.
        Assert.NotEmpty(HistoryList.Read(git, HistoryFilter.Default with { Search = "Northwind" }).Rows);
        Assert.NotEmpty(HistoryList.Read(git, HistoryFilter.Default with { Search = "3.5 GHz" }).Rows);

        // And re-writing it freely is what makes it the right home for commentary.
        Assert.True(VersionCorrections.Annotate(git, version, "Output match, 3.6 GHz").Ok);
        Assert.Equal("Output match, 3.6 GHz",
                     HistoryList.Read(git, HistoryFilter.Default).Rows.First(r => r.IsVersion).Title);

        // Taking it back puts the original in front. It erases nothing, because nothing was altered.
        Assert.True(VersionCorrections.Annotate(git, version, "").Ok);
        Assert.Equal("Match for the Northwind board",
                     HistoryList.Read(git, HistoryFilter.Default).Rows.First(r => r.IsVersion).Title);
    }

    /// <summary>
    /// R-rc11-13's travel half, on RC-9's own fixture with an annotation added.
    ///
    /// <para><b>An annotation that silently failed to travel is a correction a colleague never sees
    /// while the sender believes they made it</b> — which is the worst outcome this feature has, and
    /// it is invisible from the sending side. So the assertion is made from the OTHER copy.</para>
    ///
    /// <para>The send goes to a BARE repository, because git refuses a push to the checked-out branch
    /// of an ordinary one — which is a property of the fixture and not of this feature.</para>
    /// </summary>
    [GitFact]
    public void ACorrectionTravelsWithTheVersionItAnnotates()
    {
        using var state   = new AppDataRootScope();
        using var mine    = Librarian();
        using var scratch = new ScratchDir();

        string hub = Path.Combine(scratch.Dir, "hub.git");
        Directory.CreateDirectory(hub);
        Assert.Equal(0, RawIn(hub, "init", "--bare", "--quiet", ".").Code);

        Assert.Equal(0, mine.Raw("remote", "add", "origin", hub).Code);
        Assert.Equal(0, mine.Raw("push", "--quiet", "--set-upstream", "origin", "HEAD").Code);

        var git     = mine.Git();
        var version = Assert.Single(HistoryBrowser.Versions(git));
        Assert.True(VersionCorrections.Annotate(git, version, "Output match, 3.5 GHz").Ok);

        var sent = WorkspaceRemotes.Push(git);
        Assert.True(sent.Ok, string.Join(" ", sent.Diagnostics.Select(d => d.Render())));

        // Read from a COPY, which is the only place the question can honestly be asked.
        string theirs = Path.Combine(scratch.Dir, "theirs");
        var cloned = WorkspaceClone.Clone(GitDiscovery.Find(out _)!, hub, theirs);
        Assert.True(cloned.Ok, string.Join(" ", cloned.Diagnostics.Select(d => d.Render())));

        var theirGit = GitCommand.For(theirs)!;
        var theirRow = Assert.Single(HistoryList.Read(theirGit, HistoryFilter.Default).Rows,
                                     r => r.IsVersion);

        Assert.Equal("Output match, 3.5 GHz", theirRow.Title);
        Assert.Equal("The first version", theirRow.OriginalTitle);

        // And a FETCH brings a later correction across too, which is the other direction of the same
        // promise: a colleague who corrects a title must reach the sender's list.
        Assert.True(VersionCorrections.Annotate(theirGit, theirRow.Version!, "Output match, 3.6 GHz").Ok);
        Assert.True(WorkspaceRemotes.Push(theirGit).Ok);

        var fetched = WorkspaceRemotes.Fetch(git);
        Assert.True(fetched.Ok);
        Assert.Equal("Output match, 3.6 GHz",
                     HistoryList.Read(git, HistoryFilter.Default).Rows.First(r => r.IsVersion).Title);

        // R-rc11-13 names the namespace as circuitRF's own — the designer's own `git notes` on the
        // default reference are not circuitRF's to overwrite.
        Assert.Equal("refs/notes/circuitrf", VersionCorrections.NotesRef);
        Assert.Equal("", mine.Raw("rev-parse", "--verify", "--quiet", "refs/notes/commits").Out.Trim());
    }

    /// <summary>
    /// The third journey, and the one that could have failed for a reason peculiar to it: <b>an archive
    /// with history included carries the corrections too.</b>
    ///
    /// <para>It survives because an archive copies the repository DIRECTORY rather than negotiating a
    /// refspec — but §9A.3's own packing step is exactly what leaves <c>refs/</c> empty on disk (RC-8's
    /// own finding), so "it copies the folder" is not on its own an argument that a reference under it
    /// arrived. This is measured on an extracted archive rather than reasoned about.</para>
    /// </summary>
    [GitFact]
    public void ACorrectionSurvivesAnArchiveWithHistoryIncluded()
    {
        using var state = new AppDataRootScope();
        using var ws    = Librarian();
        var git = ws.Git();

        var version = Assert.Single(HistoryBrowser.Versions(git));
        Assert.True(VersionCorrections.Annotate(git, version, "Output match, 3.5 GHz").Ok);

        var plan = CircuitRF.Ui.Archive.WorkspaceArchiveScanner.Scan(ws.Root);
        plan.IncludeHistory = true;
        Assert.NotNull(CircuitRF.Ui.Archive.ArchiveHistoryPreparation.Prepare(plan));

        string zip = Path.Combine(ws.Root + "-out", "archive.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        CircuitRF.Ui.Archive.WorkspaceArchiveWriter.Write(plan, zip);

        string into = ws.Root + "-in";
        Directory.CreateDirectory(into);
        var extracted = CircuitRF.Ui.Archive.WorkspaceArchiveExtractor.Extract(zip, into);
        Assert.Empty(extracted.Rejected);

        try
        {
            var theirs = GitCommand.For(extracted.WorkspaceDir)!;
            var row = Assert.Single(HistoryList.Read(theirs, HistoryFilter.Default).Rows, r => r.IsVersion);

            Assert.Equal("Output match, 3.5 GHz", row.Title);
            Assert.Equal("The first version", row.OriginalTitle);
        }
        finally
        {
            Delete(into);
            Delete(ws.Root + "-out");
        }
    }

    // ══ 7. The sentence that may not be softened is present ═══════════════════════════════════════

    /// <summary>
    /// R-rc11-14. <b>The dialog says plainly that the original wording stays in the file and can still
    /// be read by anyone holding the workspace.</b>
    ///
    /// <para>A designer whose problem is embarrassment specifically needs it, and a UI that hid it
    /// would cause the exact harm they came to it to avoid. The shape is RC-7 gate 11's assertion on
    /// <c>RewritingIsYoursToDo</c>: the words are asserted, and so is the fact that they are on the
    /// face of the control rather than behind an expander.</para>
    /// </summary>
    [Fact]
    public void TheUnSoftenedSentenceIsPresentAndIsNotHidden()
    {
        string said = HistoryMessages.ACorrectionDoesNotErase;

        Assert.Contains("does not change what was written", said, StringComparison.Ordinal);
        Assert.Contains("The original wording stays in the history", said, StringComparison.Ordinal);
        Assert.Contains("can still be read by anyone holding a copy", said, StringComparison.Ordinal);
        Assert.Contains("including the copies already sent", said, StringComparison.Ordinal);

        // Nothing in it implies an erasure. That drift is the failure mode of this whole feature.
        foreach (string forbidden in (string[]) ["delete", "remove the original", "erase", "wipe"])
            Assert.DoesNotContain(forbidden, said, StringComparison.OrdinalIgnoreCase);

        // It sits beside the action a designer meets it from — the dialog, and the headless spelling.
        Assert.Contains("ACorrectionDoesNotErase",
                        RestorePointsTests.ReadSource(
                            "src/Ui/Views/Dialogs/CorrectWhatYouWroteDialog.axaml.cs"),
                        StringComparison.Ordinal);
        Assert.Contains("ACorrectionDoesNotErase",
                        RestorePointsTests.ReadSource("src/Cli/History.cs"),
                        StringComparison.Ordinal);

        // And it is on the FACE of the dialog. An expander is where §8.3's paragraph belongs — the
        // answer to a question most designers never ask — and this is the opposite: it is the answer
        // to the question the person opening this dialog already has.
        //
        // Asserted by REMOVING every expander and checking the notice is still there, rather than by
        // forbidding the word: §5.12 put the longer note behind one in this same dialog, and the rule
        // has never been "no expander anywhere" — it is that THIS sentence is not inside one.
        string xaml = RestorePointsTests.ReadSource(
            "src/Ui/Views/Dialogs/CorrectWhatYouWroteDialog.axaml");
        Assert.Contains("x:Name=\"NoticeText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NoticeText\"", WithoutExpanders(xaml), StringComparison.Ordinal);

        // There is no delete on the shared case and no code path behind one (R-rc11-12).
        string behind = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(
            "src/Ui/Views/Dialogs/CorrectWhatYouWroteDialog.axaml.cs"));
        foreach (string forbidden in (string[]) ["update-ref", "commit-tree", "-d", "Delete"])
            Assert.DoesNotContain(forbidden, behind, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-rc11-15. <b><c>RewritingIsYoursToDo</c> is re-pointed, not deleted.</b> It keeps its subject
    /// — a file that must not be in the history — and stops claiming the part §5.11 now governs.
    /// </summary>
    [Fact]
    public void TheEscapeHatchKeepsItsSubjectAndStopsClaimingTitles()
    {
        string said = HistoryMessages.RewritingIsYoursToDo;

        // Its own subject, unchanged. RC-7 gate 11 still asserts all three of these.
        Assert.Contains("git command line", said, StringComparison.Ordinal);
        Assert.Contains("offers no button", said, StringComparison.Ordinal);
        Assert.Contains("every copy anyone else has taken", said, StringComparison.Ordinal);

        // And it no longer says circuitRF will not alter a kept version FULL STOP, which after §5.11 is
        // untrue of its title and remains true of its content.
        Assert.Contains("FILES", said, StringComparison.Ordinal);
        Assert.DoesNotContain("circuitRF will not alter one after the fact", said, StringComparison.Ordinal);

        // The other half is said beside it, so the paragraph reads as the narrow rule it is.
        Assert.Contains("yours to correct", HistoryMessages.TitlesAreYoursToCorrect,
                        StringComparison.Ordinal);
        Assert.Contains("until you have shared it", HistoryMessages.TitlesAreYoursToCorrect,
                        StringComparison.Ordinal);

        Assert.Contains("TitlesAreYoursToCorrect",
                        RestorePointsTests.ReadSource("src/Ui/Views/Dialogs/KeepThisVersionDialog.axaml.cs"),
                        StringComparison.Ordinal);
    }

    // ══ 8. RC-7 gate 11 narrows and does not lift ════════════════════════════════════════════════

    /// <summary>
    /// R-rc11-9, §12 Q33. <b><c>rebase</c>, <c>filter-branch</c>, <c>filter-repo</c>, <c>replace</c>
    /// and <c>reflog</c> stay forbidden everywhere</b>, across every revision source including the
    /// three files this brief added.
    ///
    /// <para><b>And <c>--amend</c> is not used at all</b>, which is narrower than the brief permits.
    /// The permission was for one named file; what it would have bought is nothing, because
    /// <c>git commit --amend</c> needs the shared index and restamps the committer date — the two
    /// things a correction must not do. <c>commit-tree</c> over the recorded tree with the recorded
    /// identity does the job with neither, so the exemption is left unspent and the scan stays
    /// absolute.</para>
    /// </summary>
    [Fact]
    public void TheRewriteScanNarrowsAndDoesNotLift()
    {
        foreach (string path in EveryRevisionSource())
        {
            string source = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(path));

            foreach (string forbidden in new[]
                     { "\"rebase\"", "\"--amend\"", "\"filter-branch\"", "\"filter-repo\"",
                       "\"replace\"", "\"reflog\"" })
                Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                             $"{path} can rewrite a history ({forbidden})");
        }
    }

    // ══ 9. Checkpoint references survive a title correction ══════════════════════════════════════

    /// <summary>
    /// R-rc11-11. <b>§5.2a's checkpoint references are independent of the line of work.</b>
    ///
    /// <para>Asserted rather than reasoned about, because the failure is invisible until somebody
    /// restores — so this one restores.</para>
    /// </summary>
    [GitFact]
    public void CheckpointReferencesSurviveATitleCorrection()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "the state to come back to");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "before the rework").Recorded);
        var point = Assert.Single(RestorePoints.List(git));

        ws.Write("cells/a/thing.csch", "the reworked state");
        Assert.True(WorkspaceCommit.Commit(git, "Reworkd the match").Ok);

        var version = HistoryBrowser.Versions(git)[0];
        Assert.True(VersionCorrections.CorrectTitle(git, version, "Reworked the match").Ok);

        // The reference, its object and its tree are all untouched.
        var after = Assert.Single(RestorePoints.List(git));
        Assert.Equal(point.Reference, after.Reference);
        Assert.Equal(point.CommitId,  after.CommitId);
        Assert.Equal(point.TreeId,    after.TreeId);

        // And going back to it produces its state, which is the thing that would have failed silently.
        var restored = WorkspaceRestore.Restore(git, after);
        Assert.True(restored.Ok);
        Assert.Equal("the state to come back to", File.ReadAllText(ws.File_("cells/a/thing.csch")));
    }

    // ══ 10. No content is ever altered ═══════════════════════════════════════════════════════════

    /// <summary>
    /// R-rc11-1. <b>After every correction path in this brief, every tree in the repository is the
    /// tree it was.</b>
    ///
    /// <para>Trees rather than commits, deliberately: a title correction writes a new commit object by
    /// design, and the whole claim is that the STATE it names is untouched. A gate on commit
    /// identities would fail on the feature working correctly and would say nothing about the
    /// property that matters.</para>
    /// </summary>
    [GitFact]
    public void NoContentIsEverAltered()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "v1");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "a save point").Recorded);
        ws.Write("cells/a/thing.csch", "v2");
        Assert.True(WorkspaceCommit.Commit(git, "The first version").Ok);
        ws.Write("cells/a/thing.csch", "v3");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.WorkspaceClosed, null).Recorded);

        var beforeTrees = EveryTree(ws);
        Assert.NotEmpty(beforeTrees);

        // Every path in this brief, one after another, on the same repository.
        var points = RestorePoints.List(git).OrderBy(p => p.Sequence).ToList();
        Assert.True(RestorePoints.Rename(git, points[0], "the renamed one").Ok);
        Assert.True(RestorePoints.Forget(git, points[1]).Ok);

        var version = HistoryBrowser.Versions(git)[0];
        Assert.True(VersionCorrections.CorrectTitle(git, version, "The corrected version").Ok);
        Assert.True(VersionCorrections.Annotate(git, HistoryBrowser.Versions(git)[0],
                                                "and a correction on top").Ok);

        var afterTrees = EveryTree(ws);

        // Every tree that existed still exists and still holds exactly what it held. A correction may
        // add objects; it may never change one.
        foreach (var (id, listing) in beforeTrees)
        {
            Assert.True(afterTrees.ContainsKey(id), $"tree {id} is no longer in the repository");
            Assert.Equal(listing, afterTrees[id]);
        }
    }

    // ══ 11. The review lists the titles about to leave, for all three journeys ═══════════════════

    /// <summary>
    /// R-rc11-16, R-rc11-18, §12 Q36. <b>Send, copy and archive each show the same list from the same
    /// function</b> — and the archive shows §9A.3's file warning as well, because the two are about
    /// different things and both are true.
    /// </summary>
    [GitFact]
    public void TheReviewListsWhatIsAboutToLeaveForAllThreeJourneys()
    {
        using var state   = new AppDataRootScope();
        using var mine    = Librarian();
        using var scratch = new ScratchDir();

        var git = mine.Git();

        mine.Write("cells/a/thing.csch", "v2");
        Assert.True(WorkspaceCommit.Commit(git, "Match for a customer's board").Ok);

        // A restore point too — an archive carries them and the other two journeys do not (§5.2a).
        mine.Write("cells/a/thing.csch", "v3");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "before the rework").Recorded);

        // COPY and ARCHIVE carry every version there is.
        var copying = TitlesLeaving.For(git, LeavingJourney.Copy);
        Assert.Equal(2, copying.Count);
        Assert.Contains(copying, t => t.Title == "Match for a customer's board");
        Assert.Contains(copying, t => t.Title == "The first version");

        var archiving = TitlesLeaving.For(git, LeavingJourney.Archive);
        Assert.Equal(3, archiving.Count);
        Assert.Contains(archiving, t => t.Title == "before the rework");

        // SEND carries what the other copy does not have yet, which is a different question.
        string hub = Path.Combine(scratch.Dir, "hub.git");
        Directory.CreateDirectory(hub);
        Assert.Equal(0, RawIn(hub, "init", "--bare", "--quiet", ".").Code);
        Assert.Equal(0, mine.Raw("remote", "add", "origin", hub).Code);

        // Nothing fetched yet: this machine has never heard from the other end, so everything is about
        // to go — the honest answer, and the same unknown VersionSharing reads as "shared".
        Assert.Equal(2, TitlesLeaving.For(git, LeavingJourney.Send).Count);

        Assert.Equal(0, mine.Raw("push", "--quiet", "--set-upstream", "origin", "HEAD").Code);
        Assert.Empty(TitlesLeaving.For(git, LeavingJourney.Send));

        mine.Write("cells/a/thing.csch", "v4");
        Assert.True(WorkspaceCommit.Commit(git, "One more thing").Ok);

        var sending = Assert.Single(TitlesLeaving.For(git, LeavingJourney.Send));
        Assert.Equal("One more thing", sending.Title);

        // R-rc11-18. §9A.3's own warning is a different computation and both reach the archive dialog.
        var summary = HistoryArchive.Summarise(git);
        Assert.NotEmpty(summary.Describe());

        // R-rc11-19. The line under the list points at the correction, because the useful response to
        // reading a bad title is fixing it rather than abandoning the send.
        Assert.Contains("correct it", HistoryMessages.TitlesLeavingWhatToDo, StringComparison.Ordinal);
        Assert.Contains("History panel", HistoryMessages.TitlesLeavingWhatToDo, StringComparison.Ordinal);

        // And the three journeys read as sentences rather than as fragments.
        foreach (var journey in (LeavingJourney[])
                 [LeavingJourney.Send, LeavingJourney.Copy, LeavingJourney.Archive])
        {
            Assert.StartsWith("One title is ",
                              HistoryMessages.TitlesLeaving(1, TitlesLeaving.Describe(journey)),
                              StringComparison.Ordinal);
            Assert.StartsWith("4 titles are ",
                              HistoryMessages.TitlesLeaving(4, TitlesLeaving.Describe(journey)),
                              StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// R-rc11-16's structural half: <b>all three journeys go through the ONE function</b>, and each
    /// window surface calls it rather than working out what leaves for itself.
    /// </summary>
    [Fact]
    public void EachJourneyAsksTheOneFunction()
    {
        foreach (string path in (string[])
                 ["src/Ui/ViewModels/WorkspaceViewModel.Sharing.cs",
                  "src/Ui/ViewModels/WorkspaceViewModel.cs"])
            Assert.Contains("ReviewWhatIsLeaving",
                            RestorePointsTests.StripComments(RestorePointsTests.ReadSource(path)),
                            StringComparison.Ordinal);

        // Exactly one place computes it, and the dialog is handed the answer rather than a repository.
        string dialog = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(
            "src/Ui/Views/Dialogs/TitlesLeavingDialog.axaml.cs"));
        Assert.DoesNotContain("GitCommand", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("rev-list",   dialog, StringComparison.Ordinal);

        // R-rc11-19. NOT A CONFIRMATION PROMPT WITH A CHECKBOX. The correction is reachable from the
        // row that showed the problem, the buttons are Stop and the operation's own name, and there is
        // no tickbox anywhere in it.
        string xaml = RestorePointsTests.ReadSource("src/Ui/Views/Dialogs/TitlesLeavingDialog.axaml");
        Assert.DoesNotContain("<CheckBox", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCorrectRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Stop\"",       xaml, StringComparison.Ordinal);

        // And the fix goes through the SAME two commands the panel's own menu invokes, so this surface
        // holds no third spelling of §5.11.
        string host = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(
            "src/Ui/ViewModels/WorkspaceViewModel.Sharing.cs"));
        Assert.Contains("RenameRestorePoint",  host, StringComparison.Ordinal);
        Assert.Contains("CorrectWhatYouWrote", host, StringComparison.Ordinal);
    }

    // ══ 12. `history` has a spelling for each correction ═════════════════════════════════════════

    /// <summary>
    /// §5.3d. <b>The verb run as a PROCESS against the in-process call, byte for byte.</b>
    ///
    /// <para>A rename is the strictest of the four to compare, because the whole claim is that only the
    /// label moved: two workspaces built identically, one renamed through the process and one through
    /// the function, must produce the same message and the same tree.</para>
    /// </summary>
    [GitFact]
    public void HistoryRenameIsTheRenameAction()
    {
        using var viaWindow = Armed();
        using var viaCli    = Armed();

        foreach (var ws in (GitWorkspace[]) [viaWindow, viaCli])
        {
            ws.SetClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
            ws.Write("cells/a/thing.csch", "identical content\n");
            Assert.True(WorkspaceCheckpoints.Take(ws.Git(), CheckpointOrigin.SavePoint,
                                                  "a careless line").Recorded);
        }

        var point = Assert.Single(RestorePoints.List(viaWindow.Git()));
        Assert.True(RestorePoints.Rename(viaWindow.Git(), point, "a corrected line").Ok);

        var run = RunCli(viaCli, "history", "rename", viaCli.Root, "--point",
                         point.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "--label", "a corrected line");
        Assert.Equal(0, run.ExitCode);

        var window = Assert.Single(RestorePoints.List(viaWindow.Git()));
        var cli    = Assert.Single(RestorePoints.List(viaCli.Git()));

        Assert.Equal(window.TreeId, cli.TreeId);
        Assert.Equal(MessageOf(viaWindow, window.CommitId), MessageOf(viaCli, cli.CommitId));
        Assert.Equal(window.CommitId, cli.CommitId);   // same tree, same message, same fixed clock
    }

    /// <summary>The other three nouns, each doing the thing its name says.</summary>
    [GitFact]
    public void EveryCorrectionHasAHeadlessSpelling()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "v1");
        Assert.True(WorkspaceCommit.Commit(git, "Ouptut match").Ok);
        ws.Write("cells/a/thing.csch", "v2");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "a save point").Recorded);

        var point = Assert.Single(RestorePoints.List(git));

        // forget — and it is TIDIED AWAY, not destroyed, which is what the output says.
        var forgotten = RunCli(ws, "history", "forget", ws.Root, "--point",
                               point.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, forgotten.ExitCode);
        Assert.Empty(RestorePoints.List(git));
        Assert.Single(RestorePoints.ListIncludingThinned(git), p => p.Thinned);
        Assert.Contains("tidied away", forgotten.StdOut, StringComparison.OrdinalIgnoreCase);

        // retitle — with no --version, it means the newest, which is what §5.11 case (b) is about.
        var retitled = RunCli(ws, "history", "retitle", ws.Root, "--title", "Output match");
        Assert.Equal(0, retitled.ExitCode);
        Assert.Equal("Output match", HistoryBrowser.Versions(git)[0].Title);

        // correct — and R-rc11-14's sentence is said where a caller with no dialog would meet it.
        var corrected = RunCli(ws, "history", "correct", ws.Root, "--text", "Output match, 3.5 GHz");
        Assert.Equal(0, corrected.ExitCode);
        Assert.Contains("The original wording stays in the history", corrected.StdOut,
                        StringComparison.Ordinal);
        Assert.Equal("Output match, 3.5 GHz",
                     HistoryList.Read(git, HistoryFilter.Default).Rows.First(r => r.IsVersion).Title);

        // review — the list, and nothing written.
        string beforeReview = ws.Raw("rev-parse", "HEAD").Out.Trim();
        var reviewed = RunCli(ws, "history", "review", ws.Root, "--archive");
        Assert.Equal(0, reviewed.ExitCode);
        Assert.Contains("Output match, 3.5 GHz", reviewed.StdOut, StringComparison.Ordinal);
        Assert.Contains("originally", reviewed.StdOut, StringComparison.Ordinal);
        Assert.Equal(beforeReview, ws.Raw("rev-parse", "HEAD").Out.Trim());

        // Every noun is on the usage line, so a caller who types the verb alone can find them.
        var usage = RunCli(ws, "history");
        foreach (string noun in (string[]) ["rename", "forget", "retitle", "correct", "review"])
            Assert.Contains("history " + noun, usage.StdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The view model kept no second copy</b> — the standard <c>AuthoringCliVerbTests</c> scan, over
    /// the paths this brief touched. An operation re-implemented in a view model diverges from the verb
    /// silently.
    /// </summary>
    [Fact]
    public void NeitherSpellingHoldsACorrectionOfItsOwn()
    {
        foreach (string path in (string[])
                 ["src/Ui/ViewModels/WorkspaceViewModel.Revision.cs",
                  "src/Ui/ViewModels/WorkspaceViewModel.Sharing.cs",
                  "src/Ui/Revision/WorkspaceHistoryService.cs",
                  "src/Ui/ViewModels/Dock/HistoryTool.cs",
                  "src/Ui/Views/Dialogs/CorrectWhatYouWroteDialog.axaml.cs",
                  "src/Ui/Views/Dialogs/TitlesLeavingDialog.axaml.cs",
                  "src/Cli/History.cs"])
        {
            string source = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(path));

            foreach (string forbidden in (string[])
                     ["commit-tree", "CheckpointMessage.Build", "\"notes\"", "update-ref"])
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        // Both spellings call the same four src/Design functions.
        foreach (string path in (string[])
                 ["src/Ui/Revision/WorkspaceHistoryService.cs", "src/Cli/History.cs"])
        {
            string source = RestorePointsTests.ReadSource(path);
            foreach (string called in (string[])
                     ["RestorePoints.Rename", "RestorePoints.Forget",
                      "VersionCorrections.CorrectTitle", "VersionCorrections.Annotate"])
                Assert.Contains(called, source, StringComparison.Ordinal);
        }
    }

    // ══ The filter is a display decision and no longer a repository read (RC-11) ═════════════════

    /// <summary>
    /// <b>Toggling a filter starts no git process at all.</b>
    ///
    /// <para>Owner-reported, 2026-09-07: the filter did not feel snappy. Every checkbox re-read the
    /// versions, the restore points, the thinning journal, the sharing set, the corrections and the
    /// incoming versions, plus a second full pass over the restore points for the empty line's count —
    /// a dozen subprocesses on the UI thread for a decision that changes nothing but which rows are
    /// drawn. <b>The fix is not a thread</b>: <see cref="HistorySources.Under"/> is pure, so the read
    /// belongs to a boundary and the filter belongs to the checkbox.</para>
    ///
    /// <para>Asserted as a COUNTER rather than as a duration — the property is "it does not read the
    /// repository", and a timing assertion would measure the machine.</para>
    /// </summary>
    [GitFact]
    public void TogglingTheFilterReadsNothingFromTheRepository()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        foreach (var origin in (CheckpointOrigin[])
                 [CheckpointOrigin.SavePoint, CheckpointOrigin.WorkspaceClosed,
                  CheckpointOrigin.BeforeBatch])
        {
            ws.Write("cells/a/thing.csch", $"{origin} {Guid.NewGuid():N}");
            Assert.True(WorkspaceCheckpoints.Take(git, origin, "something", attended: false,
                                                  forceRecord: true).Recorded);
        }

        var sources = HistoryList.ReadSources(git);

        var tool = new HistoryTool();
        tool.SetSources(sources, hasWorkspace: true);

        int rowsWithDefault = tool.Rows.Count;
        Assert.DoesNotContain(tool.Rows, r => r.Entry.Class == HistoryEntryClass.Automatic);

        // The repository is made UNREADABLE, so anything that tried to read it would fail loudly
        // rather than quietly succeeding from a cache. The toggle must still work.
        string git_ = Path.Combine(ws.Root, ".git");
        string moved = git_ + "-moved";
        Directory.Move(git_, moved);
        try
        {
            tool.ShowAutomatic = true;
            Assert.True(tool.Rows.Count > rowsWithDefault);
            Assert.Contains(tool.Rows, r => r.Entry.Class == HistoryEntryClass.Automatic);

            tool.ShowAutomatic = false;
            Assert.Equal(rowsWithDefault, tool.Rows.Count);

            tool.SearchText = "something";
            Assert.NotEmpty(tool.Rows);

            tool.SearchText = "a phrase nobody wrote";
            Assert.Empty(tool.Rows);
        }
        finally { Directory.Move(moved, git_); }
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every source RC-11's absence gate reads — the RC-7 list plus what this brief added.
    /// Named rather than globbed, so a file added to the feature and not to this list is a visible
    /// omission rather than a silent one.</summary>
    private static IEnumerable<string> EveryRevisionSource()
    {
        foreach (string name in (string[])
                 ["WorkspaceCommit", "CommitMessage", "HistoryBrowser", "HistoryMessages",
                  "DocumentClash", "RestoreProvenance", "WorkspaceRestore",
                  "VersionCorrections", "VersionSharing", "TitlesLeaving",
                  "RestorePoints", "CheckpointMessage", "HistoryList", "WorkspaceRemotes",
                  "WorkspaceClone", "GitCommand"])
            yield return $"src/Design/Revision/{name}.cs";

        yield return "src/Ui/Revision/WorkspaceHistoryService.cs";
        yield return "src/Ui/ViewModels/WorkspaceViewModel.Revision.cs";
        yield return "src/Ui/ViewModels/WorkspaceViewModel.Sharing.cs";
        yield return "src/Ui/ViewModels/Dock/HistoryTool.cs";
        yield return "src/Ui/Views/Dialogs/CorrectWhatYouWroteDialog.axaml.cs";
        yield return "src/Ui/Views/Dialogs/TitlesLeavingDialog.axaml.cs";
        yield return "src/Cli/History.cs";
    }

    /// <summary>Every tree in the repository, with what it holds — the oracle gate 10 compares.</summary>
    private static Dictionary<string, string> EveryTree(GitWorkspace ws)
    {
        var trees = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string line in ws.Raw("cat-file", "--batch-all-objects", "--batch-check=%(objectname) %(objecttype)")
                                  .Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Trim().Split(' ');
            if (parts.Length != 2 || parts[1] != "tree") continue;
            trees[parts[0]] = ws.Raw("ls-tree", "-r", parts[0]).Out;
        }

        return trees;
    }

    private static string MessageOf(GitWorkspace ws, string commitId)
        => ws.Raw("--no-pager", "log", "-1", "--format=%B", commitId).Out;

    /// <summary>An armed workspace with a resolvable identity.</summary>
    /// <summary>
    /// The XAML with every <c>&lt;Expander&gt;…&lt;/Expander&gt;</c> block cut out, so "this control is
    /// not hidden behind one" can be asserted about a named control rather than about the file.
    /// </summary>
    private static string WithoutExpanders(string xaml)
    {
        while (true)
        {
            int open = xaml.IndexOf("<Expander", StringComparison.Ordinal);
            if (open < 0) return xaml;

            int close = xaml.IndexOf("</Expander>", open, StringComparison.Ordinal);
            Assert.True(close > 0, "an <Expander> in this file is never closed.");

            xaml = xaml[..open] + xaml[(close + "</Expander>".Length)..];
        }
    }

    private static GitWorkspace Armed()
    {
        var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");

        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.True(armed.Armed);
        return ws;
    }

    /// <summary>The same, with one version kept in it — the fixture the copy tests are made from.</summary>
    private static GitWorkspace Librarian()
    {
        var ws = Armed();
        ws.Write("Amp/thing.csch", "the first state");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "The first version").Ok);
        return ws;
    }

    private static string CloneOf(GitWorkspace far)
    {
        string near = Path.Combine(Path.GetTempPath(), "crf-rc11near-" + Guid.NewGuid().ToString("N")[..12]);

        var cloned = WorkspaceClone.Clone(GitDiscovery.Find(out _)!, far.Root, near);
        Assert.True(cloned.Ok, string.Join(" ", cloned.Diagnostics.Select(d => d.Render())));
        return near;
    }

    private static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A throwaway directory for the fixtures that need somewhere outside the workspace to
    /// put a bare repository or a copy. Local rather than shared, exactly as RC-9's own is.</summary>
    private sealed class ScratchDir : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "crf-rc11-" + Guid.NewGuid().ToString("N")[..12]);

        public ScratchDir() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (int Code, string Out, string Err) RawIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory       = dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, o, e);
    }

    /// <inheritdoc cref="CommitAndHistoryTests"/>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(
        GitWorkspace ws, params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(CorrectingWhatYouWroteTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;

        string dll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(dll), $"the CLI was not built beside these tests: {dll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = ws.Root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        psi.Environment["GIT_CONFIG_GLOBAL"]   = ws.GlobalConfig;
        psi.Environment["GIT_CONFIG_SYSTEM"]   = Path.Combine(ws.HomeDir, "no-such-system-config");
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["HOME"]                = ws.HomeDir;
        psi.Environment["USERPROFILE"]         = ws.HomeDir;
        psi.Environment["XDG_CONFIG_HOME"]     = ws.HomeDir;

        // The fixed clock the byte-for-byte comparison needs, when one is in force.
        if (Environment.GetEnvironmentVariable("GIT_AUTHOR_DATE") is { Length: > 0 } authored)
        {
            psi.Environment["GIT_AUTHOR_DATE"]    = authored;
            psi.Environment["GIT_COMMITTER_DATE"] = Environment.GetEnvironmentVariable("GIT_COMMITTER_DATE");
        }

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
