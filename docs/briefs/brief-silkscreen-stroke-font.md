# Brief — A stroke font for silkscreen text (Hershey Roman Simplex)

**Tag:** `R-ssf-<n>` · **Depends on:** AS-10 (`src/Design/Layout/Recognition/Silkscreen/`, its embedded Hershey glyphs and
`LICENSE-hershey.txt`) · **Area:** `src/Design/Layout/Text/` (new: `StrokeFont.cs`, `StrokeText.cs`),
`src/Design/resources/stroke-font/`, `src/Design/Layout/LayoutModel.cs` (`LabelShape`), `src/Render/Renderers/LayoutRenderer*.cs`,
`src/Render/DocumentExtents.cs`, `src/Design/Layout/Interchange/GerberExport.cs`, `src/Design/Layout/Footprints/FootprintLabel.cs`,
`src/Ui/Layout/LayoutEditorViewModel.Flatten.cs`, `src/Ui/ViewModels/LayoutShapePropertiesViewModel.cs`,
tests in `tests/Ui.Tests/Layout/Text/`

---

## 1. Goal

Silkscreen text that a board shop prints as drawn and that circuitRF can read back. Today every layout label is drawn in
a bundled TrueType face and a Gerber export fills each glyph's outline as a region. Board tools write silkscreen with a
plotter stroke font: one pen of one width, a few strokes per glyph. Give circuitRF one — Hershey Roman Simplex, already
in the repo for AS-10 — and make it the font of every layout label (D1).

What it buys, each of which is a gate below:
1. **Fabrication.** One stated pen width, so a silkscreen minimum-line-width rule has something to check; a Gerber legend
   of D01 strokes rather than curve-flattened regions with holes (a fraction of the size).
2. **Round trip.** AS-10 reads stroked text only. A board circuitRF exports today comes back as "filled shapes, not read".
3. **One shape everywhere.** The TrueType faces load through Avalonia's `AssetLoader`, so a label flattened headlessly
   (`circuitrf convert`, `render`) is a different SHAPE from the same label in the app, and every label-carrying export
   says so (`LayoutTextOutline.TypefaceSource`, root `CLAUDE.md`). A stroke font is plain embedded data in `src/Design`:
   identical in both, and the label's extent becomes computable below the firewall.

## 2. What exists (read before starting)

- `LabelShape` (`LayoutModel.cs`): `Text`, `Height` (DBU), `RotationDegrees` (any angle), `Style`
  (`LabelFontStyle { Regular, Bold, Italic, Condensed }`), `HAlign`/`VAlign`, `IsPort` and the port fields.
- **`Height` is the TrueType EM size** — `LayoutTextOutline.BuildGlyphContours` passes it straight to `SKFont`. The cap
  height of the bundled face is about 0.7 of it.
- Canvas: `LayoutRenderer.DrawLabelText`. Extent: `LayoutRenderer.MeasureLabelWorldBbox` (in `src/Render`, because it
  needs Skia glyph metrics — which is why `LayoutHitTest` cannot move below the wall; `src/Render/RESOLVED.md`).
  `DocumentExtents` uses it.
- Gerber: `GerberExport` turns every non-port label into polygons through `LayoutTextOutline` + `LayoutTextFlatten`;
  `GerberWriter` already writes a `PathShape` as D01 strokes with a circular aperture (`IsStroke`).
- GDSII, DXF and the board-file writer emit native TEXT records — they carry the string, not geometry.
- Designators: `FootprintLabel` computes each placement's designator as a `LabelShape` on the technology's silkscreen
  role, default height `DefaultHeightMm = 0.8` (an em size today). **A mirrored placement draws its designator
  UN-mirrored** (R-fp4b-3b) — that stays.
- Flatten to Polygon (`LayoutEditorViewModel.Flatten.cs`, `FlattenToPolygonDialog`) uses the TrueType outlines.
- AS-10's `GlyphTemplates.Draw(text, capHeight)` already lays Hershey text out as centre-line strokes, and its resource
  holds only the 39 designator glyphs.

## 3. Owner decisions (settled 2026-10-08)

