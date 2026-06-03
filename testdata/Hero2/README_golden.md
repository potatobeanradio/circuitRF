# Hero 2 Test Data — Golden Reference Files

## Self-generated files (circuitRF self-consistency, not cross-validated)

| File | Description |
|------|-------------|
| `hero2_self_V_n_gate.csv`    | Interface voltage V at n_gate, all harmonics DC+4, per Pin |
| `hero2_self_V_n_drain.csv`   | Interface voltage V at n_drain, all harmonics DC+4, per Pin |
| `hero2_self_INl_n_gate.csv`  | Nonlinear device current I_nl at n_gate, per harmonic, per Pin |
| `hero2_self_INl_n_drain.csv` | Nonlinear device current I_nl at n_drain, per harmonic, per Pin |

**Sweep:** Pin = -20…0 dBm, step 1 dBm (21 points, 21 converged).
**MaxHarm:** K = 4. **f0:** 2.000 GHz.

**Label: SELF-GENERATED REGRESSION — NOT independently validated** against other simulators. These files freeze the current engine state for regression detection (CI catches any numerical change ≥ 1e-5). An independent cross-check against other simulators with the identical SDD FET is a future task.

## Deprecated files (superseded)

| File | Status |
|------|--------|
| `hero2_golden_reference_n_drain.csv` | **DEPRECATED** — external reference generated with the old Y_DC_VIRT clamped DC (wrong physics). Do not use for validation. |
| `hero2_golden_reference_n_gate.csv`  | **DEPRECATED** — same reason. |
