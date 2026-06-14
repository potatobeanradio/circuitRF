# Brief: polish-symbol-export-pins (B18) — symbol PDF/SVG/PNG export includes pins at screen scale

**Goal.** Copying a symbol (or a symbol selection) to the clipboard should produce vector/raster images
(PDF, SVG, PNG) that include the **pin markers** — dot + port label — drawn at the same scale the editor
uses, and sized so pins aren't clipped. Today the symbol clipboard export renders primitives only; pins
are missing from both the image and the page bounds.

Size: **M**. Files: `src/Ui/Renderers/SymbolEditorRenderer.cs`, `src/Ui/Clipboard/SymbolClipboard.cs`.

## Scope note — the schematic side is already correct (no change)

Verified in `SchematicRenderer.Draw` (the exact method `SchematicClipboard` exports through): component
port markers (`DrawPortMarkers`), connection dots (`DotHalfSize`), and unconnected-endpoint boxes
(`PortBoxHalf`) are drawn in the **base pass** (not the overlay, which the export passes as null), using
**world-unit** sizes (`PortBoxHalf = 8f` "world units; zoom=1 → 8px", `DotHalfSize = 5f`, each with a
`max(3, zoom*…)` floor). So the schematic export already includes pins at screen-matching relative scale.
**Do not modify the schematic path.** (If the owner saw a specific schematic-export pin problem, capture the
exact symptom separately — nothing in the current path drops or mis-scales them.)

## Root cause (symbol side)

`SymbolClipboard.RenderSymbol` only calls `SchematicRenderer.DrawSymbol(...)` (primitives), and
`CopyAsync` sizes the page from `SymbolGeometry.ComputeBb(primitives)` — pins are never drawn and never
unioned into the bounds. The editor's on-screen pin rendering lives in
`SymbolEditorRenderer.DrawPinMarkers` (private, overlay-aware). We reuse its scale via a new plain
(no-selection) variant.

## Fix

### 1. `SymbolEditorRenderer` — add a plain pin-marker renderer

Add an `internal static` method (next to `DrawPinMarkers`). It draws each pin as the editor does, minus
selection/ghost state. **Keep the `r` / `strokeW` / `fontSize` formulas identical to `DrawPinMarkers`** so
exports match the screen:

```csharp
    /// <summary>
    /// Draws plain pin markers — filled dot + port label — at the editor's on-screen scale, with no
    /// selection/overlay state. Shared by the symbol clipboard export (PDF/SVG/PNG) so exported images
    /// match the editor. Keep the r / strokeW / fontSize formulas in sync with DrawPinMarkers.
    /// </summary>
    internal static void DrawPinMarkersPlain(
        SKCanvas canvas, IReadOnlyList<SymbolPin> pins,
        double panX, double panY, double zoom, SchematicRenderTheme theme)
    {
        if (pins.Count == 0) return;

        float r        = (float)Math.Max(3.0, zoom * 5.0);
        float strokeW  = (float)Math.Max(1.0, zoom * 1.5);
        float fontSize = (float)Math.Max(8.0, zoom * 12.0);

        using var fill      = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                            Color = theme.ConnectedPin };
        using var stroke    = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                            StrokeWidth = strokeW, Color = theme.Wire };
        using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                            Color = theme.ComponentNameText };
        using var labelFont = new SKFont(SkiaFonts.PlexBold, fontSize);

        foreach (var pin in pins)
        {
            float sx = (float)((pin.LocalX - panX) * zoom);
            float sy = (float)((pin.LocalY - panY) * zoom);
            canvas.DrawCircle(sx, sy, r, fill);
            canvas.DrawCircle(sx, sy, r, stroke);
            string lbl = pin.Name is { Length: > 0 } n ? n : $"P{pin.PortIndex + 1}";
            canvas.DrawText(lbl, sx + r + 2, sy + fontSize * 0.35f, SKTextAlign.Left, labelFont, textPaint);
        }
    }
```

### 2. `SymbolClipboard` — thread pins through render + bbox

**(a) `RenderSymbol`** — add a `pins` parameter and draw them after the primitives:

```csharp
    private static void RenderSymbol(
        SKCanvas                       canvas,
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double panX, double panY, double zoom,
        SchematicRenderTheme           theme,
        bool                           useTransparentBackground)
    {
        canvas.Clear(useTransparentBackground ? SKColors.Transparent : theme.Background);
        SchematicRenderer.DrawSymbol(
            canvas, primitives,
            compX: 0, compY: 0,
            rotation: SymbolRotation.R0, mirrorX: false,
            panX: panX, panY: panY, zoom: zoom,
            theme: theme);
        // Pins: same dot + port-label rendering and scale as the editor, so exports match screen.
        SymbolEditorRenderer.DrawPinMarkersPlain(canvas, pins, panX, panY, zoom, theme);
    }
```

