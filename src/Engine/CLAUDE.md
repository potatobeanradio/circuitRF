# Engine — local conventions

Standing instructions for `src/Engine` (the numeric layer: MNA assembly, linear analyses, and the
HB sub-engine in `HarmonicBalance/`). Read with the root `CLAUDE.md`. Design notes:
`docs/design/linear-engine.md` and `docs/design/harmonic-balance.md`.

## Node-picker filter fix — StackSweepAxis passes `__`-prefixed metadata cubes unstacked (brief-node-picker-filter-fix, 2026-06-16)

`DataSet.StackSweepAxis` (RfCore/src/Data/DataSet.cs) now passes `__`-prefixed cubes through from the first dataset verbatim instead of calling `DataCube.PrependAxis` on them. Before the fix, stacking prepended the sweep axis onto `__LabeledNodes`, making it rank-2 (`[sweep, label]` instead of `[label]`); `RebuildAxisRolesCore` read `Axes[0].Labels` which was then the numeric sweep axis (Labels == null) → empty labeled set → filter broke for swept runs. The fix also updates `TraceRowViewModel.RebuildAxisRolesCore` to find the label axis by `Name == "label"` instead of by position, so it is resilient to any future shape change. Gate: `Stack_PreservesLabeledNodesShape`, `Stack_MetaCubeNotSwept` (Engine.Tests); `Picker_FiltersAfterSweep` (Ui.Tests); `Table_TraceHeader_HitTest_ReturnsTraceHeaderKind` (Ui.Tests). 4 new tests + 2 existing fixes; 1483 total tests pass.

## CNL provenance round-trip — `labelednets` directive (brief-cnl-labelednets-provenance, 2026-06-16)

**Root cause fixed:** `CnlWriter` never emitted `TestBench.LabeledNets`, so after the GUI wrote a `.cnl` file and `CnlReader` read it back, `tb.LabeledNets` was always empty → `HbEngine` skipped `__LabeledNodes` → picker showed all nodes.

**Fix:** `CnlWriter.Write` appends `labelednets n1 n2 …` (sorted, top-level) when `tb.LabeledNets.Count > 0`. `CnlReader.TryParseLine` parses it back into `tb.LabeledNets`. The directive is only valid at top level (inside `define … end` throws).

**T7 test** (`HbLabeledNodesCubeTests.T7_EndToEnd_SchematicCnl_EmitsLabeledNodesCube`): populates LabeledNets in-memory → `CnlWriter.Write` → `CnlReader.Read` → `Elaborator` → `HbEngine` → asserts `__LabeledNodes` present with correct labels. This is the regression guard for the full GUI run path (T4/T6 only covered the in-memory injection path).

## Node-picker labeled filter — `__LabeledNodes` side cube (brief-node-picker-labeled-filter, 2026-06-16)

`HbEngine.BuildSingleToneDataSet` (and `BuildTwoToneDataSet`) emit a `__LabeledNodes` metadata cube when `_netlist.Nodes.LabeledNames` is non-empty. The cube has one axis `label` with `Labels` = the labeled node names that actually appear in the `node` axis; values are all-zeros (unused). The `__` prefix marks it as metadata: the signal list and signal picker skip all `__`-prefixed cubes. Round-trips automatically via the generic DataSet `.npy` exporter.

- Absent `__LabeledNodes` (hand-written CNL, no schematic labels) → picker UI defaults to show-all.
- Present-but-empty (schematic ran, user tagged nothing) → picker shows nothing (filter ON, empty set).
- Present-and-non-empty → picker shows only the labeled nodes by default (`ShowAllNodes=false`).

Provenance thread: `NetExtractor.AssignNetNames` → `TestBench.LabeledNets` → `Elaborator` → `NodeMap.LabeledNames` → `HbEngine` → `__LabeledNodes` cube. Gate tests: `HbLabeledNodesCubeTests.cs` (T4, T6).

## Z_Port per-port references — 2N nets, ± pairs (brief-zport-per-port-refs, 2026-06-16)

**Z_Port now uses 2N nets as differential ± pairs with per-port references** (`V_p = V(net[2p]) − V(net[2p+1])`),
parallel to the SDD — **NOT** the N-or-(N+1) shared-reference convention. That single-shared-reference
convention still applies to **SnP/TLIN/user freq-models** (unchanged).

- `ZPortModel` no longer reads `ElaboratedComponent.ReferenceNode` (stays at default 0; stamp ignores it).
- Arity validated in `Elaborator.ResolveZPortParameters`: odd net count → error; netCount ≠ 2·portCount → error.
- Schematic: ZPort reuses the SDD 2N-pin ± port generator (`GenerateSddPorts` / `GenerateSddVariadicPorts`).
  `PortCount` = N (signal ports); pin count = 2N; `FromRenderModel` derives ZPort N = pins/2.
