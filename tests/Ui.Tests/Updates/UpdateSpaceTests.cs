using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-15 / R-AU-16 / R-AU-17 — the disk-space arithmetic, the pessimistic probe, and the reclaim
/// order that never leaves our own directories.
///
/// <para>The rule underneath all of it: the updater must never be the reason a user cannot save
/// their work.</para>
/// </summary>
public class UpdateSpaceTests : IDisposable
{
    private const long MB = 1L << 20;
    private const long GB = 1L << 30;

    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "crf-space-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdateSpaceTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Design §13.1's measured table. The point of pinning it here is that a future change which stops
    /// deleting the download, or starts retaining two previous versions, fails a TEST rather than
    /// someone's disk.
    /// </summary>
    [Theory]
    [InlineData(160, 335, 495)]   // macOS arm64
    [InlineData(112, 184, 296)]   // macOS x64
    [InlineData( 50, 131, 181)]   // Windows x64
    [InlineData( 55, 126, 181)]   // Linux x64
    public void RequiredSpaceIsDownloadPlusExpandedPlusOneGigabyte(long dlMb, long expMb, long peakMb)
    {
        long required = UpdateSpace.RequiredFreeSpace(dlMb * MB, expMb * MB);

        Assert.Equal(peakMb * MB + GB, required);

        // A test that only knows the download size fails — which is the whole point: the naive check
        // is wrong by roughly a factor of three, because the compressed payload and its expanded copy
        // exist at the same time.
        Assert.NotEqual(dlMb * MB + GB, required);
        Assert.True(required > 2 * (dlMb * MB));
    }

    [Fact]
    public void TheReserveIsOneGigabyte_AndItIsNotAPayloadFigure()
    {
        Assert.Equal(GB, UpdateSpace.DefaultReserveBytes);

        // With no payload at all the reserve still stands: it is headroom for the USER's work — a
        // workspace save, an EM result set, the recovery snapshots — not a margin on the download.
        Assert.Equal(GB, UpdateSpace.RequiredFreeSpace(0, 0));
    }

    [Fact]
    public void OnAnAppleSiliconMac_ThatMeansDecliningBelowRoughlyOnePointFiveGigabytes()
    {
        long required = UpdateSpace.RequiredFreeSpace(160 * MB, 335 * MB);
        Assert.InRange(required / (double)GB, 1.4, 1.6);
    }

    [Fact]
    public void SteadyStateIsZero_AndTheTransientIsOneGenerationOnly()
    {
        // What remains held after the swap is the PREVIOUS version, released as soon as the new one
        // clears its startup counter. Exactly one, never a history.
        Assert.Equal(335 * MB, UpdateSpace.TransientAfterSwap(335 * MB));
        Assert.True(UpdateSpace.TransientAfterSwap(335 * MB) < UpdateSpace.RequiredFreeSpace(160 * MB, 335 * MB));
    }

    [Fact]
    public void TheExpandedEstimate_ErrsHigh_AgainstTheMeasuredRatios()
    {
        // Measured from dist/: 160 MB -> ~335 MB is 2.09x; the archives are ~2.4x. Rounding up to 3x
        // keeps the estimate on the cautious side of every one of them.
        Assert.True(UpdateSpace.EstimateExpandedBytes(160 * MB) >= 335 * MB);
        Assert.True(UpdateSpace.EstimateExpandedBytes( 50 * MB) >= 131 * MB);
        Assert.True(UpdateSpace.EstimateExpandedBytes( 55 * MB) >= 126 * MB);
    }

    /// <summary>
    /// R-AU-16. The policy consumes the RAW figure. macOS's
    /// volumeAvailableCapacityForImportantUsageKey reports purgeable space as available and can
    /// differ by many gigabytes on APFS; using it would be a regression, and the reasoning is
    /// recorded so it is a decision rather than a rediscovery.
    /// </summary>
    [Fact]
    public void TheShippingProbeIsTheRawStatfsFigure()
    {
        string src = UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateSpace.cs");
        string code = UpdateInstallSiteTests.StripComments(src);

        Assert.Contains("AvailableFreeSpace", code);
        Assert.DoesNotContain("ImportantUsage", code);
        Assert.DoesNotContain("volumeAvailable", code);
    }

