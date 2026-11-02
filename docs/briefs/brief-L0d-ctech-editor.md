# Sonnet Brief — Phase L0d: the `.ctech` editor document

**Design:** `docs/design/layout-view.md` §2.1/§2.2 (layer identity, literal colors), §2.4 (what a `.ctech`
holds and its editor), §9A.1 (the DRC rule model). **Consumes L0a** (`Technology`, `TechPersistence`,
`TechValidation`, `StarterTechnologies`), **L0b** (the document/dock/tear-off pattern), and **L0c**
(`TechnologyCache` and its change seam).

**Scope is L0d ONLY: a dockable editor for the `.ctech` file.** This is the last brief in Phase L0; when it
lands, L0's gate line *"a layer color edit live-refreshes open layouts"* is satisfied and L1 (drawing tools)
can begin.

## Goal

Double-clicking a `.ctech` in the project tree opens a tear-off document with three editable sections —
**layer table**, **stackup**, **DRC rules** — plus live validation. Saving writes the file, invalidates the
cache, and fires L0c's change seam so every open layout picks the change up immediately.

## Verified substrate (consume — already exists)

- **L0c**: `TechnologyCache.Invalidate(absPath)` → `TechnologyChanged` → `WorkspaceViewModel.OnTechnologyChanged`
  → `LayoutEditorViewModel.ApplyTechResolution`. **The whole refresh path already works.** L0d's only job on
  the refresh side is to call `Invalidate` after a successful save.
- **`OpenNode`'s missing `TechFile` case** — L0c deliberately left `.ctech` falling through to the no-op
  `default` arm, documented as "L0d opens the editor". **Fill in that arm; that is the intended seam.**
- **`LayoutDocument` / `LayoutEditorView` / `LayoutEditorWindow`** (L0b) — clone the document, view and
  tear-off-host shapes. `WorkspaceViewModel` already has the `_openDocsByPath` registration, dirty
  participation, `ConfirmCloseDockable`, `SaveAllDocuments` and `.cws` restore patterns to mirror.
- **`ColorPickerDialog`** (`src/Ui/Views/Dialogs/ColorPickerDialog.axaml`) — **reuse it.** Do not add a new
  color-picking UI. Convert to/from `CircuitRF.Ui.Theming.Rgba` at the call site, in code-behind, per the
  UI firewall.
- `TechValidation.Validate(Technology) → IReadOnlyList<string>`, never throws.
- `ProjectTreeNodeViewModel` already has `IsTechFile`, `SetAsWorkspaceDefaultCommand`, `ReloadTechnologyCommand`.

## Code changes

### 1. `src/Ui/Layout/TechDocument.cs` + `TechEditorViewModel.cs`

Clone `LayoutDocument`'s shape: `Document`, `FilePath` (never scratch — see §5), `IsDirty` mirrored from the
VM, `OnSavedAs`, title with the dirty bullet.

**Undo: coarse-grained snapshots, and this is a deliberate departure.** The schematic and symbol editors use
fine-grained `IUiCommand`s on an `UndoRedoStack`, and L1's layout editor will too — geometry documents are
large, so cloning them per edit is not viable. A `Technology` is not: it is tens of layers, a handful of
stackup entries, and a few rules. So:

- Implement whatever undo interface `SymbolEditorDocument` implements, so Ctrl+Z routes correctly and the
  toolbar/menu light up.
- **Snapshot via the serializer**: `TechPersistence.Serialize(tech)` → string is an exact, already-tested
  deep clone that captures precisely what persists and nothing else. Push the *before* snapshot on each
  committed edit; undo deserializes and replaces the working `Technology`, then re-runs validation and
  re-projects the row collections.
- One snapshot per **committed** edit — a cell commit, an add, a remove, a reorder — not per keystroke.
- Say all of this in the class header, with the reason. Otherwise it reads as someone not knowing the house
  pattern.

The VM owns: `Technology Working`, the three row collections (§2–§4), `IReadOnlyList<string> ValidationIssues`,
`bool HasValidationIssues`, `SaveCommand`/`SaveAsCommand`, and `Undo`/`Redo`.

