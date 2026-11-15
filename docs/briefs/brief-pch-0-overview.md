# Brief series PCH — the propagation channel

**Goal.** A whole radio link in one schematic: a transmitter, a **Propagation Channel** component, a receiver, and one
analysis whose result is what a system designer reads at the far end — received power, SINR, EVM after equalization,
BER, outage and availability. The transmitter's PA, its antenna or phased array, and the receiver's noise and linearity
are all circuits, simulated together, through a channel carrying the impairments terrestrial and satellite links are
designed against: path loss by ITU-R and 3GPP models, shadowing, multipath with Rayleigh, Rician or Nakagami fading,
Doppler, atmosphere and rain. With arrays at the ends, the channel's directions meet the arrays' patterns, the strongest
beam pair is found automatically, and the user can pick any other. The RF Chain Budget's link gains the same models and
the margins they imply, checked by a Link bench.

**Design note:** [`docs/design/propagation-channel.md`](../design/propagation-channel.md). Read it first; its section
numbers are cited below as "note §n".

## Why a series

A phased array can be driven by harmonic balance or a modulated envelope and its radiated signal measured in named
directions (PAA-6b); a receive array takes incident plane waves and has its own noise (PAA-7); the Chain Budget closes a
free-space link in closed form. Five things stop that from being a link:

1. **Nothing carries a signal from one antenna to another.** The transmit array's field and the receive array's incident
   waves exist separately; a receive wave is a fixed power density from a fixed direction, not what a transmitter sent.
2. **There is no propagation model** beyond free space and a dB/km term: no path-loss model, no shadowing, no multipath,
   no fading, no Doppler, no atmosphere or rain.
3. **The demodulation cannot survive a channel.** EVM fits one complex gain over the record with no equalizer and no
   frequency-offset correction, so a frequency-selective fade or a Doppler shift reads as a broken signal; nothing
   decides symbols or counts bit errors; there is no single-carrier QAM/APSK waveform.
4. **Nothing picks a beam.** An array is steered by hand or by expression; nothing scores beams against a channel.
5. **Nothing gives statistics.** A link is specified by outage and availability, which need many realizations.

## What already exists and is reused

