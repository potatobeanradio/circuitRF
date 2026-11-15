# Brief TRN-4 — lines and frequency-defined elements in transient

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn4-<n>` · **Depends on:** TRN-1 · **Design note:** §5.3, §5.4, §5.5, §13.2,
§13.3
**Area:** `src/Engine/Transient`, the shared pieces moved down from `EnvelopeLineDelay` and `EnvelopeSnpFit`,
`IUniformLine` models, the EM-cell path (`model=em`), `src/Engine/RESOLVED.md`.

## The gap

An RF schematic is mostly lines, S-parameter files, filters and EM results. After TRN-1 every one of them is refused in
transient. The envelope's realizations cover a band around each harmonic; transient needs DC to the top of the run, and
a lossy line's models are not causal, so the envelope's approach cannot be used as it stands.

## Requirements

**R-trn4-1 — Lossless lines** as delays by the method of characteristics, with `EnvelopeLineDelay`'s passive quadratic
interpolation moved down and shared (envelope results unchanged, bit for bit). A breakpoint is added τ after every
source breakpoint so an edge that crosses the line lands on a step.

**R-trn4-2 — Lossless coupled lines** by modal decomposition with the frequency-independent modal transformation, one
delay per mode. Lossy or dispersive coupled lines are refused by name.

**R-trn4-3 — Lossy and dispersive two-conductor lines** per note §5.3: the residual H̃(f) made causal by giving its
magnitude the minimum phase, realized by a low-order rational fit, τ the high-frequency delay. Measure, on every shipped
microstrip, stripline and coplanar model at three representative geometries each, the residual fit's error and the
phase change the causality correction makes at the band edge; record both. **If the shipped models cannot be realized
within a stated tolerance (proposed: |ΔS21| ≤ 1e-3 against the frequency-domain model up to `f_max`), stop and report**,
and refuse lossy lines in transient by name instead of shipping the approximation. A line whose correction exceeds the
tolerance in a run is warned about by name.

**R-trn4-4 — Wideband rational fits** per note §5.4 for SnP blocks, System Filters, Duplexers, Z(freq) blocks, tapers
and any linear element the affine test rejects: the fit band DC to `f_max`; passivity checked on a grid at least 10×
denser than the samples and beyond them to `f_max`; enforced by the smallest residue perturbation that restores it,
reported per element with the fit error. A fit that misses its tolerance is refused naming the element. Data stopping
short of DC is extrapolated from its lowest points and data stopping below `f_max` is held, each with a note.

**R-trn4-5 — EM cells.** `model=em` on a tran analysis runs each EM-capable instance through its cached EM result,
fitted by R-trn4-4, exactly as an S-parameter run would select it.

**R-trn4-6 — Tone source terminations.** A `P1Tone`/`PnTone` with harmonic terminations `Z[k]` is realized by
R-trn4-4's fit of its impedance, lifting TRN-2's refusal.

**R-trn4-7 — Refusals.** Note §5.5's list stays refused by name, each sentence naming the analysis that supports it.

## Gates

1. **Lossless reflection.** A pulse into a 50 Ω line terminated in 100 Ω: the reflection arrives at 2τ with amplitude
   1/3 within 1e-6, on the step it should.
2. **Coupled lines.** A lossless coupled pair: near- and far-end crosstalk equal the modal closed form within 1e-6.
3. **Lossy line against the frequency domain.** The step response's transform equals the S-parameter engine's S21 of
   the same line within R-trn4-3's tolerance up to `f_max` (or the brief's stop-and-report).
4. **SnP.** A committed band-pass `.s2p` driven by a pulse: the output's transform equals S21 within the fit's reported
   error; a deliberately non-passive file is made passive and the perturbation reported.
5. **Long run stability.** Gate 4's bench run for 10⁴ of its slowest time constants does not grow (energy bounded).
6. **Envelope unchanged** after the line delay and rational fit moved down: the envelope test classes and fixed files.

## Not here

Lossy coupled lines, a time-domain convolution with measured impulse responses, translating any of this to the dialect
(TRN-6 refuses it; `SpiceFit=yes` in TRN-6 writes the realization).