**Re-run `TechValidation.Validate` after every committed edit** and republish `ValidationIssues`. It is cheap
and it never throws, so there is no reason to defer it to save time.

### 2. Layer table

A `DataGrid` (or the existing grid idiom used by the parameter editor — match whichever the codebase already
uses rather than introducing a second) over `ObservableCollection<LayerRowViewModel>`, columns:

| Column | Editing |
|---|---|
| Name | inline text |
| Layer / Datatype | inline integer, `>= 0` |
| Color | a swatch button opening `ColorPickerDialog` |
| Fill opacity | inline `0.0–1.0` |
| Z-order | inline integer; plus ↑/↓ buttons that renumber |
| Visible / Selectable | checkboxes |
| Purpose | inline text, free-form |

Add / Remove / Duplicate buttons. **New rows get the next free `(Layer, Datatype)`**, never a duplicate —
duplicates are a validation error, and offering one by default is a trap.

**Sorting is display-only.** Sort by any column for browsing, but `Technology.Layers` keeps its own order and
the ↑/↓ buttons are what change it. A sort that silently reorders the persisted list would produce spurious
diffs on save.

### 3. Stackup

An ordered **top → bottom** list — a plain list editor with Add / Remove / ↑ / ↓, **not** a graphical
diagram. (§10.4's stackup *diagram* is L6, when the MoM work actually consumes it; building it now would be
polish on top of a model nothing reads yet.)

- Two combo boxes above the list for `Stackup.Top` and `Stackup.Bottom` (`Open` / `Ground`).
- Each row shows its `StackupKind` and name; the detail pane below shows **only the fields that kind uses** —
  `Dielectric` → thickness, εr, tanδ, µr; `Conductor` → thickness, σ (S/m), drawing layers;
  `Via` → thickness, drawing layers. Showing σ on a dielectric is how a stackup gets filled with meaningless
  numbers that later look authoritative.
- **Drawing layers** is a multi-select against the layer table — a checked list of `LayerDef`s by name, not a
  free-text field. This is the mapping §10.4 depends on, and it must be impossible to name a layer that
  doesn't exist. (`TechValidation` catches it anyway; the UI should make it unreachable.)
- Thickness fields are **physical dimensions**: use `LayoutUnits.TryParse` / `Format` so `1.6mm`, `35u` and
  `100 um` all work, displayed in the technology's `DefaultDisplayUnit`. Do not hand-roll a number parser.

### 4. DRC rules

A plain grid over `Technology.DrcRules`: Name (text), Kind (combo — `MinWidth` / `MinSpacing`), Layer
(combo against the layer table), Value (a `LayoutUnits` dimension field), Severity (combo — `Error` /
`Warning`). Add / Remove. Nothing executes these until L5b; L0d only has to make them editable and correct.

### 5. Creating, opening, saving

- **`OpenNode`** — the `.ctech` case opens or activates the editor via a new `OpenOrActivateTech`, mirroring
  `OpenOrActivateLayout` exactly (dedupe on `_openDocsByPath`, `await using` the stream, `ShowErrorAsync` on
  failure).
- **A `.ctech` that fails to load** — corrupt JSON or a newer `FormatVersion` — shows the error and does
  **not** open a blank document. Silently offering an empty editor over a file you couldn't parse invites
  saving over it.
- **New Technology…** — a File-menu and workspace-tree command. Prompt for a name and a starting point
  (**PCB starter** / **MMIC starter** / **Empty**), write to `tech/<slug>.ctech` (creating `tech/` if
  needed), then open it. Offer "Set as workspace default" as a checkbox in the same prompt.
- **No scratch technologies.** Unlike layouts, a `.ctech` is always backed by a file from the moment it
  exists — it is workspace-scoped configuration, and an unsaved floating one has nothing to be scoped to.
  `FilePath` is therefore never null; skip the whole scratch/materialize path.
- **Save** — `TechPersistence.SaveToFile` through `AtomicFile`, then **`TechnologyCache.Invalidate(path)`**.
  That single call is what makes L0c's seam fire and the open layouts refresh. Saving **is permitted while
  validation issues exist** (with the issues visible) — §2.4's rule is that a bad technology warns and still
  works, and refusing to save a work-in-progress would be worse than the problem.
