# Brief TO-6 — The optimizer core and the first four algorithms

**Series:** `brief-tuneopt-0-overview.md` (D10–D17) · **Tag:** `R-to6-<m>` · **Depends on:** TO-2 (TO-1 for the model)
**Area:** new `src/Engine/Optimization/` (pure numerics), new `src/Design/Optimization/` orchestration
(beside TO-1's model), tests in `tests/Engine.Tests/Optimization/` and `tests/Ui.Tests/Optimization/` (or wherever
`src/Design` is tested today)

---

## 1. Goal

A headless optimizer the GUI, the CLI and MCP all drive, with the machinery every algorithm shares built once, and
four algorithms on it: **Random**, **Nelder–Mead**, **Levenberg–Marquardt** ("Gradient"), **BFGS-B** ("Quasi-Newton").

## 2. The shape — ask/tell

Every algorithm is a resumable state machine over the **unit box** [0, 1]ⁿ:
```
interface IOptimizerAlgorithm {
    IReadOnlyList<double[]> Ask();               // a batch of points to evaluate (1 for NM, n+1 for an FD Jacobian, λ for a population)
    void Tell(IReadOnlyList<Evaluation> results); // cost, residual vector, failed flag, per point
    bool IsFinished { get; }  string? FinishReason { get; }
    OptimizerState Capture();  void Restore(OptimizerState s);  // pause/resume and later checkpointing
}
```
This one shape gives pause (stop asking), parallel evaluation (evaluate a batch concurrently), deterministic replay
(seeded RNG is part of the state) and yield later (batches of samples). No algorithm calls the simulator.

## 3. Requirements

**R-to6-1 — Variable transform.** `src/Design` maps each opt-enabled entry to one unit-box coordinate: linear or
logarithmic per D4 `Scale`; integers and steps are applied when a point is **decoded** (so the cache and the history
see the value actually simulated). Decoded values are formatted as the parameter's value text.

**R-to6-2 — Goal residuals (D10, D11).** For each enabled goal, evaluate its expression (TO-2 R-to2-2 extra
expressions) on its analysis's result, restrict to the axis range, and compute a **violation per grid point**:
`le`: max(0, x − L); `ge`: max(0, L − x); `eq`: |x − L|; `in` [a, b]: distance to the interval; `out` [a, b]:
distance to the nearer edge when inside, else 0. Sloped limits interpolate L linearly along the axis. A complex result
is an error naming `dB`/`mag`/`phase`/`real` (the `max_over` rule).

**R-to6-3 — Normalization (define, document, test).** Divide each violation by the goal's **scale** and multiply by
its weight. Proposed default scale: `in`/`out` → b − a; otherwise max(|L|, 1) in the expression's own unit; a goal may
override it (`scale=` in the directive; TO-1 adds the key). Divide each goal's residuals by √(points) so a 201-point
goal and a scalar goal weigh alike. The residual vector is the concatenation; **least squares** cost = Σ r², **minimax**
cost = max r. Record the final rule in the design note — users will read it.

**R-to6-4 — Evaluation failures.** A non-converged or failed evaluation is a result with `Failed = true` and a finite
penalty cost larger than any successful cost seen (so ranking still works); algorithms must handle it (LM and BFGS-B:
shrink the step). Count failures; if **every** evaluation fails, the run ends with exit-code-2 semantics (D15).

**R-to6-5 — Cache.** Results are cached by the decoded value vector. A repeated point costs nothing (counter).

**R-to6-6 — Parallel evaluation.** A batch is evaluated concurrently up to `parallelism` (default: cores − 1, capped
by batch size), only when TO-2's service reports the design reentrant (R-to2-5); otherwise one at a time and the run
notes why once.

**R-to6-7 — Stopping.** Max iterations, max evaluations, time limit, **all goals met** (cost = 0), stall (relative
best-cost improvement below a tolerance over k iterations; defaults documented), user Stop. `FinishReason` says which.

**R-to6-8 — Pause/resume/stop (D16).** Pause = finish the batch in flight, then hold; Resume continues from the held
state; the run is identical (same evaluations, same order) to an unpaused run with the same seed — that is a test.

