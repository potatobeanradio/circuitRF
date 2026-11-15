# Brief PAA-8b — a move that reaches only the weights does not elaborate

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa8b-<n>` · **Depends on:** PAA-8 · **Design note:** §6.3, §6.4
**Area:** `src/Core/Elaboration/` (the re-weigh and the read-set check), `src/Core/Elaboration/ElaboratedNetlist.cs` (a copy of an
elaborated netlist), `src/Design/Circuit/CircuitEvaluation.cs` and `PreparedCircuit.cs` (the kept netlist), `src/Core/RESOLVED.md`.

## The gap

PAA-8 made the circuit half of a weights-only change cheap: at 4096 elements, coupled, with an identical channel, the solve is a
back-substitution and the whole fields evaluation about 8 ms. A tuning-slider move still costs about 70 ms, because every
evaluation elaborates the bench again first: 4096 element scopes, the coupled array's network, and 12,288 channel components.
That caps a 4096-element slider near 14 frames a second, below the 20 a hand-steered beam needs. The PAA-9 scan maps
(R-paa9-5) evaluate hundreds of steering directions; each pays the same elaboration.

Measured on the PAA-8 harness (Apple M4, Release), elaboration at 4096: 31–39 ms with no channel and coupling on, 12.5 ms with
coupling off, 57–69 ms with an identical channel. The weight expressions themselves cost a few milliseconds of that. A move that
changes only a weight therefore re-does tens of milliseconds of work whose result it already has.

## Requirements

**R-paa8b-1 — What reaches only the weights.** A change of tuned values reaches only the weights when every value that changed
is a global variable, and every expression that reads it, directly or through other global variables or user functions, is an
`Amp`, `Phase` or `Delay` field (or an `[n]` override of one) of a Feed-mode Phased Antenna Array placed at the top level of the
bench, or a measurement or goal. Any other reader — a cell's parameter, variable or instance, another instance's parameter, the
array's `PhaseBits`, `AttenStep` or other parameters, an analysis directive — means the bench is elaborated as before. The check
reads the design's expressions as parsed names (the expression engine's tokenizer), never by substring.

**R-paa8b-2 — The re-weigh.** When a change reaches only the weights, the evaluation does not elaborate. It takes the netlist of the
last full elaboration of the same circuit and makes a new one that shares every component with it except each affected array's
beamformer, whose weights are evaluated again by the same function the elaborator uses (element scope, overrides, quantisation),
with the global variables resolved again from the tuned values. Everything the elaboration said — warnings, notes — is carried over;
the beamformer-gain warning is decided again from the new weights. The result is the full elaboration's to rounding: the same
pattern, Γ_act, waves and measurements.

**R-paa8b-3 — When the kept netlist is not used.** A bench whose elaborated netlist holds a part that releases something outside
the process when disposed (an external device) is always elaborated, since two netlists share its parts. So is an evaluation that
sets values outside the tunables (`--set`), draws statistics (yield), differs in its EM switch from the one kept, or plans an EM
solve. Two evaluations of one circuit at once (the optimizer's parallel points) never share a kept netlist: the second elaborates.

**R-paa8b-4 — Counted, and said.** The circuit counts its elaborations (it already does) and its re-weighs. `explain --analysis`
needs no change: re-weighing is how an evaluation is run, not something the bench declares.

## Gates

1. **Counters, not timing.** At 4096 elements with an identical channel, ten tuning evaluations moving only `theta0` make one
   elaboration and nine re-weighs (and PAA-8's one factorisation and ten back-substitutions); moving the channel's VAR once adds an
   elaboration. A VAR read by a cell's parameter, by `PhaseBits`, or by an analysis's frequency elaborates every time.
2. **The re-weigh is the elaboration.** At 256 elements, coupled with an identical channel, with a measurement reading `theta0`, a
   re-weighed evaluation's pattern, Γ_act, waves and measurement equal a full elaboration's to 1e-12, and its notes are the same.
3. **A scan.** Twenty-five steering directions at 4096 elements evaluated one after another make one elaboration.
4. **The numbers.** PAA-8's per-move table (one tuning evaluation, Debug and Release, 64 to 4096 elements, with and without a
   channel) before and after, in `src/Core/RESOLVED.md`. No timing assertion. The target is 20 moves a second at 4096 with a channel.

## Not here

A parametric sweep of a weight (each point still elaborates, `ParametricSweepEngine`); an array inside a sub-cell (elaborated as
before, said in the completion note); the Data Display's own redraw cost.

## Completion note (2026-11-14)

Built: `PhasedArrayReweigh` (the read-set check) and `Elaborator.Reweigh` in `src/Core/Elaboration`, with `FeedWeights` factored out of
`ElaboratePhasedArrayFeed` so both use one function; `ElaboratedNetlist.Copy` and `NodeMap.Copy`; `ElaborationReuse` on `PreparedCircuit`
(`Reweighs` beside `Elaborations`), held by `CircuitEvaluation.Evaluate` for its run. Numbers and findings: `src/Core/RESOLVED.md` §
brief-paa-8b.

- **Gate 1:** ten theta0 moves at 4096: 1 elaboration, 9 re-weighs, 1 factorisation, 10 back-substitutions; the channel's VAR elaborates.
  A VAR read by the channel's attenuator, by `PhaseBits` or by the fields analysis's `freq` elaborates at every move.
- **Gate 2:** at 256 elements with two off, the re-weighed evaluation equals a fresh elaboration exactly (0 difference) in pattern, Γ_act,
  a, wQuantised and `PointingErrorDeg(F1, theta0, 0)`, with identical notes and warnings.
- **Gate 3:** 25 directions (θ0 × φ0) at 4096: 1 elaboration, 24 re-weighs.
- **Gate 4:** a move at 4096 with a channel 70 → 25 ms Release, 76 → 34 ms Debug; 30-40 moves a second.

Deviations and things to know:

- **Added beyond the brief: a parse cache in `Evaluator`**, since parsing the same weight text 4096 times was the largest part of a
  re-weigh. It applies to every evaluation an evaluator makes.
- **An array inside a sub-cell is elaborated every move**, as the brief allowed; only top-level arrays are re-weighed.
- **A model's once-per-instance warning is said by the first evaluation only** while re-weighs reuse it (`src/Core/RESOLVED.md`).
- **The drawing half of a frame is not measured here** (PAA-9's R-paa9-7 now asks for the window's own; the Data Display's is open).
