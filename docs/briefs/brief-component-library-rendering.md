# Sonnet Brief — Component-library rendering & symbol geometry

Five changes, all in `src/Ui` (no engine, no Core). Do them in order; build 0W/0E
(TreatWarningsAsErrors) after each. Parts 1–2 are renderer; parts 3–5 are symbol geometry +
port definitions. A sibling brief `brief-p1tone-num-sddx-defaults.md` covers the P1Tone Num
parameter, s-param port behavior, and SDDX default params — do NOT touch those here.

Files:
- `src/Ui/Schematic/EditableSchematic.cs` — FullBb fix (Part 1); FET/Pin port defs + SddBodyRect (Parts 3,5).
- `src/Ui/Schematic/SchematicModelBuilder.cs` — parallel FullBb fix + glyph BB + BuildPorts (Parts 1,3,5).
- `src/Ui/Renderers/SchematicRenderer.cs` — round-join constant + symbol paints (Part 2).
- `src/Ui/Renderers/SymbolEditorRenderer.cs` — uses the same shared paints (Part 2).
- `src/Ui/Schematic/BuiltInSymbols.cs` — BuildFetSdd, BuildP1Tone, BuildPin redraws (Parts 3,4,5).

---

## PART 1 — Off-screen cull bug for tall symbols (Z1P / SDD / ZPort)

**Symptom:** Z1P and SDD instances sometimes don't render when the symbol's CENTER scrolls out of
view; worst for high port counts (the symbol is elongated in ±y).

**Root cause (confirmed):** A component's culling box `FullBb` is seeded from the fixed ±200
`GetBoundingBox()` and then unioned ONLY with label positions — it is **never unioned with the
glyph BB**. For SDD/ZPort the glyph spans far beyond ±200 in Y (`SymbolPortDefs.SddBodyRect(n).HalfH
= maxPinY + 60`, and pins sit at cy ± 100 with cy spanning `(p − (nLeft−1)/2)·400`). The spatial
index (`SchematicSpatialIndex`) is built on `FullBb`, and the per-frame cull in
`SchematicRenderer.Draw` also tests `FullBb`. So when the center scrolls off, the index cell holding
the visible top/bottom of the tall symbol isn't returned → the symbol vanishes. The glyph BB
(`GlyphBb`) is already computed correctly and N-aware; it's simply not folded into `FullBb`.

**Fix:** union `FullBb` with the glyph BB in BOTH parallel code paths.

### 1a. `EditableComponent.ToRenderComponent` (`EditableSchematic.cs`)
The method already computes `(glyphMinX, glyphMinY, glyphMaxX, glyphMaxY)` and seeds
`fullMinX..fullMaxY` from `bb` (the ±200 box). Before the label loop, also union in the glyph BB:
```csharp
double fullMinX = Math.Min(bb.MinX, glyphMinX), fullMinY = Math.Min(bb.MinY, glyphMinY);
double fullMaxX = Math.Max(bb.MaxX, glyphMaxX), fullMaxY = Math.Max(bb.MaxY, glyphMaxY);
```
(Replace the four lines currently seeding `fullMinX = bb.MinX … fullMaxY = bb.MaxY`.) Leave the
label-union loop that follows unchanged.

### 1b. `SchematicModelBuilder.MakeComponent` (`SchematicModelBuilder.cs`)
Same bug, parallel code. It computes `(gMinX, gMinY, gMaxX, gMaxY)` via `ComputeGlyphBbLocal`. Seed
the FullBb from the union of the ±HalfBound box AND the glyph BB:
```csharp
double fullMinX = Math.Min(cx - HalfBound, gMinX), fullMinY = Math.Min(cy - HalfBound, gMinY);
double fullMaxX = Math.Max(cx + HalfBound, gMaxX), fullMaxY = Math.Max(cy + HalfBound, gMaxY);
```
(Replace the four lines seeding `fullMinX = cx - HalfBound … fullMaxY = cy + HalfBound`.) Leave the
label loop unchanged.

> Note: `GlyphBb`/`ComputeGlyphBbLocal` for ZPort/Sdd already walk `SymbolPortDefs.For(kind,n)` pin
> positions, so they cover the full ±y extent. No change needed there. The fixed `BbMinX..BbMaxY`
> (±200) is still used for overall-bounds/zoom-fit and stays as-is — only `FullBb` (cull + index)
> changes.

**Part 1 test** (`tests/Ui.Tests/`, e.g. extend a spatial-index or render-model test):
- Build a high-port-count SDD (e.g. N=6) at the origin. Assert its `FullBbMinY ≤ glyph top` and
  `FullBbMaxY ≥ glyph bottom` (i.e. FullBb fully contains the glyph BB). Concretely:
  `comp.FullBbMinY <= comp.GlyphBbMinY && comp.FullBbMaxY >= comp.GlyphBbMaxY`.
