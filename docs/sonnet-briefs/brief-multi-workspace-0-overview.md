# Sonnet Brief — Multiple open workspaces: overview, findings and decisions

**Read this first; it is the map for `brief-multi-workspace-1-windows.md`,
`-2-external-cell-refs.md` and `-3-workspace-dnd.md`. It contains no work of its own** — it records what
the code says today, answers the four questions the owner raised, and fixes the decisions the three
implementation briefs depend on. Every claim below was read out of the tree, not assumed; file and line
references are given so a later reader can check them rather than trust them.

---

## 1. What the code already supports, and what actually blocks a second workspace

### 1.1 The window shell is already almost multi-window

`WorkspaceViewModel` is **not** a singleton, and neither is anything the shell hangs off it:

| Thing | Scope today | Evidence |
|---|---|---|
| Dock factory (and therefore **every tool panel**) | per view model | `WorkspaceViewModel.cs:396` — `_factory = new CircuitRfDockFactory()` |
| Project Tree, Library, Properties, Analyses, Messages, DRC tools | per factory | `CircuitRfDockFactory.cs:77-81` |
| `TechnologyCache` | per view model | `WorkspaceViewModel.cs:109`, `:893` |
| Schematic / layout edit sessions | per view model | `WorkspaceViewModel.cs:88`, `:92` |
| Undo | per document | `Phase6f_PerDocumentUndo_Brief.md` |
| `View ▸ Reset Layout` | a command on the view model | `WorkspaceViewModel.cs:2617` |
| Dock arrangement persistence | in the `.cws` | `Docking/DockLayoutCapture.cs` |
| `File ▸ Quit` | already iterates **every** `WorkspaceWindow` | `App.axaml.cs:626-630` |

A second `WorkspaceWindow` is even constructible today: the macOS no-window background menu has a
**New Workspace** item that does exactly `new WorkspaceWindow { DataContext = new WorkspaceViewModel() }`
(`App.axaml.cs:576-580`). It is reachable only when zero windows are open, so it has never been exercised
alongside another one.

**So the answer to "can circuitRF support this" is yes, and the shell is not where the cost is.**

### 1.2 The actual blocker: four process-global registries torn down on every workspace open

Opening a workspace calls, on **static** state shared by the whole process:

| Registry | Where it is cleared | What it holds |
|---|---|---|
| `PdkKitRegistry` (static class) | `WorkspaceViewModel.cs:4374` — `PdkKitRegistry.Clear()` | every mounted kit's symbols and `.ccell` interfaces, keyed by **kit name** |
| `KitLayoutGenerators` (static) | `WorkspaceViewModel.cs:4382` | kit layout-generator id map |
| `PCellRegistry` resolvers (static) | `WorkspaceViewModel.cs:641` — `ClearResolvers()` | the live Python PCell resolver |
| `ExternalDeviceRegistry` resolvers (static) | `WorkspaceViewModel.cs:4904` — `ResetResolved()` | device-worker providers |

**Opening workspace B in a second window would silently unmount workspace A's kits, PCell generators and
device workers.** Window A would keep rendering — until the next symbol-cache miss, at which point its kit
parts would resolve `NotFound` and draw as pin-less placeholders, and its simulations would stop finding
their providers. Nothing would report it. This is the one thing that must be fixed before a second window
is allowed to exist, and MW1 §3 is about exactly that.

Two supporting facts that shape the fix:

- The reference form itself is name-keyed and **written into user files** — `pdk://<kitName>/<partId>`
  (`PdkKitRegistry.cs:78`). The key cannot simply become a path.
- The resolver entry point is static and carries no workspace handle:
  `CellSymbolResolver.Resolve(cellRef, baseDir)` (`CellSymbolResolver.cs:85`). It does carry `baseDir`,
  an absolute path — and `WorkspaceRootFinder.FindAncestorCws` (`src/Design/Workspace/`) already turns an
  absolute path into its owning workspace. That walk-up is the existing, proven mechanism
  (`brief-foreign-documents.md` R-fgn-3 uses it for technology; `TechnologyResolver.cs:108`,
  `WBondSymbolProvider.cs:186`, `MicrostripSubstrateInjection.cs:48` all call it).

