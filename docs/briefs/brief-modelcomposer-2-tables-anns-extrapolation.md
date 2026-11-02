# Brief MC-2 — Runtime tables and ANNs, guided extrapolation, drain–source symmetry

**Series:** `brief-modelcomposer-0-overview.md` (D3, D4) · **Tag:** `R-mc2-<m>` · **Depends on:** MC-1
**Area:** `src/Core/Devices/Composed/` (evaluators, extrapolation, symmetry); the `.cmodel` schema
(`Extrapolation`, table and ANN payloads); `docs/design/model-composer.md`
**Read first:** [P1] §6.2.6.2 (why the interpolant decides low-level distortion; Fig. 6.8), §6.2.7 (ANNs, Fig.
6.10), §6.2.8 (guided extrapolation, Fig. 6.12), §6.5.1–6.5.3 (eqs. 6.55, 6.61–6.64); **[X07]** before
R-mc2-3/R-mc2-4

---

## 1. Goal

A constituent can be a **table** or an **ANN** as well as an equation. Every method is smooth enough for
low-level distortion, is defined for every voltage a Newton iteration can visit, and stays exactly differentiable.

## 2. Requirements

**R-mc2-1 — Table evaluator.**
- A rectilinear grid over the constituent's inputs (`vgs`, `vds`, and `T`, `phi1`, `phi2` when present), with
  non-uniform axes allowed. Values are stored as `.npy`.
- Interpolation is a **tensor-product cubic spline, C² on every axis**, with exact first derivatives. Never
  bilinear: its first derivative jumps at every knot, which is exactly what makes table models wrong at low drive
  ([P1] §6.2.6.2, Fig. 6.20).
- An optional **smoothing** spline, a single penalty weight λ stored in the payload, for noisy data ([P1]
  §6.2.6.2).
- Even a C² cubic spline has piecewise-constant third derivatives and none beyond, so a table cannot predict
  higher-order distortion at very low drive ([X06] §I). Tables stay available; the ANN is the default where
  low-level IM matters, and the design note says so.
- The spline is fitted once, when the `.cmodel` is written (MC-11), and its coefficients are stored, so loading
  does no fitting.

**R-mc2-2 — ANN evaluator.**
- A multilayer perceptron:
  - affine input normalisation;
  - 1–4 hidden layers of 1–64 neurons, activation `tanh` (default) or logistic sigmoid ([P1] Fig. 6.10);
  - a linear output, then output de-normalisation.
- Returns the value and its **exact gradient with respect to the inputs**, by forward-mode chain rule through the
  layers. This is the derivative network of [P1] §6.2.7.
- Weights, biases and normalisation are stored as `.npy`.
- The evaluator is immutable and thread-safe. Scratch buffers are per call (stack or a per-thread pool), so
  `EvaluateInto` stays allocation-free.

**R-mc2-3 — Guided extrapolation ([P1] §6.2.8).**
- Each table or ANN constituent stores its **training domain**: a polygon in (`vgs`, `vds`) — the convex hull of
  its training points, optionally user-trimmed — and a box for any other input.
- **Inside**, evaluate. **Outside**, let `p_b` be the nearest domain point:
  - currents: `f(p) = f(p_b) + ∇f(p_b)·(p − p_b) + G_x·(v_own − v_own,b)·d/d₀`, where `v_own` is `vds` for `Id`
    and the branch voltage for `IgGs`/`IgGd`, `d = |p − p_b|`, and `d₀` is 5 % of the domain's extent. The last
    term is an added conductance that grows with distance and vanishes, with its derivative, at the boundary —
    the "sharply increasing branch conductance" of [P1] Fig. 6.12b.
  - charges: linear continuation only, so capacitances hold their boundary values.
- `G_x` defaults to 10× the largest conductance in the training data and is stored in `Extrapolation`.
- Required properties:
  - value and gradient continuous across the boundary;
  - finite everywhere;
  - no derivative jump inside the domain.
- A derivative jump *outside* the domain, away from the boundary — at a polygon vertex's normal cone — is allowed;
  rounding the hull's corners is an implementation option.
- Equation constituents are not extrapolated: an equation is global by construction ([P1] §6.2.5.2).

**R-mc2-4 — Drain–source symmetry option ([P1] §6.5, [X07]).** `Symmetry: "Exchange"` (off by default, because
field-plated power devices are deliberately asymmetric — [P1] §6.5.5):
- Constituents are defined for `vds ≥ 0` only.
- For `vds < 0`, evaluate at the image point `(vgs − vds, −vds)` ([P1] eq. 6.61), then transform:
  - currents: `IgGs ↔ IgGd` exchanged ([P1] eq. 6.64); `Id(vgs, vds) = −Id(vgs − vds, −vds)` for the channel
    branch;
  - terminal charges: `Qg(vgs, vds) = Qg(vgs − vds, −vds)` and `Qd(vgs, vds) = −Qg(vgs − vds, −vds) − Qd(vgs − vds,
    −vds)`, the charge form of [P1] eq. 6.63.
- Continuity at `vds = 0` is a property of correct data, not something this phase enforces. The composer reports
  the jump (MC-7/MC-8).

**R-mc2-5 — Grid evaluation.** `ComposedCore` overrides `EvaluateGrid` (or uses `EvaluateInto`) so an HB time grid
evaluates every constituent in one pass, with no per-sample allocation. Assert it with an allocation counter
(`GC.GetAllocatedBytesForCurrentThread` before/after a grid), never with a timing.

## 3. Not in this phase
Fitting tables or training ANNs (MC-6/MC-7/MC-8); choosing the domain polygon from data (MC-7/MC-8 produce it).

## 4. Gates (minimal tests, run only these classes)
- `TableEvaluatorTests` — a spline over a sampled analytic function reproduces it and its gradient to within the
  sampling's stated error bound; second derivative continuous across a knot (finite-difference check); smoothing
  with λ = 0 equals interpolation.
- `AnnEvaluatorTests` — a hand-set 2-layer network's value and input gradient against finite differences to 1e-8
  relative, for both activations.
- `GuidedExtrapolationTests` — value and gradient continuous across the boundary to 1e-9 relative; a DC solve
  started far outside the domain converges to the in-domain answer; the result is never NaN on a 10⁴-point sweep
  of ±10× the domain.
- `DrainSourceSymmetryTests` — an `Exchange` model satisfies [P1] eq. 6.55 at random points in both half-spaces;
  the Fig. 6.28 two-configuration test gives identical terminal currents.
- `ComposedCoreGridTests` — zero bytes allocated per HB grid after warm-up; an ANN `.cmodel` equals an equation
  `.cmodel` encoding the same function in DC/SP/HB to 1e-9.
