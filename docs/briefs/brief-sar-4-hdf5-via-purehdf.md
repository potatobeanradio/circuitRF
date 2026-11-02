# Brief SAR-4 — openEMS's HDF5 through the PureHDF circuitRF already ships

**Series:** `brief-sar-0-overview.md` (O1, settled: reuse) · **Tag:** `R-sar4-<m>`
**Depends on:** —
**Area:** `src/RfCore/Hdf5/` (new: a thin adapter over PureHDF), `src/RfCore/Export/MatWriter.cs` (the existing
writer, untouched), `src/Design/Em3d/OpenEmsFarField.cs` (its header comment), `docs/design/sar.md` §6

---

## 1. Goal

These openEMS files are all HDF5:
- the raw SAR dump (`DumpType 29`);
- `sar_calc`'s output;
- field dumps written as HDF5;
- `DiscMaterial` voxel files.

circuitRF already carries a managed, MIT, no-native HDF5 library: **PureHDF 2.1.2**, referenced by `src/RfCore` for
the `.mat` v7.3 export (`MatWriter.cs`, data-export.md decision 8.8) and by the RfCore and Engine test projects.
**Reuse it.** Add no second HDF5 implementation and no new dependency. This phase is an adapter that knows openEMS's
layouts, plus proof that PureHDF handles them.

## 2. Requirements

**R-sar4-1 — Pin what openEMS writes.** List every HDF5 feature in the files SAR-1 produced with openEMS 0.37.0
(`h5dump -H -p`, or Python, on the developer machine): superblock version, layout (contiguous, chunked, compact),
filters (deflate, shuffle), datatypes, attribute storage and string forms, group structure. Record the table in the
design note. Confirm that PureHDF 2.1.2 reads every row; anything it cannot read is a finding for the owner, never a
reason to write a reader of our own.

**R-sar4-2 — `OpenEmsHdf5` reader.** `ReadSarRaw(path)` → SAR-5's `SarGrid` (mesh lines, complex E, σ, ρ, volume per
cell, per frequency); `ReadField(path)` for E/H frequency-domain dumps; `ReadSarResult(path)` for `sar_calc` output.
One dataset at a time, through PureHDF's selection API, so a 1 GB dump is never read whole into memory. A file whose
groups or attributes differ from the pinned layout is a **refusal** naming the object path and what was expected.
openEMS's array order versus C order is handled in one place, and a test pins it.

**R-sar4-3 — `DiscMaterialWriter`.** Writes the `DiscMaterial` version-2 layout CSXCAD reads, with PureHDF's writer
(the API `MatWriter` already uses):
- a root `Version` attribute;
- `/DiscData` (uint8 or uint16 tissue indices, in the order CSXCAD indexes) with attributes `DB_Size`, `epsR`,
  `kappa`, `density` and `Name`;
- `/mesh/x|y|z` (float32, metres).

Plain layout unless size forces deflate. **Whether CSXCAD accepts the file is the gate**, not whether a generic HDF5
tool opens it. If PureHDF cannot produce a form CSXCAD accepts (an attribute type, for example), record exactly which
one and stop for the owner.

**R-sar4-4 — The stale comment.** `OpenEmsFarField.cs`'s header says openEMS's HDF5 output would need a reader circuitRF
does not carry. Correct it: the far-field path keeps VTK for its own reasons, which the comment states, and HDF5 is read
through this adapter.

**R-sar4-5 — Firewall.** The adapter is in RfCore beside `MatWriter`, with no UI reference (`tests/Firewall.Tests`).
`src/Design` consumes it; `src/Engine`'s SAR code receives arrays, never a file.

## 3. Not in this phase

Any HDF5 code of our own; replacing VTK on existing field paths.

## 4. Gates (minimal)

- `OpenEmsHdf5Tests`: SAR-1's raw dipole dump and one field dump read to the same values the Python reference wrote
  (exact for float32), and the array order is pinned.
- `DiscMaterialWriterTests`: a 3 × 2 × 2 file written by circuitRF, loaded by the installed openEMS in a one-cell
  run, gives the materials it declares. That test launches openEMS and is skipped with a reason when it is absent.
- One refusal test: a file with an unexpected group layout names the path.
