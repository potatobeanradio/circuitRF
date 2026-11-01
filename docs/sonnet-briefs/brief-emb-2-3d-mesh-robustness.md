# Brief EMB-2 — 3D meshing of imported artwork: fast, clean, and said out loud when it is not

**Series:** EMB-0 … EMB-5 · **Tag:** `R-emb2-<n>`
**Area:** `src/Design/Em3d/GmshGeoWriter.cs`, `src/Design/Em3d/PalaceRun.cs`, `src/Design/Em3d/PalaceLogProgress.cs`
(`GmshLogProgress`), `src/Design/Layout/Em3d/Em3dLayoutSolids.cs` (via barrels), `src/Design/Layout/Em/EmSetupPersistence.cs`
(`RobustMeshing`), `src/Design/ThreeD/C3dProblemAssembly.cs` + `src/Design/Layout/Em3d/Em3dGenerator.cs` (air-box
floor), `src/Design/ThreeD/C3dPorts.cs` (inference vs robust meshing), `docs/user/src/reference/em-3d.md`
**Depends on:** —

---

## 0. The gap

On a 35 × 20 mm two-layer 0.6 mm FR-4 board imported from `.kicad_pcb` (full top-side ground pour,
~60 vias, one 2-pin through-hole header), five consecutive Palace runs failed before one solved, and every
failure was in the mesh, not the solver:

- **Order-2 curving is the bottleneck.** `GmshGeoWriter` writes `Mesh.ElementOrder = 2;` and
  `Mesh.HighOrderOptimize = 2;` unconditionally (~line 618). Gmsh's elastic high-order optimisation ran
  **17+ minutes on one core** over 330k nodes and had not finished. The same script with
  `Mesh.SecondOrderLinear = 1` and no optimisation meshed in **10–30 s**, and the run reused it through
  `PalaceRun.Mesh`'s `model.geo.sha256` check. Layout-derived geometry has no curved surface but via
  barrels; curving buys almost nothing there.
- **Hollow via barrels make inverted elements.** A via lowers to a copper cylinder with an air cylinder
  25 µm inside it (`Em3dLayoutSolids.cs` ~823, `entry.Fill == ViaFillKind.Plated`): 57 inverted elements.
  `Fill: Solid` removed them and is electrically identical at RF.
- **A through-hole pad meshed into 167 zero-volume tetrahedra** (both copper layers, around its drill).
  Palace then aborted with "Chebyshev/Jacobi smoother … requires a strictly positive diagonal" — no cause,
  no place. Four runs were lost before a script outside circuitRF found them by volume. Removing the pad:
  zero.
- **`RobustMeshing` is off in any setup not made by the GUI** (`EmSetupPersistence`: "null means off").
  On this board it removed 797 of 2,541 near-collinear outline points and cut Gmsh's ill-shaped tets from
  1,255 to 255. With it off, one lumped port sheet was not embedded conformally (MFEM `STable3D` abort).
  With it on, `check` passed a port the run then refused: robust meshing moves outlines by up to 0.5 µm
  before the run infers ports, and inference wants 1 DBU — `check` infers on the unmoved outline.
- **The air below a solid bottom plane is meshed.** A PEC floor at the bottom copper (`AirBox.ZMin`,
  0 padding) cut 165,992 → 139,530 tetrahedra with the same physics.
- **Gmsh's progress is invisible.** `gmsh.log` is written at exit; the run line said "Done meshing 3D" for
  the whole 17 minutes of high-order optimisation.

## 1. Requirements

**R-emb2-1 — Straight-sided order 2 for layout-derived geometry.** When every curved surface in the
lowered problem is a via barrel or a pad arc (no `.c3d` cylinder, sphere or STEP solid), write
`Mesh.SecondOrderLinear = 1;` and no `HighOrderOptimize`. Otherwise keep today's curving, with a wall-clock
cap (setup field `MeshCurvingMinutes`, default 5): past it, Gmsh is restarted with straight-sided order 2
and the run says so. **Why:** the measured 17+ min vs 10–30 s; the F0 validation that chose
`HighOrderOptimize = 2` was for genuinely curved packages, and the comment at ~616 should say which.

**R-emb2-2 — Solid vias for EM.** The 3D lowering of a via for an EM problem (driven, eigenmode,
electrostatic, magnetostatic) uses a solid barrel regardless of the technology's `Fill`; a thermal problem
keeps the hollow one. `explain` says which, per via entry. **Why:** inverted elements on every hollow
barrel at a cost of nothing an RF answer can see; the thermal model is where the wall matters.

