# Brief #4 (SDD control currents): S-parameter `StampLinearized` control column

Design ref: `docs/design/sdd-control-current.md` §5 (S-param), `sdd.md` §6 ("Not yet wired"). Depends on
briefs #1–#3 (contract, resolver, DC, HB residual+Jacobian — all landed). This brief is the **last** gap: the
small-signal S-parameter linearization of an SDD that uses `_cn` currently **omits** the control-current
contribution. This adds it — the frequency-linearized mirror of brief #1's DC branch-column stamp.

**This is more involved than it sounds** — two real architecture problems below (a missing MNA primitive and a
stamp-ordering/branch-resolution issue), both confronted head-on. Core + Engine. Build **0W/0E**. Every
existing S-param test stays green (no `C[n]` → no control column → identical).

---

## What "control column" means

`StampLinearized` stamps the SDD's small-signal admittance block:
```
Y[p,q] = Σ_w H[w](ω) · ∂I[p,w]/∂V_q |_bias        // node-row p × node-col q
```
With control currents, `I[p,w]` also depends on `_cn` (a referenced branch current), adding a term to the SDD
**port-p KCL row** that couples to the **referenced device's branch-current unknown**:
```
∂(KCL at SDD port p) / ∂(I_branch,ref)  =  Σ_w H[w](ω) · ∂I[p,w]/∂_cn        // node-row p × BRANCH-col ref
```
The sensitivities are already produced by the SDD's dual-AD and carried on `NonlinearResult` (landed in #3):
`DControl` (w=0, `∂I[p,0]/∂_cn`), `DControlCharge` (w=1, `∂Q[p]/∂_cn`), and `WeightedTerm.JacCtrl` (w≥2,
`∂I[p,w]/∂_cn`). So the column value per (port p, control n) is:
```
col[p,n] = DControl[p,n]·H[0](ω)  +  DControlCharge[p,n]·H[1](ω)  +  Σ_{w≥2} JacCtrl_w[p,n]·H[w](ω)
         = DControl[p,n]          +  jω·DControlCharge[p,n]       +  Σ_{w≥2} Weight(w,ω)·JacCtrl_w[p,n]
```
Exactly the `Σ_w H[w](ω)·(sensitivity)` shape of the existing `Y[p,q]` block, with the control sensitivity in
place of the voltage gradient.

## Problem 1 — there is no `(node-row, branch-col)` MNA primitive

`IMnaContext` today: `AddBlockAdmittance(rowNode, colNode, y)` is node×node; `AddConstraint(branch, node,
coeff)` is branch-row×node-col; `AddBranchConstraint(branch, otherBranch, coeff)` is branch×branch. **None
stamps `(node row, branch col)`** — which is what the control column is (KCL row at a node, depending on a
branch-current unknown). This is the transpose-position of `AddConstraint`.

**Add a primitive** to `IMnaContext` + `MnaSystem`:
```csharp
// Group 2: add coeff at (node row, branch col) — a node-KCL dependence on a branch current.
// Transpose-position of AddConstraint (branch row × node col).
void AddNodeBranchCoupling(int node, int branch, Complex coeff);
```
`MnaSystem` impl: `int n = Col(node); if (n >= 0) Accum(n, branch, coeff);` (the branch column index is the
absolute matrix column = `nodeCount + branchLocal`, same space `AddBranch` returns — so `branch` is passed as
that absolute index). Mirror the existing `Accum` pattern; ground node dropped as usual.

> Sign: the control column adds `col[p,n]` to the **SDD port-p+ row** and `−col[p,n]` to the **port-p− row**
> (the port spans `Nodes[2p], Nodes[2p+1]`), exactly like the 4-corner pattern in the existing `Y[p,q]` stamp,
> but the "column" is a single branch index, so only two entries per (p,n): `(np, branch, +col)` and
> `(nm, branch, −col)`. Match the DC engine's `StampDgToColumn` sign convention from brief #1 (the DC branch
> column is the same physical coupling — keep them identical so DC and S-param agree).

## Problem 2 — stamp order + branch-index resolution in the S-param MNA

`StampLinearized(mna, c, omega, bias)` needs the **referenced device's branch index in *this* (S-param) MNA**.
Two hazards:
1. **`ControlBranchIndices` (resolved by the DC/HB path) is NOT valid here.** Branch numbering depends on which
   devices stamp and in what order. The S-param `StampAll` skips ports (wave path) and stamps the SDD via
   `StampLinearized` (which allocates no branch). So the S-param MNA's branch numbering differs from the DC
   engine's. **Re-resolve** the referenced branch index against the S-param assembly.
2. **Order:** the SDD's `StampLinearized` may run before its referenced devices have stamped (and thus before
   their `LastBranchIndex`/`PortBranchIndices` are set for this MNA). `StampAll` iterates components in netlist
   order with no ordering guarantee.

**Resolution — reuse the existing preliminary-pass pattern.** `SParameterEngine.CollectPortsAndBranchLabels`
*already* does a full ω=1 stamp pass that assigns every device's branch index and builds a branch-label map.
Extend that pass (or add a parallel one) to capture, for each referenced device (by instance name), its
**S-param-MNA branch index** (`LastBranchIndex` for Vdc/IProbe/L; `PortBranchIndices[port-1]` for SnP/ZnP).
Build a map `referencedInstance → branchIndex` once. Then:
- Give the SDD its resolved S-param branch indices before `StampAll`. Cleanest: pass a
  `IReadOnlyDictionary<string,int>? controlBranchResolved` (instance→branch) into the S-param `StampLinearized`
  call path. Since `StampLinearized` is a `ComponentModel` virtual shared with HB/DC, **don't widen its
  signature**; instead, the engine resolves the indices and writes them into the SDD's existing
  `ControlBranchIndices` array **for the S-param run** (the SDD already exposes `ControlRefs` with instance
  names + ports — the engine maps each to the S-param branch index and sets `ControlBranchIndices[i]`). This
  reuses the field the DC/HB engines already populate; each engine sets it against its own MNA. Document that
  `ControlBranchIndices` is **per-run** (the owning engine resolves it before stamping).
