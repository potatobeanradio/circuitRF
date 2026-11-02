# Sonnet Brief — MW1: Multiple workspace windows

**Read `brief-multi-workspace-0-overview.md` first.** It records the findings this brief is built on and
fixes the decisions (R-mw-1 … R-mw-4) it does not repeat.

**Scope: the shell only.** Two or more `WorkspaceWindow`s, each with its own workspace, its own panels and
its own documents, coexisting correctly. **No cross-workspace references and no drag-drop** — those are
MW2 and MW3, and this brief must ship without them.

---

## 1. What the user gets

- **`File ▸ New Window`** — a second workspace window, opening empty (Welcome state), ready for
  `Open Workspace…`. Placed directly on `File`, immediately after `New Workspace…`.
- **`File ▸ Open Workspace…` gains a companion, `Open Workspace in New Window…`**, so the common gesture
  (I want to look at that project *too*) is one step rather than two.
- **`File ▸ Close Window`** in a workspace window closes that window; the application keeps running while
  any other workspace window is open. On macOS, closing the last one leaves the app resident with the
  background menu, exactly as today (`App.axaml.cs:663-684`).
- Each window's **Window menu** lists that window's own floats, plus **every other open workspace window**,
  so the set is navigable from any of them (§6).
- **`Open Source Workspace`** (`LayoutEditorView.axaml.cs:1728`), which today replaces the current
  workspace, opens it **in a new window** instead. That command exists precisely because the user wants to
  see where a foreign document came from *without losing what they were doing*, and today it does the
  opposite.

**R-mw1-1. Nothing about a single-window session changes.** One window open must look, behave and persist
exactly as it does today, including the launch action, the Window Layout preference, `Reset Layout`,
Show/Hide Dockers, and the `.cws` dock capture. A user who never opens a second window must not be able to
tell this brief landed.

---

## 2. Window creation and identity

