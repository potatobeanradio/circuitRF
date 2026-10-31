# Tuning and optimization

**Status:** the series is built, TO-1 … TO-12 (`docs/sonnet-briefs/brief-tuneopt-*.md`): the model and
file format, the evaluation service, the live session and Data Display, the Tuning panel, presets, the
optimizer core, every algorithm on the menu, goal functions and "Add as goal", the Optimizer panel, the
`opt` verb and the MCP run, and the user pages with the `Optimization` example. The overview brief
(`brief-tuneopt-0-overview.md`) holds the decisions D1–D18 in full; §17 says how each was built, and
this note is the standing reference for what exists.

---

## 1. What exists

| Piece | Where | What it is |
|---|---|---|
| The model | `src/Core/Design/TuningSetup.cs` | `TuningSetup` = `TunableEntry` list + `TuningPreset` list + `OptimizationGoal` list + `OptimizerSettings`. `TunableKey` splits a key. |
| Algorithm registry | `src/Core/Design/OptimizerAlgorithms.cs` | The one list of algorithms: id, menu label, use-when sentence, options with defaults, accepted cost forms, whether it differentiates (§11). |
| `.cnl` grammar | `src/Core/Netlist/AnalysisDirectiveSchema.Tuning.cs` | The four directives, their keys and bare words — the table the reader checks keys against and the `reference` topics print. |
| `.cnl` read/write | `src/Core/Netlist/TuningDirectiveText.cs` | Reader and writer of `tune`, `preset`, `goal`, `optimize`. Called by `CnlReader` and `CnlWriter`. |
| `.csch` block | `SchematicPersistence` (`CschTuning`) | The `"Tuning"` object; see `project-file-formats.md`. |
| Extraction | `NetExtractor.Extract` / `NetlistSchematic.Build` | Copy the setup across in both directions, so every run verb's `.cnl` carries it with no extra code. |
| Tunable catalog | `src/Design/Optimization/TunableCatalog.cs` | Every tunable of a schematic at any depth, and the setup keys that name nothing. |
| In-memory overrides | `src/Design/Optimization/TunableOverrides.cs` | The design to elaborate with tuned values substituted. Writes nothing. |
| Rules | `src/Design/Optimization/TuningValidator.cs` + `TuningDiagnostics.cs` | What `check` reports and the windows refuse on. |
| CLI | `check`, `explain --tunables`, `reference tuning`, `reference goals`, `reference optimizers` | See `cli.md`. |
| Optimizer algorithms | `src/Engine/Optimization/` | Ask/tell state machines over the unit box: `RandomSearch`, `NelderMead`, `LevenbergMarquardt`, `BfgsB` (§10); `Minimax`, `TrustRegionModel`, `PatternSearch`, `DifferentialEvolution`, `ParticleSwarm`, `CmaEs` (§11). `OptimizerFactory` builds one from its id. |
| Optimizer run | `src/Design/Optimization/OptimizationRun.cs` + `OptimizationVariables`, `GoalResiduals`, `OptimizationDiagnostics` | Decode, goals → residuals, cache, parallel batches, stopping, pause, progress, history (§10). |
| Goal templates | `src/Design/Optimization/GoalTemplates.cs` | The catalog the goal editor lists: S-parameter, WSProbe, measurement and custom templates (§13). |
| Trace → goal | `src/Design/Optimization/TraceToGoal.cs` + `src/Render/DataDisplay/TraceGoalReader.cs` | "Add as goal" on a Data Display trace (§13). |
| Tuning panel | `src/Ui/Tuning/` + `src/Ui/Views/Tuning/` | Sliders, ranges, Push, presets, the live session (§8, §9). |
| Optimizer panel | `src/Ui/Optimization/` + `src/Ui/Views/Optimization/` | Variables, goals and the goal editor, run/pause/stop, keeping the result (§15). |
| `opt` verb, MCP | `src/Cli/Optimize.cs` | The headless run (§14; `cli.md` §24). |
| Example | `examples/Optimization/` | Three benches: an L-section, a minimax bandpass filter, an amplifier with a complex source impedance and a railed range (§16). |
| User pages | `docs/user/src/reference/tuning.md`, `optimization.md`, `cli.md#opt` | With two captured figures (`DocTuningFixtures`) and the algorithm table generated from the registry (`{{table: optimizers}}`, §16). |

**Why the model is in `src/Core` and not `src/Design/Optimization`.** The brief placed the model classes
in `src/Design/Optimization`. A `TestBench` carries the setup and the `.cnl` reader and writer live in
`src/Core`, which cannot reference `src/Design` — so the plain data sits beside `Analysis` and
`Measurement`, and everything that needs a schematic, a resolver or a workspace is in
`src/Design/Optimization`, as the overview's D12 has it.

## 2. What is tunable (D1, D2)

A tunable is a **component parameter** whose stored value is a plain number with an optional unit
(`47`, `47 pF`, `47pF`, `1.2e-9`), or a **VAR row** whose expression is one. Anything else — an
expression, a reference to a cell parameter, a string, an enum, a bool — is **not offered at all**. The
way to tune an expression is to tune the variable it reads.

- **A sub-circuit instance's declared cell parameters are offered per instance** (`X1.Rbias`,
  `X2.Rbias`), whether the instance overrides them or inherits the default. An inherited one is reported
  `IsDefault = true` at the cell's default, so Push knows to add an override.
- **A value inside a cell is identified by the cell DEFINITION** (`DUT:R3.R`): tuning it moves it in every
  instance, and the catalog reports how many (`DUT · ×2`).
- **Only values the drawing holds.** Extraction adds parameters a drawing never stated — a microstrip's
  substrate (`H`, `T`, `Er`, …) comes from the workspace technology. Those are not values of the design and
  Push has no row to write them into, so the catalog offers an instance parameter only when the component
  in the drawing holds it. A `.cnl` holds every value it states.
- **Integer parameters** (`m`, `Nf`, `NumFingers`, `Fingers`) are offered and flagged `IsInteger`.
  Identity numbers (`Num`, `NumPorts`, `NumFreqs`) are not offered.
- A VAR that an **enabled parametric sweep** sweeps is offered with `DisabledReason = "swept by SW1"`.
- **Read-only** (`ReadOnlyReason`): a cell from a kit's netlist rather than a schematic, a cell outside
  the workspace, or a cell whose schematic file is read-only. Such a value can still be tuned and
  optimized; Push skips it.

**The catalog walks the extracted netlist, not the drawing.** `TunableCatalog.Discover(schematic,
resolver, workspaceRoot)` extracts once with the caller's resolver — the one Simulate uses — so cell names
in keys are spelled exactly as the `.cnl` spells instance types (a second `Amp` from another workspace is
`Amp_2` in both), and there is no second resolver to disagree with the first.
`NetExtractor.ExtractionResult.CellKeys` maps each library name back to its cell folder for the read-only
test.

**Default range on first activation (D4):** a positive value v → [v/2, 2v]; a negative one → [1.5v, 0.5v];
zero → [0, 1] in the parameter's unit, flagged `RangeGuessed`. `auto` scale is log when min > 0 and
max/min ≥ 10.

## 2a. Complex values (D18)

A value written as a **complex literal** — numbers only: `40+15j`, `50-j10`, `-3j`, `complex(40,15)` or
`polar(42.7,20.6)`, with an optional unit after the whole — is tuned and optimized **by its parts**,
never whole. Anything that reads a name (`4+j*X`) is an expression and is not offered; tune `X`. A plain
real number stays an ordinary tunable; to tune the imaginary part of `50`, write it `50+0j`.
`ComplexValue.TryParse` (in `src/Design/Optimization/ComplexValue.cs`) is the one test.

