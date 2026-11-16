---
title: Phased Antenna Array: an array from one EM run
slug: reference/phased-array.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Phased Antenna Array
lede: An array of up to 64 × 64 elements built from the EM result of one element or a small sub-array: steered, tapered, quantised, driven or received through a circuit per element, simulated by brute force in harmonic balance when it is small, and fast enough to steer by hand.
keywords: phased array, antenna array, array factor, embedded element pattern, active element pattern, mutual coupling, active reflection, active reflection coefficient, Gamma active, scan, scan loss, scan blindness, beam steering, steer, steerDelay, true time delay, squint, taper, Taylor, Chebyshev, cosine taper, taylor, chebyshev, cosq, lambda, sidelobe, grating lobe, beamwidth, pointing error, phase shifter, phase bits, attenuator step, quantisation lobes, beamformer, channel cell, Feed mode, Elements mode, PhasedArray, sub-array, position classes, isolated, embedded, exact, receive, G/T, antenna temperature, system temperature, sky temperature, Tsky, interferer, Rx, brute force, harmonic balance, waves=, envelope, dirs=, loadpull, Phased Array tool, scan map, fast path
---

A phased array is many copies of one antenna, each driven with its own amplitude and phase. Its pattern is the sum of the
elements' patterns, and the circuit behind each element is loaded by its neighbours: the reflection a power amplifier sees
changes as the beam is steered. The **Phased Antenna Array** component (System category, `.cnl` type `PhasedArray`) does
both from **one** EM run — of one element, of a small sub-array, or of the whole array when it is small — so an array of
thousands of elements is designed in a schematic, steered with a slider, swept, optimized and simulated in harmonic balance,
with no EM run beyond the first.

The worked example is **Phased Array** (*File ▸ New ▸ Examples*): a 16 × 16 transmitter, an 8 × 8 receiver, and four power
amplifiers on a 1 × 4 array, all from shipped EM results of a 5.9 GHz patch. Every number on this page comes from it.

## 1. Three levels of fidelity {#levels}