**R-mw1-2. A workspace window is created in exactly one place.** Add a single internal factory —
`App.NewWorkspaceWindow(string? workspacePath = null)` — that builds the window, applies the layout
preferences and shows it. Route the existing three construction sites through it:
`App.axaml.cs:156` (first window), `:363` (`HandleFilesInternal` fallback) and `:578` (the macOS
background menu's New Workspace). A fourth construction site added later is how the layout preference gets
silently skipped for one route — that has already happened once, and `App.axaml.cs:163-175` carries the
note about it.

The first window keeps its current privileges: it is the one `desktop.MainWindow` points at, the one the
crash-report announcement and the release-notes dialog are posted to, and the one that runs
`ApplyLaunchSettings`. **Subsequent windows run `ApplyLayoutPreferences` only** — never the launch
*action*, which would open the user's start-up workspace in a window they asked to be empty.

**R-mw1-3. No window is a "second-class" window.** Beyond the startup duties above, every workspace window
has identical capability: every menu command, every panel, tear-off, Save All, the quit prompt. If any
existing code keys off "is the main window" to decide anything other than those startup duties, that is a
bug to fix here.

---

## 3. The process-global registries — the real work of this brief

Four static registries are cleared and rebuilt on every workspace open (overview §1.2). Left alone,
**opening workspace B silently unmounts workspace A's kits, PCell generators and device workers**, and the
first symptom is A's kit parts drawing as pin-less placeholders with nothing reported.

**R-mw1-4. Every one of the four becomes workspace-scoped. A workspace's entries are added when it opens,
removed when its window closes, and never removed by another workspace opening.**

| Registry | File | Today | Required |
|---|---|---|---|
| `PdkKitRegistry` | `src/Ui/Schematic/PdkKitRegistry.cs` | `Dictionary<kitName, PdkKitPart>` + `Clear()` on every open | keyed by `(workspaceRoot, kitName)`; `ClearWorkspace(root)` replaces `Clear()` |
| `KitLayoutGenerators` | `src/Ui/Schematic/KitLayoutGenerators.cs` | two flat `Dictionary<string,string>` + `Clear()` | same treatment |
| `PCellRegistry` resolvers | `src/Ui/Layout/PCells/PCellRegistry.cs` | `List<IPCellGeneratorResolver>` + `ClearResolvers()` | resolvers carry their workspace root; remove by root |
| `ExternalDeviceRegistry` resolvers | `src/Core/Devices/External/ExternalDeviceRegistry.cs` | `ResetResolved()` clears all | remove only the departing workspace's resolver |

### 3.1 How a call site knows which workspace it is asking on behalf of

The awkward part, and it has an answer already in the tree.

`CellSymbolResolver.Resolve(cellRef, baseDir)` is static and holds no workspace handle
(`CellSymbolResolver.cs:85`) — but `baseDir` is an absolute path, and
**`WorkspaceRootFinder.FindAncestorCws` (`src/Design/Workspace/WorkspaceRootFinder.cs:18`) turns an
absolute path into its owning workspace.** That is the mechanism `brief-foreign-documents.md` R-fgn-3
already fixed for technology, and three live call sites already use it
(`TechnologyResolver.cs:108`, `WBondSymbolProvider.cs:186`, `MicrostripSubstrateInjection.cs:48`).

**R-mw1-5. A kit reference resolves against the referencing document's OWN parent workspace**, found by
that walk-up. One rule, the same one technology already uses, and it is what makes MW2 §4 fall out rather
than needing a second mechanism.

**R-mw1-6. Cache the walk-up.** `Resolve` is called per render for every cell instance on screen; a
directory-existence walk per instance per frame is not acceptable. Key a small memo by the `baseDir`
string and invalidate it wherever `CellSymbolResolver.InvalidateAll()` is already called
(`WorkspaceViewModel.cs:8145`). **Measure it** — take the frame time on a layout with a few hundred kit
instances before and after; if the memo is not enough, say so rather than shipping a slower canvas.

### 3.2 The three exemptions

- `WBondSymbolProvider` and `SpiceModelPeek` caches (`WorkspaceViewModel.cs:4381`, `:4385`) are keyed by
  **absolute path and mtime**, so they are already correct across workspaces. `RestoreInstalledPdks`
  invalidates them wholesale today only because it is a convenient moment. **Stop doing that on a
  workspace open** — with two windows it throws away another workspace's live cache for no reason. Keep
  the invalidation on the paths that genuinely need it (a technology reload, an external edit).
- `CellSymbolResolver`'s own symbol cache is keyed by `(CellAbsDir, primary name, mtime)`
  (`CellSymbolResolver.cs:52`) and needs no scoping.
- `BuiltInSymbols`' shape caches are pure functions of their key and are workspace-independent.

**R-mw1-7. State the audit's result explicitly in the completion note**: every remaining static mutable
field under `src/Ui`, `src/Design` and `src/Core`, classified as *workspace-scoped* (fixed here),
*path-keyed and therefore safe*, or *genuinely process-wide and correct* (theme, preferences,
`ExternalWorkerPolicy`, `PCellRegistry`'s built-in generator table). A registry missed here fails the same
way the four above do — silently, in the window the user is not looking at.

---

## 4. Preferences: read-modify-write is now a race

`AppPreferencesIo.Load()` deserialises the whole `preferences.json` and `Save` writes the whole thing back
(`Theming/AppPreferences.cs:294-331`). Two windows each doing load → mutate one field → save will lose one
of the two edits. It is latent today (harmonicaRF and wBond share the same file) and becomes routine with
two windows — most visibly on **Recent Workspaces**, which every workspace open touches.

**R-mw1-8. One in-process `AppPreferences` instance, shared by every window, written through a single
save path.** Not a lock around the file: two windows in one process is the case that matters, and an
in-memory single instance settles it without inventing cross-process file locking that would then have to
work on three platforms.

---

## 5. Opening the same workspace twice

**R-mw1-9. A workspace may be open in at most one window. Opening one that is already open activates that
window instead.** Two `WorkspaceViewModel`s over one `.cws` means two independent
`SchematicSessionRegistry`/`LayoutSessionRegistry` instances (`WorkspaceViewModel.cs:88`, `:92`) over the
same files: two undo stacks, two dirty flags, last-save-wins. That is a data-loss trap, and refusing it is
both correct and cheaper than reconciling it.

Compare by **fully-resolved absolute path**, case-insensitively (the comparison `TechnologyCache` already
uses, and for the same reason — `TechnologyCache.cs:15`).

**R-mw1-10. The same rule at the FILE level, across windows.** A document already open in window A must
not open a second edit session in window B. Activate A's tab instead — the same behaviour opening an
already-open document within one window has today. This is the guard that makes MW2's external references
safe: a referenced cell edited in its own workspace's window must not also be editable through the window
that references it.

---

## 6. Menus and window enumeration

### 6.1 The Window menu

`EnumerateWindowEntries` (`WorkspaceViewModel.WindowMenu.cs:88-128`) enumerates
`desktop.Windows.OfType<CrfHostWindow>()` — **every float in the process**. Window B would list window A's
torn-off panels and offer to raise them as if they were its own.

**R-mw1-11. A float is attributed to the workspace window that owns it**, and each Window menu lists only
its own. The ordering rule (shell, documents, separator, tools, separator, editors) is unchanged.

**R-mw1-12. Add a band for the other workspace windows**, below the existing ones, after a separator —
each labelled by its workspace folder name with the same `DirtyMark` convention (`WindowMenu.cs:37`), so
any window can reach any other. This is what makes the menu the navigation surface for the feature.

Ownership needs a real answer, not a heuristic: `CrfHostWindow`s are created by the factory
(`CircuitRfDockFactory.FloatTool`, `SplitToWindow`), and the factory is per-view-model — so **the owning
factory can stamp the window as it creates it**. Do not infer ownership from window position, z-order or
title.

### 6.2 macOS native menu

Two mechanisms currently assume one shell:

1. `AttachNativeMenuAtApplicationScope` sets the app-scope menu to a shell's own `NativeMenu` instance,
   guarded only by `ReferenceEquals` (`WorkspaceWindow.axaml.cs:229-235`). With two shells, whichever
   activated last wins the fallback.
2. `AttachSharedNativeMenuIfMacOS` hands **the same instance** to every floated window, because Avalonia
   does not fall back to the app-scope menu for a key window that has none (`WorkspaceWindow.axaml.cs:213-219`).
   A float owned by B must be given **B's** menu, not whichever shell got there first — its commands bind
   through that menu's DataContext.

**R-mw1-13. Every window shows the menu of the workspace it belongs to.** The app-scope menu is a
*fallback for when no window is key*; setting it from the most recently activated shell is acceptable and
should be made deliberate rather than left to a first-writer-wins race.

**Tread carefully here and expect to iterate.** `macos-menu-bar-becomekeywindow` records an intermittent,
still-unreproduced launch where the workspace's menu never reaches `SetMainMenu`; `harmonicarf-r3` records
that Avalonia binds one `NativeMenu` per window for the window's lifetime. **If a menu change here makes
that intermittent worse, stop and report it** — do not build a workaround on top of an unreproduced bug.
`Diagnostics/MenuBarProbe` (`CRF_MENU_DIAG`) already exists for exactly this and should be run across a
two-window session as part of the gate.

### 6.3 The "find the workspace" lookups

Nine sites resolve "the" workspace as
`desktop.Windows.OfType<WorkspaceWindow>().FirstOrDefault()`: `App.axaml.cs:362`, `:507`;
`WirePanelKeys.cs:76`; `TornOffFileMenuView.axaml.cs:58`; `LayoutEditorView.axaml.cs:349`, `:734`,
`:759`, `:1722`, `:1736`. Every one is reached from a view whose own DataContext is a document, not a
workspace — and under two windows each would answer with an arbitrary one.

**R-mw1-14. Resolve the workspace from the CALLER's own window**, by walking up from the control to its
`TopLevel` and, for a floating window, to the shell that owns it (§6.1). Add one helper and route all nine
through it; `WorkspaceViewModel.ShellWindow` (`WorkspaceViewModel.cs:1051`) is the same lookup in the
other direction and should be its sibling, not a second implementation.

`App.axaml.cs:507` (the About dialog's owner) may legitimately keep "any visible window" — About is not
workspace-bound. Say so in the completion note rather than leaving it looking like one that was missed.

---

## 7. Files arriving from the operating system

`HandleFilesInternal` (`App.axaml.cs:360-367`) picks the first `WorkspaceWindow` and opens into it. Under
two windows, a double-clicked file would land in an arbitrary one. `OpenFiles` also deliberately opens
**at most one** workspace even when several are named, because a switch replaces a window's contents
(`App.axaml.cs:377-400`).

**R-mw1-15. A forwarded document opens in the window whose workspace CONTAINS it**, when exactly one does;
otherwise in the most recently active workspace window. This is the answer the user expects and it is
cheap — the containment test is the same ancestor walk-up as R-mw1-5.

**R-mw1-16. Several `.cws` paths in one launch now open one window each**, since that is no longer
destructive. Cap it (four is plenty) and report the rest rather than opening twelve windows because
someone multi-selected a folder.

---

## 8. Closing, quitting and persistence

- **R-mw1-17.** Closing one window prompts for **that window's** dirty documents only — docked and
  floated. `HasAnyDirtyWork(includeFloated: true)` (`WorkspaceWindow.axaml.cs:329`) must count only the
  floats that window owns, which R-mw1-11's ownership stamp supplies.
- **R-mw1-18.** `File ▸ Quit` prompts each workspace window in turn and aborts the whole quit if any
  prompt is cancelled — `App.AbortQuit` (`App.axaml.cs:619`) already exists for exactly this and already
  iterates every window (`:626-630`). Verify it rather than rewriting it; a cancelled prompt in the second
  window must leave the first window open too.
- **R-mw1-19.** Each window persists its own dock layout into its own `.cws`, unchanged. **The set of open
  windows is NOT persisted in this brief** — there is no "session of workspaces" file, and adding one
  raises questions (what does the launch action mean then?) that this brief has no reason to answer.
  Record that as a deliberate omission, not an oversight.

---

## 9. Gate

New tests in `tests/Ui.Tests` (the only project this brief can reach — do not touch `src/Core`,
`src/Engine`, `RfCore`; run `dotnet test tests/Ui.Tests` and `dotnet test tests/Firewall.Tests` as two
separate invocations):

1. **The registry test that would have caught the whole problem.** Mount a kit into workspace A's scope;
   open workspace B; assert A's kit still resolves through `CellSymbolResolver` and B's does too. Then
   close B and assert A is untouched. Do the same for `KitLayoutGenerators`, the PCell resolver and the
   device-worker resolver. **Write this test first and watch it fail against the current code** — a
   registry test that has never been red proves nothing.
2. Two view models, two dock factories, disjoint tool instances; `Reset Layout` on one leaves the other's
   arrangement byte-identical in its captured `.cws` layout.
3. `EnumerateWindowEntries` for window A contains A's floats and B's shell, and **not** B's floats.
4. Opening an already-open workspace activates rather than duplicates (R-mw1-9); opening an already-open
   document in a second window activates the existing tab (R-mw1-10).
5. Preferences: two windows each mutating a different field, both survive (R-mw1-8).
6. A static-state audit test in the spirit of `tests/Ui.Tests/Harmonica/HarmonicaStandaloneTests.cs:204`
   (which already source-scans for forbidden constructions): assert the four registries expose no
   unscoped `Clear()`-style API that a future call site could reach. **Strip comments before scanning** —
   that trap is recorded in `project-brief-harmonicarf-h8`.

**Manual check, and it is the one that matters:** open two real workspaces that each import a different
kit, in two windows, and place a part from each. Both must render with pins. Run it on macOS with
`CRF_MENU_DIAG=1` (§6.2).

---

## 10. On completion

Write the findings to `src/Ui/RESOLVED.md` — **never to `CLAUDE.md`** — and update
`docs/design/ui-architecture.md` with the per-window vs. per-process boundary R-mw1-4 and R-mw1-7 settle.

**Report, do not silently absorb:**
- Anything in the R-mw1-7 audit that could not be scoped, and what it breaks.
- The R-mw1-6 measurement, whether or not the memo was sufficient.
- Any change in the macOS menu intermittent (§6.2), in either direction.
