# Brief DFR30-2 — Simpler vias away from the signal path: measure first, then decide

**Series:** designer feedback round 30 (2026-10-28). · **Tag:** `R-dfr30-2-<n>`
**Area:** `src/Design/Layout/Em3d/Em3dLayoutSolids.cs` (vias, pads, antipads), `src/Design/Em3d/GmshGeoWriter.cs`
(`Cylinder`, `MeshSizeFromCurvature`), `src/Design/Em3d/Solvers/EMerge/circuitrf_emerge.py` (`addCylinder`),
`src/Design/Layout/Em/EmSetupModel.cs`, `EmSetupPersistence.cs`, `src/Ui/Layout/Em/EmSetupEditorViewModel*.cs`, `src/Ui/ThreeD/C3dEditorViewModel.Simulate.cs` (only if R-dfr30-2-4 is reached). Findings go in `src/Design/RESOLVED.md`.
**This is a measurement brief.** Phases 1–3 decide whether phase 4 is built at all.

---

## 0. Why

The designer asked how much round vias cost in a 3D EM run. Near the path a signal takes from port to port, the
precision matters. Further away — stitching vias more than two or three trace widths from that path — he suggested
a simpler shape (a square or a low polygon) would mesh faster for no loss that matters. He also noted that cropping to
a rectangle is quicker than following the board's shape.

How a via reaches the mesher today (read before writing this brief):

- **The barrel is a true cylinder.** `Em3dLayoutSolids` builds an `Em3dCylinder` per via, plus a second, inner one of
  air for a plated wall. `GmshGeoWriter` writes them as OpenCASCADE `Cylinder`, and `circuitrf_emerge.py` as
  `occ.addCylinder`.
- **Gmsh resolves every curve:** `Mesh.MeshSizeFromCurvature = 12` (`Em3dFidelity.PalaceCurvatureElements`), so each
  barrel's circumference gets at least 12 elements, on every layer it crosses.
- **Pads and antipads are NOT cylinders.** They are layout circles flattened to polygons by `LayoutFlattener` at the
  technology's chord tolerance. So each via stacks a faceted pad, a curved barrel and a faceted antipad on every layer.
  Facets that do not line up with a curved surface beside them are one known source of small, poorly shaped
  tetrahedra in thin gaps.
- **An edge-cut barrel is already a prism**: `Extrude(polygon, z0, z1)`, R-dfr28-1-1. A polygonal barrel needs no new
  solid type.
- A test-pad board's crop holds many stitching vias, and its Gmsh logs show 360–400 k elements for a crop.

Nobody has measured how much of that the vias are. Until that is known there is nothing to design, so this brief
measures first.

**Risk, said up front:** the element count may be driven mostly by the thin layers — 18–35 µm copper, 69–77 µm
dielectric — and by the barrel-to-antipad gaps, not by curvature. If so, faceting the vias saves little. The stop rule
in phase 3 exists for that outcome, and it is a fine result.

## 1. What to do

### R-dfr30-2-1 — An internal switch for the via shape

Behind an internal setting only (no UI, no file format), let `Em3dLayoutSolids` build each via's barrel, its wall
fill, its pads and its antipads as **one regular N-gon, the same N and the same rotation for all four**, so their
facets line up. Offer N = 4, 6, 8 and 16. The polygon keeps the circle's **area** (D1). Apply it to every via, or only
to vias farther than a distance *d* from the **path**: the copper galvanically joined to any port, found through the
existing partition (`CopperPieces`), with *d* a multiple of the widest trace on that copper (2 and 3 widths to start).

### R-dfr30-2-2 — Measure the mesh

On the test-pad crop (ask the owner for the workspace; never commit it or name it), Gmsh only, for: round (today), all-N = 16 / 8 / 6 / 4, and
far-only N = 8 and N = 4 at *d* = 2 and 3 widths. Record:

- tetrahedra, nodes and Gmsh wall time;
- peak memory;
- the number of `ill-shaped tets` warnings.

Use a scratch harness, not the Benchmark tier. Put a memory and disk watchdog on every Gmsh and EMerge process (kill
at ~3–4 GB RSS or under 10 GB free disk). Keep each run under five minutes, and **ask the owner before any longer
run.** One table into RESOLVED.