**R-emb2-3 — `RobustMeshing` on by default, and one outline for everyone.** A setup that does not state it
reads as on (the GUI already writes it on; flipping the persistence default changes no GUI-made file). Port
inference in `C3dPorts` — in `check` and in the run — runs on the outline robust meshing produced, so the two
cannot disagree. An outline point moved by robust meshing that a port edge touched moves the edge with it.

**R-emb2-4 — A post-mesh check that names what is wrong.** After Gmsh, before Palace: count zero- and
negative-volume elements and surface triangles that are no face of any tetrahedron; map each to its
physical group through the entity table circuitRF already reads (`GmshGeoWriter.ReadEntities`) and to the
object it bounds. Any found refuses the run with one sentence per object: *"167 zero-volume elements lie in
board/Top Copper (1 oz)/6 and board/Bottom Copper (1 oz)/2 around the through-hole pad at (146.04, -87.00)
mm; set the via entry's Fill to Solid, enable robust meshing, or leave the part out of the model."* The
check costs one pass over the mesh — seconds — and replaces a solver abort with no location.

**R-emb2-5 — PEC floor on a solid bottom plane.** When the lowest conductor is a ground-reference plane
whose outline covers every other conductor's footprint (holes allowed only where no signal metal lies
above), the air box floor defaults to that plane's top, PEC, zero padding, and `explain` says why.
Otherwise today's rule. **Why:** 16 % fewer tetrahedra here with identical physics; em-3d.md already states
the rule for an undrawn plane.

**R-emb2-6 — Live Gmsh progress.** Run Gmsh with `-v 5` (or its log callback) unbuffered and feed
`GmshLogProgress` its stage lines as they come, including high-order optimisation with its node count
(*"Gmsh: high-order optimisation, 330,000 nodes"*), so a long stage is visible and cancellable.

## 2. Gates

- Geo writer: a layout-only problem gets `SecondOrderLinear = 1` and no `HighOrderOptimize`; a problem with a
  `.c3d` cylinder keeps curving — string assertions on the script.
- Via lowering: an EM problem lowers a plated via without the `/fill` solid; a thermal one keeps it.
- Persistence: a `.cem` and an embedded setup that omit `RobustMeshing` read as on; round-trip omits it.
- Inference: a port whose edge sits on a point robust meshing moves is inferred identically by `check` and
  the run.
- Post-mesh check: a committed tiny `.msh` with one flat tetrahedron and one orphan triangle (hand-made,
  not meshed in the test) produces both sentences naming their groups.
- PEC floor: a fixture with a full bottom plane gets the floor; one with a signal trace on the bottom does
  not.

## 3. Not in scope

- Changing Palace's element order or solver choice.
- Improving Gmsh itself, or meshing through-hole pads differently (R-emb2-4 names them; R-emb2-2 and
  robust meshing are the remedies measured).

---

## Completion note — EMB-2 (2026-11-01)

### Per requirement

**R-emb2-1 — straight-sided order 2. Done.**
- `Em3dProblem.ArtworkBarrels` (Engine): the solids whose curved faces are a layout's drilled barrels (a via's barrel, its air core, a drilled hole). It is set by `Em3dGenerator.Generate` and by `C3dProblemAssembly.Assemble`, both through `Em3dGenerator.ArtworkBarrels(solids, origins)`. A `.c3d`'s own cylinders have no layout origin, so they are never in the set.
- `GmshGeoWriter.Curved(problem, settings)` is true when a solid has a curved surface that is not one of those barrels: a cylinder, sphere, truncated sphere, ellipsoid, round sweep, or a kernel/STEP solid with a curved face. Only then does the script write `Mesh.HighOrderOptimize = 2;`. Otherwise it writes `Mesh.SecondOrderLinear = 1;` (`CurvingLine`, `StraightSidedLine`). The comment says that F0 chose the curving on case A's round bond wire.
- The cap: `Palace.MeshCurvingMinutes` (`CemPalace`, default 5, validated as positive) and `PalaceSettings.StraightSided` (used only for the restart).
  - `PalaceRun.Mesh(..., straight, curvingMinutes)` watches a curving script through the watch's `OnTick`. Once Gmsh's high-order stage has run past the cap, it kills the process, meshes the straight-sided twin, and sets `PalaceStep.Note`. The Palace run service adds that note to the run's notes.
  - The hash file keeps the requested script's hash next to the straight-sided `model.geo`. The next run therefore reuses that mesh (and says so) instead of curving again.
  - The thermal writer's own order-2 curving was not touched (out of scope).
- No GUI control for `MeshCurvingMinutes`. It is a `.cem` field, documented in the em-setup table.

