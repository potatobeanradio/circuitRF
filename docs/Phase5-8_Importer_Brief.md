# Phase 5-8 — Brief: `.npy` Importer (Level 1) + Consumer Data-Format Guide (Claude Code / Sonnet)

**Goal:** make `.npy` circuitRF's native, round-trippable data format by adding (1) a **C# importer** in
RfCore that reconstructs a `DataSet` from a `.npy` file (**Level 1** — rehydrate the stored cubes), and (2)
a **consumer-facing guide** that documents the file schema and the full **Level 2** lazy-reconstruction
recipe (rebuilding any linear-interior node V / branch I from the serialized sparse admittance data), with
detailed worked examples — but **Level 2 is documented only, not implemented yet.**

> Read first: `RfCore/src/Export/CLAUDE.md` (local conventions + the alpha file-format policy),
> `circuitRF/src/Core/Data/CLAUDE.md` (the result-model contract + "File-format stability"),
> `circuitRF/docs/design/data-export.md` (the format the exporter writes — the importer is its inverse),
> and the exporter code in `RfCore/src/Export` (`NpyWriter.cs`, `DataSetExporter.cs`, `ExportOptions.cs`,
> `ILinearNetworkPayload.cs`). Design notes win.

## File-format policy (standing — do not re-litigate)
The on-disk format is **NOT stable during alpha**; we do **not** read older files. Build NO migration or
version-negotiation. A `format_version` may be written and the importer may **reject** a mismatch with a
clear error, but must never read/migrate an old version. (Full statement: `RfCore/src/Export/CLAUDE.md`.)
If improving the format is convenient while building the importer, **change exporter + importer together**
and regenerate fixtures — don't preserve old-file readability.

## Scope

### STEP 1 — `.npy` importer (Level 1) in RfCore
The inverse of `NpyWriter`. Read a circuitRF `.npy` and reconstruct the `DataSet`:
- Parse the `.npy` header (magic, version, the structured-array dtype, shape) and the buffers — a circuitRF
  file is a 1-element structured array (`shape (1,)`) whose fields are the named cubes + `__meta__` + the
  optional `__linnet_*` fields (data-export.md §3).
- Rebuild each cube: field name → cube name; the field's sub-array shape + dtype → `DataCube` shape +
  `DataKind` (`complex128`→`Complex`, `float64`→`Real`); the buffer → the cube's backing array (respect
  row-major/stride layout the writer used — round-trip is the check).
- Rebuild axes from the `__meta__` JSON blob (names, units, values, optional labels) and attach to each
  cube. The reconstructed `DataSet` must be **equal to the original** cube-for-cube, axis-for-axis.
