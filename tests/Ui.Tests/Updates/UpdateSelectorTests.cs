using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>R-AU-7, R-AU-9, R-AU-11 — no downgrade, and channels are the prerelease flag alone.</summary>
public class UpdateSelectorTests
{
    private static readonly List<ReleaseInfo> Feed =
    [
        CannedReleases.Release("v0.9.0",       prerelease: false),
        CannedReleases.Release("v1.0.0-beta.1", prerelease: true),
        CannedReleases.Release("v1.0.0-beta.2", prerelease: true),
        CannedReleases.Release("v1.0.0-beta.10", prerelease: true),
        CannedReleases.Release("v1.1.0",       prerelease: false, draft: true),   // never offered
    ];

    private static ReleaseInfo? Pick(string running, bool betas)
        => UpdateSelector.SelectRelease(Feed, SemanticVersion.Parse(running), betas);

    [Fact]
    public void BetasOff_SeesOnlyStableReleases()
        => Assert.Equal("v0.9.0", Pick("0.5.0", betas: false)?.TagName);

    [Fact]
    public void BetasOn_SeesPrereleasesToo_AndTakesTheNewest()
        => Assert.Equal("v1.0.0-beta.10", Pick("0.5.0", betas: true)?.TagName);

    [Fact]
    public void Drafts_NeverAppear_OnEitherChannel()
    {
        Assert.DoesNotContain("v1.1.0", Feed.Where(r => !r.IsDraft).Select(r => r.TagName));
        Assert.NotEqual("v1.1.0", Pick("0.5.0", betas: false)?.TagName);
        Assert.NotEqual("v1.1.0", Pick("0.5.0", betas: true)?.TagName);
    }

    /// <summary>
    /// R-AU-7. A user on 1.0.0-beta.3 whose channel's newest stable is 0.9.0 is offered NOTHING, and
    /// that is correct — it is what stops the beta channel from silently downgrading people.
    /// </summary>
    [Fact]
    public void ARunningBeta_NewerThanTheNewestStable_IsOfferedNothing()
        => Assert.Null(Pick("1.0.0-beta.3", betas: false));

    [Fact]
    public void TheSameVersion_IsNotAnUpdate()
        => Assert.Null(Pick("1.0.0-beta.10", betas: true));

    [Fact]
    public void AnOlderVersion_IsNeverOffered()
    {
        // Betas ON, running the newest beta: 0.9.0 is on the channel and is LOWER, so nothing.
        Assert.Null(Pick("1.0.0-beta.10", betas: true));
    }

    [Fact]
    public void ABetaUser_IsOfferedTheStableRelease_OnceItShips()
    {
        List<ReleaseInfo> withStable = [.. Feed, CannedReleases.Release("v1.0.0")];
        ReleaseInfo? picked = UpdateSelector.SelectRelease(
            withStable, SemanticVersion.Parse("1.0.0-beta.3"), includeBetas: false);

        Assert.Equal("v1.0.0", picked?.TagName);   // because 1.0.0 > 1.0.0-beta.3
    }

    [Fact]
    public void ChannelSwitch_IsTheFlagAndNothingElse()
    {
        // A release whose TAG says "beta" but whose prerelease flag is false is a stable release.
        // No naming convention, no second list, no maintained channel file.
        List<ReleaseInfo> odd = [CannedReleases.Release("v2.0.0-beta.1", prerelease: false)];
        Assert.Equal("v2.0.0-beta.1",
            UpdateSelector.SelectRelease(odd, SemanticVersion.Parse("1.0.0"), includeBetas: false)?.TagName);
    }

    [Fact]
    public void UnparseableTags_AreSkipped_NotFatal()
    {
        string json = """
        [{"tag_name":"nightly","prerelease":false,"draft":false,"assets":[]},
         {"tag_name":"v2.0.0","prerelease":false,"draft":false,"assets":[]}]
        """;

        IReadOnlyList<ReleaseInfo> parsed = GitHubReleasesFeed.ParseReleases(json);
        Assert.Single(parsed);
        Assert.Equal("v2.0.0", parsed[0].TagName);
    }

    [Fact]
    public void ParsesGitHubShapedJson_IncludingTheDigestField()
    {
        string json = """
        [{"tag_name":"v1.0.0","prerelease":false,"draft":false,"assets":[
           {"name":"circuitRF-1.0.0-arm64.dmg","browser_download_url":"https://x/a","size":160316903,
            "digest":"sha256:ABCDEF0123"}]}]
        """;

        ReleaseInfo r = GitHubReleasesFeed.ParseReleases(json).Single();
        Assert.False(r.IsPreRelease);
        Assert.False(r.IsDraft);

        ReleaseAsset a = r.Assets.Single();
        Assert.Equal(160316903, a.Size);
        Assert.Equal("abcdef0123", a.Sha256);   // lower-cased, prefix stripped
    }

    [Fact]
    public void AnUnfamiliarDigestAlgorithm_IsNoHash_RatherThanARefusal()
    {
        string json = """
        [{"tag_name":"v1.0.0","prerelease":false,"draft":false,"assets":[
           {"name":"a.dmg","browser_download_url":"https://x/a","size":1,"digest":"sha512:aa"}]}]
        """;

        Assert.Null(GitHubReleasesFeed.ParseReleases(json).Single().Assets.Single().Sha256);
    }

    /// <summary>R-AU-9 — the endpoint is the release LIST. /releases/latest excludes prereleases and
    /// drafts, which would make the beta channel permanently and silently empty.</summary>
    [Fact]
    public void TheFeedUrl_IsTheReleaseList_NotLatest()
    {
        Assert.EndsWith("/releases", GitHubReleasesFeed.DefaultApiUrl);
        Assert.DoesNotContain("/releases/latest", GitHubReleasesFeed.DefaultApiUrl);
    }

    [Fact]
    public async System.Threading.Tasks.Task SelectAsync_FindsTheAssetForThisAppAndArch()
    {
        List<ReleaseInfo> feed =
        [
            CannedReleases.Release("v2.0.0", false, false,
                "circuitRF-2.0.0-arm64.dmg", "harmonicaRF-2.0.0-arm64.dmg", "wBond-2.0.0-arm64.dmg"),
        ];

        UpdateCandidate? c = await UpdateSelector.SelectAsync(
            new FakeUpdateFeed(feed), feed, SemanticVersion.Parse("1.0.0"), includeBetas: false,
            app: "wBond", UpdatePlatform.MacOS, Architecture.Arm64, CancellationToken.None);

        Assert.Equal("wBond-2.0.0-arm64.dmg", c?.Asset.Name);
        Assert.False(c?.FromManifest);
    }

    [Fact]
    public async System.Threading.Tasks.Task SelectAsync_ReturnsNothing_WhenTheReleasePublishesNoAssetForThisPlatform()
    {
        List<ReleaseInfo> feed = [CannedReleases.Release("v2.0.0", false, false, "circuitRF-2.0.0-arm64.dmg")];

        UpdateCandidate? c = await UpdateSelector.SelectAsync(
            new FakeUpdateFeed(feed), feed, SemanticVersion.Parse("1.0.0"), includeBetas: false,
            app: "circuitRF", UpdatePlatform.Linux, Architecture.Arm64, CancellationToken.None);

        Assert.Null(c);
    }
}
