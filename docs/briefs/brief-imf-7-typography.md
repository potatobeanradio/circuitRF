# Brief IMF-7 — Words in the type datasheets and textbooks use

**Series:** `brief-imf-0-overview.md` (F10, O4, L3, L6) · **Tag:** `R-imf7-<m>`
**Depends on:** IMF-4 (glyphs lifted off symbols, subscripts joined to their base)
**Owner decision:** O4 — in-house (2026-10-11)
**Area:** `src/Design/Layout/Recognition/Silkscreen/` (`GlyphTemplates.cs`, `StrokeGlyphs.cs` — shared with AS-10,
so every change is held by AS-10's gates too), `src/Design/resources/silkscreen-glyphs/` and `resources/stroke-font/`,
`src/Design/Schematic/Recognition/` (`ImageText.cs`, `ValueGrammar.cs`, `LabelAssociation.cs`), design note §23

---

## 1. Goal

**Datasheets** set their labels in a sans face — often **bold** (ds-d, refused today as too heavy), often small at 1×
(7–9 px caps), often coloured — with `µ` and `Ω` in every value, **word designators** (`Cblock`, `Rbias`, `RFC`,
`CIN`, `COUT`, `Cbypass`), subscripted ones (`Ci`, `CADJ`, `R1` set as `R₁`), designator-over-value blocks, `DNP`
markings, and pin names (`VIN`, `GND`, `RFOUT`, `PWDN`) inside or beside an IC box. On the sixteen datasheet crops
the pin names partly misread (`VIN` → `V1N`, `GND` → `QNO`, `OUT` → `OU1`), `1µF` was unread, and bold labels were
refused. **Textbooks** set theirs in serif, mostly italic, with subscripts (`R₁`, `C_n`, `L_{n−1}`, `v_out`) and
single-letter designators (`R`, `C`); not one designator was read on the nine web pictures. The reader (§11) matches
skeletons against a sans stroke font, wants two glyphs to make a word and wants a designator to be letters-then-a-digit.
This phase reads both — datasheets first — in-house, as D1 requires.

## 2. Requirements

**R-imf7-1 — Bold sans, read (datasheets).** §11.5 refuses a word whose stroke weight exceeds 0.22 of its cap height
because Bold read *wrong*. Instead of refusing, **normalise the weight**: erode the word's ink by (weight − 0.15)·`h`/2
before skeletonising, so a bold stem thins to a regular one and a `=` reopens; then match as usual. The refusal stays
only for what still reads wrong after normalising — measure IBM Plex and DejaVu Bold at 9, 14 and 24 px caps (the
§11.5 survey's twelve words, plus `VIN`, `GND`, `RFOUT`, `1.0 µF`, `240 Ω`, `1N4002`) and record the table in §23.

**R-imf7-2 — Datasheet vocabulary.** A **known-word list** ranks above "other name" in the beam (§11.3): pin and net
names datasheets use (`VIN`, `VOUT`, `VCC`, `VDD`, `VEE`, `VEN`, `EN`, `GND`, `NC`, `IN`, `OUT`, `RFIN`, `RFOUT`,
`RF In`, `RF Out`, `RF-IN`, `RF-OUT`, `PWDN`, `PD`, `ADJ`, `FB`, `SW`, `BIAS`, `Vs`, `Vd`), the not-fitted markers
(`DNP`, `NC`, `NF`, `OPEN`), and the word designators (R-imf7-3's prefixes). A known word wins over a reading that
differs by one glyph of the confusion pairs §11.3 lists (I/1, O/0, S/5, B/8, G/Q/O) — so `V1N` reads `VIN` and `QNO`
reads `GND`. The list is a resource file, one word per line; adding a word is adding a line.

**R-imf7-3 — Word designators.** `ValueGrammar`'s designator gains a **word form**, only for a word beside a symbol
of the matching kind: a kind prefix followed by letters (`Cblock`, `Cbypass`, `Rbias`, `CIN`, `COUT`, `Ci`, `CADJ`,
`Ldrain`), and the common function abbreviations for an inductor (`RFC`, `BEAD`, `FB`). The designator written is the
word made legal and unique; the parts table keeps the word as read.

**R-imf7-4 — µ, Ω and the value block.** The value glyph class holds `µ` (both the micro sign and Greek mu, and the
`u` that stands for it) and `Ω` at the sizes and weights the sans faces draw them; IM-12's designator-over-value
**label block** (§13.2) is read on datasheet layouts (`R1` / `240 Ω`, `Ci` / `0.1 µF`, `D1` / `1N4002`) where the two
lines are left-aligned or centred and 0.3–1.2 `h` apart. A value list (`1.2V/1.8V/2.5V…`) is a comment.

**R-imf7-5 — Annotation phrases.** Two or more words on one or two lines that parse as no designator, value, known
word or single name (`DC Blocking Capacitor`, `RF Bypass Capacitors`, `(Optional)`, `(Required)`, `Test Board`) are
**comments**: kept as one comment, attached to the nearest part in the report and the parts table's Notes, never a
net name. Today they become garbage names (ds-a: `U3D8C1f0r`).

**R-imf7-6 — Serif skeletons.** A serif adds short spurs at stroke ends; skeletonised, each is a terminal branch the
sans templates do not have. Before matching, trim every terminal branch shorter than 0.15 `h` that leaves its stroke
at more than 45° (a serif foot), and close the gap a bracketed serif leaves at a stem's end. Measured, not assumed:
report the mean match distance of IMF-1's serif fixtures before and after.

**R-imf7-7 — Slant from the picture, not the word.** IM-12 found per-word lean too noisy on two-glyph words (§13.3).
Measure slant once per **picture** (or per text style: words grouped by `h` and stroke weight) from every near-vertical
stem in every word; if it is 8–20° and consistent (spread under 4°), shear that style's words upright before
matching. An upright picture is untouched.

**R-imf7-8 — Serif and italic stroke sets (O4).** Add a serif stroke face and its italic — public stroke-font drawings
of the classic serif and serif-italic sets, the same family as the existing `resources/stroke-font/` (one copy of each
face, a header stating its licence) — as **variants** of the existing glyphs, never a second designator class:
`GlyphTemplates` offers a glyph's best match over all faces. Each glyph's variants are drawn in one frame (cap height,
baseline) so a word may mix them. Include `Ω µ λ π ±` and the italic `v` `x` `y` that textbooks use for quantities.

**R-imf7-9 — Subscripts.** IMF-4 joins a subscript (0.5–0.8 of the base's height, its baseline dropped by 0.2–0.5 of
the base's) to the glyph before it. The reader reads the base and the subscript as separate runs and joins them:
`R` + `1` → `R1`, `R` + `L` → `RL`, `v` + `out` → `vout`, `L` + `n−1` → `Ln-1`. A subscript run is read with the
**value** glyph class (it may be lower case).

**R-imf7-10 — Designators the web writes.** `ValueGrammar`'s designator gains, **only** for a word beside a symbol of
the matching kind: a single prefix letter (`R`, `C`, `L`, `Q`, `D`, `V`), and a prefix with a letter or `n`-expression
subscript (`Ra`, `RL`, `Rs`, `Cn`, `Ln-1`). The single glyph is a word in this case only (IM-7 wants two, and keeps
wanting two everywhere else). The designator written into the schematic is the reading made legal: `Ln-1` → `Ln_1`;
two parts reading the same designator get `_2`, `_3`, reported.

**R-imf7-11 — Designator, then value, in one run.** `R₃10kΩ`, `C₁100pF`, `Q₁2N2222`, `V_s6V` (picture h): the
subscript's end is a boundary; what follows on the base line is read as a value (or a part number for a `Q`/`D`/`U`,
kept in the parts table's PartNumber column). A value set after a space (`R3 10kΩ`) reads the same way. Trailing
parentheticals (`(approx 50MHz)`) are a comment: read, reported, attached to nothing.

**R-imf7-12 — Distances in `h`.** Every association distance in `LabelAssociation` is in `h` (IMF-2), so a label 3
caps from a heavy-pen symbol is as near as one 3 caps from a light one.

## 3. Not in this phase
OS text recognition (D1 holds unless IMF-10 misses its target with these); handwriting; bold serif (refused as heavy,
§11.5, unchanged).

## 4. Gates (minimal tests, run only these classes)
- `ImageTextTests` — `InlineData` added for datasheet words: bold `VIN`, `240 Ω`, `1.0 µF` at 9 and 14 px caps;
  `V1N`-shaped and `QNO`-shaped glyph runs read as `VIN` and `GND`; `Cblock`, `RFC`, `CADJ` beside their symbols; a
  two-line `DC Blocking` / `Capacitor` comment. And for textbook words: an italic serif `R₁`, `C_n`, `v_out`, `R₃10kΩ`, `Q₁2N2222`, a lone `R` beside a
  resistor; each read to the stated text and class.
- `StrokeGlyphTests` and the AS-10 silkscreen gates stay green unchanged (the serif faces are variants; a silkscreen
  designator still reads as before).
- `ImageDatasheetStyleTests` — every designator and value on `ds-regulator` (bold) and `ds-ldo`, every pin name on
  `ds-rf-amp`, at 1× and 2×; `ds-rf-amp`'s notes are comments. Turn those on.
- `ImageWebStyleTests` — `typeset` reads every designator and at least 5 of its 7 values; `textbook-zigzag` reads `R`
  and `C`. Turn those on.
