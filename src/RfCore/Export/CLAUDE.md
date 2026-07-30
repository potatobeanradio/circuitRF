# Export / Import — local conventions (RfCore)

Standing instructions for `src/RfCore/Export`. Read with the root `CLAUDE.md` and the authoritative
result-model contract at `circuitRF/src/Core/Data/CLAUDE.md`.

This directory holds the `DataSet` **exporter** (`.mat` / `.npy`) and **importer** (`.npy` → `DataSet`).
`.npy` is **circuitRF's native data file format** — the file circuitRF writes and splotRF (and other
viewers) read. The importer is the inverse of the exporter; `export → import` round-trip is the symmetry
oracle, and `IncludeLinearNetwork` round-trip (reconstruct a node V / branch I from the serialized sparse
admittance data and match the back-solver's `GetSolution`) is the correctness oracle.

## File-format stability — NO backward compatibility during alpha
**The on-disk format is NOT stable until the product approaches final release. Do NOT support reading
older files, and do NOT add migration/compat shims.** This is a deliberate standing decision — do not
re-raise it.
- **Break the format freely** when a better layout/dtype/schema emerges. Update the **exporter and
  importer together** and regenerate test fixtures. Do not preserve the ability to read files written by
  an earlier alpha build.
- **No version negotiation, no upgraders, no "read v1 / write v2."** A `format_version` field may be
  *written*, and the importer may *reject* a mismatched version with a **clear error** — but never
  *read/migrate* an old version. Reject-with-clear-error is the only backward behavior.
- The relaxation is **on-disk files only.** The in-process `DataSet`/`DataCube` API and the requirement
  that the **three serialization sites move in lockstep** (circuitRF exporter, this importer, splotRF's
  reader) still hold — changing the format means upgrading all three in the same change. What we drop is
  the obligation to keep reading *yesterday's files*.
- **Revisit near final product** — a real versioning/compat policy replaces this then; flag the
  transition (root `CLAUDE.md`). Full rationale + the in-process-vs-on-disk distinction:
  `circuitRF/src/Core/Data/CLAUDE.md` → "File-format stability".

## Importer scope (current)
- **Level 1 only** for now: rehydrate the stored `DataSet` (cubes + axes + kinds + metadata) from a
  `.npy`. Enough for splotRF to plot everything that was written.
- **Level 2 (consumer-side lazy reconstruction of *un*exported linear-interior V/I from the serialized
  sparse admittance data) is documented but NOT implemented here yet** — see the consumer guide
  (`circuitRF/docs/design/`), to be built when splotRF needs interactive reconstruction (Phase 7). The
  exported `__linnet_*` payload already carries everything Level 2 needs; the 5-7 round-trip test proves
  it is complete.

## MatWriter and NpyWriter must track each other
Both writers serialise the same logical payload (cubes, axes, linnet) to different container formats.
**Any bug found in one writer must be investigated in the other.** The two known examples of this
class: the union-sparsity-pattern fix (G_data nnz = union across all harmonics, not just k=0) and
the `format_version` addition — both affected MatWriter and NpyWriter identically. When you fix or
change either file, open the other and apply the equivalent change before closing the task.

## Don't densify
Sparse `G` stays sparse on disk (triplet/CSC) and sparse on import. Never materialize the dense MNA
matrix — memory.
