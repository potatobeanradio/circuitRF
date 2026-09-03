# circuitRF — Schematic Hierarchy Navigation (Push In / Pop Out)

**Status:** Draft (rev 1) for build · **Date:** 2026-06-13 · **Phase:** 6i (hierarchy navigation)

Specifies in-place hierarchical navigation for the schematic editor: **Push In** to a cell instance's
primary schematic, **Pop Out** one level, **Pop to a level** via a breadcrumb, and **Open Cell in New
Tab** as a side-by-side alternative. Covers the editing-session model that makes edits live across
multiple views of one cell, the dirty/save integration, and the resolution rule the (future)
hierarchical net extractor must follow so simulation is WYSIWYG.

Companions: `workspace-and-project-tree.md` (cells, `.ccell` primacy, `CellFolder.ResolvePrimary`,
the cell-reference model, project tree), `scratch-and-save-lifecycle.md` (dirty tracking, Save All
plan dialog, close/quit prompts, `SchematicDocument`), `net-extraction-and-run.md` (extraction is
`SchematicEditModel → Design model`, from memory; hierarchical extraction deferred),
`ui-architecture.md` (design-model-down / DataSet-up), `src/Ui/CLAUDE.md`.

---

## 1. Model: editing sessions, navigation frames, and the stack

### 1.1 The editing session (single source of truth per cell)
The editing unit today is the `SchematicViewModel` — it owns its `EditModel`, its `UndoRedoStack`, and
its `Selection`. We make that unit **shared per cell schematic**:

> **An editing session is exactly one `SchematicViewModel` per open `.csch`, keyed by its absolute
> path.** Every view that shows that schematic — a content tab, a pushed-in frame, a torn-off window —
> references the **same** session VM.

Because all views share the one VM+EditModel+UndoRedo, content edits and undo are coherent and appear
**live in every view** (the existing `EditModel.Changed → rebuild` path already drives re-render). This
is the "single source of truth kills the drift bug" rule applied to hierarchy: there is never a second
divergent `EditModel` for the same file.

**Accepted v1 tradeoff — shared selection.** Since the whole VM is shared, two simultaneous views of one
cell also share **selection** (zoom/pan stay per-canvas because they live in the canvas control, not the
VM). Selecting in one view highlights in the other. For the dominant flow (push in, look/edit, pop out)
there is only one view, so nothing is shared visibly. Per-view independent selection is a deferred
refinement (it requires splitting Selection/overlay out of the session).

A **session registry** keyed by absolute path lives in `WorkspaceViewModel`. Both "open a schematic as a
tab" and "push into a cell" resolve to a session via this registry: reuse if present, else create
(load the `.csch` into a new `SchematicViewModel`).

### 1.2 Navigation frame and stack
A content tab is a `SchematicDocument`. It gains a **navigation stack** of frames:

> **Frame** = a reference to a session VM + a display label (the instance designator pushed through,
> e.g. `X1`) for the breadcrumb.

The bottom frame is the document's **base** (what the tab was opened on). Pushing in pushes a frame;
popping out pops one. The document exposes the **active frame's** session as `ActiveViewModel`; the view
binds to it. Tab **title** and **dirty bullet** reflect the active frame's session.

### 1.3 Session lifetime and dirty
- A session is **alive** while referenced by ≥1 frame across all documents (its base tab counts as a
  reference).
- A session is **dirty** per its own `UndoRedoStack` (unchanged from today).
- When the last reference to a session drops (e.g. pop out of the only frame showing it, and it has no
  base tab): if **clean**, retire it (free memory); if **dirty**, **keep it alive and tracked** as an
  open dirty document so it still appears in **Save All** and the **close/quit prompt** (by cell name),
  and is never silently lost.

---

## 2. The four navigation actions

### 2.1 Push In
From a focused schematic view, with exactly one **resolvable cell instance** selected (or right-clicked):
1. Resolve the instance's cell directory: `EditModel.SchematicDirectory` + `comp.CellRef` → absolute
   cell dir.
