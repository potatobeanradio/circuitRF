using System.Net;
using System.Net.Http.Headers;

namespace CircuitRF.Design.Net;

/// <summary>How one transfer ended.</summary>
public enum TransferEnd
{
    /// <summary>The server closed the body normally. The partial file holds everything it sent; whether
    /// that is the RIGHT length is the caller's question, because only the caller knows the length.</summary>
    Completed,

    /// <summary>The server answered with a status that is not success. Nothing was written.</summary>
    HttpError,

    /// <summary>No byte arrived for <c>stallTimeout</c>. The partial file is kept for a resume.</summary>
    Stalled,

    /// <summary>More bytes arrived than the cap allows. The partial file is DELETED — it is not a prefix
    /// of anything worth resuming.</summary>
    OverCap,

    /// <summary>The caller's per-chunk callback asked to stop (the updater's free-space re-check). The
    /// partial file is kept.</summary>
    StoppedByCaller,

    /// <summary>The token was cancelled. The partial file is kept.</summary>
    Cancelled,

    /// <summary>A network or file-system failure. The partial file is kept.</summary>
    Failed,
}

/// <summary>What one transfer did.</summary>
/// <param name="End">How it ended.</param>
/// <param name="Transferred">Bytes THIS transfer moved — a resume moves fewer than the file holds.</param>
/// <param name="ResumedFrom">Where the transfer really started: the requested resume point, or 0 when
/// the server ignored the <c>Range</c> header and the partial file was restarted.</param>
/// <param name="StatusCode">The HTTP status, when a response arrived.</param>
/// <param name="Error">The exception's own message for <see cref="TransferEnd.Failed"/>, else null.</param>
public sealed record TransferResult(TransferEnd End, long Transferred, long ResumedFrom, int? StatusCode, string? Error = null);

/// <summary>
/// The body of an HTTP download, written into a <c>.partial</c> file: resumable, bounded, and policed
/// by an IDLE timeout rather than a whole-operation one.
///
/// <para><b>Moved here from <c>src/Ui/Updates/UpdateDownloader</c></b> (brief-em3d-24 R-em3d24-2c), which
/// still calls it: the solver install assistant needs the same transfer from a process with no
/// <c>src/Ui</c> in it (<c>circuitrf solver install</c>), and a copy would be a second loop to keep
/// right. Every rule below was learned by the updater and is recorded there; what stays in the updater
/// is what only an UPDATE needs — the asset-name and URL allow-lists and the free-space re-check, which
/// it runs through <c>keepGoing</c>.</para>
///
/// <list type="bullet">
/// <item><b>An idle timeout, re-armed per read.</b> <see cref="HttpClient.Timeout"/> bounds the whole
/// operation including the body, so a 30 s client timeout is a 30 s budget for a 160 MB payload.</item>
/// <item><b>A server that ignores <c>Range</c></b> answers 200 with the whole file; appending that would
/// give the right length and the wrong content, so the partial is restarted.</item>
/// <item><b>A byte cap</b>, because a server that never closes the connection otherwise writes until the
/// volume is full.</item>
/// </list>
/// </summary>
public static class StreamingDownload
{
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Fetches <paramref name="url"/> into <paramref name="partialPath"/>, appending from
    /// <paramref name="resumeFrom"/> when that is non-zero.
    /// </summary>
    /// <param name="cap">The most bytes the FILE may reach; beyond it the transfer ends
    /// <see cref="TransferEnd.OverCap"/>.</param>
    /// <param name="progress">Told the file's length after each chunk, before the cap is checked.</param>
    /// <param name="keepGoing">Asked after each chunk that is within the cap, with the file's length so
    /// far; false stops the transfer (<see cref="TransferEnd.StoppedByCaller"/>) with the partial kept.
    /// Null never stops.</param>
    /// <param name="onLength">Told the whole file's length once the response arrives, or null when the
    /// server did not say — what a byte counter needs for its denominator.</param>
    public static async Task<TransferResult> TransferAsync(
        HttpClient http, string url, string partialPath, long resumeFrom, long cap, TimeSpan stallTimeout,
        IProgress<long>? progress, Func<long, bool>? keepGoing, CancellationToken ct, Action<long?>? onLength = null)
    {
        long transferred = 0;
        int? status = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

            using HttpResponseMessage res =
                await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            status = (int)res.StatusCode;

            if (resumeFrom > 0 && res.StatusCode != HttpStatusCode.PartialContent)
            {
                resumeFrom = 0;
                try { File.Delete(partialPath); } catch { /* best effort */ }
            }

            if (!res.IsSuccessStatusCode) return new TransferResult(TransferEnd.HttpError, 0, resumeFrom, status);
            onLength?.Invoke(res.Content.Headers.ContentLength is { } body ? resumeFrom + body : null);

            using Stream src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var dst = new FileStream(partialPath,
                                           resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                                           FileAccess.Write, FileShare.None, BufferSize);

            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(stallTimeout);

                int n;
                try
                {
                    n = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await dst.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    return new TransferResult(TransferEnd.Stalled, transferred, resumeFrom, status);
                }

                if (n == 0) break;

                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                transferred += n;
                progress?.Report(resumeFrom + transferred);

                if (resumeFrom + transferred > cap)
                {
                    await dst.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    dst.Close();
                    try { File.Delete(partialPath); } catch { /* the caller's cleanup takes it */ }
                    return new TransferResult(TransferEnd.OverCap, transferred, resumeFrom, status);
                }

                if (keepGoing is not null && !keepGoing(resumeFrom + transferred))
                {
                    await dst.FlushAsync(ct).ConfigureAwait(false);
                    return new TransferResult(TransferEnd.StoppedByCaller, transferred, resumeFrom, status);
                }
            }

            await dst.FlushAsync(ct).ConfigureAwait(false);
            return new TransferResult(TransferEnd.Completed, transferred, resumeFrom, status);
        }
        catch (OperationCanceledException)
        {
            return new TransferResult(TransferEnd.Cancelled, transferred, resumeFrom, status);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            return new TransferResult(TransferEnd.Failed, transferred, resumeFrom, status, e.Message);
        }
    }
}
