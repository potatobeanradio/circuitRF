# Brief series — Tuning and Optimization (TO-1 … TO-12)

**Status:** TO-1…TO-4 built · **Date:** 2026-10-07 · **Decisions:** locked (§2), owner-reviewed ·
**Amended 2026-10-07:** D18 (complex values) added at the owner's request after TO-4; TO-1…TO-4 were brought up
to it in the same change, and every later brief carries its part.
**Scope source:** `docs/PRD.md` v3.1 §5.1, §9, §10, §17 (the "no optimization engine" non-goal is retired)
**Supersedes:** `brief-agent-authoring-overview.md` §AA-4 (its two open questions are answered in §2 below)
**Follow-on, NOT in this series:** yield — Monte Carlo, design centering, yield optimization. It will build on this
series' variables, goals and evaluation service, so every phase here must leave room for it (§5).
**Requirement tag:** `R-to<n>-<m>` (phase n, requirement m).
**Design note to be written by TO-1:** `docs/design/tuning-optimization.md` — this overview is its source.

---

## 0. What the owner asked for (paraphrased)

- Any component parameter or VAR variable can be **activated for tuning**; nothing is activated by default.
- A **Tuning** window with **sliders**; the **Data Display updates live** as the sliders move; simulation runs off the
  UI thread and the user is told when a result **lags** the sliders.
- A good set of values can be **locked in** as a **preset** stored in the `.csch`; recalling a preset is **best
  effort** when components have since been deleted.
- Per-parameter **min/max**, persisted, with sensible defaults.
- **Push** the values into the schematic with one button, **undoable**.
- An **Optimizer** window: pick variables, define **goals** (measurement equations, one analysis each, limit types
  upper / lower / inside / outside), pick an **algorithm** from a menu (persisted), **max iterations**, **pause/stop**,
  lock-in and push like Tuning, feedback after each iteration on how close the goals are, and a mark on any variable
  that has **railed** at its min or max. The Data Display updates per iteration and may skip frames to keep up.
- Goals over **frequency** (the `freq` axis), in dB or linear, phase, **μ / μ′ stability**, HB **efficiency**,
  **WSProbe** values — each easy to pick.
- The optimizer runs from the **CLI and MCP**; an AI agent can discover how to write goals and run it.
- Strong algorithms, including **Newton-family** and slow-but-thorough global ones.
- **UX is the priority**: compact toolbar buttons in the style of the Analyses panel.

Owner answers on 2026-10-07 (paraphrased): expression-valued parameters are **not offered** for tuning at all;
parameters **inside sub-circuits are tunable**, found through a search field like the Instances panel's; μ and μ′
already exist in RfCore and should be reused; standard-value snapping should reuse the Smith Chart tool's preferred
values; **one shared range** per variable for tuning and optimizing; unpushed tuned values are kept automatically as a
"Last tuned" preset; yield is a later series; goals are authored **only** in the Optimizer window.

Owner request after TO-4, 2026-10-07 (paraphrased): complex values must be tunable and optimizable. Picking one
offers its **real**, **imaginary**, **magnitude** or **phase (degrees)** part; a user who wants the whole number adds
two parts (real + imaginary, or magnitude + phase), and is free to mix them (real and magnitude of one value). The
**limits of a value's parts are always respected**, in tuning and in optimizing, and a limit change on one part that
conflicts with the value's other limits is a **refusal**. A complex value partly built from another name
(`ZL = 4.0 + jX`) is an expression and is not offered. The point is to spare the user two VAR rows and a third that
combines them. → D18.

---

## 1. The phases

