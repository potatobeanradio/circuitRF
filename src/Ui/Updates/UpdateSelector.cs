using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>What a check decided, and everything the download step needs.</summary>
/// <param name="Release">The release chosen.</param>
/// <param name="Asset">The single asset this application, platform and architecture accepts.</param>
/// <param name="FromManifest">True when the release's <c>update-manifest.json</c> supplied the asset.</param>
/// <param name="FeedUrl">A manifest's accepted <c>feedUrl</c>, to be persisted for next time; null otherwise.</param>
/// <param name="ManifestSigned">True when the manifest carried a signature valid under the compiled-in
/// release key — which is what makes the payload's SHA-256 a real integrity guarantee on every
/// platform rather than a best-effort check served by the same host as the bytes (design §15.5).</param>
public sealed record UpdateCandidate(
    ReleaseInfo Release,
    ReleaseAsset Asset,
    bool FromManifest,
    string? FeedUrl,
    bool ManifestSigned = false);

/// <summary>
/// Turns a release list into "the one update to fetch, or nothing".
///
/// <para>Two rules do most of the work, and both look like bugs from the outside:</para>
/// <list type="bullet">
/// <item><b>Never offer a version that is not strictly greater than the running one.</b> No equal, no
/// lower, no "reinstall". A user on <c>1.0.0-beta.3</c> whose channel's newest stable is <c>0.9.0</c>
/// is offered nothing — that is what stops the beta channel from silently downgrading people.</item>
/// <item><b>Channels are the GitHub prerelease flag and nothing else.</b> No second list, no naming
/// convention, no maintained channel file. Drafts are always excluded.</item>
/// </list>
/// </summary>
public static class UpdateSelector
{
    /// <summary>
    /// The pure half: pick the newest eligible release. No network, no manifest — the manifest hook
    /// needs one fetch and lives in <see cref="SelectAsync"/>.
    /// </summary>
    public static ReleaseInfo? SelectRelease(
        IEnumerable<ReleaseInfo> releases, SemanticVersion running, bool includeBetas)
        => releases
            .Where(r => !r.IsDraft)                 // a draft is not published, on either channel
            .Where(r => r.HasUsableVersionText)     // the tag's own spelling becomes a path segment
            .Where(r => includeBetas || !r.IsPreRelease)
            .Where(r => r.Version > running)        // strictly greater — never equal, never lower
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

    /// <summary>
    /// The full check: choose a release, then honour its manifest if it published one, falling back
    /// to name matching silently when it did not — which is the normal case today.
    /// </summary>
    public static async Task<UpdateCandidate?> SelectAsync(
        IUpdateFeed feed,
        IReadOnlyList<ReleaseInfo> releases,
        SemanticVersion running,
        bool includeBetas,
        string app,
        UpdatePlatform platform,
        Architecture arch,
        CancellationToken ct,
        ReleaseTrust? trust = null)
    {
        trust ??= ReleaseTrust.Compiled;

        ReleaseInfo? release = SelectRelease(releases, running, includeBetas);
        if (release is null) return null;

        string archToken;
        try { archToken = UpdateAssetNames.ArchToken(platform, arch); }
        catch (PlatformNotSupportedException) { return null; }

        string wantedPrimary  = UpdateAssetNames.Expected(app, release.VersionText, platform, archToken);
        string wantedFallback = UpdateAssetNames.Expected(app, release.Version.ToString(), platform, archToken);

        ReleaseAsset? manifestAsset = null;
        string? feedUrl = null;
        bool signed = false;

        ReleaseAsset? manifestFile = release.Manifest;
        if (manifestFile is not null)
        {
            byte[]? bytes = await feed.GetAssetBytesAsync(manifestFile, ct).ConfigureAwait(false);
            UpdateManifest? m = UpdateManifest.TryParse(bytes);

            if (m is not null)
            {
                // The signature covers the BYTES as served, so it is checked against `bytes` and not
                // against anything reconstructed from the parse.
                signed = await VerifySignatureAsync(feed, release, bytes!, trust, ct).ConfigureAwait(false);
                m.SignatureVerified = signed;

                // minimumUpgradableFrom is a refusal, not a fallback: a release that says this client
                // may not jump to it directly means it, and quietly name-matching around that would
                // defeat the only reason the field exists.
                if (!m.AllowsUpgradeFrom(running)) return null;

                manifestAsset = m.Select(wantedPrimary) ?? m.Select(wantedFallback);

                // A SIGNED manifest may re-point the feed anywhere over https — that is design
                // §15.4's migration, and it only works because §15.5's signature makes the host
                // untrusted for integrity. An UNSIGNED one is held to the compiled-in allow-list, and
                // a feedUrl off it is DROPPED rather than obeyed and rather than fatal: the update
                // itself is still fine, we simply keep asking the feed we already trust.
                if (signed ? IsHttps(m.FeedUrl) : FeedUrlAllowList.IsAllowed(m.FeedUrl))
                    feedUrl = m.FeedUrl;
            }
        }

        // R-AU-15.5. With a release key compiled in, an UNSIGNED release is not a candidate at all —
        // and that has to be unconditional rather than "check the signature if there is one", because
        // an attacker who can publish a release can publish one with no manifest, and an updater that
        // reads the absence of a signature as "nothing to check" has learned nothing from checking.
        if (trust.RequireSignedManifest && (!signed || manifestAsset is null)) return null;

        ReleaseAsset? asset = manifestAsset ?? UpdateAssetNames.Select(release, app, platform, arch);
        if (asset is null) return null;

        return new UpdateCandidate(release, asset, manifestAsset is not null, feedUrl, signed);
    }

    private static bool IsHttps(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Fetches the detached signature asset and checks it against the compiled-in release key.
    ///
    /// <para>False for every reason there is — no key compiled in, no signature asset, an oversized
    /// one, bytes that do not verify. The caller turns that into "unsigned", and what "unsigned"
    /// then means is <see cref="ReleaseKeys.RequireSignedManifest"/>'s decision, in one place.</para>
    /// </summary>
    private static async Task<bool> VerifySignatureAsync(
        IUpdateFeed feed, ReleaseInfo release, byte[] manifestBytes, ReleaseTrust trust,
        CancellationToken ct)
    {
        ReleaseAsset? sigAsset = release.ManifestSignature;
        if (sigAsset is null) return false;

        byte[]? sig = await feed.GetAssetBytesAsync(sigAsset, ct).ConfigureAwait(false);
        if (sig is null || sig.Length == 0 || sig.Length > UpdateManifest.MaxSignatureBytes) return false;

        string text;
        try   { text = System.Text.Encoding.UTF8.GetString(sig).Trim(); }
        catch (ArgumentException) { return false; }

        return trust.Verify(manifestBytes, text);
    }
}
