# Brief series — Pasted schematic pictures from datasheets and the web (IMF-1 … IMF-10)

**Status:** done 2026-10-11 — IMF-1 … IMF-10 (design note §17–§26; the field numbers against §4, each miss with its cause, in §26) · **Date:** 2026-10-11 · **Decisions:** §5 locked by this brief; §6 settled by the owner 2026-10-11
**Requirement tag:** `R-imf<n>-<m>` (phase n, requirement m).
**Design note:** `docs/design/image-to-circuit.md` — this series adds §17 onward (IMF-1 opens it with the baseline).
**Builds on:** the Create from Image series (`brief-img-0-overview.md`, D1–D18), above all the schematic reader IM-7…IM-10
(`src/Design/Schematic/Recognition/`) and the first field paste (design note §16, commit 71ba8b80):
`SchematicImageRecognition.PasteDrawing` → `SchematicImageDrawing` (as drawn, no invented labels or ports); the System
components (`src/Core/Devices/System/`: `Amp`, `Atten`, `Filter`, `Switch`, `Circulator`, `Coupler`, `Balun`,
`Duplexer`).

---

## 0. What the owner asked for (paraphrased)

Edit ▸ Paste Image as Schematic reads every picture with one fixed set of default settings, and schematics taken from
the web do not come back as good schematic data. Fix it. **Application circuits screenshotted from datasheets matter
most** — their style differs from textbook pictures, and that style must work. Paste stays a no-dialog command (the
owner's §16 decision): the fix is a reader that reads the pictures people actually paste, choosing its own settings per
picture, not a dialog that asks for them. circuitRF has System components (the amplifier among them), and a pasted
schematic should use them where the picture draws one.

---

## 1. The measured baseline (2026-10-11)

Every picture read with `recognize <png> -o x.cnl --overlay x.png` on the current build — the same
`SchematicImageRecognition.Read` Paste calls, every option at its default. All kept, git-ignored, under
`testdata/image-sources/` with an `expected.pending.json` (renamed to `expected.json` by IMF-1 once the scorer exists;
until then `ImageFieldTests` passes over them). Overlays: rerun the command.

### 1.1 Datasheets (primary)

Six datasheets from four RF/analog part makers; eight application, test or evaluation-board circuits, each cropped as
a screenshot at **1× (110 dpi)** and **Retina 2× (220 dpi)** — sixteen pictures, `picture-ds-a1` … `picture-ds-h2`.

| Picture | Drawing | What came back (1× / 2×) |
|---|---|---|
| ds-a | RF amplifier: hairline pen, 8-pin IC box (pin-number boxes, pin names inside, gradient fill), filled-dot RF connectors, VCC arrow, "DC Blocking Capacitor" notes, a hop | the IC box merged with C3/C4/L1 into a **12- / 20-pin block**; C3/C4/C5 plates read as wires; connector dots `?`; notes read as garbage names; no port |
| ds-b | gain block: blue text, grey triangle with pin numbers, coax-connector symbols, current arrows | triangle + C1 one 4-pin block; coax symbols `?`; at 2× L1 emitted **as a capacitor** |
| ds-c | gain block: cyan-filled triangle, open-circle pin markers on the leads, word designators (`Cblock`, `Rbias`, `RFC`) | 1×: a 12-pin and a 4-pin block; **2×: read as a layout picture** (the fill) and refused for want of a scale |
| ds-d | regulator: bold sans labels, curved-plate capacitors, filled diodes, variable resistor, double-headed `V_ref` arrow | **one 17–20-pin region over the whole circuit**; the `V_ref` dimension arrow drawn as a **wire shorting two nets**; bold labels refused as heavy |
| ds-e | test circuit: dashed fixture boxes, solid block boxes, coax connectors, triangle | a 9-pin block; dashed box sides read as wire |
| ds-f | differential amplifier: 1 px pen, blue text, op-amp triangle, transformer, 7 px labels with Ω µ | 1×: a **65-** and a **46-pin** block; **2×: read as a layout** |
| ds-g | evaluation board: dense, green not-populated parts, two transformers, coax connectors, two ICs | 1×: **one 270-pin region**, 152 nets; **2×: read as a layout** |
| ds-h | regulator: hairline pen, IC box with pin names inside, open-circle terminals, a switch drawn as ON/OFF lines | the closest: VIN and VOUT ports found; but the IC region swallowed both capacitors (8 pins for 4), `µ` unread, `VIN` → `V1N`, `GND` → `QNO` |

**None of the sixteen is usable.** No picture got its passives right by kind; RF ports were found only on ds-h. The
coloured pictures are classified as layouts at 2× — Paste forces the schematic kind and escapes that, but `recognize`,
MCP and the dialog's automatic kind do not.

### 1.2 Web pictures (secondary)

Nine schematic pictures from a public image library, `picture-a` … `picture-i`.

| Picture | Drawing | What came back |
|---|---|---|
| a | attenuator pads: zig-zag R, triangle grounds, terminal circles, serif italic `R_a` | **refused** as "curved or slanted lines" — the zig-zags |
| b | RC low-pass: heavy pen, thick plates, terminal pairs, voltage arrows | R `?`; plates read as wire; 3 ports (one per circle) |
| c | CE amplifier: heavy pen, IEC boxes, BJT in a circle, AC source, `+` supply | **two regions** — 15 and 32 pins — swallowing every part |
| d | RC divider: hatched zig-zag, serif italic | two 3-pin blocks, 4 `?` |
| e | ladder at 359 px: diamond junctions, dashed continuation | 10 blocks; at ×3 one 31-pin region |
| f | Cauer ladder: coils, thick plates, labels touching symbols, dashes | dashes as parts, every C demoted, a merged block |
| g | a shaded 3D illustration — not a schematic | read as a layout and refused for scale (Paste forces the schematic kind and would read it) |
| h | Colpitts, typeset-document style, `R₃10kΩ` | 3 R, 3 C right; coil, BJT, source as blocks; no designator or value |
| i | bias tee: looped coil, `RF+DC` / `DC` / `RF` | coil `?`; the names read right and associated with nothing |

---

## 2. The failures, by cause

Each is named once and owned by one phase. **D** = seen on datasheets, **W** = on web pictures.

| # | Cause | Seen on | Phase |
|---|---|---|---|
| F1 | **One fixed unit.** Every tolerance is in stroke widths `w`; a datasheet hairline at 1× (`w` = 1.8–2.4 px, 9 px caps) and a heavy textbook pen (`w` = 8 px) both fall outside it. | D W | IMF-2 |
| F2 | **Colour and fills.** Coloured text, a cyan or grey filled triangle, a gradient IC fill: a coloured picture classified as a layout, fills taken for symbols' ink or dropped. | D | IMF-2 |
| F3 | **Plates read as wire** — hairline plates with a wide gap, a curved plate, heavy plates 19 `w` long; IEC box sides. | D W | IMF-3 |
| F4 | **Annotation strokes read as wire** — a double-headed dimension arrow (shorts two nets), current arrows beside or on wires, dashed fixture boxes, dashed continuation lines. | D W | IMF-3 |
| F5 | **Zig-zags trip the curved-lines refusal.** | W | IMF-3 |
| F6 | **Region merge runs away** — the IC box or triangle chains to every neighbour. | D W | IMF-4 |
| F7 | **Labels touching symbols** join their region. | D W | IMF-4 |
| F8 | **Two-pin and transistor vocabulary** — curved-plate and hairline capacitors, filled diodes, variable resistor, looped coil, heavy/hatched zig-zag, a transistor in a circle, source circles. | D W | IMF-5 |
| F9 | **Ports, terminals, return rails, names at a distance.** | D W | IMF-6 |
| F10 | **Type** — bold sans (datasheets), serif and italic (textbooks), subscripts, word designators (`Cblock`, `RFC`), designator-then-value runs. | D W | IMF-7 |
| F11 | **Datasheet conventions** — an IC box with pin numbers and pin names, an amplifier triangle with pin numbers and ground pins, coax-connector and filled-dot RF connectors, open-circle pin markers on a lead, a supply arrow, annotation phrases and parentheticals, block-diagram symbols. | D | IMF-8 |
| F12 | **One reading, taken whatever it is.** No plausibility check, no "this is not a schematic". | D W | IMF-9 |

---

## 3. The phases

| Phase | Brief | Delivers | Depends on |
|---|---|---|---|
| IMF-1 | `brief-imf-1-corpus-and-scorer.md` | Committed datasheet-style and web-style fixtures drawn by our own code; the field scorer; the baseline | — |
| IMF-2 | `brief-imf-2-picture-scale-profile.md` | The picture's own scales (pen, text, symbol, grid), tolerances that follow them, resampling, colour and fills | IMF-1 |
| IMF-3 | `brief-imf-3-strokes-heavy-pens-dashes.md` | Plates and boxes at any pen weight; annotation arrows, dashed boxes and dashed lines; zig-zags never refuse | IMF-2 |
| IMF-4 | `brief-imf-4-regions-and-touching-labels.md` | Regions that stop at parts; labels lifted off the symbols they touch | IMF-3 |
| IMF-5 | `brief-imf-5-two-pin-and-device-symbols.md` | Two-pin and transistor symbols in datasheet and textbook drawings; sources | IMF-4 |
| IMF-6 | `brief-imf-6-ports-terminals-names.md` | Terminal pairs → ports, return rail → ground, far names, unknown parts emitted honestly | IMF-5 |
| IMF-7 | `brief-imf-7-typography.md` | Bold sans, serif and italic, subscripts, word designators, designator-then-value runs | IMF-4 |
| IMF-8 | `brief-imf-8-datasheet-conventions.md` | IC boxes and amplifier triangles with pin numbers → SnP / System `Amp`; RF connectors; pin markers; supply arrows; annotation text; System block symbols | IMF-5, IMF-6, IMF-7 |
| IMF-9 | `brief-imf-9-auto-settings-and-paste.md` | Several readings, one chosen by a plausibility score; "not a schematic"; the time budget; Esc cancels; Paste's one-line outcome | IMF-2…IMF-8 |
| IMF-10 | `brief-imf-10-acceptance-and-docs.md` | The field numbers against §4, user pages, design note, RESOLVED.md | all |

IMF-7 runs in parallel with IMF-5/6 (it touches `ImageText`/`LabelAssociation` only). **Where a phase has to choose
between a datasheet convention and a textbook one, the datasheet wins** (§0).

---

## 4. Acceptance — what "good" means

Measured by IMF-1's scorer, reported per picture, held by IMF-10:

- **Committed fixtures (IMF-1):** every one reads its circuit exactly — kinds, nets, ports, grounds — and every
  designator in an upright sans or serif face.
- **Datasheet pictures (primary):** every coloured one classified as a schematic. At **both** 1× and 2×, **at least
  5 of the 7** circuits a–f and h (g is the stress picture: scored, not held) read with every R, L, C, D
  counted right by kind, the right topology (the netlist graph isomorphic to `expected.json`'s), the RF ports found,
  and the amplifier or IC as one part with the right pin count. At least **three quarters** of drawn designators read
  right; every value printed beside its part read right on ds-d.
- **Web pictures (secondary):** g refused in one sentence saying it is not a schematic; at least 6 of the 8
  schematics with kinds and topology right; half the designators.
- **No region with more than the drawn pin count** on any picture.
- **Paste time (O8):** a simple diagram under 3 s and a complex one under 10 s on the owner's Debug build, all
  readings included (IMF-9); Esc cancels at any point and nothing is pasted.
- **No regression:** every existing image gate (`tests/Ui.Tests/Imaging`, `tests/Ui.Tests/ImageRecognition`,
  `ImageScreenshotPasteTests`, AS-10's silkscreen gates) stays green.

---

## 5. Decisions locked by this brief

- **L1 — Paste asks nothing.** The owner's §16 decision stands. Every setting Paste needs it chooses per picture
  (IMF-9); Design ▸ Create ▸ Schematic from Image remains where a user overrides one.
- **L2 — As drawn, still.** D12 and §16: the picture's wires are drawn as the picture draws them; nothing is invented.
  A reading this series cannot make is reported and left out, never guessed into the drawing.
- **L3 — No ML, no native dependency** (the IM series' D1). Every fix is geometry, templates and rules.
- **L4 — Other people's pictures are never committed.** The committed gate is pictures our own code draws in the
  datasheet and web conventions (IMF-1); the downloaded ones stay git-ignored under `testdata/image-sources/` with
  generic names, and no brief, test or comment names the maker or the part they came from.
- **L5 — One reader.** Paste, the dialog, `recognize` and MCP all call `SchematicImageRecognition.Read`; the
  per-picture settings are chosen inside it (an `Auto` option), so every entry point improves together.
- **L6 — Datasheets first.** Where conventions conflict, the datasheet reading wins (§3).

## 6. Owner decisions (2026-10-11)

All eight settled by the owner on 2026-10-11; each phase applies its own.

- **O1 — A transistor (IMF-5).** Placed as **circuitRF's own BJT or FET**, pins from the drawing (emitter/source
  arrow, base/gate lead), default model parameters, reported. D9's S-parameter block is no longer used for one.
- **O2 — Sources (IMF-5).** An AC/sine source circle is a **`P1Tone`** (circuitRF's single-tone RF source with its
  own reference impedance); a DC source circle, battery or `+` supply circle is a **`Vdc`** with the value read
  beside it (else a tunable global), negative to ground.
- **O3 — A two-pin part of no known kind (IMF-6).** A **0 Ω part** designated `?<n>` — the circuit stays as drawn —
  selected after paste and counted in the outcome line. The artwork recognition keeps its own rule.
- **O4 — Type (IMF-7).** **In-house.** Stroke-font serif and serif-italic sets, serif-spur trimming, a picture-level
  unslant and weight normalisation for bold sans; no OS text recognition. Fixtures that draw serif type use an
  outline serif face as a test asset only, never shipped.
- **O5 — An amplifier triangle (IMF-8).** The System **`Amp`**: `in+` the base lead, `out+` the apex lead, ground
  leads into its reference, a supply lead drawn as a wire ending at its body and reported; `Gain` a tunable global.
- **O6 — An IC box (IMF-8).** An **SnP** whose ports are the non-ground pins in the picture's own pin-number order,
  named by their pin names, the ground pins its reference.
- **O7 — Block-diagram symbols (IMF-8).** **Yes**: filter, attenuator, circulator, switch, coupler and balun symbols
  become their System components with default parameters; a mixer and anything else an SnP.
- **O8 — Time, and cancelling (IMF-9).** A **simple** diagram pastes in **under 3 s**; a **complex** one may take up
  to **10 s** (IMF-9 defines the two by measured content). **Esc cancels a paste in progress** — for a very large
  picture — and a cancelled paste leaves the document untouched.

## 7. Not in this series

Hand-drawn schematics; photographs of paper; multi-page sheets; vector sources (a datasheet PDF's own vector drawing is
the better source and is D18's, not this series'); an editable correction loop inside Paste (that is the dialog's job).
