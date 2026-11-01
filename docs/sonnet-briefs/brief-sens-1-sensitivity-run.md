# Brief SENS-1 — Sensitivity: a ranked list of what moves each goal, at the nominal design

**Series:** SENS-1 … SENS-3 (this brief holds the series decisions, §0) · **Tag:** `R-sens1-<n>`
**Area:** new `src/Design/Statistics/SensitivityRun.cs` + `SensitivityDataSet.cs`, `src/Design/Statistics/DoeRun.cs`
(factor out the σ-level helper it already has), `src/Core/Design/TuningSetup.cs` + the `.cnl`/`.csch` tuning
serializers (a `sensitivity` line), `src/Design/Statistics/StatisticsValidator.cs`, `src/Render/DataDisplay/
ContributionParetoPreset.cs` (take a group), `src/Cli/Yield.cs` (a `sens` noun), `src/Cli/Serve/ToolCatalog.cs`,
`src/Cli/Explain.cs`, `docs/design/yield.md` (new §18), `docs/design/results-dataset-layout.md`,
`docs/design/cli.md` §25, the `reference statistics` topic
**Depends on:** the yield series (YA-0…16) and TO-8 as built. Nothing in SENS-2 or SENS-3.

---

## 0. The series

### The gap

circuitRF can already say what matters, in four places, but none is a quick ranked answer at the nominal design:

| Where | What it ranks | Why it is not the answer |
|---|---|---|
| Optimizer ▸ Sensitivity, `optimize --sensitivity` (`OptimizationRun.Sensitivity`, TO-8 R-to8-6) | ∂cost/∂u of the `opt=1` entries across their ranges | needs an optimization run first; normalized by tuning range, not by production spread |
| Yield ▸ Contributions, `yield mc --contributions` (`StatisticalContributions`, YA-4 R-ya4-9) | each tolerance's share of a goal's spread | the right production ranking, but it needs a Monte Carlo (hundreds of simulations) |
| Yield ▸ DOE, `doe factors=stat` (YA-14) | ±kσ effects per goal, as an effects Pareto | a deliberate setup; screening designs alias interactions |
| Data Display ▸ Yield Sensitivity vs (YA-8) | yield per bin of one variable | one variable at a time |

And an SnP block offers nothing to any of them: its only parameters are its file and interpolation settings, so no
S-parameter of a Touchstone block can be ranked, toleranced or optimized (SENS-2).

### What the series builds

| Brief | Builds |
|---|---|
| **SENS-1** (this) | the run: one-at-a-time ± steps about the nominal through the one evaluator, the ranked result per goal, `yield sens`, MCP `run analysis=sensitivity`, the `sensitivity` line |
| **SENS-2** | every S-parameter of every SnP instance as an ordinary tunable (`dB` and `deg` offsets), reciprocal pairs tied automatically from the file, and the entry / port / instance roll-ups of the ranking |
| **SENS-3** | a **Sensitivity** dock panel — the Yield panel's view with its own mode set — tabbed between Tuning and the Optimizer, the matrix tile, the Pareto display, docs and an example |

### Decisions (apply to all three briefs)

- **D1 — One evaluator.** Every simulation goes through `OptimizationRun.ForEvaluation` / `EvaluateValues`, exactly
  as a Monte Carlo trial and a DOE run do (cache, parallelism, not-re-entrant rule). Sensitivity is a fourth client of
  that door, never a second evaluation path.
- **D2 — Central one-at-a-time steps.** For k factors: the nominal, then each factor at +step and −step with every
  other factor at nominal — 2k + 1 evaluations, deduplicated through the cache. No random draws, so the result is
  deterministic and needs no seed.
- **D3 — The step is the production spread where one is known.** A factor with a tolerance (`stat=1`) steps ±kσ
  (`sigma=k`, default 1) through its OWN distribution, by the same code DOE's `levels=sigma:k` uses (`DoeRun`, the
  `SampleValues.Apply` call at ~line 428, truncation clip included) — factor that into one shared helper; do not copy
  it. A factor with no tolerance steps ±`step` (default 1 %) of its value on its own scale. SENS-2's SnP entries step
  ±`snpdb` dB (default 0.1) and ±`snpdeg` degrees (default 1) when they carry no tolerance. **Every contributor states
  its basis** (`σ`, `1 %`, `0.1 dB`, `1°`), because a ranking that mixes bases is a ranking of what a designer could
  change, not of what production will do — both are useful, and the result must say which it is.
