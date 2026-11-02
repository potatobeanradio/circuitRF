# Brief TO-10 — The Optimizer panel

**Series:** `brief-tuneopt-0-overview.md` (D4, D10–D17, §3 UX) · **Tag:** `R-to10-<m>`
**Depends on:** TO-5 (presets), TO-6 (core), TO-9 (goal picker); TO-7/TO-8 only add menu entries
**Area:** `src/Ui/ViewModels/Dock/` (a new `OptimizerTool`), `DockPanelIds.Optimizer`, `src/Ui/Optimization/`,
the TO-4 row controls (reused), the TO-3 publishing path (reused)

---

## 1. Goal

Set up goals and variables, pick an algorithm, run it, watch it converge, keep the result. Same look and density as
the Analyses and Tuning panels.

## 2. Layout (top to bottom)

```
┌ Optimizer ─────────────────────────────────────────────────┐
│ ▶ amp_testbench.csch        Iter 37 · 412 evals · 1:08     │ header + run status
│ [▶][⏸][■] Algorithm: Gradient (LM) ▾ [⚙] [🔒][⤓ Push] Presets ▾ [?] │
│ Cost  ╲__▁▁▁▁▁  0.0123 (best)   Goals met 3/4               │ cost sparkline (log), best cost
├ Goals ──────────────────────────── [＋][✎][⧉][✕][↑][↓] ──┤
│ ☑ G1  dB(S21) ≥ −0.5  1–2 GHz  SP1   ███████████░ −0.62  ✕  │ per-goal bar: margin at best point
│ ☑ Stab mu ≥ 1.05  0.1–10 GHz  SP1    ████████████ 1.11  ✓  │
├ Variables ───────────────────────────────── [＋][⚙] ──┤
│ ☑ C1.C   0.9 ─────●──── 3.6 pF   1.84  pF   s: 0.41        │ value, range bar, sensitivity (TO-8)
│ ☑ L2.L   1   ──────────● 10 nH  10.0  nH  ⚠ max  [Widen]   │ D17 railed mark
└────────────────────────────────────────────────────────────┘
```

## 3. Requirements

**R-to10-1 — Dock.** `DockPanelIds.Optimizer`, default tabbed **behind Tuning**; Window ▸ entry; layout migration as
TO-4 R-to4-1. Follows focus exactly as Tuning does.

**R-to10-2 — Variables** are the same entries as Tuning's (D4), filtered to `opt`-enabled; the ＋ button is TO-4's
search popup setting the `opt` flag. Editing a range here edits it for Tuning too. Each row: enable check, min — bar
with the current best marked — max, best value in its unit, sensitivity (TO-8, blank until asked), railed mark with
**Widen** (doubles the span on that side, log-aware; one undo step).

**R-to10-3 — Goals list** with the Analyses panel's toolbar set: Add, Edit, Duplicate, Remove, Move up/down. Each row:
enabled check, name, a compact readable summary (`dB(S21) ≥ −0.5 · 1–2 GHz · SP1`), a **margin bar** at the current
best point (green when met, its length the normalized margin), the worst value, ✓/✕. Double-click edits.

**R-to10-4 — Goal editor dialog.** Left: the TO-9 template catalog grouped by analysis kind (and "Existing
measurements", "Custom"). Right: the fields — name, **analysis** (drop-down of declared analyses, defaulting to the one
the expression references), **expression** (editable, live-validated with the engine's own error text), **axis range**
(axis drop-down from that analysis's swept axes, `freq` default; lo/hi with units; "whole range" check), **type** as
five small toggle buttons (≤ ≥ = in out) with one or two limit boxes, **weight**, **sloped** check revealing the
second limit, **scale** (advanced, collapsed). A small preview plots the expression's current values over the range
with the limit drawn — when results exist; otherwise nothing.

**R-to10-5 — Algorithm menu** reads the TO-7 registry; the tooltip is each algorithm's use-when line. Choice persists in
the document (D5). ⚙ opens the settings: max iterations, max evaluations, time limit, cost form (least squares /
minimax; locked when the algorithm requires one), **analyses: goal analyses only / all enabled** (default goal only),
parallelism, seed, and the selected algorithm's own options (generated from its options schema, advanced ones
collapsed).

**R-to10-6 — Run / Pause / Stop (D16).** Run validates first (no opt variables, no enabled goals, a goal error —
refused with the reason in the status line, never a dialog). Pause and Resume share one button. Running: the header shows
iteration, evaluations, elapsed and stage (Auto); the cost sparkline updates per iteration (log scale, best cost);
goal bars and variable best values update per iteration; failed-evaluation count shows when > 0.

**R-to10-7 — The Data Display follows the best point.** Each time the best point improves, publish its `DataSet`
through TO-3's path with the **Optimizing** chip; frame coalescing (R-to3-6) does the skipping. With "analyses: goal
analyses only", plots of analyses the optimizer is not running keep their old data and draw dimmed (they are stale).
On finish, re-run the best point with **all** enabled analyses once so every plot is current, then write `run.npy`
with provenance (TO-3 R-to3-7).

**R-to10-8 — Keeping the result.** Lock in (TO-5, recording the cost), Push (TO-4 R-to4-8, one undo step per document),
**Send to Tuning** (loads the best values into the Tuning session so the user can hand-tune from there), and — when
any variable is discrete — **Snap and polish** (TO-8 R-to8-4). Available while paused and after finishing.

**R-to10-9 — Finish summary** in the status line: reason, all goals met or which not, best cost; full detail in
Messages. No modal dialog.

**R-to10-10 — Complex values (overview D18; amended 2026-10-07).**
- The Variables list shows opt-enabled **parts** as rows (`mag(ZL)`, `phase(ZL)`), added through the same search
  popup and the Inspector/canvas parts menu with the `opt` flag; the best value of every part of a value is the
  view of ONE decoded complex number, so a part that is not opt-enabled but is tune-enabled shows the value it took.
- Range edits and **Widen** obey the D18 refusal (Widen only grows a range, so it is refused only when another edit
  already conflicts). Railed (D17) on a part names the part whose range holds it, when that is another part.
- Run refuses more than two opt-enabled parts of one value with TO-6's sentence, in the status line.
- The header and finish summary show infeasible points beside failed ones when > 0 ("12 infeasible").

## 4. Gates (minimal; run only these classes)
- `OptimizerPanelRunTests` (headless view model, fake fast evaluator): run → iterations update the goal bars and best
  values; pause holds; resume continues; stop keeps the best; the run validates and refuses a setup with no goals.
- `GoalEditorTests` — a template fills the fields; the analysis defaults from the expression; the type toggles set
  limits correctly; an invalid expression shows the engine's message.
- `OptimizerPublishTests` — best-point publishes are coalesced (counter); finish re-runs all analyses once.
- `RailedWidenTests` — railed detection per D17 and Widen as one undo step.
- Docking: the panel appears behind Tuning by default.
- `OptimizerPanelRunTests` gains: a `mag`/`phase` pair runs and every row shows the decoded value; three parts refuse.
