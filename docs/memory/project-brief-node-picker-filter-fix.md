---
name: project-brief-node-picker-filter-fix
description: Node-picker filter fix: StackSweepAxis passes __-prefixed metadata cubes unstacked; picker reads label axis by name — completed 2026-06-16
metadata:
  type: project
---

Node-picker filter fix (brief-node-picker-filter-fix) — COMPLETE 2026-06-16.

**Fix 1a:** `DataSet.StackSweepAxis` (RfCore/src/Data/DataSet.cs) skips `__`-prefixed cubes from `PrependAxis` and passes them through verbatim from `datasets[0]`. Root cause: stacking prepended the sweep axis to `__LabeledNodes`, making it rank-2; `Axes[0].Labels` was then the numeric sweep axis (null labels) → filter showed nothing or all nodes.

**Fix 1b:** `TraceRowViewModel.RebuildAxisRolesCore` (src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs, ~line 899) now finds the label axis by `Name == "label"` (with fallback to first axis with Labels), not positional `Axes[0]`. Defensive against any future axis shape change.

**Bug 2 (already implemented):** Table column double-click → `PlotControl.HandleDoubleTapAt` already routes `TableHitKind.TraceHeader` to `ShowPlotInspector(idx)` + `_inspectorView?.FocusSpecTextBox(idx)` at PlotControl.cs:1008-1012.

**Tests (4 new):** `Stack_PreservesLabeledNodesShape`, `Stack_MetaCubeNotSwept` (Engine.Tests/HarmonicBalance/HbLabeledNodesCubeTests.cs); `Picker_FiltersAfterSweep` (Ui.Tests/NodePickerLabeledFilterTests.cs); `Table_TraceHeader_HitTest_ReturnsTraceHeaderKind` (Ui.Tests/TraceCardLayoutTests.cs).

**Why:** `ParametricSweepEngine.StackSweepAxis` was treating all cubes uniformly, but `__`-prefixed cubes are sweep-invariant metadata and must not have a sweep axis prepended.

**How to apply:** Any future metadata cube with `__` prefix is automatically passed through unstacked.
