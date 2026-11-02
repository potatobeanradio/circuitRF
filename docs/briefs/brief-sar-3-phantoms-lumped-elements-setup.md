# Brief SAR-3 — Phantoms, a lumped RLC element, and the FDTD keys replication needs

**Series:** `brief-sar-0-overview.md` (L3, L5) · **Tag:** `R-sar3-<m>`
**Depends on:** SAR-2
**Area:** `src/Design/ThreeD/C3dDocument.cs` (objects), the OCC kernel lowering, `src/Design/Em3d/CsxcadWriter.cs`,
`src/Engine/Em3d/FdtdGrid.cs`, the `.c3d` editor's palette and Inspector (`src/Ui`), `docs/design/sar.md` §5

---

## 1. Goal

Everything SAR-7 and SAR-8 need to rebuild openEMS's two examples from `.c3d` objects alone:
1. **An ellipsoid**, and **layered phantoms** made from ellipsoids, cylinders and boxes.
2. **A lumped capacitor** across a gap (the loop coil has three).
3. **Two FDTD settings**: openEMS's cell-constant material switch, and running on explicitly given grid lines.

## 2. Requirements

**R-sar3-1 — `C3dEllipsoid`.** Centre plus three semi-axes (DBU), axis-aligned in its own frame and placed by the
existing transform, so it rotates like any object. In the OCC kernel it is a scaled sphere. In CSXCAD it is a
`Sphere` under a `Scale` transform when that is exact, and otherwise a `Polyhedron`; which one is written, and why, is
in the writer's comment. A `C3dSphere` stays as it is.

**R-sar3-2 — Layered phantoms, as one object.** `C3dPhantom` expands into nested solids, each naming a material, with
precedence from inside out (the innermost wins), as openEMS's examples set priorities. Shapes:
- `Ellipsoid3` — outer semi-axes plus two shell thicknesses, so skin, skull and brain stay concentric. It covers the
  dipole head phantom and the 298 MHz head phantom.
- `Cylinder3` — a radius, a length and two shell thicknesses (the 128 MHz body phantom).
- `FlatBox` — a liquid volume in a shell of given thickness and εr: the flat phantom of phone SAR (SAR-12).
- `Sphere1` and `Box1` — homogeneous ones, for analytic checks.

Each phantom's materials are chosen in the Inspector from the workspace's materials; the defaults are SAR-2's
openEMS-phantom records. The object tree shows it as one row with its layers under it, like a group. Flatten (the
existing 3D Flatten) turns it into ordinary solids.

**R-sar3-3 — `C3dLumpedElement`.** A gap, given exactly as a lumped port's (same face and edge picking, same snapping
rules, brief-em3d-123), carrying **series or parallel R, L, C**, each optional, with units. openEMS: a `LumpedElement`
with `R`/`L`/`C`, using v0.37.0's series and parallel forms; the writer refuses a combination 0.37.0 cannot express,
naming it. It is drawn like a port with a capacitor glyph. It is **not** a port: it adds no S-parameter row. It is a
tunable expression like any `.c3d` value (`C=Ctune`), so the tuning series can sweep it by re-running.

**R-sar3-4 — Cell-constant material.** `OpenEms.CellConstantMaterial` (bool). Writes openEMS's
`CellConstantMaterial="1"`. **Default: on for a setup that requests SAR (SAR-6), off otherwise**, so every existing
setup writes exactly what it wrote before. The setup editor shows it in the openEMS group.

**R-sar3-5 — Explicit grid lines.** `OpenEms.GridLines`: per axis, either a list of coordinates (with a unit) or a
path to a CSXCAD XML file whose `RectilinearGrid` is taken verbatim. With it, `FdtdGrid` places **no** line of its own.
It refuses with a sentence when a port, lumped element or required line (brief-em3d-123) falls between lines it needs.
This is how SAR-7 and SAR-8 run on the reference's own grid; it is also a feature for anyone porting an openEMS
script.

**R-sar3-6 — Boundaries.** Confirm that `PML_8` on all faces and `MUR` on all faces, the two examples' choices, are
both expressible and written identically to the scripts' XML. Add whatever is missing.

## 3. Not in this phase

Voxel and mesh import (SAR-11); a lumped element on Palace (its series).

## 4. Gates (minimal)

- `C3dEllipsoidTests`: volume against 4/3·πabc to kernel tolerance; the CSXCAD text for a scaled sphere.
- `C3dPhantomTests`: an `Ellipsoid3` writes three solids with the right precedence; Flatten gives the same solids.
- `LumpedElementCsxTests`: a 5.4 pF element writes the same `LumpedElement` attributes as SAR-1's `MRI_Loop_Coil`
  XML.
- `ExplicitGridTests`: reading the SAR-1 dipole XML's grid reproduces its lines exactly; a port off the lines is
  refused.
- One test that a setup without SAR writes byte-identical XML to before this phase.
