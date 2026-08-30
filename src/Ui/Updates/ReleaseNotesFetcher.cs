using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>Why the Release Notes dialog is showing what it is showing.</summary>
public enum ReleaseNotesOutcome
{
    /// <summary>The release for this version was found and carries a body.</summary>
    Found,

    /// <summary>The feed answered, but nothing in it matches this version — or its body is empty.</summary>
    NotPublished,

    /// <summary>The feed could not be reached, or did not answer with a release list.</summary>
    Unavailable,
}

/// <summary>What the dialog was handed.</summary>
/// <param name="Outcome">Which of the three forms to render.</param>
/// <param name="Version">The version the notes were asked for — shown in every form.</param>
/// <param name="Markdown">The release body, empty unless <see cref="ReleaseNotesOutcome.Found"/>.</param>
/// <param name="BrowseUrl">The repository's releases page, for the user to check themselves.</param>
public sealed record ReleaseNotesResult(
    ReleaseNotesOutcome Outcome, string Version, string Markdown, string BrowseUrl);

/// <summary>
/// Fetches the running version's release notes from the same feed the updater already reads.
///
/// <para><b>It reuses <see cref="GitHubReleasesFeed"/> rather than calling <c>/releases/latest</c> or
/// a per-tag endpoint</b>, and that is the point: the feed URL, the allow-list, the User-Agent and the
/// response-size cap are all decisions this feature must not get a second, weaker copy of. Design
/// §15's move to another host carries this along with everything else for free.</para>
///
/// <para><b>The version asked for is the RUNNING one, not the newest one.</b> The dialog opens because
/// this build has just been installed, so the notes that matter are its own; the newest release on the
/// feed may be one the user has not been offered yet, and showing its notes would describe an
/// application they are not running.</para>
/// </summary>
public static class ReleaseNotesFetcher
{
    /// <summary>
    /// Where the dialog sends a user whose notes could not be fetched — derived from the feed URL
    /// rather than written down, so the two cannot name different repositories.
    ///
    /// <para><c>https://api.github.com/repos/OWNER/REPO/releases</c> is the API address of the page at
    /// <c>https://github.com/OWNER/REPO/releases</c>. A feed URL of any other shape (a manifest may
    /// have re-pointed it) has no derivable web page, so the API address is offered as-is: a URL that
    /// answers is better than a guess that does not.</para>
    /// </summary>
    public static string BrowseUrl(string feedUrl)
    {
        const string apiPrefix = "https://api.github.com/repos/";

        if (feedUrl.StartsWith(apiPrefix, StringComparison.OrdinalIgnoreCase))
            return "https://github.com/" + feedUrl[apiPrefix.Length..];

        return feedUrl;
    }

    /// <summary>
    /// The page size asked for. GitHub's <c>/releases</c> defaults to <b>30</b>, and a default that
    /// silently truncates the list is the wrong thing to depend on for a lookup that has to work for
    /// <i>any</i> release, not just a recent one — the version being asked about is whichever one the
    /// user has installed, which need not be the newest.
    ///
    /// <para>100 is the API's own maximum. Applied HERE and not to
    /// <see cref="GitHubReleasesFeed.DefaultApiUrl"/>, because that constant is the updater's and this
    /// is not the updater: the update check wants the newest candidate and is correct on one page.
    /// Past 100 releases this degrades to <see cref="ReleaseNotesOutcome.NotPublished"/> with a working
    /// link, which is the honest answer rather than a wrong one.</para>
    /// </summary>
    public const int PageSize = 100;

    /// <summary>
    /// Adds the page size to a feed URL, preserving any query it already carries. Same scheme and same
    /// host, so <see cref="FeedUrlAllowList"/> is unaffected — it is checked against this exact string
    /// inside <see cref="GitHubReleasesFeed"/> either way.
    /// </summary>
    public static string Paged(string feedUrl)
    {
        if (feedUrl.Contains("per_page=", StringComparison.OrdinalIgnoreCase)) return feedUrl;
        return feedUrl + (feedUrl.Contains('?') ? '&' : '?') + "per_page=" + PageSize;
    }

    /// <summary>
    /// Asks the feed for <paramref name="version"/>'s notes. Never throws: every failure is an
    /// <see cref="ReleaseNotesOutcome.Unavailable"/> result the dialog can render, because the only
    /// alternative on this path is an unhandled exception on a background task during launch.
    /// </summary>
    public static async Task<ReleaseNotesResult> FetchAsync(string version, CancellationToken ct = default)
    {
        string feedUrl = UpdateScheduler.FeedUrl();
        string browse  = BrowseUrl(feedUrl);

        try
        {
            using HttpClient http = UpdateDownloader.CreateHttpClient();
            var feed = new GitHubReleasesFeed(http, Paged(feedUrl));

            IReadOnlyList<ReleaseInfo> releases = await feed.ListReleasesAsync(ct).ConfigureAwait(false);
            return Select(releases, version, browse);
        }
        catch (Exception)
        {
            // Offline, DNS failure, rate limit, a body over the size cap, a feed that answered with
            // something that is not a release list. All one thing to the user: we could not fetch it,
            // here is where to look.
            return new ReleaseNotesResult(ReleaseNotesOutcome.Unavailable, version, "", browse);
        }
    }

    /// <summary>
    /// The choosing half, with no network in it.
    ///
    /// <para><b>Matched on parsed version, not on tag text.</b> A release tagged <c>v1.0.0-beta.4</c>
    /// is this build when <c>VERSION</c> says <c>1.0.0-beta.4</c>, and a tag written <c>1.0</c> is
    /// version <c>1.0.0</c> — the same normalisation trap <see cref="ReleaseInfo.VersionText"/>
    /// documents from the other direction. A string comparison would look right and miss.</para>
    ///
    /// <para>A draft is skipped: it is visible only to the publisher, so matching one would show notes
    /// nobody else can see. A prerelease is NOT skipped — every beta is one, and its own notes are
    /// exactly what its users need.</para>
    /// </summary>
    public static ReleaseNotesResult Select(IReadOnlyList<ReleaseInfo> releases, string version, string browseUrl)
    {
        if (!SemanticVersion.TryParse(version, out SemanticVersion? running) || running is null)
            return new ReleaseNotesResult(ReleaseNotesOutcome.NotPublished, version, "", browseUrl);

        foreach (ReleaseInfo r in releases)
        {
            if (r.IsDraft || !r.Version.Equals(running)) continue;

            return string.IsNullOrWhiteSpace(r.Body)
                ? new ReleaseNotesResult(ReleaseNotesOutcome.NotPublished, version, "", browseUrl)
                : new ReleaseNotesResult(ReleaseNotesOutcome.Found, version, r.Body, browseUrl);
        }

        return new ReleaseNotesResult(ReleaseNotesOutcome.NotPublished, version, "", browseUrl);
    }
}
