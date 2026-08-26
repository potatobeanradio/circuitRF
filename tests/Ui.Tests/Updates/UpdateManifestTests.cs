using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-12 / R-AU-13 — the manifest hook and its allow-list. We publish no manifest today; this is
/// the whole migration path off GitHub, and it is only ever cheap BEFORE it is needed.
/// </summary>
public class UpdateManifestTests
{
    private const string Wanted = "circuitRF-2.0.0-arm64.dmg";

    /// <summary>
    /// An allow-listed payload URL. A manifest asset's <c>url</c> is checked against the SAME list as
    /// <c>feedUrl</c> — see <see cref="AManifestAssetUrlOffTheAllowList_IsSkipped"/> for why.
    /// </summary>
    private const string OnList = "https://objects.githubusercontent.com/payload.bin";

    private static (FakeUpdateFeed Feed, List<ReleaseInfo> Releases) WithManifest(string manifestJson)
    {
        List<ReleaseInfo> releases =
        [
            CannedReleases.Release("v2.0.0", false, false, Wanted, UpdateManifest.AssetName),
        ];
        return (new FakeUpdateFeed(releases, new() { [UpdateManifest.AssetName] = manifestJson }), releases);
    }

    private static Task<UpdateCandidate?> Select(FakeUpdateFeed feed, List<ReleaseInfo> releases,
                                                 string running = "1.0.0")
        => UpdateSelector.SelectAsync(feed, releases, SemanticVersion.Parse(running), includeBetas: false,
                                      "circuitRF", UpdatePlatform.MacOS, Architecture.Arm64,
                                      CancellationToken.None);

    [Fact]
    public async Task AManifestWins_OverNameMatching()
    {
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest($$"""
        {"assets":[{"name":"{{Wanted}}","url":"{{OnList}}",
                    "size":999,"sha256":"AABB"}]}
        """);

        UpdateCandidate? c = await Select(feed, releases);

        Assert.True(c!.FromManifest);
        Assert.Equal(OnList, c.Asset.Url);   // NOT the release's own URL
        Assert.Equal(999, c.Asset.Size);
        Assert.Equal("aabb", c.Asset.Sha256);
    }

    [Fact]
    public async Task TheSameRelease_WithoutOne_FallsBackSilently()
    {
        List<ReleaseInfo> releases = [CannedReleases.Release("v2.0.0", false, false, Wanted)];

        UpdateCandidate? c = await Select(new FakeUpdateFeed(releases), releases);

        Assert.False(c!.FromManifest);
        Assert.Equal(Wanted, c.Asset.Name);
    }

    [Fact]
    public async Task AManifestWithNoEntryForThisAsset_FallsBackToNameMatching()
    {
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest("""
        {"assets":[{"name":"wBond-2.0.0-arm64.dmg","url":"https://objects.githubusercontent.com/w"}]}
        """);

        UpdateCandidate? c = await Select(feed, releases);

        Assert.False(c!.FromManifest);
        Assert.Equal(Wanted, c.Asset.Name);
    }

    [Fact]
    public async Task AnUnparseableManifest_FallsBack_RatherThanFailingTheCheck()
    {
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest("{ this is not json");

        UpdateCandidate? c = await Select(feed, releases);

        Assert.NotNull(c);
        Assert.False(c!.FromManifest);
    }

    [Fact]
    public async Task MinimumUpgradableFrom_IsHonoured()
    {
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest($$"""
        {"minimumUpgradableFrom":"1.5.0","assets":[{"name":"{{Wanted}}","url":"{{OnList}}"}]}
        """);

        Assert.Null(await Select(feed, releases, running: "1.0.0"));      // below the floor: nothing
        Assert.NotNull(await Select(feed, releases, running: "1.5.0"));   // exactly at it: allowed
    }

    [Fact]
    public async Task AnUnparseableMinimum_IsTreatedAsAbsent_NotAsARefusal()
    {
        // A typo in a manifest must not brick the update path for everyone.
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest($$"""
        {"minimumUpgradableFrom":"oldest","assets":[{"name":"{{Wanted}}","url":"{{OnList}}"}]}
        """);

        Assert.NotNull(await Select(feed, releases, running: "0.1.0"));
    }

    [Fact]
    public async Task AnAllowListedFeedUrl_IsCarried()
    {
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest($$"""
        {"feedUrl":"https://api.github.com/repos/potatobeanradio/circuitRF/releases",
         "assets":[{"name":"{{Wanted}}","url":"{{OnList}}"}]}
        """);

        Assert.Equal("https://api.github.com/repos/potatobeanradio/circuitRF/releases",
                     (await Select(feed, releases))!.FeedUrl);
    }