| Phase | Brief | What it delivers | Depends on |
|---|---|---|---|
| TO-1 | `brief-tuneopt-1-format-and-model.md` | The model, the `.csch` block, the `.cnl` directives, the tunable catalog, path overrides in elaboration, `check`/`explain`/`reference` | — |
| TO-2 | `brief-tuneopt-2-evaluation-service.md` | One headless "evaluate this design with these overrides" service, below the firewall, used by Simulate, the CLI, tuning and the optimizer; concurrency audit | TO-1 |
| TO-3 | `brief-tuneopt-3-live-session-and-display.md` | The live tune session (latest-wins, lag tracking) and the Data Display's in-memory source, frame coalescing and snapshot ghosts | TO-2 |
| TO-4 | `brief-tuneopt-4-tuning-window.md` | The Tuning dock panel, sliders, adding variables (Inspector, canvas, search), canvas marking, Push/Revert with undo | TO-3 |
| TO-5 | `brief-tuneopt-5-presets.md` | Lock-in, recall (best effort), "Last tuned", preset management — shared by both windows | TO-4 |
| TO-6 | `brief-tuneopt-6-optimizer-core.md` | Goal cost, variable transforms, cache, parallel evaluation, stopping, pause, history; Random, Nelder–Mead, Levenberg–Marquardt, BFGS-B | TO-2 |
| TO-7 | `brief-tuneopt-7-algorithms-global-and-dfo.md` | Differential evolution, particle swarm, CMA-ES, trust-region model DFO, mesh adaptive direct search, minimax | TO-6 |
| TO-8 | `brief-tuneopt-8-bayesian-discrete-auto.md` | Bayesian optimization, discrete / preferred values with re-polish, Auto, sensitivity | TO-7 |
| TO-9 | `brief-tuneopt-9-goal-functions-and-picker.md` | μ, μ′, K, Δ, max gain, group delay, VSWR in the expression engine; the goal-template catalog; "Add as goal…" from a trace | TO-1 |
| TO-10 | `brief-tuneopt-10-optimizer-window.md` | The Optimizer dock panel | TO-5, TO-6, TO-9 (TO-7/8 add menu entries) |
| TO-11 | `brief-tuneopt-11-cli-and-mcp.md` | `opt` verb, MCP `run analysis=optimize`, reference topics, progress | TO-6, TO-9 |
| TO-12 | `brief-tuneopt-12-docs-and-example.md` | User docs, an `Optimization` example workspace | all |

TO-6 and TO-9 can run in parallel with TO-3…TO-5. TO-7 and TO-8 can land after TO-10 — the algorithm menu is a list.

---

## 2. Locked decisions

### D1 — What is tunable
A **tunable** is one of:
- a **component parameter** whose stored value is a **plain number** with an optional unit/scale (`47`, `47 pF`,
  `1.2e-9`), or
- a **VAR row** whose expression is a plain number with an optional unit.

Anything else is **not offered at all** — not greyed, not listed: an expression (`W=Wvar*2`), a reference to a cell
parameter, a string, an enum, a bool, a file reference. A **complex literal** is offered through its parts (D18), never
whole. (The way to tune an expression is to tune the
VAR it reads, and that VAR is offered.) An **integer-typed** parameter (finger count, turns) is offered and snaps to
integers. A parameter that a **parametric sweep** in the same schematic sweeps is offered but disabled while that sweep
is enabled, with the reason on its row.

### D2 — Hierarchy
Tunables are found at **any depth** below the schematic being tuned. The identity of a tunable inside a sub-cell is
its **cell definition**, not its instance path: tuning `R3.R` inside cell `DUT` moves R3 in **every** instance of
`DUT`, because Push writes the `DUT` schematic and cannot write anything narrower. The row says where it lives and how
many instances share it (`DUT · ×2`).

**A sub-circuit instance's own parameters are tunable, per instance.** If cell `DUT` declares a parameter `Rbias`,
then `X1.Rbias` and `X2.Rbias` are two separate tunables of the schematic that holds `X1` and `X2`. That holds whether
the instance currently **overrides** the parameter or is **inheriting the cell's default**; in the second case the
slider starts at the default, and Push **adds** the override to that instance. The same applies one level down:
`DUT:X5.Wf` is the parameter `Wf` of instance `X5` inside `DUT`. As with any parameter, an instance value that is an
expression (`Rbias=Rx`) is not offered, and the VAR it reads is. The cell's declared **default** is not offered on its
own: tuning one instance's value is what tuning that parameter means.

**Dirty state.** Tuning changes nothing on disk and dirties no document: tuned values exist only in the session. Push
is the moment documents change. Each document Push writes into becomes **dirty, with one undo step of its own**: the
tuned schematic for top-level and instance values, and the sub-cell's own `.csch` for values that live in it. A sub-cell
that is not open is **opened as a tab without taking focus**, so its unsaved state is visible and goes through the
ordinary unsaved-changes prompt. A dirty document never hides in a background session.

A tunable inside a **read-only** cell (a library or PDK cell, a cell of another workspace, a file the user cannot
write) **can be tuned and optimized**; its row says Push is unavailable and why, and Push skips it and reports it.

### D3 — Identity and spelling (the key used by presets, `.cnl` and the CLI)
- Top level: `R1.R` (instance.parameter), `Wline` (a VAR variable, by name — it survives moving the row to another VAR).
- Inside a sub-cell: `DUT:R3.R`, `DUT:Wline`, where `DUT` is the cell reference **spelled exactly as the `.cnl` spells
  that cell's instance type**. TO-1 confirms the spelling against `CnlWriter` and records it in the design note.
