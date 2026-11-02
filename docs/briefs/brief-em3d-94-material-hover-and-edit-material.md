# Brief 94 — Materials from the 3D view: a hover that shows what matters, and Edit Material… from the tree

**Tag:** `R-em3d94-n` · **Series:** 3D editor round 8 (90–95).
**Area:** `src/Ui/Viewer3D/Viewer3DViewModel.cs` (`Describe`), `src/Render/Scene3D/Scene3DModel.cs`,
`src/Ui/ThreeD/C3dEditorViewModel.TreeMenu.cs`, `C3dEditorViewModel.FieldPlots.cs` (`TreeGroupMenuItems`),
`src/Ui/ViewModels/WorkspaceViewModel.Materials.cs`, `src/Ui/Layout/MaterialsTableViewModel.cs`,
`src/Ui/Views/Materials/MaterialsEditorView.axaml.cs`, `src/Ui/Views/Layout/TechEditorView.axaml.cs`,
`docs/user/src/reference/drawing-in-3d.md`
**Depends on:** 53 (the `.cmat` materials editor), 73 (thermal material properties) · **Blocks:** —

## 1. `R-em3d94-1` — the hover names the properties that apply to what the object is

### What is wrong

`Viewer3DViewModel.Describe` prints the same line for every material:
`{name}: εr …, tanδ …, σ … S/m at T °C`. A dielectric shows `σ 0 S/m`, and a conductor shows an εr and a tanδ it has no
use for.

### What to build

A pure function (in `src/Render/Scene3D` beside the scene model, so a headless label could use it too) from the object's
**role** and its `Em3dMaterial` values, plus the thermal values when a thermal setup is active, to the lines to show.
The role is the one the elaborator decided: `Scene3DObject.Kind` Conductor / Dielectric / Air / Wire / Via / Sheet, and a
sheet's role from what it lowered as. It is not re-derived from the material, because a Role override on the object
(`C3dObject.Role`) wins and the hover must agree with the solve.

| Role | Shows | Never shows |
|---|---|---|
| Conductor, wire, via, conducting sheet | σ (S/m, at the operating T); μr only if ≠ 1 | εr, tanδ |
| Dielectric | εr (or the tensor `εr (xx, yy, zz)`), tanδ; **σ only if > 0** (a lossy substrate); μr only if ≠ 1 | σ = 0 |
| Air | `Air` | everything else |
| Unstated / refused | the material's name and why (`states nothing`) | — |

- **With a thermal setup active**, add `k` (W/(m·K), at the setup's temperature, or the tensor) on every solid. Add ρ and
  c only if the setup is transient or pulsed, since that is when they matter.
- **Owner decision D1:** a dielectric's σ when > 0. The recommendation is to show it: a doped silicon substrate's σ is
  the property that matters most. The owner's report asked for "not σ for dielectrics"; the complaint was the `σ = 0`
  line, which this rule removes.
- Number formats stay as they are (`G4`/`G3`, invariant culture).
- Gate: a table-driven test of the function, one row per role, including a Role-overridden ambiguous material and a
  lossy dielectric, plus a thermal row.

## 2. `R-em3d94-2` — Edit Material… from the object tree

### What to build

- **Where it is offered:**
  - the **material header** of the By-material tree (a `C3dTreeGroup` whose header is a material's name; not
    `No material`). `TreeGroupMenuItems` today only offers Field Plots' New Field Plot…; it gains **Edit Material…**;
  - an **object row** with a material, in either grouping: **Edit Material 'FR4'…**, beside the existing Material
    item.
  - An instance part's row edits the material **of the placed cell's technology** (its provenance), which may be a
    different file. The item says which: `Edit Material 'Au' (in pa.ctech)…`.
- **What it opens** is where the material is actually defined, which the resolved technology knows
  (`Technology.LibraryMaterials[i].SourcePath`, or the `.ctech`'s own `Materials`):
  - **defined in a `.cmat` library:** `OpenOrActivateMaterials(cmatPath, select: name)`;
  - **defined in the `.ctech`:** `OpenOrActivateTech(techPath)`, then its editor's Materials tab
    (`TechEditorViewModel.MaterialsTabIndex`) and `MaterialsTable.Select(name)`;
  - **a shipped, read-only technology:** open it the way the app opens one read-only, with the row selected, so it can
    at least be read;
  - **no technology resolved:** the item is disabled with the reason (the 3D ▸ Materials command's own
    `ChooseC3dTechnologyAsync` path may be offered instead).
- **"Open or bring into focus":** an editor already open is activated. That includes one in another tab, a floated
  window, or another workspace window of the same process, so do not open a second session on the same path. The row is
  selected **even if the editor was already showing another row**.
- **It scrolls to the row.** Selecting is not enough, because a long table leaves the selected row off-screen. The
  Materials DataGrid (both the `.cmat` view and the tech editor's tab) scrolls the selected row into view
  (`DataGrid.ScrollIntoView`) whenever `SelectedRow` is set from code, including when the editor is opened and its grid
  has not yet been laid out. Defer the scroll until the grid is attached and its rows are realised. Check that a
  scroll-then-layout does not leave it at the top.
- The 3D editor raises a request (`EditMaterialRequested(string material, string? technologyPath)`); the workspace view
  model handles it, the same pattern as `SetupAnalysesRequested`. The editor view model stays free of workspace types.

### Gate

1. The hover function's table test (§1).
2. The tree: a By-material header's menu has Edit Material…; `No material`'s does not; an object row's has
   `Edit Material 'X'…`.
3. The workspace handler, with a technology whose material comes from a `.cmat`: the `.cmat` document is opened (or
   activated) and `Table.SelectedRow.Name == "X"`. With the material in the `.ctech`: the tech editor is on the
   Materials tab with the row selected. Called twice: one document session, not two.
4. The scroll: a view test (headless-capable only if the repo's Ui.Tests already host DataGrids; otherwise assert that
   the view's `SelectedRow` handler calls `ScrollIntoView`, and say pixels were not seen).

Run only the classes touched, with `--filter`.

## Docs

`drawing-in-3d.md`: the hover's per-role lines, and Edit Material…. Doc sources only.

## On completion

`src/Ui/RESOLVED.md`, never CLAUDE.md. Do not commit unless the owner asks.
