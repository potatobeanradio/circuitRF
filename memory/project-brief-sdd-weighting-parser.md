---
name: project-brief-sdd-weighting-parser
description: SDD weighting parser (brief #3): I[p,w≥2] + H[w]=expr pipeline from netlist → engine; 10 gate tests; completed 2026-06-19
metadata:
  type: project
---

SDD arbitrary weighting `I[p,w≥2]` + `H[w]=expr` (brief #3 of SDD weighting series) — COMPLETE 2026-06-19.

Depends on brief #1 (equivalence test) and brief #2 (engine generalization — WeightedTerm/NonlinearResult.Terms + ComponentModel.Weight).

**Changes:**
- `SddModel.cs`: `_higherAst[][]` (per-port w≥2 bucket lists) + `_weightAst` (w→H[w] AST); `Evaluate` emits one `WeightedTerm` per distinct w; `Weight(w,ω)` override evaluates `H[w]` via Complex `Evaluator` with `freq=ω/2π` bound; `EvalWeight` helper; ctor gains 2 optional params.
- `ComponentModelFactory.cs`: drops v1 w≥2 hard-error; `RxWeightFn` regex; H[w] parsing + cross-validation.
- `CnlReader.cs`: `SddAssignmentHeader` extended to match `H\[\d+\]`.
- `Elaborator.cs`: `RxSddEquation` extended with `H` (char class `[IFCQiH]`).
- `SddModelTests.cs`: updated `WeightingW2_ThrowsHardError` → `WeightingW2_MissingH_ThrowsCrossValidationError`.

**New test files:**
- `tests/Core.Tests/Devices/SddWeightingParserTests.cs` — 8 tests (parse, complex H, missing H error, built-in redefine error, regression)
- `tests/Engine.Tests/HarmonicBalance/SddWeightingParserE2eTests.cs` — 2 tests (HB equivalence w=1 vs w=2, S-param equivalence)

**Key design note:** Two deliberately separate evaluators:
- `I[p,w]` (time-domain, voltage-controlled, real, dual-AD) → `SddEvaluator`
- `H[w]` (frequency-domain, freq-controlled, Complex) → `Evaluator`

**Why:** `H[0]=1`, `H[1]=jω` are built-in; `w≥2` are user-defined frequency-domain functions.
**How to apply:** When adding SDD features, maintain the two-evaluator separation.

Related: [[project-brief-sdd-weighting-engine]] [[project-brief-sdd-nonlinearc-equivalence]]
