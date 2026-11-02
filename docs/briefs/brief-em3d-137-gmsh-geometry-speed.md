# Brief 137 — The Gmsh geometry step on a multilayer board: minutes, not ten

**Tag:** `R-em3d137-n`
**Status:** Written 2026-10-24.
**Area:** `src/Design/Em3d/GmshGeoWriter.cs` (the Palace script: the background construction ~279-300, the fragment
~318-325, the global options ~236-240), `docs/design/em-3d.md` (§6.1's recipe). Read, do not change:
`src/Design/Em3d/Solvers/EMerge/circuitrf_emerge.py` and `GmshGeoWriter.Thermal.cs`, which build no chained
background (below). Findings in `src/Design/RESOLVED.md`.
**Depends on:** DFR26-1's two writer fixes (the hole winding `PlanarRings`, the touching-conductor claims), which are
what let this board mesh at all.
**Out of scope:** the mesh itself (element sizes, refinement), Palace's solve time, and the planar solver.

---

## 0. The gap

A ten-layer board cropped to a 12 mm RF path — 152 solids: 9 dielectric slabs, the air above, 74 conductor pieces
(inner planes with antipads, via pads), 34 plated barrels and their 34 air fills — takes **705 s** in Gmsh before
Palace could start. **About 650 s of that is the geometry**, before Gmsh writes its first mesh line; meshing 1D, 2D, 3D
and order 2, with their optimisation, is about 40 s. Peak memory is 3.1 GB, which is fine. Every re-run whose
geometry changed pays the 650 s again; a designer sizing a cut-out under a test pad changes the geometry every time.

**Where the time goes**, measured with a CPU timestamp printed after every boolean in the script (the cropped board,
Gmsh 4.15.2, one thread):

| Step | Operations | CPU |
|---|---:|---:|
| Disjoint by construction order (each solid minus the higher-precedence solids it overlaps) | 116 | 18 s |
| **The background: the air box minus every solid, one solid per `BooleanDifference`** | **152** | **580 s** |
| The one `BooleanFragments` | 1 | 36 s |
| `Recursive Delete` of the conductors | 1 | 16 s |

Inside the background chain the cost does not follow the tool: cutting a 0.6 mm via pad costs ~9 s, the same as
cutting a whole plane. It follows the BACKGROUND, which by then holds the region below the stack plus one cavity
for every conductor (the slabs were cut around them, so their volume is still background), and every cut re-processes
all of it. Sampling the process shows the time in OCC's booleans and in Gmsh's own entity bookkeeping
(`OCC_Internals::_unbind`, `_recomputeMaxTag`), which grows with the model. The 74 conductor cuts are 463 s of the 580.

**Why the background is chained:** an outside contribution on 2026-10-21 (`859dda96`, its write-up in
`src/Design/RESOLVED.md` "The background was cut with one multi-tool difference, and OCC landed a wrong region")
replaced ONE multi-tool difference with one cut per solid, because on "a stack of overlapping dielectric slabs and
conductor pieces" the multi-tool difference landed a wrong region and a solid lost its tag. **DFR26-1 has since found an
OCC failure with the same symptoms and a different cause:** a plane whose antipad ring is wound against its outline
(Clipper's convention — every extracted polygon) empties the slab it is cut from whenever a second tool is in the same
cut. That is fixed now (`GmshGeoWriter.PlanarRings`). It is quite possible the multi-tool difference was never the
problem, and that it is safe — and much faster — again.

## 1. Reproduction

The owner has the cropped board's workspace; ask for it, and **do not commit it or name it anywhere**. Gates use
synthetic fixtures (§3). For the measurement, the script is enough: generate the problem and write `model.geo` from a
scratch harness (`Em3dGenerator.Generate`, then `GmshGeoWriter.Write`), then run `gmsh model.geo -0` (geometry only)
and `gmsh model.geo -3` under the EM memory/disk watchdog (kill past ~4 GB or under 10 GB free). Print
`Printf("T %g line N", Cpu);` after each boolean to profile. Measure in a scratch harness, never with a new timing test.

## 2. What to do

### R-em3d137-1 — Measure the candidates on the cropped board, before changing the writer

Each is a script-level change that can be tried by editing `model.geo` by hand. Record each one's geometry CPU, and
whether the entity table still closes (every volume classified, `unclassified_single_sided 0`, no group short):

- **(a) One multi-tool difference again**, now that holes are wound right: `bg[] = BooleanDifference{ Volume{bx}; Delete; }{ Volume{every solid}; };`.
  Also re-run the reduced case `859dda96` added (`PalaceBackendTests`' nested slabs and sheets) and its goldens on it.
- **(b) The conductors out of the chain.** Conductors are deleted afterwards anyway. Subtract the dielectrics and the
  air one by one as now, then take every conductor away in ONE difference, or delete the background fragments that
  lie inside conductors after the fragment rather than cutting them out first.
- **(c) A fused tool:** `BooleanUnion` of all solids (they are disjoint after step 1), then one two-solid difference.
- **(d) The background from the fragment itself:** fragment the box together with every solid once, and take as
  background the pieces no solid claims (classified the way the entity table already classifies volumes). **This is
  what the EMerge driver already does** (`circuitrf_emerge.py`: the air box is one more volume at priority 0,
  `commit_geometry` fragments everything once, and every fragment a conductor owns is removed), and the thermal
  writer has no background chain at all. Time EMerge's geometry on the same board as a reference point, under the same watchdog.
- **(e) Gmsh's own options**, alone and with the above: `Geometry.OCCParallel = 1`, and the cost of
  `Geometry.OCCBooleanPreserveNumbering = 1` (the recipe relies on it to keep volume tags — do not drop it without
  replacing what it gives).

Stop and report the table before implementing. **If none of them gets the cropped board's geometry under 60 s CPU,
say so with the numbers and stop**: the next step would be a different construction (a layered build that never forms
the full background), which is a decision for the owner.

### R-em3d137-2 — Implement the winner in the Palace script

Only `GmshGeoWriter`'s Palace script chains the background. Update `docs/design/em-3d.md` §6.1 and the writer's header comment, which state the chained form as the rule. If (a)
wins, write in RESOLVED what the 2026-10-20 failure was, re-tested on the fixed winding, and amend that entry rather
than leaving two contradicting rules.

### R-em3d137-3 — Nothing else changes

The same problem must give the same physical groups: the same volume and surface groups, with their counts, as
before. Compare the entity tables of the shipped 3D examples' scripts, old against new; any difference is a finding.

## 3. Tests

Minimal — one test per claim, targeted classes only.

1. **A counter, not a clock:** the number of `BooleanDifference` lines the background step emits for a problem of N
   solids (one, or a small constant — whatever the winner is). No new timing test.
2. The existing goldens (`PalaceBackendTests.Gate1_…`, `PalaceStaticTests.Gate1_…`) regenerated deliberately, and
   `PalaceBackendTests.TheBackgroundIsCutOneSolidAtATime` replaced by test 1 if the chain goes.
3. The two DFR26-1 Gmsh tests in `MultilayerPortRound26Tests` (`ASlabUnderAPlaneWithAnAntipad_…`,
   `ABlindViaOverhangingItsPlanesEdge_…`) still pass, and the nested-slab case from `859dda96` still meshes with every
   volume classified (a `GmshFact` test, if it does not have one).
4. Run `PalaceBackendTests`, `PalaceStaticTests`, `PalaceEigenTests`, `MetalPrecedenceTests`, `KernelSolverTests`
   and `MultilayerPortRound26Tests`. Not the full suite.

**On the cropped board (local, not committed):** report the geometry CPU and the whole Gmsh wall time before and
after, the node and element counts, and the entity table. Do not run a solve.

## 4. Text

None for users: the run gets faster and says nothing new. `docs/design/em-3d.md` §6.1 only.

## 5. Out of scope

- The mesh sizes and the 40 s of meshing.
- The cost of very many vias (an antipad field): measure it if (b) or (d) wins, but do not design for it here.
