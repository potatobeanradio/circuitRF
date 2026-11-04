# Monte Carlo, yield, corners and design centering

**Series:** `docs/sonnet-briefs/brief-yield-0-overview.md` (decisions D1–D14) · **Builds on:**
`docs/design/tuning-optimization.md` (its §19 lists the seams this series takes) and the PDK corner work
(`src/Core/Pdk/PdkCorners.cs`, `src/Ui/Schematic/WorkspaceCorners.cs`, `docs/design/spice-models.md` §8.11).

This note records the model and grammar as built, then one section per phase as it lands.

---

## 1. YA-1 — the model

There is **one** setup block per testbench, the tuning setup (`TuningSetup`, `src/Core/Design`), and the
statistical content lives in it (D1): a tolerance is part of the variable entry, a yield spec is a goal.

| Type | What it holds |
|---|---|
| `TunableEntry` | gains `Stat` (bool), `Distribution` (`None\|Gauss\|Unif\|LogNorm\|Discrete`) and `Spread` (`StatSpread`: `Sd`, `Tol`, `Sigmas`, `Lo`, `Hi`, `By`, `Trunc`, each the TEXT written). `IsStatistical` = `Stat` and a distribution. |
| `OptimizationGoal` | gains `Use` (`Both\|Opt\|Yield`, default `Both`); `ForOptimizer` / `ForYield`. |
| `StatisticsSettings` | the `statistics` line: trials, seed, sampling, target, confidence, autostop, nonconverged, save, process, mismatch, sigmascale, parallel, analyses, corners. Every default is held as null (or the enum's first member), so neither serialization ever writes one. |
| `StatCorrelation` | two keys and ρ. |
| `CornerDefinition` | name, enabled, kit `AxisSelections` (`.csch` only), `Temp`, `Values` (global names and tunable keys → value text), and a statistical origin (`Trial`, `Seed`, `Sampling`, `Trials`). |
| `TuningSetup` | gains `Correlations`, `Statistics`, `Corners`. |

**Why the spread is text.** The form a value was written in is part of what it means (D2): a percent
follows the nominal when centering moves it, an absolute value does not. Keeping the text keeps the form
and makes the writer's output byte-stable for free, the same reason a tuning bound is text.

**Defaults** (where the overview named none, this phase chose): `trials` **100**, `seed` 1, `sampling`
random, `confidence` 95 %, `nonconverged` fail, `save` auto, `process`/`mismatch` on, `sigmascale` 1,
`analyses` goals, `corners` none.

### 1.1 The spread in numbers (`ResolvedSpread`, `src/Design/Statistics`)

A spread is resolved against the tunable's nominal into base SI:

- **A width** (`sd`, `tol`, `by`) written as a percent is that percent of |nominal|; otherwise a value
  with its unit, a bare number being in the parameter's own unit.
- **An end** (`lo`, `hi`) written as a percent is that percent **of** the nominal — `lo=90% hi=110%` is
  0.9 to 1.1 times it, not an offset.
- `gauss`/`lognorm`: σ = `sd`, or `tol`/`sigmas`. For `lognorm`, σ/nominal is the log's σ.
- `unif`: [nominal − tol, nominal + tol] or [lo, hi]. `discrete`: lo, lo+by, … ≤ hi.

**The non-physical probability** (D2's warning) is P(draw ≤ 0) for a value that must be positive — a
resistance, capacitance, inductance, conductance or length (by `Units.BaseUnit`) or a magnitude part, with a
positive nominal. A truncated normal is renormalized over its ±k σ window: P = (Φ(−μ/σ) − Φ(−k)) / (1 − 2Φ(−k))
when μ/σ < k, else 0. Above `1e-9` it is a `check` warning naming `trunc=` and `lognorm`; that threshold puts
an untruncated Gaussian resistance's limit at about 16.7 % (6 σ).

## 2. YA-1 — the `.cnl` grammar

`TuningDirectiveText` (`src/Core/Netlist`) reads and writes the four tuning directives and three more; the
schema is `AnalysisDirectiveSchema.TuningDirectives`, which is also what `reference` prints. The spelling
rules are the tuning grammar's: a unit after a space, `%` glued to its number, booleans `1/0/true/false/
yes/no`, an unknown key a warning that is kept, a default never written.

```
tune R1.R   min=10 Ohm max=200 Ohm opt=1 dist=gauss sd=2%
tune C1.C   dist=unif tol=0.1 pF
tune L1.L   dist=gauss tol=5% sigmas=3 trunc=3
tune mag(ZL) dist=unif lo=45 Ohm hi=55 Ohm stat=0
tune X1.Nf  dist=discrete lo=4 hi=8 by=2
correlate R1.R R2.R rho=0.9
statistics trials=500 seed=7 sampling=lhs target=95%
goal S21 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge 14 use=yield
corner SS_hot temp=85 Vdd=3.0 V R1.R=47 Ohm
corner Worst_S21 trial=417 seed=7 sampling=lhs trials=500
```

- **`tune`** — the statistical keys come after the tuning ones in the order `dist`, `sd`, `tol`, `sigmas`,
  `lo`, `hi`, `by`, `trunc`, then `stat=0` only when it says something `dist` does not imply. `stat`
  defaults to 1 when `dist` is present wherever on the line `stat=` sits.
- **`correlate <key> <key> rho=<v>`** — `rho` required.
- **`statistics`** — at most one line (`cnl.statistics.repeated`). `target`/`confidence` are percents;
  `save` is `auto|scalars|all|<n>`; `corners` is `none|all|<name>,<name>`.
- **`corner <Name>`** — a one-word name. `enabled`, `trial`, `seed`, `sampling`, `trials` and `temp` are
  the line's own words **wherever they sit**; every other key is a binding and must be a variable's name or
  a tunable key, bound once. A global variable named like one of those words cannot be bound by a corner
  (`temp` is that global already). The writer writes the words first, then the bindings.
- **`goal … use=opt|yield`** — written after `enabled=`, absent at `both`.

A malformed line is refused with its line number: `cnl.statistics.malformed`, `cnl.statistics.value-invalid`
(also for a statistical key on a `tune` line), `cnl.corner.malformed`, `cnl.corner.value-invalid`.

**Byte stability.** `CnlWriter → CnlReader → CnlWriter` reproduces every line, and so does
`.csch → .cnl → .csch → .cnl` (`StatisticsSetupRoundTripTests`).

## 3. YA-1 — `.csch` and extraction

`CschTuning` carries `Correlations`, `Statistics` and `Corners` beside the tuning parts; the entry's and
goal's new fields are omitted at their defaults. A schematic with no statistical content writes the bytes it
always did. **No `format_version` bump**: the file's own rule (`project-file-formats.md`) is that an
optional, null-by-default field is additive.

**Kit corners (D10).** A `.csch` corner may select kit corner sections, keyed as the design's own
`CornerSelections` are. `NetExtractor.Extract` takes a `cornerBinder` — the GUI passes
`WorkspaceCorners.BindingsFor` over the workspace's axes, the function Simulate already binds the design's
selections with — and for each corner binds the corner's selections **overlaid on the design's own**. Every
bound constant goes into the corner's `Values` (a value the corner states itself wins), and the selections
are dropped, so the `.cnl` corner line binds values and never names a kit file. A selection that does not
resolve is reported as `Corner '<name>': ` followed by that function's own sentence — only what the corner's
selections add, since the design's own are reported where they are applied. With no binder (a schematic belonging
to no workspace, or one whose workspace offers no kit corner axes) a corner's kit selections are reported as not
applied. The CLI binds them too since YA-6 (§10.1).

## 4. YA-1 — `check` (`StatisticsValidator`)

`TuningValidator.Validate` calls `StatisticsValidator.Validate`, so `check`, the Tuning and Optimizer windows
and every later run read one rule set (a setup `check` passes is not refused later for a reason `check` could
have seen).

| Code | Severity | When |
|---|---|---|
| `yield.dist.not-tunable` | error | a distribution on a key the catalog does not offer |
| `tuning.part.not-statistical` | error | a distribution on a vendor part's `Instance.Part` entry — a part is chosen, never drawn: its static model states no tolerance (brief vendorlib-4 R-vl4-3d). A statistical run's catalog holds no part, so a `Part` entry is skipped there, and the part stays as placed |
| `yield.spread.missing` / `.extra` | error | the spread keys are not exactly the distribution's (`gauss` with `lo`; `tol` without `sigmas`) |
| `yield.spread.not-a-number` | error | a spread value that resolves to no number |
| `yield.spread.wrong-unit` | error | a spread value in a unit of another quantity than the parameter's (`sd=3 pF` on a resistance) — it would convert to a meaningless number; a bare SI prefix, a percent or a unitless parameter is never refused |
| `yield.spread.not-positive` | error | `sd`, `tol`, `sigmas`, `trunc` or `by` ≤ 0 |
| `yield.spread.inverted` | error | `lo` ≥ `hi` |
| `yield.spread.step` | error | `by` does not step from `lo` to `hi` a whole number of times (to 1e-6) |
| `yield.dist.lognorm-nonpositive` | error | `lognorm` on a value ≤ 0 |
| `yield.dist.integer` | error | `gauss`/`lognorm` on a whole-number value |
| `yield.complex.mixed` / `.too-many` | error | tolerances on a mixed pair of a complex value's parts, or on more than two |
| `yield.correlate.not-stat` / `.same-key` / `.rho` | error | a non-statistical key; a key with itself; ρ outside (−1, 1) |
| `yield.statistics.lhs-autostop` | error | `sampling=lhs` with `autostop=1` |
| `yield.statistics.autostop-target` | error | `autostop=1` with no `target` |
| `yield.corner.unknown-key` | error | a binding that is neither a global variable nor a key the catalog offers |
| `yield.corner.unknown-part` / `.unknown-part-corner` | error | a `part:` selection naming no part the design places, or a corner that part does not offer |
| `yield.corner.temp` | error | `temp` not a number |
| `yield.corner.trial-incomplete` | error | `trial=` without `seed`, `sampling` and `trials` |
| `yield.dist.nonphysical` | warning | §1.1 |
| `yield.dist.off` | warning | a distribution with `stat=0` |
| `yield.goal.nothing-varies` | warning | an enabled `use=yield` goal, no statistical entry and no distribution call (§7) |
| `yield.correlate.repair` | warning | the correlations are not positive definite; reports the largest change |

**The correlation matrix** (`StatisticsValidator.CorrelationOf`) is over the keys the `correlate` lines name,
in setup order; a non-positive-definite one is repaired by `NearestCorrelation.Repair`
(`src/Engine/Statistics`, Higham's alternating projections with Dykstra's correction, eigenvalues floored at
1e-8 so the result factors). YA-2 samples with the same matrix.

**The optimizer and `use=`.** `OptimizationRun` aims only for enabled goals with `ForOptimizer` — a
`use=yield` goal is a yield spec only (D4). The goal editor carries `Use` through an edit.

## 5. YA-1 — `explain` and `reference`

- `explain --tunables` adds a line per toleranced entry (`gauss σ = 1 Ohm (2 %)`, `unif 1.9 pF .. 2.1 pF
  (±5 %)`, `, truncated at ±3σ`, ` — off (stat=0)`) and, in JSON, a `stat` object: the spread in base SI
  (`sigma`, `lo`, `hi`, `step`, `trunc`, with the base `unit`) and `written`, the keys as written.
- `explain --analysis` adds a `statistics` object whenever the setup has statistical content: the effective
  settings, the statistical entries, every goal with its `use`, the corners, the correlation matrix a run
  would use (and whether it was repaired), and **what the trial count can resolve**: the expected
  half-width of the Clopper–Pearson yield interval at a yield of 90 % and the fewest trials that bring it
  under ±2 % (500 trials: ±2.73 %; 912 trials reach ±2 %). `ClopperPearson` (`src/Engine/Statistics`)
  takes the Beta quantiles through a continued-fraction incomplete beta inverted by bisection;
  `StatisticsExplainTests` checks it against the interval found from binomial tail sums directly.
- `reference statistics` is generated from the schema: the tune line's statistical keys, the distribution
  table (`AnalysisDirectiveSchema.Distributions`), the percent-vs-absolute rule, truncation, the three
  directives with every key and default, statistical and kit corners, goal `use=`, pass/fail (D4) and how
  yield and its interval are reported (D7, D8), with a worked example that `check`s clean. `reference goals`
  lists `use=`.

**On the interval with `lhs` and `sobol`.** The Clopper–Pearson interval assumes independent trials. Latin
hypercube and low-discrepancy samples are not independent and typically estimate a yield more tightly than
random sampling does, so the binomial interval is conservative for them. That is said here, once, and not
on screen (D8).

## 6. YA-2 — sampling and batch evaluation

A trial is produced in three steps, each a pure function, and evaluated through the optimizer's own
evaluator.

**1. Streams and the plan** (`src/Engine/Statistics`, pure numerics). Each statistical entry is one
*stream*, identified by `StatStreams.Id` — FNV-1a over the key text's UTF-8, then `SplitMix64.Mix` (the
optimizer's generator's output mix, made public for this). A draw is `Hash(seed, trial, stream, k)`: each
input folded in through its own mix, the top 53 bits offset by half a step so the uniform lies in the open
interval (0, 1) and Φ⁻¹ of it is finite. No generator state exists, so trial N drawn alone equals trial N
inside a parallel batch, and a new entry moves no other entry's `random` draws (D5). `SamplingPlan` turns
trial t into one **independent** standard normal per stream:

| `sampling=` | Trial t (1-based), stream s |
|---|---|
| `random` | Φ⁻¹(U(seed, t, s)) |
| `lhs` | Φ⁻¹((π_s(t−1) + v)/N): π_s a Fisher–Yates permutation of 0…N−1 whose draws sit at trial −N (no real trial), v = U(seed, t, s). One draw per stratum per stream. |
| `sobol` | Φ⁻¹ of point t−1 of a scrambled Sobol sequence, Gray-code order, computed directly from the index. Streams take dimensions in ascending order of their ids, so the mapping does not depend on discovery order. |

**Sobol.** Direction numbers are S. Joe and F. Y. Kuo's `new-joe-kuo-6.21201`, its first 1111 dimensions
(the range over which the set satisfies Property A), embedded unmodified as
`src/Engine/Statistics/SobolDirections.txt` with its BSD-style licence (`licenses/Joe-Kuo-Sobol.txt`,
`THIRD-PARTY-NOTICES.md` §4). Dimension 0 is van der Corput. 32-bit direction numbers: 2³² points. The
scramble is a **random linear matrix scramble** (per dimension a lower-triangular binary matrix with a unit
diagonal, each output digit mixing in the digits above it) followed by a **random digital shift**, both drawn
from the stream hash with the run seed and the dimension; both preserve the net structure. A stream past
dimension 1111 is drawn at `random` and the sampler reports `yield.sampling.sobol-dimensions`. The unscrambled
first ten points in three dimensions match those the table's authors publish for their reference generator.

**2. Correlation** (`GaussianCopula`). The plan's normals of the streams named by `correlate` lines pass through
z ← L·z, L the Cholesky factor of the matrix `StatisticsValidator.CorrelationOf` builds (§4) — restricted to the
keys that are statistical, a principal submatrix of a valid matrix being valid. Uncorrelated streams skip it.
A matrix that is not positive definite is repaired (Higham, §4) and the sampler's notes carry
`yield.correlate.repair` with the largest change. The result is a `StatisticalSample`: the trial number and
its correlated z per stream key — the stored form of a trial (D5).

**3. Values** (`SampleValues.Apply`, `src/Design/Statistics`). Each entry's spread is resolved
(`ResolvedSpread`, §1.1) against the nominal it is **given** — `SampleValues.Nominals(catalog, moved)` yields
the catalog's tunables at a moved design (a real value by its key, a complex value whole) — and its marginal
(`ResolvedSpread.Marginal`) evaluated at z:

| `dist=` | Value at z | Mean, variance |
|---|---|---|
| `gauss` | μ + σ·q(z) | μ; σ²·(1 − 2kφ(k)/(1 − 2Φ(−k))) when truncated at k |
| `lognorm` | m·exp(s·q(z)), s = σ/m: the **median** is the nominal | m·M(s); m²·(M(2s) − M(s)²), M(t) = E[e^{tY}] of the (truncated) standard normal |
| `unif` | lo + (hi − lo)·Φ(z) | (lo + hi)/2; (hi − lo)²/12 |
| `discrete` | rung ⌊Φ(z)·n⌋ of lo, lo + by, … | lo + by(n − 1)/2; by²(n² − 1)/12 |

q(z) is z, or with `trunc=k` the truncated normal sampled **exactly**: Φ⁻¹(Φ(−k) + Φ(z)·(1 − 2Φ(−k))),
computed from the lower tail by symmetry. Φ⁻¹ is Acklam's approximation plus one Halley step against
`SpecialFunctions.NormalCdf` (§5), the upper half by Φ⁻¹(p) = −Φ⁻¹(1 − p); `DistributionTests` holds it to
1e-12 against tabulated quantiles.

- **`sigmascale`** multiplies σ (`gauss`, `lognorm`) and a uniform's half-width about its centre. A
  `discrete` list is not scaled — its values are the legal ones.
- **Whole numbers** are rounded half away from zero.
- **A complex value's parts** are drawn in their own units (a phase in degrees) and composed with the
  nominal whole by `ComplexValue.Compose`, written back by `ComplexValue.Format` in the form the schematic
  wrote it (`polar(52,29) Ohm`).
- **A non-physical draw** — ≤ 0 for a value that must be positive (§1.1's rule) — refuses the **trial**
  (`yield.trial.nonphysical`); it is never clamped. `SampledValues.Draws` still holds the draw. So do a key
  that names nothing, a missing draw, a spread that does not resolve at the given nominal (a log-normal of a
  nominal ≤ 0) and a pair of parts no complex value has (`yield.trial.*`).

**Evaluation** (`OptimizationRun.EvaluateValues`). `OptimizationRun.ForEvaluation(circuit, options, goals)`
validates the setup as a run does and scores its enabled goals for `GoalUse.Yield` (the default), `Opt` or
`Both`; it needs no optimized variable and no algorithm. Its analyses are the goals', or every runnable one
under `statistics analyses=all` or with no goal (a Monte Carlo of spreads alone). `EvaluateValues` takes value
maps (key → value text) and returns per map a `PointEvaluation`: status (`Evaluated` / `DidNotEvaluate` with
its reason), every goal's `GoalScore` (worst violation, met, margin), `Pass`, the cost, and the `DataSet`
unless `keepData: false` drops it after scoring. It is **the** evaluator: the optimizer's unit-box batches are
now decoded into value maps and handed to the same private method, so the cache (keyed by
`OptimizationRun.CacheKeyOf` — the map's `key=value` lines in its own order, the form a decoded point's key
always had), the parallelism, the `NotReentrantReason` serial rule with its note, and the cancellation token
are shared. A cache hit returns its scores and no `DataSet` (the cache keeps scores, not results).
`BatchEvaluateTests` holds a map's results bit-identical to Simulate with the same values typed.

**Nothing nominal changes** (R-ya2-7): with no statistical entry nothing in this section runs during
Simulate, Tuning or Optimization; the optimizer's own classes pass unchanged.

## 7. YA-3 — kit statistics and distribution functions

**The functions** (`Evaluator.Statistical.cs`, `src/Core/Expressions`). `agauss(nom, dev[, k])`,
`gauss(nom, rel[, k])`, `aunif(nom, dev)`, `unif(nom, rel)` and `limit(nom, dev)` are circuitRF built-ins with
the SPICE dialect's meaning; `limit(x, lo, hi)` is the clamp (`expressions.md` §7 has the table). Outside a trial
each is its **first argument, evaluated alone** — the spread is never read — and the unit rules read the same
nominal view (`Evaluator.NominalView`), so a nominal result is the one the importer's old reduction gave, bit for
bit. Inside a trial the evaluator holds an `IStatisticalDraws` (`Evaluator.Statistics`, set through
`Elaborator.Statistics` — per elaboration, never global) and computes:

| Call | Value at the stream's draw (z, u = Φ(z), s = `sigmascale`) |
|---|---|
| `agauss(nom, dev, k)` | nom + (s·dev/k)·z |
| `gauss(nom, rel, k)` | nom·(1 + (s·rel/k)·z) |
| `aunif(nom, dev)` | nom + s·dev·(2u − 1) |
| `unif(nom, rel)` | nom·(1 + s·rel·(2u − 1)) |
| `limit(nom, dev)` | nom − s·dev if u < ½, else nom + s·dev |

k defaults to 1; **k = 0 is no spread** — a kit writes `(mm_ok != 1 ? 0 : 1)` as k to switch its mismatch off,
and that is the only reading that does not divide by zero; k < 0 is refused. A draw on a complex argument, a wrong
arity or a spread that does not evaluate throws, and is also recorded in `StatisticalProblems` (on the evaluator and
on `ElaboratedNetlist`) — some parameter paths fall back to verbatim text when evaluation throws, so YA-4 treats a
non-empty list as a trial that did not evaluate (D7; `yield.trial.draw-failed`).

**Process and mismatch** (D6) are the scope's. `Scope.InstancePath` is null for the testbench's global scope and the
instance's full path for a cell scope (inherited by a user function's call frame). The evaluator keeps a stack of
SITES: resolving a named binding pushes (owner's instance path, name); an instance parameter's override is evaluated
through `Evaluator.EvalParameter` with site `<instance>.<param>` (`Elaborator.EvalOverride`, every per-device
resolver). A call's kind is its site's scope — no path is **process**, a path is **mismatch** — and its stream is
`<path>.<name>`, or `<name>` alone in the global scope, with `#2`, `#3` … for later calls in one site:

| Written | Stream | Kind |
|---|---|---|
| global `Rnom = agauss(100, 10, 1)` | `Rnom` | process — memoised, so one draw per trial shared by every instance |
| cell `sub` default `R=agauss(Rnom,5,1)`, instance `X1` | `X1.R` | mismatch |
| inside `X1`, `R1 a b R='agauss(…)'` | `X1.R1.R` | mismatch |
| top-level `R1 in 0 R=agauss(…)` | `R1.R` | process (testbench scope; one instance, so one draw either way) |

`ElaboratedNetlist.StatisticalCalls` lists every call reached, once per stream, nominal or not.

**The draws** (`ExpressionDraws`, `src/Design/Statistics`). A stream's z is `StatStreams.Normal(seed, trial, Id(stream))`
at **slot k = 1** — slot 0 belongs to the setup's statistical entries, so a stream that happens to be spelled like an
entry's key never shares its draw. `process=0` / `mismatch=0` make that kind evaluate at its nominal; `sigmascale`
is passed with every draw. Streams are discovered during elaboration, after any plan exists, so they draw at
`random`; a caller that elaborates nominally first can plan them (`StatisticalCalls` names them) and pass their z in
`planned`, which win — that is how YA-4 reaches `lhs`/`sobol` for them. `Drawn` records each stream's kind and z, the
per-stream record YA-4's result keeps.

**The SPICE reader** (`SpiceDistributions`, `spice-models.md` §8.4 has the per-path table). The extraction's read
and a corner section's read keep the call LIVE, in circuitRF's spelling; the import gestures, which write files a
user keeps, reduce it to the NOMINAL; a placed part's parameter rows are seeded BLANK where the default holds a
distribution, so the file's own default — and its per-instance draw — stands. A `.if` condition and a controlled
source's equation are always nominal. `SpiceExpression.ReduceDistributions` is the nominal form of any text.

**Kit sections** (`spice-models.md` §8.11). A kit's statistical section binds process globals whose values are
distributions — they reach the run as corner bindings. Its mismatch section includes a VARIANT model library whose
subcircuits carry the per-instance draws; `PdkCorners.SectionFor` returns the section's definitions with its
bindings, `WorkspaceCorners.Bind` returns both for the design's selections, and `KitCornerVariants.Apply` (called as
the extraction loads a part's SPICE netlist) puts the variant's subcircuits in place of the part library's — only
when the section does not include the part library itself (by content), so a nominal section moves no byte. A
SPICE model placed straight on a schematic runs the file it names; its distributions are live, but no section
substitutes its definitions.

**Not tunable** (R-ya3-4): a value holding a distribution is an expression, and `TunableCatalog.WhyNotOffered`
says so in its own words ("its value '…' is a distribution, which varies in a Monte Carlo trial and is not tuned —
write its nominal as a plain value with a tolerance (dist=) to tune and vary it").

**Discovery and refusal** (`KitStatistics`, R-ya3-5). `KitStatistics.Report(calls, selected sections, axes)` gives
the process and mismatch counts, the selected sections that brought statistics (`r_stat (rCorners.lib)`), and an
Info note per kit axis whose statistical section is not selected (`yield.kit.section-not-selected`, naming the
axis and the section). Which sections are statistical is a TEXT scan (`PdkCorners.StatisticalSections`): a
distribution call outside a comment, in the section's own lines or a file it includes or requests — reading every
section would read its model library each time. `KitStatistics.NothingVaries(setup, calls)` is the run's refusal
(`yield.run.nothing-varies`): an enabled yield goal, no tolerance and no distribution call. `check`'s
`yield.goal.nothing-varies` warning counts distribution calls too (the elaborated calls when `check` has them, the
testbench's own text otherwise). `explain --analysis` adds a `distributions` object — counts and every stream. The
CLI's yield report names no sections; YA-4's run report takes them from `WorkspaceCorners.Bind`.

**Not covered.** A distribution inside a FREQUENCY-DEPENDENT expression is evaluated at stamp time by the model's
own evaluator, which has no trial context, so it stays at its nominal. The other simulator dialect's
`statistics { process {…} mismatch {…} }` blocks are not read (`spice-models.md` §8.11 names the seam).

**Nominal identity** (R-ya3-6, `KitStatisticsNominalIdentityTests`): every example extracts a netlist with no
distribution in it; a kit-shaped design runs bit-identically live and reduced, and a nominal section moves no byte;
over a real kit's corner files (a git-ignored `testdata/kit-statistics`, skipped with a reason when absent) every
section read live is the nominal read modulo the distributions' spelling, and every global and subcircuit
expression evaluates to the same bits.

## 8. YA-4 — the run service

`StatisticalRun` (`src/Design/Statistics`) is the one Monte Carlo and yield implementation: the GUI, the CLI and MCP
call it, and it evaluates through the optimizer's evaluator (D11). *(brief-sar-10: a fields analysis in a trial recombines nothing until a
spec reads it, and a re-run trial writes its field as `<analysis> (trial n)` for the 3D view; `sar.md` §12.5.)*

| Piece | Where |
|---|---|
| The run, pause/resume/stop, re-run one trial, contributions, worst trials | `StatisticalRun.cs` |
| Options, progress, the trial record, the result | `StatisticalTypes.cs` |
| The result `DataSet` (D9) | `StatisticalDataSet.cs`; names in `results-dataset-layout.md` §"Monte Carlo and yield" |
| Contributions | `StatisticalContributions.cs` over `Regression` (`src/Engine/Statistics`) |
| Yield, interval, auto-stop | `YieldEstimate` over `ClopperPearson` (`src/Engine/Statistics`) |
| The statistics functions | `Evaluator.Reductions.cs`, `SampleStatistics` (`src/Core/Expressions`); `measurements.md` "Reductions over an axis" |

### 8.1 Creating a run

`StatisticalRun.Create(circuit, options)` builds `OptimizationRun.ForEvaluation` with `GoalUse.Yield` (mode `Yield`:
the `use=yield|both` goals) or `GoalUse.Both` (mode `MonteCarlo`: every enabled goal) — so the setup is validated by
the rule set `check` runs and a refusal is `check`'s sentence. It then refuses `yield.run.no-yield-goal` (a yield run
with no yield goal) and `yield.run.nothing-varies` (no statistical entry and no distribution call — in either mode).
`analyses=goals|all` is the evaluator's (§6). The design is elaborated once, nominally, to list its distribution
calls (`KitCalls`).

### 8.2 Trials

The nominal is evaluated first (tag `nominal`); a nominal that does not evaluate is the refusal
`yield.run.nominal-failed`. Trial t is then drawn and evaluated in three steps, each a pure function of t:
`StatisticalSampler.Sample(t)` → `SampleValues.Apply` (entries, at `sigmascale`) → `ExpressionDraws.For(settings, t,
planned)` (distribution calls). A trial whose sample is refused (`yield.trial.*`) did not evaluate and costs no
simulation.

- **Kit streams in the plan.** Under `random` a distribution call draws natively from its own stream (slot 1).
  Under `lhs`/`sobol` the nominal's calls join the plan as `kit:<stream>` — one plan across every stream, or two
  plans' Sobol dimensions would coincide — and their z is handed to `ExpressionDraws` as `planned`. A call a trial
  reaches that the nominal did not draws natively.
- **Draws reach every elaboration.** `CircuitEvaluationRequest.Statistics` puts the trial's draws on the evaluation's
  own bench copy (`TestBench.StatisticalDraws`, run-time only, never written), and `Elaborator.Elaborate` takes them
  from there when it has none of its own — so a parametric sweep's per-point elaboration and a parallel S-parameter
  run's per-worker elaboration draw the trial's values too, not the nominal. A draw that could not be made fails the
  evaluation with `yield.trial.draw-failed`.
- **The cache.** A `ValuePoint` carries a tag (`trial <n>`) appended to its cache key: two trials whose values happen
  to coincide (a `discrete` draw, or kit draws alone with an empty value map) are still two simulations, and a cached
  answer would carry no results. `EvaluateTrial(n)` tags `trial <n> (re-run)` and so always simulates afresh.
- **Batches.** `parallel=` trials at a time by default (`StatisticalOptions.BatchSize` overrides), through
  `OptimizationRun.EvaluateValues`, numbered in the order drawn and slotted by number, so the result does not depend
  on parallelism (`YieldDeterminismTests`). A circuit that is not re-entrant runs one trial at a time with the
  optimizer's note.
- **Did not evaluate (D7).** The trial's reason is kept; `nonconverged=fail` counts it in the denominator as a fail,
  `warn` leaves it out. Either way the result notes `yield.run.did-not-evaluate` with the count. No trial evaluating at
  all is outcome `NoneEvaluated` (exit 2).

### 8.3 Yield, interval, auto-stop (D8)

`YieldEstimate.Of(passes, counted, confidence)` — the Clopper–Pearson interval of §5 — overall and per goal. A trial
passes when it evaluated and every scored goal is met. With no goal (a Monte Carlo of the spread alone) there is no
yield: the estimate is NaN and the `pass` cube is not written.

**Auto-stop** (`autostop=1`, yield mode, a target) is decided after each batch **at every trial count the batch
reached, in trial order**: the run stops at the first count n ≥ 50 counted trials whose lower bound is at or above the
target (`Above`) or whose upper bound is below it (`Below`), and the trials of that batch past n are discarded. So the
count it stops at is a function of the draws alone — the same for any batch size (`AutoStopTests`: a design at 100 %
yield against 95 % stops at ⌈ln 0.025 / ln 0.95⌉ = 72). Outcome: `BelowTarget` (exit 3) when auto-stop said `Below`,
or, without an auto-stop verdict, when the yield is below the target; otherwise `Finished` (exit 0). A Monte Carlo run
has no target.

### 8.4 Save policy and writing (R-ya4-5, R-ya4-6)

The nominal's cubes give the size of one trial's analysis results (real 8 bytes, complex 16; scalar measurements and
`__` metadata excluded — the scalars are kept for every trial anyway). `save=scalars` keeps none, `all` every trial's,
`<n>` the first n, `auto` all when they fit in 256 MB and otherwise the largest leading count that does. The result
notes `yield.run.saved` with the sentence saying which. `ResultPathFor(source)` is `<design>.yield.npy` beside the
source; the run writes there when `StatisticalOptions.ResultPath` is set and it finishes or is stopped, never when
cancelled. `StatisticalOptions.Publish` receives the result so far at most once per `PublishInterval` (250 ms) after a
batch and once at the end — the in-memory Data Display source's feed (D14), which the panel (YA-10) wires.

### 8.5 Re-running one trial (R-ya4-7)

`EvaluateTrial(n)` draws trial n exactly as the run does and evaluates it with its results kept, whatever the save
policy — equal, value for value, to trial n inside a run (`YieldDeterminismTests`). Under `lhs` a trial past the plan's
count is `yield.trial.out-of-range`.

### 8.6 Contributions and worst trials (R-ya4-9, R-ya4-10)

`Contributions(name)` regresses a goal's value at its tightest point (or a scalar measurement) over the evaluated trials
on every stream's z — entries' correlated z, and the kit streams' (0 where a trial did not draw one) — standardized.
Ordinary least squares with at least 5 trials per stream; otherwise ridge with a penalty of 0.01 per trial on the
standardized normal equations, flagged `Underdetermined`. Each contributor reports its standardized coefficient, its
**share** of the explained variance by Pratt's measure β·r/R² (the shares sum to 1; with independent streams it is
β²/Σβ²), and its Spearman rank correlation with the scalar. A kit process stream is one contributor; **an instance's
mismatch streams are one contributor**, named by the instance (the stream's site less its parameter and `#n`), with
the root sum square of its coefficients, its streams' summed share and the rank correlation of its fitted linear
part. Never run unasked. `WorstTrials(goal, k = 10)` lists the evaluated trials with the smallest margin, with their
values.

**Owner decision D-a (2026-10-08, brief-yield-16): a share is displayed clamped to 0–100 %.** With correlated entries
β·r is not bounded — on the shipped Bandpass, `PassbandSpec` ranks `C1.C` at 122.8 % and `C3.C` at −43 %. β·r is the
honest number and stays unchanged in `--json` and in the result file (`yield.contrib:<goal>`). Every DISPLAY shows
`Contributor.ShownShare` — clamped to 0–1 — : the Pareto's bars (`yield.contrib:<goal>:shown`), the Yield panel's share
bars and their tooltips, and the CLI's text `--contributions` table. The Pareto's cumulative line
(`yield.contrib:<goal>:cumulative`) is the running total of the shown shares renormalised to end at 100 %, so it never
decreases. The ranking is β·r's and does not change.

### 8.7 Not covered here

The statistics functions reduce whatever cube they are given. A Data Display TRACE expression that calls one is
evaluated once over the cubes it names rather than point by point (`TraceExpression`, §9.3), so `plot` and the trace
card take them; the drawing styles a histogram wants are YA-8's. The measurement
scope reads the trial's drawn globals (`ResolvedGlobals` come from the trial's elaboration); a stamp-time,
frequency-dependent expression stays nominal (§7).

## 9. The `yield` verb and MCP (brief-yield-5)

### 9.1 One verb, three nouns

`circuitrf yield mc|estimate|trial <path>` (`docs/design/cli.md` §25) runs `StatisticalRun` and nothing else:
`mc` in `StatisticalMode.MonteCarlo`, `estimate` in `StatisticalMode.Yield`, and `trial` (or `--trial n` on either)
`EvaluateTrial(n)`. Its flags override the `statistics` line for one run, and the setup reaches the run only when a
flag changed it, so a flag-less run's `.yield.npy` is byte for byte the in-process run's. Over MCP it is `run
analysis=montecarlo|yield`, with a progress notification per batch.

### 9.2 The two writes

`--save-preset <name> --trial n` locks a trial's values in as a preset (`TuningPresets.LockIn`); `--save-corner <name>
--trial n` adds `corner <name> trial=n seed=s sampling=m trials=N` — the run's EFFECTIVE seed, sampling and trial
count, flags included, since those three identify the trial (§3). A `.csch` only, after a history checkpoint (D12).

### 9.3 Statistics functions in a trace

`TraceExpression` binds each reference of an expression that calls one of `Evaluator.AxisFunctions` as the cube its
slice leaves and evaluates once; the result's single remaining axis is the X. `check` asks `StatisticalRun.Create`
for the refusal a run would give, and `explain --analysis` reports the chains a yield run evaluates under each
`analyses=` scope and its cost in nominal evaluations.

## 10. Corners (brief-yield-6)

| Piece | Where |
|---|---|
| One evaluation per corner, a Monte Carlo at each | `CornerRun.cs` |
| The corner `DataSet` and the stacker | `CornerDataSet.cs`; names in `results-dataset-layout.md` §"Corners" |
| Statistical corners: proposing, replaying | `StatisticalCorner.cs`, `StatisticalRun.Replay`, `RecordedTrial` |
| The cross product | `CornerGenerator.cs` |
| Kit corner binding | `WorkspaceCorners` — moved to `src/Design/Workspace` |

### 10.1 A corner is a value map

A `.cnl` corner line binds values and a temperature (D10): a `.csch` corner's kit axis selections were resolved at
extraction (§3) into the values the line binds, the schematic's other axes as the schematic has them, since the
binder overlays the corner's selections on the design's. So `CornerRun` evaluates a corner as the value map
`CornerRun.BindingsOf` gives — its values, then `temp` as the ambient global — through `TunableOverrides.Apply`, the
tuned-value door: a VAR value replaces the variable, a tunable key the instance's assignment. **`temp` on a design that
never wrote it adds the global**, as typing it would (`Elaborator` reads the ambient by name either way); every other
key naming nothing is skipped with the note it always had. A corner naming a variable `--set` also sets is the refusal
`yield.corner.set-conflict`, checked when the run is created rather than per point.

**The headless extraction binds kit corners now.** `WorkspaceCorners` (axes, `Bind`, `BindingsFor`) was in `src/Ui`
only because nothing below the firewall had needed it; it moved to `src/Design/Workspace` unchanged except that it
resolves a kit path itself (rooted as stored, otherwise under the workspace root — `WorkspaceRefs.Resolve`'s rule,
which stays in `src/Ui`). `WorkspaceCorners.ForDocument(path)` reads the axes of the workspace a document belongs to,
and `SchematicCircuit.ExtractInWorkspace` binds the design's selections and passes the corner binder exactly as
`WorkspaceViewModel.WriteNetlist` does. Every path-based extraction — the run verbs' `.csch` input, `netlist`, `check`
— goes through it, so a schematic in a kit workspace runs headlessly at the corners Simulate runs it at. A workspace
offering no axes extracts exactly as before, byte for byte.

### 10.2 One run, every corner (R-ya6-1, R-ya6-2)

The nominal (tagged `nominal`) and every enabled corner (or `--corners` names; an unknown name is
`yield.corner.unknown`, none at all `yield.corner.none`) are ONE batch of `OptimizationRun.EvaluateValues` — in
parallel, each tagged `corner <name>` so two corners whose maps coincide are still two simulations. They are scored
against the **yield specs** (`GoalUse.Yield`): a corner analysis verifies the specs, and an opt-only goal is a design
target the yield goals are deliberately looser than (D4). A corner that does not evaluate is a fail under
`nonconverged=fail` (D7) and is reported with its reason either way.

Outcome: `Finished` (exit 0) when every goal is met at every corner, `BelowTarget` (exit 3) when one is not,
`NoneEvaluated` (exit 2) when nothing evaluated, the nominal included. The result carries each corner's evaluation and,
per goal, its **worst corner** — the smallest margin. The `DataSet` (§"Corners" of the layout note) has an outer
`corner` axis labelled with the names, the nominal first, and is written to `<design>.corners.npy`
(`CornerRun.ResultPathFor`) — never `run.npy` or the Monte Carlo file.

### 10.3 A Monte Carlo at each corner (R-ya6-3)

`CornerOptions.MonteCarlo` runs a `StatisticalRun` per corner, the nominal first, each with the corner's bindings as
`StatisticalOptions.Bindings`: applied to the nominal and every trial, and the nominal each spread is drawn around
(`SampleValues.Nominals` at the moved values), so a percent spread on a value the corner moves follows it. **At a
corner the kit's process draws are off** — the corner is the process answer; drawing process too would count one
question twice — while mismatch draws as configured; the nominal row is the ordinary run, process included. `check`
warns `yield.corner.process-double-counted` when the statistics line says `corners=` with `process=1` written. A
statistical corner is one trial and has no Monte Carlo of its own; it is left out with a note.

The per-corner results stack under the `corner` axis (`CornerDataSet.Stack`): every cube with an outer `trial` axis is
padded with NaN to the longest corner's trial count (auto-stop ends corners at different counts), a cube whose shape
differs any other way is left out, and the file is `<design>.yield.npy`. The outcome is `BelowTarget` when any corner's
yield is below its target; the result names the corner with the lowest yield. `statistics corners=all|<names>` (or
`--corners`) makes `yield mc|estimate` this run.

### 10.4 Statistical corners (R-ya6-4)

`corner <Name> trial=n seed=s sampling=m trials=N` replays trial n against the CURRENT nominal.
`StatisticalCorner.Replay` builds a `StatisticalRun` at the corner's seed, sampling and trial count (and the setup's
current process, mismatch and σ scale; auto-stop off) with the corner's own bindings, and asks it for trial n:

- **With the run's record** — `RecordedTrial`, from a run in memory (`RecordedTrial.Of`) or from the `.yield.npy`
  beside the design when its seed and sampling match and it holds the trial (`RecordedTrial.FromDataSet`; a rank-1
  `z:` cube per stream) — the recorded z-vector is used AS IT STANDS. An entry or distribution call the recording has
  no draw for takes its nominal (`ExpressionDraws`'s `unplannedAtNominal`), and a recorded stream the design no longer
  has is named in the warning `yield.corner.streams-gone`.
- **Without one**, the trial is drawn afresh from (seed, trial, stream) — the same draws for every stream the run had
  under `random` and `lhs`, whose streams are independent — and the note `yield.corner.not-recorded` says a renamed or
  added variable cannot be told apart. Under `sobol` a changed stream set moves the dimension mapping, which only the
  record avoids.

Because the stored form is z (D5), the same vector over a moved nominal is the same relative deviation for a percent
spread and the same absolute one for an absolute spread; correlated entries keep their correlation, the vector being
the one drawn after it. `StatisticalCorner.FromRun(result, goal, k)` proposes the k worst trials of a goal as corner
definitions named `<goal>_t<trial>`, with the run's effective seed, sampling and trial count
(`StatisticalResult.Settings`) — what "save as corner" uses.

### 10.5 The generator (R-ya6-5)

`CornerGenerator.CrossProduct(axes, temps, values)` crosses kit axes × options, temperatures and variable values into
explicit `CornerDefinition`s, named from their parts joined by `_`: a kit option as written, a temperature as its
number (`-` → `m`, `.` → `p`; `t25`, `tm40` when it starts the name), a variable or key as its letters and digits with
its value's number (`Vdd3p0`, `R2R1100`). Duplicates (the same bindings) are dropped and a name collision gets `_2`.
More than 256 corners is the refusal `yield.corner.generate-too-many` naming the count. `CornerGenerator.Parse` reads
the CLI's `axis=a,b;temp=-40,25;Vdd=3.0,3.6`: a name is a kit axis when one of the workspace's axes answers to it (its
label, file stem or key), `temp` the ambient, anything else a variable or tunable key — checked by `check` with the
rest of the corner lines. It writes lines; it is not a grammar.

### 10.6 `explain` (R-ya6-7)

`explain --analysis` lists each corner's bindings in base SI with the base unit (`temp` in °C; a bare number takes the
unit of what it binds) and, for a `.csch` in a kit workspace, each kit axis's section at that corner and whether the
corner sets it or inherits the schematic's selection (the axis's first section when the schematic chose none, as
`WorkspaceCorners.Bind` binds it).

## 11. Tune and optimize across corners (brief-yield-7)

### 11.1 A corner, evaluable at any point

`CornerPoint` (`src/Design/Statistics`) is one corner made ready to be evaluated at ANY point of the design — the
optimizer's candidates, the Tuning panel's sliders. `CornerPoint.Prepare(circuit, setup, names, nominal, recorded,
sets)` makes the list: the nominal first unless `nominal=0`, then each named ENABLED corner in the order named (null:
every enabled one, in setup order). A name that is no enabled corner (`yield.corner.unknown`, listing them), a corner
binding a `--set` name, and a statistical corner that cannot be replayed (`yield.corner.replay-refused`) are refusals.
A setup that is not the netlist's own (the Optimizer panel's, the schematic's) takes the EXTRACTED corner of the same
name, since only extraction resolves a kit axis selection into values (§10.1).

`At(values)` is the map a point evaluates at that corner. **A value corner is its bindings OVER the point's values —
the corner is the condition, so a value it binds wins over the optimizer's or the slider's.** A statistical corner is
prepared once (its `StatisticalRun`, its recorded vector) and replays its trial **around the point's values**
(R-ya7-3): `StatisticalRun.Replay(trial, recorded, at)` computes the nominals at the moved design
(`SampleValues.Nominals` over the point's values with the corner's bindings over them), so a percent spread moves with
the candidate. A draw that is not physical at a point refuses that point at that corner, not the run.

### 11.2 The optimizer across corners (R-ya7-1, R-ya7-2, R-ya7-5)

`optimize corners=none|all|<names> [nominal=0]` — default `none`, which is today's run byte for byte (the corner path
is a separate branch of `EvaluateBatch`). With corners, every candidate is evaluated at every point of the list
through the batch door: the batch × corners is ONE call to the evaluator, so it runs in parallel, and **the cache keys
by (values, corner)** — `<values key>\n#corner <name>`. Per candidate the outcomes merge:

- **The residual vector is every corner's residuals in turn**, so least squares and minimax keep their meaning;
  minimax then minimizes the worst violation over the corners — ordinary worst-case design.
- **A goal is met only when met at every corner.** Its `GoalReport` is the BINDING corner's — the largest violation,
  or when met everywhere the smallest margin — with `Corner` naming it and `PerCorner` the goal at each corner.
- **A corner that fails fails the candidate**, its reason prefixed `at corner <name>:`; the penalty for a failed point
  is computed from the largest MERGED cost, so it still ranks below every real point.
- `Evaluations` counts simulations: a point costs `EvaluationsPerPoint` = 1 + corners. Auto's global-stage budget is
  50(n + 1) POINTS, so it is multiplied by that; the user's own `maxevals` stays in simulations. Sensitivity (TO-8)
  goes through the same batch door, so it is over the same corner set. The results kept per point (and published) are
  the first corner's — the nominal's, when it is evaluated. Railing is unchanged.

`OptimizationRun.EvaluationPointsOf(setup)` is the list a run would evaluate at, without preparing it — what
`explain --analysis` prints (`at nominal, hot — 2 evaluations per point`) and the Optimizer panel's status shows
(`Iter 4 · 26 evals (2 per point)`).

### 11.3 Tuning at a corner (R-ya7-4)

The Tuning panel's toolbar has an **Evaluate at** picker — Nominal, or any enabled corner of the schematic — shown
only when there is a corner. `TuneSession.EvaluateAt(name)` resolves it through the session's prepared circuit
(`CornerPoint.For`), cancels the evaluation in flight and evaluates the sliders' values again there; each later
request is evaluated at the corner. **Push still writes the sliders' values alone.** The Data Display chip reads
`Tuning · <corner>` (`ITuneResultSink.AtCorner`), and the canvas's tuned labels end `@ <corner>`. A statistical corner
replays the run beside the schematic (`<design>.yield.npy`) when it is there.

### 11.4 Headless (R-ya7-6)

`opt --corners all|none|a,b` overrides the file; the binding corner is the goals table's `binding` column, and
`--json` carries `corner` and `perCorner` per goal plus the run's `corners` and `evaluationsPerPoint`. A statistical
corner replays the `<design>.yield.npy` beside the design when its seed and sampling match. MCP:
`run analysis=optimize corners=…`.

Gates: `tests/Ui.Tests/Statistics/CornerOptimizationTests.cs` (`CornerOptimizationTests`,
`CornerOptimizationCacheTests`, `TuneAtCornerTests`) and `OptCliVerbTests.Corners_ReportsTheBindingCorner`.

## 12. The Data Display's statistics (brief-yield-8)

**The numbers are the expression engine's; the Data Display adds drawing.** Every statistical picture is an ordinary
cube trace whose expression calls one of §8's functions, so the card, `plot`, a `measure` line and `render` compute it
with one piece of code. No new plot type: these are Rect plots (and a Table), so markers, export, Plot Versus and the
trace card keep working.

| Piece | Where |
|---|---|
| Draw style `Line|Bars|Step` (`TraceProperties.DrawStyle`, `.cdd` `Properties.DrawStyle`, not written when Line) | `src/Render/DataDisplay/Models/Misc.cs` |
| Bars, steps, normal fit, spec lines | `Renderers/StatisticsRenderer.cs`, called from `PlotRenderer` on Rect plots |
| The menu as a function — `Build`, `Apply`, `BackToCurves`, `CompanionOf` | `TraceStatistics.cs` |
| Goals → lines | `SpecLineResolve.cs`; the reverse match is `TraceGoalReader.GoalExpressionOf` + `TraceToGoal.SameQuantity` |
| The statistics table | the run's `statistics` group (`StatisticalDataSet`), drawn by `StatisticsTablePreset` |
| `normq`, histogram `"percent"`, Φ⁻¹ | `src/Core/Expressions` (`NormalDistribution`; `Engine.Statistics.SpecialFunctions` forwards to it) |

### 12.1 Bars (R-ya8-1)

A bar is one point, centred on its X, as wide as the expression's `width` companion (`TraceExpression.TryEvaluate`
now hands companions back, and the resolve stores the width on the trace) or, with none, the smallest X spacing. Fill
is the trace colour at `RenderTheme.FillOpacity`, outline the line colour. A family's members split each bin evenly
and stand side by side. **Step** is the bars' outline when there is a width and the post-step through the points when
there is not (an empirical CDF). Autoscale frames a bar trace down to zero and half a bin past each end.

