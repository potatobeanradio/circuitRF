# Brief 92 — Per-object transparency: saved, grouped, drawn, exported and rendered headless

**Tag:** `R-em3d92-n` · **Series:** 3D editor round 8 (90–95).
**Area:** `src/Design/ThreeD/C3dDocument.cs` (+ `C3dPersistence`, `C3dElaborator`), `src/Render/Scene3D/Scene3DBuilder.cs`,
`src/Render/Renderers/Em3dDrawing.cs`, `Em3dDrawingExport.cs`, `Em3dSectionRenderer.cs`, the brief-89 surface renderer,
`src/Ui/ThreeD/C3dPropertiesViewModel.cs`, `src/Ui/ThreeD/C3dEditorViewModel.Groups.cs`, `src/Cli/RenderEm3d.cs`,
`src/Cli/RenderEm3dField.cs`, `src/Cli/DocumentSchema.cs`, `docs/user/src/reference/drawing-in-3d.md`,
`docs/user/src/reference/cli.md`
**Depends on:** — · **Blocks:** 95 (copy/paste carries it)

## Why

A colour comes from the object's material (`TechMaterial.Color`, `#rrggbb`, no alpha). How see-through it is comes from
its **kind**: dielectrics are drawn at `DielectricAlpha`, conductors opaque, air at `AirAlpha` (`Scene3DBuilder`). The
user cannot see through a lid to the die under it without changing the material, and a material change affects every
object made of it and belongs to the technology. Transparency has to be a property of the **object**, independent of
the display colour.

## 1. `R-em3d92-1` — the document

- `C3dObject.Transparency`: a nullable percentage, `0` (opaque) to `100` (fully transparent) in steps of 1. **Null means
  the kind's default**, today's look, so every existing document draws exactly as it does now. The key is written only
  when set: `"Transparency": 60`.
