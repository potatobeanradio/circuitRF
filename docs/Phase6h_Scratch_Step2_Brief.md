# Phase 6h — Scratch Step 2: materialize + the Save plan dialog (Claude Code / Sonnet)

The "save once" flow: turn dirty **scratch** documents into real on-disk cells/files via a single
**HIG-compliant plan dialog** that shows everything that will be created/saved, asks the minimum, and reports
every path written. **This brief is step 2** — materialize + plan dialog + Save/Save-All wiring. The
**three-tier save** detail (loose Known-File / loose plain-file) and the **close/quit/open-workspace prompts**
and **autosave** are **step 3** — keep this to the core "Save All from the plan" path (into-cell, creating
workspace+cells as needed). Read `scratch-and-save-lifecycle.md` §3 (esp. §3.1–3.5) first. Sub-gated;
**report and stop between every layer.** Firewall green.

> Read first: `docs/design/scratch-and-save-lifecycle.md` §3 (the dependency chain, decisions-not-documents,
> the plan dialog incl. the "each own cell / all in one cell" toggle, analysis⇒TestBench, reporting). Context
> code: `src/Ui/Schematic/SchematicDocument.cs` (`FilePath`/`IsScratch`/`IsDirty`; step-1 scratch identity),
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (`_scratchDocs`, `_openDocsByPath`, `CurrentWorkspacePath`,
> `NewWorkspace`/the `NewWorkspaceDialog` create path, `Messages`, `ResolveOwner`/`GetMainWindow`,
> `_factory.ProjectTreeTool?.Refresh`), `src/Ui/Schematic/CellFolder.cs` (`CreateCellFolder`, `SubFolderPath`,
> `ViewExtension`, `ViewType`), `src/Ui/Schematic/CellPersistence.cs` (`CcellFile.PrimarySchematic`,
> `IsTestBench`), `src/Ui/Schematic/SchematicPersistence.cs` (`SaveToFile(path, model, cellName)`),
> `src/Ui/Schematic/EditableSchematic.cs` (`SchematicEditModel` — note **no analyses collection yet**; the
> analysis⇒TestBench detection is a hook for 6e, see L2), `src/Ui/Views/Dialogs/NewWorkspaceDialog.axaml(.cs)`
> + `InputNameDialog.axaml` (HIG dialog patterns to mirror — centered buttons, `ShowDialog<T?>` return).
> Design docs win on any conflict.

## The spine (do not violate)
- **Materialize missing ancestors, topmost-first** (§3.1): schematic → needs a cell → needs a workspace. The
  save flow plans the whole set, materializes each ancestor **at most once**, then saves all documents and
  reports every path.
- **Decisions, not documents** (§3.2): one workspace decision; one decision **per distinct destination cell**;
  zero for already-homed docs. Don't prompt per-document.
- **One plan, shown once** (§3.3): a single reviewable/editable dialog — NOT a stream of prompts. Defaults so
  the common case is one **Save All** click.
- **HIG** (§3.3): Save All is the **default** button (prominent, trailing); Cancel beside it (Esc); **button
  labels centered** (the recurring bug — get it right); inline `NameValidator` errors; elide long paths.
- **Report every file written with its full path** (§3.5).
- **Scope fence (step 2):** the into-cell Save-All path (create workspace+cells as needed, save scratch
  schematics, report). NO loose/plain-file tiers, NO close/quit/open prompts, NO autosave (step 3). Symbols/
  data-displays: the algorithm should be **general** (not schematic-only) where cheap, but step 2 only needs to
  prove it on **scratch schematics** (the only scratch type that exists today).

---

## LAYER 1 — the materialize-plan model + algorithm (framework-free, headless)

A framework-free planner (e.g. `src/Ui/Schematic/SavePlan.cs`) that, given the current workspace state +
the set of dirty scratch documents, computes a **plan**:
1. **Inputs:** `CurrentWorkspacePath` (null ⇒ no workspace), the tracked default parent dir, and the list of
   dirty scratch documents (name + type + the edit model). For step 2 the documents are scratch schematics.
2. **Plan model:** a `SavePlan` with:
   - an optional **workspace step** (`CreateWorkspace { Name, ParentDir }`) — present only when no workspace
     is loaded;
   - a set of **cell steps** (`CreateCell { Name, IsTestBench }`) — **de-duplicated by cell name**;
   - a set of **save steps** (`SaveDocument { Document, TargetCellName, ViewType, FileName }`).
   - Each step carries enough to execute (L2) and to render a row (L3).
