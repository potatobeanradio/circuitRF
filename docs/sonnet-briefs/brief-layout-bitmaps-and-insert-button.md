# Sonnet Brief — Bitmap objects in Layout, and an "Insert Bitmap" button for both editors

**Design:** `docs/design/layout-view.md` §3.1 (primitives), §8 (interchange), §9A (DRC), §10.5 (meshing).
The symbol editor already implements almost all of this — **this is a port, not a design**.

**Sequencing.** Touches `LayoutModel`, `LayoutRenderer`, the layout toolbar and properties panel, the layout
clipboard, and the symbol editor's VM/view. **Land after L1j** (the properties-inspector brief), since the
bitmap property row slots into the structure L1j builds. Flag any collision rather than merging blind.

---

## 1. What already exists — read this first

| Piece | Where |
|---|---|
| `BitmapPrimitive` — `ImagePathRef`, `X/Y/W/H`, `Opacity`, `Locked`; *"stored as a path reference, not embedded bytes"*, *"z-index always lowest"* | `Schematic/SymbolModel.cs` ~272 |
| Decode cache — `ConcurrentDictionary<string, SKBitmap?>`, `LoadCachedBitmap`, `InvalidateBitmapCache`, `TryGetBitmapPixelSize` | `Renderers/SchematicRenderer.cs` 39–70 |
| Symbol bitmap draw (affine matrix, opacity paint) | `SchematicRenderer.cs` 394–436 |
| **`DrawBrokenBitmapBox`** — a missing file renders a visible placeholder rather than vanishing | `SchematicRenderer.cs` 1289 |
| `DropBitmap(path, worldX, worldY)` — aspect-preserving sizing from pixel dimensions, falls back to 200×150 | `ViewModels/SymbolEditorViewModel.cs` 1541 |
| `OnPointerRightPressed` → `ResolveBitmapPath` / `RefreshBitmapCache`, and the `IsBroken` check | `SymbolEditorViewModel.cs` 1570–1596 |
| Right-click menu wiring (single XAML `ContextMenu`, `Opening` cancels when no bitmap under pointer) | `SymbolEditorView.axaml` ~535 + `SymbolEditorCanvas.BitmapContextPrimIdx` |
| Clipboard — `BitmapPrimitive` round-trips automatically via the polymorphic `$type` | `Clipboard/SymbolClipboard.cs` |
| Resize gripper for a selected bitmap | `SchematicRenderer.cs` ~1140, `SymbolEditorViewModel.cs` 965 |

Note there are **already two** bitmap models — `BitmapPrimitive` (symbol) and `SchematicBitmap` (schematic
canvas objects). Layout adds a third. **Do not attempt to unify the three**: they live in different
coordinate systems with different hosts, and merging them is a large refactor with no payoff here.

## 2. Extract the cache — the one piece of shared work

`_bitmapCache` is private static inside `SchematicRenderer`. `LayoutRenderer` must not reach into the
schematic renderer for it, and must not start a second cache that decodes the same file twice and misses
invalidations.

**R-bmp-1. Move the cache to `src/Ui/Renderers/BitmapCache.cs`** — `Load(path)`, `Invalidate(path)`,
`TryGetPixelSize(path)` — plus `DrawBrokenPlaceholder(...)`, which both renderers need. Keep
`SchematicRenderer.InvalidateBitmapCache` / `TryGetBitmapPixelSize` as thin forwarders so no existing caller
changes. **One cache, one invalidation path, one broken-file visual.**

## 3. Layout model

```csharp
public sealed class BitmapShape : LayoutShape   // gains Layer + Net from the base
{
    public string ImagePathRef { get; set; } = "";
    public long   X, Y, W, H   { get; set; }    // DBU — not doubles
    public double Opacity      { get; set; } = 1.0;
    public bool   Locked       { get; set; }
}
```

Register it in the `$type` discriminator list as `"Bitmap"`. **Additive, so no `FormatVersion` bump** —
older `.clay` files simply contain none.

