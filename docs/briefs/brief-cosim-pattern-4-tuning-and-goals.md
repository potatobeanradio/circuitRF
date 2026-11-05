# Brief CP-4 — Tuning sliders, optimizer and yield on the pattern

**Series:** `brief-cosim-pattern-0-overview.md` · **Tag:** `R-cp4-<n>` · **Depends on:** CP-3
**Area:** `src/Design/Em3d/FieldCosimulation.cs`, `src/Design/Em3d/CosimFieldMetrics.cs`, `src/Core/Expressions/FieldMetrics.cs`,
`src/Design/Optimization/GoalTemplates.cs`, `src/Ui/Tuning/TuneSession.cs`, `src/Ui/DataDisplay/`, `docs/design/tuning-optimization.md`

## 0. Why

CP-3's analysis runs on Simulate. The value of the method is that a part's value can move and the pattern follows it, and that goals
can be set on the result. SAR-10 built those paths; this brief puts the far field on them.

## 1. Requirements

**R-cp4-1 — The three outputs.**

| `FieldsOutput` | Who asks | Far field does |
|---|---|---|
| `Written` | Simulate, a slider's release, the optimizer's final point | Full cubes and metrics |
| `Local` | A slider moving | Full cubes and metrics, nothing on disk |
| `OnDemand` | Optimizer, yield, centering, DOE | The waves only; a metric computes what it reads |

**R-cp4-2 — A plot follows the slider.** `TuneSession` publishes through `ITuneResultSink`. An open polar cut, rectangular cut and 3D
pattern surface follow without a reload, and keep their zoom, floor and pinned axes.

**R-cp4-3 — Measure the drag.** At 1° with three ports and one frequency, measure one evaluation in a scratch harness: circuit solve,
sum, metrics, publish, redraw. Record the split in `RESOLVED.md`. If metrics dominate, a moving slider may compute only what an open
plot or a live goal reads. The existing **run on release** suggestion remains the slow case's answer, and no timing test is added.

**R-cp4-4 — Functions.** Add to `FieldMetric`, each by the name of the existing cube so one word means one thing:

| Function | Value |
|---|---|
| `RealizedGainDbi(F)`, `GainDbi(F)`, `DirectivityDbi(F)` | The peak at the analysis's frequency |
| `RealizedGainDbi(F, theta, phi)`, and the same for the other two | That direction, on the published grid |
| `RadiationEfficiency(F)` | Percent, as the cube is |
| `TrpDbm(F)`, `PeakEirpDbm(F)` | As the cubes are |

A `freq=all` analysis refuses a scalar function by name and points at one-frequency analyses, or at `goal … over=freq` if the goal
grammar supports it when this is built.

**R-cp4-5 — Goal editor.** `GoalTemplates.FieldsTemplates` lists a pattern's functions when the block's record has a far field and SAR's
when it has a field set, by what the record holds rather than by a fixed list.

**R-cp4-6 — Tunables.** `explain --tunables` is unchanged: part values are already tunables, and the block contributes its existing
entries. S offsets on this block stay refused.

## 2. Gates

- A slider's evaluation and Simulate at the same value give identical cubes.
- `OnDemand`'s `RealizedGainDbi` equals the cube's value from `Written`.
- One optimizer run on a synthetic block moves a part to a known best value, starting no EM run (`CosimNoEmRunTests` holds the counters
  already).
- UiSnapshot of a polar cut before and after a value change, inspected.
