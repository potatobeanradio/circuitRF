// ================================================================
//  SolvePool.cs  —  M5 of brief-harmonicarf-h4-h5
//
//  R-h45-8  the solve pool (§6.7): cores − 2 workers, each owning its OWN HarmonicaContext and
//           ContourGrid (D2). Contexts are pooled and rebuilt only on structural change.
//           The UI thread never solves; it renders the most recent completed result.
//  R-h45-9  latest-wins cancellation (D3): a newer frame supersedes an in-flight one, and the
//           superseded job stops at the next cancellation point rather than finishing.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Engine;

namespace CircuitRF.Harmonica;

/// <summary>
/// One worker's private, reusable state: a <see cref="HarmonicaContext"/> and a
/// <see cref="ContourGrid"/> that belong to this worker alone.
///
/// <para><b>Why per-worker rather than shared (D2, §0.3 item 6).</b> Neither type is thread-safe —
/// <c>HarmonicaContext</c> mutates its netlist on <c>SetBias</c>, and <c>ContourGrid</c> rewrites its
/// point list and RBF factorization on every <c>Build</c>. Sharing one of either across workers would
/// be a data race whose symptom is a wrong contour, not a crash. Giving each worker its own is the
/// cheap, correct answer: elaboration is milliseconds and happens once per structural change.</para>
/// </summary>
public sealed class SolveWorker
{
    private HarmonicaContext? _ctx;

    internal SolveWorker(int index, double z0) { Index = index; Grid = new ContourGrid(z0); }

    /// <summary>Which worker this is. Stable for the pool's lifetime; useful in diagnostics.</summary>
    public int Index { get; }

    /// <summary>This worker's own grid. Reused across frames — <c>Build</c> clears it itself.</summary>
    public ContourGrid Grid { get; }

    /// <summary>This worker's own context, or null until the first <see cref="EnsureContext"/>.</summary>
    public HarmonicaContext? Context => _ctx;

    /// <summary>How many times this worker has CREATED a context from scratch (never reused one).</summary>
    public int ContextCreateCount { get; private set; }

    /// <summary>
    /// How many times this worker's context has rebuilt its netlist. Reported straight off the
    /// context so the pooling claim is measured where it actually happens, not counted twice here.
    /// </summary>
    public int ContextRebuildCount => _ctx?.RebuildCount ?? 0;

    /// <summary>
    /// The pooling rule: reuse this worker's context, applying the model to it. <c>Apply</c> rebuilds
    /// the netlist only when <see cref="CircuitModel.StructuralKey"/> moved — a bias or termination
    /// change costs nothing. A context is created here exactly once per worker.
    /// </summary>
    public HarmonicaContext EnsureContext(CircuitModel model, AnalysisSettings? settings = null)
    {
        if (_ctx is null)
        {
            _ctx = HarmonicaContext.Create(model, settings);
            ContextCreateCount++;
            return _ctx;
        }

        _ctx.Apply(model);
        return _ctx;
    }
}

/// <summary>
/// The frame pump. Jobs are submitted from the UI thread and run on the thread pool against a bounded
/// set of <see cref="SolveWorker"/>s; the newest submission always wins.
///
/// <para><b>Framework-free and headlessly testable.</b> The job is a delegate the caller supplies, so
/// this file knows nothing about frames, panels or options — Tier 4 drives it with a fast synthetic
/// job and asserts the POLICY (bounded completions, last event wins) without paying for real HB
/// solves. That is the whole reason the pool is generic in its result rather than typed to
/// <c>HarmonicaFrame</c>, which lives on the other side of the UI firewall.</para>
///
/// <para><b>Latest-wins, not a queue (D3).</b> Every submission cancels the one before it. Without
/// this a fast drag builds an unbounded backlog and the UI lags <i>further the faster you move</i> —
/// the classic failure mode for a live-solve tool, and the one users notice first.</para>
/// </summary>
public sealed class SolvePool<TResult> : IDisposable
{
    private readonly SolveWorker[] _workers;
    private readonly ConcurrentBag<SolveWorker> _free = [];
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _current;
    private long _sequence;
    private long _latest;
    private bool _disposed;

    /// <summary>
    /// §6.7's worker count: <c>cores − 2</c>, leaving one core for the UI thread and one for the OS.
    /// Never below 1 — a single-core machine still has to be able to solve.
    /// </summary>
    public static int DefaultWorkerCount => Math.Max(1, Environment.ProcessorCount - 2);