- Query the spatial index with a viewport that includes only the symbol's TOP (center below the
  viewport) and assert the component index is returned.

---

## PART 2 — Round stroke joins for all symbol rendering, one switch point

**Requirement:** all SYMBOL stroke joins → round join, in BOTH the schematic and the symbol editor.
Make the join trivially switchable later.

**Current state:** `SchematicRenderer.DrawSymbol` builds a fresh `using var paint` per primitive with
`StrokeWidth` set but **no `StrokeJoin`/`StrokeCap`** (Skia default = Miter). `bodyPaint` and
`plusPaint` (used for variadic port leads via `DrawVariadicPortLeads`) also set no join. The symbol
editor (`SymbolEditorRenderer`) renders through the same `DrawSymbol`, so fixing the per-primitive
paint fixes both. Wires are separate (`wirePaint`, Miter) and are NOT symbol rendering — leave wires
on Miter.

**Fix:** add one public constant on `SchematicRenderer` and apply it to every SYMBOL paint.
```csharp
// Single switch point for symbol stroke joins (schematic + symbol editor).
// Change to Miter/Bevel here to restyle all symbol corners at once.
public  const SKStrokeJoin SymbolStrokeJoinStyle = SKStrokeJoin.Round;
public  const SKStrokeCap  SymbolStrokeCapStyle  = SKStrokeCap.Round;
```
Apply to:
1. The per-primitive `using var paint` in `DrawSymbol`: add
   `StrokeJoin = SymbolStrokeJoinStyle, StrokeCap = SymbolStrokeCapStyle,` to its initializer.
2. `bodyPaint` and `plusPaint` in `Draw` (these stroke symbol bodies and the variadic port leads):
   add `StrokeJoin = SymbolStrokeJoinStyle` (and `StrokeCap = SymbolStrokeCapStyle`).

Do NOT change `wirePaint` or the overlay selection paints (those are wires/selection, not symbol
geometry). The symbol editor needs no change — it calls `DrawSymbol`, which now carries the join.
The ghost `overridePaint` path: the ghost paints are created by callers; optionally set the same
join on the ghost paint in `DrawOverlay` for visual consistency, but it's not required.

**Part 2 check:** visual — symbol corners (resistor zigzags, FET leads, boxes) render rounded in
both the schematic canvas and the symbol-editor canvas. No automated test needed; confirm by build +
eyeball, or assert the constant is `Round` in a trivial unit test.

---

## PART 3 — Simplify the FET symbol (`BuildFetSdd`)

**Requirement:** no box, straight (non-angled) drain & source leads, no arrows. Pin locations change
to match the new geometry.

**Current geometry** (`BuiltInSymbols.BuildFetSdd`): box ±80, gate tip at (−200,0), drain diagonal to
(200,−100), source diagonal to (200,100), plus channel bars and arrow notches. **Current pins**
(`SymbolPortDefs.For(FetSdd)` and `SchematicModelBuilder.BuildPorts(FetSdd)`): gate(−200,0),
drain(200,−100), source(200,100).

**New design (DECIDED — perfectly straight leads, pins on grid):** a clean FET with a vertical gate
bar and a vertical channel bar, and **perfectly straight horizontal** drain/source leads (no jog, no
diagonal, no box, no arrowheads). The leads run straight at y = ∓100 to the pin tips at (200,∓100).
To keep the leads perfectly straight the channel bar spans y∈[−100,100] so each lead leaves it
horizontally and runs to the on-grid pin tip with no bend. (User asked for pins to "move" with
straight leads; in fact the pin tips were already on-grid at (200,∓100) — removing the diagonal makes
the lead straight to that exact point, so no pin-coordinate change is required, just the drawing.)

```csharp
private static Symbol BuildFetSdd() => Sym([
    L(-200,    0,  -60,    0),   // gate lead (tip at -200,0)
    L( -60, -100,  -60,  100),   // gate vertical bar
    L( -40, -100,  -40,  100),   // channel vertical bar (parallel to gate)
    L( -40, -100,  200, -100),   // drain: PERFECTLY STRAIGHT horizontal lead to pin tip (200,-100)
    L( -40,  100,  200,  100),   // source: PERFECTLY STRAIGHT horizontal lead to pin tip (200,100)
], SymbolKind.FetSdd);
```
Pin tips: gate (−200,0), drain (200,−100), source (200,100) — all on grid, leads perfectly straight.
The x/y of the pins is unchanged from today (still (200,∓100)); only the symbol DRAWING changes (the
diagonal/jog is gone, the channel is taller). So the pin definitions need NO coordinate change — but
**verify** all three sites still read gate(−200,0)/drain(200,−100)/source(200,100) and leave them as-is:

