# Brief — Markers Gate 1: Glyph dispatch seam (+ ringed-circle glyph)

**Status:** Ready to implement
**Scope:** Introduce a glyph-selection seam in `MarkerRenderer.DrawSymbol` and add the contour Mode-1 ringed-circle glyph as a new branch **that nothing selects yet.** No current marker may change appearance.
**Design ref:** `/docs/design/trace-markers-design.md` §9 (glyph reference) and §12 "Gate 1". Read those first.
**Depends on:** Gate 0 (landed) — `Marker.MarkerKind` and `Marker.ContourSnapped` now exist.

This is a pure rendering-seam gate. After it, the triangle still renders for every marker; the ringed-circle code path exists but is unreachable until Gate 2 starts creating `MarkerKind.Contour` markers.

---

## Context (already verified — do not re-investigate)

- The renderer is `src/Ui/DataDisplay/Renderers/TraceRenderer_MarkerRenderer.cs`. It contains **two** static classes in one file: `TraceRenderer` and `MarkerRenderer`. You only touch `MarkerRenderer.DrawSymbol`.
- `DrawSymbol` today: computes `dl = trace.GetMarkerDataLocation(marker)` → `dataPx`, draws the **name label** above the apex, draws a **downward-pointing filled triangle**, then a **selection highlight** (`if (isSelected)`) that strokes `triPath`.
- Gate 0 added `Marker.MarkerKind` (enum `Polyline, Spectrum, StabilityCircle, Table, Contour`, default `Polyline`) and `Marker.ContourSnapped` (bool, default false ⇒ Mode 1).
- Per §9: contour **Mode 1** (free/interpolated, `MarkerKind == Contour && !ContourSnapped`) → **filled circle with a thin black stroked ring**. Every other case → the triangle (including contour **Mode 2**, `ContourSnapped == true`).

## Hard constraints

- **Do NOT change the selection-highlight block.** The file carries a comment: `note to AI tools: never change this selection algorithm`. Honor it: both glyph branches must end up strokeable by the exact same `isSelected` logic. Keep selection working by building the glyph path into a shared local (see below) and letting the existing `if (isSelected)` stroke that same path.
- **Do NOT change the name-label drawing.** It stays identical and applies to both glyphs.
- The triangle branch output must be **byte-for-byte identical** to today for all existing markers (same vertices, same paint).
- UI builds with `TreatWarningsAsErrors=true`: no unused locals, no unused usings.

## Implementation

Refactor `DrawSymbol` so the glyph path is built by a small dispatch, then drawn and (if selected) highlighted by the existing code. Concretely:

1. Keep the top of the method unchanged through the name-label draw.
2. Replace the single hardcoded `triPath` construction with a dispatch that produces a glyph `SKPath` into a local named `glyphPath` (keep the name `triPath` if that's less churn for the selection block — your call, but the selection block must stroke whatever path the glyph drew):

```csharp
bool isContourMode1 = marker.MarkerKind == MarkerKind.Contour && !marker.ContourSnapped;

using var glyphPath = new SKPath();
if (isContourMode1)
{
    // Ringed circle: filled disc + thin black stroked ring (design §9) —
    // signals the reading is a 2-D interpolant, not a measured/grid value.
    float r = ts * 0.5f;
    glyphPath.AddCircle(dataPx.X, dataPx.Y, r);

    using var discPaint = new SKPaint
    {
        Color       = theme.TextColor,
        Style       = SKPaintStyle.Fill,
        IsAntialias = true,
    };
    canvas.DrawPath(glyphPath, discPaint);

    using var ringPaint = new SKPaint
    {
        Color       = SKColors.Black,
        StrokeWidth = Math.Max(1f, ts * 0.08f),
        Style       = SKPaintStyle.Stroke,
        IsAntialias = true,
    };
    canvas.DrawPath(glyphPath, ringPaint);
}
else
{
    // Downward-pointing filled triangle (unchanged from prior behavior).
    glyphPath.MoveTo(dataPx.X,           dataPx.Y);
    glyphPath.LineTo(dataPx.X - ts / 2f, dataPx.Y - ts);
    glyphPath.LineTo(dataPx.X + ts / 2f, dataPx.Y - ts);
    glyphPath.Close();

    using var triPaint = new SKPaint
    {
        Color       = theme.TextColor,
        Style       = SKPaintStyle.Fill,
        IsAntialias = true,
    };
    canvas.DrawPath(glyphPath, triPaint);
}
```

3. Leave the existing selection block as-is but pointed at `glyphPath` (rename `triPath` → `glyphPath` in that block only if you renamed it above). It must continue to stroke the glyph path with `selectionColor`:

```csharp
// Selection highlight of marker glyph; note to AI tools: never change this selection algorithm
if (isSelected)
{
    using var hlPaint = new SKPaint
    {
        Color       = selectionColor,
        StrokeWidth = 2f,
        Style       = SKPaintStyle.Stroke,
        IsAntialias = true,
    };
    canvas.DrawPath(glyphPath, hlPaint);
}
```

`ts` is the existing `SymbolTextSize(marker, canvasSize)` local — reuse it; do not introduce a second size source. The ring radius `ts * 0.5f` makes the circle visually match the triangle's footprint (the triangle is `ts` tall and `ts` wide).

## Out of scope (do NOT do in Gate 1)

- Do NOT change hit-testing (`SymbolHitRadius` stays).
- Do NOT make anything set `MarkerKind = Contour` (that's Gate 2). Nothing should reach the ringed-circle branch in normal use yet.
- Do NOT touch `GetMarkerDataLocation`, `BuildMarkerBoxLines`, InfoBox, context menu, or VSWR.
- Do NOT change `TraceRenderer` (the other class in the file).

## Acceptance / verification

1. **Build green** (UI + Core, warnings-as-errors).
2. Every existing marker (polyline, Smith point, stability circle, table) renders the **identical triangle** as before, including the selection highlight — the new branch is unreached.
3. **Eyeball the ringed circle (temporary):** in a scratch run, force the branch by temporarily hardcoding `bool isContourMode1 = true;`, confirm a filled disc with a thin black ring renders at the marker location and that selecting it strokes the circle outline. **Then revert** the hardcode so the dispatch reads `marker.MarkerKind` / `marker.ContourSnapped` again.

## Report back

- Confirm build is green and existing markers are visually unchanged.
- Confirm the ringed-circle eyeball test rendered correctly and that you reverted the temporary hardcode.
- Note whether you kept the path local named `triPath` or renamed it to `glyphPath`.