- **Four parts, four keys:** `real(K)`, `imag(K)`, `mag(K)`, `phase(K)`, where `K` is the value's own D3
  key. The words are the expression engine's own functions; the phase is in **degrees** (`deg`). Each part
  is an entry of its own — its own `tune`/`opt` flags and range — and **any combination is allowed**:
  real with imaginary, magnitude with phase, or real with magnitude of the same value.
- **One value, four views.** The session holds one complex number per value; every part row shows its
  own view of it. **Moving a part holds its partner in the same coordinate system** — real holds
  imaginary, imaginary holds real, magnitude holds phase, phase holds magnitude — so each slider is one
  straight path in the plane whatever else of the value is tuned (`ComplexValue.With`).
- **The ranges of all of a value's parts always hold, together** (`ComplexRegion`). Every entry of a
  value constrains it, whatever its flags and in either window: a range belongs to the entry, not to a
  window. A part's move that would leave the region **stops at the first edge it meets**. A range edit
  — typed, re-centred, reset, or a first activation's default — that leaves **no** complex value inside
  every range of that value is **refused**, with a sentence naming the other ranges, and `check` reports
  the same condition in a hand-written file as an error (`tuning.range.complex-disjoint`). The emptiness
  test is exact: the plane is cut into ≤ 90° wedges; in each, the real/imaginary box and the wedge make a
  convex polygon over which the magnitude spans [nearest, farthest], which either meets the magnitude
  range or does not.
- **Default ranges:** real, imaginary and magnitude follow D4 (to six significant figures); a phase gets
  [φ − 90°, φ + 90°] and is **linear** (an angle's range is not a ratio; `auto` reads as `lin` on a phase).
  A phase range wider than 360° is an error.
- **A complex value is written whole.** The session request, a preset and Push all carry the WHOLE
  value under its own key `K` (`Zsrc=45+10j Ohm`), **in the form the schematic wrote it** — rectangular
  stays rectangular, `complex(…)` and `polar(…)` stay calls — so a pushed value reads as a typed one.
  `TunableOverrides.Apply` also accepts part keys for a headless caller and composes them with the
  design's value by `ComplexValue.Compose`, the optimizer's own rule (below).
- **Optimizing (TO-6):** each opt-enabled part is one coordinate. One part: its partner is held at the
  start. Two parts of one system set the value directly; a **mixed pair** (real with magnitude, say) is
  solved geometrically, the free sign taken from the start (`Compose`). A decoded point no complex value
  satisfies, or one outside any range of the value, is **infeasible**: not simulated, ranked worse than
  every feasible point. More than two opt-enabled parts of one value is a refusal at Run — a complex
  value has two degrees of freedom — while the others' ranges still limit it.

## 3. Keys (D3)

| Key | Names |
|---|---|
| `R1.R` | parameter `R` of top-level instance `R1` |
| `Wline` | top-level VAR `Wline` (by name — it survives moving the row to another VAR block) |
| `X1.Rbias` | declared cell parameter `Rbias` of top-level sub-circuit instance `X1` |
| `DUT:R3.R` | parameter `R` of `R3` inside cell `DUT` — every instance of `DUT` |
| `DUT:Wline` | VAR `Wline` inside cell `DUT` |
| `DUT:X5.Wf` | declared parameter `Wf` of instance `X5` inside cell `DUT` |
| `mag(Zsrc)` | the magnitude of the complex VAR `Zsrc` — likewise `real(…)`, `imag(…)`, `phase(…)` (D18) |
| `phase(DUT:X5.ZL)` | the phase, in degrees, of a complex parameter inside cell `DUT` |

The cell part is the cell **as the `.cnl` spells its instance type** — `CellScope.NameFor` in
`NetExtractor`: the cell folder's leaf name, or `name_2`, `name_3` … when two cells with one leaf name
meet in one design. **A key that names nothing is never an error**: a preset skips it, `check` warns, a
run notes it.

## 4. The `.cnl` grammar

The authoritative table is `AnalysisDirectiveSchema.TuningDirectives`; `circuitrf reference tuning` and
`reference goals` print it.

```
tune <key> [min=<v> [unit]] [max=<v> [unit]] [scale=auto|lin|log] [step=<v> [unit]]
           [discrete=none|integer|preferred] [tune=1] [opt=1]
preset "<name>" [created=<yyyy-MM-ddTHH:mm:ssZ>] [lasttuned=1] [cost=<c>] <key>=<v> [unit] ...
goal <Name> = <expression> [analysis=<A>] [over=<axis> lo=<v> [unit] hi=<v> [unit]]
     (le|ge|eq) <limit> [unit] [to <limit> [unit]]  |  (in|out) <a> [unit] <b> [unit]
     [weight=<w>] [scale=<v> [unit]] [enabled=false]
optimize [algorithm=<id>] [maxiter=<n>] [maxevals=<n>] [timelimit=<v> s|ms|min|h]
         [cost=lsq|minimax] [analyses=goals|all] [seed=<n>] [parallel=<n>] [alg.<option>=<v>] ...
```

Settled details:
- **Units** follow a value after a space, as `measure` and instance lines write them. A value that would
  not read back as itself is written quoted.
- **A goal's expression** runs from after `=` to the first token, at bracket depth zero, that the grammar
  owns (a `key=` or a goal type), searched from the second token. The writer quotes the expression when
  that rule would end it early (`goal X = "Lout - out" ge 0`). Its own spacing is kept verbatim.
- **Sloped limits** are `le -15 to -10`: the limit at `lo`, `to`, the limit at `hi`. Only `le`/`ge`/`eq`
  slope; `in`/`out` take a band of two limits.
- **Inside a goal line a length in inches is spelled `inch`**, because `in` is a goal type.
- **`timelimit`** takes `s`, `ms`, `min` or `h`. These are local to this grammar: the shared unit table
  has no time units, and adding a bare `s` there would change what a bare word after an instance-line value
  means.
- **`created`/`lasttuned`/`cost` come before a preset's values**; once a value key is seen, every later
  `key=value` is a value. (So a preset whose FIRST value is a VAR named `created`, `lasttuned` or `cost`
  does not round-trip — the same limit `created` always had.)
- **Booleans** read `1/0/true/false/yes/no`; the writer writes `1` and omits defaults.
- **Unknown keys** are a warning naming the key (`cnl.tuning.unknown-key`) and are **kept** in the
  record's `Extra` map and written back — the room yield's tolerances will need (overview §5).
- **A malformed line is refused** with a `cnl.tuning.*` diagnostic (`TuningDirectiveDiagnostics`) and its
  line number.
- **Byte stability:** `CnlWriter → CnlReader → CnlWriter` and `.csch → .cnl → .csch → .cnl` reproduce the
  tuning lines exactly. `TuningPreset.Created` is held to the whole second, the precision the `.cnl`
  spelling carries.

## 5. Applying values in memory

`TunableOverrides.Apply(tb, lib, values, setVariables)` returns copies of the testbench and library with
each value substituted — a top-level instance parameter or VAR in the testbench, a cell's in its library
definition, an inherited cell parameter as an added override. Objects are replaced, never mutated, so the
input keeps its own. A value written without a unit keeps the row's existing unit (typing `47` into a row
whose unit column says pF means 47 pF). **The gate is equality with typing**: elaborating the result equals
elaborating a design in which the same text had been typed.

