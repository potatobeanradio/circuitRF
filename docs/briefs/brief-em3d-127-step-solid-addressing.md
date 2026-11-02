# Brief 127 — STEP: address one solid inside a part

**Series:** [3D EM, ninth series](brief-em3d-126-overview.md) · **Tag:** `R-em3d127-n`
**Area:** `tools/geometry-worker/geometry_worker.cpp` (`ReadPart`, `ReadStep`, `OpImportStep`, `BuildStep`),
`src/Design/ThreeD/Occ/GeometryKernel.cs` (`GeometryKernelImportPart`), `src/Design/ThreeD/Occ/GeometryKernelTree.cs`
(the `step` node), `src/Design/ThreeD/C3dDocument.cs` (`C3dStep`), `src/Design/ThreeD/C3dValidation.cs`,
`src/Design/ThreeD/C3dElaborator.cs` (provenance text), `src/Design/ThreeD/Step/StepImport.cs` (`PlanReload`),
`src/Cli/DocumentSchema.cs`. Findings in `src/Design/RESOLVED.md`.
**Depends on:** — · **Blocks:** 128, 129, 130

---

## 0. What this brief settles

The **model and the worker** for a piece of a STEP part. Nothing user-facing changes here except that a hand-written
`"Solid": k` works. The import (128) and the split command (129) are the two ways a user gets one.

## 1. `R-em3d127-1` — the worker reads solids

**R-em3d127-1a.** `ReadPart` gains a list of its solids, in `TopExp_Explorer(p.shape, TopAbs_SOLID)` order after
healing. For each solid:
- `closed` and `why`, from `ClosedSolid` applied to that solid alone;
- `faces`, the face count;
- `name`, from the solid's XCAF label when the file names it, else empty;
- `colour`, per overview D6, in that order: the solid sub-shape's colour (`XCAFDoc_ShapeTool::FindSubShape` and then
  `XCAFDoc_ColorTool::GetColor`, surface colour first); else the colour every face shares; else the part's colour when
  no face carries one; else none, with a `mixed` flag when the faces disagree;
- `volume_um3` and the bounding box in µm (for reload's geometric match, §4, and for 128's table).

A part of one solid lists one solid. **A part's own `closed` does not change meaning:** the compound is still checked
as today, so a document that never names a `Solid` builds exactly as before.

**R-em3d127-1b.** `import-step`'s reply carries the list per part (`"solids": [...]`). `GeometryKernelImportPart` gains
`IReadOnlyList<GeometryKernelImportSolid> Solids`. With `display_rel` the triangle estimate is per solid as well, so
128's table can show it per row.

**R-em3d127-1c.** The `step` tree node takes an optional `"solid": k`. With it, `BuildStep` returns the k-th solid of
the part, its faces named `face<n>` in **that solid's own** `TopExp::MapShapes(TopAbs_FACE)` order (brief 68
R-em3d68-1e, applied to the solid). It refuses as follows:
- k out of range: `"the part has <n> solids; there is no solid <k>"`;
- the solid not closed: `"solid <k> of part <p> is not a closed solid: <why>"`.

Without `solid` the node is unchanged.

**R-em3d127-1d.** Healing notes for a solid name the solid (`part 1, solid 3: ...`). The per-file read cache
(`g_stepFiles`) is shared by every solid of a file. **Building seven pieces of one file reads it once.** Gate it with
the worker's existing read counter, not a timing.

## 2. `R-em3d127-2` — the model

**R-em3d127-2a.** `C3dStep` gains:

```csharp
/// <summary>brief-em3d-127 — which solid of <see cref="Part"/>, 1-based in the reader's topological order; null for the whole
/// part (legal, and named by `check` when the part has several solids — brief 129). Meaningful for the recorded Hash.</summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public int? Solid { get; set; }
```

It is written only when set, so every existing `.c3d` round-trips byte for byte. `GeometryKernelTree` writes
`"solid"` only when it is set, so **a cached build of an existing object keeps its cache key**. Assert that key in the
gate: a key change re-tessellates every STEP object of every user on first open.

**R-em3d127-2b.** `C3dValidation`: `Solid < 1` is an error naming the object. Out of range is the worker's refusal
and needs the kernel, so it is reported where the build is, never guessed here.

**R-em3d127-2c.** The elaborator's provenance text (`C3dElaborator.cs` ~1144) reads `Step part 1, solid 3 of
x.step (in millimetre)`. `explain` and the Inspector show it.

**R-em3d127-2d.** `DocumentSchema`'s `Step` entry documents `Solid` in one sentence, with the example extended. The
MCP `reference 3d-view` topic serves the same text.

**R-em3d127-2e.** Overview D7 (format compatibility): implement the recommended default, which needs no code. If the
owner picks the alternative, bump `CurrentFormatVersion` only when a document holds a `Solid`, and record the
choice in `RESOLVED.md`.

## 3. `R-em3d127-3` — STEP export is unchanged, and says so

Brief 69 writes one part per object, so seven pieces export as seven parts, each with its own material colour.
**Add no special case.** The gate asserts that a part built from `Solid = k` and exported, then re-imported, is one
part of one solid with the same volume within the face-matching tolerance.

## 4. `R-em3d127-4` — reload matches solids by geometry

`PlanReload` today maps an object to the revised file's part by `Part` path, then each referenced face by geometry
(`SameFace`). A revised file may reorder or add solids. **A `Solid` index is not trusted across a revision:**

**R-em3d127-4a.** For an object with `Solid = k`, find the revised part by `Part` as today. Then find the **one** solid
of it whose bounding box matches the old solid's within the face tolerance (`tolUm`) and whose volume matches within
1e-6 relative. Take the old solid's box and volume from the current copy's read; the plan already reads both files.
Use that solid's index:
- several matches: a refusal, `"'<obj>' is solid <k> of part <p>, which matches <n> solids of the revised file;
  circuitRF does not choose one"`;
- none: `"...which the revised file no longer has"`.

The face re-pointing then runs against that solid, unchanged.

**R-em3d127-4b.** A solid that changed shape (a lead lengthened) has no box/volume match, so it is refused with the
object kept on the old copy. **This is deliberate:** a geometric change is exactly when a port on it must be
re-checked by a person. The refusal text says so.

**R-em3d127-4c.** New solids in a revised part that no object names are offered like new parts are today
(R-em3d68-5d). The offer carries the solid index.

## 5. Gates

Fixture: a hand-written minimal AP214 file, `testdata/step/two-solids-one-product.step`. It holds one `PRODUCT` with
two `MANIFOLD_SOLID_BREP` boxes of different sizes. The first has a `STYLED_ITEM` colour on the solid. The second has
two face colours that differ, which makes it *mixed*. A revised variant moves the second box and swaps the two
solids' order in the file. **No third-party file.**

In `tests/Ui.Tests/ThreeD/StepSolidTests.cs`, all `[KernelFact]`:
1. `import-step` lists one part with two solids, each with the expected face count, volume, colour, and *mixed* for
   the second.
2. `Solid = 1` and `Solid = 2` each build one closed solid of the right volume. `Solid = 3` is refused with the
   worker's text. No `Solid` builds the compound as before, **with its tree cache key unchanged** (assert the key
   string against one captured before the change).
3. Two pieces of one file read the file once (the worker's read counter).
4. A `.c3d` without `Solid` round-trips byte for byte. One with `Solid` round-trips with it.
5. Reload against the reordered revision re-points `Solid` by geometry for the unchanged box. The moved box is refused
   with the object kept.
6. Export then re-import of one piece gives one part of one solid of the same volume.

Run only `StepSolidTests`, `StepImportTests` and `StepExportTests`.
