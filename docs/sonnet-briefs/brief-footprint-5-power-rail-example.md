# Brief 5 — the Power Rail example rebuilt on real footprints

**Series:** [SMT footprints](brief-footprint-0-overview.md) · **Tag:** `R-fp5-n` · **Phase:** P3
**Area:** `examples/Power Rail/`, `src/Ui/Diagnostics/Fixtures/DocRailFixtures.cs`,
`docs/user/src/reference/railrf.md`
**Depends on:** [4b](brief-footprint-4b-designators.md),
[railRF 23](brief-railrf-23-mount-and-unmount.md)
**Found by:** the designer's pass, 2026-09-20 — *"you don't have a component layer in the example
.clay, only pads without visible reference"*

---

## 0. What this brief delivers

The shipped Power Rail example has **no silkscreen, no soldermask, no outline and zero instances.**
Measured:

```
examples/Power Rail/tech/pcb-4layer-1p6mm.ctech   5 layers: TOP, GND, IN3, BOT, VIA
examples/Power Rail/Sensor board/layout/Board.clay  80 shapes, 0 instances
```

Every one of the thirteen capacitors is a pair of bare rectangles. A reader cannot tell which pads
are one part, cannot see a reference designator anywhere on the board, cannot select a part on the
artwork, and cannot depopulate one. The designer said all four of those, in different words, and
they are one defect.

This brief rebuilds the example on real footprint cells, and — per the owner's decision,
2026-09-20 — **re-spaces the parts and the runs so the connectivity is legible**, which means every
published number is re-measured.

---

## 1. `R-fp5-1` — the technology gains the three missing layers

`pcb-4layer-1p6mm.ctech` gains **Soldermask Top**, **Silk Top** and **Outline**, with
`Interchange.PcbLayerName` set to `F.Mask`, `F.SilkS` and `Edge.Cuts` so brief 1's role resolution
finds them.

**`R-fp5-1a`** The keys go on the END of the layer table, not interleaved. The existing keys
1, 2, 3, 4, 10 are written into `Board.clay`'s eighty shapes and into every number in the README;
renumbering them would rewrite the example for no gain.

**`R-fp5-1b`** The stackup is UNCHANGED. Copper thicknesses, dielectric heights and permittivity
stay exactly as they are — the added layers are non-conducting drawing layers and must not move a
single millivolt. The gate is that Q0's answer is invariant under this change alone, before any
geometry moves.

**`R-fp5-1c`** This technology is the one `SubstrateResolver` would have refused on before
(brief 1 R-fp1-3b), so it is also the test case for the refusal going away. Keep a copy of the
five-layer table as a test fixture for R-fp1-3a's diagnostic path — the case of a PCB technology
with copper and no silk is real and must stay covered.

---

## 2. `R-fp5-2` — the parts become instances of footprint cells

`Board.gen.py` is the source of the artwork and stays so: it writes the `.clay`, the `.ipc` and the
placement table together, because they have to agree and hand-editing three files is how that goes
wrong silently.

**`R-fp5-2a`** The thirteen capacitors become **instances**, not rectangles: six 0402s, two 0603s,
one 0805, one moulded bulk, three more 0402s. The `.clay` gains an `Instances` list, which today is
empty.

**`R-fp5-2b`** The bulk part is the one the case table was extended for. A 100 uF polymer is a
`7343-31` (D) or a `2220`, not an 0805 — pick one and say which in the README, because the
mounting-loop story turns on the land geometry.

**`R-fp5-2c`** Each instance carries a refdes drawn on Silk Top — **which is
[brief 4b](brief-footprint-4b-designators.md)'s doing, not this brief's.** Nothing here draws, stores
or positions a designator; it states the layer role and consumes what 4b provides. (When this
requirement was written there was no such mechanism at all — the layout model had no per-placement
designator and the renderer skipped every label inside an instance, which is why 4b exists and why it
runs first.) **That is the "component layer" the designer asked for**, and it is what makes the
parts table's Position column and the row-to-board selection legible rather than a highlight over
anonymous copper.

**`R-fp5-2d`** The generator's existing invariant holds unchanged and is still enforced: *a pad in
the netlist that is not under a land in the `.clay` is a part railRF cannot locate, and a land with
no anti-pad under it is a decoupling capacitor shorting the rail to its reference.* It now checks
the footprint cells' pads rather than loose rectangles, and it still refuses to write anything if
either is violated.

**`R-fp5-2e` — the C10 question answers itself.** The designer asked *"is C10 shorted in your
example?"* — a question nobody could answer from the picture, because there was no way to see which
pads were C10 or whether an anti-pad was under them. With instances, a refdes and R-fp5-2d's check
running over real pads, the question is either answered on the board or refused at generation time.
Verify explicitly and record the answer in the README.

---

## 3. `R-fp5-3` — re-spaced so the connectivity is legible

> **Decision, owner 2026-09-20:** re-position the parts and the runs so it is visually obvious what
> connects to what and on which layer. The designer could not tell, and heavy zoom was the only
> way to read anything.

