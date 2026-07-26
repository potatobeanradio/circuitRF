# Sonnet Brief — Phase L1a: the layout canvas — rendering, pan/zoom, grid, rulers

**Design:** `docs/design/layout-view.md` §2.2/§2.3 (layer colors and the rendering contract), §3.2 (curved
primitives), §5.3 (the four things that matter), §1 R6 (live physical readout). **Consumes all of Phase L0.**

**Scope is L1a ONLY: put geometry on screen and let the user navigate it.** No editing whatsoever — no
tools, no selection, no hit-testing, no undo. Those are L1b (drawing tools), L1c (selection + vertex
editing) and L1d (Clipper2 ops + clipboard).

This is the first phase where a layout is *visible*, which makes it the phase that finally closes the loop
on L0c/L0d: a layer color edited in the `.ctech` editor now changes what you see.

## Goal

Opening a `.clay` shows its geometry, drawn in the resolved technology's layer colors, over a grid, framed
by rulers in the document's display unit, with fluid pan and zoom. Everything §5.1 asks for at scale is L2's
problem; L1a's job is to get the **coordinate and compositing architecture right** so L2 is purely additive.

## Verified substrate (consume — already exists)

- **`SymbolEditorCanvas`** (`src/Ui/Controls/SymbolEditorCanvas.cs`) is the control template: a `Control`
  subclass with a `DirectProperty` for its VM, `_panX`/`_panY`/`_zoom` owned **by the canvas** (mirrored to
  the VM for readouts), an `ICustomDrawOperation` bridging to SkiaSharp via `Avalonia.Skia`, middle-mouse
  pan, cursor switching, and initial-fit-on-first-render. **Clone this shape.**
- **`src/Ui/Renderers/`** is where the Skia drawing code lives (`SchematicRenderer.DrawSymbol` et al).
  Layout gets its own renderer there — **do not extend `DrawSymbol`** (§0: it builds one `SKPath` per
  primitive per frame, which is right for 20 shapes and wrong for 50,000).
- **L0a**: `LayoutView`, `LayoutGeometry.BboxOf`/`Bbox`, `LayoutArc` (bulge ↔ center/radius/angles),
  `LayoutUnits.Format`.
- **L0c**: `FallbackPalette.For(LayerKey)`, and the `TechnologyChanged` → `ApplyTechResolution` seam that
  already delivers a new `Technology` to an open document.
- **L0b/L0d**: the metadata bar (extend it), and `LayoutEditorViewModel.Technology`.

## Code changes

### 1. `src/Ui/Renderers/LayoutRenderer.cs`

```csharp
public static void Draw(SKCanvas canvas, LayoutView view, Technology? tech,
                        LayoutViewport vp, LayoutRenderOptions opts);
```

#### 1.1 Coordinate convention — decide this now, not in L2

**`SKPath` is float32: a 24-bit mantissa, so ~16.7 million distinct integers.** A 300 mm board at 1 nm
resolution is 3×10⁸ DBU, well past that. Feeding raw DBU into `SKPath` therefore quantizes coordinates on
large designs, and the artefacts appear only when someone zooms in far from the origin — the worst possible
time to discover it.

**R-L1a-1. Paths are built in "path space": `(float)((dbu - origin) * dbuToUm)`, where `origin` is a single
per-frame anchor derived from the viewport centre**, quantized to a coarse step so it changes rarely.
Magnitudes are then bounded by the visible extent in micrometres — small, at every zoom. The residual
translate/scale goes on the `SKMatrix`.

**R-L1a-2. Pan and zoom are `SKMatrix` operations on the canvas — never a geometry rebuild.** Panning must
not touch a single path. This is §5.3's headline point and it is the difference between "snappy" and not.

L1a may rebuild paths each frame (designs are small and nothing is cached yet). **L2 adds caching on top of
this convention**, so the convention has to be right now; a per-tile pinned origin is the natural extension.

#### 1.2 The compositing contract (§2.3 R8a)

- **Fills are drawn per shape**, at the layer's `FillOpacity`, so same-layer overlap **darkens**. This is the
  owner's decision and it is why fills cannot be collapsed into one path.
