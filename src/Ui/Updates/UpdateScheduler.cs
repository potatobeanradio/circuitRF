using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// Schedules the background check — one shared scheduler, three call sites, no copy.
///
/// <para><b>At least 60 seconds after the main window opens</b>, on a background thread, so it never
/// competes with startup and never appears in a cold-start measurement. circuitRF has never made an
/// outbound network call; this feature introduces the first one, and that deserves to be deliberate
/// rather than incidental.</para>
///
/// <para><b>Every path is wrapped</b> so that no exception from the update subsystem can reach the
/// UI thread or affect shutdown. The updater is not permitted to be the reason anything else fails.</para>
/// </summary>
public static class UpdateScheduler
{
    /// <summary>How long after the window appears the first check may run.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Up to this much extra delay, chosen per machine, so a lab imaged from one disk does not
    /// arrive at the API in lockstep.
    /// </summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(30);

    private static int _started;

    /// <summary>
    /// Called once from each application's startup path. Returns immediately; everything happens on
    /// a background thread.
    ///
    /// <para>Idempotent by a flag rather than by luck: three <c>Application</c> subclasses share this
    /// assembly, and a second call must not produce a second check.</para>
    /// </summary>
    public static void ScheduleFirstCheck(IMessageSink? messages, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        // The preference is read HERE, before anything else happens, so that with automatic updates
        // off there is not even a timer — let alone a socket.
        if (!UpdatePolicy.Current.AutomaticUpdates) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay + Jitter(), ct).ConfigureAwait(false);
                await RunCheckAsync(messages, manual: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception) { /* the updater never takes anything else down with it */ }
        }, ct);
    }

    /// <summary>Help ▸ Check for Updates… — the same check, ignoring the throttle.</summary>
    public static Task<CheckResult> CheckNowAsync(IMessageSink? messages, CancellationToken ct = default)
        => RunCheckAsync(messages, manual: true, ct);

    /// <summary>
    /// <b>The Cancel is created HERE, not at the menu item</b>, so both entry points get one from a
    /// single place: Help ▸ Check for Updates… and the once-per-launch background check run the same
    /// code, and a download started by the background check is exactly as worth being able to stop as
    /// one the user asked for. Putting it in the view-model command would have given Cancel to the
    /// path nobody is watching and withheld it from the path that moves 160 MB unprompted.
    ///
    /// <para>The source is linked to <paramref name="ct"/> so shutdown still cancels everything;
    /// <see cref="RunCancellation.Finish"/> runs in the <c>finally</c>, which is what makes a bar left
    /// on screen by a completed run stop offering a Cancel that would do nothing.</para>
    /// </summary>
    private static async Task<CheckResult> RunCheckAsync(IMessageSink? messages, bool manual, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancellation = new RunCancellation("the update download", () =>
        {
            // Cancellation lands at the next 80 KB buffer, which is immediate at any real transfer
            // rate — so unlike the EM run there is nothing worth saying about a pending stop.
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already settled */ }
        });

        try
        {
            var service = new UpdateService(
                () => new GitHubReleasesFeed(UpdateDownloader.CreateHttpClient(), FeedUrl()),
                new DriveFreeSpaceProbe(),
                messages);

            return await service.CheckAsync(manual, cts.Token, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The user's own Cancel, arriving as an exception from somewhere that does not return a
            // DownloadResult. Not a failure.
            return new CheckResult(CheckOutcome.Cancelled);
        }
        catch (Exception e)
        {
            return new CheckResult(CheckOutcome.Failed, Detail: e.Message);
        }
        finally
        {
            cancellation.Finish();
        }
    }

    /// <summary>
    /// The feed to ask: a <c>feedUrl</c> a manifest supplied, once one has been seen and accepted,
    /// otherwise the compiled-in default. This is what makes design §15's migration reach the
    /// installed base.
    ///
    /// <para><b>What "accepted" means depends on whether this build carries a release key.</b>
    /// Without one, the only thing standing behind a manifest is TLS to a host we chose, so the
    /// compiled-in allow-list is the constraint. With one, a <c>feedUrl</c> is persisted only after
    /// arriving in a manifest signed by that key (<see cref="UpdateSelector"/>), so it may name any
    /// <c>https</c> host — which is precisely what design §15.4's migration off GitHub and §15.5's
    /// free mirroring need, and what the allow-list would otherwise make impossible.</para>
    /// </summary>
    internal static string FeedUrl() => ResolveFeedUrl(UpdateStateIo.Load().FeedUrl);

    /// <summary>The testable half — no state file.</summary>
    internal static string ResolveFeedUrl(string? persisted)
        => FeedUrlAllowList.IsAcceptable(persisted) ? persisted! : GitHubReleasesFeed.DefaultApiUrl;

    /// <summary>
    /// Per-machine jitter, stable across runs, derived from the machine rather than from a clock —
    /// so it does not have to be persisted and cannot drift back into lockstep.
    /// </summary>
    private static TimeSpan Jitter()
    {
        // FNV-1a, not string.GetHashCode: .NET randomises string hashing PER PROCESS, so the obvious
        // spelling produced a different offset on every launch and the "stable across runs" this
        // comment claims was simply not true (corrected in review, 2026-08-25). Stability is the
        // point — an offset that is re-rolled every launch is a machine that wanders back into
        // lockstep with every other one over enough launches.
        return TimeSpan.FromTicks((long)(MaxJitter.Ticks * StableFraction(System.Environment.MachineName)));
    }

    /// <summary>A stable value in [0,1) derived from <paramref name="text"/>. FNV-1a, 32-bit.</summary>
    internal static double StableFraction(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash / (double)uint.MaxValue;
    }

    /// <summary>For tests: forget that a check was scheduled.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _started, 0);
}