The component reads a saved EM result (a `.cemr`, as an [EM Field Data](cosimulation.html#pattern) block does) and builds
the array at one of three levels. `Level` left at `auto` picks it from the record:

| Level | The record | Each element's pattern | Coupling between elements |
|---|---|---|---|
| `isolated` | One element | The element's own, solved alone, at every position | None: each element reflects its own S₁₁ |
| `embedded` | A sub-array (5 × 5 recommended) | The sub-array's centre, edge or corner element's, by the element's position class (`Patterns=classes`, the default), or the centre's alone (`Patterns=centre`) | The sub-array's, by element offset, out to the offsets it measured |
| `exact` | The whole array, solved with a port per element | Each port's own | The record's own |

The array's pattern is the sum of each element's pattern times its wave, with the element's position supplying the phase
(the array factor). At `exact` that sum is exactly what a whole-array EM run would give. The other two are approximations,
and how good they are was measured: the same patch array was solved whole at several sizes, and each level was compared with
it, scanned to 0, 30, 45 and 60° in both principal planes and the diagonal, at 5.9 GHz.

| Predicting | From | Level | Worst peak realized-gain error | Worst RMS pattern error (within 20 dB of the peak) | Worst \|ΔΓ_act\| of any element |
|---|---|---|---|---|---|
| 6 × 6 | one element | `isolated` | 3.61 dB | 4.39 dB | 0.50 |
| 6 × 6 | 3 × 3 | `embedded` | 1.99 dB | 3.32 dB | 0.19 |
| 6 × 6 | 5 × 5 | `embedded`, centre pattern only | 1.11 dB | 3.51 dB | 0.095 |
| 6 × 6 | 5 × 5 | `embedded`, position classes | **0.75 dB** | **2.65 dB** | **0.11** |
| 1 × 16 | 1 × 9 | `embedded`, position classes | 0.15 dB | 0.54 dB | 0.027 |
| 1 × 16 | one element | `isolated` (1 × 9 shown) | 2.83 dB | 2.46 dB | 0.45 |

**Use a 5 × 5 sub-array.** It is the smallest that sees an interior element's whole neighbourhood; a 3 × 3 does not, and an
isolated element misses the coupling entirely — its active reflections are wrong by up to 0.5, which is the difference
between an amplifier at its best load and one far from it. The RMS active-reflection error of the 5 × 5 is 0.04 or less at
every scan. Elements farther apart than the sub-array measured are taken as uncoupled, and the run says so
("Coupling extended from the 5 × 5 sub-array: offsets to ±4 rows and ±4 columns…").

**What the measurements leave out.** Every record in them is a planar run, whose ground plane and laminate extend to
infinity. A real board ends, and its edge diffracts; the array's lower hemisphere and the edge elements' patterns change with
it. That was not measured. A record from a 3D run with a drawn, finite board carries it, at a 3D run's cost.

**The pitch.** `dx` and `dy` may be anything, and the array factor is exact at any pitch. But a sub-array's patterns and
coupling were solved at one pitch, so at another they are wrong by an amount that grows with the difference; the run warns
when the pitch differs from the record's by more than 0.02 λ (a move of 0.04 λ changed the nearest neighbours' coupling by
about 0.04). An `isolated` record has no pitch and is never warned about.

**The power balance.** At `embedded` and `isolated` the radiated power and the power the elements accept are not quite
consistent. `PowerBalance(F1)` is their ratio. On a planar record even the whole-array run's ratio is its efficiency
(0.41–0.88 on these records, surface-wave loss included), so the run stays quiet within 0.1 of the centre element's own
efficiency, warns beyond it, and always warns above 1, which no passive array can reach.

## 2. Feed mode and Elements mode {#modes}

{{symbol: phased-array}}

| `Mode` | Pins | Use it for |
|---|---|---|
| `feed` (the default) | One, `F`: the beamformer is inside | Any size up to 64 × 64. Steering and tapering by the weights, an identical circuit per element in the channel cell, the receive side, the Phased Array tool |
| `elements` | One per element, numbered row-major from the top left seen from boresight (at most 64) | Small arrays driven by the circuit: an amplifier per element in harmonic balance, a hand-drawn feed network |

**In Feed mode** the feed is split equally N ways and element n is driven through its weight `Amp·e^(jφ)`, delayed by
`Delay`, rounded to `PhaseBits` and `AttenStep`, and then through its own copy of the `Channel` cell if one is named. The
split is ideal: matched, and each path isolated from the others. A taper is attenuation after an equal split, as an
attenuator per channel is, so the power it takes from the edge elements is dissipated, not passed to the centre: the 16 × 16
example's −25 dB Taylor taper lowers its realized gain from the feed by 6.2 dB against uniform weights (53.90 against
60.14 dBi at boresight), for sidelobes at −24 dB instead of −12.6.

**In Elements mode** the weights are not used: a weight after an amplifier would rotate the load the amplifier sees, which no
real phase shifter does. Steer with the sources' phases or a [Phase Shifter](components.html#phaseshifter) per element.
Changing `Rows` or `Cols` in Elements mode changes the pin count, so a sweep of either is refused there; adding rows keeps
every existing pin where it is.

The symbol draws the aperture as seen from the front (a larger array as its top-left 8 × 8 and its size), elements switched
off as hollow squares, and in Feed mode a beam tilted by θ0 with `θ0 30°  φ0 0°` beside it when `Phase` is a `steer()`.

## 3. Parameters {#parameters}

{{table: components/PhasedArray}}

