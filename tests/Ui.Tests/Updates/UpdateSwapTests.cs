using System;
using System.IO;
using System.Text;
using CircuitRF.Ui.Updates;
using Xunit;
// Two different AtomicFile types exist — the updater's rename-only writer (this one) and the
// design-layer text writer that moved to CircuitRF.Design.Cells, which src/Ui/GlobalUsings.cs
// now pulls in everywhere. This file means the updater's.
using AtomicFile = CircuitRF.Ui.Updates.AtomicFile;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-31 / R-AU-32 / R-AU-36 — the atomicity properties, tested WITHOUT a full disk.
///
/// <para>Gate item 14, "the bricking case", is the one that matters most: an aborted <c>current</c>
/// write must leave the previous <c>current</c> intact and the installation launching. A truncating
/// write that fails with ENOSPC leaves <c>current</c> EMPTY, the stub with nothing to run, and a full
/// disk turned into an uninstallation — the single most destructive failure available to this
/// design, and it costs nothing to make impossible.</para>
/// </summary>
public class UpdateSwapTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "crf-swap-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdateSwapTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Dir(params string[] parts)
    {
        string p = Path.Combine([_tmp, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    // ── the bricking case ────────────────────────────────────────────────────────────────────

    [Fact]
    public void WritingCurrent_LandsByRename_SoAnAbortedWriteLeavesTheOldPointerIntact()
    {
        string root = Dir("install");
        string pointer = Path.Combine(root, UpdateInstallSite.CurrentPointerName);
        File.WriteAllText(pointer, "app-1.0.0");

        // Abort a write partway: the temp file exists and holds nothing usable, and the rename never
        // happened. This is exactly the ENOSPC shape.
        File.WriteAllText(pointer + ".tmp", "");

        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
        Assert.Equal("app-1.0.0", File.ReadAllText(pointer));
    }

    [Fact]
    public void ThePointerIsNeverOpenedForTruncation()
    {
        string root = Dir("install");
        string pointer = Path.Combine(root, UpdateInstallSite.CurrentPointerName);
        File.WriteAllText(pointer, "app-1.0.0");

        // Hold the pointer open for READING throughout: a truncating writer would have to open it,
        // and on every platform the content is either the old value or the new one, never neither.
        using (FileStream held = File.Open(pointer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            UpdateSwap.WriteCurrent(root, "app-2.0.0");
        }

        Assert.Equal("app-2.0.0", File.ReadAllText(pointer));
        Assert.False(File.Exists(pointer + ".tmp"));
    }

    [Fact]
    public void AFailedTempWrite_LeavesNoDebrisAndDoesNotTouchTheOriginal()
    {
        string root = Dir("install");
        string pointer = Path.Combine(root, UpdateInstallSite.CurrentPointerName);
        File.WriteAllText(pointer, "app-1.0.0");

        // A directory where the temp file wants to be: File.WriteAllText cannot write it, so the
        // write throws BEFORE any rename. The original must be untouched and the debris cleaned.
        Directory.CreateDirectory(pointer + ".tmp");
        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllTextAtomic(pointer, "app-2.0.0"));

        Assert.Equal("app-1.0.0", File.ReadAllText(pointer));
        Directory.Delete(pointer + ".tmp");
    }

    [Fact]
    public void ASymlinkPointer_IsRepointedByRename_NotByDeleteThenCreate()
    {
        if (OperatingSystem.IsWindows()) return;   // symlink creation needs privilege there

        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");

        string pointer = Path.Combine(root, UpdateInstallSite.CurrentPointerName);
        File.CreateSymbolicLink(pointer, "app-1.0.0");
        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));

        UpdateSwap.WriteCurrent(root, "app-2.0.0");

        Assert.Equal("app-2.0.0", UpdateSwap.ReadCurrent(root));
        Assert.True(AtomicFile.IsSymlink(pointer));    // still a symlink, not replaced by a text file
        Assert.False(AtomicFile.ExistsIncludingLink(pointer + ".tmp"));
    }

    // ── .partial discipline ──────────────────────────────────────────────────────────────────

    [Fact]
    public void NothingIncompleteEverHoldsARealName()
    {
        Assert.False(UpdateStager.IsStageable("/x/app-1.0.0" + UpdatePaths.PartialSuffix));
        Assert.False(UpdateStager.IsStageable("/x/circuitRF.app" + UpdatePaths.PartialSuffix));
        Assert.True(UpdateStager.IsStageable("/x/app-1.0.0"));

        Assert.EndsWith(UpdatePaths.PartialSuffix, UpdateStager.PartialNameFor("/x/app-1.0.0"));
    }

    [Fact]
    public void AnAbortedUnpack_LeavesOnlyAPartialTree_AndTheInstallStillLaunches()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0.0");

        // A killed unpack: a partial tree with content in it, and no rename.
        string partial = Dir("install", "app-2.0.0" + UpdatePaths.PartialSuffix);
        File.WriteAllText(Path.Combine(partial, "half-a-file.bin"), "...");

        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
        Assert.False(Directory.Exists(Path.Combine(root, "app-2.0.0")));

        // And the next launch reclaims it, unconditionally.
        string updates = Dir("updates");
        new UpdateReclaimer(updates, root).ReclaimDebris();

        Assert.False(Directory.Exists(partial));
        Assert.True(Directory.Exists(Path.Combine(root, "app-1.0.0")));
        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
    }

    // ── the atomic directory exchange ────────────────────────────────────────────────────────

    /// <summary>
    /// R-AU-32. On macOS this must take the <c>renamex_np(…, RENAME_SWAP)</c> path —
    /// <c>File.Move</c> will not atomically swap two directories, so a fallback taken there is a
    /// finding, not a detail. The test records which happened rather than assuming.
    /// </summary>
    [Fact]
    public void SwapDirectories_ExchangesBothWays()
    {
        string a = Dir("bundleA");
        string b = Dir("bundleB");
        File.WriteAllText(Path.Combine(a, "who.txt"), "A");
        File.WriteAllText(Path.Combine(b, "who.txt"), "B");

        AtomicFile.SwapDirectories(a, b, out bool atomic);

        Assert.Equal("B", File.ReadAllText(Path.Combine(a, "who.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(b, "who.txt")));

        // The atomic form is required on macOS; elsewhere the two-rename fallback is acceptable.
        if (OperatingSystem.IsMacOS())
            Assert.True(atomic, "renamex_np(RENAME_SWAP) did not run on macOS; the P/Invoke is broken.");
    }

    [Fact]
    public void SwapDirectories_LeavesNoAsideDirectoryBehind()
    {
        string a = Dir("one");
        string b = Dir("two");
        AtomicFile.SwapDirectories(a, b, out _);

        foreach (string d in Directory.GetDirectories(_tmp))
            Assert.DoesNotContain(".swapaside-", d);
    }

    // ── the interrupted exchange ─────────────────────────────────────────────────────────────

    /// <summary>Only the exact <c>&lt;original&gt;.swapaside-&lt;8 hex&gt;</c> shape may ever match.</summary>
    [Fact]
    public void SwapAsidesOf_MatchesOnlyItsOwnDebris()
    {
        string app = Dir("circuitRF.app");
        Dir("circuitRF.app.swapaside-0a1b2c3d");        // ours
        Dir("circuitRF.app.swapaside-zzzzzzzz");        // not hex
        Dir("circuitRF.app.swapaside-0a1b2c");          // too short
        Dir("circuitRF.app.backup");                    // not the marker
        Dir("somethingelse.app.swapaside-0a1b2c3d");    // another application's

        string found = Assert.Single(AtomicFile.SwapAsidesOf(app));
        Assert.Equal("circuitRF.app.swapaside-0a1b2c3d", Path.GetFileName(found));
    }

    /// <summary>
    /// A kill between the fallback's second and third rename strands the version the user was running
    /// beside the installed bundle. It is the rollback §14 calls the design's best insurance, so the
    /// next launch COMPLETES the interrupted rename rather than deleting it — otherwise a new version
    /// that then failed to start twice would find nothing to revert to.
    /// </summary>
    [Fact]
    public void AnInterruptedExchange_LeavesTheDisplacedBundleAsTheRollback()
    {
        string app     = Dir("circuitRF.app");
        string aside   = Dir("circuitRF.app.swapaside-0a1b2c3d");
        string updates = Dir("updates");
        File.WriteAllText(Path.Combine(aside, "who.txt"), "the version that was running");

        UpdateSwap.ReclaimSwapAside(Site(app), updates);

        Assert.False(Directory.Exists(aside));
        Assert.Equal("the version that was running",
                     File.ReadAllText(Path.Combine(updates, "previous", "who.txt")));
    }

    /// <summary>With a rollback already retained, the stranded copy is redundant and simply goes.</summary>
    [Fact]
    public void AnInterruptedExchange_WithARollbackAlreadyHeld_JustClearsTheDebris()
    {
        string app     = Dir("circuitRF.app");
        string aside   = Dir("circuitRF.app.swapaside-0a1b2c3d");
        string updates = Dir("updates");
        File.WriteAllText(Path.Combine(Dir("updates", "previous"), "who.txt"), "the retained one");

        UpdateSwap.ReclaimSwapAside(Site(app), updates);

        Assert.False(Directory.Exists(aside));
        Assert.Equal("the retained one", File.ReadAllText(Path.Combine(updates, "previous", "who.txt")));
    }

    /// <summary>
    /// The case worth the guard: a kill between the FIRST and second rename leaves nothing at the
    /// launch path, and the aside is then the user's ONLY copy of the application. It must survive.
    /// </summary>
    [Fact]
    public void WithNothingAtTheLaunchPath_TheAsideIsNeverTouched()
    {
        string app   = Path.Combine(_tmp, "circuitRF.app");   // deliberately NOT created
        string aside = Dir("circuitRF.app.swapaside-0a1b2c3d");

        UpdateSwap.ReclaimSwapAside(Site(app), Dir("updates"));

        Assert.True(Directory.Exists(aside));
    }

    /// <summary>The versioned layout has no bundle to strand, and this must not go looking.</summary>
    [Fact]
    public void TheVersionedLayout_IsLeftAlone()
    {
        string root  = Dir("install");
        string aside = Dir("install.swapaside-0a1b2c3d");

        UpdateSwap.ReclaimSwapAside(
            new InstallSite(root, InstallShape.VersionedPointer, true, root), Dir("updates"));

        Assert.True(Directory.Exists(aside));
    }

    private static InstallSite Site(string bundle)
        => new(bundle, InstallShape.MacOsBundle, IsWritable: true, ProbeDirectory: bundle);

    // ── the versioned layout's half of the same gap ──────────────────────────────────────────

    /// <summary>
    /// Windows and Linux have the same two-operation gap — the pointer write and the record of it are
    /// separate — but a re-flip is idempotent, so nobody is downgraded. What is lost is the ROLLBACK:
    /// the second flip reports the running directory as the previous version, and by then the running
    /// directory is the new one. Revert would restore the failing version to itself and report
    /// success, which is the bug the macOS half of Revert already had once.
    /// </summary>
    [Fact]
    public void KilledAfterThePointerFlip_TheRollbackIsNotMisrecordedAsTheNewVersion()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-2.0.0");

        var site  = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState { StagedVersion = "2.0.0", StagedPath = "app-2.0.0" };

        // The relaunch runs app-2.0.0, because the stub read the pointer the kill left flipped.
        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-2.0.0", "2.0.0",
                                                mutate => mutate(state));

        Assert.Equal(SwapOutcome.SwapAlreadyApplied, r.Outcome);
        Assert.Null(r.PreviousDirectoryName);              // NOT app-2.0.0, which is what it used to say
        Assert.Null(r.NewExecutable);                      // the stub already launched the right tree
        Assert.Equal("app-2.0.0", UpdateSwap.ReadCurrent(root));
    }

    /// <summary>
    /// The half of that test which is easy to get wrong: `current` naming the staged version is ALSO
    /// the ordinary state between a flip and the next launch. That session is still the old tree and
    /// must record itself as the rollback, so the running directory is what separates the two.
    /// </summary>
    [Fact]
    public void TheSessionThatFlipsThePointer_StillRecordsItselfAsTheRollback()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0.0");

        var site  = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState { StagedVersion = "2.0.0", StagedPath = "app-2.0.0" };

        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-1.0.0", "1.0.0",
                                                mutate => mutate(state));

        Assert.Equal(SwapOutcome.PointerFlipped, r.Outcome);
        Assert.Equal("app-1.0.0", r.PreviousDirectoryName);
        Assert.Equal("app-2.0.0", UpdateSwap.ReadCurrent(root));
    }

    // ── the downgrade window ─────────────────────────────────────────────────────────────────
    //
    // The exchange and the state write that records it are two operations. A kill between them left
    // state.json advertising the new version as STAGED while the disk already had it INSTALLED, so
    // the next launch exchanged the pair back, execv'd the OLD version, and then released
    // updates/previous — destroying the update and silently downgrading the user.

    /// <summary>A fabricated macOS install: the bundle, a staged replacement, and the state to match.</summary>
    private (InstallSite Site, UpdateState State, string Updates, string Staged) MacInstall()
    {
        string app     = Dir("circuitRF.app");
        string updates = Dir("updates");
        string staged  = Dir("updates", "staged", "circuitRF.app");

        File.WriteAllText(Path.Combine(app, "who.txt"), "1.0.0-beta.3");
        File.WriteAllText(Path.Combine(staged, "who.txt"), "1.0.0-beta.4");

        return (Site(app), new UpdateState { StagedVersion = "1.0.0-beta.4", StagedPath = staged },
                updates, staged);
    }

    /// <summary>The state file, as a durable side effect the swap can write to mid-operation.</summary>
    private static Action<Action<UpdateState>> PersistInto(UpdateState state)
        => mutate => mutate(state);

    /// <summary>
    /// The marker has to be durable BEFORE anything moves, or the whole scheme is a no-op: it is read
    /// by a launch that only exists because the previous one died partway through.
    /// </summary>
    [Fact]
    public void TheExchange_RecordsThatItIsUnderway_BeforeItTouchesTheDisk()
    {
        (InstallSite site, UpdateState state, string updates, string staged) = MacInstall();

        string? markerWhenTheDiskMoved = null;
        UpdateSwap.ApplyAtLaunch(site, state, updates, "", "1.0.0-beta.3", mutate =>
        {
            mutate(state);
            markerWhenTheDiskMoved ??= state.SwapInProgress;

            // The exchange must not already have happened when the marker was written.
            Assert.Equal("1.0.0-beta.3", File.ReadAllText(Path.Combine(site.Root, "who.txt")));
        });

        Assert.Equal(staged, markerWhenTheDiskMoved);
    }

    /// <summary>
    /// Killed AFTER the exchange: the disk is right and only the bookkeeping is missing. The next
    /// launch must recognise that it is already the new version — not swap the pair back.
    /// </summary>
    [Fact]
    public void KilledAfterTheExchange_TheNextLaunchKeepsTheNewVersion()
    {
        (InstallSite site, UpdateState state, string updates, string staged) = MacInstall();

        // The disk as the kill left it: the exchange done, the retaining move not.
        AtomicFile.SwapDirectories(site.Root, staged, out _);
        state.SwapInProgress = staged;

        // …and the relaunch is the NEW version, because the new bundle is what is at the launch path.
        SwapResult r = UpdateSwap.ApplyAtLaunch(
            site, state, updates, "", "1.0.0-beta.4", PersistInto(state));

        Assert.Equal(SwapOutcome.SwapAlreadyApplied, r.Outcome);
        Assert.Null(r.NewExecutable);   // this process IS the new version; there is nothing to exec

        Assert.Equal("1.0.0-beta.4", File.ReadAllText(Path.Combine(site.Root, "who.txt")));

        // The retaining move the kill interrupted is completed, so the rollback still exists.
        Assert.Equal("1.0.0-beta.3",
                     File.ReadAllText(Path.Combine(updates, "previous", "who.txt")));
    }

    /// <summary>
    /// The regression itself: without the marker this is the launch that exchanged the pair BACK.
    /// Running it twice is what a second kill would do, and the version must not oscillate.
    /// </summary>
    [Fact]
    public void KilledAfterTheExchange_ASecondLaunchDoesNotSwapItBack()
    {
        (InstallSite site, UpdateState state, string updates, string staged) = MacInstall();
        AtomicFile.SwapDirectories(site.Root, staged, out _);
        state.SwapInProgress = staged;

        UpdateSwap.ApplyAtLaunch(site, state, updates, "", "1.0.0-beta.4", PersistInto(state));

        // RecordSwap is UpdateStartup's, so stand in for it: the marker is cleared and nothing is
        // staged any more.
        state.SwapInProgress = null;
        state.StagedVersion  = null;
        state.StagedPath     = null;

        SwapResult again = UpdateSwap.ApplyAtLaunch(
            site, state, updates, "", "1.0.0-beta.4", PersistInto(state));

        Assert.Equal(SwapOutcome.Nothing, again.Outcome);
        Assert.Equal("1.0.0-beta.4", File.ReadAllText(Path.Combine(site.Root, "who.txt")));
    }

    /// <summary>
    /// Killed BEFORE the exchange — the common case, since it covers every ordinary failure as well
    /// as a kill. Nothing moved, so the update must simply be retried rather than abandoned.
    /// </summary>
    [Fact]
    public void KilledBeforeTheExchange_TheUpdateIsRetried()
    {
        (InstallSite site, UpdateState state, string updates, string staged) = MacInstall();
        state.SwapInProgress = staged;   // written; the exchange never followed

        SwapResult r = UpdateSwap.ApplyAtLaunch(
            site, state, updates, "", "1.0.0-beta.3", PersistInto(state));

        Assert.Equal(SwapOutcome.BundleSwapped, r.Outcome);
        Assert.Equal("1.0.0-beta.4", File.ReadAllText(Path.Combine(site.Root, "who.txt")));
        Assert.Equal("1.0.0-beta.3", File.ReadAllText(Path.Combine(updates, "previous", "who.txt")));
    }

    /// <summary>
    /// `AppVersion.Display` normalises a `1.0` tag to `1.0.0` while `staged_version` is the tag the
    /// release carried. Compared as text those are different versions, and the resolution would then
    /// swap a correctly-installed update straight back out.
    /// </summary>
    [Fact]
    public void TheTwoSpellingsOfOneVersion_AreTheSameVersion()
    {
        (InstallSite site, UpdateState state, string updates, string staged) = MacInstall();
        state.StagedVersion = "1.0";
        AtomicFile.SwapDirectories(site.Root, staged, out _);
        state.SwapInProgress = staged;

        SwapResult r = UpdateSwap.ApplyAtLaunch(
            site, state, updates, "", "1.0.0", PersistInto(state));

        Assert.Equal(SwapOutcome.SwapAlreadyApplied, r.Outcome);
    }

    // ── rollback ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoFailedStartups_RevertToThePreviousVersion()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-2.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState
        {
            PendingVersion  = "2.0.0",
            PendingPath     = "app-2.0.0",
            PreviousVersion = "1.0.0",
            PreviousPath    = "app-1.0.0",
            LaunchAttempts  = UpdateSwap.MaxFailedStartups,
        };

        // The launch under test IS app-2.0.0's own — which is what makes the counter mean anything.
        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-2.0.0");

        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
    }

    /// <summary>
    /// The bug this whole distinction exists for (found in review, 2026-08-25).
    ///
    /// <para>The pointer flip is performed by the OLD version, for the NEXT launch — the stub read
    /// <c>current</c> before this process started. So the flipping session keeps running app-1.0.0,
    /// and it must NOT be counted as app-2.0.0's attempt to start. Counting it there is what used to
    /// raise the counter and then clear it from the same session, leaving the new version's real
    /// first launch carrying no counter at all: a release that crashed before its first window could
    /// never reach <see cref="UpdateSwap.MaxFailedStartups"/>, and rollback was inert on every
    /// platform.</para>
    /// </summary>
    [Fact]
    public void TheSessionThatFlipsThePointer_IsNotTheNewVersionsStartupAttempt()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState
        {
            PendingVersion = "2.0.0",
            PendingPath    = "app-2.0.0",
            LaunchAttempts = UpdateSwap.MaxFailedStartups,
        };

        // Running out of app-1.0.0 while app-2.0.0 is pending: not our attempt, whatever the count.
        Assert.False(UpdateSwap.LaunchBelongsToPending(site, state, "app-1.0.0"));
        Assert.True(UpdateSwap.LaunchBelongsToPending(site, state, "app-2.0.0"));

        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-1.0.0");

        // No revert, because this session proves nothing about the pending version either way.
        Assert.NotEqual(SwapOutcome.RolledBack, r.Outcome);
        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
    }

    /// <summary>
    /// The pending version's own launch raises the counter and moves nothing. This is the outcome
    /// that has to exist for a crash before the first window to be counted at all.
    /// </summary>
    [Fact]
    public void ThePendingVersionsOwnLaunch_RecordsAnAttemptAndMovesNothing()
    {
        string root = Dir("install");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-2.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState
        {
            PendingVersion = "2.0.0", PendingPath = "app-2.0.0", LaunchAttempts = 1,
        };

        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-2.0.0");

        Assert.Equal(SwapOutcome.AttemptRecorded, r.Outcome);
        Assert.Equal("app-2.0.0", UpdateSwap.ReadCurrent(root));
    }

    /// <summary>
    /// A pointer flip reports the directory it was running out of, because that — not the version
    /// STRING — is what a rollback has to go back to. <c>AppVersion.Display</c> normalises a
    /// <c>1.0</c> tag to <c>1.0.0</c> while the directory is named after the tag.
    /// </summary>
    [Fact]
    public void APointerFlip_ReportsTheOutgoingDirectory()
    {
        string root = Dir("install");
        Dir("install", "app-1.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState { StagedVersion = "2.0.0", StagedPath = "app-2.0.0" };

        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-1.0");

        Assert.Equal(SwapOutcome.PointerFlipped, r.Outcome);
        Assert.Equal("app-1.0", r.PreviousDirectoryName);
    }

    /// <summary>
    /// <b>The bug that made a Windows update take two launches</b> (owner-reported, 2026-09-04).
    ///
    /// <para>The stub resolves <c>current</c> and starts the app; the flip is then made BY that app,
    /// one step too late for its own launch. So a flip that hands nothing back leaves the user
    /// running the old version after doing exactly what the Message Panel asked, and needing a second
    /// launch to see the update. macOS never showed it because a bundle swap execs.</para>
    ///
    /// <para>The executable handed back is the one the stub itself would have built from the flipped
    /// pointer, which is what makes the hand-over produce the same process a launch earlier rather
    /// than a different one.</para>
    /// </summary>
    [Fact]
    public void APointerFlip_HandsBackTheExecutableToBecome()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState { StagedVersion = "2.0.0", StagedPath = "app-2.0.0" };

        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-1.0.0");

        Assert.Equal(SwapOutcome.PointerFlipped, r.Outcome);
        Assert.NotNull(r.NewExecutable);

        // Built the way the stub builds it: <root>\<what current now names>\<app>.exe.
        string expected = Path.Combine(root, UpdateSwap.ReadCurrent(root)!,
                                       OperatingSystem.IsWindows() ? UpdateApp.Name + ".exe" : UpdateApp.Name);
        Assert.Equal(expected, r.NewExecutable);
        Assert.Equal(UpdateSwap.VersionedExecutable(root, "app-2.0.0"), r.NewExecutable);

        // And it is the NEW version's, not this session's — the whole point.
        Assert.DoesNotContain("app-1.0.0", r.NewExecutable!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same asymmetry on the rollback path: flipping <c>current</c> back leaves THIS process
    /// running the version that does not work, so the restored executable is handed back for the
    /// caller to become — exactly what the macOS half has always done.
    /// </summary>
    [Fact]
    public void ARollbackInThePointerLayout_HandsBackTheRestoredExecutable()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-2.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState
        {
            PendingVersion  = "2.0.0",
            PendingPath     = "app-2.0.0",
            PreviousVersion = "1.0.0",
            PreviousPath    = "app-1.0.0",
            LaunchAttempts  = UpdateSwap.MaxFailedStartups,
        };

        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-2.0.0");

        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
        Assert.Equal(UpdateSwap.VersionedExecutable(root, "app-1.0.0"), r.NewExecutable);
    }

    /// <summary>
    /// The wiring half, because the result carrying an executable is inert unless the caller acts on
    /// it — and "the pointer flip needs no re-exec" is precisely the belief that has to stay dead.
    /// </summary>
    [Fact]
    public void TheFlippingLaunch_HandsOverRatherThanCarryingOnAsTheOldVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string startup = File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "Ui", "Updates", "UpdateStartup.cs"));

        int from = startup.IndexOf("case SwapOutcome.PointerFlipped:", StringComparison.Ordinal);
        int to   = startup.IndexOf("case SwapOutcome.BundleSwapped:", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from, "the PointerFlipped case is no longer where this test looks");

        string body = startup[from..to];
        Assert.Contains("HandOverTo(result.NewExecutable", body);

        // Windows has no execv, so the hand-over cannot be an exec alone.
        Assert.Contains("Process.Start", startup);
        Assert.Contains("Environment.Exit(0)", startup);
    }

    [Fact]
    public void OneFailedStartup_DoesNotRevert()
    {
        string root = Dir("install");
        Dir("install", "app-1.0.0");
        Dir("install", "app-2.0.0");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-2.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        var state = new UpdateState
        {
            PendingVersion = "2.0.0", PendingPath = "app-2.0.0",
            PreviousVersion = "1.0.0", PreviousPath = "app-1.0.0", LaunchAttempts = 1,
        };

        SwapResult r = UpdateSwap.ApplyAtLaunch(site, state, Dir("updates"), "app-2.0.0");

        Assert.Equal(SwapOutcome.AttemptRecorded, r.Outcome);
        Assert.Equal("app-2.0.0", UpdateSwap.ReadCurrent(root));
    }

    [Fact]
    public void NothingStaged_IsTheCommonCase_AndCostsOneCheck()
    {
        string root = Dir("install");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        SwapResult r = UpdateSwap.ApplyAtLaunch(site, new UpdateState(), Dir("updates"));

        Assert.Equal(SwapOutcome.Nothing, r.Outcome);
        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
    }

    [Fact]
    public void AFlatInstall_IsNeverSwapped()
    {
        string root = Dir("opt-install");
        var site = new InstallSite(root, InstallShape.Flat, true, root);
        var state = new UpdateState { StagedVersion = "2.0.0", StagedPath = "app-2.0.0" };

        Assert.Equal(SwapOutcome.Failed, UpdateSwap.ApplyAtLaunch(site, state, Dir("updates")).Outcome);
    }

    /// <summary>Self-heal: debris from a killed update is reclaimed at the next launch, before
    /// anything else happens — which is what makes a disk-full event self-limiting.</summary>
    [Fact]
    public void DebrisIsReclaimedAtLaunch_BeforeAnythingElse()
    {
        string root = Dir("install");
        string updates = Dir("updates");
        Dir("updates", "staging");
        Dir("install", "app-3.0.0" + UpdatePaths.PartialSuffix);
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0.0");
        Dir("install", "app-1.0.0");

        var site = new InstallSite(root, InstallShape.VersionedPointer, true, root);
        UpdateSwap.ApplyAtLaunch(site, new UpdateState(), updates);

        Assert.False(Directory.Exists(Path.Combine(updates, "staging")));
        Assert.False(Directory.Exists(Path.Combine(root, "app-3.0.0" + UpdatePaths.PartialSuffix)));
    }

    [Fact]
    public void ReadCurrent_ToleratesTrailingWhitespaceAndReportsNothingForAnEmptyPointer()
    {
        string root = Dir("install");
        string pointer = Path.Combine(root, UpdateInstallSite.CurrentPointerName);

        File.WriteAllText(pointer, "app-1.0.0\n");
        Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));

        File.WriteAllText(pointer, "", Encoding.ASCII);
        Assert.Null(UpdateSwap.ReadCurrent(root));
    }
}
