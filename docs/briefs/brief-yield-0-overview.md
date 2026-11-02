# Brief series — Monte Carlo, Yield, Corners and Design Centering (YA-1 … YA-13, optional YA-14)

**Status:** written, not started · **Date:** 2026-10-07 · **Decisions:** locked (§2), owner-reviewed
**Scope source:** `docs/PRD.md` v3.1 §2 / §5.1 ("yield is the planned follow-on to tuning and optimization")
**Builds on:** the Tuning and Optimization series (`brief-tuneopt-0-overview.md`, design note
`docs/design/tuning-optimization.md`, especially §19 "the seams overview §5 reserved") and the PDK corner work
(`src/Core/Pdk/PdkCorners.cs`, `src/Ui/Schematic/WorkspaceCorners.cs`, `docs/design/spice-models.md` §8.11, §10)
**Requirement tag:** `R-ya<n>-<m>` (phase n, requirement m).
**Design note to be written by YA-1:** `docs/design/yield.md` — this overview is its source.

---

## 0. What the owner asked for (paraphrased)

- A **Monte Carlo and yield** system for schematic simulation, reusing as much of Tuning and Optimization as
  possible, at least matching what the commercial RF and IC design suites offer their users.
- The **earlier PDK corner work** must be usable by it.
- Any **additional Data Display plotting** it needs.
- It must work **from the CLI and over MCP**: an AI agent must be able to set up and run a Monte Carlo and a yield
  analysis on its own.

Owner answers on 2026-10-07 (paraphrased):
- Tolerances live **on the same variable entry** as tuning and optimizing — one row per parameter, with a Tolerance
  column.
- Yield specs **are goals**. The intended workflow: a designer centres the design with the optimizer against a goal,
  then loosens that goal for the yield analysis.
- A trial whose simulation does not converge is **counted as a fail by default and reported separately**; an option
  reports it as a warning instead (excluded from the yield).
- In scope beyond the minimum: a **quadratic surrogate for design centering only**. Out of scope: high-sigma methods.
  Design of experiments was then added as an **optional** phase (YA-14), off the critical path.
- Only the SPICE-dialect kit statistics are known to matter today; another simulator dialect's statistics blocks can
  be added **later, if needed**.
- A new **Yield** panel beside Tuning and Optimizer.

### 0.1 The commercial baseline (researched 2026-10-07; no product names, by repo rule)

What the board/module RF suites and the IC analog/RF environments offer, and so what circuitRF users will expect as
a minimum. Each line names the phase that delivers it.

