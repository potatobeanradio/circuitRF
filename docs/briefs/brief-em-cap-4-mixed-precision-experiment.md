# Brief CAP-4 — EXPERIMENT: factor in single precision, answer in double

Series: `brief-em-cap-0-overview.md`. Depends on CAP-2's lower-triangle storage. **This is an
experiment with a decision gate.** Its outcome may be "do not ship", and that is a result to record
with the measurements that decided it, not a failure.

## The idea

Store and factor the lower triangle in **complex single** (8 bytes per stored entry, so ~4·N² bytes
for the triangle — half of CAP-2), then recover a double-precision solution by iterating against a
double-precision operator. If it holds, a 16 GB machine reaches **~60,000 unknowns**, and the
factorisation itself runs faster (single-precision arithmetic, half the memory traffic).

**Known risk, stated up front:** MoM impedance matrices become ill-conditioned at low frequency (the
vector- and scalar-potential terms separate by orders of magnitude as ω → 0), and the factorisation
is unpivoted. A single-precision factor can be too poor a preconditioner there, and the iteration then
fails to converge. That case is gate E3 below, and it is the most likely way this experiment ends.

## What to build

1. **A complex-single lower-triangle type** with CAP-2's block-column layout. .NET has no built-in
   complex single; use a two-`float` struct and vectorise the trailing update with
   `System.Numerics.Vector<float>` (measure against a scalar version — vectorisation is where the
   speed comes from).
2. **The fill computes in double and rounds into single** panel by panel. No full double matrix is
   ever resident.
3. **The double-precision operator for the residual** — Z·x in double without storing Z. Two
   candidates; build (a), try (b):
   - **(a) Re-fill on demand.** Recompute Z in bounded row panels (as `PlanarFillMerged` already
     does) and apply each panel to x. Exact and general — multi-level, vias, merged, everything the
     dense path accepts. Cost: one fill per iteration (6.5 s at N = 11,210 against a 37 s factor).
   - **(b) The AIM operator** (`PlanarAimOperator`, `PlanarBorderedAimOperator`). Far cheaper per
     product, but its own approximation error bounds the attainable accuracy, so it converges to the
     AIM answer, not the dense one. Measure that difference; it is only acceptable if inside E1's
     tolerance.
4. **The iteration: GMRES preconditioned by the single factor** (`PlanarGmres`), not plain
   iterative refinement — GMRES survives a weaker preconditioner. Converge to a backward error of
   1e-10 or fail by name; a non-converged solve throws, as `PlanarAimOperator.Solve` already does.
   Each port column is a right-hand side; batch them so one operator pass serves all ports.
5. **A switch, off by default**, in `PlanarFillSettings` (and persisted in the `.cem` only if the
   decision gate says ship). The accounting (`ResidentBytes`, `CeilingFor`) must use the single
   figure when it is on.

## Experiment gates

- **E1 Accuracy:** against the double path, `|ΔS| ≤ 1e-6` on the S-parameter heroes; the hero
  tolerance from the PRD. Judge small entries by `|ΔS|`, not dB.
- **E2 Breadth:** the same on a multi-level via mesh, a merged mesh, a de-embedded run, and a
  lossy-conductor run.
- **E3 Low frequency:** a short line from 10 MHz to the band top. Report iterations per point across
  the band. Converges everywhere → pass. Fails below some frequency → record the frequency and
  whether falling back to the double path below it is clean.
- **E4 Cost (scratch harness, recorded):** at N = 11,210 and at the largest N that fits in double
  on the test machine, report per point: factor time, iterations, operator time, total, against the
  double path. The single path must be **no slower in total** at the larger N to be worth shipping.
- **E5 Capacity:** one frequency point at an N that does not fit in double on the test machine but
  does in single, peak working set measured and recorded.

## Decision

Record in `src/Engine/Mom/RESOLVED.md` and stop for the owner:
- **Ship** (switch on above the N where double no longer fits, or on always) — only if E1–E5 pass.
- **Ship with a low-frequency fallback** — if only E3 fails, and the fallback point is clean.
- **Do not ship** — with the numbers. Leave the code behind the off switch only if it is small and
  tested; otherwise remove it.

Targeted tests: one per claim — the single type's round-trip, the factorisation on a known small
matrix, the GMRES path agreeing with the double path on a small mesh, the throw on non-convergence,
and the accounting. Do not add timing tests.