### R-dfr30-2-3 — Measure what it changes, then stop or go

For round, all-8 and far-only 8 / 4 (the best *d*): the electrostatic capacitance matrix (Palace, the fast problem) and
one driven frequency in the band (S11 and S21). **Go** to phase 4 only if far-only reaches **at least 25 % fewer
tetrahedra or 25 % less Gmsh time**, with **|ΔC| under 1 %** and **|ΔS| under 0.01** against round. Otherwise stop: record
the table and the reason in RESOLVED, remove the switch, and report.

### R-dfr30-2-4 — Only on Go: the **Reduced Via Mesh** checkbox (owner, 2026-10-28)

One checkbox, **Reduced Via Mesh**, beside **Robust meshing** in the 3D EM setup and handled exactly as that one is
(`EmSetup.RobustMeshing`, brief-dfr28-3). There are no choices behind it. The polygon (N) and the distance (*d*) are the
best pair phase 3 measured, fixed as constants with the measurement that chose them in their comments. A per-setup
knob is added only if the owner later asks for one.

- **`EmSetup.ReducedViaMesh`**, persisted as `bool?` in `EmSetupPersistence`: **null means off**, so every `.cem` and
  `.c3d` setup written before it existed reproduces its results and keeps its mesh.
- **On in every NEW setup**: `EmSetup.CreateNew` (File ▸ New ▸ EM Setup, the layout editor's EM button) and the 3D
  view's Add (`C3dEditorViewModel.Simulate`), as `RobustMeshing` is. That is the owner's "on by default if it works
  well": it is reached only through the phase 3 Go, so a setup that gets it on gets the measured saving at the
  measured accuracy.
- **Shown where Robust meshing is shown** (`ShowRobustMeshing`: a 3D setup, not thermal), for the Gmsh-meshed
  solvers, Palace and EMerge. openEMS does not read it (§4), and the box is disabled with a tooltip saying so when
  openEMS is the solver.
- **Part of the geometry**: toggling it makes an earlier result stale, as Robust meshing does.
- **Said in the run log, one line**: *"Reduced via mesh: 42 of 61 vias more than 3 trace widths from the path were
  built as octagons."* `check` and `explain` report it. The tooltip says what it does and that vias on or near the
  path stay round.
- **CLI**: the setting is the file's; no flag (a run verb's override replaces a directive, and this is not one).
- Docs: the EM setup reference page, beside Robust meshing.

## 2. Decisions for the owner

- **D1 — which radius the polygon keeps.** (a) **Equal area** (recommended): the barrel's cross-section, and so its
  DC resistance and roughly its inductance, is kept. (b) Circumscribed: the polygon's corners touch the circle, which
  keeps the drilled hole's clearance but makes the barrel larger. (c) Inscribed.
- **D2 — what the path is**, if a cheaper definition is wanted: the copper joined to the ports (recommended, it
  follows the signal through vias and layer changes), or the traces the impedance finder reads on the ports' layers.

## 3. Gates

1. The switch at N = 8: the lowering writes prisms for barrel, fill, pad and antipad with identical vertex angles. The
   entity check passes, and no group loses a volume.
2. Far-only at *d* = 3 widths on a synthetic board (a trace between two ports, a via on it, and a ring of stitching
   vias at 1 and 5 widths): the via on the trace and the 1-width ring stay round; the 5-width ring is N-gons.
3. The phase 2 table and the phase 3 verdict in RESOLVED.
4. Only on Go: a `.cem` with no `ReducedViaMesh` key reads as off and writes a byte-identical `.geo`. A setup made by
   `CreateNew`, and one made by the 3D view's Add, has it on. Toggling it in the setup editor marks the result stale.

## 4. Not in this brief

- openEMS (FDTD): its cost is grid lines, and a round via there is already staircased to the grid. A separate question.
- Cropping to a rectangle rather than to the board's shape: already possible with Crop to New Cell, and a different
  saving.
- Coarser mesh settings away from the path (a size field): a natural next step if phase 3 shows the far region
  dominates. Not started here.
