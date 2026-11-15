# circuitRF — The propagation channel

**Status:** PCH-1 built (the terrestrial and indoor models of §3.1, `src/RfCore/Propagation`; P.1546 not built, §3.1); the rest is design · **Series:** [`brief-pch-0-overview.md`](../briefs/brief-pch-0-overview.md) ·
**Builds on:** [`phased-array.md`](phased-array.md) (the transmit field from a circuit's waves, incident waves by
reciprocity, the fast path and re-weigh, the Phased Array tool), [`chain-budget.md`](chain-budget.md) (the link in front
of a receive chain), the envelope engine and its signal quality (`brief-nz-8`, `src/RfCore/Signals`), the yield engine's
stateless draws ([`yield.md`](yield.md) §6).

The goal is a whole radio link in one schematic: a transmitter, a **Propagation Channel**, a receiver, and one analysis
whose result is what a system designer reads at the far end — received power, SINR, EVM after equalization, BER, outage
and availability — with the transmitter's PA, its antenna or array pattern, and the receiver's noise and linearity all
simulated as circuits, at the same time, through a channel with the impairments terrestrial and satellite systems are
designed against. The same propagation models serve the RF Chain Budget's closed-form link.

---

## 0. Terms

| Term | Meaning here |
|---|---|
| **Propagation Channel** | The component: everything between the transmit antenna's terminals and the receive antenna's terminals that is not an antenna — path loss, shadowing, multipath, Doppler, atmosphere. `.cnl` type `PropagationChannel`, instance prefix `CH`. |
| **Link** | Transmitter, propagation channel and receiver together. |
| **End** | One side of a channel: the transmit end or the receive end. An end is either a **pin** (an antenna described by a gain or a pattern record, §5.1) or an **instance** (a `PhasedArray` or an EM Field Data block whose far field the channel reads, §5.2). |
| **Ray** | One plane-wave path from the transmit end to the receive end: a departure direction, an arrival direction, a delay, a complex gain (time-varying), a polarization matrix and a Doppler frequency. |
| **Cluster** | Rays sharing a delay and a mean direction. |
| **Drop** | One realization of everything random in a channel: the shadowing, line-of-sight state, the small-scale fading, for a scenario the clusters themselves. `Drop` is a parameter; a sweep over it is a Monte Carlo (§8). |

"Air interface" is not used: in wireless and satellite standards it names the protocol and physical-layer
specification. "Channel" alone is not used for the component either: a Feed-mode Phased Array already has a **channel
cell** (the per-element T/R path), and in satellite systems a channel is often a transponder's frequency slot. Every
name in the user interface says **propagation**.

## 1. What circuitRF already has

