# Brief P9 — should adaptive frequency sampling be on by default?

**This is a decision brief.** The design note calls adaptive frequency sampling "essential" and
"the best performance investment after the mesh" (§10.7); it is built (L9e/M1, `PlanarAdaptiveSweep`),
gated, and ships `PlanarSolveSettings.Adaptive = null` — off. It is the only lever in this series that
acts on POINT COUNT, so it multiplies every per-point gain the other briefs buy.

Read first: `PlanarAdaptiveSweep.cs` header; `PlanarSolve.Run`'s adaptive branch; `AdaptiveSweepTests`
(especially `T4_2`, the interpolant decision, and the tolerance measurements); the EM panel's
settings surface in `src/Ui/Layout/Em/` (grep `Adaptive`).

## Milestones

1. **Measure what the default would do** on five representative sweeps, alone: the three series
   fixtures plus one resonant structure (a stub or a coupled-line filter from `CoupledLineTests`) and
   one wide-band 1–20 GHz taper. For each: solved points out of requested, worst stopped
   disagreement, and the max |ΔS| of the modelled points against a full solve of the same grid.
   Tolerance 1e-3 (the default) and 1e-4.
2. **State the risk honestly**: where does the interpolant miss a feature? A narrow resonance
   between two solved points is the failure mode; quantify it on the resonant fixture and show what
   `InitialPoints` catches it.
3. **Write the recommendation** — on/off, and if on, with which `Tolerance`/`InitialPoints` — in
   `RESOLVED.md` with the table, and the panel wording that would tell the user a point was modelled
   rather than solved (the result already carries `SolvedPointCount`; the UI must show it).
4. **If the owner says on**: flip the default in `PlanarSolveSettings`, keep the `.cem` field so an
   existing setup that says off stays off, gate that R-adf-1's bit-identity with adaptive OFF is
   untouched, and update the user-facing `docs/user/src/reference/mom-engine.md` (the design note
   says the two must not contradict).

## Must NOT

- Flip the default before the owner's answer.
- Change the refinement criterion or the interpolant.

## Gates

The table; the recommendation; if flipped, `AdaptiveSweepTests` plus the UI gate that the modelled
point count is displayed.
