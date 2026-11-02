# Brief 90 — The object tree's record rows: Hide all reaches them, their ticks work, they select both ways, and the Inspector edits them

**Tag:** `R-em3d90-n` · **Series:** 3D editor round 8 (briefs 90–95, from one owner report of 2026-09-29). This brief comes
first: 91 (the Hide key/button) calls the one visibility function it builds.
**Area:** `src/Ui/ThreeD/C3dEditorViewModel.TreeGrouping.cs` (`SetListedVisibility`), `C3dEditorViewModel.cs`
(`TreeVisibilityChanged`, `OnSelectedTreeItemChanged`, `SceneObjectsOfRow`), `C3dEditorViewModel.Thermal.cs`
(`RebuildThermalTree`, `ThermalTints`, `FillThermalOverlay`), `C3dEditorViewModel.ThermalBlocks.cs`,
`C3dEditorViewModel.FieldPlots.cs` (`SetPlotsVisibility`), `C3dPropertiesViewModel.cs` (`Reload`),
`src/Render/Scene3D/Scene3DBuilder.cs` + `Scene3DModel.cs` (face tints, `Pickable`), `docs/user/src/reference/drawing-in-3d.md`,
`docs/user/src/reference/thermal.md`
**Depends on:** 75 (thermal places, tints), 76 (symmetry planes), 83 (field plots) · **Blocks:** 91

"Records" in this brief means the tree rows that are not document objects: heat sources, probes, mesh regions,
effective blocks, symmetry planes, thermal boundaries and field plots.

---

## 1. `R-em3d90-1` — Hide all / Show all reach every row the tree lists

### What is wrong (read from the code)

