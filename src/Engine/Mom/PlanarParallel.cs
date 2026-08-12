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
    internal void Enter() => _permits.Wait();

    internal void Exit() => _permits.Release();
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