- The referenced device must actually allocate a branch in the S-param MNA. **All five do** in the legacy path
  (ports stamp 0 V branches; L/Vdc/IProbe/SnP/ZnP stamp normally). **Wave path caveat:** the wave path
  `skipPorts:true` skips Port/Term/P1Tone — but those are **not** referenceable control targets, so they don't
  matter. Vdc/IProbe/L/SnP/ZnP all still stamp in the wave path (they're not `IsSParamPort`). Confirm each
  referenced device's branch exists in the chosen path; if a referenced branch index comes back unset
  (`< 0`), **error** clearly (e.g. "SDD 'X1': C[1]=L1 — referenced device allocated no branch in the
  S-parameter matrix").

## `StampLinearized` change (Core)

Generalize the SDD's stamp (the base `ComponentModel.StampLinearized`, or an SDD override — prefer an **SDD
override** so the base stays control-free and only the SDD pays for it). After the existing `Y[p,q]` block:
```csharp
// Control-current column: ∂(KCL port p)/∂(I_branch,ref) = Σ_w H[w](ω)·∂I[p,w]/∂_cn
if (r.DControl is not null /* or any control sensitivity present */ && ControlRefs.Length > 0)
{
    for (int p = 0; p < P; p++)
    {
        int np = c.Nodes.Length > 2*p   ? c.Nodes[2*p]   : 0;
        int nm = c.Nodes.Length > 2*p+1 ? c.Nodes[2*p+1] : 0;
        for (int nCtrl = 0; nCtrl < ControlRefs.Length; nCtrl++)
        {
            int branch = ControlBranchIndices[nCtrl];
            if (branch < 0) continue;   // (engine errors earlier if truly unresolved)
            Complex col = new Complex(r.DControl?[p,nCtrl] ?? 0, 0)
                        + Weight(1, omega) * (r.DControlCharge?[p,nCtrl] ?? 0);
            foreach (var term in r.Terms)
                if (term.JacCtrl is not null)
                    col += Weight(term.W, omega) * term.JacCtrl[p, nCtrl];
            if (col == Complex.Zero) continue;
            mna.AddNodeBranchCoupling(np, branch, +col);
            mna.AddNodeBranchCoupling(nm, branch, -col);
        }
    }
}
```
`Evaluate(bias)` must be called with the control currents seeded at the **bias operating point** so the
sensitivities are evaluated at the right point — but note the S-param `StampLinearized` currently calls
`Evaluate(bias)` (1-arg, no control currents). The control sensitivities (`DControl` etc.) are derivatives, so
they need `_cn` seeded as dual variables even at the bias point. **Call `Evaluate(bias, controlBias)`** where
`controlBias` holds the DC value of each referenced branch current (from the same DC solve the S-param engine
already ran — `dcNodeVoltages` is available; the referenced branch DC currents come from the DC result, or are
re-derivable). If wiring the DC control-current *values* is awkward, note that for the **linear** small-signal
stamp only the **sensitivities** (`∂I/∂_cn`) matter, and those are constant w.r.t. the seed for an SDD whose
`_cn`-dependence is linear; for a nonlinear `_cn`-dependence the sensitivity is bias-dependent and the seed
matters. Seed `controlBias` from the DC referenced-branch currents to be correct in general.

## Tests (`tests/Engine.Tests/Linear`)

- **S-param control column correctness:** a 1-port SDD whose port current includes `+beta*_c1` referencing a
  device carrying a known small-signal current → S11 reflects the control coupling. Compare against an
  equivalent linear circuit (hand-computed Y) where the control coupling is replaced by its explicit admittance.
- **Equivalence to a built-in:** construct a case where `_c1` references a branch whose current equals a port
  voltage over a known impedance, so the control column reduces to a known `Y` entry; assert S matches.
- **DC/S-param agreement:** the control coupling's contribution at ω→0 matches the DC engine's branch-column
  stamp (same physical entry, same sign) — pins Problem 1's sign convention.
- **All five kinds:** S-param run with an SDD referencing each (Vdc, IProbe, L, SnP port, ZnP port); the column
  is stamped and the run is non-singular.
- **Unresolved-branch error:** a referenced device that (hypothetically) allocates no branch → clear error.
- **Regression:** every existing S-param test green (no `C[n]` → no column → byte-identical). The
  NonlinearC/SDD S-param equivalence tests must not move.

## Gate
Build 0W/0E; all green; small-signal S-parameters of an SDD using `_cn` now include the control-current
contribution, matching DC at ω→0. **This completes the control-current arc across all three analyses** (DC,
HB, S-param). After landing, update `sdd.md` §6 (move the S-param column from "Not yet wired" to landed) and
§8.5 (S-parameters now honor `_cn`), and `sdd-control-current.md` §9 (mark `StampLinearized` ✓). 

## Risk note (for the human)
Lower-risk than the HB Jacobian brief (no FD-oracle hunt — the stamp is a direct admittance entry), but it has
the two architecture wrinkles above (the new MNA primitive and the per-run branch re-resolution). The most
likely bug is a **branch-index mismatch** (using a DC-resolved index in the S-param matrix) or a **sign flip**
on the column. The DC/S-param-agreement test (ω→0) is the tripwire for both — if it passes, the column is in
the right place with the right sign.
