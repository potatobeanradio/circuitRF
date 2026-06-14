# Brief: text-primitive — 3 fixes (rotate pivot, live drag, anchor snap)

Three runtime bugs on the symbol Text primitive, each traced to a specific cause. Independent edits in
three files. Sizes: 1 = XS, 2 = S, 3 = M.

---

## Fix 1 — R rotates a text about the origin (flings it off-screen)

**Cause:** `RotateSelectionCommand.ComputeCenter` derives the pivot from `SymbolGeometry.ComputeBb(prims)`,
but `ComputeBb` **intentionally skips Text and Bitmap** (no font/image extent). A text-only selection
therefore contributes nothing, `any` stays false, and the pivot collapses to `(0,0)`. Rotating about the
origin throws a text far from origin across (and out of) the canvas.

**File:** `src/Ui/Commands/Symbol/RotateSelectionCommand.cs`, in `ComputeCenter`.

Replace the prims block:
```csharp
        if (prims.Count > 0)
        {
            var (bx0, by0, bx1, by1) = SymbolGeometry.ComputeBb(prims);
            if (bx0 < minX) minX = bx0;
            if (by0 < minY) minY = by0;
            if (bx1 > maxX) maxX = bx1;
            if (by1 > maxY) maxY = by1;
        }
```
with a per-primitive union via `BboxOf` (which *does* handle Text/Bitmap, centered on the text box):
```csharp
        // Use BboxOf per-primitive, NOT ComputeBb: ComputeBb skips Text/Bitmap, which would collapse a
        // text-only selection's pivot to the origin and fling the text across the canvas on rotate.
        foreach (var prim in prims)
        {
            var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(prim);
            if (bx0 < minX) minX = bx0;
            if (by0 < minY) minY = by0;
            if (bx1 > maxX) maxX = bx1;
            if (by1 > maxY) maxY = by1;
        }
```
For a lone text the pivot is now its own box center, so R rotates it in place (the Rotation-advance from
the earlier brief already cycles the glyph orientation). Mixed selections rotate about the true combined
center. (`BboxOf` enforces a symmetric ±5 min half-size — negligible and centered, so it doesn't shift the
pivot.)

---

## Fix 2 — text doesn't render live while dragging; remove its dashed selection box

**Cause:** in `SymbolEditorRenderer`, the base pass draws the committed text at its old position, and the
selection overlay for text only draws a translated dashed bbox — the glyphs never move. The dashed box is
also what the user wants gone.

**File:** `src/Ui/Renderers/SymbolEditorRenderer.cs`.

**(a) Base pass — suppress a live-dragged text** (mirror the existing live-bitmap suppression, so the
overlay can redraw the glyphs at the offset without doubling). Replace:
```csharp
        int liveBitmapIdx = -1;
        {
            var (ddx, ddy) = overlay.LiveDragOffset;
            bool dragging  = ddx != 0 || ddy != 0;
            bool resizing  = overlay.InProgressPrimitive is BitmapPrimitive;
            if ((dragging || resizing) && overlay.SelectedIndices.Count == 1)
            {
                int sel = overlay.SelectedIndices.First();
                if (sel >= 0 && sel < symbol.Primitives.Count && symbol.Primitives[sel] is BitmapPrimitive)
                    liveBitmapIdx = sel;
            }
        }

        // Draw the symbol at local origin, no rotation, using the editor pan/zoom.
        // When a bitmap is live (drag/resize), draw everything except that one primitive.
        IReadOnlyList<SymbolPrimitive> basePrims = liveBitmapIdx < 0
            ? symbol.Primitives
            : symbol.Primitives.Where((_, i) => i != liveBitmapIdx).ToList();
```
with:
```csharp
        // Primitives the OVERLAY pass redraws at the live position; suppress their committed copies in the
        // base pass so they don't appear twice (old + moving):
        //  • a live-dragged/resized bitmap (opaque image would double up), and
        //  • any live-dragged text (glyphs are re-drawn at the offset by the selection overlay).
        var suppressed = new HashSet<int>();
        {
            var (ddx, ddy) = overlay.LiveDragOffset;
            bool dragging  = ddx != 0 || ddy != 0;
            bool resizing  = overlay.InProgressPrimitive is BitmapPrimitive;
            if ((dragging || resizing) && overlay.SelectedIndices.Count == 1)
            {
                int sel = overlay.SelectedIndices.First();
                if (sel >= 0 && sel < symbol.Primitives.Count && symbol.Primitives[sel] is BitmapPrimitive)
                    suppressed.Add(sel);
            }
            if (dragging)
                foreach (int sel in overlay.SelectedIndices)
                    if (sel >= 0 && sel < symbol.Primitives.Count && symbol.Primitives[sel] is TextPrimitive)
                        suppressed.Add(sel);
        }

        IReadOnlyList<SymbolPrimitive> basePrims = suppressed.Count == 0
            ? symbol.Primitives
            : symbol.Primitives.Where((_, i) => !suppressed.Contains(i)).ToList();
```

