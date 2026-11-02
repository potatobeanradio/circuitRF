# Brief EMI-11 — Surface current J_s from an EMerge run

**Tag:** `R-emi11-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-7
**Area:** the EMerge driver (`src/Design/Em3d/Solvers/EMerge/circuitrf_emerge.py`), `EmergeScriptWriter.cs` (the
problem it hands the driver), `EmergeFields.WriteCollections`. The viewer, `FieldRun`, `VtuReader`, `FieldSurfacePlot`,
`FieldFacePaint` and `render --field` should need **no change** — if one does, stop and say why. Findings in
`src/Design/RESOLVED.md`.

---

## 0. The gap

A Palace run's field plots offer **J_s — Surface current (surfaces)** on conductors and sheets. An EMerge run's do not:
the 3D editor's Inspector lists only the volume quantities, E and B.

The cause is the file layout, not the viewer. A Palace run writes TWO collections per problem — the volume
(`postpro/paraview/driven/…`) and the boundary (`postpro/paraview/driven_boundary/…`), the boundary's triangles holding
`J_s_real`/`J_s_imag` (and `Q_s`, `E`, `B`, `S`, `U_e`, `U_m`; see the committed fixture
`testdata/em3d/fields/cavity/postpro/paraview/eigenmode_boundary/`). EMI-7's driver writes only the volume, with four
arrays (`write_field`: `E_real/E_imag/B_real/B_imag`). `FieldRun.OpenParaview` already looks for `<problem>_boundary` and
pairs its steps with the volume's by timestep; it finds nothing, so `FieldSolution.BoundaryPvtu` is null and no boundary
quantity is listed. And the boundary attributes have nothing to map to: the driver's `groups.json` (`write_groups`)
holds only Dimension 3 groups, while `FieldSurfacePlot.Em` draws a boundary quantity on groups of
`Kind` `"Conductor"` or `"Sheet"` and `FieldFacePaint.Boundary` looks a face up by name, Dimension 2, and kind.

So the work is: **the driver writes a boundary collection in Palace's layout, with J_s, on the conductor and sheet faces,
and names those faces in `groups.json` as Palace's mesh does.** Every surface plot then reads it with the reader it
already has, as EMI-7 R-emi7-1 did for the volume.

## 1. `R-emi11-1` — what J_s is, computed from what the driver already has

J_s = n̂ × [H], the jump in tangential H across the face, with n̂ the face's normal:

- **A conductor's face** (`walls` in `build`: a face shared between a removed conductor and meshed space, carrying
  PEC or `SurfaceImpedance`). H is zero inside a void conductor, so J_s = n̂ × H, with H the one-sided limit in the
  adjacent tetrahedron and n̂ pointing **out of the conductor, into the meshed side** (toward that tetrahedron).
- **A sheet** (PEC or thin, `assign_thickness`): an interior face with a tetrahedron on each side. J_s = n̂ × (H₊ − H₋),
  n̂ the sheet's own +n (§3), H₊ evaluated in the tetrahedron on the +n side and H₋ in the one on the −n side.

Before writing the code, **confirm on the committed Palace fixture that Palace's own `J_s` on a sheet is the jump**
(not one side's n̂ × H). If it is one-sided, match Palace, record the finding, and say which side.

Everything needed is already in the driver:
- `mesh.tris` (3, n_tris), `mesh.tri_to_tet` (2, n_tris; the second row is `_MISSING_ID` on a one-sided face) and
  `mesh.ftag_to_tri` / `mesh.get_triangles(tags)` (face tag → triangle indices). These are the pinned 3.0.0b2's
  names; check them against the pinned version, not an older one.
- `evaluate(field, points, cells)` already evaluates E and H at given points **inside the named cell** — exactly the
  one-sided limit wanted. Give each triangle node the adjacent tetrahedron's index (twice for a sheet: once per side).
- The scale is the volume's: the same `scale` factor `write_field` applies (R-emi7-2), so J_s on a driven step is at
  `incidentPowerW` and on a mode at Palace's energy normalisation. Do not introduce a second scale.

Each triangle's nodes are its own (the 3 corners at order 1; corners plus edge midpoints, 6 nodes, at order 2 — VTK
type 69, which `VtuReader` reads) and are not shared, for the same reason `cell_nodes` does not share a tetrahedron's.

## 2. `R-emi11-2` — the file layout

Per step written today, one boundary step beside it:

- `postpro/paraview/driven_boundary/excitation_<k>/Cycle<n>/data.pvtu` + `proc000000.vtu` (driven), and
  `postpro/paraview/eigenmode_boundary/Cycle<n>/…` (eigenmode). The same `Cycle` number as the volume step it belongs to.
- An UnstructuredGrid of Lagrange triangles, `J_s_real` and `J_s_imag` as 3-component Float32 point data, and the
  per-cell `attribute` (§3). `write_step` hard-codes the tetrahedron type; generalise it over the cell type rather than
  copying it.
- `run.json`'s step gains `"boundaryPvtu"`, and `EmergeFields.ReadSteps`/`EmergeFieldStep` read it.
  `EmergeFields.WriteCollections` writes the boundary `.pvd`s **with the same timesteps as the volume's** — that is how
  `FieldRun` pairs a step with its boundary (`driven_boundary/excitation_<k>/excitation_<k>.pvd`,
  `eigenmode_boundary/eigenmode_boundary.pvd`; the mode-numbering filter that applies to the volume applies here too).
- The boundary's bytes count toward `fieldBytes`, and so toward the same field-size warning (R-emi7-1b).
- Written whenever the volume step is written (`fields.write`), and never otherwise. No new setup key.

## 3. `R-emi11-3` — the face groups

`groups.json` gains one Dimension 2 group per conductor (`Kind: "Conductor"`, `Name` the conductor's own object name)
and per sheet (`Kind: "Sheet"`, its name), each with an attribute of its own after the volumes'. A boundary triangle's
`attribute` is its group's. This is what lets the existing surface plot draw "the conductors" and lets right-click a
face ▸ Plot Field find a conductor face by name. Match the fields that `GmshGeoWriter`'s groups for Palace carry
wherever `FieldGroups` reads them.

Two things `build` does not keep today and must:
- **The conductor each wall belongs to.** `walls` is keyed by material (that is what its boundary condition needs);
  also record the conductor object index per face tag (`owner[in_metal[0]]` already gives it).
- **Each sheet's normal and face tags.** Add the sheet's normal to `problem.json` from `EmergeScriptWriter` (its local
  +z through `sh.World`, the same +n the editor's Plot Field ▸ Top side names), and keep each sheet's face tags after
  meshing (the `GeoSurface`'s tags). A face whose triangle normal points against the sheet's normal is flipped, so +n
  is the same on every triangle of a sheet.

Ports and lumped elements are sheets too, but Palace's surface plot does not draw them (they are not
`Conductor`/`Sheet` groups). Leave them out. The air box's own PEC faces are also not drawn on a Palace run; leave them
out too. That is parity with Palace, not a new gap — a ground plane meant to carry plotted current is drawn as a
conductor or a sheet.

## 4. `R-emi11-4` — Q_s, if it costs two lines

Palace's boundary also carries Q_s = n̂ · [D]. On the same triangles, from E, which `evaluate` already returns, with ε
from `field._der`, it is a few lines more. **Add it only if it is that cheap**; otherwise leave it and say so.
Nothing else from Palace's boundary list is in scope.

## 5. Tests (targeted, per `feedback-minimal-tests`)

1. **No Python.** A test fixture writes a driver-shaped boundary step in the §2 layout (two triangles, a known J_s) with
   a `groups.json` naming one conductor. `FieldRun.OpenEmerge` pairs it with its volume step, and the quantity list of
   that solution includes `J_s` with `OnBoundary: true`. This proves the layout and the groups, not EMerge.
2. **The orientation, no Python.** The driver's J_s arithmetic is a pure function of (H, normal, side). Unit-test it
   in the C# mirror if one exists, or with a canned H in a small Python test the existing driver tests run. A uniform
   H = x̂ above a z = 0 conductor gives J_s = ẑ × x̂ = ŷ. A sheet with H₊ = x̂, H₋ = −x̂ gives 2ŷ.
3. **With EMerge installed (Skipped otherwise), on an existing example with a conductor or a sheet:** the run writes a
   boundary step, `render --field` with a J_s surface plot draws non-empty geometry, and the plot's peak |J_s| sits on
   the conductor's edge, where current crowds.
4. **With Palace and EMerge both installed (Skipped otherwise):** one driven problem, both solvers, the same port and
   frequency. The two peak |J_s| agree within the two solvers' S-parameter mesh agreement on that problem. Report the
   number. Do not tighten a tolerance to pass.

Gate: those test classes alone, plus `tests/Firewall.Tests`. No full suite.

## 6. Docs

The EMerge page's field-plot section (EMI-8) says J_s is available on conductors and sheets, as on Palace. Edit the
doc source only; DocGen is the owner's, at the end of the series.

## Non-goals

- J_s on the air box's own walls, ports or lumped elements (§3: parity with Palace).
- Any other Palace boundary array (E, B, S, U_e, U_m on the boundary).
- Any viewer change. If the viewer needs one, that is a finding to report, not a change to make here.
