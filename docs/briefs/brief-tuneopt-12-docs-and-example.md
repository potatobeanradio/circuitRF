# Brief TO-12 — User documentation and the Optimization example

**Series:** `brief-tuneopt-0-overview.md` · **Tag:** `R-to12-<m>` · **Depends on:** all earlier phases
**Area:** `examples/` (a new workspace + `examples.json`), `docs/user/src/` (doc sources only — **no DocGen run**; the
owner regenerates at the end of the series), `docs/design/tuning-optimization.md` (final state)

---

## 1. Goal

A user can learn tuning and optimization from one example and two pages; an agent can learn it from the reference
topics alone (TO-11). Nothing here adds behavior.

## 2. Requirements

**R-to12-1 — Example workspace `Optimization`** (Tools ▸ Examples; add the folder and its `examples.json` entry the
way the six shipped examples are). Three cells, each small enough that a full optimization takes seconds:
1. **L-section match** — two tune/opt variables, one goal (|S11| dB ≤ −20 over a band). The CLI/MCP gate's file.
2. **Bandpass filter** — four to six variables, passband ripple `in` goal plus two stopband `le` goals; set up for
   **Minimax**; a preset "As drawn" and the shipped optimum as a preset "Equiripple".
3. **Stability + gain** — a small amplifier with an SDD or built-in FET: a μ ≥ 1.05 goal over a wide band and a
   gain goal, showing two goals on one analysis and a railed variable on purpose (one range drawn too narrow).
Each cell's schematic opens with a Data Display already bound to the goals' quantities. Where an analysis setting was
reduced to keep the run short, say which and what the full value would be (the example's own note).

**R-to12-2 — User pages** (`docs/user/src`): *Tuning* (activate, sliders, ranges, lag, snapshot, push, presets) and
*Optimization* (variables, goals and the picker, "Add as goal…", algorithms with the use-when table, settings, reading
the progress, railed variables, snap and polish, lock in and push), plus a short *Optimizing from the command line*
section in the CLI page. Screens and figures are produced by the doc fixtures as for every other page; register any new
figure fixture, but do not run DocGen.

**R-to12-3 — Final design note.** Bring `docs/design/tuning-optimization.md` to the shipped state: every decision as
built, the normalization rule, the algorithm table, the file grammar, and a "for the yield series" section listing the
seams overview §5 reserved and whether each held.

**R-to12-4 — Vendor check.** Before handing back, grep the series' new files for commercial tool and vendor names
and remove any found; report what was removed.

**R-to12-5 — Complex values (overview D18; amended 2026-10-07).** The **Stability + gain** cell drives its
amplifier from a complex source impedance VAR (`Zs = polar(…) Ohm`) whose `mag` and `phase` are tune- and
opt-enabled, so the example shows a complex value tuned by parts and pushed back in its own form. The *Tuning* and
*Optimization* pages explain picking a part, the partner-held rule, the always-held ranges and the refusal, the
two-part limit in the optimizer and infeasible points. The design note's final state records D18 as built.

## 3. Gates
- `OptimizationExampleTests` — each example cell's `check` is clean; the L-section and filter optimize to all goals
  met with their saved algorithm and seed (evaluation count bounded, not timed).
- The examples list test that already covers `examples.json` passes with the new entry.