- If `__linnet_*` fields are present, load them too (the sparse `G` triplets per harmonic, `bSrc`, `iNl`,
  interface-node list, name maps) and expose them on the imported result so a future Level-2 consumer can
  use them — but **do not implement the reconstruction solve** (that's Level 2). Just make the data
  available (e.g. an `ImportedLinearNetwork` holding the arrays + maps). Keep sparse data **sparse** — no
  densify.
- **`format_version`:** read it; if absent or mismatched against the current writer's version, **throw a
  clear error** naming the expected vs found version and stating that alpha files are not
  backward-compatible. (Add the `format_version` write to `NpyWriter` if not already present — a small
  field in `__meta__`.)

### STEP 2 — round-trip oracle (the importer's correctness gate)
- **`export → import` symmetry test:** take a hero `DataSet` (e.g. Hero 2 with measurements), export to
  `.npy`, import it back, assert the reconstructed `DataSet` equals the original — every cube's `DataKind`,
  shape, values (exact for the buffers; complex and real), and every axis's name/unit/values/labels. This
  is the natural oracle and it must be tight (bitwise/round-trip-exact for the numeric buffers; `.npy`
  stores the same IEEE doubles, so equality should be exact, not approximate).
- **With `IncludeLinearNetwork=true`:** round-trip the `__linnet_*` payload too — import the sparse `G`,
  `bSrc`, `iNl`, maps, and assert they match what was exported (the 5-7 exporter already has the
  reconstruction-correctness oracle; here the gate is just that import faithfully reloads the arrays).

### STEP 3 — consumer data-format guide (documents Level 1 AND Level 2)
Write a **consumer-facing** guide (suggest `circuitRF/docs/design/data-file-format.md` — a reader's guide,
distinct from `data-export.md` which is the writer's design note). Audience: an author of splotRF or any
third-party viewer, in **both C# and Python**. It must cover:
- **The logical schema:** what a circuitRF `.npy` contains — the structured array, the per-cube fields
  (name, dtype↔DataKind, shape↔axes), the `__meta__` JSON (axis names/units/values/labels), and the
  optional `__linnet_*` linear-network fields. State the alpha "format may change, no back-compat" caveat
  up front so consumers don't build on it as if frozen.
- **Level 1 — load and reconstruct a labeled DataSet:** worked examples loading the file and pulling a cube
  as a labeled trace, in Python (`np.load`, read `__meta__`, index a field) and C# (the new importer).
  Show extracting, e.g., `S(2,1)` over frequency and a node-voltage spectrum vs Pin.
- **Level 2 — lazy reconstruction of linear-interior V/I from the sparse admittance data (THE detailed
  part):** this is the payoff of `IncludeLinearNetwork`, and it's the section that must be rich with detail
  and examples, because no one will discover it from the field names alone. Document, end to end:
  - the math: `G(ω_k)·x = b`, where `b = bSrc[si,k] − iNl` injected at interface nodes (the exact
    `SolveFullNetwork` operation), and `x[0..nonGround-1]` = node voltages, `x[nonGround..]` = branch
    currents;
  - the index/representation conventions that bite if gotten wrong: **1-based circuit nodes vs 0-based
    matrix indices** (node n → matrix row n−1), the sparse-`G` triplet/CSC layout, that `G`'s pattern is
    **shared across harmonics** (rows/cols once, data per harmonic), the DC (k=0) case (ω=0, real matrix in
    a complex container — use as-is), and the node/branch **name → index** maps;
  - a complete **Python worked example** (assemble `scipy.sparse.csc_matrix` from the triplets, form `b`,
    `spsolve`, look up a node by name, read its voltage; and a branch current from the `x[nonGround+…]`
    region by branch name) — essentially data-export.md §4.8 expanded with real index lookups and a couple
    of fully worked node/branch retrievals;
  - a **C# sketch** of the same (the eventual splotRF path, using the imported sparse data + a CSparse
    solve), noting it is the Level-2 work to be implemented in Phase 7;
  - the worked numbers should match a hero point so a reader can verify their implementation against a
    known answer (reference the 5-7 round-trip test's hero/point).
- State clearly: **Level 1 is implemented now; Level 2 is documented here and implemented when splotRF needs
  interactive reconstruction (Phase 7).** The exported payload already contains everything Level 2 needs.

## Acceptance
1. `.npy` importer in RfCore reconstructs a `DataSet` (cubes, kinds, shapes, axes, metadata) — Level 1.
2. `export → import` round-trip equals the original exactly (buffers bitwise-equal; axes/labels/units/kinds
   identical), with and without `IncludeLinearNetwork`; `__linnet_*` arrays reload faithfully and stay
   sparse.
3. `format_version` written by the exporter and checked by the importer; mismatch/absent → clear error, no
   migration attempted.
4. Consumer guide written (`data-file-format.md`): schema, Level-1 examples (C# + Python), and a detailed
   Level-2 lazy-reconstruction recipe with fully worked Python example + C# sketch matching a hero point;
   states Level 2 is documented-not-yet-implemented and the alpha no-back-compat caveat.
5. `dotnet build`/`dotnet test` green; nothing in Phases 1–5(-7) regresses; values unchanged.

## Guardrails
- **Level 1 only** for the importer; do NOT implement the Level-2 solve — but DO load and expose the
  `__linnet_*` data so Level 2 can be built later without re-touching the importer.
- Importer is the **exact inverse** of `NpyWriter`; round-trip equality is the gate. If a layout detail is
  ambiguous, make the writer and reader agree and prove it with the round-trip, rather than guessing.
- Sparse stays sparse (no densify) on import.
- Alpha file-format policy: no back-compat, no migration; break-and-regenerate is fine; `format_version`
  reject-with-error only. (Standing — `RfCore/src/Export/CLAUDE.md`.)
- The consumer guide is **reader-facing** (splotRF / third-party authors), distinct from `data-export.md`
  (writer-facing design). Both C# and Python examples; the Level-2 section is the detailed one.
- Diagnostics over grinding; flag any format ambiguity rather than guessing silently.

*Phase 5-8 makes `.npy` a true round-trippable native format (write + read in RfCore) and documents the full
consumer contract incl. Level-2 reconstruction — the complete splotRF (Phase 7) input spec. Closes Phase 5.*
