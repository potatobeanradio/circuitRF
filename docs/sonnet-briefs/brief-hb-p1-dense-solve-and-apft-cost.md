# Sonnet Brief — HB-P1: the dense solve, the APFT triple product, and the transform cache

**Design:** `docs/design/harmonic-balance.md` §6.4–§6.6 (the T-tone lattice and APFT), §8 (the
dense Newton solve — **its "NumFlat LU" sentence describes code that does not exist; this brief makes
it true**), §16 item 5 (block/iterative solve — stays deferred; nothing here builds it).
**Code:** `src/Engine/HarmonicBalance/HbNewton.cs` (`SolveGaussian`), `HbApft.cs`
(`AccumulateTripleProduct`, the constructor), `HbNewtonNd.cs` (`BuildJNd`), `HbEngine.cs`
(`RunMultiTone`, `RunTwoTone`).

**One sentence:** replace the hand-rolled Gauss-Jordan with a real LU on every Newton path, turn the
APFT Jacobian's triple product into a matrix multiply, and build the APFT once per lattice instead of
once per sweep point — measured together as roughly **8× on a 6-tone order-3 point (1.0 s → ~0.12 s)**
and ~3× at the shipped 6-tone order-2 fixture, with the single-tone and two-tone paths untouched in
their answers.

**Why (HB performance review, 2026-08-29).** A scratch Release-build harness on the Hero fixtures
(Apple M4, single-threaded; ±30 % run-to-run, proportions stable) put the per-point cost where the
design did not expect it:

| fixture | per point | where |
|---|---|---|
| Hero 5, 6 tones, order 3 (M=189, dof 756) | **1,010 ms** | `new HbApft` 93 ms; `BuildJNd` **169 ms per iteration**; `SolveGaussian` **124 ms per iteration**; device evaluation 3 ms |
| Hero 5, 6 tones, order 2 (M=43, dof 172) — as shipped | 12 ms | `new HbApft` 2.0 ms; `BuildJNd` 2.3 ms/iter; `SolveGaussian` 1.1–1.5 ms/iter |
| Hero 5, 3 tones, order 3 (M=32, dof 128) | 7 ms | `new HbApft` 1.1 ms; `BuildJNd` 1.0 ms/iter; `SolveGaussian` 0.6 ms/iter |
| Hero 5, two-tone, order 5 (M=31, dof 124) | 16.5 ms | `SolveGaussian` 0.54 ms/iter (NumFlat LU on the same matrix: 0.19 ms) |
| Hero 2 single-tone, dof 24–32 | 0.3–0.45 ms | `BuildJ` + solve 20–35 µs — **under 8 %; nothing to gain here** |

Design §6.4 measured "6 tones at order 3 in 4.3 s" and concluded the dense solve was adequate. It is
adequate as a *choice*; the *implementation* of that choice is three separate O(n³) or O(S·D²) scalar
loops running at a few GFLOP/s, plus a transform rebuilt every point although it depends on nothing
that changes between points.

**What this brief is NOT.** It is not a sparse Jacobian (the HB Jacobian is structurally dense — every
node-pair block is a Toeplitz+Hankel conversion matrix and `Y_NN` fills the rest), not a Krylov
solver, not a preconditioner (none exists in HB; the GMRES in `src/Engine/Mom/` is the EM solver's
and stays there), and not a change to any converged answer. Every milestone is gated on producing the
same ΔV / the same Jacobian to round-off.

**Structural facts.**

1. **`HbNewton.SolveGaussian` is Gauss-Jordan with a full augmented copy** (`HbNewton.cs` ≈ line
   1111): it reduces every row above and below the pivot at every column (~n³ flops against LU's
   n³/3), and first copies the n×n matrix into an n×(n+1) buffer, doubling the transient. All three
   Newton loops (`HbNewton.Solve`, `HbNewton2D.Solve`, `HbNewtonNd.Solve`) call it. NumFlat is
   already a dependency of `CircuitRF.Engine` and RfCore uses its `Lu()`; measured on the harness it
   is 2.9–3.3× faster at dof 124–756 *including* the copy into `Mat<double>`. At dof ≤ 40 it is a
   wash (the copy dominates) — keep the small case honest by measuring, not by assuming.
2. **`HbApft.AccumulateTripleProduct(w, block)` computes `Aᵀ⊙w · Γ` as a scalar triple loop.** It is
   a `(D×S)·(S×D)` matrix product with one diagonal weight; `BuildJNd` calls it **2·N² times per
   iteration** (dg and dc for every node pair), each `S·D²` multiply-adds — 756·378² ≈ 1.1e8 per
   call, 8.6e8 per iteration at 6 tones/order 3, in 169 ms ≈ 5 GFLOP/s. A blocked, SIMD-friendly
   product (or the existing `Mat<double>` multiply, if it is blocked — measure it first) is 5–10×
   that on this machine. Two further free wins in the same loop: dg and dc for the same (n,m) share
   `A` and `Γ` and can be produced in one pass by stacking the two weight vectors; and the N² node
   pairs are independent, so they parallelise trivially (`Parallel.For` over (n,m) with a private
   block each — no shared writes).
