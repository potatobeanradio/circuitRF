# Brief NZ-8 — EVM, SNR and the constellation from an envelope run

Series: `brief-nz-0-overview.md`. Depends on NZ-7.

**Why.** EVM is the number a receiver or transmitter is specified by. circuitRF already measures it, but only in the
Signal Designer: `RfCore.Signals.SignalEvm` demodulates an LTE or NR carrier against its ideal symbols, and the Signal
Designer draws its constellation. The envelope analysis reports NMSE, a waveform error, but no EVM and no constellation.
So the effect of a circuit's distortion, and after NZ-7 its noise and LO phase noise, on the signal's quality cannot be
read from a run.

## What to build

### 1. Cubes, at every IProbe that carries RF, beside ACLR

| Cube | Axes | Meaning |
|---|---|---|
| `EVM_pct`, `EVM_dB` | [branch, carrier] | RMS EVM of each LTE/NR carrier, measured by `SignalEvm` on the probe's fundamental-band record, after the same gain and delay alignment NMSE already uses |
| `EVMpeak_pct` | [branch, carrier] | Peak EVM |
| `MER_dB` | [branch, carrier] | −20·log10(EVM) |
| `Const` | [branch, carrier, symbol] complex | The equalised received symbols, decimated to at most a stated count (a constant beside `CloudMaxSamples`) |
| `ConstRef` | [carrier, symbol] complex | The ideal symbol positions, so the two plot together |

Rules:
- **A carrier that is not LTE or NR** (CW, pulse, imported I/Q without symbol data) has no EVM. Its cubes are NaN, and
  the run says once which carriers had none and why. NMSE remains the waveform measure for them.
- **The `NoRfFraction` rule** that already NaNs ACLR on a probe carrying no RF applies to the new cubes too.

### 2. Separating noise from distortion — `EvmSplit=yes`

When the run has `Noise=yes` or any `PhaseNoise`, `EvmSplit=yes` runs the same record twice: once as asked, and once
with every noise source and mask off. It writes:
- `EVMdist_pct`: distortion alone;
- `EVMnoise_pct`: √(EVM² − EVMdist²), clamped at 0 with a note when sampling makes it negative.

That is the split a designer needs to know whether to fix linearity or noise. The second run's cost is stated in
`explain`.

### 3. SNR

`SNR_dB` [branch, carrier]: in-channel signal power over in-channel noise power. The noise is taken from the
noise-on/noise-off difference of the two records, so it needs `EvmSplit=yes`. Without it, `SNR_dB` is not written,
rather than estimated from the spectrum's floor, which would mix in distortion.

### 4. The constellation in the Data Display

- `Const` and `ConstRef` plot on an ordinary plot: Im against Re, points only, with a locked 1:1 aspect.
- If the rectangular plot cannot lock its aspect today, add that as an axis option, not a new plot kind
  (`docs/design` keeps the plot kinds to the ones `PlotType` lists).
- The Signal Designer's constellation is drawn by a `PlotControl` already. Reuse its trace styling so the two look the
  same.
- An envelope result with EVM opens to a preset: constellation, EVM per carrier, and the PSD. That is one action, by
  the minimum-clicks rule.

## Gates

1. **Distortion-free, noise-free.** A matched pad: EVM equals the Signal Designer's own EVM of the source signal
   (`SignalEvm` on the source record) to 1e-6 percentage points.
2. **Additive noise.** A matched pad with `Noise=yes` and the source power set for a stated SNR: EVM_rms equals
   1/√SNR to within the record's statistics, which the test computes.
3. **Phase noise.** An LO mask integrated over the subcarrier spacing's band gives the closed-form common-phase and
   inter-carrier EVM contribution to ±10 % (the closed form's own accuracy, stated).
4. **The split adds up.** EVMdist² + EVMnoise² = EVM², to the clamp.
5. **No key, no change.** An envelope run with neither noise nor LTE/NR carriers writes no new cubes, and its existing
   cubes are byte-identical.
