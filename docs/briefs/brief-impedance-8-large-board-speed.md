# Brief 8 — a whole large board in minutes, not half an hour

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp8-n` · **Phase:** performance
**Area:** `src/Design/Layout/Em/TraceCrossSection.cs` (`Cut`, the reach and conductor rules, `Solve`),
`src/Design/Layout/Em/TraceImpedanceAnalysis.cs` (`Cut`, the solve loop, progress), `src/Cli/Impedance.cs` (progress
lines), `src/Engine/Mom/` (`BoundaryMesher`, `ChargeSolver`, `EmMeshSettings`) only where §2 shows the cost is there.
**Depends on:** 7 · **Blocks:** 9 (a summary of a whole board is only useful if the board can be run)
**Rule:** the board, its vendor and the reporter must not be named anywhere in the repo. It is "board C" below.

---

## 0. What was measured (2026-10-27)

Board C: a 10-copper-layer HDI board, Gerber import, 144 MB `.clay`, ~36,700 shapes, ~3,000 drill holes. The whole
board, every copper layer, no scope (`circuitrf impedance <clay> --no-scope --layers <all ten> --max-freq 6GHz`),
Release build, on a 10-core 16 GB machine:

| | |
|---|---|
| Traces / findings / junctions | 1,800 / 8,326 / 1,257 |
| Wall time | **29 min 22 s** (15,765 s CPU — the solve is parallel and was ~9 cores busy) |
| Peak memory (RSS) | **8.2 GB** |
| Reading and preparing the board | ~8–11 s (a `--survey` of all ten layers is 25 s) |
| Top Copper, 489 traces, 2,521 cross-sections | ~25 s of solving |
| Top + Inner 1–6 together | under 3.5 min |
| **Inner 7, 276 traces, 6,368 cross-sections** | **~24 min — over 80 % of the run** |
| Bottom Copper, 881 traces, 7,863 cross-sections | ~1.5 min |

Inner 7 is stripline with coplanar ground (97 µm traces between two planes). A 15 mm × 15 mm region of the same layer
(147 cross-sections) solved in ~3.5 s after the read — about **0.16 CPU-s per cross-section**. Across the whole layer the
average is about **2 CPU-s per cross-section**. So the cost is not uniform: a minority of Inner 7's cuts cost many
seconds each and they ARE the run. Inner 7's findings say where to look: 3,445 findings on 276 traces, many of them
`partial-reference` and `return-broken` (a reference plane pulled back or missing under the trace), and the run reports
762 `unsolved` stations board-wide.

The pad search (DFR29-3) is under 0.5 s of this and is not in scope.

Earlier work already bounds the cut: `MaxConductors = 6` (20 conductors took 20–45 s a cut; see the comment in
`TraceCrossSection`), the coplanar-ground reach cap, and the key quantisation that lets near-identical cuts share one
solve. Read those before adding a rule beside them.

## 1. Goal

The whole of board C in **5 minutes or less** on the same machine, **peak memory 4 GB or less**, with the same answers
(§4). And while it runs, the user sees how far it has got and how long is left.

**Owner decision D1 — the time target.** (a) recommended: whole board ≤ 5 min, no layer > 2 min. (b) whole board
≤ 10 min. Stop and report if §2's measurement says (a) needs an approximation outside D2's budget.

**Owner decision D2 — the accuracy budget for any approximation.** (a) recommended: every trace's Z0 min, mean and max
within **0.1 Ω** of the baseline, and no verdict changes. (b) within 0.5 %, verdict changes allowed when each is listed
with its trace and both numbers.

## 2. `R-imp8-1` — measure first: where the 24 minutes go

Before changing anything, record a **baseline** of board C on all ten layers with `--json` (keep it outside the repo).
Then add temporary instrumentation (removed before completion) to `TraceCrossSection.Solve` and print, for every
unique cross-section on Inner 7: its time, the mesh's panel count N, the conductor count, the dielectric interfaces
meshed, the reach, the configuration name, and whether the solve refused. From that, report in `src/Design/RESOLVED.md`:

- the distribution of per-cut time (median, 90th, 99th percentile, max) and what share of the layer's total the top
  10 % of cuts are;
- for the expensive cuts, which of N, conductor count, reach or interface count is large — and whether they are the
  `partial-reference` / `return-broken` / `unsolved` stations;
- how much of a cut's time is the mesh, the C solve and the C₀ (air-filled) solve;
- where the 8.2 GB is: several dense solves in parallel at N² complex entries each is the first suspect.

Do this with a scratch harness or a scoped CLI run, not a new test (project rule: no new timing tests).

## 3. `R-imp8-2` — make the expensive cuts cheap

Choose the fix from §2's numbers, not from this list. Candidates, in the order to check:

1. **Reach where a reference is pulled back.** The coplanar-ground cap bounds the reach only when same-layer ground is
   found on both sides. A stripline whose plane is cleared or pulled back under part of the trace may fall through to
   the full reach, with long plane rectangles and many panels. Bound it by the reference heights (a few × the larger
   of H-below and H-above) where both references exist anywhere within reach.
2. **Dielectric interfaces outside the two references.** Between two solid planes the field does not see the stack
   beyond them. If `BuildRegions` meshes every interface of a ten-layer stack across the full reach for an inner cut,
   truncating the stack at the references (where both span the reach) removes most of N.
3. **Panel density.** `EmMeshSettings.Default` may be finer than a 0.1 Ω answer needs on a long plane rectangle far from
   the signal. Grade it with distance from the signal rather than coarsening everything.
4. **Memory and scheduling.** Bound the number of LARGE solves running at once (by estimated N²), so peak memory is
   held without serialising the cheap ones; start the most expensive keys first so one late giant does not leave nine
   cores idle at the end.
5. **A cut that cannot be solved** (a refusal) should cost no more than a cheap one: if the expensive tail is mostly
   `unsolved`, find what makes the solver work hard before refusing.

Anything in `src/Engine/Mom` is shared with the EM extraction: change it only behind a setting the impedance check
passes, and the existing `Mom` tests must pass unchanged.

## 4. `R-imp8-3` — the same answers

After the change, re-run board C exactly as the baseline and compare per trace: Z0 min / mean / max, verdict, and
finding kinds. Report in `RESOLVED.md` the largest Z0 change and every verdict change (D2). Also re-run the two
synthetic boards of brief 6/7 and the pad-shunt fixtures. Their numbers must not move.

## 5. `R-imp8-4` — progress the user can read

The CLI prints `Inner 7 (8 of 10): solving (6368 cross-sections)` and then nothing for 24 minutes. Print progress on
stderr at most every few seconds: cross-sections done of total, elapsed, and an estimate of time left once enough are
done to make one. The Impedance panel shows the same thing in its progress line. `RunControl` already counts the stage
(`BeginStage` / `TickStage`); this is reporting what it knows. `--json` stdout is unaffected.

## 6. Gates

1. Board C, all ten layers, no scope: wall time and peak RSS against D1 and 4 GB, measured as in §0, reported.
2. §4's comparison against the baseline, within D2.
3. A counter, not a timer, holds the fix: e.g. on a synthetic stripline whose plane is pulled back under the trace,
   the cut's conductor count / panel count is bounded (whatever §3 changed). It must fail on the old code.
4. The CLI prints progress with an estimate during a long solve, and `--json` stdout is byte-identical with and
   without it.
5. Board C itself is run by hand and reported; it is not committed and no test names it.

## 7. Tests (minimal)

One per gate 3–4. Run only the impedance test classes (`--filter FullyQualifiedName~Impedance`) and, if `src/Engine/Mom`
was touched, the `Mom` classes — never the whole of `Ui.Tests` or `Engine.Tests`.

## 8. Not in this brief

The report's length and layout on a large board (brief 9). The pad search. Scoping by net or region (briefs 2, 4, 6)
— the point here is that a whole-board run is affordable when someone wants one.

## 9. On completion

`src/Design/RESOLVED.md` (§2's measurements, what was changed and why, §4's comparison), never any `CLAUDE.md`. Update
the impedance user doc only if the user sees something new (the progress estimate).
