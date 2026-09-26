using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>Why a download stopped.</summary>
public enum DownloadOutcome
{
    Completed,

    /// <summary>Free space fell below the requirement mid-transfer. The partial file is kept for a resume.</summary>
    OutOfSpace,

    /// <summary>Network, DNS, timeout, 403 rate-limit. Silent; retried at the next scheduled check.</summary>
    Failed,

    Cancelled,
}

/// <summary>The result of one download attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Path">The completed file, when <see cref="DownloadOutcome.Completed"/>.</param>
/// <param name="BytesTransferred">How much this attempt moved — a resume moves less than the file's size.</param>
public sealed record DownloadResult(DownloadOutcome Outcome, string? Path, long BytesTransferred);

/// <summary>
/// Fetches one release asset into <c>staging/</c>, resumably, re-checking free space as it goes.
///
/// <para><b>Resumable, because of the payload size.</b> A macOS update is a 160 MB transfer; one
/// that restarts from zero on a dropped connection is not acceptable at that size, especially to a
/// user on hotel wifi who never asked for it.</para>
///
/// <para><b>Space is re-checked every ~16 MB</b>, not only at the start. Free space is not a
/// constant: another process can fill the volume during a 160 MB transfer, and a check made only at
/// the start is a check made at the one moment it is least likely to still be true.</para>
///
/// <para><b>Nothing incomplete ever holds a real name.</b> The transfer lands in
/// <c>&lt;name&gt;.partial</c> and is renamed only when the byte count is right.</para>
/// </summary>
public sealed class UpdateDownloader
{
    /// <summary>How often free space is re-measured during a transfer, in the shipping configuration.</summary>
    public const long DefaultSpaceRecheckInterval = 16L * 1024 * 1024;

    /// <summary>
    /// The interval actually used. Settable ONLY so a test can drive the re-check loop without a
    /// 32 MB payload — the shipping value is <see cref="DefaultSpaceRecheckInterval"/> and nothing in
    /// the application changes it.
    /// </summary>
    public long SpaceRecheckInterval { get; init; } = DefaultSpaceRecheckInterval;

    /// <summary>
    /// Which payload URLs may be fetched. The shipping value is
    /// <see cref="FeedUrlAllowList.IsAcceptable"/> — absolute <c>https</c> on a compiled-in host, or
    /// on any host at all once this build carries a release key (design §15.5) — and
    /// <b>nothing in the application replaces it</b>; it is settable only so the download tests can
    /// reach their own loopback listener, which by construction is neither.
    /// </summary>
    public Func<string?, bool> UrlIsAllowed { get; init; } = FeedUrlAllowList.IsAcceptable;

    /// <summary>
    /// The cap actually applied to a transfer. The shipping value is <see cref="MaxPayloadBytes"/>
    /// and nothing in the application changes it; it is settable only so the cap can be TESTED
    /// against a 300 KB fixture rather than by moving two gigabytes.
    /// </summary>
    public long MaxTransferBytes { get; init; } = MaxPayloadBytes;

    /// <summary>
    /// The most bytes any release asset may be, advertised or actually transferred.
    ///
    /// <para><b>Both halves matter and neither was bounded</b> (security review, 2026-08-25). The
    /// advertised size feeds <see cref="UpdateSpace.EstimateExpandedBytes"/>, whose <c>checked</c>
    /// multiply turns an absurd figure into an exception rather than an answer; and when a feed
    /// publishes NO size the read loop had no stop condition at all, so a server that never closes
    /// the connection writes until the volume is down to the 1 GB reserve — the one outcome design
    /// §13 exists to prevent, arriving from the network rather than from the arithmetic.</para>
    ///
    /// <para>2 GB is roughly four times the largest artifact any of the three packaging pipelines
    /// has ever produced (the arm64 <c>.dmg</c>, 160 MB), so it constrains nothing real.</para>
    /// </summary>
    public const long MaxPayloadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// How long a transfer may make no progress at all before it is abandoned. This is an IDLE
    /// timeout, reset on every chunk that arrives — not a budget for the whole download.
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly IFreeSpaceProbe _space;

    public UpdateDownloader(HttpClient http, IFreeSpaceProbe space)
    {
        _http  = http;
        _space = space;
    }

    /// <summary>How many times free space was measured during the last transfer — the counter the
    /// re-check test reads, in place of a wall-clock assertion.</summary>
    public int SpaceChecks { get; private set; }

