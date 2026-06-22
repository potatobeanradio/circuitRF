---
name: project-brief-7.5b-summary-table-model-persistence
description: Phase 7.5b complete: SummaryTableTypes + SummaryColumnData + SummaryColumns + Plot/Trace/Config/VM round-trip; 1379 Ui.Tests pass
metadata:
  type: project
---

Phase 7.5b — summary-table model + persistence (src/Ui). Completed 2026-06-21.

**Why:** Provides the data structures and .cdd round-trip that the renderer (7.5c), header controls
(7.5d), and auto-fill (7.5e) build on. No UI controls or rendering in this slice.

**New files:**
- `src/Ui/DataDisplay/Models/SummaryTableTypes.cs` — `TableOptimum { Mxp, Mxe }` and
  `TableReadMode { Interp, Nearest }` enums.
- `src/Ui/DataDisplay/Models/SummaryColumnData.cs` — `SummaryColumnKind` enum (Metric, Zload,
  Zsource, Zin, OperatingPoint) + `SummaryColumnData` class with Clone().
- `src/Ui/DataDisplay/Models/SummaryColumns.cs` — static `AutoHeader`, `MetricHeader`,
  `IsComplexColumn`, `FreqHeader` helpers (shared by renderer + auto-fill).

**Modified files:**
- `Plot.cs` — added `TableOptimum`, `TableReadMode`, `TableCompression` in the Table view region.
- `Trace.cs` — added `SummaryColumn?`, `IsSummaryColumn`, and `SummaryColumn?.Clone()` in copy ctor.
- `DataDisplayConfig.cs` — added `SummaryColumnConfig` record; `TableOptimum`/`TableReadMode`/
  `TableCompression` on `PlotContainerConfig`; `SummaryColumn?` on `TraceConfig`.
- `DataDisplayViewModel.cs` — `BuildPlotContainerConfig` emits the three table-wide fields;
  `BuildTraceConfig` emits `SummaryColumn` block; `LoadPlotContainerConfigAsync` restores
  table-wide fields and handles the `isSummaryTrace` branch (parallel to `isContourTrace`).

**Round-trip:** a Table Plot with summary traces persists/reloads correctly; a pre-7.5 .cdd loads
as a normal Table with defaults (TableOptimum=Mxp, TableReadMode=Interp, TableCompression=3.0).

**Build:** 0W/0E in Ui (2 pre-existing RfCore CS0649 warnings from 7.5g, unchanged).
**Tests:** 1379 Ui.Tests pass.

**How to apply:** 7.5c (TableRenderer) reads `Plot.TableOptimum`/`TableReadMode`/`TableCompression`
and `Trace.SummaryColumn` to render summary columns. 7.5d (header controls) mutates the Plot-level
fields. 7.5e (auto-fill) uses `SummaryColumns.AutoHeader`/`MetricHeader` to populate column headers.