**(b) Selection overlay — replace the dashed box with re-drawn glyphs at the live offset.** In
`DrawSelectionOverlay`, replace the text branch:
```csharp
                if (prim is TextPrimitive)
                {
                    // Text: corrected bbox box highlight (Layer 7).
                    var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(prim);
                    bx0 += dx; by0 += dy; bx1 += dx; by1 += dy;
                    float sx0 = (float)((bx0 - panX) * zoom);
                    float sy0 = (float)((by0 - panY) * zoom);
                    float sx1 = (float)((bx1 - panX) * zoom);
                    float sy1 = (float)((by1 - panY) * zoom);
                    float margin = (float)Math.Max(2.0, zoom * 2.0);
                    var rect = SKRect.Create(sx0 - margin, sy0 - margin,
                                             sx1 - sx0 + 2 * margin, sy1 - sy0 + 2 * margin);
                    canvas.DrawRect(rect, boxFill);
                    canvas.DrawRect(rect, boxStroke);
                }
```
with:
```csharp
                if (prim is TextPrimitive)
                {
                    // Re-draw the glyphs in the selection accent at the live-drag offset: moves the text
                    // live during a drag and signals selection by colour — no dashed box.
                    using var selText = new SKPaint
                    {
                        IsAntialias = true, Style = SKPaintStyle.Fill,
                        Color       = theme.SelectionBox,
                    };
                    SchematicRenderer.DrawSymbol(
                        canvas, [prim],
                        compX: 0, compY: 0,
                        rotation: SymbolRotation.R0, mirrorX: false,
                        panX: panX - dx, panY: panY - dy, zoom: zoom,
                        theme: theme,
                        overridePaint: selText);
                }
```
`DrawSymbol`'s text branch honours `overridePaint.Color` for the glyph fill, so the selected text renders
in the accent colour at rest and tracks the cursor while dragging. **`boxFill` is now unused — delete its
`using var boxFill = …` declaration** (the bitmap branch still uses `boxStroke`, so keep that one).

---

## Fix 3 — (Align, VAlign) corner doesn't land on the grid

**Cause:** the text branch in `SchematicRenderer.DrawSymbol` draws with `SKTextAlign.Center` centred on the
box centre derived from the *estimated* width (`TextBoxSize` = `0.58·len·FontSize`). The real glyph advance
differs from the estimate, and the error grows with string length, so a Left-anchored string's actual left
edge drifts away from `AnchorX` — the snapped anchor no longer coincides with the visible corner.

**Fix:** anchor the drawn text to the real (Align, VAlign) corner using Skia's actual metrics
(`MeasureText` + `GetFontMetrics`). Keep the centred-draw + center-flip structure (so `ForceReadable` still
flips in place); only the centre is now computed from the anchor with the measured width/ascent/descent.