/// <summary>
/// What happens when the user changes one of the two settings — which is more than writing JSON.
///
/// <para>A user who unchecks the box and is then moved to a new version on the next relaunch has
/// been lied to by the checkbox. That is the whole reason this exists separately from the
/// preference itself.</para>
/// </summary>
public static class UpdatePreferenceChange
{
    /// <summary>
    /// Discards whatever the new settings no longer justify: automatic updates off drops the staged
    /// update outright; betas off drops a staged <i>prerelease</i> and <b>leaves a staged stable
    /// version alone</b>.
    /// </summary>
    /// <param name="site">
    /// How to find the install root, which is what a staged version DIRECTORY name is relative to.
    /// Injected so this is an ordinary unit test against a temp fixture rather than something only
    /// a real installation can exercise.
    /// </param>
    public static void Apply(bool automaticUpdates, bool includeBetas, Func<InstallSite>? site = null)
    {
        UpdateState state = UpdateStateIo.Load();
        if (state.StagedVersion is null) return;

        bool discard = !automaticUpdates
                       || (!includeBetas && state.StagedIsPreRelease == true);

        if (!discard) return;

        // The install root is passed, not inferred: `staged_path` comes out of state.json, and
        // Discard now refuses anything that is not inside the updater's own tree or an `app-*`
        // child of this root. See UpdatePaths.IsOurs.
        string installRoot = (site ?? UpdateInstallSite.Detect)().Root;

        UpdateStager.Discard(state.StagedPath is null
            ? null
            : ResolveStagedPath(state.StagedPath, installRoot), installRoot);

        try { if (Directory.Exists(UpdatePaths.Staging)) Directory.Delete(UpdatePaths.Staging, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* next launch */ }

        UpdateStateIo.Update(s =>
        {
            s.StagedVersion      = null;
            s.StagedPath         = null;
            s.StagedIsPreRelease = null;
        });
    }

    /// <summary>
    /// A staged macOS bundle is recorded as an absolute path; a staged version directory is recorded
    /// as a NAME relative to the install root, because the root can move and the name cannot.
    /// </summary>
    private static string ResolveStagedPath(string staged, string installRoot)
        => Path.IsPathRooted(staged) ? staged : Path.Combine(installRoot, staged);
}
