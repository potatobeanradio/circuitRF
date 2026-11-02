# Brief IMF-10 — Acceptance on the field pictures, the docs, the record

**Series:** `brief-imf-0-overview.md` (§4, L4) · **Tag:** `R-imf10-<m>`
**Depends on:** all of IMF-1 … IMF-9
**Area:** `tests/Ui.Tests/ImageRecognition/ImageFieldTests.cs`, `docs/user/` (the Create from Image page's sources —
DocGen is the owner's, at the end of the series), design note §17–§25, `src/Design/RESOLVED.md`

---

## 1. Goal

Show, with the scorer's numbers, that pasted datasheet schematics — and web schematics after them — now read as good
schematic data; say plainly what still does not; and leave the record in the right places.

## 2. Requirements

**R-imf10-1 — The field table.** Run IMF-1's scorer over every local field picture (`testdata/image-sources/`:
sixteen datasheet crops, nine web pictures) and every committed fixture, and put the table in design note §17 beside
the baseline, one row per picture: kinds, topology, ports, IC/Amp pins, designators, values, largest region, chosen
reading, plausibility, time. Overview §4's targets are met or each miss is a sentence naming its cause.

**R-imf10-2 — The field promises become assertions.** `ImageFieldTests` asserts, per picture, only what overview §4
promises for it (datasheet pictures: kinds, topology, ports, the IC/Amp as one part; web pictures: kinds and
topology; g: refused) — so a later reader change that loses ground fails on the owner's machine, and a fresh clone
still skips.

**R-imf10-3 — A paste, end to end.** One test per resolution drives **Paste Image as Schematic** at the view-model
level with `ds-gain-block` on the clipboard into an open schematic, and asserts: nothing asked; the drawing selected
and connected to nothing that was there; an `Amp`, two Ports, the passives by kind; the one Messages line; the
picture under it. Then the drawing is netlisted and an S-parameter run completes (the Amp's default gain visible in
S21) — a pasted datasheet circuit simulates with no edit.

**R-imf10-4 — User docs.** The Create from Image page gains a short section, written for the user who pastes: what
pastes well (datasheet application circuits at screen resolution, textbook schematics), what to expect to fix (an
IC's Touchstone file, an amplifier's gain, a value the picture did not print), and what does not read (§7 of the
overview and design note §13.7, updated). No change history (owner rule). One new `FigureCatalog` row only if the page
needs a picture; DocGen is run by the owner at the end of the series.

**R-imf10-5 — The record.** `src/Design/RESOLVED.md` gets one entry per finding that would surprise the next reader
(the fill that made a schematic a layout, the dimension arrow that shorted two nets, the box region that grew past
its outline, bold type read wrong vs normalised) — never in `CLAUDE.md`. The overview's status line says done.

## 3. Not in this phase
New reader behaviour — a miss found here is reported, and fixed only if it is a defect in an IMF requirement.

## 4. Gates (minimal tests, run only these classes)
- `ImageFieldTests` (local pictures), `ImageDatasheetStyleTests`, `ImageWebStyleTests`, `PasteDatasheetEndToEndTests`
  (new).
- The existing image gates once: `tests/Ui.Tests/Imaging`, `tests/Ui.Tests/ImageRecognition`, the AS-10 silkscreen
  classes, `Firewall.Tests` — by class filter, not the whole of `Ui.Tests`.
