# Brief IML-8 — Parts the size of the picture's, the picture under them

**Series:** `brief-iml-0-overview.md` (§0 "very important — size", §4 size targets, L10) · **Tag:** `R-iml8-<m>`
**Depends on:** IML-7
**Area:** `src/Design/Schematic/GlyphBody.cs` (from IML-2), `src/Design/Schematic/Recognition/SizeFrame.cs` (new),
`SchematicImageEmit` (the frame), `SchematicImageRecognition.PasteDrawing` / `PasteInto` / `IntoSchematic` / `Run`
(the picture's placement), `PictureGrid.cs` (demoted), design note §10

---

## 1. Goal

The owner's most important requirement for this feature: **after a paste, each circuitRF part sits over the picture's
part, at the picture's size.** circuitRF's glyphs are fixed (L10), so what moves and scales is the **picture**: it is
scaled so that circuitRF's parts are as big as the picture's parts, and placed so that every part lies on the part it
was read from. Today the frame comes from the picture's canvas grid when it has one (`PictureGrids.Detect`, design
note §16) and the symbols override only by a factor beyond 2.5 — so a picture with a grid can come out with parts a
third of its size, and one without a grid gets a rough guess. This phase makes **part size** the frame.

## 2. Requirements

**R-iml8-1 — Body against body.** `GlyphBody.Of(kind, portCount)` (IML-2) gives each circuitRF glyph's **body** — its
drawing without the straight leads ending on its pins — in schematic units. The picture's part body is the detector's
box refined to the symbol-ink mask inside it (tight bounds of the ink, leads excluded by the same rule). For each part
whose class is `sizeReference` (IML-1) and whose turn is known:

> rᵢ = picture body long side (px) ÷ circuitRF body long side (units), measured along the part's own axis.

The frame scale **s** (px per schematic unit) is the weighted median of rᵢ (weights: confidence × class reliability,
the latter fixed per class from IML-6's val size errors). With no reference part, s comes from pass 1's part-size head
against the median `sizeReference` body; with neither, from the grid as today. `--symbol-scale` (O5) / the dialog's
override sets s directly.

**R-iml8-2 — Place, then snap.** Each part's instance origin is placed so its circuitRF body centre lands on its
picture body centre (÷ s), then **snapped to the 100-unit connection grid** — pins must be on the grid or wires do not
connect, so this is not optional. Two parts that would snap onto overlapping bodies where their picture bodies do not
overlap are separated by one grid step along the axis that keeps them in their picture order.

**R-iml8-3 — Fit the picture to the snapped parts.** Snapping moves parts by up to half a step. Fit the picture's
placement — a uniform scale s′ and an offset — by least squares over the pairs (picture body centre → snapped
circuitRF body centre), with s′ held within ±10 % of s (L10: the parts stay their size; the picture absorbs the
rounding). The bitmap is placed with s′ and that offset. Report the residuals: the median and largest distance between
a part's placed body centre and its picture body centre, in grid steps.

**R-iml8-4 — Wires follow the parts, not the pixels.** `SchematicImageDrawing` already draws the picture's wire graph
with node coordinates taken from the pins they reach; with the new frame, a node a pin stands on takes the snapped pin,
and every other node maps through the fitted transform and snaps to the grid. A part whose picture leads are longer
or shorter than circuitRF's gets its wires stretched or shortened — the wire, never the glyph (L10).

**R-iml8-5 — Every target.** The same frame is used by Paste (`PasteDrawing` → `PasteInto`, the picture under the
drawing, as now), by the dialog's preview and its Make (new cell, or into a placed bitmap), by `recognize` and MCP
(the written `.csch` carries the bitmap at the fitted size), and by `IntoSchematic` on a placed bitmap — where **the
bitmap the user already placed is moved and resized** to the fit, in the same undo step (D14), because the user's
bitmap is the picture the parts must sit on.

**R-iml8-6 — Said once.** The paste's outcome line (IMF-9 R-imf9-4) gains nothing unless the fit is poor: when the
largest residual exceeds ½ step, it adds "some parts could not be placed exactly over the picture" and those parts are
in the review selection (IML-9).

## 3. Gates

- `SizeFrameTests` (no data home, < 1 s):
  - a committed fixture drawn at three zooms (resistor bodies 20, 60, 180 px) — after paste, every reference part's
    placed body long side is within ±10 % of the picture's at all three;
  - a picture **with** a canvas grid whose pitch disagrees with the parts' size — size wins (the case §16's frame got
    wrong);
  - a picture with mixed classes (R, C, L, ground, a source) — one s for all, every residual ≤ ¼ step on our own
    renders;
  - a picture with no reference part (a box and lines only) — the head's size is used and reported;
  - a crowded picture where two parts snap together — separated, picture order kept.
- `ImageScreenshotPasteTests` and `PasteDatasheetEndToEndTests` updated to the new frame, asserting overlap rather than
  the old grid frame.
- Over the data home (skips when absent): the §4 size numbers — share of parts within ±20 %, median and 95th-percentile
  centre residual — in design note §10.

## 4. Not in this phase

Changing any glyph (L10). Off-grid placement. Rotating the picture (a scan's skew ≤ 2° is left as it is; parts snap
to the grid, the residual reports it).

## 5. Traps

- **Long side along the part's own axis** — a capacitor's plates are wider than its body is long; measure the axis
  the pins lie on, in both the picture and the glyph, or a turned capacitor gives the wrong ratio.
- **The ink-refined box** must exclude the label stack and pin markers (IML-3's pin squares) — measure inside the
  symbol mask, not all ink.
- **A two-circuit picture** has one frame; if the two halves disagree by more than 25 % in rᵢ, report it rather than
  splitting the frame.
