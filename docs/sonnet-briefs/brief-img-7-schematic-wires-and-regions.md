# Brief IM-7 — A schematic picture: wires, junctions, text and symbol regions

**Series:** `brief-img-0-overview.md` (D9, D11, D16) · **Tag:** `R-im7-<m>`
**Depends on:** IM-2 (runs in parallel with IM-3 … IM-6)
**Area:** new `src/Design/Schematic/Recognition/` (`SchematicImageReading.cs`, `WireGraph.cs`, `TextRegions.cs`,
`SymbolRegions.cs`, `SchematicImageOptions.cs`, `SchematicImageReport.cs`), design note §8

---

## 1. Goal

Split a schematic picture into the three things it is made of — **wires**, **words** and **symbols** — and know
which wire ends touch which symbol. Nothing here names a component; IM-8 does.

## 2. Requirements

**R-im7-1 — Entry point.** `SchematicImageReading.Read(ImageSource, SchematicImageOptions, RunControl?) →
SchematicImageRead`: the stroke width, the text regions, the wire graph, the symbol regions with their attachment
points, and a report. Pure.

**R-im7-2 — Normalise.** Binarise (R-im1-3); a coloured drawing (a coloured wire layer, a red highlight) is first
reduced to its darkest clusters, a light background grid (engineering paper, a tool's dot grid) removed as the cluster
of periodic small components on a lattice; deskew by the dominant orientation of long straight runs when it is within
± 5° and more than 0.3° (rotate, re-binarise). The stroke width `w` (R-im1-5) scales every tolerance below; options
state them in units of `w`.

**R-im7-3 — Text regions first.** Components small relative to the drawing (height 2–12 `w`, not on a long straight
run) grouped into rows and words by AS-10's line grouping (`StrokeGlyphs` units → lines), offered whole and split at
the widest gap. Each word is a region with its box and its skeleton strokes for IM-9; **its pixels are removed before
wires are traced**, so a label touching a wire does not become a wire stub. A text candidate touching a wire along more
than 2 `w` stays (it is more likely a symbol detail) and is reported.

**R-im7-4 — Wires.** On the remainder, the skeleton graph (R-im1-6). An edge is a **wire segment** when it is straight
(residual < 0.25 `w`) and longer than 3 `w`, and orthogonal within the snap angle; a non-orthogonal straight run longer
than 8 `w` is a wire too (diagonal wires exist) and is reported. Collinear wire segments through a degree-2 node merge.
`WireGraph`: nodes at segment ends, T-junctions and crossings; each node knows its degree and whether a **junction
dot** sits on it — a filled blob of diameter 2–6 `w` centred within 1 `w` (a distance-transform peak, R-im1-5).

**R-im7-5 — Crossings (D11).** Degree 3 → connected. Degree 4 → connected per the option *Never / With a dot*
(default) */ Always*. A **hop** (a small semicircle on one wire at a crossing) is recognised as not-connected whatever
the option. Each degree-4 node and its reading are in the result so the overlay can draw them and the user can flip
one (IM-10).

**R-im7-6 — Symbol regions.** Everything that is not wire and not text: the residue components, grown by 1 `w`, then
**merged** when they face each other across a gap shorter than the larger one's extent along the facing axis — which
joins a capacitor's two plates, a ground's three bars and an inductor's separate humps into one region. A region's
**attachment points** are the wire ends that stop within 1.5 `w` of it, each with its incoming direction. A region with
no attachment is a decoration (a logo, a frame, a title block's lines) and is reported, not passed on. A long
rectangular frame enclosing most of the drawing is a border and is removed first.

**R-im7-7 — Net labels and power marks.** A text region at a dangling wire end (within 2 `w`, aligned with the wire) is
that wire's **net label**; a short perpendicular bar or an up-arrow at a dangling end is a **supply mark** and keeps the
word beside it as its net name. Both are attachments of a kind IM-10 turns into connections by name.

**R-im7-8 — The report.** Stroke width, deskew angle, grid removed, words found, wire segments, junction dots, crossings
by reading, hops, symbol regions, decorations removed, dangling ends. Refused only when there is **no wire at all** —
*no connected line work: this picture has no wires to read* — or no symbol region.

## 3. Not in this phase
What each symbol is (IM-8), what each word says (IM-9).

## 4. Gates (minimal tests, run only these classes)
Pictures drawn in memory with SkiaSharp (anti-aliased, 2 px strokes, one of circuitRF's shipped fonts):
- `WireGraphTests` — an L-network drawing gives the right segment count; a T without a dot connects; a + with a dot
  connects and without one does not under the default; a hop never connects; a 1.2° skewed copy reads the same graph.
- `TextRegionsTests` — `C1` and `10p` beside a capacitor are two words and are removed before tracing (no wire stub);
  a word touching a wire for 3 `w` is kept and reported.
- `SymbolRegionsTests` — a capacitor's two plates are one region with two attachments; a ground's bars are one region
  with one; a title-block frame is removed as a border.
- `SchematicImageRefusalTests` — a picture of words only refuses with the *no wires* sentence.
