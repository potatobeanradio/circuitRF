# Brief — 3D view, seventh series: the realistic view (appearance, lighting, glTF)

**Status:** Briefed, not built · **Date:** 2026-10-04 · **All decisions settled by the owner, 2026-10-04** (§3)
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md). This series adds a display mode and changes no physics.
**Previous briefs:** 40–53, 60–70, 71–102 (all built). This series is numbered **103–111**.
**Area:** `src/Render/Scene3D/` (shading normals, the look, the CPU mirror, glTF export), `src/Design/Layout/TechModel.cs` +
`MaterialLibraryPersistence` + `MaterialValidation` (appearance on a material), `src/Design/ThreeD/` (per-object appearance,
the `Look` block, the appearance resolver, run-input hashing), `src/Ui/Viewer3D/` (the shader, three backends, the toggle), `src/Ui/Layout/Materials*`
(appearance editing), `src/Cli/` (`render --look`, `convert … .glb`), `docs/user/src/reference/drawing-in-3d.md`
**Requirement tag:** `R-em3d<n>-<m>` as before.

---

## 0. The short answer

The owner asked whether circuitRF should have an optional, photo-realistic way to view a `.c3d`: metals that look like
metal and dielectrics that look like glass, ceramic or laminate. It would be used after modelling is finished, to make a
picture for technical marketing. It must never interfere with modelling or simulation, and a toolbar button turns it on
(default off).

**The answer is yes, within limits.** Build a real-time physically based **realistic view** on the GPU path the 3D view
already has, plus a **glTF export** so anyone who wants a full ray-traced image can get one from any path tracer. **No ray
tracer is built into circuitRF in this series** (§4).

When the series is done:

- **A toolbar toggle, Realistic view** (off by default), redraws the 3D view with physically based materials lit by a studio
  environment. Metals reflect it, dielectrics take a body colour, gloss and translucency, and there are soft shadows and
  contact shading. The CAD overlays (edges, grid, ports, the air box) step aside. Selection and hover still work, because it
  is still the editor.
- **A material carries an optional appearance** (`.cmat`/`.ctech`): base colour, metallic, roughness, transmission, IOR and
  clear-coat. An object in a `.c3d` may override it. Neither ever reaches a solver.
- **A material with no appearance still looks right**, because the defaults are derived from its role (conductor,
  dielectric, via, wire). The built-in library ships true reflectances for its metals.
- **Edits are live.** Dragging a roughness slider changes one uniform buffer. Nothing is re-tessellated or re-elaborated,
  and no solve goes out of date.
- **The scene's look** (environment, its rotation, exposure, background, shadows) is saved in the `.c3d` and is stripped from
  what a run sees, as `Hidden` and `Transparency` are.
- **Field plots stay data.** In the realistic view a field's colour is still exactly its colour map's colour. A *Glow* style
  dims the model around an exact field. A *Lit* style changes the colours, so the view and every
  picture made that way carry a small `Lit Fields` indicator (and `Blended Fields` for an opacity below 100 %).
- **Export Picture** works in the realistic view (supersampled, with an optional transparent background), and
  **`render x.c3d --look realistic -o shot.png`** draws the same picture with no window.
- **File ▸ Export ▸ glTF…** and `convert x.c3d -o x.glb` write the model with its appearances, smooth normals and names, and
  optionally the field plot as unlit vertex colours.

### The three rules this series adds

> **1. Appearance never reaches a solver.** Every appearance value, on a material or an object, and the whole `Look` block,
> is classified as display: excluded from the solver-equality comparison (`MaterialLibraries.cs` ~300–306), stripped by
> `C3dPersistence.SerializeForRun` (~177–195), and so invisible to brief 87's manifest and brief 98's staleness. Changing
> roughness never marks a solve out of date.
>
> **2. A field's colour is a datum.** The shader already says so ("the colour is the datum", `scene.wgsl`). The realistic
> view keeps it: no tone curve, no lighting and no exposure touches a field fragment unless the user turns on the Lit
> style, and then the view and every picture say so with a small `Lit Fields` indicator.
>
> **3. One appearance resolver, two renderers, one exporter.** The question *what does this object look like* is answered in
> exactly one place (`AppearanceResolver`, brief 105). The GPU view, the CPU mirror that `render` uses and the glTF writer
> all consume its answer, and none of them re-derives a default.

