# Phase 6f — Standard Library Art: vertical symbols (step 3) (Claude Code / Sonnet)

Replace the placeholder symbol art with the real **vertical** standard-library geometry, flip the 2-terminal
pins to vertical, and re-lay-out the GUI demo. **This brief is ONLY step 3** of `symbol-editor.md` §11 — the
art swap. Steps 1–2 (primitive model, `DrawSymbol`, `BuiltInSymbols.Primitives`, `.csym`) are **done**; this
builds on them. The geometry is fully specified in `standard-library-symbols.md` — **implement to it**. Read
that doc and `symbol-editor.md` §4 (orientation) first. Sub-gated; **report and stop between every layer.**
Firewall green.

> Read first: `docs/design/standard-library-symbols.md` (the concrete per-symbol primitive geometry — the
> authority for every coordinate), `docs/design/symbol-editor.md` §4 (vertical-passive / horizontal-box
> orientation rule, approved). Context code (all landed in steps 1–2):
> `src/Ui/Schematic/SymbolModel.cs` (the primitive types — `LinePrimitive`, `ArcPrimitive`, `CirclePrimitive`,
> `QuadCurvePrimitive`, `PolygonPrimitive`, `RoundedRectPrimitive`, `RectPrimitive`, `SinePrimitive`,
> `SymbolColorRole`, `SymbolStrokeTier`, `SymbolPin`, `Symbol`), `src/Ui/Schematic/BuiltInSymbols.cs` (the
> `Build*()` methods to rewrite + the `L(...)`/`P(...)`/`Sym(...)` helpers), `src/Ui/Renderers/
> SchematicRenderer.cs` (`DrawSymbol` — must already render Arc/Circle/QuadCurve/Polygon/RoundedRect/Sine;
> if any of those was stubbed in step 1, implement it here), `src/Ui/Schematic/EditableSchematic.cs`
> (`SymbolPortDefs.For` — the 2-terminal pin coords to flip; `SymbolGeometry`/`ComputeGlyphBb` reads
> primitives already), `src/Ui/Schematic/SchematicModelBuilder.cs` (`BuildHero2PA`/`MakeComponent`/
> `GenerateStressTest` — the demo to re-lay-out). Design docs win on any conflict.

## The spine (do not violate)
- **Implement the geometry in `standard-library-symbols.md` exactly** (coordinates, primitives, pins). It is
  the spec; don't improvise shapes. The few items flagged **tunable** in that doc (cap curve bow, inductor
  bumps, polarity-dot spot, sine amp, Term zigzag) may be reasonable starting values — get them recognizable.
- **Orientation (§4, approved):** R, L, C, V, Tone, Port/Term, GND are **VERTICAL** (pins top/bottom, on the
  y-axis); FET, ZPort, Sdd, Generic stay **horizontal** (ports left/right). 
- **Pins on `P`:** every pin tip is an exact multiple of 100 in local coords. `SymbolPortDefs` and the
  `Symbol`'s pins must agree (the lead in the art ends at the pin coordinate).
- **One source still holds:** `BuiltInSymbols.Primitives(kind)` remains the single geometry source; you are
  changing its *contents*, not adding a parallel path. Don't reintroduce a `float[]`.
- **Scope fence:** NO editor, NO connectivity rewire, NO Option-B pin binding. Just the art + pin coords +
  demo re-layout + any DrawSymbol primitive still stubbed.

---

## LAYER 1 — finish `DrawSymbol` for the primitives the new art needs

Step 1 may have stubbed Text/Bitmap and possibly Sine/HalfWave. The vertical art uses **Arc** (inductor),
**Circle** (sources), **QuadCurve** (capacitor plate), **Polygon** filled (ground triangle), **RoundedRect**
(Term box), and **Sine** (tone source). Confirm `DrawSymbol` renders **all of these** correctly (stroke +
fill); implement any that are still stubbed. Text and Bitmap may remain stubbed (no step-3 symbol uses them).

- **Arc:** map (Cx,Cy,R,StartDeg,SweepDeg) → `SKPath.AddArc`/`ArcTo` (degrees clockwise from +x, per the
  model comment). Verify a semicircle renders as a smooth curve, not a chord.
- **Sine:** sample `SinePrimitive` (amp, cycles, length, axis) to an `SKPath` polyline (e.g. ~16 pts/cycle).
- **Polygon filled / RoundedRect / Circle / QuadCurve:** honor `Filled` (Fill vs Stroke paint).

**Layer 1 gate:** a scratch render of one Arc, one filled Polygon, one QuadCurve, one Circle, one Sine looks
correct (curves smooth, fills solid). Report.

---

## LAYER 2 — rewrite the vertical 2-terminal symbols in `BuiltInSymbols`