- A part of a complex value (D18): `real(K)`, `imag(K)`, `mag(K)`, `phase(K)`, where `K` is any key above —
  `mag(Zsrc)`, `phase(DUT:X5.ZL)`.
- **A key that resolves to nothing is never an error** anywhere in this series: a preset skips it and says so;
  `check` reports it as a **warning**; a run ignores it with a run note.

### D4 — One variable list, one range
Tuning and optimizing share **one entry per tunable**: `tune` flag, `opt` flag, `min`, `max`, `scale`
(`auto`/`lin`/`log` — `auto` is log when min > 0 and max/min ≥ 10), optional `step`, optional `discrete`
(`none`/`integer`/`preferred`). Both windows show and edit the same entries. The **current tuned value** lives in the
session, not in the entry.

**Default range on first activation:** for a positive value v with scale log → [v/2, 2v]; linear → [v − |v|/2,
v + |v|/2]; v = 0 → [0, 1] in the parameter's display unit (editable, and the row says the range was guessed). The
range is shown in the parameter's own unit and scale (pF stays pF).

### D5 — Where it is stored
A `tuning` block in the **tuned schematic's** `.csch` (the testbench schematic, even for sub-cell tunables), holding
the variable entries, the presets, the goals, and the optimizer settings (algorithm and its options, limits, cost form,
analysis scope, seed). The same content is legal `.cnl` text — `tune`, `preset`, `goal`, `optimize` directives — and
`NetExtractor` emits it, so a `.csch` and the `.cnl` it extracts to say the same thing. **Changing any of it marks the
document dirty and is undoable** through the schematic's own undo stack, the way an analysis-card edit is.

### D6 — Unpushed tuned values persist
When the document is saved or closed while tuned values differ from the schematic, they are stored as the automatic
preset **"Last tuned"** (one, overwritten each time). Nothing else about a session persists.

### D7 — Live tuning runs newest-wins, without cancelling
One evaluation in flight per session. A slider change while one is running **replaces the pending request** (there
is at most one). The in-flight run is **not cancelled** by a newer value — cancelling on every tick means a slow bench
never shows anything while the user drags. It is cancelled by Stop, Revert, Push, closing the document and switching
the tuned schematic. A **"Run on release"** toggle (per session, default off; default ON when the last evaluation took
> 2 s) makes dragging send only the release value.

