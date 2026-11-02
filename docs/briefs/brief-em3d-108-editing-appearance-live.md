# Brief 108 — Editing appearance, with live feedback

**Tag:** `R-em3d108-n` · **Series:** the realistic view ([overview 103](brief-em3d-103-overview.md)).
**Area:** `src/Ui/Layout/MaterialsEditorViewModel.cs`, `MaterialsTableViewModel.cs`, `src/Ui/Views/Materials/`,
`src/Ui/ViewModels/WorkspaceViewModel.Materials.cs` (`EditMaterial` ~246), `src/Ui/ThreeD/C3dPropertiesViewModel.cs`
(beside the Transparency row, ~286–475), `src/Ui/ThreeD/C3dEditorViewModel.EditMaterial.cs`, `src/Ui/Viewer3D/Viewer3DViewModel.cs`,
new `src/Ui/Views/ThreeD/LookPanel.axaml(.cs)`, `src/Render/Scene3D/Look/AppearanceSwatch.cs`
**Depends on:** 105, 106 (107 and 109 add rows to the Look panel when they land) · **Blocks:** —

## Why

The owner's requirement: whatever carries a visual property, the user must see what a change does, **live**, in the 3D view.
Appearance is per-material uniform data (106 §3b). A roughness drag is therefore a uniform write: no re-tessellation, no
re-elaboration, no upload of geometry. This brief puts the controls where the properties live, and holds that cost with
counters.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| The Materials editor edits **copies**; OK writes them | `MaterialsEditorViewModel`, `MaterialsTableViewModel` (the materials-editor redesign) |
| Edit Material… from a 3D object (brief 94) opens it on that object's material and technology | `C3dEditorViewModel.EditMaterial.cs`; `WorkspaceViewModel.EditMaterial` ~246 |
| The Inspector's Transparency row: multi-select, live preview while dragging, one undo entry, a stray-preview guard | `C3dPropertiesViewModel` ~286 (`EndStrayTransparencyPreview`), ~462 (`LoadSelectionTransparency`) |
| `ColorView` needs its Fluent theme supplied, or it instantiates blank | `HarmonicaAppearanceSettingsView.axaml` ~25–32 |
| The resolver's per-field provenance | 105 §3a |
| The canonical physics form of a material library | 105 §5a |

## 1. `R-em3d108-1` — appearance in the Materials editor

- **a.** An **Appearance** section in the material's detail pane: each of 105's keys as a slider plus a numeric box
  (colours through `ColorView`, with its theme supplied as the Harmonica dialog does), a **Like** combo listing the
  materials in scope, and a per-field **reset** that returns the field to "not stated". A field not stated shows the value
  it resolves to, greyed, with its provenance in the tooltip (`role default`, `Like 'Gold'`).
- **b.** A **swatch**: a 96 × 96 sphere shaded on the CPU by `AppearanceSwatch`, which calls 106's `Pbr.cs` with an
  **analytic** sphere (a per-pixel normal, no rasterisation) under the current environment. It needs neither the GPU nor 110,
  so it works when no 3D view is open. It redraws on every edit; at 9,216 pixels that is cheap, and it is held by a counter
  of evaluations, not a timing.
- **c.** **Live in the 3D view**: while the dialog is open, each edit to a material's appearance is pushed as a **preview**
  to every open 3D view whose technology contains that material (`Viewer3DViewModel.PreviewAppearance(material,
  appearance)`). The view re-resolves only the affected slots and writes the appearance table. **Cancel** removes the
  preview and the table returns byte-identical. **OK** commits through the existing save path.
- **d.** After OK, the technology reloads. **If the old and new technology are equal in their canonical physics form**
  (105 §5a), the 3D view takes a **display-only path**: re-resolve appearances, and re-colour vertices if `Color` changed
  (a buffer patch). **No elaboration and no tessellation.** Any physical change takes today's path. Results stay current
  either way for a display-only edit (105 §5).

## 2. `R-em3d108-2` — appearance in the Inspector

- **a.** An **Appearance** group under Transparency, for the selected objects and instances (Object mode, as Transparency
  works). Each field shows its **resolved** value. A field the object overrides is shown normally and the others greyed
  with provenance. Editing a field writes the object's override for that field only. **Clear override** removes the
  object's `Appearance`. **Like** offers the materials in scope ("Gold" on a copper trace is the plating case).
- **b.** Multi-select: a field whose resolved values differ shows the mixed state Transparency already uses, and an edit
  writes all of them.
- **c.** Live: a slider drag previews (a slot re-intern and a shade-stream patch for the affected objects, 104 §3a) and
  commits **one undo entry on release**, reusing the Transparency row's preview/commit/stray-guard pattern rather than
  writing a second one.
