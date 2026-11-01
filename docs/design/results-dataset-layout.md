# Results dataset layout — per-testbench, grouped

Status: implemented (stages 1–3 shipped). Supersedes the per-analysis results-file decision in
`data-display.md` §1.3 / §3 (one file per analysis). Alpha: no migration, no back-compat — the `.npy`
layout and the in-memory `DataSet` change shape and old files are simply regenerated.

**Update (brief-results-storage-and-data-display.md, 2026-07-29):** stages 1–3 above shipped the
grouped `DataSet`/`.npy` correctly, but `RunResultsWriter` itself shipped writing
`results/<schematicKey>/run.npy` (a per-schematic SUBDIRECTORY) — the opposite of Open Question 1's own
stated lean toward the flat filename below. This was a real documented-vs-implemented divergence, not a
deliberate change of plan. It has now been fixed: a run writes **flat**, `results/<schematicKey>.npy`,
exactly as this document always intended — see Open Question 1, now resolved, and the migration this
required (R-res-11) since a per-schematic-subdirectory layout had actually shipped and needed moving.

Read with: `data-display.md` (the display/source model this revises), `data-export.md` +
`src/RfCore/Export/CLAUDE.md` (the `.npy` format), `src/Core/Data/CLAUDE.md` (the DataSet/DataCube
contract), `measurements.md` (measurements, which this simplifies), and `family-curves.md` /
`parametric-sweep-ux.md` (trace addressing this touches).

## Decision

1. **Results file unit = the testbench.** One `.npy` per run holds every analysis from that run, instead
   of one `.npy` per analysis.
2. **Grouped/nested DataSet.** A `DataSet` is an ordered collection of named **analysis groups**, each a
   bundle of cubes. The analysis boundary is a real structural fact, not a string convention.
3. **Unified `Analysis.Cube` addressing.** Both plot trace specs and measurement expressions address a
   cube as `Analysis.Cube` (e.g. `HB1.V[:, 0]`, `SP1.S(2,1)`). This is the notation the measurement
   accessor already uses, so plotting and measurements finally speak one language.
4. **No migration.** Break the `.npy` layout and the `DataSet` shape freely; regenerate by re-running.

## Why

A measurement is a function of the whole simulation setup, so it belongs *in* that simulation's dataset,
not a parallel file — and with the entire run in one dataset, a measurement (or a comparison trace) can
reference any analysis with no cube duplication and no ambiguity about where the result "lives." It also
matches how an engineer thinks ("this simulation produced this dataset") and how VendorA stores a run.
The cost is concentrated in cube-name namespacing and the display's source-tree/addressing — see below.

## Model

A `DataSet` becomes a map of **group name → cubes**. Two shapes coexist:

- **Grouped (run results):** groups are analysis names — `HB1`, `SP1`, `DC1`, plus a `measurements`
  group (see below). Cubes are addressed `Group.Cube`.
- **Flat (Touchstone / imported):** a `.sNp` file or any single-analysis import has **one default
  (unnamed) group**. Its cubes are addressed by bare name (`S`, `Z0`), exactly as today.

Resolution rule (one rule serves both): a bare `Cube` resolves in the default group, or — when there is
exactly one group — in that group; a qualified `Group.Cube` resolves in the named group. So existing
bare-name specs and all Touchstone sources keep working unchanged, and run-results gain the analysis
level. The same rule backs the measurement accessor `HB1.V(...)`.

## Storage / on-disk format

The `.npy` stays a single flat NumPy structured array — the format has no native nesting, and forcing
one in would break the "plain `np.load`" consumer contract. Grouping is encoded **explicitly in
metadata**, not by splitting field-name strings (cube names already legitimately contain `:`/`.`/`/`,
so a name-split separator is unsafe):

- Each cube is still one NumPy field. Field names are **uniquified** (two analyses can both have `V`),
  but the field name is treated as an opaque key — consumers never parse it for the group.
- `__meta__` JSON gains, per cube, its **`group`** (analysis name; empty/absent = default group), and a
  top-level **`groups`** list giving group order. The reader rebuilds the grouped `DataSet` from
  `group`; the writer flattens groups → fields on write.
- `format_version` bumps; the reader rejects the old flat layout (alpha, no migration).

