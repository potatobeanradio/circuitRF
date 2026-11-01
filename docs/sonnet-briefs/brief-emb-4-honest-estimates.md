# Brief EMB-4 — Honest estimates: size, memory and time a run will take, said before it starts

**Series:** EMB-0 … EMB-5 · **Tag:** `R-emb4-<n>`
**Area:** `src/Design/Em3d/Solvers/Palace/Em3dRunService.Palace.cs` (`EstimatePalace`), `src/Cli/ExplainEm3d.cs`,
`src/Engine/Mom/SurfaceMesher.cs` (budget sentences ~1804, `UsesAcceleratedCeiling` ~228, the core-count gate),
`src/Engine/Mom/PlanarCoreFootprint.cs` (`DistinctBandKeys`), `src/Engine/Mom/PlanarAimBordered.cs` (`FactorNear`),
`src/Engine/Mom/Dcim.cs` (`ValidatedRhoOverLambdaAtHeights`), `src/Design/Layout/Em/EmRunService.cs` (`Preflight`),
`src/Design/Layout/Em/PlanarMemoryCheck.cs`
**Depends on:** —

---

## 0. The gap

Every estimate below was wrong in the direction that costs the most time, on one imported 35 × 20 mm
two-layer 0.6 mm FR-4 board with a full top-side ground pour and ~60 vias:

- **Palace element estimate 4× low.** `explain` said ~43,800 elements and 9.1 GB on the direct solver;
  Gmsh made 185,000, so the real figure was ~30 GB. Imported zone outlines carry many short edges and the
  estimate works from region volumes and refinement shells.
- **Planar: the accelerated solve judged on the dense ceiling.** With vias, `UsesAcceleratedCeiling` is
  `aimOn && !multiLevel` (a recorded owner decision), so the warning quoted **23 GB** for a run the
  accelerator holds in well under 1 GB, and demanded `--force`.
- **"which allows 0 unknowns here — ∞× OVER"** and *"past the 0 the accelerated solve is judged against"*:
  when a mesh's own cores exceed the limit, `CeilingFor` returns 0 and the sentences read as nonsense.
- **The via ρ/λ refusal comes after minutes, not in `check`.** `Dcim.ValidatedRhoOverLambdaAtHeights`
  (0.1) refused a 24 mm via spread at 2.05 GHz (ρ/λ = 0.163) — after ~2 min of meshing and setup. `check`
  passed the setup; `Preflight` already has the geometry and the sweep's top.
- **`PlanarBorderedAimOperator.FactorNear` ran 18 min on one core** (CSparse `SparseLU`) without finishing
  the first of ~41 points: 23.7k unknowns at 230 µm cells, where the near radius floor of 2h makes the
  "sparse" near matrix close to dense. Nothing estimated it; the run line said "50 MHz — solving the
  structure" throughout.
- **The core count is O(band) with a serial merge.** `DistinctBandKeys` merges one `HashSet` per 256th of
  the rows under one lock; at 46k unknowns `check` sat for 16+ minutes on one core. F1 (uncommitted) skips
  the count when the matrix alone is already past the limit; a mesh just under it still pays.
- **`PlanarMesh.Auto: true` overrides `EdgeMesh: false`** silently except for `check`'s warning.

## 1. Requirements

**R-emb4-1 — The Palace estimate counts outline complexity.** `EstimatePalace` adds a term for the boundary
edges of every extruded outline (short edges force small surface elements, which force small tetrahedra),
calibrated so the measured board's estimate is within 1.5× of Gmsh's count, and says *"the estimate is low on
imported outlines with many short edges"* when that term dominates. After meshing, the run's warning already
compares Gmsh's count — keep it, and add the corrected memory figure for the solver actually chosen.

**R-emb4-2 — Say the accelerated figure.** When AIM is on and the mesh is judged on the dense ceiling
because it is multi-level, the warning gives both numbers: *"judged on the dense ceiling (23 GB); the
accelerated solve this run uses holds about 0.4 GB"* — the bordered operator's own `ResidentBytes` estimate.
The ceiling decision itself is unchanged (owner decision); only the sentence.

**R-emb4-3 — No zero ceiling in a sentence.** When `CeilingFor` is 0, the budget sentence and the
accelerated-run warning say what alone is over the limit and by how much (*"this mesh's cores alone need
19 GB of the 17 GB limit"*), never "allows 0 unknowns" or "past the 0".

**R-emb4-4 — The via ρ/λ refusal in `check`.** `EmRunService.Preflight` evaluates the vertical-kernel
validity (`ValidatedRhoOverLambdaAtHeights` against the via-footprint spread at the sweep's top frequency)
the same way the solve does, and `check` reports the refusal with its remedies before any run.

**R-emb4-5 — The near-field factor is estimated, reported and parallel.** (a) `check`/`explain` estimate the
near matrix's nonzeros (near radius in cells² × cells) and the factor's fill, and warn when it is past a
measured threshold; (b) the factor reports progress (columns done / total) through `RunControl`; (c) replace
or supplement the single-threaded exact LU with a parallel factor or an incomplete one (ILU with a stated
drop tolerance) when the estimate is large — measure both on the fixture first and record in
`src/Engine/Mom/RESOLVED.md`; an incomplete factor must not change the answer beyond GMRES's tolerance.

**R-emb4-6 — The core count without a serial merge.** `DistinctBandKeys` accumulates per worker
(`localInit`/`localFinally`) and merges once per worker, not once per 256th of the rows. Bit-identical class
ids are the gate (ascending distinct keys).

**R-emb4-7 — `Auto` and explicit mesh knobs.** When `PlanarMesh.Auto` is true and the file states
`EdgeMesh`, `MinCellsAcrossConductor` or `DetailFloorDivisor`, the stated values win and `Auto` fills only
what is unstated; `check` notes it. (Or refuse the combination with the fix in the sentence — owner's choice;
either beats a stated value silently ignored.)

## 2. Gates

- `EstimatePalace` on a committed many-short-edge outline fixture is within 1.5× of a recorded Gmsh count.
- Budget/warning sentence tests for R-emb4-2 and R-emb4-3 (string assertions on `SurfaceMesher`'s output).
- `Preflight` on a two-via fixture past ρ/λ 0.1 at its top frequency returns the refusal.
- `DistinctBandKeys` returns identical arrays before and after on three existing meshes.
- R-emb4-5: a counter assertion that progress is reported per factor; the parallel/incomplete choice is a
  RESOLVED measurement, not a timing test.

## 3. Not in scope

- Moving any ceiling constant (CAP series territory).
- Planar memory accounting beyond the sentences above.
