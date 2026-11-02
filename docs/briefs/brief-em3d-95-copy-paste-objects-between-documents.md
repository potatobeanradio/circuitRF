# Brief 95 — Copy and paste objects from the object tree, between `.c3d` documents and workspaces

**Tag:** `R-em3d95-n` · **Series:** 3D editor round 8 (90–95). Built last: the fragment must carry 92's `Transparency` and
93's `Model`.
**Area:** new `src/Design/ThreeD/C3dFragment.cs` (what a copy is, and what a paste means), new
`src/Ui/Clipboard/C3dClipboard.cs` (clipboard traffic only), `src/Ui/ThreeD/C3dEditorViewModel.TreeMenu.cs` (+ a new
`C3dEditorViewModel.Clipboard.cs`), `src/Ui/Views/ThreeD/C3dEditorView.axaml.cs` (`OnTreeContextRequested`, the tree's
keys), `src/Ui/ViewModels/WorkspaceViewModel.Materials.cs` (material creation), `src/Design/Layout/LayoutFragment.cs` (its rebase path logic lifted into a shared helper), `src/Design/ThreeD/C3dElaborator.cs` (foreign `CellRef`s), `src/Design/ThreeD/C3dVariableEdits.cs` (`C3dExpressionText.Rename`), a new small dialog under
`src/Ui/Views/Dialogs/`, `docs/user/src/reference/drawing-in-3d.md`
**Depends on:** 90 (the symmetry-plane extent rule in `src/Design`), 92, 93 · **Blocks:** —

## Precedent to follow, not re-invent

The layout editor's clipboard already does this job for shapes: `LayoutClipboard` (clipboard traffic only) and
`LayoutFragment` (every "what does the paste mean" decision: rescale, layer reconciliation, placement). Copy that split
exactly. `C3dClipboard` holds no decisions, and `C3dFragment` in `src/Design` holds all of them. That makes the paste
testable with no clipboard and no window, and reachable headlessly later.

## 1. `R-em3d95-1` — what a copy carries

