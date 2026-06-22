---
name: project-brief-7.5g-loadpull-importer-derived-fields
description: Phase 7.5g complete: Zin_real/Zin_imag, AMPM, IRL derived at import in SplReader+LpcwaveReader; ZSource rank-1 {freq} cube; 6 gate tests; RfCore only
metadata:
  type: project
---

Phase 7.5g — loadpull importer derived fields (RfCore, headless). Completed 2026-06-21.

**Why:** Summary Table (Phase 7.5) needs Zin, AMPM, IRL, and ZSource available as cubes so the
UI can display them without re-deriving from raw columns. Computed once at import time.

**New file `RfCore/src/Loadpull/LoadpullDerivedFields.cs`:**
- `Derive(foms, nGrid, nPin, ginMag, ginPhaseDeg, transPhaseDeg, reflDb, reflLin)` — shared helper.
  - `Zin_real`/`Zin_imag`: from Γin mag+phase → `RfHelpers.G2Z(gin)*50`. Presence-gated on ginMag+ginPhaseDeg.
  - `AMPM`: per-grid drive-up relative-phase (first−current), then `UnwrapDegInPlace`. Gated on transPhaseDeg.
  - `IRL`: priority reflDb → reflLin (20log10) → ginMag (−20log10). Gated per input.
  - All outputs guarded by `!foms.ContainsKey(...)` (Part C presence guard, no overwrite).
- `UnwrapDegInPlace(deg)` — degree-domain phase unwrap, NaN-aware.

**`SplReader.cs` changes:**
- `FreqBlock` extended with `GinMag/GinPhase/TransPhase/ReflDb/ReflLin` (double[]?) and `SourceGamma`/`HasSourceGamma`.
- `ParseFreqBlock`: post-fill pass detects column indices for raw inputs using first-present-wins (colIdx OrdinalIgnoreCase dict). Captures source Γ from first row (RI pair `gamma_src1_real`/`gamma_src1_imag` or MA pair `|GS@F0|`/`PhiS@F0[deg]`). Fills per-sample arrays. Calls `LoadpullDerivedFields.Derive(...)`.
- `AssembleDataSet`: adds `ZSource` rank-1 `{freq}` complex cube when `HasSourceGamma`.

**`LpcwaveReader.cs` changes:** same pattern using relative indices `i - dataOffset`. Detects `|GinWaves@F0|`/`PhiinWaves@F0[deg]`, `PhiLWaves@F0[deg]`, `|GS@F0|`/`PhiS@F0[deg]`.

**`LoadpullSurface.cs` fix (from 7.5a):** `BuildFreqSlices` ZSource reader changed from `zc[fi, 0]` to `zc[fi]` — ZSource is always rank-1 `{freq}` from 7.5g importers.

**`LoadpullSurfaceTests.cs`:** `SourceZ_Absent_ReturnsNull` updated to `SourceZ_PresentAfterImport_ReturnsFiniteValue` since standard fixture now has `gamma_src1` → ZSource.

**Tests in `RfCore.Tests/LoadpullDerivedFieldsTests.cs` (6 new, all pass):**
1. `UnwrapDegInPlace_WrapsCorrectly` — pure math; 0→90→180→270→-80 unwraps to 280.
2. `Spl_Ideal_DerivedCubesPresent` — all four derived cubes present; shapes match Pout.
3. `Spl_Ideal_ZSourceCubePresentAndRankOne` — rank-1 freq axis, DataKind.Complex.
4. `Spl_Ideal_ZSourceValueFiniteAndPlausible` — real part in (0, 500) Ω.
5. `Spl_ConvertedFile_DerivedCubesOrGracefulAbsence` — lpcwave-style columns; no throw.
6. `Lpcwave_DerivedCubesPresent` — Zin, AMPM, ZSource present; ZSource ≈ 27.38+21.17j Ω (±1Ω).

RfCore.Tests: 214 pass / 1 pre-existing fail (MaxPower_DifferentKernels, unrelated to 7.5g).
Ui.Tests: 1379 pass / 0 fail.

**How to apply:** Phase 7.5b (model + persistence) and 7.5c (TableRenderer) consume ZSource via `LoadpullSurface.SourceZ(freqIdx)` and Zin/AMPM/IRL directly from `DataSet`.
