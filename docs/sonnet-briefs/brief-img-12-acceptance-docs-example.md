# Brief IM-12 — Acceptance, field pictures, user pages, example

**Series:** `brief-img-0-overview.md` (§1, §5, D16, D17, D18) · **Tag:** `R-im12-<m>`
**Depends on:** IM-6, IM-10, IM-11
**Area:** `tests/Ui.Tests/ImageRecognition/` (round trips), `tests/Ui.Tests/ImageRecognition/Fixtures/` (committed
PNGs and their generator), `testdata/image-sources/` (git-ignored field pictures + a committed README),
`examples/Schematic from Image/` (new), `docs/user/src`, design note §12–§13

---

## 1. Goal

Prove the series on pictures circuitRF did not draw as well as ones it did, write down where it stops working, and give
users an example that works the first time.

## 2. Requirements

**R-im12-1 — Round trip through circuitRF's own renderer.** For each of AS-9's round-trip boards and for two shipped
example schematics:
- `render` the `.clay` (copper layers only, `--detail full`) to PNG at 10, 20 and 40 µm/px → `convert` back at the known
  scale → **XOR area per layer** against the original ≤ its perimeter × ½ px; → `recognize` → the same topology and kinds
  as the artwork route, and **|ΔS| ≤ 0.02** against the artwork route's schematic at 40 µm/px (the gate scales with
  the pixel; the measured figures go in the design note).
- `render` the `.csch` to PNG at two zooms → `recognize` → the same netlist (kinds, connectivity, values) as the
  original's extraction, compared as graphs (an isomorphism, since names may differ).
This proves the pipeline end to end; it does **not** prove the reader handles other drawings, because circuitRF's
symbols are its own templates — hence R-im12-2.

**R-im12-2 — Pictures drawn another way.** A generator in `tests/` draws a small set of schematics and layouts in
**conventions circuitRF does not use** (IEC symbols, a different stroke width, value text in the oblique and condensed faces *shipped with
circuitRF*, a dark-background layout viewer palette, translucent overlapping layers), and the derived variants that
real pictures suffer: JPEG at quality 60, 1.5° rotation, 50 % downscale, salt noise. Generated once and **committed as
small PNGs** (D17: every platform reads the same bytes), each with its expected netlist or layout beside it. One test per
variant claim, not per combination.

**R-im12-3 — Pictures with nothing in them.** A photograph-like gradient, a blank page, a page of text, a picture of a
Smith chart (lines, but not a circuit): each refused with its sentence, and the dialog view model shows the dimmed
state with the override.

**R-im12-4 — Field pictures (never committed).** Pictures users actually bring — datasheet application circuits,
layout figures, screenshots — go under the git-ignored `testdata/image-sources/<generic-name>/` with an
`expected.json` (kinds, net count, part count, scale), read by `FixtureFact`s that skip with a reason on a fresh clone,
following `testdata/artwork-boards/`'s README pattern (which this phase copies, generically). No folder or file name may
say where a picture came from.

**R-im12-5 — The example.** `examples/Schematic from Image/`: a workspace holding a picture of a small matching
network (drawn by the generator, committed) and a picture of its layout, plus the two cells created from them, so a
user opens it and sees the underlay, the recognised parts and the tune variables. Registered as AS-9 registered its
example (`ExampleWorkspacesTests` must pass).

**R-im12-6 — User pages.** The *Create from Image* page finished: a short *What reads well* table (the overview §1 in
user words), *Getting a good picture* (crop to the circuit, PNG over JPEG, 2 px strokes or more, a scale bar or a known
part for a layout), troubleshooting by report line. A screenshot slot per figure for DocGen. Edit sources only;
**do not run DocGen**.

**R-im12-7 — Known limits.** Design note §13 (and the user page, in user words): hand-drawn pictures, photographs
(until IM-13), serif and script fonts, values printed far from their parts, dense layouts below ~4 px per trace width,
stackup never read, vector sources not read (the better source when one exists).

## 3. Not in this phase
Photographs (IM-13).

## 4. Gates (minimal tests, run only these classes)
- `ImageRoundTripTests` — R-im12-1 (the 40 µm/px |ΔS| case and the schematic isomorphism; the other resolutions only as
  XOR-area cases).
- `ImageForeignDrawingTests` — R-im12-2, one case per variant.
- `ImageNothingHereTests` — R-im12-3.
- `ImageFieldTests` — `FixtureFact`s over `testdata/image-sources/*/expected.json`.
- `ExampleWorkspacesTests` (existing) — passes with the new example.
