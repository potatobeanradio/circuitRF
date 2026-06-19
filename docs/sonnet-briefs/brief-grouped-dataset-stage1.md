# Brief — Grouped DataSet, stage 1 (storage + model)

Design: `docs/design/results-dataset-layout.md`. This is **stage 1 of 4**: make `DataSet` grouped and
persist groups through the `.npy` round-trip. No run-pipeline or display changes here (stages 2–3).
Alpha: no migration — bump the format version and reject old files.

Scope = RfCore only: `src/Data/DataSet.cs`, `src/Export/NpyWriter.cs`, `src/Export/NpyReader.cs`,
`src/Export/DataSetExporter.cs` (size estimate only), plus tests. Verify `DataSetImporter.cs`,
`DataSetBuilder` (`FromSnp`/`ToSnp`/`ClassifyZ0`), and `MatWriter.cs` need **no** change (they operate on
the default group; see below) — touch them only if the build forces it.

## Model: default-group grouping

`DataSet` becomes an **ordered map of group-name → (cube-name → cube)**. A group name is an analysis name
(`"HB1"`, `"SP1"`, `"measurements"`) or the **default group `""`**. Every existing bare operation targets
the default group, so the engine, the `S/Y/Z`/`V/I` accessors, and `StackSweepAxis` are unchanged — a
freshly-built engine DataSet is a single default-group DataSet exactly as today. Named groups appear only
when a caller uses the new group API (stage 2).

### `DataSet.cs` changes

Replace the flat `Dictionary<string,DataCube> _cubes` with ordered group storage, e.g.
`List<string> _groupOrder` + `Dictionary<string, Dictionary<string,DataCube>> _groups`
(ordinal-cased). Add `public const string DefaultGroup = "";`.

Keep these working exactly as before, all routed through the default group / bare-resolution:
- `void Add(string name, DataCube cube)` → adds to the default group `""`.
- `DataCube this[string spec]` → **resolution rule** (below).
- `bool Contains(string spec)` → same resolution, bool, no throw.
- `S/Y/Z`, `V/I`, `StackSweepAxis` → unchanged source (they call the bare indexer / `Cubes`, which
  resolve in the default/sole group). `StackSweepAxis` builds default-group DataSets — leave it as is.
- `IReadOnlyDictionary<string,DataCube> Cubes` → returns the **default group's** cubes (empty for a
  purely-grouped results DataSet). Legacy bare consumers in the UI are migrated in stage 3.

Add (new):
- `void AddToGroup(string group, string name, DataCube cube)` — creates the group on first use,
  preserving insertion order in `_groupOrder`.
- `IReadOnlyList<string> Groups` — ordered group names that contain cubes (includes `""` when the
  default group is used).
- `bool ContainsGroup(string group)`.
- `IReadOnlyDictionary<string,DataCube> CubesIn(string group)` — throws `KeyNotFoundException` if absent.

**Resolution rule** for `this[spec]` / `Contains(spec)`:
1. If `spec` has no `.`: *bare-resolve* — return from the default group if present; else if there is
   exactly one group total, return from it; else throw `KeyNotFoundException` with a message naming the
   groups and telling the caller to qualify as `Group.Cube`.
2. If `spec` has a `.`: split at the **first** `.` into `(group, cube)`. If that group exists, return its
   `cube` (throw if the group lacks that cube). Otherwise fall back to bare-resolve of the whole `spec`
   (so a default-group cube literally named `a.b` still resolves).

Throwing/`false` semantics: `Contains` returns false where the indexer would throw.

## On-disk: flat fields + explicit group metadata

The `.npy` stays a flat NumPy structured array (one field per cube). Grouping is carried in `__meta__`;
the reader never parses field-name structure. Bump `NpyWriter.FormatVersion` `1 → 2`.

### `NpyWriter.cs`
- `FormatVersion = 2`.
- `CollectFields`: iterate `ds.Groups` in order, then each cube in `ds.CubesIn(group)`. Assign a
  **unique** field name per cube: base = `EscapeName(cube)`; if already used, `EscapeName(group)+"."+EscapeName(cube)`;
  if still used, append `"~"+n`. Keep a `fieldName → (group, cube)` map for the data + meta passes. (The
  field name is opaque; uniqueness is its only job.)