    public async Task<DownloadResult> DownloadAsync(
        ReleaseAsset asset,
        string stagingDirectory,
        long requiredFreeBytes,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        SpaceChecks = 0;

        // The name is written by whoever published the release and is about to become a path. A
        // separator or a `..` in it writes outside the one directory the updater owns — so it is
        // checked HERE as well as in UpdateManifest, because this is the line that builds the path
        // and no caller should have to remember (added in a second review, 2026-08-25).
        if (!UpdateAssetNames.IsSafeAssetFileName(asset.Name))
            return new DownloadResult(DownloadOutcome.Failed, null, 0);

        // The URL is checked HERE, at the one line that turns it into a request, for the same reason
        // the name is: no caller should have to remember. UpdateManifest already allow-lists the URL
        // it supplies, but the FEED's own asset URLs went straight through unexamined — including a
        // plain `http` one, which is exactly what an on-path attacker would want to substitute, and
        // including any host at all (security review, 2026-08-25). On macOS and Windows the
        // publisher-identity check would still refuse the payload; on Linux there is no such check,
        // so an unconstrained URL there is the whole of the trust chain.
        if (!UrlIsAllowed(asset.Url))
            return new DownloadResult(DownloadOutcome.Failed, null, 0);

        // An absurd advertised size is refused before it reaches the space arithmetic, which
        // multiplies it.
        if (asset.Size < 0 || asset.Size > MaxTransferBytes)
            return new DownloadResult(DownloadOutcome.Failed, null, 0);

        Directory.CreateDirectory(stagingDirectory);

        string finalPath   = Path.Combine(stagingDirectory, asset.Name);
        string partialPath = finalPath + UpdatePaths.PartialSuffix;

        long resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        // A partial larger than the asset is debris from a different release that happened to share
        // a name; start again rather than resuming into the middle of the wrong file.
        if (asset.Size > 0 && resumeFrom > asset.Size)
        {
            try { File.Delete(partialPath); } catch { /* best effort */ }
            resumeFrom = 0;
        }

        if (asset.Size > 0 && resumeFrom == asset.Size)
        {
            AtomicFile.ReplaceOrMove(partialPath, finalPath);
            return new DownloadResult(DownloadOutcome.Completed, finalPath, 0);
        }

        // The transfer itself — the idle timeout, the Range fallback and the byte cap — is
        // CircuitRF.Design.Net.StreamingDownload, below the firewall since brief-em3d-24 so the solver
        // install assistant runs the same loop headlessly. What stays here is what only an update
        // needs: the free-space re-check every SpaceRecheckInterval bytes.
        long cap = asset.Size > 0 ? asset.Size : MaxTransferBytes;
        // Counted per attempt from zero, as the loop always did — a server that ignores Range restarts
        // the file, and the running total then goes DOWN, which must not skip a check.
        long lastTotal = resumeFrom, sinceCheck = 0;
        var transfer = await CircuitRF.Design.Net.StreamingDownload.TransferAsync(
            _http, asset.Url!, partialPath, resumeFrom, cap, StallTimeout, progress,
            keepGoing: total =>
            {
                sinceCheck += total >= lastTotal ? total - lastTotal : total;
                lastTotal = total;
                if (sinceCheck < SpaceRecheckInterval) return true;
                sinceCheck = 0;
                SpaceChecks++;
                // The partial stays when space runs out: it costs nothing, the next attempt resumes
                // from it, and the launch-time reclaim removes it if the update never happens.
                return _space.AvailableFreeSpace(stagingDirectory) >= requiredFreeBytes;
            },
            ct).ConfigureAwait(false);
        long transferred = transfer.Transferred;

        switch (transfer.End)
        {
            case CircuitRF.Design.Net.TransferEnd.Completed:       break;
            case CircuitRF.Design.Net.TransferEnd.Cancelled:       return new DownloadResult(DownloadOutcome.Cancelled, null, transferred);
            case CircuitRF.Design.Net.TransferEnd.StoppedByCaller: return new DownloadResult(DownloadOutcome.OutOfSpace, null, transferred);
            default:                                               return new DownloadResult(DownloadOutcome.Failed, null, transferred);
        }

        // Only a file of exactly the advertised length gets the real name. A size of 0 means the feed
        // did not say, in which case there is nothing to check and the signature is what catches a
        // truncated payload.
        long got = new FileInfo(partialPath).Length;
        if (asset.Size > 0 && got != asset.Size)
            return new DownloadResult(DownloadOutcome.Failed, null, transferred);

        AtomicFile.ReplaceOrMove(partialPath, finalPath);
        return new DownloadResult(DownloadOutcome.Completed, finalPath, transferred);
    }

    /// <summary>
    /// The <c>User-Agent</c> the GitHub API requires. <b>Deliberately minimal</b>: no machine
    /// identifier, no telemetry, no usage data. Some of this application's users work under
    /// export-controlled or otherwise restricted network policies and will be asked by their IT
    /// department exactly what the binary contacts; the honest answer has to stay short.
    /// </summary>
    public static string UserAgent => $"{UpdateApp.Name}/{AppVersion.Display}";

    /// <summary>
    /// The client for the FEED — a small JSON document, so a whole-operation timeout is exactly
    /// right and 30 seconds of it is generous.
    /// </summary>
    /// <summary>
    /// The most bytes a feed document — the release list, or an <c>update-manifest.json</c> — may be.
    ///
    /// <para>Both are read with <c>ReadAsStringAsync</c>, which buffers the whole body, and neither
    /// was bounded: a host serving an endless response would have been answered by allocating until
    /// the process died (security review, 2026-08-25). A release list for this repository is tens of
    /// kilobytes and a manifest is a few hundred bytes.</para>
    /// </summary>
    public const long MaxFeedResponseBytes = 8L * 1024 * 1024;

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxFeedResponseBytes,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// The client for the PAYLOAD, and it is a separate one on purpose.
    ///
    /// <para><see cref="HttpClient.Timeout"/> bounds the whole operation, response body included —
    /// so the feed's 30 seconds would be 30 seconds to move 160 MB. Progress is policed by
    /// <see cref="StallTimeout"/> in the read loop instead, which is the question actually worth
    /// asking of a large transfer: not "has it finished yet" but "is it still arriving".</para>
    /// </summary>
    public static HttpClient CreateDownloadHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }
}
