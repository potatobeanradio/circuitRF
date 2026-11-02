# Brief — layer visibility and selectability belong to the VIEW: out of the `.ctech`, into the layout's own Layers list

**Tag:** `R-lyr-n` · **Phase:** feature + format change (field feedback)
**Area:** `src/Design/Layout/TechModel.cs`, `src/Design/Layout/TechPersistence.cs`,
`src/Design/Workspace/WorkspaceUserPersistence.cs`, `src/Ui/Layout/LayoutEditorViewModel.cs` (+ a new
`LayoutEditorViewModel.Layers.cs`), `src/Ui/Views/Layout/LayoutEditorView.axaml`,
`src/Ui/Layout/TechEditorViewModel.cs` + `src/Ui/Views/Layout/TechEditorView.axaml`,
`src/Ui/Layout/UndefinedLayerSession.cs` (retired), `src/Design/RailRf/RailDocument.cs` +
`src/Ui/RailRf/RailRfViewModel.Layers.cs`, `src/Cli/Render.cs`, `src/Cli/Explain.cs` +
`ExplainQueries.cs`, `src/Ui/Layout/TechnologyMerge.cs`, docs
**Model to follow:** [railRF brief 20](brief-railrf-20-layer-visibility.md), whose list this one
copies in look and behaviour
**Found by:** a first-time user, 2026-10-15. Clicking a microstrip line that sits on a ground plane
selected the plane. They wanted to stop the plane being selectable and could not find a way to do it.

---

## 0. The decisions (owner, 2026-10-15)

| | |
|---|---|
| **D1** | **`Visible` and `Selectable` are no longer stored in the `.ctech`.** They are a property of a VIEW of a layout, not of the process. A technology is a shared manufacturing document. Using it as a display switch made one user's navigation into another user's diff, and turning off selection on a ground plane dirtied the technology for every layout that uses it. |
| **D2** | **Each layout's choices are kept in the per-user `.cwsuser`, keyed by layout path.** They are not in the `.clay`, which is the shared artwork, and they are not session-only. |
| **D3** | **Nothing is carried over.** A user's existing `.ctech` that had a layer's `Vis` or `Sel` unticked opens with that layer visible and selectable. No shipped or example technology sets either flag to false (checked against every tracked `.ctech`), so this affects only hand-edited files. |
| **D4** | A layout nobody has changed shows **every layer visible and selectable**. |

## 1. What exists today

- **`LayerDef.Visible` and `LayerDef.Selectable`** (`TechModel.cs:94`, `:97`) are serialized as part
  of the layer, because `CtechFile.Layers` is a `List<LayerDef>` (`TechPersistence.cs:22`, `:242`).
- **The technology editor's `Vis` and `Sel` columns** (`TechEditorView.axaml:483`, `:621`) write
  them. From a layout, the only way there is the status bar's **Technology ▾ ▸ Edit…**.
- **`LayoutEditorViewModel.DrawTechnology`** (`:176`) is a clone used only for drawing and picking.
  Today it carries only `UndefinedLayerSession`'s rows, for the layers the technology does not define.
- **Click-cycling exists, and nothing on screen tells the user.** A second click at the same spot
  selects the next shape underneath (`ClickCycleCache`, `LayoutEditorViewModel.cs:1453`).

## 2. `R-lyr-1` — the format change

### `R-lyr-1a` — stop persisting, keep the in-memory properties

`LayerDef.Visible`/`Selectable` **stay as runtime properties**. Every renderer, hit test, snap query
and extent calculation in `src/Render` reads them off whatever technology it is handed, and none of
them needs to change. What changes is that they are **never written to a `.ctech` and are ignored
when one is read**. Use `[JsonIgnore]`, or a separate file DTO for a layer, whichever is cleaner.

**Compatibility, both ways, with no format-version bump:**
- An older `.ctech` that has the keys loads, and the values are ignored (D3).
- An older circuitRF reading a new `.ctech` finds no keys and uses `true`, its own default.

**Re-save every tracked `.ctech`** (examples, shipped technologies, test fixtures) so the keys are
removed. Otherwise every file keeps the stale keys until someone happens to save it. This is a
mechanical, separate commit-sized step. Check it with a test (§7, test 1), not by eye.

### `R-lyr-1b` — THE TRAP: `Clone` round-trips through JSON

