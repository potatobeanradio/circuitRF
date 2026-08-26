using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-25 / R-AU-26 / R-AU-27 — unpacking, the naming discipline, and the rules that only bite on a
/// real signed build.
/// </summary>
public sealed class UpdateStagerTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "crf-stage-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly UpdateStager _stager = new(new FakeFreeSpaceProbe(long.MaxValue));

    public UpdateStagerTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string MakeZip(string name, params (string Path, string Content)[] entries)
    {
        string zip = Path.Combine(_tmp, name);
        using var fs = File.Create(zip);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach ((string path, string content) in entries)
        {
            ZipArchiveEntry e = archive.CreateEntry(path);
            using StreamWriter w = new(e.Open());
            w.Write(content);
        }
        return zip;
    }

    [Fact]
    public async Task AZipIsUnpacked_AndOnlyGetsItsRealNameWhenItIsComplete()
    {
        string zip = MakeZip("payload.zip", ("circuitRF.exe", "MZ"), ("data/x.txt", "hello"));
        string dest = Path.Combine(_tmp, "app-2.0.0");

        StageResult r = await _stager.StageArchiveAsync(zip, dest, "circuitRF.exe", CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal(dest, r.StagedPath);
        Assert.True(File.Exists(Path.Combine(dest, "circuitRF.exe")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "data", "x.txt")));

        // The .partial name is gone: it only ever existed while the tree was incomplete.
        Assert.False(Directory.Exists(dest + UpdatePaths.PartialSuffix));
    }

    /// <summary>
    /// An archive with no application in it is wreckage, and wreckage never gets a real name — so
    /// nothing counts it as staged and nothing tries to execute from it.
    /// </summary>
    [Fact]
    public async Task AnArchiveMissingTheExecutable_LeavesNothingStagedAndNoPartialBehind()
    {
        string zip = MakeZip("empty.zip", ("readme.txt", "nothing useful"));
        string dest = Path.Combine(_tmp, "app-2.0.0");

        StageResult r = await _stager.StageArchiveAsync(zip, dest, "circuitRF.exe", CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(StageOutcome.UnpackFailed, r.Outcome);
        Assert.False(Directory.Exists(dest));
        Assert.False(Directory.Exists(dest + UpdatePaths.PartialSuffix));
    }

    [Fact]
    public async Task ACorruptArchive_IsAnUnpackFailure_NotAnException()
    {
        string bad = Path.Combine(_tmp, "corrupt.zip");
        await File.WriteAllTextAsync(bad, "this is not a zip file");

        StageResult r = await _stager.StageArchiveAsync(
            bad, Path.Combine(_tmp, "app-2.0.0"), "circuitRF.exe", CancellationToken.None);

        Assert.Equal(StageOutcome.UnpackFailed, r.Outcome);
    }

    [Fact]
    public async Task StagingOverAPreviousAttempt_ReplacesItCleanly()
    {
        string zip = MakeZip("payload.zip", ("circuitRF.exe", "MZ"));
        string dest = Path.Combine(_tmp, "app-2.0.0");

        // Debris from a killed attempt, under both names.
        Directory.CreateDirectory(dest + UpdatePaths.PartialSuffix);
        await File.WriteAllTextAsync(Path.Combine(dest + UpdatePaths.PartialSuffix, "junk"), "x");
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "stale"), "x");

        StageResult r = await _stager.StageArchiveAsync(zip, dest, "circuitRF.exe", CancellationToken.None);

        Assert.True(r.Ok);
        Assert.False(File.Exists(Path.Combine(dest, "stale")));
        Assert.False(File.Exists(Path.Combine(dest, "junk")));
    }

    [Fact]
    public void DiscardRemovesAStagedTree()
    {
        string dir = Path.Combine(_tmp, "app-3.0.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "f"), "x");

        // The install root has to be named: Discard now refuses anything that is not inside the
        // updater's own tree or an `app-*` child of that root, because its callers take the path
        // from state.json. See UpdateStatePathGuardTests.
        UpdateStager.Discard(dir, _tmp);
        Assert.False(Directory.Exists(dir));

        UpdateStager.Discard(null, _tmp);                        // and null is a no-op, not a crash
        UpdateStager.Discard(Path.Combine(_tmp, "app-gone"), _tmp);  // and so is one that is not there
    }

    /// <summary>
    /// R-AU-26. ZipFile drops Unix mode bits and symlinks; a macOS bundle missing its executable bit
    /// and its Frameworks links has a BROKEN code signature and is refused at launch — the exact
    /// failure this feature exists to prevent, arriving by the least obvious route, and only on a
    /// real signed build so no unit test catches it. The guard is therefore a source scan.
    /// </summary>
    [Fact]
    public void TheMacBundlePath_UsesDittoAndNeverZipFileOrARecursiveCopy()
    {
        string src = UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateStager.cs");
        int i = src.IndexOf("public async Task<StageResult> StageMacBundleAsync", StringComparison.Ordinal);
        int j = src.IndexOf("public async Task<StageResult> StageArchiveAsync", StringComparison.Ordinal);
        Assert.True(i > 0 && j > i);

        string body = UpdateInstallSiteTests.StripComments(src[i..j]);

        Assert.Contains("ditto", src[i..j]);          // named in the RunAsync call
        Assert.DoesNotContain("ZipFile", body);
        Assert.DoesNotContain("File.Copy", body);
        Assert.DoesNotContain("CopyTo", body);
    }

    /// <summary>The image is detached in a finally. A leaked mount is a leaked disk.</summary>
    [Fact]
    public void TheDiskImageIsAlwaysDetached()
    {
        string src = UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateStager.cs");
        int i = src.IndexOf("hdiutil", StringComparison.Ordinal);
        int j = src.IndexOf("private static StageResult Promote", StringComparison.Ordinal);

        string region = src[i..j];
        Assert.Contains("finally", region);
        Assert.Contains("detach", region);

        // The detach runs on CancellationToken.None: a cancelled check must still unmount.
        Assert.Contains("CancellationToken.None", region);
    }

    // ── the real thing, when the artifact is on this machine ────────────────────────────────

    /// <summary>
    /// Stages the ACTUAL shipped disk image, if one is in dist/. Skipped on a clean clone and on any
    /// platform but macOS, because it exercises hdiutil and ditto rather than a mock of them.
    ///
    /// <para>This is the only test that can catch an hdiutil or ditto invocation that is wrong in a
    /// way a fixture would not show — which is the whole reason R-AU-26 exists.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]   // MEASURED at 12.0 s (2026-08-25): a real 160 MB dmg attach,
                                       // a 334 MB ditto and a --deep codesign. Tagged mechanically
                                       // per the root CLAUDE.md's ~5 s rule; it is a CORRECTNESS test
                                       // that happens to be slow, not a timing measurement.
    public async Task StagingTheRealDiskImage_ProducesAVerifiableBundle()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "circuitRF.slnx"))) root = root.Parent;
        if (root is null) return;

        string dist = Path.Combine(root.FullName, "dist");
        if (!Directory.Exists(dist)) return;

        string? dmg = Directory.EnumerateFiles(dist, "circuitRF-*-arm64.dmg").FirstOrDefault()
                   ?? Directory.EnumerateFiles(dist, "circuitRF-*-x64.dmg").FirstOrDefault();
        if (dmg is null) return;

        string dest = Path.Combine(_tmp, "circuitRF.app");
        StageResult r = await _stager.StageMacBundleAsync(dmg, "circuitRF.app", dest, CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.True(File.Exists(Path.Combine(dest, "Contents", "MacOS", "circuitRF")));

        // The seal survives the extraction — which is the property ZipFile would silently destroy.
        ProcessResult verify = await ProcessRunner.RunAsync(
            "codesign", ["--verify", "--strict", "--deep", dest], CancellationToken.None, TimeSpan.FromMinutes(3));
        Assert.True(verify.Ok, verify.StdErr);

        // And the identity check has something to read.
        Assert.NotNull(await PayloadVerifier.TeamIdAsync(dest, CancellationToken.None));

        // No mount was left behind.
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath())
                                       .Select(Path.GetFileName),
                              n => n is not null && n.StartsWith("crf-update-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sha256IsComputedOverTheWholeFile()
    {
        string f = Path.Combine(_tmp, "bytes.bin");
        await File.WriteAllTextAsync(f, "abc");

        // The known SHA-256 of "abc".
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                     await PayloadVerifier.Sha256Async(f, CancellationToken.None));
    }

    [Fact]
    public async Task AnAbsentDigestIsNotARefusal_AndAWrongOneIs()
    {
        string f = Path.Combine(_tmp, "bytes.bin");
        await File.WriteAllTextAsync(f, "abc");

        Assert.True((await PayloadVerifier.VerifyHashAsync(f, null, CancellationToken.None)).Ok);
        Assert.True((await PayloadVerifier.VerifyHashAsync(f, "", CancellationToken.None)).Ok);

        // Case is not significant; content is.
        Assert.True((await PayloadVerifier.VerifyHashAsync(
            f, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", CancellationToken.None)).Ok);

        VerifyResult bad = await PayloadVerifier.VerifyHashAsync(f, "00" + new string('0', 62), CancellationToken.None);
        Assert.Equal(VerifyOutcome.HashMismatch, bad.Outcome);
        Assert.False(bad.Ok);
    }

    /// <summary>
    /// R-AU-25's step three, which is the only one that is a security boundary: the staged bundle's
    /// Team ID must equal the RUNNING application's. Steps one and two only establish that the bytes
    /// are the bytes the host served.
    /// </summary>
    [Fact]
    public async Task AnUnsignedBundleIsRefused()
    {
        if (!OperatingSystem.IsMacOS()) return;

        string bundle = Path.Combine(_tmp, "fake.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        await File.WriteAllTextAsync(Path.Combine(bundle, "Contents", "MacOS", "fake"), "#!/bin/sh\n");

        VerifyResult r = await PayloadVerifier.VerifyMacBundleAsync(bundle, bundle, CancellationToken.None);
        Assert.False(r.Ok);
    }

    // ── the two archives do not have the same shape ──────────────────────────────────────────

    /// <summary>
    /// The bug that broke Linux updates completely and silently (found in review, 2026-08-25).
    ///
    /// <para><c>build-msi.ps1</c> runs <c>Compress-Archive -Path publish\*</c>, so the <c>.zip</c>
    /// holds the publish tree at its ROOT. <c>build-tarball.sh</c> packs
    /// <c>circuitRF-&lt;ver&gt;/</c> holding <c>install.sh</c>, an icon, a <c>current</c> seed AND
    /// <c>app-&lt;ver&gt;/</c> — because that archive is also the first-install payload and its shape
    /// IS the installed shape. A fixed <c>--strip-components</c> count cannot serve both, and getting
    /// it wrong produced <c>UnpackFailed</c> on every Linux machine, forever, with no error anywhere
    /// and no user report, because a user who is not being offered an update has nothing to
    /// notice.</para>
    /// </summary>
    [Theory]
    // The Windows .zip: the tree is the extraction root.
    [InlineData(new[] { "circuitRF.exe" }, "circuitRF.exe")]
    // The Linux .tar.gz: one wrapper, and the tree is its app-<ver> child.
    [InlineData(new[] { "circuitRF-1.0.1/install.sh", "circuitRF-1.0.1/app-1.0.1/circuitRF" }, "circuitRF")]
    // A single wrapping directory, which is what most hand-rolled archives look like.
    [InlineData(new[] { "circuitRF-1.0.1/circuitRF" }, "circuitRF")]
    public void ThePayloadTreeIsLocated_NotAssumedFromAStripCount(string[] entries, string exe)
    {
        string root = Path.Combine(_tmp, "extracted-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (string e in entries)
        {
            string full = Path.Combine(root, e.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }

        string? found = UpdateStager.FindPayloadRoot(root, exe);

        Assert.NotNull(found);
        Assert.True(File.Exists(Path.Combine(found!, exe)));
    }

    [Fact]
    public void AnArchiveWithNoExecutableAtAll_IsRefused()
    {
        string root = Path.Combine(_tmp, "empty-ish");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "README.md"), "x");

        Assert.Null(UpdateStager.FindPayloadRoot(root, "circuitRF"));
    }

    /// <summary>
    /// The whole Linux path, end to end, against a tarball with the shape
    /// <c>build-tarball.sh</c> actually produces. The promoted directory must BE the app tree — the
    /// first-install scaffolding beside it is not something the updater has any use for.
    /// </summary>
    [Fact]
    public async Task ARealShapedTarball_StagesTheAppTreeAndDropsTheInstallScaffolding()
    {
        if (OperatingSystem.IsWindows()) return;   // this is the tar path

        string stage = Path.Combine(_tmp, "stage", "circuitRF-1.0.1");
        Directory.CreateDirectory(Path.Combine(stage, "app-1.0.1"));
        File.WriteAllText(Path.Combine(stage, "app-1.0.1", "circuitRF"), "the app");
        File.WriteAllText(Path.Combine(stage, "app-1.0.1", "circuitRF.dll"), "payload");
        File.WriteAllText(Path.Combine(stage, "install.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(stage, "current"), "app-1.0.1");

        string tgz = Path.Combine(_tmp, "circuitRF-1.0.1-linux-x64.tar.gz");
        ProcessResult packed = await ProcessRunner.RunAsync(
            "tar", ["-C", Path.Combine(_tmp, "stage"), "-czf", tgz, "circuitRF-1.0.1"],
            CancellationToken.None, TimeSpan.FromMinutes(1));
        Assert.True(packed.Ok, packed.StdErr);

        string destination = Path.Combine(_tmp, "install", "app-1.0.1");
        Directory.CreateDirectory(Path.Combine(_tmp, "install"));

        StageResult r = await _stager.StageArchiveAsync(tgz, destination, "circuitRF", CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(destination, r.StagedPath);
        Assert.True(File.Exists(Path.Combine(destination, "circuitRF")));
        Assert.True(File.Exists(Path.Combine(destination, "circuitRF.dll")));
        Assert.False(File.Exists(Path.Combine(destination, "install.sh")));
        Assert.False(Directory.Exists(Path.Combine(destination, "app-1.0.1")));
    }

    // ── verification happens before the promotion, not after ─────────────────────────────────

    /// <summary>
    /// R-AU-27, closing the one place it was relaxed: the signature and publisher checks used to run
    /// AFTER the rename, which left an unverified <c>app-&lt;ver&gt;</c> holding a real name in the
    /// live install root whenever the process died in between.
    /// </summary>
    [Fact]
    public async Task AFailedVerification_NeverLetsTheTreeReachItsRealName()
    {
        string zip = MakeZip("payload.zip", ("circuitRF.exe", "bytes"));
        string destination = Path.Combine(_tmp, "install", "app-9.9.9");
        Directory.CreateDirectory(Path.Combine(_tmp, "install"));

        string? verifiedPath = null;
        StageResult r = await _stager.StageArchiveAsync(
            zip, destination, "circuitRF.exe", CancellationToken.None,
            p =>
            {
                verifiedPath = p;
                return Task.FromResult(new VerifyResult(VerifyOutcome.IdentityMismatch, "not ours"));
            });

        Assert.Equal(StageOutcome.VerificationFailed, r.Outcome);
        Assert.Null(r.StagedPath);

        // What was handed to the verifier still carried the .partial name...
        Assert.NotNull(verifiedPath);
        Assert.Contains(UpdatePaths.PartialSuffix, verifiedPath!);

        // ...and nothing survives under either name.
        Assert.False(Directory.Exists(destination));
        Assert.False(Directory.Exists(UpdateStager.PartialNameFor(destination)));
    }

    /// <summary>
    /// <b>The most destructive line in the stager, and it was reachable.</b>
    /// <c>Promote</c> deletes an existing destination before renaming into it. When that destination
    /// is what <c>current</c> names, a failure or a crash between the delete and the rename leaves the
    /// stub with a pointer to a directory that is not there and an application that will not start.
    ///
    /// <para>It was not hypothetical: a version already swapped in and pending is not recorded in
    /// <c>StagedVersion</c>, so Help ▸ Check for Updates… — which ignores the throttle — re-ran the
    /// whole fetch with <c>destinationDir = &lt;root&gt;/app-&lt;version&gt;</c> straight over the
    /// live tree (found in a second review, 2026-08-25). <c>UpdateService</c> now refuses it earlier
    /// too; this is the guard no future caller can route around.</para>
    /// </summary>
    [Fact]
    public async Task StagingOverTheDirectoryCurrentNames_IsRefused_AndTheLiveTreeSurvives()
    {
        string root = Path.Combine(_tmp, "install");
        string live = Path.Combine(root, "app-2.0.0");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "circuitRF"), "the running application");
        File.WriteAllText(Path.Combine(live, "irreplaceable.dat"), "not in any archive");
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-2.0.0");

        string zip = MakeZip("payload.zip", ("circuitRF", "a different build"));

        StageResult r = await _stager.StageArchiveAsync(zip, live, "circuitRF", CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("current", r.Detail, StringComparison.Ordinal);

        // The live tree is intact, byte for byte, and nothing partial was left beside it.
        Assert.Equal("the running application", File.ReadAllText(Path.Combine(live, "circuitRF")));
        Assert.True(File.Exists(Path.Combine(live, "irreplaceable.dat")));
        Assert.False(Directory.Exists(live + UpdatePaths.PartialSuffix));

        // ...and the pointer still names something that is there, which is the property that matters.
        Assert.Equal("app-2.0.0", UpdateSwap.ReadCurrent(root));
    }

    /// <summary>The same predicate, answering NO for the ordinary case — a NEW version beside the live one.</summary>
    [Fact]
    public void ANewVersionDirectoryBesideTheLiveOne_IsNotTheLivePointerTarget()
    {
        string root = Path.Combine(_tmp, "install2");
        Directory.CreateDirectory(Path.Combine(root, "app-1.0.0"));
        File.WriteAllText(Path.Combine(root, UpdateInstallSite.CurrentPointerName), "app-1.0.0");

        Assert.True(UpdateStager.IsLivePointerTarget(Path.Combine(root, "app-1.0.0")));
        Assert.False(UpdateStager.IsLivePointerTarget(Path.Combine(root, "app-2.0.0")));
    }

    [Fact]
    public async Task APassedVerification_PromotesToTheRealName()
    {
        string zip = MakeZip("good.zip", ("circuitRF.exe", "bytes"));
        string destination = Path.Combine(_tmp, "install2", "app-9.9.9");
        Directory.CreateDirectory(Path.Combine(_tmp, "install2"));

        StageResult r = await _stager.StageArchiveAsync(
            zip, destination, "circuitRF.exe", CancellationToken.None,
            _ => Task.FromResult(new VerifyResult(VerifyOutcome.Ok, "signed")));

        Assert.True(r.Ok, r.Detail);
        Assert.True(File.Exists(Path.Combine(destination, "circuitRF.exe")));
        Assert.False(Directory.Exists(UpdateStager.PartialNameFor(destination)));
    }
}
