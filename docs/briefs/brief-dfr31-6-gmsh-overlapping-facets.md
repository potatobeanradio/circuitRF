# Brief DFR31-6 — Gmsh "Invalid boundary mesh (overlapping facets)" on a cropped imported board

**Series:** designer feedback round 31 (2026-10-29). · **Tag:** `R-dfr31-6-<n>`
**Area:** `src/Design/Layout/Em3d/` (the robust-meshing outline clean-up, via barrels clipped at the board edge —
DFR28-1), the `.geo` writer, and the Gmsh failure reporting in the Palace run service.
Findings go in `src/Design/RESOLVED.md`.
**Tests:** a synthetic reproduction in `tests/Engine.Tests` or `tests/Ui.Tests/Em3d/`. Keep the mesh run short
(owner rule): one Gmsh 3D mesh of a small board, not a whole-board run.

---

## 0. Why

A driven Palace run of a Gerber-imported 10-layer board, cut down by hand to a few square millimetres around the RF
lines, failed in Gmsh after about four minutes:

`Invalid boundary mesh (overlapping facets) on surface 22561 surface 22184`

That came with "No elements in volume …" for about 70 volumes, and 3 surface elements with negative Jacobians (worst
distortion −0.157, worst gamma 4e-7). The run's notes before meshing said: robust meshing removed 36 of 4,172 outline
points (each moved by at most 0.44 µm); 4 vias crossed the board edge and were built as the part of each barrel on the
board; and the air box's floor was below the geometry. A later crop of the same board, re-imported, meshed and solved
cleanly. So the failure depends on the geometry, and it is not a size limit.

The failing run directory survives in the field-test material, which is kept outside the repo and must not be
committed: `model.geo`, `gmsh.log` (the failing log is a separate copy from the run directory's own, which belongs
to a later, cancelled run), `groups.json` and `entities.txt`. The `.clay` and `.cem` that produced it are in the same
material.

## 1. Requirements

**R-dfr31-6-1 — Find the two surfaces.** Re-run Gmsh on the saved `model.geo` locally, with the same Gmsh version
(4.15.2) and the same options, to the 2D stage only. Report what surfaces 22561 and 22184 are: which volumes they
bound, and which circuitRF objects those volumes came from (`groups.json` / `entities.txt`). The candidates, in order
of likelihood:
- a via barrel clipped at the board edge (DFR28-1), meeting a copper piece that was itself cut at the same edge;
- two outline pieces left a sub-micrometre sliver apart by the robust-meshing point removal;
- a near-tangent between a via barrel and a copper pour's edge.
Stop and report if the 2D stage alone takes more than 10 minutes.

**R-dfr31-6-2 — Reproduce it small.** Build the smallest synthetic layout that gives the same Gmsh error, and commit
that one as the test. Commit nothing from the field-test board.

**R-dfr31-6-3 — Fix at the source**, in the generator, not by loosening Gmsh tolerances. If the cause is a sliver,
the robust pass must merge features closer than its threshold ACROSS objects, not only within one outline. If it is an
edge-clipped barrel, snap the clip to the board-edge segment that the copper was clipped to.

**R-dfr31-6-4 — When Gmsh still fails, name the object.** Map the surface tags Gmsh names back to circuitRF objects
through the existing group table, and add one sentence to the refusal, for example: "The overlap is between
'via/12' (cut at the board edge) and 'Inner 3/7'." A tag number alone gives the user nothing to act on.

## 2. Gates

1. The synthetic reproduction fails with the same Gmsh message before the fix, and meshes after it.
2. R-dfr31-6-4: a forced overlap reports both object names.
3. The `3D EM` and DFR28-1 edge-via tests still pass.
