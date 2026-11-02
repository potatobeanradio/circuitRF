# Brief TO-7 — Global and derivative-free algorithms, and minimax

**Series:** `brief-tuneopt-0-overview.md` (D13) · **Tag:** `R-to7-<m>` · **Depends on:** TO-6
**Area:** `src/Engine/Optimization/` only (plus the algorithm list the UI and CLI read)

---

## 1. Goal

Add the algorithms that find answers the local methods cannot: from a bad start, on a multi-modal cost, or where the
simulator is noisy or occasionally fails. All on TO-6's ask/tell shape, all in-house, MIT, written from the published
descriptions — no GPL or copyleft code read into the tree.

## 2. Requirements

Each algorithm: an id, a menu label (D13), a one-line "use when" sentence (the UI tooltip and the MCP reference read
it — TO-11), its options with documented defaults, seeded randomness carried in its state.

**R-to7-1 — Differential evolution** (menu: "Differential evolution"; the genetic-family entry). Success-history
parameter adaptation (the SHADE family) with linear population-size reduction; current-to-pbest mutation, binomial
crossover, bound repair by midpoint-to-parent. Batch = one generation.

**R-to7-2 — Particle swarm.** Constriction-factor form, ring or global topology (option), velocity clamping, bound
handling by reflection. Batch = one swarm step.

**R-to7-3 — CMA-ES.** Standard (μ/μ_w, λ) with cumulative step-size adaptation; boundary handling by penalty or
re-sampling (document the choice); **restarts with increasing population** (IPOP) on stagnation, as an option on by
default. Batch = λ.

**R-to7-4 — Trust-region model** (derivative-free, Powell family). Builds an interpolating quadratic model from as few
as 2n+1 points, bound-constrained, with a trust radius that shrinks to a documented minimum. Excellent on smooth,
expensive costs with 2–20 variables. Batch = 1 (or the initial 2n+1).

**R-to7-5 — Pattern search** (mesh adaptive direct search). Poll on a positive spanning set, mesh refinement and
coarsening; tolerant of failed evaluations and discontinuities (an HB that fails to converge at some points). Batch =
the poll set.

**R-to7-6 — Minimax.** On the residual vector, minimize the **largest** weighted violation: sequential linear
programming in a trust region on a finite-difference Jacobian (batched as LM's). This is the method for equiripple
filter responses and worst-case specs. It requires the minimax cost form, and selecting it sets it.

**R-to7-7 — Algorithm registry.** One list (id, label, use-when, options schema, which cost forms it accepts, whether
it needs gradients) read by the Optimizer window, the CLI `--algorithm` help, `check`, and the MCP reference — no
second list anywhere.

**R-to7-8 — Complex values (overview D18; amended 2026-10-07).** No algorithm sees a complex number: the parts are
ordinary coordinates (TO-6 R-to6-12). Every algorithm here must accept TO-6's **infeasible** results — population
methods rank them (DE's selection, PSO's personal best and CMA-ES's ranking never prefer one over a feasible point),
pattern search treats one as a failed poll, the trust-region model leaves it out of its interpolation set, Minimax
treats it as a failed step. The registry entry (R-to7-7) gains nothing.

## 3. Gates (minimal; run only these classes)
- Each population method (DE, PSO, CMA-ES) reaches the global optimum of a 4-D Rastrigin within tolerance, with a
  **fixed seed**, inside a stated evaluation budget. One seed, one test each — not a statistics study.
- Trust-region model and pattern search reach a 4-D Rosenbrock optimum; pattern search does so with 10 % of points
  forced to fail.
- Minimax: an equal-ripple target on a hand-made residual set lands with the two worst residuals equal within tolerance.
- `AlgorithmRegistryTests` — the registry, the CLI help text and the MCP reference list agree.
- Evaluation counts, never times.
- One of the population tests reruns with a complex VAR's `real`/`mag` pair and a ring of infeasible points; the
  best point is feasible and the infeasible count is reported.