- **Strokes batch.** They are fully opaque, so overlapping them is idempotent — accumulate a layer's outlines
  into one `SKPath` and stroke it once.
- **Strokes are hairlines: `SKPaint.StrokeWidth = 0`.** Skia renders that as exactly 1 device pixel at any
  transform, which is what §2.3 R8 wants and what stops outlines from ballooning as you zoom in.
- **One `SKPaint` per layer per frame**, reused across all its shapes. Paint churn, not draw-call count, is
  usually the real cost at this scale.
- Layers draw in ascending `ZOrder`; `Visible == false` layers are skipped entirely.

**The R8b merge tier is L2.** L1a always draws per-shape fills. Do not add a threshold yet.

#### 1.3 Layer resolution

For each shape's `LayerKey`: the `Technology`'s `LayerDef` if present; otherwise `FallbackPalette.For(key)`.

**Wire the "warn once per unknown layer per load" behaviour that L0c specified and deliberately left
unwired** (its note says "not yet wired since nothing renders layers until L1/L4" — this is that moment).
Once per layer, never once per shape, and never inside the draw loop: collect unknown keys during rendering,
and have the view model post them to Messages after the frame.

#### 1.4 Curves render natively — no flattener

**Map edge lists onto Skia's own path operations**: `Line` → `LineTo`, `Arc` → `ArcTo` (convert bulge to
centre/radius/sweep with L0a's `LayoutArc`), `Cubic` → `CubicTo`. `Circle` → `AddCircle`,
`RoundedRect` → `AddRoundRect`.

Skia tessellates adaptively at the current transform, which **is** §3.2 R9c's "rendering flattens adaptively
at screen resolution" — for free, and better than we would do by hand. **No flattener is written in this
phase**; `ToClipperPaths` and Flatten-to-Polygon arrive in L1d with the boolean ops that need them.

#### 1.5 `PathShape` — centerline to outline

A `Path` is a physical trace: its width is in DBU and **must** scale with zoom (unlike the hairline outline).
Build the centerline, then use `SKPaint.GetFillPath` with `StrokeWidth = width` and the mapped cap to obtain
a real outline path, then fill + hairline-stroke it like any other shape. Cap mapping: `Flush` → `Butt`,
`Round` → `Round`, `Square` → `Square`, `Extended` → `Butt` with the centerline extended by `width/2` at
each end.

### 2. `src/Ui/Controls/LayoutCanvas.cs`

Clone `SymbolEditorCanvas`'s structure.

- **Viewport state lives on the canvas** (`_panX`, `_panY`, `_zoom`), mirrored to the VM for readouts —
  same split as the symbol editor.
- **Pan**: middle-mouse drag always; space-drag as an alternative. **Zoom**: wheel, **anchored at the
  cursor** — the DBU point under the pointer must stay under the pointer.
- Commands on the VM, surfaced on a small toolbar and via the usual accelerators: **Zoom Fit**, **Zoom In**,
  **Zoom Out**, **Zoom 1:1** (one screen pixel per… pick a sane definition and document it — suggest 1 px
  per display-unit tick).
- **Initial fit on first render** when the layout is non-empty; keep L0b's centered placeholder text when it
  is empty.
- Left mouse does **nothing** in L1a beyond pan-modifier handling. Leave a clearly-marked seam where L1b's
  tool dispatch will go.

### 3. Grid

Dots at the snap pitch, drawn under the geometry.

**R-L1a-3. The grid decimates and never draws sub-pixel.** At 1 nm resolution the raw snap grid is
sub-pixel at every usable zoom, so: if the pitch would fall below ~8 device pixels, multiply it by
successive decade steps (1 / 2 / 5 / 10 …) until it clears the threshold; if even the coarsest sensible
step cannot, draw no grid at all. Draw a distinguishable major grid every 5 or 10 minor steps. A grid that
renders a dot per pixel is both useless and the single easiest way to make this canvas feel slow.

### 4. Rulers and the cursor readout

