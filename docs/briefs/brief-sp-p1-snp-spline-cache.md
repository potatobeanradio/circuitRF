# Sonnet Brief — SP-P1: an SnP fits its splines once, and a sweep parses each Touchstone file once

**Design:** `docs/design/linear-engine.md` §4 (how each category stamps — the SnP Z(ω) branch-current expansion),
`docs/design/data-model.md` §5 (component models contribute stamps; the engine owns the matrix).
**Code:** `src/Core/Devices/SnpModel.cs` (`Stamp`, `LoadSnp`), `src/RfCore/RFNetwork.Interpolation.cs`
(`Interpolate`, the private `Spline1D`, `PhaseUnwrap`, `ToType`), `src/RfCore/TouchstoneIO.cs`
(`ReadFile`), `src/Core/Devices/ComponentModelFactory.cs` (`CreateSnpModel`),
`src/Engine/ParametricSweepEngine.cs:161` (the per-point re-elaboration that makes every SnP new).

**One sentence:** `SnpModel.Stamp` calls `RFNetwork.Interpolate(snp, [hz], …)` once per frequency,
and that call re-fits every one of the file's 2·N² splines from scratch each time — 98 % of the
run on any SnP-heavy circuit — so fit the splines once per model and evaluate them per ω, and stop
re-parsing the Touchstone file at every point of a parametric sweep.

**Why (S-parameter engine performance review, 2026-08-30).** Measured Release, M4, single thread,
scratch harness (not a test):

| Circuit | µs per frequency point | allocated per point |
|---|---|---|
| 200-node RLC ladder, 2 ports | 110–150 | 380 KB |
| the same ladder with 20 of its inductors replaced by 2-port SnPs (2001-pt files, cubic) | **4,700** | **34 MB** |
| `RFNetwork.Interpolate` on that file, 401 targets, one call per target | 91.5 ms total | — |
| the same 401 targets in ONE `Interpolate` call (= fit once, evaluate 401×) | **0.26 ms** | — |

The SnPs made the circuit 46× slower; the interpolation alone is 350× cheaper when the splines
are built once. A design built from a device kit's Touchstone files — dozens of SnPs, thousands of
points each — is exactly this shape, and today its S-parameter sweep is spent almost entirely in
spline fitting that produces the same coefficients every time.

**Structural facts.**

1. **Nothing in the fit depends on the target frequency.** `Interpolate` steps 1 (`ToType`) and 3
   (extract component arrays, `PhaseUnwrap`, construct `Spline1D`) depend only on the source SNP,
   the method and the format. Only the two `Eval`/`EvalExtrap` calls per (r, c) and the
   out-of-range decision depend on the target. The `SnpModel` holds all four inputs as readonly
   fields (`_snp` after the first load, `_interpMethod`, `_interpFormat`, `_extrapPolicy`).
2. **`Spline1D` is a private readonly struct inside `RFNetwork`**, so the model cannot hold one
   today. RfCore needs a small public type that owns the fitted splines — call it
   `SnpInterpolator` — built from `(SNP, method, format, interpolateIn, outOfRange)` and exposing
   `Mat<Complex> Evaluate(double hz)` plus an `Evaluate(double[] hz) → SNP` that `Interpolate`
   itself delegates to, so there is ONE fitting path, not two that can drift.
3. **The out-of-range warning is emitted per call** (`Warn(...)` in step 2, through the static
   `RFNetwork.OnWarning`). Per-point calls therefore warn once per frequency today, and the engine's
   drain dedupes by key. Keep the single-`Interpolate`-call behaviour exactly: the interpolator
   warns once, the first time a target falls outside the stored range.
4. **The result must be bit-identical.** `Spline1D.Eval` is deterministic and the coefficients are
   the same doubles whether fitted once or 2001 times, so `Hero1Tests`, `SnpStampTests`,
   `RfCore.Tests/InterpolationTests` and every golden that passes through an SnP must pass
   unchanged, with no tolerance loosened. If a number moves, the refactor is wrong.
5. **`SnpModel` instances are per elaboration.** `ParametricSweepEngine` re-elaborates at every
   sweep point by design (`using var netlist = new Elaborator(lib)…`), which news every model, so
   `LoadSnp` runs `TouchstoneIO.ReadFile` again for every SnP at every point (2.6 ms per 2001-pt
   file, 0.06 ms per 84-pt one) and the fit from fact 2 would be redone. The cure is a
   process-wide cache of parsed `SNP`s keyed by **full path + last-write time + length**, consulted
   by `LoadSnp`. The fitted interpolator can live in the same cache entry keyed additionally by
   (method, format, policy), since two models of one file with the same settings need one fit.
   The SNP returned to callers is treated as immutable today (nothing in `SnpModel` writes to it);
   confirm with a grep before sharing it, and if anything mutates, share a copy.
