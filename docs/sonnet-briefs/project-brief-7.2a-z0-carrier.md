---
name: project-brief-7.2a-z0-carrier
description: Phase 7.2a — Z0 carrier cube + producer fix (DataSetBuilder + SParameterEngine)
metadata:
  type: project
---

Phase 7.2a delivered the per-port `Z0` complex cube to every S-parameter DataSet.

**Why:** `SParameterEngine.Run` already computed S with per-port complex Z0 (`YToS(yMat, z0PerPort)`) but discarded the per-port info — collapsing to `z0PerPort[0]` and letting `FromSnp` store nothing. A user who set non-uniform/complex Term `Z` got correct S values but a silently mis-recorded reference impedance. The fix carries the true per-port data.

**What shipped:**
- `Z0Kind` enum (`UniformReal`, `UniformComplex`, `NonUniform`) in `RfCore.Data`.
- `DataSetBuilder.BuildZ0Cube(Complex[])` — builds the 1-axis complex cube with 1-based `port` axis.
- `DataSetBuilder.ClassifyZ0(DataCube)` — headless helper for the later UI indicator (Phase 7.2e).
- `DataSetBuilder.FromSnp` — now also emits a uniform Z0 cube (all entries = `snp.Z0`); every S DataSet has a `Z0` cube.
- `DataSetBuilder.ToSnp` — reads `Z0` cube: uniform → `SNP.Z0`; non-uniform → port-1 + `RFNetwork.Warn`; absent → 50 Ω.
- `RFNetwork.Warn` promoted from `private` to `internal` so `DataSetBuilder` can call it.
- `SParameterEngine.Run` — after `DataSetBuilder.FromSnp(snp)`, overwrites uniform placeholder with `DataSetBuilder.BuildZ0Cube(z0PerPort)`.

**Tests added:** 12 in `RfCore.Tests/DataSetBuilderZ0Tests.cs` (gates 1/3/4/5) + 5 in `Engine.Tests/Linear/Z0CubeTests.cs` (gate 2). Full suite green.

**How to apply:** Next brief is 7.2b — data-source library (`file → DataSet`, generalising `SnpLibrary`).

Completed: 2026-06-15