Rewrite these `Build*()` methods to the `standard-library-symbols.md` geometry (vertical). Use the real
primitive types now (not all-Line): Arc for the inductor coils, Circle for sources, QuadCurve for the cap
plate, filled Polygon for the ground triangle, RoundedRect + zigzag Polyline for Term, Sine for the tone
source. Keep roles: body = `SymbolLine`, ± marks = `SymbolPlus`.
- **Resistor** — vertical 6-zig polyline (lead + zigzag + lead), per doc.
- **Inductor** — 4 `Arc` coils bulging +x + leads + filled polarity `Circle`.
- **Capacitor** — flat plate `Line` + curved plate `QuadCurve` + leads.
- **Voltage Source** — `Circle` + leads + ± (`SymbolPlus` lines).
- **Tone Source** — `Circle` + `Sine` + leads.
- **Port (Term)** — lead + `RoundedRect` + internal zigzag `Polyline`, 1 pin top.
- **Ground** — stem `Line` + filled downward `Polygon` triangle, 1 pin at (0,0).

**Layer 2 gate:** the six vertical symbols build; a manual render shows each recognizable and vertical
(pins top/bottom). The horizontal box symbols (FET/ZPort/Sdd/Generic) are unchanged this layer. Report.

---

## LAYER 3 — flip `SymbolPortDefs` 2-terminal pins to vertical

In `SymbolPortDefs.For`, change the **2-terminal** entries from horizontal to vertical so pins match the new
art (every coordinate a `P` multiple):
- Default 2-terminal (R/L/C/V/Tone): `(-200,0)/(200,0)` → **`(0,-200)` top / `(0,+200)` bottom**.
- **Port**: single pin `(-200,0)` → **`(0,-200)`** (top signal).
- **Ground**: pin stays at **`(0,0)`** (the connection point; matches the new art's stem origin).
- **FET, ZPort, Sdd, Generic**: **unchanged** (stay horizontal, left/right ports).

The `Symbol`'s pins are built from `SymbolPortDefs` (via the `Sym(...)` helper), so they update automatically
— but **verify** each new symbol's lead ends exactly at its pin coordinate (art and pin agree).

**Layer 3 gate:** `SymbolPortDefs` 2-terminal entries are vertical; `Symbol` pins match; lead ends coincide
with pin tips; all pins on `P`. Report.

---

## LAYER 4 — re-lay-out the GUI demo for vertical passives

`SchematicModelBuilder` (`BuildHero2PA`, `GenerateStressTest`, `MakeComponent`, the wire-stitching) assumes
horizontal pins (wires from a component's **right** port `(x+200,y)` to the next's **left** port `(x-200,y)`).
With vertical passives the ports are now top `(x,y-200)` / bottom `(x,y+200)`. Re-lay-out so the demo is
correct and connected again:
- Re-route the inter-component wires to the new vertical pin positions (top/bottom), OR rotate series
  passives 90° (`SymbolRotation.R90`) where the signal path is horizontal — whichever yields a clean,
  connected, on-`P` layout (rotating series elements to lie horizontal is the normal EDA workflow and likely
  the smaller change). FET/ZPort/Sdd stay horizontal.
- Keep the demo **connected** (no dangling pins) and **on-grid** (all wire endpoints/pins on `P`).
- `GenerateStressTest` (perf grid): adjust pitch/wiring so it still connects and stays on `P`. (Perf target
  unchanged; vertical vs horizontal doesn't affect the benchmark.)

**Layer 4 gate:** the demo renders connected, on-grid, and sensible with vertical passives; no dangling-pin
indicators except where intended. Report (a screenshot description or the connectivity check).

---

## Acceptance (step 3)
1. The six 2-terminal symbols are the real **vertical** art from `standard-library-symbols.md` (R zigzag, L
   arcs+dot, C flat+curved plate, V circle+±, Tone circle+sine, Term box, GND triangle); FET/ZPort/Sdd/Generic
   unchanged (horizontal).
2. `DrawSymbol` renders Arc/Circle/QuadCurve/filled-Polygon/RoundedRect/Sine correctly (curves smooth, fills
   solid).
3. `SymbolPortDefs` 2-terminal pins are vertical (`(0,∓200)`), Port `(0,-200)`, Ground `(0,0)`; pins on `P`;
   each symbol's lead ends at its pin.
4. The GUI demo is re-laid-out: connected, on-grid, vertical passives.
5. `dotnet build`/`dotnet test` green; firewall green; **no editor, no connectivity rewire, no Option-B**;
   engine Hero fixtures (netlist-level) unaffected; nothing else regresses.

## Guardrails
- **Implement the doc's geometry exactly** (the tunable items may be starting values; everything else is
  spec). Don't invent shapes.
- **Pins on `P`, art free** — every pin tip a multiple of 100; lead ends meet pin tips; body art can be
  off-`P`.
- **One geometry source** — change `BuiltInSymbols` contents; do NOT add a `float[]` or a second path.
- **Vertical only for 2-terminal + Port + GND**; FET/ZPort/Sdd/Generic stay horizontal (§4).
- **Scope fence:** no editor, no `SymbolPortDefs` for the box symbols, no connectivity/Option-B work.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `symbol-editor.md` §11 status (step 3 done) and note in `src/Ui/CLAUDE.md` that 2-terminal symbols
  are now vertical (so future schematic-layout code expects top/bottom pins).

*Exit: the standard library renders as real vertical art (curves, arcs, fills via the primitive model), pins
are vertical and on-grid, and the demo is re-laid-out connected — the visible payoff of the primitive model,
with the symbol editor (step 4+) still to come.*
