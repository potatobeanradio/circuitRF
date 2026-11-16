# Phased Array

**Run order:** the first three benches run in any order. Run **4 PA Loadpull** before **5 Brute Force** and **6 Envelope**:
their displays draw the active reflections over its contours. Nothing here needs an EM run first; the two sub-array results
the benches read ship in `results/`.

A phased array of the *Patch Antenna* example's inset-fed patch at 5.9 GHz, built from **one EM simulation of a 5 × 5
sub-array** and steered, tapered, received with and driven by power amplifiers from schematics. The same patch fed the
same way, at a 25.86 mm pitch (0.508 λ at 5.9 GHz), in three EM views:

| Cell | What it is |
|---|---|
| `Patch Element` | One patch, with the edge port on its 5 mm feed line |
| `Patch 1x4` | Four in a row along x: the brute-force benches' array, solved whole |
| `Patch 5x5` | The 5 × 5 sub-array the large arrays are built from |

The **Phased Antenna Array** component (System) reads a saved EM result and builds an array of any size up to 64 × 64
from it. From the 5 × 5 record it takes each element's embedded pattern by position (corner, edge, interior) and the coupling
between elements by their offset, out to four elements in each direction; from the 1 × 4 record, solved whole, it uses every
port's own pattern and the full coupling. The component's reference page, **Phased Antenna Array** in the user docs, says how
far each of these is from a whole-array solve.

## What was traded for speed

The shipped results are **reduced**: one frequency (5.9 GHz) and a 5° pattern grid, sampled from the full runs, never solved
again, so they open at once and are about 2 MB together. The EM setups themselves are coarser than the *Patch Antenna*
example's: 12 cells a wavelength with the edge mesh off (the 5 × 5 sub-array is 13,300 unknowns at that), which moves the
patch's resonance to about 5.88 GHz. That is why everything here is at 5.9 GHz rather than 5.8.

Simulating `Patch 5x5/em/Patch 5x5.cem` again writes the full result (5.8, 5.9 and 6.0 GHz, 1° grid) over the shipped one:
about 15 minutes and 3.2 GB of memory. The benches then read it unchanged. The 5° grid is coarse for an array's beam, but the
array never reads its beam off it: the readouts are formed on a fine grid around the beam with the element pattern
interpolated, and so are the pattern cuts every fields analysis on an array publishes (`farfield.CutU`, in the planes `cuts=`
names, φ = 0 and 90° by default, at a tenth of the beamwidth or finer), which is what the polar plots in the displays draw. Only
the overview, `farfield.U` and the 3D view, is on the record's grid.

## 1 Transmit

A 16 × 16 array in Feed mode: one pin, `F`, and a beamformer inside that splits the feed 256 ways. Each element's weight is
`Amp=taylor(-25,4)` (a −25 dB Taylor taper) and `Phase=steer(theta0,phi0,f0)`, rounded to 6-bit phase shifters
(`PhaseBits=6`), and each element is driven through its own copy of the `TxChannel` cell: a 12 dB driver and a 20 dB power
amplifier with 14 dB output return loss. Because every channel is identical, the run solves the channel once and closes the
coupled array with one sparse factorisation that a change of steering only back-substitutes. `explain` says so ("Fast path").

`F1` is the array at `theta0` (30°); `SW1` sweeps `theta0` from 0 to 60° around `F2`. At 30°:

| Measure | Value |
|---|---|
| Realized gain from the feed (`Gr_dBi`) | 53.24 dBi |
| Half-power beamwidth, elevation / azimuth | 8.57° / 7.49° |
| First sidelobe (`SllDb`) | −24.66 dB |
| Scan loss | 0.43 dB |
| Grating-lobe margin | 44.86° |
| Worst active VSWR | 1.59 |

