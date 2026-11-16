# Brief TRN-8 — transient-assisted harmonic balance and oscillator start-up

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn8-<n>` · **Depends on:** TRN-1, TRN-4, TRN-5 · **Design note:** §2 D4, §10,
§12.6
**Area:** `src/Engine/HarmonicBalance` (`HbEngine` warm start, `OscEngine`), `src/Engine/Transient` (the seed run),
`AnalysisDirectiveSchema` (`hb`, `osc`), `ParametricSweepEngine`, the HB and osc analysis cards, `src/Cli` (`explain`),
`docs/design/harmonic-balance.md`, `src/Engine/RESOLVED.md`.

## The gap

Harmonic balance starts from the DC point and steps the drive up. A switching PA, a hard-driven mixer or a divider whose
steady state is far from that path can fail to converge or converge to the wrong solution, and an oscillator needs a
frequency guess the designer has to supply.

## Requirements

**R-trn8-1 — `InitialGuess=dc|transient|fallback`** on `hb`, default `dc` (no existing result changes). `transient`
seeds per note §10.2: DC point, native transient at full drive with step `T/(8·(MaxHarm+1))`, until consecutive periods
of every interface waveform agree within `SeedTol` (default 1e-3) or `SeedPeriods` (default `auto`, capped at 200),
then the last period's `fft` on the interface as `V[N, K+1]` handed to `HbEngine` as `warmStart`, with drive stepping
off. `fallback` tries `dc` first and seeds only on non-convergence.

**R-trn8-2 — Fast-bias scaling**, `SeedFastBias=yes` by default, per note §10.2: identify each L and C whose reactance at
the fundamental makes it an RF choke or block (|X| above 10× or below 0.1× the impedance it sees at the DC point);
scale it in the seed run only so its time constant is a few periods while its reactance stays beyond the threshold;
report the scaled elements. Harmonic balance solves the unscaled circuit.

**R-trn8-3 — Tones.** Single fundamental, or commensurate tones seeded over their common period. Incommensurate
multi-tone: `transient` refused with that reason; `fallback` uses `dc` with a note.

**R-trn8-4 — Sweeps.** In a parametric sweep, only the first point and any point that fails are seeded; the rest
warm-start from their neighbour as today.

**R-trn8-5 — Oscillators.** `InitialGuess=transient` on `osc` makes `Fguess` optional, per note §10.3: a kick of `KickV`
(default 1 % of the largest DC supply) at the OscProbe's node released at t = 0⁺; run until the probe amplitude is
steady within `SeedTol`; frequency from level crossings, amplitude from the fundamental, both handed on as `Fguess` and
`Aguess` with the interface waveforms as the harmonic start. No oscillation within the cap: a refusal that includes the
probe's small-signal start-up result.

**R-trn8-6 — Reported.** `Seed.Periods`, `Seed.Settled`, `Seed.ScaledElements`, `Seed.HbIterations`, and the note
"harmonic balance started from a transient seed". `explain --analysis` states the seed settings and the estimated seed
length.

**R-trn8-7 — Cards.** `InitialGuess` on the HB and osc cards, the seed keys under More. UiSnapshot PNG, looked at.

## Gates

1. **Class-E.** A class-E stage (TRN-3's switch, or a built-in FET law driven hard) that harmonic balance does not
   converge from DC with `DriveStepping=Never`: with `InitialGuess=transient` it converges, and where a drive-stepped
   run also converges the two solutions agree within 1e-6 relative on every interface harmonic.
2. **Fewer iterations.** On gate 1's bench, `Seed.HbIterations` is lower than the drive-stepped run's total Newton
   iterations (a counter, not a time).
3. **Divider.** A regenerative ÷2 divider: from `dc` harmonic balance on the output's subharmonic grid finds the trivial
   (undivided) solution; from `transient` it finds the divided one, confirmed by a long native transient's `fft`.
4. **Fast bias.** A PA with a 10 µH choke and a 1 µF block at 2 GHz seeds within the 200-period cap with scaling and
   does not settle without it; the final HB solution is identical either way.
5. **Oscillator.** A shipped oscillator example without `Fguess` finds the frequency within 1e-6 relative and the
   amplitude within 1e-4 of the run with `Fguess`.
6. **Unchanged default.** Every existing HB and osc gate passes with no `InitialGuess` key.

## Not here

A shooting (periodic-steady-state) analysis, seeding envelope runs, seeding loadpull beyond what its HB points inherit.

## Completion note (2026-11-15)

Built as specified, with these choices. The settle test extrapolates the period-to-period change (Δ/(1 − ρ)) because plain
consecutive-period agreement passes a slowly charging bias network. Fast bias scales chokes and blocks to a reactance ratio of
50 (an 8-period time constant against the impedance seen): 20 settled faster but left the seed far enough off to double the
Newton iterations. Harmonic balance's line search converges every hard-driven stage tried from DC, so gate 1 states an
iteration budget (`MaxIter=20`), as an existing drive-stepping test does. An oscillator's seed uses the small-signal start-up
frequency (a 1 kHz to 1 THz search without Fguess) for its step, cuts the probe signal into cycles and settles on period and
amplitude. The native transient could not run any BJT circuit (its interface Newton condensed a singular linear partition at the
transistor's internal nodes); `Auto` now takes the full path there, which gate 5 needed. An idle OscProbe now steps in transient.
Gates: `tests/Engine.Tests/HarmonicBalance/TransientSeedTests.cs` (gates 1-4, incommensurate tones, the sweep) and
`tests/Ui.Tests/Transient/TransientSeedSurfaceTests.cs` (gate 5 on the shipped VCO, the keys through .cnl, .csch and both cards,
malformed keys). Findings in `src/Engine/RESOLVED.md`; design in `docs/design/transient.md` §10.3a and
`docs/design/harmonic-balance.md` §10.
