# Brief MC-6 — The ANN trainer: value training and adjoint (derivative) training

**Series:** `brief-modelcomposer-0-overview.md` (D4, D13, D14) · **Tag:** `R-mc6-<m>` · **Depends on:** —
**Area:** a new `src/Engine/Modeling/Ann/` (no domain types); the runtime network of MC-2 (shared definition, so a
trained network loads without conversion)
**Read first:** **[X03]** (the adjoint method — required before starting), [P1] §6.2.7 and §6.3.6 (Fig. 6.17),
§6.4.2 (the same method for RF conductances); **[X06]**

---

## 1. Goal

One in-house, deterministic trainer for the small multilayer perceptrons the composer uses. It trains a network
to **values** (a current from I–V data), to **partial derivatives** (a charge from capacitances, a current from
RF conductances), or to **both at once** ([X03]'s combined training of the original and adjoint networks). The second is the adjoint training of [P1] §6.3.6 and [X03].

## 2. Requirements

**R-mc6-1 — The network** is exactly MC-2's definition (layers, activations, normalisation), so training produces
the runtime object directly. Inputs are scattered points of any dimension 1–5. There is one scalar output per
network.
- **Default size:** 2 hidden layers of 8 `tanh` neurons. The trainer counts the targets (one per value, one per
  derivative component) and warns once when the network's weights exceed a tenth of them — [L26]'s one-in-ten
  rule, which sized its 74-weight network for 794 points. Held-out error (R-mc6-5) stays the real test.

**R-mc6-2 — Value training.** Loss `Σ_k w_k (f(x_k) − y_k)²`, where `w_k` is a per-point weight (for example,
relative weighting for currents spanning decades), plus an optional L2 weight penalty.

**R-mc6-3 — Derivative (adjoint) training.**
- Loss `Σ_k Σ_j w_kj (∂f/∂x_j (x_k) − t_kj)²` over chosen input directions `j`, plus an optional anchor
  `(f(x_0) − y_0)²` that fixes the arbitrary constant (a charge's zero).
- The gradient with respect to the weights is computed **exactly**, by back-propagating through the derivative
  network — the adjoint network of [X03]. Never by finite differences.
- When the targets are not the gradient of any function (non-conservative data, [P1] eq. 6.27 violated), the
  result is the least-squares compromise. That is the documented, intended behaviour ([P1] §6.3.6 ¶2).

**R-mc6-3b — Combined training** ([X03] eq. 8, both weights non-zero). One loss that sums R-mc6-2's value term
and R-mc6-3's derivative term, each over its own point set, with a weight per term. [X03] §IV-C (Fig. 11) trains
FET currents exactly this way, on DC values and S-parameter derivatives together. The composer uses it for the
drain current: DC (and pulsed) I–V as values, and the S-parameters' `Gm`/`Gds` as partial derivatives at the
frozen trap and thermal state of each DC point — the small-signal reading of [X10] §VI (MC-9, MC-10). [X10]
itself trains on large-signal waveforms, and [X14] on waveforms, DC and S together; waveform training is not in
this series (MC-16). The weight-gradient is exact, by the same back-propagation through both networks.

**R-mc6-4 — Optimiser.**
- **L-BFGS** with a line search, in-house, double precision. The existing `BfgsB` is an ask/tell box optimiser
  without analytic gradients and is not reused here.
- Stops on a gradient-norm tolerance, a loss-change tolerance, or an iteration cap; all are settings.
- Initialisation is seeded through `SplitMix64` (Xavier/Glorot-scaled).
- Restarts: `n` seeded restarts, keeping the best on the **held-out** set.

**R-mc6-5 — Held-out error.**
- A deterministic stratified split of the points: a default 10 %, chosen by a seeded spread over the domain, never
  by array order.
- Report training and held-out RMS — absolute, and relative to the target's range.
- Early stopping on held-out error is optional.

**R-mc6-6 — Determinism.** Single-threaded training with the same seed and data gives the same weights, bit for
bit, on every platform: fixed summation order, no parallel reduction. An opt-in parallel mode may use more
threads and then promises only the same result to within 1e-12 relative, and says so in its setting's
description.

**R-mc6-7 — Long work.** `RunControl`: progress per iteration and restart, cancellable. On cancel the trainer
returns **nothing** — the caller keeps the previous network (D13).

## 3. Not in this phase
Which data a constituent trains on, and its weights and anchors (MC-7, MC-8, MC-9, MC-10).

## 4. Gates (minimal tests, run only these classes)
- `AnnValueTrainingTests` — a 1×10 tanh network fits `tanh(x)·y²` on 200 scattered points to a stated held-out
  RMS.
- `AnnAdjointTrainingTests`:
  - trained only on the analytic gradient of a known non-separable `Q(v1, v2)`, the network's value matches `Q` up
    to the anchored constant within a stated tolerance;
  - on a deliberately non-conservative target field, the result equals the least-squares projection computed
    independently on a grid, within tolerance;
  - the adjoint weight-gradient equals a finite-difference check to 1e-6 relative;
  - combined training on the values of a known `f(v1, v2, s)` and its `v`-derivatives at fixed `s` recovers both
    within tolerance, where value-only training on the same points does not recover the derivatives.
- `AnnDeterminismTests` — two runs with the same seed are bit-identical; different seeds differ; held-out
  selection is independent of the order of the input array.
- `AnnTrainingCancelTests` — cancel returns no network and leaves the caller's network unchanged.
