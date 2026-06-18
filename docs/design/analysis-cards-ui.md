# Analysis Simulation Cards — UI Design

Status: reflects shipped behavior (alpha). Companion docs: `analysis-authoring.md` (the Add/Edit
dialog internals), `parametric-sweep-ux.md` (the locked decisions behind sweep semantics), and
`family-curves.md` (how a swept cube renders as a family of curves in the Data Display). This
doc is the higher-level "how the cards work" reference we'll base user docs on.

## What it is

The **Analyses panel** is the list of simulation **cards** attached to a schematic. Each card is one
analysis the engine will run when the user simulates: a DC operating point, an S-parameter sweep, a
Harmonic Balance run, a Loadpull, or a Loadpull-Pursuit. A schematic can hold any number of cards;
they run top-to-bottom in listed order and each writes its own results.

The panel appears in two places, both driven by the **same** view-model instance so edits in one are
instantly reflected in the other:
- docked in the left **Properties Inspector** pane (the everyday, non-modal home), and
- in the **"Setup Analyses…"** modal dialog (a larger working surface).

The panel header shows the active schematic's filename, with a **▶ Run** button to its left and a
button toolbar on the row below (Add, Edit, Duplicate, Remove, Move Up/Down, Copy, Copy-All, Paste,
Insert/Save Template, Help).

## How it works for the user

**Cards.** Each card shows an Enabled checkbox, a small type badge (DC / SP / HB / LP / LPP / SW), the
analysis name, and a one-line summary. Disabling a card's checkbox keeps the card but excludes it from
the run. Double-clicking a card (or selecting it and pressing Edit) opens the Edit Analysis dialog.

**Parametric sweeps.** A sweep is not a standalone analysis — it *wraps* another analysis and re-runs
it across a swept variable (e.g. run a DC at each Vds). Sweeps appear as indented **SW** cards directly
beneath the analysis they wrap, forming a visually grouped **chain**: the base analysis on top, then
its sweep axes listed **innermost → outermost** going down. Convention: the **innermost sweep axis is
the default plot X-axis**; the next axis out becomes the plot's curve family. A chain can stack several
sweep axes (e.g. a DC swept over Vds then Vgs → an I–V family).

**Reordering.** Move Up / Move Down behave by selection:
- a **base** card selected → the whole chain moves as a unit relative to other analyses;
- a **sweep** card selected → that sweep moves **inner/outer within its own chain** (Up = more inner,
  Down = more outer), which also changes the default plot X-axis.
The moved card keeps its selection highlight so the user can see what happened.

