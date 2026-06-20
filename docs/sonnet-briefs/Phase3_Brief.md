# Phase 3 — Implementation Brief: Nonlinear DC, AD & the SDD (Claude Code / Sonnet)

**Goal:** bring up the nonlinear engine on the **DC-only** problem. Implement the `Evaluate` contract,
the automatic-differentiation engine, the SDD device, and the nonlinear-DC Newton solver — validated
by the **Phase-3 hero** (a grounded-source GaN HEMT operating point). No FFT, no harmonics, no
conversion matrix — those are Phase 4. This phase proves the nonlinear foundation in isolation.

> Read first, in order: root `CLAUDE.md`, `src/Core/CLAUDE.md`, `src/Engine/CLAUDE.md`, then
> `docs/design/nonlinear-dc.md` (the whole note — authoritative), `docs/design/expressions.md` (§6
> value model, §12 AD tiers), `docs/design/harmonic-balance.md` (§4 the (i,q,dg,dc) contract, §10–§11
> nonlinear-DC-as-seed, extended-domain/clamp-warn). Where this brief and a design note disagree, the
> design note wins — flag, don't guess.

## Prerequisite (done)
Phases 1–2 complete: expression engine + elaboration + `.cnl` reader (Phase 1); linear engine (MNA,
DC, S-parameters), RfCore, Hero 1 (1e-6) and Hero 1B (scale) all pass (Phase 2).

## Scope — build these, in this order (each step gated)

### STEP 1 — The AD engine (foundation; do first)
- **Make the Phase-1 expression evaluator generic over its scalar type** (nonlinear-dc §2.2): it runs
  on `double` (plain resolution — existing behavior, must not regress) and on a new `Dual` type (AD).
  ONE tree-walk, two scalar types — value and derivative share the exact code path so they cannot
  drift. This refactors existing Phase-1 evaluator code; keep all Phase-1 tests green.
- **`Dual` type** (nonlinear-dc §2.1, §2.6): value + a **fixed-size N-wide gradient** (N = port
  count), **allocation-free** (stack/by-value, no per-op heap allocation — the HB hot path will
  demand it). Forward-mode: seed each independent variable with a unit gradient in its slot.
- **Derivative table** (§2.3): arithmetic (`+ − × ÷ ^`) and the function set (`exp`, `ln`, `tanh`,
  `sqrt`, `sin`/`cos`, `abs`) each with its value-and-derivative. Small, closed set.
- **Numerical robustness** (§2.5): overflow-safe `exp` and the `ln(1+exp(x))` softplus (guarded form
  for large/very-negative x, value AND gradient consistent); `ln`/`sqrt` domain errors **clamp + emit
  an obvious user-facing warning** (naming model + operation), never hard-fail; non-diff points
  (`abs`, `if`) take the active-branch derivative.
- **FD cross-check** (§2.4): a finite-difference of the same expression, used as the AD oracle (and
  later as the fallback tier).
- **Gate:** AD of the hero `i2` equation at (v1=−3.05, v2=48) matches central finite-difference to
  ≥4 sig figs: **gm = ∂i2/∂v1 ≈ 62.4 mS, gds = ∂i2/∂v2 ≈ −9.45 µS** (note: gds is NEGATIVE — correct,
  see §5.3, not a sign bug). Unit test, no solve.

### STEP 2 — The `Evaluate` contract + the SDD device
- **`Evaluate(in v) → (i, q, dg, dc)`** on `ComponentModel` (nonlinear-dc §1): `i`,`q` length-N real;
  `dg`,`dc` N×N real. `q`/`dc` plumbed but return zero for a resistive device.
- **SDD device** (§3) — a `ComponentModel` whose behavior is user-authored expressions, evaluated in
  dual arithmetic (reuses the Phase-1 cached AST, now run on `Dual`).