---

## 1. Things that are not obvious, resolved here once

**a. Why metals look dull today.** `fs_color` (`scene.wgsl` ~149) shades with a headlight: `rgb · (0.3 + 0.7·|n·v|)`. A metal
has no diffuse colour; it is recognised **only** by what it reflects. With nothing to reflect, gold reads as brown plastic
whatever its parameters. The most important single ingredient is therefore **image-based lighting**: an environment the
metals reflect and the dielectrics are lit by. Specular highlights and shadows matter less.

**b. Facets.** The normal is taken per pixel from screen-space derivatives (`cross(dpdx, dpdy)`), so every triangle is flat.
`Scene3DVertex` carries no normal. A glossy material on a 32-facet cylinder makes the facets obvious. **Smooth shading
normals are the prerequisite (brief 104).** The builder already emits one scene vertex per (mesh vertex, face)
(`Scene3DBuilder.cs` ~1010), so a per-face smooth normal falls out of the existing split: curved faces become smooth and
edges between faces stay sharp.

**c. Optical index is not √εr.** Microwave permittivity says nothing about optical refraction. Alumina has εr 9.8 (which would
give n ≈ 3.1) but an optical n of about 1.76. FR-4 has εr 4.4 (n ≈ 2.1) but an optical n of about 1.5. **No appearance value is
ever derived from a physical one.** The only link is the fallback of `BaseColor` to the material's existing display `Color`.

**d. Tone mapping is forward (per fragment), not a post pass.** With a post pass, the field fragments go through the curve
too, and keeping rule 2 would need an inverse curve or a mask. Applying the curve at the end of the PBR fragment shader keeps
the field shader untouched. It also means no HDR render target and no extra pass, and the CPU mirror shades one fragment at
a time anyway. The cost is that translucent highlights blend after the curve, which is invisible at this use.

**e. The live view stays on-demand.** The pane draws only when something changed (`Viewer3DPane.RequestFrame`). The realistic
view must keep that: no temporal accumulation, no animated noise, and nothing that needs continuous frames (the idle-power
work). Quality that needs many samples (supersampling, larger shadow kernels) belongs to **Export**, which draws once.

**f. The shared swap image's alpha stays 1** (brief 27's rule in `Viewer3DBackend.cs`). A transparent background is
therefore an **export** option only, drawn offscreen.

**g. Headless parity is not optional here.** The repo's rule is that what the GUI can make, the CLI can make. `render`
already draws `.c3d` pictures through a software depth buffer that mirrors the shader (`Em3dSurfaceField`, brief 89).
Brief 110 extends that mirror. It does not start a second renderer.

**h. glTF is the schema, not just an export format.** glTF 2.0's metallic-roughness model is an open standard and every
renderer reads it. Using its parameter set (with the transmission, IOR, clear-coat and volume extensions) as the appearance
schema makes the export lossless, and makes "render it elsewhere with full ray tracing" a file save rather than a
translation.

---

## 2. The briefs