| # | Question | Decided |
|---|---|---|
| D1 | Which labels use the stroke font? | **Every label, on every layer, including every label in an existing `.clay`.** A per-label font choice remains; Sans is something a user picks, never a default. Few users have sent silkscreen Gerbers yet, so changing existing designs is accepted — a user who wants a label's old look switches that label to Sans. |
| D2 | What does `Height` mean for a stroke label? | **Cap height** — what a fab drawing and a board tool mean by text height. A Sans label keeps the em size, unchanged. |
| D3 | Designator default height | Keep `0.8 mm`, now a cap height (designators get ~40 % taller than today). |
| D3a | An existing label's stored `Height` | **Kept as stored and read as a cap height** (a consequence of D1 + D2): existing text draws ~40 % taller. The file is never rewritten to compensate; switching a label to Sans restores its old size exactly, because the stored value is untouched. |
| D4 | Pen width | A per-label `StrokeWidth`; unset = `Height / 6.5`, never below a minimum-width design rule on the label's layer when the technology has one. |
| D5 | Characters outside the font | Full printable ASCII from Hershey Roman Simplex; `Ω µ ° ±` from the Hershey simplex Greek and symbol glyphs. Anything else draws as a hollow box of the glyph's advance and is **counted** by every export, never dropped silently. |
| D6 | Bold / Italic / Condensed on a stroke label | Bold = pen width × 1.6; Italic = 12° shear; Condensed = 0.8 × advance. Same `Style` field, no new enum values. |
| D7 | Flatten to Polygon on a stroke label | Gives **paths** (one `PathShape` per pen stroke at the label's pen width), with a "Polygons" choice in the dialog for the stroke outlines. |

## 4. Requirements

**R-ssf-1 — The font is data in `src/Design`.** `src/Design/resources/stroke-font/hershey-roman-simplex.txt` holds the
full printable ASCII set in AS-10's line format (plus the D5 extras), with `LICENSE-hershey.txt` beside it carrying the
acknowledgement the distribution requires — the text AS-10 already ships, extended to the new glyphs. **Its last
paragraph must be rewritten, not just moved:** AS-10's copy says the glyphs "are used as matching templates … nothing is
drawn with them", which this brief makes false. It must say what is now true — the font draws layout labels in the
stroke font (canvas, rendered pictures, Gerber export) and supplies AS-10's matching templates — and name every glyph
set the file covers (the ASCII set, the D5 extras and their Hershey sources, and `hershey-variants.txt`). **AS-10's
`silkscreen-glyphs/hershey-roman-simplex.txt` is removed and `GlyphTemplates` reads its 39 matching templates out of
the shared font** — one copy of the data. `hershey-variants.txt` stays with AS-10: variants are matching templates,
never drawn. Nothing native; no new package.

**R-ssf-2 — `StrokeFont` lays text out; nothing else does.** `StrokeFont.Layout(text, style, capHeight)` returns centre-
line strokes in the text's own frame (baseline y = 0, reading +x), the advance, and the count of characters not in the
font. `StrokeText.For(LabelShape)` applies alignment, rotation (any angle) and position, in DBU, and returns the
strokes plus the pen width — the ONE place a stroke label becomes geometry. The canvas, extents, hit-testing, Gerber
export and Flatten all call it. `GlyphTemplates.Draw` becomes a call to it.

**R-ssf-3 — The model.** `LabelShape` gains `Font` (`LabelFont { Stroke, Sans }`, **`Stroke` the default**) and
`StrokeWidth` (nullable DBU). Both are omitted from the file at their defaults (`Stroke`, null), and `"Font": "Sans"` is
written only for a label the user switched: so an existing `.clay` — whose labels have no `Font` key — loads as all-
stroke, and re-saves byte for byte, with no `FormatVersion` bump. It DRAWS differently (D1, D3a); that is the decision,
not a regression. An older circuitRF reading a newer file ignores the key and draws Sans. `TechModel`/`.ctech`
unchanged.

**R-ssf-4 — Defaults (D1, D3, D4).** Nothing chooses a font: the Label tool, `FootprintLabel`'s designators, Paste,
the schematic-to-layout generator and every importer make labels with the model's default, `Stroke`. Grep every
`new LabelShape` and confirm none sets `Font` — a site that sets `Sans` is a defect. The pen width default is computed at
use, never stored, so changing a label's height changes its pen with it until the user sets a width.

**R-ssf-5 — Drawing and extent.** `DrawLabelText` draws a stroke label as its strokes with round caps and joins at the
pen width — the same geometry the Gerber gets. A stroke label's world bbox is the strokes' box grown by half the pen,
computed in `src/Design` (`StrokeText.Bounds`); `MeasureLabelWorldBbox` returns it for a stroke label. Hit-testing,
snapping, selection outline and `DocumentExtents` follow without further change. Sans labels: untouched.

**R-ssf-6 — Gerber.** A stroke label is written as D01 strokes with one circular aperture of its pen width — no
regions. A Sans label is converted exactly as today. The export result counts stroke labels and Sans labels separately
and reports characters drawn as boxes (D5). **A file with only stroke labels no longer carries the "headless face"
note**: that note is about the TrueType seam and is said only when a Sans label was converted.

**R-ssf-7 — Other writers.** GDSII, DXF and the board-file writer keep emitting TEXT records. The board-file writer
states the label's pen width where its text syntax has a thickness, so the receiving tool draws the same weight.
Readers: an imported text record becomes a label in the default font (Stroke), with its height read as a cap height.

**R-ssf-8 — Editing.** The label's properties panel shows Font (Sans / Stroke) and, for Stroke, Pen width (blank =
default, showing the default as a watermark). Switching font keeps `Height` as stored — it means em size for Sans and
cap height for Stroke, and the panel's height caption says which.

