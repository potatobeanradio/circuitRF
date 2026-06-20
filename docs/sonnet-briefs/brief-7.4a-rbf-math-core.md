# Brief 7.4a — RBF (multiquadric) 2-D interpolant + 1-D linear interp (headless RfCore math core)

**Phase:** 7.4a (Data Display loadpull contours — the math primitive beneath the surface model).
**Design:** `docs/design/loadpull-contours.md` §2.3 (scipy.Rbf scope), §2.4 (solver/perf), §1.3 (custom dense
LDLᵀ decision).
**Goal:** a fast, allocation-free **2-D radial-basis-function interpolant** that **numerically matches
scipy.interpolate.Rbf** for the subset the loadpull surface model uses, plus a tiny **1-D linear
interpolator**. This is the foundation 7.4b (`LoadpullSurface`) sits on. **Headless, framework-free, RfCore.**
**Reference (read-only, outside repo):** `<workspace>/loadpull-contours-refs/SPLData.py` —
class `DRFRbf` and every `scipy.interpolate.Rbf(...)` / `scipy.interpolate.interp1d(...)` call show the exact
parameters used: `function='multiquadric'`, `smooth=1e-3`, `norm='euclidean'`, complex Γ split into
`(X.real, X.imag)`, NaN values dropped before fitting. **Match scipy's conventions exactly** — they are the
numerical gate.

**No UI. No DataSet. No loadpull semantics.** Pure numerics: arrays in, arrays out. 7.4b wires it to cubes.

---

## 1. What scipy.interpolate.Rbf actually computes (the spec to match)

For nodes `xᵢ ∈ ℝ²` (here `xᵢ = (Re Γᵢ, Im Γᵢ)`) and scalar values `dᵢ`, scipy builds:

1. **Pairwise distance matrix** `r[i,j] = ‖xᵢ − xⱼ‖₂` (euclidean).
2. **Epsilon (shape parameter), scipy's default** when `epsilon` is not given:
   `epsilon = (Π_dim (max(coord_dim) − min(coord_dim))) / N) ** (1/dim)` clamped: scipy computes
   `ximax/ximin` per dimension, `edges = ximax − ximin`, `edges = edges[edges != 0]`,
   `epsilon = power(prod(edges)/N, 1.0/edges.size)`. For 2-D: `epsilon = sqrt((Δx · Δy) / N)` using only
   non-degenerate axes. **Replicate this exactly** (including the `edges != 0` filter) or numerical parity
   fails.
3. **Basis (kernel) applied to scaled distance** `φ(r)`:
   - **multiquadric** (the one used): `φ(r) = sqrt((r/epsilon)² + 1)`.
   - **thin_plate**: `φ(r) = r² · ln(r)` (with `φ(0)=0`); scipy uses `xa*xa*log(xa)` guarded at r=0.
   - **gaussian**: `φ(r) = exp(-(r/epsilon)²)`.
   Implement all three (a `RbfKernel` enum) — three lines each — but multiquadric is the default and the only
   one the gate must match tightly.
