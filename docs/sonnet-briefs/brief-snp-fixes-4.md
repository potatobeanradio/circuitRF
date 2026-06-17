# Sonnet Brief — SnP Ref pin: bring up 100 units for n≥4

One small change in `src/Ui/Schematic/EditableSchematic.cs`, `SymbolPortDefs.GenerateSnpPorts`, the
`refNode` block. Build 0W/0E.

**Issue:** for n≥4 the reference pin sits one grid square (100 units) further from the box than needed,
giving a too-long stem on small components. Bring the pin up 100 (shorten the stem by 100); it must stay
on grid, and the stem must remain between the pin and the box. n≤3 is correct and must not change.

**Cause / arithmetic:** the Ref pin Y is `CeilG(cy + halfH) + 100f`. After the +50 box padding, the n≥4
half-height ends in 50 (`halfH = CeilG(halfSpan) + 50`) and `cy` is grid-aligned, so the box bottom edge
`B = cy + halfH` ends in 50 (e.g. 150). `CeilG(B)` already rounds that UP to the next 100 (200) — a
50-unit stem — and the extra `+ 100f` then pushes the pin a full square further out (300) → a 150-unit
stem. For n≤3, `halfH = 100` and `B` is already on grid (100), so `CeilG(B) + 100 = 200` is the intended
100-unit stem.

**Fix:** drop the `+100` only for n≥4. The current `refNode` block is:
```csharp
if (refNode)
{
    if (n == 1)
        pins[1] = ("Ref", +bodyX, 0f);
    else
    {
        float cy    = SnpBodyCenterY(n, cfg, pitch);
        float halfH = SnpBodyHalfH(n, cfg, p);
        pins[n] = ("Ref", 0f, CeilG(cy + halfH) + 100f);
    }
}
```
Replace the `else` body so the Ref pin sits one grid square below the box for n≤3 (unchanged) but only
just past the (off-grid-by-50) bottom edge for n≥4:
```csharp
if (refNode)
{
    if (n == 1)
        pins[1] = ("Ref", +bodyX, 0f);
    else
    {
        float cy     = SnpBodyCenterY(n, cfg, pitch);
        float halfH  = SnpBodyHalfH(n, cfg, p);
        float bottom = cy + halfH;                 // box bottom edge
        // n<=3: edge is on grid → one full square of stem (edge + 100).
        // n>=4: the +50 box padding makes the edge end in 50, so CeilG already lands one grid
        // point below the edge (a 50-unit stem) — do NOT add another 100, or the pin sits a square
        // too far out. Both cases keep the pin on grid with the stem between the box and the pin.
        pins[n] = ("Ref", 0f, n <= 3 ? CeilG(bottom) + 100f : CeilG(bottom));
    }
}
```

> Worked check (Loose):
> - n=2 (n≤3): cy=0, halfH=100, bottom=100 → `CeilG(100)+100 = 200`. Stem 100→200 = 100. Unchanged. ✓
> - n=4: cy=0, halfH=CeilG(200)+50=250, bottom=250 → `CeilG(250) = 300`. Stem 250→300 = 50, on grid.
>   (Was `CeilG(250)+100 = 400` → 150 stem; now 100 closer.) ✓
> - n=8 taller: bottom=cy+halfH ends in 50 → `CeilG(bottom)` = bottom+50, on grid, 50 stem. ✓

`BuildSnpSymbol` is unchanged — it draws the Ref lead from the box bottom edge to the pin tip
(`L(0, bodyBot, 0, ly)`), so shortening the pin Y automatically shortens the stem; the stem stays
between the box and the pin.

## Gate
Build 0W/0E. Verify:
- n≥4 with RefNode on: the Ref pin is 100 units closer than before, on grid, with a short stem still
  joining it to the box bottom; "Ref" text unchanged (still inside the box bottom edge).
- n≤3 with RefNode on: Ref pin unchanged (100-unit stem below the box).
- `SnpPinsAreGridAligned` still passes (Ref pin Y stays a multiple of 100 in every n/pitch/cfg case).
