# Brief IMF-1 — The datasheet- and web-style corpus, the scorer, the baseline

**Series:** `brief-imf-0-overview.md` (L4, L6) · **Tag:** `R-imf1-<m>`
**Depends on:** —
**Area:** `tests/Ui.Tests/ImageRecognition/` (`Fixtures/DatasheetStylePictures.cs` and `Fixtures/WebStylePictures.cs`
new, `ImageFieldTests.cs`, `NetlistShape.cs`, `FieldScore.cs` new), `testdata/image-sources/README.md`, design note §17

---

## 1. Goal

Before any reader change, a gate that says how well a picture reads, as numbers, on pictures drawn the way datasheets
and the web draw them — committed ones our own code draws, and the git-ignored real ones (overview §1). Every later
phase reports its effect against this baseline.

## 2. Requirements

**R-imf1-1 — Datasheet-style pictures, drawn by our own code (primary).** `Fixtures/DatasheetStylePictures.cs`,
beside `ForeignPictures.cs` and on its terms (SkiaSharp, a seeded answer `.cnl` written with each picture,
`CRF_IMF1_FIXTURES=1` regenerates). Each is drawn **at 1× and 2×** (pen, type and spacing scaled together), because a
pasted datasheet is a screenshot at one or the other. Every fixture is a small circuit with an exact answer, and the
set covers every convention overview §1.1 showed:

| Fixture | Convention it reproduces |
|---|---|
| `ds-rf-amp` | hairline pen; 8-pin IC box with pin numbers in small boxes, pin names inside, a light gradient fill; flat hairline capacitor plates with a wide gap; a coil; filled-dot RF connectors with `RF In` / `RF Out`; a VCC supply arrow; two-line annotation phrases under parts; a hop |
| `ds-gain-block` | coloured (blue) text; a grey- or cyan-filled amplifier triangle with pin numbers `3`, `1`, `2,4`; coax-connector symbols (circle with a centre dot and a ground); open-circle pin markers on the leads; a current arrow beside a wire; word designators (`Cblock`, `Rbias`, `RFC`); a parenthetical note |
| `ds-regulator` | bold sans labels; curved-plate capacitors; filled diodes; a variable resistor; an IC box with pin names outside; a double-headed dimension arrow labelled `Vref = 1.25 V`; values in a designator-over-value block (`R1` / `240 Ω`, `Ci` / `0.1 µF`) |
| `ds-ldo` | hairline IC box with pin names inside, open-circle terminals `VIN` / `VOUT`, two capacitors beside the box, a value list beside the output |
| `ds-test-circuit` | a dashed box around a fixture, solid boxes around blocks, coax connectors, an amplifier triangle |

**R-imf1-2 — Web-style pictures (secondary).** `Fixtures/WebStylePictures.cs`, the same terms:

| Fixture | Convention it reproduces |
|---|---|
| `heavy-iec` | `w` = 8 px, IEC boxes, thick plates 19 `w` long, a BJT in an envelope circle, a sine source circle, a `+` supply circle, arrowheads into terminal dots, `R₁` subscripts |
| `textbook-zigzag` | zig-zag R, thick zig-zag, terminal-circle pairs, voltage arrows between them, serif italic labels |
| `ladder-dashed` | coils, thick plates, designators touching their symbols, a dashed continuation in both rails |
| `typeset` | thin pen, IEC boxes, a coil, `R₃ 10kΩ` designator-then-value runs in a serif italic face, a ± source circle |
| `loop-coil-names` | a looped coil, words set 4–6 text heights from the open wire ends they name |
| `low-res` | `textbook-zigzag` at 360 px wide: 1–2 px pen, 7 px caps, diamond junctions |
| `illustration` | shaded gradients and slanted cylinders, no circuit — must be refused |

Fonts: sans faces from `src/Render/Assets/Fonts`; the serif and serif-italic fixtures need an outline serif face the
application does not ship — add one as a **test asset only** under `Fixtures/fonts/` (DejaVu Serif and Serif Italic,
the licence already shipped with DejaVu Sans, its licence file beside it), per O4.

**R-imf1-3 — The scorer.** `FieldScore.Of(SchematicImageResult, Expected)` → a record: kinds right / drawn,
**topology** (the netlist's bipartite part–net graph isomorphic to the answer's, grounds and ports included —
extend `NetlistShape`, which already canonicalises a netlist, rather than write a second), ports, grounds, ICs and
amplifiers as one part with the right pin count, designators right / drawn, values right / drawn, largest region pin
count, kind classified (schematic / layout / none) **both forced and automatic**, refused (and the sentence), read
time. One line per picture, the same columns for every picture, printed to the test output so the TRX carries it.

**R-imf1-4 — `expected.json` grows.** Optional keys, each checked only when stated (the README's rule): `source`
(`datasheet` / `web`), `refused`, `grounds`, `terminals`, `ports`, `transistors`, `sources`, `amplifiers`, `ics`,
`transformers`, `designators` (list), `values` (designator → value text), `names` (net or port names), `style` (a
free-text note, never checked). README table updated. Rename the `expected.pending.json` files under
`testdata/image-sources/` to `expected.json` (local, git-ignored).

**R-imf1-5 — Field tests score, they do not demand.** `ImageFieldTests` keeps its theory, but a field picture fails
only on what IMF-10 promises (overview §4); everything else is scored and printed. Until IMF-10 the only promises are
`refused` matching and "no exception". The committed fixtures are separate classes, `ImageDatasheetStyleTests` and
`ImageWebStyleTests`, one `[Fact]` per fixture and resolution, each `Skip`ped with the phase that owns its cause until
that phase turns it on — the skip reasons are the series' to-do list.

**R-imf1-6 — The baseline.** Design note §17 opens with the scorer's table for every fixture and field picture on the
current reader, and the overview's F1–F12 with one sentence of overlay evidence each. Nothing in the reader changes.

## 3. Not in this phase
Any reader change.

## 4. Gates (minimal tests, run only these classes)
- `ImageDatasheetStyleTests`, `ImageWebStyleTests` — run; every fact skipped with its owning phase, except
  `illustration`, asserted only not to throw.
- `FieldScoreTests` — two netlists differing only in net names score topology-equal; a swapped R and C does not.
- `ImageFieldTests` — runs over the local pictures without failing, and prints the table.
