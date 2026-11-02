# Brief YA-2 — The sampling core: distributions, correlation, sampling plans, streams, batch evaluation

**Series:** `brief-yield-0-overview.md` (D2, D3, D5, D11) · **Tag:** `R-ya2-<m>` · **Depends on:** YA-1
**Area:** a new `src/Engine/Statistics/` (pure numerics), a new `src/Design/Statistics/` (entries → samples →
overrides), `src/Design/Optimization/OptimizationRun.cs` (the batch evaluator's public door),
`src/Design/Optimization/TunableOverrides.cs`, `src/Design/Optimization/ComplexValue.cs`

---

## 1. Goal

Given a design's statistical entries and settings, produce trial N's values — reproducibly, on any thread, in any
order — and evaluate a batch of trials through **the** evaluation service. No run orchestration (YA-4), no yield.

## 2. Requirements

**R-ya2-1 — Distributions (`src/Engine/Statistics`).** For each D2 distribution: inverse CDF from a standard
normal z (the copula form, D3), the CDF, mean and variance. Truncation samples the truncated law exactly
(Φ⁻¹ over the truncated probability interval), never by clipping. Φ and Φ⁻¹ to full double precision (Acklam
plus one Halley step, or Wichura AS241 — written in-house); the test compares against tabulated values, not
against itself. `discrete` maps z through its CDF to a rung. No domain types in this folder.

**R-ya2-2 — Correlation.** A Cholesky factor of the correlation matrix among the stat entries named by
`correlate` lines (others are independent and skip the factor). A matrix that is not positive definite is
repaired with Higham's nearest-correlation-matrix iteration; the repair and its largest element change are returned
for `check` (YA-1) and the run report to state. Correlation applies to z **before** each marginal's inverse CDF, so
it works across distributions (a Gaussian R correlated with a uniform C).

**R-ya2-3 — Sampling plans.** `random`: z from the stream (R-ya2-4). `lhs`: for N trials and each stream, a
stratified uniform u = (π_s(n) + v)/N with a per-stream permutation π_s and jitter v, both drawn from the stream,
then z = Φ⁻¹(u) — so LHS stays a pure function of (seed, N, trial, stream). `sobol`: a scrambled Sobol sequence
(direction numbers from the published Joe–Kuo table, embedded as data, with a citation; an Owen-style or random
digital-shift scramble seeded by the run seed), dimension assigned per stream in a **stable order** (sorted stream
id), so a design's dimension mapping does not depend on discovery order. Sobol over more dimensions than the table
holds falls back to `random` for the excess dimensions and says so.

**R-ya2-4 — Counter-based streams (D5).** A draw is `Hash(seed, trial, streamId, k) → uniform` (k for a stream
needing more than one number), built on the existing `SplitMix64` mixing so it is identical on every platform. The
stream id is a stable 64-bit hash of the entry's **key text** (`DUT:R3.R`, `mag(ZL)`) or, for kit draws (YA-3), of
the instance path and parameter. Gate: trial 37 drawn alone equals trial 37 drawn in a 100-trial batch on 8 threads;
adding an unrelated stat entry leaves every other entry's `random` draws unchanged.

**R-ya2-5 — Samples to values (`src/Design/Statistics`).** `StatisticalSample` = the trial number and its z-vector
by stream. `SampleValues.Apply(entries, nominals, sample)` → the value text for each stat entry: percent spreads
relative to the **given** nominal (so a moved nominal moves the draw with it), absolute spreads in the parameter's
unit, integers rounded, a complex part composed into the whole value with `ComplexValue.Compose` in the schematic's
form (tuning D18). A non-physical value (≤ 0 where the unit forbids it, a lognorm of a non-positive nominal) is
returned as a **refusal for that trial**, never clamped. `sigmascale` multiplies every spread.

**R-ya2-6 — The public batch door.** `OptimizationRun.EvaluateBatch` is internal and takes unit-box points
(tuning §19). Add a public entry that takes **value maps** (key → value text) and returns per point the `DataSet`
(optionally discarded after scoring), each goal's violation, pass and margin (`GoalScore.Margin`), and a status
with a reason — **over the same method**, not a second evaluator. Its cache keys by decoded value text as today. It
honours `NotReentrantReason` (serial with the existing note) and a `CancellationToken`. The optimizer keeps calling
the unit-box form, which becomes a thin decode over the new one.

**R-ya2-7 — Nothing nominal changes.** With no stat entries, or with `stat=0` everywhere, no code path in this
phase runs during Simulate, Tuning or Optimization; existing tests for those stay green untouched.

## 3. Not in this phase
Kit distribution functions (YA-3), the run loop, auto-stop, the result DataSet (YA-4), any UI.

## 4. Gates (minimal tests, run only these classes)
- `DistributionTests` — Φ⁻¹ against tabulated quantiles to 1e-12; each distribution's sample mean and variance over
  a fixed-seed 20,000-draw sample within 4 standard errors; truncation leaves no draw beyond ±k σ and no pile-up at
  the edge (the edge bin's frequency within 4 SE of its exact probability).
- `CorrelationTests` — the sample correlation of a ρ = 0.8 Gaussian/uniform pair within 4 SE; a non-PD matrix is
  repaired, reported, and the repaired matrix is PD.
- `SamplingPlanTests` — LHS puts exactly one draw per stratum per stream; Sobol's first points (unscrambled) match
  the published sequence; scrambled is a pure function of the seed.
- `StreamReproducibilityTests` — R-ya2-4's two claims.
- `SampleValuesTests` — percent vs absolute against a moved nominal; integer rounding; a complex pair composed in the
  schematic's form; a non-physical draw refused.
- `BatchEvaluateTests` — value-map evaluation of the L-section equals Simulate with the same values typed; the
  optimizer's existing `OptimizationRun` tests pass unchanged (run those classes only).