    public SolvePool(int? workerCount = null, double z0 = 50.0)
    {
        int n = Math.Max(1, workerCount ?? DefaultWorkerCount);
        _workers = [.. Enumerable.Range(0, n).Select(i => new SolveWorker(i, z0))];
        foreach (var w in _workers) _free.Add(w);
        _slots = new SemaphoreSlim(n, n);
    }

    /// <summary>The pool's workers. Exposed so a caller (or a test) can read the pooling counters.</summary>
    public IReadOnlyList<SolveWorker> Workers => _workers;

    public int WorkerCount => _workers.Length;

    /// <summary>Jobs that reached the solve delegate at all.</summary>
    public int StartedCount { get; private set; }

    /// <summary>Jobs that ran to completion AND were still the latest when they finished.</summary>
    public int CompletedCount { get; private set; }

    /// <summary>
    /// Jobs that were superseded — either dropped before starting, or cancelled part-way. This is the
    /// number that makes latest-wins visible: on a 200-event drag it carries almost all of them.
    /// </summary>
    public int SupersededCount { get; private set; }

    /// <summary>The sequence number of the most recent job whose result was published.</summary>
    public long LastCompletedSequence { get; private set; }

    /// <summary>The most recently published result, or default if none has completed yet.</summary>
    public TResult? LastResult { get; private set; }

    /// <summary>
    /// Raised on the worker thread when a job completes AND is still the latest. The caller marshals
    /// to its own UI thread; this class deliberately owns no dispatcher.
    /// </summary>
    public event Action<TResult, long>? Completed;

    /// <summary>Raised on the worker thread when a job throws anything other than cancellation.</summary>
    public event Action<Exception, long>? Failed;

    /// <summary>
    /// Submits a frame. Returns its sequence number. Any job submitted earlier is cancelled: a job
    /// that has not started yet is dropped outright, and one already running stops at its next
    /// cancellation point (for a grid build, between Γ points).
    /// </summary>
    public long Submit(Func<SolveWorker, CancellationToken, TResult> job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(_disposed, this);

        long seq;
        CancellationToken ct;
        lock (_gate)
        {
            seq = ++_sequence;
            Volatile.Write(ref _latest, seq);

            // Cancel the previous frame BEFORE the new one queues, so a worker frees up promptly.
            _current?.Cancel();
            _current?.Dispose();
            _current = new CancellationTokenSource();
            ct = _current.Token;
        }

        var task = Task.Run(() => RunAsync(seq, job, ct));
        _inFlight[seq] = task;
        return seq;
    }

    private async Task RunAsync(long seq, Func<SolveWorker, CancellationToken, TResult> job,
                                CancellationToken ct)
    {
        SolveWorker? worker = null;
        bool slotHeld = false, borrowedShared = false;
        try
        {
            // Cheap pre-check: a job superseded before it ever reached a worker costs nothing.
            if (Volatile.Read(ref _latest) != seq) { CountSuperseded(); return; }

            await _slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            slotHeld = true;

            // Re-check AFTER acquiring — the wait itself is where most supersession happens on a drag.
            if (Volatile.Read(ref _latest) != seq || ct.IsCancellationRequested)
            {
                CountSuperseded();
                return;
            }

            if (!_free.TryTake(out worker))
            {
                // Cannot happen while the semaphore and the bag hold the same count, but a worker
                // that is somehow unavailable must not deadlock the last frame of a drag.
                worker = _workers[0];
                borrowedShared = true;
            }

            CountStarted();
            TResult result = job(worker, ct);

            // A job that finished but is no longer the latest is DISCARDED, never published — the
            // whole point of latest-wins is that a stale frame must not overwrite a newer one.
            if (Volatile.Read(ref _latest) != seq) { CountSuperseded(); return; }

            lock (_gate)
            {
                CompletedCount++;
                LastCompletedSequence = seq;
                LastResult = result;
            }
            Completed?.Invoke(result, seq);
        }
        catch (OperationCanceledException)
        {
            CountSuperseded();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex, seq);
        }
        finally
        {
            if (worker is not null && !borrowedShared) _free.Add(worker);
            if (slotHeld) _slots.Release();
            _inFlight.TryRemove(seq, out _);
        }
    }

    // The counters are written from several worker threads and read alongside the published result,
    // so they share the same lock rather than being merely atomic.
    private void CountStarted()    { lock (_gate) StartedCount++; }
    private void CountSuperseded() { lock (_gate) SupersededCount++; }

    /// <summary>
    /// Waits for every submitted job to finish or abandon. Test-facing: a drag test needs a defined
    /// point at which the counters are final. Never used by the UI, which is event-driven.
    /// </summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            var pending = _inFlight.Values.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* individual failures are reported through Failed */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }
        _slots.Dispose();
    }
}
