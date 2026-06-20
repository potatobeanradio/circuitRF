# SDD Control-Current (`_cn`) — Architecture Feasibility Investigation

**Status:** Investigation (not a brief) · **Date:** 2026-06-20 · For: scoping the SDD control-current feature
**Reads with:** `sdd.md`, `harmonic-balance.md` (§2–3 partition/interface, §9 back-substitution),
`nonlinear-dc.md`, `linear-engine.md` (§9/§10 extraction).

## The ask

VendorA's SDD can reference the current flowing in another device via `C[n]` (instance name) + `Cport[n]`
(port, for multiport), exposed in the SDD equations as `_cn` alongside `_vn`/`_in`. Referenceable devices:
independent voltage sources, IProbe, inductors, SnP, ZnP. Assume the referenced device is in the same
schematic (same hierarchy level). **Question: does our architecture support this, and for how many of the five
device classes?**

## Headline answer

**The data is all there. The challenge is the *timing*, and it is HB-specific.**

All five referenceable device classes already allocate their port current as a **first-class Group-2
branch-current unknown** in the MNA system — I verified each on disk:

| Device | Branch alloc | Exposure today |
|---|---|---|
| Vdc (indep. V source) | `mna.AddBranch()`, 1 branch | `LastBranchIndex` (public) |
| IProbe | `mna.AddBranch()`, 1 branch | `LastBranchIndex` (public) |
| Inductor | `mna.AddBranch()`, 1 branch | `LastBranchIndex` (public) |
| ZnP (`ZPortModel`) | `mna.AddBranch()`, **N** branches (one/port) | local array (not exposed) |
| SnP (`SnpModel`) | `mna.AddBranch()`, **N** branches (one/port) | local array (not exposed) |

So this is **not** a "we'd have to invent a current for an admittance stamp" problem — even SnP/ZnP are stamped
by the Z(ω) branch-current expansion (`linear-engine §4.1/§4.4`), so their per-port currents are solved
unknowns, not post-hoc computations. **All five are architecturally reachable.** That's a better starting point
than VendorA's own list might suggest.

The blocker is a single, well-defined ordering problem in the HB engine, described next. It is **solvable**, and
the difficulty is **the same for all five devices** — once the mechanism exists, supporting one referenceable
class is ~the same work as supporting all five.

## Why it's not free: the contract and the HB partition

Two facts collide:

**1. The `Evaluate` contract is voltage-only.** `ComponentModel.Evaluate(in PortVoltages v)` receives port
voltages and nothing else. There is no channel for "another device's current." The SDD factory makes this
explicit — `RxCurrentCtrl = ^C(port)?\[` currently **hard-errors**: *"current-controlled equation C[]/Cport[]
not supported (Evaluate is voltage-controlled)."* That error is the feature's to-do marker.

**2. In HB, the referenced devices live in the *linear* partition, and their currents are not Newton
unknowns.** HB's unknowns are "the Fourier components of the node voltages at the nonlinear-facing nodes"
(`harmonic-balance.md §2`). Vdc/L/SnP/ZnP/IProbe are all linear → they're folded into the interface admittance
`Y_{N×N}` and the source injection `Y_s·V_s`, and their branch currents are recovered **only by the §9 linear
back-substitution, *after* Newton converges.** The SDD's `Evaluate`, however, runs **inside every Newton
iteration**. So `_cn` is needed at a point in the solve where the engine doesn't currently compute it.

This is the crux: `_cn` is a **well-defined function of the present interface-voltage iterate `V`** (the linear
network is linear, so each referenced branch current is a fixed linear functional of `V` plus the source
excitation) — but the engine would have to compute that functional *mid-iteration*, and feed it into a device
whose `Evaluate` is currently voltage-only. It also enters the **Jacobian**: `∂_cn/∂V` is nonzero, so a
correct, fast-converging Newton needs that coupling, not just the residual.

## Feasibility by engine

**DC (`NonlinearDcEngine`) — straightforward.** The state vector is literally `[V_nodes | I_branches]`; the
referenced branch currents are *already* unknowns in the same Newton system as the SDD's node voltages. A
control current is just another column the SDD's residual/Jacobian reads. `∂I[p]/∂_cn` stamps into the
SDD-row × referenced-branch-column entry. No back-substitution timing problem here — everything is
simultaneous. This engine could support `_cn` with a contained change.

**S-parameter / linearized (`StampLinearized`) — straightforward-ish.** Same MNA-with-branches structure; the
linearized SDD admittance block would gain entries coupling to the referenced branch unknown. Bounded.

