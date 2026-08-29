// M1/M2 (brief-em-sweep-performance) — the ONE budget, and the fan-out that spends it.
//
// §1 of that brief is the thing to read before touching anything here: "the sim is slow, let the
// user pick how many cores" implies the solver is single-threaded, and it is not and never was.
// PlanarFill.ForRows has wrapped every one of its call sites in Parallel.For since L8c. So a
// core-count cap on its own can only ever make a run SLOWER; what M1 builds is the seam, and M2 is
// the first thing worth capping.
//
// ── WHY THERE IS A BUDGET OBJECT AND NOT SIMPLY TWO NUMBERS (R-emp-10) ──────────────────────────
//
// M2 fans out the DUT and every calibration standard at one frequency, and each of those solves
// fills its matrix with its own Parallel.For over rows. That is NESTED parallelism, and
// MaxDegreeOfParallelism on the outer loop does not bound the inner one — five solves on a 10-core
// box would happily ask for 50 workers. Two independent caps a reader has to multiply in their head
// is exactly what the brief forbids, so there is ONE number and it is spent by the INNERMOST work:
// a fill-row worker takes a permit for as long as it participates in a loop, and releases it when
// that loop ends. However many solves are in flight, the number of threads doing fill work at any
// instant is the cap.
//
// ── AND WHAT THAT ACTUALLY BUYS, WHICH IS NOT WHAT IT LOOKS LIKE ────────────────────────────────
//
// Interleaving five fills does not make the fills finish sooner — the fill already saturates the
// cores, and the total arithmetic is unchanged. What the budget buys is the OVERLAP: a solve that
// has finished its fill and gone into PlanarSystem.Lu (NumFlat, single-threaded) is holding no
// permits, so another solve's fill takes the cores it just gave back. M2's speed-up is the serial
// fraction of a solve, not the number of solves. See src/Engine/Mom/CLAUDE.md's own M2 section for
// what that measured.
//
// Permits are only ever held by threads doing pure fill arithmetic, which never wait on another
// permit, so a permit always comes back and the scheme cannot deadlock. A permit holder CAN block
// on PlanarKernelSet's fit gate (the multi-level fill asks for terms from inside a row) — that
// wastes a permit for the duration of one fit and is not a cycle, because the fitting thread needs
// no permit of its own.
//
// ── DOES THE FAN-OUT STARVE THE THREAD POOL? MEASURED, AND NO (P10) ─────────────────────────────
//
// The obvious worry about the scheme above is that K concurrent solves each ask Parallel.For for up
// to `cap` workers, so K*cap pool threads are requested and (K-1)*cap of them park in Enter() while
// the pool injects replacements a thread or two per second. brief-em-p10-fanout-starvation.md
// instrumented exactly that (the counters below) and REFUTED it; HISTORY.md's five §P10 tables are
// the evidence, and RESOLVED.md §P10 is the write-up. Two facts make it work, neither visible from
// reading ForRows:
//
//   * `cap` never exceeds Environment.ProcessorCount (PlanarSolve materialises a null cap as
//     exactly that), and the pool's MINIMUM worker count is also ProcessorCount. A fill therefore
//     needs no injected thread to reach its cap, and in fact reaches it within ~30 ms of starting.
//   * Parallel.For's unmet demand QUEUES; it does not block. It is a replicating task, so a replica
//     becomes a thread only when the pool has one to give. K concurrent loops produce K*cap pending
//     work items, not K*cap parked threads.
//
// Threads DO park once the pool is already bigger than the cap — force it and 34 of them park at
// once — and it costs nothing: the same point takes the same time either way, because a parked
// thread burns no CPU and holds no permit. Do not "fix" this with a shared row queue, a second cap,
// or by sizing the pool; all three were measured or forbidden and none buys anything.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>The one parallel budget a planar run spends</b>, shared by every solve in flight at one
/// frequency. See this file's header for why it is an object rather than a second integer.
///
/// <para>Created by <see cref="PlanarSolve"/> when it is going to fan out, and handed to every
/// <see cref="PlanarSolveContext"/> through <see cref="PlanarFillSettings.Budget"/>. A fill that has
/// no budget attached — every direct caller, every test, every non-de-embedded run — behaves exactly
/// as it did before this brief.</para>
/// </summary>
public sealed class PlanarParallelBudget
{
    private readonly SemaphoreSlim _permits;

