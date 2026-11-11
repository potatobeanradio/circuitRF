using System.Net.Http;
using System.Reflection;

namespace CircuitRF.Design.Plugins;

/// <summary>Plugin and circuitRF version strings, compared one way everywhere.</summary>
public static class PluginVersions
{
    /// <summary>Dotted numeric parts compared as numbers ("0.10.0" after "0.9.1"); a pre-release suffix sorts before its
    /// release; anything unparseable compares as text.</summary>
    public static readonly IComparer<string> Comparer = new VersionComparer();

    public static int Compare(string? a, string? b) => Comparer.Compare(a, b);

    /// <summary>The running circuitRF's version, without build metadata.</summary>
    public static string CircuitRF
    {
        get
        {
            string v = typeof(PluginVersions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = v.IndexOf('+');
            return plus < 0 ? v : v[..plus];
        }
    }

    /// <summary>"needs circuitRF x.y" when <paramref name="minCircuitRF"/> is newer than this build, else null.</summary>
    public static string? NeedsNewer(string? minCircuitRF, string? running = null)
    {
        running ??= CircuitRF;
        if (string.IsNullOrWhiteSpace(minCircuitRF) || running.Length == 0) return null;
        return Compare(Release(running), minCircuitRF.Trim()) < 0 ? $"needs circuitRF {minCircuitRF.Trim()}" : null;
    }

    // A pre-release of 1.8.0 can run what asks for 1.8.0: the list names releases, and a beta is that release in testing.
    private static string Release(string v) => v.IndexOf('-') is int d and >= 0 ? v[..d] : v;

    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            var (ah, apre) = Split(a); var (bh, bpre) = Split(b);
            string[] ap = ah.Split('.'), bp = bh.Split('.');
            for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
            {
                string x = i < ap.Length ? ap[i] : "0", y = i < bp.Length ? bp[i] : "0";
                int c = long.TryParse(x, out long xn) && long.TryParse(y, out long yn) ? xn.CompareTo(yn) : string.CompareOrdinal(x, y);
                if (c != 0) return c;
            }
            if (apre is null) return bpre is null ? 0 : 1;
            if (bpre is null) return -1;
            return string.CompareOrdinal(apre, bpre);
        }

        private static (string Head, string? Pre) Split(string v)
        {
            v = v.TrimStart('v', 'V');
            int dash = v.IndexOf('-');
            return dash < 0 ? (v, null) : (v[..dash], v[(dash + 1)..]);
        }
    }
}

/// <summary>What one request answered: the body, or — for anything but success and 404 — the sentence that says why
/// there is none. A 404 is a body of null with no failure: "not there" is an answer, not an error.</summary>
public sealed record PluginHttpResult(byte[]? Body, string? Failure = null)
{
    public static PluginHttpResult Fail(string why) => new(null, why);
}

/// <summary>
/// Every request the plugin installer makes goes through this (brief-plg-6 gate 5). <b>Nothing reaches a network
/// without being asked</b>: only Refresh, Install and Update call it, and a test hands in a counting one to prove that
/// opening Settings, listing and starting plugins make no request at all. Failures are RETURNED as sentences, never
/// thrown (<c>src/Design/CLAUDE.md</c>).
/// </summary>
public interface IPluginHttp
{
    Task<PluginHttpResult> GetBytesAsync(string url, CancellationToken ct);

    /// <summary>Streams <paramref name="url"/> to <paramref name="file"/>; null on success, else why not.</summary>
    Task<string?> DownloadAsync(string url, string file, CancellationToken ct);
}

/// <summary>
/// The real <see cref="IPluginHttp"/>: no credential, no cookie, a generic agent string, and a size cap. A private
/// repository answers 404 to an anonymous request, which is reported as not found — never a prompt.
/// </summary>
public sealed class PluginHttp : IPluginHttp, IDisposable
{
    /// <summary>A plugin archive larger than this is refused rather than filling the disk.</summary>
    public const long MaxDownloadBytes = 1L << 30;

    private readonly HttpClient _http;

