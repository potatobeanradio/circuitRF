---
name: project-brief-node-picker-labeled-filter
description: Node-picker labeled filter — cube node axis filtered to user-labeled nets, provenance-based, 5 hops, 11 gate tests — completed 2026-06-16
metadata:
  type: project
---

Node-picker labeled filter — completed 2026-06-16.

**Why:** Auto-generated net names like `n1`, `n2` polluted the node-axis picker in Data Display cube traces, making it hard to find meaningful signals. The fix filters the picker to only nodes that came from a user-placed schematic net label.

**How to apply:** Provenance is threaded through 5 hops:
1. `NetExtractor.AssignNetNames` → `TestBench.LabeledNets` (names whose final assigned name matches the label text after Pin-override check)
2. `Elaborator.Elaborate` copies `tb.LabeledNets` → `netlist.Nodes.LabeledNames` (top-level only)
3. `HbEngine.BuildSingleToneDataSet`/`BuildTwoToneDataSet` emit `__LabeledNodes` metadata cube (axis "label" with Labels = labeled node names that appear in the node axis; values = zeros)
4. `.npy` round-trip is automatic (generic DataSet exporter handles it)
5. `TraceRowViewModel.RebuildAxisRoles` reads `__LabeledNodes` and filters the "node" axis options to labeled names; parallel `PinOptionIndices[]` maps display-row → true cube-axis index; `TruePinIndex` used in `FlushSliceAndRebuild`

**Key behaviors:**
- Filter ON by default (`ShowAllNodes = false`)
- `__LabeledNodes` absent (hand-written CNL) → `ShowAllNodes = true` by default (show-all escape hatch)
- `__LabeledNodes` present-but-empty (schematic ran, user tagged nothing) → picker shows nothing
- "Show all nodes" toggle on trace card for cubes with a node axis
- `__`-prefixed cubes skipped by `RebuildSignals` signal list (never shown as selectable signals)
- `_rebuildingAxisRoles` flag prevents re-entrancy when `ShowAllNodes = true` is set inside `RebuildAxisRoles`

**Files changed:** `TestBench.cs`, `NodeMap.cs`, `Elaborator.cs`, `NetExtractor.cs`, `HbEngine.cs`, `TraceRowViewModel.cs`, `AxisRoleRowViewModel.cs`, `PlotInspectorView.axaml`, `src/Ui/CLAUDE.md`, `src/Engine/CLAUDE.md`

**Tests:** 11 gate tests — `NodePickerLabeledFilterTests.cs` (Ui.Tests, T1–T3, T5, T7–T11) + `HbLabeledNodesCubeTests.cs` (Engine.Tests, T4, T6). Build 0W/0E; 1460 total tests pass.
