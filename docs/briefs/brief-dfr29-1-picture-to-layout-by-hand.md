# Brief DFR29-1 — Create Layout from Image: correct the reading by hand before Create

**Series:** designer feedback round 29 (2026-10-27). · **Tag:** `R-dfr29-1-<n>`
**Area:** `src/Ui/Recognition/ImageSourceViewModel.cs`, `ImageOverlayCanvas.cs`, `ImageReadingPanel.axaml(.cs)`,
`ImageSourcePane.axaml`; `src/Design/Layout/Recognition/Image/ImageTrace.cs` (the trace input and `Build`),
`ImageLayerMap.cs`, `PictureTechnology.cs`; `src/Cli/PictureInput.cs` (flags); `docs/user/src/reference/create-from-image.md`.
Findings go in `src/Ui/RESOLVED.md` and `src/Design/Layout/Recognition/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Imaging/ImageTraceGeometryTests.cs` (the round-hole via test is the model),
`tests/Ui.Tests/Recognition/ImageSourceViewModelTests.cs`, `ImageScaleViewModelTests.cs`, `ImageConvertCliVerbTests.cs`.
Render each dialog change with `tools/UiSnapshot --image` (README: *The picture dialog*). **No recognizer work:** this
brief adds hand tools and never changes how a colour or a shape is read.

---

## 0. Why

The designer traced a datasheet's test-board drawing (17 mm × 17 mm, FR-4 0.2 mm, no Gerber published) with Create
Layout from Image. The copper fill came out right and the size was right. What he could not do in the dialog:

- mark the vias himself (fixed in part on 2026-10-27: a round hole in the copper up to 1 mm is now a via, but nothing
  lets him add a missed one, remove a wrong one, or say a hole is not plated);
- say what a piece of geometry is: a via, a part, or a shape / fill area on a chosen layer;
- draw the board outline;
- state the stackup: the impedance figures used the chosen technology's, and no shipped technology is FR-4 0.2 mm;
- decide what the two large mounting holes are: they are not plated vias, and nothing says what else they could be.

He then placed every via by hand in the layout editor. He asked for a manual way to settle all of these in the dialog,
before the layout is written.

What already exists to build on: the dialog's overlay is hit-tested (`SelectedItem`, `ImageOverlayItem`), the
schematic-picture mode keeps per-picture corrections (`Overrides`, *Clear corrections*), the Board corners… quad is
four draggable handles, `ImageLayerRole` has `Drill`, `BoardOutline` and `Ignore`, and a via layer can be non-plated
(`StackupLayer.Plated = false`, GI1). **No shipped technology carries a non-plated hole layer.**

## 1. What to do

### R-dfr29-1-1 — Corrections are kept on the picture and applied by the trace

Add a list of corrections to `ImageTraceInput` (Design), each in picture pixels so it survives a re-scale:

