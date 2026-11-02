# Brief MC-8 — The charge constituents and Check Charge Conservation

**Series:** `brief-modelcomposer-0-overview.md` (D4, D7, D13) · **Tag:** `R-mc8-<m>` · **Depends on:** MC-2, MC-4, MC-6
**Area:** `src/Design/Modeling/` (charge fit flows, the conservation checks); `src/Engine/Modeling/`
(line integrals, curl metrics)
**Read first:** [P1] §6.3 in full — eqs. 6.19–6.31, §6.3.6 (Fig. 6.17–6.19), §6.3.7 (eq. 6.32 and Fig. 6.21 —
why a capacitance model fails in HB), §6.3.8 (transcapacitance is allowed); [GSF19] (steady-state multi-bias S is
not isodynamic, so measured C fails eq. 6.27); **[I11]** for R-mc8-3

---

## 1. Goal

`Qg` and `Qd` as charge functions of the intrinsic voltages, built by the adjoint ANN by default. And **one button**
that tells the user how charge-conservative their data is, their model is, and any circuitRF device is.

## 2. Requirements

**R-mc8-1 — Training data.**
- The intrinsic `C11, C12` (for `Qg`) and `C21, C22` (for `Qd`), each averaged over the flatness band of MC-4
  R-mc4-5, at the intrinsic bias points. Points whose flatness is poor are down-weighted by a stated rule and
  reported.
- The anchor is `Qg = Qd = 0` at a reference bias (default `vgs = vds = 0`, or the nearest valid point) — the
  arbitrary constant of [P1] §6.3.4.
- One temperature per fit until MC-9; trap-dependent `Qd` arrives with MC-10.

**R-mc8-2 — Methods.**
- **Adjoint ANN** (default): MC-6 derivative training, `∂Qg/∂vgs → C11`, `∂Qg/∂vds → C12`, and likewise for
  `Qd`. It trains on the scattered intrinsic points; the ragged boundary is no difficulty ([P1] §6.3.6).
- **Line integral** ([P1] eqs. 6.30, 6.31):
  - a smooth interpolant of each `Cij` over the scattered points (`Rbf2D`, or a trained value-ANN of `Cij`);
  - the integral along **both** paths of [P1] Fig. 6.16 onto a rectilinear intrinsic grid, by adaptive
    quadrature, to a table;
  - the user picks path A, path B or their mean. The difference between the paths is kept: it is check (a)'s
    second number.
- **Table**: the line-integral grid, kept as an MC-2 table.
- **Equation**: a template or the user's expression with fitted parameters. Templates: the depletion form with
  [P1] eq. 6.11's linear extension, and the Angelov-style charge expressions. The fit is on the derivatives — the
  same residual as the ANN's — through the optimizer algorithms.

**R-mc8-3 — Drift-charge decomposition** (advanced option, [P1] §6.3.9, [I11] eq. 3).
- `Qg = QGS(vgs) + QGD(vgd) + Qdrift(vgd, Id)` and `Qd = −QGD(vgd) − (1 − λ)·Qdrift(vgd, Id) + QDS(vds)`, in
  [I11]'s sign convention. `λ` is the charge-partition factor, default 0 ([I11] found 0 consistent with data).
- `QGS` and `QGD` come from adjoint training on the 1-D capacitances at `Vds = 0` ([P1] eq. 6.43).
- `QDS(vds)` cannot come from `Vds = 0`, and [I11] does not say where it does. From the two equations above,
  `C22 + C12 = dQDS/dvds + λ·∂Qdrift/∂vds` at **every** bias, so with `λ = 0` it is a 1-D adjoint fit to `Cds`
  (eq. 6.24) over all the intrinsic points (with `λ ≠ 0`, the known `λ·∂Qdrift/∂vds` is subtracted first). The
  spread of `Cds` across `vgs` at fixed `vds` is reported in one line: it is what the decomposition cannot
  represent.
- `Qdrift` is adjoint-trained on its two partial derivatives (`τ(vgd, Id)` and `CGD,drift`, [I11] eq. 4), with
  `Qdrift = 0` at `Id = 0` imposed during training.