1. `BuiltInSymbols.BuildFetSdd()` — the drawing (above).
2. `SymbolPortDefs.For(SymbolKind.FetSdd, _)` in `EditableSchematic.cs` — the pin tuples (unchanged).
3. `SchematicModelBuilder.BuildPorts(FetSdd)` in `SchematicModelBuilder.cs` — the `SchematicPortDef`s (unchanged).

> The user asked for "pins should move, but must be on grid" with perfectly straight leads. The
> physically-meaningful change is the GEOMETRY (straight leads, taller channel, no box/arrows); the
> pin tips were already at the on-grid (200,∓100) and the straight lead now terminates exactly there,
> so no net coordinate move is needed. If the channel-at-±100 look is too tall for your taste, the
> ONLY alternative that keeps leads perfectly straight AND pins on grid is to move pins to (200,0)…
> which collapses drain and source onto one point — not viable. So ±100 is the right channel height.

Also update the FET glyph BB if it's hardcoded: `ComputeGlyphBbLocal` in `SchematicModelBuilder.cs`
returns `(cx−210, cy−110, cx+210, cy+110)` for `FetSdd` — that still bounds the new geometry, leave
it. (No `ComputeGlyphBb` FetSdd special-case in `EditableSchematic.cs`; it walks primitives.)

**Part 3 test:** assert `BuildFetSdd().Primitives` contains no `PolygonPrimitive` arrowheads and no
4-line box (count line primitives — should be 5: gate lead, gate bar, channel bar, drain lead, source
lead), the drain lead is a single straight horizontal line ending at (200,−100) and the source lead a
single straight horizontal line ending at (200,100), and that `SymbolPortDefs.For(FetSdd)` still
returns gate(−200,0)/drain(200,−100)/source(200,100). Existing FET symbol tests must stay green.

---

## PART 4 — Redraw the P1Tone symbol (`BuildP1Tone`)

**Requirement:** keep pin locations EXACTLY where they are (default 2-terminal: (0,−200) top,
(0,+200) bottom — P1Tone is not special-cased in `SymbolPortDefs`, so it uses the default and must
stay that way). New look:
- A rounded box the **same size as the Term symbol** — Term uses `RRect(0,0,110,240,12)`.
- **Top half:** a zig/zag resistor, drawn SMALLER than Term's resistor.
- The resistor connects to a **"voltage source circle"** in the **bottom half**.
- **Inside the circle:** a sine primitive (1 cycle) filling the circle's width.

Term reference geometry: frame `RRect(0,0,110,240,12)` (y∈[−120,+120]); leads (0,−200)→(0,−110) and
(0,+120)→(0,+200); internal zigzag PLine spanning y∈[−110,+120]. Match Term's box + leads so P1Tone
reads as the same family.

Proposed implementation (tune amplitudes/radius for a clean look; keep pins at (0,∓200)):
```csharp
private static Symbol BuildP1Tone() => Sym([
    L(    0, -200,    0, -110),          // top lead into box (same as Term)
    RRect(0,    0,  110,  240,   12),    // frame box, SAME size as Term
    // Top half: small zigzag resistor (smaller than Term's), spanning roughly y∈[-100,-10]
    PLine(  0, -100,   0, -85,
           18, -73, -18, -55,
           18, -37, -18, -19,
            0,  -7,   0,   0),           // resistor body ends at circle top
    // Bottom half: voltage-source circle centered ~ (0,+55)
    Circ(  0,  55,  45),
    // Sine inside the circle: 1 cycle, fills the circle width (length ≈ 2·r = 90)
    Sine(  0,  55,  20,    1,   90, SineAxis.Horizontal),
    L(    0,  120,    0,  200),          // bottom lead from box (same as Term)
], SymbolKind.P1Tone);
```
Notes:
- Resistor sits entirely in the TOP half (y<0); the circle sits in the BOTTOM half (y>0). The
  resistor's bottom end (0,0) should meet/lead into the circle top (circle top = 55−45 = 10; add a
  tiny connector `L(0,0, 0,10)` if you want them visibly joined).
- The `Sine` `length` must equal the circle's diameter (2·r) so it "fills the width."
- The polarity `+`/`−` text from the old P1Tone is removed (the new symbol is box-framed like Term);
  if you want polarity markers, place them outside the box like Term does — but the request doesn't
  ask for them, so omit unless they read well.
- Do NOT change `SymbolPortDefs` for P1Tone — pins stay at the default (0,∓200).

**Part 4 test:** assert `BuildP1Tone().Primitives` contains exactly one `RoundedRectPrimitive` of
size 110×240, one `CirclePrimitive`, and one `SinePrimitive` with `Cycles == 1`; and that
`SymbolPortDefs.For(P1Tone)` is unchanged (default 2-terminal (0,−200),(0,+200)).

