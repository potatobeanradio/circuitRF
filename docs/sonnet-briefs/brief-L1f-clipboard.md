# Sonnet Brief — Phase L1f: cut / copy / paste across cells

**Design:** `docs/design/layout-view.md` §6.4 (clipboard across cells), §1.4 R4 / §1.5 (the rescale and
warn-don't-mangle machinery this reuses), §2.4 (technology scope), §3.4 R10a (nets survive), §3.1a (holes
survive). **Consumes** all of L1a–L1e.

**Scope is L1f ONLY: the clipboard.** This is the **last brief of Phase L1**; when it lands, the layout
editor can draw, reshape, combine and move geometry between cells, and **L2 (performance)** is next.

## Goal

Copy a selection in one cell's layout, paste it into another cell's layout — in the same window or a
different circuitRF instance — with its layers, nets and holes intact, correctly rescaled if the two layouts
use different DBU resolutions, and with nothing silently dropped.

## Verified substrate (consume — already exists)

**`src/Ui/Clipboard/SchematicClipboard.cs` + `WindowsClipboard.cs` are the template, and §6 below is the
most important section in this brief.** Mirror them closely:

- **JSON text on the system clipboard is the primary format**, guarded by a marker string that paste checks
  before parsing. Any text without the marker is silently ignored.
- **Graphic formats ride alongside** so a layout selection pastes into PowerPoint, Word, Pages and Keynote as
  a proper vector graphic — EMF on Windows, PDF on macOS/Linux. This is §6.
- `IClipboard` arrives as a **parameter**, never resolved statically — which is what keeps it testable.
- Async `CopyAsync` / `PasteAsync`.

Because the payload is system-clipboard text, **copy-in-one-instance / paste-in-another works for free**.
That is worth preserving deliberately, not accidentally.

Also consuming: `LayoutScaling` (the DBU ratio logic — reuse it, do not re-derive), `LayoutPersistence`'s
serializer options and polymorphic `$type` setup, `TechnologyCache` + the L1-fix live-technology mechanism,
and `ReplaceShapesCommand` (L1e).

## Code changes

### 1. Split the logic from the I/O

**`src/Ui/Layout/LayoutFragment.cs` — framework-free.** Everything that decides *what the paste means* lives
here and is headless-testable: building a fragment from a selection, rescaling it, reconciling layers,
computing the paste offset.

**`src/Ui/Clipboard/LayoutClipboard.cs` — Avalonia.** Only serialization, `IClipboard` traffic and the rich
formats. It calls `LayoutFragment`; it contains no rescale or reconciliation logic.

This split is the reason the hard parts of this phase get real tests. Do not collapse it.

### 2. The fragment payload

```csharp
private const string Marker = "circuitrf/layout-clipboard-v1";

private sealed class Payload
{
    public string?           Marker       { get; set; }
    public int               DbuPerMicron { get; set; }   // the SOURCE layout's resolution
    public long              AnchorX, AnchorY { get; set; } // selection bbox min, in source DBU
    public List<LayerDef>    Layers       { get; set; } = [];  // only the layers actually used
    public List<LayoutShape> Shapes       { get; set; } = [];
}
```

**R-L1f-1. The fragment is self-describing.** It carries its source `DbuPerMicron` **and** the `LayerDef`s it
references. It has to be pasteable into a workspace with a different technology, a different resolution, and
a different running process, so it may not depend on any ambient state. This is the whole reason §6.4 asks
for a fragment rather than a list of shapes.

Shapes serialize through the **same** polymorphic setup as `.clay`, so holes (§3.1a), edge lists, nets and
flatten tolerances round-trip with no extra work. Instances are **not** carried — nothing can create one
until L3; add a `// L3` note rather than speculative plumbing.

### 3. Paste: rescale, then reconcile, then place

**R-L1f-2. DBU rescale.** Source and destination `DbuPerMicron` equal → paste as-is. Different → scale by the
exact ratio through `LayoutScaling`'s existing logic. Where the ratio is non-integer, or coarsening would
round a coordinate, **warn through Messages naming the affected shapes and proceed** — §6.4 is explicit that
paste warns and continues rather than refusing. (Note the deliberate difference from
`LayoutScaling.TryChangeResolution`, which *does* refuse: that operation mutates an existing design in place,
while this one is adding new geometry the user can undo. Comment it, or someone will "fix" the inconsistency.)