    /// <summary>Threads that may be doing fill arithmetic at once, across every solve in flight.</summary>
    public int Cap { get; }

    public PlanarParallelBudget(int cap)
    {
        if (cap < 1)
            throw new ArgumentOutOfRangeException(nameof(cap), cap,
                "A parallel budget of zero cores would run nothing. Use null for unbounded.");
        Cap      = cap;
        _permits = new SemaphoreSlim(cap, cap);
    }

    /// <summary>Take one permit, blocking until one is free. Held for as long as a worker
    /// participates in one <c>Parallel.For</c> over fill rows.</summary>
    internal void Enter()
    {
        Interlocked.Increment(ref _waiting);
        long t0 = Stopwatch.GetTimestamp();
        try { _permits.Wait(); }
        finally
        {
            Interlocked.Add(ref _waitTicks, Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref _enters);
            Interlocked.Decrement(ref _waiting);
        }
        Interlocked.Increment(ref _held);
    }

    internal void Exit()
    {
        Interlocked.Decrement(ref _held);
        _permits.Release();
    }

    // ── The instrument (P10). Four process-wide counters and nothing else. ─────────────────────
    private static int  _waiting;
    private static int  _held;
    private static long _enters;
    private static long _waitTicks;

    /// <summary>
    /// <b>P10's instrument, and it is deliberately PROCESS-WIDE.</b> Threads parked inside
    /// <see cref="Enter"/> right now — pool threads a fill loop has consumed and that are doing no
    /// arithmetic at all. A run creates exactly one budget, so "any budget" and "the budget" are the
    /// same set; a static needs no handle on an object <see cref="PlanarSolve"/> owns privately.
    ///
    /// <para>Zero throughout a run means the permit scheme is never what makes a fill wait. A number
    /// that grows with the pool's own thread count is the fan-out starving the pool, which is the
    /// hypothesis P10 exists to test. Two interlocked adds per worker per LOOP (not per row), so it
    /// is free enough to leave switched on.</para>
    /// </summary>
    public static int WaitingThreads => Volatile.Read(ref _waiting);

    /// <summary>Permits handed out right now — threads actually doing fill arithmetic. Never exceeds
    /// the live budget's <see cref="Cap"/>, which is the whole point of the object.</summary>
    public static int HeldPermits => Volatile.Read(ref _held);

    /// <summary>How many times a worker has joined a budgeted fill loop — once per worker per loop,
    /// NOT once per row.</summary>
    public static long EnterCount => Interlocked.Read(ref _enters);

    /// <summary>Thread-seconds spent parked in <see cref="Enter"/>, summed over every worker. This is
    /// the number the starvation hypothesis is actually about: set against a point's wall clock
    /// × cap it is the fraction of the budget the permit scheme itself throws away.</summary>
    public static double TotalWaitSeconds =>
        Interlocked.Read(ref _waitTicks) / (double)Stopwatch.Frequency;

    /// <summary>Zeroes the cumulative counters so a measurement can bracket one run.</summary>
    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _enters, 0);
        Interlocked.Exchange(ref _waitTicks, 0);
    }
}

/// <summary>
/// <b>M2's fan-out: run independent solves concurrently under the same cap, or in order when there
/// is no cap to spend.</b>
///
/// <para><b>One implementation, not two that agree.</b> A cap of 1 runs the work in the order it was
/// given, on the calling thread — it does not take a different path through different arithmetic,
/// which is what makes R-emp-13's "cap 1 and cap 8 produce bit-identical results" a property of the
/// code rather than of a tolerance.</para>
/// </summary>
internal static class PlanarFanOut
{
    /// <param name="cap">Total budget, or null for unbounded. 1 means strictly in order.</param>
    public static void Run(int? cap, IReadOnlyList<Action> work)
    {
        if (work.Count == 0) return;
        if (work.Count == 1 || cap == 1)
        {
            foreach (var w in work) w();
            return;
        }

        try
        {
            Parallel.ForEach(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = cap is { } c ? Math.Min(c, work.Count) : -1 },
                w => w());
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Rethrow the FIRST inner exception with its stack intact rather than the aggregate.
            // Two things depend on this: RunControl's cancellation is an OperationCanceledException
            // thrown from a work item, and every caller here catches that type by name; and this
            // area's refusals are asserted on their MESSAGE, which an AggregateException would bury
            // under "One or more errors occurred".
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
    }
}
