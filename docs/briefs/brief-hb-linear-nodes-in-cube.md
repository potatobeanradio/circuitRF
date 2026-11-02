# Sonnet Brief — HB V/INl cubes: include linear (non-interface) nodes like Vout2

**Problem (confirmed).** A node such as `Vout2` is missing from the trace-card node options for HB-sweep data,
even though it's in the netlist. Root cause: the HB `V`/`INl` cubes are built **only over interface nodes** —
`extractor.InterfaceNodes` (the nonlinear-device port nodes). A node connected only to **linear** elements
(R/L/C/sources, no nonlinear-device port) is not an HB unknown, so it never enters the `node` axis. The data
display lists exactly the cube's `node` axis labels, so linear-only nodes are absent.

**The recovery already exists.** `HbLinearBackSolver.GetNodeVoltage(circNode, k, si)` solves the full MNA network
from the converged interface solution + snapshotted source RHS and returns **any** non-ground node voltage,
including linear-only ones. The single-tone `Run` already constructs this back-solver and returns it in
`HbRunResult`. The gap is purely that `BuildSingleToneDataSet` doesn't *use* it to populate extra nodes, and
the swept path (`ParametricSweepEngine`) only consumes the DataSet.

**Goal.** The HB `V` cube's `node` axis should include **all non-ground circuit nodes** (interface + linear),
with linear-node voltages back-solved. So `Vout2` appears in the picker and reads its real spectrum.

## Design

### Single-tone (`HbEngine.Run` + `BuildSingleToneDataSet`)
`Run` already has `extractor`, the converged interface `V`/`INl`, and builds `backSolver`. Extend the V cube to
all non-ground nodes:
- Determine the full node set: circuit nodes `1 .. _netlist.Nodes.Count-1` that are non-ground (node 0 is
  ground). Skip internal `__`-prefixed mint nodes? **Decision: include them but they're rarely probed** — or
  filter names starting with `__` to reduce clutter. **Recommend: filter `__`-prefixed nodes out of the cube**
  (they're engine-internal: tuner/p1tone mint nodes), keeping user-facing nodes only. State this in the note.
- For each node `c` in the full set and harmonic `k`:
  - if `c` is an interface node, use the converged `V[ifIdx, k]` (exact);
  - else use `backSolver.GetNodeVoltage(c, k, 0)` (single-point → si=0).
- Build `nodeNames` from `_netlist.Nodes.NameOf(c)` for the full set; node-axis values = sequential indices.
- **INl for linear nodes is zero** (no nonlinear device current there) — fill 0 for non-interface nodes so the
  `INl` cube keeps the same node axis as `V`. (INl is only meaningful at interface nodes; zero elsewhere is
  correct and keeps the two cubes aligned.)

Pass the `backSolver` (or a `Func<int,int,Complex>` voltage accessor) and the full node list into
`BuildSingleToneDataSet`. Keep the harmonic axis unchanged. The branch-current `I:*` cubes are unchanged.

**Signature change:** `BuildSingleToneDataSet` currently takes the interface `V`/`iNl` + `nodeNames`. Change it
to take the **full** node set it should emit + a voltage accessor for non-interface nodes (or pre-assemble the
full `Vfull[node,k]`/`INlfull[node,k]` arrays in `Run` and pass those — simpler and keeps the builder dumb).
**Recommend pre-assembling `Vfull`/`INlfull` + `nodeNamesFull` in `Run`** (where `backSolver`, `extractor`,
`ifNodes` are all in scope), then `BuildSingleToneDataSet` just packs them. Minimal change to the builder.

### Two-tone (`RunTwoTone` + `BuildTwoToneDataSet`)
`RunTwoTone` currently does **not** construct a back-solver. Two options:
- **(A) Add a back-solver to the two-tone path** mirroring single-tone (snapshot `bSrc` per mix product, build
  `HbLinearBackSolver` analog over the mixing lattice) → emit full nodes. This is more work (the back-solver is
  harmonic-indexed `k`; two-tone is mixIndex-indexed — the solver's `GetSolution(k, si)` and `Extract` are
  per-ω, and `ExtractMix` already handles negative-ω, so a mix-indexed back-solve is feasible but non-trivial).
- **(B) Scope this brief to single-tone**, and for two-tone keep interface-only nodes for now with a noted
  follow-up. The user's case (HB sweep, `Vout2`) is single-tone.

**Recommend (B):** do single-tone fully; note two-tone linear-node recovery as a follow-up. If (A) is cheap once
single-tone is factored (the mix-indexed back-solve reuses `extractor.SolveFullNetwork` at each `grid.OmegaOf`),
do it — but don't block single-tone on it. Flag which you did.

### Sweep path (no change needed)
`ParametricSweepEngine` re-runs `HbEngine.Run` per sweep point and stacks each point's DataSet. Once each
single-point DataSet carries the full node axis, the stacked swept cube does too — **no `ParametricSweepEngine`
change**. (Each point re-elaborates and re-back-solves; the back-solver is per-run, which is correct.)

### Node ordering / stability
Emit nodes in a **stable order** (ascending circuit-node index) so the node axis index is deterministic across
sweep points (the stacker requires identical axes per point — same names, same order). Interface vs linear
interleave by circuit-node number; that's fine as long as it's identical every point (it is — topology is
constant across a sweep).

## Tests (`tests/Engine.Tests`)
1. **Hb_VCube_IncludesLinearNode:** a circuit with a linear-only node (e.g. an RC divider off the drain to a
   `Vout2` node with no nonlinear device) → the single-tone `Run` DataSet's `V` cube `node` axis Labels contain
   `Vout2`, and its fundamental voltage matches a hand/back-solved value (cross-check against
   `backSolver.GetNodeVoltage`).
2. **Hb_LinearNode_INlZero:** the `INl` cube at the linear node is 0 at all harmonics (no device current there).
3. **Hb_InterfaceNodes_Unchanged:** interface-node voltages in the full cube equal the converged `V` (regression
   — back-solve must reproduce the interface solution exactly at interface nodes, or be sourced directly from
   `V`).
4. **Hb_InternalNodesFiltered:** `__`-prefixed mint nodes do not appear in the `node` axis (if filtering chosen).
5. **Sweep_FullNodes:** a `ParametricSweepEngine` over an HB analysis → the stacked `V` cube includes the linear
   node at every sweep point (axis identical across points).
6. Existing HB tests (`Hero2*`, golden generators reading interface nodes by **name** via `Axes[1].Labels` /
   `node` axis) still pass — they look up `n_drain`/`n_gate` by name, which still resolve; the axis just has more
   entries. **Verify the golden generators/regression tests find nodes by name, not by hard-coded index** — if
   any use a fixed node index, fix them to look up by label (they should already, per the data-model "node axis
   carries Labels" convention).

## Gate
Build 0W/0E; tests green. Manual: re-run the attached HB sweep → `Vout2` now appears in the trace-card node
options; selecting V at `Vout2`/fundamental plots its real spectrum (not absent/zero). `n_drain`/`n_gate` still
work. Golden regression tests unchanged (name-based lookup).

## On completion
Note in `src/Engine/CLAUDE.md`: the HB `V` cube node axis now includes **all non-ground user nodes** (interface
nodes use the converged solution; linear-only nodes are recovered via `HbLinearBackSolver`); `INl` is zero at
non-interface nodes. `__`-prefixed internal mint nodes are excluded. Two-tone linear-node recovery is a noted
follow-up (interface-only for now). Sweep stacking is unaffected (per-point full node axis is identical across
points).