`--set` applies first; a tuned value naming a variable `--set` also sets is a refusal naming both.

## 6. What `check` reports

Errors: min ≥ max; `scale=log` with min ≤ 0; a bound, limit or range end that is not a number; a goal
whose analysis is not declared; a goal range that holds no point of the analysis's grid (computed for
`freq` of an S-parameter sweep, also through a wrapping parametric sweep, and for a sweep's own variable;
other axes are checked at run time); an expression that does not parse; an `in`/`out` goal without two
limits, or whose two limits are equal (two limits, or a range's two ends, given high-first are read
low-first — a sloped limit keeps the end it was written against); a sloped limit with no range; a key naming a value D1 does not offer
(a complex value named whole, or a part of a value that is not complex, included); a phase range wider
than 360°; ranges of one complex value's parts that leave no value inside all of them (D18);
`discrete=integer|preferred` on a part of a complex value, and `discrete=preferred` on a value whose unit
names no ladder (§12); an unknown algorithm id; a `timelimit` that is not a duration. **Warning:** a key — in a `tune` line or a
preset — that names nothing. An unknown function name in a goal expression parses, exactly as in a
`measure` line; it is reported where `measure` reports one.

## 7. Room left for yield

A variable entry, a goal and the optimizer settings each carry an `Extra` map, and both serializations
keep unknown keys, so a tolerance can be added to a `tune` line without a format break.

## 8. The Tuning panel (TO-4)

| Piece | Where |
|---|---|
| Panel state | `src/Ui/Tuning/TuningPanelViewModel.cs` (+ `TuningRowViewModel`, `TuningAddViewModel`, `TuningScopeViewModel`) |
| Dock + view | `TuningTool`, `DockPanelIds.Tuning`, `src/Ui/Views/Tuning/TuningToolView.axaml` |
| Shell wiring | `WorkspaceViewModel.Tuning.cs` — routing, session creation, sub-cell sessions, reveal |
| Slider maths | `TuningSliderMapping` (log/lin position, integer and step snap, keyboard nudges) |
| Entry edits | `src/Design/Optimization/TuningSetupEdits.cs` (pure) → `SetTuningSetupCommand` (one undo step) |
| Push | `TuningPush` → one `CommandBatch` per owning document |
| Canvas colour | `SchematicViewModel.TunedLabels` → `SchematicOverlay.TunedLabels` → `SchematicRenderTheme.TunedText` |

- **The tuned schematic is the focused tab's TOP frame**, so pushing into a sub-cell keeps tuning the
  bench. A non-schematic document clears the panel and stops the session — except a Data Display while a
  session runs, because that is where its results are watched.
- **One key mapping.** `TunableCatalog.KeysFor(drawing, component, parameter)` is what the Inspector toggle,
  the canvas's right-click ▸ Tune and the panel agree on — one key for a plain number, four part keys for
  a complex value, which the Inspector and the canvas offer as a menu of checkable parts; `TunableCatalog.Drawings` gives each scope's
  drawing — the open session's own model in the GUI, so Push edits what is on screen.
- **Discovery is lazy.** A schematic with nothing tuned never extracts on an edit; the catalog is computed
  when a row, the Add… list or the Inspector asks.
- **The session evaluates what Simulate would**: the same `NetExtractor.Extract` with the workspace
  resolver and corner bindings, written by `CnlWriter` and read back in memory (`PreparedCircuit.FromText`)
  rather than through `netlist.cnl`, which Simulate owns.
- **Push** writes `G15` numbers with the row's own unit spelling where it means the same unit (a complex
  value whole, in the form the schematic wrote it, D18); an
  instance inheriting a cell default gains the override (`AddParameterCommand`). A sub-cell session with no
  tab is opened with `CircuitRfDockFactory.OpenDocumentInBackground`.
- **A layout saved before TO-4** gains the panel beside wherever it put Analyses
  (`DockLayoutDefaults.WithMissingPanelsFilled`'s `TabbedBehind` rule), not at the default column.

## 9. Presets (TO-5)

| Piece | Where |
|---|---|
| Recall | `src/Design/Optimization/PresetRecall.cs` — `Apply(preset, catalog, setup)` → values to load, a per-key report, and the setup with recalled keys tune-enabled and ranges widened |
| Edits | `src/Design/Optimization/TuningPresets.cs` — `LockIn`, `WithLastTuned`, `Rename`/`Duplicate`/`Delete` (by INDEX: a hand-written file may repeat a name), `Compare`, `CnlText` |
| `.cnl` line | `TuningDirectiveText.WritePreset` / `ReadPresetLine` — "Copy as .cnl" and its inverse |
| Panel | `TuningPanelViewModel.Presets.cs` + `TuningPresetItemViewModel`; the drop-down and Compare table in `TuningToolView.axaml` |
| Last tuned | `TuningPanelViewModel.StoreLastTuned`, called by `WorkspaceViewModel.StoreLastTuned` from every schematic save and close path |

- **Recall never refuses.** Each key is *applied*, *widened* (outside its entry's range: applied, and the
  bound moved to the value), *missing* (names nothing), *no longer tunable* (now an expression, a value
  that is not complex any more, or swept), or *no longer applicable* (not a number; complex parts no value
  has). The report is one sentence (`Applied 11 of 12 · R7 no longer exists`) plus one line per key.
- **Recall loads; it does not push.** Values go into the panel (and the running session). The only document
  edit is the setup's: a recalled key that was not tune-enabled is turned on — a complex value with no tuned
  part gets real + imaginary, or magnitude + phase when written `polar(…)` — and widened ranges. One undo
  step. Widening only grows a complex value's region, so it never makes a D18 conflict.
- **Lock in stores every tune-enabled row**, moved or not — the schematic's own text for an unmoved one, a
  complex value once and whole. `TuningPreset.Cost` is the Optimizer's (TO-10), null for a person's.
- **Last tuned** is written only when some row differs from the schematic, and not again when it already
  holds the same values (so a second save does not dirty the document). Writing it is an ordinary undoable
  setup edit, so closing a clean schematic with unpushed values asks to save. Renaming "Last tuned" makes it
  an ordinary preset; the next save writes a new one.
- **The drop-down** orders Last tuned first, then by `Created`, newest first. Compare takes the schematic,
  or the preset earlier in the file, as the first side; the difference is second − first, per tuned part for a complex
  value (all four when none is tuned).

## 10. The optimizer core (TO-6)

**Shape.** Every algorithm is an ask/tell state machine over the unit box [0, 1]ⁿ
(`IOptimizerAlgorithm`): `Ask` hands out a batch, `Tell` gives back a cost, a residual vector and a
failed flag per point. No algorithm calls the simulator. Each is written as ONE iterator whose locals
hold all of its state (`AskTellAlgorithm`), so `Capture` is the list of batches told and `Restore` is a
replay of them into a fresh instance, checked point for point — plain numbers a checkpoint can hold.
`SplitMix64` is the seeded generator: its output is fixed by the seed on every platform.

**Variables (`OptimizationVariables`).** Each opt-enabled entry is one coordinate: linear, or
logarithmic per its effective `Scale` (a phase is always linear). Integers and steps are applied when a
point is DECODED, so the cache and the history see the value simulated. A decoded value is written as
the parameter's value text (`G15`, its own unit); a complex value is composed WHOLE by
`ComplexValue.Compose` from its start and written in the schematic's form. An entry with no range uses
the catalog's default and says so. A start outside its range is moved to the nearest bound and
reported; a complex start outside its region is moved to the nearest point of the start's own
coordinate paths (`ComplexRegion.Move` from the feasible grid point nearest the start), reported.

**Goal residuals (`GoalResiduals`) — the rule users read.**
- *Violation per grid point*: `le` max(0, x − L); `ge` max(0, L − x); `eq` |x − L|; `in` [a, b] the
  distance to the interval; `out` [a, b] the distance to the nearer edge when inside, else 0. A sloped
  limit interpolates L linearly along the range axis.
- *Scale*: the goal's `scale=` when given; otherwise the band's width b − a for `in`/`out`, and
  **max(|L|, 1) in the limit's own unit** for the others (the larger end of a sloped limit). So −15 dB
  is worth 15 dB, a limit of 0.5 is worth 1, and 2 pF is worth 2 pF rather than 1 F.
- *Residual*: violation ÷ scale × weight ÷ √(points), so a 201-point goal and a scalar goal weigh alike.
- *Cost*: least squares Σ r² (default) or minimax max r over the concatenated residual vector.
- *Met*: a violation within 1e-9 of the goal's scale counts as zero. Least squares approaches a one-sided
  limit from the violating side and lands on it only to rounding (|S11| = 1e-4 + 5e-15 against
  `le 1e-4`), so without it "all goals met" would never fire on an inequality.
- A complex result, a range axis the value does not have, a range holding no grid point, a non-finite
  value: errors (`opt.goal.*`). A goal that cannot be scored at the START point is a refusal (exit 1),
  not a hundred failed evaluations.
- A goal with no analysis reads variables only and costs no simulation; when no goal names an analysis
  nothing is simulated at all.

**Failures and infeasible points.** A failed evaluation (status not Success, an analysis reporting
`Converged = false`, a goal error) costs **10·(1 + the largest successful cost seen)**, assigned after
the batch in batch order so concurrency cannot change it. An infeasible complex point is not simulated
and costs that plus its normalized distance to the region. Both reach the algorithm as `Failed`:
Levenberg–Marquardt, BFGS-B and Minimax shrink the step; the trust-region model leaves the point out of
its interpolation set; pattern search counts it a poll point that did not improve; Nelder–Mead, Random
and the population methods rank it — and a population method ranks it behind EVERY evaluated point,
whatever the two penalty costs say (§11).

**Counters.** `Evaluations` = simulations run; `CacheHits` = points the cache (keyed by the decoded
value vector) or an earlier point of the same batch answered; `Infeasible`; `Failures`.

**Parallel.** A batch's uncached points run concurrently up to `parallel=` (default cores − 1, capped by
the batch), only when `PreparedCircuit.IsReentrant`; otherwise one at a time, noted once
(`opt.parallel.serial`).