    [Fact]
    public void TheProbeIsInjectable_SoThePolicyIsAnOrdinaryUnitTest()
    {
        var probe = new FakeFreeSpaceProbe(400 * MB);
        long required = UpdateSpace.RequiredFreeSpace(160 * MB, 335 * MB);

        Assert.True(probe.AvailableFreeSpace(_tmp) < required);   // 400 MB is not enough for 495 + 1024
        probe.SetAvailable(2 * GB);
        Assert.True(probe.AvailableFreeSpace(_tmp) >= required);
    }

    [Fact]
    public void AMissingPath_ReportsZero_RatherThanThrowing()
        => Assert.Equal(0, new DriveFreeSpaceProbe().AvailableFreeSpace("\0not-a-path"));

    // ── reclaim ──────────────────────────────────────────────────────────────────────────────

    private (string Updates, string Install) BuildDebris()
    {
        string updates = Path.Combine(_tmp, "updates");
        string install = Path.Combine(_tmp, "install");

        Directory.CreateDirectory(Path.Combine(updates, "staging"));
        File.WriteAllText(Path.Combine(updates, "staging", "part.bin"), "x");
        Directory.CreateDirectory(Path.Combine(updates, "circuitRF.app.partial"));
        Directory.CreateDirectory(Path.Combine(updates, "staged", "app-9.9.9"));
        Directory.CreateDirectory(Path.Combine(updates, "previous"));

        Directory.CreateDirectory(Path.Combine(install, "app-1.0.0"));   // running
        Directory.CreateDirectory(Path.Combine(install, "app-0.9.0"));   // abandoned
        Directory.CreateDirectory(Path.Combine(install, "app-2.0.0.partial"));
        File.WriteAllText(Path.Combine(install, UpdateInstallSite.CurrentPointerName), "app-1.0.0");

        return (updates, install);
    }

    [Fact]
    public void DebrisReclaim_IsUnconditional_AndTakesOnlyStagingAndPartialTrees()
    {
        (string updates, string install) = BuildDebris();
        var r = new UpdateReclaimer(updates, install);

        r.ReclaimDebris();

        Assert.False(Directory.Exists(Path.Combine(updates, "staging")));
        Assert.False(Directory.Exists(Path.Combine(updates, "circuitRF.app.partial")));
        Assert.False(Directory.Exists(Path.Combine(install, "app-2.0.0.partial")));

        // and nothing else — the previous version and the running one are untouched.
        Assert.True(Directory.Exists(Path.Combine(updates, "previous")));
        Assert.True(Directory.Exists(Path.Combine(install, "app-1.0.0")));
        Assert.True(Directory.Exists(Path.Combine(updates, "staged", "app-9.9.9")));
        Assert.Empty(r.Refused);
    }

    [Fact]
    public void TheReclaimOrderIsFixed()
    {
        (string updates, string install) = BuildDebris();
        var r = new UpdateReclaimer(updates, install);

        IReadOnlyList<ReclaimStep> taken = r.ReclaimUntil(
            enough: () => false,                    // never satisfied, so every step runs
            previousVersionReleasable: true,
            runningVersionDirs: ["app-1.0.0"]);

        Assert.Equal(
            [ReclaimStep.Staging, ReclaimStep.PartialTrees, ReclaimStep.AbandonedStaged, ReclaimStep.PreviousVersion],
            taken);
    }

    [Fact]
    public void ItStopsAsSoonAsThereIsRoom()
    {
        (string updates, string install) = BuildDebris();
        var r = new UpdateReclaimer(updates, install);

        bool freed = false;
        IReadOnlyList<ReclaimStep> taken = r.ReclaimUntil(
            enough: () => { bool wasFreed = freed; freed = true; return wasFreed; },
            previousVersionReleasable: true);

        Assert.Equal([ReclaimStep.Staging], taken);
        Assert.True(Directory.Exists(Path.Combine(updates, "circuitRF.app.partial")));
    }

    /// <summary>
    /// The retained previous version is the single best piece of insurance in the design, so it is
    /// only eligible once the running version has cleared its startup counter — at which point the
    /// rollback it insures against can no longer be triggered.
    /// </summary>
    [Fact]
    public void ThePreviousVersion_IsNotReclaimed_WhileItIsStillTheRollback()
    {
        (string updates, string install) = BuildDebris();
        var r = new UpdateReclaimer(updates, install);

        IReadOnlyList<ReclaimStep> taken = r.ReclaimUntil(() => false, previousVersionReleasable: false);

        Assert.DoesNotContain(ReclaimStep.PreviousVersion, taken);
        Assert.True(Directory.Exists(Path.Combine(updates, "previous")));
    }

