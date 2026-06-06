# Phase 5-7 — Implementation Brief: Export (`.mat` / `.npy`) with Linear-Network Options (Claude Code / Sonnet)

**Goal:** export a run's `DataSet` to **`.mat`** and **`.npy`**, with options controlling whether the
**linear-network admittance information** (for consumer-side lazy reconstruction) is included, and whether
**linear-interior voltages/currents are evaluated** into the output (all, or only a caller-specified list),
with a **disk-space estimate + >100 MB warning** before any evaluation. **Write the design note
(`docs/design/data-export.md`) FIRST and flag it for owner review before implementing.**

> Read first: `src/Core/Data/CLAUDE.md` (the DataSet/DataCube contract), `docs/PRD.md` §11 (the `.npy` =
> one packed structured array decision), `src/Engine/HarmonicBalance/HbLinearBackSolver.cs` +
> `HbLinearExtractor.cs` (what the back-solve retains and how `SolveFullNetwork` works — this is exactly
> what a consumer reconstructs), `RfCore/src/Data/DataSet.cs`. The exporters live in **RfCore** (shared with
> splotRF, the primary consumer). Design note + owner review gate the implementation.

## STEP A — Write `docs/design/data-export.md` FIRST (flag for review, do not implement yet)
Specify, for owner approval:
- **`.mat` mapping:** each named `DataCube` → a named MATLAB variable/struct field; the whole `DataSet` →
  one `.mat` file (a struct of cubes). Axis labels/units → companion metadata fields. Complex cubes →
  MATLAB complex; Real → double. State the MATLAB version/format target (v5 vs v7.3/HDF5 — v7.3 if cubes can
  exceed the ~2 GB v5 limit or if HDF5 is preferred; pick and justify).
- **`.npy` mapping:** the **whole DataSet as ONE packed structured/record array** with axis metadata (PRD
  §11 — NOT one file per cube). Specify the structured dtype (field per cube + axis descriptors), how
  Complex vs Real fields are typed, and how axis names/units ride along (a header/JSON sidecar field or a
  metadata record). If a single packed structured array can't hold ragged cube shapes, specify the chosen
  resolution (e.g. an `.npz` of named arrays + a metadata array) and justify against PRD §11.
- **Linear-network inclusion option** (the new bit — see STEP C for the data): the serialized form of the
  per-harmonic linear system a consumer needs to reconstruct any linear node V / branch I — i.e. enough to
  replicate `HbLinearExtractor.SolveFullNetwork` (`G·x = bSrc − iNl_at_interface`). Specify exactly what is
  written: the per-harmonic admittance/MNA matrix `G(ω_k)` (sparse: triplets or CSC), the per-(harmonic,
  sweep) source RHS `bSrc`, the per-(harmonic, sweep) interface `iNl`, the interface-node index list, and
  the node-index↔name and branch-index↔name maps. Define the on-disk layout in both `.mat` and `.npy`.
- **The evaluation-mode enum and the disk estimate** (STEP B/D): name them and define the estimate formula
  and the warning behavior.
- Flag the note for owner review. **Implement only after approval.**

## STEP B — The export API surface (after the note is approved)
A single exporter entry (in RfCore) taking a `DataSet`, an output path, format (`Mat`/`Npy`), and an options
object:
- **`IncludeLinearNetwork` (bool, default false):** when true, serialize the per-harmonic linear-network
  admittance info (STEP C) so a consumer can lazily reconstruct all linear V/I. When false, omit it (the
  common, compact case).