3. **`new HbApft(lattice, oversample)` depends only on (tone count, `MaxMixOrder`, oversample).**
   `RunMultiTone` constructs it per call (`HbEngine.cs:693–694`), i.e. per sweep point, and the
   constructor builds Γ (S×D trig evaluations), the D×D normal matrix, a Cholesky, and S solves —
   93 ms at order 3. A parametric sweep of 20 points pays 1.9 s for twenty identical transforms. It
   is immutable after construction and `_gamma`/`_at` are read-only by contract, so a process-wide
   cache keyed `(T, order, oversample)` is safe to share across threads and across `HbEngine`
   instances. The same holds for `MixingLattice`.
4. **The Jacobian is real-split and must stay so.** The `G[k+i]` term couples `V` to `conj(V)`, so
   the system is not complex-linear; a complex LU on a half-size matrix is not available. The real
   LU at 2·N·M is the right object.
5. **Memory is not the problem.** At the 600-product ceiling (N=2) J is ~46 MB and Γ+Aᵀ ~92 MB;
   Gauss-Jordan's augmented copy is the only avoidable transient. Do not spend effort on memory here.
6. **The two-tone path is frozen by its goldens, the n-tone path is not.** `HbNewtonNdVs2DTests`
   already drives the lattice path at T=2 against `HbNewton2D`; they agree on DC and the carriers and
   converge to each other as the diamond grows (design §6.5). The two-tone FFT box evaluates the
   device on **1,024 samples to solve 62 complex unknowns**; the lattice at the same order needs
   ~250. That is a ~4× two-tone win per iteration, but it changes the Hero-5 goldens at truncation
   level — so it is **M4, owner-gated, off by default**, not part of the performance gate.

**Sequencing.** M1 LU everywhere (smallest, unlocks the rest of the measurement). M2 the triple
product. M3 the APFT cache. M4 (optional, owner decision) two-tone on the lattice behind a setting.

---

## 1. M1 — a real LU on every Newton path

`HbNewton.SolveGaussian(double[] A, double[] b, int n)` keeps its signature and its `null`-on-singular
contract (all three callers branch on `null` and print the singular-Jacobian line). Inside:

- **n ≤ 48:** keep the existing Gauss-Jordan (it is faster than the `Mat<double>` copy at Hero size —
  verify the crossover with the harness rather than trusting 48; record the measured value in the
  code comment).
- **n > crossover:** partial-pivot LU. Preferred: NumFlat's `Mat<double>.Lu()` + `Solve`, copying
  in and out (the copy is O(n²), the factorisation O(n³); at n=124 the copy is already under the
  factorisation). If NumFlat's LU turns out not to be blocked (measure at n=756: expect ≤ 40 ms), a
  ~60-line in-place blocked LU with partial pivoting on the `double[]` we already own is acceptable —
  keep it in `HbNewton` and gate it against NumFlat's result to 1e-10 relative.
- Singular detection must match today's: pivot magnitude below 1e-30 → `null`. NumFlat throws on a
  singular matrix; catch that one exception type and return `null`.

**Do not** change `BuildJ`, `BuildJ2D`, `BuildJNd`, or any `Idx`/DC-dummy handling.

## 2. M2 — the triple product as a matrix multiply

In `HbApft`, replace `AccumulateTripleProduct(double[] weights, double[] block)` by a method that
takes **both** weight vectors of a node pair and returns both blocks, and make `BuildJNd` call it once
per (n,m):

```
AccumulateTripleProducts(double[] wG, double[] wC, double[] blockG, double[] blockC)
```

Implementation choices, in order of preference — measure each on the 6-tone/order-3 fixture and keep
the fastest that passes the gate:

1. Form `W = [Aᵀ⊙wG | Aᵀ⊙wC]` as a `(2D × S)` row-major array once (O(S·D)), then one blocked
   product `W · Γ` (`(2D×S)·(S×D)`), with a cache-blocked kernel over (row-block, k-block, col-block)
   and `Vector<double>` on the innermost column loop. This is the plain GEMM shape and is what to
   ship unless (2) is measurably better.
2. NumFlat `Mat<double>` multiply on the same operands, if its multiply is blocked (it may not be —
   a naive triple loop in a library is no better than ours).
3. `Parallel.For` over the N² node pairs in `BuildJNd`, each thread owning its two block buffers.
   Independent of (1)/(2); do it after, and only if `N ≥ 2` (Hero 5 is N=2 — four pairs).

The AllZero shortcuts (`dg`/`dc` identically zero for a node pair with no device across it) stay.
The `R(ω_row)` rotation of `blockC`, the guard cutoff, `Y_NN` on the diagonal, and the Maas DC
special cases are unchanged and stay *after* the product, exactly where they are.

## 3. M3 — the APFT cache

A static, thread-safe cache in `HbApft` (or a small `HbApftCache` beside it):

```
public static HbApft Get(int toneCount, int maxMixOrder, double oversample)
```