- Stored as a percentage ("transparency", the owner's word), not an `Opacity` fraction.
- **Capped at 95 % (owner, 2026-09-29), and the cap must be easy to change later.** It is **one named constant** in
  `src/Design` (e.g. `C3dTransparency.Max = 95`). The Inspector's slider maximum, the numeric box's validation, `check`'s
  range refusal, the CLI override's validation and the refusal sentences all read it; **no literal `95` appears
  anywhere else**. The refusal sentence is built from the constant (`"… 0 to {Max} %"`). A test sets values at
  `Max` and `Max + 1` through each entry point, using the constant. Raising the cap to 100 later must then need only the
  constant changed, plus a decision about picking a 100 % object, which the constant's doc comment records: "a fully
  transparent object must stay pickable and keep its edges drawn".
- Carried by every drawn object: box, prism, cylinder, sheet, polygon, wire, and an operation (a boolean, fillet or
  chamfer). The operation carries it for its result, as it carries `Group` and `Hidden`. An operand has none of its own
  while inside the boolean. A polyline is a line, and a line has no transparency.
- **An instance** carries it too: its whole placed content, **multiplied onto** each part's own. Note that `C3dInstance`
  is **not** a `C3dObject` (it is its own class in `C3dDocument.cs`), so it needs the property added separately, with the
  same key and rules.
- Undoable, and a document edit (dirty mark), exactly as `Hidden` is.
- `src/Cli/DocumentSchema.cs` documents the key. `check` refuses a value outside the allowed range and names it.

## 2. `R-em3d92-2` — groups

- Groups are a **path on each member** (`C3dObject.Group`, `C3dGroups`). There is no group object to hold a value, and
  none is added. "Each member keeps its own until the group's is edited" follows from that: the members' values are the
  only values.
- The Inspector, with a group selected whole (`LoadGroup`), shows **one Transparency row**:
  - every member equal: that value;
  - members differ: an empty field with the placeholder `mixed`;
  - editing it writes the value to **every member at every depth** (nested groups included). That is one undo entry
    through `ChangeObjects`.
- The same for a multi-selection of ungrouped objects: the Inspector's multi-selection heading (`"{n} selected"`) gains
  the Transparency row. It is the one property a multi-selection edits here; say so if that leaves the multi-selection
  view looking unfinished.

## 3. `R-em3d92-3` — the Inspector

- A **Transparency** row on every object that carries it. It is a slider (0 to the cap) plus a numeric box, and a
  *Default* button that clears it back to null. The slider **previews while dragging and commits on release**: one undo
  entry per drag, as brief 86's sweep slider does. The box accepts a number, not an expression (D3 below).
- With the value null, the row shows the kind's default, greyed with `(default)`, so the user can see what they are
  starting from.

## 4. `R-em3d92-4` — the 3D view

- `C3dElaborator` hands the value through to the scene: `Scene3DObject` gains the alpha override. `Scene3DBuilder` packs
  the alpha from it, and `Translucent` becomes true whenever the result is not fully opaque. That includes a **conductor**,
  which today is always opaque. **Measure the ordering.** Check how `Scene3DFramePlan` orders translucent draws (it sorts
  by object today, or does not). A transparent conductor in front of a dielectric must look right from both sides; if
  the plan does not sort translucent objects back to front by depth, that is part of this brief. **Order-independent
  transparency is not** part of it: say what artefacts remain.
- The "dimmed"/ghost and context looks (`Dimmed`, `Ghosted`, the entered-boolean ghosts) keep their own alphas, and they
  win while in force.
- Picking: an object at any transparency is picked by its surfaces as now. The ID pass ignores colour.
- Instances: the parent's value multiplies onto each part's, so a 50 % instance of a part drawn at 50 % shows at 75 %
  transparency.

## 5. `R-em3d92-5` — exports: the PNG and the vector drawing

- **Copy and Export Picture…** (`Viewer3DPictureCopy`, GPU read-back) come for free from §4. Gate it anyway: the read-back
  is Premul RGBA, and a transparent object over the cleared background must come out with the right alpha when the
  background option is "transparent".
- **Export Drawing… (SVG/PDF, `Em3dDrawing` / `Em3dDrawingExport`)** takes a per-object opacity map beside
  `style.ObjectColours`. Fills are painted with it. Today a conductor is forced to `WithAlpha(0xFF)`.
  - **Hidden-line removal (owner, 2026-09-29): an object with transparency > 0 does NOT occlude.** Edges behind it are
    drawn (solid, not hidden-dashed), and its fill is painted over them at its alpha. That is what "see through it" means
    on paper.
  - **It is a flag, easy to change later:** one property on the drawing request,
    `Em3dDrawingRequest.TransparentObjectsOcclude` (default `false`), read in exactly one place, where the occluder set
    is built. Nothing else branches on transparency for occlusion. It is a request property, not a `const`, so Export
    Drawing… can later offer it as a checkbox, and the CLI as a flag, without a refactor. Neither is added now. The
    gate covers both values of the flag.
  - SVG: `fill-opacity`. PDF: Skia's alpha. Both are checked for the right result. PDF's transparency group must not turn
    the whole page into a raster; confirm it stays vector.
- **3D Copy as vector** (the 2026-09-27 vector copy) uses the same renderer, so the same map.

## 6. `R-em3d92-6` — the CLI

- `circuitrf render x.c3d` in every mode that draws objects: the isometric outline and sections (`RenderEm3d`, via
  `Em3dSectionRenderer`), the field plots' context geometry (`RenderEm3dField`), and brief 89's `Surfaces`/`Faces`
  pictures. Each honours the document's per-object transparency, drawn by the same renderer the GUI export uses. The
  verb owns no drawing, on `Render.cs`' terms (cli.md §13.1).
- **Owner decision D5:** add an override for one render, `--transparency name=60[,name=…]` (a group path allowed, applying
  to its members), in the spirit of `--layer-colors`. The recommendation is **yes**: a picture for a report often wants
  one lid see-through without editing the design. An unknown name is a refusal listing the names. The flag applies to a
  **copy** of the document, never the loaded instance (the `TechnologyLayerSelection` clone lesson).
- `explain x.c3d` lists objects with a non-default transparency only if the owner wants it. Not needed for the gate.

## 7. Gate

1. Round trip: a `.c3d` with `"Transparency": 60` on a box, a group of two, and an instance loads, saves byte-identical,
   and a document with none of the key is byte-identical to before.
2. Group Inspector: members at 20 and 70 show `mixed`; setting 40 writes 40 to both (and to a nested group's members) in
   one undo entry; undo restores 20 and 70.
3. Scene: the box's `Scene3DObject` alpha is the percentage's (`255 × (1 − 0.6)` rounded as specified), `Translucent`
   true, and a conductor at null stays opaque.
4. Export Drawing SVG: the box's fill carries `fill-opacity="0.4"`. With `TransparentObjectsOcclude = false` (the
   default), an edge of an object behind it is present and not dashed. With `true`, the same edge is hidden as today.
   The cap: `Max` is accepted and `Max + 1` refused, at every entry point (§1).
5. CLI: `render x.c3d -o iso.png --iso` run **as a process** compared byte for byte with the in-process renderer call on
   the same document (the pattern `RenderCliVerbTests` uses). `--transparency lid=80` changes the output and leaves the
   file on disk untouched.
6. Firewall: `src/Render` and `src/Cli` gain no Avalonia reference (`Firewall.Tests`, `--no-build`).

Run only the classes touched, with `--filter`.

## 8. Decisions for the owner

**Settled (owner, 2026-09-29):** cap at 95 %, as one constant (§1). Transparent objects do not occlude in drawings, as
one request flag (§5).

**Still open, built as recommended unless the owner says otherwise:** D1 percent rather than an opacity fraction ·
D3 a plain number rather than an expression · D5 a `--transparency` override on `render`.

## Docs

`drawing-in-3d.md` (the property, groups, default), `cli.md` (render honours it; the override flag). Doc sources only.

## On completion

`src/Design/RESOLVED.md` and `src/Render/RESOLVED.md` (the draw-order finding especially), never CLAUDE.md. Do not commit
unless the owner asks.