- **Dirty / close / quit / Save All / `.cws` restore** — full participation, mirroring `LayoutDocument`.
  `.cws` open-document `kind="tech"`.
- **`ITreeActions.IsNodeDirty` / `SaveNodeAsync` / `SaveHeader`** — extend for `.ctech` ("Save Technology"),
  as L0b did for `.clay`.

### 6. App wiring

`App.axaml` maps `TechDocument → TechEditorView`. Add a `TechEditorWindow` tear-off host mirroring
`LayoutEditorWindow`, with `Ctrl/Cmd+Z`/`Y` key bindings since this document *does* have undo.

## Scope guardrails (do NOT do in L0d)

- **No graphical stackup diagram** (L6). No layer-visibility or lock UI driving anything — those flags feed
  the renderer, which does not exist until L1/L2.
- No DRC execution (L5b). No interchange layer mapping — GDSII/DXF/Gerber mappings are **L4** and are not
  in the `Technology` model yet; do not add fields for them speculatively.
- No geometry rendering, no layout tools, no fine-grained `IUiCommand` undo (see §1).
- No `FileSystemWatcher` — L0c ruled it out deliberately; saving invalidates explicitly.
- No technology inheritance/includes. No per-layout technology override UI (the model supports `TechRef`;
  a UI for setting it can wait until there is a reason to deviate).
- Don't touch `src/Core`, `src/Engine`, or `RfCore`.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. Double-clicking a `.ctech` opens the editor; it tears off and re-docks; a second double-click activates
   the existing tab rather than opening a duplicate.
3. **The L0 gate line.** With a layout open in a workspace whose default technology is `T`: open `T`, change
   a layer's color, save — and the open layout's `TechSummaryText` re-resolves through L0c's seam. Assert the
   layout document received the new `Technology` instance. *(The visible color change lands in L1/L2 when
   there is a renderer; what is provable now is that the notification arrives.)*
4. **Round-trip** — edit one field in each of the three sections, save, reload from disk, and assert all
   three changes persist and nothing else moved.
5. **Undo** — a color edit, a layer add, a layer reorder, a stackup reorder and a DRC-rule edit each undo
   and redo correctly; undo past the first edit is a no-op; `IsDirty` tracks back to false when undone to the
   saved state.
6. **Validation is live** — introducing a duplicate `(Layer, Datatype)` surfaces the `TechValidation` message
   without saving; fixing it clears the message. **Saving with issues present is allowed** and the file is
   written.
7. **New Technology…** from each of the three starting points writes a valid `.ctech` under `tech/`, opens it,
   and (with the checkbox on) sets `CwsFile.DefaultTechRef`.
8. **A corrupt `.ctech` reports and does not open a blank document.**
9. **Drawing-layer selection is closed** — a stackup conductor can only reference layers that exist in the
   table; deleting a referenced layer surfaces a validation message rather than corrupting the stackup.
10. **Dimension fields parse** — `1.6mm`, `35u`, `100 um` in a thickness field all produce the right DBU, and
    the value redisplays in the technology's default display unit.
11. Dirty participation: blocks a silent close, appears in Save All, shows the tree dirty dot and "Save
    Technology", prompts on quit, restores from `.cws`.

## On completion

1. Add a "Phase L0d — COMPLETE" entry at the top of `src/Ui/CLAUDE.md` in the established style. Call out
   explicitly: the **snapshot-undo departure and why**, that **`.ctech` has no scratch state**, that
   **save→`Invalidate` is what drives L0c's seam**, that **saving with validation issues is intentionally
   allowed**, and the test file names.
2. Note in the entry that **Phase L0 is complete** and that the next brief is **L1 — draw & edit**
   (the primitive tools, the §3.2 edge-list flattener and Flatten-to-Polygon, Clipper2 booleans and offsets,
   selection with overlap cycling, vertex/edge/bulge editing, clipboard, and fine-grained undo).
3. Report back before L1 is briefed.