**R-L1f-3. Layer reconciliation never drops geometry.** For each distinct `LayerKey` in the fragment:

| Destination state | Behaviour |
|---|---|
| Layer exists in the destination technology | Use the **destination's** `LayerDef`. Its color and name win — it is the destination's technology, and the fragment's copy is only a description of where the geometry came from. |
| Layer absent | Ask once, with **Apply to all remaining** — never once per shape. |

The three offered choices, in this order:

1. **Keep as unknown** *(default)* — the shape keeps its `LayerKey` and renders through `FallbackPalette`.
   Non-destructive and reversible; the right default.
2. **Map to an existing layer** — pick from the destination table; the shape's `LayerKey` is rewritten.
3. **Add to the technology** — append the fragment's `LayerDef`. This **marks the `.ctech` dirty through the
   live-technology mechanism** (the L1-fix `SetLive` path) rather than writing the file behind the user's
   back. The user still decides whether to persist it, and it is undoable in the tech editor. Offer this
   option only when a technology actually resolved.

**Placement.** Two commands, because both are genuinely wanted:

- **Paste** (Ctrl/Cmd+V) — the fragment attaches to the cursor as a live ghost, drawn through the existing
  `Overlay`; the fragment's `Anchor` lands on the snapped cursor; click places it, Escape cancels with no
  command pushed.
- **Paste in Place** (Ctrl/Cmd+Shift+V) — original coordinates, no ghost, immediate.

Pasted shapes are **appended** to `LayoutView.Shapes`, i.e. topmost within their layers. That is the
expected result and it keeps undo's restore-at-index rule trivial.

### 4. Commands

| Command | Notes |
|---|---|
| **Copy** (Ctrl/Cmd+C) | No model change, no undo entry. |
| **Cut** (Ctrl/Cmd+X) | Copy then delete, as **one** undo entry. |
| **Paste** / **Paste in Place** | One undo entry each, via `ReplaceShapesCommand` with an empty removed-set. |
| **Duplicate** (Ctrl/Cmd+D) | **Does NOT touch the system clipboard.** Copies the selection internally and places it offset by one snap step. Clobbering the user's clipboard as a side effect of Duplicate is a small betrayal that people notice. |

After a paste or duplicate, the **new** shapes become the selection — so the next action operates on what
was just placed, which is what every user expects.

### 5. Cross-editor safety

A symbol-clipboard payload pasted into a layout, or a layout payload pasted into a symbol editor, must be
**ignored silently** — the distinct marker strings make this automatic, but assert it, because the failure
mode without it is a confusing partial paste rather than a clean no-op.

## 6. Graphic formats — pasting a layout into PowerPoint / Word / Pages / Keynote

A layout selection must paste into Office and iWork as a **vector graphic**: **EMF on Windows**, **PDF on
macOS/Linux**. `SchematicClipboard` already solves this, and it was hard-won. **Reuse it; do not re-derive
it.**

### 6.1 What is reused verbatim — expect zero changes to these files

`WindowsClipboard.SetClipboard(hwnd, pdf, svg, json, bitmap, pageW, pageH)` is **entirely format-agnostic**:
it takes already-rendered bytes and strings and knows nothing about schematics. Layout calls it with its own
rendered output and **nothing in `WindowsClipboard.cs` needs to change.** The same is true of
`ClipboardFormats` (`com.adobe.pdf`, `public.svg-image`) and `ClipboardRenderPolicy.Resolve()`.

**If you find yourself editing `WindowsClipboard.cs`, stop and re-read — you have almost certainly taken a
wrong turn.** Its header comment documents four non-obvious constraints that took real effort to get right:

1. **Avalonia's clipboard ownership problem.** On Windows, `clipboard.SetDataAsync()` calls
   `EmptyClipboard()` and keeps ownership, so a *second* session to add EMF would fail. That is why Windows
   bypasses Avalonia entirely and writes every format in one `OpenClipboard` → `EmptyClipboard` →
   `SetClipboardData`×N → `CloseClipboard` P/Invoke session.
2. **`CF_ENHMETAFILE` is set first**, because Word and PowerPoint enumerate formats and take the first one
   they recognise. Order is the feature, not an accident.