The realized gain counts the channels' 32 dB, because it is per watt available at the feed. Against `theta0` it falls from
53.90 dBi at boresight to 53.24 at 30° and 50.04 at 60°. The taper is attenuation after an equal split, so the power it takes
from the edge elements is dissipated: against uniform weights it costs 6.2 dB of realized gain at boresight (60.14 dBi), for
sidelobes at −24 dB instead of −12.6.

**1 Transmit.cdd** draws the cut in the steering plane (`F2.farfield.CutU` at φ = 0, −90 to +90°) at each `theta0`, the realized gain against `theta0`, and every
element's |Γ_act| at 0, 30 and 60° (the edges and corners differ most, because their neighbours differ). Measures that read the
pattern (`SllDb(F1)` and the rest) are one number per run, so they read `F1`, which no sweep wraps.

**Open it in the Phased Array tool** (Tools ▸ Phased Array, with the schematic active): the aperture coloured by amplitude,
phase or |Γ_act|, the beam in direction cosines with the grating-lobe positions, the cuts with the isolated array factor beneath,
and the readouts. Drag the beam to steer: it writes `theta0` and `phi0` into the VAR block as one undo step. Click an element to
switch it off. The Scan maps tab shows realized gain and worst |Γ_act| over every scan direction.

## 2 Receive

An 8 × 8 Feed-mode array used as a receiver: each element through an `RxChannel` (an LNA of 20 dB, NF 1.5 dB, OIP3 +10 dBm, then
a 3 dB attenuator), the beamformer, a mixer with a 5.5 GHz LO and a 400 MHz IF filter. It looks at a sky of 5 K overhead rising to
285 K at the horizon (`Tsky=5+280*exp(-el/0.0873)`), and a signal of −90 dBm/m² arrives from the direction it is steered to.

`NF1` is the noise at the feed and `F1` the pattern from the same port; the receive measures read both. At boresight:

| Measure | Value |
|---|---|
| Antenna temperature `AntennaTempK(F1)` | 98.6 K |
| System temperature `SystemTempK(F1, NF1)` | 242.2 K |
| G/T `GoverTDbK(F1, NF1)` | −2.70 dB/K |
| SNR in 1 MHz `SnrDb(F1, NF1, 1, 1e6)` | 9.02 dB |
| Signal at the feed / at the IF output (HB1) | −97.58 dBm / −105.08 dBm |

**G/T against scan.** `theta0` steers the receive beam and moves the signal with it. G/T needs the noise analysis and the fields
analysis at the same steering, so it is read one steering at a time: move `theta0` on a Tuning slider and the measures follow.
From the command line, `circuitrf fields "2 Receive/schematic/2 Receive.csch" --set theta0=30` gives the same:

| `theta0` | G/T | SNR in 1 MHz |
|---|---|---|
| 0° | −2.70 dB/K | 9.02 dB |
| 15° | −3.09 dB/K | 8.62 dB |
| 30° | −3.27 dB/K | 8.45 dB |
| 45° | −3.58 dB/K | 8.06 dB |
| 60° | −5.26 dB/K | 5.59 dB |

The system temperature hardly moves (236 to 244 K), so what G/T loses with scan is gain: the beam's own scan loss, and at 60°
the element pattern falling away.

## 3 Receive Interferer