### 1.3 Cross-workspace cell references already work in the file format

`CellRef` is written as `Path.GetRelativePath(schematicDir, cellAbsDir)` at **every** site that produces
one (`SchematicViewModel.cs:3247`, `:3426`; `LayoutEditorViewModel.PaletteDrag.cs:218`;
`LayoutEditorViewModel.Group.cs:84`; `MatchFlattenService.cs:148`; `LayoutToSchematicGenerator.cs:312`;
`SchematicCanvas.cs:1013`; `LayoutCanvas.cs:894`), and it is read back with a plain
`Path.GetFullPath(Path.Combine(baseDir, cellRef))` (`CellSymbolResolver.cs:117`,
`HierarchyResolver.cs:24`). Nothing constrains it to the workspace, and nothing rejects a `../..` form.

**So a reference to a cell in another workspace resolves today** — for geometry, symbol, pins, push-in
navigation and the parameter interface. What does *not* work is everything around it: nothing in the UI
offers you a cell outside the workspace to reference; the technology is wrong (§2.2); a kit part inside
the referenced cell is unresolvable (§1.2); and the workspace archive has no idea the reference exists.

**And the reference FORM was never chosen** — it is whatever `GetRelativePath` happened to produce.
`docs/design/workspace-and-project-tree.md` §5A R37 is explicit about this: cross-workspace instancing is
deferred, its central question is *"a named library alias resolved through `.cws` versus a raw path
recorded in every file"*, and answering it accidentally as a side effect of another feature is named as
the thing to avoid. **MW2 §2 answers it on purpose — an alias — and the reasoning is recorded there.**

---

## 2. The four questions, answered

### 2.1 "Can documents from different workspaces dock together?" — **No. Agreed, and it is also the cheap answer.**

**R-mw-1. A document belongs to exactly one workspace window, and cannot be dragged into another's
document area.** The owner's instinct is right on UX grounds and the code agrees on mechanical ones: a
document tab carries dirty tracking, undo routing, `.cws` session membership, Save-All participation and
Remove/Rename-Cell participation, all of which resolve through the `WorkspaceViewModel` that owns the
dock. Moving the tab would move none of that.

This is **not** the same as the existing foreign-document behaviour, which stays exactly as
`brief-foreign-documents.md` defines it: a document opened from outside the current workspace, or orphaned
by a switch, is editable and saveable in the window it is in. Multi-window adds a second, better route to
the same need — open the other workspace in its own window — but removes nothing.

### 2.2 "How do the tool palettes get handled? What does Reset Layout do?" — **One set per window, and it is already built that way.**

**R-mw-2. Every workspace window owns its own Library, Project Tree, Properties, Analyses, Messages and
DRC panels, its own dock arrangement, and its own `View ▸ Reset Layout`.** Reset Layout resets the window
it was invoked from and touches no other. This falls out of `_factory` being per-view-model (§1.1) and
costs nothing.

The **default** layout a reset resets *to* stays a single application preference
(`AppPreferences.WindowLayout`, `Theming/AppPreferences.cs:63`) — one place to choose a layout, as that
file's own note says. Two windows resetting to the same default is correct.

Two consequences that are **not** free and are MW1's work:

- The `Window` menu enumerates `desktop.Windows.OfType<CrfHostWindow>()` — every float in the *process*
  (`WorkspaceViewModel.WindowMenu.cs:100-108`). Window B would list window A's torn-off panels.
- On macOS, `NativeMenu.SetMenu(app, menu)` pins **one** window's menu at application scope
  (`WorkspaceWindow.axaml.cs:229-235`), and every floating window is given that same instance to share
  (`AttachSharedNativeMenuIfMacOS`). With two shells the app-scope fallback becomes whichever won the
  race, and a float belonging to B can end up showing a menu whose commands bind to A.

