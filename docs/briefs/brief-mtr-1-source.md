# Brief MTR-1 — the modulated source in transient

**Series:** MTR-0 … MTR-3 · **Tag:** `R-mtr1-<n>` · **Depends on:** TRN-1, TRN-2
**Area:** `src/Engine/Transient/TransientEngine.cs`, `TransientElements.cs`, `TransientTypes.cs`, the seeds
(`HbTransientSeed`, `OscTransientSeed`), `docs/design/transient.md` (§5.5, a new §6.4).

## The gap

A `MOD_Source` in a transient analysis is refused by name ("modulated signals are simulated by the envelope analysis").
Nothing plays its signal in time.

## Requirements

**R-mtr1-1 — The drive.** In a native transient run, a `MOD_Source` drives `v(t) = Re{Vs·g(t)·b(τ)·e^(j2πFc·t)}` behind
its `Z`. In that expression:
- `Vs = √(8·Re Z·Pavg)` is its peak available-drive phasor, as in every other analysis.
- `b(τ)` is the composite baseband record (unit mean power, each carrier at its offset from `Fc`).
- `τ = RecordStart + t` is the time into the record.
- `g(t)` is the soft start of R-mtr1-3.

Like a shaped tone (TRN-2), it is its own right-hand-side column, taken by difference with its drive scaled to zero in
the aggregate.

**R-mtr1-2 — The record, sampled once.** `b(τ)` is read from a table over the run's window, built once per run by the
signal's own band-limited window interpolation (`ModulatedSignal.CompositeWindow`).
- The table rate is 32 × the record's widest offset plus half its native rate.
- Between table points the value is the cubic through the four nearest.
- A table longer than 2^24 points is refused, naming the window and the rate, and saying to shorten `Stop`.

**R-mtr1-3 — Keys on the source.** Two keys are read in transient only; every other analysis ignores them.
- `RecordStart=` (s, default 0): where in the record the run starts. The record is periodic, so any value works.
- `Ramp=` (s, default 0): a raised-cosine rise over `Ramp`, as a tone's `Ramp` is. A breakpoint is placed at its end.

**R-mtr1-4 — The step bound.** The highest frequency a `MOD_Source` brings is `Fc` plus the record's widest offset plus
half its native rate. The automatic step bound (1/20 of the highest source period) counts it.

**R-mtr1-5 — Seeds play CW.** A transient seed for HB or the oscillator analysis (TRN-8) plays a `MOD_Source` as its CW
tone at `Fc`, the steady state harmonic balance solves for. A `PlayModulation` switch on the run's parameters, false in
both seeds, carries this.

**R-mtr1-6 — Refusals.** The refusal by kind is removed for `MOD_Source`. ngspice keeps refusing it (MTR-0 D1).
`docs/design/transient.md` §5.5 is updated, and a new §6.4 describes the drive.

## Gates

1. **The drive.** A `MOD_Source` (an LTE 5 MHz carrier at 1 GHz) into a matched 50 Ω load: the load voltage over a
   2 µs window equals `½·Re{Vs·b(τ)·e^(jωt)}` from the signal's own window interpolation, to 1e-3 of `Vs` at every
   output point.
2. **RecordStart.** Starting at `RecordStart = 1 µs` gives the waveform a run from 0 has at 1 µs (to the same
   tolerance), up to the carrier's phase `e^(jω·1µs)`.
3. **HB unchanged.** A bench with a `MOD_Source` and an HB analysis gives the same result, bit for bit, with `Ramp` and
   `RecordStart` set.

## Not here

`Delay`; ngspice; the results' shape (MTR-2).