Net: the file is still a flat field bag readable by `np.load`; the grouping is recoverable from
`__meta__` and is authoritative in memory.

## Addressing & the display

- **Trace spec / `CubeTraceSpecParser`:** accept an optional `Analysis.` prefix on the cube name; resolve
  `Analysis.Cube` against the grouped DataSet, bare `Cube` via the default/sole-group rule. `TraceExpression`
  (multi-cube) gets the same qualified-name resolution, so cross-analysis expressions (`HB1.V - SP1.V`)
  work within one file.
- **Data-source tree:** today file → cube. Becomes **file → analysis → cube** (the grouped structure
  makes this fall out; the `measurements` group appears as a sibling analysis). This is the main UI work.
- **Trace binding:** a trace still carries one `SourcePath` (the one results file). What changes is the
  in-file address: `CubeName`/`Expression` become group-qualified.

## Measurements (simplified by this change)

With the whole run in one dataset, the earlier "attach each measurement to the analysis it references
(and duplicate for cross-analysis)" machinery is unnecessary — a measurement is in-scope no matter which
group holds it. So:

- Measurements are evaluated once into the **one** run DataSet and stored in a dedicated **`measurements`
  group** (a sibling of the analyses). They read any analysis via `HB1.V(...)`; results are written once.
- This **reverts** the per-analysis attachment + access-tracking added to `MeasurementEvaluator` /
  `MeasurementContext`: `EvaluateInto(theRunDataSet)` (the original simple form) is what the run pipeline
  calls again, targeting the run's single grouped DataSet's `measurements` group.
- `measurements.md` is updated to match: the run-wiring section drops the per-analysis attach; the
  reference contract stays `Analysis.Cube`.