- `linear-engine.md` §4.1/§4.4 note: Z_Port is the exception to the N-or-(N+1) shared-reference rule.
- CNL format: `Z_Port:Name  n1+ n1−  n2+ n2−  …  Z[i,j]=expr` — 2N nets, no trailing refnet.
- 9 gate tests: `ZPortArityTests` (Core.Tests), `ZPortPerPortRefTests` (Engine.Tests),
  `ZPortSymbol_2Port_Has4Pins` + `ZPort_NetExtraction_4Nets` (Ui.Tests).

## HB V cube — full user-node axis (brief hb-linear-nodes-in-cube, 2026-06-16)

The HB `V` cube's `node` axis now includes **all non-ground user-facing nodes** (interface + linear-only), not only the nonlinear-device interface nodes.

- **Interface nodes** (nonlinear-device port nodes): use the converged Newton solution directly.
- **Linear-only nodes** (connected only to R/L/C/sources, no nonlinear port): recovered via `HbLinearBackSolver.GetNodeVoltage(c, k, 0)`.
- **`INl`** at linear-only nodes is 0 at all harmonics (no nonlinear device current there). The `V` and `INl` cubes keep the same `node` axis.
- **`__`-prefixed internal mint nodes** (e.g. `__p1tone_*_drv`, `__tuner_*_block/bias`) are **excluded** to reduce clutter; only user-named nets appear.
- **Stable order**: nodes emitted in ascending circuit-node-index order (topology-invariant across sweep points — required for `ParametricSweepEngine` axis stacking).
- **Two-tone** linear-node recovery is a noted follow-up: `RunTwoTone` still emits interface-only nodes (no back-solver there yet).
- **`ParametricSweepEngine`** is unaffected: each per-point DataSet already carries the full node axis; stacking works unchanged.
- 5 gate tests: `HbLinearNodeTests` T1–T5 (`tests/Engine.Tests/HarmonicBalance/HbLinearNodeTests.cs`).
- `Hero2Tests.ExtractVMatrix` updated to filter to interface nodes (using `HbLinearExtractor`) for `RunJacobianDiagnostic` (which needs Newton unknowns only).

## P1ToneModel — single-tone RF power source (brief-sweep-5, 2026-06-16)

`P1ToneModel` (`src/Core/Devices/P1ToneModel.cs`) is the power-domain RF source: available power
`Pavl` (dBm) behind internal impedance `Z` (Ω, default 50), with optional per-harmonic-band
terminations `Z[k]`/`G[k]` (same as Tuner).

**Key design points:**
- Node layout: `[0]` = DUT-facing, `[1]` = reference (ground), `[2]` = minted `__p1tone_<inst>_drv`.
- Band-assignment rule: `n = roundHalfUp(|f|/f_c)` = `(int)Math.Floor(|f|/f_c + 0.5)`.
- `f_c` (band-center) set by `SetToneContext(fc, driveFreqHz)` — called by `HbEngine.Run()` /
  `RunTwoTone()` before extraction. Single-tone: `fc=f0`; two-tone: `fc=(f1+f2)/2`.
- `|Vs| = sqrt(8·Re(Z_at_fundamental)·Pavl_W)` (matched-load; recomputed in `SetToneContext`).
- S-param mode (`_fc≤0`): stamps `Z_Port(nExt, nRef, Z[1])` only — no drive branch.
- HB mode: drive branch `V=Vs@driveFreqHz` at `nDrv→nRef`; `Z_Port(nExt, nDrv, GetZ(ω))`.
- `HbEngine.CheckCommensurability` and `CheckCommensurabilityMultiTone` check `P1ToneModel.FreqHz`.
- Factory: `"P1Tone"` added to `_parameterizedTypes`; `CreateP1ToneModel` uses same `RxTunerZ`/`RxTunerG`
  regex + Γ→Z conversion. `Z` serves as both `Zdefault` and `Z0` for conversion.
- Elaborator: mints `__p1tone_{childPath}_drv`; dispatches `ResolveP1ToneParameters`.
- 7 gate tests in `tests/Engine.Tests/HarmonicBalance/P1ToneTests.cs`.

## HB sweep architecture (Sweep-3 migration, 2026-06-16)

