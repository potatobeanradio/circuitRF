# Brief 140 — Building a whole board's geometry in Gmsh: minutes, not most of an hour

**Tag:** `R-em3d140-n`
**Area:** `src/Design/Em3d/GmshGeoWriter.cs` (solids, the disjoint cuts, the background, the fragment), `src/Design/Layout/Em3d/` (where a 2D result would be produced), `src/Design/Em3d/PalaceRun.cs` (reusing a built geometry), `docs/design/em-3d.md` §6.1.
Findings go in `src/Design/RESOLVED.md`. Measure with a scratch harness and `gmsh model.geo -0`, under the memory and disk watchdog; add no timing test.
**Builds on:** brief 137, which made the background one multi-tool difference. Its measurements stand and its candidates are not repeated here.

## The gap

A four-layer board placed whole in a `.c3d`, with 442 solids, 285 cylinders, three edge-cut vias and 28,004 outline points, spends about 46 of Gmsh's 47 minutes reading its script. Meshing 1D, 2D and 3D then takes 32 s. A failure in the mesh therefore arrives after 46 minutes of work that says nothing about the mesh, and every re-run pays the same again. A crop of the same layout, with 9,959 points, takes about 4 minutes: 2.8 times the points costs about 10 times the time.

| Step of the script | From the run's five-minute stamps |
|---|---|
| The first 144 disjoint cuts | about 10 min |
| Cut 145: the largest pour against 295 solids | about 15 min |
| The rest of the cuts and the background | under 5 min |
| The one `BooleanFragments` | about 5-10 min |
| Between the fragment and the end of the script | about 11-14 min |
| Meshing 1D, 2D, 3D | 32 s |

- One pour holds 12,717 of the board's 27,976 outline points, in 277 rings. Most of them are the faceted clearances round its vias: median edge 48 µm, each corner within 3.9 µm of its neighbours' line.
- A 5 s sample of the process late in the script spent 52 % of its time in `OCC_Internals::booleanOperator` → `_unbind` and 43 % in `getAllElementaryTags` → `OCC_Internals::synchronize` → `GModel::remove`. Both are single-threaded bookkeeping that grows with the model, not geometry.
- Reducing the same script to its three dielectric slabs plus one edge-cut via reproduces a real mesh fault in 6 s, where the whole script took 47 minutes to reach it.

## Decisions for the owner before building

**D1. May a pour's faceted clearances be simplified away from the signal path?** Fewer sides on a clearance far from any port cuts the largest cost directly, but changes geometry. Recommended: yes only if R-em3d140-1 measures a real saving, under the rule brief DFR30-2 set (|ΔS| < 0.01 at the sized frequency), and with a run note saying how many outlines were simplified.

**D2. What is the target?** Recommended: this board's geometry under 5 minutes, with the crop no slower than it is now. If R-em3d140-1 shows that cannot be reached, stop and report the best measured figure before changing the writer.

## Requirements

- **R-em3d140-1** Measure each candidate by editing the script by hand, on the board above and on brief 137's ten-layer crop. Record geometry CPU, peak memory, entity table identical or not, and whether it meshes. Candidates:
  - **(a) Disjoint cuts in 2D.** A pour and the prisms and cylinders of the same height range are cut in `Clipper` before Gmsh sees them, so the outline arrives with its holes and cut 145 does not exist. A cylinder stays round through a circular `Curve Loop`.
  - **(b) A layered construction.** Conductors are voids that `Recursive Delete` removes, so each slab could be built from a 2D plane with its holes, with no conductor solid made and then deleted. Brief 137 left this open.
  - **(c) Fewer tools.** A cut names only the solids that overlap the object's outline, not its bounding box.
  - **(d) Less bookkeeping.** Fewer `Volume{:}` and `Surface{:}` statements, which each force a `synchronize`, and deleting before the fragment rather than after.
  - **(e) Simplified clearances**, under D1.
- **R-em3d140-2** Build the candidate or candidates that reach D2. `entities.txt` stays byte-identical on `testdata/em3d/gmsh-versions/corpus`, or each difference is explained. S-parameters of the shipped examples stay within the tolerances in `docs/PRD.md`.
- **R-em3d140-3** Reuse a built geometry. Gmsh saves the built model beside the script, keyed by a hash of the geometry statements. A run whose geometry is unchanged, because only a frequency, mesh size or quality changed or the mesh failed, starts from it.
- **R-em3d140-4** Say the cost before it is paid. The pre-run note gives an expected geometry time from the outline points and solids, calibrated on the measured boards after R-em3d140-2, with a Crop to New Cell remedy when it passes 10 minutes. It is one more line in the same note, not a new dialog.
- **R-em3d140-6** Recalibrate the Palace memory estimate on this board. Its 927,863-tetrahedron mesh, order 1, one process, no refinement, was estimated at 25.0 GB after meshing and 19.2 GB before it. The run measured 8.9 GB by Palace's own report and a 16.0 GB footprint, with swap rising by about 2.4 GB, so the pre-run note's "will probably be stopped" was wrong for it. State which of the two figures the estimate targets, and hold the crop's measurement and this one together.
- **R-em3d140-5** Find mesh faults before the whole geometry. Past 10 minutes of expected build, first mesh a reduced script holding the dielectric slabs and every edge-cut object, and stop on any fault with its location. Skip this if R-em3d140-2 brings the whole board under 5 minutes, since it would then pay for little.

## Gates

- This board's geometry time meets D2, measured by harness and recorded in `src/Design/RESOLVED.md`.
- It still meshes end to end at 2 GHz with no error, as a `.c3d` with no crop, as it does today in 45 minutes (927,863 tetrahedra, Gmsh peak 9.65 GB), and solves as it does today in 25 minutes at one process.
- Editing only a frequency and re-running repeats no geometry work: the second run's log shows no `Difference` line.
- A fixture with a seeded edge-cut fault stops in the reduced pass, if R-em3d140-5 is built.
- `dotnet test tests/Ui.Tests --filter` on the `Em3d` classes this touches; no new `Category=Benchmark` timing test.