**HB — the real design work.** Options, roughly in increasing fidelity:
- **(a) Residual-only, recompute `_cn` from `V` each iterate.** At each iteration, before/with the nonlinear
  evaluation, compute each referenced branch current from the current `V` (a linear back-solve of the
  *referenced* branches — a subset of the §9 machinery, run per-iterate instead of once at the end). Feed
  `_cn(t)` into the SDD time-domain evaluation. Leave the Jacobian's `∂_cn/∂V` coupling **out** (treat `_cn`
  as frozen within the linear solve of the step). Newton still converges to the right answer if it converges,
  but the missing Jacobian term may slow/weaken convergence (it's a quasi-Newton step). Cheapest path to
  *correct* results; risk is robustness on stiff cases.
- **(b) Full Jacobian coupling.** Add the `∂_cn/∂V` block to the conversion-matrix Jacobian so Newton sees the
  control-current feedback exactly. This is the correct, fast-converging version and the most work — it
  touches the §7 Jacobian assembly the weighting-function arc just finished generalizing. The control current
  is itself a linear functional of `V` per harmonic, so the block is a (precomputable-per-sweep-point) linear
  map — conceptually clean, but it's real surgery in `HbNewton.BuildJ`.
- **(c) Promote referenced branches to interface unknowns.** Make the referenced branch currents first-class
  HB unknowns (extend the Newton vector). Most invasive; probably overkill for v1.

My read: **(a) is the v1 target** (prove it works, get correct numbers), with **(b) as the convergence-quality
follow-on** if real circuits need it — mirroring how the weighting arc shipped the residual path then hardened
the Jacobian against the FD oracle.

## How many of the five can we do?

**All five are feasible** — the branch-current data exists for every one. They split by *plumbing effort*, not
by possibility:

- **Tier 1 — already expose the branch (Vdc, IProbe, Inductor):** each has a public `LastBranchIndex`. A
  resolver mapping instance-name → branch index is trivial for these. Single-port, so `Cport[n]` is moot.
- **Tier 2 — allocate branches but don't expose them (SnP, ZnP):** each allocates N per-port branches into a
  *local* array. They need a small change to **expose a per-port branch-index map** (e.g. record
  `int[] PortBranchIndices` on the model at stamp time, paralleling `LastBranchIndex`). `Cport[n]` selects
  which. Once exposed, identical to Tier 1 downstream.

So if we want to **stage** it: Tier 1 (V source, IProbe, inductor) is the smaller first cut and covers the most
common control-current uses (sense a bias current, an IProbe, an inductor current). Tier 2 (SnP, ZnP) adds the
per-port exposure and `Cport[n]`. **Recommendation: scope v1 to Tier 1 + the DC and HB-residual paths**, then
add Tier 2 and the Jacobian coupling as follow-ons. That keeps each brief small and lets the equivalence tests
gate each step — the same staging that worked for the weighting functions.

## Cross-cutting pieces any version needs

1. **Name→branch resolver (elaboration-time).** `C[n]=L1` / `Cport[n]=2` must resolve the instance name to a
   referenced component, validate it's a *referenceable kind* (error otherwise, naming the device — mirroring
   the existing SDD setup-error style), validate the port for multiport, and capture the branch-index handle.
   "Same hierarchy level" simplifies this — no cross-hierarchy path resolution; the referenced instance is a
   sibling in the same elaborated netlist. Still must error cleanly if the name is missing or the kind is
   unsupported.
2. **`Evaluate` contract extension.** `Evaluate` needs the control currents alongside port voltages. Likely a
   second array on `PortVoltages` (or a sibling struct) — `_c1.._cn` seeded the way `_v1.._vn` are. This is a
   contract change touching every nonlinear engine call site, so it wants its own small brief.
3. **`_in` while we're here.** VendorA exposes `_in` (the SDD's *own* port current) too. We already compute the
   SDD's port current (`ComputeDevicePortCurrents`, post-convergence) — but mid-iterate `_in` is the device's
   own residual current, a different timing question. Worth noting but **out of scope** for the control-current
   ask; flag it so we don't conflate `_cn` (other device) with `_in` (self).
4. **Lift the factory hard-error** on `C[]/Cport[]` as the last step, once the path exists.

## Bottom line for scoping

- **Supported by the architecture? Yes — all five device classes**, because every one already solves its
  current as a branch unknown. This is more than VendorA-parity-is-possible; it's parity-is-natural.
- **Free? No.** The cost is (i) a `Evaluate`-contract extension for `_cn`, (ii) a name→branch resolver, (iii)
  the HB mid-iterate computation of `_cn` from `V` (residual path = modest; full Jacobian coupling = real
  work), and (iv) per-port branch exposure for SnP/ZnP.
- **Suggested v1 cut:** Tier-1 devices (indep. V source, IProbe, inductor) + DC engine + HB **residual** path,
  with the contract extension and resolver. Defer SnP/ZnP (Tier 2) and the HB **Jacobian** coupling to
  follow-ons, each gated by an equivalence test (e.g. an SDD sensing an IProbe in series with a known current
  must reproduce that current in `_c1`).
- This is a **multi-brief arc**, not a one-shot. If that scope sounds right, the natural first brief is the
  contract extension + name→branch resolver + DC-engine support (the part with no HB timing subtlety), proving
  `_cn` end-to-end at DC before tackling the HB iterate.
