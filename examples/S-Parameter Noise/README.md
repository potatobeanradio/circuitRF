# S-Parameter Noise

**Open a numbered bench, Simulate, and its Data Display opens on the result.** The three run in a second each, in any
order:

- **`1 Attenuator`**: a matched 6 dB pad, its temperature swept from 20 K to 400 K. Its noise figure is 6 dB at 290 K
  and `1 + (T/T0)(L − 1)` at every other temperature.
- **`2 Friis Cascade`**: a low-noise stage, a pad and a second stage. The run's noise figure lies on Friis' formula at
  every pad loss from 1 to 10 dB.
- **`3 LNA Noise Circles`**: a transistor-like two-port behind an input noise match, with a stabilising resistor. NF
  against NFmin, the optimum source on a Smith chart, and two circles of constant noise figure.

Each bench's S-parameter analysis has **Noise** ticked (`Noise=yes` in the netlist). That adds the noise-wave
correlation `Cs` to the run, and for a two-port the noise figure `NF`, `NFmin`, `Rn` and `GammaOpt`, as cubes beside
`S`. Every part in these benches is noisy: the resistors and the lossy L and C are thermal at their own temperature, and
each SnP block is noisy by the noise data in its file. So no run warns about a noiseless part. The full description is
in *Reference ▸ Simulations ▸ Small-signal noise*.

## Running a bench

1. **File ▸ New ▸ Examples ▸ S-Parameter Noise** copies this workspace to a folder you choose and opens it.
2. In the Workspace tree, open a bench's schematic, for example `1 Attenuator`'s.
3. **Simulate.**
4. The bench's Data Display, `1 Attenuator.cdd` beside the `.cws`, opens on the new result. It is in the tree too, to
   open again later.

To see the noise switch, open the bench's `SP1` from the Analyses panel: the **Noise** box is under the frequency plan.

## 1 Attenuator

A π pad of three resistors between 50 Ω ports. Its loss is the variable `L_dB = 6`, and the resistors follow from it:

```
K = 10^(L_dB/20)      Rsh = 50·(K + 1)/(K − 1) = 150.48 Ω      Rse = 50·(K² − 1)/(2K) = 37.35 Ω
```

`temp = 16.85` is the design's ambient in °C, which is 290 K. A parametric sweep `SW1` steps `temp` from −253.15 °C to
126.85 °C, which is 20 K to 400 K in 10 K steps, around one frequency, 1 GHz. A pad is flat, so one is enough.

A passive part's noise is the thermal noise of its own loss, at its own temperature. For a matched pad at temperature T
with loss L (a ratio), that gives

```
F = 1 + (T/T0)·(L − 1)          T0 = 290 K
```

At T = T0 the noise figure is the loss. The `MEAS1` block writes this formula as `NF_closed`, beside the run's `NF`:

| Pad temperature | NF, circuitRF | `NF_closed` |
|---|---|---|
| 20 K | 0.812 dB | 0.812 dB |
| 80 K | 2.606 dB | 2.606 dB |
| 290 K | **6.000 dB** | 6.000 dB |
| 300 K, circuitRF's default ambient | 6.111 dB | 6.111 dB |
| 400 K | 7.086 dB | 7.086 dB |

They agree to 1e-14 dB at all 39 temperatures. **What the display shows** (`1 Attenuator.cdd`):

- **Noise figure of a 6 dB pad against its temperature**: the run's NF as dots, `NF_closed` as a line through them, and
  the loss, −dB(S21), dashed at 6 dB. The two cross at 290 K, where the marker reads 6.000 dB.
- **Noise temperature of the wave leaving port 2**: `Tn = Re Cs(2,2)/k`, against Bosma's `T(1 − 1/L)` for a passive
  network. At 290 K both are 217.2 K.

**A design that states no temperature runs at 26.85 °C, 300 K**, not at T0, and there this pad reads 6.111 dB.
Noise figures are referred to 290 K, so a passive part 10 K warmer than that makes a little more noise than its loss.
Write `temp = 16.85` in a design whose passive parts should be at T0.

## 2 Friis Cascade

Port 1, stage A, a matched π pad, stage B, port 2, all matched to 50 Ω. Both stages are SnP blocks whose files carry a
Touchstone noise block:

| Stage | File | Gain | Noise data | NF from 50 Ω |
|---|---|---|---|---|
| A, low-noise | `data/amp_a.s2p` | 16 dB | NFmin 0.8 dB at Γopt 0.25∠45°, rn 0.25 | 0.957 dB |
| Pad | three resistors at 290 K | −`L_dB` | its loss | `L_dB` |
| B | `data/amp_b.s2p` | 12 dB | NFmin 3 dB at Γopt 0, rn 0.4 | 3 dB |

`SW1` sweeps the pad's loss `L_dB` from 1 to 10 dB. Friis' formula for three matched stages is

```
F = FA + (L − 1)/GA + (FB − 1)·L/GA
```

where FA is stage A's noise factor from a 50 Ω source. Stage A's optimum source is not 50 Ω, so FA is above its Fmin:

```
FA = Fmin + 4·rn·|Γopt|² / |1 + Γopt|²       (Γs = 0)
```

The VAR block holds each stage's numbers as its file states them (`GA_dB`, `NFminA_dB`, `GoptA`, `rnA`, `GB_dB`,
`NFB_dB`). `MEAS1` turns them into `FA`, `GA`, `FB`, and `NF_friis` is the formula above.