All swept HB results — single-tone and two-tone — come from `ParametricSweepEngine`. The swept
axis is **prepended** (first) and named after the sweep variable (e.g. `Pavl_dbm`, `Pin`, `Vgg`).
HB-internal sweeping is fully retired: `HbEngine.Run` is always single-point, producing
`V[node, harmonic]` or `V[node, mixIndex]` plus scalar `Converged`/`Residual`.

- **Axis layout after ParametricSweepEngine:** `V[sweep…, node, harmonic]` (single-tone),
  `V[sweep…, node, mixIndex]` (two-tone); branch `I:*[sweep…, harmonic/mixIndex]`.
- **Tests and golden generators** were migrated to the parametric path; golden CSV numbers
  are unchanged.
- **Exported linear-network payload / back-solver** (`LinearPayload`, `ILinearBackSolver`) is
  single-point per HB run; a sweep-aware exported payload is a known follow-up.
- `TwoToneMeasurements` now finds node/mixIndex axes by **name** (not positional), so it works
  regardless of how many sweep axes are prepended.
- The `MeasurementEvaluator` V/INl accessor likewise finds the node axis by `Name=="node"`;
  the I branch accessor treats the last axis as harmonic/mixIndex with sweep axes prepended.

## ParametricSweepEngine inner-analysis dispatch (Sweep Fix 2, 2026-06-15)

`ParametricSweepEngine.RunInner` now dispatches `SParameterAnalysis` and `DcAnalysis` in addition
to the original `HarmonicBalanceAnalysis` and `ParametricSweepAnalysis`, so any of these can be
wrapped in nested parametric sweeps (e.g. S-params vs a bias variable, a DC curve-tracer Vds×Vgs).

- **`SParameterAnalysis`:** calls `spa.Expand(netlist.ResolvedGlobals)` to get the flat frequency
  array, then delegates to `SParameterEngine.Run(netlist, freqs, settings)` and returns its DataSet.
  The S/Z0 cubes stack cleanly under a prepended sweep axis via `DataSet.StackSweepAxis`.
- **`DcAnalysis`:** calls `NonlinearDcEngine.Run(netlist, settings)` → `DcResult`; delegates all
  packing to the shared **`DcResultPacker.Pack(result, netlist)`** (same packer used by the standalone
  `SchematicRunService` path). The packer emits: `V[node]` cube (node-name labels), scalar
  `Converged`/`Residual` cubes, scalar **`I:<probe>`** cubes for each `IProbeModel` instance
  (sign: np→nm, matching `AddBranchCurrent`), and `__LabeledNodes` metadata when present.
  `IProbe` branch currents live in `DcResult.ProbeCurrents` (keyed by instance path, set by
  `ExtractProbeCurrents` from `x[probe.LastBranchIndex]` after each Newton solve).
  A FET I–V family-of-curves = two nested parametric sweeps (Vgs outer, Vds inner) wrapping DC +
  IProbe in the drain — `I:IPd` scalar cubes stack into a `[Vgs, Vds]` cube after `StackSweepAxis`.
  Gate tests: `tests/Engine.Tests/Parametric/ParametricSweepDcSParamTests.cs` (5 tests) +
  `tests/Engine.Tests/Nonlinear/IProbeCurrentTests.cs` (3 tests).
- **Loadpull and other engine-owning analyses** remain unsupported in the generic sweep;
  `default:` still throws `NotSupportedException` with a diagnostic message.

## HB swept-axis naming (Sweep Fix 1, 2026-06-15)

The HB result's swept axis is named after `HbAnalysisParams.SweepVarName` with **unit = ""** (empty
string). The legacy hardcoded `"Pin"/"dBm"` sentinel has been removed from both `BuildSingleToneDataSet`
and `BuildTwoToneDataSet`. If `SweepVarName` is null (no-sweep path, which never creates a sweep
axis anyway), the fallback name `"sweep"` is unreachable in practice.

HB-internal sweep-axis ownership is slated for removal in the parametric-sweep consolidation
(Briefs 3–4); this fix is the interim de-sentinel so existing HB-internal sweeps stop lying about
their axis name.

## S-parameter port formulation — wave path (2026-06-15)

`SParameterEngine` uses a **Z0-terminated power-wave (Norton / Kurokawa) formulation** when all
port Z0 references have `Re(Z0) > 1e-12` (the common case).

- **Per port:** stamp conductance `1/Z0` between its nodes via `AddAdmittance` (no branch unknown).
- **Excitation:** for driven port `j` with unit incident wave `a_j = 1`, inject Norton current
  `I_j = 2·√(Re Z0_j) / Z0_j` at the port's nodes.
