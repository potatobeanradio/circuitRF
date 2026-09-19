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
| **TOP** (1 oz, 35 µm) | the regulator's output run, and the two runs that land at the loads |
| **GND** (½ oz, 17.5 µm) | the reference plane, with an anti-pad round every barrel |
| **IN3** (½ oz) | declared in the stackup, carrying no rail copper — the other signals live here |
| **BOT** (1 oz) | the supply's long way round the connector cut-out, 0.20 mm wide |

One rail, `+3V3`. One source at the regulator's output pad: 3.3 V open circuit behind **60 mΩ and
1 µH**, which is the protection FET's on-resistance plus the ferrite's DC resistance and
inductance. Two loads — `U1` drawing **350 mA**, and `U3` stating **no current at all**.

`U3` is the point of having two. A load row with no current is an **observation port**: it
contributes nothing to the DC solve and is still reported, and over frequency it is a place the
impedance is judged. The DC report lists it as *observed* rather than leaving it out, which is what
tells you the tool did not simply ignore a row you meant to fill in.

Thirteen capacitors: six 100 nF 0402s, two 1 µF 0603s, a 10 µF 0805, a 100 µF polymer bulk, and
three more 0402s placed further away — `C11`–`C13`, with 2.4–2.6 nH of mounting inductance against
`C1`–`C3`'s 0.85 nH. Three aggressors: the timekeeping crystal at 32.768 kHz, the converter at
2.2 MHz and the radio's reference at 26 MHz.

## Q0 — is it connected, and what does it cost

**48.7 mV** at `U1` against a **52 mV** budget. Met, and only just.

```
22.7 mV  47%   26.5 mm of 0.209 mm BOT copper          64.9 mOhm
21.0 mV  43%   the source's own series resistance      60.0 mOhm
 2.0 mV   4%    4.8 mm of 0.438 mm TOP copper           5.8 mOhm
 1.9 mV   4%    3.9 mm of 0.387 mm TOP copper           5.4 mOhm
 0.6 mV   1%   the reference return                     1.7 mOhm
 0.2 mV   0%   2 parallel vias, 0.3 mm at 25 µm         0.6 mOhm
```

Half the budget is one trace. That is the finding this example exists for: on a compact board
with thin copper the artwork is not a rounding error on the parts, it is the largest single term
after the parts you already knew about. Widen the BOT run from 0.20 mm to 0.40 mm in the layout
editor, re-run, and the drop falls to **37.6 mV** — the copper term halves and the FET becomes the
thing worth arguing about.

Three via transitions, none over its current limit.

## Q1 — does it meet its target

The band is 100 kHz to 100 MHz, the target a flat **88 mΩ**: a 22 mV ripple allowance against a
250 mA step. The rail **passes by 0.6 dB at its worst, at 100 MHz** — the top of the band, where
every capacitor is its own mounting inductance and nothing else.

Two anti-resonances are named rather than left to be found on the plot:

| | |
|---|---|
| 3.04 MHz, 35 mΩ | L(C9) against C(C7–C8) |
| 8.25 MHz, 31 mΩ | L(C7–C8) against C(C1–C13) |

and the converter's **fourth harmonic at 8.8 MHz sits 6.2 % away from the second of them**, which
is the coincidence check earning its place: neither the peak nor the harmonic is alarming on its
own.

## Q2 — which capacitors are doing anything

The ranking re-solves the whole sweep once per part and reports what deleting it would cost:

```
C10  100 uF bulk   12.1 dB   -> -11.5 dB.  It is holding the low band up on its own.
C1   100 nF 0402    1.1 dB   -> -0.4 dB.   Removing it fails the target.
...
C11  100 nF 0402    0.4 dB   ->  0.2 dB.   Removing it still passes.
C12  100 nF 0402    0.4 dB   ->  0.2 dB.
C13  100 nF 0402    0.4 dB   ->  0.2 dB.
```

`C11`–`C13` are the same part as `C1`–`C3` and are worth a third as much, because they were placed
further from the load and carry three times the mounting inductance. On this board all three can
come off; none of `C1`–`C9` can. Delete one of each in the parts table and re-run to watch the
worst margin move.

## Q3 — did the form factor break it

Press **Compare…** and pick a second `.crail`. The way to make one is to copy this workspace,
re-shape the board in the copy, and compare the two. There is deliberately no second board shipped
here: the comparison is about *your* re-layout, and a canned pair would teach the report's format
rather than the question.

## Fast and Accurate — what this example is set up for

railRF opens on **Fast**, which is what makes the window re-solve while you edit. Fast reads each
trace section as one resistance from its own geometry; **Accuracy** meshes the copper instead.

Run both. On this board they agree closely:

| | Drop at U1 |
|---|---|
| Fast (the default) | 48.698 mV |
| Accuracy | 49.488 mV |

**1.6 % apart, and the Fast answer is the optimistic one** — which is the direction it is always
wrong in, and the reason the two are worth running once on any board you intend to trust. Most of
the difference is the reference return: Fast prices the plane at 1.7 mΩ from 38 coarse cells,
Accuracy at 3.7 mΩ from 327,457. The supply trace itself — the term that actually matters here —
agrees to under 1 %.

Those cell counts are the whole of the difference in what the two cost. Accuracy meshes the entire
reference plane, and on this board the mesh is capped by cell count rather than by the copper's own
width — so a finer answer is not available by asking, and the cap is what the *"meshed coarsely"*
note after the run is reporting.

## What this example does not show

- **Ports are coordinates, not refdes.** Every report row here reads a point in DBU rather than
  `U1.VDD`, because a refdes anchor resolves against a placement file and this workspace ships no
  board import. The same is true of any `.crail` that names no placement.
- **The series FET and the ferrite are the source's R and L**, not rows of their own. In the lumped
  model the source is the whole branch feeding the rail node, and this is where a series part lives.
- **Nothing above about 180 MHz.** The top of the band is a tenth of this plane pair's first cavity
  mode; past that the plane is distributed and the lumped answer is not the right one.