**(b) The three render helpers** — `TryRenderToPdf`, `TryRenderToSvg`, `TryRenderToAvaloniaImage` — add
`IReadOnlyList<SymbolPin> pins,` immediately after the `primitives` parameter, and pass `pins` into their
`RenderSymbol(...)` call. (Their bbox/zoom math is unchanged — it already takes `bbMinX/bbMinY/worldW/worldH`
as parameters, which we widen in step (c).)

Example for the PDF helper (apply the same param + call change to all three):

```csharp
    private static byte[]? TryRenderToPdf(
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double bbMinX, double bbMinY, double worldW, double worldH,
        SchematicRenderTheme           theme,
        bool                           useTransparentBackground)
    {
        try
        {
            // … unchanged zoom/pan/page math …
            RenderSymbol(canvas, primitives, pins, panX, panY, zoom, theme, useTransparentBackground);
            // … unchanged …
        }
        catch { return null; }
    }
```

**(c) `CopyAsync`** — union pins into the bbox and pass `pins` to each helper. Replace:

```csharp
        // Compute bbox from the selected primitives for page sizing.
        var (bbMinX, bbMinY, bbMaxX, bbMaxY) = SymbolGeometry.ComputeBb(primitives);
        double worldW = bbMaxX - bbMinX;
        double worldH = bbMaxY - bbMinY;
```

with:

```csharp
        // Page bounds from primitives AND pins, so pins (which can sit outside the primitive bbox,
        // e.g. on stubs) aren't clipped. pinMargin covers the pin dot; the 15% render pad in each
        // helper absorbs the port label. Handles primitive-free (pins-only) selections too.
        double bbMinX = double.MaxValue, bbMinY = double.MaxValue,
               bbMaxX = double.MinValue, bbMaxY = double.MinValue;
        if (primitives.Count > 0)
        {
            var (p0, q0, p1, q1) = SymbolGeometry.ComputeBb(primitives);
            bbMinX = p0; bbMinY = q0; bbMaxX = p1; bbMaxY = q1;
        }
        const double pinMargin = 12.0;
        foreach (var pin in pins)
        {
            bbMinX = Math.Min(bbMinX, pin.LocalX - pinMargin);
            bbMinY = Math.Min(bbMinY, pin.LocalY - pinMargin);
            bbMaxX = Math.Max(bbMaxX, pin.LocalX + pinMargin);
            bbMaxY = Math.Max(bbMaxY, pin.LocalY + pinMargin);
        }
        bool hasBounds = bbMinX != double.MaxValue;
        double worldW = hasBounds ? bbMaxX - bbMinX : 0;
        double worldH = hasBounds ? bbMaxY - bbMinY : 0;
```

Then update the three calls inside the `if (worldW >= 1 && worldH >= 1)` block to pass `pins`:

```csharp
                byte[]? pdf = TryRenderToPdf(primitives, pins, bbMinX, bbMinY, worldW, worldH, renderTheme, transparent);
                …
                string? svg = TryRenderToSvg(primitives, pins, bbMinX, bbMinY, worldW, worldH, renderTheme, transparent);
                …
                Bitmap? bmp = TryRenderToAvaloniaImage(primitives, pins, bbMinX, bbMinY, worldW, worldH, renderTheme, transparent);
```

`SymbolClipboard` already has `using CircuitRF.Ui.Renderers;`, so `SymbolEditorRenderer.DrawPinMarkersPlain`
is reachable (same assembly, `internal`).

### Design choices (call out for the owner)

- Exports include the **port labels** (`P1`, `P2`, or the pin name), matching the editor. If a clean,
  label-free symbol export is wanted later, that's a follow-up toggle — not done here.
- Pins render as plain (unselected-style) markers regardless of what was selected when copied; a clipboard
  image has no selection concept.

## Verification (manual)

1. In the symbol editor, place a few pins (some named, some not), select the whole symbol (primitives +
   pins), Copy, paste into a vector app (Keynote/Preview for PDF, Illustrator/Inkscape for SVG) and a
   raster app (Pages/Word for PNG): pin dots **and** labels appear, positioned and sized like the editor.
2. Pins on stubs that extend beyond the symbol body are fully visible (not clipped at the page edge).
3. Select **only pins** (no primitives) and Copy → the pasted image shows the pins (previously produced
   no image, JSON only).
4. Schematic copy/paste of components still exports port markers/dots exactly as before (unchanged path).

## Acceptance

- Symbol clipboard PDF/SVG/PNG include pin dots + port labels at the editor's on-screen scale.
- Page bounds include pins (and pins-only selections render an image).
- Schematic export path untouched and unchanged.
