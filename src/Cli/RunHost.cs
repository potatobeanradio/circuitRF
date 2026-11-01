using CircuitRF.Engine;

namespace CircuitRF.Cli;

/// <summary>
/// Cancellation and progress for whoever is HOSTING a run, when that is not a terminal.
///
/// <para>A command line has one run and ends when it ends, so nothing here is set and every property
/// reads as it always did — <see cref="Control"/> is null, the token is
/// <see cref="CancellationToken.None"/>, and the engines take the same optional argument they
/// already took. The only caller that sets any of it is <c>serve</c>
/// (brief-automation-5-protocol-adapter.md R-aut5-8): a de-embedded full-wave point costs over a
/// minute, and a client with no progress and no cancel simply times out and retries, doubling the
/// cost of the very run it gave up on.</para>
///
/// <para><b>It is the same <see cref="RunControl"/> the <c>em</c> verb already uses</b>, not a second
/// mechanism — cancellation lands at a work boundary and progress counts leaf units, exactly as that
/// type's own contract describes. What this class adds is only WHERE the token and the observer come
/// from.</para>
///
/// <para><b>Cancellation is not wired to a lone HB solve</b>, because there is nothing to wire it to:
/// <c>HbEngine.Run</c> takes no control, and <see cref="RunControl"/>'s own remarks say a single
/// Newton loop runs to completion by design. A swept HB cancels at its point boundary like everything
/// else.</para>
/// </summary>
internal static class RunHost
{
    /// <summary>
    /// The installed host, per async flow (EMB-5 R-emb5-1): <c>serve</c> runs read-only calls on a pool, and each
    /// call's token and observer must reach that call's verb and no other's. An AsyncLocal rather than a thread
    /// static because a verb's own parallel work (a sweep's Parallel.For, a mesher's tasks) reads the same control.
    /// </summary>
    private static readonly AsyncLocal<(RunControl Control, Action<RunProgress>? Observer)?> _host = new();

    /// <summary>Set by <c>serve</c> for the duration of one tool call; null on a command line.</summary>
    public static RunControl? Control => _host.Value?.Control;

    /// <summary>The token <see cref="Control"/> carries, or <see cref="CancellationToken.None"/>.
    /// Passed where an engine takes a bare token rather than a control (<c>EmRunService.Run</c>).</summary>
    public static CancellationToken Cancellation => Control?.Token ?? CancellationToken.None;

    /// <summary>Where a host wants progress delivered, beside whatever the verb already writes to
    /// stderr. Null on a command line, which is what keeps <c>em</c>'s stderr byte-identical.</summary>
    public static Action<RunProgress>? Observer => _host.Value?.Observer;

    /// <summary>Installs a host for one invocation. Returns a scope that removes it again, so a
    /// cancelled call cannot leave a cancelled token behind for the next one.</summary>
    public static IDisposable Install(CancellationToken ct, Action<RunProgress>? observer)
    {
        var previous = _host.Value;
        _host.Value = (new RunControl
        {
            Token    = ct,
            Progress = observer is null ? null : new Synchronous(observer),
        }, observer);
        return new Scope(previous);
    }

    /// <summary>
    /// Delivers the observation on the thread that made it, rather than posting it.
    ///
    /// <para><c>Progress&lt;T&gt;</c> captures the <see cref="SynchronizationContext"/> at
    /// construction and, where there is none, posts every observation to the thread pool. On a
    /// worker thread there is none — so with it, a progress notification races the result frame it
    /// is meant to precede, and two observations race each other. A client would see a bar that
    /// jumps backwards, and observations arriving for a call it has already been told finished.
    /// Delivering inline costs nothing here: the only observer is a JSON-RPC write, which is
    /// serialized and already throttled by <see cref="RunControl.MinReportIntervalMs"/>.</para>
    /// </summary>
    private sealed class Synchronous(Action<RunProgress> observer) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => observer(value);
    }

    private sealed class Scope((RunControl Control, Action<RunProgress>? Observer)? previous) : IDisposable
    {
        public void Dispose() => _host.Value = previous;
    }
}
