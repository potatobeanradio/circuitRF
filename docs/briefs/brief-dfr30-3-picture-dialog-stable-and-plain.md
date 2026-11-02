# Brief DFR30-3 — Create Layout from Image: a tool strip that stays put, and nothing on the picture without a reason

**Series:** designer feedback round 30 (2026-10-28). · **Tag:** `R-dfr30-3-<n>`
**Area:** `src/Ui/Recognition/ImageReadingPanel.axaml(.cs)`, `ImageSourceViewModel.Corrections.cs`,
`ImageSourceViewModel.CorrectionEdits.cs`, `ImageOverlayCanvas.cs`; `src/Design/Layout/Recognition/Image/ImageTrace.cs`
(`RoundHole`, the round-hole pass in `Build`), `src/Design/Imaging/ImageOverlay.cs`;
`docs/user/src/reference/create-from-image.md`.
Findings go in `src/Ui/RESOLVED.md` and `src/Design/Layout/Recognition/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Recognition/ImageSourceViewModelTests.cs`, `tests/Ui.Tests/Imaging/ImageTraceGeometryTests.cs`, and
a headless-view test of the strip's geometry (see gate 1). One test per claim. Every dialog change is RENDERED with
`tools/UiSnapshot --image` and looked at before it is reported. Builds on `brief-dfr30-1-picture-to-layout-round-2.md`.

---

## 0. Why

The owner tried DFR30-1 and its follow-ups (the Fill tool, tools before the scale) on a datasheet's black-and-white
drawing of a test board, pasted from the clipboard. Paraphrased:

1. **The tool strip moves when it is used.** Arming Via put *From the hole* in the strip and the buttons after it moved
   right. Arming another tool moved them again. The owner has said several times that a control must never move
   because something was clicked.
2. **A huge black circle appeared round the board**, and nothing said what it was. The dialog is meant to be simple, and
   a shape the user cannot account for makes it the opposite.
3. **"Fill" is the wrong word.** To a PCB designer, a fill is layout geometry: a copper pour or a zone fill. This tool
   paints the picture: it is a paint bucket.

What the code does today (read before writing this brief):

- **1 is how the strip is built.** `ImageReadingPanel.axaml`'s draw-tool `StackPanel` holds the tools and, IN LINE
  between them, each tool's options: the conductor picker (`DrawLayerShown`, after Fill), the Drill picker
  (`DrillPickerShown`, after Via) and Other…'s text box (`OtherDrillShown`). Each is `IsVisible`-bound, so arming a tool
  inserts its options and pushes everything after it, the legend included. Rendered at 1300 × 850 on the owner's
  picture (`UiSnapshot --image … --click <tool>`): with no tool armed, Via starts at x ≈ 92. With Fill armed, Via moves to
  ≈ 205. With Via armed, Copper moves from ≈ 127 to ≈ 252. The legend moves with all of them.
- **2 is the round-hole pass treating a board as a hole.** The drawing's board outline is a closed line, so the line
  layer has a region whose hole is the whole board. `RoundHole` accepts a hole up to 1.3:1 that fills at least 60 % of
  its box. That admits a rounded square, and so the near-square board. The 10 mm cap DFR30-1 added
  (`MaxHoleMetres`) is a LENGTH, so it depends on the scale. At the true scale (32.5 µm/px) the board is 17 mm and
  passes the cap. At a small scale it does not: at 8 µm/px the board's hole is about 4 mm across, wider than 1 mm, so it
  is kept as an OPENING. The overlay draws an opening as the circle of its area (`ImageOverlay.Build`, `hole:` items), so
  a board-sized circle sits over the board. A scale that small is exactly what a wrong technology produces under
  **Line impedance…**: 50 Ω on a thinner dielectric is narrower.

## 1. What to do

### R-dfr30-3-1 — Nothing in the strip moves when anything is clicked

- Every control in the strip has ONE position, whatever is armed, selected or chosen. The tools (Fill, Via, Copper,
  Outline, Remove ▾) sit in a fixed order at fixed places.
- A tool's options do not go in line between the tools. Recommended (D1 (a)): put them in **one options slot**, AFTER the
  last tool, sized to the widest of them (Drill picker + Other… box) and reserved even when empty. The slot shows the
  armed tool's options, and a selected hand via's Drill. (b) Put the options on a second row under the strip, a fixed
  height and always present. Take (b) only if (a) does not fit at the dialog's minimum width.