### 12.2 The Statistics menu (R-ya8-2)

Over the `trial` axis when the trace has one — otherwise one submenu per axis, the chosen axis kept and every other
axis pinned (a kept one at its first sample, which the expression then shows):

| Entry | Expression | Style |
|---|---|---|
| Histogram | `histogram(<operand>, <n>)`, n by Freedman–Diaconis (`SampleStatistics.FreedmanDiaconisBins`) | Bars |
| Histogram (Percent) | `histogram(<operand>, <n>, "percent")` | Bars |
| CDF | `cdf(<operand>)` | Step |
| Normal Quantile | `normq(<operand>)` — values against Φ⁻¹ of Blom's positions; Gaussian is straight | Line |
| Yield Sensitivity vs ▸ `<key>` | `100*yield_sens(trials.pass, trials.stat:<key>, <n>)`, plus a second series `histogram(trials.stat:<key> + 0*trials.pass, <n>)` — the trials per bin, on the right axis, as a step at half opacity | Bars |
| Normal Fit (a histogram) | `mean_over`/`std_over` of the operand, scaled to the bars' area | — |
| Back to Curves | restores the trace exactly | — |

The operand is the card's own reading of the trace (`PickerBody` with its transform), so `mag(…)` stays `mag(…)`.
The FIRST rewrite records what the trace was (`TraceStatisticsOrigin`: expression, cube, slice, transform, style;
`.cdd` `StatisticsOrigin`), so Histogram then CDF still reads the original data and Back to Curves goes all the way
back. The companion's `+ 0*trials.pass` makes a trial with no pass/fail a NaN, which the histogram skips exactly as
`yield_sens` does — so the two share their bins. The added series carries an EMPTY origin, the mark Back to Curves
removes it by.