| # | Title | Depends on | What it delivers |
|---|---|---|---|
| 104 | Smooth shading normals | — | A realistic-only vertex stream: per-face smooth normal + appearance slot |
| 105 | The appearance model | — | `Appearance` on `TechMaterial` and on a `.c3d` object; role defaults; `AppearanceResolver`; display-only everywhere |
| 106 | The realistic view | 104, 105 | Toolbar toggle; PBR shader; studio environment; forward tone curve; the `Look` block; what hides |
| 107 | Shadows, contact shading, ground, export | 106 | Key-light shadow map; screen-space ambient occlusion; shadow-catcher ground; Export Picture in realistic mode |
| 108 | Editing appearance, live | 105, 106 | Appearance section in the Materials editor and the Inspector; a Look panel; counters prove nothing re-tessellates |
| 109 | Field plots in the realistic view | 106 | Exact by default; optional Lit and Glow styles; the `Lit Fields` indicator |
| 110 | Headless: `render --look realistic` | 106, 107 | The CPU mirror's PBR shade; a GPU-vs-CPU gate on macOS |
| 111 | glTF export | 104, 105 | `File ▸ Export ▸ glTF…`, `convert … .glb`, optional unlit field colours |

**111 does not need the renderer.** It can be built right after 105, and it is worth doing early: it gives a ray-traced
picture before the realistic view exists.

---

## 3. Decisions — all settled by the owner, 2026-10-04 (the remaining ones as recommended)

| # | Decision | Brief's default | Blocks |
|---|---|---|---|
| D1 | Scope | **A real-time PBR raster view + glTF export.** No built-in ray tracer in this series (§4). **Confirmed by the owner, 2026-10-04.** | all |
| D2 | Appearance schema | **glTF 2.0 metallic-roughness**: `BaseColor`, `Metallic`, `Roughness`, `Transmission`, `Ior`, `Clearcoat`, `ClearcoatRoughness`, `AttenuationColor`, `AttenuationDistance`. No textures. **Confirmed by the owner, 2026-10-04.** | 105 |
| D3 | Where appearance lives | **On the material** (`.cmat`/`.ctech`), with an **optional per-object override** in the `.c3d` (plating is usually not modelled: a copper trace that should look gold). **Confirmed by the owner, 2026-10-04.** | 105 |
| D4 | Where the scene look lives | **A `Look` block in the `.c3d`**: document state like `Hidden`, so a picture reproduces and `render` reads it. Stripped for runs. **Confirmed by the owner, 2026-10-04.** | 106 |
| D5 | Is the toggle itself saved? | **No.** It is view state like the camera, off on every open. `render` takes `--look realistic`. **Confirmed by the owner, 2026-10-04.** **Amended the same day:** still never in the `.c3d`, but a per-user setting (Settings ▸ General ▸ 3D Designs ▸ *Open in realistic view*, `AppPreferences.View3DRealisticOnOpen`, default off) opens every design with it on — what a machine can afford to draw is a property of its GPU. `render` does not read it. | 106, 110 |
| D6 | Keyboard shortcut | **None** in this series; toolbar and the 3D ▸ View menu only. **Confirmed by the owner, 2026-10-04.** | 106 |
| D7 | Tone curve | **The Khronos PBR Neutral curve, applied forward** (§1d). It keeps base colours faithful, which product pictures need. Alternative: an ACES-style filmic fit (more contrast, shifts hues). **Confirmed by the owner, 2026-10-04.** | 106 |
| D8 | Environments | **Procedural studios generated in C#** (two or three: soft studio, high-key white, dark product). No image assets, no licence question, deterministic. A user `.hdr` (Radiance RGBE) file is accepted; `.exr` is not. **Confirmed by the owner, 2026-10-04.** | 106 |
| D9 | Smooth normals in the default view too? | **No, realistic only, in this series.** The default view and every DocGen figure stay byte-identical. Revisit later. **Confirmed by the owner, 2026-10-04.** | 104 |
| D10 | Ambient occlusion | **Screen-space, from the depth buffer**, applied to environment light only. Not baked per vertex: a box face is two triangles, so a vertex can't hold a contact shadow. **Confirmed by the owner, 2026-10-04.** | 107 |
| D11 | What the realistic view hides | **Edges, grid, mesh/FDTD/section overlays, air box, ports, boundaries, face tints and reference images** are hidden by default, and **each one can be turned back on** by its own `Look` option (`Show…`). One shown is drawn exactly as the default view draws it. **Settled by the owner, 2026-10-04**: every hidden item gets an option, not only ports and images | 106, 108, 110 |
| D12 | Per-object `Transparency` (brief 92) in realistic mode | An **explicitly stated** value still applies (multiplies coverage); the **kind default** (dielectrics translucent) is replaced by the appearance's `Transmission`. **Confirmed by the owner, 2026-10-04.** | 106 |
| D13 | Fields | **Exact by default** (rule 2), and the user can turn the other styles on. *Glow* (the model dimmed, the field exact) is unmarked. *Lit* (a specular sheen over the colour) carries a **subtle, simple indicator, `Lit Fields`**, in the view and in every picture, because its lighting can disturb the colour gradient across surfaces and mislead an engineering team reading the plot. Field opacity below 100 % carries `Blended Fields` in the same style. **Confirmed by the owner, 2026-10-04.** | 109 |
| D14 | Field colours in glTF | **Opt-in**, as a separate mesh with `COLOR_0` and an unlit material (`KHR_materials_unlit`), so another renderer keeps rule 2 too. **Confirmed by the owner, 2026-10-04.** | 111 |
| D15 | Live-view accumulation | **None** (§1e). Export supersamples instead. **Confirmed by the owner, 2026-10-04.** | 106, 107 |
| D16 | Appearance slots | **256 distinct appearances per scene** (one uniform table; Vulkan guarantees 16 KB of uniform range). The 257th draws with its role default and the status line says how many fell back. **Confirmed by the owner, 2026-10-04.** | 104, 106 |
| D17 | A saved camera for pictures | **An opt-in `Look.Camera`**, written only by *Use This View for Pictures*, never by orbiting. It is a deliberate exception to "the camera is not document state", so a GUI framing reproduces in `render` and in glTF. **Confirmed by the owner, 2026-10-04.** | 108, 110, 111 |
| D18 | Results staleness on a display edit | **Fixed for material libraries and technologies**: brief 87's manifest hashes a `.cmat`/`.ctech` in a canonical physics form (display fields cleared), so editing a colour or an appearance no longer marks a run stale. Today it does, even for `Color`. **Confirmed by the owner, 2026-10-04.** | 105 |