- `WriteFieldData`: for a cube field, look up `(group,cube)` and fetch `ds.CubesIn(group)[cube]`.
- `BuildMetaJson`: add top-level `"groups":[ ...ds.Groups in order... ]`. For each cube field (keyed by
  its unique field name, as today) add `"group":"<group>"` and `"cube":"<original cube name>"` alongside
  the existing `"kind"`/`"axes"`. Group `""` serializes as the empty string.

### `NpyReader.cs`
- Section 6 (DataSet reconstruction): read top-level `groups` (ordered); pre-create each group so order is
  preserved. For each cube field, read its meta `group` and `cube`, and call `ds.AddToGroup(group, cube, …)`
  instead of `ds.Add(cubeName, …)`. Field-name unescaping is no longer used to derive the cube name — the
  cube name comes from meta `cube`. Leave `__linnet_*` handling unchanged.
- `format_version` check already references `NpyWriter.FormatVersion`, so it now requires 2 automatically.

### `DataSetExporter.cs`
- `EstimateAndWarn`: the existing `ds.Cubes.Values.Sum(...)` only sees the default group. Change it to sum
  over **all** groups: `ds.Groups.SelectMany(g => ds.CubesIn(g).Values)`. No other change.

### Verify-only (expect no change)
- `DataSetImporter.cs` — thin pass-through to `NpyReader.Read`; should compile unchanged.
- `DataSetBuilder.FromSnp` builds via `ds.Add(...)` → default group (correct: a Touchstone source is one
  default group). `ToSnp`/`ClassifyZ0` read bare `"S"`/`"Z0"` → default/sole group (correct).
- `MatWriter.cs` — operates on the default group via `ds.Cubes`; flat/single-analysis `.mat` export still
  works. Grouped `.mat` export is **out of scope** (stage-1 note: it will only see the default group).

## Tests

Existing flat round-trip tests (`NpyRoundTripTests`, `NpyRoundTripAllAnalysesTests`) must stay green
unchanged — default-group DataSets preserve bare `Contains`/indexer/`Cubes`. One required edit:
- `NpyRoundTripTests.Import_WrongFormatVersion_Throws` + `PatchFormatVersionTo0`: change the needle
  `"format_version":1` → `"format_version":2` (the writer now emits 2). The patch still rewrites the last
  digit to `0`; keep the `Assert.Contains("0", …)` and `"not backward-compatible"` assertions.

Add `tests/Engine.Tests/Export/NpyRoundTripGroupedTests.cs` (the stage-1 gate). Build a DataSet via
`AddToGroup` with three groups and a deliberate cross-group name collision:
- `HB1`: `V` (Complex, e.g. [freq×node]) and `I` (Complex).
- `SP1`: `V` (Complex, **same name, different data/shape**) and `S` (Complex [freq,i,j]).
- `measurements`: `Pout` (Real scalar via `DataCube.Scalar`).
Export → import, then assert:
1. `imported.Groups` equals `["HB1","SP1","measurements"]` (order preserved).
2. `ContainsGroup` for each; `CubesIn(g)` key sets match per group.
3. Per cube: `DataKind`, `Rank`, axis names/units/values/labels, and numeric values are **bitwise-equal**
   (mirror the existing `BasicRoundTrip_*` assertions).
4. Qualified resolution: `imported["HB1.V"]` and `imported["SP1.V"]` resolve to the two distinct cubes;
   their values differ.
5. Bare `imported.Contains("V")` is `false` (ambiguous across groups) and `imported["V"]` throws; bare
   `imported.Contains("S")` is `false` too (multiple groups, no default) — qualified `imported["SP1.S"]`
   works. Also cover a flat case: a separate default-group DataSet round-trips with bare `Contains("S")`
   true (one group total).

## Gate
`dotnet test` the `Export` tests (flat + grouped + version-reject) all green; full solution builds clean
under `TreatWarningsAsErrors`. Then stop — stages 2 (run pipeline → one grouped file + measurements group)
and 3 (display `Analysis.Cube` addressing) are separate briefs.