`SetListedVisibility` (the tree header's eye / eye-off buttons):

- collects every row except the Field Plots group, then writes `Hidden` only on rows with `ObjectIndex >= 0`. Heat
  sources, probes, **mesh regions** and effective blocks have `ObjectIndex == -1`, so Hide all skips them. Their ticks
  already work one at a time, through `SetPlaceShown`;
- does nothing for **symmetry planes** and **thermal boundaries**. Neither has any visibility state (§2);
- calls `SetPlotsVisibility(false)` for **field plots**. That path looks right in the code, but the owner reports a field
  plot still showing after Hide all. **Reproduce it before fixing.** Candidates, in order: (a) the section the plot synced
  (`SyncSectionTo` sets `Viewer.ClipEnabled = true`) stays cut after the plot is hidden, so the view still looks like a
  plot; (b) a temperature drawn by the Thermal ▸ Plot path rather than as a `FieldPlots` record; (c) in a setup's view the
  plots are the session's, not `Document.FieldPlots`, so `Document.FieldPlots.Count == 0` returns early. Fix whichever it
  is, and record which in RESOLVED.md;
- makes the plots **their own undo entry**, pushed before `BeginGroup("Hide all")`. One press is two undo entries.

### What to build

- **One function**, `SetRowsVisible(IReadOnlyList<C3dTreeItem> rows, bool visible, string description)`, that every
  visibility gesture calls: the tick, Hide all / Show all, the row menu's Hide/Show, and brief 91's H key and toolbar
  button. It dispatches by row kind to the existing per-kind writers (`ChangeObjects … Hidden`, `ChangeOperand`,
  `SetGroupVisible`, `AirBoxShown`, `SetPlaceShown`, `SetPlotShown`/`SetPlotsVisibility`, the instance's
  view-only visibility) and to the two new ones in §2. It must not add a second copy of any of them.
- Hide all / Show all = `SetRowsVisible(every listed row, …)`. Rows the filter hides are still left alone, as the tooltip
  says.
- **One undo entry per gesture** for everything the document saves (object `Hidden`, the plots' `Hidden`, the air box).
  The view-only states (places, boundaries, planes, instance parts) are not undoable, as today.
- Show all on the plots keeps its rule: one plot drawn at a time, the selected one or else the first.

## 2. `R-em3d90-2` — symmetry planes and thermal boundaries get a tick that works, and are drawn by it

### What is wrong

- A **thermal boundary**'s row is built `visible: true`, and `TreeVisibilityChanged` returns at
  `if (item.Kind == ThermalBoundaryKindName) return;`. The tick flips and nothing happens. The blue or green tint
  (`ThermalTints` → `Scene3DFaceTint`) is drawn whatever the tick says. This is the owner's "I turned it off and still see
  the blue sheet".
- A **symmetry plane**'s row is built `visible: true, IsReadOnly = true`, and its tick falls through to a `SceneObject`
  lookup that finds nothing. **The editor does not draw a symmetry plane at all.** It only mirrors a temperature plot
  about it (`Viewer.SetSymmetryPlanes`).

### What to build

- **Thermal boundary visibility** is view state, the same as a place's: a set of hidden boundary keys beside
  `_hiddenPlaces`. It is not saved and not undoable, because a boundary has no `Hidden` in the setup JSON and should not
  gain one. The key is the tint name (`thermal:<face>`) qualified by the active setup's name, so switching setups does
  not carry one setup's hidden state onto another's boundary on the same face. `ThermalTints` leaves out a hidden
  boundary's tint. It runs on the build thread, so pass the hidden set in as data rather than reading editor state from
  that thread.
- **Symmetry planes are drawn**: in `FillThermalOverlay` (or a sibling), a rectangle covering the model's extent in the
  plane's two other axes, at `At`. Draw it as outline plus hatching, the way the editor's air box draws a symmetry face,
  so it reads as the same kind of thing. The row's tick shows or hides it (view state, a hidden-planes set). It is
  labelled `Symmetry X = 120 µm`.
- Both rows' `IsVisible` start from their hidden sets, not from a constant `true`.

## 3. `R-em3d90-3` — the rows select in the 3D view, and a click in the view selects the row

### What is wrong

`SceneObjectsOfRow` finds no scene object for either row kind, so selecting the row selects nothing in the view. In the
other direction, a tint is `Scene3DKind.Boundary`, and `Scene3DObject.Pickable` excludes `Boundary`, so a click on the
blue sheet hits the solid under it. That selects the solid, never the boundary.

### What to build

- **Tree → view:** a thermal boundary's row highlights its tint, drawn in the overlay's `Selected` colour, as a
  selected heat source is (`FillThermalOverlay`'s `selected`). A symmetry plane's row highlights its drawn rectangle the
  same way. The Properties Inspector follows (§4).
- **View → tree (owner, 2026-09-29): a click on a thermal-boundary tint selects the BOUNDARY.** The tint is pickable and
  wins over the face it lies on. The boundary's row is selected, the tint is highlighted, and the Inspector shows the
  boundary (§4).
  - The tint gets its own `Scene3DKind` (or a flag) so it becomes pickable while the air box's `Boundary` faces keep
    their current picking (`PickLast`). The ID pass must give the tint priority over the coplanar face it sits on.
    Decide that by kind, **not** by z-fighting luck. Say in RESOLVED.md how the priority is enforced.
  - **The EM face-boundary tints** (`Scene3DBuilder.FaceTintPrefix`, the tree's `Boundary` rows) are the same kind of
    thing on a face. Apply the same rule and put them in the same B cycle, so the two boundary types behave alike. If they turn
    out to be built differently, do the thermal ones and report the difference; do not force it.
- **The solid under it is reached with the EXISTING B key (owner, 2026-09-29).** **B is already bound and is not
  rebound.** `Viewer3DViewModel.HandleKey` sends plain B / Shift+B to `Cycle(±1)`, "the next thing behind, or in front,
  along the line of sight through the cursor" (R-em3d43-4). It steps through `HitCycle` over `RayHits.Collect` (plus
  `PickLastHits` for the air box's faces in Face mode). This brief adds **no key and no new function**. Its job is to put
  the tint **into that hit list, in front of the face it lies on**:
  - `RayHits.Collect` (and whatever builds the Object-mode list) returns the tint as a hit at its face's depth, **ordered
    before** the coplanar solid face. Order them by kind at equal depth, not by floating-point luck. So a click selects
    the boundary, the first **B** steps to the solid (in the current select mode: the object in Object mode, the face in
    Face mode), and Shift+B steps back to the boundary. The cycle's readout (`CycleText`, "… · 2 of 3") names the tint
    as `Thermal boundary <object>/<face>`, so the step is legible.
  - Hits the user cannot see are not stepped through: a hidden tint is not in the list (§2), just as `View.Visible`
    already filters hidden objects.
  - Selecting the solid this way makes its row the tree's selection, as any B step does. Check that a B step onto the tint
    selects the **boundary's row**; that is the one new mapping (scene id → record row). It must not leave the tree on
    the solid's row.
  - **B from the tree** needs nothing new. B is a pointer-and-line-of-sight gesture, so it acts where the cursor is over
    the view, as it does today.
  - Update the Selection.cs header comment and the `Cycle` doc comment to say boundaries are in the cycle.
- A symmetry plane's drawn rectangle is an **overlay**, not a scene object. Picking it is optional. If it is not
  pickable, say so in the row's tooltip ("select it from the tree").
- A hidden tint or plane can be neither picked nor drawn highlighted, but its row can still be selected, so the Inspector
  can still edit it.

## 4. `R-em3d90-4` — the Properties Inspector edits a symmetry plane and a thermal boundary

### What is wrong

`C3dPropertiesViewModel.Reload`, with nothing selected in the scene, handles field plots and places, then sets
`Heading = "Nothing selected"`. Symmetry-plane and thermal-boundary rows reach that line.

### What to build

- **Symmetry plane:** heading `Symmetry plane X`. Rows:
  - **Axis**: read-only. To change the axis, delete the plane and declare it again on another face.
  - **At**: a length in the document's display unit, and it may be an expression (`C3dSymmetryPlane` is
    `IC3dBindable`, so bind it the way a place's fields are bound). The edit is one undo entry through
    `ChangeRecords`. Validate with the same rule as `SymmetryOfFace`, which requires the plane to lie on the model's
    extent. A value inside the extent is refused, and the reason shown is that function's own sentence, so the rule is
    written once. Move the rule into `src/Design` if `check` should also enforce it; it should, see the gate.
  - A read-only line: `1/N of the device is modelled`.
- **Thermal boundary:** heading `Thermal boundary on <object>/<face> · <setup>`. Rows:
  - **Kind**: Fixed temperature or Convection. Switching kind keeps the other kind's values in memory while the row stays
    selected, so switching back does not lose them.
  - **T** (°C) for fixed; **h** (W/(m²·K)) and **Ambient** (°C) for convection. Each accepts an expression as far as the
    setup's schema allows.
  - **Face**: read-only, with a *Select face* link that selects that face in the view (Face mode).
  - Each edit goes through the same function the right-click ▸ Thermal Boundary gesture uses (`SetThermalBoundary`),
    which is already one undo entry, so the Inspector adds no second writer.
- Both show read-only in a setup's view-only 3D view, as places do there.

## 5. Gate

View-model tests only, run with `--filter`. Pixels cannot be checked from this machine's shell (Avalonia cannot start
here), so say so in the report.

1. A document with one of each record kind plus two boxes and an active thermal setup with a FixedT boundary: Hide all
   hides every row, and each row's own state agrees (object `Hidden`, the plot's `Hidden`, the place and boundary and
   plane hidden sets, `AirBoxShown`). There is **one** undo entry, and undo restores every saved state.
2. The field-plot repro from §1, first failing, then passing, with the cause named in the test's comment.
3. A thermal boundary's tick off: `ThermalTints` for that build has no tint of that name. Tick on: it is back.
4. A symmetry plane's row selected: the overlay's `Selected` list carries its rectangle, and the Inspector heading is
   `Symmetry plane X`. Editing At to a value inside the extent is refused with `SymmetryOfFace`'s sentence, and to the
   other extent face is accepted (one undo entry).
5. Selecting a thermal boundary's row highlights its tint. A simulated pick at a point on the tint returns the tint (not
   the coplanar solid face) and selects the boundary's row. `Cycle(+1)` at the same cursor then selects the owning
   solid (the row, the scene selection and the Inspector heading all name it), and `Cycle(−1)` returns to the boundary
   and its row. With the boundary hidden, the cycle never lands on it. The Inspector's h edit on a convection boundary
   writes the setup and is one undo entry.
6. `check` on a `.c3d` whose symmetry plane lies inside the extent reports it (if the rule moved to `src/Design`).

## 6. Decisions for the owner before building

- **D1 — settled (owner, 2026-09-29):** a click on a tint selects the boundary, and the existing B (select behind) steps to the solid under it (§3).
- **D2** — is a symmetry plane's `At` editable at all, or is it read-only with "declared on a face" as the only gesture?
  The owner's report asks to edit it, so this brief assumes editable, constrained to the model's extent.

## Docs

`docs/user/src/reference/drawing-in-3d.md` (object tree: Hide all covers every row; the boundary and plane ticks; a click
on a boundary selects it, and B steps behind it to the solid) and
`thermal.md` (a boundary edited in the Inspector; the drawn symmetry plane). Edit the doc sources only; no DocGen run.

## On completion

Findings go in `src/Ui/RESOLVED.md` (and `src/Render/RESOLVED.md` for the tint picking), never CLAUDE.md. Do not commit
unless the owner asks.