3. **Default seeding** (§3.3):
   - **Workspace name:** default `Untitled-Workspace-N` at the tracked parent (only if none loaded).
   - **Each scratch schematic → its own cell**, cell name **seeded from the document name**
     (`Untitled-Schematic-N` → cell `Untitled-Schematic-N`); the schematic saves as `<cellname>.csch` (or the
     doc name) in that cell's `schematic/`. (The **mode toggle** in L3 can rewrite this to all-in-one-cell.)
   - A document that already targets a known cell (none yet in step 2, but model it) → a save step with no
     cell-create.
4. **analysis ⇒ TestBench hook:** a cell's `IsTestBench` is true iff any schematic going into it **contains
   analyses**. `SchematicEditModel` has **no analyses collection yet** (6e), so implement a single predicate
   `SchematicHasAnalyses(model)` that returns **false for now** (no analyses exist) with a clear
   `// TODO 6e: detect analysis directives` — so the wiring is in place and flips on automatically when 6e
   adds analyses. Do NOT invent an analyses model here.
5. **Mode + naming recompute:** the planner exposes a method to **rebuild** under a mode
   (`EachOwnCell` | `AllInOneCell(sharedName)`) and to apply user name edits — so the dialog (L3) can
   re-plan live. In `AllInOneCell`, all scratch schematics target one cell (name seeds from the **first**
   schematic's document name); the first is primary, the rest non-primary; if **any** has analyses the shared
   cell is a TestBench.

**Layer 1 gate:** headless tests: (a) no workspace + 2 scratch schematics, EachOwnCell → plan = create
workspace + 2 cells + 2 saves, names seeded from doc names; (b) AllInOneCell → 1 cell (named from first) + 2
saves, first primary; (c) workspace already loaded → no workspace step. The planner is framework-free
(no Avalonia). Report.

---

## LAYER 2 — execute a plan (create + save + report)

A framework-free-ish executor (may live on `WorkspaceViewModel` since it touches the factory/tree/messages, but
keep the file IO in helpers): given a confirmed `SavePlan`, perform it **in order, each ancestor once**:
1. **Workspace step (if present):** create `parentDir/<name>/` + `.cws` (reuse the exact `NewWorkspace` create
   logic — folder + `WorkspacePersistence.SaveToFile`), set `CurrentWorkspacePath`, point the tree at it.
   *(Don't ask again — the plan already has the name; this is non-interactive execution.)*
2. **Cell steps:** for each, `CellFolder.CreateCellFolder(workspaceDir, cellName)`; if `IsTestBench`, load the
   new `.ccell`, set `IsTestBench = true`, save it.
3. **Save steps:** write each scratch schematic via `SchematicPersistence.SaveToFile(path, model, cellName)`
   into `<cell>/schematic/<file>.csch`; set the cell's `.ccell` `PrimarySchematic` to the file (the first/only
   schematic in the cell is primary — sole-file makes it primary anyway, but set it explicitly when named).
4. **Update the documents:** each materialized `SchematicDocument` is no longer scratch — set its `FilePath`
   to the saved path, clear `IsDirty`, remove it from `_scratchDocs`, add to `_openDocsByPath[path]`. (The tab
   loses its bullet and becomes a normal on-disk doc.) **This is the scratch→materialized transition** (§1.2)
   — get it right: a later autosave/recovery (step 3) must not re-offer a now-saved doc.
5. **Refresh + report:** `ProjectTreeTool.Refresh()` so the new cells/files appear; **post a Message listing
   every file written with its full path** (the `.cws`, each `.ccell`, each `.csch`).

**Layer 2 gate:** executing a plan (no workspace + 1 scratch schematic) creates the workspace folder + `.cws`
+ cell + `schematic/<name>.csch`, the tab becomes a clean on-disk doc (no bullet, now in the tree), and the
Message lists all full paths. Re-saving (now materialized) just writes the `.csch` (no re-create). Report.

---

## LAYER 3 — the plan dialog (HIG)

`src/Ui/Views/Dialogs/SavePlanDialog.axaml(.cs)`, mirroring `NewWorkspaceDialog`/`InputNameDialog` conventions:
- **Title + subtitle:** "Save your work" / "circuitRF will create the following and save your documents."
- **Global mode control** (only shown when ≥1 scratch **schematic** needs a cell): segmented/radio
  **(•) Each in its own cell** / **( ) All in one cell:** `[<name>]` (name seeds from first schematic's doc
  name, editable). Toggling **live-rebuilds** the plan (L1's rebuild) and the table. A quiet hint under
  "All in one cell": "Only the first schematic will be the cell's primary view."
- **Plan table** (scrollable): one row per step — icon · action verb (Create workspace / Create cell / Save) ·
  destination (path or cell) · **editable name** field (inline `NameValidator`, error shown on the row).
  Cell-create rows show ***(TestBench)*** when `IsTestBench`. Long paths **elide** (full path on hover/tooltip).
- **Buttons (HIG):** **Save All** = default (trailing, prominent); **Cancel** = Esc (beside it). Centered
  labels. Nothing destructive.
- **Live "Will create" feel:** as the user edits names or toggles mode, the table updates (re-plan). OK/Save
  All disabled while any name is invalid or collides.
- **Return:** the confirmed `SavePlan` (or null on cancel).

**Layer 3 gate:** the dialog shows the plan for 2+ scratch schematics; toggling EachOwnCell/AllInOneCell
rewrites the destinations live; editing a cell name validates inline; TestBench annotation shows when
applicable; Save All is the centered default button, Cancel cancels. Report (screenshot description).

---

## LAYER 4 — wire Save / Save All to the plan

- **Save / Save All command:** when invoked with dirty scratch work, build the plan (L1), show the dialog
  (L3); on Save All, execute (L2). On Cancel, do nothing.
- **Menu/shortcut:** ensure **File → Save** / **Save All** (⌘S / Ctrl+S) routes here when there's scratch work.
  (A plain materialized-doc save can still just write its file; but any scratch doc in the set routes through
  the plan.) Reconcile with the existing `SaveWorkspace`/`SaveWorkspaceAs` commands — Save should now mean
  "save my work" (documents), not only "write the `.cws`"; keep `.cws` writing as part of/after the flow.
- **Already-materialized docs** in the dirty set just save to their `FilePath` (no plan row needed beyond a
  Save step with no create).

**Layer 4 gate:** with 2 scratch schematics and no workspace, ⌘S/Ctrl+S → the plan dialog → Save All creates
workspace + cells + schematics, tabs go clean, tree shows them, Message lists all paths. A second ⌘S with no
dirty work is a no-op (or "nothing to save"). Report.

## Acceptance (step 2)
1. A framework-free `SavePlan` planner computes the materialize plan (workspace?/cells de-duped/saves) with
   doc-name-seeded defaults, an EachOwnCell↔AllInOneCell rebuild, and the analysis⇒TestBench hook (false until
   6e).
2. Executing a plan creates the workspace + cells (TestBench flagged when applicable) + saves the schematics,
   transitions scratch docs to materialized (FilePath set, dirty cleared, moved to `_openDocsByPath`),
   refreshes the tree, and **reports every full path**.
3. The HIG plan dialog shows the plan, the mode toggle live-rebuilds it, names validate inline, TestBench is
   annotated, Save All (centered, default) executes / Cancel aborts.
4. Save / Save All (⌘S/Ctrl+S) routes scratch work through the plan; materialized docs save to their path.
5. `dotnet build`/`dotnet test` green; firewall green (planner framework-free); **no loose/plain-file tiers,
   no close/quit/open prompts, no autosave** (step 3); nothing else regresses.

## Guardrails
- **Materialize topmost-first, each ancestor once; decisions not documents; one plan shown once.**
- **HIG:** Save All default + centered labels + inline validation + elided paths; report all full paths.
- **Scratch→materialized transition is exact** — set FilePath, clear dirty, move from `_scratchDocs` to
  `_openDocsByPath`; a saved doc must never be re-offered as scratch later.
- **analysis⇒TestBench is a hook** — `SchematicHasAnalyses` returns false for now (TODO 6e); don't invent an
  analyses model.
- **Planner framework-free**; file IO in helpers; only the dialog is Avalonia.
- **Scope fence:** into-cell Save-All only. No loose/plain tiers, no close/quit prompts, no autosave.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `scratch-and-save-lifecycle.md` §7 status (step 2 done) and `src/Ui/CLAUDE.md` (the SavePlan
  planner/executor, the plan dialog, the scratch→materialized transition, analysis⇒TestBench hook).

*Exit: a user who built scratch schematics can hit Save and, through one reviewable plan dialog, materialize a
workspace + cells + saved schematics in a single confirm — with every written path reported — leaving the
loose-file tiers, close/quit prompts, and autosave for step 3.*
