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
  the rows under one lock; at 46k unknowns `check` sat for 16+ minutes on one core. F1 (`36ae6a845`) skips
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

---

# EMB-4 completion note (2026-11-01)

## Per requirement

**R-emb4-1 — Palace estimate counts outline complexity. Done.**
- `Em3dSizeEstimate.Palace` adds a short-edge term: for every outline/hole edge of an extruded solid or sheet with
  length e below the element there (conductor element for metal and sheets, the solid's own for dielectrics),
  `K·√(e/h)·(1 − e/h)` tetrahedra, `K = OutlineTetrahedraPerShortEdge = 63`. Reported as
  `Em3dPalaceEstimate.OutlineTetrahedra` and a region row "outline short edges"; `OutlineDominates` = a third or more.
- Calibration: synthetic fixture committed in code (`PalaceOutlineEstimateTests.ScallopedPour`: the shipped microstrip
  plus an 8 mm pour whose top edge is a sampled sinusoid). Gmsh 4.15.2, first-order mesh, under an RSS/time watchdog,
  each run < 0.4 s. Four variants were meshed (straight control, 120, 400, 800 edges) because one run cannot separate the
  short-edge term from the shells; a per-edge constant did not fit (35/20/14), `√(e/h)` did (K = 61.7/62.4/63.0).
  Gate fixture = 800 edges: Gmsh 31,530 (recorded constant), estimate 30,308 (0.96×); without the term 19,550 (0.62×).
  The board's 185,000 vs 43,800 (4.2×) is quoted in `src/Design/RESOLVED.md` as a measured ratio only.
- `explain` names the term and, when it dominates, says "The estimate is low on imported outlines with many short
  edges…".
- After meshing, the run adds a note from `Em3dRunService.MeshedAgainstEstimate`: Gmsh's count against the estimate and
  the memory at Gmsh's count for the linear solver the run actually uses (computed after the memory question may swap
  direct for iterative). The existing post-mesh memory warning is unchanged.

