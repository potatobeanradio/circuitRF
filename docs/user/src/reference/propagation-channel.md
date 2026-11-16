---
title: The Propagation Channel: a radio link in one schematic
slug: reference/propagation-channel.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Propagation Channel
lede: Everything between a transmit antenna and a receive antenna that is not an antenna — path loss, shadowing, multipath, Doppler, rain and the atmosphere — as one component, so a transmitter, the path and a receiver are simulated together and read as received power, EVM, bit errors and outage.
keywords: propagation channel, link, path loss, propagation model, ITU-R, 3GPP, TR 38.901, UMa, UMi, RMa, InH, InF, P.1238, P.1411, P.526, P.1812, P.525, P.618, P.676, P.838, P.530, P.840, P.531, P.681, TR 38.811, satellite, low earth orbit, LEO, geostationary, slant range, Doppler, drift, rain, availability, shadowing, line of sight, fading, Rayleigh, Rician, Nakagami, tapped delay line, TDL, clustered delay line, CDL, ray list, delay spread, coherence time, coherence bandwidth, drop, Monte Carlo, outage, outage_over, equalizer, genie, pilot, BER, EVM, CFO, beam selection, codebook, beam pair, far field, near field, partitions, clutter, building entry, vegetation
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#what">What it is, and what it is not</a></li>
      <li><a href="#choosing">Choosing a model</a></li>
      <li><a href="#models">The models</a></li>
      <li><a href="#ends">Pin ends and array ends</a></li>
      <li><a href="#regime">The regime readouts</a></li>
      <li><a href="#fading">Fading and multipath</a></li>
      <li><a href="#drops">Drops: a Monte Carlo over realizations</a></li>
      <li><a href="#far-end">The far end: equalizer, frequency offset and bit errors</a></li>
      <li><a href="#beam">Picking the beam</a></li>
      <li><a href="#satellite">Satellite links</a></li>
      <li><a href="#far-field">The far-field warning</a></li>
      <li><a href="#examples">Examples</a></li>
    </ol>
  </nav>

