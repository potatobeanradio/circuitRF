using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// The only feed any test in this repository is allowed to use. No test may make a network call —
/// not a "just this one", not a [Trait]-gated one — so the shipping <see cref="GitHubReleasesFeed"/>
/// is exercised only through its pure <c>ParseReleases</c> half.
/// </summary>
public sealed class FakeUpdateFeed : IUpdateFeed
{
    private readonly IReadOnlyList<ReleaseInfo> _releases;
    private readonly Dictionary<string, byte[]> _assets;

    public FakeUpdateFeed(IReadOnlyList<ReleaseInfo> releases, Dictionary<string, string>? textAssets = null)
    {
        _releases = releases;
        _assets   = [];
        if (textAssets is not null)
            foreach ((string name, string text) in textAssets) _assets[name] = Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Assets as BYTES — what a detached signature is checked against (design §15.5).</summary>
    public static FakeUpdateFeed WithBytes(
        IReadOnlyList<ReleaseInfo> releases, Dictionary<string, byte[]> assets)
        => new(releases) { _bytes = assets };

    private Dictionary<string, byte[]>? _bytes;

    /// <summary>How many times the feed was actually asked — the counter R-AU-44's gate reads.</summary>
    public int ListCalls { get; private set; }

    public Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct)
    {
        ListCalls++;
        return Task.FromResult(_releases);
    }

    public Task<byte[]?> GetAssetBytesAsync(ReleaseAsset asset, CancellationToken ct)
        => Task.FromResult((_bytes ?? _assets).TryGetValue(asset.Name, out byte[]? b) ? b : null);
}

/// <summary>
/// An <see cref="IUpdateFeed"/> that FAILS THE TEST if it is touched at all. R-AU-44's "never checks"
/// is literal — the preference is read before an HttpClient is constructed, not consulted afterwards
/// to decide whether to act on the result — and a round-trip test of the setting does not show that.
/// </summary>
public sealed class ForbiddenUpdateFeed : IUpdateFeed
{
    public Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct)
        => throw new InvalidOperationException(
            "The feed was contacted with automatic updates off. R-AU-44: circuitRF opens no socket for any reason.");

    public Task<byte[]?> GetAssetBytesAsync(ReleaseAsset asset, CancellationToken ct)
        => throw new InvalidOperationException("The feed was contacted with automatic updates off.");
}

/// <summary>A free-space probe whose answer the test dictates.</summary>
public sealed class FakeFreeSpaceProbe : IFreeSpaceProbe
{
    private long _available;
    public FakeFreeSpaceProbe(long available) => _available = available;

    public int Calls { get; private set; }

    public void SetAvailable(long bytes) => _available = bytes;

    public long AvailableFreeSpace(string path) { Calls++; return _available; }
}

/// <summary>Canned release JSON, shaped like GitHub's, built without a network.</summary>
public static class CannedReleases
{
    public static string Json(params (string Tag, bool Prerelease, bool Draft, string[] Assets)[] releases)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < releases.Length; i++)
        {
            (string tag, bool pre, bool draft, string[] assets) = releases[i];
            if (i > 0) sb.Append(',');
            sb.Append($$"""
            {"tag_name":"{{tag}}","prerelease":{{(pre ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},"assets":[
            """);
            for (int j = 0; j < assets.Length; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append($$"""
                {"name":"{{assets[j]}}","browser_download_url":"https://objects.githubusercontent.com/{{assets[j]}}","size":1234}
                """);
            }
            sb.Append("]}");
        }
        return sb.Append(']').ToString();
    }

    /// <summary>A release built directly, for tests that do not care about JSON.</summary>
    public static ReleaseInfo Release(string tag, bool prerelease = false, bool draft = false,
                                      params string[] assetNames)
    {
        var assets = new List<ReleaseAsset>();
        foreach (string n in assetNames)
            assets.Add(new ReleaseAsset(n, $"https://objects.githubusercontent.com/{n}", 1234));

        return new ReleaseInfo(tag, SemanticVersion.Parse(tag), prerelease, draft, assets);
    }
}
