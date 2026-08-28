using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>An <see cref="IProgressMessage"/> that only records, so "what did the row show?" is a list.</summary>
internal sealed class RecordingProgressMessage : IProgressMessage
{
    public List<(string Text, string? Counter, double? Percent, bool Indeterminate)> Updates { get; } = [];
    public List<(MessageLevel Level, string Outcome, bool KeepBar)> Finished { get; } = [];
    public List<(MessageLevel Level, string Text)> Completed { get; } = [];
    public RunCancellation? Bound { get; private set; }

    public void Update(string text, string? counter = null, double? percentComplete = null,
                       bool indeterminate = false)
        => Updates.Add((text, counter, percentComplete, indeterminate));

    public void Finish(MessageLevel level, string outcome, bool keepBar = true)
        => Finished.Add((level, outcome, keepBar));

    public void Complete(MessageLevel level, string text) => Completed.Add((level, text));

    public void BindCancellation(RunCancellation? cancellation) => Bound = cancellation;
}

/// <summary>
/// The owner's report: the Messages row sat on "Checking for circuitRF updates..." for the whole of a
/// 160 MB download with no bar and no way to stop it.
///
/// <para>These gate the two halves that can go wrong on their own — the throttle, which is what keeps
/// a byte counter from flooding the dispatcher, and the downloader's cancel, which has to leave the
/// partial transfer behind for the resume to be worth anything.</para>
/// </summary>
public sealed class DownloadProgressReporterTests
{
    private const long MB = 1L << 20;

    private static DownloadProgressReporter New(
        RecordingProgressMessage row, long total, Func<long> clock, TimeSpan? interval = null)
        => new(row, "Downloading circuitRF 1.0.0", total, interval, clock);

    [Fact]
    public void TheFirstReportLandsImmediately_RatherThanAfterOneInterval()
    {
        var row = new RecordingProgressMessage();
        long now = 5_000;
        var r = New(row, 100 * MB, () => now);

        r.Report(64 * 1024);

        // A row that stays blank until the first interval elapses is a row that reads as stuck for
        // exactly as long as the user is most likely to be looking at it.
        Assert.Single(row.Updates);
    }

    [Fact]
    public void AFloodOfReports_IsThrottledToTheInterval()
    {
        var row = new RecordingProgressMessage();
        long now = 0;

        // What an 80 KB buffer over a 156 MB payload actually looks like: 2,000 calls.
        const int Buffers = 2_000;
        const long Buffer = 80L * 1024;
        var r = New(row, Buffers * Buffer, () => now, TimeSpan.FromMilliseconds(100));

        // The clock advances 1 ms per call, so two seconds of wall clock at the shipping interval is
        // ~20 rewrites of the row rather than 2,000 marshalled mutations.
        for (int i = 1; i <= Buffers; i++) { now = i; r.Report(i * Buffer); }

        Assert.InRange(r.Updates, 1, 25);

        // And the counter is not a fiction: the last thing shown is the last thing reported, which
        // here is the completing byte.
        Assert.Equal("156 MB of 156 MB", row.Updates[^1].Counter);
    }

    [Fact]
    public void TheFinalByteAlwaysLands_EvenInsideTheThrottleWindow()
    {
        var row = new RecordingProgressMessage();
        long now = 0;
        var r = New(row, 10 * MB, () => now, TimeSpan.FromMilliseconds(100));

        r.Report(1 * MB);            // the first, which always lands
        now = 1;                     // still inside the window
        r.Report(10 * MB);           // ...but this is the end of the transfer

        // Without the completion exception a download that finishes inside one interval leaves the
        // row reading 1 MB and then jumps to its outcome, which reads as a transfer that stopped.
        Assert.Equal(2, r.Updates);
        Assert.Equal(100.0, row.Updates[^1].Percent);
    }

    [Fact]
    public void PercentIsOnAHundredScale_NotAFraction()
    {
        var row = new RecordingProgressMessage();
        long now = 0;
        var r = New(row, 200 * MB, () => now);

        r.Report(100 * MB);

        // LiveProgressMessage clamps to [0,100], so a fraction would silently pin every bar in the
        // application to 1% and look like a stall rather than an error.
        Assert.Equal(50.0, row.Updates[0].Percent!.Value, 6);
    }

    [Fact]
    public void WithNoAdvertisedSize_TheBarIsIndeterminateAndNoPercentIsInvented()
    {
        var row = new RecordingProgressMessage();
        long now = 0;
        var r = New(row, 0, () => now);

        r.Report(7 * MB);

        Assert.True(row.Updates[0].Indeterminate);
        Assert.Null(row.Updates[0].Percent);
        Assert.Equal("7 MB", row.Updates[0].Counter);
    }

