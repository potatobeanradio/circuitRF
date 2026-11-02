# Sonnet Brief — Standard-library symbol overhaul (SDD/ZNP autogen + 6 other components)

All work is in `src/Ui/Schematic/` — primarily `BuiltInSymbols.cs` (glyph primitives) and `EditableSchematic.cs`
(`SymbolPortDefs` pin generators). Framework-free files; no Avalonia. Build gate 0W/0E (TreatWarningsAsErrors).

This is a cohesive symbol pass. Read BOTH files first. Pin geometry comes from `SymbolPortDefs.For(kind, n)`; glyph
lines come from `BuiltInSymbols.Build*()`. The renderer draws each pin as a stub from the glyph to the pin tip, so
glyph size and pin positions must be designed together.

---

## PART A — SDD / ZNP autogen symbol (issues #1–#9)

Both SDD and ZPort share `GenerateSddPorts(n)` (2N pins, ± pairs — locked earlier). All of Part A applies to BOTH
unless noted. Pin index order contract is UNCHANGED: `pin[2(p-1)] = "p+"`, `pin[2(p-1)+1] = "p-"`.

### ⚠️ #8 FIRST — pins render as "already connected" on a fresh place (the critical bug)
**Root cause:** `GenerateSddPorts` uses `halfDiff=100` and `portSpacing=300`, producing pin Y values like
`±50, ±150, ±250` for odd-N layouts. The connectivity pass (`ComputeConnectivityGeometry.AddPortDot`) quantizes
each pin to a P-cell via `Math.Round(y / 100)`. **`Math.Round` uses banker's rounding**, so `Math.Round(-0.5)==0`
AND `Math.Round(0.5)==0` — pins at y=−50 and y=+50 collide into the SAME P-cell. `conPointCounts[cell] >= 2` ⇒ both
pins flagged Connected on an empty schematic. Same mechanism for any two pins whose Y differs by <100 or straddles a
half-grid boundary.

**Fix:** every pin tip MUST sit on a whole grid multiple (Y a multiple of 100, the connection grid P=GridSize=100).
No half-grid pin coordinates, ever. Redesign `GenerateSddPorts` so each port pair's two pins are a full 200 apart
(±100 → use ±100 only if the port center is itself on an EVEN multiple of 100 so the pins land on odd... no —
simplest correct rule below). Concretely:
- Pin "+" and "−" of one port are **200 apart** (not 200 with a half-grid center).
- Adjacent port centers are spaced a whole multiple of 100 such that NO two pins across all ports share a P-cell.
- Put every port center on a multiple of 200 and pins at center±100 → all pins land on odd multiples of 100
  (…,−300,−100,+100,+300,…), never colliding, never on a half-grid. Example layout below.

**This is the highest-priority item — verify with a test that places SDD2/SDD3/SDD4/Z2P/Z3P/Z4P on an empty
schematic and asserts EVERY pin's ToRenderComponent state is Unconnected.**

### #9 — Z1P / SDD1 special case: + left, − right, vertically centered
A 1-port (N=1) must NOT use the stacked left-column layout. Special-case `n == 1` in `GenerateSddPorts`:
`pin[0] = "1+"` at `(−200, 0)`, `pin[1] = "1−"` at `(+200, 0)` — both vertically centered, + on left, − on right.
(This applies to both Z1P and SDD1 since they share the generator.)

### #2/#5/#6 — center rectangle: bigger, grows with N, margin around stems
The glyph body (`BuildSdd` / `BuildZPort`) is currently a FIXED small box (±80×±50 / ±70×±50) — pins now float far
outside it. The body must:
- **#2** be larger than today.
- **#6** grow in ±Y as port count rises so it encloses all port rows. Height must span all pin Y positions plus
  margin. Since `Build*()` is a static no-arg cache, the body can't see N — so **move SDD/ZPort body generation to a
  port-count-aware path**: either (a) make `Primitives(kind)` for ZPort/Sdd return a body sized for the instance's
  PortCount (requires plumbing N — heavier), OR (b, PREFERRED) keep `Build*()` returning a UNIT body and have the
  renderer/`ComputeGlyphBb` already extend by pins (it does today via the `ZPort or Sdd` union block). The cleanest:
  add a port-count-aware rounded-rect to the **pin generator's companion** — i.e. introduce a small helper that
  callers already touching `SymbolPortDefs.For(Sdd, n)` use to get the body rect too.

  RECOMMENDATION (confirm if unclear): add `SymbolPortDefs.SddBodyRect(int n)` returning `(cx, cy, w, h, radius)`
  for the rounded center rect, sized to the pin span: half-height = (max |pin Y|) + margin (≥60); width fixed
  (e.g. ±90). Then in `BuiltInSymbols`, make the SDD/ZPort symbol body come from this rect at the instance's N.
  Because `BuildSdd`/`BuildZPort` are no-arg static caches, you'll need the body to be regenerated per N — the
  simplest structural fix is to STOP caching SDD/ZPort bodies as statics and instead build them where N is known.
  Trace how the renderer obtains the body (`BuiltInSymbols.Primitives(kind)`) and thread N (it already threads
  PortCount to `SymbolPortDefs.For`). If threading N into the body is too invasive for this pass, fall back to a
  body sized for a reasonable max (e.g. N up to 4) and note the limitation — but prefer the N-aware rect.