- Top and left rulers in the document's `DisplayUnit`, with tick spacing chosen from the 1 / 2 / 5 × 10ⁿ
  sequence so labels never collide, formatted with `LayoutUnits.Format`.
- A cursor position indicator tracking the pointer on both rulers.
- **X / Y readout in the metadata bar** in the display unit (§1 R6). Switching the display-unit combo must
  re-label rulers and readout **and move no geometry** — L0b's invariant, now visible.

## Scope guardrails (do NOT do in L1a)

- **No editing at all**: no tools, no selection, no hit-testing, no handles, no drag, no undo, no clipboard,
  no properties panel (L1b/L1c/L1d).
- **No flattener, no Clipper2, no Flatten-to-Polygon, no "Preview export flattening" toggle** (L1d).
- **No spatial index, no R-tree, no LOD/culling beyond a plain viewport-bbox reject, no tiled raster cache,
  no path caching, no R8b merge tier, no benchmark harness** — all L2. Rebuilding paths per frame is
  **expected** in this phase; do not pre-optimize against a design that has not been measured.
- No layer-visibility or lock UI (a layer panel is its own brief); the renderer *honours* `Visible` and
  `ZOrder`, but nothing edits them outside the `.ctech` editor.
- No instance/hierarchy rendering — `LayoutView.Instances` is **skipped** in L1a, with a `// L3` comment
  where it will be handled.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, or `SchematicRenderer`.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Everything draws.** A fixture `.clay` containing every shape type — including an arc-bearing `Curve`, an
   arc-bearing `Path`, a `RoundedRect` and a `Circle` — renders with each shape in its layer's color.
3. **Headless pixel tests.** Render into an `SKSurface`/`SKBitmap` off-screen and assert on pixels — this is
   how the visual contract gets a real oracle rather than a manual check:
   - **Overlap darkens (R8a).** Two overlapping rectangles **on the same layer**: the overlap region is
     strictly darker than the single-coverage region. This is the owner's §2.3 decision, pinned.
   - **Curves are curves.** A rendered `Circle` of radius r has a filled-pixel count within 2% of πr².
   - **Hairlines stay hairlines.** The same shape at 1× and at 100× zoom has an outline the same number of
     pixels thick.
   - **Fill opacity** matches the `LayerDef`'s `FillOpacity` against a known background.
4. **Fallback palette** — a layout with no technology renders every layer from `FallbackPalette`, and a
   technology missing one layer gap-fills only that layer. The unknown-layer warning is posted **once per
   layer**, not once per shape (assert the Messages count with many shapes on one unknown layer).
5. **Zoom anchors at the cursor** — the DBU coordinate under the pointer before a wheel zoom is under the
   pointer after it, within a pixel.
6. **Zoom Fit** frames the design bbox with a small margin, for both a tiny and a very large fixture.
7. **Grid decimation** — assert the chosen pitch never falls below the pixel threshold across a wide zoom
   sweep, and that the grid disappears rather than degenerating.
8. **Rulers** — a layout in mils and the same layout in µm produce correctly-labelled ticks; switching the
   display-unit combo relabels both rulers and leaves the serialized geometry byte-identical.
9. **Large-coordinate fidelity (R-L1a-1)** — a fixture with geometry near 3×10⁸ DBU renders without visible
   quantization: assert a small feature out there has the correct rendered size, which is the test that fails
   if raw DBU is fed to `SKPath`.
10. **The L0 loop closes.** With a layout open, change a layer's color in the `.ctech` editor and save: the
    canvas repaints in the new color. Assert via a pixel sample before and after — this is L0's
    "a layer color edit live-refreshes open layouts" gate, finally end-to-end.

## On completion

1. Add a "Phase L1a — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: the
   **path-space coordinate convention and why float32 forces it**, the **per-shape fill / batched-hairline-
   stroke** split and that it is what makes R8a's darkening work, that **curves use native Skia path ops with
   no flattener**, that **instances are skipped until L3**, and the test file names.
2. Report back before L1b (drawing tools, snap and angle mode, live dimension readout, and fine-grained
   `IUiCommand` undo) is briefed.
