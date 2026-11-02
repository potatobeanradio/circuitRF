# Brief 65 — both solvers take a kernel solid, and say what they cannot respect

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d65-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.2 (Route A, the `.geo` recipe), §6.5 (the FDTD lowering
and its grid); [`em-3d-f0-findings.md`](../design/em-3d-f0-findings.md) Q5, Q6, Q8
**Area:** `src/Design/Em3d/GmshGeoWriter.cs`, `src/Design/Em3d/CsxcadWriter.cs`, `src/Design/Em3d/{PalaceRun,
OpenEmsRun,Em3dRunService}.cs`, `src/Engine/Em3d/FdtdGrid.cs`, new `src/Engine/Em3d/Em3dFidelity.cs`,
`src/Engine/Em3d/Em3dFaceBoundary.cs` (`FaceBoundaryPieces` for kernel faces), `src/Cli/Check.cs`,
`src/Ui/ThreeD/` (the Setups panel's fidelity lines), `tests/Ui.Tests/Em3d/`, `tests/Engine.Tests/Em3d/`
**Depends on:** 64 · **Blocks:** 70

---

## 0. What this brief delivers

Brief 64 put `Em3dShapeSolid` into the problem and made both backends refuse it. This brief makes both write
it, and answers the owner's question — *do the solvers respect a fillet or a chamfer?* — **per object, per
solver, before the run and in the run's own notes**:

| | How the kernel solid reaches it | Curved geometry | What circuitRF says |
|---|---|---|---|
| **Palace** | the worker's B-rep (or STEP, D12) imported by the `.geo` with `ShapeFromFile`; faces recovered by the worker's tight per-face boxes | **respected** — curvature sizing and second-order elements, both already in the writer | a **warning** where the conductor model is known to be wrong for the radius (§4b); a **note** where a small feature will dominate the mesh (§4c) |
| **openEMS** | the worker's tessellation at a grid-derived deflection, as a PLY file read by CSXCAD's `PolyhedronReader` | **staircased** to the grid — FDTD's nature | per object: how many cells span its smallest rounded feature; *not represented* below one (§4a) |

One function states every one of those findings — `Em3dFidelity.For(problem, solver, grid)` — and three
surfaces read it: the run's notes, `check`, and the editor's Setups panel before Simulate.

**Nothing an existing problem writes changes** (gate 1). Every new line in either writer is written only
for a problem that contains a kernel solid.

---

## 1. Something the writer already does, and what it means for this brief

`GmshGeoWriter` **already** sizes every curved surface by its curvature — globally, for every problem:

```
Mesh.MeshSizeFromCurvature = 12;     // CurvatureElements: elements per full turn
Mesh.ElementOrder = 2;               // curved second-order elements
Mesh.HighOrderOptimize = 2;
```

`CurvatureElements = 12` was set for via barrels and round wires (a 1 mil wire's surface elements at about a
quarter of its diameter, F0 case A). It applies unchanged to any surface `ShapeFromFile` brings in, so a
fillet imported from the worker is meshed at twelve second-order elements per full turn — three across a 90°
fillet — **with no new line in the script**. There is no lower bound on it (`Mesh.MeshSizeMin` is not set).

So in Palace the question is not *"is the fillet resolved?"* — it always is — but *"what does resolving it
cost, and is the physics model right at that radius?"* §4 is written for that reading. (Overview §1e
anticipated a new curvature rule; none is needed, and adding a bound would change every existing problem's
mesh.)

## 2. `R-em3d65-2` — Palace

**`R-em3d65-2a` The hand-off (D12).** For each kernel solid the run service asks `GeometryKernel` to
`export` it — in **micrometres**, the `.geo`'s own length unit — into the run directory as
`kernel-<BrepHash[..16]>.brep` (or `.step` if brief 61's Q4 found Gmsh's embedded OCCT cannot read our
B-rep at every validated Gmsh version; if it can only read an older B-rep format version, the worker writes
that version, and brief 61 records which). The script then states the solid as

```
s7[] = ShapeFromFile("kernel-3f9a0c1e2b4d5a6f.brep");
If (#s7[] != 1) Printf("kernel_import_count %g", #s7[]) >> "entities.txt"; EndIf
```

