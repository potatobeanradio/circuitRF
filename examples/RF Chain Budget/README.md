# RF Chain Budget

**Open a `.cchain` and the RF Chain Budget window shows its cascade.** The benches beside it are its Transfer to
Schematic: open one, Simulate, and its Data Display opens with the budget's number beside every simulated one.

Three receive chains at 2.45 GHz, in the same 4.5 MHz bandwidth, each already transferred:

1. **`Superhet.cchain`**: an LNA, an RF bandpass filter, a mixer to a 350 MHz IF, an IF filter, an IF amplifier and an
   attenuator. Every stage is a System block with its own parameters.
2. **`Array.cchain`**: four elements, each an LNA and a phase shifter, into a 4-way combiner steered 45° per element, then
   the superhet's receiver from the RF filter on. The combiner is the library's N-way Combiner; the phase shifter is
   composed of the library's own parts, an amplifier and a line.
3. **`Front End.cchain`**: a canceller in front of an LNA, a filter and a second amplifier. The canceller subtracts a
   copy of a −40 dBm interference, 20 dB over the wanted signal, through a 10 dB coupler, with 0.02 dB, 0.2° and 0.05 ns
   of error in its reference path: 47 dB of depth.

The superhet also has two blockers at its input, 150 MHz and 20 MHz above the signal, and asks for the demo bench.

The formulas, the parts and the benches are in *Reference ▸ RF Chain Budget*.

## The chains

| Stage | Part | Gain | NF | Intercept |
|---|---|---|---|---|
| LNA | `Amp` | 16 dB | 1 dB | OIP3 15 dBm |
| RFBPF | `Filter`, 3rd-order Butterworth, 2.35 to 2.55 GHz | −1.5 dB | 1.5 dB | |
| MIX | `Mixer`, LO 2.1 GHz at 7 dBm | −7 dB | 7 dB SSB | IIP3 10 dBm |
| IFBPF | `Filter`, 3rd-order Butterworth, 320 to 380 MHz | −1 dB | 1 dB | |
| IFA | `Amp` | 20 dB | 4 dB | OIP3 25 dBm |
| ATT | `Atten` | −3 dB | 3 dB | |

