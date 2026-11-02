# Brief TO-9 — Goal functions in the expression engine, the goal picker, and "Add as goal…"

**Series:** `brief-tuneopt-0-overview.md` (D10) · **Tag:** `R-to9-<m>` · **Depends on:** TO-1
**Area:** `src/Core/Expressions/Evaluator.cs` (built-ins; `max_over`/`min_over` are the precedent),
`src/RfCore/Data/NetworkMetrics.cs` and `src/RfCore/RFNetwork.cs` (μ, μ′, K, |Δ|, max gain already computed there),
`docs/design/expressions.md` §7, `docs/design/measurements.md`, `src/Ui/DataDisplay/` (trace context menu), a goal
template catalog in `src/Design/Optimization/`

---

## 1. Goal

Every quantity the owner listed — S in dB or linear, phase, μ/μ′ stability, HB efficiency, WSProbe values — is a
**one-click goal**, and a quantity the user has already plotted becomes a goal from the trace itself.

## 2. Requirements

**R-to9-1 — Network-metric built-ins, reusing RfCore.** Add to the expression engine, computing through
`NetworkMetrics`/`RFNetwork` (no second formula):
`mu(S)`, `mu_prime(S)`, `K(S)`, `delta_mag(S)`, `max_gain(S)` (dB) and `max_gain_lin(S)`, `passivity(S)`;
each takes an `S` cube (`SP1.S`) and optional 1-based port pair `(S, i, j)` for an N-port (the two-port reduction the
Data Display already uses — find it, do not rewrite it). Result `{…, freq}`. Name the functions so a reader can guess
them; record them in `expressions.md` §7 and the `reference` function list. The **renormalization rule** in
`NetworkMetrics`' header (renormalize to a uniform real reference first, always) applies to the cube path and must be
honoured here.

**R-to9-2 — Group delay and VSWR.** `group_delay(z)` (−dφ/dω along `freq`, unwrapped, seconds) and `vswr(z)` for a
reflection coefficient. Same documentation.

**R-to9-3 — The goal-template catalog** (headless, `src/Design`). A list of templates, each producing a goal
expression and a suggested analysis and axis range from a few fields, for the analyses present:
- S-parameters: `|Sij|` in **dB** or **linear**, **phase** (degrees), **group delay**, **VSWR**, **μ**, **μ′**, **K**,
  **max gain** (choose port pair from the analysis's ports).
- HB: output power (dBm/W) at a harmonic from a node/probe pair, **drain efficiency** and **PAE** from an output
  node/probe, a DC supply and the input source — the template writes the explicit cube algebra (the
  `measurements.md` §"Accessor" form), so the user can read and edit what it made.
- WSProbe: each derived metric of `stability-wsprobe.md` §5.2 for a chosen probe label.
- Any **existing `measure` row**, by name.
- **Custom expression** — free text, validated as typed.

**R-to9-4 — "Add as goal…" from a trace.** Every Data Display trace whose source is a schematic's results gains a
context-menu item that opens TO-10's goal editor **pre-filled**: the analysis (the trace's group), the expression (the
cube and slice, with the trace's transform — `dB20` → `dB(…)`, `phase` → `phase(…)`, a network metric → `mu(…)` etc.),
the axis range (the plot's current visible X range on that axis), and a limit taken from the visible marker if there is
one. Translation lives in `src/Design` (headless), tested without a display. A trace whose transform has no expression
equivalent shows the item disabled with the reason in its tooltip.

**R-to9-5 — Complex values (overview D18; amended 2026-10-07).** No change to goals: a goal is a measurement, not a
tunable. A goal expression may read a complex variable as any `measure` line can (`mag(ZL)` in a goal is the
expression function, evaluated on the tuned whole value, not a tunable key); the goal editor's validation already
accepts it. "Add as goal…" is unaffected.

## 3. Gates (minimal; run only these classes)
- `NetworkMetricFunctionTests` — each new built-in equals `NetworkMetrics` on the same S cube (one test per function,
  including a non-50 Ω reference that forces renormalization).
- `GroupDelayVswrTests` — a delay line's group delay equals its length/velocity; a known Γ's VSWR.
- `GoalTemplateTests` — each template family produces an expression that evaluates on the matching example's results.
- `TraceToGoalTests` — dB20 S21, phase S11, μ, and a measurement trace each translate; an untranslatable transform
  reports why.
