# Phase 6h — Scratch Step 1: in-memory scratch schematic + New Schematic (⇧N) (Claude Code / Sonnet)

The first slice of the first-impression experience: **File → New Schematic (⇧N)** creates an **in-memory
scratch schematic** shown immediately in a content tab, editable right away with **no save prompt**. Establish
the **scratch-document concept** (in-memory, no disk path, dirty-tracked, invisible to the project tree) and
the launch welcome path. **This brief is ONLY step 1** — create + edit + dirty-track scratch documents. **No
save, no plan dialog, no materialize, no close/quit prompts, no autosave** — those are steps 2+. Read
`scratch-and-save-lifecycle.md` §1 first. Sub-gated; **report and stop between every layer.** Firewall green.

> Read first: `docs/design/scratch-and-save-lifecycle.md` §1 (scratch mode, the two worlds), §7 (order — this
> is step 1). Context code: `src/Ui/ViewModels/WorkspaceViewModel.cs` (the welcome Message already says
> "Create a New Schematic to get started" — but **there is no New Schematic command yet**; `NewSchematicAsync
> (cellNode)` is the *tree* path requiring an existing cell — do NOT reuse it for scratch; `_openDocsByPath`
> dedups by **absolute path** — scratch docs have **no path**, so they need separate tracking; `NewTab`,
> `OpenDocument`, `SetActiveUndoTarget`, `OnDocumentDockPropertyChanged`), `src/Ui/Schematic/
> SchematicEditModel` + `SchematicViewModel` + `SchematicDocument` (the schematic stack — a scratch schematic
> is a normal `SchematicViewModel` over a fresh `SchematicEditModel`, just with no file path),
> `src/Ui/Schematic/SchematicDocument.cs` (`IUndoableDocument`, `ViewModel`, `Messages`, `TriggerRebuild`),
> `src/Ui/Views/WorkspaceWindow.axaml` (File menu + KeyBindings — add New Schematic / ⇧⌘N / Ctrl+Shift+N).
> Design docs win on any conflict.

## The spine (do not violate)
- **Scratch = in-memory, no disk path.** A scratch schematic is a normal `SchematicViewModel` over a fresh
  `SchematicEditModel`, opened in a `SchematicDocument` tab — but with **no associated file path**. It is NOT
  in `_openDocsByPath` (that dict is keyed by absolute path; scratch has none).
- **The two worlds stay separate (§1.2):** scratch documents are **invisible to the project tree** (the tree
  reflects disk). Do not try to show scratch docs in the tree, and do not require a workspace to create one.
- **Dirty from creation.** A scratch schematic is unsaved by definition — mark it dirty and show an unsaved
  indicator on the tab. (Editing keeps it dirty; there is no save yet to clear it.)
