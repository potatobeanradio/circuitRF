# Brief PAA-8 — speed: the identical-channel fast path and sliders that keep up

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa8-<n>` · **Depends on:** PAA-4 · **Design note:** §6
**Area:** `src/Engine/Array/`, `src/Design/Em3d/FieldCosimulation*.cs` (the evaluation a slider and the optimizer call),
the tuning path (`src/Ui/` tuning sliders), `src/Engine/RESOLVED.md`.

## The gap

A designer steering an array wants the pattern to follow the hand. Through PAA-4 every change of a weight re-elaborates
and re-solves the whole circuit — at 1024 elements with a channel cell, about ten thousand nodes per frequency — and then
sums the pattern. Most of that work does not depend on the weights: when the channels are identical their two-port is the
same for every element, and the array's closure can be factorised once. None of that is exploited, and nobody has
measured what a moving slider costs at 64, 256, 1024 and 4096 elements.

## Requirements

**R-paa8-1 — Measure first.** A scratch harness (not a test) times, in Release and in Debug, one evaluation of a
Feed-mode array at N = 64, 256, 1024 and 4096 with and without coupling and with and without an identical channel: elaboration,
circuit solve, sparse closure factorisation and back-substitution, the overview sum at 1° and at 2°, the local
evaluation (cuts and peak patch), and the (u,v) FFT. Recorded in `src/Engine/RESOLVED.md` with the machine. Repeated
after R-paa8-2…4 on the same harness.

**R-paa8-2 — Identical channels solve once.** When PAA-4 marked the channel identical, the evaluation solves the channel
two-port once per frequency and closes the array with `a = (I − Γ_ch·S)⁻¹·t·w` (note §6.3): a **sparse** factorisation of the extended S's pattern (dense only at `exact`, where N is small) that
does not depend on the weights, kept across evaluations whose only change is the weights, and a sparse
back-substitution per change. The factor's fill-in at 4096 is recorded; if it is large, reorder before factorising
(CSparse.NET's orderings) and record which ordering was chosen. With `Coupling=off` the closure is diagonal. Anything else — a channel that is not identical, a nonlinear channel
in HB — takes the full solve, and `explain` says which path a bench takes and why.

**R-paa8-3 — What counts as "only the weights changed".** The evaluation compares the resolved values of everything
except `Amp`, `Phase`, `Delay`, their overrides, `PhaseBits`, `AttenStep` and `Off` with the previous evaluation's; equal
means the factorisation is reused. No cache keyed on expression text: a VAR that moves a channel value invalidates it,
a VAR that moves only `theta0` does not.

**R-paa8-4 — A slider that keeps up.** A tuning slider on a VAR that reaches only the weights re-evaluates through
R-paa8-2 and redraws the polar cut and the 3D pattern on every move. Where the fast path does not apply, the drag shows the
pattern formed from the last solved waves with the new weights applied at the beamformer, labelled "estimated", and solves
on release — the CP-4 precedent of `psSAR` waiting for release. The optimizer and yield use the same evaluation, so an
optimisation of the weights gains the same speed.

**R-paa8-5 — What a drag redraws.** A drag redraws the local evaluation (cuts and readouts) and the (u,v) map on every
move, since those carry the beam; the 3D overview may be formed at 2° during the drag and at the record's grid on
release, if R-paa8-1's numbers show it is what limits the frame at 4096; otherwise not. The decision and its numbers go in
the completion note.

## Gates

1. **Counters, not timing.** At N = 4096 with an identical channel: a sequence of ten evaluations moving only `theta0`
   performs one channel solve per frequency and one closure factorisation, and ten back-substitutions; moving a channel
   VAR once adds one solve and one factorisation.
2. **The fast path is the slow path.** At N = 256 and at 4096 (coupling on, from E5) the fast path's pattern and Γ_act equal the full solve's to 1e-10, with
   and without coupling.
3. **Fallback is said.** A channel whose attenuator depends on `n` takes the full solve, and `explain` names the parameter
   that made it non-identical.
4. **The numbers.** R-paa8-1's table before and after is in `src/Engine/RESOLVED.md`. No timing assertion.

## Not here

GPU evaluation of the pattern; a (u,v)-space FFT preview (PAA-9 may use one for its map).

## Completion note (2026-11-14)

Built: `src/Engine/Array/PhasedArrayFastPath.cs` (the split, the channel's two-port, the closure, the cache, the counters and the
estimate) and `ArrayFeedLoadModel.cs` (the closed array at F); `FieldCosimulation.Array.cs` takes the fast path for every Feed-mode
fields evaluation (a run, a sweep point, a slider, the optimizer and yield alike) and the estimate for a drag off it; `explain --analysis`
prints the path and why (`ExplainArrayJson.Solve`), and a channel that differs per element names the parameter that reads the element
(`PhasedArrayChannels.Sites`); the Tuning panel's time readout says `· estimated` while a drag shows one. Numbers, the ordering and the
findings: `src/Engine/RESOLVED.md` § brief-paa-8.

- **Gate 1:** ten moves of theta0 at 4096 elements with an identical channel make 1 channel solve, 1 factorisation (1,693,644 non-zeros in
  L and U for 313,232 in the matrix) and 10 back-substitutions; moving the channel's VAR adds one of each. Routine, 2 s.
- **Gate 2:** the fast path against the full solve, pattern, Γ_act, a and the circuit's S: 256 elements coupled 1.0e-14 / 8.8e-14, not
  coupled 1.1e-15, coupled with three elements off 1.0e-14 / 1.1e-13; 4096 coupled 7.6e-15 / 6.9e-14 (`Category=Benchmark`, 5.9 s, the
  full solve it is compared with is the cost). S is compared absolutely: the channel's amplifier is unilateral, so F sees ~1e-16.
- **Gate 3:** `Loss=3+0.1*n` takes the full solve; explain prints *Full solve: channel cell 'chan' differs per element: it reads n, in
  T1.Loss=3+0.1*n; …*.
- **Gate 4:** the before and after tables are in RESOLVED; no timing is asserted.
- **R-paa8-5's decision: no coarse overview during a drag.** At 4096 the overview with its kept tables is 7-24 ms against ~56 ms of
  elaboration, which is what a move now costs most; a 2° overview would not move the frame.

Deviations and things to know:

- **"With no channel" needs no closure at all**, coupled or not: the beamformer is matched and isolating, so nothing returns to another
  element. The factorisation exists only for an identical channel that reflects (S22 ≠ 0) on a coupled array.
- **Off is handled against the kept factor (Woodbury)** rather than refactorised, so R-paa8-3's list holds literally; past 256 elements
  off the set is factorised itself.
- **The fast path also refuses** a nonlinear part anywhere (the S-parameter run linearises about an operating point the whole circuit
  sets), a complex Z0, a channel reaching a net outside itself and a circuit wired inside the array; each is named by explain.
- **Found and fixed:** every array fields run solved the whole circuit twice (waves, then the S cube), and the phase tables for the
  record's grid were rebuilt at every evaluation (`ArrayDirections.OnGrid` made a new set per call).
- **Not done: elaboration at 4096** (~56 ms with a channel) is now the largest part of a move; a brief of its own if a 4096-element
  slider has to go faster.
- **The estimate's label was not captured mid-drag**: UiSnapshot's drag does not move the tuning slider headlessly. The readout was
  rendered with the suffix forced to check it fits; the readout's minimum width grew from 80 to 104 so the dot does not move when it
  appears. The estimate itself is gated (`ADrag_OffTheFastPath_IsEstimated_AndTheReleaseSolves`).
