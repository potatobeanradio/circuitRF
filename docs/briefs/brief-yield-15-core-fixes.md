# Brief YA-15 — Review fixes: the statistics core and run services

**Series:** `brief-yield-0-overview.md` (follow-up after YA-13/YA-14) · **Tag:** `R-ya15-<m>` · **Depends on:** all
**Area:** `src/Core/Netlist/TuningDirectiveText.cs`, `src/Core` reductions (`SampleStatistics`,
`Evaluator.Reductions.cs`), `src/Engine/Statistics/` (`LeastSquares`), `src/Design/Statistics/` (`StatisticsValidator`,
`StatisticalRun`, `StatisticalDataSet`, `TrialReplay`, `CenteringRun`, `ExpressionDraws`, `CornerGenerator`,
`StatisticsSummary`), `src/Cli/Yield.cs` (`--confidence` only), `docs/design/yield.md`

---

## 1. Goal

A post-series review of the numeric core and the run services found the defects below. Fix each one, with one small
gate per claim. Each finding gives its source location as the review saw it, so **confirm it before changing
anything**. If a finding turns out to be wrong, say so in the completion note and leave the code alone.

The review also checked these areas and found them sound; **do not touch them**: the Clopper–Pearson edges, the
Student-t quantile, truncated-normal moments, scrambled Sobol and LHS stratification, the Gaussian copula and the
nearest-correlation repair, the per-entry and per-kit stream separation, determinism across pause, resume, stop and
parallel batches, auto-stop ordering, `nonconverged=fail|warn` counting, the evaluator cache keys, `ResolvedSpread`
scaling, and the DOE fraction and Plackett–Burman tables.

## 2. Requirements

**R-ya15-1 — Out-of-range settings are refused, never thrown (most severe).**
- **The gap:**
  - `TuningDirectiveText` (≈ lines 120–128) accepts any `confidence=`, `target=`, `trials=` or `sigmascale=`, and
    accepts `parallel=0`.
  - `StatisticsValidator.Settings` (≈ 236–243) checks none of them.
  - `circuitrf yield --confidence` accepts `100%`.
- **What goes wrong today:**
  - `confidence=100%` (or `0%`) reaches `ClopperPearson.Interval`, which throws `ArgumentOutOfRangeException` from
    `StatisticalRun.Count`. That call is inside the batch loop (`Progress()`) and in `Finish()`, and only
    `OperationCanceledException` is caught there. The run dies with no exit code from D13's table.
  - `explain` crashes the same way through `StatisticsSummary.ExpectedInterval`.
  - `trials=0 sampling=lhs` throws from the `SamplingPlan` constructor inside `StatisticalRun.Create`.
  - `trials=0` under `random` "succeeds" with exit 0 and a NaN yield.
- **Fix:** `StatisticsValidator.Settings` refuses:
  - confidence outside the open interval (0, 100) %;
  - target outside (0, 100] %;
  - `trials < 1`;
  - `sigmascale <= 0`;
  - `parallel < 1`.
- **Behaviour:** `check`, `explain`, every `yield` noun, MCP and the Yield panel then refuse in the same sentence,
  because they all go through `StatisticalRun.Create` or the validator. The CLI's `--confidence` check becomes
  `p < 100`.
- **Gate:** one test per bound, through the validator, plus one CLI test proving `--confidence 100%` exits with the
  refusal code and does not crash.

**R-ya15-2 — `LeastSquares.Fit` with a dependent column comes before an independent one.**
- **Where:** `src/Engine/Statistics/LeastSquares.cs` ≈ 37–72.
- **The defect:** a column with no pivot is skipped, and so is its row. Later Householder reflections then start
  below that row, so that row of the transformed right-hand side never enters the remaining columns. The class doc
  promises that a dependent column gets coefficient 0 and leaves the others unchanged.
- **Reproduced by the review:** columns `[1, x, 0, z]` over 12 noisy points give 0.99868 / 1.98998 / 0 / 3.03966
  (R² 0.994804). The same fit with the zero column removed gives 0.99655 / 1.97475 / 3.00909 (R² 0.994902).
- **Who it reaches:**
  - `DoeEffects.Fit`, where a run dropped as NaN can make the design rank-deficient;
  - `QuadraticSurrogate.Fit`, where the design is full rank today, so this is latent there.
- **Fix:** keep a separate pivot-row counter that advances only for a live column, or move to column-pivoted QR.
- **Gate:** the review's case. The fit with the dependent column must equal the fit without it to ~1e-12, and that
  column's coefficient must be 0.

**R-ya15-3 — A statistical corner saved from a stopped LHS run records the planned trial count.**
- **The defect:** `TrialReplay.CornerOf` (≈ line 55) reads `yield.trials`. `StatisticalDataSet` (≈ line 150) writes
  that as the number of records actually run, not the planned N. Under `lhs`, every trial's permutation depends on N.
- **Failure:** a `trials=500` LHS run is stopped at 230. Save as corner from the Data Display writes `trials=230`.
  Whenever the `.yield.npy` is not at hand, the replay draws a different z-vector.
- `StatisticalCorner.FromRun` already uses `EffectiveTrials` correctly.
- **Fix:**
  - Write a `yield.planned_trials` scalar beside `yield.trials`, and add it to `docs/design/results-dataset-layout.md`.
  - `CornerOf` reads it, falling back to `yield.trials` for files written before this brief.
- **Gate:** a stopped LHS run → Save as corner → replay without the `.npy` gives the stopped run's trial z.

