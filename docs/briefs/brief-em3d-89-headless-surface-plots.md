# Brief 89 — `render --field` draws a Surfaces or Faces plot

**Tag:** `R-em3d89-n` · **Series:** brief 88's owner decision Q3 (its own brief, later). It follows brief 84 (`render --field`,
sections only) and brief 88 (a temperature section).
**Area:** `src/Cli/RenderEm3dField.cs`, `src/Render/Renderers/` (the section and outline renderers, `Em3dOutline`,
`Em3dSectionField`), `src/Render/Scene3D/Fields/` (`FieldSurfaces`, `FieldFaces`, `FieldPlotResolver`), `docs/design/cli.md`
§13.8.1, `docs/user/src/reference/cli.md`, `tests/Ui.Tests/Render/`
**Depends on:** 84, 88 · **Blocks:** —
**Status:** written as a stub on 2026-09-29, at the owner's request (brief 88 Q3). **Built 2026-09-29** — owner answers:
any direction; PNG only at first; mirrored halves drawn by default (`--no-mirror`). Findings in `src/Cli/RESOLVED.md`.

---

## 0. The short answer

`render --field` draws a ClipPlane plot only. A **Surfaces** plot (every exposed face; a temperature's *All Faces*) and a
**Faces** plot (the faces the user picked) are still refused with `render.field.not-headless`, naming Export picture. The
3D view draws them on the GPU with depth, and a headless picture of them needs a 3D projection with hidden-surface removal,
not a section. That projection exists for OUTLINES (`Em3dOutline`, the iso picture and the vector export), but it draws edges,
not filled faces with a field on them.

## 1. What a picture needs (to be decided)

1. **The view.** The document's own camera is not saved; a picture needs a stated direction (the iso, a standard view, or
   `--view-dir`), and brief 5's outline already has the iso and the standard views.
2. **Filled faces in depth order.** Either a painter's sort of the field triangles (enough for convex packages, wrong for
   interlocking parts), or a small software depth buffer for PNG with the vector page taking the painter's order. The
   outline's hidden-edge test (`Em3dHiddenEdges`) may be reusable for the edges drawn over the faces.
3. **The field on the faces.** `FieldSurfaces.Exterior` (All Faces) and `FieldFaces.OnFace` (a picked face) already give
   the triangles the view draws; a temperature adds the wires from their T(s) as brief 88 does, now as 3D tubes.
4. **Range.** A temperature: the true minimum and maximum of what is drawn (brief 75 D9), extended to the wires. An EM field
   on faces: the view's percentile rule.
5. **Everything brief 88 added carries over**: `--labels`, `--tight`, `--axes`, (`--scale-bar` is refused on a projection
   with no one scale, as on `--iso`).

## 2. Owner questions for when this is scoped

- Which views: the iso only, the six standard views, or any direction?
- PNG only at first (a depth buffer is simplest there), or vector from the start?
- Should the mirrored halves across a symmetry plane be drawn, as the 3D view draws them?
