# circuitRF — Standard Library Symbol Geometry

**Status:** Draft (rev 1) for review · **Date:** 2026-06-08 · **Phase:** 6f (standard library art)

Concrete geometry for every built-in symbol, expressed in the **symbol primitive model** (`symbol-editor.md`
§2), **vertical** for 2-terminal parts and **horizontal** for boxes (§4, approved). This is the spec that the
standard-library art (implementation order §11 step 3) is built to, and the starting point the owner will
hand-tune in the symbol editor. Companions: `symbol-editor.md` (the model + editor), `grid-and-connectivity.md`
(pins on P), the provided Core Graphics art (the drawing grammar this reuses).

> These shapes reuse the Core Graphics grammar: parametric/clean proportions, leads are part of the symbol,
> single-stroke with sparing fills, classic vocabulary. The numbers below are concrete and buildable; the few
> genuinely artistic bits (capacitor curve bow, inductor bump radius, polarity-dot placement) are flagged
> **tunable** — get them recognizable, then refine in the editor.

---

## Conventions

- **Coordinates:** component-LOCAL, origin `(0,0)` at the symbol center. **100 units = 1 connection-grid
  square `P`.** +x right, +y **down** (screen convention, matches the renderer and the Core Graphics source).
- **Pins:** every pin tip is an exact multiple of `P` (100). Notation: `Pin <n> → port <p> @ (x,y)`. The
  symbol's **lead ends at the pin coordinate**; the renderer draws the pin/connection marker (connected vs.
  unconnected coloring) — the symbol does NOT draw the pin dot itself.
- **2-terminal vertical default:** pin 1 `(0,-200)` (top), pin 2 `(0,+200)` (bottom); leads run along `x=0`.
- **Color roles** (never literal — §2.3): `SymbolLine` (body + leads), `SymbolText` (text), `SymbolPlus`
  (polarity marks). 
- **Stroke widths:** two named tiers in local units — **Normal ≈ 6** (body/leads), **Thin ≈ 3** (fine detail,
  auto-gen inner rect). Suggested values; tunable; they live in local space so they scale with zoom.
- **Primitive notation used below:**
  `Line[role,w]: (x1,y1)→(x2,y2)` · `Polyline[role,w]: (p0)(p1)…` ·
  `Circle[role,w,stroked|filled]: c=(x,y) r=R` · `Arc[role,w]: c=(x,y) r=R start=A° sweep=S°` ·
  `QuadCurve[role,w]: p0=(..) ctrl=(..) p2=(..)` · `Triangle[role,w,filled]: (a)(b)(c)` ·
  `Rect[role,w]: c=(x,y) w=W h=H` · `RoundedRect[role,w]: c=(x,y) w=W h=H r=R` ·
  `Sine[role,w]: c=(x,y) amp=A cycles=N length=L axis=horizontal|vertical` ·
  `Text[SymbolText,size]: "s" anchor=(x,y) align=..`

---

## 2-terminal vertical symbols

### Resistor — vertical zigzag (ANSI/US)
Pins: `1 → port 1 @ (0,-200)` · `2 → port 2 @ (0,+200)`
```
Polyline[SymbolLine, Normal]:
  (0,-200) (0,-90) (30,-75) (-30,-45) (30,-15) (-30,15) (30,45) (-30,75) (0,90) (0,200)
```
One polyline = top lead + 6-zig body + bottom lead. Zig amplitude ±30, body spans y∈[-90,+90].
*(Tunable: amplitude, zig count.)*

### Inductor — vertical coils + polarity dot
Pins: `1 → port 1 @ (0,-200)` · `2 → port 2 @ (0,+200)`
```
Line[SymbolLine, Normal]: (0,-200)→(0,-100)        # top lead
Arc[SymbolLine, Normal]:  c=(0,-75) r=25 start=-90° sweep=180°   # coil 1 (bulges +x)
Arc[SymbolLine, Normal]:  c=(0,-25) r=25 start=-90° sweep=180°   # coil 2
Arc[SymbolLine, Normal]:  c=(0,+25) r=25 start=-90° sweep=180°   # coil 3
Arc[SymbolLine, Normal]:  c=(0,+75) r=25 start=-90° sweep=180°   # coil 4
Line[SymbolLine, Normal]: (0,+100)→(0,+200)        # bottom lead
Circle[SymbolLine, Normal, filled]: c=(40,-95) r=6 # polarity dot (near top terminal)
```
4 semicircle coils bulging +x, spanning y∈[-100,+100]. *(Tunable: coil count/radius, dot position.)*

