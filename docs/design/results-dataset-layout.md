# Results dataset layout — per-testbench, grouped

Status: implemented (stages 1–3 shipped). Supersedes the per-analysis results-file decision in
`data-display.md` §1.3 / §3 (one file per analysis). Alpha: no migration, no back-compat — the `.npy`
layout and the in-memory `DataSet` change shape and old files are simply regenerated.

Read with: `data-display.md` (the display/source model this revises), `data-export.md` +
`RfCore/src/Export/CLAUDE.md` (the `.npy` format), `src/Core/Data/CLAUDE.md` (the DataSet/DataCube
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
- `RfCore/src/Data/DataSet.cs` — grouped structure (group → cubes); group-aware `Contains`/indexer; the
  `Analysis.Cube` resolution rule; `S/Y/Z` convenience accessors become group-aware (default group).
- `RfCore/src/Export/NpyWriter.cs` / `NpyReader.cs` — write/read `group` per cube + `groups` order in
  `__meta__`; uniquify field names; bump `format_version`.
- `RfCore/src/Export/DataSetExporter.cs` / `DataSetImporter.cs` — pass grouping through.
- `RfCore/src/Data/DataSetBuilder` (`FromSnp`/`ToSnp`/`ClassifyZ0`) — bare `S`/`Z0` lookups become
  default-group lookups; a Touchstone import is one default group.

Run pipeline:
- `src/Ui/Schematic/SchematicRunService.cs` — collect every dispatched analysis into **one** grouped
  DataSet (group per analysis) instead of a list of separate DataSets; evaluate measurements into its
  `measurements` group; return one result.
- `src/Ui/Schematic/RunResultsWriter.cs` — write **one** file per run: `results/<schematicKey>.npy`
  (drops the per-analysis directory); owner-identity collision check moves to a sidecar or `__meta__`.

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

1. **Results filename.** `results/<schematicKey>.npy` (one file, flat in `results/`) vs keeping a
   per-schematic directory `results/<schematicKey>/run.npy`. The former is simpler; the latter leaves
   room for sidecars. Leaning to the flat filename with owner identity in `__meta__`.
2. **Flat-source group label.** Does a Touchstone source show as a single unnamed node in the tree, or
   under a synthetic group label (e.g. the file stem)? Affects only presentation, not addressing.
3. **Bare-name specs across groups.** Confirm bare `Cube` resolving "in the sole group" is the desired
   convenience for a single-analysis run (so a one-analysis run still plots `V[:, 0]` without a prefix),
   with the qualified form required only once there are ≥2 groups.
