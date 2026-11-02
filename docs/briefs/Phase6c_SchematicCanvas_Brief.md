# Phase 6c — Schematic Canvas Core: read-only render + navigate (Claude Code / Sonnet)

**Goal:** build the **schematic canvas** as a virtualized SkiaSharp custom control that **renders a schematic
from a design model and lets the user pan/zoom/navigate** — read-only. It loads a design (from a `.cnl` for
now), draws the grid, component symbols, wires, ports, and connection state, and stays smooth at **10,000
components @ ≥ 30 fps**. **No editing, no placing, no wiring, no net extraction yet** (those are 6d/6e). This
is the performance-critical control proved in isolation before editing is layered on.

> Read first: `docs/design/ui-design.md` (§3 the two canvases + performance target, §4.1 navigation/grid,
> §4.3 connection visual state), `docs/design/ui-architecture.md` (§3.3 Skia-render vs Avalonia-host split),
> `src/Ui/CLAUDE.md` (canvas performance mandate). **Study splotRF's canvas** (the proven pattern — files
> cited below). Design notes win.

## The proven pattern to adapt (splotRF — cite these)
splotRF already solved "custom SkiaSharp control with pan/zoom over a coordinate-transformed model." The
schematic canvas adapts the **same machinery**, rendering symbols+wires instead of plot traces:
- **`splotRF/src/Controls/PlotControl.cs`** — THE render-host pattern: a `Control` that overrides
  `Render(DrawingContext)`, pushes an **`ICustomDrawOperation`**, and in its `Render(ImmediateDrawingContext)`
  leases the Skia canvas via **`ISkiaSharpApiLease`** and calls the renderer. **This is the officially
  recommended Avalonia 11+ approach — use it, NOT `SKCanvasView`.** Also the model-binding pattern (a
  `DirectProperty` for the model + `InvalidateVisual()` on change), and the interaction handlers (left-drag
  pan, scroll-wheel zoom centered on cursor world position, double-click reset).
- **`splotRF/src/Renderers/PlotRenderer.cs`** — the **transform pipeline** (data/world → fractional viewport
  → Skia pixels via linear transforms; `BuildTransforms`). The schematic canvas needs the same world↔pixel
  transform, driven by a pan/zoom viewport.
- **`splotRF/src/Renderers/SkiaFonts.cs`, `RenderTheme.cs`** — Skia font loading + theming; reuse the
  approach (circuitRF gets its own theme tokens, but the pattern is proven).
- The **Skia-render core stays separable from the Avalonia control host** (`ui-architecture.md` §3.3) — the
  renderer draws to a Skia canvas given (model + transform); the control hosts it and pumps input. This is
  what lets a future re-skin keep the renderer.

## Scope

### STEP 1 — the schematic view model (what the canvas renders)
The canvas renders a **read-only schematic model** derived from the design model:
- Load a design from a `.cnl` (reuse the existing `CnlReader`/elaboration — the engine side already parses
  these; the canvas consumes the *design* layer, components+nets+placement, not the elaborated matrices).
- For 6c, schematic **placement** (component positions, wire geometry, ports) comes from the design. If the
  `.cnl` has no geometry yet (authored netlists may not), generate a deterministic placeholder layout OR add
  a minimal placement section to a test schematic — enough to render a real, non-trivial schematic. (The
  authoritative placement persistence is a 6d concern; 6c needs *something* real to render and stress-test.)
- The schematic model exposes: components (each with a symbol, position, rotation/mirror, port locations,
  and the few on-schematic parameter labels), wires (polylines), ports, and connection points. **No mutation
  API in 6c** — it's a read model.

### STEP 2 — the canvas control (adapt PlotControl)
- A `SchematicCanvas : Control` using the `ICustomDrawOperation` + `ISkiaSharpApiLease` pattern from
  `PlotControl`. A `DirectProperty` binds the schematic model; `InvalidateVisual()` on change.
- **World↔pixel transform** (adapt `PlotRenderer.BuildTransforms`): a viewport (pan offset + zoom scale)
  maps schematic world coordinates to Skia pixels. Pan = drag; zoom = scroll-wheel **centered on the cursor's
  world position** (mirror PlotControl); plus the toolbar/menu Zoom-to-Fit / Zoom-to-Page / Zoom-Area hooks
  (the §7.2 schematic toolbar exists from the design; 6c wires the *navigation* ones, editing tools are 6d).