| Piece | Where | What a link gets from it |
|---|---|---|
| Free-space link in closed form | `ChainLink` (`src/Design/Chain`): Friis, a dB/km term, transmit array, receive antenna gain | The starting point of §9 |
| The transmit field of a circuit, per direction, from its waves | `FieldCosimulation` (`PortFarFieldSet.Weights`, PAA-6b's basis-and-weight over an envelope run, `FromEnvelope`) | A ray's departure field, every step of an envelope run |
| Incident plane waves on an array, by reciprocity | `ArrayIncidentWaveModel` (PAA-7), note `phased-array.md` eq. (4) | A ray's arrival at every element, its pattern and polarization included |
| The array's own noise from its pattern; G/T | PAA-7 | The receive end's noise; the channel adds none |
| Steering expressions, quantised phase, fast path, re-weigh | `steer()`, `PhasedArrayFastPath`, `PhasedArrayReweigh` | Beam selection at the cost of a re-weigh per candidate (§6) |
| Pattern sums over transient direction sets | `ArrayPattern.SumOnce` / `FactorOnce` | Array factors at a channel's ray directions, without tables |
| Delays in the envelope, sub-step, passive | `EnvelopeLineDelay` (method of characteristics, quadratic Lagrange) | The interpolation a ray's delay needs |
| Modulated sources and their demodulation | `ModSourceModel` (LTE/NR OFDM, imported I/Q, pulsed CW, FSK, DBPSK, chirp spread spectrum), `SignalEvm`, `SignalQuality`, `EnvelopeDataSet` (EVM, MER, constellation, `EvmSplit`) | The measurement end — after §7's additions |
| Stateless seeded draws | `StatStreams`, `SamplingPlan` (yield) | Drops that are reproducible one at a time (§8) |
| Axis statistics in a trace | `Evaluator.AxisFunctions` (yield §9.3) | CDF, outage and percentile over the drop axis |
| The Demo and TX benches | `ChainBenches` | The pattern the Link bench follows (§9.3) |

What is missing: a propagation model library; a component that carries a channel between two ends; time-varying
multipath in the envelope; an equalizer, frequency-offset correction and bit errors in the demodulation; a single-carrier
QAM/APSK waveform; beam selection against a channel; drop statistics; and the link's margins in the Chain Budget.

## 2. The model

### 2.1 One equation

The complex envelope arriving at receive element m (or at the receive pin) from a transmitter whose envelope at element
n is aₙ(t) is

```
r_m(t) = 10^(−(L + X)/20) · Σ_r  c_m(Ω_A,r)ᵀ · M_r(t) · g_tx(Ω_D,r ; a(t − τ_r)) · e^{j2πν_r t}                    (1)
```

- **L** is the large-scale path loss (dB) of the model chosen (§3), at the carrier, including atmosphere and clutter;
  **X** the shadowing draw (dB), zero-mean lognormal with the model's σ.
- **Ω_D,r**, **Ω_A,r** are ray r's departure and arrival directions, each in its end's own frame (§5.3).
- **g_tx(Ω; a)** is the transmit end's radiated field toward Ω as a two-component (θ̂, φ̂) vector, linear in the waves a:
  for a pin end G(Ω)·a with the antenna's pattern; for an instance end the array's Σₙ Eₙ(Ω)·e^{jk·û·rₙ}·aₙ (PAA-6b).
- **c_m(Ω)** is the receive end's reception vector: element m's embedded pattern by reciprocity, eq. (4) of
  `phased-array.md`, with the incident field supplied by the channel rather than by a fixed power density.
- **M_r(t)** is ray r's 2×2 polarization and gain matrix: its power share, its fading gain (§4), its cross-polar ratio.
- **τ_r**, **ν_r** are its delay and Doppler frequency.

Every model the channel offers is a way of filling in L, X and the ray list. A single-antenna tapped delay line is the
special case of one direction per tap (§4.1); line of sight in free space is one ray with L the free-space loss.

### 2.2 The channel is one-way

Power scattered from the receive end back to the transmit end comes back at the square of the path gain: a 60 dB path
returns at −120 dB, a satellite path below −300 dB. The channel is therefore modelled **one-way** in every analysis: it
carries the transmit end's field to the receive end and nothing back, and S12 of a pin-ended channel is 0. A reverse link
is a second channel instance; `Reverse=CH1` makes it the same realization transposed (the reciprocity a time-division
system relies on). The run notes state the bound: the neglected return is below |path gain|² of the forward one.

One-way is what makes the rest cheap. In the envelope (§5.4) the coupling is **explicit**: the receive end at time t
reads the transmit end's waves at t − τ_r ≤ t − h, which are already solved, so the channel adds a source term on the
receive side and nothing to the transmit side's Jacobian. A transmit side that does not change between drops is solved
once (§8.2).

### 2.3 The bulk delay and the bulk Doppler

The delay of the first arrival, τ₀ = d/c, is reported (the link's latency: 3.3 µs at 1 km, ~120 ms for a geostationary
hop) and **not simulated**: the channel simulates the excess delays τ_r − τ₀ plus a latency of one envelope step, which is
what keeps the coupling explicit. A common Doppler ν₀ (a low-orbit satellite: ±46 kHz at 2 GHz for 600 km, ten times
that at 20 GHz) and its rate are kept: they are a carrier-frequency offset and a drift the receiver must track, and the
demodulation of §7 measures and removes them, reporting what it removed. `DopplerComp=common` pre-compensates ν₀ at the
transmitter (a terminal that knows the ephemeris), leaving only the spread.

### 2.4 Fast and slow, and the length of a run

Whether fading is fast is a property of the run, not of the channel. The channel's coherence time is stated as the lag at
which its correlation first falls to one half — for the classical spectrum J₀(2πf_D·t) = ½, **T_c ≈ 0.24/f_D**. At 28 GHz
and 3 km/h f_D = 78 Hz and T_c ≈ 3 ms: a 1 ms envelope record sees a frozen channel, and its statistics come only from
many drops. At 100 km/h T_c ≈ 0.09 ms: the channel turns inside one OFDM slot, and the result shows inter-carrier
interference that grows with f_D over the subcarrier spacing. Every run reports f_D·T_record and f_D/Δf_sc, and the
coherence bandwidth (from the RMS delay spread) against the signal's bandwidth, so the regime a result is in is never a
guess.

Shadowing, line-of-sight state, rain and clutter change over seconds to minutes. They are **constant within a drop** and
drawn per drop; only small-scale fading and Doppler evolve inside a record.

## 3. Large-scale models

Each model is a pure function of frequency, geometry and its own parameters, returning L and the shadowing σ (and for a
scenario the line-of-sight probability, the Rician K and the delay and angle spreads). Each records the edition of the
recommendation or report it implements, says it in its notes and in `explain`, and **refuses outside its validity
range** (frequency, distance, heights) rather than extrapolating. Models are named by their ITU-R recommendation or 3GPP
report wherever one exists; otherwise by what they are.

### 3.1 Terrestrial and indoor (PCH-1)

| `PathLoss=` | Model | Inputs beyond f, d |
|---|---|---|
| `free` | Free space (ITU-R P.525) | — |
| `logdist` | Log-distance: free space to d₀, then exponent n; σ | `n`, `d0`, `Sigma` |
| `multislope` | Log-distance with breakpoints, an exponent per segment | `Breaks`, `n[k]` |
| `tworay` | Direct ray plus a ground reflection with the Fresnel coefficient of a stated ground | `TxHeight`, `RxHeight`, `GroundEr`, `GroundSigma` |
| `38901` | 3GPP TR 38.901 scenarios: `UMa`, `UMi` (street canyon), `RMa`, `InH` (mixed, open office), `InF` (SL, DL, SH, DH, HH); line-of-sight and non-line-of-sight path loss, line-of-sight probability, σ, K, delay and angle spread statistics; 0.5–100 GHz | `Scenario`, `Los=auto|los|nlos`, heights |
| `P1238` | ITU-R P.1238 indoor: distance power-loss coefficient and floor penetration loss by environment and band | `Environment`, `Floors` |
| `P1411` | ITU-R P.1411 short-range outdoor: `Mode=belowroof`, `aboveroof` (site-general), `lowheight` (terminals near street level, at a location percentage), `corner` (around one street corner) | `Environment`, `Urban`, `Locations`, `X1`, `X2`, `W1`, `W2` |
| ~~`P1546`~~ | ITU-R P.1546 point-to-area, 30 MHz–4 GHz, from its tabulated curves — **not built**: the curves are an ITU digital product that may not be redistributed without the ITU's written permission (`src/RfCore/RESOLVED.md`); `P1812` covers the same ground over a profile | — |
| `P526` | ITU-R P.526 diffraction: `Mode=knifeedge` (one edge), `sphere` (smooth spherical Earth at any distance) or `profile` (the general method over a **user-supplied terrain profile**, any number of obstructions) | `Profile` (CSV: distance, height), `EdgeHeight`, `EdgeDistance`, `EarthRadiusFactor` |
| `P1812` | ITU-R P.1812 point-to-area over a **user-supplied profile** with clutter, 30 MHz–6 GHz | `Profile` (CSV: distance, height, clutter) |

Added losses, each switchable and each a separate line of the breakdown: **partitions** (a list of partition losses on
the path, dB each, for a same-floor indoor link), **clutter** (ITU-R P.2108), **building entry** (ITU-R P.2109),
**vegetation** (ITU-R P.833), **polarization mismatch** and **pointing** loss for pin ends that carry no pattern.

### 3.2 Atmosphere and satellite paths (PCH-2)

| Item | Model |
|---|---|
| Gaseous absorption | ITU-R P.676 (the line-by-line method; the approximate one where its validity allows, the choice stated) |
| Rain, specific attenuation | ITU-R P.838 |
| Rain, terrestrial path at p % of the time | ITU-R P.530 |
| Earth–space at p % of the time: rain, gas, clouds, scintillation, combined | ITU-R P.618 with P.840 (clouds) |
| Ionospheric scintillation and Faraday rotation, below ~3 GHz | ITU-R P.531 |
| Satellite scenarios: line-of-sight probability by elevation, shadow fading, clutter loss | 3GPP TR 38.811 (dense urban, urban, suburban, rural) |
| Slant range, range rate and its rate for a circular orbit | Geometry: d = √((R_E+h)² − (R_E·cos ε)²) − R_E·sin ε; ν from the orbital velocity's projection |

**Rain in the component** is `Rain=off|fixed|drawn`. `fixed` is the attenuation exceeded for 100 − `Availability` % of
the time — the budget's number, deterministic. `drawn` takes it per drop from the model's exceedance distribution: the
drop draws a time percentage and takes the attenuation exceeded for it, zero beyond the model's rain-free fraction, so a
drop sweep's rain statistics are the model's.

**No ITU digital maps are bundled.** The values a map would give — the rain rate exceeded 0.01 % of the time, the rain
height, the water-vapour density, the cloud liquid water — are parameters with stated defaults, so a result never depends
on a data file the user cannot see.

### 3.3 Terrain and site data

A terrain profile is a CSV the user supplies; nothing is fetched and no elevation database is bundled. Site-specific
multipath (ray tracing) is not built: a user's ray list (§4.3) carries a ray tracer's output into the channel.

## 4. Small-scale models

### 4.1 Tapped delay line (PCH-7)

One direction per tap, for pin ends or when angles are not wanted:

| `Multipath=` | Taps from |
|---|---|
| `none` | One tap: line of sight, or the large-scale loss alone |
| `tdl` + `Profile=` | 3GPP TR 38.901 TDL-A…E (normalised delays scaled by `DelaySpread`, K for D and E); 3GPP TS 36.104 EPA, EVA, ETU; ITU-R M.1225 pedestrian and vehicular A and B; 3GPP TR 38.811 NTN-TDL-A…D |
| `file` + `File=` | A user power-delay profile or measured impulse response: CSV of delay, power (dB) and optionally a fixed phase, a K-factor and a Doppler spectrum per tap |

Each tap's gain is a stationary complex process: **Rayleigh** (zero-mean complex Gaussian), **Rician** (a fixed
line-of-sight component at its own Doppler plus a Rayleigh part, ratio K), or **Nakagami-m**. Doppler spectra:
`classical` (the U-shaped spectrum of uniform arrival in azimuth), `flat`, `gaussian`, `los` (a single line). The maximum
Doppler is `Velocity·f/c`, or stated as `fD`.

