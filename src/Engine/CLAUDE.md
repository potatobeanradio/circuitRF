# Engine — local conventions

Standing instructions for `src/Engine` (the numeric layer: MNA assembly, linear analyses, and the
HB sub-engine in `HarmonicBalance/`). Read with the root `CLAUDE.md`. Design notes:
`docs/design/linear-engine.md` and `docs/design/harmonic-balance.md`.

**The full dated development changelog (phase-by-phase write-ups, measured benchmark numbers, test
counts) was moved to `HISTORY.md` on 2026-08-13.** This file keeps only current architecture,
invariants and recurring pitfalls. Grep `HISTORY.md` by phase name (`L8`, `L9`, `M0`…), brief slug,
or topic when you need the "why" behind a design choice or a historical measurement.

## What lives here
- The **`MnaSystem`** and the stamping API (`AddAdmittance`, `AddBlockAdmittance`, `AddBranch`,
  `AddBranchCurrent`, `AddConstraint`, `AddBranchConstraint`, `AddCurrentInjection`,
  `AddSourceValue`, `AddNodeBranchCoupling`).
- The **linear engine**: DC analysis, S-parameter analysis, and the linear characterization the
  HB engine consumes.
- The **harmonic-balance engine** (`HarmonicBalance/`, see its own `CLAUDE.md`).
- The **loadpull engine** (`Loadpull/`, see its own `CLAUDE.md`).
- The **planar full-wave MoM kernel** (`Mom/`, see its own `CLAUDE.md` — do not duplicate its
  content here; see the pointer section near the end of this file).
- The sparse solve (CSparse.NET) and the AMD fill-reducing ordering.

The engine sees only the **elaborated netlist** (fully-resolved kinded values, numbered nodes) and
returns a **`DataSet`**. No design-layer types, no UI, no expression strings reach here.

## Fixed conventions — record once, never silently change
A sign or direction flip here is the most expensive class of bug because results still look
plausible. Fix these in code as named constants/comments and do not change them without a
documented reason:
- **Ground is node 0.**
- **Branch-current direction:** a branch current flows from the element's **first** node to its
  **second**.
- **Current-source direction:** a current source `J` **injects into its first node** (and out of
  its second).
- **Time↔frequency sign convention and harmonic ordering** (DC, +k, −k): chosen and documented in
  `HarmonicBalance/CLAUDE.md`; every FFT round-trip uses the same one.
- **Port reference convention has one deliberate exception.** Most freq-domain N-ports (SnP/TLIN/
  user freq-models) use a single shared reference node (N or N+1 nets). **`Z_Port` and `SDD` use
  2N nets as differential ± pairs with a per-port reference instead**: `V_p = V(net[2p]) −
  V(net[2p+1])`. `ZPortModel` ignores `ElaboratedComponent.ReferenceNode`. Arity is validated at
  elaboration (odd net count, or netCount ≠ 2·portCount, are hard errors).
- **`__`-prefixed cube/node names are metadata, not signal.** `DataSet.StackSweepAxis` passes
  `__`-prefixed cubes through from the first dataset **unstacked** (no sweep axis prepended) rather
  than treating them as ordinary per-point data; the signal list and node/signal pickers skip every
  `__`-prefixed cube. `HbEngine` mints internal nodes with a `__` prefix (e.g. `__p1tone_*_drv`,
  `__tuner_*_block/bias`) and they are excluded from the HB `V`/`I` cubes' node axis for the same
  reason — only user-named nets appear there.

