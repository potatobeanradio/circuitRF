---
name: project-brief-loadpull-ui-08-datadisplay-recognition
description: Loadpull UI 08 — shape-based group-aware LoadpullRecognition so a simulated LP run.npy is recognized as loadpull; completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 08 — recognize a simulated LP run.npy as a loadpull source — COMPLETE 2026-06-23

Made the Data Display recognize a simulated **Loadpull** result (`run.npy` group, e.g. `LP1`) as a
loadpull dataset — eligible for a contour/summary trace — identically to an ingested flat `.spl`/
`.lpcwave`. Recognition is now **shape-based and group-aware**, not source-kind-gated. Headless,
framework-free.

**New file:** `src/Ui/DataDisplay/Models/LoadpullRecognition.cs` (namespace `CircuitRF.Ui.DataDisplay`,
operates on `RfCore.Data` DataSet/DataCube — framework-free).
- `LoadpullView(string? Group)` — `Group == null` = top level/DefaultGroup; else the named group
  (carry forward for the surface builder — brief 09).
- `FindLoadpullViews(DataSet) → IReadOnlyList<LoadpullView>` — iterates `ds.Groups`; for each, requires
  the canonical signature within that group: **(1)** a termination cube `GammaLoad` OR `ZLoad` of rank-1
  over axis `gridPoint`, **AND (2)** a FOM cube (`Pout`/`Gt`/`Gp`/`DE`/`PAE`) of rank-2 over axes
  `{gridPoint, pinStep}` in order. Ordinal casing (matches `BuildLoadpullDataSet`). DefaultGroup `""`→null.
- `IsLoadpull(DataSet) => FindLoadpullViews(ds).Count > 0`.

**Wired:** `PlotInspectorViewModel.IsLoadpullSource` (the contour/summary eligibility gate behind
`CanAddContourTrace`/`CanAddSummaryTrace`) replaced its loose `ds.Groups.Any(g => CubesIn(g).ContainsKey(
"GammaLoad"))` (no axis-shape check, no ZLoad acceptance) with
`e.Kind is SourceKind.Spl or SourceKind.Lpcwave || LoadpullRecognition.IsLoadpull(ds)`. The SourceKind
fast path keeps measured files eligible even with an unusual cube layout; the shape check brings in
`SourceKind.Npy` LP run results.

**Note:** the data needs no change — the LP engine already emits the canonical shape and
`SchematicRunService`/`RunResultsWriter` nest it under the analysis-name group. Surface construction's
group-awareness (building `LoadpullSurface` from the located `Group`) + the render gate are brief 09.

**Gate:** 10 tests in `tests/Ui.Tests/LoadpullRecognitionTests.cs` (flat→top-level view; grouped→`LP1`
view; two groups→two views; ZLoad-only accepted; HB/DC/S-param negatives; near-misses: FOM-without-
termination, termination-on-wrong-axis, termination-without-FOM). Build 0W/0E; Core 382 / Ui 1460(+10) /
Engine 442(+1 skip) / Firewall 4 — all green.