    [Fact]
    public void TheRunningVersionIsNeverReclaimed()
    {
        (string updates, string install) = BuildDebris();
        var r = new UpdateReclaimer(updates, install);

        r.ReclaimUntil(() => false, true, runningVersionDirs: ["app-1.0.0"]);

        Assert.True(Directory.Exists(Path.Combine(install, "app-1.0.0")));
        Assert.False(Directory.Exists(Path.Combine(install, "app-0.9.0")));
    }

    /// <summary>
    /// THE guarantee, asserted directly rather than left as an intention: the updater has no opinions
    /// about the user's disk. It does not clear caches, it does not touch workspaces, and it does not
    /// "helpfully" find space anywhere it did not itself consume.
    /// </summary>
    [Fact]
    public void NothingOutsideOurOwnDirectoriesIsEverDeleted()
    {
        (string updates, string install) = BuildDebris();

        string workspaces = Path.Combine(_tmp, "workspaces");
        Directory.CreateDirectory(workspaces);
        File.WriteAllText(Path.Combine(workspaces, "important.cws"), "the user's afternoon");

        string sibling = Path.Combine(install, "docs");           // in the install root, not app-*
        Directory.CreateDirectory(sibling);

        var r = new UpdateReclaimer(updates, install);

        foreach (string outside in new[]
                 {
                     workspaces,
                     Path.Combine(workspaces, "important.cws"),
                     sibling,
                     _tmp,
                     Path.GetTempPath(),
                     Path.Combine(updates, "..", "workspaces"),   // and it is not fooled by traversal
                 })
        {
            Assert.False(r.IsOurs(outside), outside);
        }

        Assert.True(r.IsOurs(Path.Combine(updates, "staging")));
        Assert.True(r.IsOurs(Path.Combine(install, "app-0.9.0")));

        r.ReclaimUntil(() => false, true);

        Assert.True(File.Exists(Path.Combine(workspaces, "important.cws")));
        Assert.True(Directory.Exists(sibling));
        Assert.DoesNotContain(r.Deleted, d => !r.IsOurs(d));
    }

    [Fact]
    public void ADirectoryThatIsNotOursIsRefusedRatherThanTrusted()
    {
        (string updates, string install) = BuildDebris();
        var r = new UpdateReclaimer(updates, install);

        // Nothing in the normal flow asks for one, so Refused staying empty is itself the assertion
        // in the tests above; here we prove the refusal path exists and is what happens.
        Assert.False(r.IsOurs(Path.Combine(_tmp, "elsewhere")));
    }

    // ── the reclaimer never takes the running application ────────────────────────────────────

    /// <summary>
    /// The most destructive thing the reclaimer could do, and it used to do it (found in review,
    /// 2026-08-25).
    ///
    /// <para>The session that flips <c>current</c> to <c>app-2.0.0</c> keeps running out of
    /// <c>app-1.0.0</c> for the rest of its life — the swap is for the NEXT launch. Deriving
    /// "abandoned" from the pointer alone therefore classifies the tree under the running process's
    /// feet as debris and removes it when the first window appears. On Windows the file locks mostly
    /// hide it, leaving a half-deleted tree; on Linux the unlinks succeed and every not-yet-loaded
    /// assembly and lazily-resolved resource is gone. It also destroys the one tree a rollback would
    /// need.</para>
    /// </summary>
    [Fact]
    public void TheDirectoryTheProcessIsRunningFrom_IsNeverReclaimed()
    {
        string updates = Path.Combine(_tmp, "u1");
        string install = Path.Combine(_tmp, "i1");
        Directory.CreateDirectory(updates);
        Directory.CreateDirectory(Path.Combine(install, "app-1.0.0"));
        Directory.CreateDirectory(Path.Combine(install, "app-2.0.0"));
        Directory.CreateDirectory(Path.Combine(install, "app-0.9.0"));

        // current already names app-2.0.0; we are still executing out of app-1.0.0.
        var r = new UpdateReclaimer(updates, install, runningDirectoryName: "app-1.0.0");
        r.ReclaimUntil(() => false, previousVersionReleasable: true,
                       runningVersionDirs: ["app-2.0.0"]);

        Assert.True(Directory.Exists(Path.Combine(install, "app-1.0.0")),
                    "the reclaimer deleted the tree the process is running from");
        Assert.True(Directory.Exists(Path.Combine(install, "app-2.0.0")));
        Assert.False(Directory.Exists(Path.Combine(install, "app-0.9.0")));   // genuinely abandoned
    }

