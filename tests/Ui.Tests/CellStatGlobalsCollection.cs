using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The test classes that drive <c>CellStat</c>'s PROCESS-GLOBAL state — its call counter, its
/// <c>CacheEnabled</c> switch, its injected clock, and the memos
/// <c>CellSymbolResolver.InvalidateAll</c> clears — run in one collection so they never run
/// concurrently with each other.
///
/// <para><b>Why this is needed and why an exact-count test is still the right test.</b> SL4 R-sl4-6
/// chose a COUNTER over a stopwatch precisely because a counter describes the algorithm and does not
/// measure the machine — and it pins an exact number rather than an upper bound, on purpose, so the
/// cost cannot drift upward one call at a time. What a counter over a process-global cannot survive is
/// a SECOND test resolving cell references, or dropping the cache, at the same moment: xUnit runs
/// distinct test classes in parallel by default, and every failure that produces is a statement about
/// the scheduler rather than about the code.</para>
///
/// <para><b>Not <c>DisableParallelization</c>.</b> These three still run in parallel with the other
/// ~200 classes in this assembly; they simply run one at a time relative to each other, which is the
/// whole of what they need. Serialising them against the entire suite would cost far more and buy
/// nothing — no other class asserts on <see cref="CellStat.Calls"/>.</para>
///
/// <para>Add a class here the moment it either asserts on <c>CellStat.Calls</c> or calls
/// <c>CellSymbolResolver.InvalidateAll</c> in a loop. Both are how this collection came to exist:
/// TM2's own gate does the second, and it turned SL4's two count assertions red.</para>
///
/// <para><b>The rule reaches <c>WorkspaceRootFinder.InvalidateCache</c> too, and the membership grew
/// to match on 2026-09-04.</b> That method drops <c>CellStat</c>'s cache as well (along with the
/// alias table and <c>WorkspaceWritability</c>'s memo) — so a fixture calling it between two of the
/// counted edits turns a cache HIT into a fresh stat, which is what "expected 40, actual 58" was:
/// the second class's fixture, not the algorithm. Sixteen classes here call one of the two per test;
/// they had simply never been scheduled against each other, and adding two more classes to the
/// assembly was enough to make it happen. Cost is negligible — every class in this collection runs
/// in milliseconds — and it removes a flake that says nothing about the code.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class CellStatGlobalsCollection
{
    public const string Name = "CellStat globals";
}
