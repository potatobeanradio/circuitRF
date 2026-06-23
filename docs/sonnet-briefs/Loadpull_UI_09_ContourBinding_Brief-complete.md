---
name: project-brief-loadpull-ui-09-contour-binding
description: Loadpull UI 09 — group-aware LoadpullSurface binding so a simulated LP run.npy renders contours; completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 09 — group-aware contour/surface binding for a simulated LP run.npy — COMPLETE 2026-06-23

With recognition in place ([[project-brief-loadpull-ui-08-datadisplay-recognition]]), made the contour +
summary `LoadpullSurface` build **group-aware** so a simulated LP `run.npy` (cubes under an analysis group
like `LP1`) renders contours identically to a flat `.spl`/`.lpcwave`. Data Display wiring only — no engine,
model, or on-disk format change.

**Key finding:** `RfCore.Loadpull.LoadpullSurface(DataSet data, string group = "")` **already** supported a
group — `BuildFreqSlices` reads `data["{group}.{name}"]` (the DataSet indexer resolves group-qualified
specs). The two UI call sites were passing the default (top-level) and so found nothing for a grouped
source. The fix is to resolve the group and pass it.

**Edits (Data Display VMs only):**
- `TraceRowViewModel.EnsureLoadpullSurface`: resolve `LoadpullRecognition.FindLoadpullViews(ds)` → first
  view's `Group ?? ""`; build `new LoadpullSurface(ds, group)`. Added `_surfaceGroup` to the cache
  staleness key (alongside `_surfaceSourcePath`) so re-binding to a different group (LP1→LP2) rebuilds.
  Default = first loadpull view; `""` = top level (flat path unchanged).
- `PlotInspectorViewModel` summary-table build: same `FindLoadpullViews` → `new LoadpullSurface(ds,
  lpGroup)`.
- **Already group-aware, no change:** `RebuildMetricList` (iterates `ds.Groups`, dedups bare metric names);
  `HasCube` (iterates `ds.Groups`); the eligibility gate `IsLoadpullSource` (brief 08). The surface resolves
  bare metric names against its cached group, so the metric→surface path is consistent.

**Units:** both producers feed `Pout` in Watts; the surface owns the W→dBm contour conversion — no
double-conversion (parity test asserts identical resampled-grid max).

**Gate:** 1 parity test in `tests/Ui.Tests/LoadpullContourGroupParityTests.cs` — loads the real
`testdata/spl_test_data/Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` (flat), re-keys every cube under `LP1`
(`AddToGroup`, no data copy), then asserts `LoadpullSurface(flat,"")` ≡ `LoadpullSurface(grouped,"LP1")`:
same Frequencies/GridPointCount, identical MXP `Interpolated` coord (Pout @ 3 dB), identical resampled
Pout grid max (W). Skips cleanly if the fixture is absent (mirrors RfCore tests). Recognition is
re-asserted (`FindLoadpullViews(grouped)` → `LP1`). Build 0W/0E; Core 382 / Ui 1461(+1) / Engine 442(+1
skip) / Firewall 4 — all green. **Loadpull UI series (briefs 01–09 + 04b) COMPLETE.**

**Manual e2e (owner):** run the LP analysis → its `run.npy` is recognized → add a contour trace → metric
picker offers Pout/Gain/Efficiency → Smith contours + MXP/MXE render, visually identical to a `.spl`.

## Follow-up fix — contour metric list (2026-06-23)

**Symptom (user):** on a simulated LP run the contour metric picker showed bookkeeping cubes
(`isTickle`, `PavlDbm`) and omitted headline FOMs (`Pout`/`DE`/`PAE`). **Root cause:**
`TraceRowViewModel.RebuildMetricList` filtered only `GammaLoad`/`__`-prefixed + a `gridPoint` axis +
`CubeVaries`. The simulation-only bookkeeping cubes (`Converged`/`IsTickle`/`StopCode`/`PavlDbm`) all
have a `gridPoint` axis and vary, so they leaked (measured `.spl`/`.lpcwave` carry none of them — hence
sim-only). And the §10 `CubeVaries` gate hid genuine FOMs when flat (DE/PAE are 0 without a bias-tee).
**Fix:** two static sets in `TraceRowViewModel` — `NonMetricCubes` = {GammaLoad, Converged, IsTickle,
StopCode, PavlDbm} (always excluded) and `KnownFomCubes` = {Pout, Gt, Gp, DE, PAE} (always offered,
exempt from the `CubeVaries` gate). Other cubes (ZLoad, Pdc, Bias*, custom) still gated by `CubeVaries`.
1 regression test `tests/Ui.Tests/LoadpullMetricListTests.cs` (synthetic grouped LP `.npy` →
DataSetExporter → library → inspector → contour trace; asserts FOMs present incl. flat DE/PAE, Pout
first, bookkeeping excluded). **Known gap (not this fix):** importer-derived FOMs — `Pout_dBm`,
`IRL`, `Zin_real`/`Zin_imag`, `AMPM` — are computed by the `.spl`/`.lpcwave` reader (brief-7.5g), NOT by
the LP engine, so they're absent for simulated runs; computing them in the run path is a separate task.
Build 0W/0E; Ui 1462 / Core 382 / Engine 442(+1 skip) / Firewall 4 — all green.
