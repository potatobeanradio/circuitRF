# Brief 102 — A sphere primitive in the 3D editor

**Tag:** `R-em3d102-n` · **Series:** 3D editor (follows 101).
**Area:** `src/Design/ThreeD/C3dDocument.cs` (+ `C3dPersistence`, `C3dBindings`, `C3dValidation`, `C3dLowering`,
`C3dKernelUse`, `C3dHierarchy`, `Occ/GeometryKernelTree.cs`, `Kernel/C3dFaceEdit.cs`), `src/Engine/Em3d/Em3dFaceBoundary.cs`,
`Em3dTessellation.cs`, `src/Render/Scene3D/Scene3DBuilder.cs`, `tools/geometry-worker/geometry_worker.cpp`,
`src/Ui/ThreeD/Tools/` (new `SphereTool.cs`), `src/Ui/ThreeD/C3dEditorViewModel.{Draw,FaceEdit,MenuState}.cs`,
`C3dEditorViewModel.cs` (tree groups), `C3dPropertiesViewModel.cs`, `src/Ui/ThreeD/Tools/C3dDrawTool.cs` (`C3dToolKind`),
`src/Ui/Views/ThreeD/C3dEditorView.axaml`, `src/Ui/Views/WorkspaceWindow.axaml` (both 3D ▸ Draw menus), `src/Cli/DocumentSchema.cs`,
`src/Cli/ExplainEm3d.cs`, `docs/user/src/reference/drawing-in-3d.md`
**Depends on:** 45 (draw tools), 64 (operations in the document), 66 (booleans in the editor) · **Blocks:** —

## Why

The 2D layout editor has a circle; the 3D editor has no round solid except the cylinder. A sphere is the 3D counterpart. It
is not central to RF package or connector design, but it has real uses. The main one is as a boolean tool: a dimple, a
spherical cavity, or a lens cut from a block. Others are a solder ball or bump on a pad (today that can only be faked with
a cylinder), a radome or probe tip, and a simple scattering test object for checking a solver set-up against the analytic
Mie answer.

