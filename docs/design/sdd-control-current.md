# circuitRF — SDD Control Currents (`_cn`) Design

**Status:** Implemented (rev 3) — DC + HB residual + HB Jacobian + S-param column all landed · **Date:** 2026-06-20 · **Phase:** 7 (SDD enrichment)
**Reads with:** `sdd.md` (the device + weighting functions this extends), `harmonic-balance.md`
(§2–3 partition/interface, §7 conversion-matrix Jacobian, §9 back-substitution),
`nonlinear-dc.md` (the `[V | I_branch]` Newton system), `linear-engine.md` (§9/§10 extraction).
**Investigation:** `sdd-control-current-investigation.md` (feasibility — read first).
**Scope (this rev):** all five referenceable device classes + full Jacobian coupling in HB, from the start
(owner's call). No staging.

This note specifies **control currents** for the SDD: referencing the current flowing in *another* device and
using it as a variable (`_cn`) in the SDD equations, alongside the port voltages `_vn` and the SDD's own port
currents `_in`. It is the current-controlled complement to the voltage-controlled `Evaluate` the SDD has today.

---

## 1. The feature

A user references another device's current by instance name and (for multiport devices) a port:

```
SDD:X1  g 0  d 0  I[2,0]=gm*_v1 + beta*_c1     ; drain current depends on a sensed current
C[1]=L1                                          ; _c1 = current in inductor L1
C[2]=S1   Cport[2]=2                             ; _c2 = current in port 2 of SnP S1
```

- **`C[n]=<instance>`** binds `_cn` to the current in device `<instance>`.
- **`Cport[n]=<port>`** selects the port for a multiport reference (SnP, ZnP, multi-port SDD); omitted/1 for
  two-terminal devices.
- **`_cn`** is then usable in any SDD equation (`I[p,w]`) exactly like `_vn`.

**Referenceable device classes (six):** independent voltage sources (`Vdc`), tone voltage sources
(`V_1Tone`/`V_nTone`, one model `ToneSourceModel`), current probes (`IProbe`), inductors (`L`), SnP, ZnP.
Assume the referenced device is a **sibling in the same elaborated netlist** (same hierarchy level) — no
cross-hierarchy path resolution in this rev. (`P1Tone` is excluded — see §7.)

`_in` (the SDD's *own* port current) is noted in §8 as a separate, smaller follow-on — it is **not** `_cn` and
has a different timing story. This rev is `_cn` only.

---

## 2. Why the data already exists

Every referenceable class stamps its current as a **Group-2 branch-current unknown** in the MNA system (one
branch for `Vdc`/`IProbe`/`L`; **N** branches, one per port, for SnP/ZnP via the Z(ω) branch-current
expansion). So a control current is always *some entry of the MNA solution vector's branch tail* — never a
quantity we must synthesize. The branch index is recoverable:

- `Vdc`/`IProbe`/`L`: public `LastBranchIndex` (set each `Stamp`).
- SnP/ZnP: allocate per-port branches into a **local** array today → need a small change to **expose a
  per-port branch-index map** (`int[] PortBranchIndices`, paralleling `LastBranchIndex`). `Cport[n]` selects
  the entry.

Branch indices are **topology-invariant** across frequencies and sweep points (the extractor relies on this
already), so the name→branch resolution is computed once.

---

## 3. The contract change (`Evaluate` gains control currents)

`Evaluate(in PortVoltages v)` is voltage-only today. Control currents require feeding `_c1.._cm` in. Two
options; **this design picks (b)**:

- (a) widen `PortVoltages` to also carry the control currents.
- **(b) add a sibling carrier** — `Evaluate(in PortVoltages v, in ControlCurrents c)` (or a single
  `in EvalInputs` struct holding both). The control currents are seeded as **dual variables** the same way
  port voltages are, so the SDD's AD produces `∂I[p,w]/∂_cn` for free — this is essential for the Jacobian
  (§6). Default `ControlCurrents` is empty; every existing device ignores it; the contract stays source-compatible
  via an overload whose default forwards an empty carrier.

`ControlCurrents` carries, per referenced index `n`: the **value** `_cn` (real time-domain sample in HB / real
DC value) and enough identity to let the engine map `∂/∂_cn` back to the right branch. The SDD seeds
`_c{n}` into the dual-AD binding dictionary in `SddEvaluator` exactly like `_v{n}` (a new seed slot — extend
`Dual.MaxN` budget to `portCount + controlCount`, and seed control currents after port voltages).

**Important asymmetry:** `_vn` are the **Newton unknowns** (interface voltages); `_cn` are **derived** linear
functionals of those unknowns (§4). So the AD gradient `∂I[p,w]/∂_cn` is *not* directly a Jacobian column — it
must be chained through `∂_cn/∂V` (§6). The SDD produces `∂I/∂_cn`; the engine owns the chain.

---

## 4. `_cn` as a linear functional of the interface voltage

This is the keystone. In every engine, the linear network is solved as `G·x = b`, and the referenced branch
current is one component of `x`. Writing the interface-voltage vector `V` (the Newton unknowns) and the
nonlinear injection `iNl(V)`:

```
x(V) = G⁻¹ · ( bSrc − P·iNl(V) )            (P injects iNl at interface nodes; HbLinearExtractor.SolveFullNetwork)
_c_ref = e_refᵀ · x(V)                       (e_ref selects the referenced branch row)
```

So **`_c_ref` is an affine-linear function of `iNl`**, hence of `V` through `iNl(V)`:

```
_c_ref(V) = e_refᵀ G⁻¹ bSrc  −  e_refᵀ G⁻¹ P · iNl(V)
          = c0_ref           −  rᵀ_ref · iNl(V)
```

- `c0_ref = e_refᵀ G⁻¹ bSrc` — the **source-only** part (the branch current with the NL devices open). Constant
  per (harmonic, sweep point); one back-solve against the cached LU.
- `rᵀ_ref = e_refᵀ G⁻¹ P` — the **sensitivity row**: how the referenced branch current responds to the
  nonlinear interface injection. Constant per harmonic per sweep point; computed by solving `Gᵀ·y = e_ref`
  once per referenced branch (a single transpose-solve against the cached factorization), then `rᵀ_ref = yᵀP`
  picks the interface-node entries. **This is the Jacobian-coupling operator** — see §6.

Both pieces reuse `HbLinearExtractor`'s cached per-omega LU. The engine already does the forward version
(`SolveFullNetwork`) post-convergence; this design runs the *referenced-branch rows* of it **per Newton
iterate** (residual) and uses the transpose-solve (Jacobian).

> The cross-device consequence: `iNl` aggregates **all** nonlinear devices, so `_c_ref` for an SDD can depend
> on a *different* SDD's currents through the shared linear network. That coupling is real physics and the
> conversion-matrix Jacobian must carry it (§6). It's the part most likely to surprise — call it out in tests.

---

## 5. Per-engine residual

**DC (`NonlinearDcEngine`).** Easiest: the state vector is literally `[V_nodes | I_branches]`, so the
referenced branch current is **already a Newton unknown in the same system**. `_cn` is read directly from
`x[branchIdx]` at each iterate — no separate solve. The SDD residual reads it; the Jacobian entry
`∂I[p]/∂_cn` (from AD) stamps directly into the SDD-row × referenced-branch-column position. Fully simultaneous,
exact, no chaining needed (the chaining in §6 is the HB-specific cost; DC gets it for free because the branch
is already in the matrix).

**S-parameter (`StampLinearized`).** Same MNA-with-branches structure as DC. The linearized SDD admittance
block gains an entry coupling the SDD port rows to the referenced branch column, scaled by `Σ_w H[w](ω)·∂I[p,w]/∂_cn`.
Bounded; mirrors the DC stamp in the frequency-linearized matrix. **Landed (rev 3):** `SddModel.StampLinearized`
adds the column after the `Y[p,q]` block via the new `IMnaContext.AddNodeBranchCoupling` `(node-row, branch-col)`
primitive; per (port p, control n) the column value is `DControl·H[0] + DControlCharge·H[1] + Σ_{w≥2} JacCtrl_w·H[w]`,
stamped `+col` at the port-+ row and `−col` at the port-− row (the DC sign convention, so DC and S-param agree at
ω→0). The S-param engine re-resolves the referenced branch index against its own assembly (branch numbering differs
from DC/HB — the wave path skips ports) and seeds the DC operating-point control currents into `SddModel.ControlBias`
so the sensitivities are evaluated at the right bias (exact for a linear-in-`_cn` equation, where the sensitivity is
seed-independent; the bias matters only for a nonlinear `_cn` dependence).

**HB (`HbNewton`).** Per Newton iterate, before/with `EvaluateNonlinear`:
1. From the present `V`, compute `iNl(V)` (already done).
2. For each referenced branch, compute `_c_ref = c0_ref − rᵀ_ref·iNl` per harmonic (the §4 functional),
   IFFT to the time grid, and seed `_c_ref(t)` into the SDD evaluation. Because `_c_ref` is **linear in
   iNl**, its spectrum is bounded by the same `K` — no new aliasing.
3. The SDD's `Evaluate` now returns `I[p,w](v, c)` with the control current folded in.

The residual is then the usual `F(V)=Y_NN·V + iSrc + Σ_w H[w]·FT{I[p,w]}` — but `I[p,w]` now also depends on
`V` *through* `_c_ref`. That extra dependence is what §6 adds to the Jacobian.

---

## 6. The Jacobian coupling (HB — the hard part, in from the start)

The SDD current at port `p`, weight `w`, harmonic row `k` enters `F` as `H[w](kω₀)·FT{I[p,w]}`. With control
currents, `I[p,w]` depends on `V` two ways:

```
dI[p,w]/dV  =  ∂I[p,w]/∂v · ∂v/∂V    (the existing conversion-matrix path, §7 of harmonic-balance.md)
            +  Σ_ref ∂I[p,w]/∂_c_ref · ∂_c_ref/∂V     (NEW — the control-current chain)
```

The new term factors using §4 (`_c_ref(V) = c0_ref − rᵀ_ref·iNl(V)`):

```
∂_c_ref/∂V  =  − rᵀ_ref · ∂iNl/∂V
```

and `∂iNl/∂V` is **exactly the conversion matrix the engine already builds** (the `G`/`C`/`Dw` blocks the
weighting arc generalized). So the control-current Jacobian block is a **product of operators the engine
already has**:

```
J_cc  =  [ Σ_w H[w](kω₀) · (∂I[p,w]/∂_c_ref) ]  ⊗  [ − rᵀ_ref · (conversion matrix of iNl) ]
```

Concretely, per referenced branch `ref`:
- `∂I[p,w]/∂_c_ref` comes from the SDD's **dual-AD** (because `_c_ref` is seeded as a dual variable, §3) — a
  per-port, per-w time-domain sensitivity, FFT'd to the conversion grid just like `Dg`/`Dc`/`Dw`.
- `rᵀ_ref` is the **constant sensitivity row** from §4 (one transpose-solve per referenced branch per harmonic
  per sweep point — built once, cached alongside the LU).
- The harmonic structure: `_c_ref` at harmonic `m` depends on `iNl` at harmonic `m` (the linear network is
  diagonal per harmonic — `G(ω_m)` couples only within harmonic `m`); then the SDD mixes harmonics when it
  evaluates `I[p,w](…, _c_ref(t))` in the **time domain**, producing the off-diagonal conversion structure.
  So the chain is: (diagonal-per-harmonic linear sensitivity `rᵀ_ref`) → (time-domain SDD mix → conversion
  matrix). Both halves are already-understood machinery; the brief assembles them.

**Build it against the FD oracle from day one.** `CompareJacobianNumerical` perturbs `V` and re-runs the full
residual — including, now, the `_c_ref` recompute. So the FD oracle **already accounts for the control-current
dependence** with zero changes: if `J_cc` is wrong, the oracle catches it exactly as it caught the weighting
buckets. This is the single most important implementation lever — wire the residual `_c_ref` recompute first,
let FD define truth, then make the analytic `J_cc` match it. (Same discipline that cracked the weighting
Jacobian.)

---

## 7. Name→branch resolver (elaboration-time)

A resolver maps `C[n]=<instance>` (+ optional `Cport[n]`) to a referenced-branch handle:
1. Resolve `<instance>` to a sibling `ElaboratedComponent` in the same netlist; **error** (named) if missing.
2. Validate the model is a **referenceable kind** (Vdc, V_1Tone/V_nTone, IProbe, Inductor, SnP, ZPort) — else a
   clear error listing the allowed kinds (mirror the SDD setup-error style; this replaces the current factory
   hard-error on `C[]/Cport[]`). (`P1Tone` is deliberately excluded — its current is ambiguous: it stamps two
   HB branches behind an internal reference impedance, and only its S-param 0 V port branch is exposed.)
3. Validate `Cport[n]`: required iff the device is multiport; range-check against its port count; default 1 for
   two-terminal devices; **error** if a port is given for a two-terminal device or omitted for a multiport one.
4. Capture the branch handle (`LastBranchIndex`, or `PortBranchIndices[port]` for SnP/ZnP). Because branch
   indices are assigned at `Stamp` time and are topology-stable, the resolver records *which device+port* and
   the engine reads the index after the first stamp (don't cache a stale index from before stamping).

The SDD model gains the control-current binding list `[(n, referencedComponent, port)]`, parallel to its
equation ASTs. The factory parses `C[n]`/`Cport[n]` (lift the `RxCurrentCtrl` hard-error), stores the raw
references, and the elaborator/engine resolves them to branch handles once the linear partition is stamped.

---

## 8. Out of scope (flagged, not built)

- **`_in` (the SDD's own port current) in equations.** We compute SDD port currents post-convergence
  (`ComputeDevicePortCurrents`), but mid-iterate `_in` is the device's own residual current — a self-reference
  with a different timing/Jacobian story (`∂_in/∂V` is the device's own conversion matrix, not a linear
  back-solve). Smaller, separable; do it after `_cn` lands if wanted.
- **Cross-hierarchy references** (`C[n]=X1.L2`). This rev assumes same-schematic siblings.
- **Referencing a capacitor or resistor current.** VendorA's list excludes them; a resistor current is
  `(Va−Vb)/R` (a node-voltage expression, not a branch unknown) and a capacitor is charge-based — out of scope,
  matching VendorA.

---

## 9. Implementation seams (status)

**Landed:**
- `ComponentModel.Evaluate` — `ControlCurrents` carrier overload (§3); base/existing devices ignore it. ✓
- `SddEvaluator` / `SddModel.Evaluate` — seed `_c{n}` as dual variables; produce `∂I/∂_cn` (`DControl`). ✓
- `ComponentModelFactory.CreateSddModel` — parse `C[n]`/`Cport[n]`; hard-error dropped; `_cn` cross-validated. ✓
- Name→branch resolver — instance-name → referenceable-kind → branch handle (§7). ✓
- `SnpModel`, `ZPortModel` — `PortBranchIndices` exposed (§2). ✓
- `ToneSourceModel` (`V_1Tone`/`V_nTone`) — `LastBranchIndex` exposed; added to the referenceable set in all
  three engine resolvers (DC/HB/S-param), validated as two-terminal like `Vdc`. ✓
- `NonlinearDcEngine` — reads `_cn` from the branch unknown; stamps `∂I/∂_cn` into the branch column (§5). ✓
- `HbLinearExtractor` — referenced-branch sensitivity row (§4) via forward-solves against the cached LU. ✓
- `HbNewton` — per-iterate `_c_ref` recompute (residual, §5); `J_cc` block (§6); FD-oracle-gated. ✓
- `StampLinearized` — control-current column in the S-param linearized block (§5). `SddModel` overrides
  `StampLinearized` to add, after the `Y[p,q]` block, the column `Σ_w H[w](ω)·∂I[p,w]/∂_cn` coupling each
  port-KCL row to the referenced device's branch-current unknown via the new
  `IMnaContext.AddNodeBranchCoupling(node, branch, coeff)` primitive (the transpose-position of
  `AddConstraint`). The S-param engine re-resolves `ControlBranchIndices` against its own assembly (a
  throwaway `StampAll` pass — branch numbering differs from DC/HB) and seeds `ControlBias` from the DC
  pre-pass. Sign matches the DC branch column (`+col` at the +node) so DC and S-param agree at ω→0. ✓

**Complete:** the control-current arc now spans all three analyses (DC, HB, S-param).

## 10. Test plan (each gated, FD-anchored)

- **DC:** SDD sensing an IProbe carrying a known current reproduces it in `_c1`; `∂I/∂_cn` correct (it's in the
  one Newton system).
- **HB residual:** an SDD whose drain current = `beta·_c1` (current mirror of an inductor branch) gives the
  expected spectrum.
- **HB Jacobian:** `CompareJacobianNumerical` ≤ FD gate (1e-5, low-drive converged point) with a control-current
  term active — the decisive correctness check for §6.
- **Cross-device coupling:** two SDDs sharing a linear network, one sensing a branch the other drives — FD
  oracle still passes (proves the off-diagonal §4 coupling is in the Jacobian).
- **All five kinds:** one resolver test per referenceable class (Vdc, IProbe, L, SnP port, ZnP port) +
  `Cport` range/validation errors.
- **Regression:** every existing SDD/weighting test stays green (no `C[n]` → empty control carrier → identical).