3. **The EMF is built from the SVG**, via Svg.NET into a GDI+ metafile. **No SVG means no EMF** — so if the
   layout SVG render fails, Windows silently loses its best format. Treat the SVG path as load-bearing on
   Windows, not as a nice-to-have.
4. **Fonts are registered with `AddFontMemResourceEx`** before rendering and removed after, because
   SkiaSharp's SVG canvas writes font-family names without embedding font data. Layout `Label`s go through
   the same path and inherit this for free.

Also mirror `SchematicClipboard.CopyAsync`'s structure exactly: render PDF + SVG + bitmap best-effort inside
a `try`, then branch — Windows → `WindowsClipboard.SetClipboard(...)` and **return**; macOS/Linux → build a
`DataTransferItem` with `PdfNativeMacFormat`, `SvgNativeFormat`, `DataFormat.Bitmap`, `DataFormat.Text`, and
fall back to `SetTextAsync(json)` if `SetDataAsync` throws. Keep the `maxSide = 720f` page-scaling rule that
sizes the EMF frame to a Word/PowerPoint-friendly ~10 inches.

`ownerHwnd` must be threaded from the view down to `CopyAsync`, as the schematic editor does.

### 6.2 What is genuinely new: rendering a layout selection to a page

The three `TryRenderTo{Pdf,Svg,AvaloniaImage}` helpers are schematic-specific only in *what they draw*. Copy
their surrounding boilerplate (`SKDocument.CreatePdf`, `SKSvgCanvas.Create`, the `SKBitmap` surface) and swap
in `LayoutRenderer.Draw`. Four layout-specific requirements:

**R-L1f-4. Export renders the *selection*, not the current view.** Union the selected shapes' bboxes via
`LayoutGeometry.BboxOf`, add a small margin, and construct a `LayoutViewport` that maps that bbox onto the
page. The result must be identical whatever the user's on-screen zoom and pan happen to be.

**R-L1f-5. `LayoutRenderer` needs an export mode.** Add a `LayoutRenderOptions` flag that suppresses the
**background fill, grid, rulers, selection outlines, handles, marquee and ghost overlay** — geometry only.
`SymbolEditorRenderer.Draw` already carries a `useTransparentBackground` parameter for exactly this reason;
follow that precedent. A dark-theme layout pasted into a white Word page as an opaque dark rectangle is the
failure this prevents, and `ClipboardRenderPolicy.Resolve()` exists to pick the right variant — use it.

**R-L1f-6. Hairline strokes must become real widths on export.** The canvas draws outlines with
`SKPaint.StrokeWidth = 0`, which is a *device-pixel* hairline — meaningful on screen, meaningless in a
vector page that will be scaled arbitrarily by Word. In export mode, use a genuine stroke width in world
units, chosen so it reads as roughly a hairline at the export scale. A zero-width stroke in an EMF or PDF
renders as either a device pixel at the printer's resolution or as nothing at all, and both are wrong.

**R-L1f-7. Semi-transparent fills must survive.** §2.3's contract is alpha fill plus opaque outline, and
overlap darkening is a deliberate decision. PDF and SVG carry alpha natively; GDI+ metafiles are the weak
link. **Verify EMF alpha explicitly** by pasting overlapping same-layer shapes into PowerPoint. If alpha does
not survive, say so in the completion note and leave it — do **not** silently flatten to opaque, which would
make exported geometry disagree with what the user sees.

## Scope guardrails (do NOT do in L1f)

- No instances (L3) — the fragment carries shapes only.
- No spatial index, caching, LOD or the R8b merge tier (L2). No DRC (L5b), no interchange (L4).
- No new drawing tools, no changes to L1c–L1e behaviour beyond what the clipboard needs.
- Do not write to a `.ctech` file directly — "Add to the technology" goes through the live mechanism and
  leaves the document dirty.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, `SchematicClipboard`, or `SymbolClipboard`. **Reuse**
  `WindowsClipboard`, `ClipboardFormats` and `ClipboardRenderPolicy` **without modifying them** (§6.1); if a
  change to any of the three looks necessary, write down why before making it — it is far more likely that
  the layout side is calling them wrongly.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Round-trip fidelity** — copy a selection containing every shape type, including a polygon **with a
   hole**, an arc-bearing `Curve`, a curved `Path`, and shapes carrying **nets**; paste into a second layout
   and assert `LayoutPersistence.Serialize` equality of the pasted shapes against the originals (modulo the
   paste offset).
