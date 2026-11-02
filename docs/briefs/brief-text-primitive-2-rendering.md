# Brief: text-primitive (2/5) — rendering (rotation + anchor + ForceReadable)

Second of the 5-brief Text-primitive sequence. Brief 1 (model + `TextCenter`/`TextBoxSize` geometry)
is landed. This brief makes text actually **render** at its `Rotation`, positioned by its anchor-derived
center, composed with component rotation/mirror for schematic instances, with the per-text `ForceReadable`
auto-flip. After this, rotating a cell instance rotates its text, and (once brief 3 lands) in-place rotation
in the editor will be visible too.

Size: **M**. Files: `src/Ui/Schematic/SymbolGeometry.cs` (one helper),
`src/Ui/Renderers/SchematicRenderer.cs` (signature + text branch + 2 call sites).
**No change to `SymbolEditorRenderer.cs`** — it calls `DrawSymbol` with the defaults and so shows literal
authored rotation (see below).

## Key idea

Brief 1 made the anchor↔center model: `TextCenter(txt)` already bakes in `Align`/`VAlign`. So the renderer
can draw **every** text centered at its box center with `SKTextAlign.Center`, rotated by the net angle —
no per-Align positioning, no left/right-flip bugs under mirror. Position comes from `LP(center)` (which
applies the component transform); orientation comes from the net angle.

## 1. `SymbolGeometry.cs` — baseline offset helper

Add near `TextCenter` (framework-free; reuses the existing private fracs):
```csharp
    /// <summary>Baseline Y offset from the text box center, in LOCAL units (screen Y-down, +down).
    /// The renderer draws text centered at the box center, so it shifts the baseline by this to
    /// vertically center the glyph box.</summary>
    public static double TextBaselineDyFromCenter(TextPrimitive t)
    {
        var (_, h) = TextBoxSize(t);
        return t.FontSize * TextAscentFrac - h * 0.5;   // ascent below top; box centered at 0
    }
```

## 2. `SchematicRenderer.cs` — DrawSymbol signature

Add a trailing defaulted param (keeps every existing positional caller valid):
```csharp
    internal static void DrawSymbol(
        SKCanvas canvas,
        IReadOnlyList<SymbolPrimitive> primitives,
        double compX, double compY,
        SymbolRotation rotation, bool mirrorX,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme,
        SKPaint? overridePaint = null,
        bool applyForceReadable = false)      // ← NEW: true only for schematic component instances
```
(Optionally update the stale "Text and Bitmap are stubbed" line in the doc-comment.)

## 3. `SchematicRenderer.cs` — replace the TextPrimitive branch

Replace the whole `if (prim is TextPrimitive txt) { … }` block with:
```csharp
            // TextPrimitive — drawn centered at its box center, rotated in place.
            if (prim is TextPrimitive txt)
            {
                SKColor textColor = overridePaint?.Color ?? theme.SymbolLine;
                SKTypeface typeface = txt.FontStyle switch
                {
                    SymbolFontStyle.Bold      => SkiaFonts.PlexBold,
                    SymbolFontStyle.Italic    => SkiaFonts.PlexItalic,
                    SymbolFontStyle.Condensed => SkiaFonts.PlexLight,
                    _                         => SkiaFonts.PlexRegular,
                };
                float fontSize = Math.Max(1f, (float)(txt.FontSize * zoom));
                using var font   = new SKFont(typeface, fontSize);
                using var tPaint = new SKPaint { IsAntialias = true, Color = textColor };

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
            }
```
Notes:
- `rotDeg` (component rotation in degrees) is already computed at the top of `DrawSymbol`; reuse it.
- Mirror (`mirrorX`) affects only the **position** here (via `LP`), not glyph handedness — text is never
  drawn mirror-reversed. For the common horizontal text this is exactly right; rotated-text-on-mirrored-
  instance glyph angle is best-effort, and the `ForceReadable` flip still prevents upside-down.

## 4. `SchematicRenderer.cs` — pass `applyForceReadable: true` for component instances

Only the two **component-instance** body calls in `Draw(...)` opt in (named arg, since `overridePaint`
stays default null):
```csharp
            if (c.CellRefState is CellSymbolState.Resolved && c.CellRefPrimitives is not null)
            {
                DrawSymbol(canvas, c.CellRefPrimitives,
                    cx, cy, c.Rotation, c.MirrorX, panX, panY, zoom, theme,
                    applyForceReadable: true);
            }
```
and the built-in path:
```csharp
                DrawSymbol(canvas, BuiltInSymbols.Primitives(c.Symbol).Primitives,
                    cx, cy, c.Rotation, c.MirrorX, panX, panY, zoom, theme,
                    applyForceReadable: true);
```
Leave **unchanged** (they keep the default `false` ⇒ literal authored rotation, no flip):
- the ghost call in `DrawOverlay` (`…, theme, ghostPaint`),
- `SymbolEditorRenderer.Draw`’s `DrawSymbol(…, R0, false, …)`,
- `SymbolClipboard.RenderSymbol`’s `DrawSymbol(…, R0, false, …)`.

## Why legacy text is unchanged

For Left/Baseline/R0 text: `TextCenter` → `(AnchorX + W/2, AnchorY − 0.25·FontSize)`, `baselineDy` →
`+0.25·FontSize·zoom`, `netDeg` → 0. Centered draw ⇒ baseline lands exactly at `AnchorY`, left edge at
`AnchorX`. Byte-identical to the old `DrawText(..., AnchorX, AnchorY, Left, ...)`.

## Verification (runtime — please confirm)

1. Editor: a text primitive whose `Rotation` is R90/R180/R270 (set it by hand in the .csym for now, since
   the rotate key is brief 3) renders at that literal angle, centered where it was. Legacy R0 text looks
   identical to before.
2. Schematic: place a cell whose symbol has horizontal text; rotate the instance 90° → the text renders
   vertical (rotated with the symbol). At 180° with `ForceReadable=false` it reads upside-down; set that
   text's `ForceReadable=true` (by hand for now; inspector is brief 5) → it stays right-side-up.
3. Mirror a cell instance with horizontal text → text stays readable (not mirror-reversed), repositioned.
4. Clipboard/editor export of a symbol with text is unchanged for R0 text.

## Acceptance

- `DrawSymbol` draws text centered at `TextCenter`, rotated by component+text rotation; legacy R0 text
  pixel-identical.
- Schematic component instances honor `ForceReadable` (auto-flip when upside-down); editor/ghost/export
  always show literal rotation.
- New `SymbolGeometry.TextBaselineDyFromCenter` helper added; `SymbolEditorRenderer` untouched.