## Engine owns the matrix; models contribute stamps
The engine owns `MnaSystem` and orchestrates assembly and the sweep. A `ComponentModel` never sees
the raw matrix or global indices — it is handed resolved node indices and accumulates contributions
through the stamping API. This is what makes adding a component type local (root `CLAUDE.md` → "How
to add a component type"). Do not let a model reach around the API.

## One MNA assembly, three uses — keep them distinct
The same assembly/stamping serves three callers that differ in **frequency set, excitation, and
output** (`linear-engine.md` §2.1). Do not conflate them:
- **DC analysis** — single ω = 0; independent sources **on**; no ports; output = one operating
  point (node V + branch I).
- **S-parameter analysis** — swept frequency grid; independent sources **off** (zeroed: V-source →
  short, I-source → open); ports = user `Port`/`Term`; output = `S` cube.
- **HB linear partition** — per harmonic; linear partition **only** (nonlinear devices removed);
  sources **on**; "ports" = the **nonlinear-facing nodes**; output = the interface N-port **and**
  the source-excitation vector at that interface.

The DC (k = 0) member of the HB harmonic set uses the **same DC formulation** as the standalone DC
analysis — there is one DC formulation, not two.

## Element grouping (MNA)
- **Group 1 (admittance):** resistor, capacitor, current source, and any frequency-domain N-port
  **natively given as a finite `Y(ω)`**.
- **Group 2 (branch-current unknown):** inductor, voltage sources, current probe, mutual coupling,
  and **frequency-domain N-ports stamped as `Z(ω)`** (the default for Touchstone/SNP, impedance
  block, TLIN). `Z`-expansion is the robust default (every passive net has a finite `Z`); the
  native-`Y` admittance stamp is the lighter opportunistic path.

## DC correctness — no value fudges
DC is the **exact ω → 0** case: inductor → short (Group-2 constraint `Va = Vb`), capacitor → open
(admittance `jωC = 0`), floating nodes handled by a single documented **`gmin`** to ground. Never
reintroduce the prototype's large/small element-value clamps.

**Component robustness is warn-and-continue, not hard-error.** circuitRF is a research tool:
non-physical-but-mathematically-handleable inputs (R < 0, R = 0, mutual-coupling k ≥ 1) emit one
warning per component instance and proceed rather than throwing — R < 0 stamps a negative
conductance, R = 0 stamps a large-but-finite `Gmax` (`AnalysisSettings.Gmax`, default 1e12 S), and
k ≥ 1 stamps as given and relies on `InductanceRegularization` if the resulting block is singular.
Negative mutual inductance (anti-phase coupling) is **fully physical** and gets no warning at all.
Hard errors stay reserved for genuinely unresolvable conditions (missing required parameters,
elaboration failures).

## Regularization and bias stepping — the tri-state pattern
`AnalysisSettings` exposes several independent settings that all share one tri-state enum shape
(`IfNecessary` / `Always` / `Never`) rather than a bool, because "retry with regularization only on
failure" and "always regularize" are both real workflows:
- **`ConductanceRegularization`** — gmin (default 1e-12 S) from every node to ground.
- **`InductanceRegularization`** — a small series-R (1 nΩ) on every inductor branch diagonal, cures
  a rank-deficient coupled-inductance block (also reaches `TunerModel`'s internal choke branch).
- **`DcBiasStepping`** — ramps DC *bias supplies* 0→1 over `DcBiasRampSteps` (default 20) equal
  steps; distinct from HB's drive-power stepping (`DriveStepping`) — do not conflate the two, and
  see the diode-ring pitfall below for why `DriveStepping` doesn't substitute for damping.

`IfNecessary` (the default everywhere) means: attempt the direct/cold-start solve first, and only
apply the regularization/ramp on a caught failure — clean circuits pay nothing for it.

## Performance structure
- Sparse throughout (CSparse.NET); never a dense `n×n` solve for the full netlist.
- **Symbolic-once / numeric-per-frequency:** the nonzero pattern is fixed by topology — compute the
  AMD ordering + symbolic factorization once per topology, refactor numerically per frequency.
- **Factor-once / multi-RHS** for port extraction (one factorization, back-substitute per port).
- Native KLU/SuiteSparse stays a profiled, optional future optimization — never a v1 dependency.

## Output
Every analysis returns a **`DataSet`** of named single-kind `DataCube`s (→ `src/Core/Data/CLAUDE.md`).
S-parameter → `S {freq, i, j}` (Complex). DC → node V + branch I at ω = 0. HB → `V`, `I`
spectra (see `HarmonicBalance/CLAUDE.md`). Measurements are added to the DataSet as named cubes;
the engine does not invent its own result type. `__`-prefixed metadata cubes (see "Fixed
conventions" above) ride along the same `DataSet` but are excluded from the signal picker.

`MeasurementEvaluator` is resilient to a partial failure: each measurement line is evaluated in its
own try/catch, a failing one is skipped (its error collected and surfaced separately) and every
other measurement's cube is still emitted — one bad `measure` line does not blank the whole
DataSet.

## Engine diagnostics channel — firewall-safe, once per run
Engines surface run-time warnings (S-param regularization, HB non-convergence) via
`ElaboratedNetlist.AddWarning(message)` and `AddWarningOnce(key, message)` (Core-level;
`AddWarningOnce` deduplicates by key using a `HashSet`). **The engine never touches
`IMessageSink` directly** — that is a UI concept, and the UI firewall forbids any UI reference
in `src/Engine`.

- **`SParameterEngine`** calls `netlist.AddWarningOnce("sparam-regularization", ...)` once per
  run when the IfNecessary path fires (singular matrix retry). The message includes the
  `SingularMatrixException` detail, which names the floating node(s).
- **`HbEngine.Run`** (and `RunTwoTone`) accumulate `ncCount` / `worstRes` / `totalPoints`
  across ALL sweep points, then emit **one** summary via `AddWarning(...)` after the loop if
  `ncCount > 0`.

`SchematicRunService` drains `nl.Warnings` after the run (even on `EngineError`) into
`RunResult.Warnings`; `WorkspaceViewModel.RunAnalysis` posts them to the Messages pane at
Warning level.

## S-parameter analysis — port formulation

`SParameterEngine` uses a **Z0-terminated power-wave (Norton / Kurokawa) formulation** when all
port Z0 references have `Re(Z0) > 1e-12` (the common case):

- **Per port:** stamp conductance `1/Z0` between its nodes via `AddAdmittance` (no branch unknown).
- **Excitation:** for driven port `j` with unit incident wave `a_j = 1`, inject Norton current
  `I_j = 2·√(Re Z0_j) / Z0_j` at the port's nodes.
- **S extraction (Kurokawa):** after solving for port voltages `V_k`:
  `I_k = (k==j ? I_j : 0) − V_k / Z0_k`, then `b_k = (V_k − conj(Z0_k)·I_k) / (2·√(Re Z0_k))`,
  `S[k, j] = b_k`. No Y→S inversion step.
- **Singularity class eliminated:** parallel ports / port-across-short topologies are non-singular
  by construction — each port contributes a real positive conductance to its node, so the matrix is
  well-conditioned even when ports share the same node pair. Regularization is therefore a genuine
  last resort (floating internal nodes, exact admittance cancellation), not a routine crutch.
- **Legacy path** (any port has `Re(Z0) ≤ 0`, e.g. reactive reference impedance): ideal-0 V-source
  branch stamping + unit-voltage solve + `RFNetwork.YToS`. HB/DC are unaffected (they already treat
  Port/Term as inert). `PortEntry` carries both `BranchIndex` (legacy) and `Node0/Node1` (wave).

`MnaSystem.Factorize` wraps `AMD.Generate` to convert `ArgumentNullException` (empty matrix from
exact conductance cancellation) to `SingularMatrixException`, so the IfNecessary retry path fires.

**S-parameter analysis of a circuit containing nonlinear devices runs a DC pre-pass** (once, when
`Kind==Nonlinear` devices are present) and routes those devices through `StampLinearized` instead
of `Stamp` in `StampAll`, using the DC-solved node voltages as the small-signal bias point. Purely
linear circuits take no DC pre-pass and are byte-identical to before this existed. A zero/near-zero
bias or a non-converged DC pre-pass degrades to a warn-and-continue note rather than failing the
S-parameter run.

## HB sweep and result architecture

**All swept HB results — single-tone and two-tone — come from `ParametricSweepEngine`**, not from
any HB-internal sweep loop. `HbEngine.Run` is always single-point: `V[node, harmonic]` (or
`V[node, mixIndex]` for two-tone) plus scalar `Converged`/`Residual`. The swept axis is
**prepended** and named after the sweep variable, tagged with `Units.BaseUnit(origVar?.Unit ?? "")`
so marker readouts and axis labels show the right unit; when a swept variable itself carries an
explicit unit, the override is injected as a `Variable` in that same base unit so `FreqUnit.ResolveHz`'s
var-unit-wins rule doesn't double-apply a tone-frequency unit conversion. Axis layout after
stacking: `V[sweep…, node, harmonic]` / `V[sweep…, node, mixIndex]`; branch `I:*[sweep…,
harmonic/mixIndex]`. Downstream code (`TwoToneMeasurements`, the `MeasurementEvaluator` V/INl
accessor) finds the node axis **by name**, not position, so it works regardless of how many sweep
axes are prepended.

**`ParametricSweepEngine.RunInner` dispatches `SParameterAnalysis`, `DcAnalysis`,
`HarmonicBalanceAnalysis` and nested `ParametricSweepAnalysis`** — any analysis type can be wrapped
in a (possibly nested) parametric sweep (S-params vs a bias variable, a DC curve-tracer Vds×Vgs,
etc.). `DcAnalysis` delegates all result packing to the shared `DcResultPacker.Pack(result,
netlist)` (the same packer the standalone `SchematicRunService` path uses): a `V[node]` cube,
scalar `Converged`/`Residual`, scalar `I:<probe>` cubes per `IProbeModel` instance, and
`__LabeledNodes` metadata when present. Loadpull and other engine-owning analyses remain
unsupported inside the generic sweep (`NotSupportedException`).

**The HB `V` cube's `node` axis carries every non-ground user-facing node**, not only the
nonlinear-device interface nodes: interface nodes use the converged Newton solution directly,
linear-only nodes (touched only by R/L/C/sources) are back-solved via
`HbLinearBackSolver.GetNodeVoltage`, and `INl` is 0 at every harmonic for a linear-only node. Nodes
are emitted in ascending circuit-node-index order — stable and topology-invariant across sweep
points, which is what lets `ParametricSweepEngine` stack the axis.

**The harmonic axis carries integer orders `[0,1,…,K]` (unit `""`), not frozen `k·f0` frequencies.**
Physical frequency is reconstructed as `order × f0(slice)` wherever needed (via
`HbSpectrum.HarmonicFreqHz`), because the per-slice fundamental can vary across a sweep. Every HB
run also emits a stacking `ToneFreqs` cube carrying the per-operating-point fundamental(s)
(`ToneFreqs[tone]` for one tone, `ToneFreqs[tone(2)]` for two); after `StackSweepAxis` this becomes
`ToneFreqs[sweep…, tone]`, which is how a swept fundamental is recovered per point. Export emits
integer orders plus the `ToneFreqs` cube; consumers reconstruct physical frequency from the pair.

**`__LabeledNodes`** is an optional metadata cube (`HbEngine.BuildSingleToneDataSet` /
`BuildTwoToneDataSet`) with one axis `label`, emitted whenever the netlist has any user-labeled
nets. Absent → the node picker defaults to show-all; present-but-empty → the picker shows nothing
(the user tagged nothing); present-and-non-empty → the picker defaults to showing only the labeled
set.

## SDD control-current extensibility

An SDD's control current `C[n]=<ref>` may reference **`Vdc`, `IProbe`, `L`, `SnP`, `ZnP`,
`V_1Tone`/`V_nTone`** (a tone source is a Group-2 branch-current element structurally identical to
`VdcModel`, so its branch current is a first-class unknown). `P1Tone` is **deliberately excluded**
— it is a 3-node source behind an internal impedance with two HB branches, and "the current" is
ambiguous there. Each of `NonlinearDcEngine.GetControlBranchIndex`, `HbEngine.GetControlBranchIndexHb`
and `SParameterEngine.ResolveSParamBranchIndex` independently validates and resolves the referenced
device's branch index — branch numbering is **per-run** (DC/HB and the S-param wave path number
branches differently, since the wave path skips ports), so the S-param path re-resolves against a
throwaway `StampAll` pass before its frequency loop rather than reusing DC/HB's indices.

`IMnaContext.AddNodeBranchCoupling(node, branch, coeff)` is the `(node-row, branch-col)` transpose
of `AddConstraint`, added specifically so a node's KCL row can depend on a branch current — which is
what a control-current-dependent SDD needs in the S-parameter small-signal column.
`SddModel.StampLinearized` (the linearized-around-bias override used by the DC pre-pass above)
builds that column from `DControl`/`DControlCharge`/`JacCtrl_w` sensitivities evaluated at
`SddModel.ControlBias`, captured once per run by a DC pre-pass and seeded to zero if that pre-pass
does not converge.

## Port and source model specifics worth knowing before touching them
- **`P1ToneModel`** — available-power (`Pavl` dBm) source behind internal impedance `Z` (default
  50 Ω), with optional per-harmonic-band terminations `Z[k]`/`G[k]` (same shape as `Tuner`). Node
  layout `[0]`=DUT-facing, `[1]`=reference, `[2]`=minted `__p1tone_<inst>_drv`. Band assignment is
  `n = round-half-up(|f| / f_c)`; `f_c` is set per-run by `SetToneContext` (single-tone: `f_c=f0`;
  two-tone: `f_c=(f1+f2)/2`), called before extraction. In S-param mode (`f_c ≤ 0`) it stamps only
  `Z_Port(nExt, nRef, Z[1])` — no drive branch.
- **`TunerModel`** — `ComponentModel` wrapping `Z_Port` + a bias-tee (L, C, V_supply) + an optional
  `V_1Tone` drive when acting as a source tuner. Four-node layout: two declared nets plus two
  minted internal nodes (`__tuner_<inst>_block` / `__tuner_<inst>_bias`). `SetHarmonicOverride(k,
  Z)` overrides one harmonic for a loadpull grid sweep; `HbLinearExtractor` zeroes its drive/bias
  sources (not its impedance topology) when extracting the linear interface (`zeroDrive=true`).

## `HbLinearExtractor.ExtractImpedance` — the open-port intermediate

Additive to `Extract`/`ExtractDC` (both still compute exactly what they always did). It returns the
interface **impedance** matrix and the open-circuit voltages — the extraction stopped one step
before `Y = Z⁻¹`. `Y` is right for the Newton loop's interface, which is always terminated and
therefore well-conditioned; it is wrong for a network whose ports are deliberately left **open**
(e.g. a pre-terminated extraction), because an open port's driving-point impedance can span eight or
nine decades and inverting it spends them — closing the terminations in the impedance domain instead
of after a `Y` inversion recovers several more decades of accuracy. The constructor's optional
`extraInterfaceNodes` defaults to null (shipped behaviour: the interface is exactly the nonlinear-
facing nodes).

## Batched external-device evaluation (HB inner loop)

`ComponentModel.EvaluateBatch(double[][])` is the seam for a device that can amortize a whole grid
of samples in one call (e.g. an external device worker paying a round trip per evaluation) —
`PrefersBatchEvaluate` opts a device in; the default is a scalar-loop wrapper over the ordinary
`Evaluate`, so a provider that never implements batching gets correct, merely un-amortized, results.
**Built-in models never set `PrefersBatchEvaluate`**, so a built-in device's result is bit-identical
by construction, not by tolerance — batched and unbatched HB solves are gated as bit-identical.
`ElaboratedComponent` carries both flags so the device multiplier is applied in exactly one place;
`ComputeDevicePortCurrents` batches too, for the same reason. **The control-current form
(`_c_ref(t)`) is always scalar** — it is per-sample by construction and only an SDD has one, and an
SDD is never an external device, so batching never has to reach it. `NonlinearDcEngine` and
`HbNewton2D` can adopt the same seam with no second design.

## `RunControl` — cancellation and progress, at a point boundary

`src/Engine/RunControl.cs` is the one object an engine takes for both cancellation and progress, so
a caller wires them once instead of threading two parameters through every signature. Every entry
point that supports it takes `RunControl?` as a trailing optional argument defaulting to null, so a
null control reproduces the pre-cancellation behaviour exactly.

**Each engine checks only at a point boundary, never inside a factorization, back-substitution or
Newton loop** — that is what keeps the check cheap enough to be always on. `ParametricSweepEngine`
checks per sweep point, `SParameterEngine` per frequency (both paths), `LoadpullEngine` per grid
termination, `LoadpullPursuitEngine` per cache-miss query. `HbEngine` and `NonlinearDcEngine` take no
control at all — a single solve has no boundary to offer. The cost of this granularity: Stop is
answered within one point rather than instantly.

**Cancelling abandons the run; it never produces a partial result** — the per-point DataSets stack
along an axis of known length, and there is no shape a half-finished sweep could publish in. Engines
throw `OperationCanceledException`; the caller catches it and publishes nothing.

**Progress counts leaf work units, and only the innermost countable loop counts them.** A sweep
hands a non-sweep inner analysis a `Child()` token (same token, no progress sink) so an inner
analysis's own loop cannot double-count; a nested `ParametricSweepAnalysis` is handed the full
control so the innermost sweep does the counting. Reports are throttled (default ~25/s, with the
final tick of a known total always delivered) because every delivered observation posts to the
caller's UI thread.

## Loadpull

`LoadpullEngine` (2-D outer Γ/Z grid × inner adaptive Pin drive-up, `InductanceRegularization=Always`
forced for the inner HB solves, VSWR-nearest Γ-grid warm-start) and `LoadpullPursuitEngine`
(MXP/MXE search + auto-Zsource) live in `Loadpull/` — see its own `CLAUDE.md`. `FomResult` and
`LoadpullEngine.ComputeFoms` (Pout / Pin_delivered / Gt / Gp) are **public**, so a caller
(harmonicaRF's own Pin search, for one) can share the one FOM definition rather than re-deriving it;
it is a pure function of its arguments.

## `Mom/` — the planar full-wave MoM kernel (kernel B)

**`src/Engine/Mom/` has its own `CLAUDE.md` — read it before touching anything under `Mom/`.**
Everything below is a one-line pointer into phases whose full derivation, measured tables and
findings live there; do not re-derive or duplicate that detail here. Ordered roughly chronologically:

- **L6/L7 — kernel A**, the 2-D quasi-static per-unit-length MoM kernel (RLGC extraction) that
  founded `src/Engine/Mom/`. Consumes a neutral `EmProblem` (not a Ui type).
- **L7b / L7b-b — coupled lines and general modal decomposition.** L7b: symmetric-pair coupled-line
  s-parameters via a fixed `[1 1;1 −1]` modal transform (no eigensolver needed by symmetry). L7b-b:
  any N conductors, symmetric or not, through one general path (`Gevd` on the lossless problem, loss
  carried perturbatively) — supersedes L7b's forced-symmetric matrix.
- **L8a — the layered Green's function**, kernel B's foundation: DCIM over a grounded slab, an
  entirely different kernel from A. Ships a **measured validity range**
  (`Dcim.WithinValidatedRange`), not an unconditional "DCIM works" claim.
- **L8b — the surface mesher and N report.** Tensor-product grid, per-axis pitch, edge grading for
  conductor rims; N (basis functions, not cells) is what `R17`'s unknown ceiling budgets.
- **L8c — the fill and singular integrals.** Two singular kernel pieces (`1/ρ` and a real `ln ρ`
  surface-wave term) are extracted and integrated in closed form; only the smooth remainder is
  quadrature.
- **L8d — ports and de-embedding.** A port is an incidence matrix (`Y = BᵀZ⁻¹B`); de-embedding uses
  a TRL-like line-standard calibration. De-embedding accuracy is ultimately limited by radiation
  coupling between ports, not by the algebra.
- **L8e — the kernel registry, planar `DataSet`, current density.** `EmKernelRegistry` unifies the
  *output* contract (`EmKernelOutcome`) across kernels A and B, not the input; auto-selection prefers
  A whenever A accepts and falls back to B, refusing with both verdicts when neither does.
- **L9a — the general layered medium.** Arbitrary stratified stack, arbitrary source/observer
  height. The shipped one-layer kernel stays the gate (exact agreement to ~1e-13).
- **L9b — DCIM for the general layered medium.** An open-below stack's second branch point is a
  *structural* obstruction (an entire-function exponential fit cannot carry a genuine branch cut),
  refused by name rather than chased with more fit terms.
- **L9c — z-directed current, vias, the multi-level problem.** Four kernel components (not two);
  the image sign for a vertical current flips relative to a horizontal one because it reflects a
  *current*, not a voltage. A via is a rooftop one dimension over (a cell pair in z), so charge
  conservation is exact by construction.
- **L9d — multilevel ports, references, de-embedding.** Turns a two-level `Z` into an s-parameter.
  A port cannot sit on a via basis (no in-plane end to reference); `G_A^zz`'s ρ/λ ≤ 0.1 validity
  limit became a scoped refusal here, firing only on meshes that actually carry vertical current.
- **L9e — adaptive frequency sampling, N budget, refusal audit.** Adaptive sampling is what makes
  the near-DC hole closeable (an adaptive scheme can simply choose frequencies away from it). ACA
  (low-rank far-field compression) was measured and **deferred** — far blocks under R17's ceiling
  aren't many wavelengths apart, so the achievable rank is too high to pay for itself.
- **Edge mesh on curved geometry** — a **negative result**: a graded fan on a *staircased* rim buys
  nothing, because a staircase's own discretization error dominates whatever the fan resolves.
  Curved geometry needs conformal cells instead (its own later phase). `PlanarRimGrading` ships with
  `None` as the default; a Manhattan mesh is bit-identical with it enabled.
- **`G_A^zz`'s accuracy ceiling.** M0 scoped the ρ/λ ≤ 0.1 refusal to the actual via-footprint
  separation rather than the mesh diagonal, which is what let an ordinary via-bearing board run at
  all. M2 ships `PlanarFillSettings.DirectVerticalKernel` (default off) — the ẑẑ block alone can take
  its kernel from direct Sommerfeld integration instead of the DCIM fit, at real per-point cost.
- **Ground vias — the attachment basis** (Part A only; Part B, interior electrostatics / buried-level
  `C_pul`, is not started). A backside via joining a signal level to the laterally-infinite ground
  plane needed a new *attachment* (half) basis distinct from the interior-via basis; both now
  coexist in one mesh with reciprocity kept structural by a shared vertical-current sign convention.
- **The via z-integral — removing the midpoint rule (complete).** The midpoint rule froze `1/R` over
  a via's own length and overstated its inductance by ≈0.67·(ℓ/w); the fix splits the z-integral into
  closed-form asymptotic pieces plus an ordinary rule on the smooth remainder. `MaxLengthOverWidth`
  is retired (geometry no longer needs a bound); `MaxElectricalLength` remains, but now refuses on
  the basis-function approximation (uniform current along the via), not on integration error.
- **"Long enough to crowd" — the edge-attractor rule fix.** An edge earns graded meshing based on
  whether it *terminates* a conductor (both corners convex), not on its length relative to the whole
  polygon — the old rule under-meshed the narrow end of an asymmetric taper.
- **Convex decomposition — M0/M1.** `IsConvex` was a sufficient-but-not-necessary test for the real
  requirement (flow-simplicity, per transverse direction); swapping the predicate tiled a previously
  worst-case PCell to round-off with no decomposition step needed. Route B (actual polygon splitting)
  was consequently **not built** — measured to have an empty residue.
- **Mesh frequency (M0).** `PlanarMeshSettings.MeshFrequencyHz` sizes the mesh independently of
  `MaxFrequencyHz` (the sweep's own top, still used for the electrical-via bound and other physics
  refusals — never repoint those at the mesh frequency). Report notes now state the trade in
  effective cells/λ rather than hertz when the mesh frequency sits below the sweep top.
- **Calibration standards — fill only the two that are read.** A de-embedded point always solved
  every calibration standard and used only two per frequency (`GammaBest`'s own selection is
  computable before any fill); `PlanarCalibration.SelectSeparation` now gates that computation
  up front. The standard *set* is unchanged — every separation is still built.
- **M1/M2 — parallelism.** One shared `PlanarSolveSettings.MaxDegreeOfParallelism` (null =
  automatic) spends a single `PlanarParallelBudget`, replacing what would otherwise be two caps a
  reader has to multiply together. Cross-frequency/DUT-and-standards fan-out (M3) was **measured
  before being built, and not built** — a single fill already saturates the box's cores, so there is
  nothing idle to overlap.
- **M5 — the AIM accelerator.** `PlanarFillSettings.Aim` (null/off by default — the dense path is
  untouched). Its win is **memory**, not time, at practical unknown counts under R17's ceiling — a
  time win only shows up well past where memory already limits a run. The multi-level/via path is
  refused by name (a ẑ basis needs a different grid kernel and is a separate phase).
- **`PlanarFeedExtension` — the solver grows its own calibration feed.** A taper's oblique flanks
  broke the assumption behind a uniform-line calibration standard's diagonal term, producing a
  passivity-violating de-embedded result on ordinary artwork. The solver now extends a short,
  targeted feed onto the user's own port before calibrating, transparently, with the user's artwork
  and reference planes untouched.

## Known pitfalls — do not reintroduce these

- **A floating nonlinear port was solved wrong by HB and right by DC.** `HbNewton`/`HbNewton2D`
  accumulated a nonlinear device's port current at the port's **+** net only; a port spans two nets,
  so the **−** net silently violated KCL. Every circuit in the original suite referenced device ports
  to ground, which hid it — it took a port floating across two live nets (a diode ring, a bridge) to
  expose it. It **converges cleanly to a wrong answer** (a passive mixer showed 128 dB of "conversion
  loss" — i.e. none). Fixed via shared `PortAdd`/`PortAdd4` helpers (current *and* the 4-way
  Jacobian corners) used by both HB assemblers and the SDD control-sensitivity path.
  `NonlinearDcEngine` had always done both signs — the two engines disagreed with each other on the
  same circuit. Gate against a **closed-form oracle**, never another circuitRF path, since two wrong
  implementations can agree with each other.
- **DC non-convergence must say WHERE, not just how big.** A bare residual norm gives no address to
  chase on a real design with hundreds of unknowns. `DcResult.ResidualPerUnknown` plus the
  non-convergence warning naming the worst three unknowns (by node name, or `branch unknown #k` past
  the node count) turns "residual 35.6" into an actionable pointer. Build the residual vector once
  per Newton iteration, not twice (evaluating every nonlinear device is the expensive part).
- **Inductor series-`R` was silently dropped** — the original `InductorModel.Stamp` read only `jωL`
  and ignored an `R=` parameter on an `L:` line, which zeroed out the one lossy element that made a
  coupled-inductance block non-singular at AC (root cause of the Hero-1B singular-matrix failure).
  Series R (and, separately, an optional series C making the branch a series-RLC) must be stamped on
  the same branch diagonal whenever present.
- **A stiff diode ring (`Cj0 = 0`) needs Newton damping, not drive stepping.** `Lambda = 0.5`
  converges where `Lambda = 1` diverges (‖F‖ ~1e11). `DriveStepping` does not help here — there is no
  separate DC bias to ramp toward; in a passive mixer the drive *is* the bias.
- **Verify a full-suite-load timing flake in the same load it flaked in.** A wall-clock budget test
  (e.g. `Hero1BTests`) that is marginal under concurrent full-suite execution but passes cleanly
  alone is *not* fixed by re-running it in isolation — isolated repetition proves nothing about a
  race or contention effect that only appears under load.