### D8 — The Data Display reads live results in memory
`data-display.md` §2.2 names the in-memory path as a future optimization; this series builds it. A session **publishes
a `DataSet` under the schematic's own `run.npy` source identity**, so every existing trace bound to that schematic's
results updates with no change to the trace. The file stays the canonical identity: when the session stops, the last
displayed result is written to `run.npy` with provenance saying it came from tuned values and which. Redraws are
**coalesced to the newest `DataSet`** — a display that is still drawing skips the intermediate ones (this is the
optimizer's frame skip too). While a display shows tuned or optimizer data it carries a small **Tuning** / **Optimizing**
chip.

### D9 — Snapshot ghosts
A **Snapshot** toolbar button freezes the currently displayed result as a faded copy of every trace bound to that
schematic's results; **Clear snapshot** removes it. Session-only, never persisted. This is the before/after comparison
that makes tuning usable.

### D10 — Goals
A goal is: a **name**; an **expression** in the one expression engine (the same language as `measure`, so no MEAS row
is needed first); exactly **one analysis**; an optional **range on one swept axis** (`freq` by default, or a sweep
variable, power, harmonic index); a **type** — `le`, `ge`, `eq`, `in` [a, b], `out` [a, b]; a **weight** (default 1);
optional **sloped limits** (a limit at each end of the range, linearly interpolated); an **enabled** flag. Goals are
authored **only** in the Optimizer window (and by writing the `.cnl`/`.csch` text); there is no goal schematic component.
A goal range that contains no grid point is an error, by `max_over`'s existing rule. A goal that reads only variables
(`L1 - L2 ge 0`) is a constraint that costs no simulation.

### D11 — Cost
Each goal yields a **violation per grid point** (0 where met), divided by a per-goal **scale** so dB, degrees and
linear quantities mix sensibly (TO-6 defines the scale and documents it), times the weight. The run's cost is
**least squares** (Σ v²) by default or **minimax** (max v), chosen per optimization. "All goals met" means cost = 0;
an optimization that ends with cost > 0 reports each unmet goal by name with its worst point, never as "the nearest
miss" without saying so.

### D12 — Architecture
- **Pure numerics** (algorithms over a bounded vector, no domain types): `src/Engine/Optimization/`.
- **Orchestration** (tunables → overrides, goals → residuals, evaluate, cache, history): `src/Design/Optimization/`,
  referenced by `src/Ui`, `src/Cli` and the MCP server — **one** implementation; the GUI and CLI cannot disagree.
- **Evaluation** goes through TO-2's service, extracted from `SchematicRunService` — not a second run path.
- `src/Ui` holds only the two panels, their view models and the Data Display wiring.

### D13 — Algorithms (the menu, in this order)
| Menu label | Algorithm | Phase |
|---|---|---|
| Auto | global (CMA-ES) then local polish (Levenberg–Marquardt) | TO-8 |
| Gradient (Levenberg–Marquardt) | the Newton family on goal residuals, finite-difference Jacobian | TO-6 |
| Quasi-Newton (BFGS-B) | bound-constrained quasi-Newton | TO-6 |
| Minimax | trust-region sequential linear programming on the worst violation | TO-7 |
| Simplex (Nelder–Mead) | bounded via transform | TO-6 |
| Trust-region model | derivative-free quadratic-model trust region (Powell family) | TO-7 |
| Pattern search | mesh adaptive direct search | TO-7 |
| Random | uniform / Latin hypercube | TO-6 |
| Differential evolution | adaptive (success-history) DE — the "genetic" entry | TO-7 |
| Particle swarm | constriction-factor PSO | TO-7 |
| CMA-ES | with increasing-population restarts | TO-7 |
| Bayesian (slow simulations) | Gaussian process + expected improvement; trust-region variant above ~10 variables | TO-8 |
| Discrete | grid / preferred values / integers | TO-8 |

All written in-house under MIT. No GPL or copyleft library, and no code copied from one.

### D14 — Headless writes nothing to the design's values
The CLI verb and the MCP run **report** values, goal margins and the trajectory. The one opt-in write is
`--save-preset <name>`, which adds a preset to a `.csch`'s `tuning` block (taking a history checkpoint first when the
workspace keeps history). An agent that wants the values in the design writes the file — the format is the contract.

### D15 — Exit codes
`opt`: **0** finished and every enabled goal met · **3** finished, at least one goal unmet (new; added to `cli.md` §7) ·
**1** refusal · **2** no evaluation converged · **130** cancelled (writes nothing).

### D16 — Pause and stop
Pause finishes the evaluations in flight, then holds the algorithm's whole state; Resume continues exactly as if never
paused. Stop ends the run and keeps the **best** point, which is what the window shows, lock-in stores and Push writes.

### D17 — Railed
A variable whose best value is within 0.5 % of its normalized range of a bound is **railed**: the row shows a mark at
that end of its bar and offers "Widen range". Railing is a hint, never a stop condition.

### D18 — Complex values are tuned and optimized by their parts
- **What is offered.** A parameter or VAR whose value is a **complex literal** — built from numbers only: numbers,
  `j`, unary and binary `+ - *` (`40+15j`, `50-j10`, `-3j`), or one `complex(a,b)` / `polar(m,deg)` with number
  arguments — with an optional unit after the whole. Anything that reads a name (`4+j*X`, `complex(R,X)`) is an
  expression and is not offered (D1); tuning `X` is how that one is tuned. A plain real number stays a D1 tunable —
  to tune the imaginary part of `50`, write it `50+0j`.
- **Never whole; four parts.** `real(K)`, `imag(K)`, `mag(K)`, `phase(K)` (D3), phase in **degrees**. Each part is its
  own entry (D4) with its own `tune`/`opt` flags, range, scale and step, and **any combination is allowed** — real with
  imaginary, magnitude with phase, real with magnitude of the same value, or all four.
- **One value, four views.** A session holds ONE complex number per value; every part row is a view of it. **Moving a
  part holds its partner in the same coordinate system**: real holds imaginary, imaginary holds real, magnitude holds
  phase, phase holds magnitude. The other parts' rows follow.
- **Limits always hold, together.** Every entry of a value constrains it — whatever its flags, in either window, since
  a range belongs to the entry (D4). A tuning move that would leave the region allowed by all of them **stops at the
  first edge**. Any edit that leaves **no** complex value inside every range of that value — a typed bound, Re-centre,
  Reset, Widen, a first activation's default range, a preset recall's widening — is a **refusal** naming the other
  ranges; `check` reports the same in a hand-written file as an error.
