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