### 12.3 Spec lines (R-ya8-3)

A run records each scored goal's own line as `yield.goal:<g>:spec` (results-dataset-layout.md), so the limits come
from the result: `plot` has nothing else. A trace draws a goal when it is a histogram/CDF/normal plot of
`goal:<g>:worst`, or of a quantity a range-less goal reads (vertical lines at the values — a sloped limit at its
TIGHTER end, `in`/`out` at both edges); or when its "Add as goal…" translation reads the same quantity as the goal
(`TraceToGoal.Canonical`: whitespace, `dB20`→`dB`, a `nominal.` prefix, `~`, the KEPT axes of a slice and an
accessor's parentheses do not change the quantity) and its X is the goal's range axis — then the limit is drawn
across the range, sloped where it slopes. Dashed in `RenderTheme.LimitColor`, as segments (Skia's SVG device drops a
path effect), labelled `<goal> ≥ <limit>`. `Plot.ShowSpecLines` is null by default, which is ON: a line exists only
where a source records goals. The inspector shows the toggle only where one exists. **A vertical limit is always in
the picture** (brief-yield-16): an autoscaled X axis widens to take every vertical limit in, and a range the user fixed
stays as set, the limit then marked by an arrow at the edge it lies past. A goal's per-trial `worst` and `margin`
carry the goal's unit — its limit's, else dB for a whole `dB(…)`, else a named measurement's — so a histogram's axis
reads `bin (dB)` and a frequency goal is scaled to the display unit.

### 12.4 Normal fit (R-ya8-4), the table (R-ya8-5), live (R-ya8-7)

The fit is `A·φ((x − μ)/σ)/σ` with μ, σ from `mean_over`/`std_over` of the histogram's operand and A the bars' area
(Σ height × width), off by default (`.cdd` `NormalFit`). The table is the run's `statistics` group — one row per goal's
worst value and scalar measure on a labelled `quantity` axis, columns mean, σ, min, max, median, P1, P99, skew,
kurtosis, Cpk, σ-to-limit and the goal's yield with its interval, by `SampleStatistics` against
`GoalResiduals.ValueLimits` — drawn as one Table trace per column (**Add Statistics Table**, the Σ toolbar button).
While a source is published in memory (`IPlotDataSources.IsLive`), an unranged histogram holds its bin range from the
first frame with ≥ 30 values (`TraceStatistics.HoldRange`, through `pctl_over`) and is evaluated with that range
written in; once the source is a file again the range is re-derived. The table follows each frame for free, being
part of the published DataSet.

### 12.5 Headless (R-ya8-6)

`plot --trace …,stat=histogram|cdf|quantile|yieldsens[,over=<axis>][,bins=n][,percent=1][,param=<key>][,fit=normal]`
and `style=line|bars|step`; `--spec-lines`/`--no-spec-lines` (absent is the default, on). A statistic is
`TraceStatistics.Build` applied to the trace the spec built, so the `.cdd` written is the one the menu writes, origin
included. `--spec-lines` on a result recording no goal is refused (`plot.spec-lines.no-goals`).

Gates: `tests/Ui.Tests/Statistics/DisplayStatisticsTests.cs` — `BarsRenderTests`, `StatisticsMenuTests`,
`SpecLineTests`, `YieldSensitivityTests`, `HistogramPlotParityTests` (the CLI's SVG against the in-process
composer's, byte for byte, a histogram with spec lines and a fit).

## 13. The Data Display's trials (brief-yield-9)

**Which trials fail, and why.** §12 summarises the trials; this section draws them one by one — every trial's response
coloured by pass/fail with the nominal on top, an envelope where a thousand curves no longer read, scatters coloured
the same way, a contribution Pareto, and one selected trial highlighted in every plot of every display. As in §12,
nothing new is a statistic: categories are the result's own cubes, bands are §8's functions, a fit is
`Engine.Statistics.Regression` and a ranking is R-ya4-9's.

| Piece | Where |
|---|---|
| Authored views — `ColorBy`, `ShowNominal`, `Envelope`, `ShowCurves`, `ShowFitLine` (`.cdd` `ColorBy`, `Nominal`, `Envelope`, `Curves`, `FitLine`, each written only off its default) | `src/Render/DataDisplay/Models/Trace.Trials.cs` |
| Resolved — categories, counts, the nominal's points, the band, the fit, which trial each curve/point/bar is | `TrialResolve.cs`, called at the end of `TraceResolve.SetCubeDataFrom` |
| Drawing — the band, then `FamilyPlan` (passes, others, fails, nominal, selection) | `Renderers/TrialRenderer.cs`; bars dim in `StatisticsRenderer.DrawBars` |
| The menu as functions — colour-by choices, envelope refusal, Scatter vs | `TrialViews.cs` |
| A click → trials; a trial's values | `TrialPick.cs` |
| Contributions from a saved result; the Pareto | `src/Design/Statistics/ResultContributions.cs`; `ContributionParetoPreset.cs` |
| Re-run a trial; a trial as a corner | `src/Design/Statistics/TrialReplay.cs` |
| The shared selection, the trial actions, the chip | `src/Ui/DataDisplay/TrialSelection.cs`, `TrialActions.cs`, `DisplayWindowViewModel.TrialChip` |

### 13.1 Colour by and the nominal (R-ya9-1, R-ya9-2)

`ColorBy` names a per-member cube on the family axis: `pass` (the source's `trials.pass`), any address such as
`trials.goal:S21:pass`, or `corner` (a family over `corner`, each member its own wheel colour). A member reads 1 →
pass, 0 → fail, NaN → did not evaluate; and a member whose `trials.status` is not 0 did not evaluate WHATEVER its pass
reads — under `nonconverged=fail` such a trial's pass is 0, and drawing it as a fail would show a curve that was never
computed. Passes draw in the theme's neutral `RenderTheme.PassColor` at `TrialRenderer.PassOpacity` (0.35) — never the
trace's own colour, whose first wheel colour is red and drew passes and fails as one red band (brief-yield-16) — fails
in `RenderTheme.FailColor` (the limit colour), did-not-evaluate members not at all. The legend (the Y-axis label) reads `S21 — 471 pass · 29
fail` (`· n not evaluated` when there are any), counted over the WHOLE axis. The draw order is `FamilyPlan`: passes,
then members with no pass/fail, then fails, then the nominal — the trace re-read from `nominal.<cube>` with the trial
axis dropped from its slice (a measurement, spelled bare on the trace, from `nominal.measurements.<name>`) — in the trace's full colour and width (**Show Nominal**, on by default).

A trial family is capped at `Trace.MaxTrialFamilyCurves` (2,000) rather than the general 101: at 101 a failing trial
300 would never be drawn while the picture claimed to be every trial.

### 13.2 Envelopes (R-ya9-3)

`Envelope` is `minmax`, `p:<p>` (the Pp–P(100−p) band) or `sigma:<k>`, spelled the same in the `.cdd` and on
`plot --trace`. Per X point, over the family axis (`TrialResolve.EnvelopeExpressions`):

| Envelope | Lower | Upper | Line |
|---|---|---|---|
| `minmax` | `pctl_over(op, 0, "trial")` | `pctl_over(op, 100, "trial")` | `median_over` |
| `p:<p>` | `pctl_over(op, p, …)` | `pctl_over(op, 100 − p, …)` | `median_over` |
| `sigma:<k>` | `mean_over − k·std_over` | `mean_over + k·std_over` | `mean_over` |

`op` is the family read with both its family axis and its X kept (`TrialResolve.FamilyOperand`, the card's own
`PickerBody` and transform). **Min–max is percentiles 0 and 100, not `min_over`/`max_over`:** those two propagate a
NaN by design (a bad grid point in a band must not be hidden), and a trial that did not evaluate is a NaN on every X —
one such trial would blank the whole band. The band fills behind the members at the theme's fill opacity; **Curves:
off** draws the band and its line alone, which is one evaluation of three reductions however many trials there are.
On a Smith or Polar plot the envelope is refused — a pointwise band of complex values is not a region — with
`TrialResolve.ComplexPlaneRefusal` as the greyed menu item's tooltip and on `Trace.EnvelopeRefusal`.

### 13.3 Scatter (R-ya9-4)

A trace whose X is the `trial` axis offers **Scatter vs ▸** each `trials.stat:<key>`, each goal's
`trials.goal:<g>:worst` and each scalar measure. `TrialViews.ApplyScatter` writes an ordinary Plot Versus spec
(`XSpec`), turns the line off and the markers on, and colours by `pass` where the source scores goals. Each point is a
trial through the sample index the versus replaced (`Trace.SampleAxis`), so colour-by and selection apply point by
point (fails drawn after passes). **Fit Line** draws the least-squares line, `Regression.Fit` on the drawn points with
its standardized slope taken back to the plot's units, and puts `R² = …` in the legend.

### 13.4 Contributions (R-ya9-5)

Never computed unasked. **Statistics ▸ Contributions ▸ `<goal or measure>`** ranks it once through
`ResultContributions.Of` — which rebuilds the trials from the result's `z:` cubes, `goal:<g>:worst`, scalar measures
and `status`, and hands them to `StatisticalContributions.Of`, the run's own ranking — stores it as
`yield.contrib:<name>` (shares, largest first) and `yield.contrib:<name>:cumulative` over a labelled `contributor`
axis, writes the source back so a saved display redraws without recomputing, and adds a plot: the shares as bars in
percent, the running total as a line on the right axis. A Rect plot whose bars all stand on a labelled 1…N axis labels
its X ticks with the names (`Plot.XCategoryLabels`).

### 13.5 Linked trial selection (R-ya9-6)

A click on a family member, a scatter point or a histogram bar (every trial in its bin — the value on a shared edge
belongs to the bar on its right, as the histogram counts it) selects trials in that SOURCE (`TrialPick.At`, within 6 px).
The selection is `TrialSelection.Shared`, keyed by the source's full path, above every display — each display has a
library of its own, and a selection is a source's. Plots copy it onto their traces (`Trace.SelectedTrials`, never
persisted); selected elements draw last and highlighted, the rest at `TrialRenderer.DimOpacity`. The toolbar chip reads
`Trial 417` or `23 trials`; Esc clears. The click is not consumed — the plot is still selected and dragged as before.

With exactly one trial selected, the plot's context menu offers **Trial n ▸**: **Send Trial to Tuning** (the trial's
drawn values, `TrialPick.ValuesOf`, through `TuningPanelViewModel.LoadValues` on the schematic the result came from),
**Re-run Trial** (`TrialReplay.Run` — the design beside the result, `StatisticalRun.EvaluateTrial` — shown as the
snapshot ghost of every bound trace in every display, the result stacked on a one-long `trial` axis so the traces'
slices resolve unchanged), **Copy Values** and **Save as Corner…** (`TrialReplay.CornerOf`: the result's seed,
sampling and trial count, as `yield --save-corner` writes, added as one undo step). The Yield panel's trial table
(YA-10) calls the same `TrialActions`.

### 13.6 Headless (R-ya9-7)

`plot --trace …,colorby=pass|corner|<cube>,envelope=minmax|p:1|sigma:3,curves=0|1,nominal=0|1[,fitline=1]` set the
fields the window writes; selection is interactive only. Gates: `tests/Ui.Tests/Statistics/DisplayTrialsTests.cs` —
`FamilyColourByTests`, `EnvelopeTests`, `ScatterTests`, `TrialSelectionTests`, `TrialPlotParityTests` (the CLI's SVG
against the in-process composer's for a pass/fail family with a P1–P99 envelope), `ContributionParetoTests`. Both
parity tests mask Skia's `clipPath` ids, which come from a counter in the process (see `RailZMapTests.WithoutSkiaIds`).

## 14. The Yield panel (brief-yield-10)

`src/Ui/Yield/` — `YieldPanelViewModel` (lists and edits; `.Run.cs` the run, the readouts, the trial table and the
trial actions; `.Corners.cs` Corners mode), tabbed behind the Optimizer (`DockPanelIds.Yield = "Yield"`, a file
format; `DockLayoutDefaults.TabbedBehind` gives a layout saved before it the panel beside the Optimizer). It follows
the focused schematic on Tuning's rule.

### 14.1 Edits

Every edit is a pure function in `TuningSetupEdits` (`WithStat`, `WithoutTolerance`, `WithDistribution`,
`WithGoalUse`, `WithStatistics`, `WithCorrelations`, `WithCorners`) wrapped in one `SetTuningSetupCommand`. Before it
lands, `StatisticsValidator.EditRefusal(before, after, catalog)` returns the first ERROR the edit adds — the setup-only
half of `check` (`ValidateSetup`: entries, complex parts, correlations, settings) — so the refusal is `check`'s own
sentence and a problem the setup already had is not this edit's to refuse. A tolerance on a key with no entry creates
one with `tune`/`opt` off and NO range (the first tune or opt gives it the D4 default range); an entry carrying a
tolerance is never dropped as "default-shaped" when its tune and opt flags clear. The compact spread editor is
`ToleranceText` (`src/Design/Statistics`): `± 2 % at 3σ`, `σ 1 Ω`, `45 … 55 Ω`, `4 … 8 by 2`, `, trunc 3σ` to and from
the `tune` line's keys — it moves text only; validity is the validator's. The Inspector's toggle and the canvas's
right-click ▸ Tolerance… go through `IToleranceSurface`, keyed by the Tuning surface's keys.

### 14.2 The run (R-ya10-10)

`Run` creates `StatisticalRun` exactly as `yield` does — the bench prepared as Simulate netlists it, `Setup = null`
so the extracted tuning block is what runs, `ResultPath = StatisticalRun.ResultPathFor(<schematic>)` — on a background
thread; progress and frames are posted to the UI thread. The trial table is read from the frames
(`YieldTrialRowViewModel.From`: `trials.pass`, `trials.status` + `reasons`, each `goal:<g>:margin`), so it is the same
function of the same cubes during and after the run. Corners mode (and a `statistics corners=` line) drives
`CornerRun`; a corner sweep has no pause, and Stop cancels it. Gates: `tests/Ui.Tests/Statistics/YieldPanelTests.cs` —
the panel's file is the verb's byte for byte; paused and resumed it is an uninterrupted run's; stopped it keeps
exactly the trials an uninterrupted run of that many draws produces.

### 14.3 The yield display (R-ya10-8)

`YieldDisplayPreset` (`src/Render/DataDisplay`) composes, never draws: per goal a pass/fail family over the `trial`
axis, a histogram of `trials.goal:<g>:worst` (`TraceStatistics.Build`), a yield sensitivity over the first goal's top
`entry` contributor, and `StatisticsTablePreset`. The family's cube is FOUND, not guessed: every slice of every
trial-stacked cube in the goal's analysis group is tried (the goal's range axis kept as X, ports and labels pinned
in turn, each transform a goal can name) until `TraceGoalReader.GoalExpressionOf` reads the goal's quantity — the same
reverse translation the spec lines use, so the family chosen is a curve the goal's spec lines land on. A goal with
no swept axis (one number per trial) is drawn as its worst value against the trial number. The workspace writes the
document as `<design>.yield.cdd` beside the result and opens it; a display that exists is focused instead.

**A Monte Carlo at each corner is one tab per corner.** Its cubes are `[corner, trial, …]`; `Compose(ds, src, k)`
decides what to plot on `ResultContributions.CornerOf(ds, k)` — the one run that corner was, so the family search, the
goals and the contributions ranking run unchanged — and every trace it emits pins `corner` by name, so it evaluates
against the stacked file. `Build` writes the tabs in the run's order with `ActiveTabIndex` on the lowest
`yield.yield`. Three readers learned the pin rather than refusing: `TrialResolve`'s colour-by reduces the pass and
status cubes at the trace's pinned axes; a yield sensitivity slices `pass` and the stat at the trace's corner;
`TraceGoalReader` drops a pinned corner, because a goal holds at every corner — which is also what lets a spec line
land on a corner's family. A Table whose columns all pin one corner says it once, on the X column
(`quantity @ <corner>`). The file is `<design>.yield.corners.cdd`: a plain yield run writes the same `.yield.npy`,
and each display's traces are made for one shape of it. A corner run without MC has no `trials` group; the panel
disables the button with the reason rather than writing an empty display.

## 15. Design centering (brief-yield-11)

`CenteringRun` (`src/Design/Statistics`) moves the nominals of the `opt=1` entries to maximize yield against the
`use=yield|both` goals while the tolerances ride along. The designable variables are `OptimizationVariables` — the
optimizer's own decode of the unit box, one range per variable (tuning D4), the same scales, steps, preferred values
and complex parts. The statistical variables are the `stat=1` entries and the kit draws; an entry that is both is the
usual case, and its percent spread is drawn around its CANDIDATE nominal (`SampleValues`, R-ya2-5). No `opt=1` entry,
no yield goal, nothing that varies, or an algorithm centering does not offer is a refusal from the constructor, which
`check` asks too.

### 15.1 Common random numbers (R-ya11-2)

The trials are an ordinary `StatisticalRun` over the setup with its statistics line set to `trials=M` and the centering
seed (no auto-stop, no corners). Each candidate is evaluated through `StatisticalRun.EvaluateAt`: trial t's z-vector —
a pure function of (seed, t, stream), D5 — re-applied around the candidate's nominal, its values under the trial's
draws, one evaluator batch per iteration (population × M points) through the optimizer's cache and parallelism. So two
candidates' yields differ by the design, not by sampling noise, and a candidate seen before costs nothing.

### 15.2 The smooth objective (R-ya11-3)

Per trial: the smallest normalized margin over the yield goals, `m = min_g margin_g / scale_g` (the goal's
`GoalResiduals.Scale` — its `scale=`, or the default users already read), through a logistic of width `w` (default
0.05): `1 / (1 + exp(−m / w))`. The objective is the mean over the counted trials; a trial that did not evaluate scores
0 and is counted under `nonconverged=fail`, and is left out under `warn` (D7). The algorithm minimizes the NEGATED
objective — not `1 − objective`, which rounds to 1 for a candidate many widths outside a window and would make the
whole region far from the specs flat; the logistic's tail keeps ranking those down to about e^−700. A failed candidate
(infeasible, or no trial counted) costs 1 with `Failed` set, below every real point. The plain yield on the same
trials, with its interval, is reported beside the objective; the best point is the best objective.

### 15.3 Algorithms (R-ya11-4)

From the registry, as ask/tell over the unit box: `OptimizerAlgorithmInfo.SuitsNoisyObjective` marks the ones a
piecewise-flat objective suits — `simplex`, `pattern` (mesh adaptive direct search), `cmaes` (the default), `bayes`
and `discrete` — and `OptimizerAlgorithms.ForNoisyObjective` is the menu, the directive's summary and the reference's
list. Gradient methods are not offered: at finite M a finite difference of a yield is zero almost everywhere. Limits:
`maxiter` (default 100), `maxevals` (simulations, verification not counted), `timelimit`, and the optimizer's stall
rule (`stall_tol`, `stall_iters` from `OptimizerAlgorithms.CommonOptions`) on the best objective.

**Standard values.** A `discrete=preferred` or E-series entry is searched continuously, as every algorithm but
Discrete searches it in the Optimizer, and the search then ends with a snap stage: each such nominal goes to the
series values either side of where the search left it (`PreferredValues.Bracket`), every combination with up to six
of them (else the nearest only), each scored on the common trials, the best kept (`yield.center.snapped`, Info; none
evaluating is `yield.center.snap-failed`, a Warning, and the point stays between rungs). The trials around a snapped
nominal stay continuous: a built part varies continuously within its tolerance. Discrete is on the series throughout,
and a Stop ends with no snap, as the Optimizer's does. There is no polish: centering has no fast local method to hold
the snapped values while it re-searches the rest.

### 15.4 Verification and results (R-ya11-5, R-ya11-7)

At the end the start and the best point are each run by an ordinary `StatisticalRun` (Yield mode) with the point's
values as its `Bindings` — the nominal each spread is drawn around — on `verify` trials (default 1,000) at seed + 1,
an independent draw. Both use the same trials, so the comparison is like for like; `CenteringVerification` carries
both Clopper–Pearson estimates and whether the intervals overlap (the gain is then not resolved at that many trials).
The best point's run writes `<schematic>.yield.npy` — its full YA-4 DataSet, so every display of §12–13 reads a
centred design's yield with nothing new. The result also holds the per-iteration history (best plain yield, best
objective, simulations), the best nominals as value text, and the railed variables (`OptimizationVariables.Railed`,
tuning D17). Exit (D13): 0, or 3 when the VERIFIED yield is below the statistics `target`; 2 when no candidate
evaluated. Stop keeps the best point and still verifies it; cancelling writes nothing.

