# Phase 4b-2 Enhancement — Selectable Search Method: add `IteratedQuadratic` (Claude Code / Sonnet)

**Goal:** add a second, more robust pursuit search algorithm — **IteratedQuadratic** (a trust-region
iterated-quadratic search) — alongside the existing, working **SteepestAscent** (Baylis). Make the search
method **selectable** via a `SearchMethod` enum, default to the existing `SteepestAscent` (it is committed
and passing; the owner does not expect to change it). The enum must be open to future additional methods.

> Read first: `docs/design/loadpull_pursuit.md` (§1.1, §1.1.1, §1.1.2, §1.3 — the algorithm, the as-built
> VSWR/Z mechanics, the known limitation this enhancement addresses, and the start-point seeding), and
> `src/Engine/Loadpull/PursuitEngine.cs` (the current SteepestAscent implementation — REUSE its helpers).
> Where this brief and the design note disagree, the design note wins — flag, don't guess.

## Context — why IteratedQuadratic
The existing SteepestAscent is a **fixed-direction line search**: it fits the gradient once per leg, ascends
along that ray, and on rejection only **shrinks the step along the same ray** — it never re-fits the gradient
mid-leg (loadpull_pursuit.md §1.1.2). On Hero 3B this overshot the true MXP (~80 Ω) to 84.5 Ω and relied on
the final polynomial refinement's best-cardinal fallback to recover 80.5 Ω. It works for the near-1-D PA
optima here, but can zigzag/stall on curved 2-D ridges. IteratedQuadratic fixes this by **re-fitting a local
quadratic at every iterate and jumping toward its analytic peak** — using curvature to re-aim each step.

**This is purely additive.** Do NOT modify or remove the SteepestAscent code path. It stays the default.

## Scope

### STEP 1 — `SearchMethod` enum (selectable, extensible)
- Add `public enum SearchMethod { SteepestAscent, IteratedQuadratic }` (room to grow — more methods later).
- Add a `SearchMethod` directive key to `loadpull_pursuit` (resolves through the expression engine /
  directive resolution like the other keys), default **`SteepestAscent`**. Thread it into `PursuitParams`
  and through `LoadpullPursuitEngine.Run` to select which engine path runs.
- `PursuitEngine.Run` dispatches on the method: existing code → `RunSteepestAscent` (rename the current
  `Run` body), new code → `RunIteratedQuadratic`. Both share the same signature
  `(Complex startZ, Func<Complex, double?> criterion) → PursuitResult` and the same result type.

### STEP 2 — `RunIteratedQuadratic` (the trust-region iterated quadratic)
A trust-region method that REUSES the existing helpers (`FindStepLength`, `FitQuadraticSurface`,
`SolveQuadraticOptimum`, `FitLinearPlane`, the mirror/non-physical guards). Algorithm:

1. **Seed** at `startZ` (the Tuner's Z[TuneHarm]/Z0 per §1.3 — already passed in by the caller). Score it.
   Abort with a clear message if the start is unscorable (same as SteepestAscent).
2. **Trust-region radius `R`** is a VSWR (≥ 1), initialized to `DsInitial`, same VSWR-excess shrink/grow
   rule as SteepestAscent (`R = 1 + (R−1)/3` to shrink). All step distances are exact VSWR via
   `FindStepLength` (Z-plane direction, no Γ, no Z0 — same as the current code).
3. **Iterate** (cap at `MaxAscentSteps`):
   a. Place 4 cardinal neighbours at radius `R` VSWR in ±Re(Z), ±Im(Z) (exact `FindStepLength`); score
      them. **Mirror** any unscorable cardinal through `curZ` (reuse the existing non-physical/mirror
      guard — no negative-R probes).
   b. Fit the local quadratic `ΔC = m1·Δx + m2·Δy + ½(m11·Δx² + 2m12·ΔxΔy + m22·Δy²)` over the center +
      cardinals (`FitQuadraticSurface`).
   c. `delta = SolveQuadraticOptimum(...)`:
      - **Negative-definite Hessian (a real maximum) AND `curZ+delta` within the trust region** (VSWR from
        `curZ` ≤ `R`, clamp to the boundary if it lands just outside) AND physical (Re>0): jump there,
        score it. **Improved** → accept (move `curZ`), and **grow `R`** modestly (model is trustworthy
        here, e.g. `R = 1 + (R−1)·2`, capped at `DsInitial`). **Not improved** → **shrink `R`**, retry.
      - **Hessian not negative-definite (saddle/indefinite — common away from the peak), or optimum
        outside the trust region:** fall back to a **gradient step** — use the linear part `(m1,m2)` of the
        SAME fit as the ascent direction, step `R` VSWR along it (exact `FindStepLength`), score. Improved →
        accept; not → shrink `R`. (This is exactly the SteepestAscent behavior, so IteratedQuadratic
        degrades gracefully to gradient ascent where curvature isn't usable.)
   d. **Converge** when `R < ConvergenceThreshold` (VSWR near 1, e.g. 1.02 — same threshold as
      SteepestAscent).
4. Return the best-scored point seen as the optimum (track it across iterations — never return a point worse
   than one already queried).

### STEP 3 — query economy: the cache is mandatory and automatic (verify)
- The cache is in `LoadpullPursuitEngine.Run` (the `Cache` class, `CacheVswrTol = 1.02`), threaded through
  the `Query` delegate passed as `criterion`. So every `criterion(z)` the iterated search makes ALREADY
  goes through the cache — clustered iterates and re-placed cardinals that fall within `CacheVswrTol` of a
  prior query are cache hits (no new HB solve). **The new engine must not bypass this** — it only ever
  obtains scores via the `criterion` delegate (never calls the LoadpullEngine directly).
- **Verify and report** the query count for IteratedQuadratic on Hero 3B is comparable to SteepestAscent
  (target: within ~2× — the trust-region's per-iteration cardinals are largely absorbed by cache hits as
  `R` shrinks and iterates cluster). If query count explodes, the cache dedup isn't catching clustered
  cardinals — report it rather than shipping a query-hungry search.

### STEP 4 — tests
- **Both methods must pass the existing brute-force-vs-pursuit regression test** (pursuit MXP within the
  VSWR tolerance of the brute-force grid MXP) — run it for BOTH `SearchMethod` values.
- Add a test that IteratedQuadratic converges to the true Hero 3B MXP (~80 Ω) and a reasonable MXE, with a
  reported query count.
- The default (`SteepestAscent`) behavior must be byte-for-byte unchanged (its golden/regression still
  passes untouched).

### STEP 5 — incidental cleanup (you're already in the file)
- Remove the leftover `Console.WriteLine` debug lines in `LoadpullPursuitEngine.ExtractCriterion` (the
  `poutAboveDbm=… interpTo=…` and `effAbove=… interpTo=…` prints) — debugging leftovers. Keep the
  `Console.Error.WriteLine` diagnostic logging (that's intentional).

## Acceptance
1. `SearchMethod` enum (extensible) + directive key, default `SteepestAscent`; threaded through to engine
   dispatch.
2. SteepestAscent path unchanged and still passing (default).
3. `RunIteratedQuadratic` implemented as a trust-region iterated quadratic reusing existing helpers; jumps to
   the quadratic optimum where the Hessian is negative-definite, degrades to a gradient step otherwise,
   shrinks/grows `R` (VSWR) by the trust-region rule, converges at the VSWR threshold.
4. Cache used automatically (via the `criterion` delegate); IteratedQuadratic query count on Hero 3B
   reported and comparable to SteepestAscent (~≤2×).
5. Brute-force-vs-pursuit regression passes for BOTH methods; IteratedQuadratic lands at Hero 3B MXP ~80 Ω.
6. Debug `Console.WriteLine` leftovers removed from `ExtractCriterion`.
7. `dotnet build`/`dotnet test` green; Phases 1–4b still pass.

## Guardrails
- **Purely additive** — do not modify or remove the SteepestAscent path; it is the committed, passing default.
- The new engine obtains scores ONLY via the `criterion` delegate (so the cache applies automatically) —
  never call `LoadpullEngine` directly from `PursuitEngine`.
- Reuse the existing helpers (`FindStepLength`, `FitQuadraticSurface`, `SolveQuadraticOptimum`,
  `FitLinearPlane`, mirror/non-physical guards) — do not duplicate the VSWR/Z machinery.
- Distance is always exact VSWR (Z-plane direction, no Γ, no Z0) — consistent with the as-built mechanics.
- Diagnostics over grinding: if IteratedQuadratic doesn't reach Hero 3B MXP ~80 Ω, report the walk
  trajectory (use the existing `Log` writer) vs the brute-force surface — do not patch blindly.
- Update `src/Engine/Loadpull/CLAUDE.md` (and `loadpull_pursuit.md` §1 if the algorithm description needs a
  subsection for IteratedQuadratic) with the new method.