**R-emb2-2 — solid vias for EM. Done.**
- `Em3dLayoutSolidsOptions.SolidVias` and `C3dElaborationOptions.SolidVias` (added to the layout-child cache key). They are set from `Problem3D != Thermal` by the generator and by `C3dProblemAssembly`, and from `!IsThermal` by the CLI's `Em3dSetupSource` elaborations.
- The default stays the technology's wall. So the thermal run (`ThermalRunService`'s elaboration) and a picture with no setup keep the hollow barrel.
- `explain` gets one note listing every via entry (e.g. `'Signal Via' (Fill Plated, 25 µm wall) is built solid`). It is written only when some plated entry with a wall was actually made solid. A board whose entries are all Fill Solid gets no note, because the note would change nothing (it was noise in 6 dumps).
- Side effect: the 3D view of an EM setup now shows a solid barrel, so it no longer carves the bore.

**R-emb2-3 — RobustMeshing on by default; one outline for check and run. Done, with one limit.**
- Persistence: unstated reads as on, and only `false` is written. The model's default (`EmSetup.RobustMeshing`) is on too.
- `check` on a `.c3d` now infers ports through `C3dPortReports.For(doc, e, elaborationFor)` against the robust elaboration, as the run does. The only exception is a setup that states `false`.
- "The edge moves with the outline":
  - `C3dElaboration.OutlineMoveM` is the cleanup's largest move, carried up from `Em3dLayoutSolidsResult.OutlineMoveM`.
  - `C3dPortContext` takes it in its contact tolerance, its box cut-off and its edge inset.
  - `C3dPorts.FollowOutline` then moves an inferred lumped port's two end edges onto the moved outline: out to a face that receded, back from one that reaches into the sheet.
  - The port's reason says it, e.g. `its edge on 'U1/…' follows Robust meshing's outline by 0.3 µm`.
  - When nothing was moved, `OutlineMoveM = 0` and nothing changes.
- My `C3dPorts.cs` edits for EMB-3's merge: the `Distance` cut-off line, the `inset` line, the inference branch (`tol` plus the `FollowOutline` call after the polarity is chosen), the new private `FollowOutline` method before the cylinder section, and the context's `OutlineMoveM` property plus its constructor line. None of them touch the stated-ends branch or `CylDistance`.
- Not done: the 3D editor's port tool still measures the drawn outline, because its elaboration is not robust. Neither is the `.cem` layout path, whose generator places port sheets from the unmoved pieces; there, robust meshing keeps a rectangular trace end's corners anyway.

**R-emb2-4 — post-mesh check. Done.**
- **Function for EMB-1: `CircuitRF.Design.Em3d.Em3dMeshCheck.Check(string mshPath, IReadOnlyList<Em3dGroup> groups, double lengthUnitM = GmshGeoWriter.LengthUnitM)`.** It returns `Em3dMeshCheckResult` (`ZeroVolume`, `Inverted`, `OrphanTriangles`, `Sentences`, `Refusal`), or null when there is no readable mesh. An overload takes an `MshMesh` that has already been read.
- It is called in the Palace run after Gmsh, before Palace, on reused meshes too. A failure refuses the run under source `palace-mesh-check`.
- What it counts:
  - Zero-volume tetrahedra: 6V below 1e-8 × (longest edge)³.
  - Inverted tetrahedra: negative corner volume. Gmsh orients every tetrahedron positively; I checked that on its own output.
  - Folded second-order elements: Jacobian ≤ 0 at a corner or the centroid, using Gmsh's ten-node order.
  - Surface triangles that are no face of any tetrahedron.
- How it names them: each bad element by the conductor, sheet or port surfaces its corners lie on (otherwise its volume group), with nearby elements grouped into one sentence with an (x, y) in mm. It maps through the physical tags, which are the Palace attributes, so the entity table is not needed.
- Checked for false positives on two real meshes (microstrip and the plated via, 19k and 98k tetrahedra, 3 s total): zero, zero, zero. That was a scratch test, now removed.
- It names objects by group name (`board/Top Copper (1 oz)/6`), not by kind ("the through-hole pad").

**R-emb2-5 — PEC floor on a plane with holes. Done, in both paths.**
- `Em3dGenerator.CoveredHole(holes, metal, drills)` finds the first hole that has something over it in plan view.
- `Em3dLayoutSolids` (`.cem`) treats as covering any polygon on any band that is off the plane's own net (other than the plane polygon itself), and any via drill reaching into the hole.
- `C3dProblemAssembly.PlaneFloor` (`.c3d`) treats as covering any modelled conductor outside the ground set; an upright cylinder counts as a drill.
- The note says how many holes the floor plane has, or where the hole that blocked the floor is.
- Decision: the floor stays on the plane's **underside**, where designer round 33 validated it, not its "top" as the brief words it. The brief's own measurement (ZMin at 0 padding) is the underside.

**R-emb2-6 — live Gmsh progress. Done.**
- Gmsh runs with `-v 5` (`PalaceRun.GmshArguments`).
- `GmshLogProgress` labels every stage: curves, surfaces, 3D, the first-order optimiser, second-order elements, *high-order optimisation* (with the node count once Gmsh says `High-order tools set up: N nodes`), and writing. Gmsh 4.15's wording was read from a tiny local run (about 0.3 s).
- It also measures how long the curving has run (`CurvingFor`, `CurvingPast`; the clock can be replaced for tests).
- `RunProcess` now flushes the log file once a second, so `gmsh.log` fills while Gmsh runs.

### Goldens moved (reviewed line by line)
- Palace goldens: every script now writes `SecondOrderLinear = 1`. `via` and `static-ms` also lose their `via/N/fill` groups, which renumbers the attributes in their configs.
- Generator dumps:
  - `caseB-plated` and `via-padded` lose the fill solid and gain the via note.
  - Taper, `portcal separated-pair` and the two PCell spirals gain a "Robust meshing removed …" note and the cleaned outline (robust is now on by default).
- The spiral dumps need the example workspace's git-ignored `.generated-cells`. Without it they refuse in any checkout. I copied it in temporarily to regenerate them and then removed it.

### Tests
- New: `tests/Ui.Tests/Em3d/MeshRobustnessTests.cs`, 7 tests, all pass.
  - A layout problem is meshed straight-sided; a cylinder of its own keeps the curving.
  - The curving cap restarts straight-sided and the next run reuses that mesh. This uses a 1 s stand-in shell script for Gmsh; the test returns early on Windows.
  - A plated via is solid for EM; the walled lowering keeps the core.
  - Inference on a 0.3 µm bump is identical in check and the run.
  - Post-mesh check on the committed hand-made `testdata/em3d/mesh-check/flat-and-orphan.msh` (one flat tetrahedron, one inverted, one orphan port triangle).
  - The floor applies with a clear hole and not with a trace in the hole.
  - Gmsh stage lines from canned input.
- Changed for the new behaviour:
  - `RobustMeshingTests.ANewSetup_TurnsItOn_AndAFileThatDoesNotSayReadsOn` (this is also the persistence gate).
  - `Em3dGeneratorTests.Gate3…` and `APlatedDrillCircle…`: the tube and core are now checked on the walled lowering.
  - `Em3dRenderExplainTests.Gate5…`.
- Runs, targeted classes only:
  - MeshRobustness, RobustMeshing, Em3dGenerator, Em3dGeneratorDump, the PalaceBackend Gate1/Gate2 goldens and schema, PalaceStatic Gate1, Field Gate1, ThermalRun Gate8, Em3dRenderExplain, PalaceProgress, Scene3DGate: 109/109 pass, with the cells present for the spiral dumps.
  - WireSplitDump, C3dElaboration, HierarchyGate, PortEdgeWidth: 56/56.
  - CheckAndExplainCliVerb, EmCliVerb: 46/46.
  - Firewall: 78/79. The 1 failure is `UserFacingTextGateTests`, which flags strings in `src/Core/Devices/ComponentModelFactory.cs` and `src/Engine/Mom/*`, files this brief does not touch.
- No long EM runs. The longest Gmsh run was 3 s, in a scratch test.

### Docs
- `docs/user/src/reference/em-3d.md`: floor with holes, robust meshing default, solid vias, mesh order, Gmsh progress.
- `docs/user/src/reference/em-setup.md`: robust meshing default, the `MeshCurvingMinutes` row, the post-mesh check.
- Both pages regenerated with DocGen (the two HTML pages plus `search-index.js`).
- RESOLVED entry added at the end of `src/Design/RESOLVED.md`.

### Expected overlap with siblings
- `src/Design/ThreeD/C3dPorts.cs` (EMB-3): separate hunks, listed above.
- `src/Cli/Check.cs` (EMB-3's port notes): my only edit is the few lines right before `foreach (var port in C3dPortReports.For(...))` in the `.c3d` section.
- EMB-4 (`EstimatePalace` / budgets): no shared file expected, except that `Em3dRunService.Palace.cs` gained the mesh-check call and note after the "Meshed with Gmsh" note.
