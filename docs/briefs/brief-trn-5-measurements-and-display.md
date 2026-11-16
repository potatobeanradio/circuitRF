# Brief TRN-5 — time-domain measurements, the Transient preset, steering

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn5-<n>` · **Depends on:** TRN-1, TRN-2 · **Design note:** §8, §11, §12.2
**Area:** the expression engine's axis functions, `MeasurementEvaluator`, the Data Display presets, the tran analysis
card, `src/Cli` (`check`, `explain`), `docs/design/measurements.md`, `src/Core/RESOLVED.md`.

## The gap

A transient result is a set of waveforms with nothing to read off them: no rise time, overshoot, settling, period or
spectrum, no preset that plots them, and nothing that tells a user when a transient is the wrong analysis for their
question.

## Requirements

**R-trn5-1 — Functions.** Note §8's table (`rise`, `fall`, `overshoot`, `undershoot`, `settle`, `period`, `freq`, `at`,
`tmin`, `tmax`, `avg`, `rms`, `fft`, `thd`) in the one expression engine, over any cube's `time` axis, usable in
`measure` lines and traces. Before adding a name, check it against every existing function and variable name; a
collision is renamed here and the note updated, never shadowed.

**R-trn5-2 — `fft` shaped like HB.** `fft(x, f0, periods, window)` returns a harmonic-axis cube of the same shape and
units an HB result's spectrum has, so one trace definition plots either.

**R-trn5-3 — Transient preset.** The Data Display preset: the recorded voltages against time; the output probe's `fft`
in dBm (or dBV on a net); the bench's measures as a table. UiSnapshot PNG of a populated preset, looked at.

**R-trn5-4 — The analysis card**, completed: the More section with every key, word pickers, and the preset offered
after a run.

**R-trn5-5 — `explain` steering.** The line note §11 gives when `Stop` spans more than 10⁵ periods of the highest
source frequency, naming HB or envelope as fits the sources (a modulated source names envelope).

**R-trn5-6 — `check`.** A waveform source whose `Dc` differs from its t = 0 value is info; a `measure` using a
time-domain function on a non-transient result is an error naming the function.

**R-trn5-7 — The transient-versus-HB gate** (note §12.2), below.

## Gates

1. **Functions on known waveforms.** Each function against a synthetic waveform with a closed-form answer: an ideal
   first-order step (rise 10–90 % = τ ln 9), a second-order step (overshoot from the damping ratio), a sine (period,
   rms, thd = 0 within 1e-9), a clipped sine (thd from its Fourier series).
2. **Transient equals HB.** A weakly nonlinear amplifier (a built-in FET law in a lumped matching network), driven by a
   tone and run to steady state, its last period through `fft`: harmonics 1–5 equal the HB solution within 0.01 dB
   and 0.1°.
3. **Same trace, two results.** One trace definition plots the HB spectrum and the transient `fft` without edits.
4. **Steering.** `explain` on a 2.4 GHz tone over 1 ms prints the steering line naming HB.

## Not here

Eye diagrams, jitter measures, histograms over a sweep (a parametric sweep's existing axis functions cover the last).
