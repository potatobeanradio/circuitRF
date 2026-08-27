using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>A message sink that only records, so "was anything said?" is a counter.</summary>
public sealed class RecordingSink : IMessageSink
{
    public List<(MessageLevel Level, string Text)> Posted { get; } = [];

    public void Post(MessageLevel level, string text, string? filePath = null)
        => Posted.Add((level, text));

    public void Clear() => Posted.Clear();
}

/// <summary>
/// R-AU-50 and the space exceptions — gate items 27 and 9's user-facing half.
///
/// <para><b>Background failure is silent.</b> No Message Panel entry, no dialog, no toast, for an
/// unreachable network, a timeout, a rate limit or a verification failure. An offline machine is the
/// NORMAL state for a large fraction of these users.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public sealed class UpdateServiceBehaviourTests : IDisposable
{
    private const long MB = 1L << 20;

    private readonly string _root;
    private readonly string _install;
    private readonly RecordingSink _sink = new();

    public UpdateServiceBehaviourTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-update-svc-" + Guid.NewGuid().ToString("N")[..8]);
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_install);
        AppDataRoot.RedirectTo(_root);
        Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, null);
        AppPreferencesIo.Update(p => p.AutomaticUpdates = true);
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private InstallSite Site() => new(_install, InstallShape.VersionedPointer, true, _install);

    // An UNKEYED trust: these cases are about space, staging, throttling and announcement, and the
    // canned releases carry no signed manifest. The shipped build IS keyed (design §15.5.1), so
    // inheriting it would turn every one of them into "not a candidate" for that single reason.
    private UpdateService Service(IUpdateFeed feed, long available)
        => new(() => feed, new FakeFreeSpaceProbe(available), _sink, Site, new ReleaseTrust(""));

    /// <summary>A feed that throws the way an unreachable network, a timeout or a 403 does.</summary>
    private sealed class ThrowingFeed(Exception what) : IUpdateFeed
    {
        public Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct) => throw what;
        public Task<byte[]?> GetAssetBytesAsync(ReleaseAsset a, CancellationToken ct) => throw what;
    }

    public static TheoryData<string> NetworkFailures() =>
        ["dns", "timeout", "ratelimit", "io"];

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public async Task ABackgroundFailure_ProducesNoUserVisibleOutput(string kind)
    {
        Exception what = kind switch
        {
            "dns"       => new System.Net.Http.HttpRequestException("no such host"),
            "timeout"   => new TaskCanceledException("timed out"),
            "ratelimit" => new System.Net.Http.HttpRequestException("403 rate limit exceeded"),
            _           => new IOException("connection reset"),
        };

        CheckResult r = await Service(new ThrowingFeed(what), long.MaxValue)
            .CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(CheckOutcome.Failed, r.Outcome);
        Assert.Empty(_sink.Posted);
    }

    [Fact]
    public async Task BeingUpToDate_SaysNothingEither()
    {
        List<ReleaseInfo> feed = [CannedReleases.Release("v0.0.1")];

        CheckResult r = await Service(new FakeUpdateFeed(feed), long.MaxValue)
            .CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(CheckOutcome.UpToDate, r.Outcome);
        Assert.Empty(_sink.Posted);
    }

    /// <summary>
    /// The first of design §13.5's two exceptions. Being offline is often permanent and often
    /// intentional; a full disk is an accident the user wants to know about and can act on.
    /// </summary>
    [Fact]
    public async Task InsufficientSpace_PostsOneLineWithFigures()
    {
        List<ReleaseInfo> feed = [MacRelease("v9.9.9", 160 * MB)];

        CheckResult r = await Service(new FakeUpdateFeed(feed), 380 * MB)
            .CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(CheckOutcome.InsufficientSpace, r.Outcome);

        (MessageLevel level, string text) = Assert.Single(_sink.Posted);
        Assert.Equal(MessageLevel.Warning, level);
        Assert.Contains("free disk", text);
        Assert.Contains("380 MB", text);            // what there is
        Assert.Contains("GB", text);                // what is needed
    }

    /// <summary>...and at most ONE line per 30 days. Information, not nagging.</summary>
    [Fact]
    public async Task InsufficientSpace_IsNotRepeatedOnTheNextCheck()
    {
        List<ReleaseInfo> feed = [MacRelease("v9.9.9", 160 * MB)];
        UpdateService svc = Service(new FakeUpdateFeed(feed), 380 * MB);

        await svc.CheckAsync(manual: false, CancellationToken.None);
        Assert.Single(_sink.Posted);

        // A second background check, with the throttle cleared so it really runs.
        UpdateStateIo.Update(s => s.LastCheckUtc = null);
        await svc.CheckAsync(manual: false, CancellationToken.None);

        Assert.Single(_sink.Posted);
    }

    /// <summary>The other exception: the user asked, so the answer is specific enough to act on.</summary>
    [Fact]
    public async Task AManualCheck_ReportsSpaceEveryTime()
    {
        List<ReleaseInfo> feed = [MacRelease("v9.9.9", 160 * MB)];
        UpdateService svc = Service(new FakeUpdateFeed(feed), 380 * MB);

        await svc.CheckAsync(manual: true, CancellationToken.None);
        await svc.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(2, _sink.Posted.Count);
    }

    [Fact]
    public async Task TheThrottleSkipsABackgroundCheckAndNeverAManualOne()
    {
        List<ReleaseInfo> feed = [CannedReleases.Release("v0.0.1")];
        var fake = new FakeUpdateFeed(feed);
        UpdateService svc = Service(fake, long.MaxValue);

        await svc.CheckAsync(manual: false, CancellationToken.None);   // records LastCheckUtc
        Assert.Equal(1, fake.ListCalls);

        Assert.Equal(CheckOutcome.Throttled,
                     (await svc.CheckAsync(manual: false, CancellationToken.None)).Outcome);
        Assert.Equal(1, fake.ListCalls);

        await svc.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(2, fake.ListCalls);
    }

    [Fact]
    public async Task ABlacklistedVersion_IsNotRetried()
    {
        List<ReleaseInfo> feed = [MacRelease("v9.9.9", 1)];
        UpdateStateIo.Update(s => s.Blacklist_Add("9.9.9"));

        CheckResult r = await Service(new FakeUpdateFeed(feed), long.MaxValue)
            .CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(CheckOutcome.UpToDate, r.Outcome);
        Assert.Empty(_sink.Posted);
    }

    /// <summary>
    /// A successful check records its time even when nothing came of it — otherwise a machine with
    /// no updates available would re-ask every time the application started.
    /// </summary>
    /// <summary>
    /// A version that has ALREADY been swapped in and is waiting to prove it starts must not be
    /// fetched again — and the reason is not that it is wasteful.
    ///
    /// <para><c>UpdateStartup.RecordSwap</c> clears <c>StagedVersion</c> when it flips the pointer, so
    /// only <c>PendingVersion</c> records that version. Without this guard the whole fetch ran again
    /// with <c>destinationDir = &lt;root&gt;/app-&lt;version&gt;</c>, and <c>UpdateStager.Promote</c>
    /// deletes an existing destination before renaming into it — i.e. it deleted the directory
    /// <c>current</c> names. Reachable from Help ▸ Check for Updates…, which ignores the throttle
    /// (found in a second review, 2026-08-25).</para>
    ///
    /// <para>The pointer flip is made by the OLD version for the NEXT launch, so the running version
    /// is still lower than the pending one and every other guard in the check answers "yes, fetch
    /// it".</para>
    /// </summary>
    [Fact]
    public async Task AVersionAlreadySwappedInAndPending_IsNotFetchedAgain()
    {
        const string pending = "99.0.0";

        string live = Path.Combine(_install, "app-" + pending);
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "circuitRF"), "the version waiting to prove it starts");
        File.WriteAllText(Path.Combine(_install, UpdateInstallSite.CurrentPointerName), "app-" + pending);

        UpdateStateIo.Update(s =>
        {
            s.PendingVersion = pending;                 // swapped in ...
            s.PendingPath    = "app-" + pending;
            s.StagedVersion  = null;                    // ... and therefore no longer STAGED
            s.LaunchAttempts = 0;
        });

        var feed = new FakeUpdateFeed(
            [CannedReleases.Release("v" + pending, false, false, $"circuitRF-{pending}-arm64.dmg")]);

        // manual: the throttle is not what is being tested, and Help > Check for Updates... is the
        // path this was actually reachable from.
        CheckResult r = await Service(feed, 500 * MB).CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(CheckOutcome.Staged, r.Outcome);
        Assert.Equal(UpdateService.AlreadyStagedDetail, r.Detail);

        // Nothing was transferred and the live tree is untouched.
        Assert.False(Directory.Exists(UpdatePaths.Staging) &&
                     Directory.GetFileSystemEntries(UpdatePaths.Staging).Length > 0);
        Assert.Equal("the version waiting to prove it starts",
                     File.ReadAllText(Path.Combine(live, "circuitRF")));
        Assert.Equal("app-" + pending, UpdateSwap.ReadCurrent(_install));
    }

    [Fact]
    public async Task ASuccessfulCheckRecordsItsTime_EvenWhenNothingIsOffered()
    {
        await Service(new FakeUpdateFeed([CannedReleases.Release("v0.0.1")]), long.MaxValue)
            .CheckAsync(manual: false, CancellationToken.None);

        Assert.NotNull(UpdateStateIo.Load().LastCheckUtc);
    }

    [Fact]
    public async Task AFailedCheckDoesNotRecordATime_SoTheNextLaunchTriesAgain()
    {
        await Service(new ThrowingFeed(new IOException("down")), long.MaxValue)
            .CheckAsync(manual: false, CancellationToken.None);

        Assert.Null(UpdateStateIo.Load().LastCheckUtc);
    }

    // -- the shipped, KEYED configuration, through the service rather than the selector -----------

    /// <summary>
    /// Every test above injects an UNKEYED trust, so between them they cover only the path no shipped
    /// build takes any more. This is the other one: a build carrying a release key, checking a release
    /// whose manifest is validly signed by that key, driven through <see cref="UpdateService"/> rather
    /// than through <c>UpdateSelector</c> directly.
    ///
    /// <para>The observable is <see cref="CheckOutcome.InsufficientSpace"/>, and deliberately so — it
    /// is the last outcome before the download, so reaching it proves the whole chain in front of it
    /// ran: the gate did not answer notify-only (design §15.5.1, which is what makes this reachable on
    /// Windows at all), the manifest was fetched, its signature verified against the injected key, and
    /// the asset for THIS platform selected out of it. Staging itself cannot be asserted portably,
    /// since it unpacks a real <c>.dmg</c> / <c>.zip</c> / <c>.tar.gz</c>.</para>
    /// </summary>
    [Fact]
    public async Task OnAKeyedBuild_AValidlySignedReleaseIsSelectedThroughTheService()
    {
        (FakeUpdateFeed feed, string key) = SignedRelease("9.9.9", 900 * MB);

        var service = new UpdateService(
            () => feed, new FakeFreeSpaceProbe(380 * MB), _sink, Site, new ReleaseTrust(key));

        CheckResult r = await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(CheckOutcome.InsufficientSpace, r.Outcome);
    }

    /// <summary>
    /// The same release, the same keyed build, with the SIGNATURE asset removed — and now there is no
    /// candidate at all. Without this the test above would pass just as well on a build that had
    /// quietly fallen back to name matching, which is the exact failure design §15.5 exists to stop.
    /// </summary>
    [Fact]
    public async Task OnAKeyedBuild_TheSameReleaseWithNoSignatureIsNotACandidate()
    {
        (FakeUpdateFeed feed, string key) = SignedRelease("9.9.9", 900 * MB, withSignature: false);

        var service = new UpdateService(
            () => feed, new FakeFreeSpaceProbe(380 * MB), _sink, Site, new ReleaseTrust(key));

        CheckResult r = await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(CheckOutcome.UpToDate, r.Outcome);
    }

    /// <summary>
    /// A release whose manifest names every platform's asset, signed by a throwaway key. Named for all
    /// three platforms rather than for the host, so the two tests above assert the same thing on each
    /// CI runner instead of only on whichever one happens to match.
    /// </summary>
    private static (FakeUpdateFeed Feed, string PublicKey) SignedRelease(
        string version, long size, bool withSignature = true)
    {
        string[] names =
        [
            UpdateAssetNames.Expected(UpdateApp.Name, version, UpdatePlatform.MacOS,   "arm64"),
            UpdateAssetNames.Expected(UpdateApp.Name, version, UpdatePlatform.MacOS,   "x64"),
            UpdateAssetNames.Expected(UpdateApp.Name, version, UpdatePlatform.Windows, "arm64"),
            UpdateAssetNames.Expected(UpdateApp.Name, version, UpdatePlatform.Windows, "x64"),
            UpdateAssetNames.Expected(UpdateApp.Name, version, UpdatePlatform.Windows, "x86"),
            UpdateAssetNames.Expected(UpdateApp.Name, version, UpdatePlatform.Linux,   "arm64"),
            UpdateAssetNames.Expected(UpdateApp.Name, version, UpdatePlatform.Linux,   "x64"),
        ];

        const string sha = "1ac30fd677168dffa8e69a4c83256bc951fd9d50ab6d8774f60d279f84ee6406";

        string entries = string.Join(",", names.Select(n =>
            $"{{\"name\":\"{n}\",\"url\":\"https://api.github.com/{n}\","
            + $"\"size\":{size},\"sha256\":\"{sha}\"}}"));

        byte[] manifest = Encoding.UTF8.GetBytes($"{{\"assets\":[{entries}]}}");

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string sig = Convert.ToBase64String(
            key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        List<string> assetNames = [.. names, UpdateManifest.AssetName];
        var bytes = new Dictionary<string, byte[]> { [UpdateManifest.AssetName] = manifest };

        if (withSignature)
        {
            assetNames.Add(UpdateManifest.SignatureAssetName);
            bytes[UpdateManifest.SignatureAssetName] = Encoding.UTF8.GetBytes(sig);
        }

        ReleaseInfo release = CannedReleases.Release(version, assetNames: [.. assetNames]);

        return (FakeUpdateFeed.WithBytes([release], bytes),
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static ReleaseInfo MacRelease(string tag, long size)
    {
        string version = tag[1..];
        var assets = new List<ReleaseAsset>();
        foreach (string app in new[] { "circuitRF", "harmonicaRF", "wBond" })
            foreach (string arch in new[] { "arm64", "x64" })
                assets.Add(new ReleaseAsset($"{app}-{version}-{arch}.dmg", "https://x/a", size));

        foreach (string app in new[] { "circuitRF", "harmonicaRF", "wBond" })
            foreach (string arch in new[] { "x64", "arm64", "x86" })
                assets.Add(new ReleaseAsset($"{app}-{version}-win-{arch}.zip", "https://x/a", size));

        foreach (string app in new[] { "circuitRF", "harmonicaRF", "wBond" })
            foreach (string arch in new[] { "x64", "arm64" })
                assets.Add(new ReleaseAsset($"{app}-{version}-linux-{arch}.tar.gz", "https://x/a", size));

        return new ReleaseInfo(tag, SemanticVersion.Parse(tag), false, false, assets);
    }
}

/// <summary>
/// R-AU-48 / gate 26 — <b>there is no "Relaunch" button, anywhere, in any form.</b> Owner
/// instruction. The application can be holding unsaved workspaces; a one-click relaunch invites data
/// loss to save a keystroke.
/// </summary>
public class NoRelaunchControlTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void NoControlAnywhereOffersToRelaunchTheApplication()
    {
        string ui = Path.Combine(RepoRoot().FullName, "src", "Ui");

        foreach (string file in Directory.EnumerateFiles(ui, "*.axaml", SearchOption.AllDirectories))
        {
            string markup = File.ReadAllText(file);

            // The H8 lesson: strip comments first, or the scan matches the requirement's own
            // description and calls it a violation.
            markup = System.Text.RegularExpressions.Regex.Replace(markup, "<!--.*?-->", "",
                                                                  System.Text.RegularExpressions.RegexOptions.Singleline);

            foreach (string control in new[] { "<Button", "<MenuItem", "<NativeMenuItem", "<HyperlinkButton" })
            {
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(markup, control + @"[^>]*>",
                             System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    Assert.False(m.Value.Contains("Relaunch", StringComparison.OrdinalIgnoreCase),
                                 $"{Path.GetFileName(file)} declares a relaunch control: {m.Value}");
                }
            }
        }
    }

    /// <summary>
    /// The Message Panel line SAYS "Relaunch circuitRF to start using the version" — which is
    /// instruction, not a control, and is exactly what R-AU-47 specifies. This pins that the word
    /// appears only there, so a later "helpful" button cannot slip in beside it.
    /// </summary>
    [Fact]
    public void TheWordAppearsOnlyAsInstruction_InTheOneMessagePanelLine()
    {
        string service = File.ReadAllText(
            Path.Combine(RepoRoot().FullName, "src", "Ui", "Updates", "UpdateService.cs"));

        Assert.Contains("Relaunch {UpdateApp.Name} to start using the version.", service);
        Assert.DoesNotContain("RelaunchCommand", service);
        Assert.DoesNotContain("RelaunchButton", service);
    }
}