    [Fact]
    public void OnlyTheCounterChanges_SoTheBarDoesNotSlideSideways()
    {
        var row = new RecordingProgressMessage();
        long now = 0;
        var r = New(row, 100 * MB, () => now, TimeSpan.Zero);

        for (int i = 1; i <= 20; i++) { now = i * 10; r.Report(i * 5L * MB); }

        // IProgressMessage draws the bar immediately after the text, so a figure that grows inside
        // the TEXT moves the bar with it. The interface says so; this is the call site obeying it.
        Assert.Single(row.Updates.Select(u => u.Text).Distinct());
    }
}

/// <summary>
/// Cancel, against a real socket. The partial file is the whole point: a cancelled 160 MB transfer
/// that threw away what had arrived would make the Cancel button an expensive mistake rather than a
/// free one.
/// </summary>
public sealed class DownloadCancellationTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "crf-dlcancel-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly byte[] _payload = new byte[4 * 1024 * 1024];
    private readonly CancellationTokenSource _serverStop = new();

    public DownloadCancellationTests()
    {
        Directory.CreateDirectory(_tmp);
        for (int i = 0; i < _payload.Length; i++) _payload[i] = (byte)(i % 251);

        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

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

    /// <summary>Answers in slow chunks, so a cancel has somewhere to land mid-transfer.</summary>
    private async Task ServeAsync()
    {
        while (!_serverStop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            try
            {
                ctx.Response.ContentLength64 = _payload.Length;
                for (int at = 0; at < _payload.Length; at += 64 * 1024)
                {
                    int n = Math.Min(64 * 1024, _payload.Length - at);
                    await ctx.Response.OutputStream.WriteAsync(_payload.AsMemory(at, n));
                    await ctx.Response.OutputStream.FlushAsync();
                    await Task.Delay(5, _serverStop.Token);
                }
            }
            catch { /* the client went away, which is this fixture's whole point */ }
            finally { try { ctx.Response.Close(); } catch { /* already closed */ } }
        }
    }

    private ReleaseAsset Asset() => new("circuitRF-1.0.0-arm64.dmg",
                                        _prefix + "circuitRF-1.0.0-arm64.dmg", _payload.Length);

    private UpdateDownloader New()
        => new(new HttpClient(), new FakeFreeSpaceProbe(long.MaxValue))
           { UrlIsAllowed = u => u is not null && u.StartsWith(_prefix, StringComparison.Ordinal) };

    [Fact]
    public async Task ACancelledTransfer_ReportsCancelledRatherThanFailed()
    {
        var row = new RecordingProgressMessage();
        using var cts = new CancellationTokenSource();

        // The Cancel the progress bar's context menu raises, wired the way UpdateScheduler wires it.
        var cancellation = new RunCancellation("the update download", cts.Cancel);
        row.BindCancellation(cancellation);

        var progress = new DownloadProgressReporter(row, "Downloading circuitRF 1.0.0",
                                                    _payload.Length, TimeSpan.Zero);

        // Stop as soon as the transfer is demonstrably under way, which is what a user does.
        var seen = new TaskCompletionSource();
        var watcher = new ProgressRelay(b => { if (b > 0) seen.TrySetResult(); }, progress);

        Task<DownloadResult> download =
            New().DownloadAsync(Asset(), _tmp, 0, watcher, cts.Token);

        await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Same(cancellation, row.Bound);
        cancellation.Cancel();

        DownloadResult r = await download;

        // NOT Failed: UpdateService maps this to CheckOutcome.Cancelled, and the manual check's
        // switch would otherwise report the user's own Cancel back to them as an unreachable server.
        Assert.Equal(DownloadOutcome.Cancelled, r.Outcome);
    }

    /// <summary>
    /// <b>The downloader's half only.</b> It keeps the partial and can resume from it — but
    /// <c>UpdateService</c> step 1 calls <c>ReclaimDebris</c>, which deletes the whole of
    /// <c>staging/</c> before every check, so the application never actually resumes. Named for what
    /// it proves rather than for what one would like it to mean; see the companion test below, which
    /// pins the behaviour that overrides this one.
    /// </summary>
    [Fact]
    public async Task ACancelledTransfer_LeavesThePartialBehind_AtTheDownloaderLevel()
    {
        using var cts = new CancellationTokenSource();
        var seen = new TaskCompletionSource();
        var watcher = new ProgressRelay(b => { if (b > 0) seen.TrySetResult(); }, null);

        Task<DownloadResult> download = New().DownloadAsync(Asset(), _tmp, 0, watcher, cts.Token);

        await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        await download;

        // The .partial stays, and resumeFrom would pick it up if it were still there at the next
        // check. It is not — see ThePartialIsWipedBeforeEveryCheck below.
        string[] partials = Directory.GetFiles(_tmp, "*.partial");
        Assert.Single(partials);
        Assert.True(new FileInfo(partials[0]).Length > 0);
    }

    /// <summary>
    /// The behaviour that decides what a user actually experiences after cancelling: the next check
    /// starts the transfer over.
    ///
    /// <para><b>This is a trade, not a defect.</b> Wiping unconditionally costs a re-transfer and buys
    /// immunity from the stale-partial case — a release re-published under the same version and the
    /// same byte count would otherwise be resumed into, producing a file of the right length and the
    /// wrong content, which fails the hash; and a hash failure is a <c>verificationFailure</c>, which
    /// blacklists that version PERMANENTLY on that machine. The test exists so that anyone who
    /// "fixes" the resume has to decide about that first.</para>
    /// </summary>
    [Fact]
    public void ThePartialIsWipedBeforeEveryCheck_SoACancelledDownloadStartsOver()
    {
        string updates = Path.Combine(_tmp, "updates");
        string staging = Path.Combine(updates, "staging");
        Directory.CreateDirectory(staging);

        string partial = Path.Combine(staging, "circuitRF-1.0.0-arm64.dmg" + UpdatePaths.PartialSuffix);
        File.WriteAllBytes(partial, new byte[128 * 1024]);

        // Exactly what UpdateService does in its step 1, before the feed is asked.
        new UpdateReclaimer(updates, null).ReclaimDebris();

        Assert.False(File.Exists(partial));
        Assert.False(Directory.Exists(staging));
    }

    /// <summary>Watches the byte stream and forwards to the real reporter, so one download can
    /// drive both the assertion and the row.</summary>
    private sealed class ProgressRelay(Action<long> watch, IProgress<long>? inner) : IProgress<long>
    {
        public void Report(long value) { watch(value); inner?.Report(value); }
    }
}