**R-ssf-9 — Flatten (D7).** Flatten to Polygon on a stroke label produces paths by default, polygons on request; both
from `StrokeText.For`.

**R-ssf-10 — The round trip is a gate, not a hope.** A layout with stroke designators at 0°, 90°, 180°, 270° and 45°,
exported to Gerber and imported back, is read by AS-10's `SilkscreenText.Read` with every designator correct (45° is
allowed to come back unread — AS-10 reads the four cardinal frames — and is listed as such in the test's own comment).

## 5. Gates (minimal tests; run only these classes)

- `StrokeFontTests` — every printable ASCII character has a glyph; a string's advance is the sum of its glyphs'; an
  unknown character is counted and boxed (D5).
- `StrokeTextTests` — a label's strokes under each `HAlign`/`VAlign` and a 30° rotation land where the alignment says;
  `Bounds` contains every stroke grown by half the pen.
- `StrokeLabelPersistenceTests` — an existing `.clay` with labels loads as all-stroke and re-saves byte for byte; a
  Sans label round-trips `Font`, and any label `StrokeWidth`.
- `GerberStrokeLabelTests` — a stroke label exports as D01 strokes with one aperture of its pen width and no `G36`;
  a label switched to Sans exports byte-identical to what the same label exported before this brief.
- `StrokeTextRoundTripTests` — R-ssf-10.
- Existing gates to run because they are touched: AS-10's `StrokeGlyphTests` (the font moved), the Gerber export and
  label flatten test classes (`grep -l "LabelsConvertedToGeometry\|BuildGlyphContours" tests/Ui.Tests`), and
  `RenderCliVerbTests` (label extents). **Every byte-identity test whose fixture holds a label or a designator WILL
  change** (D1 reaches existing labels): regenerate its expected data deliberately, never by loosening the comparison,
  and list every file regenerated in the hand-off.

## 6. Docs

- Design: a `docs/design/layout-view.md` section on label fonts (the two meanings of `Height`, the pen default, the
  one-geometry rule), and `docs/design/cli.md`'s `render`/`convert` notes on the headless-face note now applying to
  Sans labels only.
- User: the layout editor's label page — Font, Pen width, the stroke font as every label's font with Sans as a
  per-label choice, and Height as a cap height. No change history in the page (no "labels used to be…").
- `LICENSE-hershey.txt`'s usage paragraph (R-ssf-1) — checked by reading it at hand-off, since "nothing is drawn with
  them" would otherwise survive the move unnoticed.
- Findings to `src/Design/RESOLVED.md` / `src/Render/RESOLVED.md`, never `CLAUDE.md`. DocGen at the end of the series
  (every figure with a label or a designator will change).

## 7. Out of scope

A silkscreen DRC rule set (minimum width, clearance to pads) — the pen width makes it possible; it is its own brief.
A second stroke font or user-supplied fonts. A command switching many labels to Sans at once (one label at a time
through the properties panel is the way back). Mirrored text.