### 4.2 Clustered delay line (PCH-8)

For arrays the directions are the point: beamforming gain depends on the angular spread, and a tapped delay line has none.

| `Multipath=` | Rays from |
|---|---|
| `cdl` + `Profile=` | 3GPP TR 38.901 CDL-A…E (A–C non-line-of-sight, D–E line-of-sight): per cluster a delay, power, departure and arrival azimuth and zenith, rays at the report's offsets within the cluster's angular spread, cross-polar ratio; scaled by `DelaySpread` and the angle spreads, and rotated so the cluster angles are relative to the line between the ends (§5.3); 3GPP TR 38.811 NTN-CDL |
| `rays` + `File=` | A user ray list: CSV of delay, power, departure θ/φ, arrival θ/φ, and either a cross-polar ratio or a 2×2 complex polarization matrix |
| `scenario` | A 3GPP TR 38.901 stochastic drop (§8.3): the large-scale parameters, clusters and rays drawn from the scenario's statistics |

Each ray's Doppler follows from the geometry, ν_r = (v_rx·û_A,r + v_tx·û_D,r)/λ, so the Doppler spread of an array link
comes out of its directions rather than an assumed spectrum: a beam pointed at one cluster sees that cluster's Doppler
only. Each ray carries a random initial phase per polarization pair, drawn per drop.