### 15.5 The `center` line and headless (R-ya11-6, R-ya11-8)

`center [algorithm=<id>] [trials=<M>] [verify=<n>] [maxiter=<n>] [maxevals=<n>] [timelimit=<d>] [width=<w>]
[parallel=<n>] [seed=<n>] [surrogate=none|quadratic]` (`surrogate` is §16) — `CenteringSettings` on `TuningSetup.Centering`, the `.csch` block's `Centering`, at most
one line, defaults held as null and never written. `explain --analysis` states the estimated total before anything
runs — the algorithm's first batch (its population, asked of the algorithm itself) × M per iteration, times the
iteration limit (or the evaluation limit), plus the start and both verifications. `circuitrf yield center` takes the
line's settings as flags (`cli.md` §25.8), `--save-preset` stores the centred nominals (D12), and MCP
`run analysis=center` reports a progress notification per iteration. Gates: `tests/Ui.Tests/Statistics/CenteringTests.cs`
— `CenteringDividerTests`, `CommonRandomNumbersTests`, `CenteringSettingsRoundTripTests`, `CenterCliVerbTests`.

## 16. The Centering mode and the quadratic surrogate (brief-yield-12)

### 16.1 The panel's Centering mode (R-ya12-1)

`YieldMode.Centering`, the fourth mode (`YieldPanelViewModel.Centering.cs`). The variable list shows the `opt=1` and
`stat=1` entries together (`YieldCenterRowViewModel`): a designable value's range, the tolerance beside it, and after
a run start → centred with its mark on the range and a railed end (the Optimizer's `RangeMarkBar`). The `center` line
is edited through a summary line with a chevron, as the statistics line is (R-ya10-5): algorithm (the registry's
`ForNoisyObjective` ∩ built), common trials, verify, iteration and simulation limits, surrogate — each edit one undo
step through `TuningSetupEdits.WithCentering`, which keeps the line even at every default (its presence is the design
saying it is centred). ▶ creates `CenteringRun` with `Setup = null` — the bench's own setup, as `yield center` reads
it — so the panel's centred nominals are the verb's for the same seed (`CenteringPanelTests` compares them with the
verb run as a process). The yield-vs-iteration chart is the Optimizer's `CostSparkline` with `LogScale=False`; the
verification reads `start [interval] → centred [interval]`, and the trial table and the yield bar are the best
point's verification run's. Pause, Resume and Stop are the yield run's (Stop keeps the best point and still verifies
it). **Lock in** (`TuningPresets.LockIn`), **Push** (`TuningPush`, one undo step per document — the workspace supplies
`SessionForDrawing` exactly as for the Optimizer) and **Send to Tuning** act on the centred nominals as the Optimizer's
act on its best point.