**Stopping (first that applies).** Every enabled goal met (best cost 0); the algorithm's own finish;
`maxiter` (default **100**); `maxevals` (simulations); `timelimit`; **stall** — the best cost improved by
less than `alg.stall_tol` (default 1e-9) of itself over `alg.stall_iters` (default 25) iterations; Stop.
Cancellation abandons the run (exit 130) and reports no best.

**Pause (D16).** `Pause` holds after the batch in flight; `Resume` continues. The run is identical,
evaluation for evaluation, to an unpaused one with the same seed.

**Outcome (D15).** `GoalsMet` 0 · `GoalsUnmet` 3 (each goal reported with its worst violation, where on
its axis, and the value there) · `Refused` 1 · `NoConvergence` 2 (no evaluation succeeded) ·
`Cancelled` 130.

**Progress (R-to6-9)** — `OptimizationProgress`, after every iteration: iteration, evaluations,
failures, infeasible, cache hits, elapsed, the iteration's best cost, the best cost, the best values,
per-goal worst violation and met at the best point, railed values, and the best point's `DataSet`.

**Railed (D17).** A coordinate within 0.5 % of the box of a bound; a complex value held within 0.5 %
of ANOTHER part's range is reported against that part (`RailedVariable.Against`).

**History (R-to6-10)** — group `opt`: over `eval` (every point told, in order) `cost`, `failed`,
`infeasible`, `cached` and one cube per value key in base SI (complex for a complex value); over
`iter` `best_cost` and `worst_<goal>`. The caller decides whether to write it.

**Algorithms** (`alg.<name>=` on the `optimize` line sets an option; every option and its default is in
the registry and printed by `reference optimizers` — not repeated here, so the two cannot drift):

| Id | Batch |
|---|---|
| `random` | `batch` points per iteration, the first carrying the start; Latin hypercube or uniform |
| `simplex` | 1 (n+1 to build, n to shrink) — Gao–Han adaptive coefficients, projection onto the box |
| `lm` | n for the forward-difference Jacobian, then 1 per trial |
| `bfgsb` | n for the gradient, then 1 per line-search point |

- **Levenberg–Marquardt uses Levenberg's damping (JᵀJ + λ·s·I), not Marquardt's diag(JᵀJ).** The unit
  box already puts the coordinates on one scale; Marquardt's scaling lengthens the step along the
  coordinate the residuals barely feel, and with one goal over two values it stalled 7 % short of a
  reachable limit. Bounds: a coordinate on a bound whose gradient points outward is held and left out
  of the solve. A failed difference point is retried on the other side. `cost=minimax` is refused for
  `lm` (`opt.algorithm.lsq-only`).
- **BFGS-B** is limited-memory BFGS on the free coordinates with a projected backtracking (Armijo)
  line search; a direction that is not downhill resets the memory to steepest descent.
- **The difference step is 1e-6 of the box.** 1e-4 left the gradient error larger than the gradient
  itself near a Rosenbrock optimum and BFGS-B stopped at cost 0.01. A discrete or stepped coordinate
  decodes both sides of a difference to one value (a cache hit) and so has no gradient — which is what
  snap-and-polish (§12) is for.
- **Auto** is TO-8's (§12). Algorithms not built are refused naming those that are.

## 11. Global and derivative-free algorithms, and the registry (TO-7)

**The registry (R-to7-7)** — `OptimizerAlgorithms.All`, in menu order (D13). Each entry: id, label,
use-when sentence, options (name, default text, summary), accepted cost forms, `NeedsGradients`. It is
in `src/Core` because the `.cnl` schema and `check` read the ids and Core references nothing above it.
**The algorithms read their option defaults FROM it**: `AskTellAlgorithm.Option(options, name)` takes
the value given, else the registry's numeric default, else the `auto` value the caller computes for n —
and reading an option the registry does not list throws. So a default printed by `reference optimizers`
is the default that runs. The stall options are `CommonOptions`. `OptimizerFactory.Create` is the one
id → algorithm switch; `OptimizationRun.Available` is the registry filtered by `OptimizerFactory.IsBuilt`
(every id on the menu is built since TO-8).

**Cost form.** A method accepting one form SETS it: choosing `minimax` runs minimax whatever `cost=`
says, because least squares is the default form and cannot be told apart from an unstated one. A
least-squares-only method (`lm`) with an explicit `cost=minimax` is refused — by `check` and by the run,
with the same sentence. `check` also warns on an `alg.` option the chosen algorithm does not take (not
for `auto`, whose options are those of what it runs).

**Feasibility-first ranking (R-to7-8).** `AskTellAlgorithm.Better`: an evaluated point always ranks
ahead of a failed or infeasible one; costs order only like with like. The run's penalty already ranks a
failure below every success seen SO FAR, but a later success can cost more than an earlier penalty —
DE's selection, PSO's personal best and CMA-ES's ranking must not keep a penalty over it.

