# Brief 42 — elaborate and run a `.c3d`, headless

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d42-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.1 (the neutral problem), §4.6 (headless), §6.3–§6.5
**Area:** `src/Engine/Em3d/Em3dProblem.cs`, `Em3dTessellation.cs`, `FdtdGrid.cs`;
`src/Design/Em3d/{GmshGeoWriter,CsxcadWriter,Em3dRunService}.cs`;
`src/Design/Layout/Em3d/Em3dGenerator.cs` → new `Em3dLayoutSolids.cs`; `src/Design/ThreeD/` (elaborator,
provenance, hierarchy); `src/Design/Layout/Em/{EmSetupModel,EmSetupPersistence,EmSetupResolver}.cs`;
`src/Cli/{Em3dSetupSource,Explain,ExplainEm3d,RenderEm3d,Check}.cs`
**Depends on:** 41 · **Blocks:** 43, 48, 49
**Owner decision D5:** setups are both embedded in the `.c3d` and reachable from a `.cem`.

---

## 0. What this brief delivers

A `.c3d` becomes an `Em3dProblem`, and `circuitrf em x.c3d` solves it, with no window open. This is the
brief that makes the overview's one rule true: **the editor will draw exactly what this produces**.

- Every object lowers to a neutral primitive, including two new ones: a **polyhedron** and a **sheet in
  any plane**. Both backends take them.
- **Instances** elaborate, of both views: a 3D view recursively, and a layout view through **its own
  technology**.
- Units, materials across technologies, arrays and placements are all resolved to SI.
- **Setups:** embedded in the `.c3d`, or in a `.cem` whose `LayoutRef` names a `.c3d`.
- `em`, `check`, `explain` and `render` accept a `.c3d`.

---

## 1. `R-em3d42-1` — the neutral problem grows (overview §1h)

**`R-em3d42-1a` `Em3dPolyhedron(Vertices, Faces)`**, metres. Each `Em3dFace` has:
- an outer ring and holes, as vertex indices;
- a **face name** (overview §1e), carried so brief 49's face boundaries and the viewer's face picking can
  find the face again.

