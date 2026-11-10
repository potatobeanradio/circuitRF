# Receiver Noise

**Open a numbered bench, Simulate, and its Data Display opens on the result.** Run them in order: each one builds on
the one before it.

1. **`1 LNA Noise Match`**: a one-stage FET LNA, noise-matched at 2.45 GHz. NF, NFmin, the noise and gain circles, and
   which part sets the noise. Under a second.
2. **`2 Receiver Budget`**: the receiver as system blocks, LNA to IF amplifier. Its NF lies on Friis' formula at every
   LNA gain. Under a second.
3. **`3 Mixer Noise`**: a single-diode mixer. SSB and DSB noise figure against LO drive, and how much of the noise comes
   in through the image. Under a second.
4. **`4 LNA Under a Blocker`**: bench 1's LNA with a 2.3 GHz blocker in front of it. NF against blocker power. Under a
   second.
5. **`5 Noise Sourcepull`**: bench 1's transistor stage on a source-tuner grid. NF and gain contours on the source plane,
   at two drive levels. Two seconds.
6. **`6 Receiver EVM`**: an NR signal through bench 2's chain, with a real mixer and a phase-noisy LO. EVM against input
   power, split into noise and distortion. About twenty seconds.
7. **`7 VCO Phase Noise`**: the LO itself, a varactor-tuned Colpitts VCO. Its tuning curve and phase noise. Bench 6's LO
   carries this VCO's phase noise. Two seconds.

Every bench sets `temp = 16.85`, which puts the whole design at 290 K, the temperature a noise figure is referred to. A
design that states no temperature runs at 300 K, and its passive parts are then a little noisier than their loss.
`circuitrf check` on this workspace reports that setting once per bench, as a warning, and nothing else.

The noise conventions, and which analysis computes what, are in *Reference ▸ Simulations ▸ Noise*.

## 1 LNA Noise Match

A Statz-model FET at −0.5 V gate and 3 V drain, with `Ri = 3 Ω`, `Tg = 300 K` and `Td = 3000 K`. That is the
two-temperature noise model: the intrinsic gate resistance is thermal at the gate temperature, and the output conductance
is thermal at the drain temperature. Beside it are the parts a real stage has:

- `Rg` (1 Ω) and `Rs` (0.5 Ω): extrinsic gate and source resistance, thermal at 290 K.
- `LS` (1 nH): source degeneration.
- `Rd` (10 Ω) and `Rstab` (82 Ω to ground): the stabilising network at the drain.
- `L1` (5.7 nH, 2.19 Ω, a Q of 40) and `C1` (0.56 pF): the input noise match. They were tuned so that the LNA's own
  optimum source is 50 Ω at 2.45 GHz.

`SP1` runs from 1.5 to 3.5 GHz with **Noise** ticked.

| At 2.45 GHz | |
|---|---|
| NF from 50 Ω | 0.618 dB |
| NFmin | the same, to 0.001 dB: Sopt is within 0.02 of the chart's centre |
| Gain | 11.2 dB |
| Rollett K | above 2 across the whole sweep |