### Capacitor — flat plate + curved plate (Core Graphics style)
Pins: `1 → port 1 @ (0,-200)` · `2 → port 2 @ (0,+200)`
```
Line[SymbolLine, Normal]:      (0,-200)→(0,-12)          # top lead
Line[SymbolLine, Normal]:      (-50,-12)→(50,-12)        # flat top plate
QuadCurve[SymbolLine, Normal]: p0=(-50,+22) ctrl=(0,+2) p2=(50,+22)  # curved bottom plate (bows toward gap)
Line[SymbolLine, Normal]:      (0,+12)→(0,200)           # bottom lead (from curve apex (0,+12))
```
Plate gap ≈ 24. Curved plate is the Core Graphics polarized/curved-plate look. *(Tunable: curve bow via ctrl
y; for a non-polarized cap, replace the QuadCurve with a second flat `Line` at y=+12.)*

### Voltage Source — circle + leads + ± (improved)
Pins: `1 → port 1 @ (0,-200)` (+, top) · `2 → port 2 @ (0,+200)` (−, bottom)
```
Line[SymbolLine, Normal]:   (0,-200)→(0,-60)     # top lead
Circle[SymbolLine, Normal, stroked]: c=(0,0) r=60
Line[SymbolLine, Normal]:   (0,+60)→(0,200)      # bottom lead
Line[SymbolPlus, Normal]:   (-12,-30)→(12,-30)   # + horizontal
Line[SymbolPlus, Normal]:   (0,-42)→(0,-18)      # + vertical
Line[SymbolPlus, Normal]:   (-12,+30)→(12,+30)   # − bar
```
Replaces the current circle-of-chords with a real `Circle`. ± in the `SymbolPlus` role.

### Tone / AC Source — circle + sine + leads
Pins: `1 → port 1 @ (0,-200)` · `2 → port 2 @ (0,+200)`
```
Line[SymbolLine, Normal]:   (0,-200)→(0,-60)     # top lead
Circle[SymbolLine, Normal, stroked]: c=(0,0) r=60
Line[SymbolLine, Normal]:   (0,+60)→(0,200)      # bottom lead
Sine[SymbolLine, Normal]:   c=(0,0) amp=22 cycles=1 length=70 axis=horizontal
```
The `Sine` smart-path inside the circle marks AC. *(Tunable: amp, length.)*

### Port / Term (S-parameter port) — resistor-in-box, single signal pin
Pins: `1 → port 1 @ (0,-200)` (signal, top). Bottom of the box = implicit reference.
```
Line[SymbolLine, Normal]:        (0,-200)→(0,-110)      # signal lead into box
RoundedRect[SymbolLine, Normal]: c=(0,0) w=110 h=240 r=12   # frame box (y∈[-120,+120])
Polyline[SymbolLine, Normal]:                                 # internal zigzag (termination R)
  (0,-110) (0,-80) (25,-65) (-25,-35) (25,-5) (-25,25) (25,55) (-25,80) (0,95) (0,110)
```
Core Graphics `Term` = terminating impedance in a port frame. Maps to `SymbolKind.Port`. *(If a plainer port
glyph is preferred, replace with a small circle + lead — the registry decides which kind uses this art.)*

### Ground — stem + filled triangle (Core Graphics style)
Pins: `1 → port 1 @ (0,0)` (the connection point at top of the symbol)
```
Line[SymbolLine, Normal]:           (0,0)→(0,40)           # stem
Triangle[SymbolLine, Normal, filled]: (-45,40) (45,40) (0,90)   # downward triangle (base top, apex bottom)
```
Ground pin sits at origin `(0,0)` (on grid). *(Core Graphics uses the filled downward triangle; if the 3-bar
IEC ground is later preferred, swap the Triangle for three tapering `Line`s.)*

---

## Multi-terminal / box symbols (horizontal, ports left/right)