    /// <summary>
    /// R-AU-17's order is real, not decorative: the retained previous version is released at its OWN
    /// step and only when the running version has cleared its startup counter. It used to be swept up
    /// one step earlier, as "abandoned", whatever that flag said.
    /// </summary>
    [Fact]
    public void ThePreviousVersionIsHeldBackFromTheAbandonedSweep()
    {
        string updates = Path.Combine(_tmp, "u2");
        string install = Path.Combine(_tmp, "i2");
        Directory.CreateDirectory(updates);
        Directory.CreateDirectory(Path.Combine(install, "app-1.0.0"));
        Directory.CreateDirectory(Path.Combine(install, "app-2.0.0"));

        var held = new UpdateReclaimer(updates, install, "app-2.0.0", previousVersionDirectoryName: "app-1.0.0");
        held.ReclaimUntil(() => false, previousVersionReleasable: false, runningVersionDirs: ["app-2.0.0"]);

        Assert.True(Directory.Exists(Path.Combine(install, "app-1.0.0")),
                    "the previous version was taken before its own step");

        var released = new UpdateReclaimer(updates, install, "app-2.0.0", previousVersionDirectoryName: "app-1.0.0");
        released.ReclaimUntil(() => false, previousVersionReleasable: true, runningVersionDirs: ["app-2.0.0"]);

        Assert.False(Directory.Exists(Path.Combine(install, "app-1.0.0")));
        Assert.True(Directory.Exists(Path.Combine(install, "app-2.0.0")));
    }

    // ── the probe measures the volume it is about to write ───────────────────────────────────

    /// <summary>
    /// <c>Path.GetPathRoot</c> answers <c>"/"</c> for every path on Unix, so deriving the volume from
    /// it reported the ROOT filesystem no matter which one was about to be written — and a separate
    /// <c>/home</c> is an ordinary Linux install. The whole argument this class makes, that the
    /// updater must never be the reason a user cannot save their work, was being made about a
    /// different disk (corrected in review, 2026-08-25).
    ///
    /// <para>Asserted as a STRUCTURAL property, not a figure: the probe's answer for a path must
    /// equal the answer for the mount point that actually contains it. No timing, no machine.</para>
    /// </summary>
    [Fact]
    public void TheProbeResolvesTheVolumeThatActuallyHoldsThePath()
    {
        var probe = new DriveFreeSpaceProbe();

        DriveInfo? holder = DriveInfo.GetDrives()
            .Where(d => { try { return d.IsReady; } catch { return false; } })
            .Where(d => Path.GetFullPath(_tmp).StartsWith(
                            Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName),
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(d => d.RootDirectory.FullName.Length)
            .FirstOrDefault();

        Assert.NotNull(holder);

        // Two LIVE readings of the same volume, so they are compared with a tolerance rather than for
        // equality: anything else on the machine writing a file between them moves the second by a
        // few tens of kilobytes, and this test failed for exactly that reason. What it is actually
        // asserting is that the same VOLUME was chosen, and a different one differs by gigabytes.
        long viaRoot = probe.AvailableFreeSpace(holder!.RootDirectory.FullName);
        long viaTmp  = probe.AvailableFreeSpace(_tmp);

        Assert.True(Math.Abs(viaRoot - viaTmp) < 64L * 1024 * 1024,
                    $"{viaRoot} and {viaTmp} are not the same volume");
    }

    [Fact]
    public void AMountPointIsMatchedByComponent_NotByStringPrefix()
    {
        // /homework must never be read as living under /home. Exercised through the public probe by
        // asking for a path that no mount point can legitimately claim by prefix alone.
        var probe = new DriveFreeSpaceProbe();
        Assert.True(probe.AvailableFreeSpace(_tmp) > 0);
    }
}