in place of the `Box` / `Cylinder` / `Extrude` a managed primitive would get, under the same `s<i>[]` name,
so **the rest of the recipe is untouched**: the cuts by higher-precedence solids (`Em3dPrecedence`), deleting
conductors as voids, the one `BooleanFragments`, `OCCBooleanPreserveNumbering`, and the entity table that
closes the books (F0 Q5 caveat 3). A `.step` hand-off carries its own unit; the script then sets
`Geometry.OCCTargetUnit = "UM";` before the import — a line written only in that case.

**`R-em3d65-2b` Faces are recovered by the worker's boxes.** Wherever a kernel solid's surface must be
named — a conductor's void, a face boundary, a port landing on it — the recovery is the existing **tight
`BoundingBox` query** (§6.2, F0 Q5), now built from the face table's **tight box** per named face
(`Em3dShapeFace`) instead of from circuitRF's own primitive arithmetic. `Geometry.OCCBoundsUseStl = 1` is
already written, which is what makes Gmsh's own boxes around a curved face tight enough to compare. A named
face with `#n` pieces (brief 64 §2a) expects `n` surfaces, and the count is checked in the entity table as
every other group's is; a shortfall is the existing classification refusal, naming the face.

**`R-em3d65-2c` Face boundaries on kernel faces** go through `FaceBoundaryPieces` like any other: a kernel
face's pieces are its boxes (Palace needs no polygon). Brief 64's resolution of `<tool>:<face>` references
happens before this; the writer only ever sees an object's face as the problem names it.

**`R-em3d65-2d` Determinism.** The `.brep` is the worker's bytes for that hash, and the `.geo` refers to it
by hash, so the script is byte-deterministic (R-em3d7-2b) and a re-run with an unchanged problem reuses its
mesh, as today.

## 3. `R-em3d65-3` — openEMS

**`R-em3d65-3a` The tessellation is requested for the grid, not for the screen** (overview §1j). After
`FdtdGrid` has placed its lines (§3c), the writer asks the worker to `tessellate` each kernel solid with a
linear deflection of **one quarter of the smallest grid cell inside the solid's box** and an angular
deflection of 0.25 rad. Finer than the grid can see, so the staircase is decided by the grid lines and not
by where the facets happen to fall; no finer, so a large imported part does not cost triangles the grid
throws away. The display tessellation (brief 64) is never used for this.

**`R-em3d65-3b` Written as PLY, read by `PolyhedronReader`.** Each kernel solid becomes
`kernel-<hash>-<deflection>.ply` beside the XML (ASCII, numbers through the writer's `R()` formatting, so the
file is byte-deterministic), and a `<PolyhedronReader FileName="…" FileType="PLY" Priority="…">` on the
solid's property, at `Em3dPrecedence`'s priority. PLY rather than an inline `<Polyhedron>`: an imported part
can run to hundreds of thousands of triangles, and a file keeps the XML readable. F0 Q8 verified both STL
and PLY read.

**`R-em3d65-3c` Checked before the run.** F0 Q8: a file CSXCAD cannot open is **skipped with a warning and
exit code 0** — openEMS solves without the solid. So immediately before launching, `OpenEmsRun` checks that
every `PolyhedronReader` file named in the XML exists, is non-empty, and hashes to what the writer wrote. A
failure is a refusal naming the solid and the file, and nothing is launched.

