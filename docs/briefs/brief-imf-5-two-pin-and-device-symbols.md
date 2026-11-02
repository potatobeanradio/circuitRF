# Brief IMF-5 — Two-pin parts, transformers, transistors and sources, as datasheets and textbooks draw them

**Series:** `brief-imf-0-overview.md` (F8, O1, O2, L6) · **Tag:** `R-imf5-<m>`
**Depends on:** IMF-4 · **Owner decisions:** O1, O2 (2026-10-11)
**Area:** `src/Design/resources/image-symbol-templates/` (new `.stroke` drawings), `src/Design/Schematic/Recognition/`
(`SymbolTemplates.cs`, `SymbolMatch.cs` structure checks, `RecognisedSymbol.cs`, `SchematicImageEmit.cs`), design note §21

---

## 1. Goal

With one region per part (IMF-4) and the plates and arrows sorted (IMF-3), each region is named. The datasheets drew
six two-pin conventions the template set does not match; the web pictures drew five more; and a transistor is emitted
as an S-parameter block with no file. IC boxes, amplifier triangles and connectors are IMF-8's.

## 2. Requirements

**R-imf5-1 — Datasheet capacitors.** Templates and the capacitor structure check (§10.3) accept: hairline flat plates
with a gap up to 1.5 `s` (ds-a, ds-h); one flat and one **curved** plate (ds-d), with the curved plate as pin 2 (the
polarised convention); a polarised pair with a `+` beside one plate (the `+` is a glyph, lifted by IMF-4, and sets
pin 1). The band of bare paper the check wants is measured in `s`, not `w`.

**R-imf5-2 — Diodes, filled.** A filled triangle with a bar at its apex between two collinear leads is a diode at any
pen weight; IMF-2 keeps a small filled triangle as ink, and the diode template is tried on filled regions (today only
hollow templates are, §10.1). Pin 1 (anode) at the triangle's base. A designator-over-part-number block (`D1` /
`1N4002`) puts the part number in the parts table's PartNumber column.

**R-imf5-3 — Resistors at any weight, variable and hatched.** The zig-zag check measures its swing in `s`, not `w`, so
a heavy zig-zag (b) and a hairline one (ds-d, ds-g) both alternate. A zig-zag or box **crossed by an arrow** (IMF-3
leaves it in the region) is a variable resistor: emitted as `R`, reported "variable — its value is one setting". A
**hatched** zig-zag (d) is closed by a morphological close of radius 1 `w` within the region before its skeleton is
taken.

**R-imf5-4 — Coils of every common drawing.** Hairline half-circle coils at 3–6 humps (every datasheet); the **looped
coil** (i) at 3–5 loops. The coil check accepts a looped coil: the centre line crosses itself once per loop, or (loops
merged by the skeleton) its nearest approach to the axis dips ≥ 1 `w` three or more times, either side. A box marked
`BEAD` or a filled box between two leads on a supply line is a **ferrite bead** (`Bead`).

**R-imf5-5 — Transformers.** Two coils facing each other across one or two parallel bars or a gap under 1 `s`, each
with two leads (ds-f, ds-g), are a transformer: emitted **as drawn** as two `L` and one `Mutual` coupling them (`K`
from a word beside it, else a tunable global at 0.99), a centre tap a third pin on its coil's `L` pair. A transformer
with a part number beside it keeps the number in PartNumber.

**R-imf5-6 — Transistors in an envelope, emitted as transistors (O1).** The FET and BJT templates gain envelope
variants (IMF-4 keeps the circle in the region; the match hollows it as a known stroke, not as error). Polarity from
the arrow; pins by role (the base or gate lead meets the bar from the side, the emitter or source carries the arrow).
Emitted as `BJT_NPN` / `BJT_PNP` or circuitRF's FET of that polarity, every model parameter at its default, reported
once as "default model — set its parameters". A three-lead region that is not a transistor match stays an SnP (§16).

**R-imf5-7 — Sources (O2).** Templates for a circle with `~` (AC), a circle with `+` / `−` (DC), a circle with `+` and
one lead (a supply), the long-short-bar battery, a circle with an arrow (current). Emitted: AC → **`P1Tone`** (O2 — circuitRF's
single-tone RF source behind its own reference impedance; its tone frequency and available power from a word beside
it when IMF-7 reads one — `105MHz`, `-10 dBm` — else tunable globals at the model's defaults; numbered with IMF-6's
ports as an input); DC, battery, supply → `Vdc`, value from the word beside it when IMF-7 reads one (`6V`,
`Vs 6V`), else a tunable global `<ref>_V` at 0 V, negative to ground where the picture grounds it; current → `Idc`.

**R-imf5-8 — The report and the overlay** name each new kind; the parts table's Kind column gains `Q` (with
polarity), `V`, `I`, `K` (a coupling), `Bead`; `PartsTableCsv` reads them back.

## 3. Not in this phase
Arrows (IMF-3); port pairing and the return rail (IMF-6); values (IMF-7); IC boxes, triangles, connectors, System
blocks (IMF-8).

## 4. Gates (minimal tests, run only these classes)
- `SymbolMatchTests` — `InlineData` added for: hairline flat capacitor (wide gap), curved-plate capacitor, filled
  diode at 0° and 90°, hairline zig-zag, variable resistor, heavy zig-zag at `w` = 8, hatched zig-zag, looped coil,
  transformer, NPN in a circle, PNP, AC / DC / supply circles, battery.
- `ImageDatasheetStyleTests` — `ds-regulator` reads 2 R (one variable), 3 C, 2 D; `ds-rf-amp` reads 5 C and 1 L;
  `ds-ldo` reads 2 C — at 1× and 2×. Turn those on.
- `ImageWebStyleTests` — `heavy-iec` reads its BJT as `BJT_NPN` with B/C/E on the right nets, its source as a `P1Tone`,
  its `+` circle as `Vdc`; `loop-coil-names` reads an L. Turn those on.
- `PartsTableCsvTests` — the new kinds round-trip.