The **Propagation Channel** (System, `.cnl` type `PropagationChannel`, prefix `CH`) carries what arrives at a transmit
antenna's terminals to a receive antenna's terminals. The transmitter's amplifiers, the antennas or arrays, the receiver's
noise and linearity are ordinary parts around it, simulated with it in one run, and what the run reports is what a system
designer reads at the far end: received power, SINR, EVM after equalization, bit errors, outage. Its symbol and every
parameter are on the [Components](components.html#propagationchannel) page; this page says which model to pick and what
each result means.

## What it is, and what it is not {#what}

**A channel predicts distributions, not a site.** Every model here is statistical: a path loss and its spread for an
environment, a delay profile and its fading for a kind of place. A run is one realization of that statistic, a **drop**, and
the answer to "will this link work" is a percentage of drops, which a sweep over `Drop` gives. A particular street, building
or hill is not in any of them. A ray tracer's output can be read in as a ray list (`Multipath=rays`) and a measured terrain
profile as a CSV (`P526`, `P1812`), and nothing else about a site is: no map, terrain or clutter database is bundled.

**It is one way.** It carries the transmit end to the receive end and nothing back, so its S12 is 0. Power scattered back
returns at the square of the path gain, −120 dB on a 60 dB path. A reverse link is a second channel; `Reverse=CH1` makes it
the same realization with its ends exchanged.

**It adds no noise.** A receive pin is thermal at the antenna temperature `RxTant`; a receive array's noise is its own,
from what its elements' patterns see. The channel itself is noiseless.

**What each analysis sees:**

| Analysis | The channel |
|---|---|
| DC | Open; the pins are their impedances |
| S-parameter, noise | A two-port frozen at `TimeOffset`: S21 = H(f), S12 = 0 |
| Harmonic balance | The same frozen H at every tone, each at its own frequency, with no Doppler shift |
| Envelope | Every ray delayed, faded and turned by its Doppler as the run steps |

The bulk delay d/c (3.3 µs at 1 km, 3.6 ms to a satellite 1,075 km away) is reported as `link:CH1:Delay0_s` and never
simulated: the envelope adds one step of latency plus each ray's delay in excess of the first.

## Choosing a model {#choosing}

Pick by where the antennas are and the band. Each model refuses outside the range its document gives rather than
extrapolating, and the refusal names the range.

| Where | Band | `PathLoss=` | Notes |
|---|---|---|---|
| Anywhere with a clear line of sight, or to check a bench | any | `free` | ITU-R P.525: λ/4πd between isotropic antennas |
| A measured or quoted exponent | any | `logdist`, `multislope` | Your numbers; `multislope` takes breakpoints |
| A mast over open, flat ground | any | `tworay` | The direct and ground-reflected rays: the deep nulls and the fourth-power fall-off beyond the break |
| Cellular: macro (`uma`), street-canyon micro (`umi`), rural (`rma`) | 0.5–100 GHz (RMa to 30) | `38901` | 3GPP TR 38.901: path loss, line-of-sight probability, shadowing, and the delay and angle spreads; `Multipath=scenario` draws a whole drop |
| Indoor office, mall, factory hall | 0.5–100 GHz | `38901` with `inh-…` or `inf-…` | Indoor hotspot and indoor factory scenarios |
| Indoor, by environment, with floors between | 0.3–300 GHz | `P1238` | ITU-R P.1238: office, corridor, industrial, conference room; `Floors` and `Partitions` add their losses |
| Outdoor short range: below or above rooftops, terminals at street level, around one corner | 0.3–300 GHz by mode | `P1411` | ITU-R P.1411 |
| Over an obstacle: one edge, the Earth's curvature, or a terrain profile | from 10–30 MHz | `P526` | ITU-R P.526 diffraction |
| Point to area over a terrain profile with clutter | 30 MHz–6 GHz | `P1812` | ITU-R P.1812, at a time and location percentage |
| Earth to a satellite: low, medium or geostationary orbit | the atmospheric terms' ranges | `satellite` | Slant range, latency and Doppler from the orbit; TR 38.811's terminal environments; P.618 rain, P.676 gas, P.840 clouds, P.531 ionosphere |

The added losses go on top of any model, each a line of its own in the loss breakdown: `Partitions` (walls on the path),
`Clutter` (ITU-R P.2108), `BuildingEntry` (P.2109), `Vegetation` (P.833), `PolLoss`, `PointingLoss`, and the
atmosphere (`Gas`, `Rain`, `Clouds`, `Scintillation`, `Ionosphere`) taken at one `Availability`. On a terrestrial path rain
is P.530's, on a satellite path P.618's.

## The models {#models}

Every model, from the registry the channel, the Chain Budget and `circuitrf reference` all read:

{{table: propagation-models}}

Each model's own parameters, written on the channel beside `PathLoss=`:

### `logdist` {#model-logdist}

{{table: propagation/logdist}}

### `multislope` {#model-multislope}

{{table: propagation/multislope}}

### `tworay` {#model-tworay}

{{table: propagation/tworay}}

### `38901` {#model-38901}

{{table: propagation/38901}}

### `P1238` {#model-p1238}

{{table: propagation/P1238}}

### `P1411` {#model-p1411}

{{table: propagation/P1411}}

### `P526` {#model-p526}

{{table: propagation/P526}}

On the channel the terrain profile is written `Terrain=`, because `Profile=` there names a tapped delay line; the Chain
Budget's Propagation section calls it `Profile`.

### `satellite` {#model-satellite}

{{table: propagation/satellite}}

### `P1812` {#model-p1812}

{{table: propagation/P1812}}

The terrain profile is `Terrain=` on the channel, as for `P526`.

### The added losses, on any model {#added-losses}

{{table: propagation/losses}}

No ITU digital map is bundled. The values a map would give — the rain rate exceeded 0.01 % of the time, the rain height,
the surface water vapour, the cloud liquid water — are the parameters above with stated defaults, so a result never
depends on a data file you cannot see. Two ITU-R methods are not offered, because their data may not be redistributed:
P.1546's tabulated curves (`P1812` covers the same ground over a profile) and P.676's approximate method (the line-by-line
method is used instead).

## Pin ends and array ends {#ends}

**A pin end** is an antenna terminal: `tx` drives the channel, `rx` is driven by it, each over ground. Its antenna is
`TxGain`/`RxGain` in dBi, the same in every direction, or one port of an EM result's far field (`TxPattern="results/patch.cemr#1"`),
whose realized gain toward each ray and S11 are then the end's. `TxAz`, `TxEl`, `TxRoll` turn it; unstated, its boresight
faces the other end.

**An array end** names a Phased Antenna Array (`Tx=PA1`, `Rx=PA2`), and that side has no pin: the array's own elements,
patterns and positions are the end, and the symbol draws only the pins that remain. The coupling is two unknowns per ray,
never a matrix of receive elements by transmit elements, and each receive element takes exactly the wave an incident plane
wave from that direction gives it. Several channels may end on one receive array, their waves adding on its elements, which
is how co-channel interference arrives with its own path, fading and direction. A pin receive end takes one channel.

The two mix: a base-station array to a one-antenna handset is `Tx=PA1` with an `rx` pin.

## The regime readouts {#regime}

Whether a channel is "fast" or "slow", flat or frequency-selective, is a property of the run against the channel, and each
run says which it is, in its notes and in `explain --analysis`:

| Readout | Cube | What it means for the result |
|---|---|---|
| Maximum Doppler f_D | `link:CH1:fD_Hz` | `Velocity`·f/c, or `fD`, or the ends' relative speed |
| Coherence time T_c | `link:CH1:Tc_s` | Where the fading's correlation first falls to one half: 0.24/f_D for the classical spectrum. A record much shorter than T_c sees a frozen channel, and its statistics come only from many drops |
| f_D · T_record | `link:CH1:fDxTrecord` | Below about 0.05 the record is frozen; near 1 or above, the channel turns within it |
| f_D / subcarrier spacing | `link:CH1:fDoverSCS` | OFDM only: above about 0.01 the fading's inter-carrier interference shows in the EVM, and grows with it |
| RMS delay spread, coherence bandwidth | `link:CH1:RmsDelaySpread_s`, `CoherenceBandwidth_Hz` | A signal much narrower than the coherence bandwidth fades flat, all of it together; a wider one fades frequency by frequency, which an equalizer has to undo |
| Common Doppler ν₀ | `link:CH1:Doppler0_Hz` | A frequency offset the receiver must track; the demodulation measures it as `CFO_Hz` |

A 100 kb/s FSK signal in an office (40 ns of delay spread, 11 MHz of coherence bandwidth) fades flat; a 50 MHz NR carrier on
the same path does not. At 28 GHz and walking pace (f_D ≈ 78 Hz, T_c ≈ 3 ms) a 1 ms record is frozen; at 100 km/h it is not.

## Fading and multipath {#fading}

`Multipath=` says where the rays come from:

| `Multipath=` | Rays |
|---|---|
| `none` (default) | The geometry's: the line of sight, and the ground ray for `tworay` |
| `tdl` with `Profile=` | A tapped delay line: TR 38.901 `TDL-A`…`TDL-E`, TS 36.104 `EPA`, `EVA`, `ETU`, ITU-R M.1225 `PedA`, `PedB`, `VehA`, `VehB`, TR 38.811 `NTN-TDL-A`…`D`. The normalized ones need `DelaySpread` |
| `file` with `File=` | Your power-delay profile: a CSV of `delay (ns)`, `power (dB)`, optionally `phase`, `doppler`, `k` and `spectrum` per tap |
| `cdl` with `Profile=` | A clustered delay line, rays with directions and polarization: TR 38.901 `CDL-A`…`CDL-E`, TR 38.811 `NTN-CDL-A`…`D`. For arrays: beamforming gain depends on the angular spread, which a tapped line does not have |
| `rays` with `File=` | Your ray list: delay, power, departure and arrival angles, cross-polar ratio or a 2×2 matrix |
| `scenario` | A whole TR 38.901 drop for the ends' geometry (`PathLoss=38901`): correlated large-scale parameters, clusters, rays |

Each tap's gain fades by `Fading=rayleigh`, `rician` (`KFactor`) or `nakagami` (`NakagamiM`) with a `Doppler=` spectrum
(`classical`, `flat`, `gaussian`, `los`), generated as a sum of `Sinusoids` sinusoids whose draws depend only on `Seed`,
`Drop` and the channel's name. A clustered line's rays need no generator: each has its own Doppler from its directions and
the ends' velocities, and the fading is the rays beating against each other.

## Drops: a Monte Carlo over realizations {#drops}

One run is one drop. A drop draws the shadowing (on by default, from the model's σ), the line-of-sight state when
`Los=auto`, the rain's time percentage when `Rain=drawn`, the satellite state when `States=p681`, and every tap's or ray's
fading. **A Monte Carlo is a sweep over `Drop`**, with no analysis of its own:

```
drop = 1
PropagationChannel:CH1 tx rx … Seed=1 Drop=drop
analysis MC type=parametric_sweep Var=drop Start=1 Stop=2000 Step=1 Inner=SP1
measure Prx_dBm = 10 + SP1.link("CH1", "Gain_dB")
measure Outage = outage_over(Prx_dBm, -98, "drop")
```

`outage_over(x, threshold, "drop")` is the fraction of drops below the threshold, with its exact 95 % interval as
`Outage:lo` and `Outage:hi`; `cdf`, `pctl_over`, `mean_over`, `std_over` and `yield_over` read the same axis. Drop 37 alone
equals drop 37 inside the sweep.

**Cheap drops first.** Received power over thousands of drops is an S-parameter sweep: each drop's `link:CH1:Gain_dB` is
20·log10|H| at the reference frequency with the antenna gains, shadowing and fading in it, and a drop costs a millisecond.
The signal — EVM, bit errors — is an envelope run, seconds a drop, so it is read over tens of drops, or at the drops the
power sweep picks out (the median and the one at the outage percentile, as the Chain Budget's
[Link bench](chain-budget.html#link-bench) does).

**Reuse across drops.** In an envelope sweep a transmit side that is the same in every drop is solved once and replayed into
each later drop's receive side, which is exactly what solving both gives, because the channel is one way. A transmit array
whose beam changes with the drop is solved once per distinct beam. The run says how many drops it replayed. A receive array
whose channel cells are identical and linear is replaced by its feed for the run when the run has no noise; with
`Noise=yes` the array is kept, because its cells' noise is part of it.

**What drops cost.** A receive array of nonlinear cells is stepped element by element, about 4 s a drop at 64 elements
and a minute at 256 for a 1 µs record. A transmit array of nonlinear amplifiers costs the same per step, once per beam.
So a link through large arrays keeps its receive cells linear, its beam the same across the drops, and its record a few
symbols long; the examples below say what they traded.

## The far end: equalizer, frequency offset and bit errors {#far-end}

Through a channel with delay spread, one gain over the record no longer reads a signal as a receiver would: a fade on some
subcarriers reads as distortion. The envelope analysis's `Equalizer=` key says how each carrier is read:

| `Equalizer=` | Use it for |
|---|---|
| `none` (default) | A circuit's own error, its ripple included, exactly as before any channel existed |
| `genie` | The best a receiver can do: each subcarrier divided by the channel's own H(f, t), fading included. What remains is the circuit's distortion, the noise and the inter-carrier interference the fading causes |
| `pilot` | What a receiver achieves: the response read from pilots (`Pilots=` on OFDM; a trained `Taps=` equalizer on DBPSK and single carrier), interpolated, with its estimation noise |

**The frequency offset** (`CFO_Hz`, `CFOdrift_Hzps`) is measured in every mode and removed before `genie` and `pilot`. A
single carrier or DBPSK carrier is first acquired to the nearest bin of the window from its power spectrum, so a satellite's
hundreds of kilohertz of Doppler are read to a fraction of a hertz, then fitted from the symbols' phases. A drift is
measurable only when it moves the frequency within the record: a low-orbit pass's kilohertz a second moves a 0.2 ms
record by a fifth of a hertz, so the channel's own drift, `link:CH1:Drift0_Hzps`, is the one to read.

**Errors.** `BER`, `SER`, `BitErrors`, `Bits` and the exact interval `BERlo`/`BERhi` are counted after the decisions;
`BERest` estimates the BER from each symbol's SINR, treating noise and distortion as Gaussian, and reaches below what a short
record can count. `SINR_dB` per symbol maps to a coded error rate of your own; forward error correction is not modelled.
The cubes and their definitions: [Simulations › Equalizer, frequency offset and bit errors](simulations.html#envelope-equalizer).

## Picking the beam {#beam}

When an end is a Feed-mode array whose `Phase` is a `steer()`, the run picks its beam through the drop's channel. Each end's
candidates are a grid in (u, v) one beamwidth apart out to `ScanLimit`, each made through the array's own `steer()` and
`PhaseBits` (or `Codebook=` names a CSV of directions). Every pair is scored by the power the two array factors carry
through the drop's rays, averaged over the signal's band, through the linear pattern model; the run that follows is the
full circuit.

| `BeamSelect=` | Beam used |
|---|---|
| `auto` (default) | The strongest pair |
| `rank` with `BeamRank=n` | The n-th strongest **distinct** pair: one within a beamwidth of a stronger pair at both ends is the same path, so rank 2 is the next path, not the neighbouring beam |
| `index` with `TxBeam=`, `RxBeam=` | A codebook entry (a number from 1) or a direction `(θ, φ)` per end |
| `manual` | The arrays' own weights, as written |

The beam is set for the run only; the schematic keeps its `steer()`. The run reports every pair scored
(`link:CH1:Beam_dB[pair]`, strongest first), the pair chosen, and `BeamBound_dB`, the best any phase-only weights could do
through these rays, so `BeamCodebookLoss_dB` is the price of the codebook.

**Picking another beam in the tool.** In the Phased Array tool (*Tools ▸ Phased Array*), an array that is a channel end has a
**Channel** tab: the drop's rays on the (u, v) map — departures on a transmit array, arrivals on a receive array, sized by
power — with the candidates, and the ranked pairs in a list beside it. Click a pair or a ray to use it: that writes
`BeamSelect=index` with `TxBeam`/`RxBeam` on the channel. The check button (*Apply to array*) writes the directions into the arrays' `steer()` and
sets `BeamSelect=manual`. Each is one undo step. The drop slider shows another realization without changing anything.

## Satellite links {#satellite}

`PathLoss=satellite` takes the orbit's `Altitude` and the `Elevation` the earth station sees it at, and gives the slant
range, the latency, and the Doppler and its rate for a `rising` or `setting` `Pass` at the frequency. A low orbit at 600 km
seen at 30° is 1,075 km away (3.6 ms) and moves the carrier by about 400 kHz at 20 GHz. `Scenario=` adds TR 38.811's
line-of-sight probability, shadowing and clutter for a terminal in dense urban, urban, suburban or rural surroundings.

The atmosphere is taken at `Availability`: `Rain=fixed` adds P.618's attenuation exceeded for 100 − `Availability` % of
the time, the budget's number; `Rain=drawn` draws the time percentage per drop, so a drop sweep's rain statistics are the
model's. `Gas`, `Clouds`, `Scintillation` and `Ionosphere` add their terms the same way.

`States=p681` with `StateEnvironment=` applies ITU-R P.681's land mobile-satellite model: the link moves between a GOOD
state (line of sight, light shadowing) and a BAD state (heavy shadowing and blockage), each with its own direct-signal and
multipath statistics, valid from 1.5 to 20 GHz above 20° elevation. A terminal moving at `Velocity` crosses states along the
record; a record much shorter than a state is one state, so the fraction of BAD drops is the fraction of time the link is
shadowed. The run reports `link:CH1:State` (1 GOOD, 0 BAD).

A transponder is ordinary parts: an input filter, an amplifier at its back-off, an output filter, and the channel after it.
`DopplerComp=common` pre-compensates the common Doppler at the transmit end, as a terminal that knows the ephemeris does.

## The far-field warning {#far-field}

The channel takes each end to be in the other's far field: its rays are plane waves. `check` and the run warn when the ends
are closer than 2D²/λ for an array's largest dimension D, its aperture's diagonal. A 16 × 16 half-wavelength array at
28 GHz is 121 mm corner to corner and needs 2.7 m; a 64 × 64 one needs 44 m, so a 10 m indoor link to it is in its near
field and the gain the channel gives is overstated. Near-field coupling between ends is not modelled.

## Examples {#examples}

Three examples build a link each (*File ▸ New ▸ Examples*):

- **Indoor IoT Link** — an 868 MHz FSK sensor two floors from its gateway: P.1238 with partitions, a tapped delay line read
  from a power-delay profile file, received power and outage over 2,000 drops, and bit errors over 50.
- **LEO Satellite Downlink** — a 20 GHz downlink from 600 km at 30° elevation: P.618 rain at 99.5 %, P.681 states, the pass's
  Doppler, and a 16APSK single carrier through a transponder.
- **mmWave Array Link** — a 16 × 16 transmit array with an amplifier per element to an 8 × 8 receive array, TR 38.901 UMi
  through CDL-C, the beam pair found automatically, an NR signal, EVM and bit errors over drops.

---

See also: [Components › Propagation Channel](components.html#propagationchannel) ·
[RF Chain Budget › A link in front of the chain](chain-budget.html#link) · [Phased Antenna Array](phased-array.html) ·
[Simulations › Envelope](simulations.html#envelope) · [Signal Designer › A single carrier](signal-designer.html#sc) ·
[Measurements › Statistics](measurements.html#statistics)