- **S extraction (Kurokawa):** after solving for port voltages `V_k`:
  `I_k = (k==j ? I_j : 0) − V_k / Z0_k`, then `b_k = (V_k − conj(Z0_k)·I_k) / (2·√(Re Z0_k))`,
  `S[k, j] = b_k`. No Y→S inversion step.
- **Singularity class eliminated:** parallel ports / port-across-short topologies are non-singular
  by construction — each port contributes a real positive conductance to its node, so the matrix is
  well-conditioned even when ports share the same node pair.
- **Regularization** is now a genuine last resort (floating internal nodes, exact admittance
  cancellation). The `sparam-regularization` warning no longer fires for trivial circuits.
- **Legacy path** (any port has `Re(Z0) ≤ 0`, e.g. reactive reference impedance): unchanged
  ideal-0 V-source branch stamping + unit-voltage solve + `RFNetwork.YToS`. HB/DC are unaffected
  (they already treat Port/Term as inert). `PortEntry` carries both `BranchIndex` (legacy) and
  `Node0/Node1` (wave).

`MnaSystem.Factorize` wraps `AMD.Generate` to convert `ArgumentNullException` (empty matrix from
exact conductance cancellation) to `SingularMatrixException`, so the IfNecessary retry path fires.

## What lives here
- The **`MnaSystem`** and the stamping API (`AddAdmittance`, `AddBlockAdmittance`, `AddBranch`,
  `AddBranchCurrent`, `AddConstraint`, `AddBranchConstraint`, `AddCurrentInjection`,
  `AddSourceValue`).
- The **linear engine**: DC analysis, S-parameter analysis, and the linear characterization the
  HB engine consumes.
- The **harmonic-balance engine** (`HarmonicBalance/`, see its own CLAUDE.md).
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
the engine does not invent its own result type.

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
  across ALL sweep points (no-sweep runs use `Enumerable.Repeat(0.0, 1)` so `totalPoints=1`),
  then emit **one** summary via `AddWarning(...)` after the loop if `ncCount > 0`.

`SchematicRunService` drains `nl.Warnings` after the run (even on `EngineError`) into
`RunResult.Warnings`; `WorkspaceViewModel.RunAnalysis` posts them to the Messages pane at
Warning level. Gated by `EngineDiagnosticsChannelTests` (T1: floating node; T2: HB MaxIter=1)
and by `SchematicRunServiceTests` (L1e/L1f: warnings non-empty / empty).

## Phase 2 Step 1 deliverable — COMPLETE (2026-05-31)

### `MnaSystem` — v1 backing store
`MnaSystem` (in `src/Engine/MnaSystem.cs`) implements `IMnaContext` (defined in `src/Core/`).
Backing store is `Dictionary<(int Row, int Col), Complex>` — simple for Step 1 stamp inspection.
**Step 2 replaces this with CSparse.NET triplets** and adds the LU solve, AMD ordering, and the
symbolic-once/numeric-per-frequency pattern.

### Matrix index convention
- Node k (k ≥ 1) → internal index k − 1 (method `Col(node) = node - 1`).
- Ground (node 0) → index −1, all entries silently dropped.
- Branch b (from `AddBranch()`) → internal index returned directly (= `_nodeCount + sequential counter`).
- Matrix row/col layout: `[0 .. nodeCount−1]` = voltage unknowns; `[nodeCount ..]` = branch unknowns.

## Phase 2 Step 2 deliverable — GATE PASSED (2026-05-31)

Hero 1: 4-port RLC + embedded 2-port SnP. max|S_sim − S_ref| < 1e-6 across all 16 S-params,
1–3 GHz, from the CLI. 117/117 tests pass.

### Implementation notes (reality vs. design)
- **Sign in Y-matrix extraction:** branch current flows FROM signal TO ref (AddBranchCurrent
  convention), so the port current (INTO the + terminal) = **−branch_current**. Y_kj = -x[br_k].
- **Fixture bug found and fixed:** hero1.cnl had `C3 = 0.5 pF`; the external reference used 1.5 pF.
  Also changed `InterpMode` to `"linear"` to match the external reference generation.
- **AMD perm caching:** computed on first `Factorize()` call (first frequency), reused for all
  subsequent frequencies. Both the Dictionary clearing and branch-count reset in `Reset()` are
  required to make the symbolic-once / numeric-per-frequency pattern work.
- **Gmin loop:** `for (int n = 1; n <= nonGroundNodes; n++) AddAdmittance(n, 0, gmin)` —
  uses the circuit node indices (1-based), NOT the internal 0-based matrix indices.