Open placement question resolved per your steer: a **`measurements` group** (not scattered under each
analysis), since one-file scope makes references resolve regardless and a dedicated group reads cleanly
in the tree (mirrors VendorA's equations appearing as their own dataset section).

## Touchpoints (what changes; all break freely, no migration)

Storage / model:
- `src/RfCore/Data/DataSet.cs` — grouped structure (group → cubes); group-aware `Contains`/indexer; the
  `Analysis.Cube` resolution rule; `S/Y/Z` convenience accessors become group-aware (default group).
- `src/RfCore/Export/NpyWriter.cs` / `NpyReader.cs` — write/read `group` per cube + `groups` order in
  `__meta__`; uniquify field names; bump `format_version`.
- `src/RfCore/Export/DataSetExporter.cs` / `DataSetImporter.cs` — pass grouping through.
- `src/RfCore/Data/DataSetBuilder` (`FromSnp`/`ToSnp`/`ClassifyZ0`) — bare `S`/`Z0` lookups become
  default-group lookups; a Touchstone import is one default group.

Run pipeline:
- `src/Ui/Schematic/SchematicRunService.cs` — collect every dispatched analysis into **one** grouped
  DataSet (group per analysis) instead of a list of separate DataSets; evaluate measurements into its
  `measurements` group; return one result.
- `src/Ui/Schematic/RunResultsWriter.cs` — write **one** file per run: `results/<schematicKey>.npy`
  (drops the per-analysis directory, and — per the resolution below — drops the per-schematic
  subdirectory too). The owner-identity collision check was **dropped**, not moved to a sidecar or
  `__meta__`: `SchematicKey` already disambiguates `cell` from `cell.view`, so the collision it guarded
  against (two different cells resolving to the same results file) is not reachable through normal use.

Display / addressing:
- `src/Ui/DataDisplay/.../DataSourceEntryViewModel` + the source-tree view — present file → analysis →
  cube.
- `src/Ui/DataDisplay/CubeTraceSpecParser.cs` + `TraceExpression.cs` — qualified `Analysis.Cube` resolution.
- `src/Ui/DataDisplay/.../PlotInspectorViewModel` — resolve a trace's group-qualified cube against the
  grouped DataSet; the reseed/refresh paths follow the same resolution.

Engine (simplification):
- `src/Engine/MeasurementEvaluator.cs` + `src/Core/Expressions/MeasurementContext.cs` — revert the
  per-analysis attachment / access-log; evaluate into the run DataSet's `measurements` group.

Docs:
- `data-display.md` §1.3/§3 — revise the locked per-analysis-file decision to per-testbench.
- `measurements.md` — update run-wiring + storage to the `measurements` group.

## Staged plan

1. **Storage + model** (RfCore): grouped `DataSet`; NpyWriter/Reader group metadata + version bump;
   builder/accessor group-awareness. Round-trip tests (grouped and flat) are the gate.
2. **Run pipeline**: SchematicRunService one grouped DataSet per run; RunResultsWriter one file;
   MeasurementEvaluator simplified into the `measurements` group.
3. **Display addressing**: CubeTraceSpecParser/TraceExpression qualified resolution; source tree
   file→analysis→cube; PlotInspector resolution.
4. Update `data-display.md` and `measurements.md`.

Each stage builds + tests green before the next; stage 1 is independently testable via NPY round-trip.

## Open questions

1. **RESOLVED — flat.** `results/<schematicKey>.npy` (one file, flat in `results/`), not a per-schematic
   directory. The flat form also gives R-res-2 (§ below) its whole basis: a user-named baseline
   (`results/baseline_v1.npy`) sits as a plain sibling file, not a second thing to find a directory
   convention for. Owner identity is not carried anywhere (the collision check it would have supported
   was dropped, see "Touchpoints" above) — nothing needed the sidecar/`__meta__` option this question
   once considered.
2. **Flat-source group label.** Does a Touchstone source show as a single unnamed node in the tree, or
   under a synthetic group label (e.g. the file stem)? Affects only presentation, not addressing.
3. **Bare-name specs across groups.** Confirm bare `Cube` resolving "in the sole group" is the desired
   convenience for a single-analysis run (so a one-analysis run still plots `V[:, 0]` without a prefix),
   with the qualified form required only once there are ≥2 groups.

## Monte Carlo and yield (brief-yield-4)

A Monte Carlo or yield run (`StatisticalRun`, `src/Design/Statistics`) writes ONE grouped `DataSet` to
**`<design>.yield.npy` beside the schematic or netlist** — never `results/<key>.npy`, which Simulate owns — when it
finishes or is stopped, never when cancelled. The names are fixed here; every consumer (the CLI, MCP, the Data
Display, the Yield panel) reads them and computes nothing of its own. Values are in base SI.

| Group | Cube | Axes | What |
|---|---|---|---|
| each analysis (`SP1`, `DC1`, …) | as Simulate names them | `[trial, …]` | the trials the save policy keeps (1…K); a trial that did not evaluate is NaN. `__`-metadata cubes pass through unstacked |
| `measurements` | a real scalar | `[trial]` | kept for EVERY trial whatever the save policy, being a scalar |
| `measurements` | anything else | `[trial, …]` | as an analysis cube; a `histogram`/`yield_sens` measure brings `<name>:width` (and `<name>:count`) |
| `trials` | `stat:<key>` | `[trial]` | the drawn value of each statistical entry (base SI, the base unit on the cube) |
| `trials` | `z:<key>` | `[trial]` | its standard normal after correlation — what statistical corners and centering replay |
| `trials` | `z:process:<stream>`, `z:mismatch:<stream>` | `[trial]` | each kit/expression distribution call's standard normal; NaN where a trial did not draw it |
| `trials` | `goal:<g>:pass` | `[trial]` | 1/0; a trial that did not evaluate is 0 under `nonconverged=fail`, NaN under `warn` |
| `trials` | `goal:<g>:margin`, `goal:<g>:worst` | `[trial]` | the margin (GoalScore's, in the expression's unit) and the expression's value at the tightest point |
| `trials` | `pass` | `[trial]` | every scored goal met (same 0/NaN rule); absent when the run scores no goal |
| `trials` | `status` | `[trial]` | 0 evaluated; k ≥ 1 the k-th row of `reasons` |
| `trials` | `reasons` | `[reason]` | labelled with each distinct reason sentence; present only when some trial did not evaluate |
| `nominal` | `<group>.<cube>` | as the nominal | the nominal design's cubes with no trial axis, named by their own address: a cube's nominal is `nominal.` + its address (`nominal.SP1.S`, `nominal.trials.pass`) |
| `statistics` | `mean`, `sigma`, `min`, `max`, `median`, `p1`, `p99`, `skew`, `kurtosis`, `cpk`, `sigma_to_limit`, `yield`, `lower`, `upper` | `[quantity]` | the statistics table (brief-yield-8): one row per goal's worst value (labelled `goal:<g>:worst`) and per scalar measure, over the trials that evaluated, by `SampleStatistics`; `cpk`/`sigma_to_limit` against the goal's `GoalResiduals.ValueLimits` (NaN for a measure, or `eq`/`out`); `sigma_to_limit` is signed, positive on the passing side; `yield`/`lower`/`upper` are the goal's own estimate (NaN for a measure). Absent with no goal and no scalar measure |
| `yield` | `trials`, `did_not_evaluate`, `passes`, `counted`, `yield`, `lower`, `upper` | scalar | overall; yield and interval as fractions, NaN with no goal |
| `yield` | `goal:<g>:passes` … `goal:<g>:upper` | scalar | the same per goal |
| `yield` | `goal:<g>:spec` | `[goal:<g>:spec]` | one point whose axis LABEL is the goal's own `goal …` line (`TuningDirectiveText.GoalLine`) — what a display draws the goal's limits from (brief-yield-8) |
| `yield` | `confidence`, `target`, `seed`, `saved_trials` | scalar | fractions; `target` NaN when none |
| `yield` | `planned_trials` | scalar | the trial count the run was set for (`trials=`, with auto-stop the cap). `trials` above is the count it reached, which a stopped or auto-stopped run makes smaller; under `lhs` every trial's draw depends on the planned count, so a statistical corner saved from this file records this one. Absent from files written before brief-yield-15, where readers fall back to `trials` |
| `yield` | `contrib:<name>`, `contrib:<name>:cumulative` | `[contributor]` | written only on request (brief-yield-9 R-ya9-5, the Data Display's Statistics ▸ Contributions): each contributor's share of the explained variance of a goal's worst value or a scalar measure, largest first, and the running total; the axis is labelled with the contributors' names, its values 1…N |
| `yield` | `mode`, `sampling`, `nonconverged`, `save`, `stopped` | `[<name>]` | one point whose axis LABEL is the text (`random`, the save sentence, the finish reason) |

**Trial order is drawing order** (1…N), and every per-trial cube is slotted by trial number, so the `DataSet` is
identical for any `parallel=`. With auto-stop the trial axis ends where the rule decided.

## Corners (brief-yield-6)

A corner run (`CornerRun`, `src/Design/Statistics`) writes ONE grouped `DataSet` to **`<design>.corners.npy` beside the
schematic or netlist**. Every cube has an outer **`corner`** axis whose LABELS are the corner names, the nominal first
as `nominal`; its values are 1…N.

| Group | Cube | Axes | What |
|---|---|---|---|
| each analysis (`SP1`, `DC1`, …) | as Simulate names them | `[corner, …]` | each corner's results; NaN where a corner did not evaluate. A cube whose shape differs between corners is left out |
| `corners` | `goal:<g>:pass` | `[corner]` | 1/0; a corner that did not evaluate is 0 under `nonconverged=fail`, NaN under `warn` |
| `corners` | `goal:<g>:margin`, `goal:<g>:worst` | `[corner]` | the margin and the expression's value at the tightest point, as in `trials` |
| `corners` | `pass` | `[corner]` | every goal met (same 0/NaN rule); absent when no goal is scored |
| `corners` | `temp` | `[corner]` | the corner's ambient in °C (`degC`); NaN where it sets none |
| `corners` | `status`, `reasons` | `[corner]`, `[reason]` | as in `trials` |

A **Monte Carlo at each corner** writes `<design>.yield.npy` with the Monte Carlo layout above stacked under the same
`corner` axis: `[corner, trial, …]`, `trials.*` as `[corner, trial]`, `yield.*` as `[corner]`. Corners that stopped at
different trial counts are padded with NaN to the longest.

## Design of experiments (brief-yield-14)

A design of experiments (`DoeRun`, `src/Design/Statistics`) writes ONE grouped `DataSet` to **`<design>.doe.npy` beside
the schematic or netlist**. `<r>` is a response: `goal:<g>:worst`, `goal:<g>:margin`, or a scalar measure's name.

| Group | Cube | Axes | What |
|---|---|---|---|
| `runs` | `coded:<key>` | `[run]` | each factor's coded level, −1 … +1 |
| `runs` | `actual:<key>` | `[run]` | the value set, base SI |
| `runs` | `<r>` | `[run]` | the response; NaN where the run did not evaluate it |
| `runs` | `kind`, `status` | `[run]` | 0 cube, 1 axial, 2 centre; 0 evaluated, 1 not |
| `effects` | `<r>:effect`, `<r>:coefficient`, `<r>:active` | `[term]` (labelled with the terms: `A`, `AB`, `A^2`) | twice the coded coefficient; the coefficient; 1/0 |
| `effects` | `<r>:abs`, `<r>:margin_line` | `[rank]` (labelled, largest first) | the effects Pareto's bars and its Lenth-margin line |
| `effects` | `<r>:intercept`, `<r>:lenth_pse`, `<r>:lenth_margin`, `<r>:r2`, `<r>:curvature` | scalar | the fit; curvature NaN without centre points or for `ccf` |
| `effects` | `aliases` | `[term]` (labelled with each term's alias set) | how many aliases each term has |
| `main` | `<r>:<key>` | `[level]` | the response's mean at each coded level |
| `interaction` | `<r>:<keyA>*<keyB>` | `[by_level, level]` | the mean at each (B, A) level pair, B low then high |
| `doe` | `design`, `factor_source`, `levels`, `generators`, `goal:<g>:spec` | one-point, labelled | the design described, the factor source, the levels, a fraction's generators, each goal's line |
| `doe` | `factors` | `[factor]` (labelled `A = R1.R: low \| centre \| high`) | the factors |
| `doe` | `runs`, `centre`, `evaluated`, `did_not_evaluate`, `resolution` | scalar | counts; resolution NaN unless a fraction |

## Sensitivity (brief-sens-1)

A sensitivity run (`SensitivityRun`, `src/Design/Statistics`) writes ONE grouped `DataSet` to **`<design>.sens.npy`
beside the schematic or netlist**, everything in group `sens`. `<r>` is a goal's name or a scalar measure's. A goal's
values are normalized margins (margin over the goal's scale); a measure's are in its own unit, which its cubes carry.

| Group | Cube | Axes | What |
|---|---|---|---|
| `sens` | `contrib:<r>`, `contrib:<r>:shown`, `contrib:<r>:cumulative` | `[contributor]` (labelled with each factor's label — its key, or an SnP entry's `SNP3 · S31 dB` — largest \|Δ\| first) | the shares Δ²/ΣΔ² (0–1; shown is the same values), and their running total — the contribution Pareto |
| `sens` | `contrib:<r>:by=entry`, `contrib:<r>:by=entry:cumulative` | `[contributor]` (`SNP3 · S31`, or a non-SnP key's label; largest share first) | an SnP entry's dB and phase shares summed; any other key on its own (brief-sens-2) |
| `sens` | `contrib:<r>:by=port` | `[contributor]` (`SNP3 · port 3`) | every entry in that row or column, summed — shares OVERLAP, so no cumulative cube |
| `sens` | `contrib:<r>:by=instance`, `…:cumulative`, `factors:<r>:by=instance` | `[contributor]` (`SNP3`, `R1`, a variable) | the shares of every key the instance owns; its key count |
| `sens` | `excluded` | `[instance]` (labelled with each SnP instance left out by `snp=off` / `snpexclude`) | the factors each would have contributed |
| `sens` | `delta:<r>`, `plus:<r>`, `minus:<r>`, `nominal:<r>`, `asymmetry:<r>` | `[contributor]`, same order | signed Δ; the response at + and −; m₀ at every row; the D6 asymmetry |
| `sens` | `status:<r>` | `[contributor]` (labelled `ok`, `nonlinear`, `one-sided`, `failed`) | 0–3 |
| `sens` | `spread:<r>`, `estimate:<r>` | scalar | √ΣΔ²; Φ(m₀/spread) — a goal only, and only when every basis is σ |
| `sens` | `factors`, `basis`, `step`, `status` | `[contributor]` in step order (labelled with the key; `σ`, `1 %`, `1°`; `+ v \| − v`; whether both steps evaluated for every response) | ordinals |
| `sens` | `notes` | `[note]` (labelled with each note) | ordinals |
| `sens` | `evaluations` | scalar | simulations the run spent |

