using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// The one shipping <see cref="IUpdateFeed"/>: GitHub Releases, read through the public REST API.
///
/// <para><b><c>/releases</c>, never <c>/releases/latest</c>.</b> The <c>latest</c> endpoint excludes
/// prereleases and drafts, which would make the beta channel permanently empty and look like the
/// feature simply not working. We fetch the list and filter (<see cref="UpdateSelector"/>).</para>
///
/// <para>Unauthenticated: 60 requests per hour per IP, which at one check per machine per day is
/// irrelevant. There is no authenticated option worth having — a token shipped in a desktop binary
/// is not a secret.</para>
/// </summary>
public sealed class GitHubReleasesFeed : IUpdateFeed
{
    /// <summary>The release list. One constant, which is what makes design §15's move cheap.</summary>
    public const string DefaultApiUrl = "https://api.github.com/repos/potatobeanradio/circuitRF/releases";

    private readonly HttpClient _http;
    private readonly string _url;

    public GitHubReleasesFeed(HttpClient http, string? apiUrl = null)
    {
        _http = http;
        _url  = apiUrl ?? DefaultApiUrl;
    }

    public async Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct)
    {
        // The feed URL itself. UpdateScheduler already allow-lists what it reads out of state.json,
        // but a second implementation of this interface, or a test, can hand any string to the
        // constructor — and this is the line that would fetch it.
        if (!FeedUrlAllowList.IsAcceptable(_url))
            throw new InvalidOperationException($"'{_url}' is not an allowed update feed.");

        using HttpResponseMessage res = await _http.GetAsync(_url, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseReleases(json);
    }

    public async Task<byte[]?> GetAssetBytesAsync(ReleaseAsset asset, CancellationToken ct)
    {
        // Checked at the line that makes the request, for the same reason the payload URL is: this
        // one is the MANIFEST, and a manifest can re-point the feed and name the payload's own URL.
        // Its address arrives from the release list unexamined otherwise (security review,
        // 2026-08-25).
        if (!FeedUrlAllowList.IsAcceptable(asset.Url)) return null;

        try
        {
            using HttpResponseMessage res = await _http.GetAsync(asset.Url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            // MaxResponseContentBufferSize on the feed's client is what bounds this; a body over it
            // throws HttpRequestException, which the filter below turns into "no manifest".
            return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The parsing half, separated so every test drives it from canned JSON. A release whose tag is
    /// not a version is <b>skipped</b>, not an error: the repository may carry tags this feature knows
    /// nothing about, and one of them must not stop the whole check.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        var releases = new List<ReleaseInfo>();

        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return releases;

        foreach (JsonElement r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;

            string tag = Str(r, "tag_name") ?? "";
            if (!SemanticVersion.TryParse(tag, out SemanticVersion? version) || version is null) continue;

            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out JsonElement aArr) && aArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in aArr.EnumerateArray())
                {
                    string? name = Str(a, "name");
                    string? url  = Str(a, "browser_download_url");
                    if (name is null || url is null) continue;

                    long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
                    assets.Add(new ReleaseAsset(name, url, size, Digest(a)));
                }
            }

            releases.Add(new ReleaseInfo(tag, version, Bool(r, "prerelease"), Bool(r, "draft"), assets,
                                         Str(r, "body") ?? ""));
        }

        return releases;
    }

    /// <summary>
    /// GitHub has begun returning a <c>digest</c> field, spelled <c>sha256:&lt;hex&gt;</c>. Use it when
    /// present, but treat it as best-effort — the code signature is the guarantee (design §9), so an
    /// absent or unfamiliar digest algorithm is simply no hash rather than a refusal.
    /// </summary>
    private static string? Digest(JsonElement asset)
    {
        string? d = Str(asset, "digest");
        if (d is null) return null;

        const string prefix = "sha256:";
        return d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? d[prefix.Length..].ToLowerInvariant()
            : null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
