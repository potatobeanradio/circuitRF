---
name: project-brief-var-unit-wins-consistency
description: Part A+B var-unit-wins sweep fix — ParametricSweepEngine injects swept override with base unit; Evaluator.Eval skips site unit when expr refs unit-bearing var
metadata:
  type: project
---

Brief-var-unit-wins-consistency — completed 2026-06-23.

**Bug fixed:** Parametric sweep over a frequency variable (e.g. `Var=RFfreq Unit=GHz`) ran HB at 1e18 Hz instead of 1e9 because the override was injected unit-less → `GlobalsWithExplicitUnit` didn't contain it → `FreqUnit.ResolveHz` re-applied ToneUnit → double-apply. Same latent bug in no-sweep `RFfreq=2 GHz` + `Freq=RFfreq GHz` → 2e18.

**Part A** (`src/Engine/ParametricSweepEngine.cs`): compute `effUnit = sweep.Spec?.Unit ?? origVar?.Unit ?? ""` and `baseUnit = Units.BaseUnit(effUnit)`; inject override as `new Variable(name, value, baseUnit)` — scale-1, marks the variable in `GlobalsWithExplicitUnit`. Axis tagged with same `baseUnit`.

**Part B** (`src/Core/Expressions/Evaluator.cs`): `Eval(expr, scope, unit)` calls new `ReferencesUnitBearingVar(ast, scope)` (uses `AstWalker.CollectRefs` + `Scope.Lookup`); skips site unit when any referenced variable has non-empty unit in scope.

**Tests:** 5 Core.Tests (`EvaluatorVarUnitWinsTests.cs`) + 3 Engine.Tests (`SweepFreqVarDoubleUnitTests.cs`). Build 0W/0E; 375+425+1398+4=2202 total pass.
