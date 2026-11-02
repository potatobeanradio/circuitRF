# Brief 83 — Field plots are document objects: in the tree, in the inspector, saved in the `.c3d`

**Tag:** `R-em3d83-n` · **Design note:** [`em-3d.md`](../design/em-3d.md) §8.4
**Area:** `src/Design/ThreeD/` (`C3dDocument`, `C3dPersistence`, a new `C3dFieldPlot`), `src/Ui/Viewer3D/Viewer3DViewModel.Fields.cs`,
`src/Ui/ThreeD/C3dEditorViewModel.*` (tree, records, stale banner), `src/Ui/ThreeD/C3dPropertiesViewModel.cs`,
`src/Ui/Views/ThreeD/C3dEditorView.axaml` (the Fields strip), `src/Ui/Views/ThreeD/C3dPropertiesView.axaml`,
`src/Ui/Views/Layout/EmSetupEditorView.axaml` (R-6 only), `docs/user/src/`, `tests/Ui.Tests/ThreeD/`
**Depends on:** 29 (EM fields in the 3D view), 49 (setups in the `.c3d`), 75 (temperature plot), 82 (fields on faces)
· **Blocks:** —

---

## 0. What the owner reported, and why it happens

Owner's list, 2026-09-28:

1. *Plotted fields do not survive a save, close and reopen.* Correct, and by design until now: everything the Fields
   strip holds — `ShowField`, `SelectedFieldSolution`, `SelectedFieldQuantity`, `FieldOnClipPlane`, `FieldOnSurfaces`,
   `FieldDb`, `FieldPercentile`, the phase and loop, and brief 82's `_fieldFaces` — lives on `Viewer3DViewModel` and
   nowhere else. Nothing reaches the `.c3d` or the `.cwsuser`. Reopening re-reads the run
   (`RefreshFields`) and starts with the field off.
2. *Fields should be objects in the Object Tree* — defined, shown/hidden, renamed, edited in the Properties inspector
   — and persisted in the `.c3d`, with an indicator when the data underneath is gone. The toolbar Fields strip can then
   go.
3. *Only 10 GHz was offered.* Not a picker bug: Palace saves fields only at the frequencies in
   `Palace.SaveFieldsGHz`, and **the default is the sweep's centre** (`em-setup.md`, "Fields for the 3D view"). The
   owner's Launch setup sweeps 2–18 GHz, so one step at 10 GHz is all the run wrote. The solution combobox
   (`FieldSolutions`) already lists every saved step; it had one. The fix is making the saved frequencies VISIBLE and
   EDITABLE where the plot is defined (R-6), not a new picker.

## 1. `R-em3d83-1` — `C3dFieldPlot`, a record of the document

A new list `C3dDocument.FieldPlots` (written only when non-empty; `FormatVersion` unchanged — an older build keeps an
unknown key in `Unread` as today, check that it does). One plot:

| Field | Meaning |
|---|---|
| `Name` | Unique among plots; default `Field1`, `Field2` … (the setup naming rule) |
| `Setup` | The setup whose run it reads, by name. Null = the active setup at draw time |
| `Solver` | `Palace` / `OpenEms`, only when the setup is `Both` |
| `Solution` | What to show: a frequency in GHz (driven), a mode number (eigenmode), a terminal name (static), a point index (thermal). **Stored as the value, never the combobox index** — a re-run that saves more frequencies must not move the plot to another one |
| `Quantity` | `FieldQuantity`'s array name + mode (e.g. `E`/`Peak`, `J_s`/`Real`) |
| `On` | Where: `ClipPlane` (with its own `Axis` and `Offset`, below), `Surfaces` (today's FieldOnSurfaces), `Faces` (brief 82's list of `object/face[/side]`) — one plot has exactly one target |
| `Axis`, `Offset` | The clip plane's, for a `ClipPlane` plot — owned by the plot, so two plots can cut in two places |
| `Db`, `Percentile` | The colour scale |
| `Hidden` | The tree tick, as `C3dObject.Hidden` |

The phase and loop period stay **view state** (an animation is not a design decision); so does play/pause.

**Every edit is a records edit** (`ChangeRecords` → one undo entry, dirty mark), exactly like a probe or a heat source.
Draw several visible plots at once; the colour scale is per plot (each has its own legend line — `FieldLegendLines`
grows a plot name). If several plots is more than a day's work in the renderer, ship one visible at a time and say so
— Q2.

## 2. `R-em3d83-2` — plots must not make the run stale

**Trap, read before writing R-1.** `C3dEditorViewModel.RefreshFieldsStale` compares the run's saved document text
(`RunDocumentFile`) with `C3dPersistence.Serialize(Document)`. Put plots in the document and **adding a plot marks
every result stale**. Likewise anything that hashes the document for mesh reuse or for Simulate's "unchanged model"
check. Plots, like the setups' display-only fields, are excluded from both comparisons — one helper
(`C3dPersistence.SerializeForRun` or similar) used by every such comparison, and a gate that adds, edits and hides a
plot and asserts the banner stays down and the mesh is reused.

## 3. `R-em3d83-3` — the Object Tree