- The legend does not move either: it is placed after the reserved slot, or on its own row.
- A checked tool keeps its own width (a toggle's checked style must not change padding or text).
- The rule is general, not this strip's alone: **write it into `src/Ui/RESOLVED.md` as the standing rule**, and review
  every other `IsVisible`-in-a-row in `ImageReadingPanel.axaml` and `ImageSourcePane.axaml` against it (the Read as /
  Make rows, the scale pill). List what was checked.

### R-dfr30-3-2 — No board-sized "hole"

- A round hole must be ROUND. Tighten `RoundHole` with a shape test that does not depend on the scale: its outline's
  circularity (4π·area / perimeter²) at or above a threshold a drawn circle meets and a square does not. Measure the
  threshold on drawn circles and on rounded squares of the sizes vias and pads are drawn at, and state it.
- Independently of the scale, a hole bounding a large share of the picture (or of the copper it is in) is no drilled
  hole. Choose the rule, measure it, state it, and keep `MaxHoleMetres` only if it still catches something the new rule
  does not.
- An opening is drawn as its OWN outline, not as the circle of its area. A non-round shape can then never become a
  circle the user did not draw.

### R-dfr30-3-3 — Everything drawn over the picture can be accounted for

- Every overlay item says what it is. Hovering shows one line, as the copper picker's caption does: *Opening ⌀2.1 mm —
  a hole kept open*, *Via ⌀0.6 mm*, *Painted copper on Top Copper*, *Board outline*. The legend's classes match what is
  drawn: an opening is not filed under Drills without saying so.
- If a picture yields an item the user would not expect (an opening wider than any via, say), the report names it in
  one sentence, with **Remove ▾** or the hole picker as the way out.

### R-dfr30-3-5 — The paint bucket is called **Paint Fill**

- Every word the user sees says **Paint Fill**: the strip's button (bucket icon kept), its tooltip and canvas prompt,
  the Corrections row, Remove ▾'s item (*Paint fills*), the report line, the line-drawing note
  (`ImageSourceViewModel.LineArtFillHint`), and the user doc's table and tips.
- The flag follows the word, since nothing has shipped with `--fill` (it is uncommitted): `convert --paint-fill
  "x,y=<conductor>"`, with `ImageCorrection.Spelling`, the CLI usage line and `docs/design/cli.md` to match. Rename
  `ImageCorrectionKind.Fill` to `PaintFill`, so the code says what the user sees. Check the R-dfr30-3-1 strip at the
  longer label's width.

### R-dfr30-3-4 — Render it

Each change in this brief is rendered with `tools/UiSnapshot --image` before it is reported. That covers the strip with
each tool armed in turn, a hand via selected, and the owner's black-and-white picture at a small scale (no circle). The
renders are looked at, not just written. At most four pictures in the report.

## 2. Gates

1. **The strip's geometry is fixed.** A headless-view test (the `tools/UiSnapshot` host, or `Ui.Tests`' headless
   Avalonia) reads the bounds of every strip control and of the legend with no tool armed, with each tool armed in turn,
   with a hand via selected, and with Other… chosen. Every bound is identical in every state.
2. A board drawn as a closed outline, traced at a scale where the board is 2–8 mm across, gives no hole for the board
   and no opening item in the overlay.
3. Circles drawn 0.3, 0.6 and 1.0 mm across are still round holes (DFR30-1's dark-rimmed-hole gate still passes), and a
   rounded square the size of a pad is not.
4. Every overlay item on a layout picture has a hover line. One test lists the item kinds and their lines.
5. Renders, as R-dfr30-3-4.
6. No user-visible string, flag or doc line says "Fill" for the paint bucket. A grep over `src/Ui/Recognition`, `src/Cli`
   and the doc source finds only **Paint Fill** and `--paint-fill`. The DFR30-1 same-bytes CLI test passes with
   `--paint-fill`.

## 3. Not in this brief

- Anything about the schematic reader (greyed in every entry point since 2026-10-28, owner decision).
- Reading a line drawing's copper without the user painting it (DFR30-1 R-dfr30-1-7 (b)): still recognizer work, not
  started unasked.