**Enabling/disabling within a chain.** Each sweep axis has its own Enabled checkbox independent of the
base. Disabling a sweep axis drops that dimension from the run (its Start/Stop/Step are retained for
when it's re-enabled); disabling the base makes the whole chain inert; disabling everything runs the
base alone. (Full truth table in `parametric-sweep-ux.md`.)

**Editing.** Edit/double-click on **any** member of a chain — base or any sweep — opens the dialog at
the **chain root**: the base analysis with all its sweep axes listed and reorderable. There is no
"edit one sweep in isolation" mode. OK rebuilds the entire chain and replaces the old one as a single
undoable edit.

**Duplicate / Copy / Paste / Templates.**
- Duplicate clones the selected card (name → "… copy").
- Copy/Copy-All put analyses on the clipboard as JSON; copying a **base** automatically includes its
  whole sweep chain. Paste appends them, resolving name collisions and **re-linking** the sweep→inner
  references. A lone copied sweep can be pasted onto a different selected analysis to re-attach it.
- Save/Insert Template persist a bundle of analyses to a `.canl` file for reuse across schematics.

**Running.** The ▶ Run button simulates the panel's schematic. Crucially, the panel **retains the last
schematic viewed** — focusing a Data Display or symbol/cell tab does not blank it — so the user can sit
on a Data Display, tweak an analysis, hit Run, and watch the plot refresh without switching tabs.

## Architecture (high level)

**Model layer (Core, no UI).** `Analysis` is the base type (`Name`, `Enabled`); concrete types are
`DcAnalysis`, `SParameterAnalysis`, `HarmonicBalanceAnalysis`, `Loadpull*`, and
`ParametricSweepAnalysis`. A sweep carries its swept variable, its values (always materialized) plus an
optional compact `SweepSpec` (Start/Stop/Step + Lin/Log for round-trip fidelity), and an immutable
**`InnerAnalysisName`** pointing at the analysis it wraps. A chain is therefore a singly-linked list by
name: `base ← sweep_inner ← sweep_outer`. In the schematic's `Analyses` list a chain is stored
contiguously as `[base, innermost_sweep, …, outermost_sweep]`. There is no Avalonia dependency here
(enforced by the architectural firewall).

**View-model layer (UI).**
- `AnalysesListViewModel` — the panel. Owns the `Rows`, selection, all toolbar commands, and the
  `RunRequested` event. Subscribes to the schematic's edit-model `Changed` and rebuilds `Rows` on any
  mutation. One instance is shared by the dock tool and the modal.
- `AnalysisRowViewModel` — one card. Wraps an `Analysis`; exposes Enabled (routes through a command),
  the type badge, the indent flag for sweeps, and the summary string.
- `AnalysisEditorViewModel` — the Add/Edit dialog staging model. Holds the type, name, the base
  Enabled flag, per-type body editors, and an ordered list of `SweepAxisRowViewModel`. On edit it
  resolves the selected card to the chain root and loads every sweep axis; `BuildAnalyses()` emits the
  rebuilt chain `[base, sweep0, …]` with each sweep's `InnerAnalysisName` linked bottom-up.
- `SweepAxisRowViewModel` — one sweep axis inside the editor (variable, mode = StepSize/PointCount/List,
  range, Lin/Log, Enabled, live preview).

**Hosting.** The dock tool `AnalysesTool` holds the shared `AnalysesListViewModel`.
`WorkspaceViewModel` is the orchestrator: its document-activation handler updates the panel's active
schematic, but only when a **schematic** document becomes active — it retains the last one
(`_lastActiveSchematicDoc`) otherwise, so non-schematic tabs don't blank the panel. It also wires the
panel's `RunRequested` to the run pipeline.

**Commands (all undoable, run through the schematic's undo stack).** `AddAnalysesCommand`,
`EditAnalysisChainCommand`, `RemoveAnalysisCommand`, `DuplicateAnalysisCommand`,
`MoveAnalysisChainCommand` (whole-chain move), `ReorderSweepInChainCommand` (inner/outer within a
chain, reconstructing the immutable sweep instances and re-linking), and `PasteAnalysesCommand`
(collision-resolving name + inner-reference remap). Because `InnerAnalysisName` is immutable, any
reorder/relink rebuilds the affected `ParametricSweepAnalysis` instances rather than mutating them.

**Dispatch.** At run time the pure-Core helper `AnalysisChain` resolves which analyses are runnable
roots and how Enabled flags collapse a chain (disabled sweeps drop their dimension; disabled base is
inert). The parametric-sweep engine drives the inner analysis once per swept point, overriding the
global variable and re-elaborating, then stacks the results into an N-dimensional cube whose axes are
the sweep variables (innermost last → the default plot X).

**Persistence.** Analyses live in the schematic `.csch`. The clipboard and `.canl` templates share one
serializer (`AnalysisSerialization`) that round-trips the chain including each sweep's inner reference
and compact spec. No back-compat shims (alpha): the loader rejects format-version mismatches.

## Run flow (end to end)

Run button → `AnalysesListViewModel.RunRequested` → `WorkspaceViewModel` runs the retained schematic
document: extract + write `netlist.cnl`, run the engine on a background thread
(`SchematicRunService.RunNetlist`), write each base analysis's results to a stable
`results/<key>/<name>.npy`, then refresh any open Data Displays in place so their plots re-render
against the new data. (The same pipeline backs the menu/toolbar Run.)

## Key files

- `src/Core/Design/Analysis.cs` — analysis model incl. `ParametricSweepAnalysis`, `SweepSpec`.
- `src/Core/Design/AnalysisChain.cs` — runnable-root + Enabled-collapse resolution.
- `src/Ui/ViewModels/AnalysesListViewModel.cs` — the panel.
- `src/Ui/ViewModels/AnalysisRowViewModel.cs` — a card.
- `src/Ui/ViewModels/AnalysisEditorViewModel.cs` + `SweepAxisRowViewModel.cs` — the editor.
- `src/Ui/Views/Analyses/AnalysesListView.axaml` (+ code-behind) — the panel view.
- `src/Ui/Commands/Analysis/*` — the undoable commands listed above.
- `src/Ui/ViewModels/Dock/AnalysesTool.cs` — dock host; `WorkspaceViewModel.cs` — activation + run wiring.
