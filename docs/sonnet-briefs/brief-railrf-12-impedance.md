# Brief 12 — Z(f), the mask, the aggressors, and which capacitors are earning their place

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail12-n` · **Phase:** P1
**Area:** `src/Engine/Pdn/`, `src/Design/RailRf/` · **Depends on:** 5, 11 · **Blocks:** 13, 16
**Design note:** [`railrf.md`](../design/railrf.md) §2.4 ("Over frequency"), §2.1 Q1/Q2, §4.4, §7, Q-4, Q-9

---

## 0. What this brief delivers

Q1 and Q2 of §2.1 — *does this rail meet its target*, and *which capacitors are actually doing anything.*

| Type | File | What it is |
|---|---|---|
| `PdnSweep` | `src/Design/RailRf/PdnSweep.cs` | The frequency run: one MNA solve per point, a `DataSet` out. |
| `PdnMask` | `src/Engine/Pdn/PdnMask.cs` | The target as a mask, and the violations with their margins in dB. |
| `PdnCoincidence` | `src/Engine/Pdn/PdnCoincidence.cs` | Every anti-resonance within a stated fraction of an aggressor line. |
| `PdnAntiResonance` | `src/Engine/Pdn/PdnAntiResonance.cs` | Peak finding, and **naming the two contributors**. |
| `PdnRemovalRanking` | `src/Engine/Pdn/PdnRemovalRanking.cs` | One re-solve per part. |

**`src/Engine/Pdn/` may not name a `Technology`, a `LayoutView` or a `RailDocument`** (overview §1a — `Engine`
does not reference `Design`). Everything there takes numeric arrays and a `DataSet` and returns a `DataSet`.

**At P1 the artwork is optional** (§6): mounting inductances may be typed and the copper may be absent. This
is the lumped PDN, and on these boards *"it covers the whole excitation set."*

---

## 1. `R-rail12-1` — the sweep is `SParameterEngine`'s machinery, and the result is a `DataSet`

§4.4: assemble one sparse complex MNA system per frequency and solve for the port impedances via CSparse's
LU — the same numerical layer every other circuitRF analysis uses. The result is a `DataSet` carrying a **Z
cube over `[freq, port, port]`**, the DC node voltages and branch currents, and — when asked — the full node
voltage field for the maps.

**Nothing invents a result type** (the repo invariant). Because it is a `DataSet`, a PDN curve overlays a
measurement in an ordinary Data Display with no special support — which is §5's claim for this whole
architecture and is worth one end-to-end test.

### `R-rail12-2` — an integer on a port axis is a PORT NUMBER

The `plot` verb's own recorded trap, and it applies to every cube this brief produces: **an integer on an
`i`/`j` axis is a 1-based port number, not an index.** Converting it draws S12 for `i=2,j=1` in silence,
*which is invisible on a reciprocal part* — and a PDN Z matrix is reciprocal.

---

## 2. `R-rail12-3` — the plot, with the aggressors on the same axis

§2.4:

> |Z(f)| at each observation port, log-log, with the target mask drawn as a **shaded ceiling** and violations
> marked with their frequency and margin in dB. **The declared aggressors are drawn on the same axis** — a
> vertical marker per fundamental, lighter ones for the harmonics — **because a 9 dB peak nothing excites is
> not a problem and a 3 dB peak sitting on the converter's fifth harmonic is.**

The mask is a trace (the Data Display already renders a mask as one), the aggressors are markers, and the
whole thing is a `PlotControl` in rectangular mode. **No bespoke chart** (§11.1).

Brief 4 `R-rail4-5`: after an Accuracy run the **fast curve stays on the plot beside the accurate one**, so
the error is measured on this design rather than promised in a document.

---

## 3. `R-rail12-4` — the coincidence check is the sentence the tool exists to produce

§2.4:

> A short list: every anti-resonance within a stated fraction of an aggressor line, worst first. **This is the
> sentence the tool exists to produce** — *your bulk-to-ceramic anti-resonance at 7.1 MHz is the converter's
> own fundamental at its top setting.*

So the output is not a boolean and not a count. It is a **row per coincidence**, naming the anti-resonance,
naming the aggressor and its harmonic number, and giving the separation. Worst first.

"A stated fraction" is a setting with a default, and the default is stated in the code with its reasoning.
Q-9's own arithmetic is the sanity check for the whole feature: a 470 µF bulk with 5 nH of mounting
self-resonates near **104 kHz**, a 1 µF ceramic near **5.3 MHz**, a 100 nF 0402 with 1 nH near **16 MHz**, and
**the bulk-against-ceramic anti-resonance sits near 7.1 MHz — the converter's own fundamental at the top of
its range.** Those four numbers are test rows.

---

## 4. `R-rail12-5` — the anti-resonance table NAMES ITS TWO CONTRIBUTORS

§2.4:

> Frequency, peak |Z|, margin against the mask, and **the two contributors, named**: *"L(mount, C3–C9 bank)
> against C(bulk bank)"*. **That label is the actionable output; the peak on its own is not.**

This is the hardest thing in the brief and it is the reason the table is worth building. An anti-resonance is
a parallel resonance between the inductive branch of one group of parts and the capacitive branch of another;
naming them means **attributing the peak** rather than merely locating it.

The tractable method, and the one to build: at the peak frequency, rank every branch by its contribution to
the port admittance, group the inductive contributors and the capacitive contributors, and name the top group
on each side by the part range it spans. A branch that is neither is not named.

**Do not attribute by proximity, by value or by part type.** §2.5 states the same rule for the A/B matcher and
it holds here: railRF does not pair things by guessing.

---

## 5. `R-rail12-6` — the removal ranking is computed by actually REMOVING, not by a sensitivity

§2.4, and the reason is explicit:

> The ranking is computed by **actually removing each part and re-solving**, not by a sensitivity
> approximation — the solve is cheap and **the approximation is not trustworthy near an anti-resonance.**

One re-solve per part. One row per part: **how much the worst violation grows if this part is removed, in
dB.** Parts with a zero in that column are candidates for deletion.

Q-4 closed it as *priority one* and it ships in P1 *"because it is one re-solve per part and needs no cost
data at all."* The minimum-cost search over a priced library is explicitly a later feature.

§2.6 step 7 is the worked example and it is the payoff for the whole tool: *five parts show 0.0 dB — shadowed
by lower-inductance neighbours. Delete them: still passes at 1.8 dB. **Five parts and five placements saved,
on a board that does not exist yet.***

---

## 6. `R-rail12-7` — the mask is per observation port, and the margin carries *indicative*

- §2.2: **masks are per observation port**, so the mask lives on the load row rather than on the rail.
- Brief 11 `R-rail11-4`: a margin computed from an **indicative** peak is marked as such — **on the plot, in
  this table, and in every export.** §9: *"a mask margin in dB computed from an indicative peak looks exactly
  as authoritative as a real one."*

A margin type that cannot carry the flag is the wrong type. Put it on the row.

---

## 7. `R-rail12-8` — everything exports, and the provenance goes with it

§2.4:

> Z(f) as Touchstone or `.npy`, the tables as CSV, the maps as vector graphics through the same renderers the
> window draws with. The impedance curves land in **an ordinary circuitRF Data Display**, so they overlay
> anything else — **including a measurement**. **Every export carries which model produced it** and which
> reference option was used.

---

## 8. Tests — `tests/Ui.Tests/RailRf/PdnImpedanceTests.cs` (+ `tests/Engine.Tests` for the pure arithmetic)

### The acceptance anchor — §7, and it is external data in the sense the PRD requires

> **A published measured PDN.** At least one board from the open SI literature with measured Z(f), reproduced
> within the tolerance that literature states.

Commit the digitised reference as `testdata/`, cite it in the test's own comment, and state the tolerance the
source states rather than one we chose. This is the one gate in the series that is neither our arithmetic nor
a closed form.

### The rest

- **`R-rail12-4`**: Q-9's four numbers — 104 kHz, 5.3 MHz, 16 MHz, and the 7.1 MHz anti-resonance — from the
  parts the note names, to a stated tolerance. One test, four rows.
- **`R-rail12-5`**: a two-bank board built so the answer is known by construction — a bulk bank and a ceramic
  bank with a designed anti-resonance between them — names **those two banks** and not the ports, the source
  or a third bank that is far off resonance. The negative: add a third bank at the same frequency and assert
  the naming changes.
- **`R-rail12-6`**: a part in parallel with a lower-ESL neighbour 2 mm away ranks **0.0 dB**; removing it
  changes the worst margin by less than the display resolution. A part that is the only thing holding a
  decade ranks large. And: the ranking by re-solve **differs** from a first-order sensitivity near the
  anti-resonance — which is the claim §2.4 makes and the reason for the design.
- **`R-rail12-1`**: the result is a `DataSet` with a Z cube over `[freq, port, port]`, and it loads into a
  Data Display with no special support.
- **`R-rail12-2`**: `i=2, j=1` on the trace card resolves to **port 2 into port 1**, not to index 2.
- **`R-rail12-7`**: a margin derived from a class-default ESR is flagged; one derived from a file-modelled
  part is not.
- **`R-rail12-3`**: after an Accuracy run both curves are present on the plot.

---

## 9. Scope

- **No distributed copper.** Brief 13. At P1 the artwork is optional and the mounting inductances are typed.
- **No cavity, no shunt branch, no modes, no maps.** Briefs 14 and 15.
- **No A/B.** Brief 16.
- **No transient.** §2.7: converting Z(f) into a voltage waveform for a given current profile is a defensible
  v2 feature and is deliberately not v1.
- **No cost-optimised selection.** Q-4.

**On completion:** record findings in `src/Design/RESOLVED.md` and `src/Engine/RESOLVED.md` as appropriate.
Never in a CLAUDE.md.