The same front end with the feed loaded by 50 Ω (what the mixer's RF port presents), and two equal interferers at 5.92 and
5.94 GHz, −40 dBm/m² each, arriving from `thetaI` in the φ = 0 plane. Their third-order product falls at 5.90 GHz, on the
signal. `SW1` sweeps `thetaI` over 0, 10, 20 and 30°.

| `thetaI` | Interferer at one LNA input | At the feed | IM3 at the feed |
|---|---|---|---|
| 0° | −73.33 dBm | −44.09 dBm | −170.5 dBm |
| 10° | −73.54 dBm | −50.28 dBm | −177.6 dBm |
| 20° | −73.80 dBm | −77.40 dBm | −199.2 dBm |
| 30° | −73.69 dBm | −81.71 dBm | −176.8 dBm |

**The channels see the element's pattern, not the array's.** At every angle each LNA receives the interferers within 0.5 dB of
the same level, and compresses and intermodulates by that. The beamformer suppresses them only afterwards, by the array
pattern at `thetaI`: 20° sits near a null, 30° on a sidelobe. **3 Receive Interferer.cdd** plots the three against `thetaI`.

## 4 PA Loadpull

The power amplifier the brute-force benches use, alone between a source tuner and a load tuner: a loadpull at 5.9 GHz to 3 dB
compression over a polar grid out to |Γ| = 0.55, where the array's active reflections fall (`load_grid.gam`, offset 15° from the
real axis). The amplifier is the *Harmonic Balance* example's 48 V FET (the same `FET` cell) with its matching retuned for 5.9 GHz.
**4 PA Loadpull.cdd** draws its Pout and PAE contours.

## 5 Brute Force

Four of those amplifiers (the `PA` cell) on the four ports of `Patch 1x4`, in one harmonic-balance run. The array is the
Phased Antenna Array in Elements mode, one pin per element, on a record solved whole, so it uses every port's own pattern and
the full coupling. Each amplifier is driven from its own 1-Tone source through a Phase Shifter written
`Phase=-steer(theta0,0,f0,x1,0)`: a Phase Shifter delays by its `Phase`, and `steer()` is the phase a weight adds, so each writes
its negative. `HB1` is swept over `theta0` (0, 15, 30, 45°) and `Pin` (−20, 16, 22 dBm); `F1` (`waves=HB1`) forms the pattern from
the harmonic-balance solution, so each amplifier's compression shows in the beam. The whole run takes under a second.

At 22 dBm each amplifier gives about 40 dBm. At boresight the inner two run at 68 % PAE and the outer two at 63 %; steered to 45°
the inner two fall to 37 % and the outer two to 44 and 51 %, because each one's active load moves differently.

**5 Brute Force.cdd** draws each element's Γ_act against `theta0` on a Smith chart, the same loci over the Pout and PAE contours
of *4 PA Loadpull* (which elements leave the high-efficiency region at which angle), the φ = 0 cut (`F1.farfield.CutU`) at each `theta0`, the peak realized
gain, and each amplifier's Pout and PAE against `theta0`. In the Phased Array tool, colour the aperture by PA PAE: the tool reads
the measures named `PAE1` … `PAE4`.

## 6 Envelope

The same four amplifiers and the same array driven by **one modulated signal**, as a real beamformer drives them: an NR 20 MHz
64QAM carrier at 5.9 GHz from one `MOD_Source`, through an ideal 4-way Combiner into the Phase Shifters. `ENV1` is an envelope run
of 40 µs, swept over `Pavg` (−8 and 22 dBm) and `theta0` (0 and 30°). `F1` (`waves=ENV1 dirs="peak;0,0;-30,0;60,0"`) forms the
radiated envelope in those four directions, with the spectrum, ACLR and EVM of what each direction receives, each element's
active reflection over the record, and the time-averaged pattern. It takes about 20 s.

Steered to 30° at 22 dBm, the beam's peak (at 25°) reads ACPR1 −26.3 dBc and EVM 7.6 %; at 0° and −30°, off the beam, −20 dBc and
19 %. **The distortion does not form the beam**: it leaves each element with the phase that element's own compression gives it, not
the steering phase, so off the beam, where the carrier is weak, it is relatively stronger.
At −8 dBm every direction reads the source's own EVM (0.2 to 0.4 %).

**6 Envelope.cdd** draws each element's Γ_act over the record as a cloud, the clouds over the loadpull contours, the
time-averaged φ = 0 cut (`F1.farfield.CutU`) at each drive, the radiated spectrum in each direction, and ACPR1 and EVM against drive per direction.

The result is about 34 MB (four sweep points of 19,662 steps), so it is not something to keep: run it again rather than saving it.