| Need | Existing piece |
|---|---|
| A transmit array's field per direction from its waves, over an envelope run | `FieldCosimulation.FromEnvelope` (PAA-6b), `PortFarFieldSet` |
| Incident plane waves on an array by reciprocity | `ArrayIncidentWaveModel` (PAA-7), `phased-array.md` eq. (4) |
| The receive array's noise from its pattern | PAA-7 |
| Array factors over transient direction sets | `ArrayPattern.SumOnce` / `FactorOnce` |
| Steering, quantisation, weight-only changes without elaboration | `steer()`, `PhasedArrayReweigh` (PAA-8b), `PhasedArrayFastPath` (PAA-8) |
| The tool's beam drag and its write rule | `src/Ui/PhasedArray` (PAA-9) |
| Sub-step delays in the envelope | `EnvelopeLineDelay` |
| Modulated sources, EVM, MER, `EvmSplit`, FSK/DBPSK/chirp quality | `ModSourceModel`, `SignalEvm`, `SignalQuality`, `EnvelopeDataSet` |
| Stateless seeded draws | `StatStreams`, `SamplingPlan` (yield) |
| Statistics over a sweep axis | `Evaluator.AxisFunctions` (yield §9.3) |
| The link in front of a receive chain, the TX and Demo benches | `ChainLink`, `ChainBenches` |

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-pch-1-terrestrial-models.md` | Large-scale models, terrestrial and indoor: free space, log-distance, multi-slope, two-ray, 3GPP TR 38.901 scenarios, ITU-R P.1238, P.1411, P.1546, P.526 and P.1812 over a user profile, added losses | — |
| `brief-pch-2-atmosphere-and-satellite.md` | ITU-R P.676, P.838, P.530, P.618 with P.840, P.531; 3GPP TR 38.811 large-scale; orbit geometry, slant range, Doppler and its rate | — |
| `brief-pch-3-chain-budget-propagation.md` | The Chain Budget's Propagation section: the models, the breakdown, margins at an availability, required SNR, link margin | 1, 2 |
| `brief-pch-4-equalizer-and-errors.md` | Equalizer (`genie`, `pilot`), frequency offset and drift, decisions, counted and semi-analytic BER, per-element SINR | — |
| `brief-pch-5-single-carrier-waveform.md` | Single-carrier PSK/QAM/APSK with root-raised-cosine shaping, preamble and pilots; its demodulation | 4 |
| `brief-pch-6-component-pin-ends.md` | The `PropagationChannel` component with pin ends, deterministic: large-scale loss, static multipath, Doppler shift, the one-way coupling in every analysis, `check`/`explain`, symbol, editor | 1 |
| `brief-pch-7-fading-and-tdl.md` | The fading generator; Rayleigh, Rician, Nakagami; Doppler spectra; TDL profiles and user power-delay profiles | 6 |
| `brief-pch-8-arrays-and-cdl.md` | Instance ends (`Tx=PA1 Rx=PA2`), ray unknowns, CDL profiles, user ray lists, polarization, per-ray Doppler, interference, the far-field check | 7 |
| `brief-pch-9-beam-selection.md` | Codebooks, the strongest pair by default, `rank`/`index`/`manual`, the beam table and bound, the Phased Array tool's rays and Beams list | 8 |
| `brief-pch-10-drops-and-statistics.md` | Drops, reuse across drops, statistics functions, 3GPP TR 38.901 scenario drops, ITU-R P.681 satellite states | 7 (8, 9 for arrays) |
| `brief-pch-11-link-bench.md` | The Chain Budget's Link bench: margins by Monte Carlo, the signal demodulated at the median and outage drops | 3, 4, 7, 10 |
| `brief-pch-12-docs-and-examples.md` | User pages, format references, three shipped examples | all |

PCH-1, PCH-2 and PCH-4 have no dependency on each other and can be built in any order. PCH-3 is the first thing a user
sees and needs no engine work. PCH-6 to PCH-10 are the component.

## Decisions already made

- **D1 — The name is Propagation Channel.** `.cnl` `PropagationChannel`, prefix `CH`. "Air interface" names a protocol
  specification, and "channel" alone collides with the Phased Array's channel cell and a transponder's channel
  (note §0).
- **D2 — Models are named by their ITU-R recommendation or 3GPP report** wherever one exists, by what they are otherwise
  (`logdist`, `multislope`, `tworay`). Each states its edition and refuses outside its validity range.
- **D3 — One-way.** The channel carries the transmit end's field to the receive end and nothing back, in every
  analysis; a reverse link is a second instance (`Reverse=` for the same realization transposed) (note §2.2).
- **D4 — Explicit in the envelope.** The simulated latency is at least one step, the excess delays are added to it, and
  the bulk delay is reported, not simulated; the receive side reads only solved steps of the transmit side (note §2.3,
  §5.4). HB and linear analyses see the channel frozen at `TimeOffset`, HB with no Doppler.
- **D5 — Ends are pins or instances**, independently: a pin is an antenna described by a gain or one port's pattern; an
  instance is a `PhasedArray` or an EM Field Data block. The symbol draws only the pins there are.
- **D6 — Single stream.** Analog or hybrid single-stream beamforming only; spatial multiplexing and digital beamforming
  are deferred with the arrays' own.
- **D7 — The strongest beam pair is the default, and every other beam is reachable**: the n-th strongest *distinct* pair,
  a named codebook entry or direction, or `manual` (the arrays' authored weights untouched). A selection is applied in
  memory through the `steer()` override and `PhasedArrayReweigh`; only the tool's Apply writes the schematic (note §6).
- **D8 — No new analysis type.** A Monte Carlo is a parametric sweep over the channel's `Drop`; statistics are axis
  functions (note §8.2).
- **D9 — Draws are stateless**, by the yield engine's hash: drop N alone equals drop N in a batch.
- **D10 — No data files the user cannot see.** No ITU digital maps, terrain or clutter databases are bundled; the values
  they would give are parameters with defaults, and terrain is a user CSV. No ray tracer: a user ray list carries one's
  output.
- **D11 — `Equalizer=none` is the default**, so every existing EVM is unchanged; `genie` and `pilot` are asked for.
- **D12 — Every parameter is an expression**, as the Phased Array's are; the structural words (`PathLoss`, `Scenario`,
  `Multipath`, `Profile`, `Tx`, `Rx`, `BeamSelect`) are words.

## Risks — say them now

1. **Statistical models predict distributions, not a site.** That is how these links are designed and specified, and the
   docs say it; a site-specific answer needs a ray tracer, which a ray list imports.
2. **Transcription.** The 3GPP scenario procedure (PCH-10) and the larger ITU-R methods (P.1546's curves, P.1812, P.618)
   are long documents with many tables. Each is gated against published validation data where it exists, otherwise
   against an independent script written from the document alone. If a brief's model cannot be validated that way, it
   stops and reports rather than shipping an unchecked model; P.1546 and P.1812 are the first to cut if PCH-1 runs long.
3. **Cost of nonlinear arrays with many drops.** A drop of a 4096-element array with nonlinear channel cells is an envelope
   run of that array. Reuse across drops (note §8.2) removes the transmit side; the receive side is free only on the fast
   path. PCH-10 measures it and records what a drop costs at 64, 1024 and 4096 elements; if a large nonlinear receive array
   is impractical across drops, the docs say so and the examples use a linear receive side.
4. **Low BER by counting** is out of reach of a nonlinear envelope run; the semi-analytic BER covers it and says it is an
   estimate (note §7.4).

## Simulation budget, for every brief

- **No EM runs in this series.** Arrays use committed PAA records (`testdata/phased-array/`) or synthetic patterns.
- Tests read small committed fixtures and synthetic channels; a routine test stays under ~5 s. No new `Category=Benchmark`
  test and no timing assertion: costs go in `RESOLVED.md`, gated by counters (solves, re-weighs, reused sides).
- Statistical gates use a fixed seed and a stated statistic, never "looks Rayleigh".
- Reference data is generated independently of circuitRF and committed under `testdata/propagation/`, with the script and
  the document edition it was written from.
- One test per claim. Run the named test classes only, never all of `Ui.Tests`. Ad-hoc measurements carry a memory
  watchdog on the real process id.
- Findings go to the nearest `RESOLVED.md` (`src/RfCore/RESOLVED.md` for models and signals, `src/Core/RESOLVED.md` for the
  component, `src/Engine/RESOLVED.md` for the envelope and arrays, `src/Design/RESOLVED.md` for the Chain Budget) and to
  each brief's completion note.

## Not here

Note §13: multiple streams, beam tracking within a drop, arbitrary-weight codebooks, ray tracing and bundled site data,
coded block error rate, protocol, cycles of channels in the envelope, near-field coupling between ends, a Chain Budget
transfer emitting PhasedArrays.
