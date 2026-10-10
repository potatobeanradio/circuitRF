# Brief IM-5 — The dialog: one window for artwork and pictures

**Series:** `brief-img-0-overview.md` (§4 UX principles, D5, D6, D7, D14, D16) · **Tag:** `R-im5-<m>`
**Depends on:** IM-4
**Area:** `src/Ui/Recognition/` — `CreateSchematicFromArtworkDialog.axaml[.cs]` and its view model generalised; new
`ImageSourcePane.axaml[.cs]`, `ImageOverlayCanvas.cs`, `ImageSourceViewModel.cs`, `ImageLayerRowViewModel.cs`,
`ImageScaleViewModel.cs`; shared controls extracted (`PartsTablePanel`, `RecognitionReportStrip`, `RecognitionTargetRow`);
`docs/user/src` (the page)

**UX is this series' priority, and this phase is most of it.** The owner reviews the dialog when this phase lands,
before IM-10 adds the schematic-picture mode.

---

## 1. Goal

Drop a picture, see what was read drawn over it, press Create. The artwork dialog and the picture dialog are **one
window** whose top half is the source; everything below it is the AS-8 dialog the user already knows.

## 2. Requirements

**R-im5-1 — One dialog, a source pane.** The AS-8 dialog becomes a shell with a **source pane** above the shared body:
- **Artwork source** (today's behaviour, unchanged): the focused layout, its scope row and companion files.
- **Image source** (new, R-im5-2).
The body — target row, options row, parts table, report strip, Export/Import Parts, Learn These Glyphs, Create/Replace
— is extracted into `RecognitionTargetRow`, `PartsTablePanel` and `RecognitionReportStrip` UserControls and a shared
`RecognitionSessionViewModel` (target, table edits as a CSV overlay, debounced cancellable re-run, create), so no row
of the table, the report or the target logic exists twice. The window title follows source and output: *Create
Schematic from Artwork*, *Create Schematic from Image*, *Create Layout from Image*. All AS-8 view-model tests pass
unchanged.

**R-im5-2 — The image source pane.** Top to bottom:
- **Empty state** (no picture yet): the whole pane is a drop zone — *Drop a picture here, paste it (Cmd/Ctrl+V), or
  Browse…* — with the read formats in one small line. Dropping a file, pasting, or Browse fills it. A drop of a
  non-picture file is refused in the status line with the IM-1 sentence; the zone stays.
- **Source row**: thumbnail, file name (or *Pasted picture*), pixel size, **Replace…** (browse) and **Paste**.
- **It is** — segmented: *Auto (schematic drawing)* / *Schematic* / *Layout*. Auto shows what it decided in the
  segment's own label, with its confidence as the dot the parts table already uses.
- **Make** — segmented: *Schematic* / *Layout*. *Layout* is disabled for a schematic picture with the tooltip *Create
  the schematic, then use Update Layout from Schematic* (D3).

**R-im5-3 — The picture canvas.** The left two-thirds of the body for an image source (the parts table moves to the
right column; for an artwork source the layout is unchanged). Zoom (wheel, pinch), pan (drag, space-drag), fit
(double-click). The overlay, each class toggleable from a small legend:
- layout picture — traced copper outlined per layer in the layer's own colour, drills as circles, ignored clusters
  hatched, recognised parts boxed with their designator, lines drawn as their centre lines labelled with their
  component (`MLIN W=…`), ports as the schematic's port glyph;
- schematic picture (IM-10 lights this up) — wires, junction dots, symbols boxed with kind and orientation, text boxes
  with what was read, net labels;
- **unknowns in amber** everywhere (an unread value, a `?` kind, a dangling wire end, a cluster mapped to nothing).
Clicking an overlay item selects its parts-table row (and a report item's anchor); selecting a row or report item
highlights and, if needed, pans to its overlay item. **Drag on the canvas sets the scope rectangle**; a *Whole picture*
button clears it. The overlay is rebuilt only from the recognition result; the canvas never decides anything.

**R-im5-4 — Scale (D6).** A row shown for a layout picture: the chosen metres-per-pixel with its unit, and a combo of
the evidence candidates (*As placed*, *From parts — 4 × 0603, 2 × 0402*, *Two points…*, *Line impedance…*, *File
resolution 600 dpi*). **Two points…** turns the canvas into a measure tool: click, click, a small inline box at the
second point takes a distance with its unit (a bare number is refused in place); **Line impedance…** asks for a click on
a trace and a Z0. The resolution line (R-im3-3) sits beside the value. With no usable evidence the row is highlighted,
the canvas shows *Set the scale: Two points…* as its one prompt, and Create is disabled with that as its tooltip.

**R-im5-5 — Layers (D7).** A compact table for a layout picture: swatch, share, layer combo (the technology's layers,
*Drill*, *Board outline*, *Silkscreen*, *Background*, *Ignore*). Hovering a row flashes that cluster on the canvas. An
edited row is marked and never overwritten by a re-run. **Save Preset…** / the preset combo (R-im3-8); a matching
preset is offered in the status line — *Colours match preset "viewer dark"* — and applied with one click, never
silently.

**R-im5-6 — Technology.** A combo of the workspace's technologies (default: the workspace default, or the technology
of the layout a placed bitmap sits in). Changing it re-runs.

**R-im5-7 — Advanced** (an expander, collapsed, remembered per user): threshold and window (schematic pictures),
minimum feature, simplify tolerance, snap angle and 45° snapping, colour clusters (max k, merge ΔE), crossing rule
(IM-7), and *Keep the source picture under the result* (the D5 setting, per run). Each change re-runs, debounced.

**R-im5-8 — Failure on the picture (D16).** When IM-2 reads *None*, the canvas shows the picture dimmed, one sentence
(*No drawing found: a photograph-like picture with no straight line work*) and two links, *Read it as a schematic* /
*Read it as a layout*, which set the kind. A refusal after reading (no copper at the mapped colours, nothing
recognised) is the same: dimmed picture, the sentence, the control that answers it highlighted. Create is disabled
with the sentence as its tooltip. **Never a modal error box.**

**R-im5-9 — Create.** Calls `ImageTrace.Run` (Make Layout) or `ImageRecognition.Run` (Make Schematic from a layout
picture) — the CLI's entry points (D2). The result opens focused with its underlay; the report goes to Messages; the
Tuning panel lists unknown values (as AS-8 R-as8-4). Into-the-document targets (D14) apply their edit as one undo step
in the open editor and select what was added.

**R-im5-10 — Busy and cancel.** The progress strip of AS-8; a re-run cancels the previous one; closing the dialog
cancels and writes nothing.

**R-im5-11 — User page.** `docs/user/src`: *Create from Image* — the two kinds of picture, the two outputs, the
scale and its evidence, layers and presets, the underlay, what reads well and what does not (the overview §1 table, as
a user would say it), failure, and links to the artwork page. Edit sources only; **do not run DocGen**.

## 3. Not in this phase
Menu rows, paste, context menus (IM-6); the schematic-picture mode's content (IM-10 — the *Schematic* kind is
selectable here and reports *not available yet* until then).

## 4. Gates (minimal tests, run only these classes)
View-model tests, headless:
- `ImageSourceViewModelTests` — a dropped non-picture is refused and the drop zone stays; *None* disables Create
  with the sentence and the override sets the kind; Make *Layout* is disabled for a schematic picture.
- `ImageScaleViewModelTests` — with only file-resolution evidence Create is disabled; *Two points* with `1.6 mm`
  enables it and with `1.6` refuses in place; the chosen evidence reaches the trace input.
- `ImageLayerRowViewModelTests` — an edited row survives a re-run caused by an option change; a matching preset is
  offered, not applied.
- `RecognitionSessionViewModelTests` — the AS-8 cases still pass against the extracted session (moved, not
  rewritten); Create from an image source calls the same entry point as the CLI with the same options (a recorded
  fake).
- `ImageOverlayCanvasTests` — selecting a row selects the overlay item and back (a pure mapping, no rendering).