### 16.2 The surrogate (R-ya12-2)

`center surrogate=none|quadratic` (`CenteringSettings.Surrogate`, null = none; `--surrogate` and MCP `surrogate`).
The z-space is `StatisticalRun.SurrogateDimensions`: one coordinate per statistical entry, per kit process stream, and
ONE per instance for all of its kit mismatch streams (YA-4 R-ya4-9's grouping) — the instance's coordinate x moves each
of its n streams by x/√n, so it is itself a standard normal (Σz/√n). A kind switched off has no coordinate. For each
candidate, `QuadraticSurrogate.Design(k)` places the centre and ±δ (δ = 1) on each coordinate, and, when
k ≤ 12, one cross point (+δ, +δ) per pair — exactly the 1 + 2k + k(k−1)/2 coefficients of a full quadratic; beyond 12
the fit is diagonal and a note says so. Those points are evaluated through `StatisticalRun.EvaluateAtZ` (the trial
door with the z-vector given rather than drawn; trial number 0; a cache tag of the coordinates), and **k + 2 of the
common trials** are simulated beside them through `EvaluateAt`. The check trials are what makes the fit a least-squares
fit at all: the design alone determines the quadratic exactly, so its R² would be 1 by construction.

Each yield goal is fitted separately by Householder QR (`LeastSquares`, `src/Engine/Statistics` — no normal equations,
so an exact quadratic fits to round-off). **A goal on a single number is fitted on its VALUE**, and its margin computed
from the predicted value by the goal's own rule (`QuadraticSurrogate.MarginRule`: the distance inside
`GoalResiduals.ValueLimits` for ge/le/in, `GoalResiduals.Score` itself for eq/out): a window's margin
`min(x − lo, hi − x)` has a kink at the window's centre that no quadratic follows, which is exactly where centering
ends up, while the value is smooth. A goal over a sweep is fitted on its margin. The smooth objective and the yield are
then counted on 10,000 virtual trials — `StatisticalRun.VirtualCoordinates`, the first 10,000 trials of the centering
seed and sampling (under `random`, trials 1…M ARE the common trials), mismatch aggregated as above — through the same
logistic of width w as §15.2; a virtual trial passes when every predicted margin is ≥ 0. A candidate whose design did
not evaluate whole (a point that failed, a goal with no value) is simulated on the common trials instead.

