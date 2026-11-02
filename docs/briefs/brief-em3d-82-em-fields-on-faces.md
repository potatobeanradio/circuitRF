# Brief 82 — E and H on a picked face or sheet

**Tag:** `R-em3d82-n` · **Design note:** [`em-3d.md`](../design/em-3d.md) §8.4
**Area:** `src/Ui/Viewer3D/Viewer3DViewModel.Fields.cs`, `src/Ui/ThreeD/C3dEditorViewModel.*` (the context menu),
`src/Render/Scene3D/Fields/` (`FieldGeometry.cs`, `FieldThermal.cs`, `FieldSampler.cs`), `src/Design/Em3d/CsxcadWriter.cs`
(R-4 only), `tests/`
**Depends on:** 29 (EM fields in the 3D view), 75 (Plot Temperature on a face) · **Blocks:** —

---

## 0. What exists, and the gap

Brief 29 already draws a Palace or openEMS run's fields in the 3D view — E, B/H, S, U_e/U_m, J_s, Q_s, whatever the
step lists — with phase animation, dB, a percentile colour range and a cursor readout. It draws them in exactly
three places (`Viewer3DViewModel.ScheduleFieldGeometry`):

- the **clip plane** (`FieldSlicer.Slice`);
- the whole boundary of the **one selected dielectric, air or body** (`FieldSurfaces.RegionBoundary`), for a volume
  quantity;
- **every conductor and sheet at once** (`FieldSurfaces.Boundary` over the boundary step), for a boundary quantity.

Brief 75 gave temperature what EM lacks: **right-click a face ▸ Plot Temperature**, which toggles, accumulates
across faces, and paints only that face (`RegionBoundary` of the face's own region, cut down by
`FieldFaces.OnFace(whole, Scene3DFaces.Triangles(scene, id, face), tol)`, nudged along the face normal). None of it
is temperature-specific: `OnFace` works on any `FieldSurface` of any channel count.

**The gap:** you cannot put |E| on one face of a substrate, on the top of one trace, or on a sheet. This brief adds
that, for both solvers.

## 1. `R-em3d82-1` — right-click a face ▸ Plot Field (Palace)

- A context-menu item beside Plot Temperature, shown when the active setup's run left EM fields
  (`FieldsAvailable && !IsThermalRun`), refused with a reason otherwise (gate 2's pattern: `PlotFieldRefusal()`).
  Toggles; several faces accumulate; the list clears when the run or the solution changes shape (a new mesh).
- **The painted faces stand beside the existing targets, not instead of them.** Clip plane and the selected region
  work as today; a picked face is a fourth source, drawn only for the selected quantity.
- **Dielectric, air or body face, volume quantity:** exactly temperature's path — `RegionBoundary` over the face's
  own region, then `OnFace`, nudged by `eps·n`.
- **Conductor face.** A Palace conductor is a VOID (`GmshGeoWriter` recipe step 1), so it has no region of its own:
  - a **boundary** quantity (J_s, Q_s) comes from the boundary step filtered to that conductor's attribute, then
    `OnFace`;
  - a **volume** quantity (E, B) is the neighbouring region's value at the face — the union of the
    `RegionBoundary`s of the regions that touch it, then `OnFace`. There is only one side, so no side choice.
  - Default quantity when a conductor face is painted and the current one cannot be shown there: say so in
    `FieldText`, never switch quantity silently.
- The colour range spans everything drawn, as today; phase steps revalue through the surfaces' recipes and build no
  geometry (`FieldGeometryBuilds` unchanged — the counter brief 29's gate 5 already holds).

## 2. `R-em3d82-2` — a sheet, and which side

A sheet is imprinted *inside* a volume, so both tets sharing a sheet triangle are in the same region, and
`RegionBoundary` (which keeps faces owned by exactly one tet of the region) **never returns it**. A new
`FieldSurfaces.OnSheet(tets, array, sheetTriangles, side, tol)` gathers the tet faces whose three corners lie on the
sheet's scene triangles, taking each face's values **from the tet on the chosen side**.

**The side matters, and it must be a visible choice, not an arbitrary pick.** The normal component of E jumps across
a sheet that carries surface charge — on a PEC sheet the tangential E is ~0 and the normal E *flips sign* — so the
two sides of one sheet are two different pictures. The menu offers **Plot Field ▸ Top side / Bottom side** (+n / −n
of the sheet's own normal); a boundary quantity (J_s on a PEC sheet) is single-valued and shows no choice.

## 3. `R-em3d82-3` — openEMS

openEMS writes a rectilinear dump that `VtrReader` turns into six tets per cell; it carries **no region attributes**,
so neither `RegionBoundary` nor the conductor/sheet grouping applies. Paint a face by **sampling**: subdivide the
face's scene triangles to about the grid's local spacing, and sample each vertex with `FieldSampler` at a point
nudged `eps` to the chosen side (a face of a solid: outward; a sheet: the side of R-2). The dump is at cell centres
(DumpMode 2, chosen because a node on a metal face averages E across the metal — `CsxcadWriter`'s own comment), so
the nudge must move past half a cell, not one ULP; state that in the code where it is chosen.

## 4. `R-em3d82-4` — H from openEMS

openEMS's volume dump today is **E only** (DumpType 10). Brief 31 already writes H (DumpType 11) but only on the
far-field box's six faces. Add one H dump box per saved frequency beside the E box, same mode and file type, so
`FieldQuantity.Offered` lists H for an openEMS run. **It doubles the field files on disk** — see Q1.

## 5. Non-goals

- A CLI spelling (`render --field …`). Worth a brief of its own once this exists.
- Vector arrows or streamlines on the face: magnitude, a component and phase animation, as brief 29 draws.
- Rotating the picture into a face-local frame (tangential/normal components). Listed for later: it is what a
  "surface E_t" plot would need, and it needs the face normal per vertex, which `OnFace` does not carry today.

## 6. Gates

1. **Face = region restricted.** On the committed PEC-cavity fixture (`testdata/em3d/fields/`), a painted wall
   face's triangles are exactly the `RegionBoundary` triangles within `tol` of that wall, with identical values.
2. **Physics on the cavity**, as brief 29 measured it: on a wall E is tangential to, painted |E| is below brief
   29's display tolerance; on a y-wall (E normal) it is not.
3. **Sheet sides.** A synthetic tet pair straddling one sheet triangle with opposite normal E: Top and Bottom give
   opposite E_n, the same |E_t|.
4. **Conductor face, volume quantity** reads the neighbouring region's values (synthetic fixture, no solver).
5. **openEMS sampling** on a synthetic `VtrField` with a linear E: sampled values match the analytic field to float
   precision away from the metal.
6. **No geometry on a phase step** (`FieldGeometryBuilds` counter), with painted faces present.
7. **R-4 writer bytes:** `CsxcadWriter` emits one E and one H box per saved frequency; a setup that saves no fields
   emits neither (`SaveFieldsGHz: []`).

No new timing test; each gate runs in well under a second on committed fixtures.

## 7. Owner questions

- **Q1.** openEMS H dump (R-4): on by default (twice the disk per saved frequency), or a per-setup opt-in
  `OpenEms.SaveH`?
- **Q2.** Should a painted conductor face default to |J_s| (the quantity people usually want on metal) when the
  current quantity is a volume one, or keep the current quantity and read it from the neighbouring region (R-1's
  proposal)?

## On completion

Findings go in `src/Render/RESOLVED.md` (the field model) and `src/Ui/Viewer3D/RESOLVED.md` if one exists, else
create it. Never in any `CLAUDE.md`.