`Net` is inherited but meaningless for an image: **hide the Net row in the properties panel** for a
bitmap-only selection rather than showing a field that does nothing.

**R-bmp-2. Bitmaps always render behind all layers**, matching `BitmapPrimitive`'s *"z-index always lowest"*.
The use case is tracing over a reference image, and a semi-transparent layer fill reading on top of a photo
is exactly what is wanted. A bitmap's `Layer` therefore governs **visibility and selectability only**, never
paint order — say so in the class doc comment, because "has a Layer but ignores ZOrder" is otherwise a
surprise.

**R-bmp-3. A bitmap is not geometry, and every geometric consumer must skip it**, each with a note rather
than silence:

| Consumer | Behaviour |
|---|---|
| GDSII / DXF / Gerber export (L4) | **Never exported.** One Messages note per export: *"3 bitmaps skipped — reference images are not manufacturable geometry."* |
| DRC (L5b) | Skipped |
| MoM meshing (L6) | Skipped |
| `ToClipperPaths`, booleans, offset, flatten, repair | Not applicable — commands **disabled with a reason** per R13a |
| Clipboard **graphic** export (PDF/SVG/EMF, L1f) | **Rendered** — it is a visual |
| Bbox, hit-test, select, move, scale, clipboard, undo | Full participation |

Add the forward comments now (`// L4: bitmaps are never exported`, etc.) so those phases inherit the rule
rather than rediscovering it.

## 4. Sizing — the one genuinely layout-specific problem

`DropBitmap` fits the long edge to **200 world units**. In layout, world units are DBU (nanometres), so any
fixed constant is meaningless — 200 DBU is 200 nm, invisible on a PCB and tiny even on an MMIC.

**R-bmp-4. Size a new bitmap so its long edge spans ~25% of the current viewport width**, then round to a
tidy number in the display unit. That is scale-aware and lands sensibly whether the user is working in mils
on a board or microns on a die. Preserve the image's pixel aspect ratio exactly as `DropBitmap` does, and
fall back to a 4:3 box at the same viewport fraction when the file cannot be decoded.

Do **not** try to derive physical size from image DPI metadata — it is absent or wrong in most files, and a
silently wrong physical size in a layout is worse than an obviously arbitrary one the user resizes.

## 5. Layout behaviours to port

- **Drag-and-drop** onto the canvas → `DropBitmapIntoLayout(path, wx, wy)`, mirroring `DropBitmap`.
- **Right-click menu**: **Resolve Path…** and **Refresh Cache**, ported from `ResolveBitmapPath` /
  `RefreshBitmapCache`, including the `IsBroken` check. Add them through the **single XAML-declared
  `ContextMenu` with `Opening`** that the layout canvas now uses after the context-menu fix — do not
  reintroduce a per-click menu.
- **Locked** in the properties panel and the context menu — exactly right for a tracing underlay.
- **Selection handles**: a single selected `BitmapShape` has no vertices, so it shows **bbox scale handles**
  (L1h). This extends R-L1h-5's table: *one shape → vertex handles, except a bitmap, which gets scale
  handles*. Non-uniform scaling of a bitmap is legitimate stretching and needs no arc/cubic promotion.
- **Clipboard**: `BitmapShape` round-trips through `LayoutFragment`'s polymorphic serializer with no extra
  work, exactly as `BitmapPrimitive` does in `SymbolClipboard`. Verify rather than build.
- **Properties panel** (L1j): path (with a **Browse…** button), W/H, Opacity, Locked. Hide Net.
- **Broken path** renders `DrawBrokenPlaceholder` — never an invisible gap.

**Path storage** matches the symbol editor: absolute, or relative to the containing file. Carry the same
known limitation rather than inventing a new scheme — a fragment pasted into another workspace can have a
path that does not resolve, which is what **Resolve Path…** exists for.

## 6. "Insert Bitmap" toolbar button — both editors

Currently the symbol editor's only entry point is drag-and-drop, which is undiscoverable.

**R-bmp-5. Add an Insert Bitmap button to both toolbars**, using the same handler shape in each:

