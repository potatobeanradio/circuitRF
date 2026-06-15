---
name: project-brief-engine-diagnostics-channel
description: Engine diagnostics channel — ElaboratedNetlist.AddWarning/AddWarningOnce surfacing S-param regularization + HB non-convergence to UI Messages pane — completed 2026-06-15
metadata:
  type: project
---

Phase brief-engine-diagnostics-channel: Uniform engine-diagnostics channel. Core-level `ElaboratedNetlist.AddWarning(message)` and `AddWarningOnce(key, message)` (deduplicates via `_seenWarningKeys` HashSet). Engine never touches `IMessageSink`.

**Why:** Surface S-param regularization and HB non-convergence warnings to the user in the Messages pane, once per run, with full diagnostic detail. Firewall constraint means Core/Engine can't reference UI types.

**How to apply:** To add new engine warnings, call `netlist.AddWarning(...)` or `netlist.AddWarningOnce(key, ...)` from the engine. `SchematicRunService` drains them; `WorkspaceViewModel` posts them at Warning level.

Key points:
- `SParameterEngine` emits `"sparam-regularization"` warning (once per run) on IfNecessary retry; message includes `SingularMatrixException` detail with floating node names
- `HbEngine.Run`/`RunTwoTone` accumulate ncCount/worstRes/totalPoints across all sweep points (no-sweep = `Enumerable.Repeat(0.0, 1)` → totalPoints=1), emit ONE summary warning after the loop if ncCount > 0
- `SchematicRunService.RunResult.Warnings` drains `nl.Warnings` after dispatch (even on EngineError)
- `WorkspaceViewModel.RunAnalysis`: `foreach (var w in result.Warnings) Messages.Warning(w);`
- Gated by 4 tests: `EngineDiagnosticsChannelTests` (T1 floating node, T2 HB MaxIter=1) + `SchematicRunServiceTests` (L1e non-empty warnings, L1f empty warnings)
- Build 0W/0E; 1224 tests pass — completed 2026-06-15
