# Smith Chart — a narrowband match, by hand

A device input impedance, stated over three frequencies, matched to 50 Ω with two parts. It is
one screen of work and it is meant to be **dragged**, not read.

Open **Gate match.csmith** from the project tree. The window opens with the network already built
and the chart already drawn.

## The design

The generator is the thing being matched: the input of a small-signal device, low and capacitive,
measured at three frequencies across a 2.3–2.6 GHz band.

| Frequency | Z<sub>gen</sub> |
|---|---|
| 2.30 GHz | 8.6 − j12.8 Ω |
| 2.45 GHz | 8.0 − j12.0 Ω |
| 2.60 GHz | 7.5 − j11.3 Ω |

The design frequency is **2.45 GHz**, in the middle of the table. Two elements take it to 50 Ω:

| | | |
|---|---|---|
| `L1` | series | 1.97 nH |
| `C1` | shunt | 2.98 pF |

That is the classical low-to-high L match, and the chart draws it as exactly the two arcs it is:
`L1` walks a **constant-resistance** circle from 8 − j12 up to 8 + j18.3, and `C1` walks a
**constant-conductance** circle from there to the middle of the chart.

## What it lands on

Read at each of the three table frequencies — the numbers the status strip states, and the numbers
`circuitrf smith` prints:

| Frequency | Z<sub>load</sub> | \|Γ\| | VSWR |
|---|---|---|---|
| 2.30 GHz | 35.40 + j7.87 Ω | 0.193 | 1.48 |
| **2.45 GHz** | **49.98 − j0.10 Ω** | **0.0010** | **1.002** |
| 2.60 GHz | 56.14 − j23.09 Ω | 0.220 | 1.56 |

**The band edges come apart, and that is the whole point of the example.** The match is essentially
exact at the design frequency and is 1.5:1 at each end of a band only ±6 % wide. That is not sloppy
work: it is what a two-element match on a device this far from 50 Ω costs. The loaded Q at the
corner between the two arcs is

    Q = |X| / R = 18.33 / 8.00 = 2.291

and the bandwidth follows from it. The **constant-Q arcs are switched on at Q = 2.291** in the
shipped document for exactly that reason — the pair of arcs passes through the corner gripper, so
the thing that sets the bandwidth is drawn on the same picture as the thing that sets the match.

## What to drag, and what to watch

The three grippers are at the three nodes of the walk: the generator (anchored), the corner between
`L1` and `C1`, and the load point.

- **Drag the corner gripper.** It is `L1`'s, so it slides along the constant-resistance circle
  through 8 Ω and drags `C1`'s whole arc after it. Watch the three **load points** — the 2.45 GHz
  one moves off the centre first, and the two band-edge points swing much further than it does.
  That asymmetry is the Q.
- **Drag the load-point gripper.** It is `C1`'s, and it is the last element in the cascade, so it
  moves along the constant-conductance circle `C1` is on. It does **not** solve the network for you:
  dragging the end moves the last element's value and nothing else.
- **Turn the constant-Q arcs off and on** — the **Show arcs** box in the generator panel's *Constant Q* card. With them on, the corner gripper has
  somewhere to be: a corner inside the arcs is a wider match, a corner outside them is narrower.
- **Change the design frequency** to 2.30 or 2.60 GHz. The trajectories redraw at the new frequency
  and the generator impedance is interpolated from the table, so the arcs move but the components do
  not. The emphasised load point moves to whichever frequency you chose.
- **Switch the swept band off** — the *Swept band* card's **Draw band** box; it is on, 2.3–2.6 GHz, 61 points. The continuous locus through the
  three load points is what makes the narrowness visible rather than inferable.

## The copied schematic

**Matched input ▸ schematic ▸ Matched input.csch** is this same network after **Copy** on the
network strip and a paste into a new schematic — component for component, wire for wire, with the
two ends terminated. Nothing in it was drawn by hand except the S-parameter card.

The generator end is `Gen`, a `TermG` with `Num=1` and `Z = complex(8,-12)`; the far end is `load`,
a `TermG` with `Num=2` and `Z = 50 Ω`. **Port 1 carries the generator impedance at the design
frequency and nowhere else** — a `Term` holds one impedance and the generator table holds three
rows, which is what the strip says when you press Copy.

Run it (`SP1`, 2.2–2.7 GHz, 101 points) and plot `dB(S(1,1))`:

| Frequency | dB(S11) | dB(S21) |
|---|---|---|
| 2.30 GHz | −15.8 dB | −0.12 dB |
| 2.45 GHz | −59.8 dB | −0.00 dB |
| 2.60 GHz | −15.1 dB | −0.14 dB |

S11 here is referenced to the complex port-1 impedance, so it is the **conjugate match** at the
device's own terminals — which is why it goes to −59.8 dB at 2.45 GHz rather than merely small. It
is the engine's independent reading of the same network the chart drew, and the two agree.

## Settings

**Nothing here was traded for speed.** The whole example is closed-form arithmetic — the chart
evaluates in microseconds and the 101-point S-parameter run finishes before the window repaints —
so every setting in it was chosen to make the picture legible and nothing else:

- the swept band's **61 points** is a drawing choice. 201 points draws the same locus, smoother, and
  changes no number in the tables above;
- the S-parameter card's **101 points over 2.2–2.7 GHz** frames the null. 1,001 points over the same
  span resolves the bottom of it (which is a numerical zero, not a physical one) and moves nothing
  at the three frequencies quoted.

## Two readings of the same network, against two references

The status strip's **mismatch** is the power the reflection costs against the **chart's own Z₀**,
−10·log₁₀(1−|Γ|²) — the same Γ the VSWR beside it is made of. Here it reads **0.00 dB** at 2.45 GHz,
0.17 dB at 2.30 GHz and 0.22 dB at 2.60 GHz, so it moves with the VSWR and with the picture.

The **−59.8 dB** in the table above is a different reference and a different question: S11 out of the
copied schematic is taken against the complex port-1 impedance, which makes it the **conjugate match
at the device's own terminals**. That is what the faint ⊕ target glyphs on the chart mark, and it is
why the two numbers are not two spellings of one thing — 50 Ω and conj(8 − j12) Ω are different
points, and this network lands on both at once because it was designed to.