- **d.** Read-only hosts (an instance's contents, the results viewer) show the group read-only, with the same sentence
  pattern the Transparency row uses.
- **e.** Editing works whether or not the realistic view is on. When it is off, the group's header says `Shown in the
  realistic view`, and the toggle is one click away.

## 3. `R-em3d108-3` — the Look panel

- **a.** A small panel opened from a drop-down beside the Realistic toggle (and 3D ▸ View ▸ **Look…**). It has 106's `Look`
  keys (environment combo with **Load .hdr…**, rotation, intensity, exposure, background with its colour pickers, and one **Show** check box per row of 106 §2b's table: edges, grid, overlays,
  air box, ports, boundaries, images), 107's (shadows, occlusion, ground), and 109's (field style, field opacity) as those
  briefs land. The Show check boxes are generated from 106's table, so a new row appears without a panel edit.
- **b.** The `Look` is document state, so edits are **undoable, mark the document dirty, and are one undo entry per drag**.
- **c.** Cost: rotation, intensity and exposure drags are uniform writes. A rotation step also re-renders the shadow map
  (107 §1b), which is expected. An environment change prefilters once (cached per environment, 106 §4b), off the UI thread,
  and the view keeps the previous environment until the new one is ready.
- **d.** **Use This View for Pictures** (overview D17, settled by the owner 2026-10-04): writes the current camera into
  `Look.Camera` (`Direction` toward the viewer as x,y,z; `Target` in DBU, as every `.c3d` coordinate is; `Distance`;
  `FovY` in degrees; `Projection` `Perspective` | `Orthographic`). It is one undo entry, and **orbiting never writes it**. It
  is a deliberate, opt-in exception to the `.c3d`'s rule that the camera is not document state, so record it in the
  comment on `C3dDocument` where that rule is stated. **Clear** removes it. **Go to Picture View** restores it in the live
  view. Schema, validation and `SerializeForRun` stripping apply as for every `Look` key. 110 and 111 read it.
- **e.** Opening the panel turns the realistic view on (the panel's whole purpose is to change it), and says so in its
  header.

## 4. Gate

1. **Swatch.** Deterministic. A metal's swatch differs from a dielectric's of the same base colour. Raising roughness lowers
   the brightest pixel monotonically across 0, 0.25, 0.5 and 1.
2. **Dialog preview.** A roughness edit in the dialog: the open view's appearance table changes, while
   `Scene3DBuilder.Tessellations`, the elaboration count and geometry upload bytes do not. Cancel restores the table
   byte-identical.
3. **Display-only reload.** OK with only `Appearance` (or only `Color`) changed: no elaboration, no tessellation, and a
   `Color` change patches vertex colours. A run made before the edit is still current (105's gate, reached through the
   GUI path).
4. **Physical reload.** OK with εr changed takes today's path. Assert it does elaborate, so the display-only path is not
   swallowing real changes.
5. **Inspector.** Two selected objects, Roughness set to 0.1: one undo entry; both objects hold `"Appearance": {
   "Roughness": 0.1 }`; undo removes both; `SerializeForRun` is unchanged throughout. Clear override removes the key.
   `Like: Gold` on copper resolves Gold's base colour.
6. **Provenance shown.** An inherited field's tooltip text equals the resolver's provenance string.
7. **Look panel.** A rotation drag of 10 steps: 10 uniform writes, 0 geometry uploads, one undo entry. An environment change
   prefilters once and caches; switching back costs no second prefilter.

8. **Show check boxes.** One per row of 106 §2b's table, generated from it (a test adds a row to a stub table and sees a
   new check box). Ticking one sets its `Look` key in one undo entry.
9. **Picture camera.** *Use This View for Pictures* writes `Look.Camera` once; ten orbit steps afterwards leave it unchanged;
   *Go to Picture View* restores the view's camera to it within 1e-6; *Clear* removes the key; `SerializeForRun` holds none.

UI behaviour is tested through the view models, with no window. Run only the classes touched (`--filter`).

## Decisions — settled by the owner, 2026-10-04 (each as recommended)

- **D1 Where the Look panel lives.** *Recommended:* a flyout from the toggle's drop-down, because it is small and visible
  only when wanted. *Alternative:* a dockable panel beside the Inspector.
- **D2 Does opening the Look panel turn the realistic view on?** *Recommended:* yes (§3d).

## Docs

`drawing-in-3d.md`: "Changing how things look": the material route versus the object route (and when to use each), `Like`
for plating, the Look panel, and that none of it touches a result. Edit doc sources only; DocGen runs at the end of the
series.

## On completion

Record the display-only reload path in `src/Ui/Viewer3D/RESOLVED.md` and the swatch in `src/Render/RESOLVED.md`. Never
CLAUDE.md. Do not commit unless the owner asks.