**`R-fp5-3a`** The three findings the example exists for are PRESERVED, and they are the constraint
the new layout is designed against, not an outcome to be checked afterwards:

| | must survive |
|---|---|
| **Q0** | half the drop is one thin BOT run — the 0.20 mm run stays thin and stays long |
| **Q1** | two named anti-resonances, and a converter harmonic within ~2 % of the second |
| **Q2** | three mounting-loop tiers from ONE purchased part: a via in each land, a short fan-out, a long fan-out |

Q2's is the delicate one. The three tiers are 0.56 / 0.71 / 1.20 nH and they come entirely from
fan-out length; re-spacing the field must keep three clearly separated tiers or the example loses
the finding it was built for.

**`R-fp5-3b`** The two local IN3 pours stay LOCAL and neither bridges the board. A single pour
spanning both ends puts a second path in parallel with the BOT run and deletes Q0. The README
already says this out loud because the two arrangements look alike on the picture; re-spacing is
exactly when it would happen by accident.

**`R-fp5-3c`** The board outline is drawn on the new Outline layer, so the 30 x 20 mm extent is
visible rather than implied by where the copper stops.

---

## 4. `R-fp5-4` — a series element on the rail

The designer asked for a resistor in series, to stand in for a ferrite bead, and said the richer
version is two similar load branches — one through a ferrite, one through an ideal R.

**`R-fp5-4a`** This depends on [railRF brief 25](brief-railrf-25-series-element.md), which is the
model change. **If 25 has not landed, this requirement is dropped and said so in the README** —
shipping an example that claims a series part railRF cannot represent would be worse than not
shipping one.

**`R-fp5-4b`** With 25 landed: one series element between the regulator's output and the load
field, with its own footprint and its own refdes, and the README says what it does to Q1's top
band. The existing sentence — *"the series FET and the ferrite are the source's R and L, not rows
of their own"* — is then obsolete and must be rewritten, not left standing beside the new row.

---

## 5. `R-fp5-5` — every number is re-measured

The README publishes a large number of specific values: 48.368 mV Fast against 49.025 mV Accuracy,
a seven-row drop breakdown with shares, two anti-resonance frequencies and magnitudes, a 1.8 %
harmonic coincidence, four removal-ranking rows, three mounting-loop tiers, C11's
1201.5 pH decomposition, 7.975 pF of plane capacitance over 1.87 cm2, and 2,544 cavity cells of
which 454 are unreachable.

**`R-fp5-5a`** **Every one of them is re-measured and re-published.** Not adjusted, not
approximately preserved — re-run and re-written. A README number that no longer matches what the
example produces is worse than no number, because a reader checks it and concludes the tool is
wrong.

**`R-fp5-5b`** `src/Ui/Diagnostics/Fixtures/DocRailFixtures.cs` reads this workspace's `.crail`,
`.clay`, `.ctech` and `.crlib` directly, so every doc figure regenerates from the new board. Expect
churn there and classify it: real change, not id churn. (The id counter is HEX — `\d+`
mis-classifies it.)

**`R-fp5-5c`** The A/B section of the README says there is deliberately no second board shipped,
because the comparison is about the reader's own re-layout. That stays true and stays unshipped.

---

## 6. Gate

`tests/Ui.Tests/Examples/PowerRailExampleTests.cs` — extend.

1. **The technology has the four roles.** Copper, mask, silk and outline all resolve by role
   (brief 1 R-fp1-3), so a footprint generates on this technology.
2. **The stackup did not move.** Q0's drop with the three layers added and no geometry changed is
   bit-identical to HEAD's. Run this BEFORE R-fp5-3's re-spacing, as its own commit (R-fp5-1b).
3. **The board has instances.** `Board.clay` holds thirteen, each resolving to a cell with the right
   pad count.
4. **Every part has a refdes on silk**, and every refdes in the `.ipc` has one.
5. **The generator's own two refusals still fire.** Break a pad's position and break an anti-pad, and
   assert it writes nothing in each case (R-fp5-2d).
6. **The three findings survive.** Q0's dominant term is still the thin BOT run; Q1 still names two
   anti-resonances with a harmonic within 2 % of the second; Q2 still produces three separated
   mounting-loop tiers from one part number. **Assert the SHAPE of each finding, not the old
   numbers** — the numbers are expected to move and the findings are not.
7. **The README's numbers match the run.** A test that parses the published figures out of the
   README and compares them to a live run, so the two cannot drift again. This is the one that
   makes R-fp5-5a durable rather than a one-off act of care.
8. **Depopulate works on it.** With railRF 23 landed: unmount `C10`, re-run, and the worst margin
   moves by the amount Q2 predicts.

## 7. Scope

- **No second example board.** R-fp5-5c.
- **No change to the four questions or the document format.** This is the same example, drawn
  properly.
- **No vendor part numbers.** Every part stays synthetic, including the bulk cap. The report that
  raised this came with a third-party board's BOM; none of it enters the repo.
