---
name: project-brief-7.4h-5-contour-ux-round5
description: Phase 7.4h-5: 12-item contour UX sprint — label leaks §1/§2, zoom-independent labels/glyphs §3/§8, iso-line single-color §4/§5, Count default §6, Colormap onto Fill row §7, metric sort+filter §9/§10; 12 gate tests; 2104 total — completed 2026-06-21
metadata:
  type: project
---

Phase 7.4h round 5 complete. 12 items across 5 slices.

**Why:** Fix contour UX correctness bugs, zoom-independence, and improve metric list usability.

**How to apply:** All contour UX in DataDisplay now works correctly. Load a loadpull file, add contour trace → Count mode active, Colormap+Fill share one row, metric list is priority-sorted and variation-filtered.

## Changes

### Slice 5a — Label leaks §1 §2
- `Trace.RectYLabel` returns `""` for contour traces (defense-in-depth)
- `AxesRenderer.DrawTitleAndAxisLabels`: gates per-trace fallback on `!CustomYLabelOn`; filters `IsContourTrace`
- `PlotContainerViewModel.UpdateLabelStrips`: treats `CustomYLabelOn=true` as suppressing per-trace strips even when empty; filters contour traces from leftTraces/rightTraces
- Tests T31–T34 (4 gate tests)

### Slice 5b — Iso-line single color §4 §5 + Count default §6
- `TraceRowViewModel.OnContourColorMapChanged`: adds `cd.LineColorOverridden = false`
- `ContourRenderer.DrawIsoLines`: moves baseLineColor computation before loop, single `tPos=0.5` pick
- `ContourData.LevelMode` default: `Range` → `Count`
- `PlotInspectorViewModel.AddContourTrace`: adds `LevelMode = ContourLevelMode.Count`
- Tests T35–T37 (3 gate tests)

### Slice 5c — Zoom independence §3 §8
- `DrawIsoLines`: adds `float zoomLevel` param; walks labels in world coordinates (uses `pl.Points` directly)
- `DrawOptimaMarkers`/`DrawOptimumMarker`: adds `zoomLevel`; divides `r=7f`, `sw=1.5f`, `fs=9f` by zoom
- `PlotRenderer.Draw`: forwards `zoomLevel` to both ContourRenderer calls
- No unit tests (owner-verified)

### Slice 5d — Colormap onto Fill row §7
- `PlotInspectorView.axaml`: wraps Fill row in `Grid ColumnDefinitions="60,*"` with Colormap ComboBox (Width=60) as column 0, IconSelectButton as column 1; removes separate Colormap row
- No unit tests (AXAML layout)

### Slice 5e — Metric sort/filter §9 §10
- `TraceRowViewModel.RebuildMetricList`: priority+alias sort table (`MetricPriority` static); `DataCube.CubeVaries` filter for non-varying fields
- `DataCube.CubeVaries(DataCube, epsilon=1e-9)` static method added to RfCore (real+complex, early-out, relative epsilon)
- Tests T38–T39 in Ui.Tests + 5 `CubeVaries` tests in RfCore.Tests (total 12 gate tests)

## Test counts
- Ui.Tests: 1351 total (was 1312 before this phase)
- RfCore.Tests: 201 total (was 196)
- Engine.Tests: 413 total (unchanged)
- Firewall.Tests: 4 total (unchanged)