- **#5** pins need horizontal/vertical margin: the rect's half-width should leave space so the left/right pin
  stubs are visibly attached to the rect edge with margin top and bottom of the outermost stems.

### #3 — center rectangle is a ROUNDED rect
Use `RRect(cx, cy, w, h, radius)` (already a helper) for the body instead of the 4 `L(...)` box lines. Radius ~12.

### #4 — pin stems attach to the rectangle
Today the rect is ±80 in X but pins are at ±200, and the renderer draws the stub from the glyph bbox to the pin.
After resizing, ensure the rect's left/right edges are at a fixed X (e.g. ±90) and the stub lines run from that
edge to the pin tip at ±200, so every "+/−" stem visibly touches the rounded rect. Verify the renderer draws
glyph-edge→pin-tip (it extends the body to pins in `ComputeGlyphBb`); the stems should not float detached.

### #1 — remove the "Z" line shape from ZPort
In `BuildZPort`, delete the three Z-mark lines (`Z top`, `Z diagonal`, `Z bottom`). ZPort body becomes the same
rounded rect as SDD (just the rect; the "Z" identification comes from the type label "Z2P" etc., and the port
text in #7).

### #7 — port-number + polarity text primitives
Add small `TextPrimitive`s at each pin labeling the port number and polarity. For pin "p+" show text like `p+`
(or `p` with a small `+`); for "p−" show `p−`. Place each text just inside the rounded rect, adjacent to its stem,
small font (e.g. FontSize 10–12). Use the existing `TextPrimitive` shape (see how `AutoSymbolGenerator` adds port
text: `new TextPrimitive { Content, AnchorX, AnchorY, FontSize, Align }`). Left-side pins: text right-aligned just
inside the left edge; right-side pins: left-aligned just inside the right edge. These are part of the per-N body,
so they live with the N-aware body generation (#6).

### Suggested `GenerateSddPorts` rewrite (grid-safe, #8 + #9 + layout)
```
private static (string Name, float LocalX, float LocalY)[] GenerateSddPorts(int n)
{
    if (n == 1)   // #9 special case: + left, − right, centered
        return [("1+", -200f, 0f), ("1-", +200f, 0f)];

    var ports  = new (string, float, float)[2 * n];
    int nLeft  = (n + 1) / 2;
    int nRight = n / 2;
    const float port = 400f;   // port-center spacing — MULTIPLE OF 200 so pins land on odd*100 (no half-grid)
    const float half = 100f;   // +/- pin offset from port center (pins 200 apart)

    // Left ports: centers symmetric about 0 on a 400 pitch → centers are even*100 ⇒ pins at odd*100.
    for (int p = 0; p < nLeft; p++)
    {
        float cy = (p - (nLeft - 1) * 0.5f) * port;     // ⚠ if nLeft even, centers are ±200,±600… (even*100) ✓
        // GUARD: cy must be a multiple of 100. With port=400 and the (k-0.5) factor for even counts,
        // centers can be ±200 (ok) — but verify: for nLeft=2 → cy ∈ {−200,+200} ✓; pins ∈ {−300,−100,+100,+300} ✓.
        int pn = p + 1;
        ports[2 * p]     = ($"{pn}+", -200f, cy - half);
        ports[2 * p + 1] = ($"{pn}-", -200f, cy + half);
    }
    for (int q = 0; q < nRight; q++)
    {
        float cy = (q - (nRight - 1) * 0.5f) * port;
        int pn = nLeft + q + 1;
        int i  = 2 * (nLeft + q);
        ports[i]     = ($"{pn}+", +200f, cy - half);
        ports[i + 1] = ($"{pn}-", +200f, cy + half);
    }
    return ports;
}
```
**CRITICAL self-check Sonnet must verify:** every returned `LocalY` is an exact multiple of 100. The `(k-0.5)*400`
term yields a multiple of 200 when the count is even and a multiple of 400 when odd — both fine — but DOUBLE-CHECK
for each N=2..6 that no pin Y is a half-grid value (…,−150,−50,50,150,…). If any layout produces a half-grid Y,
adjust `port`/centering so all pins land on multiples of 100. The #8 test below is the gate.

---

## PART B — other standard-library components

### B1 — +/− indicators on ToneSource, Vdc, P1Tone, V1Tone, Term
Add small "+" and "−" text (or short line ticks) **slightly to the LEFT of the vertical stems**. These symbols have
pins at (0,−200) top and (0,+200) bottom; "+" near the top stem, "−" near the bottom stem, offset left in X (e.g.
x ≈ −25). Note: there is no separate `BuildV1Tone` — `V1Tone`/`ToneSource` share `BuildToneSource`; confirm whether
"V1Tone" maps to `SymbolKind.ToneSource` (likely) and add the indicators there once. Vdc already has battery bars —
add the +/− text near the top/bottom leads. Use `TextPrimitive` (FontSize ~12), or small `L()` tick marks for the
+ (two crossed short lines) and − (one short line) if text looks cluttered — pick text for clarity.

### B2 — Pin symbol: horizontal, stem to the RIGHT, hexagon body
Currently `BuildPin` is vertical (lead (0,−200)→(0,−100), square flag at (0,−50)) with the pin at (0,−200).
Reorient horizontal:
- Pin connection point (the lead tip) moves to the RIGHT: update `SymbolPortDefs.For(Pin)` from `("1", 0, -200)` to
  `("1", 200f, 0f)` (tip on the right, on the grid).
- Body to the left of the tip: a **hexagon** whose **top and bottom edges are slightly longer than the side
  edges** (a "home plate"/elongated hexagon lying flat). Build with `Poly(filled:false, ...)` — 6 points. Stem
  `L()` from the hexagon's right vertex to the pin tip at (200,0).
- Keep the Num label behavior (shown via parameters) working.
- Suggested hexagon (centered ~(0,0), pointing right toward the stem): points roughly
  `(−90,−40),(−40,−60),(40,−60),(90,−40)`... no — top/bottom edges longer than sides means a flat-topped hexagon:
  top edge and bottom edge are the long horizontal edges; the left and right are shorter slanted edges. E.g.
  `(−40,−50),(40,−50),(80,0),(40,50),(−40,50),(−80,0)` — top edge (−40..40 = len 80) and bottom edge (len 80)
  longer than the 4 slanted side edges. Tune so it reads as a hexagon; stem from (80,0)→(200,0).

### B3 — VAR symbol: add "VAR" text in the middle
`BuildVar` is a port-less box (±80×±60). Add `new TextPrimitive { Content = "VAR", AnchorX = 0, AnchorY = 0,
FontSize = ~24, Align = Center, VAlign = Middle }` centered in the box.

---

## Tests (`tests/Ui.Tests`)
1. **#8 GATE — Sdd/ZPort_FreshPlace_AllPinsUnconnected:** place SDD2,SDD3,SDD4,SDD5 and Z2P,Z3P,Z4P,Z5P each alone
   in an empty `SchematicEditModel`; `ToRenderComponent` → EVERY port `State == Unconnected`. (This is the bug fix.)
2. **PinYOnGrid:** for SDD/ZPort N=1..6, every `SymbolPortDefs.For(kind,n)` pin `LocalY % 100 == 0` (no half-grid).
3. **Z1P_SpecialCase:** `For(ZPort,1)` → `("1+",-200,0)`,`("1-",+200,0)`.
4. **PinOrderContract_Unchanged:** `For(Sdd,2)` → names `1+,1-,2+,2-` in index order (regression).
5. **SddBodyGrowsWithN:** body rect half-height for N=4 > half-height for N=2 (#6).
6. **ZPort_NoZMark:** ZPort primitives contain no Z-diagonal line (#1) — assert the body is a RRect + stems + text,
   no interior diagonal.
7. **Pin_HorizontalRightTip:** `For(Pin)` → tip at (200,0); Pin primitives contain a 6-point polygon (#B2).
8. **Var_HasVarText:** Var primitives contain a TextPrimitive "VAR" (#B3).
9. **PlusMinusIndicators:** ToneSource/Vdc/P1Tone/Term primitives each contain "+" and "−" text (#B1).
10. **NetExtraction_Sdd3_4_Correct:** place SDD3 (6 pins), wire all + to distinct nets and all − to 0 → extracted
    line has 6 nets in ±-pair order (regression that the new geometry didn't break extraction order).

## Gate
Build 0W/0E; tests green. Manual: place SDD3/Z3P on empty schematic → all pins show DISCONNECTED (open circles, not
connected dots); body is a rounded rect enclosing all port rows with margin; each pin labeled (1+,1−,2+,…); ZPort
has no "Z"; Z1P shows + left / − right centered; Pin is a horizontal hexagon with the stem/tip on the right; VAR
shows "VAR"; ToneSource/Vdc/P1Tone/Term show +/− near their leads.

## On completion
Note in `src/Ui/CLAUDE.md`: SDD/ZPort autogen symbol uses a port-count-aware rounded-rect body (grows in ±Y with N)
with 2N ± pins whose Y coordinates are ALWAYS whole multiples of the connection grid (100) — half-grid pin Y caused
false "connected" state via banker's-rounding P-cell collision (fixed). Z1P/SDD1 special-cased (+ left, − right,
centered). ZPort "Z" mark removed; port-number/polarity text added at each pin. Pin symbol is horizontal (hexagon,
tip on right at (200,0)). VAR shows "VAR" text. ToneSource/Vdc/P1Tone/Term carry +/− indicators left of their stems.
```