---

## 4. What is deferred, and why

- **A built-in path tracer.** Live hardware ray tracing is out: the D3D11 backend has none, and Metal and Vulkan expose it
  differently, so it would mean three implementations of the hardest part. An export-only CPU path tracer in `src/Render` is
  possible, but glTF export (111) already gets the user a path-traced picture at no maintenance cost. **Revisit only if 111
  proves insufficient**, and brief it then.
- **Textures**: normal maps, roughness maps, brushed-metal anisotropy, laminate weave. The schema has room (D2 follows glTF),
  but nothing in a `.c3d` carries UVs except reference images.
- **Real refraction, caustics, screen-space reflections, bloom and depth of field.** Translucency is alpha plus Fresnel
  reflection. Depth of field for export is the most likely to come back.
- **Turntable or video export.**
- **The default view's own shading** (D9).

---

## 5. Order, and where each lands

104 and 105 can run in parallel. 111 can follow 105 at once. 106 needs both, and 107–110 follow 106. The series' gate is the
union of its briefs' gates. **No brief in this series needs a solve.** Each runs only its own test classes (`--filter`), plus
`Firewall.Tests` with `--no-build`. DocGen runs once, at the end of the series.

## On completion

Each brief records its findings in the `RESOLVED.md` beside the code it touched (`src/Render/RESOLVED.md`,
`src/Design/RESOLVED.md`, `src/Ui/Viewer3D/RESOLVED.md`, `src/Cli/RESOLVED.md`). Never CLAUDE.md. Do not commit unless the
owner asks.