- **Port collection:** a preliminary stamp pass (omega=1.0) captures `PortModel.LastBranchIndex`
  before the analysis loop. Indices are deterministic (same component order each pass), so the
  captured values remain valid throughout the sweep.
- **S-matrix Z0 metadata:** the SNP returned by `SParameterEngine.Run` stores `refZ0 = z0PerPort[0]`
  as the SNP's Z0 field (for Touchstone write metadata). The actual per-port renormalization was
  already applied via `YToS(yMat, z0PerPort)`.

## Phase 2 Step 3 — Hero 1B Singular-Matrix Diagnosis (2026-06-01)

### Diagnostics added (permanent product features)
- **`MnaSystem.FindZeroRows(nodeNamer, branchNamer)`** — pre-solve structural check: finds all-zero
  rows in the assembled MNA; names voltage nodes (with touching component list) and branch rows.
- **`MnaSystem.FindZeroCols(nodeNamer, branchNamer)`** — finds all-zero columns (a degree-of-freedom
  singularity dual to zero rows).
- **`MnaSystem.Factorize(tol, nodeNamer, branchNamer)`** — runs the structural check before
  factorization; on failure (zero row/col or CSparse "no pivot") throws `SingularMatrixException`
  with a diagnostic message naming the problematic row/branch and its touching components.
- **`SingularMatrixException`** — new exception type in `src/Engine/`.
- **`SParameterEngine`** — builds node/branch namers from the elaborated netlist (node names +
  touching-component list; branch→component map from the preliminary stamp pass); passes them to
  `Factorize()`. Preliminary pass updated to two-phase ordering (non-mutual first, then mutual).
- **`MutualInductanceModel.Stamp`** — over-coupling check: rejects k ≥ 1 (M² ≥ L1·L2) with a
  clear error naming the Mutual instance and its computed k. `_l1`/`_l2` stored in `Resolve()`.

### Step 3 audit results
- **gmin in AC path**: confirmed present in S-parameter path (not DC-only). Not a bug.
- **Short stamp**: audited and correct. Unit test `Short_AsInternalWire_SameAsDirectConnection`
  confirms identical S-params with and without an internal Short wire.
- **Mutual stamp**: stamp sign convention correct per linear-engine §7. Pairwise k-check added.
  Unit tests: `Mutual_ValidCoupling_SolvesAndIsReciprocal` and `Mutual_OverCoupling_ThrowsWithDiagnosticMessage`.

### Hero 1B diagnosis (Step 5) — root cause identified
The singularity was frequency-dependent (solved at 1 Hz, failed at 1 GHz), pointing at the jωM
terms. Root cause: `InductorModel.Stamp` silently dropped the `R=` parameter on `L:` lines — the
first circuit to have lossy inductors. With R = 0.0026 Ω per inductor omitted, the coupled
inductance block had a zero eigenvalue at AC.

## Phase 2 Step 3 — Hero 1B Gate: PASSED (2026-06-01)

### Fix 1: Inductor series-R stamping (correctness bug)
**`InductorModel.Stamp`** now reads the optional `R=` parameter (default 0) and stamps it together
with jωL on the same branch diagonal: constraint becomes `Va − Vb − (R + jωL)·i = 0`.
- At DC with R=0: exact short (unchanged behaviour).
- At DC with R>0: `Va − Vb − R·i = 0` — acts as resistor, not a short.
- The R term is independent of ω, so it is always added if non-zero.
- Unit test: `InductorWithSeriesR_ImpedanceMatchesAnalytic` verifies Z(ω) = R + jωL end-to-end.

### Fix 2: Mixed-sign mutual inductance support (already correct, verified)
Negative M values are physically valid (anti-phase coupling — geometry dependent) and must NOT be
rejected, warned on, or negated. The stamp correctly applies M with its sign intact (−jωM term).
- Over-coupling check (`k ≥ 1`) uses `m*m >= _l1*_l2` — tests the magnitude, not the sign.
- Unit test: `ThreeInductors_MixedSignMutual_SolvesCorrectly` confirms a physically-realizable
  mixed-sign inductance matrix solves, is reciprocal, and is passive.

### Feature: Two tri-state regularization settings (`AnalysisSettings`)
**`AnalysisSettings`** (new, `src/Engine/AnalysisSettings.cs`) exposes two independent `RegularizationMode` settings:
- **`ConductanceRegularization`**: controls gmin (1e-12 S, node→ground). Default: `IfNecessary`.
- **`InductanceRegularization`**: controls a small series-R (1 nΩ) added to each inductor branch
  diagonal. Cures a rank-deficient coupled-inductance block. Default: `IfNecessary`.