- **Default ranges.** Real, imaginary, magnitude: D4. Phase: [φ − 90°, φ + 90°], always linear (`auto` reads as `lin`
  on a phase). A phase range wider than 360° is an error.
- **Written whole.** A session request, a preset, `.cnl` preset text, Push and `--save-preset` all carry the **whole**
  value under `K` (`Zsrc=45+10j Ohm`), in **the form the schematic wrote it** (rectangular stays rectangular,
  `complex(…)` and `polar(…)` stay calls), so a pushed value is byte-identical to a typed one. A part key in a
  hand-written preset or a headless override is accepted and composed with the design's value by the optimizer's rule.
- **Optimizing.** Each opt-enabled part is one optimizer coordinate (its own range, its own scale). One part: its
  partner is held at the start value. Two parts of one system set the value directly; a **mixed pair** is solved
  geometrically — real + magnitude gives imag = ±√(mag² − real²), the sign taken from the start; real + phase gives
  mag = real / cos φ; and so on. A decoded point that no complex value satisfies (real larger than magnitude, a phase
  on the wrong side of the real part), or that falls outside the range of any of the value's entries, is
  **infeasible**: it is not simulated and ranks worse than every feasible point (TO-6). A complex value has two degrees
  of freedom, so **more than two opt-enabled parts of one value is a refusal** at Run (and in `check`), naming them;
  the parts that are not opt-enabled still limit it.
- **Code.** `ComplexValue` (literal test, form-preserving format, part get/set, `Compose`) and `ComplexRegion`
  (membership, exact emptiness, the stop-at-the-edge move, the conflict sentence) in `src/Design/Optimization/` — one
  implementation for the panel, `check`, the overrides and the optimizer.

---

## 3. UX principles for both panels

- **Panels follow the focused schematic** exactly as the Analyses, DRC and Instances panels do: cleared on focus change
  and set again only for a `.csch`; empty otherwise, with one line saying "Focus a schematic to tune it."
- **Header** names the schematic, as the Analyses panel does. **Toolbar** of small icon buttons below it, tooltip on
  every one — the Analyses panel (`AnalysesTool`, `AnalysesListViewModel`) is the reference for size, spacing and style.
- **Dock defaults:** Tuning tabbed **behind Analyses**; Optimizer tabbed **behind Tuning**. New `DockPanelIds`
  (`Tuning`, `Optimizer`) — those strings are a file format (`DockLayoutSchema` says why).
- **Readouts carry no explanatory prose.** A value or a short status; a note only when there is no value.
- **Every action that changes the design is one undo step** and says what it changed in the status line.
- **Keyboard:** arrows nudge the focused slider by one step, Shift+arrows by ten, Page Up/Down by a tenth of the range;
  Enter in the number box commits; Esc reverts the box.

---

## 4. Testing rules for the whole series

- Write the **minimal** tests: one per claim, not per rung; trim `InlineData`.
- Run **only the test classes the phase adds or touches** (`--filter "FullyQualifiedName~<Class>"`), never the full
  suite and never all of `Ui.Tests`. Rebuild each test project before using `--no-build`.
- **No new timing benchmark tests.** Where a phase makes a performance claim, assert a **counter** (evaluations run,
  redraws skipped, cache hits), not a time.
- Optimizer tests use **cheap closed-form problems** (an L-section match, an RC low-pass, a resistive divider with a
  known optimum, Rosenbrock, a deliberately infeasible goal) — never an EM or long HB run.

---

## 5. Leave room for yield (the next series)

- A complex value's parts (D18) are ordinary entries, so a tolerance on `mag(ZL)` needs nothing new; a sample is
  composed into the whole value the way an optimizer point is.
- A variable entry must be able to grow a **tolerance** (± % or ± absolute, distribution) without a format break —
  the `.csch` block and the `tune` directive must accept and ignore unknown keys within a format version.
- Goals must be evaluable on **many sample points** in one batch (TO-6's evaluator takes a batch of points).
- The evaluation service (TO-2) must be safe for **concurrent** evaluations — yield needs hundreds per run.

---

## 6. Housekeeping for every phase

- Findings go in the relevant `RESOLVED.md`, never in a `CLAUDE.md`. Paraphrase the owner; never quote.
- No commercial vendor or product names anywhere — code, docs, tests, commit text.
- No commits unless the owner asks; commits go to `main`.
- Edit `docs/user/src` doc sources where a phase changes user-visible behavior; **do not run DocGen** — the owner
  regenerates at the end of the series.