| Correction | Effect in `Build` |
|---|---|
| `Via(x, y, drillPx?)` | a via at that point (drill from the hole under it, else the technology's default via), the copper whole under it |
| `Hole(x, y)` | the hole under the point is a non-plated hole (R-dfr29-1-5), the copper opening kept |
| `NotVia(x, y)` | the hole under the point stays an opening (undoes the automatic round-hole via) |
| `Copper(polygonPx, layer)` | a filled shape on that layer, unioned with the traced copper on it |
| `Clear(polygonPx)` | removes traced copper inside it on every layer (a part's body the colours read as copper) |
| `Outline(polygonPx)` | the board outline, written on the technology's outline layer |

They are applied after the colour trace, so a correction never changes what is read, only what is written. A correction
that lands on nothing (a `Via` off the copper, a `Hole` with no hole) is refused in place with one sentence, never
dropped. The report counts them: *"4 corrections: 2 vias added, 1 hole not plated, 1 copper fill on Top Copper."*

### R-dfr29-1-2 — Click a piece to say what it is

In layout-picture mode, a click on a traced region or a hole selects it (the overlay already hit-tests). A small
picker beside it (placed with `ImageReadingPanel.Place`, which keeps it off the point) offers what the piece can be:

- a hole: **Via** · **Hole, not plated** · **Opening**;
- a copper region: **Copper on ▾ <layer>** (a picker of the technology's conductors, never a text box —
  `src/Ui/CLAUDE.md`'s picker rule) · **Not copper** (a `Clear` of its outline).

**And every one like it** (a check box, on by default for holes): applies the choice to every hole of the same
diameter within 10 % — 70 vias are one click, not 70. Each choice is one correction and one undo step
(`Cmd/Ctrl+Z` in the dialog, as Manual plot digitising already undoes).

### R-dfr29-1-3 — Draw on the picture: via, copper, outline

A tool strip over the canvas (beside the legend), each tool armed until Escape:

- **Via** — click to place one; a click within a hole's radius snaps to its centre.
- **Copper** — drag a rectangle, or click points and double-click to close a polygon; the layer is the strip's layer
  picker (the technology's conductors, top copper first).
- **Outline** — the same gestures, one shape; drawing another replaces it. **Board corners… offers it too:** a check box
  in the Size box, *Use as the board outline*, on by default, writes the quad (after rectification, the rectangle) as the
  outline.

Draw with the overlay's own pen classes so a correction looks like what it makes. Corrections are drawn over the
reading in a distinct dash, and *Clear corrections* (already on the canvas for a schematic picture) clears them here too.

### R-dfr29-1-4 — The stackup the picture is read with

Beside **Technology**, a **Stackup…** button opens a compact form: substrate εr, substrate height and copper thickness
(each with its unit), filled from the chosen technology. Edited, the dialog reads with a **copy** of the technology —
the `PictureTechnology` route IM-4 already uses — and Create writes that copy into the new cell's workspace as
`<cell>.ctech`, referenced by the layout, so the workspace's own technology is never changed. The report's stackup line
says which: *"The stackup is FR-4 0.2 mm, stated for this picture (from PCB 2-Layer FR-4 70 mil)."*

**Owner decision D1:** the copy is written beside the cell (recommended: the picture's board is not the workspace's),
or the form edits the workspace technology in place (simpler, but changes every layout on it).

### R-dfr29-1-5 — A hole that is not plated

`Hole` corrections need a non-plated via layer. When the technology has one (`Kind = Via`, `Plated = false`), use it.
When it has none, the stackup copy of R-dfr29-1-4 gains one named **Non-plated hole** spanning the board, and the report
says so. **Owner decision D2:** add it to the copy (recommended) or refuse the correction naming the technology editor.
Neither path edits a shared technology silently.

### R-dfr29-1-6 — The CLI says the same things

`convert <picture>` gains `--via x,y` / `--hole x,y` / `--not-via x,y` (repeatable, `px` on every number, as `--region`),
`--copper "x,y;x,y;…=<layer>"`, `--clear "x,y;…"`, `--outline "x,y;…"`, and `--stackup "er=4.3,h=0.2mm,t=35um"`. Each is the
same correction record, so the dialog and the CLI write the same bytes. `result.convert` lists the corrections as applied.
(`CLI verbs aren't restricted by the UI`; the dialog's corrections are a capability, not a view-model feature.)

## 2. Gates

1. A picture with three round holes in a pour: one automatic via removed (`NotVia`), one made a non-plated hole on a
   technology without one (D2's path), one left a via — the written layout has 1 plated via, 1 non-plated hole on the
   added layer, and the pour has 1 opening.
2. *And every one like it*: 12 holes at 0.5 mm and 2 at 2.6 mm; one click on a 0.5 mm hole marks 12, none of the 2.6 mm.
3. A `Copper` rectangle and an `Outline` rectangle drawn on the picture land within half a pixel of where they were
   drawn, on the chosen layers.
4. Board corners with *Use as the board outline* writes the 17 mm × 17 mm rectangle on the outline layer.
5. A stated stackup: the new cell's layout references its own `.ctech`, εr/h/t as typed, and the workspace's `.ctech` is
   byte-identical before and after.
6. CLI parity: the same picture with the same corrections through `convert` and through the dialog's Create writes the
   same `.clay` bytes.
7. Dialog renders (`tools/UiSnapshot --image`): the tool strip, a hole's picker, a correction drawn — at most two pictures.

## 3. Not in this brief

- Reading thin traces better (the narrow lines needed redrawing) — recognizer work, out of scope.
- The Auto kind on the painted board picture reads as a plot; Create Layout from Image… names the kind, so it is left.
- Parts: a *Part* type was asked for. A part's pads are already read from copper; a part's body is a `Clear`. Placing
  footprints from a picture is a later brief if it is wanted.
