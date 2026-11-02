# Brief 111 — glTF export: the model, its appearances, optionally its field

**Tag:** `R-em3d111-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** new `src/Render/Scene3D/Export/GltfExport.cs` (+ `GlbWriter.cs`), `src/Cli/LayoutConvert.cs` (`Fmt`, ~37–70),
`src/Ui/ThreeD/` (new `GltfExportDialogViewModel.cs`, modelled on `StepExportDialogViewModel`), `src/Ui/Views/ThreeD/`
(its dialog), `src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs`, `src/Ui/Views/WorkspaceWindow.axaml` (every File ▸ Export
menu that lists STEP…), `src/Cli/DocumentSchema.cs` or `cli.md` (the format table), `docs/user/src/reference/drawing-in-3d.md`
**Depends on:** 104 (normals), 105 (appearances). **Not** on 106: this brief can be built straight after 105.
**Blocks:** —

## Why

This is the series' answer to *should circuitRF ray-trace?* (overview §4). glTF 2.0 is the open interchange format for
physically based scenes. Its metallic-roughness materials are exactly 105's schema, so the export is lossless, and every
path tracer reads it. A user who wants a fully ray-traced picture (refraction through a glass lid, caustics, real
global illumination) exports and renders elsewhere, and circuitRF maintains no ray tracer.

It also gives the **solved field** a way out: as an unlit, vertex-coloured mesh that another renderer draws without
relighting it, so rule 2 survives the trip.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| STEP export: options Assembly, As drawn, Thicken sheets, Include air box; a planning/writing dialog with progress | `StepExportDialogViewModel` ~51–64; `src/Design/ThreeD/Step/StepExport.cs` |
| `convert` exports STEP from a `.c3d` (export one format, inferred from `-o`'s extension) | `LayoutConvert.cs` `Fmt.Step`, ~65–70 |
| **File ▸ Export ▸ STEP…** on five menu surfaces in `WorkspaceWindow.axaml` | grep `STEP…` |
| What the view draws, with face-split vertices, normals (104) and the appearance table (105) | `Scene3DModel` |
| Scene-local metres (positions relative to the scene's origin, for float precision) | `Scene3DModel` / `Scene3DBuilder` |
| A field's surfaces, and the colour map | `FieldSurfacePlot.cs`, `ColorMap3D.cs` |

**Why it lives in `src/Render`:** it exports **what the view draws**: the same triangles, the same face splits, the same
104 normals and the same 105 slots, from `Scene3DModel`. `ShadingNormals` is in `src/Render`, and `src/Design` cannot
reference `src/Render`. It draws nothing. `src/Cli` already references `src/Render`.

## 1. `R-em3d111-1` — what is written

- **a.** **`.glb` only** (binary glTF: one file, JSON chunk + BIN chunk, 4-byte aligned). `.gltf` + `.bin` is not offered
  (owner decision D1).
- **b.** **Content: what is drawn**. Every visible solid and sheet. Hidden objects are left out. The air box, ports,
  boundaries, face tints, grid and overlays are never written. Reference images are left out (D2). `Model: false`
  objects **are** written: they are drawn, and a picture wants them.
- **c.** **Meshes.** One glTF mesh per object, one primitive per appearance slot it uses. Attributes `POSITION` (float3)
  and `NORMAL` (104's normals). Indices are `UNSIGNED_INT`, or `UNSIGNED_SHORT` when the primitive has fewer than 65,536
  vertices. `POSITION` carries min/max (glTF requires it).
- **d.** **Materials** from `Scene3DModel.Appearances`, already linear, which is what glTF's factors are:
  `pbrMetallicRoughness.baseColorFactor`, `metallicFactor`, `roughnessFactor`; `KHR_materials_transmission`,
  `KHR_materials_ior`, `KHR_materials_clearcoat`, and `KHR_materials_volume` (attenuation colour and distance;
  `thicknessFactor` = the object's smallest bounding-box extent, which is stated in the export notes as an approximation).
  An extension is written **only where its value differs from glTF's default**, and is listed in `extensionsUsed`, **never
  `extensionsRequired`**, so a viewer without it still opens the file. Material names are the material's name, suffixed
  `+override` when an object override made a distinct appearance.
- **e.** **Nodes.** Object name → node name. `C3dObject.Group` paths → parent nodes. With **Assembly** on, an instance is a
  node that shares the cell's meshes (one mesh, several nodes). With it off, everything is flattened. A **root node**
  carries the scene-local origin's translation and the **Z-up → Y-up** rotation (−90° about x), so the model stands up in
  every viewer. Units are metres: glTF's and the scene's.
- **f.** **Camera** (GUI only): the current view as a glTF camera node, perspective or orthographic, so the other renderer
  opens on the same framing. When the `.c3d` holds a `Look.Camera` (overview D17), the CLI writes
  that one; otherwise the CLI writes none.
- **g.** `asset.generator` = `circuitRF <version>` (from `VERSION`, through the assembly, never a literal), and no
  timestamp, so the file is deterministic.

## 2. `R-em3d111-2` — the field, optionally (overview D14)

- **a.** **Include field plot** (an option naming one drawn plot): the plot's surfaces as a separate mesh named after the
  plot, with `COLOR_0` from the colour map at the current phase and range, and a material with **`KHR_materials_unlit`**,
  so a renderer that honours it draws the colours unshaded.
- **b.** Colour space: glTF vertex colours are **linear**. The colour map's display colours are sRGB, so they are decoded
  before writing, and a viewer shows the same sRGB colours.
- **c.** **The honest limitation**: a vertex colour is interpolated between corners, while the view maps the **value** per
  fragment (brief 84's reason). On a coarse mesh the exported colours can differ from the view between vertices. Before
  writing, the exporter subdivides each field triangle until no edge's two endpoint values span more than 1/16 of the
  range, capped at four levels. The dialog and the CLI report say "field colours are per vertex; subdivided to n
  triangles."
- **d.** With a field included, the objects it stands in for (`FieldCovered`) are left out, as the view leaves them out.

## 3. `R-em3d111-3` — where it is offered

- **a.** **File ▸ Export ▸ glTF…** beside STEP… on **every** menu surface that lists STEP (five in `WorkspaceWindow.axaml`),
  with the same "Requires an active 3D document" tooltip pattern.
- **b.** A dialog modelled on the STEP one: Assembly, Include camera, Include field plot (a combo of drawn plots, "None"
  default), and a summary line (objects, triangles, materials, and the extensions that will be used).
- **c.** **`convert x.c3d -o x.glb`**: `Fmt.Gltf`, export only. A `.glb` source is refused ("circuitRF exports glTF; it does
  not import it"). Flags mirror the dialog: `--gltf-assembly`, `--gltf-field <plot>`. The view state is the document's (no
  hidden-by-session state headlessly), and the camera follows §1f.

## 4. Gate

1. **Structure** (a small validator in the test project, no external tool): GLB header magic, version 2 and total length;
   chunks 4-byte aligned; the JSON parses; every accessor lies inside its buffer view and every view inside the buffer;
   indices < vertex count; `POSITION` min/max correct; `NORMAL` unit length within 1e-3; every extension used is in
   `extensionsUsed` and none is in `extensionsRequired`.
2. **Content.** A fixture with a hidden object, an air box, a port, an override (`Like: Gold`), a glass slab and a group:
   the hidden object, air box and port are absent; the group is a parent node; the override is its own material; the glass
   has `KHR_materials_transmission` and `ior`; a plain copper box has no extension.
3. **Orientation and units.** A 1 mm × 2 mm × 3 mm box (x, y, z) comes out with glTF y-extent 3 mm after the root rotation.
4. **Lossless appearance.** Every material's factors equal `Scene3DModel.Appearances` within 1e-6.
5. **Assembly.** Three instances of one cell write one mesh and three nodes. Flattened writes three meshes.
6. **Field.** `COLOR_0` present, the material unlit, the colours equal the colour map (sRGB-decoded) at the vertices, and the
   subdivision rule holds (no edge spans more than 1/16 of the range, or the cap was reached and reported).
7. **Determinism.** The same export twice is byte-identical, GUI path and `convert` alike, and the two paths agree byte for
   byte with the camera excluded.
8. **Refusal.** `convert x.glb -o y.c3d` refuses with its sentence.

Run only the classes touched (`--filter`), plus `Firewall.Tests --no-build`.

## Decisions — settled by the owner, 2026-10-04 (each as recommended)

- **D1 `.glb` only?** *Recommended:* yes. One file is what gets emailed. *Alternative:* also `.gltf` + `.bin`.
- **D2 Reference images.** *Recommended:* left out. They are tracing aids. *Alternative:* written as textured quads.
- **D3 The layout's 3D form** (as STEP export offers). *Recommended:* not in this brief. `.c3d` only; add it later if asked.

## Docs

`drawing-in-3d.md`: "Exporting for another renderer": what is written, the field option and its per-vertex note, Z-up →
Y-up, and that a path tracer gives refraction and caustics the realistic view approximates. Edit doc sources only; DocGen
runs at the end of the series.

## On completion

Record in `src/Render/RESOLVED.md` (why it lives in Render; the field subdivision rule) and `src/Cli/RESOLVED.md`. Never
CLAUDE.md. Do not commit unless the owner asks.