4. **Kernel matrix** `A[i,j] = φ(r[i,j])`, then **smoothing**: scipy subtracts `smooth` from the **diagonal**:
   `A := A − eye(N) * smooth`. (Note the **minus** sign — scipy's convention; `smooth=1e-3` ⇒ subtract 1e-3.)
   Match the sign exactly.
5. **Solve** `A · w = d` for weights `w` (N-vector). scipy uses `scipy.linalg.solve` (general dense LU). We use
   a symmetric factorization (A is symmetric; see §3). Result must agree with scipy to tolerance regardless of
   factorization path.
6. **Evaluate** at query points `q`: `f(q) = Σᵢ wᵢ · φ(‖q − xᵢ‖)` (same epsilon, same kernel).

**NaN-drop (from `DRFRbf`):** before fitting, drop every node whose **value** `dᵢ` is NaN (and the matching
`xᵢ`). `DRFRbf` records `IndexRemoved`/`IndexUsed` — expose an analogous `UsedIndices` (the original indices
that survived) because 7.4b uses it (`get_MXX` reads `Interpolator.xi`/`.di`; `generate_PS_interpolator`
filters by `len(IndexUsed) > 12`).

---

## 2. API (the surface the 7.4b brief will call)

New file `RfCore/src/Loadpull/Rbf2D.cs` (co-locate with `SplReader`/`LpcwaveReader`; namespace
`RfCore.Loadpull` or wherever those landed — match the 7.4f readers' namespace). Public, framework-free:

```csharp
public enum RbfKernel { Multiquadric, ThinPlate, Gaussian }

public sealed class Rbf2D
{
    /// Fit an RBF through scattered 2-D nodes. NaN-valued nodes are dropped.
    /// xRe, xIm, values must be equal length. epsilon: null ⇒ scipy default.
    public Rbf2D(
        ReadOnlySpan<double> xRe, ReadOnlySpan<double> xIm, ReadOnlySpan<double> values,
        RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null);

    public int    NodeCount { get; }          // nodes actually used (post NaN-drop)
    public double Epsilon   { get; }          // resolved shape parameter
    public IReadOnlyList<int> UsedIndices { get; }   // original indices that survived NaN-drop

    // Retained fit nodes (post-drop) — 7.4b's MXP/MXE + view-box read these (the SPLData .xi/.di).
    public ReadOnlySpan<double> NodesRe { get; }
    public ReadOnlySpan<double> NodesIm { get; }
    public ReadOnlySpan<double> NodeValues { get; }

    /// Evaluate at one point.
    public double Evaluate(double re, double im);

    /// Evaluate at many points into a caller-supplied buffer (allocation-free hot path).
    /// result.Length must equal qRe.Length == qIm.Length.
    public void Evaluate(ReadOnlySpan<double> qRe, ReadOnlySpan<double> qIm, Span<double> result);
}
```

Convenience for complex callers (7.4b works in `Complex` Γ): add overloads taking `ReadOnlySpan<Complex>` that
split into re/im internally — but the core stores `double[]` arrays (no `Complex[]` in the hot path).

Also new file `RfCore/src/Loadpull/Interp1DLinear.cs`:
```csharp
public sealed class Interp1DLinear
{
    /// x must be sorted ascending (assert/throw if not — scipy requires it). y same length.
    public Interp1DLinear(ReadOnlySpan<double> x, ReadOnlySpan<double> y);
    /// Linear interpolation; out-of-range ⇒ NaN (scipy interp1d bounds_error=False default fill).
    public double Eval(double x);
    public void Eval(ReadOnlySpan<double> xs, Span<double> result);
}
```
This mirrors `scipy.interpolate.interp1d(..., kind='linear', bounds_error=False)` — out-of-range returns
**NaN** (scipy's `fill_value=nan` default when `bounds_error=False`). `SPLData.py` relies on this NaN to drop
unsupported points downstream.

---

## 3. The solver (performance — a GATED property, design §2.4)

The kernel matrix `A` is **symmetric** (`φ(‖xᵢ−xⱼ‖)` is symmetric in i,j) and, with positive multiquadric +
the smoothing diagonal, well-enough conditioned for a symmetric factorization. **Use LDLᵀ (Bunch-Kaufman) or
Cholesky-with-fallback**, not LU — ~2× faster, cleaner. Decision (design §1.3): **custom, allocation-free
dense** — NOT CSparse (sparse solver on a dense matrix = wrong tool) and NOT NumFlat (call/setup overhead
dominates at N≈20).

Implementation requirements:
- **Allocation-free hot path.** Pre-size all buffers in the ctor; the factorization works in a single
  contiguous row-major `double[]` (the kernel matrix, overwritten in place by the factorization). `Evaluate`
  allocates nothing. Use `Span<double>`/`stackalloc` for tiny scratch only when size is bounded.
- **LDLᵀ:** factor `A = L·D·Lᵀ` (unit-lower-triangular L, diagonal D), then forward/diagonal/back substitution
  for the solve. If a pivot D is ~0 (singular kernel — degenerate/duplicate nodes), **add a tiny jitter to the
  diagonal and retry once** (a `1e-12 · trace/N` ridge), then warn-once if still singular and return a
  zero-weight fit (so the caller degrades gracefully rather than throwing — research-tool philosophy).
- **Evaluation** is the real hotspot (7.4b evaluates a 50×50 grid × up to 32 stack surfaces). Keep nodes in
  flat `double[]` (`NodesRe`, `NodesIm`, weights `W`), loop tight: for each query point sum
  `Σ wᵢ φ(sqrt((qRe−reᵢ)² + (qIm−imᵢ)²))`. Structure so a future `System.Numerics.Vector<double>` SIMD pass
  drops in without API change (don't SIMD now unless the benchmark misses target — premature).

---

## 4. Gates

### 4a — correctness (numerical parity with scipy)
- **Golden vectors.** Generate reference outputs from scipy on the **real loadpull data** and check ours
  matches. Process: pick `testdata/spl_test_data/Ideal_GaN_FET_1p6_mm_1p8_GHz.spl`; take the load-Γ grid
  points (≈145) and one reduced metric (e.g. Pout at the last pin step per point — any per-point scalar);
  fit `Rbf2D`; evaluate on a small fixed query set; compare to scipy `Rbf(re, im, vals,
  function='multiquadric', smooth=1e-3, norm='euclidean')(qre, qim)`.
  - **You (Sonnet) cannot run Python.** So either (i) the owner provides a golden CSV of
    `(qre, qim, expected)` generated from scipy, OR (ii) embed a **small hand-checked golden set** computed
    from the closed-form RBF with known weights (a 4–6 node toy problem solved by hand/exactly) AND a
    self-consistency check (interpolant reproduces nodal values at the nodes within tol when smooth→0).
  - **Mandatory self-consistency tests (no Python needed):**
    - With `smooth=0`, `Evaluate(nodeᵢ)` ≈ `valueᵢ` for every node (interpolation property), tol 1e-6.
    - Epsilon default matches the scipy formula on a known node set (assert the computed `Epsilon` against a
      hand-computed `sqrt(Δx·Δy/N)`).
    - NaN-drop: inject NaN values; `UsedIndices` excludes them; `NodeCount` drops; fit succeeds.
    - Symmetry/known-answer: a radially symmetric value field (e.g. `d = re²+im²`) evaluated at the centroid
      is monotone/sane; a constant field returns that constant everywhere (tol 1e-9, multiquadric reproduces
      constants only approximately — use a loose tol or test thin_plate/affine-augmented separately; if a
      constant is NOT reproduced exactly that's expected for plain multiquadric, document it).
- **`Interp1DLinear`:** midpoint, node, and out-of-range (→NaN) cases; against hand values.
- **Flag for the owner:** ask whether to supply a scipy-generated golden CSV (best parity gate) or accept the
  hand-checked + self-consistency suite. Put a `// GOLDEN: ...` comment where the CSV would slot in.

### 4b — performance (benchmark assertions)
Add timing tests (xUnit `[Fact]`, wall-clock with a warmup, generous CI-safe thresholds — these guard against
*regression to the wrong order of magnitude*, not micro-tuning):
- **Fit @ N=20:** construct `Rbf2D` from 20 random nodes — assert < ~0.2 ms median over 100 runs (post-warmup).
- **Fit @ N=200:** < ~5 ms median.
- **Evaluate 50×50 grid (2500 pts) @ N=200:** < ~5 ms median.
- **Full surface (fit N=200 + eval 2500):** < ~10 ms median.
(Owner: tune thresholds at bring-up to the dev machine; the point is a guarded ceiling, generous enough not to
flake on CI but tight enough to catch an accidental O(N²) eval or a per-call allocation.)

---

## 5. Constraints / gotchas
- **Firewall:** RfCore, zero Avalonia. `Rbf2D`/`Interp1DLinear`/`RbfKernel` are framework-free.
- **Nullable enable** (RfCore csproj). **No `ImplicitUsings`** in RfCore.Tests (csproj disables it) — fully
  qualify or add `using` directives in tests.
- **NumFlat is available** (RfCore references it) — but **do not use it for the RBF solve** (design §2.4: call
  overhead hurts small N; we want the allocation-free custom path). Using NumFlat would also obscure the
  perf-gate intent. (NumFlat is fine elsewhere in RfCore; just not here.)
- **scipy sign conventions are exact requirements:** epsilon formula (`edges != 0` filter), smoothing is
  `A − smooth·I` (minus), multiquadric is `sqrt((r/ε)²+1)`. A wrong sign or a missing epsilon-scale passes
  self-consistency but fails real-data parity — the most likely silent bug. Write the epsilon + kernel as
  their own tiny tested methods.
- **Determinism:** no parallelism in the fit/eval in 7.4a (keep it deterministic + simple; parallel grid eval
  is a later optimization if a benchmark demands it).
- **TreatWarningsAsErrors** not set on RfCore (it's on Core/UI) — but keep it clean anyway (no unused privates).

## 6. Tests
- New `RfCore/tests/RfCore.Tests/Rbf2DTests.cs` and `Interp1DLinearTests.cs`.
- If the owner supplies a scipy golden CSV, drop it in `RfCore/tests/RfCore.Tests/testdata/` (csproj already
  copies `testdata/**`); load with the same relative-path pattern existing tests use.
- Performance tests in a separate `Rbf2DPerfTests.cs` (so they can be `[Trait]`-filtered out of fast CI if
  needed).

## 7. Out of scope (next sub-gates)
- Compression preprocessing, metric-@-constant-other-metric surfaces, MXP/MXE auto-view-box, the off-grid
  power-sweep stack, the lazy cache → **7.4b/7.4c** (`LoadpullSurface`, which consumes this `Rbf2D`).
- Contour iso-line extraction (marching squares) → **7.4d**.
- The contour trace card + `.s1p` overlay → **7.4e**.