3. **Cross-cell** — copy in cell A's layout, paste into cell B's layout, both open; and paste into a layout
   in a **different workspace** with a different technology.
4. **DBU rescale (R-L1f-2)** — a fragment from a 1 nm layout pasted into a 0.1 nm layout scales exactly
   (×10, lossless, silent). The reverse direction with a coordinate that does not divide warns through
   Messages, names the shape, and **still pastes**.
5. **Layer reconciliation (R-L1f-3)** — all three branches: an existing layer adopts the **destination's**
   `LayerDef`; an absent layer defaults to Keep-as-unknown and renders via `FallbackPalette`; Map rewrites
   the `LayerKey`; Add marks the technology dirty **without writing the file**, and is undoable in the tech
   editor. The prompt appears **once per layer**, not once per shape.
6. **Nothing is ever dropped** — after any reconciliation choice, the pasted shape count equals the copied
   shape count.
7. **Marker guard** — pasting arbitrary text, a symbol-clipboard payload, or truncated JSON is a clean no-op
   with no exception and no partial model change.
8. **Placement through screen coordinates** — drive Paste's ghost placement from **screen pixels** through
   the canvas conversion at a realistic default viewport, on both starter technologies, and assert the
   fragment's anchor lands on the snapped cursor position. (The standing rule from the L1 fix round:
   world-coordinate tests cannot catch screen→world bugs.)
9. **Paste in Place** lands at original coordinates; **Duplicate** offsets by one snap step and leaves the
   system clipboard **untouched** (assert the clipboard still holds whatever was there before).
10. **Undo** — Cut is one entry restoring the originals at their original indices; Paste is one entry
    removing exactly the pasted shapes; Escape during the ghost pushes nothing.
11. **Selection after paste** is exactly the newly pasted shapes.
12. **Graphic paste, manually verified on both platforms** — this cannot be fully automated, so do it by
    hand and record the result: copy a layout selection and paste into **PowerPoint and Word on Windows**
    (expect a crisp, scalable EMF — not a bitmap) and into **Keynote and Pages on macOS** (expect PDF).
    Zoom in after pasting: vector output stays sharp, a bitmap fallback does not. That check is the whole
    point of the feature.
13. **Export is view-independent (R-L1f-4)** — rendering the same selection at two very different on-screen
    zoom/pan states produces byte-identical PDF page dimensions and equivalent geometry.
14. **Export mode is clean (R-L1f-5, R-L1f-6)** — the rendered output contains **no** background fill, grid,
    rulers, selection outlines, handles or ghost; strokes carry a real non-zero width. Assert headlessly by
    rendering to an `SKBitmap` with a known backdrop: corner pixels stay backdrop-coloured, and a shape
    outline is present and more than zero pixels wide.
15. **Windows loses nothing when SVG fails** — with SVG rendering forced to fail, `SetClipboard` still
    writes PDF, PNG and text without throwing, and no EMF handle leaks (§6.1 item 3).

## On completion

1. Add a "Phase L1f — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: the
   **`LayoutFragment` (pure) / `LayoutClipboard` (Avalonia) split** and why, **R-L1f-1's self-describing
   payload**, that **paste warns-and-proceeds on lossy rescale while `TryChangeResolution` refuses** and the
   reason they differ, the three layer-reconciliation branches with **Keep-as-unknown as the default**, that
   **Duplicate deliberately bypasses the system clipboard**, that **`WindowsClipboard` was reused unchanged**
   and layout only supplies rendered bytes, the **export-mode render flags** (R-L1f-5/6) and what they
   suppress, the **result of the EMF alpha check** (R-L1f-7), and the test file names.
2. Note that **Phase L1 is complete**, and that the next brief is **L2 — performance**: the R-tree spatial
   index, per-shape path caching, the §2.3 R8b merge tier with its threshold set from data, LOD culling, and
   the CI benchmark harness at 1k / 50k / 500k shapes measuring both the darkening and merged paths.
3. Report back before L2 is briefed.