    /// <summary>
    /// R-AU-13. A field that lets a release point the updater at an arbitrary host is a field that
    /// lets a COMPROMISED release point it at an arbitrary host — so an off-list URL is dropped.
    /// The update itself still proceeds; we simply keep asking the feed we already trust.
    /// </summary>
    [Fact]
    public async Task AFeedUrlOffTheAllowList_IsRefused_ButTheUpdateStillProceeds()
    {
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest($$"""
        {"feedUrl":"https://evil.example/releases","assets":[{"name":"{{Wanted}}","url":"{{OnList}}"}]}
        """);

        UpdateCandidate? c = await Select(feed, releases);

        Assert.Null(c!.FeedUrl);
        Assert.True(c.FromManifest);
    }

    /// <summary>
    /// R-AU-13's reasoning applied to the field it was missing from (found in a second review,
    /// 2026-08-25). <c>feedUrl</c> redirects where we ASK next; an asset's <c>url</c> redirects where
    /// the PAYLOAD comes from, which is the stronger case, not the weaker one — and on Linux
    /// <c>VerifyStagedAsync</c> answers NotApplicable, so nothing downstream would catch it.
    ///
    /// <para>Skipped, not fatal: the caller falls back to name matching against the release's own
    /// assets, which is the normal path today anyway.</para>
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/payload.bin")]            // an arbitrary host
    [InlineData("http://objects.githubusercontent.com/p.bin")]  // right host, plaintext
    public async Task AManifestAssetUrlOffTheAllowList_IsSkipped(string url)
    {
        (FakeUpdateFeed feed, List<ReleaseInfo> releases) = WithManifest($$"""
        {"assets":[{"name":"{{Wanted}}","url":"{{url}}","size":999}]}
        """);

        UpdateCandidate? c = await Select(feed, releases);

        Assert.False(c!.FromManifest);          // the manifest entry was refused ...
        Assert.Equal(Wanted, c.Asset.Name);     // ... and name matching served instead
        Assert.NotEqual(url, c.Asset.Url);
    }

    /// <summary>
    /// An asset name becomes a path under <c>staging/</c>, so a separator in one writes outside the
    /// only directory the updater is allowed to own. Same rule the Windows launcher stub applies to
    /// <c>current</c>, in the other place a supplied name becomes a path.
    /// </summary>
    [Theory]
    [InlineData("../../escaped.dmg")]
    [InlineData("sub/dir.dmg")]
    [InlineData("..")]
    public void AnAssetNameThatIsAPath_IsRefused(string name)
        => Assert.False(UpdateAssetNames.IsSafeAssetFileName(name));

    [Theory]
    [InlineData("circuitRF-2.0.0-arm64.dmg")]
    [InlineData("circuitRF-1.0.0-beta.2-win-x64.zip")]
    [InlineData("wBond-2.0.0-linux-arm64.tar.gz")]
    public void EveryNameThePackagingScriptsProduce_IsAccepted(string name)
        => Assert.True(UpdateAssetNames.IsSafeAssetFileName(name));

    /// <summary>
    /// The guard again at the line that actually builds the path, so no caller has to remember it —
    /// the lesson the reclaimer's running-directory refusal already taught in this subsystem.
    /// </summary>
    [Fact]
    public async Task TheDownloaderItselfRefusesATraversingName_AndWritesNothing()
    {
        string staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                "crf-dl-" + System.Guid.NewGuid().ToString("N")[..8]);
        var downloader = new UpdateDownloader(new System.Net.Http.HttpClient(),
                                              new FakeFreeSpaceProbe(long.MaxValue));

        DownloadResult r = await downloader.DownloadAsync(
            new ReleaseAsset("../../escaped.bin", OnList, 10), staging, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, r.Outcome);
        Assert.False(System.IO.Directory.Exists(staging));   // it did not even create the directory
    }

    [Theory]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("http://api.github.com/x", false)]              // plaintext is exactly what an on-path attacker rewrites
    [InlineData("https://api.github.com.evil.example/x", false)] // suffix trick
    [InlineData("https://evil.example/x", false)]
    [InlineData("//api.github.com/x", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void TheAllowList(string? url, bool allowed)
        => Assert.Equal(allowed, FeedUrlAllowList.IsAllowed(url));

    [Fact]
    public void TheSignatureField_IsParsedAndIgnored()
    {
        // Design §15.5: leaving room for it now means adding signature checking later does not need
        // a second migration of the installed base.
        UpdateManifest? m = UpdateManifest.TryParse("""{"signature":"abc","assets":[]}""");
        Assert.Equal("abc", m!.Signature);
    }
}
