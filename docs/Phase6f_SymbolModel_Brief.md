# Phase 6f — Symbol Primitive Model + `.csym` Persistence (steps 1–2) (Claude Code / Sonnet)

Foundation for the symbol editor: replace the hardcoded `float[]` line-segment symbols with a real
**primitive model** (data, not code), rendered through one generic `DrawSymbol`, and persisted to `.csym`.
**This brief is ONLY steps 1–2** of `symbol-editor.md` §11 — the model + persistence. **No editor, no new art,
no vertical flip, no connectivity changes** here. Read `symbol-editor.md` (§2 the model, §10 `.csym`) and
`standard-library-symbols.md` (the primitive notation) first — they are the authority. Sub-gated into small
layers; **report and stop between every layer.** Firewall green; values/pixels must not change.

> Read first: `docs/design/symbol-editor.md` §2 (primitive model, §2.2 one-model-three-consumers, §2.3 color
> roles, §2.4 grid, §2.5 bitmap), §10 (`.csym`); `docs/design/standard-library-symbols.md` (primitive
> notation + roles); `docs/design/project-file-formats.md` (`.csym` stub, serialization conventions);
> `src/Ui/CLAUDE.md` (firewall, command pattern). Context code:
> `src/Ui/Renderers/SchematicSymbols.cs` (the current `float[]` symbols to transcribe + `ForSymbolPlusSegments`),
> `src/Ui/Renderers/SchematicRenderer.cs` (`DrawSymbolLines`, the ghost draw, `LocalToPixel` — the call sites
> to reroute), `src/Ui/Schematic/EditableSchematic.cs` (`ComputeGlyphBb` reads `SchematicSymbols.For` — a
> geometry reader to reroute; `SymbolPortDefs` — **do NOT touch in this brief**),
> `src/Ui/Schematic/SchematicPersistence.cs` (the System.Text.Json `.csch` pattern to mirror for `.csym`),
> `src/Ui/Renderers/SchematicRenderTheme.cs` (`SymbolLine`/`SymbolPlus`/`SymbolText` resolved colors).
> Design docs win on any conflict.

## The spine (do not violate)
- **One primitive model, framework-free.** The model is **data only** — no SkiaSharp, no Avalonia, no
  `SKColor`/`SKPath`. Colors are a **`SymbolColorRole` enum** (not literal colors); coordinates are `double`.
  (Firewall manual-check: no Skia type may leak into the model — the firewall test won't catch an `SKColor`
  in a framework-free record, so check by eye.)
- **One source of symbol geometry.** After this brief, `BuiltInSymbols.Primitives(kind)` is the **single**
  source every consumer reads — the renderer body draw, the plus-marks, the ghost preview, AND
  `ComputeGlyphBb`. No consumer may keep reading the old `float[]`. (Two sources = the drift bug class.)
- **Parity is the oracle.** The transcribed built-ins are line-for-line the current `float[]`, so rendering
  through `DrawSymbol` must be **pixel-identical** to today. Any visible change = a transcription bug.
- **Scope fence:** NO editor, NO new/vertical art, NO `SymbolPortDefs` change, NO connectivity change. Those
  are later steps. This brief swaps the *representation*, nothing else.

---

## STEP 1 — primitive model + generic DrawSymbol + transcribe + reroute (parity)

### LAYER 1 — the primitive model (framework-free data types)
Create the symbol primitive model (in the framework-free layer, e.g. `src/Ui/Schematic/SymbolModel.cs` —
same layer as `EditableSchematic`, no Skia/Avalonia):
- `enum SymbolColorRole { SymbolLine, SymbolText, SymbolPlus }` (extend later if needed).
- `enum SymbolFontStyle { Regular, Bold, Italic, Condensed }`.
- `enum SymbolStrokeTier { Normal, Thin }` (or a plain `double StrokeWidth` — pick one; the doc suggests
  Normal≈6 / Thin≈3 local units).
