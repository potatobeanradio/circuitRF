# Analysis Authoring — Step 3: the Analyses list panel (dock + modal) (Claude Code / Sonnet)

The first visible piece: an **Analyses list** tied to the active schematic — a dock panel (with a modal-host
option) listing the schematic's analyses with **Enabled toggle, name, type, one-line summary**, and actions
**Add / Edit / Remove / Enable / Duplicate / reorder**, plus a friendly **empty state**. **This brief is
step 3** — the list + its VM. The **Add/Edit per-type form** is step 4 (so Add/Edit open a placeholder here),
**copy/paste + templates** are step 5. Read `analysis-authoring.md` §4.1 first. Sub-gated; **report and stop
between every layer.** Firewall green.

> Read first: `docs/design/analysis-authoring.md` §4.1 (the list panel — rows, actions, enabled-vs-present,
> dock+modal, empty state). Context code: `src/Ui/ViewModels/Dock/ProjectTreeTool.cs` (the **dock-tool
> pattern to mirror** — `Tool` base, observable collections, `[RelayCommand]`s, active-target wiring),
> `src/Ui/Schematic/EditableSchematic.cs` (`SchematicEditModel` — the in-memory analyses list from step 2),
> `src/Core/Design/Analysis.cs` (the `Analysis` types — `Name`, type, fields for the summary),
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (`OnDocumentDockPropertyChanged` / `PropertiesTool.
> SetActiveSchematic` — **how a tool tracks the active schematic**; mirror it for the analyses panel),
> `src/Ui/ViewModels/Dialogs/*` + `SavePlanDialog` (the modal-host + dual-host pattern). Design docs win on
> any conflict.

## The spine (do not violate)
- **Tied to the active schematic** — the panel shows the active `SchematicDocument`'s analyses, and follows
  the active-document change (reuse the same active-schematic tracking the Properties panel uses). No active
  schematic → a neutral empty state.
- **Operates on the model's analyses list** (step 2) — Add/Remove/Enable/Duplicate/reorder mutate the
  `SchematicEditModel`'s analyses; the schematic becomes dirty; saved via the existing `.csch` path.
- **Dock + modal, one VM** — the list VM is hosted in a dock `Tool` AND can be shown as a modal dialog
  ("Setup Analyses…"); one VM, two hosts (the dual-host pattern).
- **HIG** — friendly empty state, plain-language summaries, clear actions; this is the calm list, the scary
  fields are step 4.
- **Scope fence (step 3):** the list + VM + actions. Add/Edit open a **placeholder** (step 4 builds the real
  form). NO per-type form, NO copy/paste, NO templates, NO run changes.

---

## LAYER 1 — the Analyses list VM + row VM

1. **`AnalysisRowViewModel`** wrapping one `Analysis`: `Enabled` (toggle — mutates the model, marks dirty),
   `Name`, `TypeLabel` (DC/SP/HB), and a **`Summary`** one-liner derived from the analysis (e.g.
   "SP · 1–10 GHz, 3 segments" / "HB · f0=2 GHz, 7 harmonics" / "DC"). The summary is computed from the
   analysis fields — a small per-type formatter.
2. **`AnalysesListViewModel`** holding an `ObservableCollection<AnalysisRowViewModel>` for the active
   schematic, plus commands: **Add** (opens the step-4 form — placeholder for now), **Edit** (double-click/
   pencil — placeholder), **Remove**, **Duplicate** (clone the `Analysis` + name-collision resolution →
   "SP1 copy"/next free), **MoveUp/MoveDown** (reorder; analyses run in list order), and **Enable** per row.
   All mutate the model's analyses list + mark dirty.
3. **Active-schematic binding:** a `SetActiveSchematic(SchematicViewModel?)` that rebinds the list to that
   schematic's analyses (mirror `PropertiesTool.SetActiveSchematic`); null → empty.

**Layer 1 gate:** headless-ish VM tests — the list reflects a model's analyses; Add(stub)/Remove/Duplicate/
reorder/Enable mutate the model and the collection; Duplicate resolves names; switching active schematic
rebinds. Report.

---

## LAYER 2 — the dock panel + modal host + empty state

1. **`AnalysesTool`** (Dock `Tool`, mirror `ProjectTreeTool`) hosting the `AnalysesListViewModel` — a panel in
   the dock layout (near Properties/Project Tree). Wire it into the dock factory + the active-document hook
   (`OnDocumentDockPropertyChanged` → `AnalysesTool.SetActiveSchematic(activeVm)`), exactly like the
   Properties panel.
2. **The view:** a list with per-row **Enabled checkbox · name · type · summary**, a toolbar/footer with
   **＋ Add · Edit · Remove · Duplicate · ↑ ↓**. **Empty state:** "No analyses yet — Add one to simulate this
   schematic" + a prominent **＋ Add Analysis**. No-active-schematic state: a neutral "Open a schematic to
   set up analyses."
3. **Modal host:** a **"Setup Analyses…" command** (menu/toolbar) that shows the **same `AnalysesListViewModel`
   in a modal dialog** (reuse the dual-host pattern) — for users who prefer a focused modal. One VM, two
   hosts.

**Layer 2 gate:** the Analyses dock panel shows the active schematic's analyses (rows with enabled/name/type/
summary); switching tabs reflows to that schematic; the empty + no-schematic states show; "Setup Analyses…"
opens the same list modally; Add/Edit open a placeholder (step-4 form pending). Report (screenshot
description).

## Acceptance (step 3)
1. An `AnalysesListViewModel` + `AnalysisRowViewModel` reflect the active schematic's analyses (enabled/name/
   type/summary) and support Add(placeholder)/Edit(placeholder)/Remove/Duplicate(name-resolved)/reorder/
   Enable, mutating the model + marking dirty.
2. An `AnalysesTool` dock panel hosts it, tracks the active schematic (mirroring Properties), with empty +
   no-schematic states; a "Setup Analyses…" modal shows the same VM.
3. `dotnet build`/`dotnet test` green; firewall green; **no per-type form (step 4), no copy/paste, no
   templates, no run changes**; nothing else regresses.

## Guardrails
- **Mirror `ProjectTreeTool`/`PropertiesTool`** — dock-tool shape + active-schematic tracking; don't invent a
  new hosting pattern.
- **Operate on the model's analyses list** (step 2); mutations mark the schematic dirty.
- **One VM, two hosts** (dock + modal); Add/Edit are placeholders this step.
- **HIG empty state** guides the first action.
- **Scope fence:** list + VM + actions only.
- Sub-gate the two layers; report and stop between each.
- Update `analysis-authoring.md` §7 status (step 3 done) and `src/Ui/CLAUDE.md` (the Analyses panel: active-
  schematic-bound, dock+modal, operates on the model list).

*Exit: a calm, HIG-friendly Analyses list — dock panel (+ modal) bound to the active schematic, with add/
remove/enable/duplicate/reorder — ready for the per-analysis form (step 4) and reuse (step 5) to plug in.*