**`R-em3d65-3d` The polyhedron's missing face nodes.** Brief 42 measured that openEMS's polyhedron leaves
out grid nodes lying **exactly on** its faces (518 of an equal Box's edges became 78 on a 35 µm strip), and
the grid places a line on every axis-aligned face. A kernel solid's planar axis-normal faces — a lid's top, a
cavity's walls — would lose a cell of metal the same way. The fix here: **the tessellation's vertices on an
axis-normal planar face are moved outward along that axis by 10⁻⁴ of the local cell**, which puts every grid
node on the true face strictly inside the polyhedron and no node that was outside it inside. The outward
offset is recorded in the run's notes. The existing `onFaces` note stays for managed polyhedra, which this
brief does not change.

**`R-em3d65-3e` Grid lines from a kernel solid** (`FdtdGrid`, the shape extraction that today returns
`Rings = null` for a curved or swept solid): a kernel solid contributes
- its **extremes** on each axis, as a curved solid does today (*"Curved or swept: its extremes only; the grid
  staircases the rest"*);
- a line at every **planar axis-normal face** in its face table (kind `plane`, zero thickness along one axis),
  as `MaterialFace`, or `MetalExtreme` for a conductor;
- for a conductor, the **thirds rule** at each **straight axis-parallel edge** in its edge table, exactly as a
  rectangle's metal edges get it.
Oblique planar faces (a chamfer) and curved faces contribute nothing between their extremes; the grid
staircases them, and `Em3dFidelity` says so (§4a).

**`R-em3d65-3f` Face boundaries on kernel faces.** openEMS states a face boundary as a zero-thickness sheet
coincident with the face (brief 49). A **planar** kernel face gets one: its polygon is the boundary loops of
its triangle range, outer ring and holes, taken in index order so the result is deterministic. A **curved**
kernel face cannot be a CSXCAD sheet, and a boundary on one is refused for openEMS: *"The boundary on
'lid', face 'bore:side', is on a curved face, which openEMS cannot state as a sheet. Palace can; or put the
boundary on a planar face."* Palace takes it (§2c).

## 4. `R-em3d65-4` — the fidelity findings: what each solver will not respect

**`R-em3d65-4a` openEMS: cells across the smallest rounded feature.** For each kernel solid, for each curved
face (kind `cylinder`, `cone`, `sphere`, `torus`, `bspline`) with smallest radius of curvature *r* (a
torus's minor radius), and each chamfer face (`chamfer(<edge>)`, planar and oblique) with width *w*, the
local cell Δ is the **largest** of the three axes' cell sizes inside that face's tight box — the staircase is
as coarse as its coarsest axis. Then:

| Ratio | Severity | Sentence |
|---|---|---|
| *r*/Δ < 1 | **warning** | *"openEMS will not represent the 50 µm fillet on 'lid' (fillet(xmax\|zmax)): the grid cell there is 120 µm, so the edge is solved as sharp."* |
| 1 ≤ *r*/Δ < 4 | **warning** | *"openEMS staircases the 200 µm fillet on 'lid' with about 2 cells; expect the answer near it to depend on the grid, not the radius."* |
| *r*/Δ ≥ 4 | note | *"openEMS staircases the curved faces of 'lid' at ≥ 4 cells across their smallest radius (310 µm); refining the grid converges them."* |
| chamfer *w*/Δ < 1 | **warning** | *"openEMS will not represent the 20 µm chamfer on 'pin': the grid cell there is 40 µm, so the edge is solved as square."* |
| chamfer *w*/Δ ≥ 1 | note | *"openEMS solves the 80 µm chamfer on 'pin' as a staircase of about 2 steps."* |

A solid with several rounded features gets **one** line naming its worst one and how many others there are,
so a connector with forty fillets is one warning, not forty.

**`R-em3d65-4b` Palace: the conductor model at small radius.** Palace's conductivity boundary is a
**flat-surface** impedance (F0 Q6: on a round wire it read the resistance **9.5 % low at 1 GHz, 3.1 % at 10,
1.5 % at 40** — at 1 GHz the wire's radius was about five skin depths). So for a **conductor** kernel solid,
each curved face whose smallest radius *r* is below **ten skin depths at the sweep's lowest frequency** is a
**warning**. This is the rule `Em3dRunService` **already** applies to managed round conductors
(`ThinRoundConductors`, the *"round and under ten skin depths"* run note, which cites the same F0 Q6 numbers);
this brief extends it to kernel faces with the same threshold and the same δ = 1/√(π f μ σ) that
`Em3dFaceBoundary` computes, and does not change the existing note's text (§4e):
*"Palace treats the surface of 'pin' as flat for its loss; its 40 µm radius is about 6 skin depths at 1 GHz,
where a round conductor's loss was measured 9.5 % low at 5 skin depths. The loss there is likely under-stated
by several percent."* The geometry itself is respected; the physics on it is approximated, and that is what
the owner asked to be told.

**`R-em3d65-4c` Palace: a feature that dominates the mesh** (a **note**, not a warning — nothing is wrong
with the answer). Curvature sizing puts elements of about 2π*r*/12 on a face of radius *r* (§1). Where that
is below **one quarter** of the smallest size the writer asks for anywhere else (`sizeEdge`, the conductor
and sheet refinement), the face is setting the mesh: *"The 5 µm fillet on 'lid' puts elements of about 2.6 µm
on it — a twentieth of the smallest elsewhere — and will dominate the mesh. Disabling it (its Enabled box)
shows whether it matters."* Nothing is clamped; the note is the whole remedy, and the Enabled toggle
(overview §1f) is the experiment it points at.

**`R-em3d65-4d` One function, three surfaces.** `Em3dFidelity.For(Em3dProblem, Em3dSolver, FdtdGridResult?)`
in `src/Engine/Em3d` returns `Em3dFidelityFinding(Object, Face, Solver, Severity, Sentence)` rows, computed
from the face tables and the grid alone — **no worker call, no solver** — so it is cheap enough to run on
every edit:
- **run notes** — `CsxcadLowering.Notes` and the Palace run's notes carry the rows for their solver; the
  FDTD rows are also appended to `FdtdGridResult.Warnings` where the existing oblique-face warning lives;
- **`check`** — for each embedded setup (and each `.cem` naming the `.c3d`), a finding per row at the row's
  severity; warnings never change the exit code (cli.md §10: warnings are always reported and still exit 0);
- **the Setups panel** (brief 49) — under each setup, the rows for its solver, recomputed when the document
  or the setup changes, with the warning icon the panel already uses; *Simulate* is not blocked by them.

**`R-em3d65-4e` Kernel solids only.** A managed cylinder or wire is not given these rows in this brief: its
notes — including the existing `ThinRoundConductors` note, which stays a run note with its exact wording —
are part of existing run output and gate 1 holds them fixed. Extending `Em3dFidelity` to managed
curved primitives is a one-line follow-up the owner may ask for; it would change existing notes, which is
why it is not done silently.

## 5. Gates

1. **Byte identity.** Every existing Palace `.geo` and configuration golden, every openEMS XML golden, every
   `FdtdGrid` line list and every tessellation count is identical; a problem with no kernel solid writes no
   new line in either writer.
2. **The `.geo` for a kernel solid** (headless, a hand-built `Em3dShapeSolid` — no worker needed): the
   `ShapeFromFile` line under the solid's `s<i>[]` name, the import-count check, one recovery query per named
   face built from its tight box, expected counts equal to its `#n` pieces; the `.step` variant adds the unit
   line.
3. **Gmsh reads it** — `[KernelFact, GmshFact]`: a box minus a cylinder, exported by the worker, meshed by
   Gmsh from the written script; the entity table reports every group claimed and
   `unclassified_single_sided 0`. About a second; no Palace run.
4. **The PLY** is byte-identical across two writes; its deflection is a quarter of the smallest cell in the
   solid's box (assert the number the writer used); the pre-run check refuses a truncated file and a missing
   one, naming the solid.
5. **Grid lines.** A kernel box-with-a-bore conductor gives lines at the box's six faces, thirds-rule lines at
   its straight edges, and only the bore's extremes; the counts are asserted.
6. **Face nodes** — `[KernelFact, OpenEmsFact]`: a kernel box (a boolean whose Tool misses) and the equal
   managed `Box` give the same count of metal grid edges, measured the way brief 42's RESOLVED entry measured
   518 against 78. The one openEMS run in the routine tier; it must stay under ~5 s or go to Benchmark.
7. **Fidelity table** (headless, synthetic face tables): each row of §4a at *r*/Δ = 0.5, 2, 5 and a chamfer at
   0.5 and 2; §4b at 5 and 15 skin depths; §4c at a twentieth and at a half; forty fillets give one row naming
   the worst.
8. **Curved face boundary** in openEMS is refused with §3f's sentence; the same boundary lowers in Palace.
9. **Agreement** — `Category=Benchmark`: the showcase-lite case (a coax launch through a filleted bore) in
   both solvers, the fidelity rows printed beside the difference, recorded in the RESOLVED entry.

## 6. Owner check list (Debug build)

1. In a copy of the 3D Package example, subtract a cylinder from the lid and fillet the bore's rim with
   50 µm (hand-written per brief 64 if brief 67 is not built yet).
2. Select an openEMS setup: the Setups panel shows *"openEMS will not represent the 50 µm fillet…"*. Refine
   the grid until it says *"staircases … with about 2 cells"*.
3. Select a Palace setup: no fidelity warning for the lid (a lid-alloy lid's rim radius is far above ten skin
   depths). Run `circuitrf check` on the file: the same rows, exit 0.
4. Simulate in both; read the notes in each run's Messages output.

## 7. Scope

- **No new mesh rule for Palace.** Curvature sizing and second-order elements already exist (§1); nothing is
  clamped.
- **No change to managed primitives' lowering or notes** (§4e, gate 1).
- **No solve in the routine tier** beyond gate 6's; everything else is a script, a file, or a table.
- **The fidelity findings never block a run.** They inform; the user decides.
- Findings go in `src/Design/RESOLVED.md` (writers) and `src/Engine/RESOLVED.md` (`Em3dFidelity`, grid),
  never `CLAUDE.md`.