Faces are planar, and `Validate` checks planarity (relative 1e-9 of the solid's extent), closure and
positive volume, **all problems not the first**.

**`R-em3d42-1b` A sheet in any plane.** `Em3dSheet` gains an optional frame (origin, u, v in metres, as
unit vectors). Null means today's horizontal sheet at `Z`, so every existing problem is unchanged.

**`R-em3d42-1c` Lowering, per backend:**
- **Palace / Gmsh.** A polyhedron is written as `Point`, `Line`, `Curve Loop` (holes as further loops),
  `Plane Surface`, `Surface Loop`, `Volume`, under the OCC factory, before the fragment. The volume and
  its surfaces are then recovered **by bounding box, counted against expectation**, as today (§6.2). A
  face that is not exactly planar after the transform to metres is written as triangles.

  **Check first, in a scratch run, that the OCC factory accepts a planar surface with a hole from a curve
  loop list**, and record the Gmsh version.
- **openEMS / CSXCAD.** CSXCAD's `Polyhedron` primitive, faces triangulated by `Em3dPolygonTriangulation`,
  **on the solid's property, at its construction-order priority**.

  **Check before relying on it** that the CSXCAD version brief 9 validated reads an inline polyhedron.
  F0 verified only the file reader (`PolyhedronReader`). If the inline element is unsupported, write the
  PLY triangle file F0 verified, and check it exists before the run (§6.5: a missing file is silent).
- **FDTD grid.** Grid lines on every vertex coordinate of every axis-aligned edge. An oblique face is
  staircased, and the grid report says which object has one, as a curved surface is reported today.
- **Tessellation** (`Em3dTessellation`). Faces are triangulated in their own plane by the existing ear
  clipper. Each triangle carries its face's index, which brief 43's face picking needs.

**`R-em3d42-1d` The richest exact primitive** (overview §1g). An object lowers as follows:

| Object | Lowers to |
|---|---|
| Box, placement a pure translation or 90° multiples | `Em3dBox` |
| Prism on XY, no `Shear`, rotation about z only | `Em3dExtrudedPolygon` (rotated outline) |
| Cylinder | `Em3dCylinder` (it already takes any axis, so the placement's rotation is applied to it) |
| Anything else | `Em3dPolyhedron` |

A test pins this table, because the FDTD path's cost depends on it.

## 2. `R-em3d42-2` — the layout's solids, split out (overview §1i)

**`R-em3d42-2a`** `Em3dLayoutSolids.From(layout, tech, wires, options)` returns **only** geometry:
- solids, sheets and materials;
- each object's origin (`Em3dObjectOrigin`, already there);
- its notes.

It returns **no** air box, ports, sweep, air solid or solve region. `Em3dGenerator.Generate` is rewritten
to call it and then add those.

**`R-em3d42-2b` Gate before anything else in this brief: the generator's output is byte-identical.**
Dump the `Em3dProblem` of every shipped 3D example and every Em3d test fixture **before** the split, as
text. After the split, the same dumps must match exactly. The standing rule for refactors — dump first,
keep the old arithmetic as a named reference — is how briefs 4 and 31 were kept honest too.

**`R-em3d42-2c` Bounded extent for an instance.** When the options say *instance*:
- the dielectrics and the ground-reference planes are bounded by the board outline, or else by the drawn
  geometry's bounding box;
- the note says which.

A `.cem` run keeps today's behaviour exactly (gate 2b).

**`R-em3d42-2d` Wires.** A `.clay` instance brings its stem-paired `.wBond` wires, as a `.cem` run of that
layout does. They are part of its geometry.

## 3. `R-em3d42-3` — the elaborator

**`R-em3d42-3a`** `C3dElaborator.Elaborate(document, path, workspace)` returns a `C3dElaboration`:
- the **geometry** as `Em3dSolid`s, `Em3dSheet`s and `Em3dMaterial`s, in metres, in construction order;
- a **provenance map**: every problem object, and every face, back to (instance path, document object,
  face name). The editor uses it for picking (brief 43) and brief 49 for boundaries;
- **notes and refusals**, all of them.

Elaboration of the geometry is **independent of any setup**. A setup (§5) adds the air box, ports, sweep
and boundaries on top. So the editor can elaborate and draw with no setup at all.

**`R-em3d42-3b` Units.** DBU → metres through `LayoutUnits`' exact decimal path, then placement in
doubles. A child document with a different `DbuPerMicron` or `DisplayUnit` elaborates in its own units
and arrives in metres. **No geometry is rounded to the parent's DBU.**

**`R-em3d42-3c` Instances:**
- **`View: ThreeD`**: the child cell's primary `.c3d`, elaborated recursively.
- **`View: Layout`**: the child's primary `.clay`, through `Em3dLayoutSolids` with *instance* options and
  the child's **own** technology (`TechnologyResolver` from the layout's own path, never the parent's).
- **Resolution:** `CellFolder.ResolvePrimary`, including `ws://` references through `ExternalCellRef`.
  **`ExternalWorkspaceGate` is not consulted** (overview §1i), and the reference page says why.
- **Placement then array.** Each array element's origin is the pitch in the **parent's** frame, as
  `LayoutInstanceTransform.ArrayCellOrigin` defines it for layout. Counts are ≥ 1 on each of three axes.
- **Names.** An object inside an instance is named `<instance>/<object>`, nested for depth. An array
  element is `<instance>[i,j,k]/<object>`. These are the names a port or a terminal uses to name a
  sub-cell conductor (brief 49).
- **Ignored, and said once in the notes:** the child's ports, setups, face boundaries, a `.clay`'s port
  shapes, and a `.clay`'s `.cem` solve region (overview §1k).
- **A missing child cell or view** is a refusal naming the instance and the path tried. It is never an
  empty instance.

**`R-em3d42-3d` Cycles.** A `.c3d` that reaches itself through instances is refused, with the cycle
spelled out as `A/U1 → B/U3 → A`. Only `.c3d` → `.c3d` can cycle, because a `.clay` never references a
`.c3d`. Check this at elaboration here; brief 48 adds the edit-time check.

**`R-em3d42-3e` Materials** (overview §1j):
- every object's material resolves in **its own document's** technology;
- equal values under one name merge;
- different values are kept as `<name>@<technology name>`, with a note listing both value sets.

**Role inference:** a material with σ and no εr is a **conductor**; one with εr is a **dielectric**; the
name `Air` is **air**. An explicit `Role` wins. A material with both σ and εr and no `Role` is
**refused**, naming the material. Guessing whether a lossy dielectric is a conductor is exactly the call
the user must make.

**`R-em3d42-3f` Sheets:**
- a `Sheet` object with a conductor material is an `Em3dSheet` carrying its `ThicknessUm`;
- a sheet with a dielectric material is refused, because it has no meaning;
- a `Polyline` is skipped (brief 41 §2e).

## 4. `R-em3d42-4` — caching, because the editor will call this on every edit

Elaboration is per object and per instance. `C3dElaboration` is built from:
- a **per-object cache**, keyed by the object's serialized form and placement;
- a **per-child cache**, keyed by (child file path, file stamp, view, technology file stamp) — the stamp
  rule brief 28 learned (`MeshStamp`: a re-written file at the same path is not the same file).

An edit to one object re-elaborates **one object**. Gate 7 counts it.

## 5. `R-em3d42-5` — setups (D5)

**`R-em3d42-5a` Embedded.** The `.c3d`'s `Setups` holds `EmSetup` objects in the `.cem` JSON schema,
through `EmSetupPersistence`'s own serializer: **one schema, two containers**.
- `LayoutRef` is not written, because the geometry is the document.
- `Name` is required and unique.
- A setup must be 3D (`Solver3D` set). A planar analysis is refused with *"a `.c3d` is solved by the 3D
  solvers; set Solver3D to Palace or openEMS"*.

**`R-em3d42-5b` From a `.cem`.** A `.cem` whose `LayoutRef` names a `.c3d` elaborates that document.
Rules:
- **one field, not two**: the reference page says `LayoutRef` names *the geometry document: a `.clay` or a
  `.c3d`*;
- a planar analysis is refused, as above;
- the `.c3d`'s own embedded setups are **not** consulted: the `.cem` is the setup.

`EmSetupResolver`'s walk-ups apply unchanged, and the `.c3d` resolves its technology from its own path.

**`R-em3d42-5c` Problem assembly.** Geometry (§3) plus the setup:
- the air box, from the elaborated content's extent plus the setup's padding, through the same padding
  code the generator uses (now shared, §2);
- the sweep, the temperature, the problem type;
- ports and face boundaries, which brief 49 defines. Until then a setup with no port is refused as it is
  today.

**`R-em3d42-5d` Where results land.** `EmRunService.ResolveSnpPath` is predictable by design (CLAUDE.md,
`em`). For an embedded setup the result is named as if a `.cem` called `<c3d stem> <setup name>.cem`
stood beside the `.c3d`. A test pins the path. Series 2's results layout is otherwise unchanged.

## 6. `R-em3d42-6` — the CLI

- **`em x.c3d`** runs its one setup. With several, it is a refusal listing them, answered by
  `--setup <name>` (`render --view`'s pattern). `em x.cem` naming a `.c3d` works as any `.cem` does. Exit
  codes, cancellation and output paths are those of the `.cem` run.
- **`check x.c3d`** adds elaboration's refusals to brief 41's, through the elaborator the GUI will use.
- **`explain x.c3d`** reports the **walk**:
  - each instance: which cell folder, which view file, which technology, and from where each was resolved;
  - each unit conversion;
  - each material merge or qualification;
  - the lowering table's choice per object.

  `--extents` gives the elaborated bounds in the document's display unit.
- **`render x.c3d -o s.svg`** draws the same sections brief 5 draws for a 3D `.cem`
  (`Em3dSectionRenderer`), from the elaboration. This is the one headless picture of a `.c3d` (overview
  §4).

## 7. Gate

`tests/Ui.Tests/ThreeD/`, `tests/Ui.Tests/Em3d/` (the existing goldens), `tests/Engine.Tests`,
`tests/Firewall.Tests`.

1. **Every existing Palace and openEMS golden and every tessellation count is byte-identical** (overview
   §1h).
2. **The generator split is byte-identical** (§2b), on the pre-split dumps.
3. **The equivalence oracle.** A `.c3d` holding one **layout instance** of the Package example at the
   origin, plus an embedded copy of `Package C.cem`'s setup, assembles an `Em3dProblem` equal to the one
   the `.cem` assembles — except for the documented differences, each asserted by name:
   - the `U1/` name prefix;
   - the bounded extent, where the `.cem`'s layout has no outline.

   This is the brief's strongest test. Two routes to one problem must agree.
4. **Polyhedron lowering.** A box drawn as a `Polyhedron` and the same box drawn as a `Box` write
   Palace `.geo` volumes with equal bounding boxes and equal volume, and CSXCAD primitives covering the
   same cells in the FDTD grid.
5. **Units.** A µm child in a mil parent at a non-integer-mil offset lands at the exact metre position
   (to 1e-15 relative), with no rounding to either DBU.
6. **Materials.** Two technologies with `Gold` at different σ give two qualified materials and a note. At
   equal σ they give one.
7. **Cache.** Editing one object of a 1,000-object document re-elaborates 1 object and 0 children
   (counter).
8. **Cycle.** A → B → A is refused, with the path.
9. **A small solve.** A drawn box-on-ground microstrip through Palace (the smallest mesh that tests it),
   compared with the same geometry generated from a `.clay` through a `.cem`. |S21| and phase agree to the
   tolerance brief 7's gate 6 used. `Category=Benchmark` if it runs over ~5 s.
10. **CLI** `em x.c3d` as a process gives a Touchstone byte-identical to the in-process run, except for
    the provenance timestamp (the `em` gate's rule).

## 8. Scope

- No window (43), no ports authoring (49), no face boundaries (49).
- No expressions (51).
- The ignored sub-cell parts (§3c) are listed in the notes **once per elaboration**, not once per
  instance, or an array of 100 would print 100 lines.
