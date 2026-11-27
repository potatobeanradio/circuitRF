# Brief PCD-6 — The PCell Designer window and its entry points

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd6-n`
**Area:** new `src/Ui/PCellDesigner/`, `src/Ui/Views/PCellDesigner/`, `src/Ui/ViewModels/WorkspaceViewModel.PCellDesigner.cs`,
`src/Design/PCellDesigner/PCellDesignResponse.cs`; `src/Ui/Views/WorkspaceWindow.axaml` (both Tools menus, File ▸ Import),
`src/Ui/Views/Content/SchematicView.axaml(.cs)` and `src/Ui/Views/Layout/LayoutEditorView.axaml(.cs)` (context menus)
**Depends on:** PCD-0, PCD-1's seams. Its gesture tests need PCD-2 `Drag`, PCD-3 `Stretch`, PCD-4 `Merge`/`PinDrop` and
PCD-5 `Create`/`Export`/`Import`; the window builds against the stubs before them.

---

## 0. What this adds

One window in which a designed cell is made: its layout drawn at the current values, its parameters, its netlist text,
its circuit response and its EM response. Everything it does is a call to a `src/Design` function another lane owns,
so the window and `circuitrf pcell` (PCD-7) cannot disagree.

## 1. Requirements

- **`R-pcd6-1` — the entry points.** Tools ▸ PCell Designer… (both Tools menus) opens the designer on the active
  schematic's cell when it is designed, else on a new designed cell `PCell<k>` in the workspace (`PCellDesign.Create`).
  Edit in PCell Designer on the schematic and layout context menus opens it on a placed designed cell. File ▸ Import ▸
  PCell Package… imports a package. One window per cell folder: a second request brings the open one forward.
- **`R-pcd6-2` — the canvas.** `DesignedCellLayout.At` at the current values, drawn by `LayoutCanvas` (the
  `LayoutRenderer` `circuitrf render` draws with), read-only. Gestures, each one undo step (a snapshot of the
  schematic), each followed by a re-plan: a Library part dropped on a pin → `PinDrop.Apply`; an offset part dragged →
  `OffsetPlacer.Drag`; `MSHAPE` vertices selected → Stretch by Parameter… → `DrawnMetal.Stretch`; a vertex drag adds
  its change to the vertex's expressions.
- **`R-pcd6-3` — the parameter table.** One row per `.ccell` parameter: name, default with unit, Minimum, Maximum,
  description, and a Value column (not saved) that drives the canvas and the response. Add and Remove.
- **`R-pcd6-4` — the text view.** The cell's `.cnl` (`SchematicCircuit.CnlTextOf`), live; Apply merges an edit through
  `CellNetlistMerge.Merge`, as one undo step; a refusal is shown and nothing changes.
- **`R-pcd6-5` — the live response.** `PCellDesignResponse.Run` on a worker, debounced and cancelled as the LC Filter
  Designer's `DesignAsync`; |S| of every port pair into port 1 on the Data Display plot.
- **`R-pcd6-6` — Run EM.** Save, the cell's `.cem` made by `EmSetupFromSchematic.Run` when it has none,
  `EmCapability.Resolve` and `EmEvaluation.Evaluate` at the current values on a worker with Cancel; the result is
  overlaid on the response.
- **`R-pcd6-7` — Optimise.** Save, then open the cell's schematic and the existing Optimizer panel on it.
- **`R-pcd6-8` — Export… and Import PCell Package….** `PCellPackage.Export` (it runs the design check) and
  `PCellPackage.Import` into the workspace root; findings to the status line and Messages.
- **`R-pcd6-9` — Save.** The `.csch`, the `.ccell` (parameters, `PCellDesign`) and the cell's own `.clay`, which is
  `DesignedCellLayout.At` at the defaults (S1), never the values column.

## 2. Decisions

- **D1 — The view model owns everything a test touches.** `PCellDesignerViewModel` holds the schematic, the values,
  the undo stack and every gesture as a method taking world coordinates or names; the window only turns pointer
  events into those calls. Every behaviour is tested headless through it.
- **D2 — A snapshot undo.** The undo stack holds the serialised `.csch`. A gesture's refusal pushes nothing.
- **D3 — New cell name.** Tools ▸ PCell Designer… on no designed cell makes `PCell1` (`PCell2`, … when taken), as the
  Filter Designer's place target makes `Filter1`. Renaming is the project tree's.
- **D4 — Values are text.** A value is typed as a parameter's default is (`200 um`, or `200` in the parameter's unit).
  The canvas reads it in base SI (`TunableValue`); the response passes it as a `Sets` entry, unit kept.
- **D5 — Sweep.** The response uses the schematic's first S-parameter analysis; with none, `CcellPCellDesign.Sweep`,
  else `start=1 stop=20 npts=201 Unit=GHz`.
- **D6 — Pin poses from the plan.** A pin's pose is its generated cell's `LayoutPin` through the instance transform,
  found by the instance's `SchematicId`. `OffsetPlacer.Drag` gets the anchor's and the part's poses from there.
- **D7 — Optimise and import go through the workspace.** The window gets them as delegates from
  `WorkspaceViewModel.PCellDesigner.cs`, so it needs no `WorkspaceViewModel` and a test passes its own.

## 3. Window

```
+------------------------------------------------------------------------------------+
| Save Undo Redo | Run EM Cancel | Optimise Export… | status                         |
+------------------------------------------------+-----------------------------------+
| canvas: the layout at the Values               | Parameters                        |
|   drop a part on a pin, drag an offset part,   | Name Default Unit Min Max Desc Val|
|   select MSHAPE vertices: Stretch by Param…    |                         [+] [-]   |
|                                                +-----------------------------------+
|                                                | response: |S| circuit, EM overlaid|
+------------------------------------------------+-----------------------------------+
| .cnl text (live)                                                         [Apply]   |
+------------------------------------------------------------------------------------+
```

## 4. Tests

`tests/Ui.Tests/PCellDesigner/Pcd6*.cs`, one per behaviour, headless through the view model; the gesture tests last,
once their lanes' bodies are in.

## 5. Possible enhancements

- **A vertex editor for `MSHAPE` on the canvas** (insert, delete a vertex). The parameter rows already hold the
  vertices; a canvas editor is a second way to write them, worth it once shapes with many vertices are common.
- **A parameter sweep overlay** (the response at Minimum, default and Maximum at once), which shows a range's effect
  at a glance; three runs per edit cost three times the live response.
