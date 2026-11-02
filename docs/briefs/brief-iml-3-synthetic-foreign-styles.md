# Brief IML-3 — Generated pictures in other editors' conventions

**Series:** `brief-iml-0-overview.md` (§1 item 2, L2, L4) · **Tag:** `R-iml3-<m>`
**Depends on:** IML-2 (the circuit generator and the label writer)
**Area:** `tools/SchematicSynth/` (the style-pack renderer), `tools/SchematicSynth/StylePacks/*.json` (new),
`tools/schematic-ml/fetch_fonts.py`, design note §5

---

## 1. Goal

Most pictures users paste were drawn by something other than circuitRF: other schematic editors, datasheets,
textbooks, web pages. The networks must have seen those conventions **in volume**. Draw IML-2's random circuits again
through a **style-pack renderer** — every visual choice a data file, so a developer who meets a new convention adds a
style pack, not code.

## 2. Requirements

**R-iml3-1 — What a style pack says.** A JSON file under `tools/SchematicSynth/StylePacks/`, named by what it looks like
(`blue-on-cream`, `dark-canvas-zigzag`, `datasheet-hairline`, `textbook-serif`, `iec-boxes` — L2: never a tool's name).
It states, each as a choice or a range:
- **glyphs per class**: one or more drawings in a small stroke language (lines, polylines, arcs, circles, filled
  polygons, in the class's canonical box, with the pin points) — a zig-zag or a box resistor, two-plate or
  curved-plate capacitor, bumps or loops or a filled block inductor, three-bar or triangle or chassis ground, a circle
  or box or arrow port, a box or two-line transmission line, an op-amp or gain triangle, a transistor with or without
  its circle;
- **pens**: wire and symbol widths (they often differ), line caps, colours for wires, symbols, text and canvas
  (incl. the pink-wire / blue-symbol / cream-paper kinds of combination screenshots show);
- **text**: font families (from `$CRF_ML_DATA/fonts`), sizes relative to the part, colours, and the **label stack** —
  which lines appear and in what order (`R` / `R1` / `R=5 kOhm`; `R1` / `28.495`; `10k`; `L=0.56 nH {o}`), where the
  stack sits relative to the part, subscripts, `Ω` vs `Ohm`, `µ` vs `u`;
- **junctions**: dot shape and size, or none; pin markers (small squares or circles at pin ends, as several editors
  draw); crossing hops;
- **canvas**: grid dots or lines and their pitch, paper colour, a page frame or title block.

**R-iml3-2 — Packs written from the real families.** The implementer writes **at least 12 packs** by looking at the
owner's starting set (in `$CRF_ML_DATA`) and the datasheet/web corpus — covering every visual convention seen there —
and **draws generic shapes**: a zig-zag is a zig-zag; nothing is traced or copied from a picture. The design note lists
each pack with the conventions it covers and the family ids that motivated it (ids only, L2). IMF-1's
`ForeignPictures`/`DatasheetStylePictures` conventions are ported into packs so nothing they drew is lost.

**R-iml3-3 — Distractors.** Things real pictures contain that are not circuit, drawn and labelled as `annotations` or
`controller`/`probe` parts so the model learns to leave them out:
- analysis, variable and simulation-directive boxes with text (`.tran 3`, a boxed `TRANSIENT` with parameters);
- titles, figure captions `(a)` `(b)`, notes and dimension arrows, current and voltage arrows beside wires;
- dashed frames around sub-circuits, DUT boxes;
- generic window chrome (menus, toolbars, side palettes, status lines with coordinates — drawn as plain shapes and
  words, no real application's artwork or icons), a mouse cursor, a selection rectangle;
- **two circuits in one picture**, side by side or stacked (the owner's own screenshots do this).

**R-iml3-4 — Wider degradation than IML-2.** Everything IML-2 does, plus: scans of print (slight rotation ≤ 2°, paper
texture, uneven light), small pictures (a whole circuit in 300 px), very large ones (7,000 px, tiled by the trainer),
anti-aliased and aliased pens, coloured text on coloured paper, and heavy JPEG.

**R-iml3-5 — Fonts.** `python -m crfml.fetch_fonts` downloads a pinned list of open-licensed families (sans, serif,
mono, condensed, a few UI faces' open look-alikes) with their licence files into `$CRF_ML_DATA/fonts`, verifying each
by SHA-256. Nothing is committed (L4). circuitRF's own shipped fonts are always available too.

**R-iml3-6 — The command.** `SchematicSynth foreign --packs all --count 60000 --seed 1 --out …` — the same
determinism, resumability, histogram and `--preview` as IML-2. Each label records `"style": "<pack>"` so IML-6 can
measure per pack and leave one out.

## 3. Gates

- `tests/Ui.Tests/Vision/StylePackTests` (fast, no data, no fetched fonts — a pack whose font is missing falls back to
  a shipped face and says so): every committed pack parses; every glyph's pin points lie on its strokes; every class in
  `classes.json` that is drawable has a glyph in at least 3 packs; three seeds per pack generate and validate as
  IML-2's do.
- The owner sees one `--preview` contact sheet across packs, next to a contact sheet of real pictures from the same
  families, before IML-6 trains on them.

## 4. Not in this phase

Training. Hand-drawn styles. Any artwork copied from a real application or picture.

## 5. Traps

- **A label stack's lines are separate texts** with separate roles (the class-name line `R` is `other`, `R1` the
  designator, `R=5 kOhm` the value) — the association step in IML-7 depends on the roles being honest.
- **Pin markers are not junctions.** A small square at a pin end connects nothing by itself (the IMF `PinSquares`
  lesson); label it as part of the symbol mask, not a junction.
