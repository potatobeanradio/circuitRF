using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// The refusals added by the security review of 2026-08-25 (design §9.1).
///
/// <para>Each one closes a route by which something the FEED chose — a release tag, an asset URL, an
/// archive member, a line in <c>state.json</c> — becomes a path the updater writes to or a program it
/// runs. None of them was reachable end to end when it was written; they exist because the guard that
/// happened to stand in the way was, in every case, a single one somewhere else in the pipeline.</para>
/// </summary>
public sealed class UpdateSecurityHardeningTests
{
    // ── a release tag cannot become a path ──────────────────────────────────────────────────

    /// <summary>
    /// <c>ReleaseInfo.VersionText</c> is the TAG's own spelling and it becomes a path segment —
    /// <c>&lt;install root&gt;/app-&lt;ver&gt;</c> and <c>updates/staged/&lt;ver&gt;/</c>. SemVer's own
    /// identifier rule (<c>[0-9A-Za-z-]</c>) is what makes that safe by construction rather than by a
    /// guard somewhere downstream.
    /// </summary>
    [Theory]
    [InlineData("1.0.0+../../evil")]        // build metadata was taken verbatim
    [InlineData("1.0.0-x/y")]
    [InlineData("1.0.0-x\\y")]
    [InlineData("1.0.0-x:y")]               // an NTFS alternate data stream
    [InlineData("1.0.0-beta.1+a/b")]
    [InlineData("1.0.0+")]
    public void ATagWithAPathSeparatorInIt_IsNotAVersion(string tag)
        => Assert.False(SemanticVersion.TryParse(tag, out _));

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("v1.0.0-beta.10")]
    [InlineData("1.0.0-rc.1+abc123")]
    [InlineData("1.0")]
    public void TheTagsWeActuallyPublish_StillParse(string tag)
        => Assert.True(SemanticVersion.TryParse(tag, out _));

    [Fact]
    public void AnAbsurdlyLongTag_IsRefusedRatherThanMeasured()
        => Assert.False(SemanticVersion.TryParse("1.0.0-" + new string('a', SemanticVersion.MaxLength), out _));

    /// <summary>Every version this can produce is safe as the tail of an <c>app-*</c> directory name.</summary>
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-beta.10")]
    [InlineData("1.0.0-rc.1+abc123")]
    public void AParsedVersion_IsAlwaysASafeDirectoryName(string tag)
        => Assert.True(UpdateInstallSite.IsSafeVersionDirectoryName(
                           UpdateInstallSite.VersionDirPrefix + tag));

    // ── state.json cannot name anything outside the install root ────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app-1.0.0/../../evil")]
    [InlineData("app-..")]
    [InlineData("../app-1.0.0")]
    [InlineData("/etc")]
    [InlineData("1.0.0")]                   // no app- prefix: not ours
    [InlineData("app-1.0.0\\x")]
    public void AVersionDirectoryNameThatIsNotOne_IsRefused(string? name)
        => Assert.False(UpdateInstallSite.IsSafeVersionDirectoryName(name));

    /// <summary>
    /// <c>current</c> is the launch pointer, and the name written into it comes through
    /// <c>state.json</c>. The check is at the write, not only at the callers, so no future caller can
    /// route around it.
    /// </summary>
    [Fact]
    public void CurrentCannotBeWrittenWithSomethingThatIsNotAVersionDirectory()
    {
        string root = Temp();
        try
        {
            Assert.Throws<ArgumentException>(() => UpdateSwap.WriteCurrent(root, "../.."));
            Assert.False(File.Exists(Path.Combine(root, UpdateInstallSite.CurrentPointerName)));

            UpdateSwap.WriteCurrent(root, "app-1.0.0");
            Assert.Equal("app-1.0.0", UpdateSwap.ReadCurrent(root));
        }
        finally { Delete(root); }
    }

    // ── an archive cannot write outside its own tree ────────────────────────────────────────

    /// <summary>
    /// A tar member NAMED with <c>..</c> is refused by tar itself, but a symlink whose TARGET escapes
    /// is an ordinary valid member — and the tree it lands in is about to be renamed into the live
    /// install root and executed from.
    /// </summary>
    [Fact]
    public void ATreeHoldingALinkOutOfItself_IsReported()
    {
        if (OperatingSystem.IsWindows()) return;   // creating one needs a privilege there

        string root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app-1.0.0"));
            File.CreateSymbolicLink(Path.Combine(root, "app-1.0.0", "escape"), "../../../../etc/passwd");

            string? found = UpdateStager.FirstEscapingLink(root);
            Assert.NotNull(found);
            Assert.EndsWith("escape", found);
        }
        finally { Delete(root); }
    }

    /// <summary>The one link the Linux layout legitimately holds stays inside and is not reported.</summary>
    [Fact]
    public void ALinkThatStaysInsideTheTree_IsFine()
    {
        if (OperatingSystem.IsWindows()) return;

        string root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app-1.0.0"));
            File.CreateSymbolicLink(Path.Combine(root, "current"), "app-1.0.0");

            Assert.Null(UpdateStager.FirstEscapingLink(root));
        }
        finally { Delete(root); }
    }

    // ── the helper tools are not resolved through PATH ──────────────────────────────────────

    /// <summary>
    /// A bare name is resolved through <c>PATH</c>, and the Linux user-local install puts its own
    /// launcher in <c>~/.local/bin</c> — which most distributions place ahead of <c>/usr/bin</c>. A
    /// <c>tar</c> dropped there would be handed an archive of our choosing and a destination inside
    /// the install tree; a <c>codesign</c> dropped there would BE the verification step.
    /// </summary>
    [Fact]
    public void EveryPermittedToolResolvesToAnAbsolutePathWhenItIsInstalled()
    {
        foreach (string tool in ProcessRunner.Allowed)
        {
            string resolved = ProcessRunner.Resolve(tool);

            // On a platform that does not ship the tool at all there is nothing to resolve to, and a
            // bare name there is a degradation (no update) rather than a hole.
            if (resolved == tool) continue;

            Assert.True(Path.IsPathRooted(resolved), $"{tool} resolved to '{resolved}'");
            Assert.True(File.Exists(resolved));
        }
    }

    [Fact]
    public void TheToolsThisPlatformShips_AreResolvedAbsolutely()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("/usr/bin/tar", ProcessRunner.Resolve("tar"));
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("/usr/bin/codesign", ProcessRunner.Resolve("codesign"));
            Assert.Equal("/usr/bin/hdiutil",  ProcessRunner.Resolve("hdiutil"));
            Assert.Equal("/usr/bin/ditto",    ProcessRunner.Resolve("ditto"));
        }
    }

    // ── the payload archive is never mounted or unpacked before its container is checked ────

    /// <summary>
    /// <c>hdiutil attach</c> hands attacker-supplied bytes to a kernel filesystem parser. The image's
    /// own Developer ID signature is checked first — a source-level assertion, because the behaviour
    /// itself needs a signed image and a real Mac.
    /// </summary>
    [Fact]
    public void TheDiskImageIsVerifiedBeforeItIsMounted()
    {
        string service = UpdateInstallSiteTests.StripComments(
            UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateService.cs"));

        int verify = service.IndexOf("VerifyMacImageAsync", StringComparison.Ordinal);
        int stage  = service.IndexOf("StageMacBundleAsync", StringComparison.Ordinal);

        Assert.True(verify >= 0, "the disk image is not verified at all");
        Assert.True(stage  >= 0);
        Assert.True(verify < stage, "the image is mounted before it is verified");
    }

    /// <summary>Mounting neither honours what the image asks to open nor the ownership it records.</summary>
    [Fact]
    public void TheDiskImageIsMountedInert()
    {
        // The RAW source: StripComments removes string literals too, and these ARE string literals.
        string stager = UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateStager.cs");

        foreach (string flag in new[] { "-nobrowse", "-readonly", "-noautoopen", "-owners" })
            Assert.Contains(flag, stager);
    }

    // ── a transient failure is not permanent ────────────────────────────────────────────────

    /// <summary>
    /// The blacklist is permanent AND shared — <c>AppDataRoot</c> is one directory for all three
    /// applications and every build of them. An unsigned payload earns that; a <c>tar</c> that was
    /// not on the box does not, and used to.
    /// </summary>
    [Fact]
    public void OnlyAVerificationFailureIsBlacklisted()
    {
        string service = UpdateInstallSiteTests.StripComments(
            UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateService.cs"));

        Assert.Contains("if (verificationFailure) UpdateStateIo.Update(s => s.Blacklist_Add(version));", service);
        Assert.Contains("verificationFailure: staged.Outcome == StageOutcome.VerificationFailed", service);
    }

    // ── the Windows check covers what runs, not the first file ──────────────────────────────

    /// <summary>
    /// A payload can carry a genuine, correctly-signed <c>circuitRF.exe</c> copied verbatim from a
    /// real release beside anything at all. Verification has to cover the set of PEs, not the first.
    /// </summary>
    [Fact]
    public void TheWindowsCheckIsAgainstTheTreeAndNotOneFile()
    {
        string service = UpdateInstallSiteTests.StripComments(
            UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateService.cs"));

        Assert.Contains("VerifyWindowsTree", service);
        Assert.DoesNotContain("VerifyWindowsExecutable", service);
    }

    [Fact]
    public void AStagedTreeWithNoApplicationExecutableInIt_IsRefused()
    {
        string root = Temp();
        try
        {
            VerifyResult r = PayloadVerifier.VerifyWindowsTree(root, "running.exe", "circuitRF.exe");
            Assert.False(r.Ok);
            Assert.Equal(VerifyOutcome.SignatureInvalid, r.Outcome);
        }
        finally { Delete(root); }
    }

    // ── the feed's own two URLs are checked as well as the payload's ────────────────────────

    /// <summary>
    /// A manifest can re-point the feed and name the payload's URL, so the address it is fetched
    /// FROM matters at least as much as the payload's does — and it arrived from the release list
    /// unexamined.
    /// </summary>
    [Fact]
    public async Task AManifestOnAHostWeDoNotTrust_IsNotFetched()
    {
        var feed = new GitHubReleasesFeed(new System.Net.Http.HttpClient());

        Assert.Null(await feed.GetAssetBytesAsync(
            new ReleaseAsset(UpdateManifest.AssetName, "http://api.github.com/x", 10),
            CancellationToken.None));

        Assert.Null(await feed.GetAssetBytesAsync(
            new ReleaseAsset(UpdateManifest.AssetName, "ftp://api.github.com/x", 10),
            CancellationToken.None));
    }

    /// <summary>
    /// <b>Since a key was compiled in (2026-08-27) the HOST is no longer the constraint here</b> — a
    /// keyed build accepts a feed on any <c>https</c> host, which is exactly what §15.4's migration
    /// and §15.6's mirroring need, and is safe only because the signature makes the host untrusted
    /// for integrity. The SCHEME is still a refusal, and it is refused before any request is made.
    ///
    /// <para>The unkeyed rule has not widened and is asserted directly against
    /// <c>FeedUrlAllowList.IsAllowed</c> in <c>SignedManifestTests</c>; it cannot be exercised
    /// through the feed any more, because the feed reads the compiled-in key rather than an injected
    /// one.</para>
    /// </summary>
    [Theory]
    [InlineData("http://evil.example/releases")]
    [InlineData("ftp://api.github.com/releases")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    public async Task AFeedUrlOnASchemeWeDoNotAccept_IsNeverAsked(string url)
    {
        var feed = new GitHubReleasesFeed(new System.Net.Http.HttpClient(), url);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => feed.ListReleasesAsync(CancellationToken.None));
    }

    /// <summary>
    /// The scheduler only ever persists an allow-listed <c>feedUrl</c>, and only ever reads one back.
    /// </summary>
    [Theory]
    [InlineData("http://api.github.com/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public void AFeedUrlAManifestOffersIsNotHonouredUnlessItIsAllowed(string? url)
        => Assert.False(FeedUrlAllowList.IsAllowed(url));

    // ── design 15.5: a release key stands in for Authenticode on Windows ────────────────────

    /// <summary>
    /// The whole Windows relaxation, as a truth table. A SIGNED build is always checked, so a
    /// certificate adds a second anchor rather than replacing the key; the check is skipped only for
    /// an unsigned build that carries one.
    /// </summary>
    [Theory]
    [InlineData("CN=Someone", true,  true)]    // signed + key      -> both anchors
    [InlineData("CN=Someone", false, true)]    // signed, no key    -> Authenticode, as before
    [InlineData(null,         true,  false)]   // unsigned + key    -> the manifest digest anchors it
    [InlineData(null,         false, true)]    // unsigned, no key  -> APPLIES, and so refuses
    public void TheWindowsPlatformCheckIsSkippedOnlyForAnUnsignedBuildCarryingAReleaseKey(
        string? publisher, bool requireSignedManifest, bool expected)
        => Assert.Equal(expected,
                        PayloadVerifier.WindowsPlatformCheckApplies(publisher, requireSignedManifest));

    /// <summary>
    /// The fail-safe corner stated on its own, because it is the one that must never invert: with no
    /// certificate AND no key there is nothing to anchor a payload to, so the check APPLIES and the
    /// payload is refused. Reaching it at all would take a bug upstream — the gate before the feed
    /// has already answered notify-only — which is exactly why it must not be the permissive case.
    /// </summary>
    [Fact]
    public void WithNeitherACertificateNorAKey_TheCheckStillApplies()
        => Assert.True(PayloadVerifier.WindowsPlatformCheckApplies(null, requireSignedManifest: false));

    /// <summary>
    /// A build with no key compiled in behaves exactly as it did before §15.5 was switched on. This
    /// is what makes filling the key in a deliberate act rather than a change that already happened.
    /// </summary>
    [Fact]
    public void WithNoKeyCompiledIn_TheWindowsCheckIsUnchanged()
    {
        if (ReleaseKeys.RequireSignedManifest) return;   // a key has since been added; see the theory above

        Assert.True(PayloadVerifier.WindowsPlatformCheckApplies(null, ReleaseKeys.RequireSignedManifest));
        Assert.False(ReleaseTrust.Compiled.RequireSignedManifest);
    }

    /// <summary>
    /// Both halves of the policy must read the SAME predicate — the gate before the feed and the
    /// check after the unpack. Source-scanned because the two live in different files and drifting
    /// apart would show up only as updates that fetch a payload and then always refuse it.
    /// </summary>
    [Fact]
    public void BothHalvesOfTheWindowsPolicyReadTheOnePredicate()
    {
        string service = UpdateInstallSiteTests.StripComments(
            UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateService.cs"));
        string verifier = UpdateInstallSiteTests.StripComments(
            UpdateInstallSiteTests.SourceFile("src/Ui/Updates/PayloadVerifier.cs"));

        Assert.Contains("WindowsPlatformCheckApplies", service);
        Assert.Contains("RequireSignedManifest", verifier);
    }

    /// <summary>
    /// macOS is deliberately NOT relaxed: its builds carry a Developer ID already, so accepting a key
    /// in place of it would trade an anchor away for nothing. Pinned because the symmetry is
    /// tempting and the cost of it is invisible.
    /// </summary>
    [Fact]
    public void TheMacBundleCheckIsNotRelaxedByAReleaseKey()
    {
        string verifier = UpdateInstallSiteTests.StripComments(
            UpdateInstallSiteTests.SourceFile("src/Ui/Updates/PayloadVerifier.cs"));

        int mac = verifier.IndexOf("InstallShape.MacOsBundle", StringComparison.Ordinal);
        int win = verifier.IndexOf("OperatingSystem.IsWindows()", mac, StringComparison.Ordinal);

        Assert.True(mac >= 0 && win > mac);
        Assert.DoesNotContain("RequireSignedManifest", verifier[mac..win]);
    }

    private static string Temp()
    {
        string p = Path.Combine(Path.GetTempPath(), "crf-sec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Delete(string p)
    {
        try { Directory.Delete(p, true); } catch { /* best effort */ }
    }
}
