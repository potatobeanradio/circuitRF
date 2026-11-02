---
name: project-brief-grouped-dataset-stage2
description: Grouped DataSet stage 2: one run.npy per schematic, grouped assembly in SchematicRunService, WriteRun replaces WriteResults — completed 2026-06-18
metadata:
  type: project
---

Grouped DataSet stage 2 complete.

**What changed:**
- `MeasurementContext.cs`: removed `_accessed`, `AccessedAnalyses`, `ResetAccessLog()` — reverted to simple `GetAnalysis` accessor
- `MeasurementEvaluator.cs`: deleted `EvaluateIntoReferencedAnalyses()`; simplified `Evaluate` to `Action<Measurement, Value>` (no access-log parameter)
- `SchematicRunService.cs`: `RunResult` gets `DataSet? GroupedResults`; measurement block replaced with grouped assembly (one group per analysis + `"measurements"` group)
- `RunResultsWriter.cs`: `WriteResults` replaced by `WriteRun(... DataSet? grouped ...)` writing single `run.npy`
- `WorkspaceViewModel.cs`: call site updated to `WriteRun(result.GroupedResults, ...)`
- `RunResultsWriterTests.cs`: rewritten to one-file model (7 `WriteRun_*` tests + 4 `SchematicKey_*` tests kept)
- `SchematicRunServiceTests.cs`: added `MeasurementsAndAnalyses_AssembleIntoGroupedResults`

**Why:** Stage 2 of grouped DataSet — produce one `run.npy` per run instead of one per analysis. Measurements land in the `"measurements"` group; per-analysis attribution machinery removed.

**How to apply:** After stage 2, Data Display still addresses cubes by bare name (shows nothing from run.npy until stage 3 adds group-aware addressing). Load is crash-safe.

Gate: 0W/0E build; 1692 tests pass (291 Core + 302 Engine + 1095 Ui + 4 Firewall).