---

## PART 5 — Shrink the Pin symbol

**Requirement:** Pin is currently ~300 units wide; reduce it. Stem 100→50 units. Keep pin/port ON
GRID (a multiple of 100). Reduce the hexagon so the total x-distance of the symbol = 200 units.
Stem must touch the hexagon. **Round vertex numbers preferred — exact aspect ratio does NOT matter.**

**Current geometry** (`BuiltInSymbols.BuildPin`):
`Poly(false, -40,-50, 40,-50, 80,0, 40,50, -40,50, -80,0)` (hexagon, x∈[−80,+80], y∈[−50,+50]) plus
`L(80,0, 200,0)` (stem from hex right vertex x=80 to tip x=200 — stem length 120, total x-span
−80..200 = 280). **Current port** (`SymbolPortDefs.For(Pin)` and `SchematicModelBuilder.BuildPorts(Pin)`):
(200,0).

**New geometry (DECIDED — round numbers, aspect ratio free):** total x-span = 200, stem = 50, port
on grid at (100,0).
- Port tip at **(100,0)** (on grid). Stem from **(50,0)→(100,0)** (length 50). Hexagon right vertex
  at x=50, left vertex at x=−100 → hexagon spans x∈[−100,50] (width 150), total x-span −100..100 =
  200. ✓
- Keep the original half-height 50 (y∈[−50,50]). Hexagon center x = (−100+50)/2 = −25. Put the
  flat-top/bottom vertices a round 15 units in from center → at x=−40 and x=10. All vertices land on
  round numbers:
```csharp
private static Symbol BuildPin() => Sym([
    // Hexagon: left vertex (-100,0), right vertex (50,0), height ±50; flat-top edge vertices
    // at x=-40 and x=10 (15 units either side of center x=-25). All round numbers.
    Poly(false, -40,-50,  10,-50,  50,0,  10,50,  -40,50,  -100,0),
    L(50, 0,  100, 0),   // stem: hex right vertex (50,0) → port tip (100,0), length 50
], SymbolKind.Pin);
```
  Port tip = (100,0) — on grid. Stem touches the hexagon at its right vertex (50,0). Total x-span
  = −100..100 = 200. ✓ Every coordinate is a round number.

> Aspect ratio is intentionally NOT preserved (user confirmed it doesn't matter) in favor of round
> vertices. Hard constraints met: total x-span 200, stem 50, port on grid (100,0), stem touches hexagon.

**Update the port in ALL THREE places** to (100,0):
1. `SymbolPortDefs.For(SymbolKind.Pin, _)` in `EditableSchematic.cs`: `("1", 100f, 0f)`.
2. `SchematicModelBuilder.BuildPorts(Pin)` in `SchematicModelBuilder.cs`:
   `new SchematicPortDef("1", 100, 0, p0)`.
3. Pin glyph BB: `ComputeGlyphBb`/`ComputeGlyphBbLocal` for Pin both walk primitives (no hardcoded
   Pin box), so no change — but verify `ComputeGlyphBbLocal`'s non-box branch bounds it (it uses the
   vertical-2-terminal ±210/±65 box for "other" kinds, which still contains the new Pin; fine).

> Pin's port moving from (200,0) to (100,0) changes where wires attach. This is a geometry change the
> user explicitly asked for; existing Pin connectivity tests that hardcode (200,0) must be updated to
> (100,0). Search tests for `200, 0` / `200f, 0f` in Pin contexts (`PinOnPinConnectivityTests`,
> `GridPinNetLabelPolishTests`, `NetExtractorPinTests`) and fix the expected coordinate.

**Part 5 test:** assert `SymbolPortDefs.For(Pin)` returns port at (100,0); assert the hexagon
primitive's x-extent is [−100, 50] and the stem runs (50,0)→(100,0); assert total symbol x-span
(from `SymbolGeometry.ComputeBb`) is 200 wide.

## Gate
Build 0W/0E (TreatWarningsAsErrors). All symbol/render/connectivity tests green (update the Pin-port
coordinate in tests per Part 5). Verify on disk: high-port-count SDD stays visible when its center
scrolls off-screen; symbol corners are round in schematic + symbol editor; FET has no box/arrows with
straight leads; P1Tone is a Term-sized box with a small resistor over a sine-in-circle; Pin is 200
wide with a 50-unit stem and port at (100,0).

On completion, note in `src/Ui/Renderers/CLAUDE.md` (create if absent) that symbol stroke joins are
controlled by `SchematicRenderer.SymbolStrokeJoinStyle` (single switch point, schematic + symbol editor).