`RegularizationMode` tri-state:
- **`IfNecessary`**: first attempt without regularization; if `SingularMatrixException`, retry with
  all non-`Never` regs applied (both, for simplicity) and warn on stderr. Clean circuits pay zero.
- **`Always`**: apply before the first factorization (skip speculative failed solve).
- **`Never`**: no regularization; `SingularMatrixException` propagates immediately (debug mode).

`SParameterEngine.Run` signature changed: `gmin = DefaultGmin` parameter replaced by
`AnalysisSettings? settings = null`; uses `AnalysisSettings.Default` when null.

Hero 1 (lossless inductors, no R=): first solve succeeds (no regularization needed) — result is
identical to before (1e-6 gate still passes).

Hero 1B (lossy inductors, R=0.0026 Ω): first solve succeeds after the inductor-R fix — the series
resistance regularises the inductance block naturally. Regularization retry never fires.

Tests added: `InductorWithSeriesR_ImpedanceMatchesAnalytic`, `ThreeInductors_MixedSignMutual_SolvesCorrectly`,
`AnalysisSettings_IfNecessary_RescuesSingularOnRetry`, and `SParameterEngine_IsolatedShort_BothNodesGround_ThrowsSingular`
(updated to use `RegularizationMode.Never` so the diagnostic propagates as designed).

**Total tests: 130 pass, 0 fail.**

## Phase 2 Component Robustness (2026-06-01)

### Design philosophy: warn-and-continue
circuitRF is a research tool. Non-physical-but-mathematically-handleable inputs emit a warning to
`Console.Error` and proceed; they do NOT hard-error. Warnings fire once per component instance
(not once per frequency point) using an instance-level `_warned` flag. Hard errors are reserved
for genuinely unresolvable conditions (missing required parameters, elaboration failures).

### Change 1: ResistorModel — negative R and R=0
- **R < 0**: stamps `G = 1/R` with its sign (negative conductance — models active/negative-resistance
  elements). Emits one warning per instance: `"R:{path}: R={r} Ω < 0 — non-physical/active"`.
- **R = 0**: stamps `Gmax = 1e12 S` (near-short). Emits one warning per instance naming Gmax.
  `Gmax` is a `const double DefaultGmax` on `ResistorModel` matching `AnalysisSettings.Default.Gmax`.
- `AnalysisSettings.Gmax` (default 1e12 S) exposes the conductance ceiling. Currently used by
  `ResistorModel.DefaultGmax`; future: wire through `IMnaContext` for per-run customization.

### Change 2: InductorModel — optional series R and C (series-RLC branch)
An `L:` line may carry `R=` (series resistance) and/or `C=` (series capacitance), both optional.
The inductor's single Group-2 branch is a series-RLC element:
- Constraint: `Va − Vb − (R + jωL + 1/(jωC))·i = 0`
- `R=` absent → no resistance term (lossless). `C=` absent → no capacitive term.
- **DC with C present**: series capacitor is an open at DC (1/(jωC) → ∞ as ω→0). Stamped as
  force-i=0: constraint row has only `−i = 0` (diagonal = -1, no voltage coefficients). KCL column
  still stamped so the branch column is non-zero. Equivalent to the standalone capacitor's DC-open.
- **DC without C**: `diag = −R` (resistor if R>0, exact short if R=0 — unchanged from prior).
- Tests: `InductorWithSeriesR_ImpedanceMatchesAnalytic` (RL), `InductorRLC_AcImpedanceMatchesAnalytic`
  (RLC AC), `InductorWithC_DcOpen_BranchCurrentIsZero` (DC-open via MnaSystem inspection).

### Change 3: MutualInductanceModel — k≥1 downgraded from error to warning
- `k ≥ 1` (M² ≥ L1·L2) is non-physical but allowed at the user's peril. Warning: once per instance
  via `_warnedOverCoupling` flag. Stamping proceeds; if the inductance matrix becomes singular,
  `InductanceRegularization` (IfNecessary default) rescues the solve.
- **Negative M (mixed-sign couplings) is fully physical — no warning, no special handling.**
- Test: `Mutual_OverCoupling_WarnsAndProducesResult` verifies warning fires + result returned.

**Total tests: 134 pass, 0 fail.**

## Phase 3 deliverable — COMPLETE (2026-06-01)
Nonlinear-DC Newton solver, validated by the hero GaN HEMT operating point.

### NonlinearDcEngine (`src/Engine/NonlinearDcEngine.cs`)
Unified real sparse Newton solver (nonlinear-dc §4):