6. **Memory of the cache is bounded by what the user opened**, not by the sweep: one parsed SNP per
   distinct file. Do not add an eviction policy; do invalidate on mtime/length change so a file
   re-saved by the user is re-read on the next run (the GUI's re-run path is the case that matters).

---

## 1. M1 — `SnpInterpolator` in RfCore

`public sealed class SnpInterpolator` in `src/RfCore/RFNetwork.Interpolation.cs` (or a sibling
file), constructed with the five inputs of `Interpolate`. Constructor does steps 1 and 3 of today's
`Interpolate` once; `Evaluate(double hz)` does the per-target body; `Evaluate(double[] hz)` returns
the same `SNP` the old method returned. `RFNetwork.Interpolate` becomes
`new SnpInterpolator(...).Evaluate(targetFrequencies)` — unchanged signature and behaviour,
including the warn-once-per-call semantics (the interpolator keeps a `_warnedOutOfRange` flag; a
fresh interpolator per `Interpolate` call reproduces today's one-warning-per-call exactly).

## 2. M2 — `SnpModel` holds its interpolator

`SnpModel.Stamp`: `_interp ??= new SnpInterpolator(LoadSnp(c), …)`; then
`var zMat = RFNetwork.SToZ(_interp.Evaluate(hz), snp.Z0)` and the existing branch stamps. Nothing
else in the method changes. The `[hz]` array allocation, the per-call `Mat<Complex>[1]` and the
per-call `SNP` wrapper all go with it.

## 3. M3 — parsed-file cache

`static class TouchstoneCache` (RfCore, or `src/Core/Devices` if you prefer it next to the model —
RfCore is the better home since `TouchstoneIO` is there): `SNP Get(string fullPath)` returning the
cached parse when path, mtime and length match, else re-reading. Thread-safe (`ConcurrentDictionary`
+ `Lazy`, the pattern `MicrostripKlopfModel._geometryCache` already uses). `SnpModel.LoadSnp` calls
it after its three refusal checks, which stay as they are — they are what names the component.
Cache the fitted `SnpInterpolator` alongside, keyed by (path identity, method, format, policy).

## 4. Tests

- `RfCore.Tests`: `SnpInterpolator.Evaluate(f)` for every f in a target grid equals
  `Interpolate(snp, grid)`'s matrix at that f **exactly** (`Assert.Equal` on the `Complex`, no
  tolerance), for RI and MA formats, linear/cubic/makima, and with targets outside the range under
  both policies. One test that the out-of-range warning fires once per interpolator, not per call.
- `Engine.Tests/Linear/SnpStampTests`: add a case that runs `SParameterEngine.Run` twice on one
  netlist and once on a fresh elaboration and asserts the three `S` cubes are bit-identical
  (guards the cache sharing an SNP between models).
- Cache invalidation: write a temp `.s2p`, run, overwrite it with different data (touching mtime),
  run again — the second result reflects the new file. Use a temp directory, never `testdata/`.
- Existing `Hero1Tests`, `SnpStampTests`, `SnpRelativePathEngineTests`, `SnpMissingFileMessageTests`,
  `ParametricSweepDcSParamTests` unchanged and green.

## 5. Gates

`dotnet test tests/RfCore.Tests`, `dotnet test tests/Engine.Tests`, `dotnet test tests/Core.Tests`
green, run ONCE, failures read from the TRX. Measure before/after with a scratch harness (a
console project referencing `src/Engine`, Release build, not a `Category=Benchmark` test): the
20-SnP ladder above should land near the no-SnP ladder's per-point cost (target ≥ 20× on that
fixture) and its allocation per point should drop by at least two orders of magnitude. Report the
numbers you got, not the ones here.

## 6. On completion

Findings — before/after per-point time and allocation on the SnP ladder and on Hero 1, the
`Interpolate` call sites that were NOT converted and why, and anything that mutated a cached `SNP`
— to **`src/Core/RESOLVED.md` §SP-P1** (RfCore-side notes in the same section; RfCore has no
RESOLVED.md and does not need one for this). **Never to any `CLAUDE.md`.** Do not commit; the
owner commits.

## 7. Out of scope, deliberately

The MNA assembly (SP-P2) and the frequency loop (SP-P3). `SToZ` per point (a 2×2–4×4 inverse,
microseconds). Interpolating in Z or Y instead of S — a behaviour change, not a speed-up. The
Data Display's own use of `Interpolate` (one call per plot, already batched).