- A `SymbolPrimitive` discriminated set (records implementing a common interface/base, with a type tag for
  serialization): **Line** (p0,p1), **Polyline** (points[]), **Rect** (cx,cy,w,h), **RoundedRect**
  (+radius), **Circle** (cx,cy,r), **Ellipse** (cx,cy,rx,ry), **Arc** (cx,cy,r,startDeg,sweepDeg),
  **Triangle/Polygon** (points[], closed), **QuadCurve** (p0,ctrl,p2), **CubicCurve** (p0,c1,c2,p3),
  **Sine** (cx,cy,amp,cycles,length,axis), **HalfWave** (params), **Text** (string,anchor,fontSize,
  fontStyle,align), **Bitmap** (imagePathRef, rect, opacity, locked — §2.5; data only here).
  Each drawable vector primitive carries `SymbolColorRole`, stroke width/tier, and a `Filled` bool (where
  meaningful). Text carries fontSize+style; Bitmap carries its own fields (no role/stroke/fill).
- A `Symbol` type = an ordered `IReadOnlyList<SymbolPrimitive>` + a list of **pins** (`Pin { double LocalX,
  LocalY; int PortIndex; string? Name }`). **Pins are data here only** — they are written/read but the
  runtime still uses `SymbolPortDefs` for connectivity (do NOT rewire).

**Layer 1 gate:** model types compile, framework-free (no Skia/Avalonia ref); a unit test constructs a
`Symbol` with a few primitives + pins. Report.

### LAYER 2 — generic `DrawSymbol` (renderer)
In `SchematicRenderer`, add `DrawSymbol(SKCanvas, Symbol|IReadOnlyList<SymbolPrimitive>, compX, compY,
rotation, mirrorX, panX, panY, zoom, theme)` that walks primitives → Skia draw calls, reusing the existing
`LocalToPixel` transform (so rotation/mirror/zoom behave exactly as today):
- Map `SymbolColorRole` → theme color (`SymbolLine`→`theme.SymbolLine`, `SymbolPlus`→`theme.SymbolPlus`,
  `SymbolText`→a text role) and stroke tier → stroke width (the same `Math.Max(1, zoom*…)` flooring pattern
  the current paints use).
- Implement the **vector** primitives now: Line, Polyline, Rect, RoundedRect, Circle, Ellipse, Arc,
  Triangle/Polygon, QuadCurve, CubicCurve, Sine, HalfWave (Sine/HalfWave sample to an `SKPath`). Honor
  `Filled` (Fill vs Stroke paint).
- **Text and Bitmap rendering may be stubbed/no-op** for this brief (no current or near-term library symbol
  uses them; they land with the editor/auto-gen steps). Leave a clear `// TODO step 5/7` marker.

**Layer 2 gate:** `DrawSymbol` renders a hand-built test primitive list (a line, a circle, an arc, a filled
triangle) correctly in a scratch/manual check. Report.

### LAYER 3 — transcribe built-ins + reroute every consumer (PARITY)
1. Create `BuiltInSymbols.Primitives(SymbolKind) → Symbol` (in the framework-free layer). For each kind,
   transcribe the current `SchematicSymbols.For(kind)` `float[]` into **`Line` primitives** (one Line per
   (x1,y1,x2,y2) quad), role `SymbolLine`, Normal stroke. For **VoltageSource**, append its
   `ForSymbolPlusSegments` quads as `Line` primitives with role **`SymbolPlus`** (folding the special
   plus-path into the one primitive list — eliminating the separate `ForSymbolPlusSegments` consumer). Pins:
   populate from `SymbolPortDefs.For(kind)` (data only).
2. **Reroute the renderer:** `DrawSymbolLines(canvas, c, …)` → `DrawSymbol(canvas, BuiltInSymbols.Primitives
   (c.Symbol), …)`. Remove the separate plus-mark draw (now inside the primitive list). Reroute the **ghost
   preview** draw to `DrawSymbol` over the same `BuiltInSymbols.Primitives(ghost.Symbol)` (with the ghost
   paint/override) — so the ghost reads the one source, not `float[]`.
3. **Reroute `ComputeGlyphBb`** (`EditableComponent`) to compute the glyph BB from
   `BuiltInSymbols.Primitives(Symbol)` via a framework-free `SymbolGeometry.ComputeBb(primitives)` helper
   (bbox over all primitive extents), instead of reading the `float[]`. (Keep the existing variadic
   ZPort/Sdd port-tip extension behavior.)