| Id | Batch | Notes |
|---|---|---|
| `minimax` | n for the Jacobian (retried backward on failure), then 1 per trial | Trust-region SLP: min t s.t. rᵢ + Jᵢd ≤ t, ‖d‖∞ ≤ Δ, inside the box; dense simplex with Bland's rule, no first phase (t is written U − σ so every right-hand side is non-negative). Works on SIGNED residuals, so it levels the worst ones (equal ripple). |
| `trust_region` | 2n+1 initial (x₀ ± Δeᵢ, inward near a bound), then 1 | Least-change (minimum Frobenius norm of ΔH) quadratic model, solved as one (p+n+1)-square system in ρ-scaled coordinates; box-and-‖·‖∞ trust region solved by projected gradient on the model; two radii Δ ≥ ρ, ρ only falls, geometry point when a point is beyond 2ρ. |
| `pattern` | the 2n poll (OrthoMADS directions on the mesh, Δm = Δp²), plus a speculative and a model point | Poll points outside the box are not asked (extreme barrier). **Model search** — the minimum, within 2Δp, of the least-change quadratic through the nearest (n+1)(n+2)/2 evaluated points — shares `TrustRegionModel`'s model and step code. Without it, 4-D Rosenbrock stopped at 1.1e-3 from the optimum (default `minpoll`) or took ~69,000 evaluations (`minpoll` 1e-10); with it, 1,185. |
| `de` | one generation | L-SHADE: success-history CR/F (weighted Lehmer means, a terminal CR), current-to-pbest/1 with archive, binomial crossover, midpoint-to-parent repair, linear population reduction 18n → 4 over `budget`. **`budget` defaults to the run's `maxevals`** when stated (the run passes it), else 1000n. |
| `pso` | one swarm step | Constriction form (χ from c₁ + c₂), ring or global topology, velocity clamp, reflection at the bounds. Never finishes on its own until the swarm collapses; the run's limits end it. |
| `cmaes` | λ | (μ/μ_w, λ) with CSA and rank-one + rank-μ updates; eigen-decomposition by Jacobi each generation. **Bounds by re-sampling** (up to 100 draws, then projection), chosen over a penalty because it needs no penalty weight and every point evaluated is one the design can take. IPOP: a stalled run (tolfun, tolx, condition 1e14, generation limit) restarts at a uniform random mean with 2λ. |

**Gates, by evaluation count** (`tests/Engine.Tests/Optimization/GlobalAndDfoAlgorithmTests.cs`; seed 1):
4-D Rastrigin on [−4, 6]⁴ from the local minimum at x = 5 — DE reaches cost < 1e-4 at evaluation 3,782
(budget 4,000), CMA-ES 5,530, PSO 9,127. 4-D Rosenbrock from x = 0 — trust-region model 222
evaluations, pattern search 1,185, and pattern search with a fixed tenth of the box failing still
arrives. Minimax lands the minimax line through eᵗ on [0, 1], its two worst residuals level within 1e-6 at
0.10593. `AlgorithmRegistryTests` holds the registry, the build, the `optimize` schema's summary and
the `reference optimizers` page to each other; `PopulationComplexTests` runs DE on a `real`/`mag`
pair with infeasible points on both sides of the feasible band and ends feasible, reporting the count.

## 12. Bayesian, Discrete, preferred values, snap and polish, Auto, sensitivity (TO-8)

**Preferred values (R-to8-2).** The ladder arithmetic left `SmithPreferredValues` for a shared
`PreferredValues` (`src/Design/Matching`): the IEC 60063 series E12, E24 and E96, the snap (nearest by
RATIO), the bracket either side of a value, and the list text the editor reads and writes. The Smith Chart
calls it unchanged and still snaps only L and C (smith-chart.md §5.6a). A **resistor** ladder joined the
capacitor and inductor ones — shipped E24 over 1 Ω … 10 MΩ, E96 over the same span one button away in the
editor (the Smith Chart's Preferred Values dialog, now with a Resistors tab). The ladders stay per-USER
(`SmithPreferredValueStore`, `preferences.json`); a run takes them as an ARGUMENT
(`OptimizationOptions.Ladders`, `PreferredLadders`) — null is the shipped set, which is what a headless
run uses. `discrete=preferred` snaps by the parameter's unit: F → capacitors, H → inductors, Ω →
resistors. `TunableValue.DiscreteChoices` is the one statement of what a row offers: a part of a complex
value offers only `none` (a part is continuous — R-to8-7; `step=` stays allowed), and `preferred` is absent
where the unit has no ladder. `check` and the run refuse either in a hand-written line
(`opt.discrete.part`, `opt.discrete.no-ladder`), and a preferred entry whose range holds no rung is refused
(`opt.discrete.none-in-range`).

**Levels.** A coordinate that takes only listed values carries them (`OptimizationCoordinate.Levels`, in
its unit): an integer's integers, a step's grid (each at most 100,000), or the ladder's rungs inside the
range. Integers and steps are applied on every decode, as before; a preferred value only when the run
asks (`Decode(u, snapPreferred)`) — a continuous algorithm searches it continuously, and the run then
ends with the snap stage whatever the algorithm, so a preferred or E-series row never finishes between
rungs; Discrete keeps it on a rung throughout.

**Bayesian (`bayes`, R-to8-1).** `GaussianProcess`: zero mean on the standardized cost, Matérn 5/2 with one
length scale per variable, a noise variance; θ fitted by maximum likelihood with the analytic gradient and
a small projected BFGS, from the previous fit, a fixed start and (up to 100 points) two seeded ones; the
factor is NumFlat's Cholesky. Expected improvement (in logs, with an erfc accurate in relative terms so
z·Φ + φ stays meaningful far into the tail) maximized by 200n uniform candidates (≤ 2,000) plus
perturbations of the five best, the four best refined by compass search. The first batch is a Latin
hypercube of `initial` = 2n+1 points carrying the start. Batch = 1, or `parallel=` points when the setup
states one, by the constant liar. Above `tr_dims` = 10 variables the trust-region variant runs (a local
surrogate, a box around the best scaled by the length scales, doubled after 3 successes, halved after
max(4, n) failures, a fresh hypercube when it falls below 2⁻⁷). Failed and infeasible points are not
observations: the global variant takes no candidate within 0.02 of one; in the trust region they count
as failures. Only the `archive` (500) most recent points are fitted, the best always among them — a fit
is O(N³). **A log warp of the cost was measured and not kept**: at δ = 1e-3/1e-2/1e-1 of the spread it was
worse than the raw cost on Branin, a 2-D Rosenbrock and an L-section match (40 evaluations, six seeds).
Gate: 2-D Branin from the box centre, seed 1, within 1e-3 of 0.397887 in 40 evaluations (8.6e-6 measured).

**Discrete (`discrete`, R-to8-3).** Every optimized value must have levels; otherwise the run is refused
naming the continuous ones (`opt.discrete.continuous`), or saying that every value is a part
(`opt.discrete.only-parts`) — `OptimizationVariables.DiscreteUnavailable()` is the sentence the menu's
disabled entry shows. A grid of at most `cap` (2,000) points is searched exhaustively in blocks of a
twentieth, the start's point first; a larger one by coordinate-wise descent (every coordinate alone, 1, 2,
4 and 8 levels either side, one batch per iteration) with `restarts` (10) random restarts. The run's stall
rule applies to it as to any algorithm.

