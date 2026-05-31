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