- It needs the fitted `Id`, so it is offered only after MC-7. `Id` here is the current the core evaluates at the
  instantaneous state — `IdRF` when MC-7's dispersion option is on, because [I11] eq. 4's `gm + gDS` are RF
  conductances.
- Through `Id` it inherits the current's thermal and trap dependence ([P1] §6.3.9, [I11] §IV). With traps
  enabled, `Qg` therefore depends on the trap states through `Id` — D10's one exception, stated in one line.

**R-mc8-4 — Fit report.**
- Per charge: RMS error of the model's `∂Q/∂v` against each measured `Cij`, absolute and as % of its range.
- 1-D slices (each `Cij` against one voltage with the other fixed) ready for the window (MC-15).
- The domain hull, written into `Extrapolation` (charges extrapolate linearly — MC-2).

**R-mc8-5 — Check Charge Conservation (D7).** One function in `src/Design/Modeling/`, three parts, each reporting
one number and, where it has one, a map.
- **(a) The data.** For each row `i`, the curl `∂Ci1/∂vds − ∂Ci2/∂vgs` from the smooth interpolant, normalised by
  the typical magnitude of the two terms, as a map over the intrinsic points and an RMS %. Plus the two-path
  `ΔQ` of R-mc8-2 as % of the charge's range.
- **(b) The model.** The same curl on the model's own capacitance matrix, computed from its `∂q/∂v` by finite
  differences on a fine grid. A charge-based `ComposedCore` reads ~0 by construction. The check exists for **other**
  devices — an SDD written with capacitances, a compact model, an external device — and for any future
  constituent that might break it.
- **(c) Large signal.** An intrinsic-only testbench: DC bias at the cursor bias, the drain held at DC, a
  single-tone gate drive of amplitude `A` at `f` and then at `2f`, HB order ≥ 5. The quantity is
  `ΔIdc = Igate,dc(2f) − Igate,dc(f)` relative to the fundamental gate current amplitude.
  - Conduction DC does not depend on frequency; displacement DC from a non-conservative capacitance grows with
    `f` ([P1] Fig. 6.21). So `ΔIdc` isolates the defect without having to separate conduction from displacement
    inside a black box.
  - Reported in one line with the gate-bias consequence ("spurious 12 µA at the gate → 120 mV through a 10 kΩ
    gate bias resistor").
- **Applicability.** (b) and (c) accept **any** two-port nonlinear device instance in a `.cnl`, not only a
  `ComposedFet` (MC-13 gives it a CLI spelling).
- **Transcapacitance.** A non-reciprocal capacitance matrix (`Cm ≠ 0`) is reported as information, never as a
  failure ([P1] §6.3.8).

**R-mc8-6 — Long work.** Training, quadrature and the HB runs go through `RunControl`, cancellable. A cancelled
check reports nothing (D13).

## 3. Not in this phase
Temperature (MC-9); the window's plots of these results (MC-15).

## 4. Gates (minimal tests, run only these classes)
- `ChargeFitAdjointTests` — on `SyntheticFetData`, the ANN's `∂Q/∂v` matches the truth's `Cij` at held-out points
  within tolerance, and `Q` matches the truth's charge up to the anchor.
- `ChargeLineIntegralTests` — on conservative data, both paths agree to quadrature tolerance and equal the truth;
  on data with a deliberate curl, the paths differ by the amount the curl implies.
- `ConservationCheckDataTests` — the curl map is ~0 for conservative data and localises a planted curl to the
  region where it was planted.
- `ConservationCheckLargeSignalTests` — a charge-based SDD gives `ΔIdc` ≈ 0 (to HB tolerance); an SDD whose gate
  current is a capacitance times dV/dt (via a weighting function) gives a spurious DC that grows linearly with f
  (the step from f to 2f equals the step from 2f to 3f, to HB tolerance), as [P1] predicts; the check runs on a
  non-composed device named in a `.cnl`.
- `TranscapacitanceReportTests` — a model with `Cm ≠ 0` passes check (b) and reports `Cm` as information.
- `DriftChargeTests` — a truth built from [I11] eq. 3 with `λ = 0` is recovered: `QGS`, `QGD` and `QDS` within
  tolerance, `Qdrift = 0` at `Id = 0`, and the model's `Cij` against the truth's at held-out points.