### 4.3 The fading generator

A tap or ray process is generated as a **sum of sinusoids** whose frequencies are drawn from its Doppler spectrum and
whose phases are random, each draw from the drop's stateless stream (§8.1), so a realization is a deterministic function
of `Seed`, `Drop`, the channel instance and the tap, evaluated at any time without state. The number of sinusoids is
chosen so the envelope distribution and the autocorrelation match the target within stated tolerances (gate PCH-7);
it is a parameter. A ray of a CDL already is one sinusoid (its own Doppler), so a clustered channel needs no generator:
its fading comes from the rays' phases beating against each other.

### 4.4 Satellite states (PCH-10)

A land-mobile satellite link moves between line of sight, shadowed and blocked. ITU-R P.681's state model gives each
state's lognormal-shadowed Rician statistics and the state durations; a drop draws the state sequence over the record (or,
for a record much shorter than a state, the one state), and a run reports the state.

## 5. The component

### 5.1 Pin ends

```
PropagationChannel:CH1 tx_ant rx_ant  f0=28 GHz Distance=150 m PathLoss=38901 Scenario=UMi Los=auto \
    Multipath=tdl Profile=TDL-C DelaySpread=100 ns Velocity=3 km/h TxGain=8 dBi RxGain=0 dBi Seed=1 Drop=1
```

