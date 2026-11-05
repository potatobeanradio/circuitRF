# Brief CP-5 — A parametric sweep wraps the pattern

**Series:** `brief-cosim-pattern-0-overview.md` · **Tag:** `R-cp5-<n>` · **Depends on:** CP-3
**Area:** `src/Engine/ParametricSweepEngine.cs`, `src/Design/Circuit/CircuitEvaluation.cs`, `src/Design/Em3d/FieldCosimulation.cs`, `src/Core/Elaboration`
(chain selection, if it refuses), `src/Render/DataDisplay/`, `src/Cli/PlotVerb.cs`

## 0. Why

A slider shows one value at a time. Comparing a pattern at several values on one plot needs a sweep axis, and `cosimulation.md` lists
"No parametric sweep wraps a fields analysis" as a limit. For a pattern that limit has no physical basis: each point is one circuit
solve and a sum.

## 1. Requirements

**R-cp5-1 — Layering.** `ParametricSweepEngine.RunInner` is in `src/Engine` and `FieldCosimulation` is in `src/Design`, so the engine cannot
call it. Give the engine one optional delegate, `Func<Analysis, ElaboratedNetlist, RunControl?, DataSet>`, consulted for an inner it does
not dispatch. `CircuitEvaluation`'s `ParametricSweepAnalysis` case supplies it for a `FieldsAnalysis`. A run with no delegate behaves
exactly as it does today. Do not move far-field reading into the engine.

**R-cp5-2 — What it wraps.** A far-field-only analysis. A block that also has a SAR field set keeps today's refusal for SAR's reasons
(volumes written per point, cube averaging), and the sentence names the limit that applies. Update `explain --analysis`, the promotion of
a named inner analysis, and the run's point count, so they all agree.

**R-cp5-3 — Cubes.** `StackSweepAxis` already prepends to the default group's keys, which is where CP-3 puts them. The result is
`[L1, freq, theta, phi, port]`, the sweep's own unit on its axis. A nested sweep adds a further axis. A failed point follows the sweep
engine's existing NaN rule.

**R-cp5-4 — Size, said before the run.** A direction costs about 72 bytes across the pattern cubes, so a 1° sphere is about 4.7 MB per
frequency per sweep point. State the total in the analysis's run summary. Warn above 2 GB, naming `freq=` as the remedy, since `freq=all`
under a sweep is the way to reach that number.

**R-cp5-5 — The plot.** A pattern cut over a sweep axis draws one curve per value, labelled with its value and unit, on polar and
rectangular plots. Confirm the existing family mechanism does this for an angle plot, including the back branch of a polar cut
(`Trace.MirrorPatternAngle`); fix only what does not. The 3D surface pins the sweep axis with the existing selector. `plot --trace
cube=F1.farfield.U,cut=0,…` accepts the axis as it does any other. Peak metrics plot against the sweep axis with no special case:
`F1.farfield.RealizedGainDbi` over `L1`.

**R-cp5-6 — Doc.** Correct the limit in `cosimulation.md`, with SAR's limit kept.

## 2. Gates

- A sweep's point equals a single run at that value, cube for cube, to 1e-12.
- A SAR block's refusal is unchanged, and a sweep with no delegate is unchanged.
- UiSnapshot of a polar cut with three values, inspected: distinct curves, a legend with units, nothing clipped.