**It is cheaper than a new primitive usually is, because the numeric side already exists.** `Em3dSphere` has been in
`Em3dProblem` since the ball-bond work, and every solver writer, the tessellator, STEP export and the size estimates already
handle it (§0). What is missing is everything ABOVE the numeric layer: a design object, its file form, a draw tool, the
Inspector, the kernel node, and the refusals a curved face needs. Expect roughly the size of brief 45's cylinder slice, not
a new subsystem.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| `Em3dSphere(Point3 Center, double Radius)`, its zero-radius refusal, its bounds | `src/Engine/Em3d/Em3dProblem.cs` ~99, ~747, ~803 |
| Tessellation: 32 longitude segments × 16 latitude bands, poles exact | `Em3dTessellation.Of` → `Builder.Sphere`, `Em3dTessellation.cs` ~65, ~211 |
| Gmsh `.geo` (Palace, thermal) — `Sphere(v) = {…}` | `src/Design/Em3d/GmshGeoWriter.cs` ~847 |
| openEMS CSXCAD — `<Sphere>` | `src/Design/Em3d/CsxcadWriter.cs` ~646 |
| STEP export, and its fingerprint | `src/Design/ThreeD/Step/StepExport.cs` ~668, ~749 |
| A placed sphere moved by a placement (centre transformed, radius kept) | `C3dLowering.Transform`, ~313 |
| FDTD feature size, solver guidance ("round conductors"), volume estimate | `FdtdGrid.cs` ~1236, `Em3dSolverGuidance.cs` ~53/~106, `Em3dSizeEstimate.cs` ~187 |
| `explain` names it `sphere` | `src/Cli/ExplainEm3d.cs` ~477 |
| The worker already links `BRepPrimAPI_MakeSphere` (for ball bonds, mesh path) | `geometry_worker.cpp` ~2145 |
| The worker leaves seams and degenerate edges out of the feature edges (a sphere's seam and its two pole edges) | `geometry_worker.cpp` ~1034–1049 |
| An old worker refuses an unknown node kind with a sentence, not a crash | `geometry_worker.cpp` ~1525: `this worker cannot build a "…"` |
| **The model to copy, end to end: the cylinder** — `C3dCylinder`, `CylinderTool`, its bindings, validation, lowering, kernel node, Inspector labels, tree group, refusals on its curved side | grep `C3dCylinder` (16 production files) |

**Two places where existing code assumes a sphere is a BALL BOND, and both must change:**

- `Scene3DBuilder.KindOf`, ~646: with no origin entry, `Em3dSphere` is classed `Scene3DKind.Wire`. A user's sphere comes
  through the 3D elaborator's origins and should land on its material's kind (Conductor/Dielectric). **Verify that first.**
  If it does not, the fix goes in the origin path, not in this fallback, so a ball bond still draws as a wire.
- Spheres are "unnamed-face" primitives today (`Scene3DBuilder.FaceUnknown`, `Scene3DFeatureTable` ~7,
  `Scene3DEdges.Empty`, `Em3dFaceBoundary.FaceNames` → `[]`). A ball has no faces a user can pick. A drawn sphere has
  ONE: `surface` (§1b). Make the change **per object, not per primitive**: a ball bond keeps `FaceUnknown`, and a
  `C3dSphere`'s triangles carry face 0.

Do **not** invent a second sphere record in the engine. `C3dSphere` lowers to the existing `Em3dSphere`.

---

## 1. `R-em3d102-1` — the design object and its file form

- **a.** `C3dSphere : C3dObject` in `C3dDocument.cs`, beside `C3dCylinder`. It has `Centre` (`C3dPoint3`, DBU) and `Radius`
  (`long`, DBU). Register it as `[JsonDerivedType(typeof(C3dSphere), "Sphere")]`. Any rotation comes from a placement,
  as for every primitive, and only moves the centre.
- **b.** `FaceNameList = ["surface"]`. One face, named, so Face mode can select it (§4) and a boundary or face image on it
  can be refused BY NAME with a reason, rather than not existing.
- **c.** `C3dBindings`: `Centre` (3, Length) and `Radius` (1, Length), so both take expressions and VARs.
  `C3dResolver.IsMagnitude` (~464) already matches `nameof(…Radius)` by string. Confirm with a test that a negative typed
  radius is refused, as a cylinder's is.
- **d.** `C3dValidation`: a radius ≤ 0 is `ZeroVolume(name, "sphere", "its radius is not positive")`.
- **e.** `C3dKernelUse.IsSolid` includes it (it is a solid operand). `C3dHierarchy`: scaling (~555–561) scales the centre
  and radius. Flatten (~438–452) recognises `Em3dSphere` → `C3dSphere`, so a flattened placed cell's sphere stays a
  sphere and does not become a polyhedron.
- **f.** `src/Cli/DocumentSchema.cs`: `Sphere` in the kinds table (`Centre`, `Radius`; faces: `surface`), and in the
  boolean operand list (~642).

## 2. `R-em3d102-2` — lowering, drawing, solving

- **a.** `C3dLowering`: `case C3dSphere s` → `Transform(new Em3dSphere(centre, M(s.Radius)), C3dSphere.FaceNameList, w,
  KindSphere)`, with `KindSphere = "sphere"` beside `KindCylinder`.
- **b.** The scene: a material-coloured solid, with no feature edges drawn on it (it has none) and no facet lines. Its
  triangles carry face 0 (`surface`) per §0. A gate checks the colour against a box of the same material, and checks that
  a wire's ball bond is unchanged.
- **c.** Solving needs nothing new; §0 lists the writers. Gate it once per solver family: a sphere reaches the Gmsh
  `.geo` as `Sphere(`, and reaches CSXCAD as `<Sphere`.
- **d.** Two optional one-liners worth taking, since they are already switches on `Em3dCylinder`: the skin-depth check in
  `Em3dRunService` (~1715, radius → `Radius`) and `Em3dStaticResult` (~101, the thickest round conductor). A sphere's
  "length" there is its diameter.
- **e.** `C3dThermal.Inside` (~452) falls back to the bounding box for any unknown primitive. Give the sphere its exact
  test (distance to centre ≤ r + tol), because a probe in the corner of a sphere's bounding box is NOT in the sphere.
- **f.** Sections (`Em3dSectionScene`, `render --iso` and the clip plane) take the tessellated default. A gate checks that
  a section through the centre draws a closed outline. An exact circle is not required.

## 3. `R-em3d102-3` — the kernel

- **a.** `GeometryKernelTree.CanHold` includes `C3dSphere`. The node is `kind: "sphere"`, `centre`, `radius`, with
  the face names.
- **b.** `geometry_worker.cpp`: `BuildSphere(r, names)` next to `BuildCylinder`. It requires exactly one name and a radius
  > 0, uses `BRepPrimAPI_MakeSphere(gp_Pnt(centre), radius)`, and names its one face `names[0]`. The dispatch goes beside
  `cylinder` (~1522). The existing seam/degenerate filter keeps the seam and pole edges off the edge list. **Verify** that
  a box minus a sphere has a face named `<tool>:surface` and no edge on the seam.
- **c.** A worker built before this brief refuses with its own sentence. The editor turns that into "this geometry worker
  predates spheres; rebuild it (`tools/geometry-worker/build.sh`)" rather than a generic failure. **This brief builds and
  verifies the worker on macOS only**; the Windows and Linux builds are owed, and the completion note says so.
- **d.** Fillet and Chamfer on a sphere have no edge to pick (Edge mode finds none). The panel's refusal must SAY that ("a
  sphere has no edges"), not show an empty list.

## 4. `R-em3d102-4` — what a curved face refuses

Everywhere a cylinder's `side` is refused, a sphere's `surface` is refused with the same kind of sentence. Find each place
by grep, not memory (`"side"` near `C3dCylinder`, and `Em3dCylinder` in `Em3dFaceBoundary`). The ones known today:

- `Em3dFaceBoundary.FacePolygons`: "it is a sphere's curved surface, and a boundary is placed on a flat face".
  `FaceNames(Em3dSphere)` now returns `["surface"]` when the solid came from a `C3dSphere`. If that cannot be told apart
  at that level, refuse by face name in the 3D layer instead. Either way, the refusal reaches the user.
- Face edits (`C3dFaceEdit`, `C3dEditorViewModel.FaceEdit.cs`, `MenuState.cs` ~77–105): Move Along Normal, Move, Extrude
  to New Solid and Align are each disabled with a tip. Vertex Move has no vertex. **Owner decision D3** covers Move Along
  Normal changing the radius.
- Map Image… onto a face (brief 101 Phase B) refuses a sphere's surface, as it refuses a cylinder's side.
- Drawing Plane from Face refuses a curved face.
- **Convert to Polyhedron** is not offered for a sphere (owner decision D4).

## 5. `R-em3d102-5` — the draw tool

- **a.** `C3dToolKind.Sphere`, **appended at the end** of the enum. `SphereTool : C3dDrawTool`, modelled on
  `CylinderTool`. Step 0 is the centre: a click on the drawing plane, snapped. Step 1 is the radius: a click, or typed
  into the field (Tab, a digit, `=`), with a magnitude in the `Radius` field. The second step commits
  `C3dSphere { Centre, Radius, Material = current, Name = Host.NextName("sphere") }`.
- **b.** The rubber band: the circle of the radius on the drawing plane (the cylinder's step-1 preview), plus the two
  great circles perpendicular to it. This shows a sphere and not a disc.
- **c.** Where the sphere sits relative to the plane is **owner decision D2**. *Recommended:* centred on the plane, as the
  2D circle is centred on its click.
- **d.** Snap: the sphere's **centre** is a snap point and a Vertex-mode readout, as a cylinder's cap centres are
  (`C3dFaceEditor.CapCentres`, `C3dEditorViewModel.FaceEdit.cs` ~93). It is never a movable vertex: "A sphere's centre
  snaps and measures but does not move." The surface offers no face-centre snap, since the centroid of a curved face lies
  inside the solid (`Scene3DFeatureTable`'s own rule).

## 6. `R-em3d102-6` — where it appears

- **a. Toolbar:** a `ToggleButton` bound to a new `IsSphereArmed`, **immediately right of Cylinder**. Since the 2026-10-03
  toolbar round the order is Box, Cylinder, **Sphere**, Sheet, Polygon, Polyline. Tooltip: `Sphere: centre, radius  (Shift+A,
  E)`. Glyph: **owner decision D5** (a drawn `Viewer3DPathGlyph`, as the cylinder's).
- **b. Shift+A popup** (`C3dEditorViewModel.DrawTools`): after Cylinder, letter per **D1**.
- **c. 3D ▸ Draw**, on BOTH hand-mirrored surfaces: the in-window `Menu` (`WorkspaceWindow.axaml` ~1174) and the macOS
  `NativeMenu` (~266). Header `Sph_ere` (the D1 letter underlined), after Cylinder. Tip: "Centre, radius. Requires an active
  3D editor." The menu-mirror test must still pass.
- **d. Inspector** (`C3dPropertiesViewModel` ~868–905, ~1080): `Centre x/y/z` and `Radius`, plus a read-only `Diameter`
  row. Model, material and transparency come for free.
- **e. Object tree:** a `Spheres` group in `C3dEditorViewModel.Groups` (~1079), after `Cylinders`. That also puts it in
  the tree's type filter.
- **f. Status line and kind word:** `Sphere "sphere1"` wherever `Cylinder "cylinder1"` appears.

## 7. Gate

1. **Round trip.** A `.c3d` with a sphere (literal), a sphere with `Radius: "r_ball"` bound to a VAR, a placed cell
   holding a sphere, and a box-minus-sphere Boolean loads and saves byte-identical.
2. **Draw.** Driving `SphereTool` through the host (no window): a click, then a typed `250um`, commits one sphere with
   the expected DBU centre and radius in one undo entry. Undo removes it and redo restores it. D2's placement is
   asserted.
3. **Validation.** Radius 0 and a negative typed radius each refuse, with the sentence. `check` on a file holding a
   zero-radius sphere reports it.
4. **Lowering and scene.** The lowered solid is an `Em3dSphere` with the transformed centre under a rotated placement and
   the same radius. Its scene kind is its material's kind and not `Wire`. A ball bond's scene kind is unchanged. Face
   mode on the sphere selects `surface`.
5. **Solvers.** The `.geo` holds `Sphere(` and the CSXCAD XML holds `<Sphere` for a one-sphere document (writer output
   only, **no solve**).
6. **Kernel** (`GeometryKernelWorkerTests`, macOS): box minus sphere builds and has a `<tool>:surface` face, the result's
   volume matches the analytic answer to 1e-6 relative, and there is no seam edge. An unknown-kind refusal from a stubbed
   old worker shows §3c's sentence.
7. **Refusals.** Boundary on `surface`, every face edit, Map Image, Drawing Plane from Face and Convert to Polyhedron are
   each refused or disabled with a reason (one test per surface, table-driven).
8. **Snap.** The centre is reported as a snap point, and a Vertex Move on it is refused with §5d's sentence.
9. **Thermal.** `C3dThermal.Inside` returns false for a point in the bounding box's corner and true at the centre.
10. **Placement.** The toolbar order is Box, Cylinder, Sphere, Sheet (source scan, extending
    `EditorToolbarKeysTests.Toolbar_CylinderFollowsBox_AndSheetIsARectangle`), DrawTools has Sphere third, and both 3D ▸
    Draw menus list it after Cylinder.

Run only the classes touched, with `--filter`; `Firewall.Tests` with `--no-build`. **No solver run is needed** for any
gate. If a solve is wanted to see the Mie comparison, keep it short (one frequency, coarse mesh) and do it last, outside
the gate.

## 8. Decisions for the owner

All are built as recommended unless the owner says otherwise.

- **D1 Shift+A letter.** S (Sheet), P (Port) and H (Heat Source) are taken. *Recommended:* **E** (`Sph_ere`).
  *Alternative:* **R**.
- **D2 Where the drawn sphere sits.** *Recommended:* **centred on the drawing plane**: the click is the centre, as the 2D
  circle and the cylinder's base are. *Alternative:* **resting on the plane**, so the click is the point it touches and
  the centre is lifted by the radius along the normal. That suits a ball on a pad but makes the click not the centre.
  Ctrl/Cmd-click on a face sets the plane either way.
- **D3 Move Along Normal on the surface.** *Recommended:* **refused** in this brief ("change the Radius in the
  Inspector"). *Alternative:* the push/pull changes the radius. That is natural, but it is a face-edit path of its own.
- **D4 Convert to Polyhedron.** *Recommended:* **not offered.** 32 × 16 facets give ~500 unnamed faces, which is not
  something to edit by hand, and Booleans already cover cutting a sphere.
- **D5 Glyph.** Material.Icons 3.0.2 has **no plain `Sphere` kind** (verified against the assembly), and the cylinder's
  nearest kind (`Database`) read so badly that the cylinder is now DRAWN: `Viewer3DPathGlyph.Cylinder` in
  `Viewer3DOverlay.cs`, a 24-unit even-odd path at the icon set's 2-unit stroke (2026-10-03). *Recommended:* the same for the
  sphere: a `Viewer3DPathGlyph.Sphere` constant (an outline circle plus the front half of its equator as an ellipse), drawn as
  Skia strokes, written back as exact `A` arcs, and rendered at 16/32/64 px beside Box and the cylinder for the owner before
  it is wired. Its name goes in `DrawTools` as the cylinder's does. Do not use `CircleOutline`: it is the 2D circle and would
  read as a disc sheet.
- **D6 Kernel worker builds.** *Recommended:* built and gated on macOS here. Windows (llvm-mingw) and Linux builds are an
  owner step before release, as in brief 62.

## Docs

`drawing-in-3d.md`: the Sphere row in the tools table (~216, with D1's letter), a sentence in "What it holds" (~57), the
Model check box list (~341), the curved-face sentence (~466: "a cylinder's side or a sphere's surface"), and the keywords
line (`sphere`). Edit doc sources only; DocGen runs at the end of the series.

## On completion

Record the findings in `src/Design/RESOLVED.md` (the per-object `surface` face, versus the ball bond's unnamed face) and
`src/Render/RESOLVED.md` (the scene-kind check), and in `tools/geometry-worker/RESOLVED.md` (`BuildSphere`, and which
platforms were built). Never CLAUDE.md. Do not commit unless the owner asks.