- Icon: `MaterialIconKind.ImagePlus` (fall back to `ImageOutline` if that kind is unavailable in the
  installed pack). Tooltip *"Insert Bitmap…"*.
- Opens a `StorageProvider` file picker **in code-behind** (UI firewall), filtered to formats SkiaSharp
  decodes: `.png .jpg .jpeg .bmp .gif .webp`.
- On pick, places the image at the **centre of the current viewport**, snapped — then calls the existing
  `DropBitmap` (symbol) or its layout equivalent, so there is exactly one placement path per editor.
- **Symbol editor: no new placement logic.** The button is a second caller of `DropBitmap`. If the
  implementation there grows past a few lines, something has been duplicated.
- Disabled when the document is locked, with a reason (R13a).

## 7. Scope guardrails

- No embedding of image bytes in `.csym` / `.clay` — path references only, as today.
- Do not unify `BitmapPrimitive`, `SchematicBitmap` and `BitmapShape` (§1).
- No image editing: no crop, rotate, filters, or per-bitmap colour adjustment.
- No changes to schematic canvas-object bitmaps beyond the §2 cache extraction.
- No spatial index / LOD work (L2). Bitmaps participate in the existing draw path.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 8. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses — in
   particular the symbol editor's existing bitmap tests still pass after the §2 extraction.
2. **Round-trip** — a `.clay` containing a `BitmapShape` serializes, reloads and compares equal; an existing
   bitmap-free `.clay` still loads with **no `FormatVersion` change**.
3. **One cache (R-bmp-1)** — loading the same path from both a symbol and a layout decodes **once**;
   `Invalidate` affects both; `TryGetPixelSize` returns the same result through either entry point.
4. **Sizing (R-bmp-4)** — inserting the same image into a PCB layout and an MMIC layout at comparable zoom
   produces bitmaps of comparable *on-screen* size, with pixel aspect ratio preserved to within a DBU. An
   undecodable file yields the 4:3 fallback and a Messages note, not an exception.
5. **Always behind (R-bmp-2)** — a bitmap on a high-`ZOrder` layer still renders beneath geometry on a
   low-`ZOrder` layer. Off-screen pixel test.
6. **Broken path** renders the placeholder box and is still selectable and movable; **Resolve Path…** repairs
   it as one undo entry and the image appears.
7. **Not geometry (R-bmp-3)** — with a bitmap selected, Union / Intersect / Difference / XOR / Offset /
   Flatten / Repair are **disabled with reasons**; a mixed selection of a bitmap and a rect applies those
   operations to the rect only.
8. **Clipboard** — copy a bitmap in one layout, paste into another: path, size, opacity and `Locked` all
   survive. Copy a selection containing a bitmap and paste into **PowerPoint/Keynote**: the image appears in
   the vector graphic.
9. **Locked** blocks move and scale but not selection or property editing.
10. **Scale handles** — a single selected bitmap shows bbox scale handles; a corner drag scales uniformly, a
    side drag stretches one axis, and neither promotes anything to cubics.
11. **Insert Bitmap button** works in **both** editors: places at viewport centre at a sensible size, is one
    undo entry, and is disabled with a reason when the document is locked. Assert the symbol editor's button
    routes through the existing `DropBitmap`.
12. **Drag-and-drop still works** in both editors and produces an identical result to the button for the same
    file and drop point.

## 9. On completion

Add a "Layout bitmaps + Insert Bitmap" entry at the top of `src/Ui/CLAUDE.md`. Call out: the **extracted
shared `BitmapCache`** and that `SchematicRenderer` now forwards to it; **R-bmp-2** (bitmaps ignore layer
`ZOrder` and always paint behind — `Layer` means visibility/selectability only); **R-bmp-3** and the forward
comments left for L4/L5b/L6 so those phases skip bitmaps with a note; **R-bmp-4** (viewport-relative sizing,
and why a fixed world-unit constant cannot work in a DBU database); that the three bitmap models remain
deliberately separate; and the test file names.