- Renders into the Content tab (6b's DocumentDock) — a schematic Content tab now shows a real canvas.

### STEP 3 — rendering (the schematic renderer, Skia, separable from the host)
A `SchematicRenderer` that draws, given (model + transform + theme):
- **The grid** (§4.1) — a subtle background grid at the model's grid resolution; it must scale sensibly with
  zoom (don't draw 10k grid lines when zoomed out — cull/space by zoom level).
- **Component symbols** — each component's symbol geometry (lines, arcs, polygons) transformed by its
  placement (position + rotation + mirror). For 6c a built-in symbol set for the common primitives is enough
  (R, L, C, source, ground, the FET/SDD, a generic box for cells); symbols are vector geometry drawn in Skia.
- **Wires** — polylines in world space.
- **Connection state** (§4.3): a **dark square** at connected wire-wire / port-wire points; an **unconnected
  port** gets a **subtle red box** outline. (6c renders the connection state the model declares; it does not
  *compute* connectivity — that's net extraction, 6e. The read model can carry precomputed/known connection
  flags, or for 6c mark them from the `.cnl` topology.)
- **On-schematic parameter labels** (§4.5) — the few labels a component shows (instance name, key value with
  units); rendered as Skia text, not editable in 6c.

### STEP 4 — virtualization + spatial index (the performance mandate)
This is the make-or-break of 6c (`src/Ui/CLAUDE.md`):
- **Never render components as individual Avalonia controls** — the canvas draws itself via the one custom
  control; 10k components are 10k Skia draw calls, not 10k controls.
- **Viewport virtualization:** only draw what's in (or near) the visible viewport. Cull off-screen
  components/wires before drawing.
- **Spatial index** (e.g. a uniform grid or quadtree over world space) for fast viewport queries (what's
  visible) and future hit-testing (6d). Build it from the model; query it each frame for the visible set.
- **Level-of-detail when zoomed out:** below some pixels-per-component threshold, draw simplified glyphs
  (a box/dot) instead of full symbol geometry + labels; full detail when zoomed in. (This is how you keep
  ≥30 fps at 10k — you're never drawing 10k full symbols with text.)

### STEP 5 — the stress test (the acceptance oracle)
- Generate a **10,000-component stress schematic** in `testdata/` (a grid of R/L/C/sources with wires — a
  generator script is fine; it need not be electrically meaningful, just structurally realistic: 10k symbols,
  many wires, many nets).
- Verify **smooth pan/zoom (≥ 30 fps)** on it on the dev machine. Measure and report the frame time during
  pan/zoom (a simple in-app FPS/frame-time readout for the test, or a logged measurement). If it misses 30
  fps, the virtualization/LOD/spatial-index needs work — report the bottleneck, don't ship a janky canvas.
- Also render a **real small schematic** (a hero circuit, e.g. Hero 2's PA) correctly — symbols, wires,
  connection dots, unconnected-port boxes, labels all visually right.

## Acceptance
1. A `SchematicCanvas` custom control (ICustomDrawOperation + ISkiaSharpApiLease, adapted from PlotControl)
   renders a schematic model into a Content tab; pan (drag) + zoom (scroll, cursor-centered) + zoom-to-fit
   work.
2. The `SchematicRenderer` (separable from the host, §3.3) draws grid, symbols (placed/rotated/mirrored),
   wires, connection dots, unconnected-port red boxes, and on-schematic labels — a hero schematic renders
   correctly.
3. Virtualization + spatial index + level-of-detail in place; a generated **10k-component** stress schematic
   pans/zooms at **≥ 30 fps** on the dev machine, with the measured frame time reported.
4. Read-only — no editing/placing/wiring/extraction. The schematic model is a read model.
5. **6a firewall test still green** (the renderer + control are in `src/Ui`; the Skia-render core is
   separable from the Avalonia host). `dotnet build`/`dotnet test` green; Phases 1–5 + 6b untouched.

## Guardrails
- **Read-only render + navigate only.** No editing, placing, wiring, hit-testing-for-edit, or net extraction
  (6d/6e). Build the spatial index now (navigation + future hit-test need it), but don't wire editing.
- **Adapt splotRF's PlotControl/PlotRenderer pattern** (ICustomDrawOperation + ISkiaSharpApiLease +
  world↔pixel transform) — don't invent a render path, and do NOT use SKCanvasView.
- **Performance is the gate, proven on 10k, measured** — virtualization + spatial index + LOD are required,
  not optional; never one-control-per-component. Report the frame time; if <30 fps, report the bottleneck.
- **Skia-render core separable from the Avalonia host** (§3.3) so a re-skin keeps the renderer; firewall
  stays green (all of it in `src/Ui`).
- Diagnostics over grinding: if 30 fps is hard, report what's slow (draw calls? text? no culling?) rather
  than hacking. The LOD/virtualization design is the lever.
- Update `src/Ui/CLAUDE.md` with the canvas pattern (the render-host approach, the spatial-index/LOD
  approach) so 6d/6e and the data-display canvas reuse it.

*6c exits with a fast, navigable, read-only schematic canvas in a Content tab — the foundation 6d turns
interactive (place/move/wire/edit) and 6e wires to extraction. The same canvas core (transform + host +
spatial index) is what the data-display canvas reuses later.*