| Expected capability | Phase |
|---|---|
| A tolerance on any component parameter or design variable, in the same per-parameter setup as tune/optimize | YA-1 |
| Distributions: Gaussian (±σ % or absolute), Uniform (min/max, ±Δ %, ±Δ), Discrete (min/max/step), LogNormal; truncation | YA-1, YA-2 |
| Correlation between any two statistical variables, any distributions; a non-positive-definite matrix repaired with a warning | YA-1, YA-2 |
| Kit statistics: **process** (one draw per trial, shared) and **mismatch** (per device instance); global σ scaling | YA-3 |
| Monte Carlo (spread only), Yield analysis (pass/fail against specs), Yield optimization / design centering | YA-4, YA-11 |
| Trials, seed (same seed → same trials), re-run one trial; random / Latin hypercube / low-discrepancy sampling | YA-2, YA-4 |
| Auto-stop when yield is confidently above or below a target; yield with a confidence interval; per-spec yield | YA-4 |
| What to save (scalars vs every trial's sweep); live display while running; parallel trials | YA-4, YA-10 |
| Corner analysis: process corner × temperature × supply combinations; mismatch Monte Carlo at each corner | YA-6 |
| Statistical corners made from a Monte Carlo run's worst trials, then tuned and optimized against | YA-6, YA-7 |
| Measurement histogram with spec limits; yield sensitivity histogram (yield % per bin of a parameter) | YA-8 |
| Every trial's swept response overlaid with spec lines; scatter (parameter vs measurement, measurement vs measurement) | YA-8, YA-9 |
| Quantile (normal-probability) and CDF plots; statistics table (mean, σ, min, max, median, skew, kurtosis, Cpk, σ-to-target) | YA-4, YA-8 |
| Contribution ranking: which variables drive each measurement | YA-4, YA-9 |
| Worst-samples list | YA-4, YA-10 |
| Yield vs iteration during centering; a fast surrogate model for centering | YA-11, YA-12 |
| Design of experiments: factorial / fractional / screening designs, main effects and interactions, effects Pareto, response model, confirmation run | YA-14 (optional) |
| **Not matched, deliberately:** high-sigma estimation (scaled-sigma, worst-case distance, learned fast Monte Carlo), the other simulator dialect's `statistics {}` blocks | — |

**Where circuitRF goes further** (and the reason to hold the UX bar): linked trial selection across every plot,
**Send trial to Tuning**, z-space statistical corners that move with the nominal, one-click standard yield display,
and the whole system available headlessly to an agent with the same results as the GUI.

---

## 1. The phases

| Phase | Brief | What it delivers | Depends on |
|---|---|---|---|
| YA-1 | `brief-yield-1-format-and-model.md` | Tolerance keys on `tune` entries, `correlate`, `statistics`, `corner`, goal `use=`; `.csch` block; `check`/`explain`/`reference statistics`; the design note | — |
| YA-2 | `brief-yield-2-sampling-core.md` | Distributions, truncation, correlation (copula + repair), random/LHS/Sobol, counter-based streams, public batch evaluation | YA-1 |
| YA-3 | `brief-yield-3-kit-statistics.md` | Distribution functions live in the expression engine; process/mismatch scoping; kit statistical sections; nominal byte-identity | YA-2 |
| YA-4 | `brief-yield-4-run-service.md` | The Monte Carlo / yield run service, the result DataSet, yield + CI, auto-stop, non-converged policy, save policy, re-run trial, statistics functions, contributions | YA-2 (YA-3 for kit draws) |
| YA-5 | `brief-yield-5-cli-and-mcp.md` | `yield` verb, MCP `run`, `reference statistics`, progress, exit codes — **agent-ready here** | YA-4 |
| YA-6 | `brief-yield-6-corners.md` | Corner analysis, generator, Monte Carlo per corner, worst-corner report, statistical corners | YA-4, YA-5 (YA-3 for kit corners) |
| YA-7 | `brief-yield-7-tune-and-optimize-across-corners.md` | The optimizer meets goals at every enabled corner; Tuning evaluates at a chosen corner | YA-6 |
| YA-8 | `brief-yield-8-display-statistics.md` | Bars draw style; histogram, CDF, quantile, yield sensitivity; spec lines; statistics table; `plot`/`render` parity | YA-4 |
| YA-9 | `brief-yield-9-display-trials.md` | Pass/fail family colouring, nominal highlight, envelope bands, coloured scatter, contribution Pareto, linked trial selection | YA-8 |
| YA-10 | `brief-yield-10-yield-panel.md` | The Yield dock panel: Monte Carlo / Yield / Corners modes, Tolerance column, live yield, trial table, Send trial to Tuning, one-click yield display | YA-5, YA-6, YA-9 |
| YA-11 | `brief-yield-11-design-centering-core.md` | Design centering: common random numbers, smoothed yield, registry algorithms, verification, headless | YA-4, YA-5 |
| YA-12 | `brief-yield-12-centering-panel-and-surrogate.md` | Centering mode in the panel; the quadratic surrogate | YA-10, YA-11 |
| YA-13 | `brief-yield-13-docs-and-example.md` | User pages, a `Yield` example workspace, the design note's final state | all |
| YA-14 | `brief-yield-14-doe-optional.md` | **Optional.** Design of experiments: screening and response-surface designs, effects with aliasing, effects Pareto, main-effect and interaction plots, model optimum + confirmation, `yield doe`, a DOE panel mode | YA-12 (and everything it depends on) |

YA-3 can run beside YA-4 (YA-4's gates use designer tolerances only). YA-8 can start as soon as YA-4 lands.
YA-6/YA-7 and YA-8/YA-9 are independent of each other. YA-14 is built only when the owner asks for it; if it
lands after YA-13, it brings its own user-page section and example bench.

---

## 2. Locked decisions

### D1 — A tolerance is part of the variable entry
The tuning series' **one entry per tunable** (`TunableEntry`, the `tune` line) grows a statistical part: a `stat`
flag, a distribution and its spread. Tune, Opt and Stat are three flags on one row with one key (tuning D3), so
design centering — optimizing the nominal of a variable that also carries a tolerance — needs no second list.
Everything that is tunable can carry a tolerance; nothing else can (tuning D1 holds: an expression-valued parameter
is not offered, the VAR it reads is).

### D2 — Distributions
| `dist=` | Spread keys | Meaning |
|---|---|---|
| `gauss` | `sd=<v>` or `tol=<v> sigmas=<k>` | normal; `sd` is 1σ; `tol` at k σ (so `tol=5% sigmas=3` is σ = 5/3 %) |
| `unif` | `tol=<v>` or `lo=<v> hi=<v>` | uniform on nominal ± tol, or on [lo, hi] |
| `lognorm` | `sd=<v>` or `tol=<v> sigmas=<k>` | the value's log is normal; `sd` is the value's relative 1σ; positive values only |
| `discrete` | `lo=<v> hi=<v> by=<v>` | equally likely values lo, lo+by, … ≤ hi |

- `<v>` is a **percent of the nominal** (`2%`) or an **absolute value in the parameter's unit** (`0.1 pF`). A percent
  follows the nominal when centering moves it; an absolute spread does not. The writer keeps the form written.
- `trunc=<k>` (gauss, lognorm) **truncates** at ±k σ by sampling the truncated distribution — never by clipping,
  which would pile probability onto the edges.
- `stat` defaults to **1 when `dist=` is present**; `stat=0` keeps the distribution but switches it off. An entry
  with no `dist` is not statistical.
- An **integer** entry takes only `discrete` or `unif` (rounded); `gauss`/`lognorm` on one is a refusal.
- A **complex value's parts** (tuning D18): a tolerance on one part, or on a **same-system pair** (real + imag,
  mag + phase), each drawn independently. A **mixed pair** carrying tolerances (real + mag) is a refusal — a draw of
  two non-orthogonal parts can describe no complex number, and resampling it would bias both.
- A distribution that can draw a **non-physical** value with probability above 1e-9 (a Gaussian R wider than ~16 %
  untruncated, a negative capacitance) is a `check` **warning** that names `trunc=` and `lognorm`; a draw that is
  non-physical at run time is a trial that did not evaluate (D7).

### D3 — Correlation
`correlate <key> <key> rho=<v>`, −1 < ρ < 1, between any two stat entries of any distributions, through a
**Gaussian copula** (correlated standard normals, mapped through each marginal's inverse CDF). A matrix that is not
positive definite is **repaired to the nearest one** (Higham) and the repair is reported with the largest change;
it is never silently used as written.

### D4 — Specs are goals
A yield spec **is a goal** (tuning D10) — same grammar, same `GoalResiduals` violation rule, and *pass* means the
goal is **met** at that trial (its existing "met within 1e-9 of scale" rule). A goal gains `use=opt|yield|both`
(default `both`). The intended workflow: centre the design with the optimizer against tight goals, then loosen them
(or mark tighter copies `use=opt`) for yield. Goals are still **authored only in the Optimizer window** (tuning
D10); the Yield panel lists them, toggles `use`, and opens the Optimizer to edit one.

### D5 — Sampling and reproducibility
- `sampling=random` (default), `lhs` (Latin hypercube; needs the trial count up front, so it is refused with
  auto-stop), `sobol` (scrambled; extensible, so it works with auto-stop).
- **Counter-based streams.** Each statistical draw is a pure function of (seed, trial, stream), where the stream is
  the variable's key or, for a kit mismatch draw, the instance path and parameter. So **trial N is reproducible
  alone**, in any order, on any thread, on any platform, and adding an unrelated variable does not change the
  others' draws under `random`. The default seed is **1**: a run is reproducible unless the user changes it.
- The stored form of a trial is its **standard-normal vector z** (after correlation). A value is
  `nominal ⊕ spread(z)`, so the same z re-applied to a moved nominal is the same relative deviation. That is what
  makes common random numbers (YA-11) and z-space statistical corners (D10) possible.

### D6 — Kit statistics are live distribution functions
Today the importer reduces `agauss`/`gauss`/`aunif`/`unif`/`limit` to their nominal and reports it
(`SpiceExpression.ReplaceStatistical`), discarding the spread. YA-3 keeps them as **functions of circuitRF's
expression engine**, with the dialect's argument meaning, that evaluate to their **nominal outside a Monte Carlo
trial** — every nominal result stays byte-identical — and to a seeded draw inside one.
- A draw in a **global** (testbench-scope) expression is **process**: one draw per trial, shared by every instance.
- A draw in a **cell-scope** expression (a subcircuit parameter) is **mismatch**: one draw per instance per trial.
- The user turns kit statistics on by selecting **the kit's own statistical section** in the existing corner picker —
  which is how a kit intends it to be used; `statistics process=0|1 mismatch=0|1 sigmascale=<k>` control them.
- The same functions are legal in a VAR expression, so a design can state a distribution directly.

### D7 — Trials that do not evaluate
A trial whose analysis fails (no convergence, a refused non-physical value, an engine error) is a **did-not-evaluate**
trial, with its reason. `nonconverged=fail` (default): it counts **as a fail** and is reported separately ("12 of 500
did not evaluate — counted as fails"). `nonconverged=warn`: it is **excluded** from the yield's denominator and
reported as a warning. Either way the reason and trial numbers are in the result.

### D8 — Yield and its confidence
Yield = passes ÷ counted trials, reported with a **Clopper–Pearson** interval at `confidence=` (default 95 %),
overall and per spec. With `lhs`/`sobol` the binomial interval is conservative and says so once in the design note,
not on screen. **Auto-stop** (`autostop=1` with `target=`): after each batch, stop when the interval's lower bound
is at or above the target (pass) or its upper bound is below it (fail); never before 50 counted trials;
`trials=` is the cap.

### D9 — Results
One `DataSet` per run, written beside the design as **`<schematic>.yield.npy`** (never `run.npy`, which Simulate
owns), with an outer **`trial`** axis (1…N) on every analysis cube so the existing family machinery draws every
trial; per-trial cubes for the sample values (`stat:<key>`), each goal's pass and margin, the overall pass and the
evaluation status; the **nominal** in its own group so plots can draw it highlighted; corners as an outer **`corner`**
axis with labels (`DataCube.Axis.Labels`). YA-4 fixes the names against `docs/design/results-dataset-layout.md`.

### D10 — Corners
- A **corner** is a named set of bindings: kit corner-axis selections, `temp` (the elaborator's ambient global,
  °C), global VAR values and tunable values. Stored as `corner` lines; a `.csch`'s corner holds kit axis selections
  and is **resolved at extraction** exactly as `CornerSelections` is today, so a `.cnl` corner binds values and never
  names a kit file.
- The cross-product of axes × temperatures × values is a **generator** that writes explicit corner lines (panel and
  CLI) — not a second grammar.
- A **statistical corner** is `corner <Name> trial=<n>` with the run's seed and sampling: it replays that trial's
  **z-vector** against the current nominal (D5), so it moves with tuning and centering. A trial whose streams no
  longer exist (a renamed instance) replays with a warning naming them.
- The optimizer can meet its goals **at every enabled corner** (YA-7).

### D11 — Architecture
- **Pure numerics** (distributions, inverse CDFs, copula, nearest-PD, LHS, Sobol, Clopper–Pearson, regression):
  `src/Engine/Statistics/`, no domain types.
- **Orchestration** (entries → draws → overrides, goals → pass/margin, runs, corners, centering):
  `src/Design/Statistics/`, beside `src/Design/Optimization/`, referenced by `src/Ui`, `src/Cli` and the MCP
  server — **one implementation**; the GUI and the CLI cannot disagree.
- **Evaluation** goes through the tuning series' service: `PreparedCircuit` and `OptimizationRun`'s batch evaluator
  made public over values (YA-2) — **not a second evaluator**. A circuit that is not re-entrant
  (`NotReentrantReason`) runs serially with the existing note.
- `src/Ui` holds only the Yield panel, its view models and the Data Display wiring (`src/Ui/Yield/`).

### D12 — Headless writes nothing to the design's values
The `yield` verb and MCP `run` **report**. The opt-in writes are `--save-preset <name>` (a centred design or a trial's
values, `.csch` only, after a history checkpoint, exactly as `opt`), and `--save-corner <name> --trial <n>` (adds one
`corner` line). An agent that wants anything else in the design writes the file — the format is the contract.

### D13 — Exit codes
`yield`: **0** finished, and every enabled yield goal met its target (or there was no target) · **3** finished,
yield below `target=` (or, for corners, a goal failed at a corner) · **1** refusal · **2** no trial evaluated ·
**130** cancelled (writes nothing). The same table as `opt` (tuning D15), so a script reads them alike.

### D14 — Pause, stop, live
Pause finishes the trials in flight and holds; Resume continues as if never paused (draws are counter-based, so
nothing to restore). Stop keeps every finished trial and reports yield over them. While running, the Data Display
redraws from the in-memory source (tuning D8) coalesced to the newest state, with a **Yield** chip.

---

## 3. UX principles for the Yield panel

The tuning series' §3 applies unchanged: follows the focused schematic; header names it; a toolbar of small icon
buttons in the Analyses panel's style with a tooltip on each; **no explanatory prose under readouts** (a value or a
short status; a note only where there is no value); every design change is one undo step; keyboard as Tuning.
**Dock default:** tabbed **behind Optimizer** (`DockPanelIds`/`DockLayoutSchema.Yield = "Yield"` — the string is a
file format). A layout saved before YA-10 gains the panel by `DockLayoutDefaults.WithMissingPanelsFilled`.

---

## 4. Testing rules for the whole series

- The **minimal** tests: one per claim, not per rung; trim `InlineData`.
- Run **only the classes the phase adds or touches** (`--filter "FullyQualifiedName~<Class>"`); never the full suite,
  never all of `Ui.Tests`. Rebuild each test project before `--no-build`.
- **No new timing benchmark tests.** A performance claim asserts a **counter** (trials evaluated, cache hits,
  redraws skipped).
- Statistical claims are tested **deterministically**: a fixed seed and an exact expected sample, or a tolerance
  derived from the sampling distribution and stated in the test (e.g. a mean within 4 standard errors). Never a
  "usually passes" test.
- Cheap closed-form circuits only: a resistive divider (yield computable exactly from the normal CDF), an RC
  low-pass, the L-section and bandpass benches of `examples/Optimization`. No EM, no long HB.

---

## 5. Housekeeping for every phase

- Findings go in the relevant `RESOLVED.md`, never in a `CLAUDE.md`. Paraphrase the owner; never quote.
- **No commercial vendor or product names** anywhere — code, docs, tests, commit text, and not as a glossary of
  names to avoid. Describe a capability generically ("commercial RF suites offer…").
- No commits unless the owner asks; commits go to `main`.
- Edit `docs/user/src` sources where a phase changes user-visible behaviour; **do not run DocGen** — the owner
  regenerates at the end of the series.
- All algorithms written in-house under MIT; no GPL or copyleft code, none copied.