The array replaces the LNA with four branches of an `Amp` (18 dB, NF 1.2 dB, OIP3 10 dBm) and a `PhaseShifter` (5 dB of
loss, NF 5 dB, IIP3 15 dBm), and a `Combiner` (N = 4, direct and passive, 0.5 dB of excess loss, `Steer` = 45°; its
active variants' amplifiers 10 dB, NF 2 dB, IIP3 10 dBm).

| | Superhet | Array, per element |
|---|---|---|
| Gain | 23.50 dB | 26.02 dB (6.02 dB of it the array's) |
| NF | 2.55 dB | 4.09 dB |
| IIP3 / OIP3 | −7.40 / 16.10 dBm | −12.00 / 14.02 dBm |
| IP1dB / OP1dB | −16.36 / 6.14 dBm | −20.97 / 4.05 dBm |
| MDS in 4.5 MHz | −104.89 dBm | −109.37 dBm |
| Sensitivity at 10 dB SNR | −94.89 dBm | −99.37 dBm |
| SNR | 34.89 dB at −70 dBm | 29.37 dB at −80 dBm |
| SFDR | 65.00 dB | 64.91 dB |

**The array's NF is one element's, and its SNR is four elements' signal.** Four in-phase signals add in amplitude and
their uncorrelated noise in power, so the combiner gains 6.02 dB of signal and none of noise. The SNR rises by
10·log10 4 = 6.02 dB over one element's, and the MDS per element falls by as much. Its NF, 4.09 dB, is that of one
element's branch, the combiner's loss and the receiver after it, by Friis' formula. It is higher than the superhet's
because the phase shifter's 5 dB comes right after the LNA.

**Try it.** In the array's window set the combiner's `N` to 8: the array gain becomes 9.03 dB and the SNR rises 3 dB,
while the NF does not move. Set its `Topology` to Tree and the block view draws three levels of 2:1; set `Kind` to Active
and an amplifier follows each.

## The benches

Each chain has these three, written by **Transfer to Schematic**, and a Levels bench (below); the array has two more:

- **`1-Tone`**: a P1Tone per element and a Port at the output. With the mixer, harmonic-balance noise with the LO
  running gives the SSB NF and the conversion gain; a drive sweep at the predicted IP1dB gives the compression point.
- **`2-Tone`**: a PnTone per element, two tones 1 MHz apart, 50 dB below the predicted IIP3. The intercepts, and each
  stage's gain and OIP3 at its output.
- **`Envelope`**: a MOD_Source per element sending an NR 5 MHz carrier, its 50 Ω noisy at 290 K. The SNR in the carrier's
  4.5 MHz and the EVM's noise part.

In front of the mixer each bench has an **isolator**, a circulator with its third port terminated: it terminates the
mixer's image at 290 K, which is what its SSB noise figure is stated against. Delete it from `Superhet 1-Tone` and the
NF reads 0.28 dB higher: the RF filter reflects the image instead of terminating it, and the mixer's own noise source
then develops more noise there than across a matched termination.

In the array each element has a line in front of it, `PATH2` to `PATH4`: the arriving wave's extra path, 45° per element.
Each element's phase shifter is set to take it out again, so the four arrive at the combiner in phase.

- **`Array Steering`**: a plane wave into the four elements — one Port into a Combiner used as a splitter, its path k
  delayed by the wave's extra path — steered by the phase shifters from the VAR block's `theta_steer`, and read at the
  combiner. A sweep of `theta_in` from −90° to 90° within a sweep of `theta_steer` over −30°, 0° and 30° draws the
  array pattern, and beside it the closed-form array factor. Set `taper` to 6 and the sidelobes fall; set
  `d_over_lambda` to 1 and grating lobes appear.
- **`Array Combiners`**: the same four elements into each of the combiner's four variants, side by side, each from its
  own wave at broadside: gain and NF from one S-parameter run, beside each variant's budget.

| Variant | Gain | NF | IIP3 | OIP3 |
|---|---|---|---|---|
| Direct, passive | 18.52 dB | 1.33 dB | −9.19 dBm | 9.33 dBm |
| Tree, passive | 18.02 dB | 1.35 dB | −9.19 dBm | 8.83 dBm |
| Direct, active | 28.52 dB | 1.43 dB | −11.88 dBm | 16.64 dBm |
| Tree, active | 38.02 dB | 1.45 dB | −18.77 dBm | 19.26 dBm |

A tree's loss accumulates level by level (two 2:1 levels of 0.5 dB against one 4:1 of 0.5 dB); an active combiner's
noise figure is set by its first amplifier and its intercept by its last, which sees the most power. All four keep the
6.02 dB array gain.

## Levels, blockers, the canceller and the demo

- **`<chain> Levels`** (every chain): two tones at the input power, the signal and its IM3 at every node. Its display
  puts them beside the budget's, with the noise floor and NF from the 1-Tone bench, each blocker from the Blockers
  bench, and the SNR and the margin to compression, all against the node (0 the input, k stage k's output).
- **`Superhet Blockers`**: the blockers alone, an isolator after every stage so each node reads the wave going forward.
  The RF filter takes 10 dB off the one at 150 MHz; after the mixer it is at 500 MHz, and the IF filter takes 38 dB more.
- **`Front End Canceller`**: the canceller with real signals. A wanted 64-QAM and QPSK pair under a three-carrier
  interference (10, 5 and 20 MHz), each from a MOD_Source the Signal Designer edits. Its display: the depth against the
  phase error, the amplitude error and the delay mismatch at each interference carrier, simulated beside the closed form;
  the wanted carriers' EVM and SNR, and their constellations, with and without cancellation. Set `phase_err` or
  `delay_err` in its VAR block and Simulate again.
- **`Superhet Demo`**: the superhet demodulated end to end, NR 64-QAM and DBPSK, the LO with a phase-noise mask, the input
  power swept from noise-limited to compression-limited: the constellation, the EVM beside the budget's 1/√SNR, and its
  noise and distortion parts.

## What the displays show

Every number below is the simulation minus the budget.

| | Superhet | Array |
|---|---|---|
| Gain | −0.0001 dB | −0.0001 dB |
| NF | +0.0001 dB | +0.0001 dB |
| IIP3 | +0.0002 dB | +0.0002 dB |
| IP1dB | +0.048 dB | +0.062 dB |
| SNR | −0.13 dB | −0.27 dB |

The gain, NF and intercepts agree to the drive levels the benches use. The compression point is the cascaded-P1dB
formula's own approximation: two stages compressing at similar levels compress a little later together. The SNR is one
noise record's: four seeds of the array read 29.17 to 29.42 dB against 29.37 dB.

The array's Steering pattern sits on the closed-form array factor to 3×10⁻⁹ dB within 3 dB of each peak, and its gain,
NF and array gain at each peak, like every variant's gain and NF on the Combiners bench, to 10⁻¹⁴ dB: an S-parameter run
of ideal blocks has no drive level to differ by.

The superhet's levels sit on the budget to 2×10⁻⁵ dB (signal) and 8×10⁻⁵ dB (IM3) at every node, its blockers 0.001 to
0.003 dB under it, the blockers' own slight compression. The canceller's depth is the closed form's to 1.4 % of the
depth at every carrier and error, and cancelling takes the 64-QAM carrier's EVM from 840 % to 3.2 %. The demo's SNR is
the budget's to 0.2 dB where noise dominates; above that the LO's phase noise holds the EVM at 1.2 % until compression.

**Run the bench first.** A display reads `results/<bench>.npy`, which Simulate writes. Measured wall clock, release build:

| Bench | Superhet | Array |
|---|---|---|
| 1-Tone | 3 s | 6 s |
| 2-Tone | 7 s | 50 s |
| Envelope | 9 s | 25 s |
| Steering | | 2 s |
| Combiners | | 1 s |
| Levels | 7 s | |
| Blockers | 3 s | |
| Demo | 2 min 20 s | |

The Front End's Canceller bench runs its envelope sweeps in about 40 s.