/// <summary>
/// The cross-platform guarantee, asserted structurally because it cannot be asserted by running:
/// macOS stages a <c>.dmg</c> through <c>hdiutil</c>, Windows a <c>.zip</c> through
/// <c>VerifyWindowsTree</c>, Linux a <c>.tar.gz</c> through <c>tar</c>, and a test host can only ever
/// be one of the three.
///
/// <para>What makes the bar platform-independent is that there is exactly ONE download in the
/// application and every platform reaches it through the method that owns the row. The platform
/// branches all sit INSIDE that method, in the phases the row covers as "verifying" and "installing".
/// A second download path added later — or the <c>progress</c> argument going back to <c>null</c>,
/// which is the state this whole change started from — would silently restore the original bug on
/// every platform at once, and neither is visible in any behavioural test.</para>
/// </summary>
public sealed class DownloadProgressWiringTests
{
    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(
            Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    [Fact]
    public void ThereIsExactlyOneDownload_AndItIsGivenAProgressReporter()
    {
        string src = StripComments(Source("src/Ui/Updates/UpdateService.cs"));

        MatchCollection calls = Regex.Matches(src, @"\.DownloadAsync\(");
        Assert.Single(calls);

        // The argument list, on one logical line after whitespace collapse.
        string flat = Regex.Replace(src, @"\s+", " ");
        Assert.Contains(".DownloadAsync(candidate.Asset, UpdatePaths.Staging, required, progress, ct)",
                        flat);
        Assert.DoesNotContain(
            ".DownloadAsync(candidate.Asset, UpdatePaths.Staging, required, null, ct)", flat);
    }

    [Fact]
    public void EveryPlatformBranch_SitsInsideTheMethodThatOwnsTheRow()
    {
        string src = StripComments(Source("src/Ui/Updates/UpdateService.cs"));

        // The row is created in FetchVerifyStageAsync and the work happens in the Core it calls, so
        // every staging branch must appear at or after the Core's declaration. If one migrates out
        // to a sibling path, that path downloads and stages with no bar on that platform only —
        // exactly the kind of break a single-platform CI leg cannot see.
        int core = src.IndexOf("FetchVerifyStageCoreAsync(", StringComparison.Ordinal);
        Assert.True(core > 0, "FetchVerifyStageCoreAsync should exist - it is the row's scope.");

        // Fully qualified: `Match` alone collides with the Match Designer's own namespace.
        foreach (System.Text.RegularExpressions.Match m in
                 Regex.Matches(src, @"InstallShape\.MacOsBundle"))
            Assert.True(m.Index > core,
                        "a macOS staging branch escaped the method that draws the progress row");
    }
}