`C3dFragment.Build(C3dDocument source, Technology? sourceTech, IReadOnlyList<rows>)` → a JSON document with a marker
(`LayoutFragment.Marker`'s pattern), carrying:

- **the objects, in full**, serialised through `C3dPersistence` (not a second writer): every property, including
  `Material`, `Role`, `Group`, `Placement`, `Hidden`, `Transparency` (92), `Model` (93), expressions (`Exprs`), and a
  boolean's operands and a filleted solid's features whole. Unknown keys (`Unread`) travel too.
- **a group row** copies all its members with their group paths. A **multi-selection** copies each row once: a member
  selected along with its group is not copied twice.
- **every material any copied object names, in full** (`TechMaterial`: εr/tensor, tanδ, μr, σ₂₀, α₂₀, σ(T), k/k(T)/tensor,
  ρ, c, colour, and the `Unread` bag), as the **source technology resolved it**. A material from a library is copied
  with its values, not with a pointer to the library.
- **the source's `DbuPerMicron`**, rescaled on paste as `LayoutFragment` rescales.
- **the variables the copied expressions name, and the variables THEY name, to closure**: each `C3dVariable` whole
  (`Name`, `Expression`, `Unit`, `Linked`, `Unread`), plus its evaluated value in the source for the report. "Expressions"
  means every one the fragment carries: object dimensions (`Exprs`), an instance's `Params`, and a place's bound fields.
  A name that resolves in the source to its **cell's parameter** rather than a VAR is carried as a VAR whose expression
  is the parameter's value in the source (the `.ccell` default, or the instance override the source document sees), and
  it is marked as such for the report (see §3).
- **placed instances:** the source's `CellRef`, plus the two base-independent forms `LayoutFragment` carries for a
  layout instance, the absolute cell directory and the source-workspace-relative directory (see §3).
- **thermal places whose referents are all copied**: a heat source `in` a copied solid, a probe on a copied
  solid/face/wire, a mesh region or effective block (these reference no object). **Owner decision D1:** the
  recommendation is that places are copied only when selected, not pulled in automatically with their object.
- **ports (owner, 2026-09-29):** each `C3dPort` whole: number, name, kind, plane/offset/rect, `Z0` (an expression, so the
  variable rules apply), `Positive`/`Negative`, `Flip`, `VoltagePath`, `Model` (brief 93) and `Unread`. Pasting is §3a.
- **boundaries (owner, 2026-09-29), both kinds:**
  - **EM face boundaries** (`C3dFaceBoundary`: `Object`, `Face`, `Kind`, `Material` for a Conductive face): whole. A
    Conductive face's `Material` goes through §4's material rules, like an object's.
  - **thermal boundaries**: the record from the source's setup (`Face`, kind, T / h / ambient), including a boundary on
    the whole exposed surface (`C3dThermal.ExposedFaces`), plus the **name of the setup it came from**, for the report.
  - **symmetry planes** (owner, 2026-09-29): each `C3dSymmetryPlane` whole (`Axis`, `At`, its expression if bound,
    `Unread`).
  - Pasting any of them is §3a.
- **Not copyable yet**, offered disabled with the reason: **field plots** (owner, 2026-09-29: later; they are a reading
  of a run and name a setup and a solution) and the **air box** (the setup's, not the geometry's).

## 2. `R-em3d95-2` — the gestures

- **Copy:** in the object tree's context menu, on any copyable row or multi-selection. It also works on a canvas
  selection mapped to its rows, via the canvas menu's **Copy Objects**. The canvas's existing **Copy** is the *picture*
  copy (`Viewer3DPictureCopy.Header = "Copy"`) and keeps that name and meaning.
- **Paste:** in the tree's context menu, **on any row and on the tree's empty area**. The empty area needs
  `OnTreeContextRequested` to answer with no row. It is enabled when the clipboard holds a `C3dFragment`, disabled with
  "Nothing copied from a 3D view" otherwise.
- **Keys:** Ctrl/Cmd+C and Ctrl/Cmd+V **with focus in the object tree**. Not in the pane, where Ctrl/Cmd+C already means
  the picture. **Owner decision D3:** whether the pane's Ctrl/Cmd+C should change to objects. The recommendation is no.
- The **system** clipboard carries the JSON text, so it works between two windows, two workspaces, and two running
  instances. Nothing rides alongside as an image (unlike layout: a picture of a 3D copy is the Copy that already exists).
- **The same document** is a valid target too. The result is a Duplicate that lands on the same coordinates, with the
  names made unique as in §3. A paste into the same document is not special-cased.

## 3. `R-em3d95-3` — what a paste means (`C3dFragment.Apply`, in `src/Design`)

`Apply(C3dDocument target, Technology? targetTech, fragment, choices)` → the new objects, the renames, the material
decisions still needed, and the refusals. Pure: the editor wraps it in **one undo entry** through `ChangeObjects` (plus
`ChangeRecords` for places, ports, face boundaries and the active setup's thermal boundaries, all in the same undo
group). One Ctrl/Cmd+Z removes the whole paste.

- **Placement:** the same world coordinates, rescaled to the target's DBU. Not offset: the owner will move them, and a
  paste that lands where it came from is predictable. (`SchematicPasteGeometry` offsets because a schematic paste on top
  of its original is invisible. **Owner decision D4** if the 3D paste into the **same** document should offset too.)
- **Names:** a pasted object keeps its name when the target has none by that name. Otherwise it takes the first free
  `<name>_2`, `<name>_3`, … under the document's name rule (`NameValidator` / `C3dValidation`, and `airbox` stays
  reserved). **Every reference inside the fragment is rewritten to the new names:** a wire's pads, a boolean's operands,
  a fillet's target, a heat source's `in`, a probe's solid/face/wire, a port's `Positive`/`Negative`, a boundary's `object/face` (§3a), and
  **group paths**. A pasted group `pa` colliding with a target group `pa` becomes `pa_2`, **not** merged into the
  existing group, because merging would silently change what that group's Inspector edits. The renames are reported:
  `Pasted 5 objects; renamed box1 → box1_2, pa → pa_2`.