### FET — 3-terminal (horizontal; geometry retained)
Pins: `1 → gate @ (-200,0)` · `2 → drain @ (200,-100)` · `3 → source @ (200,+100)`
```
Line[SymbolLine, Normal]: (-200,0)→(-80,0)                       # gate lead
Rect[SymbolLine, Normal]: c=(0,0) w=160 h=200                    # body box (x∈[-80,80], y∈[-100,100])
Line[SymbolLine, Normal]: (-80,0)→(-30,0)                        # gate bar
Line[SymbolLine, Normal]: (-30,-70)→(-30,+70)                    # channel
Line[SymbolLine, Normal]: (-30,-50)→(80,-50)  ;  (80,-50)→(200,-100)   # drain
Line[SymbolLine, Normal]: (-30,+50)→(80,+50)  ;  (80,+50)→(200,+100)   # source
Line[SymbolLine, Normal]: (-30,-50)→(-20,-40) ;  (-30,-50)→(-20,-60)   # direction arrow
```
Matches the §4 / §9 3-terminal convention (gate left-center, drain+source right). This is the hand-drawn
reference the auto-gen 3-port special case mirrors.

### ZPort — N-port box (horizontal; ports drawn dynamically)
Pins: variadic (generated by `SymbolPortDefs.GeneratePorts`, left/right, on P). Body only here:
```
Rect[SymbolLine, Normal]: c=(0,0) w=140 h=100        # box (x∈[-70,70])
Polyline[SymbolLine, Normal]: (-40,-30) (40,-30) (-40,30) (40,30)   # inset "Z" mark
```
Port lead stubs are drawn by the renderer per port (not in the static list), as today. Box height grows with
port count at render time (or the auto-gen path produces a sized box).

### Sdd — N-port box (horizontal; ports dynamic)
```
Rect[SymbolLine, Normal]: c=(0,0) w=160 h=100        # box (x∈[-80,80]); no inner mark
```

### Generic — 2-port box (fallback)
Pins: `1 @ (-200,0)` · `2 @ (200,0)`
```
Line[SymbolLine, Normal]: (-200,0)→(-80,0)  ;  (80,0)→(200,0)    # leads
Rect[SymbolLine, Normal]: c=(0,0) w=160 h=100                    # box
```

---

## Special symbols (define + lock; concrete geometry deferred to their cells)

These are needed eventually and are **locked** (`UserEditable=false`, §7.2). Their concrete geometry depends
on cell semantics not yet defined (port counts, what they connect to), so the inventory + intent is fixed
here and the exact primitives are produced when their cells exist:

- **Simulation-analysis symbol** (TestBench cell) — a labeled box/badge identifying the analysis (e.g. "HB",
  "S-PARAM", "DC") placed on the test-bench schematic; likely **no electrical pins** (it is an analysis
  directive, not a wired device) or a single annotation anchor. Geometry: rounded box + `Text` (analysis
  name). Deferred until the TestBench/analysis cell is defined.
- **VAR (variable) symbol** (§7.2 Var tool) — an equation badge: a small box/tag with `Text` "VAR" (or "="),
  **no electrical pins** (variables are name=expression bindings, not wired). Geometry: rounded box + `Text`.
  Deferred until the Var cell is defined.
- **Measurement symbol** — like VAR: an equation badge with `Text` ("Meas"/"="), no electrical pins. Deferred
  until the Measurement cell is defined.

(All three are equation/directive glyphs, not wired devices — they carry text, not pins, which is why their
geometry waits on the cell definitions rather than this art pass.)

---

## Notes for implementation (§11 step 3)

- Build these in the §2 primitive model; render through the generic `DrawSymbol` (§2.2). **Prove parity**
  first by rendering the *current* placeholders through the new model (§11 step 1) before swapping in this
  art, so a model bug and an art change don't mask each other.
- **Flip `SymbolPortDefs`** 2-terminal entries to the vertical pins above (`(0,∓200)`), and the Port to a
  single top pin; keep FET/ZPort/Sdd/Generic horizontal. Re-lay-out the GUI demo (`BuildHero2PA`) for vertical
  passives (§4 consequence).
- **Leads end at pin coordinates** (the renderer draws the pin marker). Every pin coordinate above is on P.
- Body art coordinates are free/on-`p`; only pin tips must be on `P` (all listed pin tips are P-multiples).
- The **tunable** items (cap curve, inductor bumps, polarity dot, sine amplitude, Term internal zigzag) are
  expected to be refined by the owner in the symbol editor once it exists — these numbers are a correct,
  recognizable starting point, not final art.