**R-emb4-2 — say the accelerated figure. Done.** Ceiling decision unchanged. `SurfaceMesher.Mesh` gained
`aimOn` (passed by `EmRunService.Preflight` and the three `PlanarKernel` mesh calls); with AIM on and the mesh judged
dense, both the budget and `MachineLimitWarning` append `AcceleratedClause`: "…judged on the dense ceiling (X)… the
accelerated solve this run uses holds about Y, estimated (Z of it the near-field factor)". Y =
`PlanarNearFieldEstimate.ResidentBytes` (bordered operator's own terms, less the class-keyed core store).
`PlanarMemoryCheck`'s accelerated sentence no longer calls the dense ceiling "the accelerated solve's".
The EM panel's own mesh call (`EmSetupEditorViewModel`) was NOT changed (UI; the estimate is not free on every edit), so
the panel's budget lacks the clause.

**R-emb4-3 — no zero ceiling in a sentence. Done.** `SurfaceMesher.CoresAlone`: "this mesh's cores alone need X of the
Y limit, so no unknown count fits beside them — OVER." in the budget, and in `PlanarMemoryCheck.AcceleratedPastCeiling`
(public) in place of "past the 0". `PlanarMeshReport.CoreBytes`, `PlanarRunSize.CoreBytes/DenseCeiling/
AcceleratedResidentBytes` carry what the sentences need.

**R-emb4-4 — via ρ/λ refusal in `check`. Done.** `EmRunService.Preflight` asks `PlanarSolve.VerticalRangeVerdict` on the
meshed problem at the sweep's top with the run's `DirectVerticalKernel` switch and returns its refusal (with remedies).

**R-emb4-5 — near-field factor. (a) and (b) done, (c) deferred.**
- (a) `PlanarNearFieldEstimate.For(mesh, slabHeightM, aim, ct)` — counts the near set exactly as `AimProjection.NearSet`
  (asserted equal to `PlanarAimGeometry.ColIdx.Length`), then AMD + elimination-tree column counts give the factor's
  non-zeros and work Σc². Scratch measurement (synthetic near patterns, n 3,120–19,800): the prediction equals
  `SparseLU.NonZerosCount` exactly; rate 5.4–7.5e8 Σc²/s, flat. Warning past 60 s/point
  (`MeasuredFactorWorkPerSecond` 7.5e8); above 20 M near entries not analysed, large on count. Reported by `check`
  (Preflight finding) and in the run's notes (`PlanarSolve.Run`, warning when large).
- (b) `PlanarNearFactor.Factor` (shared by both operators, which had duplicate `FactorNear`) reports columns done/total
  from CSparse's `IProgress<double>` (100 reports) plus the final column; `PlanarSolveContext.FactorProgress` →
  `RunControl.SetStageDetail("factoring the near field: k / n columns")` on the structure's context.
- (c) NOT built. Reason in `src/Engine/Mom/RESOLVED.md` §EMB-4: CSparse has no parallel factor (a parallel one = native
  dependency or a large managed implementation); an ILU's deciding cost is GMRES iterations on the bordered MPIE
  operator at the scale where the exact factor is slow (20k+ unknowns, minutes/point) — a long run, ruled out; a
  synthetic pattern cannot answer it. What a taker owes is written there.

**R-emb4-6 — core count without a serial merge. Done.** `PlanarFill.ForRowsLocal<TLocal>` (localInit/localFinally,
same sequential/capped/budgeted shapes); `DistinctBandKeys` merges once per worker (larger set absorbs smaller). Gate:
equality with a serial scan on three meshes, no timing.

**R-emb4-7 — Auto and explicit knobs. Done, "stated values win".** `PlanarMeshSettings.Resolved` keeps `EdgeMesh` under
Auto (MinCells/DetailFloor already survived); Auto chooses only cells/λ and edge cells. `EmSetup.AutoKeptMeshFields()`
+ `check.em.mesh-auto-kept` (info) note the stated fields that win. `check.em.mesh-auto-override` keeps warning for
cells/λ/edge cells. `MultiLevelPortTests.CoarseForZz` (Benchmark fixture with an inert `EdgeMesh: false` beside Auto)
now spells the default so its meshes are unchanged. Two stale test comments ("EdgeMesh=false is INERT") updated.

## Functions for EMB-5 (dryRun / progress)
- Planar near-field factor estimate: `CircuitRF.Engine.Mom.PlanarNearFieldEstimate.For(PlanarMesh, double slabHeightM,
  PlanarAimSettings? = null, CancellationToken = default)`; sentence `.Describe(lengthFormat)`; `.Large`,
  `.FactorSeconds`, `.ResidentBytes`. Also on a mesh report: `PlanarMeshReport.AcceleratedEstimate` (multi-level AIM).
- Factor progress: `PlanarSolveContext.FactorProgress` (Action<long,long>), `PlanarNearFactor.Factor(..., progress)`.
- Corrected Palace estimate: `Em3dRunService.EstimatePalace(Em3dProblem, PalaceSettings)` (now with
  `OutlineTetrahedra`/`OutlineDominates`); after meshing `Em3dRunService.MeshedAgainstEstimate(long tets,
  Em3dPalaceEstimate?, PalaceSettings, bool isStatic = false, int? processes = null)`.

## Tests (only the classes added/changed, plus the existing classes my changes reach)
- Engine.Tests `HonestEstimatesTests` (new, 5): pass. Plus `PlanarMemoryAccountingTests`, `AimAcceleratorTests`,
  `PlanarP12BorderedAimTests`, `MultiLevelPortTests` (routine tier): 30/30 pass.
- Ui.Tests `PalaceOutlineEstimateTests` (new, 2), `HonestEstimatesPreflightTests` (new, 2), `EmRunSeverityCliTests`
  (one test rewritten: cells/λ is the discarded field, EdgeMesh is kept and noted), `CliStructuredOutputTests` (new id
  registered), and the two existing estimate gates my term can move
  (`TestPadRound26Tests.PalaceEstimate_OfAStaticMultilayerFixture…`, `Em3dConnectorExampleTests.ThePalaceEstimate…`):
  65/65 pass.
- Firewall.Tests: 78/79 — the one failure `UserFacingTextGateTests.NoNewUserFacingTextIsAddedBelowTheUiFirewall` names
  7 strings in `ComponentModelFactory.cs`, `BlockLowerMatrix.cs`, `SymmetricFactorization.cs`, none touched by this
  brief (pre-existing at the base commit; not mine).

## Docs
- `docs/user/src/reference/cli.md` (the Auto-override paragraph) edited and that page regenerated (`cli.html`).
  DocGen also rewrote `docs/user/assets/js/search-index.js` wholesale (the index was stale at the base commit — the
  Sensitivity page was missing); that churn was reverted to keep the patch to this brief.
- RESOLVED: `src/Engine/Mom/RESOLVED.md` §EMB-4, `src/Design/RESOLVED.md` §EMB-4, `src/Cli/RESOLVED.md` §EMB-4.
- Not edited (CLAUDE.md rule): `src/Engine/Mom/CLAUDE.md` §4 still says `Resolved` carries only some fields through Auto;
  the owner may want to add EdgeMesh there.

No UI changes, so no UiSnapshot.

## Expected overlaps with siblings
- `src/Cli/Check.cs`: one self-contained block after the existing `check.em.mesh-auto-override` call (EMB-3 edits port
  notes elsewhere in the file). `src/Cli/CliDiagnostics.cs`: one new factory before `CheckEmWarning`.
- `src/Cli/ExplainEm3d.cs`: only the Palace size note string in `Size(...)`.
- `src/Design/Em3d/Solvers/Palace/Em3dRunService.Palace.cs`: one note after `TakeIterativeIfChosen()` in
  `ExecutePalace` and one new method before `IterativeRemedy` — EMB-2 (meshing/Gmsh progress) may touch nearby code.
- `tests/Ui.Tests/Cli/CliStructuredOutputTests.cs` ExpectedIds: one line (siblings adding ids will conflict trivially).
