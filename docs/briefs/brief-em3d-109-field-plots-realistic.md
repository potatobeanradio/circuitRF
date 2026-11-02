# Brief 109 — Field plots in the realistic view

**Tag:** `R-em3d109-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** `scene.wgsl` (`fs_field` ~315 and a new `fs_field_lit`), `Scene3DFramePlan.cs`, `src/Ui/Viewer3D/FieldLayer.cs`,
`Viewer3DViewModel.Fields.cs` (the caption), `C3dDocument.cs` (`Look`), `src/Render/Scene3D/Look/Pbr.cs`
**Depends on:** 106 (107 for the shadow and occlusion interplay) · **Blocks:** 110 (its field half)

## Why

circuitRF's realistic picture can do something an external renderer cannot: show the **solved field** on photo-real
geometry. That is the reason a marketing picture would be made here rather than by exporting a STEP file and re-texturing
it. But a field plot is a **measurement**: its colour is read against a legend. The realistic view must not quietly
change what the colours mean.

This brief makes the default exact, adds two styles for pictures that want more drama, and makes the one that changes
colours say so on the picture.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| `fs_field`: the value → range → colour map, per fragment, **unshaded**; "the colour is the datum" | `scene.wgsl` ~261, ~315 |
| Why the value is mapped per fragment, not by blending corner colours | brief 84's note; `Em3dSurfaceField` header |
| Up to four plots at once, one uniform block each | brief 96; `U.f` |
| A Surfaces/Faces plot stands in for the object's faces (`FieldCovered`) | `Viewer3DViewState.IsDrawn` (`Scene3DFramePlan.cs` ~200) |
| Export legend and caption options | `Viewer3DViewModel.Fields.cs` ~770 |
| Forward tone mapping applies to `fs_pbr` only (106 §3e) | 106 |

## 1. `R-em3d109-1` — Exact (the default)

In the realistic view, `fs_field` is **unchanged**: no tone curve, no exposure, no lighting, no shadow, no occlusion.
Everything around the field is physically shaded; the field itself is today's pixels. A translucent material in front of a
field blends over it exactly as in the default view. **This needs no new shader code**; it needs gates proving nothing in
106–107 leaked into the field path.

## 2. `R-em3d109-2` — Lit (carries the `Lit Fields` indicator)

For a picture where the field should look like a coating on the part rather than a flat decal:

- **a.** `fs_field_lit`: the colour-map colour, decoded to linear, **plus** a specular sheen from the environment (a
  clear-coat-like lobe: F0 0.04, roughness 0.3) and the key light's highlight, then the sRGB encode. **No diffuse
  darkening, no shadow, no occlusion, no tone curve on the map colour.** A colour is only ever lightened toward a
  highlight, never darkened or shifted in hue away from one, so the legend still reads for most of the surface.
- **b.** It needs the shade stream's normal for a Faces plot. A clip-plane slice uses the plane's normal. A Surfaces plot
  uses its own triangles' normal, computed by 104's function, the only one.
- **c.** Pictures made with Lit carry the `Lit Fields` indicator (§4).

## 3. `R-em3d109-3` — Glow, and field opacity

- **a.** **Glow**: the field stays exact, and **the scene around it is dimmed**: every `fs_pbr` fragment's exposure is
  lowered by `GlowDim` (a named constant, about −2.5 EV) and the backdrop darkened by the same amount. The field appears to
  glow against a dark model. **Field colours are untouched, so Glow carries no indicator.** Bloom is deferred.
- **b.** **Field opacity** (0–100 %, default 100): a field over a material drawn through at less than 100 % lets the part's
  metal show through the colour. Below 100 the colours are blends, so the picture carries `Blended Fields` (§4).

## 4. `R-em3d109-4` — the indicator (owner decision D13, 2026-10-04)

- **a.** `Look` gains `"FieldStyle": "Exact" | "Lit" | "Glow"` (default `Exact`) and `"FieldOpacity": 100`, with schema,
  validation and `SerializeForRun` stripping as for every `Look` key.
- **b.** **Exact is the default**; the user turns Lit, Glow or a lower opacity on. The owner's reason for an indicator:
  Lit's lighting can disturb the colour gradient across surfaces, which would make a technical plot misleading to an
  engineering team reading values off it. The indicator is to be **subtle and simple**:
  - **`Lit Fields`**, when the style is Lit;
  - **`Blended Fields`**, when the opacity is below 100. This extends the Lit reasoning to blending, and the owner
    approved it with the other defaults (2026-10-04);
  - **`Lit, Blended Fields`**, when both apply.

  The label is small, in the legend's secondary text colour, set **directly under the field legend stack**. With the legend
  off, it goes in that corner by itself. No sentence, no asterisk, no footnote.
- **c.** Where it appears: the live 3D view (the same overlay that draws the legend), **every** exported picture (Export
  Picture, Copy) and `render --field … --look realistic`. It is **not tied to the legend or caption options**, and there is
  no setting that removes it. It is part of the picture whenever its condition holds, and absent otherwise.
- **d.** Exact and Glow add no label. Glow leaves field colours exact.
- **e.** glTF export (111) needs no label, because its field mesh is always unlit and exact.

## 5. `R-em3d109-5` — the showcase case: a field seen through glass

A clip-plane slice inside a substrate whose appearance has `Transmission` (fused silica, or the user's own) shows the
internal field through the material. That falls out of 106 (translucent materials write no depth) and needs no new code.
Gate it, because it is the picture this series is most likely to be judged by.

## 6. Gate

Metal offscreen (macOS), on a fixture with a Faces plot on a block and a clip-plane slice:

1. **Exact is exact.** Every field pixel equals, within 1/255, the colour the C# colour-map reference gives for that
   fragment's value. It is identical to the same pixel in the default view. It is unchanged with `Shadows`,
   `AmbientOcclusion`, `Exposure` ±3 EV and every environment.
2. **Lit only lightens.** For every field pixel, each channel is ≥ its Exact value, and hue (where saturation > 0.2) is within
   6° of Exact.
3. **Glow.** Field pixels are identical to Exact. A non-field model pixel is darker than in Exact by `GlowDim` within
   0.2 EV.
4. **Opacity.** At 50 % a field pixel over copper is the blend of the two within 2/255.
5. **The indicator.** With Lit, the live overlay and an export each carry `Lit Fields` under the legend, both with the
   legend and caption on and with both off (the label then sits alone in the legend's corner). Opacity 50 gives `Blended
   Fields`, both together give `Lit, Blended Fields`, and Exact and Glow carry none. `render --field --look realistic
   --look-set FieldStyle=Lit` carries it too. The label's text is one constant, not a literal per surface.
6. **Through glass.** A slice inside a `Transmission: 0.95` block differs from the same view with the slice hidden.
7. **Strip.** `FieldStyle`/`FieldOpacity` are absent from `SerializeForRun`.

Run only the classes touched (`--filter`).

## Decisions — settled by the owner, 2026-10-04 (each as recommended)

- **D1 Is Glow worth having?** *Recommended:* yes. It is the most striking picture, and it costs one uniform.
- **D2 `GlowDim`.** *Recommended:* −2.5 EV, tuned by eye on the shipped thermal and connector examples.

## Docs

`drawing-in-3d.md` (the realistic view section): the three styles, which ones keep the colours exact, and the `Lit Fields`
indicator. Edit doc sources only; DocGen runs at the end of the series.

## On completion

Record in `src/Ui/Viewer3D/RESOLVED.md` and `src/Render/RESOLVED.md`. Never CLAUDE.md. Do not commit unless the owner
asks.
