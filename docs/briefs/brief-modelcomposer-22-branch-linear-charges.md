# Brief MC-22 — Linear Cgs, Cdg, Cds, Cm one at a time

**Series:** `brief-modelcomposer-0-overview.md` (D7) · **Tag:** `R-mc22-<m>` · **Depends on:** MC-21
**Area:** `src/Core/Devices/Composed/` (`ComposedCoreModel`, `ComposedSwitches`, a new `BranchLinearisation`),
the ComposedFet rows in `ComponentTypeRegistry` / the parameter editor; `docs/user/src/reference/model-composer.md`;
`docs/design/model-composer.md`
**Read first:** MC-21 (switches, the bias-point freeze); `RuntimeEvaluator` (the Hessian along a direction);
`IntrinsicQuantities`' circuit-set convention; MC-8's `ConservationCheck` (c)

---

## 1. Goal

The designer wants to make **one** intrinsic capacitance linear and leave the others nonlinear: a linear Cgs with
nonlinear feedback and output capacitance, a linear Cdg alone, and so on. That separates the AM–PM and harmonic
content each capacitance is responsible for. MC-21's `LinearC` freezes the whole matrix. This brief freezes one
branch at a time.

**Why this needs a construction.** A composed model's charges are **terminal** charges `Qg(vgs, vds)`, `Qd(vgs, vds)`
— an ANN, a table, a template or a drift decomposition — not three two-terminal capacitors. The circuit set is defined
from the small-signal matrix `C = ∂(Qg, Qd)/∂(vgs, vds)`, using the convention `IntrinsicQuantities` and MC-17 already
use:

```
Cgs = C11 + C12     Cdg = −C12     Cds = C22 + C12     Cm = C21 − C12
```

Freezing one of those while the others follow the model cannot be done by editing the Jacobian alone. The charge must
remain a **single-valued function of the voltages**, or HB charge balance acquires a spurious DC current, and in
general no constant re-weighting of `Qg, Qd` achieves it (the design note should record the two-line proof). So each
branch is given an explicit charge of its own, defined from the bias point.

## 2. Requirements

**R-mc22-1 — The branch residuals.** Let `v_Q` be the frozen bias point (MC-21 R-mc21-3) and `Δv = v − v_Q`. Take the
charge with its temperature and trap-state inputs held at their frozen values, `Q̃(v)`, and its branch capacitances
`C_b(v)` from the convention above. Each branch `b` has a controlling voltage `u_b` (`vgs` for Cgs, `vgs − vds` for
Cdg, `vds` for Cds, `vgs` for Cm) and a terminal incidence `e_b` in (g, d): Cgs `(1, 0)`, Cdg `(1, −1)`, Cds `(0, 1)`,
Cm `(0, 1)`. Its **nonlinear residual** along the straight line from the bias point is

```
r_b(v) = [ ∫₀¹ C_b(v_Q + s·Δv) ds − C_b(v_Q) ] · Δu_b
```

and the charge of a mode that freezes the set `F` is

```
q(v) = Q̃(v) − Σ_{b ∈ F} e_b · r_b(v)
```

Three properties follow, and each is a gate:
- **Single-valued.** `q` is an explicit function of `v`, so it conserves charge.
- **Exact at the bias point.** `r_b` and its gradient vanish at `v_Q`, so every combination has the model's own C
  matrix there, and small-signal S-parameters at the bias point do not depend on the switches.
- **Composes.** Freezing all four reproduces `LinearC` exactly, by the fundamental theorem along the line, up to
  quadrature error. With branches that really are separable (`Qgs(vgs)`, `Qgd(vgd)`, `Qds(vds)`), freezing one gives
  exactly the model with that branch replaced by its bias-point capacitor.

The straight line from the bias point is a **definition**. It is what makes the three properties hold, and the design
note says so.

