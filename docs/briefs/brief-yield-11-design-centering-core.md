# Brief YA-11 — Design centering (yield optimization): core and headless

**Series:** `brief-yield-0-overview.md` (D1, D5, D11, D12, D13) · **Tag:** `R-ya11-<m>` · **Depends on:** YA-4, YA-5
**Area:** `src/Design/Statistics/` (`CenteringRun`), `src/Engine/Optimization/` (the existing ask/tell algorithms and
registry — reused, not copied), `src/Design/Optimization/OptimizationVariables.cs`, `src/Cli/Yield.cs`,
`src/Cli/Serve/ToolCatalog.cs`, `docs/design/yield.md`

---

## 1. Goal

Move the design's nominal values — the `opt=1` entries — to **maximize yield** while the tolerances ride along, and
report the yield before and after with honest intervals. Headless first; the panel's mode is YA-12.

## 2. Requirements

**R-ya11-1 — The problem.** Designable variables: entries with `opt=1` (their ranges, scales, steps and discrete
settings are the optimizer's, tuning D4 — one range per variable). Statistical variables: entries with `stat=1`,
plus kit draws. An entry may be both (the usual case: a resistor's nominal is designable and it has a tolerance); its
percent spread follows its candidate nominal (YA-2 R-ya2-5). Specs: goals with `use=yield|both`. A setup with no
`opt=1` entry, or no yield goal, is a refusal (`check` too).

**R-ya11-2 — Common random numbers.** Each centering iteration evaluates candidates against **one fixed set of M
z-vectors** (drawn once from the run's seed, D5), so two candidates' yields differ by the design, not by sampling
noise. M = `center trials=` (default 200).

**R-ya11-3 — A smooth objective.** The pass indicator is replaced by a smooth surrogate of it so derivative-free
methods see progress before a trial flips: per trial, the **smallest normalized margin** over the yield goals (each
goal's margin ÷ its `GoalResiduals` scale — the scale users already read), through a logistic of width `w`
(default 0.05 of the scale). The objective is the mean over the M trials; the reported number is always the **plain
yield** on the same trials, alongside. Did-not-evaluate trials score as fails (or as excluded, per D7).

**R-ya11-4 — Algorithms.** From the existing registry, as ask/tell over the unit box (tuning §10): default **CMA-ES**;
also mesh adaptive direct search, Nelder–Mead, Bayesian (slow benches) and discrete. Gradient methods are not offered
(the objective is piecewise flat at finite M) — the registry's existing metadata, not a hard-coded list, decides what
the menu shows: add a `SuitsNoisyObjective` flag rather than naming algorithms here.

**R-ya11-5 — Verification.** At the end, the best point is checked by an **independent** yield run (a different seed,
`center verify=` trials, default 1,000) with its Clopper–Pearson interval, and the start point is run against the
same verification trials, so "yield 71 % → 94 %" compares like with like. The report says if the verified gain is
within the intervals' overlap.

**R-ya11-6 — Settings.** `center [algorithm=<id>] [trials=<M>] [verify=<n>] [maxiter=<n>] [maxevals=<n>]
[timelimit=<d>] [width=<w>] [parallel=<n>] [seed=<n>]` — a new `.cnl` directive and `.csch` field (YA-1's
conventions, round-trip byte-stable, defaults omitted). Evaluations per iteration = population × M; `explain
--analysis` states the estimated total before anything runs.

**R-ya11-7 — Results.** A yield-vs-iteration history (best plain yield, best smooth objective, evaluations), the best
nominals as value text (the schematic's own form), railed variables (tuning D17), the verification result, and the
verification run's full YA-4 DataSet written to `<schematic>.yield.npy`. Pause/resume/stop as the optimizer (tuning
D16): Stop keeps the best point and still verifies unless cancelled.

**R-ya11-8 — Headless.** `circuitRF yield center <path>` with the `center` settings as flags, `--save-preset <name>`
(the centred nominals; D12), exit codes D13 (3 = verified yield below `--target`). MCP `run analysis=center` with
per-iteration progress (iteration, best yield, evaluations). `reference statistics` gains the centering section and
the MCP walk-through one line: "then `run analysis=center` and `read` the verified yield".

## 3. Gates (minimal; run only these classes)
- `CenteringDividerTests` — a divider whose spec window is off-centre for the starting nominal: centering moves the
  nominal toward the window's centre and the verified yield rises by more than the intervals' half-widths.
- `CommonRandomNumbersTests` — two evaluations of the same candidate return the identical objective; the trial set
  does not change between iterations.
- `CenteringSettingsRoundTripTests` — the `center` line round-trips byte-stable.
- `CenterCliVerbTests` — exit 0/3 by target; `--save-preset` adds exactly one preset.
