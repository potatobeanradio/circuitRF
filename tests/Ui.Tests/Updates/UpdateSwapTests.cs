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
