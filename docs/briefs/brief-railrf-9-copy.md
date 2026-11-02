# Brief 9 — copy to clipboard: railRF writes no clipboard code

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail9-n`
**Area:** `src/Ui/RailRf/` · **Depends on:** 8 · **Blocks:** nothing
**Design note:** [`railrf.md`](../design/railrf.md) §11.7, §7

---

## 0. What this brief delivers

`RailGraphicExport` — a call into `PlotExporter.SetClipboardDataAsync`, a payload, and an entry in one
existing parameter list. Perhaps 150 lines.

The owner instruction, §11.7:

> a copy out of railRF must land as a picture in a document or a slide, it must carry railRF's own
> rendering and not just the copper, and **it must reuse the exporter that already exists — this path has
> cost real debugging time across three platforms and none of it should be spent again.**

### `R-rail9-1` — the rule, stated as a rule

> **railRF writes no clipboard code.**

`src/Ui/Layout/StackupGraphicExport.cs` and `src/Ui/Harmonica/HarmonicaClipboard.cs` are the two worked
examples of a tool-window copy done correctly on this path. Read one of them before writing this.

---

## 1. The five pieces, in the order they are called

Each already exists. Each carries a finding that put it there, and every one of those findings is a defect
someone already shipped once.

### 1. Frame the page from what is PAINTED

**Never from the current pan and zoom, and never from raw geometry bounds.** Two separate defects are baked
into that sentence:

- **A label's stored bbox is a point**, so a page unioned from geometry bboxes once sized itself to almost
  nothing and hung the content off the edges.
- **A hidden layer must not size the page**, which is why `LayoutClipboard.ComputeSelectionBounds` and
  `LayoutRenderer.Draw` now read the same `LayerDef.Visible` flag. Before they did, two shapes on two layers
  with one hidden produced a page spanning both and a visible shape too small to read.

**`R-rail9-2` — railRF's overlays are part of the painted extent** and go into that same pass. This is the
same requirement as brief 8's `R-rail8-7` seen from the other side, and it is why the legend's placement is
a correctness question rather than a cosmetic one.

### 2. Resolve colour and background through `ClipboardRenderPolicy.Resolve()`

One app-wide setting, **never a per-call parameter** — the contract every copy path in this application
already signs. Brief 8 `R-rail8-10` already constrained the map to be **opaque paint** so that nothing in
railRF's picture depends on the page's background being any particular colour, which is what keeps this step
a one-liner here rather than the stackup copy's hole-cutting exercise.

### 3. Render PDF, SVG and a 2× PNG, and write them all at once

`PlotExporter.SetClipboardDataAsync(anchor, pdf, svg, text, bitmap)`.

- **On Windows** that call bypasses Avalonia's clipboard entirely and performs **one** P/Invoke session
  covering every format, **with the enhanced metafile first** — which is what a slide or a word-processor
  document takes when it iterates the formats on offer.
- **On macOS and Linux** it is one transfer carrying the native PDF and SVG types, the bitmap and the text,
  with a text-only fallback if the platform refuses the multi-format write.
- It is **one call and not four** because **a second Avalonia clipboard session fails on Windows**, for
  reasons `WindowsClipboard`'s own header records.

### 4. Exports opt out of the level-of-detail tiers

`DetailPixelThreshold = -1`. A PDF or SVG page has no device pixels to budget against, and a pasted bitmap
may be rescaled away from the size it was rendered at — so **what is stored is what is drawn**, exactly as
`circuitrf render --detail full` already does. On a real board this is a visible difference, not a
theoretical one.

### 5. The font repair is not optional

Skia's SVG device writes font-family names and embeds no font data, so every emitted SVG passes through
`SvgFontNormalizer`, and on Windows the embedded faces are registered for the process for the duration of
the render. **A copy must never be able to fail because of a font** — the rule that made that repair a
shared step rather than an export-only one.

---

## 2. `R-rail9-3` — the one thing that is genuinely new, and the one that will break

§11.7, and this is the whole reason brief 9 has a gate rather than a checklist:

> The overlay set a layout copy draws is an **explicit parameter list**: the EM mesh, the current-density
> map and the DRC markers were each added to `LayoutClipboard.CopyAsync` as they arrived, and each rides the
> *picture* while staying deliberately **out of the JSON payload** — a mesh or a violation marker is a
> result, not geometry, and must not paste into another layout as though it were.
>
> railRF's overlays follow that rule exactly, and the failure mode follows from it too: **an overlay nobody
> added to that list is silently absent from the copy.** The picture is still produced, it still looks
> correct, and the one thing the user copied it for is missing.

So: add railRF's overlays to that parameter list, keep them out of the JSON payload, and **gate the
overlay's presence in the real SVG text rather than trusting the wiring.**

---

## 3. `R-rail9-4` — what lands on the clipboard

§11.7's last paragraphs, in full:

- **The rail's own state as marker-guarded JSON**, so a copy pastes back into another railRF document and a
  **foreign paste is ignored rather than half-parsed**.
- **PDF, SVG and a bitmap of the board *as drawn***: the active map overlay, its legend, the source and load
  markers, the flagged vias, and **the rulers, which count as content** (brief 8: rulers stay on — they are
  document content, not overlay state, which is the same rule `circuitrf render` already follows).

### `R-rail9-5` — an empty view is NOT a licence to return early

> A copy that writes nothing to the system clipboard leaves the **previous** copy sitting there, so the next
> paste produces something unrelated and nothing reports a failure — that is precisely what a ruler-only
> layout copy did, and it is a further reason railRF's copy is of the **view** rather than of a selection.

So there is no early return on an empty board, an unsolved document or a rail with no result. A copy always
writes.

---

## 4. `R-rail9-6` — how it is invoked

Ctrl/⌘+C on the board panel and a **Copy** item in its context menu, dispatched through
`WorkspaceViewModel.InvokeClipboardAsync` on the active document's type, **exactly as every other document's
copy already is.** The overlay's own context items go through `ILayoutCanvasOverlay.BuildContextMenuItems`,
which is the seam's member for exactly this — the canvas is shared and its `ContextMenu` is built once.

**The same render path produces the picture in the `Report ▸` menu and in the headless report**, so there is
**one route from an overlay to a page and not two.** Brief 10's `-o report.svg` calls the same function.

---

## 5. Tests — `tests/Ui.Tests/RailRf/RailCopyTests.cs`

§7's gate, and it is specific about its oracle:

> A copy taken with the drop map showing **must contain the drop map**, asserted against the real SVG text
> the way `LayoutClipboardVisibilityTests` and the `TryRenderToSvg` font tests already assert against Skia's
> own output.

Read those two test files first; they are the pattern.

- **`R-rail9-3`**: copy with the drop map showing → the SVG text contains the map's own marks. Then the
  negative that makes it a real gate: **remove railRF's entry from the overlay parameter list and assert the
  test goes red.** Without that, the test passes on a picture that happens to contain copper.
- **`R-rail9-2`**: the page is framed on the painted extent **including the overlay** — a legend outside the
  copper bbox is inside the page. And: **a hidden layer does not size the page.** Both halves, per §7.
- **`R-rail9-4`**: the JSON payload round-trips into another railRF document; a foreign payload is
  **ignored** rather than half-parsed; the overlays are **absent** from the JSON and present in the picture.
- **`R-rail9-5`**: a copy of an unsolved document still writes all four formats.
- **`R-rail9-1`**: a comment-stripped source scan over `src/Ui/RailRf/` finds **no** `Clipboard`,
  `IDataObject`, `SetTextAsync` or P/Invoke — every write goes through `PlotExporter.SetClipboardDataAsync`.
- **Detail**: the exported SVG is the `--detail full` picture, asserted by element count against the same
  scene rendered at a screen-detail threshold. On a real board these differ substantially, which is what
  makes the assertion meaningful.

---

## 6. Scope

- **No clipboard implementation.** If this brief contains a P/Invoke, a format negotiation or a platform
  branch, it is wrong.
- **No new render path.** Brief 8's `RailMapRenderer` draws; this brief calls it at a page size.
- **No report layout.** `Report ▸` and the headless report are brief 10's; this brief provides the one
  function both of them call.

**On completion:** record findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.
