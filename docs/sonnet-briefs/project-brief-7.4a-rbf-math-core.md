---
name: project-brief-7.4a-rbf-math-core
description: Phase 7.4a: Rbf2D (scipy-compatible multiquadric RBF, LDLᵀ solver) + Interp1DLinear in RfCore; 15 gate tests + 4 perf tests; 143 total — completed 2026-06-20
metadata:
  type: project
---

Phase 7.4a completed 2026-06-20.

**Deliverables:**
- `RfCore/src/Loadpull/Rbf2D.cs`: 2-D RBF interpolant with multiquadric/thin-plate/gaussian kernels, scipy epsilon convention, smooth=-diag, NaN-drop, allocation-free LDLᵀ solver; Complex-overload and batch Evaluate.
- `RfCore/src/Loadpull/Interp1DLinear.cs`: 1-D linear interp matching scipy interp1d(bounds_error=False) → NaN out-of-range.
- `RfCore/tests/RfCore.Tests/Rbf2DTests.cs`: 11 correctness gate tests (epsilon, self-consistency smooth=0, NaN-drop, constant field, monotone field, hand-verified example, thin-plate, Gaussian, complex overload, batch eval, node accessors, phi formulas).
- `RfCore/tests/RfCore.Tests/Interp1DLinearTests.cs`: 7 gate tests (midpoint, non-uniform, node values, out-of-range NaN, batch eval, unsorted throws, endpoints).
- `RfCore/tests/RfCore.Tests/Rbf2DPerfTests.cs`: 4 perf tests (fit N=20 <0.2ms, fit N=200 <5ms, eval 50×50 @ N=200 <5ms, full surface <10ms) — all passed.

**Scipy conventions matched:**
- Epsilon: `(prod(non-zero axis ranges) / N) ^ (1/dims)`, `edges != 0` filter
- Smoothing: `A[i,i] -= smooth` (MINUS sign)
- Multiquadric: `phi(r) = sqrt((r/eps)^2 + 1)`

**Total tests:** 143 (142 pass; 1 pre-existing DataCube failure unrelated to this phase).

**Why:** How to apply — Rbf2D/Interp1DLinear are the foundation for brief 7.4b (LoadpullSurface). The LDLᵀ solver is custom (not NumFlat, not CSparse) per the design §2.4 perf decision.

**GOLDEN note:** Brief requests owner supply scipy CSV for numerical parity. Placeholder comment in Rbf2DTests.cs lines 211-225. Self-consistency suite (smooth=0 reproduces nodes to 1e-6) is the primary gate until CSV is provided.
