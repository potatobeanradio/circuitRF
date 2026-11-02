# Brief MC-14 — The Model Composer window: lifecycle, layout, background tasks; Data, Pads and Network steps

**Series:** `brief-modelcomposer-0-overview.md` (§3, D2, D5, D6, D13) · **Tag:** `R-mc14-<m>` · **Depends on:** MC-13
**Area:** a new `src/Ui/ModelComposer/` (view models) and `src/Ui/Views/ModelComposer/` (views);
`src/Ui/Views/WorkspaceWindow.axaml` (the Tools menu entry); `docs/user/src/reference/model-composer.md` (new,
source only)
**Read first:** `docs/design/railrf.md` §11 (window rules, panel lamps, save and close); `docs/design/smith-chart.md`
(the schematic canvas reused, clipboard both ways); `docs/design/ui-design.md`; the owner's UI rules in the
overview §3

---

## 1. Goal

A window that opens, saves and resumes a `.cmc` and stays responsive through every long task. Its first three
steps take a user from raw files to a fitted, de-embedded intrinsic data set. **This layout is the first
iteration — the owner will revise it**, so each panel is its own view and rearranging them is cheap.

## 2. Requirements

**R-mc14-1 — Lifecycle.**
- **Tools ▸ Model Composer…** opens a window on a new document: a chooser for **Active** or **Passive**, then
  Save As on first save. **Open** reads a `.cmc`; a `.cmc` in the workspace tree opens on double-click, as a
  `.crail` does.
- One window per `.cmc`; non-modal; golden-ratio opening size; a dirty bullet in the title.
- **Save** (⌘S) and **Save As** (⇧⌘S). Closing with unsaved work asks, through railRF's dialog.
- Reopening restores the panels, the selected step and the bias cursor.

**R-mc14-2 — Layout** (first iteration):
```
┌ Model Composer · <name> ───────────────────────────────────────────────────────────┐
│ [Open][Save] │ ●Steps ●Plots ●Network ●Values │ Vgs ▾ Vds ▾ T ▾ Q ▾ │ [Run ▾][Cancel] │
├────────┬───────────────────────────────────────────┬───────────────────────────────┤
│ Data   │ [tab][tab][tab]…                          │  network schematic            │
│ Pads   │ ┌───────────────────────────────────────┐ │  (the schematic canvas)       │
│ Network│ │ PlotControl                           │ │ ───────────────────────────── │
│ Current│ │                                       │ │  values: name · value · lock  │
│ Charge │ └───────────────────────────────────────┘ │          min · max · tie ·    │
│ Thermal│ bias plane: ○ extrinsic ● intrinsic × excl│          scaling · ±error     │
│ Traps  │                                           │                               │
│ Check  │                                           │                               │
│ Valid. │                                           │                               │
│ Export │                                           │                               │
├────────┴───────────────────────────────────────────┴───────────────────────────────┤
│ one-line status · warning · error                        [progress ▬▬▬▭▭] [Cancel] │
└────────────────────────────────────────────────────────────────────────────────────┘
```
- **Panels.** Step rail, plots, network and values; each has a lamp on the toolbar (railRF §11.3 item 5),
  persisted in the `.cmc`. At least one panel always shows.
- **Splitters** resize horizontally and vertically.
- **The step rail** lists the active or passive steps (overview §3). A step with a stale result shows a small
  marker. A step that cannot run yet is disabled, with its reason as the tooltip.
- **The bias cursor** (Vgs, Vds, T, quiescent point) drives every plot. It snaps to measured points, with
  arrow-key stepping.
- **Toolbar buttons** are icon buttons with tooltips, in the schematic/layout/3D editors' style. Button and
  combobox text is centred.

**R-mc14-3 — Background tasks (D13).**
- Every Run (import, de-embed, fit, check, validate, export) calls its `src/Design/Modeling` function on a worker
  with a `RunControl`.
- The status strip shows **one progress bar**, with the stage name and Cancel. Cancel leaves the previous result
  untouched.
- One task runs at a time per document. Starting another while one runs is a one-line refusal.
- The UI thread does only marshalling and drawing, and progress posts are coalesced (`RunControl`'s
  `MinReportIntervalMs`). Gate it with a **counter** of UI-thread work items per progress post, never a timing.

**R-mc14-4 — Minimal text (overview §3).** The status strip carries every message, one line each. Readouts are
values. No explanatory prose in any panel. Tooltips may say one sentence.

**R-mc14-5 — Undo.** Every edit is one undo step: a value, a lock, a bound, an exclusion, a step setting, a
network edit. A Run result is not an undo step — re-running restores, and the previous result is kept until the
next one replaces it.

**R-mc14-6 — The Data step.**
- **Import** opens the importer dialog for a kind: a file or folder chooser, the pattern or column map with a live
  preview of the first rows, and the conditions. It shows one line of what it read.
- A **data-set list** shows name, kind, ambient temperature, plane, quiescent point (and pre-pulse, when given)
  and point count. Selection
  drives the plots.
- **Plots:** I–V families (output and transfer); the **bias plane** with extrinsic ○ and intrinsic ● points (the
  [P1] Fig. 6.5 picture) and the hygiene flags; S-parameters at the cursor bias (rectangular and Smith).
- **Exclusion:** click, or lasso on the bias plane; a reason is chosen from a short list.
- `BasePeriphery` (`N0`, `W0`) is edited here, and its absence is shown as the one warning it causes.

**R-mc14-7 — The Pads step.** Mode (`None`, `OpenShort`, `TwoPorts`, `Network`) and files. The plot shows S at the
cursor bias before and after removal.

**R-mc14-8 — The Network step.**
- **Stock or own:** pick a stock network, or open, draw or paste one. The panel is the schematic canvas, with
  `.csch` copy/paste both ways (the Smith Chart pattern). A network without the six pins shows its one-line
  refusal.
- **Values table:** value, lock, min, max, tie, scaling rule (D9), and after a fit its ± error and the
  identifiability flag. A poorly determined value is marked, and its tooltip names the correlated partner.
- **Method** (cold-FET, multi-bias, fixed), its bias points (shown on the bias plane), weights and seed.
- **Plots:** intrinsic `Cgs`, `Cgd`, `Cds`, `Gm` and `Gds` against frequency at the cursor bias (flat when the
  network is right — [P1] §5.6.3); the flatness metric across bias as a map.

**R-mc14-9 — Passive Data and Topology steps.** Data: the block, or the family list with its geometry values;
analysis lines (MC-12 R-mc12-2); S plots. Topology: the top three proposals as thumbnails with their score, or the
user's own drawn in the network panel.

## 3. Not in this phase
The Current, Charge, Thermal, Traps, Check, Validate and Export steps and their plots (MC-15).

## 4. Gates (minimal tests, headless Avalonia harness; run only these classes)
- `ModelComposerLifecycleTests` — new → import → save → close → reopen restores data, panels, step and cursor
  with no source read (cache counter); closing dirty asks.
- `ModelComposerPanelsTests` — lamps hide and show their panels; the last lamp cannot be turned off; a hidden
  panel is written to the `.cmc`, and nothing is written when all are shown.
- `ModelComposerBackgroundTaskTests` — a long fit runs off the UI thread; Cancel leaves the previous network
  values; a second Run while one runs is refused; UI-thread work items per progress post ≤ 1 (counter).
- `ModelComposerNetworkPanelTests` — pasting a `.csch` selection with six pins installs it as the network; one
  without them shows the refusal; copying the network pastes into a schematic as a valid selection.
- `ModelComposerNoLogicTests` — part of MC-13's source scan, extended to this folder.