**State vector** x = [V₁…Vₙ | I_branches]: voltage unknowns + all MNA branch-current unknowns
(voltage sources, inductors — from MnaSystem at ω=0). The full augmented system is built once from
`MnaSystem` at ω=0, extracting the real parts of all entries and the source RHS.

**Residual** F(x) = G_aug·x + I_nl(x) − b_source·sourceFrac  
**Jacobian** J = G_aug + dg(x): linear matrix (constant per source-stepping fraction) + dg from
`Evaluate`, stamped at each nonlinear device's port nodes using the 4-way port-pair formula.

**Port voltage convention** (from elaborated node layout):  
SDD nodes are in 2N pairs: `[n1+, n1−, n2+, n2−, …]`. Port voltage p = V(nodes[2p]) − V(nodes[2p+1]).
dg[p,q] stamps into the (np, nq) block with ±dgPQ signs from the 4 node-pair combinations.

**gmin continuity**: shunt DefaultGmin (1e-12 S) added to every voltage row diagonal (nodes only, not
branch rows). Controlled by `AnalysisSettings.ConductanceRegularization`.

**Source-stepping** (§4.3): sources walked from 0 to 1 in DefaultMaxSteps (20) equal steps; step-halving
backoff on Newton max-iter failure (up to 10 halvings). AMD permutation cached after first iteration.

**Convergence**: ‖F‖₂ < 1e-6 (DefaultAbsTol) or ‖Δx‖₂ < 1e-9 (DefaultVTol).

### Hero gate (2026-06-01)
`tests/Engine.Tests/Nonlinear/NonlinearDcTests.cs`: Hero GaN HEMT + 20 Ω series Rd, gate −3.05 V, drain 48 V.
Converges in 68 iterations to **vds = 47.0176 V, i2 = 49.122 mA** (golden: 47.018 V, 49.12 mA).
Residual = 6.2e-11 (well below 1e-6 tolerance). All Phase 1–2 tests still pass.

## Phase 3 Follow-up — DcBiasStepping, SDD whitespace, convergence settings (2026-06-02)

### DcBiasStepping tri-state (`AnalysisSettings.DcBiasStepping`)
New `DcBiasSteppingMode` enum (same tri-state pattern as `RegularizationMode`):

- **`IfNecessary`** (default): direct cold-start Newton at frac=1.0; fall back to ramp only if it
  fails. Hero converges in **4 iterations, 1 step** — no ramp needed.
- **`Always`**: always ramp DC supplies 0→1 in `DcBiasRampSteps` (default 20) equal steps.
  Reproduces the Phase-3 behavior (68 iters across 20 steps).
- **`Never`**: direct solve only; throws `NonlinearDcNotConvergedException` on failure. For
  validation/debugging.

`DcBiasStepping` ramps DC *bias supplies* — distinct from Phase-4's reserved `DriveStepping`
(which will ramp RF *drive power*). Do not conflate the two.

`DcBiasRampSteps` (default 20): ramp step count, only relevant when `Always` or fallback fires.

`NonlinearDcNotConvergedException` — new exception, thrown only by `Never` mode.

### Convergence trace (permanent feature)
`DcResult.Trace` holds a `ConvergenceTrace` with `StepRecord` (per continuation step: source
fraction, iteration count, convergence, per-iteration `IterationRecord`) and `DampingPolicy`.
The hero final step converges super-quadratically: ‖F‖ goes 2.4 → 9.9e-4 → 6.2e-11 in 3 iters.

### Solver architecture (post-refactor)
`NonlinearDcEngine.Solve()` dispatches to:
- `SolveDirect(throwOnFailure)` — single Newton attempt at full bias (frac=1.0)
- `SolveRamped()` — source-stepping loop (the former `Solve()` body)
- `SolveIfNecessary()` — calls `SolveDirect(false)`, then `SolveRamped()` if needed

**Total tests: 199 pass, 0 fail.**

## Phase 4b-1 deliverable — COMPLETE (2026-06-03)
Core loadpull engine and the `Tuner` component, validated on Hero 3.

### `TunerModel` (`src/Core/Devices/TunerModel.cs`)
New `ComponentModel` (Kind=Linear) wrapping Z_Port + bias-tee (L, C, V_supply) + optional
V_1Tone drive (SourceTuner role). Four-node layout: two declared nets + two internal nodes
`__tuner_<inst>_block` / `__tuner_<inst>_bias` minted by the Elaborator at elaboration time.
- Role (Load / Source) assigned by `LoadpullEngine` before HB runs.
- `ChokeBranchIndex` and `BiasSupplyBranchIndex` set each Stamp() pass.
- `SetHarmonicOverride(k, Z)` overrides one harmonic for the loadpull grid sweep.
- `SetSourceDrive(f0, Pavl)` updates the SourceTuner's V_1Tone amplitude each Pin step.
- In S-param mode (no tone set), presents Z[1] flat over all frequencies.

