# Brief P5 — translation-class memoisation of cell-pair integrals

**Problem.** The mesh is a tensor-product grid (`PlanarMesh` D8) and every kernel is a function of
separation alone, so a cell pair's cores and its remainder depend only on
`(w_a, w_b, Δx, h_a, h_b, Δy)`. Two pairs with the same six numbers get the same integrals, and the
fill computes them again. Counted on the shipping mesher, 2026-08-28 (canonical orientation,
quantisation 1e-6 · smallest edge):

| structure | cells | N | cell pairs | distinct classes | reuse |
|---|---|---|---|---|---|
| FR-4 hero 2.9 × 20 mm, 10 GHz | 297 | 552 | 44,253 | 18,036 | 2.5× |
| FR-4 line 80 mm, 10 GHz | 1,053 | 1,980 | 554,931 | 70,494 | 7.9× |
| FR-4 line 256 mm, 6 GHz | 1,980 | 3,731 | 1,961,190 | 118,188 | 16.6× |
| FR-4 taper 2.9 → 0.5 mm, 20 mm | 728 | 1,371 | 265,356 | 10,430 | 25× |
| FR-4 taper 60 mm | 1,000 | 1,891 | 500,500 | 16,888 | 30× |
| GaAs line 72 µm × 2 mm, 20 GHz | 414 | 773 | 85,905 | 40,656 | 2.1× |
| two coupled 40 mm lines | 1,098 | 2,056 | 603,351 | 106,608 | 5.7× |

The reuse grows with exactly the geometries that get big. It applies to the core build, the
per-frequency remainder, and AIM's near field alike. **Do P4 first**: the primitives P4 defines are
what gets memoised, and memoising the current four-times-redundant structure would bake the
redundancy in.

Read first: `PlanarFill.cs` after P4; `PlanarMesh.cs` (`GridX`/`GridY`, `IX`/`IY`); the D8 header.

## Milestones

1. **Reproduce the table** with the counting method above as a routine test that asserts the class
   COUNT (a counter, not a time) on the hero and the 60 mm taper — so a mesher change that silently
   destroys reuse (e.g. jittering bulk spacings) is caught.
2. **Class key.** Key on grid INDICES, not on doubles: the x-class of `(ix_a, ix_b)` is
   `(spacing class of ix_a, spacing class of ix_b, Σ spacings between)`; build a per-axis table of
   distinct spacings once (exact `==` on the gridline differences is fine — the mesher produces bulk
   spacings by one subtraction; if it does not, quantise at 1e-12 relative and SAY SO). The class of
   a cell pair is `(xclass, yclass)` with canonical orientation `(Δx, Δy) ≥ 0` lexicographically.
   Pairs where either cell is cut are never memoised.
3. **Cores.** Build P4's primitives once per class into a class table; the packed cell-pair arrays
   become an `int[]` class index (4 bytes per pair instead of 7 × 2 × 8) — a **memory** win on top of
   the time win. Gate: assembled `Fill` matrix on the seven fixtures above agrees with P4's to
   **1e-12 relative per entry** (ρ is computed from the class representative's coordinates, so the
   last bit moves; bit-identity is not available and must not be faked by canonicalising
   coordinates).
4. **Remainder.** Per frequency, evaluate each class's remainder primitives once; assemble per pair
   from the class table. Same 1e-12 gate.
5. **AIM near field** (`PlanarEntryFill`): same class table, restricted to the near pairs.
6. **Measure** core build time and per-frequency fill time on the seven fixtures before/after; record
   in `HISTORY.md` alongside the class-count table. The GaAs line's 2.1× is anomalous for a straight
   line — inspect its `GridX`, and if the bulk spacing is not uniform, record that as a mesher
   finding for a separate brief (do not fix it here).

## Must NOT

- Change the mesher to increase reuse. That is a legitimate follow-up and it changes N; it is not
  this brief.
- Memoise across meshes or across frequencies (the remainder table is per frequency already).

## Gates

The class-count counter test; 1e-12 matrix agreement on seven fixtures; every existing fill oracle;
`HISTORY.md` tables; `RESOLVED.md` write-up; `docs/design/mom-engine.md` §10.7 fill-cost table gains
its `> Built at P5` note.