**File:** `src/Ui/Renderers/SchematicRenderer.cs`, `DrawSymbol`, the `TextPrimitive txt` branch. Replace
from the `// Center in screen space` comment through the trailing `continue;`:
```csharp
                // Center in screen space — anchor→center bakes Align/VAlign; LP applies the
                // component rotation/mirror/pan/zoom to the point.
                var (lcx, lcy) = SymbolGeometry.TextCenter(txt);
                var (cxp, cyp) = LP(lcx, lcy);

                // Net glyph angle = component rotation + the text's own rotation (CW, screen Y-down).
                double textRotDeg = txt.Rotation switch
                {
                    SymbolRotation.R90  =>  90.0,
                    SymbolRotation.R180 => 180.0,
                    SymbolRotation.R270 => 270.0,
                    _                   =>   0.0,
                };
                double netDeg = rotDeg + textRotDeg;

                // Readability auto-flip — schematic instances only, opt-in per text. Flip 180° about
                // the center when the net angle would render upside-down; centered drawing keeps the
                // box in place. Default ForceReadable=false ⇒ rigid (no flip).
                if (applyForceReadable && txt.ForceReadable)
                {
                    double n = ((netDeg % 360.0) + 360.0) % 360.0;
                    if (n > 90.0 && n <= 270.0) netDeg += 180.0;
                }

                float baselineDy = (float)(SymbolGeometry.TextBaselineDyFromCenter(txt) * zoom);

                int save = canvas.Save();
                canvas.Translate(cxp, cyp);
                canvas.RotateDegrees((float)netDeg);
                canvas.DrawText(txt.Content, 0f, baselineDy, SKTextAlign.Center, font, tPaint);
                canvas.RestoreToCount(save);
                continue;
```
with:
```csharp
                // Net glyph angle = component rotation + the text's own rotation (CW, screen Y-down).
                double textRotDeg = txt.Rotation switch
                {
                    SymbolRotation.R90  =>  90.0,
                    SymbolRotation.R180 => 180.0,
                    SymbolRotation.R270 => 270.0,
                    _                   =>   0.0,
                };
                double netDeg = rotDeg + textRotDeg;

                // Actual font metrics (px at this zoom) so the (Align,VAlign) corner lands EXACTLY on the
                // primitive's anchor, instead of the estimated box width that drifts with string length.
                font.GetFontMetrics(out var fm);
                float ascPx  = -fm.Ascent;                     // distance above baseline (px)
                float descPx =  fm.Descent;                    // distance below baseline (px)
                float boxHpx =  ascPx + descPx;
                float awPx   =  font.MeasureText(txt.Content);  // real advance width (px)

                // Anchor offset from the box centre, unrotated text frame, WORLD units.
                double ox = txt.Align switch
                {
                    SymbolTextAlign.Center => 0.0,
                    SymbolTextAlign.Right  => +awPx * 0.5 / zoom,
                    _                      => -awPx * 0.5 / zoom,   // Left
                };
                double oy = txt.VAlign switch
                {
                    SymbolTextVAlign.Top    => -boxHpx * 0.5 / zoom,
                    SymbolTextVAlign.Middle =>  0.0,
                    SymbolTextVAlign.Bottom => +boxHpx * 0.5 / zoom,
                    _                       => (-boxHpx * 0.5 + ascPx) / zoom,   // Baseline (legacy)
                };

                // Local box centre = Anchor − Rot(textRotation, offset); LP then applies the component
                // rotation/mirror/pan/zoom, so mirrored/rotated instances stay correct.
                var (orx, ory) = txt.Rotation switch
                {
                    SymbolRotation.R90  => (-oy,  ox),
                    SymbolRotation.R180 => (-ox, -oy),
                    SymbolRotation.R270 => ( oy, -ox),
                    _                   => ( ox,  oy),
                };
                var (cxp, cyp) = LP(txt.AnchorX - orx, txt.AnchorY - ory);

                // Readability auto-flip — schematic instances only, opt-in per text. Flip 180° about the
                // box centre (centred draw keeps it in place). Default ForceReadable=false ⇒ rigid.
                if (applyForceReadable && txt.ForceReadable)
                {
                    double n = ((netDeg % 360.0) + 360.0) % 360.0;
                    if (n > 90.0 && n <= 270.0) netDeg += 180.0;
                }

                float baselineDy = (ascPx - descPx) * 0.5f;   // baseline offset from box centre (px)

                int save = canvas.Save();
                canvas.Translate(cxp, cyp);
                canvas.RotateDegrees((float)netDeg);
                canvas.DrawText(txt.Content, 0f, baselineDy, SKTextAlign.Center, font, tPaint);
                canvas.RestoreToCount(save);
                continue;
```
This pins the Left edge to `AnchorX` for Left, centres for Center, pins the Right edge for Right; and pins
the box top/middle/bottom/baseline to `AnchorY` per VAlign — all using real measurements, so placement and
snapping land where expected. `font` and `tPaint` are already in scope above in this branch.

**Note (acceptable residual):** `BboxOf`/hit-test/rotation pivot still use the estimated box (they stay
framework-free, no Skia), so a rotated text may shift by a sub-character amount per R step — bounded and
minor, unlike the origin-fling of Fix 1. Don't change `SymbolGeometry` for this.

---

## Verification

1. Place a text far from origin, select it, press **R** → it spins in place about its own centre and stays
   on screen (no fling); undo reverses each step.
2. Drag a selected text → the glyphs move live with the cursor (no second copy left behind), and there is
   **no dashed rectangle** — the selected text simply shows in the accent colour.
3. Set Align=Left, VAlign=Bottom on a text and place/drag it → the bottom-left of the text sits on the grid
   intersection (left edge at the snapped X, descender line at the snapped Y). Try Center/Right and
   Top/Middle/Baseline → each corresponding edge lands on the anchor. Longer strings stay anchored (no
   length-dependent drift).
4. Schematic cell instances: rotating an instance still rotates its symbol text; `ForceReadable` text still
   flips to stay upright and keeps its position.

## Acceptance

- R rotates a lone text in place about its centre; no off-screen jump.
- Dragged text renders live; no dashed selection box (accent-colour glyphs indicate selection).
- The (Align, VAlign) corner coincides with the snapped anchor for all 3×4 combinations, independent of
  string length.
