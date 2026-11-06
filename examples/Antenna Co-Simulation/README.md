# Antenna Co-Simulation

A meander antenna on a 45 × 95 mm board, solved once with each of its parts as a port. From then on a schematic gives S11,
the radiation pattern and realized gain for any part values, in under a second and with no new EM run.

## The two cells

**Meander Antenna.** One copper layer on 1.6 mm FR-4: a 43 × 62 mm ground, a 1 mm coplanar feed in a slot in the ground, and a
2 mm wide meander about 98 mm long above it. Three 0402 land patterns sit where the feed meets the antenna:

| Part | Where it goes |
|---|---|
| `C1` | shunt, feed to ground |
| `L1` | series, feed to antenna |
| `C2` | shunt, antenna to ground, not fitted (Disable = Open) |

Its schematic is the board's circuit: the source, `C1` and `L1`, with `C2` drawn but not fitted. The layout labels `P1` where the feed
starts. **A 3D view does not take port labels from a layout it places,** so the 3D view has a `P1` of its own, a lumped sheet across the
0.5 mm gap at the end of the slot.

Its 3D view has one setup, **Pattern**: EMerge at the Accurate preset, 828 to 908 MHz in 20 MHz steps, with **Component ports**
on every part and **Radiation pattern** on. It keeps a far field at each of the five frequencies.

**Antenna Bench** (a test bench). The saved run as one **EM Field Data** block, `EM1`, on
`results/Meander Antenna Pattern.emerge.cemr`, with the parts drawn around it. Each part stands from its port's pin to ground,
because a gap port's own pad is that port's reference. The values come from the variables `Lser`, `dL` and `Cshunt`.

| Analysis | What it does |
|---|---|
| `SP1` | S11 from 828 to 908 MHz |
| `F1` | the pattern at 868 MHz, for the goals and the first tab |
| `F2` | the pattern at every stored frequency |
| `F3`, swept by `F3_sweep_dL` | the pattern at 868 MHz with the inductor 2 nH low to 2 nH high |

## Run it

1. **Simulate the Meander Antenna's 3D view** (setup `Pattern`). It needs EMerge, and the first run offers to install it. On a 10-core
   Mac with 17 GB it took 2 min 45 s, peaking at 9.4 GB, on 81,547 tetrahedra.
2. **Simulate the Antenna Bench**, then open its Data Display.
3. **Optimize.** The Optimizer panel already holds the variables, two goals and an algorithm (simplex, seed 1). Press **Run**, apply its best
   values and simulate again to redraw the plots. Moving a tuning slider on `Lser` or `Cshunt` redraws the pattern as you go.

## What you will see

As drawn, with 10 nH and 2.7 pF, the antenna is badly mismatched. The optimizer meets both goals in 3 iterations and 9 evaluations,
in under a second:

| At 868 MHz | As drawn | Best |
|---|---|---|
| `Lser` | 10 nH | 2.57 nH |
| `Cshunt` | 2.7 pF | 3.99 pF |
| `Match`: S11, ≤ −15 dB | −3.22 dB | −16.9 dB |
| `Gain`: realized gain, ≥ 1.5 dBi | −0.77 dBi | 2.06 dBi |
| Gain | 2.04 dBi | 2.15 dBi |
| Directivity | 2.21 dBi | 2.28 dBi |

**The parts decide how much power gets in, and barely change the shape.** Directivity moves by 0.07 dB between the two columns while
realized gain moves by 2.8 dB. The tabs of the Data Display show this three ways, and these numbers are for the best values:

- **Match and pattern.** S11 is below −9.5 dB from 858 to 894 MHz. The pattern is a donut around the board's long axis, with a
  shallow null along it.
- **Across the band.** Realized gain is −0.61, 1.02, 2.06, 1.94 and 0.71 dBi from 828 to 908 MHz, while the cuts stay nearly on top of each other.
- **Inductor error.** With `L1` 2 nH low to 2 nH high, realized gain is 1.03, 1.67, 2.06, 2.12 and 1.85 dBi. The best point on
  this curve is not 0 nH, since the optimizer stopped once both goals were met.

The curves on the last two tabs come from the stored results. A frequency between the five stored ones has S but no pattern, so
`F1` and `F3` must name one of the five, and `F2` takes them all with `freq=all`.

## Checked against Palace

The same board at 868 MHz, on Palace at second order, 74,472 tetrahedra. It took 9 min 26 s for this one frequency, with other work running on the machine.

| At 868 MHz | As drawn: EMerge | As drawn: Palace | Best: EMerge | Best: Palace |
|---|---|---|---|---|
| S11 | −3.22 dB | −3.17 dB | −16.9 dB | −19.9 dB |
| Directivity | 2.21 dBi | 2.01 dBi | 2.28 dBi | 2.03 dBi |
| Gain | 2.04 dBi | 1.36 dBi | 2.15 dBi | 1.55 dBi |
| Realized gain | −0.77 dBi | −1.50 dBi | 2.06 dBi | 1.51 dBi |
| Radiation efficiency | 96.1 % | 86.2 % | 97.2 % | 89.6 % |

The two agree on the match and on the part values that reach it. **EMerge's gain reads about 0.6 dB above Palace's and its efficiency about 8 to 10 points higher,** so read this
example's gain as showing how the parts move it, rather than as a value to design to. To compare for yourself, set the setup's solver to Palace.

## What was traded for speed

- **One copper layer and no vias.** Stitching vias set the size of a mesh long before the wavelength does, so leaving them out
  is most of the saving. A two-layer board of the same outline with 324 vias took 7 min 12 s at one frequency in Palace.
- **Parts are ideal,** and their bodies are not in the 3D model.
- **The air box is 170 mm on every face,** about λ/2. On such a board gain and efficiency keep changing as the box grows, even after
  S11 and directivity have settled, so compare a pattern's gain at a second box size before relying on it. EMerge's far field also reads
  high when the box is close.
- **Nothing is compared with a measurement.** This board was never built, so the numbers show the method rather than a design to copy.
