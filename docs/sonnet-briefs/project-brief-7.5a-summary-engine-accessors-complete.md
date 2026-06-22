---
name: project-brief-7.5a-summary-engine-accessors
description: Phase 7.5a complete: MetricAtCoord + SourceZ + OperatingPoint accessors in LoadpullSurface; BiasV/I cubes in metricNames; 6 gate tests; RfCore only
metadata:
  type: project
---

Phase 7.5a — loadpull summary-table engine accessors (RfCore, headless). Completed 2026-06-21.

**Why:** Summary Table (Phase 7.5) needs per-cell primitives to read metric values at the MXP/MXE
optimum load per frequency. All additions are presence-tolerant (missing cube → null/NaN, never throw).

**Changes in `RfCore/src/Loadpull/LoadpullSurface.cs`:**
- `metricNames` extended to include `BiasVLoad`, `BiasILoad`, `BiasVSrc`, `BiasISrc` so their drive-ups
  are captured and `OperatingPoint` can read them. Safe: absent cubes just skip.
- `FreqSlice.SourceZ Complex?` field added.
- `BuildFreqSlices`: reads optional `ZSource` cube (freq-indexed or single-freq) into `FreqSlice.SourceZ`.
- `MetricAtCoord(freqIdx, metricY, coord, constraint, plane, z0, nearest, kernel, smooth, epsilon) → double`:
  Interp = RBF.Evaluate at coord; Nearest = node value at closest measured node. Returns NaN when absent.
- `SourceZ(freqIdx) → Complex?`: exposes `FreqSlice.SourceZ`; null when `ZSource` cube absent (today).
- `OperatingPoint(freqIdx, cubeName) → double?`: reads bias cube at grid 0 / pinStep 0 (constant over sweep).

**Tests in `RfCore.Tests/LoadpullSurfaceTests.cs` (6 new, all pass):**
1. `MetricAtCoord_Interp_EqualsSurfaceEval` — accessor == Fit.Rbf.Evaluate at MXP optimum.
2. `MetricAtCoord_Nearest_ReturnsNodeValue` — result is one of the measured node values.
3. `MetricAtCoord_AbsentMetric_ReturnsNaN` — bogus metric → NaN.
4. `OperatingPoint_AbsentCube_ReturnsNull` — missing cube → null.
5. `OperatingPoint_PresentCube_ReturnsFiniteOrAbsent` — presence-tolerant (BiasVLoad may not be in fixture).
6. `SourceZ_Absent_ReturnsNull` — standard .spl fixture lacks ZSource → null.

Pre-existing failure (`MaxPower_DifferentKernels`) is unrelated to this phase (confirmed by stash check).

**How to apply:** Phase 7.5b (model + persistence) and 7.5c (TableRenderer) consume these primitives.
