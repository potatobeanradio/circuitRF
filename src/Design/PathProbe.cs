using System.Collections.Concurrent;

namespace CircuitRF.Design;

/// <summary>What a bounded look at a path found.</summary>
public enum PathState
{
    /// <summary>The path exists and answered in time.</summary>
    Present,
    /// <summary>The path answered in time and is not there.</summary>
    Missing,
    /// <summary>The file system did not answer within the time allowed: a network share whose
    /// server is down, a disconnected mapped drive, or a cloud-synced folder whose service is
    /// unreachable.</summary>
    NotResponding,
}

/// <summary>
/// Looks at a path on a thread of its own and stops waiting after a time limit.
///
/// <para><b>Why this exists.</b> <c>File.Exists</c>, <c>Directory.Exists</c> and every read are
/// synchronous kernel calls, and on a path the file system cannot reach they do not fail; they
/// WAIT. A network share whose server is down holds each call for the SMB timeout (tens of seconds
/// per call on Windows), and a cloud-only file in a synced folder holds a read until the sync
/// client gives up on downloading it (about a minute). Made on the UI thread, that is a frozen
/// window, and a recent-workspace list of ten entries on one dead share is ten of them in a row.
/// The thread the call runs on cannot be interrupted, so the only defence is not to make the call
/// on a thread that matters: run it elsewhere and stop waiting for it.</para>
///
/// <para><b>A dedicated thread per look, never the thread pool.</b> A call stuck in the kernel
/// keeps its thread until the kernel lets go, and twenty stuck pool threads are the start of pool
/// starvation for the solvers. One look at a path is in flight at a time: a second caller asking
/// about a path that is still stuck shares the first look rather than adding another stuck thread.</para>
///
/// <para><b>An answer of <see cref="PathState.NotResponding"/> is remembered</b> for
/// <see cref="UnresponsiveMemory"/>, so a dialog opened five times in a row waits once, not five
/// times. Present and Missing are not remembered: they are cheap to ask again and change under
/// the user's hands.</para>
/// </summary>
public static class PathProbe
{
    /// <summary>How long a look waits by default. Long enough for a slow but working share to
    /// answer, short enough that a window that waited for it still feels like it responded.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a path that did not answer is reported unresponsive without asking again.</summary>
    public static readonly TimeSpan UnresponsiveMemory = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Task<PathState>> InFlight = new(KeyComparer);
    private static readonly ConcurrentDictionary<string, DateTime> Unresponsive = new(KeyComparer);

    private static StringComparer KeyComparer
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool DefaultExists(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>
    /// True when <paramref name="path"/> recently failed to answer, without touching the disk.
    /// </summary>
    public static bool IsKnownUnresponsive(string? path)
    {
        if (Key(path) is not { } key) return false;
        if (!Unresponsive.TryGetValue(key, out var until)) return false;
        if (DateTime.UtcNow < until) return true;
        Unresponsive.TryRemove(key, out _);
        return false;
    }

    /// <summary>Does <paramref name="path"/> exist (file or folder), asked off the calling thread
    /// and given up on after <paramref name="timeout"/>.</summary>
    public static Task<PathState> ProbeAsync(string? path, TimeSpan? timeout = null)
        => ProbeAsync(path, timeout, DefaultExists);

    /// <summary>The same, with the existence check supplied — so a test can stand in a file system
    /// that hangs.</summary>
    internal static async Task<PathState> ProbeAsync(string? path, TimeSpan? timeout, Func<string, bool> exists)
    {
        if (Key(path) is not { } key) return PathState.Missing;
        if (IsKnownUnresponsive(key)) return PathState.NotResponding;

        var look = InFlight.GetOrAdd(key, k =>
        {
            var started = RunOnOwnThread(() => exists(k) ? PathState.Present : PathState.Missing);
            // Whenever it answers — in time or long after — it clears the way for the next look, and
            // a path that came back is no longer remembered as unresponsive.
            _ = started.ContinueWith(t =>
            {
                InFlight.TryRemove(new KeyValuePair<string, Task<PathState>>(k, t));
                if (t.IsCompletedSuccessfully) Unresponsive.TryRemove(k, out _);
            }, TaskScheduler.Default);
            return started;
        });
        var limit = timeout ?? DefaultTimeout;
        var finished = await Task.WhenAny(look, Task.Delay(limit)).ConfigureAwait(false);
        if (finished == look)
        {
            // The continuation above can run before GetOrAdd has stored the task, so the answered look
            // is removed here too; otherwise a stale answer would be handed to every later caller.
            InFlight.TryRemove(new KeyValuePair<string, Task<PathState>>(key, look));
            try { return await look.ConfigureAwait(false); }
            catch { return PathState.Missing; }
        }

        MarkUnresponsive(key);
        return PathState.NotResponding;
    }

    /// <summary>
    /// The synchronous form, for a caller that cannot await: blocks the calling thread for at most
    /// <paramref name="timeout"/>, which is the whole point. Prefer <see cref="ProbeAsync"/>.
    /// </summary>
    public static PathState Probe(string? path, TimeSpan? timeout = null)
        => ProbeAsync(path, timeout).GetAwaiter().GetResult();

    /// <summary>
    /// Looks at several paths at once, so the total wait is one timeout rather than one per path.
    /// The result is in the order of <paramref name="paths"/>.
    /// </summary>
    public static async Task<PathState[]> ProbeAllAsync(IReadOnlyList<string> paths, TimeSpan? timeout = null)
        => await Task.WhenAll(paths.Select(p => ProbeAsync(p, timeout))).ConfigureAwait(false);

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own and waits at most
    /// <paramref name="timeout"/> for it. Null when it did not finish in time (it carries on, and
    /// its result is dropped) or when it threw. For a READ of a file that may be cloud-only or on a
    /// share that may be gone.
    /// </summary>
    public static async Task<T?> WithinAsync<T>(Func<T> work, TimeSpan timeout, string? pathForMemory = null) where T : class
    {
        if (pathForMemory is not null && IsKnownUnresponsive(pathForMemory)) return null;
        var task = RunOnOwnThread(work);
        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != task)
        {
            if (pathForMemory is not null) MarkUnresponsive(pathForMemory);
            return null;
        }
        try { return await task.ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>Records that <paramref name="path"/> did not answer, for <see cref="UnresponsiveMemory"/>.</summary>
    public static void MarkUnresponsive(string? path)
    {
        if (Key(path) is { } key) Unresponsive[key] = DateTime.UtcNow + UnresponsiveMemory;
    }

    /// <summary>
    /// A sentence for a path that did not answer, naming the kinds of place that do this — the user
    /// knows which one theirs is.
    /// </summary>
    public static string NotRespondingSentence(string path)
        => $"'{path}' is not responding. It may be on a network drive that is disconnected, or in a "
         + "cloud-synced folder whose service cannot be reached; try again when it is back.";

    private static Task<T> RunOnOwnThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        })
        {
            IsBackground = true,   // a thread stuck in the kernel must never keep the process alive at exit
            Name = "circuitRF path probe",
        };
        thread.Start();
        return tcs.Task;
    }

    private static string? Key(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return null; }
    }
}