A design no cheaper than the common trials (2k + 1 [+ cross] + k + 2 ≥ M) is refused with the counts —
`yield.center.surrogate-too-many` names the coordinates, the toleranced values, the process draws, and the mismatch
draws with the instances they group into. Nothing is truncated.

### 16.3 Honesty (R-ya12-3)

The surrogate chooses where to look; it never answers. The verification (§15.4) is unchanged — the start and the best
point simulated on fresh trials — and that is the yield the result, the exit code and the report carry. A surrogate
candidate's `CandidateScore.Fit` holds its R² per goal; each `CenteringIteration` and progress report carries the
poorest per goal over the iteration's candidates. Below 0.9 on any goal is a warning on that iteration
(`yield.center.surrogate-poor-fit`); three iterations running switch the rest of the search to simulated trials
(`yield.center.surrogate-switched-back`, `CenteringResult.SwitchedBackAt`), and the best point is re-scored on the
common trials at the switch so it competes on equal terms. The report labels the search's yields as virtual ("Searched
(not verified)"), and `result.center.surrogate`/`switchedBackAt`/`history[].rSquared` carry the same in JSON.

On the divider (§15's bench, M = 200, 15 iterations of CMA-ES) the surrogate ran 610 simulations against 12,200 and
both verified at 84.3 % [81.9 %, 86.5 %] — the exact yield of a centred window. Gates:
`tests/Ui.Tests/Statistics/SurrogateTests.cs` — `QuadraticSurrogateTests` (an exact quadratic fits to R² = 1 within
1e-12 and its virtual yield and objective equal the true ones on the same trials; a cubic fits to R² = 0.876 and is a
poor fit), `SurrogateCenteringTests` (same verified yield within the intervals, fewer simulations — a counter),
`CenteringPanelTests` (the panel's centred nominals equal the verb's; Push is one undo step).

## 17. Design of experiments (brief-yield-14, optional)

A design of experiments sets the factors at the points of a structured design, runs each point once through the one
evaluator, and analyses which factors move each response. It is a noun on the `yield` verb and a mode of the Yield
panel, not a fourth run service with its own evaluator: `DoeRun` (`src/Design/Statistics`) evaluates through
`OptimizationRun.ForEvaluation` / `EvaluateValues`, exactly as a Monte Carlo trial does.

### 17.1 Factors and levels (R-ya14-1)

`factors=opt` (the default) varies the `opt=1` entries through the optimizer's own `OptimizationVariables`: coded −1 is
u = 0, +1 is u = 1 and the centre u = ½, decoded on the entry's own scale — so a log-scaled entry's centre is
geometric, and a complex value's parts (tuning D18) are factors like any entry, recomposed whole. Integer, stepped and
preferred-value entries snap (preferred values too — a DOE level is a value someone could build) and the run notes the
levels they snapped to (`yield.doe.snapped`). `factors=stat` varies the `stat=1` entries: a level is z = ±k
(`levels=sigma:k`, default 1) through the entry's OWN distribution via `SampleValues.Apply` — nominal ± k σ exactly for a
Gaussian, the Φ(±k) quantile for the others — so a tolerance study costs a handful of runs instead of a Monte Carlo. A
`trunc=` entry is set through its UNtruncated distribution with z clipped at ±trunc: the truncated quantile at z = k is
not kσ (`trunc=3` puts `sigma:1` at 0.99σ), and a level past the truncation is a value the entry never draws, so it
stops at the edge.
Correlations are not applied to the levels (each factor is set on its own; `yield.doe.uncorrelated` says so) and kit
statistics are not factors. A mixed rectangular/polar pair is refused (`yield.doe.mixed-pair`), as in D2: no corner of
two non-orthogonal ranges need describe a complex number. `levels=range` with stat factors, or `sigma:` with opt
factors, is `yield.doe.levels-mismatch`.

### 17.2 Designs (R-ya14-2)

`DoeDesigns` (`src/Engine/Statistics`, pure numerics). Factor letters are the textbook's, A–Z without I.

| `design=` | Points | Limit |
|---|---|---|
| `full2` | the 2^k corners in standard order | 10 factors (`yield.doe.too-many` names the run count) |
| `frac` | 2^(k−p) at `resolution=4\|5`: the fewest-run design in the embedded table whose resolution is at least the one asked | 4–11 factors; none held → `yield.doe.no-fraction`, listing what is |
| `pb` | Plackett–Burman, 12, 20 or 24 runs, the first k columns | 23 factors |
| `ccf` | a cube (full up to 4 factors, the table's fewest-run resolution-V fraction beyond), then ±1 on each axis | 10 factors |

The fraction table is Montgomery's "selected 2^(k−p) fractional factorial designs" (*Design and Analysis of
Experiments*), the minimum-aberration choices of Chen, Sun & Wu (1993); the Plackett–Burman designs are the cyclic
constructions of Plackett & Burman (1946) — the published first row, its cyclic shifts, a row of every factor low. Both
are data in the source with their citations. The axial points are `DoeDesigns.CentreAndAxial`, the construction YA-12's
quadratic surrogate builds its design on (`QuadraticSurrogate.Design` calls it), so the two cannot drift.

`centre=<n>` (default 1) adds centre points. **Simulation is deterministic, so there is no replication and no
randomized run order** — both exist to average out and decorrelate run-to-run noise a simulation does not have. A
repeated centre point is simulated once (identical value maps are deduplicated before evaluation); its purpose is the
curvature check, not an error estimate.

### 17.3 Effects (R-ya14-4)

Responses: each analysed goal's `worst` value and `margin` (§8) and every real scalar measure. `responses=goals` takes
the goals the factors are for — `use=opt|both` with opt factors, `use=yield|both` with stat factors; `all` takes every
enabled goal. When the bench has a `measure` line every analysis runs (`analyses=all` on the evaluator), so every scalar
measure is a response.

`DoeEffects.Model` decides what a design can estimate: `full2` every main effect and two-factor interaction; `frac` the
main effects, then each two-factor interaction not already in an earlier term's alias chain — one estimate per chain,
labelled with the whole chain (aliases are listed to three factors, from the defining relation's words); `pb` main
effects only, each with the two-factor interactions it is PARTIALLY confounded with and their correlation
(`BC (−0.33)`); `ccf` main effects, two-factor interactions and pure quadratics. An effect is never shown as if it were
clean.

Each response is fitted by least squares (`LeastSquares`, Householder QR) on coded factors over every run that evaluated
it; an effect is twice its coded coefficient — for a main effect the change from low to high. **Lenth's method**
(Lenth 1989) separates active effects from noise-sized ones, since a deterministic simulation has no pure error:
s0 = 1.5 × median|e|, PSE = 1.5 × median of the |e| below 2.5 s0, margin = t(0.975; m/3) × PSE (`StudentTQuantile`,
through the regularized incomplete beta). Active means |e| above the margin and above 1e-9 of the largest effect — on a
response the model reproduces exactly the PSE is 0, and an inert factor's 1e-16 must not read as active. With centre
points a two-level design reports the curvature (cube mean − centre mean); above the margin it is a warning naming
`design=ccf` (`yield.doe.curvature`). A response with fewer evaluated runs than coefficients is not fitted
(`yield.doe.not-fitted`).

### 17.4 Results and plots (R-ya14-5, R-ya14-6)

`<design>.doe.npy` (`DoeRun.ResultPathFor`); the layout is in `results-dataset-layout.md` §"Design of experiments".
The plots are `DoeDisplayPreset` (`src/Render/DataDisplay`), written beside the result as `<design>.doe.cdd` by the
panel's display button — composed from ordinary cube traces, nothing new to draw: per response, the **effects Pareto**
(`effects.<r>:abs` as bars over a `rank` axis labelled with the terms, the Lenth margin as a line), the **main-effects
plot** (`main.<r>:<key>`, one line per factor over the coded levels; a two-level design's means are its cube's, so a
centre point does not put the curvature into every factor's line) and the **interaction plot** of the strongest active
pair (`interaction.<r>:<A>*<B>`, a family over `by_level`). A goal's margin repeats its value's effects with the sign
turned, so the display draws the value.

### 17.5 The model optimum (R-ya14-7)

`DoeRun.ModelOptimum` searches the fitted models with the optimizer's registry algorithms (differential evolution, then
BFGS-B from its best, over the unit box; a model evaluation is microseconds) for the point inside the ranges where the
smallest predicted goal margin over its scale is largest. A goal on one number is predicted from its value's model and
scored by the goal's own rule (YA-12's `MarginRule`); a goal over a sweep, from its margin's model. A coordinate within
1e-9 of a range end is put on it (a search that rails stops a hair short), snapped to allowed values, predicted there,
and **confirmed by one real simulation**, reported as predicted vs simulated for every goal. The model is never the
answer — the confirmation is (the YA-12 rule). Stat factors are refused (`yield.doe.optimum-needs-opt`): a tolerance
study has no designable range to search. The panel's **Send to Tuning** loads the point into the sliders; **Send to
Optimizer** makes it the start of the Optimizer's next runs (`OptimizerPanelViewModel.StartFrom`, applied to the
prepared bench in memory through `TunableOverrides`, cleared from the panel).