| Pad | NF, circuitRF | `NF_friis` | NFmin, circuitRF | Pad first, `NF_padfirst` |
|---|---|---|---|---|
| 1 dB | 1.087 dB | 1.087 dB | 0.943 dB | 2.043 dB |
| 3 dB | **1.210 dB** | 1.210 dB | 1.078 dB | 4.043 dB |
| 10 dB | 2.362 dB | 2.362 dB | 2.296 dB | 11.043 dB |

The run and the formula agree to 3e-14 dB at every loss and at each of the four frequencies, 1 to 4 GHz. The gain is
`GA − L + GB` to 2e-13 dB. **What the display shows** (`2 Friis Cascade.cdd`):

- **Cascade noise figure against the pad's loss**: the run's NF as dots on `NF_friis`, and the run's NFmin dashed below
  it. NFmin is lower because stage A would rather see a source of 0.25∠45° than 50 Ω. The marker is on the 3 dB pad.
- **Why the low-noise stage goes first**: the same cascade, against `NF_padfirst`, Friis' formula with the pad ahead of
  stage A, `L·FA + (FB − 1)·L/GA`. A loss in front of the first gain adds to the noise figure decibel for decibel; behind
  16 dB of gain, the 3 dB pad costs 0.17 dB over the 1.04 dB of the two stages alone.
- **Cascade gain**: the run's gain on `GA − L + GB`.

The pad is at 290 K (`temp = 16.85`), which is what makes its noise factor equal to its loss. Noise data from a file is
absolute and does not move with the ambient.

## 3 LNA Noise Circles

`data/transistor.s2p` is a synthetic transistor-like two-port from 2 to 6 GHz with a noise block. At 4 GHz it has
NFmin 0.65 dB at Γopt 0.65∠75° and rn 0.19. It is not unconditionally stable below about 4 GHz. The bench builds a
narrow-band LNA around it:

- **Input noise match**: a shunt `C1` (SRC, 0.43 pF with 0.25 Ω of ESR) and a series `L1` (SRL, 3.16 nH with 2 Ω, a Q
  of 40 at 4 GHz). They were tuned so that the LNA's own optimum source is 50 Ω at 4 GHz.
- **Stabilising resistor**: `Rstab`, 300 Ω from the output to ground. With it, Rollett's K is 1.27 to 1.64 across
  the band. Without it, K falls to 1.01 at 3 GHz, which leaves no margin; the transistor on its own is at 0.92
  there.

`SP1` runs from 3 to 5 GHz in 50 MHz steps. `MEAS1` adds the gain, `K_rollett = K(SP1.S)`, and two noise circles,
`NF_125 = nf_circle(SP1.S, 1.25)` and `NF_200 = nf_circle(SP1.S, 2)`. Each circle is a cube over frequency and 101
points: the source reflections that give that noise figure.

| At 4 GHz | |
|---|---|
| NF from 50 Ω | 0.979 dB |
| NFmin | 0.979 dB, at a GammaOpt within 0.002 of the centre |
| Rn | 5.0 Ω |
| Gain | 11.4 dB |
| K | 1.43 |

The LNA's NFmin is 0.33 dB above the transistor's. Most of that, 0.28 dB, is the loss in `L1` and `C1`: with their
resistances set to 0, NFmin is 0.70 dB. `Rstab`'s own noise is the other 0.06 dB.

**What the display shows** (`3 LNA Noise Circles.cdd`):

- **Noise figure of the LNA**: NF from 50 Ω against NFmin, 3 to 5 GHz. They touch at 4 GHz, where the match puts 50 Ω
  at the optimum, and part on either side. Below 4 GHz the NF is lower still, since NFmin falls faster than the mismatch
  grows.
- **GammaOpt and two noise circles**: GammaOpt's locus from 3 to 5 GHz, passing through the centre of the chart at
  4 GHz (the marker), and the 1.25 dB and 2 dB circles at 4 GHz, about 0.27 dB and 1 dB above NFmin. The circle traces
  are `NF_125[20, :]` and `NF_200[20, :]`: point 20 of the frequency axis is 4 GHz. `NF_125[~, :]` draws one circle per
  frequency instead.
- **Gain** and **Stability**: dB(S21), and K above 1 everywhere.

Every point of both circles gives its own noise figure back through `nf(SP1.S, Γs)`, to 3e-15 dB.

## The data

Every file in `data/` is synthetic, written for this example, and says what it is in its header. None is a real part.

| File | What it is |
|---|---|
| `data/amp_a.s2p` | Stage A: matched and unilateral, 16 dB, NFmin 0.8 dB at Γopt 0.25∠45°, rn 0.25, flat from 1 to 4 GHz |
| `data/amp_b.s2p` | Stage B: matched and unilateral, 12 dB, NFmin 3 dB at Γopt 0, rn 0.4, flat from 1 to 4 GHz |
| `data/transistor.s2p` | A transistor-like two-port, 2 to 6 GHz in 0.5 GHz steps; every S and noise value runs linearly between the end values in its header |

The noise block is Touchstone 1.x: after the S data, one row per frequency of `f NFmin(dB) |Γopt| ∠Γopt(°) rn`, with
rn = Rn/R on the option line's R. Between rows the noise parameters are linear.

To keep a run's noise as a file, export S as Touchstone, or from a terminal:

```
circuitrf sparam "3 LNA Noise Circles/schematic/3 LNA Noise Circles.csch" -o lna.s2p
```

The `.s2p` it writes carries the noise block, so the LNA can be placed as an SnP in a bigger design with its noise.
