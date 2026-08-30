using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.Updates;

/// <summary>One downloadable file attached to a release.</summary>
/// <param name="Name">The asset file name, which is what <see cref="UpdateAssetNames"/> matches on.</param>
/// <param name="Url">Where to fetch it from.</param>
/// <param name="Size">Bytes, as the feed reports them — used by the pre-flight space check.</param>
/// <param name="Sha256">Lower-case hex, when the feed publishes one. Best-effort: the code
/// signature is the guarantee, not the hash (design §9).</param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256 = null);

/// <summary>
/// One release as the feed describes it. Deliberately not GitHub-shaped: a second
/// <see cref="IUpdateFeed"/> implementation pointed at another host produces the same type, which is
/// what makes design §15's migration a single new file.
/// </summary>
public sealed record ReleaseInfo(
    string TagName,
    SemanticVersion Version,
    bool IsPreRelease,
    bool IsDraft,
    IReadOnlyList<ReleaseAsset> Assets,
    string Body = "")
{
    /// <summary>
    /// The version exactly as the tag spells it, with a leading <c>v</c> removed — which is what the
    /// artifact <b>file names</b> carry, because the packaging scripts interpolate the repo-root
    /// <c>VERSION</c> file verbatim (<c>packaging/version.sh</c>'s <c>CRF_VERSION</c>).
    ///
    /// <para>It is deliberately NOT <c>Version.ToString()</c>: a <c>VERSION</c> of <c>1.0</c> is a
    /// version <see cref="SemanticVersion"/> normalises to <c>1.0.0</c>, while the file on the release
    /// is still named <c>circuitRF-1.0-arm64.dmg</c>. Normalising here would look right and match
    /// nothing.</para>
    /// </summary>
    public string VersionText { get; } =
        TagName.Length > 1 && (TagName[0] == 'v' || TagName[0] == 'V') ? TagName[1..] : TagName;

    /// <summary>
    /// True when <see cref="VersionText"/> is safe to use as a path segment. A release whose tag is
    /// not — leading or trailing whitespace is the reachable case, since
    /// <see cref="SemanticVersion.TryParse"/> trims before it validates — is not a candidate at all
    /// (<see cref="UpdateSelector.SelectRelease"/>), because that string becomes
    /// <c>&lt;install root&gt;/app-&lt;ver&gt;</c> and <c>updates/staged/&lt;ver&gt;/</c>.
    /// </summary>
    public bool HasUsableVersionText => UpdateInstallSite.IsSafeVersionText(VersionText);

    /// <summary>
    /// The release notes, as the publisher typed them — Markdown, and the only field here that is
    /// free text rather than something the updater makes a decision from.
    ///
    /// <para>Defaulted rather than required, because it is read by exactly one caller
    /// (<see cref="ReleaseNotesFetcher"/>) and no part of choosing, verifying or installing an update
    /// may ever come to depend on it: a release with an empty body must still update.</para>
    /// </summary>
    public string Body { get; init; } = Body;

    /// <summary>The asset named exactly <c>update-manifest.json</c>, if the release carries one.</summary>
    public ReleaseAsset? Manifest => Named(UpdateManifest.AssetName);

    /// <summary>Its detached signature, <c>update-manifest.json.sig</c> (design §15.5).</summary>
    public ReleaseAsset? ManifestSignature => Named(UpdateManifest.SignatureAssetName);

    private ReleaseAsset? Named(string name)
    {
        foreach (ReleaseAsset a in Assets)
            if (string.Equals(a.Name, name, StringComparison.Ordinal))
                return a;
        return null;
    }
}