### 17.6 The `doe` line, headless, the panel (R-ya14-3, R-ya14-8, R-ya14-9)

`doe [design=…] [resolution=4|5] [factors=opt|stat] [levels=range|sigma:<k>] [centre=<n>] [responses=goals|all]
[parallel=<n>]` — `DoeSettings` on `TuningSetup.Doe` and `CschTuning.Doe`; defaults are never held or written (levels
that are the factors' own default read back as null), a malformed value is `cnl.doe.value-invalid`, a second line
`cnl.doe.repeated`. `check` asks `DoeRun.Create` and reports its refusal. `circuitrf yield doe <path>` (`cli.md`
§25.9), MCP `run analysis=doe`; `reference statistics` carries the section with the screening guidance (`pb` first,
then `ccf` on the active few). The panel's **DOE** mode shows the plan (factors, levels and run count — the
constructor's answer, nothing simulated) before running, then the effects table per response with active effects in
bold and their aliases, the one-click display, and Model optimum → confirmation → Send.

Gates (`tests/Ui.Tests/Statistics/DoeTests.cs`): `DoeDesignTests` (the eight corners; the 2^(5−1) V generator
E = ABCD with no main-effect alias; the 12/20/24-run Plackett–Burman matrices published and orthogonal),
`DoeEffectsTests` (y = 3a + 2b + ab recovered exactly, the inert factor inactive), `DoeRunTests` (an RC low-pass: R and
C active, an inert resistor not; the line round-trips byte-stable through `.cnl` and `.csch`), `DoeModelOptimumTests`
(a `ccf` of an exact quadratic lands on the analytic optimum and the confirmation matches the prediction),
`DoeCliVerbTests` (the verb as a process; `--json` carries the alias sets; `check` refuses), `DoePanelTests`.

## 18. The example, the user pages, and the series as built (brief-yield-13)

### 18.1 The example (R-ya13-1, R-ya13-4)

`examples/Yield/` — **File ▸ New ▸ Examples ▸ Yield, Corners and Centering** — holds one test bench per question:

| Cell | What it shows | The outcome at its own settings, seed 1 |
|---|---|---|
| `BandpassYield` | the Optimization example's minimax bandpass at its equiripple point, each part its own value at ± 2 % (3σ, truncated at 3σ), `C1`/`C3` correlated ρ = 0.9; three `use=opt` goals and two looser `use=yield` specs; `BandpassYield.yield.cdd` saved beside the schematic | 86.4 % [83.1 %, 89.3 %] of 500, every failure the passband's; target 80 % met |
| `AmplifierCorners` | a Statz FET whose `Betatc` makes it lose gain hot; six corners generated from −40/25/85 °C × `Vdd` 3.0/3.6 V, the statistical corner `WorstGain` saved from trial 131, `optimize corners=all` | only `t85_Vdd3p0` fails, on gain (−0.23 dB); across corners LM meets both goals in 128 simulations with that corner binding |
| `DividerCentering` | a 1 V → 0.5 V divider, both resistors ± 3 % (3σ), `Vout` in 0.49 … 0.51 V, `R1` designable | estimate 70.8 %; centering verifies 73 % → 99.1 % on 1000 fresh trials (99.5 % with the surrogate, 410 simulations instead of 4,100) |

Each bench was authored as a `.cnl`, drawn with `netlist --to-schematic` (the bandpass reuses the Optimization
example's drawing), its corners generated and its statistical corner saved through the verbs themselves, and every
`.csch` re-saved through `SchematicPersistence` so it is byte-canonical. The `.cdd` is `YieldDisplayPreset.Build`
over the bench's own run with a results-root-relative source, `../BandpassYield/schematic/BandpassYield.yield.npy`
— a display waiting for its run, as every shipped example's is.

**A yield result lands beside its schematic, not under `results/`**, so the app's examples item group
(`src/Ui/CircuitRF.Ui.csproj`) now excludes `examples/**/*.npy` as well; running an example in place would otherwise
have shipped the bandpass's 4.6 MB result. `ExampleWorkspacesTests.TheItemGroupDoesNotShipMachineLocalState` holds
it.

Gate: `tests/Ui.Tests/Examples/YieldExampleTests.cs` — one test per bench, the CLI as a process on the shipped
`.csch` with results redirected to a temporary folder: the bandpass's yield inside [0.83, 0.90] with only the specs
scored and the saved display's plots those `YieldDisplayPreset` composes; the amplifier's single failing corner by
name and the optimizer's binding corner; the divider's verified start and centred yields. All three run in about
two seconds, so none is a benchmark.

### 18.2 Decisions as built

| Decision | As built | Where |
|---|---|---|
| D1 a tolerance is part of the entry | `TunableEntry.Stat`/`Distribution`/`Spread`; one row per key in Tuning, Optimizer and Yield | §1, §14 |
| D2 distributions | `gauss`, `unif`, `lognorm`, `discrete`; percent vs absolute kept as text; truncation by sampling the truncated distribution; the non-physical warning at 1e-9 | §1.1, §6 |
| D3 correlation | Gaussian copula; Higham repair, reported with its largest change | §4, §6 |
| D4 specs are goals | `use=opt\|yield\|both`; the optimizer scores `ForOptimizer`, a run `ForYield` | §2, §4 |
| D5 sampling | counter-based streams (seed, trial, stream); `random`, `lhs`, `sobol` (Joe–Kuo table); a trial stored as z | §6 |
| D6 kit statistics | distribution built-ins, nominal = first argument; process/mismatch by scope; statistical sections by text scan; mismatch variants swapped in | §7 |
| D7 did not evaluate | counted as a fail by default, `nonconverged=warn` excludes; reasons kept per trial | §8.2 |
| D8 yield and confidence | Clopper–Pearson; auto-stop per trial count, never before 50 | §5, §8.3 |
| D9 results | `<design>.yield.npy`, outer `trial` axis, `trials`/`nominal`/`statistics`/`contributions` groups; `<design>.corners.npy`, `<design>.doe.npy` | §8.4, `results-dataset-layout.md` |
| D10 corners | value maps; kit selections bound at extraction; generator writes lines; statistical corners replay recorded z | §3, §10 |
| D11 architecture | numerics in `src/Engine/Statistics`, orchestration in `src/Design/Statistics`, the panel in `src/Ui/Yield`; one evaluator (`OptimizationRun`'s batch door) | §6, §8 |
| D12 headless writes nothing | `--save-preset`, `--save-corner`, `--write` are the only writes | §9, §10.5 |
| D13 exit codes | 0 / 3 below target or failing corner / 1 / 2 / 130; DOE has no 3 | §9, §17.6 |
| D14 pause, stop, live | counter-based draws make pause a hold; the Data Display follows with a Yield chip | §14.2 |
| Centering (YA-11/12) | common random numbers, smoothed yield, noisy-objective algorithms, simulated verification; quadratic surrogate optional | §15, §16 |
| DOE (YA-14, optional) | full/fractional/Plackett–Burman/composite designs, Lenth's margin, model optimum with a confirming simulation | §17 |

### 18.3 What is left

Each item names the seam it would take; none needs a format change.

- **High-sigma estimation** (scaled-sigma sampling, worst-case distance, a learned fast Monte Carlo) for yields
  beyond what a plain Monte Carlo can resolve. Seam: `StatisticalRun.EvaluateAtZ` already evaluates any point in
  z-space, and the surrogate's `VirtualCoordinates` shows how a run scores trials it did not simulate; a new
  `StatisticalMode` with its own estimator would sit beside `YieldEstimate`, and `statistics` would gain a
  `method=` key (an unknown key is already a kept warning, so older readers survive it).
- **The other simulator dialect's `statistics { process { … } mismatch { … } }` blocks.** Seam:
  `PdkCorners.StatisticalSections` and `SpiceDistributions` read only the SPICE functions; a reader for those blocks
  would turn each `vary` into the same process or mismatch stream (`ExpressionDraws`) a SPICE kit's call becomes, so
  nothing downstream changes (`spice-models.md` §8.11).
- **A distribution inside a frequency-dependent expression** stays at its nominal: the model's own evaluator has no
  trial context at stamp time (§7). Seam: `Evaluator.Statistics` would be carried into that evaluator.
- **Design of experiments** — built as YA-14 (§17).

## 19. A vendor part's value and its own tolerance (brief-vendorlib-5)

A placed vendor library part is a cell emitted from its library's `.subckt`; its knob for the optimizer is
`<Instance>.Part`, the choice of part (`brief-vendorlib-4`). This section adds the part's **value** as a statistical
entry, drawn from the part's own tolerance.

**The tunable.** `TunableCatalog` offers, beside `C1.Part`, `C1.C` for a part whose header states a capacitance and
`C1.L` for an inductance — the spelling an ideal component's value has. Its kind is `TunableKind.PartValue`, its
nominal the stated value **in pF or nH** (the units the tolerance letters are written in), and `PartEntry` is the
library entry the value and tolerance are taken from. It is **statistical only**: `tune=1` or `opt=1` on it is
`tuning.part.value-statistical-only`. The Tuning and Optimizer add lists do not offer it; the Yield panel's does, and
its Yield toggle writes `dist=gauss tol=part sigmas=3 trunc=3` (brief D1).

**`tol=part`.** A spread value meaning the part's own tolerance. The reader keeps the word (`StatSpread.TolIsPart`),
so `CnlWriter` and the `.csch` JSON round-trip it byte for byte; it refuses it (`cnl.statistics.tol-part`, with the
line) on a key that is not an instance's `C` or `L`. Whether that instance IS a vendor part is not in a `.cnl` — a
netlist holds the cell an instance binds to, never the library part — so that half is `check`'s:
`yield.tol-part.not-a-part`. `ResolvedSpread.TolText` turns it into the text an explicit spread would be (`0.1 pF`,
`5%`) from the tunable's `PartEntry`, and every consumer of the spread goes through it, so `tol=part` FOLLOWS the
part: change the part and the resolved tolerance changes with no edit to the line. A part whose tolerance is not known
is `yield.tol-part.unknown`, naming the part, the reason, and the fix (write `tol=` explicitly).

**How a draw moves the model.** When the part's value entry is statistical, the extractor gives the part's emitted
cell — and every emitted candidate's — one parameter, `PartScale`, default 1, multiplied into every element of the
part's own quantity (every `C` of a capacitor's model, every `L` of an inductor's; `SpiceModelNetlist.ScaleQuantity`).
The other kinds' parasitics stay put, so the self-resonance moves as a real part's does. A trial's value for `C1.C`
is the drawn capacitance, as text, like any other value; `TunableOverrides.Apply`, handed the design's placed parts,
binds `PartScale` to drawn ÷ the stated value of whichever part the instance is bound to. A design whose part value
is not statistical gets no parameter, and its netlist is byte-identical to one written before this existed. Only the
part's own cell is scaled; a model that calls a further subcircuit of its own leaves that one's elements as written
(no static library measured does this).

**With the optimizer choosing the part.** The part's `Part` key is applied first, so the value key is bound against
the candidate the instance ends up as. At a moved design (`SampleValues.Nominals`) the value tunable takes the
candidate's stated value and `PartEntry`, so each candidate draws with its OWN tolerance. A candidate whose tolerance
is not known is left out of the candidate list and reported (`VendorPartTunable.NoTolerance`), as an unreadable one is.

## 20. Sensitivity at the nominal design (brief-sens-1)

A quick ranked answer to "what moves each goal", at the nominal design, before any Monte Carlo. `SensitivityRun`
(`src/Design/Statistics`) is the fourth client of the one evaluator (`OptimizationRun.ForEvaluation` /
`EvaluateValues`) — cache, parallelism and the not-re-entrant rule are its, never a second evaluation path.

### 20.1 Steps (D2, D3, R-sens1-1)

For k factors: the nominal (an empty value map — the design exactly as it holds itself), then each factor at + and −
with every other value at nominal: **2k + 1 evaluations**, deduplicated through the cache, no random draw, no seed.
`factors=all` (the default) takes every tunable `TunableCatalog.FromNetlist` offers; `stat` the `stat=1` entries; `opt`
the `opt=1` entries. A factor with a tolerance steps **±kσ through its own distribution** (`sigma=k`, default 1) by
`SigmaLevels` — the helper DOE's `levels=sigma:k` uses, truncation clip included, so a DOE level and a sensitivity step
are the same value for the same k. Any other factor steps **±`step` % of its value** (default 1 %); a phase ±1°. A
complex value contributes the parts that carry a tolerance, or else its `mag(…)` and `phase(…)` parts — never the
whole, never both coordinate systems. Under `factors=all`, two kinds of value are the measurement's setup rather than
the design's and are left out, each kind named in one note: **a port's values** (its reference impedance) unless
`ports=on`, and **a tone source's frequency** (`Freq…` of `V_1Tone`, `V_nTone`, `I_1Tone`, `I_nTone`, `P1Tone`,
`PnTone`, `Fc` of `MOD_Source`) — harmonic balance needs a source on its analysis's tone grid, so a step of the source
alone could never evaluate. Either is stepped when an `opt=1` or `stat=1` line names it. Also left out, each named once
in the notes (`yield.sens.left-out`): a vendor part's
choice of part, a part's value with no tolerance, a whole-number value, a value a sweep sets, a value of 0 (a percent
of it moves nothing). Every contributor states its **basis** (`σ`, `2σ`, `1 %`, `1°`).

Factors move alone (`yield.sens.uncorrelated` when the bench declares correlations) and a kit's distribution calls are
not factors (`yield.sens.kit-left-out` names their streams) — D8, as DOE §17.1.

### 20.2 The metric and the ranking (D4–D6)

Per goal g the metric is its margin at the tightest point divided by `GoalResiduals.Scale(g)` — the normalization
centering uses (§15.2) — so a goal in dB and one in volts rank on one footing. Δ = (m⁺ − m⁻)/2; contributors ranked by
|Δ|; share = Δ² / ΣΔ²; spread σ_g = √ΣΔ². **A pass estimate Φ(m₀/σ_g) is given only when every contributor's basis is
σ**; with a `%` or `°` step in the sum the spread is what a designer could change, not what production will do, and
`yield.sens.no-estimate` says so once. It is labelled *estimated*: the Monte Carlo is the yield.

asymmetry = |(m⁺ − m₀) + (m⁻ − m₀)| / max(|m⁺ − m₀|, |m⁻ − m₀|); above 0.5 the contributor is `nonlinear` — a goal over a
sweep whose tightest point moves between the steps, or a value near a resonance. **A goal whose nominal sits at the
centre of an `in` band is the extreme case**: the margin is the distance to the nearer edge, so it peaks at the centre,
both steps cost margin, Δ is near zero and every factor is flagged — the flag is the honest answer there, and a Monte
Carlo or `doe design=ccf` is the tool. The same holds for a passband goal over ripple (the shipped Bandpass example
flags every factor on its passband spec and none on its stopband one).

A step that does not evaluate leaves its contributor `failed` with the evaluator's sentence and the rest still rank; one
side alone gives a forward or backward difference, `one-sided`. The nominal must evaluate — otherwise exit 2 with its
own failure. Every real scalar `measure` is ranked too, Δ in its own unit, never against the goals. With
`responses=goals` the goals are the enabled `use=yield|both` ones, else the `use=opt|both` ones; `responses=all` every
enabled goal; `--goals` exactly the named ones. A bench with no goal and no measure is `yield.sens.no-response`; one
whose only measures are curves over a sweep (every measure of the shipped Power Amplifier example is over `Pin`) is
`yield.sens.no-scalar-response` after the nominal, naming a goal over the range as the way to rank it.

Every analysis the goals name runs as it would for the optimizer — harmonic balance under a parametric sweep included.
On the Power Amplifier example with a Pout and a PAE goal over `Pin` 28–30 dBm, 25 factors cost 51 swept HB
evaluations, about a second.

### 20.3 The result, the line, the verb (R-sens1-4 … R-sens1-7)

`<design>.sens.npy` beside the design, group `sens` — layout in `results-dataset-layout.md` §"Sensitivity". Its
`contrib:<goal>`, `:shown` and `:cumulative` cubes are the shape `ContributionParetoPreset` draws once given the group
(`Build(…, group: "sens")`); a sensitivity result is never re-ranked by it. The file is the only write.

`sensitivity [factors=all|stat|opt] [step=<pct>%] [sigma=<k>] [ports=on|off] [snp=on|off] [snpexclude=<inst>,…] [snpdb=<dB>]
[snpdeg=<deg>] [responses=goals|all] [parallel=<n>]` — `SensitivitySettings` on `TuningSetup.Sensitivity` and
`CschTuning.Sensitivity`; defaults are never held or written, a malformed value is `cnl.sensitivity.value-invalid`, a
second line `cnl.sensitivity.duplicate`. The four `snp*` keys are §20.4's.
`check` asks `SensitivityRun.Create` when the line is present; `explain --analysis` reports the factors, their bases,
the evaluations and how many run at once. `circuitrf yield sens <path>` (`cli.md` §25.11), MCP `run analysis=sensitivity`
(`result.sensitivity`). Nothing computes a sensitivity unasked (D7).

Gates (`tests/Ui.Tests/Statistics/SensitivityRunTests.cs`): `SensitivityRunTests` (a divider whose nominal sits below
its band so the margin is linear: R2 ranks first, Δ is the closed-form central difference to 1e-6, shares sum to 1;
2k + 1 evaluations and none on a second run; an estimate only when every basis is σ; a series LC whose tightest band
edge swaps between the steps is `nonlinear`), `SensitivityPortsTests` (ports out by default, in with `ports=on`),
`SensitivityHarmonicBalanceTests` (an HB harmonic goal ranks; the tone frequency is left out),
`SensitivityLineTests`, `YieldSensCliTests` (the verb's `.sens.npy` is the in-process run's bytes, its `--json` the
run's ranking).

### 20.4 Every S-parameter of every SnP block (brief-sens-2)

**The parameters.** An SnP instance takes, besides its file and interpolation settings, `SdB_<i>_<j>` (an offset in dB
on |S(i,j)|), `Sdeg_<i>_<j>` (an offset in degrees on ∠S(i,j)), both default 0, and `Reciprocal=auto|tied|untied`
(default `auto`). Ports are 1-based and always separated by `_` (`SdB_10_1`, never `S101`). No default is written. `dB`
and `deg` are ordinary units on these values and on `tune` lines; a phase offset is read with `deg` as its site unit,
so a bare `10` and `10 deg` are both ten degrees. A port outside `NumPorts` is refused at elaboration, naming the
instance and its port count — which is how `check` reports it.

**One function applies them** — `SnpPerturbation.Apply(S, offsets, hz)` (`src/RfCore`):
S′(i,j) = S(i,j) · 10^(δdB/20) · e^{j·δdeg·π/180}, after interpolation, uniform over the band (a frequency-local offset
is not modelled). At 0 Hz only the magnitude applies: a DC S-parameter is real. With every offset 0 the matrix comes
back with its own storage — no copy, no cost, and no existing result moves by a bit (the Hero 1 golden runs
unchanged). `SnpModel.Stamp` applies it; `SnpModel.ActivityFor` measures the PERTURBED matrix on the file's grid, so a
step that makes a passive file active is refused by the analyses that refuse an active block, for that point, by
name.

**The audit of every other reader of SnP data** (each either sees the offsets, reads no S, or refuses by name):

| Reader | Verdict |
|---|---|
| `SParameterEngine`, `NonlinearDcEngine`, `HbEngine`, `HbLinearExtractor` | read S only through `Stamp`; otherwise `PortBranchIndices` (port bookkeeping) |
| `ChainModel`, `SystemBlockPassivity`, `ComposedModelCache` | read no SnP data (comments name `SnpModel` as the pattern they follow) |
| `SmithCascade` | reads its own Smith elements' Touchstone files — an element carries no offsets |
| `EnvelopeSnpFit` | fits the FILE: refuses a block with a nonzero offset — "SnP 'SNP3''s S-parameter offsets are not applied by the envelope's rational fit" |
| `FieldCosimulation` | its port fields are the file's own structure's: refuses, by name |
| `ThermalCircuitLink` | its losses are the 3D view's own structure's: refuses, by name |

**Reciprocity is decided by the file.** `RFNetwork.Reciprocity(SNP)` is the worst |S(i,j) − S(j,i)| over the file's
own points and every pair, judged in absolute linear terms (a −40 dB entry 3 dB apart differs by 0.003). `auto` ties
when it is ≤ `SnpPerturbation.ReciprocalTolerance` = 0.01 — measured over all 166 Touchstone files in `examples/` and
`testdata/`: every passive one at or below 5.5e-3, every active one at 0.117 and above (`src/RfCore/RESOLVED.md`).
**Tied:** only the lower triangle (i ≥ j) is offered and one offset moves S(i,j) and S(j,i); an upper-triangle key on a
tied block is refused naming the lower-triangle key and `Reciprocal=untied`. **Untied:** all N² entries. A stated
`tied` on a file that is not reciprocal is allowed and `check` warns once (`snp.tied-not-reciprocal`), with the worst
difference in dB and where.

**The catalog.** `TunableKind.SnpEntry`: per SnP instance whose file loads (through `TouchstoneCache`), its keys per the
tie — unit `dB`/`deg`, value 0, `IsDefault`. `Tunable.SnpEntry` carries
the instance, i, j, the part and the tie state with its reason. A file that does not load leaves every key it would
offer `notOffered` with the load's sentence. Inside a sub-cell the key lives in the cell (`DUT:SNP3.SdB_3_1`) and moves
every instance. `TunableOverrides` writes the key as an instance override (a trial's or a step's value). **Labels** are `TunableKey.SnpEntryLabel` —
`SNP3 · S31 dB`, `SNP3 · S31 phase`, `S(10,1)` above 9 ports — everywhere a key is shown; the key stays the identity.
`explain --tunables` lists each block: `SNP3: 4 ports, tied (auto: worst |S(i,j) − S(j,i)| = 0.0021), 20 entries`.
**Sensitivity and yield only — never tuned or optimized.** An offset is one number at every frequency: right for "how
much does this block's S21 matter" and for a tolerance on measured data, but an optimum of it is no design anyone could
build. So `tune=1`/`opt=1` on one is refused (`tuning.snp.statistical-only`, by the validator `check`, the Tuning
window and the optimizer share), the Tuning and Optimizer Add… lists do not offer them, and the Inspector's and the
canvas's Tune are absent on such a row (its Tolerance stays). The Yield panel's Add… list shows one collapsed row per
instance; a double-click opens it, adding it adds every entry.

**Steps.** An entry with no tolerance steps ±`snpdb` dB / ±`snpdeg` ° (defaults 0.1 and 1), basis `0.1 dB` / `1°`; one
with a tolerance (`tune SNP3.SdB_3_1 dist=gauss sd=0.15 dB`) steps ±kσ like any other. `factors=all` includes them.

**Leaving blocks out.** `snp=off` (`--snp off`) leaves every SnP entry out and keeps the `snpexclude` list, unused.
`snpexclude=SNP4,DUT:SNP4` (`--snp-exclude`, replacing the list for one run) leaves those instances out, named by their
keys' owner prefix. Exclusion wins over a tolerance (`yield.sens.snp-excluded` counts the toleranced entries not
stepped) and is a sensitivity setting only. The result carries `excluded` (instance and factor count) in the data
(`sens.excluded`), the JSON and one line after the table, so a block left out is never read as one found insensitive.
A name that is no SnP instance is a warning (`yield.sens.snp-exclude-unknown`), ignored. Because each factor moves
alone, leaving entries out changes no component's Δ: it renormalizes the shares over what is left and saves two
simulations per entry.

**Roll-ups** from the same Δs, no simulation: **entry** (`SNP3 · S31`: Δ = √(Δ_dB² + Δ_deg²), share = the sum; any other
key is its own entry), **port** (`SNP3 · port 3`: every entry in row or column 3 — port shares OVERLAP, so there is no
cumulative cube and the JSON says `overlapping: true`), **instance** (`SNP3`, `R1` for `R1.R`, a variable itself — with
its factor count, so a 20-factor block and a 1-factor resistor rank honestly). Cubes
`contrib:<r>:by=entry|port|instance` (+ `:cumulative` for entry and instance) and `factors:<r>:by=instance`; JSON
`entries[]`, `ports[]`, `instances[]` per goal and measure; the text table takes `--by key|entry|port|instance`
(default `entry`, which reads as the key table when no SnP is ranked).

Gates: `SnpPerturbationTests`, `SnpReciprocityTests` (`tests/RfCore.Tests`); `SnpEntryTunableTests` (12 keys tied, 18
untied; the upper-triangle refusal; `SdB_2_1=1` moves a through pad's |S21| by 1 dB to 1e-9);
`SensitivityRollupTests`, `SnpExcludeTests` (`tests/Ui.Tests/Statistics/SensitivitySnpTests.cs`).
