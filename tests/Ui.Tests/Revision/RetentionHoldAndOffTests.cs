using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.Revision;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-6's gates — <c>docs/sonnet-briefs/brief-revision-control-6-retention-hold-and-off.md</c> §4.
///
/// <para><b>Every fixture path here is the SHAPE of a path, never a real one.</b> A workspace name
/// from a real machine must not reach this repository.</para>
///
/// <para>In <see cref="AppDataRootCollection"/> for the reason RC-3's and RC-5's own gates are: the
/// identity path redirects the per-user state directory, and the git-environment isolation these
/// tests install is process-wide, so two collections that both touch a process global still clobber
/// each other.</para>
///
/// <para><b>The clock is always an ARGUMENT, never the machine's.</b> Retention's whole design
/// constraint is that a wall clock is user-writable state, so the gates that exercise a clock fault
/// move git's own date variables and pass <c>now</c> in — a test that changed the machine's clock
/// would be measuring the machine.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class RetentionHoldAndOffTests
{
    // ══ 1. The count floor beats age (R-rc6-1) ════════════════════════════════════════════════════

    /// <summary>
    /// <b>Every entry a century old, and the newest N still survive.</b>
    ///
    /// <para>This is the rule that makes a clock jump cost the user nothing at all: a machine whose
    /// clock leaps a century forward makes every entry look expired on the next sweep, and the floor is
    /// applied FIRST so no age overrides it.</para>
    ///
    /// <para><b>The century is put on the SWEEP's clock rather than on the entries', and the arithmetic
    /// is identical.</b> Stamping the objects a century in the past is not expressible: git's date
    /// variables are seconds since 1970, and a 1926 stamp is a negative number git's commit path
    /// refuses — so a fixture written that way records nothing and asserts nothing. Moving the sweep's
    /// clock forward is also the more honest reproduction of the hazard, which is a machine that woke
    /// up in the wrong century, not a repository that was written in one.</para>
    /// </summary>
    [GitFact]
    public void ACenturyOldTimestampDoesNotBeatTheCountFloor()
    {
        using var ws = Armed();
        var git = ws.Git();

        for (int i = 0; i < 7; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        // The floor is a NUMBER, not the shipped default: every one of these fixtures uses the
        // smallest count that still exercises the rule, because each entry is several git processes
        // and a gate that cost thirty of them would have to leave the routine tier — which is exactly
        // where this brief's central guarantees belong.
        var policy = new RetentionPolicy(RetentionDays: 30, MinimumRestorePoints: 5);
        var result = RetentionSweep.Run(git, policy, DateTimeOffset.UtcNow.AddYears(100));

        Assert.False(result.Plan.Refused);

        var left = RestorePoints.List(git);
        Assert.Equal(5, left.Count);

        // And the survivors are the NEWEST five by sequence, not an arbitrary five.
        Assert.Equal(Enumerable.Range(3, 5).Select(n => (long)n).OrderByDescending(n => n),
                     left.Select(p => p.Sequence));
    }

    // ══ 2. A backwards clock does not reorder (R-rc6-2) ═══════════════════════════════════════════

    /// <summary>
    /// <b>Ordering comes from RC-5's monotonic sequence, never from the commit timestamp.</b>
    ///
    /// <para>The fixture interleaves entries with the clock moving BACKWARDS, so the two orders
    /// disagree completely: sequence 1 carries the newest timestamp and sequence 25 the oldest. The
    /// sweep must remove the ones the SEQUENCE says are oldest. A timestamp-ordered implementation
    /// passes every other gate in this file and fails this one.</para>
    /// </summary>
    [GitFact]
    public void ABackwardsClockDoesNotDecideWhatIsOldest()
    {
        using var ws = Armed();
        var git = ws.Git();

        var start = DateTimeOffset.UtcNow.AddDays(-60);
        for (int i = 0; i < 7; i++)
        {
            ws.SetClock(start.AddDays(-i));      // each entry stamped EARLIER than the one before it
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        var before = RestorePoints.List(git);
        Assert.Equal(7, before.Count);

        // The premise: sequence order and timestamp order really do disagree here.
        Assert.NotEqual(before.OrderByDescending(p => p.Sequence).Select(p => p.Sequence),
                        before.OrderByDescending(p => p.TakenUtc).Select(p => p.Sequence));

        var result = RetentionSweep.Run(
            git, new RetentionPolicy(RetentionDays: 30, MinimumRestorePoints: 5),
            DateTimeOffset.UtcNow);

        Assert.False(result.Plan.Refused);
        Assert.Equal([1L, 2], result.Thinned.Select(t => t.Sequence).OrderBy(n => n));

        var left = RestorePoints.List(git).Select(p => p.Sequence).ToHashSet();
        Assert.DoesNotContain(1L, left);
        Assert.Contains(7L, left);       // the OLDEST by clock, and the newest by sequence: kept
    }

    // ══ 3. A bounded sweep refuses and reports (R-rc6-3) ══════════════════════════════════════════

    /// <summary>
    /// <b>The clock-jump case: no deletion and one message, not a partial deletion.</b>
    ///
    /// <para>A partial deletion under a clock fault destroys real work and still leaves the fault
    /// undiagnosed, which is the worst of the three available outcomes. What the bound converts a
    /// clock fault into is a Messages entry saying something is wrong with the clock — which is both
    /// true and useful.</para>
    /// </summary>
    [GitFact]
    public void ASweepThatWantsTooMuchRemovesNothingAndSaysWhy()
    {
        using var ws = Armed();
        var git = ws.Git();

        for (int i = 0; i < 12; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        // The clock jumped a century forward, so every entry past the floor looks expired at once —
        // ten of the twelve, against an allowance of three.
        var result = RetentionSweep.Run(
            git, new RetentionPolicy(RetentionDays: 30, MinimumRestorePoints: 2),
            DateTimeOffset.UtcNow.AddYears(100));

        Assert.True(result.Plan.Refused);
        Assert.Empty(result.Thinned);
        Assert.Equal(12, RestorePoints.List(git).Count);
        Assert.Empty(ThinningJournal.Read(ws.Root));

        var said = Assert.Single(result.Diagnostics);
        Assert.Equal("revision.retention.refused", said.Id);
        Assert.Contains("clock", said.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was removed", said.Render(), StringComparison.Ordinal);
    }

    // ══ 4. Thinning is not pruning, and the journal is the way back (R-rc6-4) ═════════════════════

    /// <summary>
    /// <b>After a sweep the dropped states are still in the repository, still listed, and restoring
    /// one produces its exact tree.</b>
    ///
    /// <para>Then, in a SCRATCH COPY, an immediate expiry proves the sweep freed anything at all —
    /// the consumer-side half of RC-5 gate 14a. A chained implementation would pass the first half
    /// and fail this one, because with a parent chain nothing a sweep drops is unreachable.</para>
    /// </summary>
    [GitFact]
    public void ThinnedStatesAreStillThereStillListedAndStillRestorable()
    {
        using var ws = Armed();
        var git = ws.Git();

        var longAgo = DateTimeOffset.UtcNow.AddYears(-1);
        for (int i = 0; i < 9; i++)
        {
            ws.SetClock(longAgo.AddMinutes(i));
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        var result = RetentionSweep.Run(
            git, new RetentionPolicy(RetentionDays: 30, MinimumRestorePoints: 6),
            DateTimeOffset.UtcNow);

        Assert.False(result.Plan.Refused);
        Assert.Equal(3, result.Thinned.Count);

        // The objects are still there: a reference went, nothing else did.
        foreach (var entry in result.Thinned)
            Assert.Equal(0, ws.Raw("cat-file", "-e", entry.CommitId).Code);

        // The journal names each one, with the reference it dropped.
        var journal = ThinningJournal.Read(ws.Root);
        Assert.Equal(3, journal.Count);
        Assert.All(journal, e => Assert.StartsWith(CheckpointReferences.Namespace, e.Reference!));

        // And they are LISTED, marked, alongside the live ones.
        var all = RestorePoints.ListIncludingThinned(git);
        Assert.Equal(9, all.Count);
        Assert.Equal(3, all.Count(p => p.Thinned));

        // Restoring one from the journal is one reference update, and produces its exact tree.
        var thinned = all.Single(p => p.Thinned && p.Sequence == 1);
        Assert.True(RestorePoints.RestoreThinned(git, thinned).Ok);

        var back = RestorePoints.List(git).Single(p => p.Sequence == 1);
        Assert.Equal(thinned.TreeId, back.TreeId);
        Assert.False(back.Thinned);
        Assert.DoesNotContain(ThinningJournal.Read(ws.Root), e => e.CommitId == thinned.CommitId);

        // ── The scratch copy: an immediate expiry proves the sweep freed something ────────────────
        string scratch = ws.Root + "-scratch";
        CopyTree(ws.Root, scratch);
        try
        {
            Assert.Equal(0, RawIn(scratch, "-c", "gc.pruneExpire=now", "gc", "--prune=now").Code);

            // Everything still referenced survived, including the one brought back.
            foreach (var live in RestorePoints.List(git))
                Assert.Equal(0, RawIn(scratch, "cat-file", "-e", live.CommitId).Code);

            // And the ones that are still only in the journal did not.
            foreach (var entry in ThinningJournal.Read(ws.Root))
                Assert.NotEqual(0, RawIn(scratch, "cat-file", "-e", entry.CommitId).Code);
        }
        finally { TryDelete(scratch); }
    }

    // ══ 4a. Reclaim honours the journal's ages (R-rc6-4b) ═════════════════════════════════════════

    /// <summary>
    /// <b>Thinned at three different times; reclaimed with an age between them.</b>
    ///
    /// <para>git's own expiry is by OBJECT AGE — when a state was made — and the question here is when
    /// it was THINNED. Without the journal, a two-year-old restore point thinned yesterday would be
    /// destroyed by a one-month reclaim; the fixture is built so that a by-object-age implementation
    /// destroys all three.</para>
    /// </summary>
    [GitFact]
    public void ReclaimActsOnWhenTheyWereThinnedAndRemovesTheEntriesItActedOn()
    {
        using var ws = Armed();
        var git = ws.Git();

        // Every state is the same age — two years — so nothing but the journal can tell them apart.
        var made = DateTimeOffset.UtcNow.AddYears(-2);
        for (int i = 0; i < 3; i++)
        {
            ws.SetClock(made.AddMinutes(i));
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        var points = RestorePoints.List(git).OrderBy(p => p.Sequence).ToList();
        Assert.Equal(3, points.Count);

        // Thinned 200, 100 and 10 days ago respectively.
        var now = DateTimeOffset.UtcNow;
        var thinnedAt = new[] { now.AddDays(-200), now.AddDays(-100), now.AddDays(-10) };

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(0, ws.Raw("update-ref", "-d", points[i].Reference).Code);
            Assert.True(ThinningJournal.Append(ws.Root, new ThinnedState(
                points[i].CommitId, thinnedAt[i], points[i].Reference, points[i].Sequence,
                points[i].Label)));
        }

        // A live, kept entry that must not move.
        ws.SetClock(now);
        ws.Write("cells/a/thing.csch", "current");
        var live = WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "keep me");
        Assert.True(live.Recorded);

        var result = GitReclaim.Reclaim(git, ThinningJournal.Read(ws.Root), TimeSpan.FromDays(150), now);

        Assert.Null(result.Diagnostic);
        Assert.Single(result.Reclaimed);
        Assert.Equal(2, result.Protected.Count);

        // The oldest THINNING is gone from the repository and from the journal.
        Assert.NotEqual(0, ws.Raw("cat-file", "-e", points[0].CommitId).Code);
        Assert.DoesNotContain(ThinningJournal.Read(ws.Root), e => e.CommitId == points[0].CommitId);

        // The two newer thinnings are still listed as thinned and still restorable.
        var stillThinned = ThinningJournal.Read(ws.Root);
        Assert.Equal(2, stillThinned.Count);
        foreach (var entry in stillThinned)
            Assert.Equal(0, ws.Raw("cat-file", "-e", entry.CommitId).Code);

        var listed = RestorePoints.ListIncludingThinned(git);
        Assert.Equal(2, listed.Count(p => p.Thinned));

        // And nothing live or kept moved.
        Assert.Equal(live.Point!.CommitId, RestorePoints.Newest(git)!.CommitId);
    }

    // ══ 5. Human commits are never swept (R-rc6-5) ════════════════════════════════════════════════

    /// <summary>
    /// <b>A commit on the designer's own branch, older than every restore point, survives a sweep
    /// that removes restore points.</b>
    ///
    /// <para>They are small, they are the designer's own record, and no automatic process gets to
    /// delete them. Structurally this holds because retention only ever names a reference
    /// <see cref="CheckpointReferences.List"/> returned — but "it cannot happen by construction" is
    /// what every deletion bug was believed to be before it happened.</para>
    /// </summary>
    [GitFact]
    public void AHumanCommitOlderThanEveryRestorePointIsNeverSwept()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.SetClock(DateTimeOffset.UtcNow.AddYears(-5));
        ws.Write("cells/a/thing.csch", "the designer's own commit");
        Assert.Equal(0, ws.Raw("add", "-A").Code);
        Assert.Equal(0, ws.Raw("commit", "-m", "my own record").Code);
        string human = ws.Raw("rev-parse", "HEAD").Out.Trim();
        string branch = ws.Raw("rev-parse", "--abbrev-ref", "HEAD").Out.Trim();

        var longAgo = DateTimeOffset.UtcNow.AddYears(-1);
        for (int i = 0; i < 8; i++)
        {
            ws.SetClock(longAgo.AddMinutes(i));
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        RetentionSweep.Run(git, new RetentionPolicy(30, 6), DateTimeOffset.UtcNow);

        Assert.Equal(human, ws.Raw("rev-parse", "HEAD").Out.Trim());
        Assert.Equal(human, ws.Raw("rev-parse", branch).Out.Trim());
        Assert.Equal(0, ws.Raw("cat-file", "-e", human).Code);
        Assert.DoesNotContain(ThinningJournal.Read(ws.Root), e => e.CommitId == human);
    }

    // ══ 5a. Kept checkpoints are never swept, and count toward nothing (R-rc6-5a) ═════════════════

    /// <summary>
    /// <b>A save-point, both transition entries and a user-kept entry — each older than every unkept
    /// one — survive a sweep that removes the unkept, and are not counted toward the fraction.</b>
    ///
    /// <para>The "counted toward nothing" half is the one that would be missed: if kept entries
    /// consumed floor slots, a workspace whose newest twenty entries were all save-points would have
    /// no floor left at all, and the sweep would reach entries the count floor exists to protect.</para>
    /// </summary>
    [GitFact]
    public void KeptEntriesSurviveASweepAndAreCountedTowardNothing()
    {
        using var ws = Armed();
        var git = ws.Git();

        // Four kept entries, all older than everything else.
        var ancient = DateTimeOffset.UtcNow.AddYears(-5);
        ws.SetClock(ancient);
        ws.Write("cells/a/thing.csch", "a");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "worth keeping").Recorded);

        ws.SetClock(ancient.AddMinutes(1));
        ws.Write("cells/a/thing.csch", "b");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.RecordingOff, null, forceRecord: true).Recorded);

        ws.SetClock(ancient.AddMinutes(2));
        ws.Write("cells/a/thing.csch", "c");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.RecordingOn, null, forceRecord: true).Recorded);

        ws.SetClock(ancient.AddMinutes(3));
        ws.Write("cells/a/thing.csch", "d");
        var marked = WorkspaceCheckpoints.Take(git, CheckpointOrigin.WorkspaceClosed, null);
        Assert.True(marked.Recorded);
        Assert.True(RestorePoints.MarkKept(git, marked.Point!).Ok);

        // Eight unkept, all expired.
        var longAgo = DateTimeOffset.UtcNow.AddYears(-1);
        for (int i = 0; i < 8; i++)
        {
            ws.SetClock(longAgo.AddMinutes(i));
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        var result = RetentionSweep.Run(git, new RetentionPolicy(30, 6), DateTimeOffset.UtcNow);

        // The floor and the fraction were both computed over the 8 unkept, not over all 12.
        Assert.Equal(8, result.Plan.Unkept);
        Assert.False(result.Plan.Refused);
        Assert.Equal(2, result.Thinned.Count);

        var left = RestorePoints.List(git);
        Assert.Equal(4, left.Count(p => p.Kept));
        Assert.Contains(left, p => p.Origin == CheckpointOrigin.SavePoint);
        Assert.Contains(left, p => p.Origin == CheckpointOrigin.RecordingOff);
        Assert.Contains(left, p => p.Origin == CheckpointOrigin.RecordingOn);
    }

    // ══ 6. All four situations (R-rc6-7) ══════════════════════════════════════════════════════════

    /// <summary>Row 1 — a repository circuitRF created at the workspace root. Normal operation.</summary>
    [GitFact]
    public void ARepositoryCircuitRfCreatedIsNormalOperation()
    {
        using var ws = Armed();

        var situation = EnclosingRepository.Detect(ws.Root);
        Assert.Equal(RepositoryPlacement.Managed, situation.Placement);
        Assert.True(situation.MayRecord);
        Assert.False(situation.IsHeld);
        Assert.False(situation.NeedsAnAnswer);
    }

    /// <summary>Row 2 — a repository the USER created at the workspace root: hold, and ask.</summary>
    [GitFact]
    public void AUserRepositoryAtTheWorkspaceRootHoldsAndIsAskedAbout()
    {
        using var ws = new GitWorkspace();
        using var _  = Identity(ws);

        Assert.Equal(0, ws.Raw("init", "--quiet", ws.Root).Code);

        var situation = EnclosingRepository.Detect(ws.Root);
        Assert.Equal(RepositoryPlacement.UserRepositoryAtRoot, situation.Placement);
        Assert.True(situation.IsHeld);
        Assert.True(situation.NeedsAnAnswer);
        Assert.False(situation.MayRecord);

        // A boundary refuses rather than adopting the repository by default — the answer is theirs.
        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.False(armed.Armed);
        Assert.NotNull(armed.Refusal);

        // And it wrote NOTHING into their repository.
        Assert.False(File.Exists(Path.Combine(ws.Root, WorkspacePolicyFiles.GitIgnoreName)));
        Assert.Empty(CheckpointReferences.List(ws.Git()));
    }

    /// <summary>
    /// Row 3 — the repository root is an ANCESTOR. <b>The case that matters most.</b>
    ///
    /// <para>An automatic recording here would sweep up unrelated work in progress under a circuitRF
    /// message, and circuitRF may not write policy files into somebody else's repository root either.
    /// <b>Nothing committed, and nothing written into the ancestor's root.</b></para>
    /// </summary>
    [GitFact]
    public void AWorkspaceInsideSomebodyElsesRepositoryCommitsNothingAndWritesNothing()
    {
        using var outer = new GitWorkspace(withCws: false);
        using var _     = Identity(outer);

        Assert.Equal(0, outer.Raw("init", "--quiet", outer.Root).Code);
        File.WriteAllText(Path.Combine(outer.Root, "their-work.txt"), "work in progress");

        string inner = Path.Combine(outer.Root, "a-workspace");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".cws"), "{}");
        Directory.CreateDirectory(Path.Combine(inner, "cells"));
        File.WriteAllText(Path.Combine(inner, "cells", ".keep"), "");

        var situation = EnclosingRepository.Detect(inner);
        Assert.Equal(RepositoryPlacement.Ancestor, situation.Placement);
        Assert.True(situation.IsHeld);
        Assert.False(situation.NeedsAnAnswer);   // nothing to offer: it is not circuitRF's to take over

        var armed = WorkspaceArming.Arm(inner, CheckpointOrigin.SavePoint, true, null, true);
        Assert.False(armed.Armed);
        Assert.NotNull(armed.Refusal);

        // NOTHING WAS COMMITTED.
        Assert.NotEqual(0, outer.Raw("rev-parse", "--verify", "--quiet", "HEAD").Code);
        Assert.Equal("", outer.Raw("for-each-ref", CheckpointReferences.Namespace).Out.Trim());

        // NOTHING WAS WRITTEN INTO THE ANCESTOR'S ROOT.
        Assert.False(File.Exists(Path.Combine(outer.Root, WorkspacePolicyFiles.GitIgnoreName)));
        Assert.False(File.Exists(Path.Combine(outer.Root, WorkspacePolicyFiles.GitAttributesName)));

        // And no repository was planted inside their tree either.
        Assert.False(Directory.Exists(Path.Combine(inner, ".git")));

        // The agent surface says HELD rather than on — the reading that would otherwise tell an agent
        // it had a floor under it in the one situation where a checkpoint must never be taken.
        Assert.Equal(RevisionAvailability.Held, AgentContract.StateOf(inner, true, null));
    }

    /// <summary>
    /// Row 4 — a repository NESTED inside the workspace, found by the walk and not by
    /// <c>rev-parse</c>, which goes up and never down.
    ///
    /// <para>The workspace's own history is otherwise normal. The subtree is excluded by pathspec and
    /// reported once, and the checkpoint carries <b>neither its files nor a gitlink</b> — handing such
    /// a directory to <c>git add</c> records an embedded repository with a warning nobody reads.</para>
    /// </summary>
    [GitFact]
    public void ANestedRepositoryIsExcludedReportedOnceAndLeavesNoGitlink()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/borrowed/thing.csch", "a library somebody cloned in here");
        Assert.Equal(0, RawIn(Path.Combine(ws.Root, "cells", "borrowed"), "init", "--quiet", ".").Code);
        ws.Write("cells/mine/thing.csch", "my own cell");

        var situation = EnclosingRepository.Detect(git);
        Assert.Equal(RepositoryPlacement.Managed, situation.Placement);   // NOT a hold
        Assert.True(situation.MayRecord);
        Assert.Contains("cells/borrowed", situation.Nested);

        var outcome = WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "with a nested one");
        Assert.True(outcome.Recorded);

        // Reported ONCE.
        var reported = outcome.Diagnostics
            .Where(d => d.Id == "revision.nested-repository.excluded").ToList();
        Assert.Single(reported);
        Assert.Contains("cells/borrowed", reported[0].Render(), StringComparison.Ordinal);

        // Neither its files nor a gitlink: nothing under that path is in the tree at all.
        string listing = ws.Raw("ls-tree", "-r", outcome.Point!.CommitId).Out;
        Assert.DoesNotContain("cells/borrowed", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("160000", listing, StringComparison.Ordinal);   // the gitlink mode
        Assert.Contains("cells/mine/thing.csch", listing, StringComparison.Ordinal);
    }

    // ══ 7 / 7a / 7b. The three answers (R-rc6-7a) ═════════════════════════════════════════════════

    /// <summary>
    /// <b>Adopt writes the configuration and the policy files, and a <c>pre-commit</c> hook in the
    /// user's repository neither blocks nor fires on the checkpoints that follow.</b>
    ///
    /// <para>The hook rule is not a property of the repository and is therefore not one the user can
    /// keep: an automatic recording must neither fire somebody's tooling nor be blocked by it.</para>
    /// </summary>
    [GitFact]
    public void AdoptWritesTheConfigurationAndThePolicyFilesAndNoHookFires()
    {
        using var ws = UserRepositoryWithAHook();
        var git = ws.Git();
        git.Identity ??= RevisionIdentity.Resolve(git);

        Assert.True(RepositoryAdoption.Apply(git, AdoptionAnswer.Adopt).Ok);

        foreach (var (key, value, _) in GitRepositoryConfig.RowsForThisPlatform())
            Assert.Equal(value, ws.Raw("config", "--local", "--get", key).Out.Trim());

        Assert.True(WorkspacePolicyFiles.HasBlock(Path.Combine(ws.Root, WorkspacePolicyFiles.GitIgnoreName)));
        Assert.True(WorkspacePolicyFiles.HasBlock(Path.Combine(ws.Root, WorkspacePolicyFiles.GitAttributesName)));
        Assert.Equal(RevisionManagement.Adopted, GitRepository.ReadMarker(git));

        ws.Write("cells/a/thing.csch", "after adoption");
        var taken = WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "first");
        Assert.True(taken.Recorded);
        Assert.False(File.Exists(HookEvidence(ws)), "the pre-commit hook fired");
    }

    /// <summary>
    /// <b>All three answers do what they say — and "keep my settings" is the one that must not be
    /// built as "and run my hooks".</b>
    ///
    /// <para><i>keep</i> writes none of §4.5's rows and leaves the user's own configuration exactly as
    /// it was; the only thing it adds is circuitRF's own management marker, which is what stops the
    /// question being asked again. The brief's gate asks for the configuration to be byte-for-byte
    /// unchanged, and the marker is configuration — so what is asserted is the reading that keeps both
    /// requirements meaningful, and the difference is recorded in <c>src/Design/RESOLVED.md</c>.</para>
    /// </summary>
    [GitFact]
    public void AllThreeAnswersDoWhatTheySayAndKeepDoesNotMeanRunMyHooks()
    {
        // ── Adopt ────────────────────────────────────────────────────────────────────────────────
        using (var ws = UserRepositoryWithAHook())
        {
            var git = ws.Git(); git.Identity ??= RevisionIdentity.Resolve(git);
            Assert.True(RepositoryAdoption.Apply(git, AdoptionAnswer.Adopt).Ok);
            Assert.Equal("0", ws.Raw("config", "--local", "--get", "gc.auto").Out.Trim());
        }

        // ── Keep my settings ─────────────────────────────────────────────────────────────────────
        using (var ws = UserRepositoryWithAHook())
        {
            var git = ws.Git(); git.Identity ??= RevisionIdentity.Resolve(git);

            string configPath = Path.Combine(ws.Root, ".git", "config");
            string before     = File.ReadAllText(configPath);

            Assert.True(RepositoryAdoption.Apply(git, AdoptionAnswer.KeepUserSettings).Ok);

            // None of §4.5's rows was written.
            foreach (var (key, _, _) in GitRepositoryConfig.RowsForThisPlatform())
                Assert.NotEqual(0, ws.Raw("config", "--local", "--get", key).Code);

            // And the policy files were not written either.
            Assert.False(File.Exists(Path.Combine(ws.Root, WorkspacePolicyFiles.GitIgnoreName)));
            Assert.False(File.Exists(Path.Combine(ws.Root, WorkspacePolicyFiles.GitAttributesName)));

            // The user's own configuration is unchanged: everything but circuitRF's marker section.
            Assert.Equal(WithoutMarkerSection(before), WithoutMarkerSection(File.ReadAllText(configPath)));
            Assert.Equal(RevisionManagement.KeptUserSettings, GitRepository.ReadMarker(git));

            // THE READING THAT MUST NOT BE BUILT: circuitRF still bypasses hooks under "keep".
            ws.Write("cells/a/thing.csch", "under the user's own settings");
            Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "one").Recorded);
            Assert.False(File.Exists(HookEvidence(ws)), "the pre-commit hook fired under 'keep my settings'");
        }

        // ── Don't keep history ───────────────────────────────────────────────────────────────────
        using (var ws = UserRepositoryWithAHook())
        {
            var git = ws.Git(); git.Identity ??= RevisionIdentity.Resolve(git);
            Assert.True(RepositoryAdoption.Apply(git, AdoptionAnswer.DontKeepHistory).Ok);

            var situation = EnclosingRepository.Detect(ws.Root);
            Assert.Equal(RepositoryPlacement.Declined, situation.Placement);
            Assert.True(situation.IsHeld);
            Assert.False(situation.NeedsAnAnswer);

            Assert.False(WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true).Armed);
            Assert.Empty(CheckpointReferences.List(git));
        }
    }

    /// <summary>
    /// <b>The question is asked once</b> (R-rc3-7b). Answer it, close, reopen — no second prompt, and
    /// the recorded answer is what drives the behaviour.
    /// </summary>
    [GitFact]
    public void TheQuestionIsAskedOnceAndTheRecordedAnswerDrivesTheBehaviour()
    {
        using var ws = UserRepositoryWithAHook();
        var git = ws.Git(); git.Identity ??= RevisionIdentity.Resolve(git);

        Assert.True(EnclosingRepository.Detect(ws.Root).NeedsAnAnswer);
        Assert.True(RepositoryAdoption.Apply(git, AdoptionAnswer.Adopt).Ok);

        // "Close and reopen" is a fresh detection against the same folder — the marker is what carries
        // the answer across, and it lives in the repository's own config rather than in this process.
        var reopened = EnclosingRepository.Detect(ws.Root);
        Assert.False(reopened.NeedsAnAnswer);
        Assert.Equal(RepositoryPlacement.Managed, reopened.Placement);
        Assert.Equal(RevisionManagement.Adopted, reopened.Answer);

        ws.Write("cells/a/thing.csch", "after reopening");
        Assert.True(WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true).Armed);
    }

    // ══ 8. The grace period is measured, not assumed (R-rc6-4) ════════════════════════════════════

    /// <summary>
    /// <b>Thin a state, back-date its now-unreachable objects past two weeks, run RC-3's packing path,
    /// and assert it is still recoverable.</b>
    ///
    /// <para>This is RC-3's back-dated unreachable-object gate seen from the consumer's side, and it is
    /// what makes this brief's central promise true rather than intended. Plain <c>git gc</c> prunes at
    /// <c>gc.pruneExpire</c> — default two weeks — unasked; without RC-3's <c>never</c> row, "retention
    /// thins and never prunes" expires after a fortnight, inside §1.3's own recovery window.</para>
    /// </summary>
    [GitFact]
    public void AThinnedStateSurvivesABackDatedPack()
    {
        using var ws = Armed();
        var git = ws.Git();

        var longAgo = DateTimeOffset.UtcNow.AddYears(-1);
        for (int i = 0; i < 9; i++)
        {
            ws.SetClock(longAgo.AddMinutes(i));
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Close(ws);
        }

        var swept = RetentionSweep.Run(git, new RetentionPolicy(30, 6), DateTimeOffset.UtcNow);
        Assert.NotEmpty(swept.Thinned);

        // The premise RC-3 is supposed to have configured.
        Assert.Equal("never", ws.Raw("config", "--local", "--get", "gc.pruneExpire").Out.Trim());

        // Back-date every loose object well past git's own two-week default.
        BackDateLooseObjects(ws.Root, DateTime.UtcNow.AddDays(-90));

        var (outcome, failure) = GitPacking.Pack(git, thresholdBytes: 0);
        Assert.Null(failure);
        Assert.Equal(PackOutcome.Packed, outcome);

        foreach (var entry in swept.Thinned)
            Assert.Equal(0, ws.Raw("cat-file", "-e", entry.CommitId).Code);

        // And they are still offered, which is the half a designer can actually see.
        Assert.Equal(swept.Thinned.Count, RestorePoints.ListIncludingThinned(git).Count(p => p.Thinned));
    }

    // ══ 9. Held is visible (R-rc6-8) ══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The action is PRESENT and refuses.</b> Assert presence, not absence — this is the inverse of
    /// RC-3's absence gate, and the pair is the point.
    ///
    /// <para>Absent is harmless: a designer with no git never learns the feature exists. Held is a
    /// designer who may believe they are protected, so a hidden button would be indistinguishable
    /// from a feature that was never there.</para>
    /// </summary>
    [GitFact]
    public void InTheHoldStateTheActionStaysVisibleAndRefuses()
    {
        using var ws = new GitWorkspace();
        using var _  = Identity(ws);
        Assert.Equal(0, ws.Raw("init", "--quiet", ws.Root).Code);

        var sink    = new RecordingSink();
        var service = new WorkspaceHistoryService(sink);

        Assert.True(service.RefusedBecauseHeld(ws.Root));
        Assert.Contains(sink.Texts, t => t.Contains("not circuitRF's to write to", StringComparison.Ordinal));

        // The panel keeps its actions and gains a line saying why — it does not empty itself.
        // RE-POINTED at RC-10's merged panel; the assertion is the one it always was.
        var tool = new HistoryTool();
        tool.SetRows(new HistoryList.Result([], 0), hasWorkspace: true,
                     recordingState: HoldMessages.IndicatorDetailFor(RecordingState.Held));

        Assert.True(tool.HasWorkspace);
        Assert.True(tool.IsRecordingBlocked);
        Assert.NotEqual("", tool.RecordingState);

        // And the view does not hide the actions behind an IsVisible binding — which is the shape this
        // gate exists to forbid, and is invisible from the view model alone. The two that CREATE
        // something are header buttons; going back and keeping permanently moved to the row's own menu
        // in RC-10 (R-rc10-17) and are asserted there.
        string view = RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml");
        foreach (string name in (string[])["SavePointButton", "KeepVersionButton"])
        {
            int at = view.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at > 0, $"{name} is not in the view at all");
            int end = view.IndexOf("</Button>", at, StringComparison.Ordinal);
            Assert.DoesNotContain("IsVisible", view[at..end], StringComparison.Ordinal);
        }

        foreach (string header in (string[])
                 ["Compare to Current", "Keep permanently", "Copy the identifier"])
        {
            int at = view.IndexOf($"Header=\"{header}\"", StringComparison.Ordinal);
            Assert.True(at > 0, $"the '{header}' menu item is not in the view at all");
            int end = view.IndexOf("/>", at, StringComparison.Ordinal);
            Assert.DoesNotContain("IsVisible", view[at..end], StringComparison.Ordinal);
        }
    }

    // ══ 10. Cadence (R-rc6-9) ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A designer who switched history off is told nothing about a hold</b> (owner-reported,
    /// 2026-09-17).
    ///
    /// <para>The open report answers <i>why is there no history HERE</i> — a question only somebody who
    /// wants one is asking. With the switch off the hold is not the operative reason and its remedy
    /// (move the workspace somewhere else) would change nothing, so the message named a cause that was
    /// not true of their situation, on every single open, about a feature they had opted out of.</para>
    ///
    /// <para>Same workspace, same hold, both answers — because what has to be pinned is that the switch
    /// is what decides, not that the message can be absent.</para>
    /// </summary>
    [GitFact]
    public void AHeldWorkspaceSaysNothingOnOpenWhenHistoryIsSwitchedOff()
    {
        using var outer = new GitWorkspace(withCws: false);
        using var _     = Identity(outer);
        using var prefs = new AppDataRootScope();
        Assert.Equal(0, outer.Raw("init", "--quiet", outer.Root).Code);

        string inner = Path.Combine(outer.Root, "a-workspace");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".cws"), "{}");

        // Off: nothing at all. A fresh service per open, because the report is once per session.
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.RevisionKeepHistory = false);
        var quiet = new RecordingSink();
        new WorkspaceHistoryService(quiet).ReportStateOnOpen(inner);
        Assert.Empty(quiet.Texts);

        // On: the hold is reported exactly as it was, remedy and all.
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.RevisionKeepHistory = true);
        var told = new RecordingSink();
        new WorkspaceHistoryService(told).ReportStateOnOpen(inner);
        Assert.Contains("not keeping a history", Assert.Single(told.Texts), StringComparison.Ordinal);
    }


    /// <summary>
    /// <b>The one hold that DOES speak while history is switched off</b> — the workspace-root row,
    /// whose explanation was left to a dialog that is not going to be shown (owner-reported,
    /// 2026-09-19).
    ///
    /// <para>The test above pins the rule; this pins its exception, and the pair is what stops either
    /// being "fixed" into the other. The difference is which remedy is operative. An ancestor hold with
    /// the switch off names a cause that is not why nothing is being kept and a remedy — move the
    /// workspace — that would change nothing. This row's remedy is the switch itself: turning history
    /// on is exactly what makes circuitRF ask, so the sentence is true at the moment it is said.</para>
    ///
    /// <para><b>And the indicator is speaking anyway.</b> <see cref="WorkspaceHistoryService.State"/>
    /// tests the hold BEFORE it tests arming, so this workspace reads <i>History held</i> at the foot
    /// of the window whatever the preference says. Silence here did not make the feature invisible; it
    /// made a visible badge unexplained.</para>
    ///
    /// <para><b>A clone is how anyone reaches this state.</b> Git does not clone configuration, so a
    /// copied workspace arrives with no management marker and is held from its first open — which is
    /// exactly the fixture below.</para>
    /// </summary>
    [GitFact]
    public void TheWorkspaceRootHoldSaysSoWhenItsQuestionIsSuppressed()
    {
        using var ws    = new GitWorkspace();
        using var _     = Identity(ws);
        using var prefs = new AppDataRootScope();
        Assert.Equal(0, ws.Raw("init", "--quiet", ws.Root).Code);

        // The fixture IS the row: a repository at the workspace root that circuitRF did not make.
        Assert.True(EnclosingRepository.Detect(ws.Root).NeedsAnAnswer);

        // Off: the question will not be asked, so this line carries the explanation and the remedy.
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.RevisionKeepHistory = false);
        var told = new RecordingSink();
        new WorkspaceHistoryService(told).ReportStateOnOpen(ws.Root);
        string said = Assert.Single(told.Texts);
        Assert.Contains("already keeps a history of its own", said, StringComparison.Ordinal);
        Assert.Contains("Settings", said, StringComparison.Ordinal);

        // On: silent, exactly as before — R-rc6-7a's rule is untouched, because now there IS a dialog.
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.RevisionKeepHistory = true);
        var quiet = new RecordingSink();
        new WorkspaceHistoryService(quiet).ReportStateOnOpen(ws.Root);
        Assert.Empty(quiet.Texts);
    }


    /// <summary>
    /// <b>Turning history on with the workspace already open is the other moment the question becomes
    /// askable</b> — and it is the EDGE that asks, not the level (owner-reported, 2026-09-19).
    ///
    /// <para>The workspace open used to be the only asker, which made a workspace opened with the
    /// switch off impossible to un-hold from inside that session: the switch was thrown, every surface
    /// was re-read faithfully, and they all re-read a hold the preference has nothing to do with.</para>
    ///
    /// <para><b>Both halves have to hold or the remedy becomes the ambush.</b> The Settings broadcast
    /// also fires for retention, for a reclaim and for git becoming available, so asking on a gate that
    /// is merely OPEN would raise a modal on an unrelated settings change — which is the thing the
    /// suppression exists to prevent, arriving by a different door. The truth table is the claim.</para>
    /// </summary>
    [Fact]
    public void TheAdoptionQuestionIsPutOnTheEdgeAndNotOnTheLevel()
    {
        // The gate opening under an unanswered question — the case that was missing.
        Assert.True(RevisionArming.QuestionBecameAskable(armedBefore: false, armedNow: true, needsAnAnswer: true));

        // Already open, and stays open: every later settings change comes through the same broadcast.
        Assert.False(RevisionArming.QuestionBecameAskable(armedBefore: true, armedNow: true, needsAnAnswer: true));

        // The gate closing, and staying closed.
        Assert.False(RevisionArming.QuestionBecameAskable(armedBefore: true,  armedNow: false, needsAnAnswer: true));
        Assert.False(RevisionArming.QuestionBecameAskable(armedBefore: false, armedNow: false, needsAnAnswer: true));

        // Nothing to ask: already answered, or a hold that is not this row. The edge alone never asks.
        Assert.False(RevisionArming.QuestionBecameAskable(armedBefore: false, armedNow: true, needsAnAnswer: false));
    }


    /// <summary>
    /// <b>One message on open; a refusal per attempt with no remedy restated; no per-checkpoint
    /// messages.</b>
    ///
    /// <para>The cadences are the design. A message on every attempt that restated the remedy is how a
    /// message becomes noise a designer learns to scroll past, and a per-checkpoint message would
    /// drown the panel the restore-point list exists to keep readable.</para>
    /// </summary>
    [GitFact]
    public void TheThreeReportsKeepTheirThreeCadences()
    {
        using var outer = new GitWorkspace(withCws: false);
        using var _     = Identity(outer);

        // The hold report is only made where a history is WANTED, so this reads the preference — and
        // must read a default one rather than whatever this machine's owner has chosen.
        using var prefs = new AppDataRootScope();

        Assert.Equal(0, outer.Raw("init", "--quiet", outer.Root).Code);

        string inner = Path.Combine(outer.Root, "a-workspace");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".cws"), "{}");

        var sink    = new RecordingSink();
        var service = new WorkspaceHistoryService(sink);

        // ── On open: ONE message, saying what is not kept, why, and the remedy ───────────────────
        service.ReportStateOnOpen(inner);
        Assert.Single(sink.Texts);
        string opened = sink.Texts[0];
        Assert.Contains("not keeping a history", opened, StringComparison.Ordinal);
        Assert.Contains(outer.Root, opened, StringComparison.Ordinal);
        Assert.Contains("move or copy", opened, StringComparison.OrdinalIgnoreCase);   // the remedy

        // Called again in the same session it says nothing — it is per workspace, not per boundary.
        service.ReportStateOnOpen(inner);
        Assert.Single(sink.Texts);

        // ── On an attempt: a refusal, and the remedy is NOT restated ─────────────────────────────
        sink.Clear();
        Assert.True(service.RefusedBecauseHeld(inner));
        string refusal = Assert.Single(sink.Texts);
        Assert.DoesNotContain("move or copy", refusal, StringComparison.OrdinalIgnoreCase);

        // Per attempt, though — a designer who presses it again is answered again.
        Assert.True(service.RefusedBecauseHeld(inner));
        Assert.Equal(2, sink.Texts.Count);

        // ── The persistent indicator, which is not a message at all ──────────────────────────────
        Assert.Equal(RecordingState.Held, service.State(inner));
        Assert.NotEqual("", HoldMessages.IndicatorFor(RecordingState.Held));

        // ── And automatic checkpoints post nothing on success ────────────────────────────────────
        using var ok = Armed();
        var quiet    = new RecordingSink();
        var recorder = new WorkspaceHistoryService(quiet);
        recorder.NoteWorkspaceWrite();

        for (int i = 0; i < 5; i++)
        {
            ok.Write("cells/a/thing.csch", $"revision {i}");
            Assert.True(recorder.TakeCloseCheckpoint(ok.Root));
        }
        Assert.Empty(quiet.Texts);
    }

    // ══ 10a. A sweep runs on close, and only once (R-rc6-4a) ══════════════════════════════════════

    /// <summary>
    /// <b>Ten checkpoints in one session run no sweep; the close runs exactly one.</b>
    ///
    /// <para>This is what makes the bound a guarantee rather than an arithmetic curiosity: a sweep on
    /// every checkpoint at a quarter empties the set inside a dozen checkpoints, while a sweep once
    /// per session cannot.</para>
    /// </summary>
    [GitFact]
    public void NoSweepRunsUntilTheCloseAndThenExactlyOne()
    {
        using var ws = Armed();
        using var __ = new AppDataRootScope();

        var service = new WorkspaceHistoryService(new RecordingSink());
        service.NoteWorkspaceWrite();

        RetentionSweep.ResetSweepCounter();

        for (int i = 0; i < 10; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Assert.True(service.TakeSavePoint(ws.Root, $"edit {i}"));
        }

        Assert.Equal(0, RetentionSweep.SweepsRun);

        service.CloseHousekeeping(ws.Root);
        Assert.Equal(1, RetentionSweep.SweepsRun);

        // And a second close in the same session runs nothing more.
        service.CloseHousekeeping(ws.Root);
        Assert.Equal(1, RetentionSweep.SweepsRun);
    }

    /// <summary>
    /// <b>A session that recorded nothing sweeps nothing and packs nothing — and the assertion is on
    /// the repository directory's BYTES.</b>
    ///
    /// <para>This is the share case. RC-5's arming guard keeps a colleague's glance from creating a
    /// repository; on its own it did not keep that glance from running a sweep, under the reader's
    /// retention preference, over the owner's restore points.</para>
    /// </summary>
    [GitFact]
    public void ASessionThatRecordedNothingDoesNoHousekeepingAtAll()
    {
        using var ws = Armed();
        using var __ = new AppDataRootScope();

        // Somebody else's session filled it.
        var owner = new WorkspaceHistoryService(new RecordingSink());
        owner.NoteWorkspaceWrite();
        for (int i = 0; i < 5; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            owner.TakeSavePoint(ws.Root, $"edit {i}");
        }

        // A new session that only looks.
        var reader = new WorkspaceHistoryService(new RecordingSink());
        string before = HashTree(Path.Combine(ws.Root, ".git"));

        RetentionSweep.ResetSweepCounter();
        var result = reader.CloseHousekeeping(ws.Root);

        Assert.False(result.Ran);
        Assert.Equal(0, RetentionSweep.SweepsRun);
        Assert.Equal(before, HashTree(Path.Combine(ws.Root, ".git")));
    }

    // ══ 10b / 10c. The off transition and the flag's default (R-rc6-14a, R-rc6-14b) ═══════════════

    /// <summary>
    /// <b>Switch off, and the LAST thing in the history is an entry that contains the <c>.cws</c> with
    /// the flag already off.</b>
    ///
    /// <para>Reversed, the flag is set and nothing records it — which is the defect, and it is
    /// invisible from the flag alone. The pair of transition entries is also what gives an off period
    /// two ends, without which a browser can only render it as an interval in which nothing happened
    /// to be worth keeping.</para>
    /// </summary>
    [GitFact]
    public void TurningRecordingOffWritesTheSettingFirstAndRecordsItLast()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "before");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "before").Recorded);

        var result = RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true);
        Assert.True(result.Ok);
        Assert.NotNull(result.Transition);

        var newest = RestorePoints.Newest(git)!;
        Assert.Equal(CheckpointOrigin.RecordingOff, newest.Origin);
        Assert.True(newest.Kept);        // R-rc6-5a: retention may never erase a gap's end

        // THE ORDERING, asserted on the entry's own CONTENT rather than on the call sequence: the
        // .cws inside it already says the flag is off.
        string recorded  = ws.Raw("show", $"{newest.CommitId}:.cws").Out;
        string flattened = new string([.. recorded.Where(c => !char.IsWhiteSpace(c))]).ToLowerInvariant();
        Assert.Contains("\"revisioncontrol\":false", flattened, StringComparison.Ordinal);

        // And nothing is recorded after it.
        ws.Write("cells/a/thing.csch", "while off");
        var service = new WorkspaceHistoryService(new RecordingSink());
        service.NoteWorkspaceWrite();
        Assert.False(service.TakeCloseCheckpoint(ws.Root));
        Assert.Equal(newest.CommitId, RestorePoints.Newest(git)!.CommitId);
    }

    /// <summary>
    /// <b>A workspace with no recorded setting follows the preference; one that recorded a setting
    /// keeps it when the preference changes. The second half is the one that would be missed.</b>
    ///
    /// <para>A per-workspace decision silently rewritten by a global one is the same class of failure
    /// as §4.4's identity — per-user state deciding something about a shared artifact — and it is why
    /// "absent" and "false" are different states here rather than both being false.</para>
    /// </summary>
    [GitFact]
    public void TheWorkspaceFlagDefaultsToThePreferenceAndIsNotRewrittenByIt()
    {
        using var ws = Armed();
        string cws = WorkspaceRevisionSetting.CwsPathFor(ws.Root);

        // No recorded setting: follows the preference, both ways.
        Assert.Null(WorkspaceRevisionSetting.Read(cws));
        Assert.True(RevisionArming.IsArmed(keepHistoryPreference: true,  workspaceSetting: null));
        Assert.False(RevisionArming.IsArmed(keepHistoryPreference: false, workspaceSetting: null));

        // Recorded: keeps its own answer when the preference changes underneath it.
        Assert.True(RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true).Ok);
        Assert.False(WorkspaceRevisionSetting.Read(cws));
        Assert.False(RevisionArming.IsArmed(keepHistoryPreference: true, workspaceSetting: false));

        // The preference changing does not rewrite the file.
        Assert.False(RevisionArming.IsArmed(keepHistoryPreference: false, workspaceSetting: false));
        Assert.False(WorkspaceRevisionSetting.Read(cws));

        // And a workspace that recorded ON stays on under a preference that is off.
        Assert.True(RevisionSwitch.TurnOn(ws.Root, keepHistoryPreference: false).Ok);
        Assert.True(WorkspaceRevisionSetting.Read(cws));
        Assert.True(RevisionArming.IsArmed(keepHistoryPreference: false, workspaceSetting: true));
    }

    // ══ 11 / 12 / 13. Off writes nothing, on resumes, and the gap is recorded ═════════════════════

    /// <summary>
    /// <b>Switch off, edit, close, reopen — the repository is byte-for-byte unchanged and every prior
    /// restore point is still listed and still restorable.</b>
    ///
    /// <para>An off switch that destroyed a history would be the single most damaging control in the
    /// application: nobody expects a checkbox to be irreversible, and by the time they discover it was,
    /// there is nothing to discover it with.</para>
    /// </summary>
    [GitFact]
    public void OffWritesNothingAndDeletesNothing()
    {
        using var ws = Armed();
        using var __ = new AppDataRootScope();
        var git = ws.Git();

        for (int i = 0; i < 3; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, $"edit {i}").Recorded);
        }
        var beforeOff = RestorePoints.List(git).Select(p => p.CommitId).ToList();

        Assert.True(RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true).Ok);

        // From here on, NOTHING may be written.
        string frozen = HashTree(Path.Combine(ws.Root, ".git"));

        ws.Write("cells/a/thing.csch", "edited while off");
        var service = new WorkspaceHistoryService(new RecordingSink());
        service.NoteWorkspaceWrite();
        Assert.False(service.TakeCloseCheckpoint(ws.Root));
        service.CloseHousekeeping(ws.Root);

        // "Reopen": a fresh session against the same folder.
        var reopened = new WorkspaceHistoryService(new RecordingSink());
        reopened.ReportStateOnOpen(ws.Root);

        Assert.Equal(frozen, HashTree(Path.Combine(ws.Root, ".git")));

        // Every prior restore point is still listed AND still restorable.
        var still = RestorePoints.List(git);
        Assert.All(beforeOff, id => Assert.Contains(id, still.Select(p => p.CommitId)));

        var target = still.Single(p => p.Label == "edit 0");
        Assert.NotEqual("", GitCheckpoint.TreeOf(git, target.Reference) ?? "");
    }

    /// <summary>
    /// <b>Off, then on: the restore points from before the off period are still there and still
    /// ordered correctly — and the pair of transitions makes the stretch a GAP.</b>
    ///
    /// <para>A designer scanning the history later must be able to see that nothing was recorded
    /// between two dates BECAUSE recording was off, not because nothing happened. RC-7 owns the
    /// browser; this asserts the data exists for it to draw.</para>
    /// </summary>
    [GitFact]
    public void OnResumesTheSameHistoryAndTheOffPeriodIsRecordedAsAGap()
    {
        using var ws = Armed();
        var git = ws.Git();

        for (int i = 0; i < 3; i++)
        {
            ws.Write("cells/a/thing.csch", $"revision {i}");
            Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, $"edit {i}").Recorded);
        }
        var beforeOff = RestorePoints.List(git).OrderBy(p => p.Sequence).Select(p => p.CommitId).ToList();

        Assert.True(RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true).Ok);
        ws.Write("cells/a/thing.csch", "changed while nothing was watching");
        Assert.True(RevisionSwitch.TurnOn(ws.Root, keepHistoryPreference: true).Ok);

        ws.Write("cells/a/thing.csch", "and afterwards");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "after").Recorded);

        var all = RestorePoints.List(git).OrderBy(p => p.Sequence).ToList();

        // Everything from before is there, in the same order, at the front.
        Assert.Equal(beforeOff, all.Take(3).Select(p => p.CommitId));

        // ── The gap, with two ends ───────────────────────────────────────────────────────────────
        var gap = Assert.Single(RevisionGaps.Find(all));
        Assert.False(gap.IsOpen);
        Assert.Equal(CheckpointOrigin.RecordingOff, gap.Start.Origin);
        Assert.Equal(CheckpointOrigin.RecordingOn,  gap.End!.Origin);
        Assert.True(gap.Start.Sequence < gap.End.Sequence);

        // Both ends carry the kept mark, or retention could erase the gap and it would render as
        // exactly the quiet interval §5.7 forbids.
        Assert.True(gap.Start.Kept);
        Assert.True(gap.End.Kept);

        // And a still-off workspace reports an OPEN gap rather than none at all.
        Assert.True(RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true).Ok);
        var open = RevisionGaps.Find(RestorePoints.List(git)).Last();
        Assert.True(open.IsOpen);
    }

    // ══ 14 / 14a. Per-workspace state, and it travels ═════════════════════════════════════════════

    /// <summary>
    /// <b>Two workspaces hold independent off state</b> — the mistake this repository has already made
    /// once and recorded, where a per-installation flag was correct for the first workspace and
    /// silently wrong for the second.
    /// </summary>
    [GitFact]
    public void TwoWorkspacesHoldIndependentOffState()
    {
        using var first  = Armed();
        using var second = Armed();

        Assert.True(RevisionSwitch.TurnOff(first.Root, keepHistoryPreference: true).Ok);

        Assert.False(WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(first.Root)));
        Assert.Null(WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(second.Root)));

        // The second is still armed and still records.
        second.Write("cells/a/thing.csch", "still recording here");
        Assert.True(WorkspaceArming.Arm(second.Root, CheckpointOrigin.SavePoint, true, null, true).Armed);

        // The first is not.
        Assert.False(WorkspaceArming.Arm(
            first.Root, CheckpointOrigin.SavePoint, true,
            WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(first.Root)), true).Armed);
    }

    /// <summary>
    /// <b>The flag travels</b> (R-rc6-14c). A copy of a workspace that was switched off arrives
    /// switched off, and the recipient's preference does not override it: the flag says <i>not for this
    /// one</i> about the workspace, and the workspace is what travelled.
    /// </summary>
    [GitFact]
    public void TheOffFlagTravelsWithTheWorkspace()
    {
        using var ws = Armed();
        using var __ = new AppDataRootScope();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "content");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "one").Recorded);
        Assert.True(RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true).Ok);

        // An ARCHIVE: the directory, copied. A CLONE carries only committed content, so the .cws
        // reaches one through a commit — which is exactly how it travels in the field.
        Assert.Equal(0, ws.Raw("add", "-A").Code);
        Assert.Equal(0, ws.Raw("commit", "-m", "the workspace as it stands").Code);

        string archive = ws.Root + "-archive";
        string clone   = ws.Root + "-clone";
        CopyTree(ws.Root, archive);
        Assert.Equal(0, ws.Raw("clone", "--quiet", "--", ws.Root, clone).Code);

        try
        {
            // THE FLAG ITSELF TRAVELS BOTH WAYS, which is the requirement.
            foreach (string arrived in (string[])[archive, clone])
            {
                string cws = WorkspaceRevisionSetting.CwsPathFor(arrived);
                Assert.False(WorkspaceRevisionSetting.Read(cws),
                             $"the flag did not travel to {Path.GetFileName(arrived)}");

                // The recipient's preference is ON and does not override it.
                Assert.False(RevisionArming.IsArmed(true, WorkspaceRevisionSetting.Read(cws)));

                // And nothing is written at their first boundary.
                var service = new WorkspaceHistoryService(new RecordingSink());
                service.NoteWorkspaceWrite();

                int before = CheckpointReferences.List(GitCommand.For(arrived)!).Count;
                Assert.False(service.TakeCloseCheckpoint(arrived));
                Assert.Equal(before, CheckpointReferences.List(GitCommand.For(arrived)!).Count);
            }

            // ── The archive: reported as OFF, with the setting one click away ────────────────────
            var archiveSink = new RecordingSink();
            var archived    = new WorkspaceHistoryService(archiveSink);
            archived.ReportStateOnOpen(archive);

            Assert.Contains(archiveSink.Texts,
                            t => t.Contains("set not to keep a history", StringComparison.Ordinal));
            Assert.Equal(RecordingState.Off, archived.State(archive));

            // ── The clone: reported as HELD, and that is R-rc0-15 working rather than failing ────
            //
            // §4.5's management marker is REPOSITORY CONFIGURATION, and git does not clone a
            // repository's config — which is exactly the property that makes an archive recognised as
            // circuitRF's and a clone not. So a clone of a circuitRF workspace presents as somebody
            // else's repository at the root and is ASKED about, rather than resuming silently. It
            // still records nothing, which is what R-rc6-14c asks for; that it arrives held rather
            // than off is RC-9's to settle, and it is pinned here so the behaviour is visible rather
            // than discovered.
            var cloneSink = new RecordingSink();
            var cloned    = new WorkspaceHistoryService(cloneSink);
            Assert.Equal(RecordingState.Held, cloned.State(clone));
            Assert.True(EnclosingRepository.Detect(clone).NeedsAnAnswer);
        }
        finally { TryDelete(archive); TryDelete(clone); }
    }

    // ══ 15. An AI edit while off refuses first, and nothing is modified (R-rc6-15) ════════════════

    /// <summary>
    /// <b>Refused before anything is modified, and the refusal offers to turn recording back on.</b>
    ///
    /// <para>Switching revision control off switches off the one checkpoint RC-4 makes
    /// non-switchable, because it is §1.2 — the reason the feature exists. That is a legitimate thing
    /// for a user to choose and an illegitimate thing for them to stumble into. <b>The assertion is on
    /// the file mtimes, not just the prompt</b>: a refusal that arrived after the first write would be
    /// a floor announced after the fall.</para>
    /// </summary>
    [GitFact]
    public void AnAiEditWhileOffIsRefusedBeforeAnythingIsModified()
    {
        using var ws = Armed();
        using var __ = new AppDataRootScope();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "content");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "one").Recorded);
        Assert.True(RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true).Ok);

        var mtimes = Directory.GetFiles(ws.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
                                    StringComparison.Ordinal))
            .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.Ordinal);

        int refsBefore = CheckpointReferences.List(git).Count;

        var batch = new BatchSession { KeepHistoryPreference = true };
        var open  = batch.Open(ws.Root, "widen the output match");

        Assert.False(open.Ok);
        Assert.Null(open.Point);
        Assert.NotNull(open.Refusal);
        Assert.Contains("nothing was changed", open.Refusal!.Render(), StringComparison.OrdinalIgnoreCase);

        // NOTHING WAS MODIFIED.
        foreach (var (path, when) in mtimes)
            Assert.Equal(when, File.GetLastWriteTimeUtc(path));
        Assert.Equal(refsBefore, CheckpointReferences.List(git).Count);

        // And the window's report offers the remedy rather than only naming it.
        var sink    = new OfferSink();
        var service = new WorkspaceHistoryService(sink);
        service.ReportAiEditRefusedBecauseOff(ws.Root);

        Assert.Contains("does not keep a history", sink.Texts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing has been changed", sink.Texts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HoldMessages.TurnRecordingOnAction, sink.ActionLabels[0]);

        // The sentence still reads correctly with no button on the end — a headless sink drops it.
        Assert.Contains("Turn history on", sink.Texts[0], StringComparison.OrdinalIgnoreCase);
    }

    // ══ 16. There is no "delete all history" command, at any stage (R-rc6-16) ═════════════════════

    /// <summary>
    /// <b>A source scan, comments stripped.</b>
    ///
    /// <para>A user who genuinely wants the history gone deletes the <c>.git</c> folder — one folder,
    /// plainly named. An irreversible, total, one-click destruction of the thing this feature exists to
    /// protect has no safe place in the UI, for the same reason there is no history-rewriting command.
    /// The scan strips comments first, because this file and several product ones explain at length why
    /// the thing they forbid is forbidden.</para>
    /// </summary>
    [Fact]
    public void NoDeleteAllHistoryCommandExistsAnywhere()
    {
        string[] forbidden =
        [
            "DeleteAllHistory", "DeleteHistory", "EraseHistory", "WipeHistory",
            "RemoveAllRestorePoints", "ClearHistoryCommand", "DestroyHistory",
        ];

        foreach (string relative in EveryRevisionSource())
        {
            string source = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(relative));
            foreach (string token in forbidden)
                Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }

        // And the one destructive operation that DOES exist destroys only what was already thinned,
        // which is the distinction the absence above is meaningless without.
        string reclaim = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Design/Revision/GitReclaim.cs"));
        Assert.Contains("--prune=now", reclaim, StringComparison.Ordinal);
        Assert.Contains("thinnedLongerAgoThan is not { } age", reclaim, StringComparison.Ordinal);
    }

    // ══ Helpers ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>A workspace with an identity, a repository circuitRF manages, and its policy files.</summary>
    private static GitWorkspace Armed()
    {
        var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");

        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.True(armed.Armed);
        return ws;
    }

    /// <summary>
    /// A repository the USER created at the workspace root, carrying settings of their own and a
    /// <c>pre-commit</c> hook that would both fire and refuse.
    ///
    /// <para>The hook writes a file and exits non-zero, so a run of it is detectable two ways — and a
    /// checkpoint that had been blocked would fail rather than silently skip.</para>
    /// </summary>
    private static GitWorkspace UserRepositoryWithAHook()
    {
        var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");

        Assert.Equal(0, ws.Raw("init", "--quiet", ws.Root).Code);

        // A setting of their own, which "keep my settings" must leave exactly where it is.
        Assert.Equal(0, ws.Raw("config", "--local", "core.ignorecase", "false").Code);

        string hooks = Path.Combine(ws.Root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        string hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook,
            "#!/bin/sh\ntouch \"$(dirname \"$0\")/../../hook-fired\"\nexit 1\n");
        MakeExecutable(hook);

        return ws;
    }

    /// <summary>Where <see cref="UserRepositoryWithAHook"/>'s hook leaves its evidence.</summary>
    private static string HookEvidence(GitWorkspace ws) => Path.Combine(ws.Root, "hook-fired");

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var psi = new ProcessStartInfo("/bin/chmod") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("+x");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            p?.WaitForExit(10_000);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
    }

    private static IDisposable Identity(GitWorkspace ws)
    {
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");
        return new AppDataRootScope();
    }

    /// <summary>An unkept entry. <b>A save-point would be kept</b> (R-rc5-1f), so retention's own
    /// fixtures cannot use one.</summary>
    private static void Close(GitWorkspace ws)
        => Assert.True(WorkspaceCheckpoints.Take(ws.Git(), CheckpointOrigin.WorkspaceClosed, null).Recorded);

    /// <summary>
    /// Every source file this brief's absence gate reads. Named rather than globbed, so a file added
    /// to the feature and not to this list is a visible omission rather than a silent one.
    /// </summary>
    private static IEnumerable<string> EveryRevisionSource()
    {
        foreach (string name in (string[])
                 ["RetentionSweep", "RetentionPolicy", "RevisionSwitch", "RevisionGaps",
                  "SessionHousekeeping", "EnclosingRepository", "RepositoryAdoption", "HoldMessages",
                  "ThinningJournal", "GitReclaim", "RestorePoints", "WorkspaceCheckpoints"])
            yield return $"src/Design/Revision/{name}.cs";

        yield return "src/Ui/Revision/WorkspaceHistoryService.cs";
        yield return "src/Ui/ViewModels/WorkspaceViewModel.Revision.cs";
        yield return "src/Ui/ViewModels/Dock/HistoryTool.cs";
        yield return "src/Cli/History.cs";
    }

    /// <summary>
    /// A stable hash of every file under a directory, path and content. <b>What "byte-for-byte
    /// unchanged" is asserted with</b> — a file count would miss a rewritten pack, and a timestamp
    /// comparison would report a read as a change on some filesystems.
    /// </summary>
    private static string HashTree(string root)
    {
        var sha = SHA256.Create();
        var text = new StringBuilder();

        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                                         .OrderBy(f => f, StringComparer.Ordinal))
        {
            text.Append(Path.GetRelativePath(root, file).Replace('\\', '/')).Append(':');
            try { text.Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file)))); }
            catch (IOException) { text.Append("unreadable"); }
            text.Append('\n');
        }

        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Moves every loose object's mtime back, which is what git's own prune expiry reads.
    ///
    /// <para>Not the machine's clock, and not git's date variables: <c>gc.pruneExpire</c> is compared
    /// against the FILE's modification time, so back-dating the files is the only thing that reproduces
    /// what a repository looks like a fortnight later.</para>
    /// </summary>
    private static void BackDateLooseObjects(string workspaceRoot, DateTime when)
    {
        string objects = Path.Combine(workspaceRoot, ".git", "objects");
        if (!Directory.Exists(objects)) return;

        foreach (string file in Directory.GetFiles(objects, "*", SearchOption.AllDirectories))
        {
            try { File.SetLastWriteTimeUtc(file, when); }
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

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The configuration text without circuitRF's own marker section — what "the user's own
    /// settings are unchanged" is compared on.</summary>
    private static string WithoutMarkerSection(string config)
    {
        var kept  = new List<string>();
        bool skip = false;

        foreach (string line in config.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('['))
                skip = trimmed.StartsWith($"[{GitRepositoryConfig.MarkerSection}", StringComparison.OrdinalIgnoreCase);
            if (!skip) kept.Add(line);
        }

        return string.Join('\n', kept).TrimEnd('\n');
    }

    /// <summary>A sink that keeps what it was told, so a CADENCE can be asserted rather than a state.</summary>
    private sealed class RecordingSink : IMessageSink
    {
        public List<string>       Texts  { get; } = [];
        public List<MessageLevel> Levels { get; } = [];

        public void Post(MessageLevel level, string text, string? filePath = null)
        {
            Levels.Add(level);
            Texts.Add(text);
        }

        public void Clear() { Texts.Clear(); Levels.Clear(); }
    }

    /// <summary>The same, plus the ACTION a message carried — R-rc6-15's "offers to turn it back on"
    /// is an offer, not a sentence, and the default sink drops it.</summary>
    private sealed class OfferSink : IMessageSink
    {
        public List<string> Texts        { get; } = [];
        public List<string> ActionLabels { get; } = [];

        public void Post(MessageLevel level, string text, string? filePath = null) => Texts.Add(text);

        public void PostAction(MessageLevel level, string text, string actionLabel,
                               Func<System.Threading.Tasks.Task> action)
        {
            Texts.Add(text);
            ActionLabels.Add(actionLabel);
        }

        public void Clear() { Texts.Clear(); ActionLabels.Clear(); }
    }
}