keyed on the three inputs, `ConcurrentDictionary` with `GetOrAdd` (a duplicate construction under a
race is harmless — both are identical and immutable). `RunMultiTone` and `CheckMultiToneCeiling`
take it from the cache; the ceiling check still runs **before** the cache is touched, so an over-cap
request still refuses without allocating (design §6.6 — keep that test green). `MixingLattice` is
cached the same way or inside the same entry. Bounded size is not needed (a session sees a handful
of (T, order) pairs); document that it is unbounded and why.

## 4. M4 — two-tone on the lattice (owner-gated, optional)

Only if the owner opts in after reading the M1–M3 numbers. Add `AnalysisSettings.HbTwoToneOnLattice`
(**default false**). When true, `HbEngine.Run` routes `ToneFreqsHz.Length == 2` to `RunMultiTone`.
The `MixingLattice` at T=2 reproduces `MixingGrid`'s locked index order element for element
(`MixingLatticeTests` pins this), so cube shapes, `mixIndex` labels, `TwoToneMeasurements` and the
data display's two-tone spectrum are unaffected. Record in RESOLVED the per-point time both ways on
`hero5.cnl` and the max |ΔV| against the frozen goldens per product, ordered by diamond order — the
expected shape is "carriers to 1e-6, order-5 edge products to ~1e-3 relative", the truncation
signature design §6.5 already documents. **Do not** change the default, the goldens, or `HbNewton2D`.

## 5. Tests

`tests/Engine.Tests/HarmonicBalance/HbDenseSolveTests.cs` (add):

1. **LU agrees with Gauss-Jordan.** For the Jacobians actually produced on `hero2_convergence.cnl`
   (dof 24–32), `hero5.cnl` (dof 124) and `hero5_6tone.cnl` at order 2 (dof 172) and order 3 (dof
   756): solve the same `−F` both ways, assert `‖ΔV_lu − ΔV_gj‖ ≤ 1e-10·‖ΔV_gj‖`. Keep the old
   Gauss-Jordan as a private reference in the test project, not in `HbNewton`.
2. **Singular still returns null.** A Jacobian with a zeroed row/column on both sides of the
   crossover → `null`, no exception escapes.
3. **Crossover is a named constant** and the test asserts both branches are exercised by (1).

`tests/Engine.Tests/HarmonicBalance/HbApftTests.cs` (extend):

4. **Triple product equals the reference.** For the 3-tone and 6-tone/order-2 lattices and random
   weight vectors, the new product equals the scalar triple loop (kept in the test as the oracle) to
   1e-12 relative, for both blocks at once.
5. **Jacobian oracle unchanged.** `CompareJacobianNumericalNd` on `hero5_3tone.cnl` and
   `hero5_6tone.cnl` still passes its existing gate — this is the test that would catch a transposed
   `W` or a dropped rotation.
6. **Converged answers unchanged.** The existing multi-tone regression goldens (`hero5_3tone`,
   `hero5_6tone` self-goldens) pass to their current tolerance with M1–M3 in.
7. **The cache returns the same instance** for equal keys and distinct instances for different
   keys; `RunMultiTone` twice on the same netlist constructs one `HbApft` (count via a public
   static `HbApft.ConstructionCount` diagnostic counter — `CircuitRF.Engine` has **no**
   `InternalsVisibleTo` for its test project, so `internal` is not reachable from the tests; a
   public diagnostic counter with a doc comment saying it exists for tests is the house-compatible
   way, and the same pattern serves tests 8 and 9).
8. **The ceiling still refuses before construction.** Existing test; assert the counter did not
   move.
9. **Cost is asserted as a COUNT, not a time** (per the no-new-timing-benchmarks rule): `BuildJNd`
   on `hero5_6tone.cnl` calls the product exactly **N²** times per iteration (was 2·N²) — counter on
   the method.

Measurements — the per-point times before/after at each milestone on the four fixtures above — go in
the completion note, not in a test. The scratch harness from the review can be reused; it lives
outside the repo and is not committed.

## 6. Gates

```
dotnet build
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing. Two-tone
goldens (`Hero5Tests`) and single-tone goldens (`Hero2Tests`, `Hero4Tests`) must be byte-for-byte
where they were: this brief changes no answer on those paths.

## 7. On completion

Findings — the measured crossover, whether NumFlat's LU and multiply turned out blocked, the
before/after table for the four fixtures, anything the parallel node-pair loop needed — to
**`src/Engine/RESOLVED.md` §HB-P1**. Correct design §8's "NumFlat LU" sentence in place to describe
what is now true, and §6.4's timing table. **Never to any `CLAUDE.md`.** Do not commit; the owner
commits.

## 8. Out of scope, deliberately

- A sparse or block-structured Jacobian, Newton-GMRES, any preconditioner — the structure is dense
  and the product ceiling keeps it small; design §16 item 5 stays deferred.
- Changing `MaxMixOrder`, `HbMaxMixProducts`, or the APFT oversample.
- The device-evaluation cost (HB-P4) and the per-point extractor rebuild (HB-P2) — separate briefs;
  on the n-tone fixtures they are not the bottleneck, on single-tone they are the whole bottleneck.
- A native BLAS/LAPACK dependency — ask before adding one (root `CLAUDE.md`, "Ask before").