### `HbEngine.RunSinglePoint`
New method on `HbEngine` that runs the Newton solve at a single operating point (no sweep
loop). Accepts an optional warm-start `Complex[,]` seed. Used by `LoadpullEngine` for each
grid×Pin step. Settings override (InductanceRegularization=Always) passed per call.

### `HbLinearExtractor` changes
- `IsVoltageOrToneSource` now includes `TunerModel` — the TunerModel is stamped via
  `ZeroDriveMna` in the zeroDrive=true (Y_NN extraction) path, zeroing its V_1Tone and
  bias supply values while keeping the impedance topology active.
- `ApplyInductanceReg` now also regularizes `TunerModel.ChokeBranchIndex` (the internal
  choke, not an InductorModel, needs explicit regularization when mode=Always).

### `GamReader` (`src/Engine/Loadpull/GamReader.cs`)
Parses `.gam` grid files: mag_ang / re_im / re+j*imag formats; gamma or impedance form;
optional header; comment/blank skipping; Γ↔Z roundtrip via analytic formula.

### `LoadpullEngine` (`src/Engine/Loadpull/LoadpullEngine.cs`)
2-D sweep: outer Γ/Z grid × inner adaptive Pin drive-up. InductanceRegularization=Always
forced for all inner HB solves. VSWR-nearest Γ-grid warm-start. Compression stop at
P-xdB + one overshoot step. Stop reasons: Compression / PinMax / NonConvergence.

### Hero 3 gate — PASSED (2026-06-03)
20-point Γ grid, all converged. Gt 4.5..16.6 dB (varies with load — correct PA behavior).
Pout 14.5..26.6 dBm. Stop = PinMax for all (FET does not reach 3 dB compression at
PinMax=10 dBm from 25Ω source, which is physically correct for this bias point). Golden
frozen in `testdata/Hero3/`. SELF-GENERATED — NOT INDEPENDENTLY VALIDATED.

### `HarmonicBalanceAnalysis` / `LoadpullAnalysis` changes
- Both now carry `MaxIterExpr` (default "100") — user-settable max Newton iterations.
- `AnalysisSettings.HbMaxIter` default changed from 50 to 100.

**Total tests: 225 pass, 0 fail.**

## Phase 4b-2 deliverable — COMPLETE (2026-06-03)
Loadpull pursuit (MXP/MXE search + auto-Zsource), validated on Hero 3B.
Details in `src/Engine/Loadpull/CLAUDE.md`.

New files: `PursuitEngine`, `LoadpullPursuitEngine`, `GamWriter` (all in `Loadpull/`).
New analysis type: `LoadpullPursuitAnalysis` (Core/Design) + CnlReader dispatch.
Modified: `LoadpullEngine` (extracted `PrepareContext`/`RunOneTermination`);
           `LoadpullResult.PinStepResult` (added `PdcW`, `De`, `Pae`).

**Total tests: 245 pass, 0 fail.**

## Phase 4b-2 enhancement — IteratedQuadratic (2026-06-05)
Second, more robust search method added to `PursuitEngine` alongside the existing `SteepestAscent`.

- **`SearchMethod` enum** (`PursuitEngine.cs`): `{ SteepestAscent, IteratedQuadratic }` — extensible.
- **`PursuitEngine.Method`** init property (default `SteepestAscent`): dispatches `Run` to either
  `RunSteepestAscent` (unchanged existing path) or `RunIteratedQuadratic` (new).
- **`SearchMethod` directive key** in `loadpull_pursuit` (default `SteepestAscent`): parsed from
  `LoadpullPursuitAnalysis.SearchMethodExpr` by `CnlReader`, threaded into `PursuitParams`.
- **`FitAxis1D`** new private static helper in `PursuitEngine`: decoupled 1-D quadratic fit per axis,
  avoiding the singular AtA matrix that arises when axis-aligned cardinals feed the full 5-parameter
  `FitQuadraticSurface` (the ΔxΔy column is identically zero → `Solve5x5` returns all-zeros).
- **Debug cleanup**: removed leftover `Console.WriteLine` calls in `ExtractCriterion`.
- **Hero 3B IQ results**: MXP=77.6 Ω (brute-force VSWR=1.031 < 1.20), query ratio IQ/SA=1.86× ≤ 2×.

**Total tests: 257 pass, 0 fail (158 Core + 99 Engine).**