### 2.3 "Can circuitRF use references for cells outside its workspace today?" — **Mechanically yes; deliberately, no.**

See §1.3. The design note already anticipated this: `.cws` records *referenced libraries*
(`workspace-and-project-tree.md` §5), the Project Tree already renders each as its own sub-tree and already
marks an unresolvable one as System.Warning + italics (§3.1/§3.2), and §4 already says a placed component
references a cell from an external library **by path**. What has never existed is the same thing for
another *workspace*, which brings a technology, a kit set and a `.cws` of its own along with it.

**R-mw-3. MW2 makes it deliberate**: a chosen reference form (an alias through `.cws`, MW2 §2), a supported
way to create one, a correct technology answer, a correct kit answer, an honest bad-cell state, and
archive/portability behaviour that does not silently produce a broken archive.

### 2.4 "Could we load the referenced cell's PDK? Is a PDK item in an unowned cell an error?" — **Yes, and yes.**

**R-mw-4. A kit reference resolves against the referenced document's OWN parent workspace, found by
walking up to the nearest ancestor `.cws`** — the identical rule `brief-foreign-documents.md` R-fgn-3
already fixed for technology, and the same helper. Consequences, all of them intended:

- Workspace A's cell, referenced from B, resolves its kit parts **if A's kit is mounted** — which it is
  whenever A is open in another window, and which MW1 §3 makes possible without unmounting B's.
- A cell with **no ancestor `.cws`** has no workspace to resolve a kit against. There is no guess to make
  and no prompt worth showing (unlike a missing technology, where R-fgn-4's three routes exist). It is
  **`NotFound` — the existing reported, repairable state**, and it already draws the pin-less placeholder
  through `CellSymbolResolver` step 0 (`CellSymbolResolver.cs:90-95`). That is exactly the owner's
  "should show as a bad cell in any renderings," and it needs a *marking* pass (MW2 §5), not a mechanism.
- A kit that is declared by a workspace nobody has open is likewise `NotFound`, and is repairable by
  opening that workspace. MW2 §4 decides whether to offer to mount it without opening it. **Recommendation:
  do not** — mounting a kit is a side effect the user did not ask for, and "open the other workspace" is a
  gesture they already have.

---

## 3. Sequencing, and what is deliberately out of scope

```
MW1  Multiple workspace windows              ← the shell + the four global registries. Ships alone.
 └─ MW2  External cell references            ← needs MW1's per-workspace kit scoping
     └─ MW3  Workspace-to-workspace drag-drop ← needs MW2's Reference option to be real
```

**MW1 is independently useful and must land first.** It is the feature the owner actually asked for —
viewing and inspecting two designs at once — and it does not depend on either of the others. If MW2 or
MW3 turn out to be more than they look, MW1 still ships.

**Out of scope for the whole series, stated so it is not re-litigated:**

- Docking documents across windows (R-mw-1).
- A shared/global Library panel, or any panel shared between windows (R-mw-2).
- Running one simulation across two workspaces.
- Editing the same file in two windows at once — MW1 §5 makes that *impossible*, not merely discouraged.
- Any change to `harmonicaRF` or `wBond`, which have their own shells and no `WorkspaceViewModel`
  (`HarmonicaApp.axaml.cs:88`, `WBondApp.axaml.cs:85`).

---

## 4. On completion

Write the findings to `src/Ui/RESOLVED.md` (**not** `CLAUDE.md`), and update `docs/design/ui-architecture.md`
with the per-window/per-process split MW1 §3 settles — that boundary is the durable architectural fact
this series produces, and it belongs in a design note rather than in three briefs.

`docs/design/workspace-and-project-tree.md` **§5B was written from this series ahead of implementation** and
is the authority the briefs defer to. Where the built thing differs from §5B, **change §5B** — and say what
changed and why, rather than leaving the note describing a design that was not built.
