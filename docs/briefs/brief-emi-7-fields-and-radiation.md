# Brief EMI-7 — Field plots and radiation patterns from an EMerge run

**Tag:** `R-emi7-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-5
**Area:** the EMerge driver (EMI-4's constant program), `src/Render/Scene3D/Fields/FieldRun.cs` and `VtuReader.cs`,
`FieldPlotResolver`, the far-field/radiation-pattern path (`Em3dRadiation.cs`, `NearToFarField.cs`), `render --field`,
the 3D editor's field plots. Findings in `src/Render/RESOLVED.md` and `src/Design/RESOLVED.md`.

---

## 0. What EMerge has

EMerge solves the full 3D electric field on the tetrahedral mesh (Nédélec elements, order 1 or 2) and derives H from it.
After a solve it can evaluate E and H anywhere in the mesh, per port excitation and per frequency, and compute a far
field from a closed surface. So every field plot circuitRF draws for a Palace run can be drawn for an EMerge run. The
question is only the file format between them.

## 1. `R-emi7-1` — write what circuitRF already reads

**The driver writes EMerge's solved fields in the `.vtu` layout `VtuReader` already reads for the pinned Palace**: an
UnstructuredGrid of Lagrange tetrahedra (VTK type 71; 10 nodes per cell at order 2, 4 at order 1), every cell with its
own nodes (a Nédélec field is discontinuous across faces, so nodes are not shared — the same choice Palace makes),
inline-base64 geometry and appended-raw arrays, no compression, with complex quantities as `<name>_real`/`<name>_imag`
pairs (`FieldRun` already pairs those generically). Write `E` and `B` (or `H`, whichever Palace's files carry, so the
plots need no per-solver quantity table), the material attribute per cell, and a `.pvd`/`.pvtu` wrapper with one piece.
Each cell's nodes are evaluated **inside that cell** (by the cell's own basis, through EMerge's field evaluation with
the point-to-tetrahedron mapping supplied), never by a global nearest-cell lookup that would smear the discontinuity.

Why not a new reader: one reader, already gated against real files, and every field-plot feature (clip planes,
surface plots, the 3D viewer, `render --field`) works for EMerge on the first day. **Gate it the same way**: read a
driver-written file back through `VtuReader` and check one known value (e.g. the Cavity mode's |E| maximum location).

**R-emi7-1b — what is written, and when.** Per the setup's field plots (the `.c3d`'s `FieldPlots` say which setup,
excitation and frequency or mode), not every frequency of a sweep: a field file per requested (excitation, frequency)
and per eigenmode. Size: write the order the run used; warn above the same field-file size Palace's runs warn at
(`FieldFilesBytes`).

## 2. `R-emi7-2` — scaling

A field plot is per watt at the drive port (Palace's convention as circuitRF plots it, brief-em3d-100). EMerge's port
power is a parameter (default 1 W); state in the driver which convention it writes and check one case against Palace
on the Cavity (the mode plot's peak |E| within the two solvers' mesh agreement).

## 3. `R-emi7-3` — eigenmode extras

From the eigenmode fields, compute what Palace's table reports and EMerge does not: **energy participation** per
material (∫ ε|E|² over each material's cells / total) and, when cheap, **Q unloaded** (port loss removed). Only if each
can be computed from what the driver already has; otherwise leave them absent (EMI-5 R-emi5-4b) and say so in the docs.

## 4. `R-emi7-4` — radiation patterns

The setup's `RadiationPattern` request: the driver evaluates EMerge's far field on the absorbing boundary (or a closed
surface inside it) at the requested frequencies and writes `theta`, `phi`, `Etheta`, `Ephi` arrays; circuitRF builds the
same `DataSet` the Palace/openEMS radiation path produces. Note: 3.0 changed EMerge's far-field return type and axis
order — test against the pinned version and nothing older.

## 5. Tests (targeted)

1. A driver-shaped `.vtu` (written by a test fixture in the same layout, no Python) reads through `VtuReader`
   unchanged — proving the layout, not EMerge.
2. With EMerge installed only (Skipped otherwise): the Cavity *Modes* run writes mode 1's field; `render --field` on it
   draws; the maximum |E| sits in the cavity's middle, as Palace's does.
3. Participation from canned per-cell energies sums to 1.
4. The radiation `DataSet` shape matches the Palace path's for one canned input.
