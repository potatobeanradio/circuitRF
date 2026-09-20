# Power Rail Integrity

A synthetic four-layer board with one supply rail on it, set up for all four of the questions
railRF answers. Nothing here was measured on a real product: every shape was drawn to make a
particular answer legible, and the numbers below are what this workspace produces today.

Open **Sensor board ▸ Sensor board.crail**. The window opens on the Fast model with the board
already loaded; press **Run**.

## The board

30 × 20 mm, four copper layers, reference plane on **L2**.

| | |
|---|---|
| **TOP** (1 oz, 35 µm) | the regulator's output run, the two runs that land at the loads, and every capacitor's lands |
| **GND** (½ oz, 17.5 µm) | the reference plane, with an anti-pad round every barrel on the rail |
| **IN3** (½ oz) | two local `+3V3` pours — one under the load field, one at the regulator |
| **BOT** (1 oz) | the supply's long way round the connector cut-out, 0.20 mm wide |

One rail, `+3V3`. One source at the regulator's output pad: 3.3 V open circuit behind **60 mΩ and
1 µH**, which is the protection FET's on-resistance plus the ferrite's DC resistance and
inductance. Two loads — `U1` drawing **350 mA**, and `U3` stating **no current at all**.

`U3` is the point of having two. A load row with no current is an **observation port**: it
contributes nothing to the DC solve and is still reported, and over frequency it is a place the
impedance is judged. The DC report lists it as *observed* rather than leaving it out, which is what
tells you the tool did not simply ignore a row you meant to fill in.

**The two IN3 pours are local, and neither bridges the board.** Each hangs off the rail at one
place and carries no through current. A single pour spanning both ends would put a second path in
parallel with the 0.20 mm BOT run and delete the finding Q0 exists for — which is worth saying out
loud, because on the picture the two arrangements look much the same.

Thirteen capacitors: six 100 nF 0402s, two 1 µF 0603s, a 10 µF 0805, a 100 µF polymer bulk, and
three more 0402s out at the edge of the field. Three aggressors — see *What the aggressors are*,
below. **Every part is on the artwork**, and that is what the rest of this page turns on.

## Where the refdes come from

The `.crail` names three companion files beside the board, and they are what make a reference
designator mean anything:

| | |
|---|---|
| `layout/Board.clay` | the artwork — copper, barrels, anti-pads |
| `layout/Board.ipc` | the board netlist: a net name per pad, and the refdes and pin it belongs to |
| `layout/Board.placement.csv` | the placement: a centroid and a side per refdes |

Without the netlist every port would have to be a **coordinate**, and railRF could compute no
mounting inductance for any part — a part's mounting loop is a property of *where it was placed*,
and nothing would have said where. With it, the ports read `U1.VDD` rather than a point in DBU, the
parts table fills in its **Position** column, and every mounting inductance below was read off the
artwork rather than typed.

`layout/Board.gen.py` is what writes all three. They are kept in step by a generator rather than by
hand because they have to agree: a pad in the netlist that is not under a land in the `.clay` is a
part railRF cannot locate, and a land with no anti-pad under it is a decoupling capacitor shorting
the rail to its reference. The generator refuses to write anything if either is true.

## Q0 — is it connected, and what does it cost

**48.4 mV** at `U1` against a **52 mV** budget. Met, and only just.

```
22.7 mV  45%   26.5 mm of 0.209 mm BOT copper          64.9 mOhm
21.0 mV  42%   the source's own series resistance      60.0 mOhm
 1.6 mV   3%   3.9 mm of 0.387 mm TOP copper            5.4 mOhm
 1.3 mV   3%   9.3 mm of 0.227 mm BOT copper           22.3 mOhm
 1.1 mV   2%   2.6 mm of 0.45 mm TOP copper             3.1 mOhm
 1.0 mV   2%   2.3 mm of 0.43 mm TOP copper             2.8 mOhm
 0.6 mV   1%   the reference return                     1.8 mOhm
```

Those are the top seven of thirteen rows, and the window states the rest and their total. **Do not
add the millivolts up and expect 48.4** — the full thirteen come to 50.1 mV, and that is not a
disagreement. The table counts every group carrying current anywhere on the rail, and the port drops
only what is on the path from the source to it: this rail divides into two legs that rejoin, each
drops 1.76 mV between the same pair of nodes, and the port drops one of them while the table lists
both. The Breakdown card names its own total and says so on any board where the two differ.

Half the budget is one trace. That is the finding this example exists for: on a compact board
with thin copper the artwork is not a rounding error on the parts, it is the largest single term
after the parts you already knew about. Widen the BOT run from 0.20 mm to 0.40 mm in the layout
editor, re-run, and watch the copper term halve — the FET then becomes the thing worth arguing
about.

Three via transitions, none over its current limit.

## Q1 — does it meet its target

The band is 100 kHz to 100 MHz, the target a flat **55 mΩ**: a 13.75 mV ripple allowance against a
250 mA step. The rail **passes by 0.8 dB at its worst, at 100 MHz** — the top of the band, where
every capacitor is its own mounting inductance and nothing else.

Two anti-resonances are named rather than left to be found on the plot:

| | |
|---|---|
| 4.14 MHz, 17.9 mΩ | L(C9) against C(C7–C8) |
| 10.80 MHz, 19.8 mΩ | L(C7–C8) against C(C1–C13) |

and the converter's **fifth harmonic at 11 MHz sits 1.8 % away from the second of them**, which is
the coincidence check earning its place: neither the peak nor the harmonic is alarming on its own,
and 1.8 % is close enough that the two are the same event.

## Q2 — which capacitors are doing anything

The ranking re-solves the whole sweep once per part and reports what deleting it would cost:

```
C10  100 uF bulk   16.3 dB   -> -15.6 dB.  It is holding the low band up on its own.
C1   100 nF 0402    0.9 dB   ->  -0.1 dB.  Removing it fails the target.
C4   100 nF 0402    0.7 dB   ->   0.1 dB.  Removing it still passes, barely.
C11  100 nF 0402    0.5 dB   ->   0.3 dB.  Removing it still passes.
```

**`C1`–`C3`, `C11`–`C13` and `C4`–`C6` are the same purchased part** — one 100 nF 0402 part number,
placed nine times — and they are worth three different amounts. The whole of the difference is how
each one reaches its vias, which railRF read off the artwork:

| | how it is mounted | computed mounting loop | worth |
|---|---|---|---|
| `C1`–`C3`, `C7`–`C10` | a via in each land | **0.56 nH** | 0.8–0.9 dB |
| `C4`–`C6` | 0.35 mm of fan-out | **0.71 nH** | 0.7 dB |
| `C11`–`C13` | 0.9 mm of 0.125 mm fan-out | **1.20 nH** | 0.5 dB |

Select a row in the parts table to see which one is which on the board. The number railRF prints
for `C11` is

```
C11: 1201.5 pH — 552.1 pH power via + 16 pH return via − 2 × 1.8 pH mutual
     + 636.9 pH pad trace, with the pair 2.9 mm apart over 1065 µm of dielectric.
```

— and the **636.9 pH of pad trace** is the entire penalty. Two 0.9 mm stubs cost more than the
barrel they reach. That is a finding you can act on in an afternoon, and it is not visible in any
part's datasheet.

Delete a part row and re-run to watch the worst margin move.

## Q3 — did the form factor break it

Press **Compare…** and pick a second `.crail`. The way to make one is to copy this workspace,
re-shape the board in the copy, and compare the two. There is deliberately no second board shipped
here: the comparison is about *your* re-layout, and a canned pair would teach the report's format
rather than the question.

The per-part table is the half worth looking at first, now that the mounting loops come off the
artwork: *a part that was 0.56 nH on the reference and is 1.2 nH on yours because its fan-out grew*
is the kind of row that explains a whole band.

## What the aggressors are

Three rows — `Y1` at 32.768 kHz, `U2` at 2.2 MHz, `Y2` at 26 MHz — and they are **typed, not
derived**. Each row is a name, a fundamental and a harmonic count, and every row says `Typed` on it
for exactly that reason: railRF cannot see what switches on your board, and a frequency somebody
pre-filled and nobody checked is the one that will be wrong.

They are an input, not a decoration. They put markers on the impedance plot and they drive the
coincidence check — which is where the 1.8 % above came from. `U2` is the regulator, and it is on
this board and in the netlist; `Y1` and `Y2` are a timekeeping crystal and a radio reference that
this synthetic board does not draw, because nothing about the check needs them drawn. To use them
on your own board, replace the frequencies with your converter's switching frequency and your
oscillators', and set the harmonic count to as far up as each one still has energy.

## Fast and Accurate — what this example is set up for

railRF opens on **Fast**, which is what makes the window re-solve while you edit. Fast reads each
trace section as one resistance from its own geometry; **Accuracy** meshes the copper instead.

Run both. On this board they agree closely:

| | Drop at U1 |
|---|---|
| Fast (the default) | 48.368 mV |
| Accuracy | 49.025 mV |

**1.4 % apart, and the Fast answer is the optimistic one** — which is the direction it is always
wrong in, and the reason the two are worth running once on any board you intend to trust. Most of
the difference is the reference return: Fast prices the plane at 1.8 mΩ from 38 coarse cells,
Accuracy at 3.7 mΩ from 326,131.

Both models report the same plane capacitance, **7.975 pF over 1.87 cm² at εr 4.3**. That is the
cheapest check on this page: one glance at it tests the permittivity, the overlap area and the
dielectric thickness at once, and a stackup copied from the last board shows up there immediately.

## What this example does not show

- **The mounting loop is a via pair and two pad traces, and nothing else.** It does not carry the
  part's own body inductance or the loop area in the plane of the board. On a board whose rail is a
  trace on the same layer as its capacitors — no power via at all — the computed figure is
  structurally low, and typing one is the right answer there.
- **The series FET and the ferrite are rolled into the source's R and L *on this example*, and they
  no longer have to be.** railRF models a series element in its own right: a part row marked
  *series*, with its two rail-side terminals, its DC resistance and its impedance over frequency
  (an R-L, or its own Touchstone file). The rail then has a *before* and an *after* — everything
  upstream of the element sees one impedance and everything downstream sees another — and which
  side each capacitor and each port is on is **measured off the artwork**, by cutting the rail at
  the element's two pads and walking, rather than typed. This example keeps the lumped form
  because that is what its numbers below were computed from; a rail with a ferrite in the middle of
  it, which is where ferrites go, is the case the lumped form cannot express at all.
- **Every observation port on the rail reads the same Z(f) — *on a rail with no series element*.**
  That is P1's lumped model rather than a defect: there is no copper between the ports in the
  frequency model, so nothing in it could make them differ, and the run says so on its own notes.
  Put a series element in the rail and the ports genuinely differ, by that element's own impedance
  at every frequency; the note is then absent, because it would be false.
- **A ferrite modelled as an R-L is optimistic, and railRF says so on the result.** A bead's
  impedance is strongly bias-dependent and its datasheet curve is measured at zero DC bias; at a
  few hundred milliamps the same part is worth a fraction of its marked figure, and the curve looks
  entirely ordinary. Attach the part's own measured impedance to replace the caveat.
- **Nothing above about 180 MHz.** The top of the band is a tenth of this plane pair's first cavity
  mode; past that the plane is distributed and the lumped answer is not the right one.
