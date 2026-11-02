# Brief YA-10 — The Yield panel

**Series:** `brief-yield-0-overview.md` (D1, D4, D7, D8, D14, §3) · **Tag:** `R-ya10-<m>`
**Depends on:** YA-5, YA-6, YA-9 (YA-12 adds the Centering mode)
**Area:** a new `src/Ui/Yield/` (`YieldPanelViewModel` and row/table view models), `src/Ui/Views/Yield/`,
`src/Ui/Docking/DockLayoutSchema.cs` (`Yield = "Yield"`), `DockLayoutDefaults`, `CircuitRfDockFactory`,
`src/Ui/ViewModels/WorkspaceViewModel.Yield.cs` (routing, as `WorkspaceViewModel.Tuning.cs`), `src/Ui/Tuning/`
(the shared variable rows), `src/Design/Optimization/TuningSetupEdits.cs` (pure edits → one undo step),
`docs/user/src/reference/` (a new Yield page)

---

## 1. Goal

A designer sets tolerances, picks specs, runs a Monte Carlo, a yield analysis or a corner sweep, watches the yield
converge, finds the failing trials and acts on them — without leaving one panel, and with the same UX bar Tuning and
Optimizer set. **Reference panels:** the Optimizer panel (`src/Ui/Optimization/`) for structure, the Analyses panel
for toolbar style.

## 2. Requirements

**R-ya10-1 — Placement.** A new dock panel **Yield**, tabbed behind Optimizer (overview §3), following the focused
schematic like Tuning/Optimizer (empty with one line otherwise). Header names the schematic.

**R-ya10-2 — Mode.** A segmented control: **Monte Carlo · Yield · Corners** (YA-12 adds **Centering**). The mode
changes which sections show and what Run does; the variable and spec lists are shared.

**R-ya10-3 — Variables (with the Tolerance column).** The same entries as Tuning/Optimizer (D1), listed with
**Stat** checkbox, **Distribution** (drop-down), **Spread** (a compact editor: `± 2 %`, `σ 1 Ω`, `45 … 55 Ω`,
`4 … 8 by 2`, `trunc 3σ`), the nominal in the parameter's unit, and the share-of-variance bar once contributions have
been computed. Rows are added the way Tuning adds them (Inspector toggle, canvas right-click ▸ **Tolerance…**, the
search list); adding a tolerance to a row not yet in the setup creates its entry with `tune`/`opt` off. Every edit is
one undo step through `TuningSetupEdits` → the schematic's undo stack, and `check`'s refusals (mixed complex pairs,
lognorm of a non-positive value, …) appear as the edit's refusal, inline, in the same words. A **Correlations…**
button opens a small grid of the stat entries (ρ cells; a repaired matrix shows the repair). A **Kit statistics**
row appears when the design's kits declare statistical sections: Process ✓ Mismatch ✓, the selected section, and a
link to the corner picker when none is selected (YA-3 R-ya3-5).

**R-ya10-4 — Specs.** The goals (D4), each with a **Use** toggle (Opt / Yield / Both) and its per-goal yield and
interval once a run has data. Goals are edited in the Optimizer: **Edit goals…** focuses it with the goal selected.
"Add as goal…" from a trace (TO-9) is unchanged and lands here too.

**R-ya10-5 — Run settings.** Trials, Seed (with a dice button for a new one), Sampling, Target %, Confidence, Auto-stop,
Did-not-evaluate (Fail / Warn), Save (Auto / Scalars / All / first N), Parallel. Collapsed to one summary line by
default (`500 trials · seed 1 · random · target 95 %`), expanded by a chevron. Persisted in the `statistics` line.

**R-ya10-6 — Running.** Run / Pause / Stop in the toolbar (D14). The readout: **yield % with its interval as a bar**
against the target marker, trials done / cap, did-not-evaluate count, elapsed and an estimate to finish. Per-spec
yields update in the spec list. No prose — a value or a short status.

**R-ya10-7 — The trial table.** After (and during) a run: trial #, pass, worst goal, its margin, did-not-evaluate
reason; sortable; filter Fail only. Selecting a row is YA-9's linked selection (every bound plot highlights it); the
context menu carries YA-9's actions: **Send trial to Tuning**, **Re-run trial**, **Copy values**, **Save as corner…**.
**Contributions** button: computes them once (YA-4 R-ya4-9) for the selected goal and fills the share bars.

**R-ya10-8 — One-click yield display.** **Open yield display** creates (or focuses) a Data Display bound to the
`.yield.npy` source with: a pass/fail family per yield goal with spec lines and the nominal, a histogram of each
goal's `worst` value with spec lines, a yield sensitivity histogram for the top contributor (or the first stat entry),
and the statistics table. Built through YA-8/YA-9's own trace-writing code — this button composes, it does not draw.
Offered once at the end of the first run on a schematic with no yield display ("Open yield display"), never
automatically opened.

**R-ya10-9 — Corners mode.** The corner list (name, enabled, kit selections, temp, values; statistical corners
marked with their trial), **Generate…** (YA-6's generator in a small dialog: pick axes and options, temperatures,
VAR values — it shows the count before writing), Run, and the result as a corner × goal margin grid, failing cells
marked, worst corner per goal in bold. **MC at each corner** toggle (YA-6 R-ya6-3).

**R-ya10-10 — Parity.** The panel calls `StatisticalRun`/`CornerRun` and nothing else. Gate: the headless panel view
model's DataSet equals the `yield` verb's, byte for byte, for the same file and seed.

## 3. Gates (minimal; run only these classes — never all of `Ui.Tests`)
- `YieldPanelEditTests` — a tolerance edit is one undo step and round-trips to the same `tune` line `check` accepts;
  a mixed complex pair is refused inline with `check`'s sentence.
- `YieldPanelRunTests` — run, pause, resume and stop on the divider: the final DataSet equals an uninterrupted run's.
- `YieldPanelParityTests` — R-ya10-10.
- `YieldDisplayComposeTests` — Open yield display creates the four plot kinds bound to the `.yield.npy` source.
- `YieldPanelDockTests` — a layout saved before YA-10 gains Yield behind Optimizer (`DockLayoutDefaults.WithMissingPanelsFilled`, as `RailedWidenTests` checks it for the Optimizer).
