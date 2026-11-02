# Brief AS-10 (optional, late) — Reference designators read from silkscreen strokes

**Series:** `brief-artsch-0-overview.md` (D10) · **Tag:** `R-as10-<m>`
**Depends on:** AS-4 (its `PartEvidence` interface); built only when the owner asks for it
**Area:** `src/Design/Layout/Recognition/Silkscreen/` (`StrokeGlyphs.cs`, `GlyphTemplates.cs`,
`SilkscreenText.cs`, `RefdesAssociation.cs`), `src/Design/resources/silkscreen-glyphs/` (templates as embedded
resources), `tests/Ui.Tests/Recognition/Silkscreen/`

---

## 1. Goal

On a board with no placed footprints and no placement or BOM file, the refdes printed beside each part is the only
statement of what it is. Read it, so a land-pattern match becomes a `C6` rather than a `C_A3`, and its kind comes
from the prefix. Values are never read from silkscreen (they are almost never printed); a BOM is still what gives
values.

## 2. Requirements

**R-as10-1 — No native dependency, no trained model shipped as a binary.** Gerber silkscreen text is stroked
vectors from a small number of CAD stroke fonts, not an image, so this is **vector glyph matching**, written
in-house: the strokes are the input, not a raster of them. Nothing is added that needs the owner's native-dependency
approval.

**R-as10-2 — Glyphs.** Silkscreen paths on the outer silkscreen layers (the technology's silkscreen purpose, else
the importer's layer identity) are grouped into **text lines** (strokes of similar height, baseline-aligned, at one
of the four rotations or mirrored on the bottom side) and split into **glyphs** by horizontal gaps relative to the
stroke height. Logos and outlines (strokes far from text height, closed shapes larger than a few glyph heights)
are excluded and counted.

**R-as10-3 — Matching.** Each glyph is normalised (height to 1, stroke centre lines resampled to a fixed count) and
compared against templates for `A–Z`, `0–9` and `- _ +` by a stroke-order-independent distance (a symmetric
Chamfer or similar distance between resampled point sets). Templates: built-in sets generated from one or more
public-domain stroke fonts (their licence recorded beside them), plus any set a user teaches (R-as10-5). Each glyph
gets its best match and margin; a line reads as a refdes only when it matches `^[A-Z]{1,3}[0-9]{1,4}$` with every
glyph above a margin threshold.

**R-as10-4 — Association.** Each refdes is assigned to the nearest unassigned part candidate (AS-4) by distance from
the text's centre to the part's body box, with a one-to-one optimal assignment (Hungarian) when several are close;
a refdes farther than three body diagonals from every candidate is listed and not used. A refdes found this way is
evidence source 4 (R-as4-1): it fills `Refdes` and `Kind` only where stronger evidence gave none, and
`Evidence` says `silk`.

**R-as10-5 — Teaching.** In the parts table, correcting a refdes that came from silkscreen offers **Learn these
glyphs**, which stores the corrected glyphs' strokes as templates in the user state directory (`UserStateDirectory`),
used on later runs. Nothing is written to the workspace.

**R-as10-6 — Report.** Text lines read, refdes associated, refdes not associated, low-margin glyphs, strokes
excluded as logo or outline.

## 3. Gates (minimal tests, run only these classes)
- `StrokeGlyphTests` — text generated as strokes from the built-in font at 0°, 90°, 180°, 270° and mirrored, with
  0.5 % coordinate noise, reads back exactly; a logo of closed shapes is excluded.
- `RefdesAssociationTests` — six parts with six labels beside them, two of them closer to the wrong part than to
  their own by a little, are assigned correctly by the optimal assignment.
- `SilkscreenFieldTests` — `FixtureFact` on `testdata/artwork-boards/`: on a board whose `expected.json` lists its
  refdes, at least the stated fraction is read and associated correctly.
