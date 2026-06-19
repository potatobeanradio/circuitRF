---
name: project-brief-grouped-dataset-stage1
description: Grouped DataSet stage 1 — DataSet group model + NpyWriter/Reader group metadata + format_version 2 + round-trip tests
metadata:
  type: project
---

Stage 1 of grouped DataSet (docs/design/results-dataset-layout.md). All changes in RfCore + circuitRF.

**What shipped:**
- `DataSet.cs`: replaced flat `_cubes` dict with `_groupOrder + _groups` ordered map. Added `const DefaultGroup = ""`, `AddToGroup`, `Groups`, `ContainsGroup`, `CubesIn`. Resolution rule: bare name → default group, then sole group, else throw; qualified `Group.Cube` → named group with dot-split fallback. `Add`/`Cubes`/`StackSweepAxis` all routed through default group — no callers changed.
- `NpyWriter.cs`: `FormatVersion` 1→2. New `CubeMapping` record + `BuildCubeMappings` for uniquified field names (base=cube, then group.cube, then group.cube~N). `BuildMetaJson` emits top-level `groups` array + per-cube `group`/`cube` keys. `CollectFields`/`WriteFieldData` use mapping.
- `NpyReader.cs`: reads `groups` array and calls `ds.RegisterGroup` before iterating cubes. Per-cube reads `group`/`cube` from meta and calls `ds.AddToGroup`.
- `DataSetExporter.cs`: `EstimateAndWarn` sums over `ds.Groups.SelectMany(g => ds.CubesIn(g).Values)`.
- `NpyRoundTripTests.cs`: patched `PatchFormatVersionTo0` needle `"format_version":1` → `"format_version":2`.
- `NpyRoundTripGroupedTests.cs` (new): 3-group DataSet with cross-group "V" collision; 11 tests covering group order, ContainsGroup, CubesIn key sets, per-cube bitwise round-trip, qualified resolution, bare ambiguity, and flat-DataSet baseline.

**Result:** 1690/1690 tests pass; clean build under TreatWarningsAsErrors.

**Why:** Grouped DataSet is stage 1 of 4; stages 2 (run pipeline), 3 (display addressing), 4 (docs) are separate.
