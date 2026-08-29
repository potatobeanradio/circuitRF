# Brief P8 — a physical floor on AIM's near radius, and the ladder that failed

**Problem.** `PlanarAimSettings.NearRadiusFactor` is expressed in units of the largest basis
support. `brief-em-aim-ceiling.md`'s fixed-footprint ladder refined the mesh, which shrinks the
largest support and therefore the near radius in METRES; GMRES climbed 21 → 143 → 372 iterations
and failed at cells/λ = 140 (N = 13,967). The 12,000 ceiling was set with margin under that failure.

**The physics offers an explanation.** The scalar (charge) kernel on a grounded slab is
`1/ρ − 1/√(ρ² + 4h²)` plus smooth terms: its coupling only falls like `2h²/ρ³` beyond ρ ≈ 2h. A
near-field preconditioner that spans less than the image depth is missing the dominant low-frequency
coupling, and that is exactly what a refined mesh at fixed footprint does to a radius measured in
supports. Nothing about the AIM projection's accuracy is at stake — `|ΔZ|` stayed small; the
preconditioner is what degraded.

Read first: `PlanarAim.cs` (`PlanarAimSettings`, `NearSet`, `FactorNear`); HISTORY's closing AIM
subsection (grep `AcceleratedUnknownCeiling`) — both ladders; `CLAUDE.md` §8's AIM paragraph. Do P6
first, or every rung of the ladder pays the per-frequency geometry rebuild.

## Milestones

1. **Reproduce the failing ladder** (fixed footprint, cells/λ 80/100/120/140) at the shipped radius,
   recording iterations, near entries/row, `PreconditionerNonZeros`, and — new — the near radius in
   metres and in units of `h`.
2. **Add `NearRadiusMinM`** (default: `2·h` of the slab, derived, not a magic number — say why in the
   doc comment) applied as `max(NearRadiusFactor·maxSpan, NearRadiusMinM)`. Re-run the ladder.
   Expected: flat iteration count. If it is not flat, try `4h`; if that is not flat either, the
   hypothesis is refuted — record it in `CLAUDE.md` §6's negative-results list (a one-line correction
   of the existing AIM paragraph, not a new section) and stop.
3. **Cost.** The floor widens the near field on fine meshes: record near entries/row, near fill time
   and preconditioner nnz with and without the floor on the healthy (length) ladder too, so the
   price on the meshes that were already fine is known. Gate: on the length ladder the floor must
   change nothing (the radius there already exceeds 2h) — assert the near set is identical.
4. **Accuracy** (`AimAccuracyTests`' `|ΔI|` measure) at the top rung with the floor.
5. **Re-ask the ceiling**: with a flat ladder, does 12,000 still sit at "the healthy construction's
   top rung with margin", or can it move? Write the number and the sentence; **the decision is the
   owner's**.

## Must NOT

- Change the projection order, the pitch rule, or the preconditioner's factorisation.
- Move `AcceleratedUnknownCeiling` yourself.

## Gates

The two ladders before/after in `HISTORY.md`; the near-set-identical assertion on the length ladder;
`AimAccuracyTests` green; `RESOLVED.md` write-up; `docs/design/mom-engine.md` §10.7's ceiling note
gains the `> Built at P8` sentence.
