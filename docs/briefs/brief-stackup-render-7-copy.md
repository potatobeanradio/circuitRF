# Brief 7 — copy the drawing to the clipboard

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk7-n`
**Area:** `src/Ui/Layout/StackupGraphicExport.cs` (new), `TechEditorView.axaml.cs`
**Depends on:** brief 2 (and brief 6 for the menu item) · **Independent of:** 3, 4, 5

---

## 0. What this brief delivers

Right-click anywhere on the drawing ▸ **Copy**, and the stackup cross-section is on the clipboard as a
picture that pastes into a document, a slide, a message or a fab note.

### The instruction that governs this brief

The owner: *"We already have a lot of export to clipboard implementation. Reuse all of that because
there are many pitfalls with copy to clipboard that have already [been] solved and don't want to do
that again."*

They are right, and the pitfalls are documented in the code. **Write essentially no clipboard code.**

---

## 1. The existing path, and what each piece of it already knows

`src/Ui/WBond/WBondGraphicExport.cs` is the model — it is the most recent adoption of this path and its
header says outright that it is reuse rather than new plumbing. Read it, then write the same file for
the stackup.

```csharp
void Compose(SKCanvas c) => StackupRenderer.Draw(c, scene, theme, overlay: null);

byte[] pdf    = PlotExporter.BuildPdfBytes(Compose);
string svg    = PlotExporter.BuildSvgString(Compose);
var    bitmap = BuildBitmap(Compose);          // 2x raster, best effort

await PlotExporter.SetClipboardDataAsync(anchor, pdf, svg, json: string.Empty, bitmap);
```

What that buys, none of which should be re-derived:

- **Three formats at once, richest first** — PDF, SVG and a 2× bitmap — so the receiving application
  picks whichever it understands. A single-format copy is the one that pastes as nothing into half the
  applications people use.
- **`WindowsClipboard`'s bypass.** `SetClipboardDataAsync` routes through it on Windows with the page
  dimensions, because the cross-platform path does not carry what Windows needs.
- **The text fallback.**
- **The raster is best-effort**: `BuildBitmap` catches and returns null, so a raster failure never costs
  the vector formats. Keep that `catch`; it is not sloppiness.
- **`PagePlacement.Letter`** and `PlotExporter.PageW`/`PageH` (792 × 612) — the page every other copy in
  this application composes onto.

### R-stk7-1 — the colour variant comes from `ClipboardRenderPolicy`, always

`ClipboardRenderPolicy.Resolve()` returns `(ColorVariant, TransparentBackground)` from the user's own
preferences, and its doc comment states the contract in capital letters: **every** copy command must
resolve variant and background through it, never take them as a per-call parameter. So:

```csharp
var (variant, transparent) = ClipboardRenderPolicy.Resolve();
var theme = StackupRenderTheme.FromTheme(ThemeService.Active, variant);
```

A user who has set "always copy in light mode" has set it for this too.

---

## 2. What gets copied

### R-stk7-2 — the whole stackup, never the scrolled window

The drawing lives in a `ScrollViewer`. A copy of what happens to be visible is a crop, and a crop of a
stackup is a picture missing layers — which is not obviously wrong to anyone looking at it later.

Build a **fresh scene at the page's width** and render that. Do not screenshot the control, do not
reuse the control's cached scene, and do not reach for `RenderTargetBitmap` over the visual tree. The
scene is a pure function of `(Technology, width, options)` — that is exactly what it is for.

The scene's intrinsic height at the page width may exceed the page. Fit it: scale uniformly to the page's
usable area with `WBondGraphicExport`'s own `MarginFraction`, centred. Uniform scale only — a stackup
stretched on one axis misrepresents every thickness in it.

### R-stk7-3 — an export is artwork, not an editing surface

`overlay: null`. No selection outline, no hover, no grippers, no insertion line, no drag ghost. This is
the same decision `WBondGraphicExport` records as `Overlay = null` and `ShowGrid = false`: what is copied
is the design, not the state of somebody's pointer.

The boundary-condition notes, the band names, the specs and the ground-reference mark all **stay** —
they are content. There is nothing else on the picture to strip: brief 1 `R-stk1-4` keeps the drawing
free of annotation, so a copied stackup is the same clean cross-section the tab shows.

### R-stk7-4 — the menu item, and the keystroke

The Copy item from brief 6's menu, on every target and on empty space alike. Plus `Ctrl`/`Cmd`+`C`
while the drawing has focus.

**Check what `Ctrl+C` already does in this tab before wiring it.** If it is taken — by a selectable
text block in the summary row, or by the card list — the drawing having focus is the discriminator, and
the handler must not steal the keystroke when focus is elsewhere in the tab.

---

## 3. Tests

`tests/Ui.Tests/Stackup/StackupCopyTests.cs`. Clipboard writes themselves are not assertable headlessly;
assert everything up to the write, which is where all the decisions are.

| Tag | Test |
|---|---|
| `R-stk7-2` | The composed SVG contains every band name in the technology, including entries that would be scrolled out of view at the control's height. This is the crop test, and it is the one that matters. |
| `R-stk7-2` | A tall technology scales to fit the page: the composed content's bounds lie inside the page rect, and the x and y scales are equal. |
| `R-stk7-3` | With a selection set, the composed SVG is **byte-identical** to the one composed with nothing selected. |
| `R-stk7-1` | `ForceLight` and `ForceDark` produce different SVGs; `FollowSystem` matches the active variant's. |
| `R-stk7-1` | The composed output is byte-identical across two calls with the same preferences — brief 1's determinism gate, at the place it is user-visible. |
| — | `BuildPdfBytes` and `BuildSvgString` both return non-empty for every shipped technology, and for an empty stackup. |

---

## 4. Scope

- **Do not modify `PlotExporter`, `WindowsClipboard` or `ClipboardRenderPolicy`.** If the stackup needs
  something they do not offer, stop and report it — those files carry solved problems that are
  expensive to re-solve and cheap to break.
- No "Export to file…" and no PNG-to-disk. Copy only. A file export is a dialog, a path, an overwrite
  question and a format choice, and none of that was asked for.
- No copy of the stackup as **data** — `.ctech` merge and export already exist
  (`TechnologyExportDialog`, `TechnologyMerge`) and this item is about the picture.

**On completion:** record findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.
