# Brief MTR-2 — baseband results of a modulated transient

**Series:** MTR-0 … MTR-3 · **Tag:** `R-mtr2-<n>` · **Depends on:** MTR-1
**Area:** `src/Engine/Transient` (a new `TransientBaseband.cs`, `TransientRunner.cs`, `TransientEngine.cs`,
`TransientTypes.cs`), `src/Core/Design/Analysis.cs` (the keys), `docs/design/transient.md` (a new §8.1).

## The gap

A carrier-resolved run of a modulated signal produces its waveforms at RF rate, millions of points per net. Nothing turns
those waveforms into what a modulated run is for: spectrum, ACLR, EVM, AM-AM, the constellation.

## Requirements

**R-mtr2-1 — `Store=`.** A new key on the tran directive: `Store=auto|baseband|rf`, default `auto`.
- `auto` is `baseband` when the bench has a `MOD_Source`, and `rf` otherwise. Every existing run is therefore unchanged.
- `baseband` on a bench with no `MOD_Source` is refused: there is no carrier to take the envelopes about.

**R-mtr2-2 — The streaming downconverter.** With baseband storage, the engine reports each point of a uniform RF-rate
output grid to a sink instead of keeping it.
- The grid's spacing is `1/(4·(K+1)·Fc)`, with K = `StoreHarm`.
- The sink forms, for every recorded quantity and every harmonic k = 0 … K, the envelope
  `X_k(t_m) = c_k·Σ w(t_j − t_m)·x(t_j)·e^(−jk2πFc·t_j) / Σ w`, where c₀ = 1 and c_k = 2 above it.
- `w` is a Kaiser-windowed sinc low-pass: flat to the signal band's edge (half the signal rate), down 80 dB by the
  envelope's Nyquist frequency.
- The envelope sample rate is the envelope analysis's own rule (the signal rate, at least twice oversampled).
- The RF waveform is never held whole. Memory is the window of `w` plus the envelopes.
- When `Fc` is below the low-pass's stopband plus its passband, a neighbouring harmonic would leak in, so the run is
  refused and `Store=rf` is named.

**R-mtr2-3 — What is recorded.** Every quantity the envelope analysis records:
- every labelled net (every net when none is labelled);
- each IProbe's current and the voltage at its first net;
- each non-zero DC supply's current.

**R-mtr2-4 — One result, the envelope's.** The envelopes, the drive (the source's `Vs·g·b`) and the carriers are packed
as an envelope run result and measured by `EnvelopeDataSet.Build` (MTR-0 D3). The DataSet therefore has the envelope's
cubes: `V`, `I`, `Pprobe_W`, `PSD`, `ACLR`, `EVM_pct`, `Const`, `AMAM`, `Gt_dB`, `DE_pct` and the rest. Beside them sit
the transient's counters (`Tran.*`) and a `__TranStore` marker.
- `MeasureFrom=` (s, default `auto`: the largest `MOD_Source` `Ramp`) is the settling skip.
- `StoreHarm=` (default 3) is K.

**R-mtr2-5 — One centre frequency.** Every `MOD_Source` in a baseband run shares one `Fc` and one `RecordStart`. A bench
whose sources differ is refused, naming them and `Store=rf`.

**R-mtr2-6 — RF storage of a modulated bench.** With `Store=rf` and a `MOD_Source`, an automatic output `Step` resolves
the carrier at 1/(20 Fc_max), not Stop/1000. The cubes are the transient's own.

**R-mtr2-7 — Sweeps.** A baseband transient inside a parametric sweep stacks like an envelope run.

## Gates

1. **Envelopes match.** A `MOD_Source` into 50 Ω, run with `Store=baseband`: the `V` cube's harmonic-1 envelope at the
   load equals `½·Vs·b(τ)`, to 1e-3 of `Vs` after `MeasureFrom`. Harmonics 0, 2 and 3 are below 1e-3 of `Vs`.
2. **Measurements.** The same bench's `Pavg_dBm` at an output IProbe, into its matched load, equals `Pavg` plus the
   window's own mean power `10·log10(mean |b|²)` (a few µs of OFDM is not the record's unit mean) within 0.05 dB.
3. **Refusals.** `Store=baseband` with no `MOD_Source`, and two sources with different `Fc`, are each refused by name.
4. **RF storage.** `Store=rf` keeps the `[time, node]` cube on an output grid of 1/(20 Fc) spacing.