A **Field Plots** group (`C3dTreeGroupRole.FieldPlots`), after Probes, in both groupings (By material / By type). Each
row: the tick (Hidden), the name (inline rename, the same gesture as an object), a detail line
(`|E| · 10 GHz · clip Z`). Context menu: Show / Hide, Rename, Duplicate, Delete, **New Field Plot…**. The group's own
context menu (and the header's `+`) adds one; the right-click-a-face ▸ Plot Field of brief 82 now **creates a `Faces`
plot** (or adds the face to the selected `Faces` plot) instead of toggling view state. Show all / Hide all (round 5)
include plots.

## 4. `R-em3d83-4` — the Properties inspector

Selecting a plot row shows its fields in `C3dPropertiesView`: Setup (combo of the document's setups, "Active" first),
Solver (only for `Both`), **Solution** (a combobox of what the run actually saved — the owner's frequency picker), Quantity
(only what the step offers — `FieldQuantity.Offered`, unchanged), On, the clip plane's Axis/Offset (with units, the
round 4 inspector rules), dB, Percentile. Each commit is one `ChangeRecords` entry. The phase slider, Play and loop
period sit at the bottom of the inspector while a plot is selected (view state, not saved).

## 5. `R-em3d83-5` — when the data is missing

A plot whose setup has no run, whose run no longer holds its `Solution`, or whose quantity the step no longer offers
**stays in the document and in the tree** and draws nothing. The row carries a warning glyph and a tooltip that says
which: *"No run of setup Palace yet — Simulate to draw this plot."*, *"The run saved 2, 6 GHz; this plot shows 10 GHz.
Pick a saved frequency, or add 10 to Save fields at and run again."*, *"The run no longer offers J_s."*. The inspector
shows the same sentence above the fields. Never re-point a plot silently to the nearest frequency. A stale run
(R-2's banner) still draws, as today.

## 6. `R-em3d83-6` — saved frequencies, where the plot is defined

The Palace section of the setup panel has no box for `SaveFieldsGHz` today (it is `.cem`-only). Add **Save fields at
(GHz)** — a comma list, blank = the sweep's centre (shown as the placeholder with its value, e.g. `10`), `none` = `[]`.
Its tooltip states the cost (the `em-setup.md` §"Fields for the 3D view" sizes). The inspector's Solution combobox ends
with *"Other frequency… (needs a re-run)"*, which adds the value to the setup's list (one records entry) and marks the
plot missing until the run exists (R-5). openEMS: the same, on whatever openEMS's field dump is keyed by today.

## 7. `R-em3d83-7` — retire the Fields strip

Once R-3 to R-6 land, the toolbar strip in `C3dEditorView.axaml` (Field toggle, solution and quantity combos, clip /
surfaces / dB / percentile, the phase controls) goes. What stays on the canvas: the legend, and the stale banner. The
thermal run's extra controls (All faces, the sweep step) become fields of a thermal plot in the same inspector.
`View ▸ Fields` (if present) toggles the selected plot's visibility; the read-only `Viewer3DView` (Show 3D from a `.cem`)
shows the tree's Field Plots group read-only — it has no document to save into, so its plots are session-only, and it
says so in the group's header tooltip.

**Migration:** none needed — nothing was ever saved.

## 8. Non-goals

- Field plots in the CLI (`render --field`) — the document now carries enough to do it; brief of its own.
- Vector arrows, streamlines, a cut on an arbitrary plane (axis-aligned only, as today).
- Plots of the far field (the Radiation pattern has its own display).

## 9. Gates

1. **Round trip:** a document with two plots (one `ClipPlane`, one `Faces` with a side) serializes, reopens and draws
   the same `FieldGeometry` triangle count and colour range on the committed PEC-cavity fixture
   (`testdata/em3d/fields/`).
2. **Not stale:** add, edit, hide, rename a plot — `FieldsStaleText` stays null and the mesh-reuse check still says
   unchanged (R-2).
3. **Undo:** each inspector edit is one undo entry; undo restores the drawn geometry.
4. **Missing data:** a plot at a frequency the fixture did not save reports the R-5 sentence naming the saved ones, and
   draws nothing; saving the frequency list and a fixture with that step clears it.
5. **Solution stored by value:** a fixture run with steps {2, 10} then {2, 6, 10} keeps the plot on 10 GHz.
6. **Tree:** the group lists plots in both groupings; Show all / Hide all include them.
7. **Strip gone:** a source scan that `C3dEditorView.axaml` binds none of `ShowField`, `SelectedFieldSolution`,
   `FieldOnClipPlane` (the strip's retirement is held, not remembered).
8. Brief 29's gate 5 counter (`FieldGeometryBuilds` — no geometry on a phase step) still holds with two plots visible.

Minimal tests — one per claim, committed fixtures only, no timing test.

## 10. Owner questions

- **Q1.** Plot name default: `Field1` … or the reading itself (`|E| 10 GHz`) until renamed?
- **Q2.** Several plots visible at once (each its own colour scale and legend line), or one visible at a time with the
  others kept? The first is what a user comparing two frequencies wants; the second is cheaper.
- **Q3.** Should a plot with `Setup` = null follow the ACTIVE setup (switching setups re-draws the same reading from the
  other run), or should a new plot always pin the setup that was active when it was made?

## On completion

Findings go in `src/Ui/RESOLVED.md` (under a "brief-em3d-83" heading) and `src/Design/RESOLVED.md` for the document
record. Never in any `CLAUDE.md`. User docs: the 3D editor page's Fields section and `em-setup.md`'s
"Fields for the 3D view" — edit the sources only; the owner regenerates `docs/user`.
