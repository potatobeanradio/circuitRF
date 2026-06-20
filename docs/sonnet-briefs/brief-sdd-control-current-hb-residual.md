# Brief #2 (SDD control currents): HB residual `_c_ref` recompute (per-iterate, NO Jacobian yet)

Design ref: `docs/design/sdd-control-current.md` §4 (the linear functional), §5 (HB residual). Depends on
brief #1 (contract + resolver + DC — landed; `ControlRefs`/`ControlBranchIndices`/`Evaluate(v,c)`/`DControl`
all exist on `SddModel`). This brief makes `_cn` work in the **HB residual** — the SDD sees the correct control
current at each Newton iterate, so HB converges to the right answer. It deliberately **does NOT add the
Jacobian coupling `J_cc`** — that's brief #3. Convergence here is **quasi-Newton** (the missing `∂_c/∂V` term
may slow it), which is fine for getting correct results and for letting the FD oracle define truth in #3.

Engine only (`src/Engine`). Build **0W/0E**. Every existing HB test stays green (no `C[n]` → no recompute →
identical residual).

---

## The mechanism (one idea)

The referenced branch current is a linear functional of the interface voltage iterate `V`, and the engine
already computes it: `HbLinearExtractor.SolveFullNetwork(omega, iNlAtInterface, bSrc)` returns the full linear
solution `x` whose tail holds **every** branch current (`x[branchIndex]`, with `branchIndex = nodeCount + k`,
exactly the `ControlBranchIndices` value brief #1 resolved). So the per-iterate control current at harmonic `k`
is just:

```
x_k       = extractor.SolveFullNetwork(ω_k, iNl[:,k], bSrc[k])   // iNl = present nonlinear interface currents
_c_ref(k) = x_k[ ControlBranchIndices[ref] ]                      // pick the referenced branch
```

This is the **same call** the post-convergence back-solver makes — relocated into the Newton loop and run per
harmonic. No transpose-solve, no `rᵀ_ref` (that's the Jacobian object, brief #3). `_c_ref` is linear in `iNl`,
so its spectrum is bounded by `K` (no new aliasing); IFFT `_c_ref(0..K)` to the time grid to feed the SDD.

> Sign care: `SolveFullNetwork` injects `iNl` as the nonlinear current drawn FROM the interface node
> (`b[nodeJ] -= iNlAtInterface[j]`) — the same convention `INl` uses everywhere. The branch-current sign in
> `x` follows the device's stamp (`AddBranchCurrent(br, from, to)` → current flows first-node→second-node).
> `_cn` must equal the **same** branch current VendorA's `_cn` means; verify the sign against the DC test from
> brief #1 (the DC engine reads the branch unknown directly with a known-correct sign — match it).

## Threading the extractor + `bSrc` into the Newton loop

Today `HbNewton.Solve` doesn't have the extractor or `bSrc` — `HbEngine.Run` builds the back-solver *after*
`Solve` returns. For per-iterate `_c_ref`, `Solve` needs them. Add an **optional control-current context**
parameter so the signature change is contained and non-control circuits pass nothing:

```csharp
public sealed record ControlCurrentContext(
    HbLinearExtractor Extractor,
    Complex[][]       BSrc,            // [K+1][mnaSize] — per-harmonic source RHS (snapshotted at this sweep point)
    double            F0,
    int               K);
```
- `HbNewton.Solve(... , ControlCurrentContext? cc = null)`.
- `HbEngine.Run` builds `cc` only when the netlist contains an SDD with `ControlRefs.Length > 0` (cheap scan;
  null otherwise → zero overhead, identical path). It already snapshots `bSrcThisPoint[k] =
  extractor.BuildSourceRhs(ω_k)` — reuse that for `cc.BSrc`. The extractor is already in scope.
- `RunSinglePoint` (loadpull) and the two-tone path: pass `null` for now (control currents in loadpull /
  two-tone are out of scope this brief — note it; two-tone has no scalar-harmonic back-solver yet anyway).

## Where the recompute happens in `HbNewton`

`EvaluateNonlinear` is where each device's `Evaluate` is called per time sample. The control currents must be
ready **before** that inner loop. Per Newton iterate:

1. **After** `iNl` for the *current* `V` is known but **before** (or interleaved with) the SDD time-domain
   evaluation — there's a chicken-and-egg: `iNl` depends on the SDD output, which depends on `_c`, which
   depends on `iNl`. Resolve it the **lazy/decoupled** way that matches the residual's fixed-point structure:
   use the `iNl` from the **previous** evaluation pass within this iterate is *not* needed — instead compute
   `_c_ref` from the **current iterate's interface currents as produced by this same `EvaluateNonlinear`
   pass**. Concretely:
   - `EvaluateNonlinear` already loops time samples building `iTime`/`qTime`. Control currents are a
     **frequency-domain** quantity (`SolveFullNetwork` is per-harmonic), so they can't be computed inside the
     per-sample loop from that same pass's running sums.
   - **Cleanest correct structure:** compute `_c_ref(k)` from the **`iNl` of the current `V`** by doing a
     *first* `EvaluateNonlinear`-style pass to get `iNl(V)` ignoring control currents (or using the
     previous iterate's `_c`), then `SolveFullNetwork` per harmonic → `_c_ref`, then the **real** evaluation
     pass with `_c_ref` seeded. Since the residual is recomputed every Newton step and the whole thing is
     inside the Newton fixed point, **using the previous iterate's `iNl` to form this iterate's `_c_ref` is
     acceptable** (it converges to the self-consistent point — the Jacobian in #3 makes it quadratic). Pick
     the simplest that converges: **carry `iNl` across iterates and form `_c_ref` from the last available
     `iNl`** (one `SolveFullNetwork` per harmonic per iterate, before `EvaluateNonlinear`). Document the
     choice; the FD oracle in #3 will validate the linearization regardless.

2. Build the per-SDD `ControlCurrents` time series: for each SDD, for each of its `ControlRefs`, assemble
   `_c_ref(0..K)` (complex spectrum), IFFT to the `gridN` time grid, and pass the per-sample value into
   `Evaluate(new PortVoltages(portV), new ControlCurrents(cValsAtT))`. (The SDD seeds `_c{n}` per sample — so
   `EvaluateNonlinear` must hold each SDD's `_c_ref(t)` array and index it by `t`.)

3. The residual assembly (`BuildF`) is **unchanged** — `iNl`/`qNl`/buckets already fold in the SDD output,
   which now depends on `_c`. Only `EvaluateNonlinear` changes (it produces the right `iNl` because the SDD got
   the right `_c`).

**`EvaluateNonlinear` signature:** add the optional `ControlCurrentContext? cc` + the previous-iterate `iNl`
(for forming `_c_ref`). When `cc` is null, the method is byte-identical to today. Keep the control path off the
hot path for non-control circuits.

## Branch-index validity in HB

Brief #1 resolves `ControlBranchIndices` against the DC engine's stamp. In HB the **same absolute index**
(`nodeCount + branchLocal`) is valid in `SolveFullNetwork`'s `x` because the linear MNA uses the identical
node/branch numbering (`HbLinearExtractor.BuildMna` stamps the same linear devices in the same order). Confirm
the SDD's `ControlBranchIndices` are resolved for the HB run too — if brief #1 resolved them only inside
`NonlinearDcEngine`, add a resolve step in `HbEngine.Run` after the first `extractor.Extract`/`BuildMna` (the
extractor stamps linear devices, assigning `LastBranchIndex`/`PortBranchIndices`). Reading
`ec.Model.LastBranchIndex` etc. after the extractor's first build gives the HB-valid index. **Assert each
referenced `ControlBranchIndices[i] ≥ 0` and `< mnaSize` before the Newton loop**, with a clear error if not.

## Tests (`tests/Engine.Tests/HarmonicBalance`)

- **HB residual correctness:** an SDD whose port current mirrors an inductor (or IProbe) branch current —
  `I[1,0]=beta*_c1`, `C[1]=L1` — in a circuit with a known branch current. Assert the SDD's port current
  spectrum equals `beta ×` the referenced branch spectrum (read the referenced branch from the post-convergence
  back-solver, which already works). Converges (maybe more iterations than with the Jacobian — that's expected).
- **`_cn` spectrum sanity:** drive the referenced branch with a tone; `_c1` carries the fundamental (and
  harmonics if the path is nonlinear). The mirrored SDD current reproduces it.
- **All five kinds (HB):** smoke test that an SDD referencing each kind (Vdc, IProbe, L, SnP port, ZnP port)
  runs to convergence and `_cn` is nonzero where expected.
- **Regression:** every existing HB test green (no `C[n]` → `cc=null` → identical). The weighting-engine and
  equivalence tests must not move.

## Gate
Build 0W/0E; tests green; HB converges with `_cn` correct in the residual (quasi-Newton, possibly more iters).
**Next (brief #3): the Jacobian coupling `J_cc`** — the transpose-solve sensitivity row `rᵀ_ref = e_refᵀG⁻¹P`
(§4/§6 of the design) times the SDD's `DControl` times the conversion matrix, restoring quadratic convergence
and gated by `CompareJacobianNumerical` at 1e-5. The FD oracle already re-runs this residual, so it will define
truth for #3 with no oracle changes.
