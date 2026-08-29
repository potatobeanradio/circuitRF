# Brief P10 — is M2's fan-out starving the thread pool?

**Hypothesis to test, not a finding.** `PlanarFill.ForRows` under a `Budget` takes a semaphore permit
in `Parallel.For`'s `localInit` (`src/Engine/Mom/PlanarFill.cs:1712`). When `PlanarFanOut` runs K
solves concurrently, each `Parallel.For` asks for up to `cap` workers, so K·cap pool threads are
requested and (K−1)·cap of them sit BLOCKED in `Enter()`. The .NET pool injects threads at ~1–2 per
second once its minimum is exhausted, so on a 10-core box with five solves in flight the run can
stall for seconds at every frequency while the pool grows. `CLAUDE.md` §6 measured M2/M3's whole
ceiling at 1.09–1.15× and attributed the rest to hardware; if starvation is part of it, the
shipped fan-out is leaving real overlap on the table.

Read first: `PlanarParallel.cs` header; `ForRows`; `CLAUDE.md` §6's M3 paragraph and §8's
parallelism paragraph; `ParallelBudgetTests`, `CrossFrequencyParallelismTests`.

## Milestones

1. **Instrument**: `ThreadPool.ThreadCount`, `ThreadPool.PendingWorkItemCount` and the count of
   threads blocked in `Enter()` sampled every 100 ms during one de-embedded frequency point of the
   80 mm line at cap = ProcessorCount, standards fanned out. Plot (a table is fine) against the
   fill's own progress. If the blocked count is ~0 and pool thread count is flat, the hypothesis is
   refuted: record that in `RESOLVED.md` and stop.
2. **If it stalls**: replace the permit-per-worker scheme with ONE shared row queue across all
   fills in flight, drained by exactly `cap` long-lived workers (a `Channel<Action>` or a custom
   work-stealing loop) — the cap is then structural rather than enforced by blocking. R-emp-13's
   bit-identity across caps must survive (rows are still written once, by one worker).
3. **Re-measure** M2's overlap on the brief's own §0 design (five solves, two of them 96% of the
   work) and on the 80 mm line. Record beside §6's numbers.

## Must NOT

- Add a second cap. One number, one budget.
- Touch cross-frequency parallelism (M3) — its ceiling argument is independent of this.

## Gates

The instrumentation table in `HISTORY.md`; if changed, `ParallelBudgetTests` bit-identity at caps
1/2/unbounded unchanged and the overlap re-measurement; `RESOLVED.md` write-up; correct `CLAUDE.md`
§6's M3 sentence in place only if it becomes false.
