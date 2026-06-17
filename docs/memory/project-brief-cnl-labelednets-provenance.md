---
name: project-brief-cnl-labelednets-provenance
description: CNL labelednets directive — persists TestBench.LabeledNets through .cnl round-trip so node-picker filter survives the GUI run path — completed 2026-06-16
metadata:
  type: project
---

CNL net-label provenance fix (brief-cnl-labelednets-provenance) — COMPLETE 2026-06-16.

**Root cause:** `CnlWriter` never emitted `TestBench.LabeledNets`. The GUI run path is schematic → `NetExtractor` (populates `LabeledNets`) → `CnlWriter.Write` → `.cnl` file → `CnlReader.ReadFile` → engine. After the round-trip, `tb.LabeledNets` was always empty → `HbEngine` skipped `__LabeledNodes` → picker showed all nodes (n1/n2/n3 appeared).

**Fix 1 — CnlWriter.cs:** appends `labelednets n1 n2 …` (sorted by Ordinal, top-level) when `tb.LabeledNets.Count > 0`.

**Fix 2 — CnlReader.cs:** new branch in `TryParseLine` before `define`: parses `labelednets name1 name2 …` into `_testBench!.LabeledNets`. Throws `CnlReadException` if encountered inside a `define…end` block.

**Write path confirmed:** `WorkspaceViewModel.WriteNetlist` at line 1177 calls `CnlWriter.Write(result.TestBench, result.Library, header)` where `result` comes from `NetExtractor` — the fix is in the right place.

**Bug 2 status:** Already implemented. `PlotControl.HandleDoubleTapAt` at lines 1008-1012 already calls `ShowPlotInspector(idx)` AND `_inspectorView?.FocusSpecTextBox(idx)` for `TableHitKind.TraceHeader`. `FocusSpecTextBox` exists in `PlotInspectorView.axaml.cs:86`. Fix landed as part of `brief-table-cube-layout-fixes` (#5). The brief was authored before that fix landed.

**Tests (4 new):**
- `Cnl_RoundTrips_LabeledNets` (Core.Tests/Netlist/CnlWriterTests.cs)
- `Cnl_NoLabeledNets_NoDirective` (Core.Tests/Netlist/CnlWriterTests.cs)
- `Cnl_LabeledNets_InsideDefine_Throws` (Core.Tests/Netlist/CnlWriterTests.cs)
- `T7_EndToEnd_SchematicCnl_EmitsLabeledNodesCube` (Engine.Tests/HarmonicBalance/HbLabeledNodesCubeTests.cs)

**Why:** `HbLabeledNodesCubeTests` (T4/T6) only injected LabeledNets in-memory and missed the `.cnl` round-trip gap. T7 is the regression guard for the full GUI run path.

**How to apply:** Any future `.cnl` top-level provenance directive follows the same pattern: emit in `CnlWriter.Write` after raw directives; parse in `CnlReader.TryParseLine` before `define`; throw if inside a `define` block.
