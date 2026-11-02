# Sonnet Brief — Data Display renderer: IBM Plex missing-glyph fix (∠ and ▲/▼)

**Context:** Pre-7.2 cleanup. Skia-rendered Data Display text shows **box glyphs** for `∠` (U+2220, in
complex-value strings like `12.3∠45.0°`) and for the Table freq-column **sort arrows** `▲`/`▼`
(U+25B2/U+25BC). Root cause: the 7.1b font retarget moved the renderers from DejaVu to **IBM Plex Sans**, and
**Plex has no glyph** for those code points; `SKTypeface.FromStream` does no fallback, so Skia draws `.notdef`
(the box). `°` (U+00B0) is fine — Plex has it. **Not an encoding issue** — `∠` already *is* `0x2220`, so
char-code substitution changes nothing. Fix = (1) draw the sort arrow as an `SKPath` (no font dependency), and
(2) per-glyph **DejaVu fallback** for any code point Plex lacks (Plex stays primary everywhere). Files:
`DataDisplay/Renderers/TableRenderer.cs`, `DataDisplay/Renderers/TraceRenderer_MarkerRenderer.cs`
(MarkerRenderer section), and a small helper (in `Renderers/SkiaFonts.cs` or a new
`DataDisplay/Renderers/RendererText.cs`). Do **not** revert the Plex retarget.

## Part 1 — Table sort arrow → `SKPath` (remove the glyph)
In `TableRenderer`:
- `DrawHeaderRow`: stop appending `" ▲"`/`" ▼"` to the freq header string. Draw the freq header text alone,
  then draw a small **filled triangle** (up if `plot.TableViewAscendingSortOrder`, else down) as an `SKPath`
  just right of the header text within the freq column — mirror the existing `DrawMarkerGlyph` path pattern
  (size ≈ `fs * 0.6f`, theme text color, `IsAntialias = true`).
- `CalcFitWidth`: the freq-column branch currently measures `headerText` **including** the arrow glyph. Replace
  the glyph with a **fixed reserved width** for the drawn triangle (e.g. `fs * 0.6f + TextCellPaddingX`) added
  to the measured arrow-less header text, so auto-fit still leaves room.
- The in-cell marker triangle is already an `SKPath` (`DrawMarkerGlyph`) — leave it; only the header arrow is a
  glyph today.

## Part 2 — per-glyph DejaVu fallback for `∠` (and any non-Plex glyph)
All affected draw sites are **left-aligned** (`TableRenderer`: `CellDataHorizAlign`/`CellHeaderHorizAlign` are
`SKTextAlign.Left`; `MarkerRenderer.DrawInfoBox`/`DrawSymbol` draw at an explicit x), which makes a left-advance
run-splitter exact and simple — no center/right width-reconciliation needed.

Add a helper:
```csharp
// Draws `text` left-aligned at (x, y), using `primary` (Plex) per glyph,
// falling back to `fallback` (DejaVu) for any rune the primary typeface lacks.
// Returns total advance width. All current callers are left-aligned.
public static float DrawLeftTextWithFallback(
    SKCanvas canvas, string text, float x, float y,
    SKFont primary, SKFont fallback, SKPaint paint)
{
    float penX = x;
    foreach over consecutive runs of text grouped by
        (primary.Typeface.GetGlyph(rune.Value) != 0):   // 0 == missing glyph
        var f = covered ? primary : fallback;
        canvas.DrawText(run, penX, y, SKTextAlign.Left, f, paint);
        penX += f.MeasureText(run);
    return penX - x;
}
// plus a matching Measure(text, primary, fallback) that sums run widths.
```
Use `text.EnumerateRunes()` to group (handles any non-BMP safely; the real cases are BMP). Verify the exact
SkiaSharp 3.119.4 glyph-coverage API — `SKTypeface.GetGlyph(int codepoint)` returning 0 for missing, or
`SKFont.ContainsGlyph` — and use whichever is correct.

Wire it in:
- **`TableRenderer`** — `Draw()` and `CalcFitWidth()` already build `PlexRegular`/`PlexBold` `SKFont`s; build
  matching **`DejaVuRegular`/`DejaVuBold`** `SKFont`s at the same size. `DrawClippedText` takes a `fallback`
  `SKFont` and, for the (always-Left) draw, calls `DrawLeftTextWithFallback` instead of `canvas.DrawText`;
  `TruncateMiddle` can keep measuring with the primary font (the `∠` cells are short — the tiny advance
  difference is negligible). In `CalcFitWidth`, measure cell text with the fallback-aware `Measure` so a `∠`
  cell isn't under-measured.
- **`MarkerRenderer`** — `DrawInfoBox` draws each line with `PlexBold`/`PlexRegular`; build the matching DejaVu
  fallback per line and draw via `DrawLeftTextWithFallback`. `MeasureInfoBox` uses the fallback-aware `Measure`
  so the box is sized for the real `∠` width. (`DrawSymbol` draws only the marker name — no special glyphs —
  but routing it through the helper is harmless and future-proofs it.)

## Completeness sweep
Grep the Data Display renderers (`TableRenderer`, `TraceRenderer_MarkerRenderer`, `AxesRenderer`, `PlotRenderer`)
for the literal glyphs `∠` (U+2220), `▲` (U+25B2), `▼` (U+25BC), and any other Mathematical-Operators /
Geometric-Shapes symbols drawn as text with a Plex `SKFont`. Route every such left-aligned `canvas.DrawText`
through the fallback helper (or convert drawn shape-glyphs to `SKPath`). `°` (U+00B0) is in Plex — no change
needed, and the helper covers it for free regardless.

## Gate (verify in the running app)
1. A complex-value **Table** cell (MA/DB format) shows `12.3∠45.0°` with a real angle symbol — no box; the
   freq-column header shows a crisp up/down **triangle** that flips on sort-order toggle (double-click freq
   header) — no box; auto-fit (double-click the freq column's resize edge) still sizes correctly.
2. A **marker info box** on a Smith/Polar plot shows `∠` correctly in its value line — no box.
3. Plex remains the font for all other plot text (digits, labels, headers); only `∠` (and any other
   Plex-missing glyph) is sourced from DejaVu. Builds green; no regression to Rect/Smith/Polar/Table rendering
   or export/copy.

## On completion
Note the fix in `src/Ui/CLAUDE.md` (Data Display renderers: per-glyph DejaVu fallback for Plex-missing glyphs;
table sort arrow drawn as a path). This is a pre-7.2 cleanup — no `data-display.md` sub-phase change. Next: **7.2**.
