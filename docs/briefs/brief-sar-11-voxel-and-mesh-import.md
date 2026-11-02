# Brief SAR-11 — Segmented voxel models and per-tissue surface meshes

**Series:** `brief-sar-0-overview.md` (L2) · **Tag:** `R-sar11-<m>`
**Depends on:** SAR-3, SAR-4
**Area:** `src/Design/ThreeD/` (a voxel object), `src/Design/Em3d/CsxcadWriter.cs` (`DiscMaterial`), new readers
(`src/Design/ThreeD/Import/`), the `.c3d` editor's import commands and 3D view (`src/Ui`), `convert`/`import` verbs,
`docs/design/sar.md` §13

---

## 1. Goal

Researchers have their own anatomy. The commonest form is a **segmented label map**: one integer per voxel naming a
tissue, made from their own MRI scans and usually stored as NIfTI. Some have per-tissue surface meshes instead. Bring
both into a `.c3d` and run them on openEMS. circuitRF ships no anatomical model (L2). It ships the importers.

## 2. Requirements

**R-sar11-1 — `C3dVoxelBody`.** An object holding a reference to a label volume and a **label table** (label →
material name, from the workspace's materials, SAR-2), plus a placement (the existing transform) and an optional crop
box. It is drawn in the 3D view as tissue-coloured surfaces at a decimated resolution: a surface extraction of each
label for display only, never simulated. The label volume stays in its own file; the `.c3d` references it by relative
path, like a STEP import.

**R-sar11-2 — Readers.**
- **NIfTI-1 and NIfTI-2** (`.nii`, `.nii.gz`): header, integer data types, voxel size, and the qform/sform affine.
  A non-axis-aligned affine is **refused** in this phase, with the angle, because the FDTD grid is axis-aligned.
- **Raw:** a binary volume with dimensions, type, voxel size and byte order given in an import dialog (or CLI flags),
  plus a label list as CSV.
- **openEMS `DiscMaterial` HDF5** (SAR-4's reader), so a file prepared for openEMS opens directly.

A label the table does not map is a refusal listing the unmapped labels and their voxel counts. Label 0 is air unless
the table says otherwise.

**R-sar11-3 — Lowering to openEMS.** The writer emits a `DiscMaterial` HDF5 file (SAR-4's writer) for the cropped
volume with the label table's εr, σ and ρ, evaluated at the SAR frequency (SAR-2). It writes a `DiscMaterial` property
with the placement as a CSXCAD transform. The FDTD grid inside the body uses the voxel size by default, with lines on
the voxel boundaries, or coarser on request with a stated resampling rule. Priority interacts with other solids per
em-3d.md §6.3a.

**R-sar11-4 — Per-tissue surface meshes.** Import a set of closed surfaces (STL, OBJ, PLY), one per tissue, each mapped
to a material, nested by containment. Each becomes a mesh solid lowered through the existing `PolyhedronReader` PLY
path. A non-closed or self-intersecting surface is reported per file. Tetrahedral `.node`/`.ele` volume meshes are
**voxelised** to a label volume at a chosen size and then treated as R-sar11-1.

**R-sar11-5 — Size, said before the run.** Import reports voxel count, crop, label histogram and the estimated FDTD
cell count and memory (`Em3dMemory`). A whole-body model at 1 mm is accepted but warned.

**R-sar11-6 — Nothing committed (L2).** Tests use **synthetic** label volumes written by the test: nested spheres, and
a 3-label slab. A developer may point a git-ignored `testdata/sar/local-models/` at a free model they obtained
themselves. Tests skip with a reason when it is absent, and no file, name or licence text of such a model enters the
repository.

## 3. Not in this phase

Posing or morphing; skewed affines; anatomical models of our own.

## 4. Gates (minimal)

- `NiftiReaderTests`: a synthetic `.nii.gz` written by the test round-trips dimensions, affine and labels.
- `VoxelBodyCsxTests`: a 3-label slab writes a `DiscMaterial` file that SAR-4's reader reads back exactly; an unmapped
  label is refused.
- `VoxelVsPrimitivePhantomTests`: the `Ellipsoid3` phantom voxelised to 2.5 mm and imported as a label volume gives
  the same raw SAR dump, cell by cell, as the primitive phantom on the same grid. One opt-in `Benchmark` run; routine
  on fixed data.
