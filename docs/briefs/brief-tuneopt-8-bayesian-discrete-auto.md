# Brief TO-8 — Bayesian optimization, preferred values, Auto, sensitivity

**Series:** `brief-tuneopt-0-overview.md` (D4, D13) · **Tag:** `R-to8-<m>` · **Depends on:** TO-7
**Area:** `src/Engine/Optimization/`, `src/Design/Smith/SmithPreferredValues.cs` (generalized),
`src/Ui/Smith/SmithPreferredValueStore.cs` and its dialog (shared), `docs/design/smith-chart.md` §5.6a

---

## 1. Goal

The slow-but-thorough option the owner asked for, the step that turns an optimum into a buildable design, a sensible
default for a user who does not know which algorithm to pick, and a readout of which variables matter.

## 2. Requirements

**R-to8-1 — Bayesian** (menu: "Bayesian (slow simulations)"). Gaussian-process surrogate (Matérn 5/2, ARD length
scales fitted by maximum likelihood with NumFlat), expected improvement maximized over the unit box by multi-start
local search; initial design = Latin hypercube of 2n+1 (option). Above ~10 variables, a **trust-region** variant (local
GP in a box that grows on success and shrinks on failure; restart when it collapses). Batch = 1 by default, q points
by a constant-liar heuristic when parallelism > 1. Its own cost is real (O(N³) in evaluations): cap the archive
(option, default 500) and say so in its "use when" line. For HB, loadpull and EM benches where each run takes seconds
or more.

**R-to8-2 — Preferred values, generalized from the Smith Chart.** Move the ladder arithmetic out of
`SmithPreferredValues` into a shared `PreferredValues` (in `src/Design`) that the Smith Chart keeps using unchanged
(its tests are the gate that nothing moved), and add a **resistor** ladder (E24 by default, E96 available) beside the
existing capacitor and inductor ladders. The ladders stay **per-user** in the same preference store and are edited in
the same dialog (now with an R tab) — the Smith Chart's reason holds: the document must not carry somebody else's parts
drawer. A tunable entry with `discrete=preferred` snaps to the ladder of its quantity (R, L, C by the parameter's unit);
an entry whose quantity has no ladder cannot select `preferred` (the option is absent).

**R-to8-3 — Discrete** (menu: "Discrete"). For a problem where every opt variable is integer or preferred: exhaustive
grid when the product of choices is ≤ a cap (option, default 2,000), otherwise a coordinate-wise discrete descent with
random restarts. Batch = a block of grid points.

**R-to8-4 — Snap and polish.** After any continuous run, an Optimizer action (TO-10) and a CLI flag (TO-11):
snap every `preferred`/`integer` variable to its nearest legal values, evaluate the 2^k neighbours for the k snapped
variables when k ≤ 6 (else nearest only), keep the best, then re-optimize the **continuous** variables with
Levenberg–Marquardt from there. Reports the cost before and after snapping.

**R-to8-5 — Auto.** CMA-ES with a modest budget (documented) → Levenberg–Marquardt polish from its best point (or
Minimax polish when the cost form is minimax) → snap-and-polish when any variable is discrete. Auto's progress events
name the stage.

**R-to8-6 — Sensitivity.** On demand at the current best point: one batched finite-difference pass (n evaluations,
cached ones free) giving each variable's normalized ∂cost/∂x and, per goal, which variable moves it most. A column in
the Optimizer's variable list and a field in the CLI/MCP result. No new simulation is triggered without the user (or
caller) asking.

**R-to8-7 — Complex values (overview D18; amended 2026-10-07).**
- **Discrete is not offered on a part.** A part is continuous: `discrete=integer`/`preferred` is absent from a part
  row's choices and is a `check` error in a hand-written line; `step=` stays allowed. A problem whose only
  opt variables are parts cannot select **Discrete** (the menu entry is disabled with that reason).
- **Bayesian** fits its GP over the part coordinates; infeasible points are not observations — they shrink the
  trust region (the trust-region variant) or are excluded from the acquisition's feasible box sampling.
- **Snap and polish** leaves parts alone (nothing to snap) and re-optimizes them with the continuous variables.
- **Sensitivity** is reported per part coordinate (∂cost/∂mag(ZL) and ∂cost/∂phase(ZL)), never per whole value.

## 3. Gates (minimal; run only these classes)
- Bayesian reaches a 2-D Branin minimum within tolerance in ≤ 40 evaluations with a fixed seed (evaluation count).
- `PreferredValuesTests` — the Smith Chart's existing preferred-value tests pass untouched; R snapping to E24 and E96.
- `SnapAndPolishTests` — an L-section optimized continuously, then snapped to E24 C and an inductor ladder, ends at a
  ladder pair whose cost is the best of the neighbour set.
- `AutoStagesTests` — Auto on the L-section emits the stage sequence and finishes with all goals met.
