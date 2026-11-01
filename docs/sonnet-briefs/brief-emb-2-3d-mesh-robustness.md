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
