using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-24 / R-AU-27 / R-AU-28 — resumable transfers, the space re-check, and the naming discipline.
///
/// <para><b>No test here makes a network call.</b> The bytes come from an <see cref="HttpListener"/>
/// bound to the loopback interface inside the test process; nothing leaves the machine and nothing
/// is resolved through DNS. That is the only way an HTTP path can be exercised under the "no test in
/// this repository may make a network call" rule, which is about reaching the outside world.</para>
/// </summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "crf-dl-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly byte[] _payload;
    private readonly CancellationTokenSource _serverStop = new();

    /// <summary>When true the server answers 200 with the whole file even to a Range request.</summary>
    public bool IgnoreRange { get; set; }

    public UpdateDownloaderTests()
    {
        Directory.CreateDirectory(_tmp);

        _payload = new byte[300 * 1024];
        for (int i = 0; i < _payload.Length; i++) _payload[i] = (byte)(i % 251);

        // A free loopback port, found by binding one and letting the OS choose.
        int port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _serverStop.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task ServeAsync()
    {
        while (!_serverStop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            try
            {
                string? range = ctx.Request.Headers["Range"];
                int from = 0;

                if (range is not null && !IgnoreRange &&
                    range.StartsWith("bytes=", StringComparison.Ordinal) &&
                    int.TryParse(range[6..].TrimEnd('-'), out int start))
                {
                    from = start;
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {from}-{_payload.Length - 1}/{_payload.Length}";
                }

                ctx.Response.ContentLength64 = _payload.Length - from;
                await ctx.Response.OutputStream.WriteAsync(_payload.AsMemory(from));
            }
            catch { /* the client went away */ }
            finally { try { ctx.Response.Close(); } catch { /* already closed */ } }
        }
    }

    /// <summary>What the fixture's own listener answers to, and nothing else.</summary>
    private bool Loopback(string? url)
        => url is not null && url.StartsWith(_prefix, StringComparison.Ordinal);

    private ReleaseAsset Asset(string name = "circuitRF-1.0.0-arm64.dmg")
        => new(name, _prefix + name, _payload.Length);

    private (UpdateDownloader D, FakeFreeSpaceProbe P) New(long available, long? recheckEvery = null)
    {
        var probe = new FakeFreeSpaceProbe(available);

        // The shipping allow-list refuses this fixture's own loopback listener, which is the point of
        // it — an `http://` URL on an unknown host is exactly what it exists to stop. The refusal
        // itself is pinned by ARefusedUrl_IsNotFetchedAtAll below, against the real default.
        var d = recheckEvery is null
            ? new UpdateDownloader(new HttpClient(), probe) { UrlIsAllowed = Loopback }
            : new UpdateDownloader(new HttpClient(), probe)
              { UrlIsAllowed = Loopback, SpaceRecheckInterval = recheckEvery.Value };
        return (d, probe);
    }

    [Fact]
    public async Task ACompleteTransfer_LandsUnderItsRealNameAndMatchesTheBytes()
    {
        (UpdateDownloader d, _) = New(long.MaxValue);

        DownloadResult r = await d.DownloadAsync(Asset(), _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, r.Outcome);
        Assert.Equal(Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg"), r.Path);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(r.Path!));

        // Nothing incomplete is left holding any name at all.
        Assert.Empty(Directory.GetFiles(_tmp, "*" + UpdatePaths.PartialSuffix));
    }

    /// <summary>
    /// R-AU-24. A 160 MB transfer that restarts from zero on a dropped connection is not acceptable
    /// at this payload size — least of all to a user on hotel wifi who never asked for it.
    /// </summary>
    [Fact]
    public async Task AnInterruptedTransfer_ResumesRatherThanRestarting()
    {
        // Half a file already on disk, under the .partial name a previous attempt left it.
        string partial = Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg" + UpdatePaths.PartialSuffix);
        int half = _payload.Length / 2;
        await File.WriteAllBytesAsync(partial, _payload.Take(half).ToArray());

        (UpdateDownloader d, _) = New(long.MaxValue);
        DownloadResult r = await d.DownloadAsync(Asset(), _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, r.Outcome);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(r.Path!));

        // The counter that proves it RESUMED: only the second half moved.
        Assert.Equal(_payload.Length - half, r.BytesTransferred);
    }

    /// <summary>
    /// A server that ignores the Range header answers 200 with the WHOLE file. Appending that to a
    /// half-finished file gives a result of the right length and the wrong content — so the partial
    /// is discarded and the transfer starts over.
    /// </summary>
    [Fact]
    public async Task AServerThatIgnoresRange_IsHandledByStartingOver_NotByAppending()
    {
        IgnoreRange = true;

        string partial = Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg" + UpdatePaths.PartialSuffix);
        await File.WriteAllBytesAsync(partial, _payload.Take(_payload.Length / 2).ToArray());

        (UpdateDownloader d, _) = New(long.MaxValue);
        DownloadResult r = await d.DownloadAsync(Asset(), _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, r.Outcome);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(r.Path!));
        Assert.Equal(_payload.Length, r.BytesTransferred);
    }

    [Fact]
    public async Task AnAlreadyCompletePartial_IsPromotedWithoutTransferringAnything()
    {
        string partial = Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg" + UpdatePaths.PartialSuffix);
        await File.WriteAllBytesAsync(partial, _payload);

        (UpdateDownloader d, _) = New(long.MaxValue);
        DownloadResult r = await d.DownloadAsync(Asset(), _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, r.Outcome);
        Assert.Equal(0, r.BytesTransferred);
    }

    [Fact]
    public async Task APartialLargerThanTheAsset_IsDiscardedRatherThanResumedInto()
    {
        string partial = Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg" + UpdatePaths.PartialSuffix);
        await File.WriteAllBytesAsync(partial, new byte[_payload.Length + 4096]);

        (UpdateDownloader d, _) = New(long.MaxValue);
        DownloadResult r = await d.DownloadAsync(Asset(), _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, r.Outcome);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(r.Path!));
    }

    /// <summary>
    /// R-AU-24's second half. Free space is not a constant: another process can fill the volume
    /// during a transfer, and a check made only at the start is a check made at the one moment it is
    /// least likely to still be true. Asserted as a COUNTER, not a duration.
    /// </summary>
    [Fact]
    public async Task FreeSpaceIsReMeasuredDuringTheTransfer_NotOnlyAtTheStart()
    {
        // Asserted as a COUNTER, never a duration: the interval is lowered so a small payload
        // crosses it several times, rather than writing 32 MB to prove the loop exists.
        (UpdateDownloader d, FakeFreeSpaceProbe probe) = New(long.MaxValue, recheckEvery: 64 * 1024);

        await d.DownloadAsync(Asset(), _tmp, 0, null, CancellationToken.None);

        // The check is made between reads, and the read buffer is 128 KB, so a 300 KB payload gives
        // two of them however small the interval is. Two is enough: the property is that space is
        // re-measured DURING the transfer and not only before it.
        Assert.True(d.SpaceChecks >= 2, $"expected re-checks during the transfer, got {d.SpaceChecks}");
        Assert.Equal(d.SpaceChecks, probe.Calls);

        // ...and the SHIPPING interval is the 16 MB the design names.
        Assert.Equal(16L * 1024 * 1024, UpdateDownloader.DefaultSpaceRecheckInterval);
        Assert.Equal(UpdateDownloader.DefaultSpaceRecheckInterval,
                     new UpdateDownloader(new HttpClient(), probe).SpaceRecheckInterval);
    }

    [Fact]
    public async Task RunningOutOfSpaceMidTransfer_StopsAndKeepsThePartialForAResume()
    {
        (UpdateDownloader d, _) = New(available: 0, recheckEvery: 64 * 1024);

        DownloadResult r = await d.DownloadAsync(Asset(), _tmp, long.MaxValue, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.OutOfSpace, r.Outcome);

        // The partial STAYS: it costs nothing, the next attempt resumes from it, and the launch-time
        // reclaim removes it if the update never happens.
        string partial = Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg" + UpdatePaths.PartialSuffix);
        Assert.True(File.Exists(partial));
        Assert.True(new FileInfo(partial).Length > 0);

        // And nothing was given the real name.
        Assert.False(File.Exists(Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg")));
    }

    [Fact]
    public async Task ATruncatedTransfer_NeverGetsTheRealName()
    {
        // The feed advertises more bytes than the server has: the length check refuses to promote.
        var asset = new ReleaseAsset("circuitRF-1.0.0-arm64.dmg", _prefix + "x.dmg", _payload.Length + 1);

        (UpdateDownloader d, _) = New(long.MaxValue);
        DownloadResult r = await d.DownloadAsync(asset, _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg")));
        Assert.True(File.Exists(Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg" + UpdatePaths.PartialSuffix)));
    }

    // ── the security review's own refusals, 2026-08-25 ──────────────────────────────────────

    /// <summary>
    /// The SHIPPING allow-list, not the fixture's. A payload URL is checked at the one line that
    /// turns it into a request — plain <c>http</c> is exactly what an on-path attacker would want to
    /// substitute, and an arbitrary host is the whole of the trust chain on Linux, where there is no
    /// platform signature to fall back on.
    /// </summary>
    [Theory]
    [InlineData("http://github.com/potatobeanradio/circuitRF/releases/download/v1/x.dmg")]  // scheme
    [InlineData("https://evil.example/x.dmg")]                                              // host
    [InlineData("https://github.com.evil.example/x.dmg")]                                   // near-miss host
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url at all")]
    public async Task ARefusedUrl_IsNotFetchedAtAll(string url)
    {
        var probe = new FakeFreeSpaceProbe(long.MaxValue);
        var d = new UpdateDownloader(new HttpClient(), probe);      // the real UrlIsAllowed

        DownloadResult r = await d.DownloadAsync(
            new ReleaseAsset("circuitRF-1.0.0-arm64.dmg", url, 10), _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, r.Outcome);
        Assert.Equal(0, r.BytesTransferred);
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    /// <summary>The URLs GitHub actually serves a release asset from are all allow-listed.</summary>
    [Theory]
    [InlineData("https://api.github.com/repos/potatobeanradio/circuitRF/releases")]
    [InlineData("https://github.com/potatobeanradio/circuitRF/releases/download/v1.0.0/circuitRF-1.0.0-arm64.dmg")]
    [InlineData("https://objects.githubusercontent.com/x")]
    [InlineData("https://release-assets.githubusercontent.com/x")]
    public void TheRealFeedAndPayloadHostsAreAllowed(string url) => Assert.True(FeedUrlAllowList.IsAllowed(url));

    /// <summary>
    /// An advertised size feeds <c>UpdateSpace.EstimateExpandedBytes</c>, which multiplies it. A
    /// figure no artifact of ours has ever approached is refused before it gets there.
    /// </summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(UpdateDownloader.MaxPayloadBytes + 1)]
    public async Task AnAbsurdAdvertisedSize_IsRefusedBeforeAnythingIsOpened(long size)
    {
        (UpdateDownloader d, _) = New(long.MaxValue);

        DownloadResult r = await d.DownloadAsync(
            new ReleaseAsset("circuitRF-1.0.0-arm64.dmg", _prefix + "x.dmg", size),
            _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, r.Outcome);
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    /// <summary>
    /// A feed that publishes NO size left the read loop with no stop condition at all, so a server
    /// that never closes writes until the volume is down to the reserve — design §13's own failure,
    /// arriving from the network rather than from the arithmetic. The cap is the stop condition, and
    /// it is lowered here so a 300 KB fixture can cross it.
    /// </summary>
    [Fact]
    public async Task ASizelessAsset_StopsAtTheTransferCapRatherThanRunningOn()
    {
        var d = new UpdateDownloader(new HttpClient(), new FakeFreeSpaceProbe(long.MaxValue))
        {
            UrlIsAllowed     = Loopback,
            MaxTransferBytes = 64 * 1024,       // the fixture serves 300 KB
        };

        DownloadResult r = await d.DownloadAsync(
            new ReleaseAsset("circuitRF-1.0.0-arm64.dmg", _prefix + "x.dmg", 0),
            _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_tmp, "circuitRF-1.0.0-arm64.dmg")));

        // And the partial is gone too: an overrun is not something to resume from.
        Assert.Empty(Directory.GetFiles(_tmp, "*" + UpdatePaths.PartialSuffix));

        // The shipping cap is the documented one and is far above any artifact we produce.
        Assert.Equal(2L * 1024 * 1024 * 1024, UpdateDownloader.MaxPayloadBytes);
    }

    [Fact]
    public async Task AnUnreachableHost_FailsSilently()
    {
        var asset = new ReleaseAsset("x.dmg", "http://127.0.0.1:1/x.dmg", 10);

        (UpdateDownloader d, _) = New(long.MaxValue);
        DownloadResult r = await d.DownloadAsync(asset, _tmp, 0, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, r.Outcome);
    }

    /// <summary>
    /// R-AU-28. The absence of com.apple.quarantine (macOS) and of the Mark of the Web (Windows) is
    /// what suppresses the Gatekeeper and SmartScreen prompts, and it holds precisely because
    /// HttpClient writes the file itself. A helpful-looking refactor to a shell downloader would
    /// reintroduce both prompts, silently, and only on a real user's machine.
    /// </summary>
    [Fact]
    public void NothingInTheDownloadPathShellsOut()
    {
        string code = UpdateInstallSiteTests.StripComments(
            UpdateInstallSiteTests.SourceFile("src/Ui/Updates/UpdateDownloader.cs"));

        foreach (string forbidden in new[] { "ProcessStartInfo", "Process.Start", "ProcessRunner", "curl",
                                             "Invoke-WebRequest", "UseShellExecute" })
            Assert.DoesNotContain(forbidden, code);

        Assert.Contains("HttpClient", code);
    }

    /// <summary>
    /// ProcessRunner exists for the primitives that move bytes, and its permitted-tool list is closed
    /// deliberately — a downloader can never be added to it by accident.
    /// </summary>
    [Fact]
    public async Task TheProcessRunnerCannotBeUsedToDownloadAnything()
    {
        foreach (string tool in ProcessRunner.Allowed)
            Assert.DoesNotContain(tool, new[] { "curl", "wget", "open", "powershell", "cmd", "sh", "bash" });

        await Assert.ThrowsAsync<ArgumentException>(
            () => ProcessRunner.RunAsync("curl", ["https://example.com"], CancellationToken.None));
    }

    /// <summary>
    /// The User-Agent the GitHub API requires, and nothing more. Some of this application's users
    /// work under restricted network policies and will be asked by their IT department exactly what
    /// the binary contacts; the honest answer has to stay short.
    /// </summary>
    [Fact]
    public void TheUserAgentCarriesNoIdentifier()
    {
        string ua = UpdateDownloader.UserAgent;

        Assert.Contains("/", ua);
        Assert.DoesNotContain(Environment.MachineName, ua);
        Assert.DoesNotContain(Environment.UserName, ua);
        Assert.Equal(2, ua.Split('/').Length);
    }

    /// <summary>
    /// The payload client must not carry a whole-operation timeout.
    ///
    /// <para><see cref="System.Net.Http.HttpClient.Timeout"/> bounds the ENTIRE operation including
    /// the response body — <c>ResponseHeadersRead</c> does not exempt it — so the feed's 30 seconds
    /// was 30 seconds to move a 160 MB payload and failed on anything slower than about 5.5 MB/s.
    /// Because a failed background check is silent and only retries at the next 24-hour window, a
    /// slow connection could never converge on a complete file (found in review, 2026-08-25).
    /// Progress is policed by <see cref="UpdateDownloader.StallTimeout"/> in the read loop
    /// instead — the question worth asking of a large transfer is not "has it finished" but "is it
    /// still arriving".</para>
    /// </summary>
    [Fact]
    public void ThePayloadClientHasNoWholeOperationTimeout()
    {
        using System.Net.Http.HttpClient download = UpdateDownloader.CreateDownloadHttpClient();
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, download.Timeout);

        // The feed asks for a small JSON document, where a whole-operation timeout is exactly right.
        using System.Net.Http.HttpClient feed = UpdateDownloader.CreateHttpClient();
        Assert.NotEqual(System.Threading.Timeout.InfiniteTimeSpan, feed.Timeout);
        Assert.True(feed.Timeout > TimeSpan.Zero);

        // And the stall timeout is a real bound, so a dead connection is not held forever.
        Assert.True(UpdateDownloader.StallTimeout > TimeSpan.Zero);
        Assert.True(UpdateDownloader.StallTimeout <= TimeSpan.FromMinutes(5));
    }
}