`TechPersistence.Clone` (`:150`) is `DeserializeUnresolved(Serialize(tech))`. Once the flags are not
serialized, **every clone silently resets them to `true`**. That would break the draw copy
(`DrawTechnology`), `TechnologyLayerSelection` (the `render` CLI's `--layers`/`--hide-layers` clone),
and the other eight `Clone` call sites. Nothing errors; layers just reappear.

Fix it in `Clone`: copy the two flags across layer by layer, by key, after the round trip. Then
`Clone` stays the one answer to "a copy of this technology". Do not apply the overrides after each
call site's clone; that is ten places to remember. Test it directly (§7, test 2).

### `R-lyr-1c` — what reads or writes the flags today, and what each becomes

| Where | Today | After |
|---|---|---|
| Technology editor (`TechEditorView`, `LayerRowViewModel`, `TechEditorViewModel:292-320` header toggles) | `Vis`/`Sel` columns and select-all toggles | **Removed.** The technology editor edits the process, and the layout's list is the one place for view choices. |
| `UndefinedLayerSession` + `UndefinedLayerRowViewModel` | session-only rows in the technology editor for layers the `.ctech` does not define | **Retired.** Those layers become ordinary rows in the layout's list (§3), saved the same way. |
| `TechnologyMerge.cs:536`, `TechEditorViewModel:797` | copy/compare the flags | stop. A merge must never report a conflict over a view choice. |
| `ProcessTechnologyBuilder.cs:276` (layer-properties import), `DxfLayerReconciliation.cs:77` (a DXF layer that is Off or Frozen) | an import sets `Visible = false` | **Dropped** (D3/D4). The imported layers show. The DXF import report already lists layers, so say there, in one line, which source layers were off. |
| `GerberImport.cs:2639` | sets both `true` | no change needed (the default) |
| `RailDocument.Shows` (`:278`) + `RailRfViewModel.Layers.cs` | the `.crail`'s override on top of the `.ctech`'s `Vis`, plus a **Follow the technology** button | `Shows` becomes `!HiddenLayers.Contains(key)`. **`ShownLayers` and `HiddenByTechnology` go.** The button becomes **Show all**. An old `.crail` with `ShownLayers` still loads, and the list is ignored. The `.crail` stays railRF's store. |
| `render` CLI (`Render.cs:975-1040`) + `DocumentExtents.cs:208` | skips, and fits around, the layers the `.ctech` hides | draws **every** layer unless `--layers`/`--hide-layers` says otherwise. A headless picture never depends on anyone's view settings. Update the `render` text in `docs/design/cli.md` and the root `CLAUDE.md`'s description of `render` only where it states the old rule. |
| `explain` CLI (`Explain.cs:1681`, `ExplainQueries.cs:310`) | reports `hidden`/`locked` per layer | drops both. Check the MCP `explain` output schema and its tests for the same fields. |
| The live `.ctech` seam (`TechLiveChanged`, railRF brief 20 R-rail20-2) | carries visibility and colour | carries colour and fill only. Remove the visibility handling and leave the seam in place. |

Grep for any other reader before starting
(`grep -rn "\.Visible\b\|\.Selectable\b" src --include='*.cs'`, then filter to `LayerDef`). The
table above is what was found on 2026-10-15, not a guarantee.

## 3. `R-lyr-2` — the layout's Layers list

A collapsible **Layers** list inside the layout editor pane, laid out like railRF's board-pane list
(`RailRfWindow.axaml` ~1660-1710). It has a header row with a disclosure toggle and **Show all**,
then one row per layer. The rows are the resolved technology's drawing layers **plus** every layer
this layout's shapes use that the technology does not define, drawn with the fallback palette's look.

| Column | Meaning |
|---|---|
| **Vis** checkbox | whether this view draws the layer |
| **Sel** checkbox | whether a click, marquee, Select All or snap grab can pick a shape on it. Disabled while Vis is off, because a hidden layer is never pickable (`LayoutHitTest.cs:105`). |
| colour swatch | opaque, as in `RailLayerRowViewModel.SwatchColor` |
| name | the technology's name for the layer, or the fallback palette's spelling |

- **Clicking a row's name makes it the current drawing layer**, the same as the toolbar's **Layer:**
  combobox (`LayoutEditorView.axaml:535`), and the two stay in step both ways.
- **Show all** sets every row to visible and selectable. It is disabled, not hidden, while nothing is
  off.
- Whether the list is expanded is **session state only**, for the reason `RailRfViewModel.ShowLayerList`
  gives.
- Toggling **dirties nothing**: not the `.clay`, not the `.ctech`. It never re-runs anything.

## 4. `R-lyr-3` — "select on current layer only"

One toggle in the header. While it is on, only shapes on the current drawing layer can be picked,
whatever the Sel column says. The column is left unchanged so it returns when the toggle is turned
off. This is the one-click answer to the field report: pick the trace's layer, turn the toggle on, and
the plane cannot be selected. It follows the current layer as that changes, and it is saved with the
rest (§5).

## 5. `R-lyr-4` — storage

`CwsUserFile` gains a map keyed by layout path, written **workspace-relative** like its siblings
`Viewer3DCameras` / `C3dDrawingPlanes` (`WorkspaceUserPersistence.cs:170-179`):

```
LayoutLayerViews: { "<layout path>": { Hidden: [keys], Locked: [keys], CurrentLayerOnly: bool } }
```

- **Store only what is off**, so a layout nobody has touched writes nothing (D4).
- **Keys, not names.** A layer renamed in the `.ctech` keeps its state, because the key is the
  layer's identity.
- A key that no longer resolves to a layer is kept, not pruned. It may come back when someone switches
  the technology, and it costs nothing.
- **A layout opened with no workspace** has no `.cwsuser`, so its choices last for the session only.
  Say that in the docs; do not invent a second store for it.
- **Renaming or moving a layout should carry its entry.** There is no precedent to copy: the 3D
  camera and drawing-plane maps beside it are NOT re-keyed on a move (checked 2026-10-15), so an entry
  is orphaned and the view returns to all-on. Re-key it in the cell rename/move path
  (`WorkspaceViewModel.TreeMove.cs`, `CellRelink.cs`) if that is cheap. If it is not, accept the reset
  and say so in the completion note.
- **The new field must be added in BOTH places `.cwsuser` lists its fields**: the emptiness test
  (`WorkspaceUserPersistence.cs:185`) and the field list that routes keys to the per-user file
  (`:224`, with the copy at `:238`). If a field is missing from the routing list, it is written to the
  shared `.cws`, which is exactly where this state must not go.

## 6. `R-lyr-5` — one seam: `DrawTechnology`

The view's choices are applied to `DrawTechnology`'s clone. Keep its version-stamp cache and add a
view-state version to the cache key.

**Audit every `Visible`/`Selectable` reader in `src/Ui/Layout` and route it through `DrawTechnology`,
so there is one answer, not two.** Most already do: `ResolveLayerDef`, `HitStack` at `:1698`/`:1707`,
and `LayoutCanvas.cs:503`/`:568`. **At least one does not:** `FindClipStencil`
(`LayoutEditorViewModel.Booleans.cs:272`) passes `Technology`. Check every `Technology` use in that
file. A Boolean *operation* needs the real technology; a *pick* needs `DrawTechnology`.

**Anything that saves, exports, runs DRC/LVS/EM, builds a 3D view or installs a technology reads
`Technology`, which no longer carries view state at all** (§2). A hidden layer is never missing from
an export or a simulation.

## 7. Gate

Write the minimum: one test per claim. Run only the affected test classes, not the whole of
`Ui.Tests`.

1. **The `.ctech` carries no view state.** Save a technology whose in-memory layers have the flags
   off, and assert neither key is in the JSON. Then scan every tracked `.ctech` for the keys (R-lyr-1a).
2. **`Clone` keeps the flags.** Clone a technology with a hidden, locked layer and assert both flags
   survive. This is R-lyr-1b's trap; it fails as soon as `[JsonIgnore]` lands without the fix.
3. **An old `.ctech` with `"Visible": false` opens with the layer visible** (D3).
4. **The field report.** A trace over a larger plane on another layer: lock the plane's layer and
   assert the hit stack at a point on the trace holds the trace and no plane. Assert Select All and a
   marquee also leave the plane out. Do it again with current-layer-only instead of the Sel column.
5. **Nothing is dirtied.** Toggle, and assert the `.clay` and `.ctech` documents are clean and both
   files are byte-identical.
6. **A hidden layer is still exported.** Hide a layer, export GDSII, and assert its shapes are there.
7. **Round-trip through `.cwsuser`**, and assert the key is absent from the `.cws`.
8. **railRF:** an old `.crail` with `ShownLayers` loads, and Show all clears `HiddenLayers`. Edit the
   existing `RailLayerVisibilityTests` rather than adding new ones.
9. **`render` draws a layer an old `.ctech` hid.** Edit the existing `RenderCliVerbTests`/explain tests
   that assert the old rule. Do not add new ones beside them.
10. **Row click sets the current layer**, and the toolbar combobox follows.

## 8. Docs (sources only; the owner runs DocGen at the end)

- `layout-editor.md`: a **Layers** section covering the list, current-layer-only, the per-user
  storage in plain words, and **click-cycling** ("click again to select the next shape underneath").
  The field report shows nobody finds it.
- The technology-editor reference page: remove the Vis/Sel columns.
- railRF's page: **Follow the technology** becomes **Show all**.
- `docs/design/layout-view.md`: where §2.2 or nearby describes `Visible`/`Selectable` as technology
  data, say they are view state now.
- Record the trap (R-lyr-1b) and the reader audit in `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`.

## 9. Scope

- **No colour editing, no reordering** in this list. Those are `.ctech` properties and that editor
  exists.
- **Not the 3D view** (its own object visibility is unrelated) and not the schematic.
- **No migration of old values** (D3).