2. `CellFolder.ResolvePrimary(cellDir, ViewType.Schematic)` → primary `.csch` filename. Only
   `SoleFile` / `NamedPresent` resolve; the path = `cellDir/schematic/<ResolvedName>`.
3. Get-or-create the session for that path from the registry.
4. `document.PushIn(session, label: comp.InstanceName)`. The **same tab** now renders the sub-cell. No
   new tab, no new window.

Push In works at any depth (you can push into a sub-cell's sub-cell).

### 2.2 Pop Out
Pops one frame; the tab returns to the parent frame's session. **Pop Out never prompts to save** — it is
pure navigation (§3). Disabled/greyed at the base level (nav depth 0).

### 2.3 Pop to level (breadcrumb)
A **breadcrumb bar** shows the path `Base ▸ X1(Cell) ▸ X3(SubCell)`. Each crumb is clickable to pop
directly to that level (`PopTo(index)`); the last crumb (current) is inert. A **Pop to Top** affordance
pops all the way to base. The breadcrumb is visible only when nav depth > 0.

### 2.4 Open Cell in New Tab
The side-by-side alternative to descending in place: resolve the cell's primary `.csch` exactly as Push
In does, get-or-create its session, and open it as its **own content tab** (reusing the existing
open-or-activate-by-path path — if a tab already exists for that path, activate it). Because it's the
same session, edits stay live between it and any pushed-in frame of the same cell.

---

## 3. Dirty / save model (DECIDED)

- **Editing a sub-cell marks its session dirty** (per-session, as today). The active tab shows the
  bullet while that session is the active frame.
- **Pop Out never prompts.** Navigation is frictionless; the scratch/save-lifecycle principle is "ask the
  minimum, as late as possible."
- **A dirty session with no visible view stays tracked** (§1.3) — it surfaces in Save All and the
  close/quit prompt by cell name.
- **The cell is marked dirty in the project tree** while it has a dirty session (a bullet/asterisk on the
  cell node, cleared on save). This is the visible cue that an off-screen sub-cell has unsaved edits.
- Saving happens through the **existing** lifecycle: `Ctrl/⌘S` (Save All plan dialog), and close/quit
  prompts. No new save dialog is introduced by hierarchy.

---

## 4. Simulation and the netlist (the resolution rule)

Net extraction is `SchematicEditModel → Design model`, **from memory** (`NetExtractor.Extract`). Today
the extractor **skips cell instances** (`if (comp.CellRef is not null) continue; // deferred`), so
hierarchical *simulation* is a separate, not-yet-built phase. This doc fixes the **rule that phase must
follow** so navigation and simulation agree:

> **When hierarchical extraction encounters a cell instance, it resolves that cell's schematic to its
> in-memory session `EditModel` if the cell is open anywhere (a tab or a pushed-in frame), else it loads
> the cell's primary `.csch` from disk.**

Consequences (these are the answers to the save questions):
- Simulation is **WYSIWYG**: a dirty open sub-cell simulates as-edited; a sub-cell that isn't open
  simulates from its last saved state.
- **No save prompt is needed before simulating**, at any hierarchy level — the netlist already reflects
  the correct current state. This matches "scratch sims run from memory."

(Building the recursive extractor — `Cell:Inst` emission + sub-cell session/disk resolution + a
recursion/cycle guard — is its own later phase, not part of these navigation briefs.)

---

## 4A. External cell references — a cell in another workspace *(built 2026-09-03)*

A `CellRef` may name a cell that lives in a **different workspace**, spelled as an alias the referencing
document's own `.cws` resolves:

```
CellRef:   ws://RfFrontEnd/cells/Amp
.cws:      ReferencedWorkspaces: [ { Alias: "RfFrontEnd", Path: "../rf-front-end/.cws" } ]
```

`workspace-and-project-tree.md` §5C is the authority on the form and on why it is an alias rather than a
raw path. What matters **here** is that navigation and extraction need nothing new for it:

- **Both go through `HierarchyResolver`**, which asks `ExternalCellRef.ResolveCellDir` for the absolute
  cell folder and then does exactly what it always did. Push In, Open Cell in New Tab and §4's resolution
  rule are therefore identical for an external cell — including the WYSIWYG half, since an external cell
  open in its own workspace's window is the same `SchematicEditModel`.
- **The one thing an external reference cannot do is be open twice.** MW1's R-mw1-10 already refuses a
  second edit session on a file open in another window, which is what keeps a referenced cell from being
  editable both in its own workspace's window and through the window that references it.

**What it refuses**, and where the refusal lives:

| Situation | Answer |
|---|---|
| The alias is not declared, or its workspace has moved | `NotFound` — the existing placeholder, explaining itself as a **Broken** external reference |
| The cell is gone from a workspace that does resolve | the same, naming the alias |
| The cell uses a kit whose workspace is not open | resolves as a cell; its kit parts are `NotFound` and the repair offered is *open that workspace* |
| The cell has kit content and **no ancestor `.cws` at all** | permanent — there is nothing to resolve the kit against, and a kit cannot be chosen the way a technology can |
| A **layout** view whose technologies differ | refused at placement, naming both technologies and both workspaces (`layout-view.md` §7) |

A schematic-only external reference is never gated on technology: a schematic carries none.

**A resolved external instance is marked**, not only a broken one — `Amp — [RfFrontEnd]` in the parameter
dialog's type field and an `[alias]` tag beside the glyph on the canvas. Seeing without clicking that a
cell is not yours is the whole safety story; see §5C.3/R51.

---

## 5. Enablement (don't suggest what can't happen)

**Push In / Open Cell in New Tab** enabled only when ALL hold:
- A **schematic view** is focused (not the symbol editor, not the project tree, not a parameter editor).
- Exactly **one** component is the target (selected, or right-clicked).
- That component is a **cell instance** (`comp.CellRef != null`) whose primary schematic **resolves**
  (`ResolvePrimary` is `SoleFile`/`NamedPresent`).
- The parent schematic is **saved** (`EditModel.SchematicDirectory != null`) — a scratch schematic has no
  base for `CellRef` resolution, and cell instances can't be placed in a scratch schematic anyway.

Otherwise the items are **disabled/greyed** (with a tooltip reason) — never silently missing where a user
expects them. A broken/`NotFound` reference, `MissingNamedPrimary`, `NoPrimary`, or `NoView` cell →
disabled with a reason ("cell has no primary schematic", "cell reference not found", …).

**Pop Out / breadcrumb** enabled only when the active document's nav depth > 0.

All four actions are reachable from: the schematic **toolbar** (Push In / Pop Out buttons), the
component **context menu** (existing `CtxPushIn` + a new `CtxOpenInNewTab`), the app **menu**, and the
**keyboard** (suggested: Push In `Ctrl/⌘+]`, Pop Out `Ctrl/⌘+[`).

---

## 6. Integration points (verified on disk, 2026-06-13)

- `SchematicDocument` (`src/Ui/Schematic/SchematicDocument.cs`) wraps one `SchematicViewModel`; exposes
  `Model => ViewModel.RenderModel`, `IsDirty`, `FilePath`/`IsScratch`, `Materialize`. **H2 changes:** add
  a frame stack + `ActiveViewModel` (notifying) + `PushIn`/`PopOut`/`PopTo`/`CanPopOut` + an
  `ActiveViewModelChanged` event; `Model`/`IsDirty`/`Title` follow the active frame's session.
- `SchematicView` (`src/Ui/Views/Content/SchematicView.axaml(.cs)`) binds the canvas `Model="{Binding
  Model}"` and reaches the VM via `Vm => (DataContext as SchematicDocument)?.ViewModel`, subscribing in
  `OnDataContextChanged`. It already has the **`CtxPushIn`** context item (visible when `comp.CellRef is
  not null`) with a stub `OnCtxPushIn`, and `OnContextMenuOpening` that sets `CtxPushIn.IsEnabled`. The
  canvas exposes `ContextMenuTargetId`. **H2/H3 change:** rebind `Vm`/`EditContext`/selection on
  `ActiveViewModelChanged`; implement `OnCtxPushIn`; add `CtxOpenInNewTab`; add toolbar Push In / Pop Out
  buttons + breadcrumb host.
- `WorkspaceViewModel` (`src/Ui/ViewModels/WorkspaceViewModel.cs`, ~108 KB) owns `_openDocsByPath`
  (open/activate dedup), `OpenNode` (`.csch` → `SchematicDocument`), `RebuildOpenSchematics`,
  `CurrentWorkspacePath`, and implements `ITreeActions`. **H1/H3 change:** add `_sessionsByPath`; route
  schematic-open and push-in through a get-or-create session; add the hierarchy service methods
  (`PushIntoSelected(doc)`, `PopOut(doc)`, `OpenCellInNewTab(comp,doc)`); mark cell dirty in the tree.
  *(Sonnet: locate the active-document accessor from the Dock factory for the app-menu/keyboard path.)*
- `CellFolder.ResolvePrimary(cellDir, ViewType.Schematic)` → `PrimaryResolution { State, ResolvedName }`
  (`src/Ui/Schematic/CellFolder.cs`) — the primary-schematic resolver. `SchematicPersistence.LoadFromFile`
  loads a `.csch` and sets `EditModel.SchematicDirectory`.

---

## 7. Implementation order (briefs)

1. **hier1 — Editing-session registry.** `_sessionsByPath` get-or-create in `WorkspaceViewModel`; route
   schematic-open through it; session lifetime/refcount; dirty sessions stay tracked off-screen; cell
   dirty indicator in the project tree. Foundational.
2. **hier2 — Document navigation stack.** `SchematicDocument` frame stack + `ActiveViewModel` +
   `PushIn`/`PopOut`/`PopTo`/`CanPopOut` + `ActiveViewModelChanged`; `Model`/`IsDirty`/`Title` follow
   active; `SchematicView` rebinds to the active VM.
3. **hier3 — Actions + wiring.** Hierarchy service (resolve cell instance → primary `.csch` → session);
   Push In / Pop Out / Pop to level / Open Cell in New Tab; enablement; context menu, toolbar, app menu,
   keyboard.
4. **hier4 — Breadcrumb bar.** Breadcrumb in `SchematicView` bound to the active document's frames;
   click-to-pop; Pop to Top; visible only when nav depth > 0.

---

## 8. Open / deferred
- **Per-view independent selection** (split Selection/overlay out of the shared session) — v1 shares
  selection across simultaneous views of one cell.
- **Hierarchical net extraction** (the §4 memory-if-open-else-disk rule, `Cell:Inst` emission, recursion
  guard) — its own phase.
- **Breadcrumb overflow** for very deep hierarchies (ellipsis / dropdown) — v1 assumes shallow depth.
- ~~**Recursion/cycle guard** at navigation time~~ — **done at PLACEMENT time**, which is the earlier and
  better moment: a cycle-closing cell placement or retype is refused at the gesture and the loop is named
  (`Amp → Buf → Bias → Amp`), so there is no cycle left for a push to walk into.
  `SchematicHierarchy.WouldCreateCycle`, the counterpart of the layout view's `CellHierarchy` guard, wired
  into `CommitCellPlacementAsync` and `TryChangeToCellType`. It reads the primary `.csch` of each level
  from DISK, so a loop closed entirely through UNSAVED edits is still caught only by the extractor —
  stated rather than hidden, and the same limitation the layout view has always had. Detail and the three
  design decisions in `src/Ui/RESOLVED.md`.
- **Paste is not guarded**, on either the schematic or the layout side. A pasted `CellRef` is relative to
  the SOURCE schematic's directory, so pasting a cell instance into a different cell generally produces a
  reference that does not resolve at all; what a pasted reference should MEAN has to be settled before
  guarding it is anything but a guess.
- **`FileSystemWatcher`-driven** external-edit reconciliation of an open session — out of scope (matches
  the tree's manual/on-focus refresh, `workspace-and-project-tree.md`).