- **`LinearEvalMode` (enum):** `EvaluateAll` | `EvaluateSpecified`. Controls whether linear-interior
  voltages/currents are **evaluated and written into the output cubes**:
  - `EvaluateAll` — evaluate every linear-interior node voltage and every linear branch current (call the
    back-solver across all nodes/branches × all harmonics × all sweep points) and add them to the exported
    DataSet.
  - `EvaluateSpecified` — the caller **must supply a list** of linear node names and/or branch refs
    (`instance:terminal` or `IProbe` names) to evaluate; only those are computed and written. (Empty list
    with this mode → evaluate none of the linear interior; export only what's already in the DataSet.)
  - These two are **orthogonal to `IncludeLinearNetwork`**: a caller may include the admittance info for
    lazy consumer-side reconstruction *and/or* eagerly evaluate some/all interior points. (Include + eval
    none = "consumer reconstructs lazily"; eval all + no include = "everything precomputed, no reconstruction
    data needed"; both = belt and suspenders.)

## STEP C — Linear-network serialization (the `IncludeLinearNetwork` payload)
Serialize exactly what `HbLinearExtractor.SolveFullNetwork` consumes, so a consumer reproduces it:
- **Per harmonic k:** the linear MNA matrix `G(ω_k)` (ω_k = k·2π·f0; DC k=0 uses the ω=0 build). Export
  **sparse** (CSC or triplets) — these can be large; do not densify. `G` is topology-based and identical
  across sweep points (the code caches it per ω), so write it **once per harmonic**, not per sweep point.
- **Per (harmonic k, sweep si):** the source RHS `bSrc[si][k]` and the interface NL currents `iNl[si][k]`
  (the back-solver already stores both). The consumer forms `b = bSrc; b[interfaceNode_j] -= iNl_j` then
  solves `G·x = b` — `x[0..NonGround-1]` = node voltages, `x[NonGround..]` = branch currents.
- **Maps:** interface-node index list, node-index↔name, branch-index↔name (the extractor has `_nodeNamer`/
  `_branchNamer`; expose the maps). Without these the consumer can't address results by name.
- Source the data from the run's retained `HbLinearBackSolver` (it already holds `_iNl`, `_bSrc`, the
  extractor with its per-ω system). Add a read-only accessor on the back-solver/extractor to hand out
  `G(ω_k)` in sparse form and the maps — do not recompute.

## STEP D — Disk-space estimate + >100 MB warning (before any linear evaluation)
**Before** evaluating any linear V/I (STEP B `EvaluateAll` or a large `EvaluateSpecified` list) or
serializing the linear network (STEP C), **estimate the output size** and **warn if > 100 MB**:
- Estimate from the cube shapes and dtypes: existing DataSet cubes + (if `IncludeLinearNetwork`) the sparse
  `G` per harmonic (nnz × 16 bytes complex + index overhead) + bSrc/iNl per (harmonic, sweep) + (if
  evaluating) interior nodes×branches × harmonics × sweep points × 16 bytes.
- If the estimate exceeds **100 MB**, emit a clear warning (to stderr / the run log) naming the estimated
  size and the dominant contributor (e.g. "EvaluateAll over N interior nodes × K harmonics × S sweep points
  ≈ X MB"), so the user can switch to `EvaluateSpecified` or drop `IncludeLinearNetwork`. The warning does
  not abort (the user may want the big file) — it informs. (If the owner wants a hard cap or a
  confirm-to-proceed, that's a design-note decision; default is warn-and-proceed.)

## STEP E — CLI + tests
- CLI command to export a run's DataSet to a given path/format with the options.
- **Round-trip / conformance tests:** export a hero DataSet → `.mat` → load in Octave/SciPy (or a
  format-conformance check if no interpreter in CI) and confirm cube values + axis metadata; `.npy`
  structured-array conformance (loads in NumPy, fields and dtypes correct).
- **Linear-network round-trip:** with `IncludeLinearNetwork=true`, a test reconstructs a linear-interior
  node voltage and a branch current from the exported `G`/`bSrc`/`iNl`/maps and confirms it **matches the
  back-solver's own `GetSolution`** at a hero point (the same KCL-consistent value). This is the oracle that
  the serialized linear system is complete and correct.
- **EvalMode tests:** `EvaluateAll` populates interior cubes; `EvaluateSpecified` with a node + a branch
  populates only those; the disk estimate fires the >100 MB warning on a deliberately large case.

## Acceptance
1. `data-export.md` written, owner-approved, before implementation.
2. `.mat` (struct of cubes, whole DataSet) and `.npy` (one packed structured array per PRD §11) exporters in
   RfCore; CLI command; conformance tests pass.
3. `IncludeLinearNetwork` (bool) serializes the per-harmonic sparse `G` + per-(k,si) `bSrc`/`iNl` + maps,
   sourced from the retained back-solver (not recomputed); the linear-network round-trip test reconstructs a
   node V and branch I matching `GetSolution`.
4. `LinearEvalMode` enum: `EvaluateAll` evaluates+writes all interior V/I; `EvaluateSpecified` requires and
   honors a caller list (only those written); orthogonal to `IncludeLinearNetwork`.
5. Disk-space estimate computed before any evaluation/serialization; >100 MB emits a clear warning naming
   size + dominant contributor; warn-and-proceed (no abort) unless the note specifies otherwise.
6. `dotnet build`/`dotnet test` green; Phases 1–4 and 5-1…5-6 intact; values unchanged.

## Guardrails
- **Design note first, owner review before implementing.** This is the one Phase-5 step with a genuine new
  design surface (the `.npy` packing and the linear-network serialization format) — settle it on paper.
- Source the linear-network payload from the **retained back-solver/extractor** (`_iNl`, `_bSrc`, the per-ω
  cached system) via a read-only accessor — do NOT recompute `G` or re-run the sweep.
- Sparse `G` stays sparse on disk (CSC/triplets); never densify (memory).
- The estimate runs **before** evaluation so the warning is actionable (don't compute 2 GB then warn).
- `EvaluateSpecified` with no list = evaluate no interior (export what's in the DataSet) — not an error.
- `.npy` = one packed structured array (PRD §11); if ragged shapes force `.npz`, justify in the note.
- Reuse the measurement branch/node name resolution (5-4) for the `EvaluateSpecified` list and the export
  maps — one name-resolution path, not a second.
- Update `src/Core/Data/CLAUDE.md` (export section) and write `data-export.md`.

*Phase 5-7 closes Phase 5: one result model, measurements, generic nested sweep, and export — the engine
half complete, and the splotRF (Phase 7) consumption seam fully specified.*