**R-to6-9 — Progress events.** After every iteration: iteration, evaluations, failures, cache hits, elapsed, current
and best cost, best decoded values, **per-goal** worst violation and pass/fail at the best point, railed variables
(D17) with which end. The GUI and MCP consume the same event.

**R-to6-10 — History.** The run returns a `DataSet` group `opt` with cubes over an `eval` axis (cost, failed flag,
each variable's value) and over an `iter` axis (best cost, each goal's worst violation), so a convergence plot is an
ordinary Data Display trace. Callers decide whether to write it.

**R-to6-11 — Algorithms in this phase.**
- **Random** — uniform or Latin hypercube in the unit box (option), batch size option.
- **Nelder–Mead** — adaptive coefficients by dimension; bound handling by projection; restart on collapse (option).
- **Levenberg–Marquardt** — on the residual vector; forward-difference Jacobian evaluated as one batch of n points
  (step in unit-box space, documented default, option); bounds by projection with active-set handling; geodesic
  acceleration not required. Minimax cost is refused for LM (it is a least-squares method) with a sentence naming
  Minimax (TO-7) and Auto.
- **BFGS-B** — bound-constrained quasi-Newton on the scalar cost (limited-memory form), gradients by the same batched
  finite differences, projected line search.

**R-to6-12 — Complex values (overview D18; amended 2026-10-07).**
- **Coordinates.** Each opt-enabled part of a complex value is one unit-box coordinate with its own range and scale
  (a phase is always linear). Decoding groups a value's coordinates and composes the whole with
  `ComplexValue.Compose` from the value's start: one part holds its partner at the start; two of one system set it;
  a mixed pair is solved geometrically, the free sign from the start. The cache and the history see the decoded
  **whole** value, and the decoded value text is written in the schematic's form.
- **Infeasible points.** A decoded point no complex value satisfies, or one outside the range of ANY entry of that
  value (tune-only entries included — a range belongs to the entry), is **infeasible**: not simulated, counted
  separately from failures (progress event field), and given a penalty cost larger than every feasible cost seen plus
  its normalized distance to the region, so ranking pushes toward feasibility. LM and BFGS-B treat it as R-to6-4's
  failed step (shrink); Random and Nelder–Mead simply rank it.
- **Refusal.** More than two opt-enabled parts of one value refuses the run before any evaluation: "real(ZL),
  imag(ZL) and mag(ZL) are three parts of ZL, which has two degrees of freedom; optimize at most two — the others'
  ranges still limit it." A start point outside the region is reported and moved to the nearest feasible point of
  the start's own coordinate paths (`ComplexRegion.Move` from a feasible seed), never silently.
- **Railed (D17)** on a part means at its own bound; a value held at the edge of ANOTHER part's range is reported as
  railed against that part, naming it.

## 4. Gates (minimal; run only these classes)
Pure numerics (`Engine.Tests`):
- Each algorithm reaches the optimum of a 2-D Rosenbrock in the box within tolerance from a fixed start (NM, LM on its
  residual form, BFGS-B) or gets within a looser tolerance in a fixed evaluation budget with a fixed seed (Random).
- `PauseResumeDeterminismTests` — paused-and-resumed run == uninterrupted run, evaluation for evaluation.
- A failing-evaluation region is stepped around (LM, BFGS-B).

Circuit (`src/Design`'s tests):
- `LSectionMatchTests` — a two-variable L-section match to 50 Ω at one frequency converges from a stated poor start,
  with LM and with NM, to the analytic L and C within tolerance.
- `InfeasibleGoalTests` — `|S21| ≥ 2` on a passive network ends with cost > 0, reports that goal unmet with its worst
  point, and exit semantics 3.
- `GoalResidualTests` — one per goal type, plus a sloped limit, on a hand-made cube.
- Assert **evaluation counts**, never times.
- `ComplexDecodeTests` — real + imaginary and magnitude + phase decode directly; real + magnitude decodes with the
  start's sign; an infeasible pair is not simulated (evaluation counter unchanged) and ranks last; three opt parts
  refuse with the sentence.
- `ComplexLSectionTests` — an L-section match whose load is a complex VAR optimized by `mag`/`phase` reaches the
  analytic answer with LM, every decoded point inside the parts' ranges.
