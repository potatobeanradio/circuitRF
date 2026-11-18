# PWL-1 — The line-standard calibration, independent of the solver

Series overview: `brief-pwl-0-overview.md`. Read `src/Engine/Mom/CLAUDE.md` §3.4 and §3.5 ("Ports / solve"), and the
headers of `PlanarCalibration.cs`, `PlanarDeembed.cs` and `PlanarModalCalibration.cs`, before starting.

## Why

The planar solver's calibration is already two layers: an ALGEBRA that takes the raw S-parameters of two or more line
standards and returns γ, an error box and a de-embedded answer, and a planar PART that builds those standards from a
planar mesh and solves them with the planar kernel. Only the second is planar. But the first lives in `Mom/`, takes
planar records (`PlanarErrorBox`, `PlanarStandard`, `GroundedSlab`) and is driven by `PlanarPortCalibrator`, which is
inside `PlanarSolve.cs`. A 3D wall port needs exactly the algebra and none of the planar part.

## What to build

A namespace `CircuitRF.Engine.Calibration` (folder `src/Engine/Calibration/`) holding the algebra, taking only
matrices, lengths and frequencies:

1. **γ.** `Gamma`, `GammaBest` and `SelectSeparation` (today on `PlanarCalibration`): cosh(γΔℓ) = ½tr(M) for one
   mode, the palindromic reduction for N, the β-branch continuation and the [20°, 160°] usability window.
2. **The separation plan.** `SuggestDeltas` / `SeparationPlan` / `LongestStandardLengthM` take a `GroundedSlab` today
   only to estimate β. Give them an **ε_eff estimate and a length scale** instead (the slab's height for planar; the
   wall cross-section's for 3D), with the planar call sites passing what they derive today.
3. **The error box.** `SolveErrorBox`, `SolveLumpedErrorBox`, `Apply`, `ApplyBlocks`, `ModalToTerminal`,
   `Renormalise`, `RenormalisePseudoWave`, `CharacteristicImpedance`, and the error-box records, under neutral names
   (`LineErrorBox`, `LineBlockBox`). The planar names may stay as the planar part's own spelling only if the planar
   code is simpler for it; there must be ONE implementation.
4. **The modal group.** `PlanarModalCalibration.Solve` and its helpers (`SToT`, `TToS`, `Flip`, the polynomial,
   `NullVector`, `Orient`), unchanged in substance.
5. **The stateful driver.** What `PlanarPortCalibrator` does per frequency, in increasing frequency order (branch
   state, `GammaBest`, the box, the residuals), as `LineCalibrator`. It is handed, per frequency, the raw S of the DUT
   and of the standards it asked for, and C′ (scalar, or a matrix for a group) from the caller. It does not know how
   either was solved. `PlanarPortCalibrator` becomes the planar caller of it.

The quasi-static calibration path (QSC, `PlanarCalibration.QuasiStaticCrossoverHz` and its single separation) stays
in `Mom/`: it reads the planar kernel's electrostatics directly. What it hands to the shared algebra goes through the
same API.

## Requirements

- **R-pwl1-1 — Bit-identical planar answers.** Every planar run is byte-identical before and after, de-embedded,
  grouped (PCAL4/PCAL5), quasi-static (QSC) and grounded-edge (LFE) alike. The gate is the existing calibration test
  classes run before and after, plus one fixture per path compared as exact equality of the published S.
- **R-pwl1-2 — No planar type in the shared API.** `src/Engine/Calibration/` references nothing in `Mom/`. A source
  scan holds it, as `Firewall.Tests` holds the UI boundary.
- **R-pwl1-3 — The refusals and residuals travel with the algebra**: the palindrome residual, the cascade and
  reciprocal-pair residuals, the mode-separation floor, the a₂₂ sign residual. A 3D caller gets the same diagnostics a
  planar run gets, worded without "planar".
- **R-pwl1-4 — A synthetic gate, no solver.** One test builds a two-port and a four-port "DUT" from known error boxes
  and an ideal (coupled) line in closed form, forms the standards the same way, and recovers the boxes, γ per mode and
  the DUT to round-off through `LineCalibrator` alone. This is the test briefs 3, 5 and 6 build on.

## Not in this brief

Anything 3D. Raising `MaxCalibrationGroupSize`. Any change to what the planar solver computes.

## Completion

Record in `src/Engine/Mom/RESOLVED.md` (§PWL-1) what moved, what stayed in `Mom/` and why, and the planar classes whose
public surface changed. Tests run: the calibration classes named above, `Firewall.Tests`, and the new synthetic gate.
