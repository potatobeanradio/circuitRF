# Phase 3 Follow-up — SDD Whitespace, log10, Convergence Diagnostics & DcBiasStepping (Claude Code / Sonnet)

**Context:** Phase 3 is complete and passing (AD engine, SDD device, nonlinear-DC Newton; hero
converges to vds ≈ 47.018 V, i2 ≈ 49.12 mA). This is a small follow-up: two language/parser fixes, a
convergence diagnostic (already partly answered — see Task 3), and one architecture fix to how
continuation engages. The Newton *machinery* is healthy (verified: ~3.4 iterations per continuation
step, super-quadratic); the change in Task 4 is about *when* continuation runs, not how Newton works.

> Reads with: `docs/design/nonlinear-dc.md` (§2.3, §4), `docs/design/expressions.md` (§7),
> `docs/design/harmonic-balance.md` (§11 — the reserved `DriveStepping` note), `src/Core/CLAUDE.md`.

## Task 1 — Allow whitespace in SDD expressions (multiple assignments per line)
Remove the no-whitespace restriction on SDD `.cnl` expressions — users want readable equations.
- The expression parser already handles whitespace; the limit is the `.cnl` *line* parser splitting
  on spaces. Fix: once an `I[p,w]=`/`Q[p,w]=` assignment is seen, its right-hand side is an
  expression that **may contain whitespace**, handed intact to the expression parser.
- **Multiple assignments per line allowed** (VendorA-style). The boundary between one assignment's
  expression and the next is **the next `I[p,w]=` or `Q[p,w]=` token at bracket-depth zero** (not
  inside parens/brackets). Split on that boundary, hand each RHS (whitespace and all) to the parser.
- Support `\` line-continuation for long lines.
- Update `src/Core/CLAUDE.md` (remove the no-whitespace note; document the rule).
- Tests: spaced expressions parse identically to unspaced; multiple assignments per line parse;
  a variable/function containing the letter `I` (e.g. `Idss`) is NOT mis-split — the boundary is
  `I[<digits>,<digits>]=` at depth zero, not any `I`.

## Task 2 — Add `log10` to the expression engine + AD
- `log10(x)` = base-10 log. Value `ln(x)/ln(10)`; AD derivative `1/(x·ln(10))`. One derivative-table
  entry (same pattern as `ln`).
- **Do NOT add bare `log`** (base ambiguous across tools). `ln` (natural) and `log10` (base-10) only.
- Update `expressions.md` §7 and `nonlinear-dc.md` §2.3.
- Tests: `log10(1000)==3`, `log10(1)==0`; AD of `log10` matches finite-difference.

## Task 3 — Convergence diagnostic (DONE — keep the feature)
The convergence trace was already added and reported: the hero's "~40 iterations" was 68 total
across 20 source-stepping steps = ~3.4 iters/step, λ=1, super-quadratic — **healthy, no Newton fix
needed**. **Keep the convergence trace as a permanent diagnostic feature** (residual norm ‖F‖ and
update norm ‖ΔV‖ per iteration; iterations per continuation step; λ per step) — it's the nonlinear
analog of the Phase-2 singular-node reporter and will be needed for Phase-4 HB debugging. No further
diagnostic work required here.

## Task 4 — Make DC bias-stepping conditional: the `DcBiasStepping` setting
The Task-3 trace revealed the solver **always source-steps** the DC supplies (20 steps), even though
the hero clearly didn't need it (each step converges in ~3 iterations — a direct solve would too).
Source-stepping should be a **fallback for when the direct solve fails**, not the unconditional
default. Make it a tri-state setting, mirroring the regularization knobs:

- **`DcBiasStepping`** `{ IfNecessary, Always, Never }`, **default `IfNecessary`**:
  - **`IfNecessary`** (default): attempt the **direct** Newton solve first (cold start, or warm-start
    from a prior solution if available); only if it fails to converge within the max-iteration cap,
    **fall back to ramping the DC supplies** (the current 20-step behavior) and retry. Easy circuits
    (like the hero) pay nothing; hard circuits are rescued automatically.
  - **`Always`**: always ramp the DC supplies from a fraction up to full bias (the current behavior —
    for a known-difficult circuit where a failed direct attempt isn't worth trying).
  - **`Never`**: direct solve only; on failure, error with the diagnostic (validation/debugging mode).
- Expose the **ramp step count** (currently 20 at 5%) as a setting too.
- **Naming matters:** call it `DcBiasStepping` (it ramps the DC *bias supplies*) — NOT a generic
  "SourceStepping." Phase 4's HB gets a *separate* `DriveStepping` setting that ramps the RF *drive
  power* (reserved in harmonic-balance.md §11). Do not build `DriveStepping` now (no HB yet); just
  don't name this one in a way that collides with it.
- This is the same `{IfNecessary, Always, Never}` tri-state as `ConductanceRegularization` /
  `InductanceRegularization` — keep the pattern consistent.
- Update `nonlinear-dc.md` §4.3 (continuation) and the relevant CLAUDE.md.
- Tests: the hero converges under `DcBiasStepping=IfNecessary` via a **direct solve, no ramp** (should
  now take ~3–6 total iterations, not 68); `Always` reproduces the ramped path; `Never` errors
  cleanly on a circuit that needs a ramp (if you can construct one) or at least direct-solves the hero.

## Task 5 — Expose convergence settings (advanced users)
Expose alongside gmin and the regularization flags:
- absolute residual tolerance (default **1e-6**), optional relative tolerance, max-iteration cap.
- Document in the relevant CLAUDE.md.

## Acceptance
1. SDD expressions accept whitespace; multiple-per-line parse; no mis-split on stray `I` (Task 1).
2. `log10` works, AD matches FD (Task 2).
3. Convergence trace retained as a feature (Task 3 — already done).
4. `DcBiasStepping` tri-state works; **hero converges directly (no ramp, ~3–6 iterations) under the
   `IfNecessary` default** (Task 4).
5. Convergence tolerance + max-iter exposed (Task 5).
6. `dotnet build`/`dotnet test` green; Phases 1–3 tests still pass; hero still lands at
   vds ≈ 47.018 V, i2 ≈ 49.12 mA.

## Guardrails
- The whitespace boundary is `I[<digits>,<digits>]=`/`Q[...]=` at bracket-depth zero — never a stray
  letter `I` inside an expression.
- `DcBiasStepping`, not a generic name — `DriveStepping` is Phase 4's separate HB knob.
- Keep the tri-state pattern identical to the regularization settings.
- Flag any design question; don't improvise.