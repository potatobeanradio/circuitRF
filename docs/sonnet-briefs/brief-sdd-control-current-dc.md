# Brief #1 (SDD control currents): contract + resolver + DC engine

Design ref: `docs/design/sdd-control-current.md` (§3 contract, §4 functional, §5 DC, §7 resolver). This is the
**first** brief of the control-current arc and deliberately the one with **no HB timing subtlety**: at DC the
referenced device's current is already a Newton unknown in the same `[V | I_branch]` system, so `_cn` is read
directly and its Jacobian entry is exact. Prove `_cn` end-to-end at DC before any HB work.

Scope: the `Evaluate` contract extension, the name→branch resolver, SDD parsing of `C[n]`/`Cport[n]`, per-port
branch exposure on SnP/ZnP, and `NonlinearDcEngine` support. **HB and S-param are later briefs** — this brief
must not touch `HbNewton`/`StampLinearized` logic beyond keeping them compiling against the new contract.

Build **0W/0E**. Every existing test stays green (no `C[n]` → empty control carrier → identical behavior).

---

## 1. The `Evaluate` contract extension (widest blast radius — do it carefully)

Add a control-current carrier to the nonlinear evaluation contract. Keep the existing signature working via an
overload so non-SDD devices and existing call sites are untouched.

In `ComponentModel.cs`:
```csharp
/// <summary>Control currents _c1.._cm seeded for the SDD (empty for every other device).</summary>
public readonly struct ControlCurrents(double[] values)
{
    public double[] Values { get; } = values;
    public int Count => Values.Length;
    public double this[int i] => Values[i];
    public static readonly ControlCurrents Empty = new([]);
}
```
Extend `Evaluate`:
```csharp
// New primary overload — carries control currents.
public virtual NonlinearResult Evaluate(in PortVoltages v, in ControlCurrents c)
    => Evaluate(v);   // base default: ignore control currents (every built-in device)

// Keep the existing 1-arg as-is (devices override whichever they need).
public virtual NonlinearResult Evaluate(in PortVoltages v)
    => throw new NotSupportedException($"{GetType().Name} is not a nonlinear model");
```
Only `SddModel` overrides the 2-arg form. NonlinearC and any other device keep overriding the 1-arg form, and
the base 2-arg forwards to it — so they're unaffected. **Engines call the 2-arg form** (passing
`ControlCurrents.Empty` where they don't yet supply currents); the DC engine (§4) passes the real ones.

> The Jacobian needs `∂I[p,w]/∂_cn`. The SDD produces it via dual-AD by seeding `_c{n}` as dual variables
> (§3) — see §3 below. This brief wires that gradient through DC; HB consumes the same gradient later.

## 2. SDD: parse `C[n]`/`Cport[n]`, seed `_cn`, emit `∂I/∂_cn`

