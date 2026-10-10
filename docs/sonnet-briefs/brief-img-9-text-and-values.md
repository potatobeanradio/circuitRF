# Brief IM-9 — Words: designators, values, net and port names

**Series:** `brief-img-0-overview.md` (D1, D10) · **Tag:** `R-im9-<m>`
**Depends on:** IM-7 (IM-8 for association)
**Area:** `src/Design/Schematic/Recognition/` (`ImageText.cs`, `IImageTextReader.cs`, `ValueGrammar.cs`,
`LabelAssociation.cs`), `src/Design/Layout/Recognition/Silkscreen/GlyphTemplates.cs` (wider glyph set),
`src/Design/resources/silkscreen-glyphs/` (Hershey lower case and symbols), `src/Design/Schematic/BomTablePaste.cs` (the value
reader, shared), design note §10

---

## 1. Goal

Read the few words a schematic's simulation needs — what each part is called and what its value is — and admit, word
by word, when a word could not be read.

## 2. Requirements

**R-im9-1 — The reader is AS-10's.** A word region's skeleton strokes (R-im7-3) are read by `StrokeGlyphs` against
`GlyphTemplates` — the same normalisation, the same modified-Hausdorff measure, the same in-class matching and
runner-up margin. Typeset sans-serif text skeletonises close to a single-stroke font; that is why this reuse works and
why serif and script fonts read worse (reported per word, never refused). `IImageTextReader` is the seam (D1).

**R-im9-2 — A wider glyph set.** `GlyphTemplates` gains Hershey's lower case and `. / = ° µ Ω` and `( ) ,` — from the
same public-domain set and under the licence file already beside it — as a separate class (*value glyphs*) that the
silkscreen reader never consults, so AS-10's measured behaviour on designators cannot change. A gate holds that.

**R-im9-3 — Grammar-constrained reading.** A word is accepted only as one of:
- a **designator** (`^[A-Z]{1,3}[0-9]{1,4}$`, AS-10's rules: a known prefix decides a digit/letter tie);
- a **value**: number, optional multiplier, optional unit, in every spelling a schematic uses — `10p`, `10pF`,
  `10 pF`, `2n2`, `2.2nH`, `4R7`, `1k`, `1kΩ`, `0.5 pF`, `DNP`/`NC` — parsed by the BOM reader's own
  `TryReadValue`/`IsNotFittedMarker` (`BomTablePaste`, already shared with AS-4), extended there if a spelling is
  missing, never parsed a second way;
- a **line parameter set**: `Z=50Ω`, `Z0=50`, `E=90°`, `θ=45°`, `L=3.2mm`, `W=0.3mm`, `F=2GHz`, comma or space
  separated;
- a **net or port name**: `[A-Za-z][A-Za-z0-9_+-]{0,15}`, checked against the port words (`RFIN`, `RF_IN`, `RFOUT`,
  `IN`, `OUT`, `P1`…, `PORT1`, `J1`…).
Among a word's readings within the margin, the best-scoring **grammatical** one wins; a word with no grammatical
reading is *unread* and keeps its picture box.

**R-im9-4 — Association.** Designators and values attach to symbols by the AS-10 assignment (`RefdesAssociation`:
distance to the body box, reach in body diagonals, clustered Hungarian), run twice — designators, then values — with
a value's kind checked against the symbol's (a value in henries does not go to a capacitor; the kind mismatch is
reported and the value left unattached). A designator that **contradicts** the symbol's kind (`L3` on a capacitor
drawing) is kept, and the kind's confidence drops; the parts table shows both. Line parameter sets attach to
transmission-line symbols only. Net and port names attach to the wire ends IM-7 gave them.

**R-im9-5 — Teaching.** **Learn These Glyphs** (AS-10 R-as10-5) accepts a corrected designator **or value** from a
picture-sourced row; the lesson goes to the same user-state store, as the value-glyph class for value characters.

**R-im9-6 — Unread is not wrong (D10).** A part with no readable value gets the global variable and `tune` entry of
AS D10 in IM-10; a part with no designator gets the generated one (`C?` → the next free `C<n>`) and the report says so.

**R-im9-7 — The report.** Words found, read as designators / values / line sets / names, unread (with boxes), kind
mismatches, contradictions, unattached words.

## 3. Not in this phase
Prose, titles, notes, a title block's fields — read as nothing and counted.

## 4. Gates (minimal tests, run only these classes)
Words drawn in memory in circuitRF's shipped sans-serif face at a 14 px cap height:
- `ImageTextTests` — `C12`, `10pF`, `2n2`, `4R7`, `1kΩ`, `RFIN`, `Z=50Ω, E=90°` read as their classes; `l0pF` with an
  ambiguous first glyph reads as `10pF` by the grammar; a scribble is unread with its box.
- `ValueGrammarTests` — every R-im9-3 value spelling parses to the same SI value through the shared reader.
- `LabelAssociationTests` — two capacitors with interleaved labels associate correctly; `3.3nH` beside a capacitor
  is a mismatch and is not attached.
- `StrokeGlyphTests` (existing) — one case: the silkscreen reader's template set is unchanged by the value glyphs.
