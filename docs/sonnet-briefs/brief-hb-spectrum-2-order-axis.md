---
name: project-brief-hb-spectrum-2-order-axis
description: Stage 2 harmonic axis flip: integer orders (unit ""); Trace reconstructs freq from ToneFreqs via SetSpectrumFundamentals; HarmonicOrderOf removed; 6 gate tests — completed 2026-06-23
metadata:
  type: project
---

Stage 2: single-tone HB `harmonic` axis now stores integer orders `[0,1,…,K]` (unit `""`, never Hz). Physical frequency reconstructed `order × f0(slice)` everywhere via `HbSpectrum.HarmonicFreqHz` and `ToneFreqs`.

**Engine (Part A):** `HbEngine.BuildSingleToneDataSet` — `harmVals[k] = k` (was `k * f0`), axis unit `""` (was `"Hz"`).

**Owner injection (Part B):** `PlotInspectorViewModel` adds `GetToneFreqsCube` + `ResolveFundamentalByX` helpers. Calls `t.SetSpectrumFundamentals(f0ByX)` immediately before `t.SetCubeData(...)` and `t.SetFamilyData(...)` in all three paths (expression: null; single-slice; family). `ResolveFamily` gains `DataSet? ds` optional param.

**Trace reconstruction (Part C):** New `_f0ByX` field + `SetSpectrumFundamentals` setter (and clone ctor copy). `BuildCubePath`: harmonic X uses `order × _f0ByX[i] × freqScale`. `SetFamilyData`: harmonic X points likewise. `BuildCubeMarkerBoxLines` family branch: `harmonic={order}` + conditional `freq=… GHz`. X branch: `freq=…` first (if `_f0ByX`), then `harmonic={order}`. `GetStemFreqString`: reconstructs from `_f0ByX`. `HarmonicOrderOf` removed. Harmonic axis matched by name, not freq unit.

**X label (Part D):** `Plot.XLabel` adds harmonic-name case → `freq (GHz)`.

**Tests updated:** `PickerExcludesToneFreqsTests.cs`, `CubeTraceTests.cs`, `SparamRunAddTraceTests.cs` (harmonic axis → integer orders, unit `""`). T3 + T5.B in `MarkerSweepFreqLabelTests.cs` rewritten for stage-2 semantics + `SetSpectrumFundamentals`.

**New gate tests:** `HbOrderAxisTests.cs` (Engine T1: axis values are orders, unit `""`), `HbSpectrumStage2Tests.cs` (Ui T2–T6: regression/swept-f0/geometry/non-harmonic/table).

Build 0W/0E; 429+376+1409+4 total tests pass.

**Why:** frozen `k·f0` harmonic axis breaks per-sweep-point display when fundamental is swept; integer orders + `ToneFreqs` give correct frequency at every operating point.

**How to apply:** two-tone `mixIndex` follows same pattern (deferred); physical-frequency column in Table is a later polish. The harmonic axis is now always matched by NAME ("harmonic"), never by being a frequency unit.