Each pin end is an antenna terminal: the transmit pin presents `TxZ` (default the reference impedance; with
`TxPattern=` the record's S11), the receive pin is a source behind `RxZ` (or the record's S11) whose noise is the
antenna temperature `RxTant` (default the ambient temperature, as PAA-7's default). An end's field is an isotropic
pattern scaled by `TxGain`/`RxGain`, or the far field of one port of an EM result (`TxPattern="results/patch.cemr#1"`),
read through `PortFarFieldSet` and oriented by `TxAz`, `TxEl`, `TxRoll`.

### 5.2 Instance ends

```
PropagationChannel:CH1  Tx=PA1 Rx=PA2  Distance=150 m PathLoss=38901 Scenario=UMi Multipath=cdl Profile=CDL-C ...
```

`Tx=` / `Rx=` name a `PhasedArray` (either mode) or an EM Field Data block with far fields; that end has no pin, and the
dynamic symbol (as PAA-5's) draws only the pins the instance has. The two kinds mix: a 64×64 base-station array to a
one-antenna terminal is `Tx=PA1` with a receive pin. Several channels may end on one instance receive end: their
contributions add on the element source nodes, so an interferer through its own channel is co-channel interference with
its own path, fading and angle of arrival. A pin receive end takes one channel.

**Far field.** The channel assumes both ends are in each other's far field. `check` warns when d < 2D²/λ for either
end's largest dimension D: a 64×64 half-wavelength array at 28 GHz has 2D²/λ ≈ 44 m, so a 10 m indoor link to it is in
the near field and its gain is overstated.

### 5.3 Frames and geometry

Each end has a position (`TxPos`, `RxPos` as x, y, z, or `Distance` with heights on a flat earth, or for a satellite
`Altitude` and `Elevation`) and an orientation (azimuth, elevation, roll of the antenna's +z). The line between the ends
defines the line-of-sight directions in each end's own frame; CDL cluster angles are offsets from it; a user ray list is
given in a stated frame (`RayFrame=global|ends`). Velocities (`TxVel`, `RxVel`, or a satellite's orbit) give each ray's
Doppler (§4.2) and, for a satellite, ν₀ and its rate.

### 5.4 What each analysis sees

| Analysis | The channel |
|---|---|
| DC | Open; the pin ends are their impedances |
| S-parameter, noise | A one-way coupling frozen at t = `TimeOffset` (default 0): pin ends a two-port with S21 = H(f) = 10^(−(L+X)/20)·Σ_r M_r·e^{−j2πfτ_r}, S12 = 0; instance ends through **ray unknowns** (below). No noise of its own |
| HB, multi-tone HB | The same frozen coupling at every tone the transmit end carries (harmonics included, each at its own f; a pattern record without that frequency uses its nearest and says so); **no Doppler** — a shifted tone would leave the HB grid; said in the notes |
| Envelope | The whole of eq. (1): excess delays, time-varying gains, per-ray Doppler, common Doppler; explicit coupling |

**Ray unknowns.** For instance ends the coupling between the transmit array's N_t element waves and the receive array's
N_r element sources has rank K (the ray count), so it is stamped as K auxiliary unknowns y_r = M_r·g_tx(Ω_D,r)·a and a
source Σ_r c_m(Ω_A,r)·y_r on each receive element — sparse, one-way, never an N_r × N_t matrix.

**The envelope.** Per carrier product k, ray r's contribution at step t is the transmit field toward Ω_D,r formed from
the waves at t − τ_r (interpolated between already-solved steps, quadratic Lagrange as `EnvelopeLineDelay`, which is
why the simulated latency is at least one step), rotated by e^{−jω_k τ_r}, times M_r(t)·e^{j2πν_r t}, projected on each
receive element's c_m. Nothing on the transmit side reads the receive side. The receive side's first τ_max of the record
has no history and is left out of every measurement window, said in the notes.

**Cost.** Per step, N_t·K multiply-adds to form the departure fields and K·N_r to project them. At 4096 × 460 rays
(CDL-A) that is ~2 M per step per side, small beside an envelope step of a 4096-element nonlinear array. When the receive
array meets the fast-path conditions (identical linear channel cells, PAA-8), its beamformed output is Σ_r AF_rx(Ω_A,r)·…,
K per step; that collapse is applied only where it equals the element-by-element form (gate PCH-8).

### 5.5 Parameters

All are expressions (as PAA's D4): swept, tuned, optimized, drawn by yield. Groups, as the parameter editor shows them:

- **Geometry:** `Distance` or `TxPos`/`RxPos`, `TxHeight`, `RxHeight`, `Altitude`, `Elevation`, orientations,
  `TxVel`, `RxVel`, `Velocity`, `fD`.
- **Large scale:** `PathLoss`, `Scenario`, `Environment`, `Los`, `Shadowing=on|off`, `Sigma`, `Profile` (terrain),
  `Partitions`, `Clutter`, `BuildingEntry`, `Vegetation`, `Gas`, `Rain=off|fixed|drawn`, `RainRate` (R₀.₀₁),
  `RainHeight`, `Availability` (%),
  `PolLoss`, `PointingLoss`.
- **Small scale:** `Multipath`, `Profile`, `DelaySpread`, `AngleSpreads`, `KFactor`, `Fading=rayleigh|rician|nakagami`,
  `m`, `Doppler`, `Sinusoids`, `File`, `RayFrame`, `Xpr`.
- **Ends:** `Tx`, `Rx`, `TxGain`, `RxGain`, `TxPattern`, `RxPattern`, `TxZ`, `RxZ`, `RxTant`.
- **Beam:** `BeamSelect`, `BeamRank`, `TxBeam`, `RxBeam`, `Codebook`, `BeamSearch` (§6).
- **Realization:** `Seed`, `Drop`, `TimeOffset`, `DopplerComp`, `Reverse`.

`f0` is the frequency the large-scale model and the pattern are evaluated at when the transmit end carries several
tones; default the transmit end's carrier.

### 5.6 What a run reports

A `link` group in the result: the loss breakdown (free space, model excess, gas, rain, clutter, partitions, shadowing
draw), the line-of-sight state, K, RMS delay spread, angle spreads, f_D, T_c, coherence bandwidth, τ₀, ν₀, the ray list
of the drop, H(f, t) on the signal's band over the record, the power-delay profile and the Doppler spectrum, received
power, and the beam table of §6. `explain --analysis` reports the model, its edition and validity, the ray count, the
regime (§2.4) and the cost estimate of §5.4.

## 6. Picking the beam

### 6.1 The candidates

A **codebook** is a list of steering directions per array end. The default is a grid in (u, v) at one beamwidth spacing
within the array's scan limit, steered through the array's own `steer()` and quantised by its `PhaseBits`, so a candidate
is exactly a beam the hardware can make. `Codebook="file.csv"` gives directions instead. A codebook of arbitrary
per-element weights is not in this series.

### 6.2 Choosing

For each candidate pair the **wideband effective channel** through both beams,

```
H_eff(f) = Σ_r AF_rx(Ω_A,r)·E_rx(Ω_A,r)ᵀ · M_r · E_tx(Ω_D,r)·AF_tx(Ω_D,r) · e^{−j2πfτ_r}                         (2)
```

is evaluated at the drop's channel at t = `TimeOffset` on frequency samples across the signal's band, and the metric is
its mean power over the band. This is the same pattern model the fields analysis forms (array factor and embedded element
patterns), not a circuit solve; it costs K · N_f per pair after `ArrayPattern.FactorOnce` over the ray directions.
`BeamSearch=exhaustive` scores every pair; `alternating` (the default past 10⁶ pairs) fixes the receive beam, picks the
best transmit beam, then the reverse, until neither moves — the beam-pair search a radio runs.

### 6.3 The user's choice

| `BeamSelect=` | Beam used |
|---|---|
| `auto` (default) | The strongest pair |
| `rank` with `BeamRank=n` | The n-th strongest **distinct** pair: a pair within one beamwidth, at both ends, of a higher-ranked pair does not count, so rank 2 is the next path, not the neighbouring beam half a dB down |
| `index` with `TxBeam=`, `RxBeam=` | A named codebook entry, or a direction `(θ, φ)`, per end |
| `manual` | The arrays' own authored weights, untouched: whatever their `steer()` VARs or weights say, so every way of steering an array that exists today still works |

A selected beam is applied **in memory for the run** exactly as the Phased Array tool's drag applies one: the `steer()`
call's VARs are set (or its literals replaced), and a weights-only change goes through `PhasedArrayReweigh`, so trying a
beam does not re-elaborate. Nothing is written to the schematic unless the user applies it (§6.4). `auto`, `rank` and
`index` on an array whose weights are not a `steer()` refuse with the reason, as PAA-9's drag does, and say that
`manual` uses the weights as they are.

The result's `beam` table holds every scored pair (or every pair the alternating search visited) with its metric, the
rank, the pair chosen and why, and the **unconstrained bound** — the dominant eigenvector of the channel's spatial
covariance, phase-only — so the loss a codebook costs is a number. The selection uses the linear pattern model; the
run that follows is the full nonlinear one, and the notes say both.

### 6.4 In the Phased Array tool

When the tool's array is an end of a channel, the (u, v) map draws that channel's rays of the current drop — departure
directions on a transmit array, arrival directions on a receive array — sized by power, and the codebook's beams. A
**Beams** list beside it holds the ranked pairs. Clicking a pair or a ray selects it: it writes `BeamSelect=index` with
`TxBeam`/`RxBeam` on the channel, one undo step; **Apply to array** writes the direction into the array's `steer()` VARs
(PAA-9's write) and sets `BeamSelect=manual`, also one step. A drop slider steps through realizations.

## 7. The far end: demodulation and errors

### 7.1 What exists, and why it is not enough

`SignalEvm` fits **one** complex gain over the record and no equalizer, on purpose: a filter's in-band ripple is EVM. A
multipath channel makes that EVM meaningless — a frequency-selective fade puts some subcarriers 20 dB down and no
receiver leaves them there. A Doppler shift rotates the constellation over the record. Nothing decides symbols or counts
bits.

### 7.2 Equalization (PCH-4)

`Equalizer=` on the envelope analysis (per probe, as `EvmSplit`):

| Value | OFDM | Single carrier (DBPSK, the new QAM/APSK) |
|---|---|---|
| `none` (default) | Today's single gain: every existing result unchanged | Today's |
| `genie` | Per subcarrier, per symbol, from the **true** channel: the propagation channel's own H(f, t) when one is upstream, times one fitted gain for the circuit; with no channel upstream, the per-subcarrier response averaged over the record | A linear MMSE equalizer computed from the known channel |
| `pilot` | From designated resource elements only (a comb in frequency and time, `Pilots=` spacing), interpolated, as a receiver estimates; EVM measured on the others | A linear MMSE FIR trained on a preamble of `Training=` symbols and applied to the rest |

`genie` isolates the circuit's impairments from estimation error and is an upper bound; `pilot` is what a receiver
achieves, its estimation noise included. A circuit's own linear ripple is equalized by both, as a receiver would: the
single-gain `none` stays for a circuit designer who wants ripple counted.

### 7.3 Frequency offset and drift

Estimated from the data (data-aided: the phase slope and its curvature over the record) and removed before the
equalizer; `CFO_Hz` and `CFOdrift_Hzps` are reported. A Doppler spread is not removed: what remains is inter-carrier
interference, and it is EVM, as it is in a receiver.

### 7.4 Decisions, symbol and bit errors (PCH-4)

After equalization each symbol is decided to the nearest constellation point; with the generator's own bit mapping the
decided bits are compared with the sent ones. `SER`, `BER`, `BitErrors`, `Bits` per probe and carrier, with an exact
binomial confidence interval. Counting needs errors: BER 10⁻⁶ wants ~10⁸ bits for 100 errors, more than an envelope run of
a nonlinear circuit holds. So beside the count, a **semi-analytic** BER: each resource element's post-equalizer SINR from
its error vector (noise and distortion treated as Gaussian, which `EvmSplit` checks), the modulation's closed-form
conditional BER, averaged over the record — so a fading pattern across subcarriers is in the average. It is labelled
*estimated (Gaussian)*. The per-element SINR is a cube, so a user can map it to a coded block error rate of their own;
forward error correction is not in this series.

### 7.5 A single-carrier waveform (PCH-5)

Satellite links are mostly single carrier. `MOD_Source` gains `Carrier[k]="SC;Rs=…;Mod=QPSK|8PSK|16QAM|…|16APSK|32APSK;
RollOff=…;Rings=…;Data=PN…"`: root-raised-cosine shaping, ring ratios as parameters, an optional preamble and pilot
blocks; demodulated by a matched filter, timing recovery from the known record, then §7.2–7.4. The Signal Designer
draws it.

## 8. Drops and statistics

### 8.1 Draws

A drop's random numbers are the yield engine's stateless draws, `Hash(Seed, Drop, stream, k)`, with one stream per
channel instance and quantity (shadowing, line-of-sight state, each tap's or ray's phases, each sinusoid's frequency and
phase, a scenario's cluster parameters). So drop N alone equals drop N in a batch, and adding a tap moves no other draw.

### 8.2 A Monte Carlo is a sweep over `Drop`

There is no new analysis type. With `Drop=drop` on the channel and a global `VAR drop=1`,
`analysis MC type=parametric_sweep Var=drop Start=1 Stop=500 Step=1 Inner=ENV1` runs 500 realizations; a nested sweep over transmit power or distance outside it gives BER or EVM against SNR. Statistics are
measurement functions over the drop axis (`Evaluator.AxisFunctions`): `cdf`, `pctl_over`, `mean_over`, `std_over` and
`yield_over` exist; `outage_over(x, threshold)` (the fraction of drops below a threshold, with its binomial interval) is
added.

**Reuse across drops.** When the channels form no cycle (no side transmits to a side that transmits back) and a
transmit side's resolved parameters do not change between drops (a hash, as PAA-8's), its recorded waves are reused and
only the receive side is solved — exactly equal to the joint solve, since the coupling is explicit (gate PCH-10). A
transmit side whose beam changes with the drop is solved once per distinct beam and cached by its weights. A receive side
that meets the fast-path conditions is a linear filter of its incident field and is not stepped at all. The run reports
how many drops solved which side.

### 8.3 Scenario drops

`Multipath=scenario` draws a 3GPP TR 38.901 drop for the ends' geometry: correlated large-scale parameters (delay
spread, angle spreads, K, shadowing), cluster delays, powers and angles, rays, cross-polar ratios, line-of-sight state
from its probability. This is the largest single piece of the series and the one most exposed to transcription error; it
is gated against an independent implementation written from the report alone (PCH-10).

## 9. The RF Chain Budget

### 9.1 The link's propagation (PCH-3)

`ChainLink.Path` becomes **Propagation**, and the free-space formula one choice among §3's models, with the same names
and parameters as the component. Its readouts become a breakdown and three margins at a stated **availability** p:

```
M_shadow = σ · Q⁻¹(1 − p)                                       lognormal shadowing
M_fade   = the fade depth the fading distribution exceeds with probability 1 − p:
           Rayleigh  −10·log10(−ln p)  (20.0 dB at 99 %, 30.0 dB at 99.9 %);  Rician and Nakagami numerically
M_comp   = the same for the composite of both (lognormal-shadowed fading), the default; the sum is offered as the
           conservative bound
A_rain   = ITU-R P.530 (terrestrial) or P.618 (earth–space) at 100 − p % of the time
```

Then the **required SNR** for the modulation at a target BER (uncoded, closed form), the receiver's sensitivity from the
chain's noise figure and bandwidth, and the **link margin** = received power − margins − sensitivity. A satellite link
takes its distance from altitude and elevation (§3.2) and reports latency and Doppler. The `.cchain` format version rises
only for a chain that uses the new fields, as version 2 did.

### 9.2 The Blocks view

A propagation glyph between the transmitter and the receive chain, with the loss and margins beneath it.

### 9.3 The Link bench (PCH-11)

The transfer gains a **Link** bench: the TX bench's PA and antenna gain, a Propagation Channel with the chain's model,
the receive chain and a modulated source, in two analyses. The **margins** are checked by a drop sweep of the
received power through the chain's linear analysis — cheap per drop, so it is sized to the availability (≥ 100/(1 − p)
drops, ~100 outage events) — and the display sets the budget's predicted outage and margin beside the Monte Carlo's, as
every other bench sets budget against simulation. The **signal** is demodulated end to end by an envelope run with
`Equalizer=pilot` at two drops picked from that sweep — the median and the one at the outage percentile — so EVM and
BER are read where the budget says the link is typical and where it is about to fail, without thousands of envelope
runs. Antennas are pin ends with the link's gains; a bench that emits a PhasedArray stays deferred
(`phased-array.md` §11).

## 10. Satellite links, assembled

Nothing beyond §3.2, §4.4 and §7.5 is satellite-specific: an uplink channel, a transponder (an Amplifier with AM/AM and
AM/PM, a filter), a downlink channel, and a ground receiver are an ordinary schematic, solved in one envelope run —
the transponder's compression of the uplink's noise and the downlink's fade both in the EVM. The shipped example (PCH-12)
is a low-orbit downlink with its Doppler, a land-mobile state model and a single-carrier APSK signal.

## 11. Verification

| What | Against |
|---|---|
| Each ITU-R model | The validation examples published with the recommendation where they exist; otherwise an independent script written from the recommendation alone, committed with its output (`testdata/propagation/`) |
| 3GPP path loss, line-of-sight probability, TDL/CDL tables | An independent script from the report; the tables' powers sum to one and their RMS delay spread is the stated one |
| Fading generator | Envelope distribution (Rayleigh, Rician at K, Nakagami at m) by a goodness-of-fit statistic at a fixed seed; autocorrelation against J₀(2πf_D τ); level-crossing rate and average fade duration against their closed forms |
| Doppler | A CW through a single ray at ν appears at f + ν, exactly, in the envelope spectrum |
| One-way, explicit | A joint run equals a partitioned run (transmit side solved once, receive side alone) to 1e-12 |
| Ray unknowns | One line-of-sight ray between two arrays equals PAA-7's incident wave at the same direction with power density from free space — the existing reciprocity gate, through the channel |
| Beam selection | A one-ray channel picks the codebook entry nearest the ray; rank 2 of a two-cluster channel picks the other cluster; `manual` leaves the weights bit-identical |
| Equalizer | A static two-tap channel within the cyclic prefix, `genie`: EVM equals the no-channel EVM; beyond the prefix, intersymbol interference appears; `pilot` approaches `genie` as SNR rises |
| BER | AWGN QPSK/16QAM counted BER against the closed form within its interval; flat Rayleigh BPSK averaged over drops against ½(1 − √(γ/(1+γ))) |
| Chain Budget | The Link bench's Monte Carlo outage at the budget's margin equals the target availability within its interval |

## 12. Placement in the code

| Piece | Where |
|---|---|
| Large-scale models, satellite geometry, TDL/CDL tables, ray-list reader, fading generator | `src/RfCore/Propagation` — pure numerics, no domain types (the draw function moves down beside it if `StatStreams` cannot be reached from RfCore) |
| Equalizer, frequency offset, decisions, BER, single-carrier waveform | `src/RfCore/Signals` |
| The component | `src/Core/Devices/System/PropagationChannelModel.cs`, registered in the factory; ray unknowns stamped by it; instance binding where PAA-7's `ArrayIncidentWaveModel` reads its patterns |
| The envelope coupling | `src/Engine/Envelope/EnvelopeChannel.cs` |
| Beam selection | `src/Engine/Array` (scoring, uses `ArrayPattern`); the in-memory override through `PhasedArrayReweigh` |
| Drop reuse | Beside `CircuitEvaluation`'s elaboration reuse (PAA-8b) |
| Chain Budget | `src/Design/Chain/ChainLink.cs`, a new `ChainPropagation.cs`; benches in `ChainBenches` |
| UI | Parameter editor groups, the dynamic symbol (`BuiltInSymbols`), the Phased Array tool's rays and Beams list (`src/Ui/PhasedArray`, `src/Render/PhasedArray`), the Chain window |
| `check` / `explain` | Model validity, the far-field warning, cycles, a non-`steer()` beam refusal; the regime, the cost, the walk of each end |

## 13. Not in this series

Multiple streams (spatial multiplexing, precoding, digital beamforming: with the arrays' own deferred digital
beamforming); beam tracking within a drop; a codebook of arbitrary weights; ray tracing and bundled terrain, clutter or
ITU map data; forward error correction and coded block error rate; protocol (scheduling, retransmission); cycles of
channels in the envelope (a two-way link in one bench); near-field coupling between ends; a Chain Budget transfer that
emits PhasedArrays.
