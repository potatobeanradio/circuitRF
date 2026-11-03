# Brief SDL-7 — Sweeps, tuning and optimisation by EM, in the designer's own schematic

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl7-n`
**Area:** `src/Design/Optimization/` (`OptimizationRun`, `OptimizationVariables`, `TunableCatalog`),
`src/Engine/Optimization/` (algorithm selection only), `src/Core/Netlist/AnalysisDirectiveSchema.Tuning.cs`,
`src/Ui/Optimization/`, `src/Ui/Tuning/`, `src/Cli/ReferenceTuning.cs`, MCP `run analysis=optimize`
**Depends on:** SDL-6. SDL-9 for series-gap test benches.

---

## 0. What this adds

The designer optimises in the schematic they drew: its VAR block holds the variables (`w50`, `L90`),
its analysis has `model=em` (SDL-6 Path A), and its goals and `optimize` line sit beside them. The VARs
are already tunables. What is missing is a way to optimise them that respects what an EM solve costs: an
optimiser that calls EM at every evaluation can need hundreds of solves. This brief adds two ways to
optimise copper geometry, a budget that cannot be exceeded, writing the result back into the same
schematic and its layout, and an honest tuning-panel story. The same works for a cell placed as a part
(SDL-6 Path B), with the instance's VAR overrides as the tunables.

## 1. Decisions

- **D1 — Three modes, on the `optimize` line.** `em=correct` (the default whenever a goal reads an
  analysis with `model=em`, or a tuned variable reaches an instance with Model = EM), `em=direct`, and `em=off` (circuit model only, as today).
- **D2 — `em=correct`: the same schematic does the searching, EM corrects it.** The circuit model is
  the same schematic with its analysis run by circuit models (SDL-6 D1): it exists by construction,
  costs nothing, and is usually close. The loop:
  1. Optimise on the circuit model, as today, to *p₀*.
  2. Solve EM at *pₖ*. For every goal, at every sample of its trace, record the offset
     *dₖ = goal_EM(pₖ) − goal_circuit(pₖ)*.
  3. Optimise again on the circuit model **plus the fixed offset *dₖ***, starting at *pₖ*, with each
     variable limited to a step around *pₖ* (start: 10 % of its range). If the EM result at the new
     point is worse than at *pₖ*, halve the step and retry from *pₖ*.
  4. Stop when the EM result meets every goal, when the new point gives the same geometry as *pₖ*
     (same SDL-6 cache key: nothing more can change), when the step falls below the geometry's DBU, or
     at the solve budget.

  The offset is in goal space, so it works for any goal expression with no S-parameter algebra. Its
  limit is stated in the docs: a mismatch that moves a response in frequency, rather than lifting it,
  is corrected slowly; the run report then suggests a short `em=direct` pass from the last point.
- **D3 — `em=direct`: every evaluation is a cached EM solve.** Only algorithms that work in few
  evaluations are accepted: pattern search, Nelder-Mead, the trust-region model and Bayesian
  (`OptimizerAlgorithms`). A population algorithm is a refusal naming the accepted ones.
- **D4 — A budget is always set.** `emBudget=` (default 12 for `correct`, 30 for `direct`) caps uncached
  solves. The run stops at it and returns its best EM-verified point. SDL-6's counting pass reports the
  budget up front.
- **D5 — Steps are at least two DBU.** A length variable's finite-difference or search step is never
  below 2 DBU of the cell's layout, because smaller steps give an identical geometry and a zero
  gradient. Variables with no length unit are left alone.
- **D6 — Parameters that do not reach the copper are refused, not optimised.** SDL-6 D4's finding is a
  refusal here: an optimiser varying a parameter with no effect wastes the budget and reports a
  meaningless sensitivity.
- **D7 — Tuning stays live on the circuit model.** Sliders drive the circuit model. A **Solve EM here**
  button runs one EM solve at the current values and overlays its traces; they are marked stale as soon
  as a slider moves. Optional **Circuit + EM correction** traces apply the last solve's offset (D2) to
  the live circuit traces, labelled as an estimate.
- **D8 — Sweeps run per point; yield and sensitivity are refused in EM mode.** A `parametric_sweep`
  over the variables of an analysis run by EM solves (or reads from cache) each point under SDL-6 D5. Yield and
  sensitivity need hundreds of trials, so with `model=em` they are refused with the trial count and a
  pointer to `em=off`.

- **D9 — Connectivity is held fixed for the whole run.** A geometry optimisation changes lengths, widths
  and gaps; it must never change which pieces of metal touch. At the start point the run records the
  layout's **connectivity signature**: for each schematic net, whether all its pins are joined in copper,
  and for each pair of different nets, whether their copper touches. It comes from the planner's output
  through `DrcConnectivity`, mapped onto schematic nets, and costs no EM solve.
  - **Every candidate point is checked first.** The planner runs at the candidate's values (milliseconds)
    and its signature is compared with the start's. A different signature is an **infeasible point**
    (R-to7-8, which every algorithm already handles): no EM solve is spent on it, and the history records
    why, e.g. *"L90 = 3.912 mm: ML7 overlaps MT2 (nets n4, n9 touch)"* or *"w50 = 1.410 mm: loop gap at MT3
    pin 3"*.
  - **The `em=correct` inner search is checked too.** The circuit model knows nothing of geometry, so
    without the check its inner search would settle on values the layout cannot build.
  - **A gap by design is a pair of different nets.** Coupled-line spacing, a series gap (SDL-9), an
    interdigital finger gap, the gap a bridging resistor spans: the schematic already says the two sides
    are separate nets, so the signature records them as not touching. A variable driving that gap is
    free to vary, and is infeasible only where the two sides would touch, or where the gap falls below
    the technology's minimum spacing for that layer when the technology declares one.
  - **Gaps the user left open on purpose stay open.** A joint opened by hand (SDL-4 D6) is part of the
    start signature and is held like any other.
  - **The start must agree with the schematic.** If the start point's copper already has a loop gap, an
    unexpected short or an overlap the schematic does not imply, the run refuses before any solve, quoting
    the planner's report.
- **D10 — Ranges are scanned before the first solve.** For each variable alone, the planner runs at 64
  points across its range (other variables at their start values) and the run reports the interval
  around the start value over which connectivity holds: *"L90: connected from 3.95 mm to 6.20 mm (range
  3.00-8.00 mm)."* The GUI offers to tighten the bounds to it; CLI and MCP report it in
  `result.optimize`. The per-point check (D9) still catches combinations the one-at-a-time scan misses.
  The tuning panel shows the same interval on each slider, and a value outside it shows a status line
  naming the gap or short. It never blocks the slider.

## 2. Work

- **`R-sdl7-1`** Directive grammar: `em=`, `emBudget=` on `optimize`; `reference tuning` and
  `reference optimizers` updated; `check` validates them.
- **`R-sdl7-2`** The `correct` loop (D2) in `src/Design/Optimization/OptimizationRun.cs` as a new stage,
  reusing the existing optimiser for each inner run. The offset is applied in `GoalResiduals`, not in
  the goal text.
- **`R-sdl7-3`** `direct` (D3, D5) and the budget (D4).
- **`R-sdl7-4`** Results: `result.optimize` and the opt history `.npy` gain one row per EM solve (point,
  cache key, goal margins from EM). The best point written back is always EM-verified, never a circuit
  model point.
- **`R-sdl7-5`** Tuning panel (D7) and the Optimizer panel's mode and budget fields.
- **`R-sdl7-6`** Sweep and yield/sensitivity behaviour (D8).
- **`R-sdl7-8`** **Apply.** Until applied, the optimiser's best values exist only in its result; the
  schematic's VAR block and its saved layout still hold the starting geometry. One command on the result
  (the Optimizer panel's existing write-back, and `--apply` on the CLI/MCP run) writes each best value
  where its name is defined (a VAR's expression in the VAR block, or a parameter's default), so the
  design keeps its own structure; then runs Update Layout from Schematic (SDL-4's rules, so hand edits are
  kept); then checks that the saved layout's SDL-6 cache key equals the key of the EM-verified best point,
  so the layout on disk is exactly the geometry that was solved. A mismatch is reported, never ignored.
  For a placed EM component (Path B), where each value is written is SDL-10 D7's rule: the instance or
  the parent, never the cell unasked. One undo step per document touched.
- **`R-sdl7-7`** `ConnectivitySignature` in `src/Design/Layout/SchematicSync/` (D9), shared with SDL-8;
  the per-point check, the range scan and its report (D10), in GUI, CLI and MCP. Sweeps report points
  whose signature differs from the first point's and solve them anyway: a sweep may be studying exactly
  that.

## 3. Tests

Most tests use a **stub EM provider**: the circuit model with a known error (every length 3 % longer,
every width 10 µm wider), which makes "where EM's optimum is" known.

- `correct` on C2 itself (goals on its own `SP1 model=em`) meets its goals on the stub within 6 solves, and lands within the
  goals' tolerance of where `direct` lands.
- Budget: `emBudget=3` solves exactly 3 and returns the best of them, flagged as stopped by budget.
- Same-geometry stop: a point that moves less than a DBU ends the loop with no extra solve.
- D3 refusal for a population algorithm; D6 refusal for a parameter held in the layout; D8 refusal for
  yield with `model=em`.
- Write-back: the values written back are the EM-verified best.
- Apply: after an optimisation on C2, its VAR block holds the best values (still as VARs), its saved
  layout's cache key equals the best point's, and nothing else in the `.csch` changed. Path B is tested in
  SDL-10.
- Connectivity: on C2, a range for `L90` wide enough to make ML7 overlap MT2 gives infeasible points
  with no solve spent on them; the D10 scan reports the connected interval exactly; a coupled-line test
  bench optimises its spacing S and never evaluates S ≤ 0; a start point with a loop gap is refused.
- At most one real end-to-end (a single short MLIN, width tuned to a target |S11|, coarse mesh).
  Measure it; `Benchmark` if at or above ~5 s. No timing assertions.

## 4. Docs

User page `em-cells.md` gains *Optimising geometry*: the three modes, when to choose each, the budget,
and the D2 limit in plain words. Tuning panel page: Solve EM here and the correction traces.
`docs/design/cli.md` and MCP `run` description: the new options.

## 5. Gate

`dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~Optimiz|FullyQualifiedName~Tuning|FullyQualifiedName~SchematicSync"`,
`dotnet test tests/Engine.Tests --no-build --filter FullyQualifiedName~Optimization`,
`tests/Firewall.Tests`. UI: the Optimizer fields and the tuning button rendered with `tools/UiSnapshot`,
looked at, PNGs listed. Findings in `src/Design/RESOLVED.md`.