**What can be swept.** Every numeric parameter is an expression, so a VAR it reads is swept by a parametric sweep, moved by a
tuning slider, drawn by yield and set by the optimizer: `dx`, `dy`, `Amp`, `Phase`, `Delay` and their `[n]` overrides,
`PhaseBits`, `AttenStep`, `Z0`, `Tsky`, `Tground` and every `Rx…[k]`. `Rows` and `Cols` sweep in Feed mode only. The words —
`Record`, `Mode`, `Level`, `Patterns`, `Lattice`, `Coupling`, `Channel` — and the `Off` list are not swept.

**Lengths.** `dx` and `dy` take any length unit circuitRF knows (`mm`, `mil`, `um`, `in`…) or an expression such as
`0.5*lambda(f0)`; a bare number takes the unit shown beside the field, as every length does.

## 4. Steering and tapering {#weights}

`Amp`, `Phase` and `Delay` are evaluated **once per element**, with five names bound to that element:

| Name | Value |
|---|---|
| `n` | Element number, 1 … N, row-major from the top left seen from boresight |
| `row`, `col` | 1-based |
| `x`, `y` | Position from the array's centre, in metres; columns run along x, rows down y |

and five functions that read them:

| Function | Value |
|---|---|
| `steer(theta0, phi0, f0)` | The phase in degrees that points a phase-shifter beam to (θ0, φ0) at f0 |
| `steerDelay(theta0, phi0)` | The delay in seconds that points a true-time-delay beam, at every frequency |
| `taylor(sll, nbar)` | A Taylor taper of sidelobe level `sll` dB over the rows and the columns, 1 at the peak |
| `chebyshev(sll)` | A Chebyshev taper |
| `cosq(q)` | A cosine-to-the-q taper |

**The angles are angles**: `30 deg`, or a VAR whose unit column says `deg`. A bare number is radians, as it is to every
trigonometric function, so `theta0 = 30` with no unit steers to 30 radians. Write `theta0 = 30 deg`.

The example's transmitter, steered and tapered:

```
theta0 = 30 deg
phi0   = 0 deg
f0     = 5.9 GHz
PhasedArray:PA1 f Record="results/Patch 5x5.cemr" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm Mode=feed Rows=16 Cols=16 dx=25.86 mm dy=25.86 mm Phase=steer(theta0,phi0,f0) Amp=taylor(-25,4) PhaseBits=6 Channel=TxChannel
analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1
analysis F2 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1
analysis SW1 type=parametric_sweep Var=theta0 Values=0,10,20,30,40,50,60 Unit=deg Inner=F2
```

(`RecordRows`…`RecordDy` give the sub-array's lattice; a record written since the array arrived stores its ports' positions
and needs none of them.) At 30° this gives a 8.57° × 7.49° beam, a first sidelobe of −24.66 dB and 0.43 dB of scan loss.
`Phase[n]`, `Amp[n]` and `Delay[n]` set one element; `Off=3,17,200` switches elements off (weight zero, terminated in Z0).

