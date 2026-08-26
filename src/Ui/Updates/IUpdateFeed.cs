using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// Where the list of releases comes from. One shipping implementation
/// (<see cref="GitHubReleasesFeed"/>); the interface exists so every test in this repository can run
/// against canned JSON and <b>no test ever makes a network call</b>, and so that design §15's move
/// off GitHub is a second file rather than a rewrite.
/// </summary>
public interface IUpdateFeed
{
    /// <summary>
    /// Every release the host knows about, newest-first or not — <see cref="UpdateSelector"/> sorts.
    /// Drafts may be included; the selector drops them.
    /// </summary>
    Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct);

    /// <summary>
    /// Fetches a small asset as <b>bytes</b> — the manifest and its detached signature.
    ///
    /// <para>Bytes, not text, because the signature covers the file as served. Decoding to a string
    /// and re-encoding is a round trip that a BOM, a lone CR or anything not valid UTF-8 does not
    /// survive, and a verification step whose input is a re-encoding of its input is not verifying
    /// what was served.</para>
    /// </summary>
    Task<byte[]?> GetAssetBytesAsync(ReleaseAsset asset, CancellationToken ct);
}
