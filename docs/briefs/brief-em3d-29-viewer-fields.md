# Brief 29 — the viewer: fields

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d29-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8.5 ("then fields read from either solver's output")
**Area:** `src/Design/Em3d/PalaceConfigWriter.cs` (field output), `src/Design/Layout/Em/EmSetupPersistence.cs`
(`CemPalace.SaveFieldsGHz`), `src/Render/Scene3D/Fields/` (new: VTU reader, field model, slicing,
colour mapping), `src/Ui/Viewer3D/` (the field pass and the animation), `src/Design/Em3d/CsxcadWriter.cs`
and `OpenEmsRun.cs` (§6 only)
**Depends on:** 28 (22 and 23 for their own fields) · **Blocks:** 30
**Owner decision D7:** the default field save is the sweep's centre frequency only.

---

## 0. What this brief delivers

- |E| on any clip plane and on the boundary of any solid;
- the surface current on the conductors;
- at any saved frequency;
- **animated through a cycle of phase**, so a standing wave or a resonance *moves*.

It also shows, from brief 23, the eigenmode shapes and, from brief 22, the electrostatic potential.
Every value drawn comes from the solver's own field files (overview rule). A **PNG export** of the view
is how the owner puts a picture in front of someone.

---

## 1. `R-em3d29-1` — asking Palace for fields (overview §1d)

**`R-em3d29-1a`** Series 1's config asks for no fields. Add:
- `Problem.OutputFormats.Paraview: true`, stated explicitly even though it is the schema default,
  because a default is not a contract;
- for **driven** runs, the save frequencies in the driven samples' `Save` array, per the pinned schema
  (`testdata/em3d/palace-schema/0.18.1.json`);
- for **eigenmode**, `Solver.Eigenmode.Save = N` (every computed mode);
- for **static** runs, whatever the pinned schema's electrostatic and magnetostatic sections take to
  save the potential or field.

**`R-em3d29-1b` `CemPalace.SaveFieldsGHz`**: null means the sweep's **centre frequency** (D7), `[]`
saves nothing, and a list saves those. A requested frequency that is not a sweep point: find out from
a real run whether the pinned Palace solves it exactly (an extra solve) or refuses it, and state which
in the reference page. Do not assume.

**`R-em3d29-1c` Re-golden once.** This changes every Palace config golden. Rewrite them in one commit
under `CRF_WRITE_PALACE_GOLDENS=1`, with the diff shown to contain **only** the output lines.

**`R-em3d29-1d` Size is reported.** After a run, the summary line (brief 21) adds the field files'
total size. Measure it on F0 case A at order 2, record it in `src/Design/RESOLVED.md`, and put it in
the `SaveFieldsGHz` reference description, so a user asking for twenty frequencies knows the cost
first.

---

## 2. `R-em3d29-2` — reading what Palace writes

**`R-em3d29-2a` A managed reader for the ParaView files the pinned Palace writes, and only those.**
Find the format by reading a real run's output, then commit a **small** run's field files as a fixture
under `testdata/em3d/fields/` (a coarse single-rank cavity; keep it under 1 MB). Expect, and confirm
or refute each:
- a `.pvd` collection → per-step `.pvtu` → **one `.vtu` piece per MPI rank**. Merge the pieces, and
  de-duplicate shared nodes by global ID if Palace writes one; otherwise by exact coordinate;
- **high-order Lagrange cells** (element order 2). Either sample them at their nodes, or split them
  into linear sub-tets for display, and say which;
- **appended binary data, possibly zlib-compressed.** Use `System.IO.Compression.ZLibStream`, which
  ships with .NET. No VTK library.

Complex fields arrive as separate real and imaginary arrays (names per the fixture). The reader is in
`src/Render/Scene3D/Fields/` and has no GPU code.

**`R-em3d29-2b` The field model is generic** (overview §4, so that F3 plugs in): a mesh plus named
arrays, each **scalar or 3-vector, real or complex, per node or per cell**. Nothing in it says
"electric". The UI lists whatever arrays the file holds, with friendly names for the known ones.

**`R-em3d29-2c` Stream and bound memory.** A field on a 1 M-unknown mesh must not load as boxed
doubles. Use flat `float` arrays on the GPU path, and keep `double` only where a value is printed.
Gate 6 bounds the resident size.

---

## 3. `R-em3d29-3` — drawing fields

**`R-em3d29-3a` On surfaces and clip planes only, never the volume** (§8.5):
- the boundary faces of any solid the user picks;
- the conductor surfaces (for surface current);
- the clip plane, as an exact slice of the tetrahedra, interpolating linearly within each sub-tet.

