# Brief B — Clipboard & Export Parity (bitmaps in PDF/SVG; Symbol Editor multi-format copy)

**Scope:** clipboard copy/export only. Two bugs:
1. Schematic Copy drops bitmaps from the PDF/SVG/PNG render (only components+wires are rendered).
2. Symbol Editor Copy ships **only JSON text**, so pasting into Keynote/Pages yields raw JSON instead of an image. Make it behave like the Schematic Editor (which already ships PDF/SVG/PNG and pastes correctly into 3rd-party apps).

**Architectural firewall:** UI-layer only (`src/Ui/Clipboard`, `src/Ui/Renderers`). No engine/Core changes.

---

## Read first (real names)

- `src/Ui/Clipboard/SchematicClipboard.cs` — the reference implementation. Note:
  - `CopyAsync(IClipboard, components, wires, canvasObjects, gridSize)` sets four formats on one `DataTransferItem`: PDF (`PdfNativeMacFormat`=`com.adobe.pdf` / `PdfNativeWinFormat`=`application/pdf`), SVG (`SvgNativeFormat`=`public.svg-image`), PNG (`DataFormat.Bitmap`), and JSON (`DataFormat.Text`, always last/primary).
  - The image formats come from `TryRenderToPdf` / `TryRenderToSvg` / `TryRenderToAvaloniaImage`, **all of which call `BuildSelectionModel(components, wires)`** and then `SchematicRenderer.Draw(...)`.
  - **The bug lives in `BuildSelectionModel`:** it builds a temp `SchematicEditModel`, adds `Components` and `Wires`, and calls `BuildRenderModel()` — it NEVER adds the `canvasObjects` (bitmaps). So the render model has empty `Bitmaps`, and the exported PDF/SVG/PNG omit every image. (`CopyAsync` already RECEIVES `canvasObjects` — it just doesn't pass them into the selection model.)
  - `(variant, transparent) = ClipboardRenderPolicy.Resolve()` supplies the render variant + background.
- `src/Ui/Clipboard/SymbolClipboard.cs` — currently `CopyAsync(IClipboard, primitives, pins, gridSize)` serializes a `Payload` (marker `circuitrf/symbol-clipboard-v1`) and calls **only** `clipboard.SetTextAsync(json)`. No `DataTransfer`, no image formats. This is why Keynote pastes JSON.
- `src/Ui/Renderers/SymbolEditorRenderer.cs` — `Draw(SKCanvas, (W,H) size, Symbol?, SymbolEditorOverlay, panX, panY, zoom, theme)`. This is how a symbol is rendered to a canvas. It clears to `theme.Background` and has no transparent-bg / exclude-grid switches today (see Layer 2 decision).
- `src/Ui/Schematic/SymbolGeometry.cs` — `ComputeBb(primitives)` gives the symbol's local bbox for sizing the page. `BboxOf` for a single primitive.
- `src/Ui/Schematic/EditableSchematic.cs` — `SchematicEditModel.BuildRenderModel()` and how `CanvasObjects` (List<EditableCanvasObject>) project into `SchematicModel.Bitmaps`. Bitmaps are `OfType<EditableBitmap>()` ordered by ZOrder.
- How copy is invoked: `SchematicViewModel.ClipboardCopyAsync` passes `objs` (selected `EditableCanvasObject`s) to `SchematicClipboard.CopyAsync`; `SymbolEditorViewModel`'s copy path (grep `SymbolClipboard.CopyAsync`) passes primitives+pins. Read both call sites.

---

## Spine (do-not-violate)

1. JSON text remains the **primary** clipboard format for both editors (round-trips perfectly; internal paste depends on it). Image formats are additive and best-effort (wrap in try/catch; never let an image-render failure drop the JSON).
2. Do not change the JSON payload schema or markers (internal paste must keep working).
3. Match the Schematic copy's format set and platform gating (PDF always; SVG non-Windows; PNG always).
4. Use the existing renderers (`SchematicRenderer.Draw`, `SymbolEditorRenderer.Draw`) — do not write a new rasterizer.

---

## Layer 1 — FIX: include bitmaps in the Schematic export render

In `SchematicClipboard`, make the selection model include the selected canvas objects so `SchematicModel.Bitmaps` is populated and the renderer draws them.

1. Thread `canvasObjects` into `BuildSelectionModel`. Change its signature to also accept `IReadOnlyList<EditableCanvasObject> canvasObjects` and, after adding components+wires to the temp `SchematicEditModel`, also add each canvas object: `tmp.CanvasObjects.Add(obj)`. Confirm `BuildRenderModel()` then projects them into `Bitmaps` (it already does — `OfType<EditableBitmap>()`).
2. Update `TryRenderToPdf`, `TryRenderToSvg`, `TryRenderToAvaloniaImage` to accept and forward `canvasObjects` to `BuildSelectionModel`. `CopyAsync` already has `canvasObjects` in scope — pass it down.
3. **Bounding box:** `BuildSelectionModel` computes `worldW/worldH` from `rm.BbMaxX-BbMinX` etc. Confirm `BuildRenderModel()`'s overall bbox includes bitmaps. **Read `BuildRenderModel`'s bbox loop** — if it only unions components+wires (it currently iterates `comps` and `wires`), a selection of ONLY bitmaps (no comps/wires) would yield a degenerate bbox and `BuildSelectionModel` returns null (the `worldW<1` guard) → no image. If the bbox loop excludes bitmaps, extend the temp-model path so the exported page still bounds the images. Simplest contained approach inside `SchematicClipboard`: after `BuildRenderModel()`, union the bitmap rects (`rm.Bitmaps` each has X,Y,Width,Height top-left) into the worldW/H/pan computation used for the page size and pan, rather than relying solely on `rm.BbMinX/MaxX`. Keep this local to `BuildSelectionModel`.

**Gate 1:** Copy a schematic selection that contains a bitmap (and also one that is bitmap-only). Paste into Keynote/Preview → the image appears in the PDF/PNG at the right place/size. Internal paste (into another schematic tab) still works (JSON path unchanged).

---

## Layer 2 — ADD: Symbol Editor multi-format copy (PDF/SVG/PNG + JSON)

Make `SymbolClipboard.CopyAsync` ship the same four formats as `SchematicClipboard`, rendering the **symbol selection** to PDF/SVG/PNG via `SymbolEditorRenderer.Draw`, with JSON still primary.

**Approach — mirror `SchematicClipboard` structure:**

1. Add the same format constants to `SymbolClipboard` (`PdfNativeMacFormat`/`PdfNativeWinFormat`/`SvgNativeFormat`), or factor them into a tiny shared internal static (e.g. `ClipboardFormats`) used by both files — your call, but DON'T duplicate the UTI strings in two places if you can avoid it cheaply. If factoring risks scope creep, duplicate the three `DataFormat` fields and note it.
2. Build the copy with a `DataTransferItem` + `DataTransfer` exactly like `SchematicClipboard.CopyAsync` (PDF first, SVG non-Windows, PNG, JSON last). Keep the existing `SetTextAsync` fallback in a catch for when `DataTransfer` isn't supported.
3. Render the **selected** primitives (and pins, if you include them visually — pins are usually part of the symbol; for a copy image, rendering just the selected primitives is acceptable and simpler. Decide and state it). Build a transient `Symbol` (or reuse the editor's `Symbol`/`EditableSymbol.ToSymbol()`) containing the selected primitives, compute its bbox via `SymbolGeometry.ComputeBb`, and render with `SymbolEditorRenderer.Draw` into PDF/SVG/PNG canvases. Mirror the page-sizing math from `SchematicClipboard.TryRenderTo*` (pad 0.15, clamp sizes, pan = bbMin − world*pad, zoom = min(target/worldDim)).
4. **Render policy:** read `ClipboardRenderPolicy.Resolve()` for `(variant, transparent)` and build the theme the same way (`SchematicRenderTheme.FromTheme(ThemeService.Active, variant)`).

**Layer 2 decision — transparent background / no grid:**
`SymbolEditorRenderer.Draw` currently always `canvas.Clear(theme.Background)` and always draws the grid. For a clean clipboard image you want **no grid** and (ideally) a transparent/ချpolicy-driven background. Two options — pick the lower-effort one and state which:
- **(Preferred, low effort)** Add optional params to `SymbolEditorRenderer.Draw`: `bool useTransparentBackground = false, bool excludeGrid = false` (mirroring `SchematicRenderer.Draw`'s existing flags). Default values keep the live editor identical; the clipboard path passes `true,true`. Guard the `Clear` and the `DrawGrid` call on these flags.
- (Fallback) If adding params is messy, render to the symbol image WITH its normal background but pass `excludeGrid`-equivalent by skipping grid only. Note the visual cost.

**Gate 2:** In the Symbol Editor, select some primitives, Copy, paste into Keynote/Pages → an **image** (PDF/PNG) appears, NOT JSON text. Paste into another Symbol Editor tab → primitives paste correctly (JSON path unchanged, marker intact). With nothing selected, Copy is a no-op (as today).

---

## Layer 3 (optional, only if truly low-effort) — SVG already covered

The Schematic SVG export is fixed for free by Layer 1 (same `BuildSelectionModel`). The Symbol SVG export is delivered by Layer 2 (`TryRenderToSvg` analogue). No extra work — just confirm the SVG slot is set on non-Windows in both. If `SKSvgCanvas` chokes on the bitmap draw (it sometimes rasterizes `DrawBitmap` poorly), it's acceptable for SVG to fall back to omitting the image as long as PDF/PNG include it — note it in your report; do not block on it.

---

## Acceptance

- Schematic copy containing bitmaps → PDF/PNG (and SVG where supported) include the images, correctly positioned/sized. ✅
- Bitmap-only schematic selection still produces a valid image (bbox includes bitmaps). ✅
- Symbol Editor copy → Keynote/Pages paste yields an image, not JSON. ✅
- Internal paste (both editors) unchanged; JSON remains primary; markers unchanged. ✅
- Image-render failure never drops the JSON (best-effort try/catch around the rich formats). ✅

## Guardrails

- Do not alter JSON payload schemas or the `circuitrf/symbol-clipboard-v1` marker.
- Do not invent a new renderer; reuse `SchematicRenderer.Draw` / `SymbolEditorRenderer.Draw`.
- Keep the `DataFormat.Text` (JSON) set LAST and outside the try/catch that wraps the image formats (exactly as `SchematicClipboard` does).
- If you add params to `SymbolEditorRenderer.Draw`, default them so the live editor render is byte-identical to today.

## Scope fence (do NOT do here)

- No gripper work (Brief A). No project-tree (C/D). No cell/properties (E).
- Do not implement Windows EMF/CF_ENHMETAFILE (future; see splotRF `WindowsClipboard.cs`).

## Exit / report

State: the new `BuildSelectionModel` signature; how bitmap bbox is unioned for bitmap-only selections; whether you factored the UTI constants or duplicated them; whether you added the two `SymbolEditorRenderer.Draw` flags; the SVG-with-bitmap outcome; and confirmation you mentally ran both gates.