    public PluginHttp(TimeSpan? timeout = null)
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = true })
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("circuitRF-plugins");
    }

    public async Task<PluginHttpResult> GetBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new PluginHttpResult(null);
            if (!response.IsSuccessStatusCode) return PluginHttpResult.Fail(Answered(url, response));
            return new PluginHttpResult(await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (HttpRequestException e) { return PluginHttpResult.Fail(Unreachable(url, e)); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return PluginHttpResult.Fail(TimedOut(url)); }
    }

    public async Task<string?> DownloadAsync(string url, string file, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return Answered(url, response);
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes) return TooLarge(url);
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(file);
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > MaxDownloadBytes) return TooLarge(url);
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            return null;
        }
        catch (HttpRequestException e) { return Unreachable(url, e); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return TimedOut(url); }
    }

    private static string Answered(string url, HttpResponseMessage r) => $"{url} answered {(int)r.StatusCode} {r.ReasonPhrase}.";
    private static string Unreachable(string url, HttpRequestException e) => $"{url} could not be reached: {e.Message}";
    private static string TimedOut(string url) => $"{url} did not answer in time.";
    private static string TooLarge(string url) => $"{url} is larger than {MaxDownloadBytes >> 20} MB; a plugin is not.";

    public void Dispose() => _http.Dispose();
}

/// <summary>Where a plugin's repository is hosted. Both are reached anonymously, through their public APIs.</summary>
public enum PluginRepositoryHost { GitHub, GitLab }

/// <summary>
/// A plugin's repository on GitHub or GitLab, read from whatever link a user pastes (brief-plg-6 §2): the repository's
/// page, a clone address (<c>https://…/repo.git</c> or <c>git@host:owner/repo.git</c>), or a link to one release, tag or
/// tree, which also names the tag. GitLab's subgroups (<c>group/sub/project</c>) are kept whole.
/// </summary>
/// <param name="Path"><c>owner/repo</c> on GitHub; <c>group[/subgroup…]/project</c> on GitLab.</param>
public sealed record PluginRepository(PluginRepositoryHost Host, string Path)
{
    public string HostName => Host == PluginRepositoryHost.GitHub ? "github.com" : "gitlab.com";
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    /// <summary>The repository's page — what <c>active.json</c> records as the source.</summary>
    public string Web => $"https://{HostName}/{Path}";

    public string ReleasesApi => Host == PluginRepositoryHost.GitHub
        ? $"https://api.github.com/repos/{Path}/releases?per_page=100"
        : $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(Path)}/releases?per_page=100";

    public string TagsApi => Host == PluginRepositoryHost.GitHub
        ? $"https://api.github.com/repos/{Path}/tags?per_page=100"
        : $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(Path)}/repository/tags?per_page=100";

    public string SourceArchive(string tag) => Host == PluginRepositoryHost.GitHub
        ? $"https://github.com/{Path}/archive/refs/tags/{Uri.EscapeDataString(tag)}.zip"
        : $"https://gitlab.com/{Path}/-/archive/{Uri.EscapeDataString(tag)}/{Name}-{Uri.EscapeDataString(tag)}.zip";

    /// <summary>The repository a pasted link names, and the tag when the link names one; null when it names neither host's
    /// repository. Only https and <c>git@</c> links: anything else would need a credential or a protocol this has not.</summary>
    public static (PluginRepository Repository, string? Tag)? Parse(string link)
    {
        string text = link.Trim();
        if (text.StartsWith("git@", StringComparison.OrdinalIgnoreCase) && text.IndexOf(':') is int colon and > 4)
            text = $"https://{text[4..colon]}/{text[(colon + 1)..]}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        PluginRepositoryHost host;
        if (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) host = PluginRepositoryHost.GitHub;
        else if (u.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.gitlab.com", StringComparison.OrdinalIgnoreCase)) host = PluginRepositoryHost.GitLab;
        else return null;

        var parts = Uri.UnescapeDataString(u.AbsolutePath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        List<string> repo;
        List<string> rest;
        if (host == PluginRepositoryHost.GitHub)
        {
            if (parts.Count < 2) return null;
            repo = parts.Take(2).ToList();
            rest = parts.Skip(2).ToList();
        }
        else
        {
            // GitLab marks where the project's path ends with a '-' segment: group/sub/project/-/releases/v1.
            int dash = parts.IndexOf("-");
            repo = dash < 0 ? parts : parts.Take(dash).ToList();
            rest = dash < 0 ? [] : parts.Skip(dash + 1).ToList();
            if (repo.Count < 2) return null;
        }
        if (repo[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo[^1] = repo[^1][..^4];
        if (repo.Any(p => p.Length == 0 || p is "." or ".." || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) return null;

        // …/releases/tag/v1 (GitHub), …/-/releases/v1 and …/-/tags/v1 (GitLab), …/tree/v1 (both).
        string? tag = rest switch
        {
            ["releases", "tag", var t, ..] => t,
            ["releases", var t, ..] when host == PluginRepositoryHost.GitLab => t,
            ["tags", var t, ..] => t,
            ["tree", var t, ..] => t,
            _ => null,
        };
        return (new PluginRepository(host, string.Join('/', repo)), tag);
    }
}