**`R-em3d29-3b` Quantities:** |E|, Re{E·e^{jφ}} as a vector magnitude, surface current |J_s| on
conductors (from whatever Palace writes on boundaries; if it writes none, from n × H if H is written;
if neither, the option is absent, not approximated), and, for static runs, potential. A quantity
needing an array the files do not hold is **not offered**, never derived from something else.

**`R-em3d29-3c` Colour:**
- linear or dB scale;
- automatic range with a clamp at a chosen percentile, so one singular edge does not wash out the
  picture;
- the percentile and range shown in the legend;
- colour maps from `Scene3D` (perceptually uniform by default, and legible in both themes).

A value outside the range is drawn in the end colour, never transparent.

**`R-em3d29-3d` The phase animation.**
- **Upload Re and Im once**; the animation is a phase uniform on the GPU. Gate 5 counts bytes uploaded
  per animated frame: 0.
- Play, pause and a phase slider.
- The loop period is a display choice (default 2 s) and has nothing to do with the frequency, which
  the legend states.

**`R-em3d29-3e` Pickers:** saved frequency (driven), mode with its f and Q (eigenmode), terminal
(static). The hover tooltip gains the field value under the cursor, **read from the field data**, not
from the colour.

---

## 4. `R-em3d29-4` — an independent check that the picture is right

The overview's rule needs one check that does not go through circuitRF's own reader. Palace's
`Domains.Postprocessing.Probe` writes the field at a point to a CSV **by itself**. For the gate case
the config adds three probes. The value circuitRF's reader and slicer give at those points must equal
Palace's own probe values (gate 3). This is the only reference that tests the reader, the merge of the
rank pieces and the interpolation together.

---

## 5. `R-em3d29-5` — export

*Export picture…* writes a PNG of the view at a chosen size up to 4× the window, with an optional
legend and frequency caption. It reads back from the GPU, and the file is the pixels shown. No video
encoder: an animation is out of scope, because every route to one is a native dependency.

---

## 6. `R-em3d29-6` — openEMS fields (the second half, can ship later)

**`R-em3d29-6a`** openEMS dumps frequency-domain fields from a **dump box** in the CSXCAD XML at stated
frequencies. Add one per saved frequency, over the problem's extent. Find the file format the pinned
openEMS writes for a frequency-domain dump (F0 Q11 noted VTK or HDF5) by a real run, and read only
that. **If it is HDF5, stop and ask the owner**: no managed HDF5 reader is in the repo, and the
standard one is native (CLAUDE.md, ask before).

**`R-em3d29-6b`** The field is on the rectilinear grid, so slicing is trivial and exact on grid lines.
The same field model (§2b) carries it, with no special case in the drawing code.

**`R-em3d29-6c`** No difference view between the two solvers (overview §4).

---

## 7. Gate

`tests/Ui.Tests/Viewer3D/FieldTests.cs` and `tests/Ui.Tests/Em3d/PalaceBackendTests.cs` (goldens).

1. **Goldens.** After the re-golden, each config differs from the previous golden only in output lines
   (diff-scan).
2. **Reader on the fixture.** Node and cell counts equal the `.vtu` headers, pieces merge to the count
   Palace's log reports, and real/imaginary arrays pair up.
3. **Palace's own probes.** On the fixture run, circuitRF's interpolated |E| and complex E at the three
   probe points equal Palace's probe CSV within **1e-3 relative** (order-2 fields displayed through
   linear sub-tets differ from the exact point value; if 1e-3 is not met, measure what the display
   error is and justify the tolerance from it, citing the measurement).
4. **Slice exactness.** A linear field on a synthetic tet mesh sliced by an arbitrary plane equals the
   analytic field on the plane to 1e-12.
5. **Animation uploads nothing.** 100 phase steps upload 0 bytes (recording fake).
6. **Memory bound.** Loading F0 case A's field keeps the reader's managed allocation under a stated
   bound (from `GC.GetAllocatedBytesForCurrentThread`). This is a counter, not a timing.
7. **Absent arrays are not offered.** A fixture with no H and no boundary current offers no |J_s|.
8. **Eigenmode fields** (if brief 23 is built). The cavity fixture's TE101 |E| peaks at the cavity
   centre and is zero on the PEC walls to the display tolerance.
9. **openEMS** (if §6 is built). The dump's |E| at the matched line's centre is within 2 % of the value
   from the port voltage over the line's height, for the uniform-field region of a stripline.

## 8. Owner check

- The via transition at the centre frequency: |E| on a clip plane through the via, then animated.
- The bond wire: surface current on the wire and pads.
- The cavity's first three modes (if 23).
- *Export picture…* at 4× opens in an image viewer and matches the screen.

## 9. Scope

- No volume rendering, streamlines or far-field patterns. They are good later additions; far field has
  its own Palace output.
- No difference field. No video export.