- **Variables (owner, 2026-09-29): the expressions come through, and so do their variables.** Nothing is frozen to a
  number. For each carried variable:
  - **the target has no such name** (no VAR, and no cell parameter of its cell): the VAR is **created** in the target
    with its source expression and unit. The report lists the variables created.
  - **the target already has the name**, as a VAR or as its cell's parameter: this is a **conflict**, and the user is
    asked (§4's Paste dialog, one row per conflict) to choose one of two answers:
    - **Reuse the one already here**: the pasted expressions keep the name and evaluate against the target's
      definition. The row shows both definitions and both evaluated values side by side (`h_lid = 200 um here · = 250 um
      in the copy`), so the user can see what reusing changes.
    - **Rename the pasted variable**: an editable name, prefilled with the first free `h_lid_2`, `h_lid_3`, … and
      validated live (a legal identifier, unused in the target, and not colliding with another row's new name). The
      pasted VAR is created under the new name, and **every pasted expression that named it is rewritten**: object
      dimensions, instance `Params`, place fields, and the other pasted VARs' own expressions.
  - **Rewriting is by token, never by string substitution** (the repo's expression invariant). Use
    `C3dExpressionText.Rename`, which already renames by token and re-parses to prove it. Do not write a second renamer.
  - **Owner decision D5a:** a conflict whose two definitions are **identical** (same expression text after parsing, same
    unit) is not really a conflict. The recommendation is to reuse it **silently** and list it in the report as
    "reused, identical", so the dialog only asks about names that actually differ. The alternative is to ask about every
    shared name.
  - **Cycle detection is mandatory** (repo invariant). After the choices are applied, the target's variables go through
    the existing cycle check. A cycle, which a Reuse choice can create (the target's `a` names `b`, the pasted `b` names
    `a`), refuses the paste with the cycle named, and the dialog stays open so the user can rename instead.
  - A carried **cell-parameter** name (§1) follows the same rules. The report says it came from a parameter in the
    source, since it becomes a plain VAR here.
- **Placed cells (owner, 2026-09-29): they paste across workspaces, as a layout instance does today.** A 3D instance
  gets exactly the layout rule, so from the user's side the two views behave alike. The reference is **rebased**
  with `LayoutFragment.RebaseInstances`' resolution order:
  1. relative to the target document, from the source's absolute cell directory;
  2. else the source-workspace-relative directory, when the target resolves a workspace root where it exists;
  3. else the absolute directory as the `CellRef`.

  **Do not copy that logic.** Lift the path half of `RebaseInstances` into a shared helper in `src/Design` that both
  fragments call. The layout paste's behaviour must stay byte-identical: its existing tests are the check.
  - A reference that ends up **outside the target's workspace** is a foreign reference, which the layout side already
    allows and reports. Check that the **3D elaborator resolves such a `CellRef`** (an absolute path, or a relative one
    leaving the workspace). If it refuses one today, making it resolve is part of this brief, reported with the rule it
    follows. It must not become a silent fallback.
  - The placed cell keeps **its own technology** (placed cells resolve their own), so its contents never go through §4's
    material dialog. Its `Params` expressions go through the variable rules above.
  - A reference whose cell directory does not exist at paste time (for example, deleted since the copy) still pastes,
    as a layout instance does, and shows the existing broken-reference state. The report names it.
- **Selection after paste:** the pasted rows are selected in the tree **and** the scene (`SetTreeRows`, then the view's
  selection), so the user can group or move them at once. The tree expands to show them.

## 3a. `R-em3d95-3a` — ports and boundaries: what their references bind to

Ports and boundaries **name objects**: a port its `Positive`/`Negative` conductors, a boundary its `object/face`. One rule
covers all of them, in one function in `C3dFragment`. Heat sources and probes (§1) use the same function, so there is one
rule and not three.

1. **The referenced object was copied too:** the reference follows it through any rename (§3). `lid/zmax` becomes
   `lid_2/zmax`.
2. **It was not copied, and the target has an object of that name with that face** (for a port, that conductor): the
   reference binds to the **target's** object. **The report names every such binding** (`port P1's + conductor 'trace'
   is this document's 'trace', not a copied one`), because an object that happens to share a name is the one binding the
   user did not choose.
3. **Neither:**
   - a **port's** `Positive`/`Negative` are **cleared to null**, which means **inferred** from what the rectangle
     touches (the documented default, and not a broken state). The report says the port's conductors will be inferred
     here. The pair is always cleared together, because stating one without the other is refused.
   - a **boundary** on a face that does not exist in the target is **not pasted**. The report names it, and the rest of
     the paste goes ahead. A boundary on nothing is not a state the document has.

**Ports:**
- **Number:** a pasted port keeps its number when that number is free in the target. Otherwise the pasted ports take the
  **next free numbers, in their source order**. The report gives the mapping (`P1→P3, P2→P4`), since a port's number is
  its column in the result. Numbers stay unique; the renumbering is the one that happens at paste, not brief 93's
  run-time mapping.
- **Name:** unique, with the `_2` rule, like any object (§3).
- **A wave port's** rectangle is a region of an air-box face of the setup being run. It pastes where it was. If the
  target's air box puts no face there, the existing wave-port refusal says so at the next elaboration; nothing is
  invented here.
- A pasted port with `Model: false` stays off.

**Boundaries:**
- An **EM face boundary** goes into the target's document-level `FaceBoundaries`.
- A **thermal boundary** goes into the target's **active setup, when that setup is thermal**. With no thermal setup
  active, the thermal boundaries are **not pasted**, and the report says why and names the setups that are thermal
  (`make 'Thermal1' active to paste them`). The rest of the paste goes ahead. Pasting never creates a setup.
- **A face that already carries a boundary of the same family in the target** (EM on EM; thermal on thermal in the
  active setup) is a **conflict**, answered in the Paste dialog (§4) in a third section, *Boundaries*, one row each:
  **Keep this document's** (the default) or **Replace with the pasted one**. Both definitions are shown side by side.
  It is never overwritten silently.

**Symmetry planes:**
- `At` is rescaled to the target's DBU like any coordinate. A bound `At` expression goes through the variable rules (§3).
- **One plane per axis** (the document's own rule, `ToggleSymmetryPlane`). A pasted plane on an axis the target leaves
  free is added. One on an axis the target already declares **at the same position** is the same plane: nothing
  changes and no question is asked. At a **different** position, it is a conflict row in the dialog's *Boundaries*
  section: **Keep this document's** (the default) or **Replace with the pasted one**, with both positions shown.
- **It is not validated against the model's extent at paste time.** A plane must lie on the model's extent (brief 90
  moves that rule, `SymmetryOfFace`'s, into `src/Design`), but the extent is the target's and changes as the pasted
  objects are moved, which is the first thing the user is expected to do (D4). So the plane pastes. If it does not lie
  on the extent as pasted, the report says so (`symmetry plane X = 120 µm is not on this model's extent: a run refuses
  it until it is`), and the document's own rule (the Inspector, `check`, the run) decides from then on, as for a plane
  whose model was edited. This is the one reference here that is kept although it does not currently fit, because it
  names a position rather than an object.
- The tree's group header (`1/N of the device is modelled`) follows, since it is rebuilt from the records.

## 4. `R-em3d95-4` — materials: match, create, or leave unassigned

For each material the fragment carries, look it up in the **target's resolved technology**, using the same lookup the
elaborator uses (`Technology.FindMaterial`), so "the same name" means what a run would take:

- **Found:** used. If its values differ from the fragment's, the paste still uses the target's, and the report says so
  in one line per material (`'FR4' in this workspace differs: εr 4.3 here, 4.5 in the copy — this workspace's is used`).
  The target's is never overwritten.
- **Not found:** **one Paste dialog for the whole paste**, not one per material. It is the **same dialog as the
  variable conflicts' (§3)**: a *Variables* section (only when there is a conflict), a *Boundaries* section (only when a pasted
  boundary lands on a face that already has one, or a pasted symmetry plane on an axis already declared elsewhere, §3a), and a *Materials* section (only when something is missing). A
  paste with none of them shows no dialog at all. The Materials section lists each missing material
  with its key values and a tick (all ticked by default), and names where a created material would go: the target
  technology's own materials, `…/tech/board.ctech`, or its writable library. Buttons: **Paste** (create the ticked
  materials, apply the variable and boundary choices), **Paste Without Creating** (the variable and boundary choices
  still apply; no material is created) and **Cancel**. For the owner's Create / No: Paste with materials ticked is Create, and Paste Without
  Creating (or unticking) is No.
  - **Paste, material ticked (Create):** each ticked material is added **with all its properties** through the function the 3D Materials
    dialog already commits with (`CommitMaterialList`: the tech editor's `ReplaceOwnMaterials`, or the library
    document's `CommitEdit`). That opens the technology or library document and marks it unsaved, with the existing
    "now unsaved — save it to keep them" message. The pasted objects name it. A name collision cannot happen, because it
    was not found.
  - **Paste Without Creating, or unticked (No):** the objects are **still pasted**, with `Material` left **unassigned** (null). They
    list under `No material` and elaboration refuses them until one is given. That is the existing state for a box drawn
    with no material, and the report says so.
  - **Cancel** (Esc or the window's close): nothing is pasted, no VAR is created and no material is created.
- **The target technology cannot take a new material** (shipped read-only, or none resolved): the dialog says why and
  offers only Paste Without Creating. When none is resolved it also offers the Choose a Technology path the Materials command uses
  (`ChooseC3dTechnologyAsync`).
- **Undo:** Ctrl/Cmd+Z in the `.c3d` removes the pasted objects, ports, boundaries and symmetry planes, and restores a
  boundary or plane that a Replace choice overwrote. It does **not** remove a created material, which is an
  edit of another document and has its own undo there. The report says this once, so it is not a surprise.

## 5. Gate

`C3dFragment` tests (no clipboard, no window) carry most of it:

1. Round trip: build from a document holding a filleted boolean in a group, with `Transparency`, `Model: false` and an
   expression. Apply to an empty document: the objects serialise identical to the source's, apart from nothing.
2. Collisions: apply twice into the same document. The second set is renamed `_2`, and every internal reference (wire
   pads, operands, group path, a heat source's `in`) points at the renamed objects, not the originals.
3. DBU: a fragment from a 1000-DBU/µm source into a 100-DBU/µm target lands at the same metres.
4. Variables, driven through `Apply`'s choices with no dialog:
   - a VAR the target lacks is created with its source expression, and so is a VAR that VAR names (closure);
   - a conflict answered **Reuse**: the pasted expression still names it and evaluates to the target's value;
   - a conflict answered **Rename** `h_lid_2`: the VAR is created under the new name, and every pasted expression that
     named it (an object dimension, an instance `Param`, another pasted VAR) names the new one, proved by
     `C3dExpressionText.Names`, not by a substring check;
   - an identical definition is reused with no conflict raised (if D5a stands);
   - a Reuse that makes a cycle is refused with the cycle named, and the target is unchanged;
   - a source cell-parameter name arrives as a VAR and is reported as such.
5. Materials: one found with different values (target's used, reported), one missing plus Create (a `TechMaterial` equal
   to the fragment's is committed, and the object names it), one missing plus Paste Without Creating (`Material == null`,
   reported). Cancel leaves the target byte-identical: no objects, VARs or materials added.
6. Placed cells across workspaces: an instance copied from workspace A pasted into a document in workspace B gets the
   `CellRef` the shared rebase helper computes (each of the three resolution cases covered). **It elaborates in B**:
   the 3D elaborator resolves the foreign reference to the same cell, and its parts appear in the scene. The layout
   fragment's existing rebase tests pass unchanged, which proves the helper's extraction changed nothing.
7. Ports: a port copied with both its conductors lands with `Positive`/`Negative` following their renames. Copied alone
   into a target that has a same-named conductor, it binds there and the binding is reported. Copied alone into one that
   has neither, both are null (inferred) and that is reported. Numbers 1 and 2 pasted into a document holding 1–2 become
   3 and 4 in source order, with the mapping reported. `Model: false` and a `Z0` expression survive, and the `Z0`
   expression goes through the variable rules.
8. Boundaries: an EM Conductive face boundary pasted with its object follows the rename, and its `Material` goes through
   the material rules. Pasted alone onto a missing face, it is skipped and reported. A thermal boundary lands in the
   target's active thermal setup; with none active, it is skipped with the setup names reported. A face already bounded
   produces a conflict row, where Keep leaves the target's and Replace writes the pasted one. The exposed-surface
   boundary round-trips.
9. Symmetry planes: a plane on a free axis is added (rescaled across DBU). The same axis at the same position changes
   nothing and raises no conflict. The same axis at a different position raises a conflict row, where Keep leaves the
   target's and Replace writes the pasted one. A plane off the target's extent still pastes, the report says so, and
   `check` then reports it through the rule brief 90 moved to `src/Design`.

Then view-model tests: Paste is one undo entry; after it the pasted rows are the tree's selection and the scene's; Paste
on the empty tree area is offered; Copy on a field plot is disabled with its reason, and Copy on a port or a boundary
is enabled. Run only these classes, with `--filter`.
Pixels were not seen; say so.

## 6. Decisions for the owner

**Settled (owner, 2026-09-29):** variables come through with their expressions, and each name clash is a prompt to
Reuse or Rename (§3). Placed cells paste across workspaces on the layout paste's rebase rule (§3). **Ports, both kinds
of boundary, and symmetry planes are copyable** (§1, §3a). **Field plots wait** for a later brief.

**Still open, built as recommended unless the owner says otherwise:** D1 places are copied only when selected · **D2** a pasted symmetry
plane off the target's extent pastes and is reported, not skipped (the extent moves as the pasted objects are moved) ·
**D2c** a boundary or symmetry-plane conflict defaults to keeping the target's · **D2d** thermal boundaries with no active thermal setup are
skipped and reported, not given a new setup · D3 the pane's Ctrl/Cmd+C stays the picture copy ·
D4 no offset on paste, even into the same document (the pasted objects arrive selected, ready to move) · **D5a** a clash
between two IDENTICAL definitions is reused silently and listed in the report, not prompted.

## Docs

`drawing-in-3d.md`: Copy / Paste in the tree, renames, variables (created / reused / renamed), materials (match / create /
unassigned), placed cells from another workspace, what does not copy.
Doc sources only.

## On completion

`src/Design/RESOLVED.md` (the fragment) and `src/Ui/RESOLVED.md`, never CLAUDE.md. Do not commit unless the owner asks.