**Phase shifters squint; delays do not.** `steer()` is a phase, right at f0 only, so away from f0 the beam points elsewhere;
`steerDelay()` points the same way at every frequency. The [Phase Shifter](components.html#phaseshifter) block delays by its
`Phase`, the opposite sign to a weight's, so a channel that steers through one writes `Phase=-steer(…)`.

**What a fields analysis on the array publishes.** The pattern (`farfield`), as for any [radiation pattern](cosimulation.html#pattern);
its plane cuts, `farfield.CutU`, `CutEtheta` and `CutEphi`, over `freq`, `cut` (the plane's φ, degrees), `angle` and `port`; and
an `array` group over the element axis: `a`, `b`, `GammaActive`, `Pacc`, `w`, `wQuantised`, `row`, `col`, `x`, `y`. The
measurements:

| Measurement | Value |
|---|---|
| `RealizedGainDbi(F1)`, `GainDbi(F1)`, `DirectivityDbi(F1)` | As for any pattern; realized gain is per watt available at the driven port, so it counts any gain in the channels |
| `HpbwDeg(F1, "elevation")`, `HpbwDeg(F1, "azimuth")` | Half-power beamwidth in the two principal planes through the peak, degrees |
| `SllDb(F1)` | First sidelobe level below the peak, dB |
| `PointingErrorDeg(F1, θ, φ)` | The angle from (θ, φ) to the peak, both numbers of degrees. A VAR in `deg` reads as radians in a measure, so write `PointingErrorDeg(F1, theta0*180/pi, phi0*180/pi)` |
| `ScanLossDb(F1)` | How much below the boresight gain the scanned peak is |
| `GratingMarginDeg(F1)` | How far the nearest grating lobe is from entering visible space; negative once one has |
| `PowerBalance(F1)` | Radiated over accepted power ([§1](#levels)) |
| `ActiveVswrMax(F1)`, `ActiveGamma(F1, n)` | The worst element's active VSWR, and element n's active reflection (complex) |
| `BeamNfDb(F1)` | The noise figure from the driven port to the beam's peak |

**Beamwidth and sidelobes are never read off the record's grid.** A 64 × 64 array's beam is 1.6° wide, so a 1° grid puts one
sample across it and would read its sidelobe at −16.5 dB where it is −13.2. The cuts and these metrics are formed on a fine
grid around the beam, the element pattern interpolated (it is one element's, smooth over a beamwidth) and the array factor
summed exactly there. Only the overview — `farfield.U`, `Etheta` and `Ephi`, and the 3D view — is on the record's grid, and
the run says so when that grid is coarser than a tenth of the beam.

**Plot the cuts, not the overview.** `cuts=` names the planes (degrees, comma-separated; `0,90` by default, the lattice's two
principal planes). Each cut runs through boresight from −90° to +90°: a positive angle t is the direction (θ = t, φ), a negative
one (θ = −t, φ + 180°), so one trace on a polar plot is the whole plane and a family over `theta0` overlays on it. The step is a
tenth of the array's beamwidth or finer (never finer than 0.05°, so at most 3,601 samples a plane) and the record's step divides
by it, so every record direction in the plane is a sample; the run says the step it took. Pin `cut` and keep `angle` as X:

```
analysis F2 type=fields block=PA1 freq=5.9 freqUnit=GHz cuts=0,45,90
```

**A sweep and the measurements.** Each of these is one number per run. Under a parametric sweep they are refused with a sentence
naming the cube to plot instead (`F1.farfield.RealizedGainDbi` over `theta0`, `F1.array.GammaActive` over `theta0`). The
example keeps an unswept `F1` for its measures and sweeps a second fields analysis, `F2`.

## 5. The channel cell {#channel}

`Channel=TxChannel` names a cell with two pins — its first towards the beamformer, its second towards the element — and the
array places one copy of it per element as `PA1.ch[1]`, `PA1.ch[2]`…, an ordinary hierarchical instance. Draw it as any cell
(*File ▸ New Cell*, a Pin for each side). Its parameters may read `n`, `row`, `col`, `x` and `y` too, to give one element a
different channel; a channel that reads none of them is **identical**, which the run says, and which is what makes it fast
([§9](#speed)). An element switched off has its identical channel left out.

## 6. Receiving {#receive}

A plane wave arriving at the array is `RxTheta[k]`, `RxPhi[k]` (degrees), `RxPd[k]` (power density, dBm/m²), `RxFreq[k]` and
`RxPol[k]` (`theta`, `phi`, `rhcp`, `lhcp`, or an angle from θ̂ towards φ̂). Each element receives it through its own pattern,
by reciprocity, and in harmonic balance and envelope runs it is a tone, as a 1-Tone source's is, so a signal, an interferer and
an LO pass through the channels together.

**The array's own noise is what its patterns see.** `Tsky` is the brightness above the horizon — a temperature, or an
expression of the elevation `el` in radians, such as `5+280*exp(-el/0.0873)` (5 K overhead, 285 K at the horizon) — and
`Tground` below it. Left blank, both are the array's own temperature, which is the noise any passive block has. The noise is
correlated between elements, and the correlation is computed from the patterns with the sky's brightness folded in.

With a fields analysis `F1` driven from the receive port and an S-parameter analysis `NF1` with `Noise=yes`:

| Measurement | Value |
|---|---|
| `AntennaTempK(F1)` | The antenna temperature at the port: what the elements' noise delivers, over what a 1 K passive array would |
| `SystemTempK(F1, NF1)` | Every noise referred to the elements' terminals: the antenna temperature plus the receiver's |
| `GoverTDbK(F1, NF1)` | G/T, dB/K |
| `SnrDb(F1, NF1, k, B)` | The SNR of incident wave k in bandwidth B (Hz) |
| `NullDepthDb(F1, theta, phi)` | The receive pattern in a direction, below its peak, dB |

All of them work through channels that pass signal one way only (an LNA), because they use the array's receive weights, not
its transmit pattern. The example's 8 × 8 receiver at boresight: 98.6 K, 242.2 K, −2.70 dB/K and 9.02 dB in 1 MHz. Steered to
60° its G/T is −5.26 dB/K: the system temperature hardly moves, the gain does.

**An interferer reaches every channel through the element's pattern, not the array's.** The channels sit between the elements
and the beamformer, so an interferer in a sidelobe arrives at each LNA almost at full strength, compresses and intermodulates
it there, and is suppressed by the array factor only afterwards. In the example, two interferers from 30° arrive at each LNA at
−73.7 dBm and at the feed at −81.7 dBm, against −44.1 dBm from boresight. That is why a phased receiver's channel linearity
matters.

## 7. Brute force: an amplifier per element in harmonic balance {#brute-force}

When the array is small enough to solve whole, the most direct simulation is also the most faithful: a record with a port per
element, an Elements-mode array (or the EM Field Data block itself), and one power amplifier per port, all in one
harmonic-balance run. Each amplifier is then loaded by its neighbours exactly as the coupling says.

```
analysis HB1 type=hb Tone="f0" ToneUnit=GHz MaxHarm=5
analysis SW_Pin type=parametric_sweep Var=Pin Values=-20,16,22 Inner=HB1
analysis SW_theta type=parametric_sweep Var=theta0 Values=0,15,30,45 Unit=deg Inner=SW_Pin
analysis F1 type=fields block=ARR waves=HB1
```

`waves=HB1` forms the pattern from the harmonic-balance solution rather than from a linear solve, so each amplifier's
compression shows in the beam; `harmonic=k` takes the k-th harmonic instead, when the record holds that frequency. A sweep
around HB1 gives a pattern at every point. The `array` group adds `Pdel`, the watts delivered into each element.

**The active reflections over the loadpull contours.** Plot `F1.array.GammaActive` on a Smith chart with `element` as the family
and `theta0` along the trace: one locus per element. Add the same trace to a plot of the amplifier's loadpull contours, and the
picture says which elements leave the high-efficiency region at which angle. In the example, at 22 dBm and 45°, the two inner
amplifiers' PAE falls from 68 % to 37 % while the outer two fall to 44 % and 51 %.

{{ui: phased-array-bruteforce-display}}

**Under a modulated signal.** `waves=` also takes an envelope analysis, and `dirs=` names up to 16 directions (`peak`, the
time-averaged pattern's maximum, by default; `theta,phi` pairs in degrees, separated by `;`). Each direction's radiated
envelope gets the spectrum, ACLR, EVM, PAPR and CCDF the [envelope analysis](simulations.html#envelope) gives a probe; each
element's active reflection is a cloud over the record; `farfield.U` is the time-averaged pattern and `farfield.CutU` its
cuts. Steered to 30° at 22 dBm,
the example's beam reads ACPR1 −26.3 dBc and EVM 7.6 %, and the directions off the beam −20 dBc and 19 %: the distortion leaves
each element with the phase its own compression gives it, not the steering, so it does not form the beam.

## 8. The example's Transmit bench {#transmit}

{{ui: phased-array-transmit-display}}

The 16 × 16 array's cut in the steering plane (`F2.farfield.CutU`, φ = 0, at a step of 0.556° against the record's 5°) at
each `theta0`, its realized gain at the peak against `theta0` (53.90 dBi at
boresight, 50.04 at 60°, counting the channels' 32 dB), and every element's active reflection at 0, 30 and 60°: the edge and
corner elements differ from the interior, because their neighbours do. The worst element's active VSWR at 30° is 1.59.

## 9. Speed {#speed}

A change of steering on a 4096-element array with a channel costs about 25 ms. Three things make that possible, and `explain
--analysis` says which applies, in a line that starts "Fast path:" or "Full solve:":

- **No channel, or one that does not reflect:** the beamformer isolates every path, so nothing returns to another element and
  nothing is solved; a change of weights costs the pattern sum.
- **An identical reflecting channel on a coupled array:** the channel is solved once per frequency as a two-port, and the coupled
  array is closed with one sparse factorisation kept while only the weights change — a change of steering, taper, bits or the
  elements switched off is one back-substitution. Elements switched off are an update to the kept factorisation.
- **A move that reaches only the weights** — a VAR read only by `Amp`, `Phase`, `Delay` or measurements — does not elaborate the
  bench again either: the last elaborated circuit is re-weighed.

**What takes a bench off the fast path**, each named by `explain`: a channel that differs per element (it reads `n`, `row`,
`col`, `x` or `y`, and `explain` names the parameter that does), a nonlinear part anywhere in the circuit (an amplifier whose
`IP3` is not 200 counts: a linear analysis linearises it about an operating point the whole circuit sets), a complex `Z0`, a
channel wired to a net outside itself, or a circuit wired inside the array. Off the fast path every evaluation solves the circuit
as elaborated; harmonic balance of thousands of nonlinear channels is not interactive and is not presented as such.

**Dragging a slider off the fast path** shows the pattern with the circuit held at its last solve, labelled *estimated* in the
Tuning panel's time readout, and solves when released ([Tuning](tuning.html)).

## 10. The Phased Array tool {#tool}

*Tools ▸ Phased Array* opens on the selected array of the active schematic (or asks which). It is a view of that one
instance, not a document: every edit it makes is an edit of the schematic, with undo there, so the two cannot disagree. *File ▸
New ▸ Phased Array* makes a cell with a Port, an 8 × 8 Feed-mode array at half a wavelength steered by `steer(theta0,phi0,f0)`,
and a fields analysis, and opens the tool on it.

{{ui: phased-array-tool-beam}}

**Beam.** The aperture as built, each element coloured by a chosen quantity: amplitude, phase, |Γ_act|, delivered power, and —
from measurements named for an element, `Pout3_dBm`, `PAE3`, `NF3` — the amplifier's output power and PAE or the channel's noise
figure. Click an element to switch it off (it writes `Off`); hover for its numbers. Beside it, the realized-gain pattern in
direction cosines (u, v) with the visible circle, the scan-limit circle and the grating-lobe positions, the beam marked; **drag
the beam to steer** when the weights are a `steer()`, which writes `theta0` and `phi0` into the VAR block as one undo step on
release. Below, the two principal cuts, co- and cross-polar, rectangular or polar, with the isolated array factor dimmed beneath,
and the readouts: directivity, gain, realized gain, EIRP (or G/T when the bench has a `GoverTDbK` measurement), beamwidth in two
planes, sidelobe level, pointing error, scan loss, grating-lobe margin, worst |Γ_act| and power balance. Directivity here
integrates the (u, v) map, so on a coarse record it can differ from the run's own `farfield.DirectivityDbi`, which integrates the
record's grid.

{{ui: phased-array-tool-gamma}}

**Scan maps.** Realized gain and the worst element's |Γ_act| over every scan direction to the scan limit, computed in the
background one steering at a time through the fast path, and started again after any edit. A scan blindness shows as a dark
ring. A bench off the fast path is refused here, with the reason.

{{ui: phased-array-tool-scan}}

**Tapers and bits.** The taper family and level, the phase-shifter bits and the attenuator step; *Apply* writes them to `Amp`,
`PhaseBits` and `AttenStep`.

{{ui: phased-array-tool-tapers}}

**Failures.** Elements switched off at random, in memory, over many trials: the spread of gain and sidelobe level.

**Frequency.** Where the beam points across ±20 % of the frequency, steered by phase shifters and by true time delays.

{{ui: phased-array-tool-frequency}}

A frame is one evaluation of the fields analysis. With the fast path it takes about 25 ms at 1024 elements and 65 ms at 4096 in a
Release build.

**Channel.** Shown when the array is an end of a [Propagation Channel](propagation-channel.html#beam). The (u, v) map draws
that channel's rays of the current drop — departure directions on a transmit array, arrival directions on a receive array,
each sized by its power — and the codebook's candidate beams; **Beams** beside it lists the ranked distinct pairs with their
power through the channel, the chosen one in bold. Click a pair, or a ray on the map, to use it: the channel gets
`BeamSelect=index` with that `TxBeam`/`RxBeam`. The check button (*Apply to array*) writes the selected directions into each
steered array's `steer()` VARs and sets `BeamSelect=manual`. Each is one undo step. The **Drop** slider shows another
realization of the channel and writes nothing; the scoring runs in the background, its bar beside the slider.

## 11. As an end of a Propagation Channel {#channel-end}

A Propagation Channel's `Tx=` or `Rx=` names an array, and that end of the channel is the array's own elements: their
patterns, their positions on the lattice, and the array's orientation (`TxAz`, `TxEl`, `TxRoll`, or facing the other end).
The coupling is two unknowns per ray — never a matrix of receive elements by transmit elements — and each receive element
takes exactly what an incident wave from that direction gives it in [Receiving](#receive). Several channels may end on one
receive array, and their waves add on its elements: a wanted link and an interferer, each with its own path, fading and
direction of arrival.

When the array's weights are a `steer()`, the run picks the beam through the channel, `BeamSelect=auto` by default, and sets
it for the run only, without elaborating again; the [Channel](#tool) tab above shows the candidates and lets you pick
another. A Feed-mode receive array whose channel cells are identical and linear is reduced to its feed: a harmonic-balance
run reports the power each channel delivers there (`link:CH1:FeedPower_dBm`) and, with several channels, each one's share
(`link:PA1:SIR_dB`); an envelope run with no noise replaces the array by the reflection it presents, so a drop into 4096
elements takes seconds. With nonlinear cells, or with `Noise=yes`, the array is solved element by element. Every channel
parameter and what the run reports: [Components › Propagation Channel](components.html#propagationchannel).

## Limits {#limits}

- Rectangular and triangular lattices only (`Lattice=rect` or `tri`); no conformal or arbitrary positions.
- 64 × 64 elements in Feed mode, 64 in Elements mode.
- A sub-array's patterns and coupling are solved at one pitch; another pitch is warned about, not re-solved.
- `embedded` truncates the coupling at the sub-array's reach, and its accuracy against a whole-array solve is the table in
  [§1](#levels), measured on planar records with an infinite ground.
- An incident wave at a frequency the record does not hold uses the record's nearest frequency, and says so.
- Measurements that read the pattern are one number per run, so a parametric sweep plots their cubes instead.

---

See also: [Co-simulation › The radiation pattern](cosimulation.html#pattern) · [Patch antennas](antennas.html) ·
[System components](system-components.html) · [Simulations › Fields](simulations.html) · [Tuning](tuning.html) ·
[CLI › check and explain](cli.html)