- **D4 — The metric is each goal's normalized margin.** For goal g at a point: its margin at the tightest point (YA-4
  R-ya4-4) divided by `GoalResiduals.Scale` — the same normalization centering uses (`docs/design/yield.md` §15.2) —
  so goals in dB and in volts rank on one footing. Δ_i,g = (m⁺ − m⁻)/2. Every real scalar `measure` is a response
  too, with Δ in its own unit (no ranking across measures).
- **D5 — The ranking.** Per goal: contributors by |Δ| descending; share_i = Δ_i² / Σ Δ²; predicted spread
  σ_g = √Σ Δ² (normalized margin units). **A first-order pass estimate Φ(m₀/σ_g) is reported only when every
  contributor's basis is σ** — with any `%` or `dB` step in the sum, σ_g is not a production spread and the estimate is
  left out (the reason stated once, in the result's notes). It is labelled `estimated`; the Monte Carlo is the yield.
- **D6 — Nonlinearity is flagged, not hidden.** asymmetry_i = |(m⁺ − m₀) + (m⁻ − m₀)| / max(|m⁺ − m₀|, |m⁻ − m₀|).
  Above 0.5 the contributor is flagged `nonlinear` — typically a goal over a sweep whose tightest point moves between
  the two steps, or a factor near a resonance. The flag is a column, not a paragraph; `reference statistics` says once
  that a flagged factor wants DOE (`design=ccf`) or a Monte Carlo.
- **D7 — Never run unasked** (the YA-4 / TO-8 rule). Nothing computes a sensitivity on load, on edit, or after
  another run.
- **D8 — Factors are not correlated and kit statistics are not factors**, as in DOE §17.1: each factor moves alone;
  the result notes `uncorrelated` when the bench declares correlations, and names the kit streams it left out.

## 1. Requirements

**R-sens1-1 — Factors.** `factors=all|stat|opt` (default `all`):
- `stat` — the `stat=1` entries (toleranced), at ±kσ.
- `opt` — the `opt=1` entries, at ±`step` of their value (not of their range: the question is "what moves the goal
  near here", and a range is a search box, not a spread).
- `all` — every tunable the catalog offers (`TunableCatalog.Discover` / `FromNetlist`), each at its tolerance when it
  has one and at ±`step` otherwise. Left out of `all`, each named once in the result's notes with its reason: a
  vendor part's choice of part (not a number), an integer parameter (a finger count), a value disabled by a sweep, a
  read-only value the catalog already refuses. A complex value contributes its `mag(...)` part at ±`step` and its
  `phase(...)` part at ±1° (D18's parts, never the whole value).
- `--vars key,key` narrows any of the three (as `yield mc --vars` does).

**R-sens1-2 — Goals and responses.** The goals analysed: the enabled `use=yield|both` goals; when the bench has none,
the enabled `use=opt|both` goals; `--goals` names them explicitly. A bench with no goal and no scalar measure is a
refusal naming both ways to add one (`goal … use=yield`, `measure …`). Every real scalar measure is a response.

**R-sens1-3 — The run service.** `SensitivityRun` (`src/Design/Statistics`): `Create(PreparedCircuit, SensitivityOptions)`,
`Describe()` (factors, evaluations = 2k + 1 less the cache's known points), `Run(progress, ct)`, Pause/Resume/Stop on
`DoeRun`'s pattern. The nominal must evaluate; if it does not, exit 2 with the nominal's own failure. A step that
fails to evaluate leaves that contributor `failed` with the evaluator's sentence, and the rest still rank (a partial
answer with the failure named is worth more than none). If one direction alone failed, the contributor uses a forward
or backward difference from the side that ran, labelled `one-sided`.

**R-sens1-4 — The result.** `<design>.sens.npy` beside the design (`SensitivityRun.ResultPathFor`), group `sens`:
- `sens.contrib:<goal>` — shares over a labelled `contributor` axis, largest first; `sens.contrib:<goal>:shown` (the
  same values: shares here are already 0–1) and `sens.contrib:<goal>:cumulative`, so `ContributionParetoPreset` draws
  it unchanged once it takes the group as a parameter (today it reads `ResultContributions.Group` — `yield`).
- `sens.delta:<goal>` (signed Δ, normalized), `sens.plus:<goal>`, `sens.minus:<goal>`, `sens.nominal:<goal>`,
  `sens.asymmetry:<goal>` over the same axis; the same set `sens.delta:<measure>` … for each scalar measure.
- `sens.basis` (text per contributor: `σ`, `1 %`, …), `sens.step` (the value text actually set at + and −),
  `sens.status` (`ok|nonlinear|one-sided|failed`), `sens.spread:<goal>` and, when D5 allows it, `sens.estimate:<goal>`.
- `sens.notes` — the left-out factors, `uncorrelated`, and why an estimate is absent.

Add the layout to `results-dataset-layout.md`. The file is the only write; the design is never changed.

**R-sens1-5 — The `sensitivity` line.** `sensitivity [factors=all|stat|opt] [step=<pct>%] [sigma=<k>]
[snp=on|off] [snpexclude=<instance>,<instance>] [snpdb=<dB>] [snpdeg=<deg>] [responses=goals|all] [parallel=<n>]`
on `TuningSetup` (beside `Doe`) and `CschTuning`.
YA-1's conventions: defaults are never written (a value equal to its default reads back null), a malformed value is
`cnl.sensitivity.value-invalid`, a second line is `cnl.sensitivity.duplicate`, and `.cnl`/`.csch` round-trip byte-stable.
`snp`, `snpexclude`, `snpdb` and `snpdeg` are accepted and validated now and used from SENS-2 (R-sens2-5). The line exists so the panel's settings persist
with the design and a headless run reproduces what the panel ran.

**R-sens1-6 — `check` and `explain`.** `check` refuses what the run would refuse (no factors, no goals or measures, a
`--vars` key that is not offered). `explain --analysis` reports the factor count, the evaluations it will spend and
the per-evaluation cost, as it already does for a Monte Carlo — the cost line is what lets an agent decide before it
spends.

**R-sens1-7 — Headless.** `circuitrf yield sens <path>` — a seventh noun on `yield`, not a new verb: one run service,
one set of output conventions (`docs/design/cli.md` §25). Flags: `--factors`, `--step`, `--sigma`, `--vars`,
`--goals`, `--responses`, `--parallel`, `--set`, `-o`, `--json`, `-q`, and `--top <n>` (the text table's length,
default 10; `--json` always carries every contributor). Exit 0 done, 1 refused, 2 the nominal did not evaluate,
130 cancelled — there is no target, so no 3. stdout text: per goal, `goal  m₀  σ_g  [estimated pass]`, then the
table `rank  contributor  basis  Δ  share  status`. MCP: `run analysis=sensitivity` (`ToolCatalog`, aliases
`["yield","sens"]`), a progress notification per batch, and `result.sensitivity` in the JSON:
`{goals:[{goal, nominal, spread, estimate?, contributors:[{key, label, basis, delta, share, status}]}], measures:[…],
notes:[…], evaluations}`. Its description says what the answer is for in one sentence ("which values move each goal
most at the nominal design, and by how much per tolerance or per 1 %").

**R-sens1-8 — Docs.** `docs/design/yield.md` gains §18 (the decisions above, the result, the line, the verb);
`reference statistics` gains a Sensitivity section with one runnable example and the guidance: sensitivity first,
then Monte Carlo on what it ranks, then DOE `design=ccf` on anything flagged nonlinear. `docs/user/src/reference/
yield.md` is SENS-3's (the panel is what a user reads about).

## 2. Gates (minimal — one test per claim; run only these classes)

- `SensitivityRunTests` (`tests/Ui.Tests/Statistics/`, beside `DoeTests`):
  - on a resistive divider with goal `Vout in 0.49 0.51` and R1 at ±1 %, R2 at ±5 %: R2 ranks first; Δ matches the
    analytic ∂Vout/∂R · step / scale to 1e-6; shares sum to 1.
  - the evaluation count is 2k + 1, and a second run on the same evaluator spends 0 (cache).
  - one factor with a tolerance and one without: no `estimate`, and the note says why; both with tolerances: an
    `estimate` equal to Φ(m₀/σ_g).
  - a goal over a sweep whose tightest point jumps between steps is flagged `nonlinear`.
- `SensitivityLineTests` — the line round-trips through `.cnl` and `.csch` byte-stable, defaults unwritten.
- `YieldSensCliTests` (beside `YieldCliTests`) — `yield sens` as a process equals the in-process run's `.sens.npy` byte for byte (the YA-5
  pattern) and its `--json` matches `result.sensitivity`.

Measure the shipped Bandpass example's run time once and record it in `src/Design/RESOLVED.md` — no timing test.

## 3. Out of scope

SnP entries (SENS-2); the panel and display (SENS-3); adjoint derivatives (finite differences through the one
evaluator are the contract — a faster derivative may come later behind the same result); frequency-resolved
sensitivity curves.
