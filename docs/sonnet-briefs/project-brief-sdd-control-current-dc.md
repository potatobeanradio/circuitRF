---
name: project-brief-sdd-control-current-dc
description: SDD control currents at DC (brief #1): C[n]/Cport[n] parsing, _cn AD seeds, resolver, DControl stamping; 12 gate tests
metadata:
  type: project
---

SDD control currents at DC (brief #1 of control-current arc). Completed 2026-06-19.

**What:** SDD devices can now reference the branch current of a sibling device (`_cn`) in their DC equations. Supports all five referenceable device classes: Vdc, IProbe, Inductor, SnP, Z_Port.

**Files changed (9):**
- `src/Core/Expressions/Dual.cs` — MaxN 8→16
- `src/Core/ComponentModel.cs` — `ControlCurrents` struct, `DControl` on `NonlinearResult`, `Evaluate(v,c)` overload
- `src/Core/Expressions/SddEvaluator.cs` — `EvalDual` overload with control current seeds (`_c{N}` at slot `nV+i`)
- `src/Core/Devices/SddModel.cs` — `Name`, `ControlRefs`, `ControlBranchIndices` properties; 2-arg `Evaluate`
- `src/Core/Devices/ComponentModelFactory.cs` — `C[n]`/`Cport[n]` parsing (removed hard-error); `_cn`↔`C[n]` cross-validation
- `src/Core/Devices/SnpModel.cs` — `PortBranchIndices` populated in `Stamp`
- `src/Core/Devices/ZPortModel.cs` — `PortBranchIndices` populated in `Stamp`
- `src/Core/Elaboration/Elaborator.cs` — skip `_c\d+` in `InjectSddScopeVars`
- `src/Engine/NonlinearDcEngine.cs` — `ResolveControlCurrentBranches`, `ResolveForSdd`, `GetControlBranchIndex`, `ValidateSinglePortBranch`, `ValidateMultiPortBranch`; `DControl` column stamping in Jacobian

**Tests (12) in `tests/Engine.Tests/Nonlinear/SddControlCurrentDcTests.cs`:**
- T1: DC read-through (IProbe current mirrored by SDD via _c1)
- T2: Jacobian exactness (beta*_c1 converges in ≤10 iters)
- T3: Vdc kind resolves
- T4: Inductor kind resolves
- T5: ZPort kind resolves with Cport[1]=1
- T6: Missing instance throws
- T7: Non-referenceable kind (resistor) throws
- T8: Cport on two-terminal device throws
- T9: Missing Cport on multi-port throws
- T10: Cport out of range throws
- T11: `_cn` in equation without `C[n]` throws at factory
- T12: Regression — SDD without C[n] works identically

**Also updated:** `tests/Core.Tests/Devices/SddModelTests.cs` — replaced stale `CurrentControlled_C_ThrowsHardError` test with `ControlRef_CDeclaration_ParsesAndPopulatesControlRefs` (C[n] is now valid syntax).

**Why:** Port=0 sentinel distinguishes "Cport absent" from "Cport=1 explicit" — two-terminal devices accept 0 or 1; multi-port devices require ≥1. Dual.MaxN bumped to 16 to cover portCount + controlCount gradient slots.

Build: 0W/0E. Total: 1966 tests pass (1 pre-existing skip).
