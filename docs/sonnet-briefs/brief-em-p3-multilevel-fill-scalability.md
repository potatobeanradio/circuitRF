# Brief P3 — the multi-level fill must scale like the single-level one

**Problem.** `PlanarFill.FillMultiLevel` (`src/Engine/Mom/PlanarFill.cs`, from ~line 780) was written
for correctness against the single-level reduction, and it does per-ENTRY work inside the parallel
`ForRows` loop that the single-level fill deliberately hoists:

- `set.Get(kernel, za, zb)` per cell pair and per basis pair (`PlanarFill.cs:814` and the vector arm)
  takes `lock (_terms)` in `PlanarKernelSet.Get` — every thread contends on one lock in the O(N²)
  inner loop — and `.With(order, rhoFloor)` allocates a new `PlanarKernelTerms` per pair.
- `RemFor(...)` takes `lock (remCache)` per pair.
- `HorizontalVectorEntry` allocates `new[] { (ma, na), … }` per entry (`PlanarFill.cs:1065`) and
  recomputes `RampHalves` per entry; the single-level `AddDirectionBlock` caches `halves` once.
- `SpanOf`, `ZzTermsFor` (`lock (zzTerms)`) and `MixedDerivativeFor` (`lock (mixedDer)`) likewise
  per entry.

The single-level fill measures 5.4× on 10 cores. Nobody has measured the multi-level fill's scaling;
its shape says it will be far worse.

Also in scope, because it is the same file and the same loop shape: the dense fill writes `z[i, j]`
with `j` innermost and mirrors with `z[j, i] = z[i, j]` (`PlanarFill.cs:683`, `703`, and the multi-level
twins at 842, 973). `Mat<Complex>` is column-major, so both stride by N. At N = 5,000 that is 25 M
cache-missing writes per fill, and `CLAUDE.md` §6 attributes the fill's parallel fall-off (98% at 2
cores → 53% at 10) to "memory bandwidth" without naming a cause.

Read first: `PlanarFill.cs` — `Fill`, `AddDirectionBlock`, `FillMultiLevel`, `HorizontalVectorEntry`;
`PlanarKernelSet.Get`; `CLAUDE.md` §6's M3 paragraph (the scaling numbers).

## Milestones

1. **Measure first.** Multi-level fill wall clock at caps 1, 2, 4, 10 on the two-level via fixture
   used by `ViaPhysicsTests` (or `MultiLevelPortTests`' largest routine fixture) and on a two-level
   mesh with no vias. Record efficiency per core. This is the number the brief exists to move.
2. **Hoist per-pairing state out of the row loop.** Before `ForRows`: resolve `PlanarKernelTerms`
   and the remainder evaluator for every (kernel, level, level) pairing into a small array indexed by
   `(layerA, layerB)`; resolve every via span's `ZzTermsFor`/`MixedDerivativeFor` for every (span,
   span) and (span, level) combination the mesh contains (there are a handful). Cache `RampHalves`
   and `Halves` per basis exactly as `Fill` does. Remove every lock and every allocation from the
   inner loops. Gate: `FillMultiLevel` **bit-identical** on every existing multi-level fixture (the
   arithmetic per entry is unchanged; only when it is looked up changes).
3. **Re-measure** milestone 1. Record.
4. **The strided writes.** Fill the LOWER triangle (contiguous in column-major) or fill each packed
   row into a `Complex[]` and scatter once; mirror in the cache-friendly direction. Gate:
   bit-identical `Fill` on the hero. Measure the single-level fill's scaling at caps 1/2/4/10 before
   and after on the 256 mm line; record beside `CLAUDE.md` §6's numbers. If the fall-off does not
   move, say so — that is a result and it retires a hypothesis.

## Must NOT

- Change any quadrature rule, table spacing or kernel evaluation.
- Add a second parallelism cap or a new `Parallel.For` shape — `ForRows` stays the one loop.

## Gates

Bit-identity on every multi-level and single-level fill fixture; the two scaling tables in
`HISTORY.md`; `RESOLVED.md` write-up. If `CLAUDE.md` §6's "hardware, not scheduling" sentence
becomes false, correct it in place.
