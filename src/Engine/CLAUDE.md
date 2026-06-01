# Engine — local conventions

Standing instructions for `src/Engine` (the numeric layer: MNA assembly, linear analyses, and the
HB sub-engine in `HarmonicBalance/`). Read with the root `CLAUDE.md`. Design notes:
`docs/design/linear-engine.md` and `docs/design/harmonic-balance.md`.

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

### What Step 3 needs (VendorA importer + Hero 1B)
- Replace Dictionary backing with CSparse triplet/compressed-column storage.
- `Solve()` method: AMD ordering (symbolic once per topology), numeric LU factorization per
  frequency, multi-RHS back-substitution for port extraction.
- DC analysis, S-parameter analysis, Port/Term extraction, per §§5, 9, 6 of linear-engine.md.
- Stamps for R, L, C (updating existing stubs), voltage/current sources, Port/Term, TLIN.
- CLI driver.