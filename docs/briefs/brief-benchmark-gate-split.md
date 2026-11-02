# Sonnet Brief — Make the 500k benchmark stop gating, without losing its safety net

Owner: **five minutes to pass a gate is unacceptable.** The 500,000-shape `LayoutPerf` cases dominate the
full suite and must come out of the routine gate. Two options were offered — defer 500k until the layout is
faster, or simply benchmark at 50k instead. **Take a third that keeps most of the value at a fraction of the
cost**, then fall back to the simpler answer if it does not pan out.

---

## 1. Why not just drop 500k

Benchmarking at 50k alone leaves §5.1's *pathological / imported full chip* row completely untested. That is
the row most likely to regress catastrophically and least likely to be noticed: an accidental O(n²) —
a nested loop over candidates, a per-shape dictionary lookup that degrades, a cache that thrashes — can look
fine at 50k and be minutes at 500k. Losing that signal entirely is a real cost.

## 2. The split that resolves it

**L2a already established the principle: counters are the gate, wall-clock is the diagnostic (R-L2a-3).**
The 500k case has been paying for the diagnostic on every run, which is the part nobody needs routinely.

**R-perf-1. Split the 500k cases into counter assertions (gated) and timed sweeps (opt-in).**

- **Counter assertions at 500k — stay in the gate.** `ShapesExamined`, `ShapesDrawn`, `PathsConstructed`,
  `DrawCalls`, cache hit counts. These are deterministic, machine-independent, and need **one** rendered
  frame — not a warm-up plus a timed sweep across profiles and paths. They are exactly what catches an
  algorithmic regression, which is the only thing worth blocking a commit over.
  - Generate **one** shared 500k layout and reuse it across the assertions rather than regenerating per
    case; generation is itself a large part of the cost.
  - Target roughly **≤15 s total** for the 500k gated portion. Measure and report what it actually lands at.
- **Timed sweeps at 500k — opt-in only.** Median/p95 across profiles × render paths × pan/zoom/full-extent
  is a *measurement* exercise for someone actively working on performance, not a per-commit check.

**R-perf-2. 50k becomes the timed benchmark.** Keep the full timed sweep — warm-up, median, p95, both render
paths, all three profiles — at 50k, and at 1k. That is the routine performance signal.

## 3. The gate definition changes, explicitly

The standing rule has been "gate item 1 means the full unfiltered suite." That rule now costs five minutes,
so change it in the open rather than letting people quietly filter:

**R-perf-3. Tag the opt-in timed sweeps `Category=Benchmark`, and define the gate as**

```
dotnet test --filter "Category!=Benchmark"
```

Update the root `CLAUDE.md` fast-loop section and say plainly:

- this filtered run **is** the gate for every brief's item 1, replacing "full unfiltered suite";
- the `Benchmark` sweeps are required when touching **rendering, the spatial index, the path/instance
  caches, or LOD**, and at any performance-phase boundary — name those triggers explicitly so the
  requirement is a rule rather than a judgement call;
- `Category=Nightly` keeps its existing meaning; if `Benchmark` fully supersedes it for the 500k timed
  cases, consolidate to one tag rather than leaving two overlapping ones.

## 4. Record the deferred target honestly

§5.1's 500k **timing** target is now unmet *and* unmeasured on routine runs. That is a deliberate trade, not
an oversight, and it must be visible:

**R-perf-4. Note in `CLAUDE.md` that the 500k timed gate is deferred until the tiled raster cache (L2d)
lands**, with L2c's measured shortfall (13–15× over the 50 ms floor) as the reason, and that re-enabling it
is part of L2d's own gate. Otherwise this quietly becomes permanent.

## 5. If §2 does not pan out

If the gated 500k counter portion cannot be brought under ~15 s — most likely because generating 500k shapes
is itself slow — then **fall back to the owner's second option**: 50k becomes the benchmark outright, 500k
moves entirely to `Benchmark`, and R-perf-4's note additionally records that 500k has no routine coverage at
all. Report the generation cost either way; if it dominates, a cached/serialized fixture generated once is
worth considering, though a 219 MB `.clay` (L2a's measured 500k CurveHeavy size) may make that a bad trade.

## 6. Guardrails

- **No test is deleted, skipped, or weakened** — this is about *when* things run and *which* assertions gate.
- Do not reduce the counter assertions' coverage to hit the time target; reduce iterations and shared setup
  instead.
- Do not change any product code, rendering behaviour, or the counters themselves.
- Do not silently change what `dotnet test` with no filter does — the change is to the *documented gate*, and
  it must be written down.

## 7. Gate

1. `dotnet test --filter "Category!=Benchmark"` runs green and is **measurably** under a minute — report the
   actual time and test count.
2. The 500k **counter** assertions still run in that filtered set, and still fail if culling regresses
   (verify by temporarily disabling culling and confirming a red test).
3. `dotnet test --filter "Category=Benchmark"` still produces the full timed sweep, unchanged in what it
   measures.
4. `dotnet test` with no filter still runs everything, and the total test count is unchanged from before.
5. Root `CLAUDE.md` documents the new gate definition, the `Benchmark` triggers, and R-perf-4's deferral.

## 8. On completion

Report the before/after gate time, the 500k gated portion's cost, and — if §5's fallback was taken — say so
plainly, with the generation cost that forced it.
