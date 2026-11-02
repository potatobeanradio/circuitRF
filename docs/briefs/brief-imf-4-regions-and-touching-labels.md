# Brief IMF-4 — Regions that stop at parts; labels lifted off the symbols they touch

**Series:** `brief-imf-0-overview.md` (F6, F7, L6) · **Tag:** `R-imf4-<m>`
**Depends on:** IMF-3
**Area:** `src/Design/Schematic/Recognition/` (`SymbolRegions.cs`, `TextRegions.cs`, `SchematicImageReading.cs`),
design note §20

---

## 1. Goal

One symbol region is one part. Today the face-to-face merge (§9.4) chains parts together across a whole drawing. On
datasheets the IC box or amplifier triangle is the seed: ds-h's IC region swallowed both capacitors beside it, ds-a's
took C3, C4 and L1 (20 pins), ds-d's covered the whole regulator, ds-g at 1× became one 270-pin region. On web
pictures c came back as a 15- and a 32-pin region, e at ×3 as one 31-pin region. And a designator touching its
symbol joins the region and spoils the match (f's `C₁`, `L₂`; h's `R₃10kΩ`).

## 2. Requirements

**R-imf4-1 — Merge only what cannot be two parts.** Two residue pieces merge only when all hold: they face each other
across a gap shorter than 1 `s` (not "shorter than the larger one's extent" — that is what let a merge cross a
whole sheet); no wire runs through the gap (as now); **no wire end attaches to either piece on the side facing the
gap**; and the merged region's box is no larger than 4 `s` on its shorter side. A merge that would give a region more
than four attachments is refused unless both pieces already have ≥ 3 (an IC's two halves).

**R-imf4-2 — A box is its own outline.** A closed box (IMF-3) is a region bounded by its outline: nothing outside it
merges into it, and everything inside it — pin names, pin-number boxes, the part name, a gradient — belongs to it as
detail (pin-number boxes are IMF-8's; glyphs inside go to the word candidates, tagged *inside a box*). Its
attachments are the wires that meet its outline. The same holds for a triangle (an amplifier) and its pin numbers
just outside it, which go to the word candidates tagged *at a pin*.

**R-imf4-3 — Envelope circles stay with their contents.** A circle 3–8 `s` across (fitted by IM-1's circle fit) with
ink inside it and two or three leads crossing it is **one** region — a transistor, a source — and nothing outside it
merges into it. A circle with nothing inside and one lead is a terminal (IMF-6). A circle with a glyph-shaped mark
inside (`~`, `+`, `−`) is a source region carrying that mark (IMF-5 reads it).

**R-imf4-4 — Glyphs come off symbols first.** Before regions are grown, a **glyph pass** over the residue: a
component (or a component cut where it meets a symbol stroke at a single short contact) whose size is within 0.7–1.4
`h` and whose shape is glyph-like (IMF-2's glyph test) and which lies in a row with at least one other glyph — or
alone, in a subscript position (below-right of a glyph, 0.5–0.8 its height) — is moved to the word candidates and
painted out of the residue. Subscripts and their base letter are one word. This replaces the "touching the line work
along more than 2 w" exclusion (§9.2) for anything glyph-shaped; a capacitor's plate is never glyph-shaped (aspect),
a coil's hump is not in a row of glyphs of height `h`.

**R-imf4-5 — A runaway region is re-cut, not accepted.** A region with more than eight attachments after merging that is not one closed box (an IC may have sixteen), or
whose box holds more than one IMF-3 box or plate pair, is split along the shortest wire-free cut between its
attachment clusters (the graph of its pieces, cut at the weakest merge). Reported as "a region was split into N".
Only if no cut exists does it stay — and then it is an S-parameter block named as such.

**R-imf4-6 — The report.** Boxes and triangles closed on their outlines, regions merged, merges refused (by which rule), envelope circles, glyphs lifted off symbols,
regions split.

## 3. Not in this phase
What a region is (IMF-5); reading the lifted glyphs (IMF-7).

## 4. Gates (minimal tests, run only these classes)
- `ImageDatasheetStyleTests` — `ds-ldo`'s IC box has exactly its 4 wired pins and both capacitors are their own
  regions; `ds-rf-amp`'s IC box has its 8 pins and C3/C4/L1 are separate; `ds-regulator` has one region per part, at
  1× and 2×. Turn those on.
- `ImageWebStyleTests` — `heavy-iec` has one region per part (5 R, 3 C, 1 BJT, 2 sources) and no region with more than
  three attachments; `ladder-dashed`'s designators are words, not part of their symbols; `typeset`'s `R₃10kΩ` is one
  word beside a two-pin region. Turn those facts on.
- `SymbolRegionsMergeTests` (new, in-memory): two resistors 1.5 `s` apart on one wire stay two regions; a transistor
  in a circle with three leads is one region.
- `ImageScreenshotPasteTests`, `ImageRoundTripTests` stay green.