**R-mc22-2 — Evaluation.** The integral is Gauss–Legendre with 8 nodes (a named constant), applied to the charge's
first derivatives. The Jacobian `dc` is the exact derivative of that quadrature sum. The `s·∂C_b·Δu_b` term needs the
charge's second derivatives along the two voltage directions, which every `RuntimeEvaluator` already returns as "the
Hessian along a direction". If an evaluator does not (check the drift charge), fall back to a central difference of the
analytic first derivatives with a step of 1e-6 V, record it in the design note, and hold it with the Jacobian gate
below. Work only for the branches in `F`. An instance with `F` empty runs the code path it runs today, with no
quadrature.

**R-mc22-3 — The switches.** `ComposedSwitches` gains four booleans, default `false`: `LinearCgs`, `LinearCdg`,
`LinearCds`, `LinearCm`, read by `BooleanParameter` like MC-21's. `LinearC=true` is the same as setting all four, and
also freezes the charge's temperature and trap inputs. `Charge=false` still wins. `Describe()` gives `linear Cgs, Cds`
style text for the schematic annotation and the Messages note. Add the four parameter-editor rows to MC-21's **Model
switches** group.

**R-mc22-4 — Interaction with the drain–source image.** With `Exchange` symmetry, `Q̃` already includes the image
(`ComposedCoreModel.Core`). The construction acts on the terminal-frame `Q̃` and its terminal-frame C matrix, so a line
that crosses `vds = 0` needs no special case. Hold this with a gate.

**R-mc22-5 — Docs** (sources only). Extend `#switches` in `model-composer.md` with the four branch switches. Say in one
paragraph what "linear Cgs" means for a model whose charges are not separate capacitors: the capacitance is held at
its bias-point value, the others follow the model, and the result is exact at the bias point. Add the circuit-set
identities. The design note records R-mc22-1's definition, why a Jacobian edit is not enough, and the quadrature.

## 3. Not in this phase
A different path (e.g. along constant `vds` first). An option to freeze the circuit set at a drive-dependent operating
point rather than the quiescent one.

## 4. Gates
- `BranchLinearisationTests`, on a **separable polynomial** truth core (`Qg = Qgs(vgs) + Qgd(vgs − vds)`,
  `Qd = −Qgd(vgs − vds) + Qds(vds)`, each a cubic, so 8-node Gauss–Legendre is exact):
  - for each single branch and for the pair `LinearCgs + LinearCds`, `q` and `dc` equal the hand-built model with
    those branches replaced by their bias-point capacitors, to 1e-12, at ten random voltages;
  - all four together equal `LinearC` to 1e-12.
- On `SyntheticGan` (non-separable, ANN or template charges):
  - S-parameters at the bias point, for all 16 combinations, equal the nonlinear model's to `|ΔS|` ≤ 1e-12;
  - all four together equal `LinearC`'s `q` to 1e-6 relative (quadrature error);
  - `dc` against a central difference of `q` agrees to 1e-6 relative at five points;
  - MC-8's large-signal conservation check (c) reads below its threshold for `LinearCgs` and for `LinearCdg`.
- An `Exchange` model with `LinearCds`, evaluated on a line from `vds_Q > 0` to `vds < 0`, satisfies the `dc`-vs-FD gate.
- One HB power sweep per single-branch switch on the example's `GanPowerSweep` bench converges at every rung. The AM–PM
  and H2 differences against the nonlinear model are printed to the test output, not asserted.
- Run only these classes, MC-21's, and `ModelComposerDocumentedPageTests`.

## 5. Open for the owner
- Cm (transcapacitance) is exposed as a fourth switch. Without it, the three circuit capacitances frozen together would
  not equal `LinearC`.
- With any branch frozen, the **whole** charge sees the DC temperature and trap state, not only the frozen branch. This
  keeps the construction exact and cheap. The alternative (unfrozen branches keep their thermal and trap dependence) is
  possible but makes "all four" differ from `LinearC`.
