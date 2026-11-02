# Brief 107 — Shadows, contact shading, a ground, and the realistic picture export

**Tag:** `R-em3d107-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** `scene.wgsl` (+ regenerated), the three backends, `Scene3DFramePlan.cs`, `src/Render/Scene3D/Look/`,
`src/Ui/Viewer3D/Viewer3DViewModel.Fields.cs` (`CapturePicture` ~779), `src/Ui/Views/ThreeD/C3dEditorView.axaml.cs`
(`OnExportPicture` ~163), `Viewer3DPictureCopy`, `C3dDocument.cs` (`Look`)
**Depends on:** 106 · **Blocks:** 110

## Why

Environment lighting makes the materials read correctly. What makes a picture read as **real** is how objects sit on each
other: a die's shadow on its substrate, the darkening where a bond wire meets a pad, a package resting on something rather
than floating in a gradient. Without those a realistic render looks pasted together. This brief adds them, and then makes
**Export Picture** produce the realistic view at marketing quality: supersampled, optionally on a transparent background.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| The key light, a direction, from the environment (106 §4a) | `StudioEnvironment` |
| Depth-bias ties and their slope-scale helper | `Scene3DFramePlan.DepthBias` ~274 |
| A depth-range camera for the scene | `Scene3DFramePlan.DepthCamera` ~875 |
| Offscreen export at 1–4 × the window, capped at `FieldPicture.MaxSide`; legend and caption options | `Viewer3DViewModel.Fields.cs` ~770–800 |
| Readback is RGBA8, rows top to bottom | `Viewer3DBackend.RenderPixels` |
| The live image's alpha must stay 1 | `Viewer3DBackend.cs` header |
| On-demand frames | `Viewer3DPane.RequestFrame` |

## 1. `R-em3d107-1` — key-light shadows

- **a.** A **shadow map** rendered from the key light's direction, orthographic and fitted to the visible scene's bounds,
  `Depth32F`, 2048² live and 4096² at export. It needs a depth-only pipeline (`ShadowDepth`, vertex stage only) on each
  backend.
- **b.** **It is re-rendered only when what it depends on changes**: geometry generation, a patch, a drag preview's
  transforms, visibility, or the key-light direction (the `Look` rotation). **An orbit renders no shadow pass**, because
  the map does not depend on the camera. That is a counter gate.
- **c.** Sampling: percentage-closer filtering with a **fixed** Poisson pattern (16 taps). The kernel's width follows the key
  light's softbox angle, so the High-key preset gives soft shadows and Dark gives crisp ones. Slope-scaled bias through the
  existing `DepthBias` helper, not new literals.
- **d.** Casters: every drawn object **except** one whose resolved `Transmission` ≥ 0.5 (owner decision D1). Ports,
  boundaries and fields never cast.
- **e.** The shadow scales only the key light's **direct** term in `fs_pbr`. Environment light is darkened by occlusion
  (§2), not by the key light's shadow.

## 2. `R-em3d107-2` — ambient occlusion (contact shading)

- **a.** **Screen-space, from depth** (overview D10). A depth prepass of the opaque `Pbr` draws, then a full-screen pass
  computes horizon-based occlusion into an `R8` target. `fs_pbr` samples that target at the fragment's screen position and
  scales **only the environment terms**.
- **b.** Radius in world units: a fixed fraction of the scene's bounding radius (a named constant), so the contact
  darkening looks the same at every zoom. Fixed sample kernel, no per-frame noise (overview §1e). A 4 × 4 depth-aware
  blur removes the kernel's pattern.
- **c.** Translucent objects neither write the prepass nor receive occlusion.
- **d.** Fields are untouched (rule 2): `fs_field` never samples the occlusion target.

## 3. `R-em3d107-3` — the ground

- **a.** An optional **shadow catcher**: a horizontal disc at the scene's lowest z, radius 4 × the scene's bounding radius,
  fading to nothing at its rim. It **draws only darkening**: the key light's shadow and the occlusion, as black with alpha.
  Over a solid background it looks like a floor the model rests on; over a transparent export it becomes a soft shadow in
  the PNG's alpha.
- **b.** It ignores the section clip plane, is never picked, and is not drawn when the camera is below it.
- **c.** A reflective floor is deferred (overview §4).

## 4. `R-em3d107-4` — `Look` keys

`Shadows` (bool, default true), `AmbientOcclusion` (bool, default true), `Ground` (bool, default true; owner decision D2).
These are added to 106's `Look` block, its schema entry and its validation.

## 5. `R-em3d107-5` — the realistic picture export

The existing **Export Picture…** (`OnExportPicture` → `CapturePicture`), when the view is realistic:

- **a.** **Supersampling**: draw at k × the output size (k = 1, 2 or 4, default 2; the internal size is still capped by
  `FieldPicture.MaxSide`) and downsample in C# with a separable filter. The output size is what the user asked for. The
  downsample lives in `src/Render` so 110 uses it too.
- **b.** **Transparent background** (a checkbox, realistic only): the offscreen target is cleared to (0, 0, 0, 0) and the
  backdrop is skipped. With today's blend (colour SRC_ALPHA/ONE_MINUS_SRC_ALPHA; alpha ONE/ONE_MINUS_SRC_ALPHA) the result is
  **premultiplied**, so the readback is un-premultiplied in C# before the PNG is encoded. Glass over nothing therefore
  becomes partially transparent glass in the file, and the ground's shadow becomes a soft alpha shadow. **The live view never
  does this**; its alpha stays 1.
- **c.** The shadow map is 4096² for the export (§1a).
- **d.** The legend and caption options behave as they do today. 109 adds the `Lit Fields` indicator, which is independent of both options.
- **e.** No hover, selection or cursor in the picture (already true: `CapturePicture` parks the cursor and plans with
  `pick: false`). A test holds it for the realistic plan.

## 6. Gate

1. **Shadow present** (Metal offscreen, macOS): a box on a plate, key light from above at an angle. A plate pixel inside the
   box's shadow is darker than an unshadowed plate pixel by more than 20/255. With `Shadows: false`, the two match within
   2/255.
2. **No shadow pass on orbit.** Counter: three orbit steps render zero shadow passes. One `Look.Rotation` step renders one.
3. **Translucent casters.** A `Transmission: 0.9` slab casts no shadow; at 0.2 it does.
4. **Occlusion.** The inside corner of an L-shaped solid is darker than its open face (`AmbientOcclusion` true), and equal
   within 2/255 when false.
5. **Fields untouched.** A field pixel inside a shadow and an occluded corner reads exactly its colour-map colour.
6. **Transparent export.** A single box with `Ground` true over nothing: alpha 0 far from the box, between 1 and 254 in its
   shadow, 255 on the box. A 50 % grey `Transmission` slab over nothing un-premultiplies back to its straight colour within
   1/255.
7. **Supersampling.** The output is the requested size at k = 1, 2 and 4. The downsample filter has a unit test on a known
   pattern.
8. **Idle.** After a realistic frame with nothing changing, no further frame is requested over a simulated idle period
   (counter, not a timing).

Run only the classes touched (`--filter`), plus `Firewall.Tests --no-build`. **No new timing tests**; every property is a
counter or a pixel.

## Decisions — settled by the owner, 2026-10-04 (each as recommended)

- **D1 Which objects cast shadows.** *Recommended:* all except `Transmission` ≥ 0.5. *Alternative:* everything, with glass
  casting full shadows, which looks wrong.
- **D2 Ground on by default?** *Recommended:* yes. A floating model is the most common "looks fake" complaint.
- **D3 Default supersampling.** *Recommended:* 2× (4 × the pixels drawn). 4× is offered for final pictures.

## On completion

Record in `src/Ui/Viewer3D/RESOLVED.md`: the shadow-map invalidation rule and the premultiplied readback. Record in
`src/Render/RESOLVED.md`: the occlusion kernel and radius constant. Never CLAUDE.md. Do not commit unless the owner asks.
