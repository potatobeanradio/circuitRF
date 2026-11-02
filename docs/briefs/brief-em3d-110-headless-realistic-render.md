# Brief 110 — Headless: `render x.c3d --look realistic`

**Tag:** `R-em3d110-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** `src/Cli/RenderEm3d.cs`, `RenderEm3dField.cs`, `src/Cli/Render.cs` (argument parsing),
new `src/Render/Scene3D/Look/RealisticPicture.cs`, `src/Render/Renderers/Em3dSurfaceField.cs` (factor out its rasteriser),
`src/Cli/ExplainEm3d.cs`, `docs/design/cli.md` (§13.8), `tests/Ui.Tests/Render/` and `tests/Ui.Tests/Viewer3D/`
**Depends on:** 106, 107 (109 for fields) · **Blocks:** —

## Why

The repo's rule is that what the GUI can make, the CLI can make. A realistic picture is exactly the kind of thing to make
in a batch: every variant of a package, for a datasheet, with no window. `src/Cli` cannot reach the GPU backends (they are
in `src/Ui`, above the firewall). `render` already draws `.c3d` pictures through a **software depth buffer that mirrors the
shader** (`Em3dSurfaceField`, brief 89). This brief extends that mirror to the realistic shading. It does not start a
second renderer: every shading function it calls is 106's `Pbr.cs`, which is already the GPU's reference.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| `render x.c3d -o out.png` with `--field`, `--iso`, `--view-dir`, `--region`, `--no-mirror`, `--labels`, `--axes`, `--transparent`, width/height/scale | `RenderEm3d.Request`, `RenderEm3dField.cs` |
| `--transparency name=percent` applied to a **copy** of the document | brief 92 D5; `Request.Transparency` |
| A software depth buffer, orthographic, shading `0.3 + 0.7|n·v|` "as the view's shader shades it" | `Em3dSurfaceField.cs` header |
| The 3D view's field surfaces, shared with the GUI | `src/Render/Scene3D/Fields/FieldSurfacePlot.cs` |
| Progress, cancellation (exit 130, writes nothing) | `RunHost`'s `RunControl`, as `em` and `render` use |
| The real Metal backend can render offscreen in a test (macOS) | `Viewer3DFrameGateTests.Metal_DrawsTheScene_…` ~282 |
| The C# reference shading, tone curve, prefilter, downsample, un-premultiply | 106 §3g, §4; 107 §5 |

## 1. `R-em3d110-1` — the verb

- **a.** `render x.c3d -o shot.png --look realistic`. PNG only, as for brief 89. `--look plain` (the default) is today's
  picture, unchanged byte for byte.
- **b.** The `.c3d`'s own `Look` block is used as written. `--look-set Key=value` (repeatable) overrides one key on the
  **copy** the run reads, through the same validation the file goes through, as `--transparency` does. **There is no flag
  per Look key**: the format is the contract, and a new Look key needs no new flag.
- **c.** `--supersample 1|2|4` (default 2, 107 §5a). `--transparent` (existing) means the transparent export (107 §5b).
- **d.** Direction: `--iso` and `--view-dir` as brief 89 (orthographic). If the `.c3d`'s `Look` holds a camera (overview D17, written
  by 108 §3e), that camera is the default, perspective included, and `--iso`/`--view-dir` override it.
- **e.** `--field <plot>` composes with `--look realistic` and draws 109's styles from the Look (`--look-set
  FieldStyle=Glow`).
- **f.** Refusals, in the verb's existing sentence style: `--look realistic` on a `.cem` (no appearances), or with an SVG or
  PDF output ("a realistic picture is pixels; use .png").
- **g.** `explain x.c3d --look` prints the resolved Look, the environment's source (preset, or the `.hdr` path and whether
  it was found), and the appearance table with each slot's provenance (105 §3a).

## 2. `R-em3d110-2` — the CPU picture

`RealisticPicture` in `src/Render/Scene3D/Look/`:

- **a.** Rasterises the scene's triangles (with 104's shade stream) through the software depth buffer, **factored out of
  `Em3dSurfaceField`** into one rasteriser both use. Do not make a second copy. Add perspective projection to it, which
  `Look.Camera` needs (overview D17).
- **b.** Shades each sample with `Pbr.cs`: the **same** functions, constants, prefiltered environment arrays (106 §4b) and
  tone curve the GPU path is checked against. Shadows come from a light-space depth buffer rendered by the same rasteriser
  (107 §1). Occlusion is the same horizon algorithm over the CPU depth buffer (107 §2). Ground, supersampling and
  un-premultiplying are 107's functions.
- **c.** Fields: `FieldSurfacePlot`'s triangles mapped **per sample** (brief 84's rule), Exact/Lit/Glow as 109.
- **d.** Deterministic: rows (or tiles) in parallel, each pixel computed independently, no reduction whose order depends on
  scheduling. The same command twice writes the same bytes.
- **e.** Progress per tile band and cancellation through `RunControl`. A cancelled render writes nothing (exit 130).

## 3. `R-em3d110-3` — the GPU and CPU must agree

The only way the two paths can drift is the WGSL and `Pbr.cs` disagreeing. 106 §3g already scans that their constants
match. This brief adds the picture-level check:

- **a.** A fixture: a row of spheres (Copper, Gold, Silver, Aluminium, a rough dielectric, a clear-coated dielectric, a
  glass one), a box on a plate (shadow, occlusion, ground), and a field plane in Exact and Lit. Drawn by the **Metal backend
  offscreen** and by `RealisticPicture`, at the same size and camera.
- **b.** Compared per pixel, after excluding a one-pixel band at depth discontinuities (the two rasterisers' coverage rules
  differ at edges): **at least 99 % of pixels within 4/255 per channel; field pixels within 1/255.**
- **c.** macOS only; elsewhere the test is **skipped with a reason**, not passed.

## 4. Gate

1. **Plain unchanged.** `render x.c3d -o a.png` with no `--look` is byte-identical to before the brief (run it on a fixture,
   and compare against a hash recorded by running the verb at the start of the work, in the same test file's setup).
2. **Look from the file.** Changing the `.c3d`'s `Exposure` changes the picture. `--look-set Exposure=…` matches editing the
   file, byte for byte.
3. **Refusals.** SVG output, a `.cem` input, and an out-of-range `--look-set` each refuse with their sentence and exit 1.
4. **Determinism.** Same command twice → same bytes, and on a different thread count (`DOTNET_PROCESSOR_COUNT` or the
   rasteriser's own knob).
5. **GPU = CPU** (§3, macOS).
6. **Cancellation.** A cancelled render exits 130 and the output path does not exist.
7. **As a process.** The verb run as a process against the in-process call, byte for byte, as `RenderCliVerbTests` does
   for 2D pictures.
8. **`explain --look`** lists every slot with provenance, and names a missing `.hdr`.

No new timing tests. The rasteriser's work is held by counters (samples shaded, shadow texels written).

## Decisions — settled by the owner, 2026-10-04 (each as recommended)

- **D1 A saved camera in the `Look`: settled by the owner, 2026-10-04 (overview D17).** `Look.Camera` is an opt-in
  exception to the `.c3d`'s camera rule, written only by 108 §3e's button. This brief reads it, so add a gate: a `.c3d`
  holding a perspective `Look.Camera` renders from that camera, and the GPU-vs-CPU agreement (§3) includes one perspective
  view.
- **D2 `--look-set` versus one flag per key.** *Recommended:* `--look-set`, per §1b.

## Docs

`docs/design/cli.md` §13.8: the `--look` paragraph, `--look-set`, `--supersample`, and the GPU/CPU agreement rule. The user
reference's `render` page gets the same, edited as source only. DocGen runs at the end of the series.

## On completion

Record in `src/Cli/RESOLVED.md` and `src/Render/RESOLVED.md` (the shared rasteriser, the tolerance and why the edge band
is excluded). Never CLAUDE.md. Do not commit unless the owner asks.
