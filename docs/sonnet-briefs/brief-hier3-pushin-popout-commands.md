# Brief hier3 — Push In / Pop Out / Open Cell in New Tab: actions + wiring

**For:** Claude Code (Sonnet) · **Phase:** 6i hierarchy navigation, step 3 of 4
**Design authority:** `docs/design/schematic-hierarchy-navigation.md` (§2, §4, §5, §6). Read it first.
**Prereq:** hier1 (session registry) + hier2 (nav stack + retarget) landed and green.

## Goal
Wire the real navigation: resolve a selected cell instance to its primary schematic session and drive the
document's stack. Provide **Push In**, **Pop Out**, and **Open Cell in New Tab**, reachable from the
context menu, toolbar, app menu, and keyboard, with correct **enablement** everywhere.

## Scope (do exactly this)

### A. Hierarchy service on `WorkspaceViewModel` (`src/Ui/ViewModels/WorkspaceViewModel.cs`)
Add methods that own the resolve + registry logic (the document does pure stack ops from hier2):

1. **Resolve helper:** `(string AbsCschPath, string Reason)? ResolveCellPrimarySchematic(EditableComponent comp, SchematicEditModel parentModel)`:
   - Require `parentModel.SchematicDirectory != null` and `comp.CellRef != null`; else return a reason
     string (for the disabled tooltip), no path.
   - `cellDir = Path.GetFullPath(Path.Combine(parentModel.SchematicDirectory, comp.CellRef))`.
   - `var pr = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);`
   - Accept only `PrimaryState.SoleFile` / `NamedPresent` → path =
     `Path.Combine(cellDir, CellFolder.SchematicSubFolder, pr.ResolvedName!)`. Other states →
     reason ("cell has no primary schematic", "primary schematic missing", "cell reference not found" when
     the dir doesn't exist). Keep the reasons human-readable for tooltips.

2. **`bool CanPushInto(EditableComponent? comp, SchematicEditModel? parentModel, out string? reason)`** —
   the single enablement predicate used by every surface (returns false + reason when not pushable). True
   only when `comp` is a resolvable cell instance per §5.

3. **`void PushIntoCell(SchematicDocument doc, EditableComponent comp)`:**
   - Resolve via (1); if no path, post the reason to Messages and return.
   - `var session = GetOrCreateSession(absPath);` (hier1)
   - `doc.PushIn(session, comp.InstanceName);` (hier2)

4. **`void PopOutOf(SchematicDocument doc)`:**
   - `var popped = doc.PopOut();` if non-null, `RetireSessionIfUnreferenced(popped's path)` (hier1) — but
     only retire if it's not referenced by any other doc/frame and is clean. (You need a path↔session
     reverse lookup, or store the abs path on the session/frame — simplest: store `AbsPath` in the
     `NavFrame` or a `Dictionary<SchematicViewModel,string>` alongside the registry. Pick one and keep it
     consistent with hier1.)

