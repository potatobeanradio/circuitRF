# RF Chain Budget

**Open a `.cchain` and the RF Chain Budget window shows its cascade.** The benches beside it are its Transfer to
Schematic: open one, Simulate, and its Data Display opens with the budget's number beside every simulated one.

Two receive chains at 2.45 GHz, in the same 4.5 MHz bandwidth, each already transferred:

1. **`Superhet.cchain`**: an LNA, an RF bandpass filter, a mixer to a 350 MHz IF, an IF filter, an IF amplifier and an
   attenuator. Every stage is a System block with its own parameters.
2. **`Array.cchain`**: four elements, each an LNA and a phase shifter, into a 4-way combiner steered 45° per element, then
   the superhet's receiver from the RF filter on. The phase shifter and the combiner are composed of the library's own
   parts: an amplifier and a line, and a Wilkinson combiner of lines and resistors.

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
loss, NF 5 dB, IIP3 15 dBm), and a `Combiner` (N = 4, 0.5 dB of excess loss, `Steer` = 45°).

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
while the NF does not move.

## The benches

Each chain has three, written by **Transfer to Schematic**:

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

**Run the bench first.** A display reads `results/<bench>.npy`, which Simulate writes. Measured wall clock, release build:

| Bench | Superhet | Array |
|---|---|---|
| 1-Tone | 3 s | 6 s |
| 2-Tone | 7 s | 50 s |
| Envelope | 9 s | 25 s |
