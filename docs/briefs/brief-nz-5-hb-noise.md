# Brief NZ-5 — noise in harmonic balance

Series: `brief-nz-0-overview.md`. Depends on NZ-3 (the Mixer's `NF=`) and NZ-4 (the noise interface on every model).

**Why.** A mixer's noise figure, an LNA's noise figure while a blocker drives it, and the noise a PA puts into a receive
band are all large-signal questions. The S-parameter noise analysis cannot answer them: it linearises at DC, so it has
no LO, no blocker and no frequency conversion. Harmonic balance already computes the conversion matrix about its
operating point (`HbSmallSignal`, behind the HB analysis's `SSStart…SSNpts` small-signal sweep and the WSProbe under
HB). Noise is the same linear system driven by the circuit's own noise sources instead of a probe.

## The analysis — a key on HB, not a new analysis

`analysis HB1 type=hb … SSStart=… SSStop=… SSNpts=… Noise=yes NoiseIn=1 NoiseOut=2 NoiseSideband=-1`

| Key | Default | Meaning |
|---|---|---|
| `Noise` | `no` | `yes` adds the noise cubes. It needs the small-signal sweep keys; without them it is a refusal naming them. |
| `NoiseIn` | `1` | The port whose source the noise figure is referred to. |
| `NoiseOut` | `2` | The port the output noise is read at. The small-signal sweep's frequencies ARE the output frequencies. |
| `NoiseSideband` | `0` | Which sideband of the input carries the signal: the input frequency is `f_out + k·f0`. `0` is the LNA case (same frequency in and out). For a down-converter with `f_IF = f_RF − f_LO`, it is `+1`; for `f_IF = f_LO − f_RF`, it is `−1` on the conjugate side. `explain` prints the resulting input frequency for the first and last output point, so a wrong sign is visible before a run. |
| `NoiseContrib` | `no` | As NZ-1: per-part contribution cubes. |

A multi-tone HB (a blocker plus an LO) uses the lattice sidebands `HbSmallSignal` already supports. `NoiseSideband`
then takes a lattice index `k1,k2`.

## The method

1. **Conversion matrix.** At each output frequency, the conversion matrix about the converged HB solution is the
   `HbSmallSignal` system at `ω_ss = 2π·f_out`. Nothing here builds a second one.
2. **Adjoint.** One transposed solve per output frequency, for the `NoiseOut` port's wave at sideband 0. That gives the
   transfer from a current injected at any node, at any sideband, to the output. This is PR #50's adjoint idea with
   sidebands added.
3. **Sources.**
   - **Passive parts:** Bosma at each sideband frequency `f_out + k·f0`. They are stationary, so they are uncorrelated
     between sidebands (block-diagonal across sidebands).
   - **Nonlinear devices:** `EvaluateNoise` (NZ-4) at every time sample of the large-signal waveform gives each source's
     white density `p(t)`. Its Fourier coefficients give the correlation between sidebands k and l: `⟨j_k j_l*⟩ = P_{k−l}`.
     That is the whole of cyclostationary shot and thermal noise, and the step this brief exists for.
     - Flicker: the modulated coefficient times `1/f^Af`, taken at each sideband's own frequency. When k ≠ l, use the
       geometric mean of the two. State that approximation in the design note.
   - **The Mixer system block** (NZ-3): its `NF` as an input-referred stationary source at its RF port, with SSB/DSB
     converted by its `NFType`.
4. **Output noise.** N_out = the adjoint row × all sources' sideband correlation × its conjugate.
5. **Noise figure, both definitions.** The input port's own termination noise, at T0 at every sideband, is split:
   - **SSB:** only the `NoiseSideband` term counts as signal. The image sideband's termination noise counts as noise.
   - **DSB:** the signal sideband and its image both count as signal.

   For an LNA (`NoiseSideband = 0`) the two are the same, and only `NF` is written.

## Results

| Cube | Axes | Unit |
|---|---|---|
| `NF`, `Fn` | [ssfreq] | dB, linear (SSB) |
| `NFdsb`, `Fndsb` | [ssfreq] | dB, linear (only when the input and output frequencies differ) |
| `Te` | [ssfreq] | K, SSB |
| `Nout` | [ssfreq] | W/Hz at the output port, absolute |
| `Gc` | [ssfreq] | dB, the conversion (or small-signal) gain from the input sideband to the output: the denominator of NF, written so the two can be checked against each other |
| `NFsb` | [ssfreq, sideband] | %, the share of output noise arriving through each input sideband: noise folding made visible |
| `NFc`, `NFpct` | [ssfreq, part] | NZ-1's layout, when `NoiseContrib=yes` |

Inside a `parametric_sweep`, LO power or blocker power is an ordinary sweep axis, so "NF against LO drive" and "NF
against blocker level" need nothing more.

## Verilog-A in HB

A compiled device's noise is read from the worker at each time sample. Add a batch form of the worker's `noise`
command, `noise_batch`, taking every time sample of one instance in one request, as the batching rule for `eval`
already does. One request per time sample per instance is not acceptable. Gate 5 measures the request count, not the
time.

## Gates

1. **Small-signal limit.** An LNA built from NZ-4 devices, with the HB drive at −200 dBm: `NF` equals the S-parameter
   noise analysis's NF to 1e-6 dB at every frequency.
2. **Ideal mixer.** A multiplier mixer with a matched resistive termination at every port, at T0:
   - SSB NF = 3 dB + the loss terms the closed form gives, and DSB NF is 3 dB below SSB;
   - the image's share in `NFsb` is 50 %.
3. **Switching diode mixer.** A single diode driven hard by a square-ish LO, against the closed-form conversion loss and
   the noise of a time-varying conductance. Tolerance as the closed form's own error, stated.
4. **Blocker desensitisation.** An LNA's NF rises with blocker power. Its value at a blocker level 30 dB below P1dB
   matches gate 1 to 0.01 dB.
5. **Batching.** A compiled device in an HB noise run makes one `noise_batch` request per instance per operating point.
6. **No key, no change.** Every HB result with `Noise` absent is byte-identical. That includes the WSProbe's small-signal
   outputs, which share the conversion matrix.

## Out of scope

Oscillators (NZ-9/10). An HB of a free-running circuit is refused here exactly as it is today.