4. **Retire the `float[]` path:** once every consumer reads primitives, delete `SchematicSymbols`'
   `float[]` constants + `For`/`ForSymbolPlusSegments` (or leave a thin shim that throws if called — but
   prefer deletion so no second source survives).

**Layer 3 gate (the parity oracle):** the existing GUI demo (`BuildHero2PA` etc.) renders **pixel-identical**
to before — same symbols, leads, plus-marks, glyph bounding boxes, selection outlines, ghost preview. Diff a
before/after screenshot or assert BBs unchanged. `dotnet build`/`dotnet test` green; firewall green. **If any
symbol looks different, it's a transcription bug — fix it; do not accept "close."** Report the parity result.

---

## STEP 2 — `.csym` persistence (read/write the primitive model)

The `.csym` reader/writer does not exist yet — create it, mirroring `SchematicPersistence.cs`'s System.Text.Json
conventions (enum-as-string, nullable/defaulted, references-not-payloads, `format_version` reject-on-mismatch,
`Id` never persisted).

### LAYER 4 — `.csym` schema + round-trip
1. `SymbolPersistence.cs` (+ `CsymFile` model): `FormatVersion` (reject-on-mismatch), the **primitive list**
   (each primitive serialized with a **type discriminator** — use `[JsonPolymorphic]`/`[JsonDerivedType]` or
   a custom converter so the union round-trips), **pins** (LocalX/LocalY + PortIndex + optional Name), and
   the **authoring `GridSize`** (`P_src`, for the future cross-grid paste check — write it, don't act on it).
   **Bitmap** stores a **path reference, not bytes** (§2.5 / references-not-payloads).
2. Read + write methods; a **round-trip test**: build a `Symbol` (mix of primitive types + pins) → write to a
   temp `.csym` → read back → asserts the primitive list and pins are identical (coords, roles, stroke, fill,
   font, bitmap path/opacity/locked). Include a `format_version` mismatch test (rejects with a clear error).
3. **Built-ins stay as code for now** (`BuiltInSymbols.Primitives`); `.csym` is the format for user symbols +
   future library externalization. Do NOT migrate the built-ins to `.csym` files in this brief (that's a
   later step) — just prove the format round-trips.

**Layer 4 gate:** `.csym` round-trips a primitive symbol losslessly; format_version mismatch rejected cleanly;
bitmap stored as path ref. Firewall green. Report.

---

## Acceptance (steps 1–2)
1. A framework-free primitive model exists (vector primitives + Text + Bitmap as data; color = role enum, no
   Skia types in the model).
2. A generic `DrawSymbol` renders all vector primitives (Text/Bitmap stubbed); the **single** geometry source
   is `BuiltInSymbols.Primitives(kind)` — renderer body, plus-marks, ghost, and `ComputeGlyphBb` all read it;
   the old `float[]` path is gone.
3. **Parity:** the existing demo renders pixel-identical (symbols, leads, plus-marks, BBs, ghost) — proven,
   not assumed.
4. `.csym` reads/writes the primitive model + pins losslessly (round-trip test), with format_version reject
   and bitmap-as-path-ref.
5. `dotnet build`/`dotnet test` green; firewall green; **no editor, no new art, no `SymbolPortDefs`/
   connectivity change**; nothing in prior phases regresses.

## Guardrails
- **Model is framework-free** — no `SKColor`/`SKPath`/Avalonia in the primitive model (manual firewall check).
- **One geometry source** after this — every consumer reads `BuiltInSymbols.Primitives`; delete the `float[]`.
- **Parity is non-negotiable** — pixel-identical to today; "close" is a bug. The transcription is mechanical
  (each segment → one `Line`), so identical output is achievable and expected.
- **Do NOT touch** `SymbolPortDefs`, connectivity, the editor, or the standard-library art/orientation — those
  are steps 3+. Pins are written/read as data only; the runtime still uses `SymbolPortDefs`.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `symbol-editor.md` §11 status (steps 1–2 done) and `src/Ui/CLAUDE.md` (the primitive model is the
  single symbol-geometry source; `.csym` exists) to match what was built.

*Exit: symbols are a framework-free primitive model rendered through one generic `DrawSymbol` from a single
source, pixel-identical to the old hardcoded segments, and round-trip to `.csym` — the foundation the standard
library art (step 3) and the symbol editor (step 4+) build on, with no behavior change yet.*
