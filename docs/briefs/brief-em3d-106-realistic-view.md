# Brief 106 — The realistic view: PBR, a studio environment, a tone curve, a toggle

**Tag:** `R-em3d106-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** `src/Ui/Viewer3D/Shaders/scene.wgsl` (+ regenerated `.metal`/`.hlsl`/`.spv` via `tools/ShaderGen`), the three
backends (`Metal/`, `D3D11/`, `Vulkan/`), `Viewer3DBackend.cs`, `Viewer3DViewModel.cs`; `src/Render/Scene3D/Scene3DFramePlan.cs`,
new `src/Render/Scene3D/Look/` (`StudioEnvironment.cs`, `EnvironmentPrefilter.cs`, `RadianceHdr.cs`, `ToneCurve.cs`,
`Pbr.cs`); `src/Design/ThreeD/C3dDocument.cs` + `C3dPersistence.cs` (the `Look` block); `src/Ui/Views/ThreeD/C3dEditorView.axaml`,
`src/Ui/Views/WorkspaceWindow.axaml` (both 3D ▸ View menus); `src/Cli/DocumentSchema.cs`; `docs/user/src/reference/drawing-in-3d.md`
**Depends on:** 104 (normals + slot stream), 105 (appearances) · **Blocks:** 107, 108, 109, 110

## Why

This is the brief the series exists for. A toolbar toggle, **Realistic view**, off by default, redraws the 3D view with
physically based materials lit by an environment. Metals reflect, dielectrics have body, gloss and translucency, and the
CAD chrome steps aside. It is a **view**: it changes no document state, and modelling, picking and selection keep working
underneath it.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| One WGSL source → Metal/HLSL/SPIR-V offline; `Viewer3DFrameGateTests.Gate1b` fails when a generated file is stale | `scene.wgsl` header; `tools/ShaderGen/README.md` |
| Today's shading: headlight Lambert on derivative normals | `scene.wgsl` `fs_color` ~149 |
| Uniform block `U` (binding 0, 2,016 bytes), per-draw `MX` (binding 1; "not an immediate": naga writes those as `ConstantBuffer<T>`, which vs_5_0 refuses) | `scene.wgsl` ~25–85 |
| Pipelines: Opaque, Translucent, Lines, Pick, Field, Edges, OnTop, Grid, Image, ImageTranslucent | `Scene3DFramePlan.cs` ~21 |
| The live image is **BGRA8 unorm, not sRGB** (Metal `FmtBGRA8 = 80`), and its **alpha must stay 1** | `MetalViewer3DBackend.cs` ~34; `Viewer3DBackend.cs` header |
| 2D texture upload exists (brief 101, RGBA8) | `_textures.Sync(scene, UploadTexture, ReleaseTexture)`, `MetalViewer3DBackend.cs` ~252 |
| The view's background colour | `Viewer3DViewState.Background` (`Scene3DFramePlan.cs` ~178) |
| Frames are drawn on demand only | `Viewer3DPane.RequestFrame` |
| `AirBoxHidden`: a `.c3d` document-level display flag, saved and undoable | `C3dDocument.cs` ~758–762 |
| The toolbar's projection pair (the toggle goes after it) | `C3dEditorView.axaml` ~103–108 |
| Display-property stripping for runs | `C3dPersistence.SerializeForRun` ~177–195 |

## 1. `R-em3d106-1` — the toggle

- **a.** `Viewer3DViewModel.IsRealistic`: view state, **never saved, off on every open** (overview D5). Toggling it
  marks nothing dirty.
- **b.** A `ToggleButton` after the Perspective/Orthographic pair in `C3dEditorView.axaml` (every host of the 3D view,
  read-only included). Tooltip: `Realistic view: materials, lighting and shadows, for pictures. Changes nothing in the
  model.` Glyph: **owner decision D1**.
- **c.** 3D ▸ View ▸ **Realistic View** (a check item) on **both** hand-mirrored menus (the in-window `Menu` and the macOS
  `NativeMenu` ~201). No key equivalent (overview D6). The menu-mirror test must still pass.
- **d.** Status line, while on: `Realistic · <environment> · <exposure> EV`, plus `· n objects use a default look (more than
  256 appearances)` when `Scene3DModel.AppearanceFallbacks > 0` (105 §4).
- **e.** Turning it on uploads the shade stream (104 §3) and the environment's textures (§4) **once**. It re-tessellates
  nothing and re-elaborates nothing. Turning it off releases both.

## 2. `R-em3d106-2` — what is drawn, and what steps aside

`Scene3DFramePlan.Plan` takes the view's realistic flag and, when set:

- **a.** Draws every visible object with triangles through two new pipelines, **`Pbr`** (opaque: depth write, no blend) and
  **`PbrTranslucent`** (sorted with today's translucent objects: depth test without write, blend). Both read the shade
  stream as a second vertex buffer.
- **b.** **Hides by default, each with its own `Look` option to show it again** (overview D11, settled by the owner
  2026-10-04):

  | Hidden by default | `Look` key that shows it |
  |---|---|
  | the general feature-edge lines (`SceneLines`) | `ShowEdges` |
  | the drawing grid | `ShowGrid` |
  | mesh, FDTD-grid and section overlays | `ShowOverlays` |
  | the air box | `ShowAirBox` |
  | ports | `ShowPorts` |
  | boundaries and face tints | `ShowBoundaries` |
  | reference images | `ShowImages` |

  A shown item is drawn **exactly as the default view draws it**: its existing pipeline and colours, no appearance, no
  lighting, no tone curve. These items are CAD chrome and data, not materials. A `Show…` option only lifts the realistic
  view's own suppression. It never overrides the default view's own state, so an air box hidden by `AirBoxHidden`, an overlay
  switched off, or a hidden object stays hidden. One table in `Scene3DFramePlan` maps each key to the draws it governs, so
  adding a kind of chrome later means one row, and the gate iterates the table.
- **c.** **Keeps** editing feedback: hover tint, Face-mode fill, and the selected object's outline (`Edges`/`OnTop`). It is
  still the editor. The **export** plan (`CapturePicture`, `pick: false`, cursor parked) draws none of them, as it already
  parks hover.
- **d.** The section clip plane is honoured. A cut solid's cap (flag 2, back faces) is shaded as its own appearance with the
  normal facing the viewer, rather than flat.
- **e.** Per-object `Transparency` (brief 92), overview D12: a **stated** value multiplies the fragment's coverage; the
  **kind default** (dielectrics translucent) does not apply, because the appearance's `Transmission` decides.
- **f.** Field draws are unchanged (rule 2; brief 109 adds the opt-in styles).

## 3. `R-em3d106-3` — the shader

New entry points in `scene.wgsl`, beside the existing ones; **no existing entry point changes**:

- **a.** `vs_pbr`: `vs`'s work, plus the normal rotated by `MX.m` (104 §1f) and the slot passed flat.
- **b.** The appearance table: a third uniform binding, `array<AP, 256>`, three `vec4f` per entry (base colour + metallic;
  roughness, transmission, IOR, clear-coat; clear-coat roughness + attenuation colour), **12,288 bytes**. 256 is overview
  D16; the reason is Vulkan's guaranteed minimum `maxUniformBufferRange` of 16 KB. Record the backend binding slots beside
  `MX`'s note at the top of the file.
- **c.** `fs_pbr`, in **linear** space:
  - base colour decoded from sRGB;
  - a Lambert diffuse term for the non-metal part;
  - a microfacet specular term: Trowbridge-Reitz (GGX) distribution, height-correlated Smith visibility, Schlick Fresnel with
    F0 = 0.04 (dielectric, or from `Ior` when stated) blended to the base colour by `Metallic`;
  - a clear-coat lobe (F0 0.04, its own roughness) layered over it, energy taken from the base;
  - **image-based lighting**: diffuse from 9 spherical-harmonic irradiance coefficients (uniforms), specular from the
    prefiltered environment at the roughness level, through the split-sum BRDF lookup table (§4);
  - the key light's direct contribution (shadowed in 107);
  - two-sided: a back face (`front_facing` false) flips the normal, for sheets (104 §1g).
- **d.** **Transmission (a raster approximation, and the brief says so):** coverage alpha = `1 − Transmission·(1 − F)`.
  The reflected light is added on top, and what shows through is tinted by `AttenuationColor`. True refraction and the
  distance term are left to glTF export (111) and the deferred path tracer.
- **e.** **Forward tone mapping** (overview §1d, D7): exposure (`2^EV`), then the tone curve, then the sRGB encode,
  **all at the end of `fs_pbr`**, because the target is UNORM, not sRGB. `highlight()` (hover and selection) is applied after
  the encode, so its colours are today's.
- **f.** The background: a third fragment entry `fs_backdrop`, drawn first as a full-screen pass like `vs_grid`, for the
  `environment` and `gradient` backgrounds (§5). A solid or theme background is the existing clear.
- **g.** The same functions (BRDF, SH evaluation, tone curve, sRGB encode) are written in C# in `src/Render/Scene3D/Look/Pbr.cs`
  and `ToneCurve.cs`. That C# is the **reference** for 110's CPU mirror and for this brief's gates. Every constant (F0 0.04,
  the tone curve's parameters, the sRGB breakpoints) is written once in C# and once in WGSL, and a source scan asserts the
  two match.
- Regenerate with `tools/ShaderGen`. `Gate1b` must pass. The HLSL must still compile as vs_5_0/ps_5_0: no immediates, and no
  storage buffers unless the D3D11 backend already binds one.

## 4. `R-em3d106-4` — the environment

- **a.** **Procedural studios** (overview D8), generated in C# by `StudioEnvironment`: a soft vertical gradient (ceiling,
  horizon, floor) plus two or three rectangular area lights ("softboxes"), one of them the **key light**, whose direction
  107 shadows from. Three presets: **Studio** (default, soft), **High key** (white, low contrast) and **Dark** (product
  shot: a black room, strong rim lights). Each is a small parameter record; a preset is data, not code.
- **b.** `EnvironmentPrefilter` turns an environment into what the shader samples, **on the CPU, deterministically**:
  - an **octahedral-mapped 2D atlas** of radiance at five roughness levels (0, 0.25, 0.5, 0.75, 1), level 0 at 256²,
    GGX-prefiltered by importance sampling with a **fixed** sample sequence. 2D only, because only 2D texture upload exists
    on all three backends (brief 101); no cube maps;
  - SH9 irradiance coefficients;
  - a 32 × 32 split-sum BRDF table.

  Results are cached per environment. **Rotation is applied in the shader** by rotating the lookup direction, so dragging
  the rotation slider recomputes nothing.
- **c.** Texture format: half-float RGBA (`RGBA16F`), which needs a format argument on the texture upload path (it is
  RGBA8 today). If a backend cannot take it, encode RGBE into RGBA8 and decode in the shader. Record which was done.
- **d.** A **user environment**: a Radiance `.hdr` (RGBE) file, read by `RadianceHdr` (a small reader, no new
  dependency), resampled from equirectangular into the same atlas and prefiltered the same way. `.exr` is not read
  (overview D8). The path is stored relative to the `.c3d`. A missing or unreadable file is a **warning**: the view falls
  back to Studio and says so on the status line, and `check` reports it.

## 5. `R-em3d106-5` — the `Look` block

A document-level, display-only block in the `.c3d`, saved and undoable like `AirBoxHidden` (overview D4):

```json
"Look": {
  "Environment": "Studio",          // Studio | HighKey | Dark | a relative path to a .hdr
  "Rotation": 30,                    // degrees about +z
  "Intensity": 1.0,
  "Exposure": 0.0,                   // EV
  "Background": "Theme",            // Theme | #rrggbb | "#rrggbb,#rrggbb" (a vertical gradient) | Environment
  "ShowEdges": false,
  "ShowGrid": false,
  "ShowOverlays": false,
  "ShowAirBox": false,
  "ShowPorts": false,
  "ShowBoundaries": false,
  "ShowImages": false
}
```

107 adds `Shadows`, `AmbientOcclusion` and `Ground`. 108 adds `Camera` (overview D17). 109 adds `FieldStyle` and `FieldOpacity`. Omitted, or any key omitted, means the defaults
shown. Ranges are refused by validation (`Exposure` −10 to +10, `Intensity` 0 to 10, `Rotation` taken mod 360).
`SerializeForRun` clears the block (rule 1). `DocumentSchema` lists it. The values in the JSON above are this brief's
defaults; the comments are for the brief only, since the file format has none.

**Editing the Look** is brief 108's panel. Until it lands, the block is edited by writing the file, which is the format
contract.

## 6. Gate

1. **Plan.** With realistic on, a fixture scene's plan has `Pbr`/`PbrTranslucent` draws for every visible solid and sheet;
   none of the §2b table's draws; and the selected object's `Edges` present. Then, **for each row of that table**, setting
   its key brings back exactly that row's draws, identical to the default view's draws of the same items, and nothing else.
   With `ShowAirBox` true and `AirBoxHidden` true, the air box stays hidden. The export plan has no hover or selection draws.
2. **Not document state.** Toggling marks nothing dirty and is not saved. The `Look` block round-trips byte-identical, is
   undoable, and is absent from `SerializeForRun`'s output.
3. **Reference math** (`Pbr.cs`, no GPU): the BRDF table at (N·V = 1, roughness 0) is (≈1, ≈0) within 1e-3. White-furnace
   test: a uniform white environment's SH9 irradiance is constant over directions within 1e-4, and a rough white dielectric
   reflects ≤ 1 in total. The tone curve is monotonic on [0, 64] and maps 0 → 0. The sRGB encode matches the standard at its
   breakpoints.
4. **Determinism.** The Studio prefilter produces identical bytes twice and on a second thread.
5. **Constants agree.** The source scan of §3g.
6. **Metal offscreen** (macOS, the existing pattern in `Viewer3DFrameGateTests`):
   - a mirror sphere (metallic 1, roughness 0) facing the camera returns, at its centre pixel, the environment colour from
     directly behind the camera, matching `Pbr.cs`'s CPU evaluation within 3/255 per channel;
   - a rough dielectric shows no specular peak;
   - the background is the theme colour;
   - every alpha is 255 (the swap-image rule).
7. **Counters.** Toggling on and off three times: `Scene3DBuilder.Tessellations` unchanged, no elaboration, the shade stream
   and environment uploaded once per *on*, and a rotation drag uploads only uniforms.
8. **Menus and toolbar.** The menu-mirror test passes, and a source scan holds the toggle's place after Orthographic.
9. **D3D11 and Vulkan**: generated-shader currency only (Gate1b). **Their runtime is unverified on this machine**, and the
   completion note must say so (as briefs 62 and 101 did).

Run only the classes touched (`--filter`), plus `Firewall.Tests --no-build`.

## Decisions — settled by the owner, 2026-10-04 (each as recommended)

- **D1 Glyph.** First check the Material.Icons assembly for a suitable kind (a lit sphere or a camera aperture), as brief
  102 D5 did. *Recommended* if none reads well: a drawn `Viewer3DPathGlyph.Realistic` (a sphere outline with a highlight
  crescent), shown to the owner at 16/32/64 px before it is wired.
- **D2 Default environment.** *Recommended:* Studio. *Alternative:* High key, which suits a white-background marketing
  page.
- **D3 Available in the read-only viewer too?** *Recommended:* yes. It is the same view model, and a results viewer is
  where pictures with fields get made.

## Docs

`drawing-in-3d.md`: a "Realistic view" section: the toggle, what steps aside, the Look keys, environments (and the `.hdr`
option), and that nothing in it changes the model or a result. Edit doc sources only; DocGen runs at the end of the series.

## On completion

Record in `src/Ui/Viewer3D/RESOLVED.md`: binding slots per backend, the texture format chosen, and which backends were
run. Record in `src/Render/RESOLVED.md`: the prefilter's sample sequence and sizes. Never CLAUDE.md. Do not commit unless the
owner asks.