**R-ya15-4 — Re-run trial refuses a result drawn under a different seed or sampling.**
- **The defect:** `TrialReplay.Run` (≈ 31–43) re-runs trial N under the design's CURRENT seed and sampling.
- **Failure:** change `seed=` after a run, then "Re-run trial 17" on the old result. It silently draws trial 17 of
  the new seed and labels it as the old trial.
- **Fix:** compare the result's `yield.seed` and sampling against the setup, as `RecordedTrial.FromDataSet` already
  does. Prefer the result's own seed and sampling over the setup's: the user asked to re-run THAT trial. Refuse only
  if the result does not record them, naming what differs.
- **Gate:** re-running after a seed change reproduces the original trial's values.

**R-ya15-5 — Tolerances on a complex part's phase are labelled in the wrong unit.**
- **The defect:** `StatisticalRun.Record()` stores the SI value (`x · Units.Scale("deg")`, i.e. radians). But
  `StatisticalDataSet` (≈ 86 and 134) labels the cube with `Units.BaseUnit("deg")`, which returns `"deg"`, because
  `deg` is the one scaled unit missing from the base-unit map.
- **Failure:** a `phase(ZL)` entry drawn at 30° appears in `trials.stat:phase(ZL)` and `nominal` as 0.5236 "deg".
- **Fix:** map `deg` → `rad` in `Units.BaseUnit`. Before changing it, grep for every other caller of `BaseUnit("deg")`
  (or of `BaseUnit` on an angle). If any caller relies on today's answer, record the angle in the entry's own unit at
  the yield call sites instead, and say which you chose and why.
- **Gate:** a phase entry's trial cube reads in a unit consistent with its numbers.

**R-ya15-6 — Formatting is culture-invariant.**
- **Where:** `StatisticalRun.cs` ≈ 222–223 (`{EffectiveConfidence:G4}`, `{target*100:G4}`), `CenteringRun.cs` ≈ 608
  (`{stallTol:G3}`), `Evaluator.Reductions.cs` ≈ 272 and 322–324.
- **Why it matters:** these strings reach the `yield.stopped` label inside the `.npy` and the CLI output. On a
  comma-decimal machine 99.5 % prints as "99,5 %".
- **Fix:** format with `CultureInfo.InvariantCulture`. Also grep the rest of `src/Design/Statistics` and
  `src/Engine/Statistics` for any interpolated or `ToString` number without a culture.
- **Gate:** one test that runs a stopped-label format under `de-DE` and asserts a period.

**R-ya15-7 — The non-physical-draw warning applies `sigmascale`.**
- **The defect:** `StatisticsValidator.Entry()` calls `NonPhysicalProbability` on the unscaled spread.
- **Failure:** a Gaussian R with σ = 10 % and `sigmascale=2` has P(R ≤ 0) ≈ 3e-7, above the 1e-9 threshold, yet no
  warning appears. Those trials then fail to evaluate.
- **Fix:** scale σ before asking.
- **Gate:** that case warns.

**R-ya15-8 — A percentile at an infinite extreme returns the extreme, not NaN.**
- **The defect:** in `SampleStatistics.Percentile` (≈ line 52), when `h == lo` the code computes
  `0 · (Inf − Inf)`, which is NaN.
- **Failure:** a worst-value sample whose minimum is −Inf (dB of an exact 0) shows NaN for min and p1 in the
  statistics table.
- **Fix:** return `s[lo]` when `h == lo`.
- **Gate:** percentile 0 of `[-Inf, 1, 2]` is −Inf.

**R-ya15-9 — Latent items.** Fix each one cheaply or record it, without a test for each:
- `ExpressionDraws.For` (≈ 48–53) accepts `unplannedAtNominal` and never passes it on. Wire it through, or delete the
  parameter if nothing will ever set it.
- `CornerGenerator.CrossProduct` multiplies its corner count in a `long` before the 256 cap. Check the cap while
  multiplying.
- The DOE stat levels are documented as "nominal ± kσ exactly for a Gaussian". That is not true for a truncated
  Gaussian, because z = k goes through the truncated quantile (`trunc=2` with `sigma:3` gives about 1.99σ). Either
  map k through the untruncated σ, or correct the sentence in `docs/design/yield.md` §17 and the user page's DOE
  section. Prefer whichever makes the levels mean what a designer reads.

## 3. Gates (minimal; run only these classes)

- New `tests/Ui.Tests/Statistics/ReviewCoreFixesTests.cs`: R-ya15-1, 3, 4, 5, 6, 7.
- `tests/Engine.Tests/Statistics/`: a new `LeastSquaresTests` for R-ya15-2.
- `tests/Core.Tests`: the `StatisticsFunctionTests` class gains the R-ya15-8 case.
- Re-run the existing classes whose code you touched: `YieldRunTests`, `CenteringTests`, `SurrogateTests`, `DoeTests`,
  `CornerTests`, `YieldExampleTests`. **Run targeted classes only** (`--filter "FullyQualifiedName~<Class>"`), never a
  whole project. Rebuild each test project you run before using `--no-build`.

## 4. Out of scope

- The CLI flag hygiene, the MCP output and every Data Display finding. Those are YA-16.
- Any change to the sampling, correlation or counting maths that the review found sound.
- DocGen. If you edit a user-page sentence (R-ya15-9), edit only the source.

## 5. Completion note

Add a short "review fixes" entry to `src/Design/RESOLVED.md`, listing what was confirmed, what was fixed and anything
found not to be a defect. Do not write to any `CLAUDE.md`, and do not commit.