5. **`void PopToLevel(SchematicDocument doc, int frameIndex)`** — calls `doc.PopTo(index)`, retires each
   popped session if unreferenced+clean. (Used by hier4's breadcrumb; expose it now.)

6. **`void OpenCellInNewTab(SchematicDocument fromDoc, EditableComponent comp)`:**
   - Resolve via (1); if no path, post reason + return.
   - Open/activate a tab for that path using the **existing open-or-activate-by-path** path
     (`_openDocsByPath` / `OpenNode`'s `.csch` branch), which now (hier1) wraps the shared session — so the
     new tab and any pushed-in frame share live edits. If a tab already exists for the path, activate it.

7. **Active-document accessor for menu/keyboard:** locate how the Dock factory exposes the focused/active
   `Document` (the active dockable). Add `SchematicDocument? ActiveSchematicDocument` that returns it when
   the active dockable is a `SchematicDocument`, else null. *(Verify the Dock API on disk — likely the
   factory's active-dockable or the layout's `FocusedDockable`.)*

8. **App-menu/keyboard command surface:** add `RelayCommand`s `PushInCommand` / `PopOutCommand` /
   `OpenCellInNewTabCommand` that act on `ActiveSchematicDocument` + its active VM's single selected
   component, with `CanExecute` mirroring `CanPushInto` / `doc.CanPopOut`. Raise `CanExecuteChanged` on
   selection/active-doc/nav changes (match how existing commands requery — find the pattern).

### B. Context menu (`SchematicView.axaml` + `.axaml.cs`)
The menu already has `CtxPushIn` (visible when `comp.CellRef != null`) with stub `OnCtxPushIn`, and
`OnContextMenuOpening` that currently force-disables it.
1. Add a sibling item **`CtxOpenInNewTab`** ("Open Cell in New Tab", `Kind="OpenInNew"`), visible when
   `comp.CellRef != null`.
2. In `OnContextMenuOpening`: for the target component (`Vm.EditModel.FindComponent(ContextMenuTargetId)`),
   compute `CanPushInto(comp, Vm.EditModel, out reason)`; set `CtxPushIn.IsEnabled` / `CtxOpenInNewTab.IsEnabled`
   accordingly, and set a tooltip with `reason` when disabled.
3. Implement `OnCtxPushIn` and `OnCtxOpenInNewTab`: get the target comp + the `SchematicDocument`
   (DataContext) and call the workspace service. **The view needs the WorkspaceViewModel** — inject a
   reference the same way `SchematicDocument.Messages` is injected (preferred: add a hierarchy callback/
   service reference onto `SchematicDocument` at construction in `WorkspaceViewModel`, e.g.
   `doc.Hierarchy = this` via a small interface `IHierarchyHost { PushIntoCell(...); PopOutOf(...);
   OpenCellInNewTab(...); CanPushInto(...); }`). Do **not** have the view walk the visual tree to find the
   window's DataContext if a clean injected reference is available — match the `Messages` injection
   pattern.

### C. Toolbar (`SchematicView.axaml` + `.axaml.cs`)
Add two buttons in the toolbar (after the zoom group, with a separator), following the existing
`Button`/`MaterialIcon` pattern:
- **Push In** — `Kind="ArrowRightBoldBox"` (or similar), `ToolTip.Tip="Push Into Cell  (Ctrl+])"`,
  `Classes="SelectionBtn"`, enabled only when the current selection is a single resolvable cell instance.
- **Pop Out** — `Kind="ArrowLeftBoldBox"`, `ToolTip.Tip="Pop Out  (Ctrl+[)"`, enabled only when
  `doc.CanPopOut`.
Update their enabled state in `UpdateDisableButtonStates()` (selection change) and on
`ActiveViewModelChanged` (nav depth change). Click handlers call the workspace service via the injected
`IHierarchyHost`.

### D. Keyboard
Add Push In / Pop Out to the focus-independent tunnel handler `OnViewKeyDownTunnel` in `SchematicView`
(it already handles S/W/Z/F/Esc). Use **Ctrl/⌘+]** = Push In, **Ctrl/⌘+[** = Pop Out (the handler
currently early-returns when `ctrl` is held — add an explicit ctrl-branch for these two keys before that
return). Guard with the same enablement predicate.

### E. App menu (`src/Ui/Views/WorkspaceWindow.axaml` / native menu)
Add **Push In** / **Pop Out** / **Open Cell in New Tab** items bound to the §A.8 commands. Locate the
existing menu (View or a new "Hierarchy" submenu — match the app's menu organization). Greyed via
`CanExecute`.

## Constraints / rules
- **Enablement is centralized** in `CanPushInto` (§A.2) — context menu, toolbar, keyboard, and app menu
  all consult it. Don't duplicate the resolve logic in the view.
- Push In disabled (with reason) for: not a cell instance, scratch parent (no `SchematicDirectory`),
  unresolved/NotFound cell, `MissingNamedPrimary`/`NoPrimary`/`NoView`. Symbol editor / tree focus → the
  app-menu/keyboard commands are inert because `ActiveSchematicDocument` is null.
- **No new tab on Push In** (hier2 stack). Open Cell in New Tab **does** open/activate a tab.
- Reuse the existing open-or-activate-by-path path for Open-in-New-Tab; don't fork it.
- Firewall unaffected.

## Tests (add; keep green)
`tests/Ui.Tests/HierarchyPushInTests.cs` — headless via the workspace service where feasible:
- Build a tiny on-disk cell fixture (a cell dir with `schematic/<primary>.csch` + `.ccell`) in a temp dir
  (mirror existing tests that create cell folders — e.g. `CellFolderTests`/`CellSymbolResolverTests`).
  A parent schematic with a cell instance (`CellRef` relative to its `SchematicDirectory`).
- `CanPushInto` true for the resolvable instance; false (+reason) for: a built-in component, a scratch
  parent, a cell whose `schematic/` is empty (`NoView`).
- `PushIntoCell` resolves to the primary `.csch`, gets a session, and the document's `ActiveViewModel` is
  that session; `PopOutOf` returns to base and retires the (clean) session.
- `OpenCellInNewTab` opens/activates a tab whose session is the **same** instance as a pushed-in frame's
  session for that path (shared-session assertion).
- Full suite green; report count.

## Done when
- Push In / Pop Out / Open Cell in New Tab work from context menu, toolbar, keyboard, and app menu, with
  correct centralized enablement + disabled-tooltips.
- A pushed-in cell and an Open-in-New-Tab of the same cell share one live session.
- Full suite green; report the number and the anchors you located (active-dockable accessor, app-menu
  insertion point, command-requery pattern).
