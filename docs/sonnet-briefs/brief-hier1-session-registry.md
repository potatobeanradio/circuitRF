# Brief hier1 — Editing-session registry (single source of truth per cell)

**For:** Claude Code (Sonnet) · **Phase:** 6i hierarchy navigation, step 1 of 4
**Design authority:** `docs/design/schematic-hierarchy-navigation.md` (§1, §3, §6). Read it first.
**Prereq:** none (foundational). hier2/hier3/hier4 build on this.

## Goal
Make the `SchematicViewModel` for a given `.csch` a **shared editing session keyed by absolute path**, so
every view of that schematic (a tab today; pushed-in frames in hier2) uses the same VM+EditModel+UndoRedo.
This kills divergent-copy bugs and is what makes edits live across views. Also: keep a **dirty** session
tracked even when no tab/frame shows it, and mark its cell **dirty in the project tree**.

This brief is **non-visual and mostly mechanical** — no new navigation UI yet.

## Scope (do exactly this)
In `src/Ui/ViewModels/WorkspaceViewModel.cs`:

1. **Add a session registry:** `Dictionary<string, SchematicViewModel> _sessionsByPath` keyed by the
   **absolute, normalized** `.csch` path (use the same normalization `_openDocsByPath` uses — find and
   reuse it; likely `Path.GetFullPath`). 

2. **Add `GetOrCreateSession(string absCschPath) → SchematicViewModel`:**
   - If `_sessionsByPath` has it, return it.
   - Else load it: `SchematicPersistence.LoadFromFile(absCschPath)` into a `SchematicEditModel` (this sets
     `EditModel.SchematicDirectory`), construct a `SchematicViewModel(editModel, messageSink)` exactly as
     the current schematic-open path does (find that construction in `OpenNode`'s `.csch` branch and reuse
     its wiring — message sink, any event subscriptions like cell-symbol-auto-gen), register, return.
   - **Verify on disk** how the current `.csch` open path builds the VM and copy that wiring faithfully
     (don't invent new construction). Keep view-pan/zoom restore behavior identical.

3. **Route schematic-open through the registry.** Where the current code opens a `.csch` as a tab
   (`OpenNode` `.csch` branch / the open-or-activate-by-path helper), get the VM from
   `GetOrCreateSession(absPath)` instead of `new`-ing a fresh `SchematicViewModel`. The `SchematicDocument`
   is still created per tab, but it **wraps the shared session VM**. (`SchematicDocument`'s constructor is
   unchanged in this brief — hier2 adds the nav stack.)
   - This fixes the latent bug where opening the same `.csch` twice could create two `EditModel`s.

4. **Session lifetime + dirty retention.**
   - Add a way to enumerate "all sessions that are dirty" (iterate `_sessionsByPath`, `vm.UndoRedo` /
     however `SchematicDocument.IsDirty` is derived — match the existing dirty signal).
   - Ensure **Save All** and the **close/quit prompt** include dirty sessions that may not have a visible
     tab. **Locate** the existing dirty-work enumeration used by `SaveAllDocuments` / `HasAnyDirtyWork` /
     the close prompt (see `scratch-and-save-lifecycle.md` §5) and extend it to also include dirty
     registry sessions not already represented by an open `SchematicDocument`. Do **not** double-count a
     session that already has a tab.
   - When a session is saved (its `.csch` written), clear its dirty signal through the existing path.
   - **Retirement:** add `RetireSessionIfUnreferenced(absPath)` that removes a session from the registry
     **only if** it is clean AND has no referencing document/frame. In this brief there are no frames yet,
     so "referenced" = "an open `SchematicDocument` wraps it". (hier2 will call this on Pop Out.) Do not
     retire dirty sessions.

5. **Cell dirty indicator in the project tree.**
   - When a session becomes dirty/clean, mark the owning **cell node** in the project tree dirty/clean.
     Derive the cell directory from the `.csch` path (`…/<cell>/schematic/<file>.csch` → cell dir is two
     levels up). **Locate** `ProjectTreeNodeViewModel` (`src/Ui/ViewModels/ProjectTree/`) and add an
     `IsDirty`/bullet visual (mirror how the tab bullet is done; reuse the tree's existing styling
     conventions — see `workspace-and-project-tree.md` §3.2). Wire `WorkspaceViewModel` to set it when a
     session's dirty signal changes and when it's saved. Keep it cheap (no rescan; just toggle the node).

## Constraints / rules
- **Single source of truth.** Exactly one `SchematicViewModel` per abs path. Never construct a second VM
  for a path already in the registry.
- Match existing construction/wiring of the schematic VM **exactly** (message sink, auto-gen callback
  subscription, pan/zoom restore). Verify against the current `OpenNode` `.csch` branch before writing.
- Per-document undo stays as-is (the session owns its `UndoRedoStack`).
- Don't change `SchematicDocument` in this brief beyond what's needed to make it wrap a supplied VM (it
  already takes a `SchematicViewModel` in its constructor — pass the shared one).
- Architectural firewall unaffected (all in `src/Ui`).

## Tests (add; keep green)
In `tests/Ui.Tests/` (new file `HierarchySessionRegistryTests.cs`), headless where possible:
- **Reuse:** opening the same `.csch` path twice yields the **same** `SchematicViewModel` instance (and
  the same `EditModel`). (If opening a tab needs Dock plumbing that's awkward headless, test
  `GetOrCreateSession` directly — make it `internal` + `InternalsVisibleTo` if needed, matching how other
  WorkspaceViewModel internals are tested.)
- **Dirty tracking:** a session edited (push an undoable command) reports dirty through the same signal
  Save All consumes; after the save path clears it, it's clean.
- **Retire:** a clean, unreferenced session is removed by `RetireSessionIfUnreferenced`; a dirty one is
  **not**.
- Run the full suite; everything stays green. Report the count.

## Done when
- One shared session per path; second open reuses it.
- Dirty off-screen sessions appear in Save All / close prompt and as a dirty cell node in the tree.
- Clean unreferenced sessions retire; dirty ones persist.
- Full suite green; report the number and any anchors you had to locate (active method names/lines) so the
  hier2/hier3 briefs can reference them.

## Notes for the implementer
- This is plumbing; resist adding navigation UI — that's hier2/hier3.
- If the existing open path already dedups by path in a way that effectively shares the VM, say so and
  adapt (the goal is one VM per path; don't duplicate machinery).
- Flag in your report: the exact dirty-enumeration method names you extended, and the
  `ProjectTreeNodeViewModel` dirty-visual approach you used.
