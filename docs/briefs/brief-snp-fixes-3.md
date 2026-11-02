# Sonnet Brief — SnP symbol geometry tweaks (3-port centering + N≥4 box padding)

Two small geometry fixes in `src/Ui/Schematic/EditableSchematic.cs` (`SymbolPortDefs` helpers only).
`BuildSnpSymbol` in `BuiltInSymbols.cs` needs NO change — it already reads `SnpBodyCenterY` /
`SnpBodyHalfH` / `SnpBodyRect` for the box and anchors the "Ref" text to the box bottom edge, so both
fixes flow through automatically once the helpers are corrected. Build 0W/0E.

Root cause shared by both: `SnpBodyCenterY` and `SnpBodyHalfH` currently include TOP/BOTTOM stem pins
(3-port's top port; the Ref pin) in the body's vertical extent. The box must be centered and sized by
the LEFT/RIGHT (side) pins ONLY — top/bottom pins are stems that poke out of the box, never part of it.

---

## FIX 1 — 3-port box is off-center and the port-3 stem cuts through it

**Symptom:** the 3-port looks nothing like the 2-port — the rounded-rect is shifted up and port 3's
stem runs through the middle of the box.

**Cause:** for n=3 the signal pins are at y = {0 (port1), 0 (port2), −200 (port3)}. `SnpBodyCenterY`
averages all three → midpoint of [−200, 0] = −100, snapped to −100. So the box is built at cy=−100 with
halfH=100 → spans y∈[−200, 0]. Ports 1/2 (at y=0) sit on the box's bottom edge and port 3 enters at the
very top — the box is high and the stem appears to cut through because the box bottom is at y=0 where
ports 1/2 are.

**Desired (matches the 2-port):** a 200×200 square centered at the origin (cy=0, halfH=100), ports 1/2
at mid-left/right (y=0), and port 3 a short STEM rising from the box top edge (0,−100) to the tip
(0,−200) — exactly the 2-port square plus one top stem.

**Fix — exclude top/bottom pins from the body center & height.** In `SnpBodyCenterY`, compute the
midpoint of the SIDE pins only (those with |LocalX| == bodyX), and in `SnpBodyHalfH` keep n≤3 at the
fixed 200×200 square. Replace the two helpers:

```csharp
// Body center Y = midpoint of the SIDE pins only (left/right). Top/bottom pins (3-port's port 3,
// the Ref pin) are stems that extend BEYOND the box and must not pull the box off-center.
private static float SnpBodyCenterY(int n, SnpPinConfig cfg, SnpPitch pitch)
{
    var pins = GenerateSnpPorts(n, refNode: false, cfg, pitch);
    var side = pins.Where(q => Math.Abs(q.LocalX) >= 199f).ToArray();   // left/right pins only
    if (side.Length == 0) return 0f;                                     // (n=1 falls here → 0)
    float minY = side.Min(q => q.LocalY), maxY = side.Max(q => q.LocalY);
    return SnapG((minY + maxY) * 0.5f);
}
```
(For n=1/2/3 the side pins are all at y=0, so center = 0 — the box is centered at the origin and port 3
becomes a clean top stem. ✓)

`SnpBodyHalfH` already returns 100 for n≤3, so the n=3 box is the 200×200 square. Confirm that branch is
unchanged (see Fix 2 for the n≥4 branch).

> Why `>= 199f` rather than `== bodyX`: bodyX is 200; guard against float drift. Side pins are exactly
> ±200; top/bottom pins are at x=0. The threshold cleanly separates them.

---

## FIX 2 — For n≥4, pad the box ±50 in y (and the Ref text follows)

**Request:** for n≥4, extend the rounded-rect 50 units further in BOTH +y and −y. The "Ref" text
primitive should move an extra 50 with the (now-lower) bottom edge. The Ref PIN stays on grid where it
is (growing the box toward it by 50 does not push the pin off-grid).

**Fix — add 50 to the n≥4 half-height.** In `SnpBodyHalfH`, only the n≥4 branch changes:
```csharp
private static float SnpBodyHalfH(int n, SnpPinConfig cfg, float p)
{
    if (n <= 3) return 100f;            // 1/2/3-port: 200-tall square (unchanged)
    int nLeft = (n + 1) / 2;
    float halfSpan = (nLeft - 1) * 0.5f * p;
    return CeilG(halfSpan) + 50f;       // grid-aligned side-pin span, padded +50 each side (item 1)
}
```
Effect, all automatic via the existing `BuildSnpSymbol`:
- Box grows 50 further up and 50 further down (halfH += 50), so the top/bottom side pins now sit 50
  inside the box edges instead of exactly on them — a cleaner look.
- The "Ref" text is drawn at `bodyBot − 22`; since `bodyBot = cy + halfH` grew by 50, the text moves
  down 50 with the edge. ✓ (No change needed in `BuildSnpSymbol`.)
- The body half-height is `CeilG(halfSpan) + 50`. `CeilG(halfSpan)` is a multiple of 100; +50 makes
  `halfH` end in 50, so `bodyBot = cy + halfH` ends in 50 (cy is grid-aligned). That's fine for a body
  EDGE (edges need not be on grid).

**Ref pin must stay on grid and not move.** The Ref pin Y is computed in `GenerateSnpPorts` as
`CeilG(cy + halfH) + 100`. With the +50, `cy + halfH` now ends in 50; `CeilG` rounds that UP to the next
100, then +100. Verify the Ref pin remains a multiple of 100 (it does — `CeilG` guarantees it) and that
it still sits one clear square below the padded box bottom (it does — `CeilG(bodyBot) ≥ bodyBot`, then
+100). No change to the Ref-pin formula is required; just confirm with the test below.

> Net: the only line that changes for Fix 2 is `SnpBodyHalfH`'s n≥4 return (`CeilG(halfSpan)` →
> `CeilG(halfSpan) + 50`). Everything else (box draw, Ref text anchor, Ref pin) follows.

---

## Gate
Build 0W/0E. Verify:
- **3-port:** box is a 200×200 square centered at the origin (cy=0); ports 1/2 at (∓200,0) on the
  left/right mid-edges; port 3 is a top stem from (0,−100) to (0,−200); with RefNode on, the Ref pin is
  a bottom stem and "Ref" sits just inside the bottom edge. Visually consistent with the 2-port.
- **n≥4:** the box is 50 units taller on each side than the side-pin span; side pins sit 50 inside the
  top/bottom edges; "Ref" text sits just inside the (now-lower) bottom edge; the Ref PIN is unchanged
  and on grid.
- All signal + Ref pin tips remain on multiples of 100 for n ∈ {1,2,3,4,5,6,8} × pitch{Tight,Loose} ×
  cfg{Standard,SplitLR,DualRow} × refNode{off,on} — the existing `SnpPinsAreGridAligned` test must still
  pass unchanged (these fixes only move the BOX and the Ref TEXT, never the pin tips).

No new test needed beyond confirming `SnpPinsAreGridAligned` still passes; optionally add an assertion
that for n=3 the body center is 0 and for n≥4 `SnpBodyRect(n).HalfH` equals the old value + 50.