- **No save anything in step 1.** Creating/editing only. Save, materialize, the plan dialog, close/quit
  prompts, and autosave are steps 2+ — do NOT build them. (It is acceptable in step 1 that closing a scratch
  tab loses it — step 2/3 add the prompt. Note it, don't fix it here.)
- **Scope fence:** New Schematic (⇧N) + scratch-doc concept + dirty indicator + launch state. Nothing else.

---

## LAYER 1 — scratch-document concept (model/tracking)

1. **Scratch identity on the document.** A scratch schematic needs to be distinguishable from a materialized
   one. Add a lightweight notion of "scratch / no path yet" — e.g. a `string? FilePath` on `SchematicDocument`
   (null = scratch) and/or an `IsScratch` computed flag. (A materialized doc has a path; scratch is null.)
   Keep it minimal — this is the seed the save/materialize step (2) will consume.
2. **Dirty tracking.** Expose an `IsDirty` on the document (or reuse an existing dirty signal on
   `SchematicViewModel`/`SchematicEditModel` if one exists — check first; the edit model raises change
   notifications the undo stack already uses). A scratch doc starts dirty (`IsDirty = true`); any edit keeps
   it dirty. (No save to clear it in step 1.)
3. **Scratch tracking list.** Since scratch docs aren't in `_openDocsByPath` (no path), track open scratch
   documents in a separate small collection on `WorkspaceViewModel` (e.g. `List<SchematicDocument>
   _scratchDocs`) so step 2/3 can enumerate "dirty scratch work to save." Add/remove as scratch tabs
   open/close.

**Layer 1 gate:** a scratch `SchematicDocument` is identifiable (null path / `IsScratch`), starts dirty, and is
tracked separately from `_openDocsByPath`; a headless or simple test confirms the flags. Report.

---

## LAYER 2 — File → New Schematic (⇧N) creates a scratch tab

1. **Command:** add `NewSchematic` `[RelayCommand]` to `WorkspaceViewModel` (parameterless — scratch needs no
   workspace/cell). It:
   - creates a fresh `SchematicEditModel` (empty);
   - wraps it in a new `SchematicViewModel(model, Messages)` and a `SchematicDocument(title, vm)` with a
     **scratch title** = the next available `Untitled-Schematic-N` (N = lowest free integer among current
     scratch/open schematic titles — mirror the "lowest free integer" pattern used elsewhere, e.g.
     `SchematicEditModel.NextAvailableName` / the workspace `Untitled-Workspace-N`);
   - marks it scratch (null path) + dirty, adds it to `_scratchDocs`;
   - opens it via `_factory.OpenDocument(doc)` and makes it active.
2. **Menu + shortcut:** add **File → New Schematic** to both the macOS NativeMenu and the in-window Menu in
   `WorkspaceWindow.axaml`, bound to `NewSchematicCommand`; add the **⇧⌘N (macOS) / Ctrl+Shift+N** KeyBinding
   on the window (note: the owner's spec says "⇧N" — implement as Shift+Cmd/Ctrl+N to avoid clashing with a
   plain N typed into the canvas/fields; confirm against existing New Workspace = ⌘N/Ctrl+N so New Schematic
   is the Shift variant). Place New Schematic sensibly in the File menu (near New Workspace).
3. **Always enabled** — New Schematic does **not** require a workspace (it's the whole point: start
   immediately). No `CanExecute` gate.

**Layer 2 gate:** ⇧⌘N / Ctrl+Shift+N and File → New Schematic both create a new `Untitled-Schematic-N` tab that
opens active and is immediately editable (place a component, draw a wire); multiple invocations give
`Untitled-Schematic-1`, `-2`, …; undo/redo works on each independently (per-document stack, the existing
routing); no workspace is required. Report.

---

## LAYER 3 — launch state + dirty indicator

1. **Launch:** at startup the app shows no on-disk workspace (already the case — `CurrentWorkspacePath` is
   null) and the welcome Message ("Create a New Schematic to get started"). **Optionally** auto-open one
   scratch `Untitled-Schematic-1` at launch so the user lands directly on an editable canvas — *confirm with
   the owner's intent: the spec says the user "wants to start doing things right away."* Implement
   **auto-open one scratch schematic at launch** (it's the most direct read of "start immediately"); if it
   proves intrusive it's trivially removed. State that you did this.
2. **Dirty indicator on the tab:** show an unsaved marker (e.g. a "•" prefix or a dot) on a scratch/dirty
   `SchematicDocument`'s tab title. Bind it to `IsDirty`. (This is the visual seed for "you have unsaved
   work"; the actual save-prompt is step 3.)
3. **Title:** the tab shows `Untitled-Schematic-N` (with the dirty marker). The window title can stay
   "circuitRF" when no workspace is open (no change needed).

**Layer 3 gate:** launching lands on an editable `Untitled-Schematic-1` (dirty-marked) without any workspace
setup; creating more scratch schematics shows each dirty-marked; the canvas is fully usable. Report.

---

## Acceptance (step 1)
1. A scratch schematic is an in-memory `SchematicDocument` (null path / `IsScratch`), dirty from creation,
   tracked in `_scratchDocs` (separate from the path-keyed `_openDocsByPath`), invisible to the project tree.
2. File → New Schematic and ⇧⌘N / Ctrl+Shift+N create an `Untitled-Schematic-N` scratch tab, active and
   immediately editable, **with no workspace required** and **no save prompt**; per-document undo works.
3. Launch lands the user on an editable scratch schematic (auto-opened); dirty tabs show an unsaved indicator.
4. `dotnet build`/`dotnet test` green; firewall green; **no save / materialize / plan dialog / close-quit
   prompt / autosave** (steps 2+); nothing in prior phases regresses. (Closing a scratch tab losing it is
   acceptable in step 1 — the prompt comes in step 3; just note it.)

## Guardrails
- **Scratch = in-memory, no path, dirty, tree-invisible** — don't put scratch docs in `_openDocsByPath` or the
  tree; don't require a workspace.
- **Reuse the schematic stack** — scratch is a normal `SchematicViewModel`/`SchematicDocument`, just path-less;
  don't fork a new schematic type.
- **No save of any kind in step 1** — creation/edit only; the save/materialize/plan/close-prompt/autosave are
  steps 2+. Don't build ahead.
- **New Schematic needs no workspace** and is always enabled.
- Sub-gate the three layers; report and stop between each; don't run the full suite into the output limit.
- Update `scratch-and-save-lifecycle.md` §7 status (step 1 done) and `src/Ui/CLAUDE.md` (scratch-document
  concept: in-memory/no-path/dirty/tree-invisible; New Schematic = ⇧⌘N, no workspace required; `_scratchDocs`
  tracking).

*Exit: the app opens straight onto an editable Untitled-Schematic the user can build and edit immediately with
no save friction — the scratch-document foundation that the save/materialize plan dialog (step 2) and the
close/quit save prompts + autosave (step 3) build on.*