**Factory (`ComponentModelFactory.CreateSddModel`):** drop the `RxCurrentCtrl` hard-error. Add:
- `RxControlRef  = ^C\[(\d+)\]$`  → `C[n]=<instanceName>` (String value = referenced instance name).
- `RxControlPort = ^Cport\[(\d+)\]$` → `Cport[n]=<port>` (Real value, 1-based port).
Collect into a per-`n` list `[(int N, string RefInstance, int Port)]` (Port default 1 when no `Cport[n]`).
Validate `n ≥ 1` contiguous-ish (allow gaps but require each referenced `_cn` in an equation to have a `C[n]`;
cross-validate like the H[w] check). Store the raw references on the model (unresolved — the resolver §3 binds
them to branch handles at elaboration, since branch indices aren't known until stamping).

**`SddModel`:** add `_controlRefs` (the raw `[(n, refInstance, port)]`) and a resolved
`_controlBranchIndices` (filled by the resolver). In `Evaluate(v, c)`, seed `_c{n}` into the dual-AD binding
dictionary the same way `_v{n}` are seeded — **extend `SddEvaluator.EvalDual`** to take the control-current
array and seed `_c{n+1}` as `Dual.Seed` *after* the port-voltage seeds. The gradient then includes
`∂result/∂_cn` in slots `[portCount .. portCount+controlCount-1]`. Return those extra gradient columns in the
`NonlinearResult` so the engine can stamp `∂I/∂_cn`.

**The gradient plumbing — key decision:** `NonlinearResult.Dg` is `[port,port]` (∂I/∂v). Control-current
sensitivities are a *different* shape: `[port, controlIndex]` (∂I[p]/∂_cn). Add a parallel field
`double[,]? DControl` to `NonlinearResult` (null/empty when no control currents) carrying `∂I[p,0]/∂_cn`, and —
for completeness with the weighting work — the charge/bucket control sensitivities too if present. For **this
DC brief**, only the `w=0` current path matters (DC drops charge, and buckets vanish at DC unless `H[w](0)≠0`);
spec `DControl` to carry the `w=0` current sensitivity `∂I[p,0]/∂_cn` now, and note bucket control-sensitivity
is added in the HB brief. Keep `DControl` defaulted so existing constructors are unaffected.

`Dual.MaxN` must cover `portCount + controlCount` — bump the budget / assert with a clear message.

## 3. Name→branch resolver (elaboration)

The referenced instance must resolve to a sibling `ElaboratedComponent` and a branch handle. Where the SDD's
other elaboration data is assembled (the path that builds `SddPortCount`/equation params —
`Elaborator.ResolveSddParameters` / wherever the `SddModel` is constructed with resolved data), add a
resolution step that runs **after the linear partition is stamped at least once** (so `LastBranchIndex` /
`PortBranchIndices` are assigned). Concretely, the resolver:
1. For each `C[n]=<instance>`: find the sibling component by instance name in the same netlist; **error**
   (named, SDD-setup-error style) if not found.
2. Validate referenceable kind ∈ {`VdcModel`, `IProbeModel`, `InductorModel`, `SnpModel`, `ZPortModel`}; else
   error listing allowed kinds. (This is the message that replaces the old factory hard-error.)
3. Resolve the branch index:
   - Vdc/IProbe/Inductor: `LastBranchIndex` (single branch; `Cport[n]` must be absent or 1 — error otherwise).
   - SnP/ZnP: `PortBranchIndices[port-1]` (§5); `Cport[n]` **required**, range-checked against port count.
4. Store the resolved branch index per `n` on the `SddModel` (`_controlBranchIndices[n]`).

**Timing detail:** branch indices are assigned during `Stamp`. The DC engine builds its linear system in the
constructor (stamps all linear devices once) — so resolve **against that stamped pass**. Don't cache an index
from before stamping. If the resolver can't run pre-engine (because nothing has stamped yet), have the DC
engine resolve lazily on first build (it already iterates all components to build `_gAug`; capture the
referenced models' `LastBranchIndex`/`PortBranchIndices` right after that stamp loop, before the Newton loop).
Pick whichever is cleaner given how `SddModel` gets its resolved data; the brief's requirement is: **by the
time `BuildResidualAndJacobian` runs, each `_controlBranchIndices[n]` is a valid index into the state vector.**

## 4. `NonlinearDcEngine` — read `_cn`, stamp `∂I/∂_cn`

The state vector `x` is `[V_nodes (0..nodeCount-1) | I_branches (nodeCount..systemSize-1)]`, and a branch index
from `AddBranch()` is `nodeCount + k` — a **direct index into `x`**. So:

In `BuildResidualAndJacobian`, for each nonlinear device that is an SDD with control refs:
1. **Read control currents** from the present iterate: `c[n] = x[branchIndex_n]` (`.Real` — DC is real). Build
   the `ControlCurrents` array in `n` order.
2. Call `ec.Model.Evaluate(new PortVoltages(portV), new ControlCurrents(cVals))`.
3. Residual is unchanged in form (`res.I[p]` stamped into the port nodes as today) — but now `res.I[p]` already
   includes the control-current dependence.
4. **Jacobian:** in addition to the existing `StampDg` (∂I[p]/∂v_q into node columns), stamp
   `∂I[p]/∂_cn = res.DControl[p,n]` into the **referenced branch column**:
   ```
   ∂(KCL at SDD port p node np/nm) / ∂(x[branchIndex_n])  +=  DControl[p,n]   (np row), −DControl[p,n] (nm row)
   ```
   i.e. a `StampDgToColumn(j, np, nm, branchIndex_n, DControl[p,n])` that adds to `(np-1, branchIndex_n)` and
   `(nm-1, branchIndex_n)` with the port sign convention (mirror `StampDg`, but the column is a branch index,
   not a node-pair). This is the exact ∂F/∂x entry that makes Newton quadratic — it's correct and trivial at
   DC because the branch is already an unknown.

No back-solve, no chaining: the referenced branch current and the SDD voltages are in the **same** Newton
system, so the coupling is one matrix entry per (port, control-ref).

## 5. Expose per-port branch indices on SnP / ZnP

Both allocate per-port branches into a local array today. Add a public `int[] PortBranchIndices` (length =
port count), set during `Stamp` (assign `PortBranchIndices[p] = branches[p]`), paralleling the existing
`LastBranchIndex` on the single-branch devices. Default to an empty/`-1`-filled array before first stamp. No
behavior change — purely exposing what's already computed.

## 6. Tests (`tests/Engine.Tests/Nonlinear` + `tests/Core.Tests/Devices`)

- **DC read-through:** `IProbe` in series carrying a known current `I0` (set by a resistor + Vdc); an SDD with
  `I[1,0]=_c1`, `C[1]=IP1` → the SDD sources `I0` into its port. Assert node voltages reflect `_c1 = I0`.
- **DC Jacobian (exactness):** a circuit where the SDD current depends on `_c1` (`I[1,0]=beta*_c1`) converges
  in few Newton iterations (quadratic) — proves `DControl` stamps the right ∂F/∂x. Optionally an FD check of
  the assembled DC Jacobian column for the branch DOF.
- **Resolver errors:** missing instance; non-referenceable kind (e.g. `C[1]=R1`) → named error; `Cport` on a
  two-terminal device → error; missing `Cport` on SnP/ZnP → error; `Cport` out of range → error.
- **All five kinds resolve:** one resolver test each (Vdc, IProbe, L, SnP port k, ZnP port k) → correct branch
  index.
- **Regression:** every existing SDD/NonlinearC/weighting test green; an SDD with no `C[n]` evaluates
  identically (empty `ControlCurrents`, null `DControl`).

## Gate
Build 0W/0E; tests green; the factory hard-error on `C[]/Cport[]` is gone and replaced by the resolver's
referenceable-kind validation. After this, `_cn` works end-to-end at DC for all five device classes. Next
brief: the HB residual `_c_ref` recompute (§5 of the design) — the per-iterate linear functional — followed by
the HB Jacobian coupling `J_cc` (§6) gated by the FD oracle.
