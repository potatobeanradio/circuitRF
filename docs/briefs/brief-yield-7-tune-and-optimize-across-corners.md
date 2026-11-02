# Brief YA-7 — Tune and optimize across corners

**Series:** `brief-yield-0-overview.md` (D10) · **Tag:** `R-ya7-<m>` · **Depends on:** YA-6
**Area:** `src/Core/Design/TuningSetup.cs` (`OptimizerSettings`), `src/Design/Optimization/OptimizationRun.cs`,
`GoalResiduals.cs`, `src/Ui/Tuning/` (`TuneSession`, `TuningPanelViewModel`), `src/Ui/Optimization/`
(`OptimizerPanelViewModel*`), `src/Cli/Optimize.cs`, `docs/design/tuning-optimization.md`, `docs/design/yield.md`

---

## 1. Goal

What makes corners — and statistical corners above all — worth having: the optimizer can be told to meet its goals
**at every enabled corner at once**, and a designer can watch a slider's effect **at a chosen corner**.

## 2. Requirements

**R-ya7-1 — Optimize across corners.** `optimize corners=none|all|<names>` (default `none` — today's behaviour,
byte-for-byte). With corners, one optimizer point is evaluated at the nominal (unless `corners=` omits it with
`nominal=0`) and at each listed corner, through the batch door, in parallel; the **residual vector is the
concatenation** of every goal's residuals at every corner, so least squares and minimax keep their tuning-series
meaning (minimax then minimizes the worst violation over corners — the ordinary worst-case design). A goal is **met**
only when met at every corner. The cache keys by (values, corner).

**R-ya7-2 — Reporting.** The optimizer's goal report (`GoalReport`, the panel's goal list, the `opt` verb's table,
`--json`) gains the **binding corner** per goal — where its worst violation or tightest margin is — and a per-corner
breakdown in `--json`. Railing (tuning D17) is unchanged.

**R-ya7-3 — Statistical corners in the optimizer.** A statistical corner (YA-6 R-ya6-4) is replayed against **each
candidate point's** nominal — percent spreads move with the candidate. This is what lets a designer optimize against
the worst Monte Carlo trials and have them stay meaningful as the design moves.

**R-ya7-4 — Tuning at a corner.** The Tuning panel gains an **Evaluate at** picker in its toolbar: Nominal (default)
or any enabled corner. The live session evaluates at the chosen corner (its bindings over the schematic's own
corner selections). Push still writes nominal values only. The canvas tuned-label colour and the Data Display chip
(`Tuning · ss_85`) show the corner when one is chosen.

**R-ya7-5 — Cost honesty.** The Optimizer panel's status line and `explain --analysis` show evaluations per point
(1 + corners) so a 12-corner optimization is not a surprise. Sensitivity (TO-8) is computed over the same corner set.

**R-ya7-6 — Headless.** `opt --corners all|a,b` overrides the file; MCP `run analysis=optimize corners=…`.

## 3. Gates (minimal; run only these classes)
- `CornerOptimizationTests` — an RC low-pass whose cutoff goal is met at nominal but not at a hot corner: with
  `corners=none` the run ends met; with `corners=all` it ends at a different point, met at both, and the report names
  the hot corner as binding.
- `CornerOptimizationCacheTests` — a counter: evaluations = points × (1 + corners), cache hits on a revisited point.
- `TuneAtCornerTests` — the headless Tuning view model's result at a corner equals `CornerRun`'s for that corner.
- `OptCliVerbTests` gains: `--corners` reports the binding corner.