- **`.cnl` SDD line** (VendorA-compatible grammar): `SDD:Name <2N nets> I[p,w]=<expr> ...`
  - **2N nets in +/− pairs**: `(p1+, p1−, p2+, p2−, …)`. Port voltage `_vp = v(p+) − v(p−)`. A `−`
    net of `0` grounds that port's return. (NOTE: this is 2N nets in pairs — DIFFERENT from the SnP
    block's N-or-N+1 shared-reference convention. Do not conflate.)
  - Port-voltage variables in the expressions are named **`_v1`, `_v2`, …** 
  - **`I[p,0]`** = the conductive **current** equation → `i` (and `dg = ∂i/∂_v` via AD). **Supported.**
  - **`I[p,1]`** = the **charge** equation (×jω in freq domain → current) → `q` (and `dc = ∂q/∂_v`).
    **Supported and parsed**, evaluated into `q`/`dc` via the same AD path. The **nonlinear-DC solver
    drops it** (jω = 0 at DC, so the charge term vanishes) — this exercises the charge plumbing while
    DC physics correctly ignores it. (The hero is resistive — no `I[p,1]` — but build the path.)
  - **`w ≥ 2`** (user weighting `H[w]`) → **HARD-ERROR** ("SDD weighting w≥2 / H[w] not supported").
  - **`F[p,w]`** (implicit equation) → **HARD-ERROR** ("implicit SDD F[...] not supported; use I[...]").
  - **`C[n]`/`Cport[n]`** (current-controlled) → **HARD-ERROR** ("current-controlled SDD not supported").
  - **`In[p,w]`, `Nc[p,q]`** (noise) → store-as-string or skip; no warning (out of v1 scope, doesn't
    affect the solve).
  - Equation references parameters (`B`, `Sc`, `TV0`, …) by name — ordinary `.cnl` variables in scope.
- **Gate:** the hero SDD parses; `Evaluate` at the bias returns i2 = 49.11 mA, i1 = −61 mA, and the
  AD `dg` matches Step 1's gm/gds.

### STEP 3 — The nonlinear-DC Newton solver
- **Real, sparse, full-circuit Newton** (nonlinear-dc §4): residual `F(V) = G_linear·V + I_nl(V) −
  I_source`; Jacobian `J = G_linear + dg(V)` (the device's AD `dg` stamped at its nodes each
  iteration). Reuse the Phase-2 linear DC stamps for `G_linear` and the CSparse real sparse solve
  (symbolic-once, refactor per iteration).
- **`gmin` continuity** + the `IfNecessary/Always/Never` regularization (linear-engine §4.3) — every
  node has the gmin shunt so `J` stays non-singular when a device is pinched off.
- **Source-stepping continuation** (§4.3): walk supplies from a fraction up to target bias, each step
  seeded by the last converged V; step-halving backoff on max-iter; damping λ≤1.
- **Convergence:** `‖F‖ < 1e-6` (default) + small `‖ΔV‖`; max-iter cap.
- **CLI:** a headless nonlinear-DC operating-point command.
- **Gate — the hero:** the grounded-source GaN HEMT (SDD above) with gate DC source −3.05 V through a
  choke (or series R) and drain DC source +48 V through **Rd = 20 Ω**. Newton converges from a cold
  start to **vds ≈ 47.018 V, i2 ≈ 49.12 mA, i1 = −61 mA**. (The 20 Ω load makes vds a genuine
  self-consistent fixed point; gate vgs is set by the source = −3.05 V.)

## Acceptance gate (Phase 3 complete)
1. AD matches FD at the bias (gm ≈ 62.4 mS, gds ≈ −9.45 µS, ≥4 sig figs).
2. `Evaluate` returns the golden i2/i1 at the bias.
3. Nonlinear-DC Newton converges to the hero loadline point (vds ≈ 47.018 V, i2 ≈ 49.12 mA).
4. Robustness: an overshooting iterate driving the `exp` argument large does not NaN.
5. `dotnet build`/`dotnet test` green; **Phases 1–2 tests still pass** (the generic-evaluator refactor
   must not regress plain evaluation, elaboration, or the linear engine).

## Test fixtures (`testdata/`)
- The hero SDD `.cnl` (the GaN HEMT + bias circuit) — owner can provide or Sonnet drafts from §5 for
  owner review. Golden values are in nonlinear-dc §5.3 (verified by hand/MATLAB — no reference-tool
  run needed).
- Tiny SDD test circuits for the unit tests (a simple `I[1,0]=_v1/R` linear-SDD sanity case; a
  two-port with known derivatives).

## Guardrails
- **The generic-evaluator refactor (Step 1) touches working Phase-1 code** — keep every Phase-1 test
  green; the `double` path behavior must be identical.
- AD validated against FD is the most important test — if it's wrong, everything downstream silently
  fails. Do not skip it.
- `Dual` allocation-free from the start (Phase 4's hot path depends on it) even though Phase 3 DC
  doesn't stress it.
- Hard-error (don't silently ignore) on `F`/`C`/`Cport`/`w≥2` — these change the device's physics.
- Flag design questions to Opus/Chat; don't improvise.
- After the phase, update `src/Core/CLAUDE.md` (AD/SDD) and `src/Engine/CLAUDE.md` (nonlinear-DC
  Newton) to record what was built.