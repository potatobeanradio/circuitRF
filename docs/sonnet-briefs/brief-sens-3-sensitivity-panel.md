# Brief SENS-3 — The Sensitivity panel: the Yield panel's view, its own tab, before the Optimizer

**Series:** SENS-1 … SENS-3 (decisions in `brief-sens-1-sensitivity-run.md` §0) · **Tag:** `R-sens3-<n>`
**Area:** new `src/Ui/ViewModels/Dock/SensitivityTool.cs`, `src/Ui/Docking/DockLayoutSchema.cs` (`DockPanelIds`),
`src/Ui/Docking/DockLayoutDefaults.cs`, `src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs`,
`src/Ui/ViewModels/WorkspaceViewModel.Yield.cs` (routing, beside the Yield panel's), new
`src/Ui/Yield/YieldPanelViewModel.Sensitivity.cs` + `SensitivityRowViewModel.cs` + `SnpMatrixTileViewModel.cs`,
`src/Ui/Yield/YieldPanelViewModel.cs` (`YieldMode`, the mode set), `src/Ui/Views/Yield/YieldToolView.axaml`,
`src/Ui/Views/WorkspaceWindow.axaml` (both menus), `src/Ui/Optimization/OptimizerPanelViewModel.Run.cs` +
`src/Ui/Views/Optimization/OptimizerToolView.axaml` (the Sensitivity button), `src/Render/DataDisplay/
ContributionParetoPreset.cs`, `tools/UiSnapshot/Program.cs` (`--yield`), `docs/user/src/reference/yield.md` (+ a
Sensitivity page and `_nav.txt`), `examples/Yield/`
**Depends on:** SENS-1 and SENS-2.

---

## 0. The gap

Even once SENS-1 and SENS-2 exist headlessly, a designer working by hand has to know to look for them. The order a
designer works in is: understand what the design is sensitive to, tune or optimize the few things that matter, then
check yield. The panels should present that order: a **Sensitivity** tab that is visible without being looked for,
placed before the Optimizer.

## 1. Requirements

**R-sens3-1 — One view, two dock tools.** `SensitivityTool` (`DockPanelIds.Sensitivity = "Sensitivity"`, added to
`DockPanelIds.All`) hosts its own `YieldPanelViewModel` and the same `YieldToolView`. The view model gains an
`AvailableModes` set: the Yield tool's is the five it has today; the Sensitivity tool's is `{ Sensitivity }` (a new
`YieldMode.Sensitivity`). The mode radio row shows only the available modes, and with one mode it is not shown at all
(the space is not left empty — the toolbar moves up into that row permanently, not by an `IsVisible` toggle in a
row of buttons). No second view and no copied XAML: a control the Sensitivity mode does not use is hidden by mode,
exactly as Centering and DOE already hide what they do not use (`ShowVariableTolerances`).

**R-sens3-2 — Both panels edit one design.** Each panel watches its schematic's `EditModel.Changed` and edits through
`Execute` onto that schematic's undo stack (`YieldPanelViewModel.Watch`/`Execute`) — so a tolerance set in Sensitivity
is in Yield at once, and ⌘Z undoes it from either. Neither keeps a copy of the tuning block. Routing follows the
focused schematic as the Yield panel's does (`RouteYieldPanel` / `ClearYieldPanel`): add the Sensitivity panel to
the same calls, and keep the Optimizer/Yield bench hand-off (`WorkspaceViewModel.Yield.cs` ~128) pointing at the same
schematic for all three. The `IToleranceSurface` relay (the Inspector's and canvas's tolerance actions) keeps pointing
at the Yield panel.

**R-sens3-3 — Placement: Tuning, Sensitivity, Optimizer, Yield.** In BOTH shipped default layouts
(`DockLayoutDefaults`, ~lines 101-105 and 134-138) Sensitivity gets `Order = 3` and Optimizer and Yield shift to 4
and 5. A saved layout that predates the panel must gain it **immediately before the Optimizer**, not as the last tab:
today `TabbedBehind` + `BesideItsSibling` append a newcomer after its sibling's dock's last tab. Add an insert-before
rule (`TabbedBefore[Sensitivity] = Optimizer`, falling back to after Tuning, then to the default) and renumber the
orders it passes. **Keep the choice in those two tables and the default `Order` values only** — the owner may move
the tab to directly before Yield after using it, and that must be a change to those lines alone. Menus: a
"Sensitivity" item before "Optimizer" in both the native menu (`WorkspaceWindow.axaml` ~596) and the window menu
(~1719), on the same `ShowToolPanelCommand`.

**R-sens3-4 — The Sensitivity mode's controls.** One settings strip, the `sensitivity` line's values (R-sens1-5),
each written to the line as one undo step only when changed from its default:
- **Factors**: `All` / `Toleranced` / `Designable` (`all` / `stat` / `opt`).
- **Step** `1 %` and **σ** `1` (number boxes).
- **S-parameters** checkbox (`snp`, the master switch of R-sens2-5a), and `0.1 dB` / `1°` beside it, disabled while
  unticked. The disabled boxes keep their place; nothing in the strip shifts.
- **S-parameter blocks** — under the strip, one compact row per SnP instance in the design, there BEFORE any run so a
  fixed block can be left out before the run's cost is spent: an **Include** checkbox, the instance, `4 ports · tied ·
  20 factors`. Unticking writes `snpexclude` (R-sens2-5a) as one undo step; the cost estimate updates at once. With
  the master unticked every Include box is disabled but keeps its tick, so ticking the master back restores the
  choices. The section is absent (not empty) on a design with no SnP; with more than about five instances it shows
  five and a "Show all n" toggle.

Goals: the existing goal rows. Run / Pause / Stop: the existing toolbar. Progress: `n of 2k + 1 simulations` in the
status line, with the run's cost estimate shown before it starts (from `Describe()`).

**R-sens3-5 — The ranked list.** The variable list, in this mode, shows one row per contributor for the selected goal
(goal picker as today), sorted by share, with columns: label (R-sens2-4's label), basis, signed Δ, share bar (the
existing contribution bar on `YieldVariableRowViewModel`), and a status glyph for `nonlinear` / `one-sided` / `failed`
whose tooltip is the reason. A **By: Entry / Port / Instance** toggle (default Entry) regroups the same result
(R-sens2-6) with no re-run; at Instance an SnP row expands to its entries and an entry to its dB and phase keys. At
Port the share column header reads "share (overlapping)". Each goal row shows m₀, σ_g and, when SENS-1 D5 allows it,
the pass estimate labelled `estimated` — no explanatory text under it.

Row actions (context menu): **Add tolerance…** (the existing add-tolerance flow, for a row whose basis is a step),
**Send to Tuning** (adds the key to the Tuning panel), **Use in Optimizer** (sets `opt=1` with the catalog's default
range). A button **Run Yield on these** switches to the Yield tab in Monte Carlo mode; when some ranked factors have
no tolerance, the Yield panel's status line says how many.

An excluded instance appears in every grouping as one greyed row at the bottom of the list, basis `excluded`, no Δ
and no share bar, with its Include box ticked off on the row itself — the designer sees it was left out on purpose and
can bring it back from the list without scrolling up. A run is not re-run automatically when an instance is
re-included (D7); the status line says the result is out of date until the next Run.

**R-sens3-6 — The SnP matrix tile.** Selecting an SnP instance (or any of its entries) shows a tile under the list: an
N×N grid, each cell coloured by that entry's share for the selected goal (one sequential scale for the whole tile,
legend 0 to the tile's maximum), the selected port's row and column outlined, and the selected entry's cell
outlined. A tied instance colours S(i,j) and S(j,i) as one. On the tile: an **Include** checkbox (the same setting as
the instance's row in S-parameter blocks — one value, two places), and **Tie reciprocal pairs (S21 = S12)**
checkbox, its automatic state and reason in the tooltip (`auto: worst |S(i,j) − S(j,i)| = 0.0021`), writing
`Reciprocal=` on the instance as one undo step. Changing it when `tune` lines name keys that would no longer exist asks
once, listing those lines, and removes them in the same undo step; cancelling leaves everything as it was. Above
roughly 8 ports the grid shrinks its cells rather than scrolling; the share is in each cell's tooltip.

**R-sens3-7 — The display.** The display button writes `<design>.sens.cdd` beside the design and opens it: per goal, the
Pareto (`ContributionParetoPreset` over the `sens` group, by the toggle's level; no cumulative line at Port) — the same
preset Contributions uses, now taking the group as a parameter. Nothing new to draw.

**R-sens3-8 — The Optimizer's Sensitivity button.** With a finished run it does what it does today (TO-8, at the best
point over the opt ranges). With no run it is enabled and opens the Sensitivity panel on the same schematic, tooltip
"Open the Sensitivity panel". There is one nominal sensitivity, not two.

**R-sens3-9 — Visual verification.** `tools/UiSnapshot` gains `--yield <file.csch> [--mode sensitivity] [--run]
[--by entry|port|instance] [--select <key>]`, hosting the panel as the shell hosts it (the `--optimizer` pattern,
~line 591). Render and look at: the Sensitivity panel before a run (S-parameter blocks with one instance unticked,
and with the master unticked), after a run at Entry and at Instance (the excluded instance's greyed row), the matrix
tile on a tied and an untied instance, and the Yield panel unchanged (five modes) — light and dark. List the PNGs in
the completion note.

**R-sens3-10 — Docs and example.** `docs/user/src/reference/` gains a Sensitivity page (what it answers, the basis
column, the three levels, the matrix tile, the flags, and the order: Sensitivity → Tuning/Optimizer → Yield), linked
from `yield.md`, `optimization.md` and `_nav.txt`. Rules for the text: a reader's question first, no change history,
no "the design's own". `examples/Yield/` gains a cell with a reciprocal SnP (a spiral `.s2p` from
`examples/Model Composer/passive-spiral/s/`) and a non-reciprocal one (the amplifier `.s2p` from
`examples/S-Parameters/Amplifier/schematic/`) in one circuit with yield goals, and a `sensitivity` line, so the
example shows a tied and an untied tile; its README says what to click. Do not run DocGen; the owner regenerates at
the end of the series — name in the completion note which figures the change will move.

## 2. Gates (minimal; run only these classes)

- `SensitivityPanelTests` (`tests/Ui.Tests/Statistics/`, beside `YieldPanelTests`): a tolerance added from the Sensitivity
  panel appears in the Yield panel's rows without a refresh, and one ⌘Z removes it; the Entry/Port/Instance toggle
  regroups without a second run (evaluation count unchanged); unticking one instance's Include writes `snpexclude` as
  one undo step and lowers the cost estimate by twice its factor count, and unticking the master disables every
  Include box while keeping its tick.
- the dock placement, in `SensitivityPanelTests` on the pattern of `YieldPanelTests`' own placement test (~line 246): both defaults order Tuning, Sensitivity, Optimizer, Yield; a
  saved layout without Sensitivity gains it immediately before the Optimizer, also when the Optimizer was moved to the
  right column.
- `SnpMatrixTileTests`: toggling Tie with a now-invalid `tune` line removes it in the same undo step.

## 3. Out of scope

Moving the tab to before Yield (a later owner decision; R-sens3-3 keeps it a two-table change); a frequency-resolved
sensitivity plot; a Contributions roll-up by instance in the Yield panel.