**Which part sets the noise** (`NFpct`, each part's share of F − 1):

| Part | Share |
|---|---|
| `F1`, the transistor | 34.1 % |
| `Rstab` | 30.4 % |
| `L1`, the match inductor's loss | 28.7 % |
| `Rg` | 3.5 % |
| `Rs`, `Rd` | under 2 % each |

The stabilising resistor costs as much as the transistor. It sits at the drain, after only one stage of gain, so its
thermal noise is not divided down much. A resistor further down the chain would cost less, and a lossless stabilising
network would cost nothing.

**What the display shows** (`1 LNA Noise Match.cdd`):

- **Noise figure of the LNA**: NF from 50 Ω against NFmin. They touch at 2.45 GHz, where the marker reads 0.618 dB.
- **Noise and gain circles at 2.45 GHz**: the **Noise and Gain Circles** chart from the toolbar. Sopt sits at the
  centre. The three noise circles are 0.25, 0.5 and 1 dB above NFmin, and the three available-gain circles are 1, 2 and
  3 dB below the maximum. The source stability circle is off the chart, because the stage is unconditionally stable.
- **Which part sets the noise at 2.45 GHz**: `NFpct` as bars, one per part.
- **Gain and stability**: dB(S21) and Rollett's K.

## 2 Receiver Budget

The receiver as system blocks, at 2.45 GHz and 290 K:

| Block | Part | Gain | NF |
|---|---|---|---|
| LNA | `Amp`, `Gain=G_lna NF=NF_lna` | `G_lna`, swept 4 to 24 dB | 0.62 dB, bench 1's |
| RF filter | `Filter`, 3rd-order Butterworth, 2.35 to 2.55 GHz | −2 dB | 2 dB, its loss |
| Mixer stand-in | `Atten`, 7 dB | −7 dB | 7 dB, its loss |
| IF amplifier | `Amp`, `Gain=20 NF=4` | 20 dB | 4 dB |

An S-parameter run has no LO, so a mixer cannot convert in it. The stand-in is a 7 dB pad, the loss and the noise
figure of a passive mixer. Bench 3 has a real one, and bench 6 runs the chain with a `Mixer` block and an LO.

`MEAS1` writes Friis' formula for four matched stages as `NF_friis`:

```
F = F1 + (Lf − 1)/G1 + (Lm − 1)·Lf/G1 + (F4 − 1)·Lf·Lm/G1
```

The run's NF agrees with it to 1e-9 dB at every LNA gain.

| LNA gain | NF | LNA's share | IF amplifier's share |
|---|---|---|---|
| 16 dB | **2.12 dB** | 24.4 % | 47.9 % |
| 24 dB | 0.90 dB | 67.0 % | 20.9 % |

At 16 dB of LNA gain, the IF amplifier sets half the receiver's noise and the LNA a quarter. The receiver's gain there
is 27 dB. The LNA dominates only above about 20 dB of gain.

**What the display shows** (`2 Receiver Budget.cdd`):

- **Receiver noise figure against the LNA's gain**: the run's NF as dots on `NF_friis`. The marker is at 16 dB.
- **Each stage's share of F − 1**: `NFpct` for each block against the LNA's gain. The LNA's share climbs and crosses the
  IF amplifier's.
- **Receiver gain**.

## 3 Mixer Noise

A single Schottky-like diode (`Is = 5 nA`, `N = 1.05`, `Rs = 4 Ω`) to ground. The RF port reaches it through a series
resonator at 2.45 GHz, and the LO through one at 2.1 GHz. The IF, 350 MHz, comes out through a low-pass to port 2. The
LO is a P1Tone, `PLO`: an available power `Plo` behind 50 Ω.

`HB1` has **Noise** ticked with `NoiseSideband=1`: the signal comes in at f_IF + f_LO = 2.45 GHz. The image is at
f_LO − f_IF = 1.75 GHz. `SW1` sweeps the LO's available power `Plo` from −10 to +14 dBm.

| LO | SSB NF | DSB NF | Conversion gain |
|---|---|---|---|
| 0 dBm | 7.96 dB | 6.75 dB | −13.40 dB |
| **+6 dBm** | **4.73 dB** | **3.46 dB** | **−9.20 dB** |
| +10 dBm | 4.38 dB | 3.09 dB | −8.49 dB |

With too little LO the diode never switches, and the mixer does not convert: at −10 dBm the SSB noise figure is
above 39 dB.

SSB is 1.27 dB above DSB at +6 dBm, not 3 dB. SSB − DSB is 10·log10(1 + image gain / signal gain), and the RF resonator
reflects part of the image, so the image converts at about a third of the signal's gain.

At +6 dBm, the noise arrives through these input sidebands (`NFsb`):

| Sideband | Share |
|---|---|
| +1, the signal | 38.7 % |
| −1, the image | 17.2 % |

The diode's own noise is the remainder, and it cannot be split by sideband. By part (`NFpct`), the diode is 53.7 %, port
1's termination at its other sidebands (mostly the image) 27.7 %, and the two resonators' losses 9.4 % (`LR`) and
9.2 % (`LL`). **The diode sets half this mixer's noise**: it is noisy only while it conducts, and the LO switches it on
for part of every cycle.

The P1Tone is noiseless, as every source is in a harmonic-balance noise analysis: its 50 Ω is the LO's termination, not
a noise source. A real LO's own noise reaches the diode through the LO resonator, which passes 1.75 GHz and 2.45 GHz
almost as well as 2.1 GHz. To see what it costs, replace `PLO` by a `V_1Tone` behind a 50 Ω resistor: the resistor is
thermal, and its share of the noise is the LO port's.

**What the display shows** (`3 Mixer Noise.cdd`):

- **Mixer noise figure against LO drive**: SSB and DSB. The marker is at +6 dBm.
- **Conversion gain, RF to IF**.
- **Where the noise comes in**: `NFsb` as bars over the input sideband at +6 dBm.
- **Which part sets the noise**: `NFpct` as bars at +6 dBm.

## 4 LNA Under a Blocker

Bench 1's LNA, part for part, with port 1 a `P1Tone` putting a 2.3 GHz blocker of `Pblk` into it. `HB1` is a harmonic
balance at the blocker, with **Noise** ticked and `NoiseSideband=0`, reading the noise figure at 2.45 GHz. `SW1` sweeps
`Pblk` from −40 to +10 dBm.

| Blocker | NF at 2.45 GHz |
|---|---|
| −40 dBm | 0.618 dB, bench 1's S-parameter NF to 1e-4 dB |
| −2.5 dBm | 0.67 dB |
| +2.5 dBm | 1.62 dB |
| +10 dBm | 4.45 dB |

The blocker's own gain is 11.83 dB while it is small, and it is 1 dB compressed between +2.5 and +5 dBm. The noise
figure has risen by 1 dB by then. A blocker 10 dB below the LNA's compression point costs almost nothing. A blocker at
compression costs as much as the LNA's whole noise figure. The signal's gain at 2.45 GHz falls faster than the
blocker's own, which is desensitisation.

**What the display shows** (`4 LNA Under a Blocker.cdd`):

- **LNA noise figure against a 2.3 GHz blocker**. The marker is on the small-blocker end, bench 1's 0.618 dB.
- **Gain, blocker and signal**: the blocker's own gain (`Gblk_dB`) and the signal's at 2.45 GHz (`Gc`).

## 5 Noise Sourcepull

Bench 1's transistor stage without its input match: `Rg`, the FET, `Rs`, `LS`, `Rd` and `Rstab`. A source tuner `TS`
replaces the match and a load tuner `TL` holds 50 Ω. Each tuner carries its bias. `LP1` is a sourcepull with **Noise**
ticked: at every point of `source_grid.gam` (|Γ| = 0 to 0.9 in steps of 0.15, at 24 angles), it drives the stage from
−30 to 0 dBm and reads NF at every step.

| | At −30 dBm |
|---|---|
| Lowest NF | 0.39 dB, at Γs = 0.75∠15° |
| Highest gain | 11.50 dB, at Γs = 0.60∠15° |
| NF from 50 Ω | 0.97 dB |

At 0 dBm of drive, the lowest NF on the grid has risen to 0.53 dB.

Source degeneration (`LS`) put the noise and gain optima on the same angle, close together. The lowest NF is 0.23 dB
below bench 1's 0.618 dB. Most of that difference is the match's own loss, `L1`'s 28.7 % share in bench 1, so a
lower-loss inductor wins most of it back.

**What the display shows** (`5 Noise Sourcepull.cdd`):

- **NF and gain on the source plane, −30 dBm**: NF contours from 0.5 dB in 0.25 dB steps, and gain contours from 7 dB in
  1 dB steps.
- **NF on the source plane, −30 and 0 dBm**: the same NF contours at both drive levels. The drive shrinks the low-NF
  region.

## 6 Receiver EVM

Bench 2's chain in an envelope run, with a real mixer:

- `SRC`: a `MOD_Source` sending an NR carrier, 5 MHz, 15 kHz subcarriers, 64QAM, at 2.45 GHz. **Noisy** is ticked at
  290 K, so its 50 Ω adds the input noise a noise figure is referred to.
- `LNA`: `Gain=16 dB NF=0.62 IP3=5 dBm`, so it compresses.
- `FLT`: the RF filter.
- `MIX`: a `Mixer` block, −7 dB, `NF=7`.
- `VCO`: the LO, a P1Tone at 2101.2 MHz and 7 dBm. Its `PhaseNoise` is bench 7's VCO at 3.5 V, copied from that bench's
  phase-noise trace with **Copy as Phase-Noise Mask**. The envelope run gives it a fundamental of its own, because it is
  far from the signal's frequency.
- `FIF`: an IF filter at 349 MHz.
- `IFA`: the IF amplifier, 20 dB, `NF=4`.

The IProbe `Iif` is after the IF amplifier. Each carrier is measured where it arrives, its power, gain, ACLR and EVM
alike. Here that is the IF, 348.8 MHz (2.45 GHz − 2101.2 MHz). At −50 dBm the IF power is −24.0 dBm: the chain's
26.0 dB of gain (16 − 2 − 7 − 1 + 20). At −20 dBm the LNA has compressed it to 23.8 dB.

`ENV1` has **Noise** and **EvmSplit** ticked: each point runs twice, the second time with every noise source and the
phase-noise mask off. The EVM then splits into its noise part and its distortion part. `SW1` sweeps the input power `Pin`
from −95 to −20 dBm.

| Pin | EVM | Noise part | Distortion part | SNR |
|---|---|---|---|---|
| −95 dBm | 31.08 % | 31.08 % | 0.40 % | 10.1 dB |
| −80 dBm | 5.66 % | 5.65 % | 0.40 % | 25.0 dB |
| −65 dBm | 1.64 % | 1.60 % | 0.40 % | 36.0 dB |
| **−50 dBm** | **1.34 %** | 1.28 % | 0.40 % | 37.9 dB |
| −35 dBm | 1.53 % | 1.28 % | 0.83 % | 37.9 dB |
| −20 dBm | 11.76 % | 1.29 % | 11.69 % | 37.8 dB |

There are three regimes:

- **Below about −70 dBm, thermal noise.** EVM falls 1 dB for every dB of signal.
- **From about −60 to −35 dBm, the LO's phase noise.** The SNR stops at 37.9 dB, and stays there however strong the signal
  is: the LO's phase noise multiplies the signal, so it grows with it.
- **Above −35 dBm, distortion.** The LNA compresses.

The distortion part's floor, 0.40 %, is the NR signal's own EVM from its shaping, plus the filters' in-band ripple.

**What the display shows** (`6 Receiver EVM.cdd`):

- **EVM against input power**: EVM, its noise part and its distortion part, in dB. The marker is at −50 dBm.
- **Signal over the noise the run added**: the in-channel SNR, rising and then flat at the phase-noise floor.
- Three constellations. At −50 dBm the points are tight. At −20 dBm compression scatters them. At −95 dBm thermal noise
  blurs every point into a cloud.

## 7 VCO Phase Noise

A common-base Colpitts on the built-in BJT (`Kf = 1e-10`, so its base current has flicker noise), with a 3.2 nH tank.
A reverse-biased diode `DV`, coupled through 1 pF, tunes it from `Vtune` through 10 kΩ. `OSC1` is an oscillator analysis
with **Phase noise** ticked, from 100 Hz to 10 MHz off the carrier. `SW1` sweeps `Vtune` from 3 to 9 V.

| Vtune | Fosc | Kvco | PN at 100 kHz | Flicker corner |
|---|---|---|---|---|
| 3 V | 2092.7 MHz | 17.1 MHz/V | −106.6 dBc/Hz | 6.9 kHz |
| **3.5 V** | **2101.2 MHz** | 16.1 MHz/V | **−106.5 dBc/Hz** | under 10 Hz |
| 4 V | 2108.7 MHz | 14.3 MHz/V | −106.4 dBc/Hz | 3.2 kHz |
| 9 V | 2155.6 MHz | 6.6 MHz/V | −105.6 dBc/Hz | 42.3 kHz |

At 3.5 V the phase noise is −128.8 dBc/Hz at 1 MHz, 20 dB per decade, which is white noise turned into phase noise.
`PNpct` names the transistor (about 80 %), the tuning resistor `Rt` and the tank's load `RL`. `Rt` matters because its
thermal noise is a voltage on the varactor, and the varactor turns it straight into frequency.

The flicker corner, below which phase noise falls at 30 dB per decade, moves with tuning. At 3.5 V it almost vanishes,
because there the oscillation's frequency is insensitive to the transistor's flicker current. That makes 3.5 V the
quietest place to run this VCO close in, which is why bench 6's LO is this curve.

Copying it: in the display, right-click the 3.5 V `PN` trace's legend entry and choose **Copy as Phase-Noise Mask**. The
clipboard then holds `PhaseNoise="100Hz:-49.20, …"`, which is what `VCO`'s `PhaseNoise` in bench 6 holds. The example's
test checks that bench 6's mask is this bench's result, point for point.

**What the display shows** (`7 VCO Phase Noise.cdd`):

- **Tuning curve** and **Tuning sensitivity**: Fosc in MHz and Kvco = d(Fosc)/d(Vtune).
- **Phase noise** at 3.5, 6 and 9 V on a log offset axis. The marker is on 3.5 V at 100 kHz. The 6 V and 9 V curves bend
  up below their flicker corners. The 3.5 V curve does not.
- **Flicker corner against tuning**, with its null at 3.5 V.
- **Which part sets the phase noise, 3.5 V**: `PNpct` as bars at 100 kHz.

## The parts

Every part in this workspace is a circuitRF built-in with typed values. None is a model of a real product.
