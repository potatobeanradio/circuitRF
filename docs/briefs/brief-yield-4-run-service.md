# Brief YA-4 — The Monte Carlo and yield run service

**Series:** `brief-yield-0-overview.md` (D4, D5, D7, D8, D9, D14) · **Tag:** `R-ya4-<m>`
**Depends on:** YA-2 (YA-3 for kit draws; this phase's gates use designer tolerances only)
**Area:** `src/Design/Statistics/` (`StatisticalRun` and friends), `src/Engine/Statistics/` (interval, moments,
regression), `src/Core/Expressions/Evaluator.cs` (the `_over` reductions), `docs/design/results-dataset-layout.md`,
`docs/design/measurements.md`, `docs/design/yield.md`

---

## 1. Goal

One headless service that runs a Monte Carlo or a yield analysis on a `PreparedCircuit`, streams progress, can be
paused, resumed and stopped, and returns one `DataSet` that every later consumer — the CLI, MCP, the Data Display,
the panel — reads without computing anything of its own.

## 2. Requirements

**R-ya4-1 — Shape.** `StatisticalRun.Create(PreparedCircuit, StatisticalOptions)` mirrors `OptimizationRun.Create`:
the same refusal style (a setup that cannot start is a refusal with `check`'s sentence — YA-1 R-ya1-4 calls the same
code), the same `CancellationToken`, pause and resume (D14), and an `IProgress` that reports after each batch:
trials done / counted / passed / did-not-evaluate, yield and interval, per-goal yield. Modes: `MonteCarlo` (no goals
needed; every enabled goal is still scored if present) and `Yield` (needs at least one goal with `use` ≠ `opt`).
`analyses=goals` runs only the analyses the yield goals name; `all` runs every analysis on the bench.

**R-ya4-2 — Trials.** The nominal is evaluated once first (its failure is a refusal — the design does not run).
Trials are evaluated in batches through YA-2's public batch door, `parallel=` at a time, numbered 1…N in the order
they were **drawn**, not finished; results are slotted by trial number so the DataSet is identical for any
parallelism. A did-not-evaluate trial (D7) records its reason; `nonconverged=fail|warn` decides whether it counts.

**R-ya4-3 — Yield and interval (D8).** Overall and per-goal: passes, counted trials, yield, Clopper–Pearson interval
at `confidence=`. The exact interval is computed from the regularized incomplete beta function (written in-house,
tested against tabulated values). Auto-stop as D8.

**R-ya4-4 — The DataSet (D9).** Fix the names against `results-dataset-layout.md` and record them there. At least:
every analysis cube the run keeps, with **`trial` as its outermost axis**; `stat:<key>` [trial] (value in base SI);
`z:<stream>` [trial] (the standard-normal draw — what statistical corners and centering replay); per goal
`goal:<name>:pass`, `goal:<name>:margin` [trial] (the margin in the goal expression's unit, GoalScore's rule) and
`goal:<name>:worst` [trial] (the expression's value at that tightest point — what a histogram against the spec
line is drawn from, YA-8);
`pass` [trial]; `status` [trial] with a reason table; the **nominal** as its own group with the same cube names and
no trial axis; and a `yield` summary group (the numbers of R-ya4-3, the seed, sampling, trial count, settings).
Kit draws are recorded per stream, grouped process/mismatch.

**R-ya4-5 — Save policy.** `save=scalars` keeps everything in R-ya4-4 except the analysis cubes' trial copies;
`all` keeps every trial's analysis cubes; `<n>` keeps the first n trials' analysis cubes and scalars for all;
`auto` (default) keeps all when the estimated size is under 256 MB, otherwise the largest n that fits, and the
report says which. Estimate from the nominal's cube sizes before the first trial.

**R-ya4-6 — Writing.** The DataSet is written to `<schematic>.yield.npy` beside the schematic (`<bench>.yield.npy`
for a `.cnl`) when the run finishes or is stopped, never on cancel. While it runs, it is published in memory under
that source identity through the tuning series' in-memory Data Display source (D14), coalesced.

**R-ya4-7 — Re-run one trial.** `StatisticalRun.EvaluateTrial(n)` returns trial n's full DataSet regardless of save
policy, identical to trial n inside a full run (D5) — this is what "Re-run trial", Send-to-Tuning and the CLI's
`yield trial` use.

**R-ya4-8 — Statistics as expression functions.** Reductions over an axis, in the `max_over` family
(`Evaluator.EvalReduceOver`), defaulting to the `trial` axis when the operand has one: `mean_over`, `std_over`,
`median_over`, `pctl_over(x, p)`, `skew_over`, `kurt_over`, `min_over`/`max_over` (existing), `yield_over(cond)`
(fraction true), `cpk(x, lo, hi)` (either limit may be omitted), `sigma_to(x, limit)`. And three that build a new
axis: `histogram(x, bins[, lo, hi])` (a `bin` axis of bin centres with counts; a `width` companion), `cdf(x)`
(sorted values against cumulative fraction), `yield_sens(pass, x, bins)` (per bin of x: yield and count). They work
in `measure` lines, in goals (a goal on `std_over(...)` is legal and costs a Monte Carlo — say so in `reference`), in
the Data Display and in `plot`. `measurements.md` documents them.

**R-ya4-9 — Contributions.** `StatisticalRun.Contributions(goalOrMeasure)` — for each stat entry and each kit
draw group: the standardized regression coefficient of the scalar on the z-values, the share of explained variance,
R², and Spearman rank correlation. Ordinary least squares when trials ≥ 5 × variables, otherwise ridge with the
"underdetermined" flag set. Mismatch draws of one instance are grouped into one contributor (the instance), because a
designer can act on an instance, not on a stream. Never run unasked (like `Sensitivity`).

**R-ya4-10 — Worst trials.** Per goal, the k trials with the smallest margin (default 10), with their sample values —
the list the panel's trial table and the CLI's report print.

## 3. Not in this phase
Corners (YA-6), centering (YA-11), any UI or CLI verb (YA-5 wraps this).

## 4. Gates (minimal tests, run only these classes)
- `YieldDividerTests` — a resistive divider with Gaussian R1, R2 and a goal `Vout in [a, b]`: the yield of a
  2,000-trial fixed-seed run lies inside the interval around the **exact** yield computed from the normal CDF of the
  linearized ratio (state the linearization error bound in the test).
- `YieldDeterminismTests` — `parallel=1` and `parallel=8` produce identical DataSets; `EvaluateTrial(n)` equals trial n.
- `NonconvergedPolicyTests` — a forced failing trial is a fail under `fail` and excluded under `warn`, reported both ways.
- `AutoStopTests` — a design at ~100 % yield with `target=90%` stops at the first batch whose lower bound clears 90 %,
  and the trial count is the one computed independently from the beta quantile.
- `ClopperPearsonTests` — against tabulated intervals.
- `StatisticsFunctionTests` — one case per function on a known vector.
- `ContributionTests` — a divider where Vout depends on R1 only: R1's share > 0.99 and R2's < 0.01.
