using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Updates;

/// <summary>One asset as an <see cref="UpdateManifest"/> describes it.</summary>
public sealed class UpdateManifestAsset
{
    [JsonPropertyName("name")]   public string? Name   { get; set; }
    [JsonPropertyName("url")]    public string? Url    { get; set; }
    [JsonPropertyName("size")]   public long?   Size   { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

/// <summary>
/// The optional <c>update-manifest.json</c> asset. <b>We publish none today</b> — cutting a release
/// is still "upload the installers" — but every shipped client already knows how to obey one, and
/// that is the entire migration path off GitHub (design §15.4).
///
/// <para><b>Why implement it before it is needed.</b> An already-installed copy asks the feed it was
/// compiled against. Turn GitHub off with no manifest support in the field and every client older
/// than the migration is stranded on its last known version, silently, forever — the worst kind of
/// failure, because nobody notices. With it, one small asset published to GitHub re-points the whole
/// installed base. It is roughly twenty lines and it is only ever cheap <i>before</i> it is needed.</para>
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>The exact asset name a release must use for the manifest to be honoured.</summary>
    public const string AssetName = "update-manifest.json";

    /// <summary>
    /// The detached signature's asset name — the manifest's, plus <c>.sig</c>. Its content is base64
    /// of an ECDSA P-256 / SHA-256 signature over the manifest file's bytes as served
    /// (<see cref="ReleaseKeys"/>).
    /// </summary>
    public const string SignatureAssetName = AssetName + ".sig";

    /// <summary>The most bytes a signature asset may be. A P-256 DER signature is about 72.</summary>
    public const int MaxSignatureBytes = 1024;

    [JsonPropertyName("assets")]
    public List<UpdateManifestAsset>? Assets { get; set; }

    /// <summary>
    /// The oldest version that may upgrade straight to this release. A client below it is offered
    /// nothing rather than a jump the release notes say is unsupported.
    /// </summary>
    [JsonPropertyName("minimumUpgradableFrom")]
    public string? MinimumUpgradableFrom { get; set; }

    /// <summary>
    /// Where to ask next time. <b>Not a blind redirect</b> — see <see cref="FeedUrlAllowList"/>:
    /// a field that lets a release point the updater at an arbitrary host is a field that lets a
    /// <i>compromised</i> release point it at an arbitrary host.
    /// </summary>
    [JsonPropertyName("feedUrl")]
    public string? FeedUrl { get; set; }

    /// <summary>
    /// <b>Not used, and deliberately still parsed.</b> This field was reserved for design §15.5's
    /// signature before that was built; the signature that shipped is <i>detached</i>, in the
    /// <see cref="SignatureAssetName"/> asset, because a signature carried inside the document it
    /// signs needs a canonicalisation rule — a second specification, which two programs written
    /// years apart both have to get exactly right. Signing the bytes as served has no such rule.
    ///
    /// <para>It stays parsed so that a manifest carrying it — one written against the earlier note —
    /// is not rejected as malformed. Nothing reads it.</para>
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>
    /// True when this manifest arrived with a signature that verifies against the compiled-in release
    /// key. Set by <see cref="UpdateSelector"/> at the moment of verification and never serialised —
    /// it is a property of THIS fetch, not of the document.
    ///
    /// <para>It is what decides whether the manifest may name a payload outside
    /// <see cref="FeedUrlAllowList"/>: a signed manifest makes the host untrusted for integrity on
    /// every platform, which is exactly what design §15.5 said mirroring and the move off GitHub
    /// would need.</para>
    /// </summary>
    [JsonIgnore]
    public bool SignatureVerified { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses a manifest, returning null for anything that is not one. Never throws.</summary>
    public static UpdateManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try   { return JsonSerializer.Deserialize<UpdateManifest>(json, Opts); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The same, from the BYTES as served — which is what the feed hands over, because those are the
    /// bytes the detached signature covers.
    /// </summary>
    public static UpdateManifest? TryParse(byte[]? json)
    {
        if (json is null || json.Length == 0) return null;
        try   { return JsonSerializer.Deserialize<UpdateManifest>(json, Opts); }
        catch (Exception e) when (e is JsonException or System.Text.DecoderFallbackException) { return null; }
    }

    /// <summary>
    /// The asset this application wants, by exact name, from the manifest's own list. Null when the
    /// manifest carries no entry for it — in which case the caller falls back to name matching.
    /// </summary>
    /// <remarks>
    /// <para><b>The asset's own <c>url</c> is allow-listed exactly as <see cref="FeedUrl"/> is</b>
    /// (added in a second review, 2026-08-25 — it was not, and the reasoning that justifies the
    /// <c>feedUrl</c> allow-list applies to this field <i>more</i> strongly, not less). <c>feedUrl</c>
    /// redirects where we ask next; this one redirects where the <b>payload</b> comes from. On macOS
    /// and Windows the publisher-identity check would still refuse a substituted binary, but
    /// <c>UpdateService.VerifyStagedAsync</c> answers <see cref="VerifyOutcome.NotApplicable"/> on
    /// Linux — there is no signing infrastructure to ask — so on that platform an unconstrained URL
    /// is the whole of the trust chain.</para>
    ///
    /// <para>An entry that fails either check is <b>skipped</b>, not fatal: the caller falls back to
    /// name matching against the release's own assets, which is the normal path today anyway.</para>
    /// </remarks>
    public ReleaseAsset? Select(string wantedName)
    {
        if (Assets is null) return null;

        foreach (UpdateManifestAsset a in Assets)
        {
            if (a.Name is null || a.Url is null) continue;
            if (!string.Equals(a.Name, wantedName, StringComparison.Ordinal)) continue;

            if (!UpdateAssetNames.IsSafeAssetFileName(a.Name)) continue;

            // A SIGNED manifest may name any https host: its integrity no longer depends on where it
            // came from, which is the whole of design §15.5's "mirroring becomes free". An UNSIGNED
            // one is constrained to the compiled-in list, because there TLS to a host we chose is all
            // there is.
            if (!(SignatureVerified ? IsHttps(a.Url) : FeedUrlAllowList.IsAllowed(a.Url))) continue;

            // A signed manifest that names no hash for the asset is a signature over nothing that
            // matters: the signature proves the manifest, and only the hash carries that proof
            // through to the bytes. Refuse rather than fall back to name matching, which would
            // silently drop back to the unsigned path.
            string? sha = a.Sha256?.Trim().ToLowerInvariant();
            if (SignatureVerified && !IsSha256Hex(sha)) continue;

            return new ReleaseAsset(a.Name, a.Url, a.Size ?? 0, sha);
        }
        return null;
    }

    private static bool IsHttps(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>Exactly 64 lower-case hex characters. A short or malformed digest is not a digest.</summary>
    public static bool IsSha256Hex(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (char c in s)
            if (!char.IsAsciiDigit(c) && c is < 'a' or > 'f') return false;
        return true;
    }

    /// <summary>
    /// True when <paramref name="running"/> is allowed to upgrade straight to this release. An
    /// unparseable <see cref="MinimumUpgradableFrom"/> is treated as absent rather than as a refusal:
    /// a typo in a manifest must not brick the update path for everyone.
    /// </summary>
    public bool AllowsUpgradeFrom(SemanticVersion running)
        => !SemanticVersion.TryParse(MinimumUpgradableFrom, out SemanticVersion? min) || min is null
           || running >= min;
}