**Snap and polish (R-to8-4).** `OptimizationOptions.SnapAndPolish` (the CLI's flag) runs it after the
algorithm, and every run with a preferred or E-series value runs it without being asked;
`OptimizationRun.SnapAndPolish()` (the Optimizer's action) runs it on a finished run, its limits
counted afresh and without the time limit. Each integer, stepped and preferred value goes to the legal
values either side of where the continuous run left it (`PreferredValues.Bracket`); with k ≤ 6 of them all
2^k combinations are evaluated as one batch, else the nearest only. **From then on the run's best is a
snapped point** — the continuous one is not a design anyone can build. The continuous values (parts of
complex values included) are then re-optimized from the best snapped point with the snapped ones held —
Levenberg–Marquardt, or Minimax under `cost=minimax` — over the sub-box of those coordinates.
`OptimizationResult.Snap` holds the cost before, at the snap and after the polish, and a note says the same
(`opt.snap.report`). The snap runs after any end but Stop, a goals-met continuous point included — and
after a Stop too when any variable is on a standard series (E6…E96 or `preferred`), whose continuous best
is not a value the row allows; the panel keeps no paused best that lies between rungs
(`OptimizationRun.BestOffSeries`). Every algorithm whose `NeedsGradients` is false — except the trust-region
model, which fits a smooth surface and stalls on a staircase — and Auto's CMA-ES stage search a series row ON
its series (`SearchesOnSeries`: the snapped decode throughout, as Discrete's), so for them the snap finds the
point already on its rungs and only polishes. The polish runs only within
the limits, and never after a Stop. Gate: an L-section optimized continuously, snapped to E24 C and E12 L, ends
on the pair the closed-form cost ranks best of its four neighbours.

**Auto (R-to8-5).** CMA-ES for 50(n + 1) evaluations, or half of `maxevals` when that is fewer; then
Levenberg–Marquardt (Minimax under `cost=minimax`) from its best point; then snap-and-polish when any value
is discrete. A stage ends on its own finish, its budget or a stall (counted within the stage); every goal
met skips to the snap; a run limit or Stop ends the run. `OptimizationProgress.Stage` names the stage
(`OptimizationStages`), and `OptimizationResult.Stages` lists those that ran. Iterations count across
stages; `maxiter`, `maxevals` and `timelimit` are the whole run's. Gate: the L-section at |S11| ≤ 1e-5
runs the global stage, then the polish, and meets it (the global stage alone does not reach 1e-5; LM alone
stalls at 1.2e-6 against 1e-6 on its 1e-6 difference step).

**Sensitivity (R-to8-6).** `OptimizationRun.Sensitivity(at)` — never run unasked. One batch at the best
point (or `at`): a forward difference of 1e-4 of the box per continuous coordinate, the next level for a
discrete one, backward at the top; evaluated through the cache, so a known point is free, and recorded
nowhere — the run's best and log do not move. Each coordinate's ∂cost/∂u (the cost change across its whole
range in its own scale, to first order) and its share of the total; per goal, the coordinate that moves
that goal's cost most. A part of a complex value is its own coordinate, so ∂cost/∂mag(ZL) and
∂cost/∂phase(ZL) are reported, never a whole value.

## 13. Goal functions, the template catalog and "Add as goal" (TO-9)

*(brief-sar-10: the field measurements over a `type=fields` analysis — `B1pMean`, `psSAR`, `SarEff` and four more — are goal
functions too, with a template group of their own, and an optimizer evaluation computes each only when a goal scores it;
`sar.md` §12.)*

**Network metrics are expression built-ins** (`src/Core/Expressions/Evaluator.Network.cs`): `mu`,
`mu_prime`, `K`, `delta_mag`, `max_gain` (dB), `max_gain_lin`, `passivity`, `group_delay`, `vswr` — the
list and spellings are in `expressions.md` §7. Each takes the analysis's S cube (`SP1.S`) and an optional
ordered (input, output) port pair, and computes nothing itself: it calls `NetworkMetrics`' matrix-level
overloads, the same ones the Data Display's µ/K/MAG/σ/group-delay traces call. **The reference
impedances come from the analysis that owns the cube**, found by reference
(`MeasurementContext.TryFindOwner`, the WSProbe functions' precedent), because the metrics are only
defined after renormalizing to a uniform real reference and an S cube does not carry its Z0. A cube no
analysis owns — sliced or computed — is therefore refused rather than assumed to be 50 Ω. A parametric
sweep's stacked Z0 (`[sweep…, port]`) is read per sweep point.

**Templates** (`GoalTemplates.For(circuit or bench)`): per S-parameter analysis |Sij| in dB or linear,
phase, group delay, VSWR, µ, µ′, K and max gain (ports from the bench's `Port` instances); per
S-parameter analysis of a circuit with WSProbes, the single-probe metrics of `stability-wsprobe.md` §5.2,
a complex one through a chosen part; every `measure` row by name, its analysis found through its own text
(`AnalysisReferencedBy`, following measure names); and a custom expression (`Validate` gives the
parser's own message). **There is no HB family on purpose** — an output power, efficiency or PAE goal is a
`measure` row the user writes and then picks by name. A template fills the analysis's swept range and a
suggested type (transmission ≥, reflection ≤, µ/µ′/K ≥ 1); a limit only where one is conventional.

**"Add as goal" (R-to9-4).** `TraceGoalReader` (in `src/Render`, beside the `Trace` model) reads what a
trace is; `TraceToGoal` (headless) turns that into a goal: the trace's group as the analysis, its read
plus its transform as an expression, the plot's visible X range clipped to the data, and the first
visible marker's value as the limit. **The card's transform is translated, not copied**: its `dB20` is a
measure line's `dB()`, and its plain `dB` is a POWER dB, so it becomes `dB10()` — copying the name would
make every power goal 3 dB wrong. A narrowed X slice is written as `:` (the goal's range says it), and a
family's `~` as `:`. Refused with a reason (the menu row greyed, the reason on its tooltip): a complex trace
with no reducing transform and `conj`, stability circles and the passive readouts, a "versus" trace, a
trace renormalized by the plot's Z0 override, Z/Y, and a source that is not a simulation's results. The
menu is the plot menu's **Add as Goal** submenu (one row per curve), the marker menu and a Table's
trace-header menu. **Until TO-10's goal editor exists** the workspace adds the goal straight to the open
schematic the results came from (`WorkspaceViewModel.AddGoalFromTrace`, one undo step), disabled when
the trace gave it no limit; TO-10 replaces that body with opening the editor pre-filled.

## 14. The `opt` verb and the MCP optimizer (TO-11)

`circuitrf opt <path.csch|path.cnl>` and `run analysis=optimize` are one code path (`src/Cli/Optimize.cs`;
`cli.md` §24): `OptimizationRun` over the `PreparedCircuit` Simulate prepares, flags overriding the
`optimize` line for one run, `--vars`/`--goals` narrowing to a subset, values reported as the text the
schematic would hold, `--save-preset` (a `.csch` only, `TuningPresets.LockIn` after a checkpoint) the one
write. Exit 0 / 3 (a goal unmet) / 1 / 2 / 130. **The final result only by default**; `--show-iterations` (`showIterations`) adds each iteration — stderr lines, `perIteration` in the document and MCP progress notifications. **A met goal reports its tightest point and a margin**:
`GoalScore.Margin` (and `GoalReport.Margin`) is the slack at the point closest to the limit, in the
expression's own unit, −(worst violation) when unmet — the Optimizer window's "worst value" for a met
goal is that point too, where it used to be the first grid point. `check` asks `OptimizationRun.Create`
whether a setup would start (no evaluation happens there), so a run-time refusal is a check error in
the run's own words; `explain --analysis` reports each analysis's scopes through
`OptimizationRun.AnalysesUnder`. The goals reference page lists the template catalog run over a fixed
bench (`Reference.FunctionsBench`), so a template added to `GoalTemplates` appears there unedited.

## 15. The Optimizer panel (TO-10)

| Piece | Where |
|---|---|
| Panel state | `OptimizerPanelViewModel` (+ `.Run.cs`), `OptimizerVariableRowViewModel`, `OptimizerGoalRowViewModel`, `OptimizerSettingsViewModel`, `GoalEditorViewModel` — `src/Ui/Optimization/` |
| Dock + views | `OptimizerTool`, `DockPanelIds.Optimizer` (tabbed behind Tuning), `src/Ui/Views/Optimization/` (`OptimizerToolView`, `GoalEditorDialog`) |
| Shell wiring | `WorkspaceViewModel.Optimizer.cs` — the same `PrepareTunedCircuit` the tuning session uses, the display, the goal context and preview, Send to Tuning |
| Display | `IOptimizerDisplay`, implemented by `DisplayTuneSink` |

- **One list of entries (D4).** The Variables list is every entry that is tune- OR opt-enabled; the
  check is the `opt` flag. Unticking a row keeps it until focus moves, so it does not vanish under the
  click. The ＋ popup is Tuning's, generalized through `ITunableAddHost`.
- **The run is `OptimizationRun`**, over the circuit Simulate would prepare, on a background thread the
  view model starts through an injectable `StartBackground` (inline in tests and in the documentation
  fixtures). Progress is posted to the UI thread; the readouts after a finish carry no elapsed time.
- **The display follows the best point** only when it improves. Under `analyses=goals`, a published best
  point carries only the goals' analyses: the display merges the file groups it has and draws the
  others dimmed (`Plot.DimmedTraces`, ~40 % alpha) until the finish, when the best point is evaluated
  ONCE with every enabled analysis and written as the schematic's results with tuned-values provenance.
- **Add as Goal opens the editor pre-filled** (TO-9's interim direct add is now only the fallback).
- **Keeping the result**: Lock in (`TuningPresets.LockIn`, the preset records the cost), Push (TO-4's
  `TuningPush`), Send to Tuning (`TuningPanelViewModel.LoadValues`), Snap and polish and Sensitivity on a
  finished run. The optimizer toolbar has no Presets list of its own: presets are recalled in Tuning.
- **Widen** (D17) doubles the span on the railed side — by ratio on a log range, by width on a linear
  one — of the entry whose range holds the value (the other part's, when a complex value railed against
  it); refused only when the value's ranges already conflict.

## 16. The example and the user pages (TO-12)

**`examples/Optimization/`** — three test-bench cells, each drawn from a `.cnl` by `netlist
--to-schematic` and each with an authored Data Display (`<cell>.cdd` beside the `.cws`, the
`ResultsWriter.AuthoredDisplayPath` a finished run opens) plotting its goals' quantities:

| Cell | Variables | Goals | Saved algorithm | Outcome (seed 1) |
|---|---|---|---|---|
| `LSectionMatch` | `L1.L`, `C1.C` | \|S11\| ≤ −20 dB, 0.95–1.05 GHz | `lm` | met, 39 evaluations |
| `BandpassFilter` | VARs `Lp`, `Cp`, `Ls`, `Cs` (six parts) | passband `in` −0.5…0 dB, two stopbands ≤ −25 dB | `minimax` | met, 85 evaluations; presets *As drawn* and *Equiripple* (the latter written by `opt --save-preset`) |
| `StabilityAndGain` | `Rstab` (1–3 Ω, too narrow on purpose), `Rshunt`, `mag(Zs)`, `phase(Zs)` of `Zs = polar(50, 0) Ohm` | `mu(SP1.S)` ≥ 1.05 over 0.5–10 GHz, gain ≥ 13 dB over 2–3 GHz | `auto` | exit 3, Rstab railed at max; met after two Widens (1–9 Ω) |

The gate is `tests/Ui.Tests/Examples/OptimizationExampleTests.cs`: `check` on the workspace is clean;
the L-section and the filter meet every goal within an evaluation bound (100, 200), the filter's
*Equiripple* preset equals that run's best point; the amplifier exits 3 with `Rstab` railed at max.
`OptParityTests` (TO-11) now runs on the example's L-section. **The filter's 151-point grid is the
setting traded for speed**: on 1,501 points the *Equiripple* passband dips to −0.509 dB between two
coarse points; the README says so.

**User pages.** `reference/tuning.html` and `reference/optimization.html` (both in the Simulate section
of `_nav.txt`), a `#opt` section in `cli.html` with exit code 3, short pointers left at
`simulations.html#tuning` and `#optimizer`. The two panels' Help buttons open the new pages
(`DocAnchors.WholePages`). Figures `tuning-panel` and `optimizer-panel` are `DocTuningFixtures` rows in
`FigureCatalog`, captured on the example's amplifier — the Optimizer figure runs the optimizer inline
(deterministic: the saved seed fixes every evaluation). The algorithm table is
`{{table: optimizers}}` → `DocTables.Optimizers()`, read from `OptimizerAlgorithms.All`, so the page's
use-when sentences are the menu's tooltips. **Not generated in this phase**: DocGen is run once at the end
of the series, so `DocsFactoryTests`' figure-exists and deep-link gates fail for these two pages and two
figures until it is.

**Found while doing it.** `check` warned that every network trace in a `.cdd` "names no cube and no
expression" — a trace with a source and no `CubeName` reads the source's network by `MatrixType`, which
is how the trace card writes every S-parameter trace (the S-Parameters example had nine of them). The
rule now applies only to a trace with no source (`src/Cli/RESOLVED.md`).

## 17. The decisions as built

| | Decision | As built |
|---|---|---|
| D1 | What is tunable | As decided; integers flagged by name (`m`, `Nf`, `NumFingers`, `Fingers`); identity numbers (`Num`, `NumPorts`, `NumFreqs`) not offered (§2). |
| D2 | Hierarchy | As decided; the catalog walks the EXTRACTED netlist but offers only values the drawing holds (§2). |
| D3 | Keys | As decided; the cell part is `CellScope.NameFor` — the `.cnl` instance-type spelling (§3). |
| D4 | One entry, one range | As decided; a negative value's default range is [1.5v, 0.5v] (§2). |
| D5 | Stored in the tuned `.csch` | As decided; the model is in `src/Core/Design` because the `.cnl` reader is in Core (§1). |
| D6 | Last tuned | As decided; written only when some row differs and not again for the same values (§9). |
| D7 | Newest-wins, no cancel | As decided; Run on release suggested after a 2 s evaluation (`TuneSession.SlowEvaluation`). |
| D8 | In-memory Data Display | As decided; plus dimmed traces for analyses an optimizer evaluation did not run (§15). |
| D9 | Snapshot ghosts | As decided; GUI copy/export draws ghosts, CLI render does not, tables never. |
| D10 | Goals | As decided; a `scale=` key added (TO-6) (§4, §10). |
| D11 | Cost | Normalization: violation ÷ scale × weight ÷ √points; met within 1e-9 of scale (§10). |
| D12 | Architecture | As decided; the panels and the verb share `OptimizationRun` and `PreparedCircuit` (gated by `OptParityTests`). |
| D13 | Algorithms | Every menu entry built (§18). |
| D14 | Headless writes nothing | As decided; `--save-preset` the one write, `.csch` only. |
| D15 | Exit codes | As decided. |
| D16 | Pause and stop | As decided; a paused run is evaluation-for-evaluation identical to an unpaused one. |
| D17 | Railed | 0.5 % of the normalized range; Widen doubles the span on that side (§15). |
| D18 | Complex values by parts | As decided; `ComplexValue` / `ComplexRegion`; more than two opt-enabled parts refused; infeasible points not simulated and ranked last (§2a, §10). A part is always continuous (`discrete=` refused on it). |

**Not built, and worth knowing:** there is no panel control for `discrete=integer|preferred` — an
integer-typed parameter is detected and `step=` has ⋮ ▸ Step…, but `discrete=preferred` is set only in
the file text (`.cnl`, or a `.csch` round-tripped through `netlist --to-schematic`).

## 18. The algorithm menu as built

The registry `OptimizerAlgorithms.All` is authoritative (labels, use-when sentences, options, cost forms)
and `reference optimizers` and the user page's table print it. In menu order:

| Menu | Id | Family | Built in |
|---|---|---|---|
| Auto | `auto` | CMA-ES → LM (Minimax under minimax) → snap and polish | TO-8 (§12) |
| Gradient (Levenberg–Marquardt) | `lm` | Newton family, forward-difference Jacobian, Levenberg damping | TO-6 (§10) |
| Quasi-Newton (BFGS-B) | `bfgsb` | limited-memory BFGS, projected Armijo line search | TO-6 |
| Minimax | `minimax` | trust-region sequential LP on signed residuals | TO-7 (§11) |
| Simplex (Nelder–Mead) | `simplex` | adaptive coefficients, projection onto the box | TO-6 |
| Trust-region model | `trust_region` | least-change quadratic model, no derivatives | TO-7 |
| Pattern search | `pattern` | OrthoMADS + quadratic model search | TO-7 |
| Random | `random` | Latin hypercube or uniform | TO-6 |
| Differential evolution | `de` | L-SHADE | TO-7 |
| Particle swarm | `pso` | constriction factor, ring or global | TO-7 |
| CMA-ES | `cmaes` | IPOP restarts, bounds by re-sampling | TO-7 |
| Bayesian (slow simulations) | `bayes` | GP (Matérn 5/2) + expected improvement; trust region above 10 variables | TO-8 |
| Discrete | `discrete` | exhaustive grid ≤ 2,000 points, else coordinate descent with restarts | TO-8 |

## 18a. Across corners (brief-yield-7)

`optimize corners=none|all|<names> [nominal=0]` makes the optimizer meet every goal at the nominal and at each corner
at once: one evaluation per corner per point, the residual vectors concatenated (so minimax is worst-case design), a
goal met only when met everywhere, and its report the BINDING corner's. The Tuning panel's **Evaluate at** picker
evaluates the sliders at a corner; Push still writes the values alone. The design and its rules are
`docs/design/yield.md` §11.

## 18b. A vendor part as a variable (brief vendorlib-4)

A placed vendor part offers one tunable, `<Instance>.Part` (`TunableKey.PartParameter`), whose value is a
PART. Its candidates are chosen by the `tune` line: `kind=same` (the default, D1) is
`VendorPartChange.SameKind`; `parts="<query>"` is `VendorLibrarySearch` over the part's own library;
`min=`/`max=` bound the stated value in the part's unit. `VendorPartCandidates.Select` is that one rule,
from the index alone; a candidate of another port count is removed and counted. There is no cap on the count
(issue #32 retired D2's 200): a candidate is one small subcircuit read once per extraction, and Discrete moves from
an exhaustive grid to coordinate descent on its own when the list is long.

**A part change is a change of the instance's CELL.** `NetExtractor.PartTunablesOf` describes every placed
part, and when the setup tunes or optimizes one, emits a cell per candidate from its own byte block
(`VendorPartSource`, `SpiceModelNetlist.Build`) under the part's own name. The description rides on
`ExtractionResult.PartTunables` → `PreparedCircuit.PartTunables` → `TunableCatalog.FromNetlist(parts:)`,
because a `.cnl` cannot say which library part an instance came from. An evaluation's override for
`C1.Part` is a cell name, and `TunableOverrides` rebinds the instance to it, keeping only the overrides
that cell declares; the elaborator sees an ordinary netlist.

**The search.** A part is an `OptimizationCoordinate` over the candidates' INDEX in value order
(`VendorPart`, `Levels` 0 … k−1). Discrete searches it as it searches any levelled coordinate, and Auto
runs Discrete alone when every coordinate is levelled and one is a part. Every continuous search relaxes
a part with a C/L glyph to an IDEAL stand-in (D3): `Decode(…, snapParts: false)` gives the fractional
index, the value is the candidates' values interpolated on a log scale, and the override is that value
with a unit, which `TunableOverrides` binds as the primitive `C` or `L` on the part's two nets. A
continuous run over a part always ends in the snap (as a standard-value row does), where the part takes
the candidates either side; `SnapResult.Parts` and `opt.part.snap-report` carry the ideal cost and the
real one. A part with no glyph has no stand-in and decodes to a candidate in every stage. A continuous-only
algorithm on parts alone is refused (`opt.part.discrete-only`), and the Optimizer greys it with that reason.

**Write-back** is `SchematicViewModel.VendorPartChangeCommand` — the edit `ChangeVendorPart` makes —
inside Push's one undo batch, so the footprint rule of brief 3 holds. A part is never statistical; its
footprint is not evaluated in a search (D4).

## 19. For the yield series — the seams overview §5 reserved

The yield series' design note is **`docs/design/yield.md`**; each seam below says which of its phases took it.

| Seam | Held? |
|---|---|
| A tolerance on a complex value's part needs nothing new | **Held — taken by YA-1** (the model and `check`'s same-system-pair rule); YA-2 composes the draws. A part is an ordinary entry with its own key, and `ComplexValue.Compose` composes any set of parts into the whole value — the rule a sample would use. |
| A variable entry can grow a tolerance without a format break | **Held — taken by YA-1.** `TunableEntry` grew typed `Stat`/`Distribution`/`Spread` fields, `OptimizationGoal` a `Use`; the keys a hand-written file had parked in `Extra` now land in them. `TunableEntry`, `OptimizationGoal` and `OptimizerSettings` carry an `Extra` map; the `.cnl` and `.csch` readers keep unknown keys and write them back (`cnl.tuning.unknown-key` is a warning, not a refusal). A preset has no `Extra` — a sample set is not a preset. |
| Goals evaluable on many points in one batch | **Held — taken by YA-2**, widened by YA-4 and YA-7. `OptimizationRun.ForEvaluation` and `EvaluateValues` are the public value-map door over the same `EvaluateBatch` (the unit-box batch now decodes into it), YA-4's `ValuePoint(Values, Draws, Tag)` carries a trial's kit draws through it, and YA-7 keys its cache by point and corner. Every Monte Carlo, corner, centering and DOE run evaluates through it — there is no second evaluator (`yield.md` §6, §8, §11). |
| The evaluation service safe for concurrent evaluations | **Held, with the known exception — taken by YA-4.** `PreparedCircuit` is read once and evaluated concurrently; a circuit holding an external device or a Verilog-A model is not re-entrant (`NotReentrantReason`) and runs one point at a time with a note, and a yield run over such a circuit is serial for the same reason. Trials are drawn from counter-based streams, so the result is the same at any parallelism (`yield.md` §8.2). |
