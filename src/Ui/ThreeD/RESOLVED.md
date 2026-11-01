# `src/Ui/ThreeD` — resolved findings

## Review of brief-em3d-101 (2026-10-03)

- **Save As rewrites the image paths the UNDO HISTORY holds**, not only the document's (`C3dEditorViewModel.RebaseHistoryImages`,
  `IC3dObjectTextEntry`). An entry stores each object as the file spells it, and an image's path is spelled for the `.c3d`'s
  folder: undoing past a Save As into another folder put back a path that resolved against the NEW folder, i.e. nowhere. Each
  entry kind that holds objects (`C3dEdit`, `C3dListsEdit`, `C3dDocumentEdit`, `C3dGroupEdit`, `C3dFilesEdit`) rewrites its own
  text in place; the stack's saved marker is a reference, so it still names the same entry. Text with no image is left byte for
  byte. Other relative references in an object (a STEP part's `File`, an instance's `CellRef`) are NOT rebased by Save As at all —
  that is older than brief 101.
- **Locked was enforced in Object mode only.** A sheet's face is the whole sheet, so Face mode's Move Along Normal, Move and Align
  to Face moved a locked image sheet, and so did Set Coordinates on a vertex. All four refuse with `LockedRefusal` now.
- **The first image placed was `image2`**: `NextFreeName("image1")` increments the trailing number. Placement now takes the
  smallest free number, as `NextName` does.
- **Keep aspect follows the image's pixels** (R-em3d101-3b), in the Inspector and the corner resize; it followed the rectangle's
  present aspect, so a sheet once stretched with Keep aspect off stayed stretched.
- **A numeric Width/Height on an image sheet unbinds the expression that size held**, as the sheet's own Size rows do; an
  expression typed there binds through `SetFieldText` (with Keep aspect off — the other side cannot follow a formula).
- **Delete on a hidden face image's tree row** removes it: a hidden one has no scene object, so the scene selection never had it.
- **Ticking Hidden in a face image's Inspector section deselected it.** Two causes, and the first fix found only the second:
  - **The real one:** when the edited scene is adopted, `RebuildRecordsTree` removes every face-image (and EM boundary) row
    and makes it again. The tree VIEW drops a removed row from its selection and raises an empty one, which cleared the
    selected row, and the Inspector went with it. It now finds the rows selected before by name afterwards, as `RebuildTree`
    does. Any Inspector edit of a selected face image hit it; Hidden is where it showed. A test that reads only the view
    model's `SelectedTreeItem` passes without the fix, because the stale row object is still held there and there is no tree
    view to drop it. The gate asserts the selected row IS the tree's current row object.
  - `TreeRowStillSelected` keeps a row with nothing of its own in the scene selected when the scene selects nothing:
    symmetry planes and thermal boundaries already, now a hidden face image.
- **A face image's Width/Height box left empty is that size unstated**: it follows the other at the image's aspect, and both empty
  is the default, fitted to the face (the offset stays; Fit to Face clears it). An empty box was an error before.
- **The face image's Transparency box stretched to the slider's row height** — the same trap the object's Transparency row
  already notes: a TextBox in a Grid row with a Slider needs `VerticalAlignment="Center"`.

## brief-em3d-101 follow-ups (2026-10-03)

- **A face image now follows its object's drag** (`C3dEditorViewModel.FaceImagesFollow`). A face image is a scene record of its
  own (`image:object/face`), so the preview's moving set — built from the dragged objects' own scene objects — left it where the
  object was until the drop. It is matched by scene name, the object half split at the last `/` (`C3dModelled.ObjectOfFace`), so
  a placed cell's own face images follow the instance too; the image draw follows because `AddImage` keys its preview on the
  record's id. **Face-boundary tints still do not follow**: a tint is named after its boundary, not its object, and one boundary
  can span faces of several objects, so there is no name to match without carrying the owners on the tint.
- **An image placed while the pane has no usable size** (under `MinPlacementPixels` on a side: not laid out yet, or squeezed to a
  sliver) is sized and centred as the default 800 × 500 view would place it. A sliver's aspect collapsed the visible width and
  the sheet came out at the snap minimum. `Viewer3DViewModel.Resized` also no longer takes an aspect of 0 from a pane with no
  width, which a later Fit would have framed against.
- **"Drop to place…" is taken back when a file drag leaves without dropping** (`EndFileDrag`), restoring what the status line
  said before the drag, unless something else has written to it since.

## 3D editor keys only worked while the canvas had focus (2026-10-03)

**Symptom.** Pressing 1 (Isometric), or any toolbar key in the tooltips, did nothing in the 3D editor.

**Cause.** Every key is `Viewer3DViewModel.HandleKey`, reached only from `Viewer3DPane.OnKeyDown`, and the pane took focus only
on a pointer press inside it (and once, on the view's first attach). A toolbar click focuses the BUTTON, so the next key went
to it and was dropped; returning to an already-attached 3D tab focused nothing, because `C3dEditorView` never subscribed to
`ActivationFocusRequested` as the layout editor does. The same thing made image sheets seem hard to move: G after the
toolbar's Insert Image… went to that button.

**Fix.** Three parts, in `C3dEditorView`: a toolbar `Button.ClickEvent` hands focus back to the pane (the layout editor's
`OnToolButtonClick`; a button with a flyout keeps it); `ActivationFocusRequested` focuses the pane; and a bubbling KeyDown nobody
in the view handled is forwarded through `Viewer3DPane.ForwardKey` — never from a TextBox/ComboBox/NumericUpDown/AutoCompleteBox,
and never Delete/Backspace, so a key pressed on another control cannot delete geometry. No window menu was intercepting:
the macOS `NativeMenu` 3D items carry no `Gesture`, and the in-window `InputGesture`s are display-only.

## Make Port ▸ Wave on a face that is not a rectangle — brief-em3d-121 (2026-10-06)

**The coax face was never greyed out for the reason brief 121 gives.** Brief 121 expected *Make Port ▸ Wave* on the
Launch's bore face to be refused by the port's overlap rule. The editor refused it earlier: `PortFromFace` requires a face
whose area equals its bounding rectangle's, and the bore's end is a disc ("Face bottom is not a rectangle"). A wave port's
rectangle is only a region of the box's face, so a wave port now takes any flat, axis-normal face as the rectangle round it
(`WaveRegion`): a cylinder's cap exactly, from the elaborated primitive's bounds (its tessellated polygon falls short of the
circle on one axis or the other), and any other face from its tessellation's corners. A lumped port still needs a
rectangular face.

**That square is not the shipped Launch's port**, measured once through brief 117's gate 4: on the 1.34 mm square, openEMS's
default grid left the current probe round the pin 106 µm from the housing where it needs a cell (153 µm), and the run was
refused; Palace ran but its Draft |S| moved by up to 1.9 dB (S22 at 14 GHz), from the different Gmsh geometry. The 2 × 2 mm
rectangle's grid lines at ±1 mm are what give the probe its room. The owner chose to ship P1 as the Port tool draws it,
2 × 2 mm round the axis, stating no ends and no path; it lowers to the problem the runs were recorded with.

**The status line names why Make Port chose the reference.** `AddPort` resolved the port it had just written, whose
`Reference` is then stated, so the reason read "the reference is stated". `MakePortFromFace` passes `TerminalsFor`'s reason
through (`AddPort(port, referenceWhy)`).

## Import STEP: a header row per product of several solids, and the group (2026-10-09, brief-em3d-128)

The dialog draws `Table` — each header row (`StepImportProductRow`) followed by its solids, which are ordinary
`StepImportRow`s indented — while `Rows` keeps every importable row for the logic. The header's check box is three-state
(every closed solid, none, some) and its combo is blank when its solids disagree; both act through `StepImport`'s
`SetProductImport`/`SetProductMaterial`, so the CLI and the gate reach the same rule. The group field shows only when the
file has more than one row, and a reload's offer of new parts imports them loose: the file's group is already in the view.

The commit needed no new selection code: selecting every object the import made is, by `C3dGroups.Units`, the group taken
whole. `StepImportGroupUxTests` proves that, a view pick on a piece taking the package, the group's Corner moving every
piece by one step, and a member's tree-row Material changing that member alone, each one undo entry.

## Split into Solids: the command, and 3D ▸ STEP (2026-10-09, brief-em3d-129)

Enablement reads the elaboration's `C3dKernelBuild.Solids` for the object, so opening a menu never asks the kernel to read
a file; an object not built yet is disabled with that reason. The plan runs off the UI thread on a copy of the document
(Reload's shape); the commit is one `C3dDocumentEdit` of the whole text, so one undo restores the document byte for byte.
A part with a solid that is not closed asks through the editor's `Confirm` hook, and with no hook (headless) the split is
not made: geometry is never dropped unasked. Refusals and notes reach Messages through `StepReported`.

3D menu cleanup's gate forbade any `STEP` header in the 3D menus, to keep Import/Export STEP… on File only. The brief puts
Reload from Source and Split into Solids in a 3D ▸ STEP submenu, so `MenuCleanupTests` now forbids `STEP…` (the two
dialog commands) and requires Split into Solids in both spellings.

## Wire points: add/remove in the Inspector, and the camera turning during a wire-point move (2026-10-09)

**Add/remove.** Each wire point row has +▲ (every row but Start), +▼ (every row but End) and − (interior rows only).
An added point is the middle of the uniform Catmull–Rom cubic through the points on either side,
`(−p₀ + 9p₁ + 9p₂ − p₃)/16`. A missing neighbour past an end is that end's segment carried straight on, so a two-point
wire gains its plain midpoint. Each add or remove is one undo entry (`InsertWirePoint`/`RemoveWirePoint`). Wire points
take no expressions today. `RenumberPointExpressions` is the fallback should they ever take them: `Points[k]…` entries
follow their point, and any other `Points` key shape refuses the edit. Brief 131 is the feature.

**The camera turned while a wire point moved.** There were two causes; the first is the one that was reported.
- *A key that arms a tool during a still press.* The press was classified as an orbit when it went down, before G
  armed Move. Holding the button and dragging after G therefore orbited the camera, and the point followed the cursor
  ray as the camera turned under it. `Viewer3DPane.OnPointerMoved` now turns a press that has not moved into the armed
  tool's click (`_drawPress`), so the drag moves the point and the release places it. A press that has already moved
  stays an orbit, because mode keys wait for a gesture under way (R-em3d43-2a).
- *The wire snapped to itself.* A wire's point is on its axis, inside the wire, so the cursor moving it is always over
  the wire. Geometry snap could take the wire's own surface, which moved the wire, which the next frame snapped to
  again. `StartVertexMove` now excludes the wire being edited (body, balls, array elements) from the snap, as R-snpf-4
  intends for anything a gesture moves.

A headless probe showed the camera itself never moves in the view model during a G-move: `Camera.Target`, `Distance`
and `Yaw` were constant, and the origin carry was not involved. Look in the pane's press classification first when
this symptom recurs.

## Expressions in a wire's points: the editor (2026-10-09, brief-em3d-133)

**The Inspector.** Each wire point row's x, y and z are `C3dDimensionField`s, built by the same `Field` helper as every
other dimension (`WirePointFields`). Notes (`z = 200 µm`) and errors sit under the row. A commit passes only the
components whose text changed (`SetWirePoint(index, k, texts)`). A number replaces an expression; anything else is bound
at its site unit. Text that does not parse is refused with the shared sentence. A name that does not resolve is stored
and shown red, as for every dimension. A name is an expression now, so `abc` is no longer a refusal: round 3's test types
`1 +` instead.

**One rule change makes every wire edit work: `PlanNames` no longer compares a wire's points path by path.** Brief 51's
rule puts back an expression a tool dropped and solves a bound component whose number changed. For a wire's points
both are wrong. An insert renumbers `Points[k]`, so a path-by-path comparison put the old point's expression on the new
point. A translation already wrote the offset (`BakePlacement`, 132), and solving that component as well rewrote the
variable. `WirePointPlan` replaces it. The edit's own point expressions are authoritative and nothing is put back. A
bound component whose number no longer equals its expression's value is one the edit moved without rewriting it, as a
seat does. That component takes the strict drag rule of overview R-em3d131-3: a bare name is solved and written, and
any other expression refuses the edit, naming the field. Brief 51's general rule solves anything affine in one name;
points do not. `C3dVariableEdits.Evaluate` is the comparison, and 0.5 DBU is the tolerance.

**Who writes the offset.** Move, Align and Align to Face (and a duplicate's copies) already do, through `BakePlacement`.
A vertex Move (G) and a Span move numbers directly, so both call `C3dWires.OffsetBoundPoints(was, moved)`. The vertex Move
calls it in the preview and again at the release, so the scene previews the text the commit writes. Without it, the
preview's resolver evaluates the old expression and the point does not move.
- **An end's z is not offset.** The seat decides it (R-em3d133-2). A vertex Move of an end that drifts a little in z
  would otherwise write `t_die + 3um`, which the seat moves back to the pad top. That z would then no longer equal its
  expression, and a compound refuses. So `OffsetBoundPoints` keeps a bound end z's number and text. The seat that follows
  writes nothing when the expression still lands.

**The seat (D1).** `C3dWires.Reseat` leaves a bound end z that already lands on a top (`PadAt`), even when a higher top
is under it. Re-seating it onto the higher top would rewrite the name. An unbound end keeps the old behaviour (the highest
top). A bound z that misses is seated, and the drag rule at Push decides: a bare name is rewritten, a compound refuses.
Re-Seat Wire Ends now returns when Push refuses, so the refusal stays on the status line. Before this, the summary
overwrote it.
- **A typed expression on an end's z is judged, never corrected.** When it misses, the edit is refused with
  `C3dWires.NoPad`'s sentence plus `Its z is t_die + 5um.` Running the drag rule here would rewrite the name the user had
  just typed. Elaboration's refusal for an end that misses carries the same suffix (`C3dWires.BoundEnd`).

**Loop height (R-em3d133-4)** is refused before anything is computed when any point component is bound, other than an
end's z. The refusal names the first one (`'w1' point 2 z holds h_loop: …`). A bound end x or y is refused as well,
because the decision allows only the two ends' z.

Gate: `WirePointExpressionsTests` (11). A mutation that restores the path-by-path rule for wire points fails 6 of them.

## Wire points: dragged at their own depth, one gesture, and Esc leaves Vertex mode (2026-10-09)

**The point fell to the grid.** A vertex Move with nothing locked and nothing snapped took `FreePoint`: the drawing plane,
usually the grid at z = 0. A wire's point is in mid-air, so the first hover put the apex on the floor, often millimetres
along the ray. The preview wire was refused (a 179° turn, or an end off its pad) and drawn red. A click in that state
committed the broken wire, and Esc then had nothing to cancel: the wire stayed red until Undo. The scene's extent also
jumped with the stray point, which reads as the camera misbehaving. `MoveTool.HoldsDepth` (set for a wire's vertex move)
takes `IC3dDrawHost.DepthPoint` instead: a geometry snap as it is, else the point of the cursor's ray nearest the base.
The STEP from the base is rounded to the grid, not the point, so an off-grid apex does not jump on the first hover. A solid's
corners keep the drawing plane.

**One gesture.** In Vertex mode a plain press on a wire's point (`HoveredItem`) selects it and starts its Move
(`C3dEditorViewModel.PointDrag`). The pane routes the drag through the gizmo path (`PressGizmo` falls back to `PressPoint`),
so it never orbits. A release past the click slop commits; one that stayed put was a click, which selects the point and
moves nothing (`ReleaseGizmo(moved)` — the pane now tracks the slop for a gizmo drag too). A refused release puts the point
back and keeps the sentence, since a drag has no second click to retry with. G, then a click, still works.

**Esc.** After the tool, the measurement and Measure, Esc in Face, Vertex or Edge mode returns to Object mode (dropping that
mode's selection); in Object mode it clears the selection as before. A headless probe confirmed Esc always disarmed the
vertex Move. The red that stayed was a committed refused wire, not a tool still armed.

Gate: `WirePointDragTests` (3). Turning `HoldsDepth` off fails the drag test.

**The highlight stayed behind.** A vertex selection is carried by POSITION (`Scene3DItem.Point`), so after a vertex Move
the pink ring stayed on the spot the vertex left. `ReselectVertex` (beside `ReselectFace`, run at adoption) selects the
moved vertex where it now is. The target is recorded BEFORE `Push`, because the scene that Push asks for can be adopted
before Push returns (synchronously in the tests). It is cleared if the Push is refused. This applies to every vertex Move,
not only a wire's.

## The Wire tool highlights the top face an end lands on, not the object (2026-10-09)

Picking a wire's start or end lit the whole object under the cursor, which was the ordinary Object-mode hover tint. An end
is seated on the object's TOP face, whatever face the cursor is over, so the tint showed the wrong thing. While the Wire
tool is armed, `Viewer3DViewState.HoverHidden` zeroes the hover uniform (`Scene3DFramePlan.Fill`; the group tint goes too),
and `FillWireOverlay` puts the landing face into `Viewer3DDrawOverlay.TargetFaces`. That face is the pad's outline at its
`TopM`, holes included, chosen with `WireTool.Landing`, the same `OnPad` lookup the click uses. The overlay fills it in the
shader's hover cyan. The shader was left alone: Object mode has no per-face hover, and the three backend copies would all
have had to change for a face index the pad lookup does not even have. The overlay is 2D and has no depth test, so a
top face hidden behind another object still shows through. That is acceptable for a target marker and is the price of not
touching the shaders. Gate: `WireGateTests.PickingAnEnd_HighlightsOnlyTheTopFaceItLandsOn` (hovers the die's `ymin` face
and expects the die's top).

## Drawing a wire from Top view: the loop height is asked for, and a clash is said (2026-10-09)

**The third step was invisible.** Top view is the easy place to put a wire's ends in plan, but there the vertical through
the arch is seen end-on. The loop height then neither shows nor follows the cursor, and after the second click the tool sat
waiting with nothing on screen to say so. `WireTool.LooksDown` treats a line of sight within about 11° of vertical
(|d_z| ≥ 0.98) as "looking down". A nearly end-on vertical is as bad as an exact one, because a pixel becomes millimetres.
When the second end is placed looking down, the height field opens at once, labelled with what Enter takes (`loop height (µm)
— Enter: 150 µm`). The prompt says LOOP HEIGHT, and a click or an empty Enter places the wire at the default (the last
wire's, else 150 µm). The click used to be refused there ("looking straight down; type the loop height") with no field open.
The field opens empty rather than prefilled: a typed digit is appended to an open field, so a prefilled "150" would become
"1502".

**A wire through another part could not be seen from above.** `Scene3DPicking.SegmentCrossings` (src/Render) tests a
segment against the scene's own triangles, behind a per-object AABB prefilter. `WireClashes` runs it over the wire's axis.
Air, ports, boundaries and the air box are not obstacles. The two end pads are ignored on the FOOT segments only, which
start on them, and a crossing within 1e-3 of a segment's end is not counted. While the height is set the prompt names what
the arch passes through and the crossing segments are drawn red (`overlay.Crossing`); this is recomputed only when the arch
changes. Placing the wire anyway appends the same warning to the status line. The tool warns and never refuses. It tests
the AXIS, not the diameter, so a wire that only grazes is not reported.

Gates: `WireGateTests.FromTopView_TheLoopHeightFieldOpensAfterTheSecondEnd_AndEnterTakesTheDefault` and
`…AnArchThroughAnotherPart_IsNamedWhileSettingTheHeight_AndWhenPlaced`.

**The offered height (`SuggestLoopHeight`).** Measured on an owner's design, the default height produced a wire that ran
straight DOWNHILL through its own start pad. The loop height is measured from the LOWER foot. With pads 2.6 mm apart in
height, the remembered 150 µm sat far below the higher foot, so the arch was clamped flat. The clash check flagged it
correctly; the default simply did not avoid it. The offer for the two ends just placed now works like this:
- It starts at least 150 µm above the HIGHER foot: max(remembered, Δz + 150 µm).
- It rises in steps (a quarter of the first try, at least 75 µm) for up to 24 steps, until the arch is clear.
- A height counts as clear only when the axis passes through nothing AND the axis lowered by the wire's radius also clears
  everything except the end pads, between the feet. So the wire's underside does not graze a top. This errs on the side of
  no intersection.
- It is rounded UP to a tidy step of the display unit: 0.1 mil for mil or inch (2.54 µm), else 1 µm.

If nothing clears within reach, it is left at the first try and the clash warning names what is in the way. The prompt says
"(raised to clear what is under it)" when the search moved it. The clash WARNING still tests the axis alone, so grazing
does not raise false alarms there. Gates: `WireGateTests.FromTopView_PadsAtDifferentHeights_TheOfferedArchRisesAboveTheHigherFoot`
(mil, rounding) and `…TheOfferedHeightClearsAPartBetweenThePads_AndATypedLowerOneIsNamed` (µm).

## The 3D Wire tool takes Settings ▸ Wirebonds (2026-10-09)

The 3D Wire tool never read `WBondDefaults`. Its diameter came from the LAST WIRE IN THE DOCUMENT, then the workspace's
assembly rules, then a built-in 1 mil. A document whose wires state no diameter therefore pinned every new wire at 1 mil,
whatever Settings said. Its arch was also hard-coded to `C3dWires.SeedPoints` (7), which matched the shipped points-per-wire
only by coincidence. Now (`TakeWireDefaults`):
- The diameter and metal are Settings' values, read the first time the tool is armed and again whenever Settings has
  CHANGED since. The toolbar's own edits stand otherwise.
- A diameter the workspace's `.wasm` does not allow gives way to its first allowed one.
- The points per wire are read at every arm and passed through `C3dWireTemplate.Points` to `C3dWires.Arch` and
  `ForAssemblyHeight` (a new optional parameter, default `SeedPoints`).
- The section and the bond styles, which Settings does not state, still follow the document's last wire.
- A Settings metal the technology lacks falls back case-insensitively, then to Gold, then to the first metal.

**The tests read the REAL preferences file**, as the wBond tests always have (there is no global `AppDataRoot`
redirect). So assertions are made against `WBondDefaults.*`, never a literal. A clash test that drew two wires 10 µm
apart broke on a machine set to 2 mil: the second wire really did pass through the first. Gate:
`WireGateTests.ANewWire_TakesItsDiameterAndPointsFromSettings_NotFromTheDocumentsLastWire`.

## A wire's Loop height and Span take an expression, and hold it (2026-10-09)

Brief 131 §4 left Loop height and Span as one-shot edits of numbers, because both are worked out from the points.
`SetWireLoopHeight`/`SetWireSpan` parsed their text with `C3dDimension.Parse`, and anything that was not a value (an
expression included) fell through to "A loop height is a positive length." The owner chose a real binding over
evaluate-once: the wire follows the variable.

- **Two optional fields on `C3dWire`, `LoopHeight` and `Span` (DBU), bindable like any dimension** (`C3dBindings.Fields`).
  Each is written only when held, so every existing file re-saves byte for byte. `SetNumber` learned an unset `long?`, and
  `ToProperty` reads one.
- **`C3dWires.Hold` shapes the points to them, Span first**, through the same primitives the one-shot edits use: `WithSpan`
  (wBond's `ScaleSpan`, the end moving in plan) and `FitLoopHeight` (the assembly-height iteration lifted out of the editor).
  It runs in TWO places on purpose. The elaborator runs it on each wire before its array elements are copied, which covers
  `em`, `render`, thermal and child cells. The editor runs it in `ResolveDocument`, so the live points, the overlay and the
  Inspector agree with what is built. When the wire is already there it changes nothing, so a held wire re-saves unchanged.
- **The loop height is measured on the wire's own feet** (`AssemblyAtFeet`): a pad under each end with its top at the end's
  z. `Em3dWires.Resolve` reads a pad's polygon only for the foot-overhang warning, so this is exactly the elaborated
  measurement for a seated wire, and holding needs no elaboration. That is what lets the editor hold before the first scene.
- **Holding never re-seats.** A held span that moves the end off its pad gets elaboration's ordinary "no longer on a pad"
  refusal. Moving it silently would change the wire's inductance, the same rule as everywhere else for wires.
- **Conflicts are refused, never resolved** (`HeldConflict`). A held span moves every point but the start in plan, so it
  refuses an expression in any other point's x or y. A held loop height rewrites interior z. The Inspector refuses the
  binding, `SetWirePoint` refuses the expression, and elaboration refuses a file that has both.
- **An empty field lets go, as the owner asked, and keeps the wire as it is**: the points are untouched, so the shape keeps
  the number it had.
- **Brief 51's drag rule had to skip them.** `PlanNames` puts back an expression an edit dropped, and solves the name when
  the number changed. Letting go of a held span through a `C3dEdit` therefore came back bound, with `s_w` solved to 0. A
  wire's held fields are only ever set or released by the Inspector's own edit, which states them, so `PlanNames` takes
  them as the edit leaves them, as brief 133 already does for the points.
- A vertex edit of a held wire is re-shaped on the next resolve: the drag changes the profile, and the held value wins.
- **A name nothing defines is REFUSED in the Inspector, and the field keeps its value** (owner, 2026-10-09). A first
  follow-up bound such a name as typed and showed it red, as brief 51 binds one. The owner ruled the other way for the
  Inspector: a mistyped VAR committed there is refused and the old value kept. `UndefinedNamesIn` is checked by every
  Inspector dimension commit (object fields, wire point x/y/z, loop height, span) before anything is written, and the
  field's text is put back. The draw tools' typed fields keep their Define strip. Gate:
  `…AMistypedNameCommittedInTheInspector_IsRefused_AndTheFieldKeepsItsValue`.

Gate: `tests/Ui.Tests/ThreeD/WireHeldShapeTests.cs`.

## The Variables panel's Add row ignored Enter (2026-10-09)

The owner added a VAR, saw the document stay clean, saved, and reopened to find no VAR. The saved file had the wire's
held `"Span": { "Expr": "mySpan" }` and no `Variables` at all. The VAR never reached the document. Headlessly,
`C3dVariablesViewModel.Add` was correct (dirty, saved, the row followed). The Add row's name and value boxes had no key
handler, and only the small + glyph BELOW them added. A value typed and Entered, as every other field in the panel takes
one, did nothing and said nothing. Now:
- Enter in either box adds the VAR (`OnNewVariableKey`).
- The panel lists the names a dimension uses that nothing defines (`UnknownText`), and offers the first in the Add row's
  empty name. Defining the name a held span already uses is then a value and Enter.

Gate: `WireHeldShapeTests.AVarAddedInTheVariablesPanel_IsOfferedByName_DirtiesTheDocument_AndIsSaved`. The key handler
itself is view code and is not driven by the gate: the GUI cannot be launched from this shell.

## A field that does not resolve no longer blanks the drawing (2026-10-09)

R-em3d51-5a built NOTHING when any field failed to resolve, so one mistyped name in a wire's span made the whole drawing
vanish, including the wire the mistake was on. The owner's rule is now a best attempt to draw what works, everywhere:
- **The elaborator always builds what resolved.** An item holding a field that did not resolve is left out (its numbers
  are not its expression's). A wire whose only such field is its held loop height or span is drawn as it stands, unheld.
  The refusals are unchanged, so `Ok` is false, and every run (`em`, thermal, STEP export, glTF convert, problem assembly)
  still refuses: they all gate on `Ok`. `ExpressionsGateTests.Gate8` now asserts the refusal AND that only the resolving
  base was built.
- **`render` draws what resolved too.** `Em3dSetupSource.WhatResolved` draws a refused view's elaboration in a box at its
  own extent. Each refusal is reported as `render.em3d.partial` (a warning, on stderr and in `--json`), so a gap in the
  picture is never silent. Exit 0. Gate: `WireHeldShapeTests.Render_OfAViewWithAMistypedName_DrawsWhatResolved_AndARunStillRefuses`.

## A snapped gizmo drag lands the PIVOT on the snap (2026-10-09)

- **Symptom:** a sheet dragged up its Z arrow and snapped to another box's corner ended up at neither height. It stopped
  short of the corner's z by however far up the arrow the handle had been grabbed.
- **Cause:** a gizmo drag's base point is where the HANDLE was grabbed (the point on the arrow nearest the cursor, or
  where the cursor met the plane square), not a point on the selection. `MoveTool` measured a geometry snap from that
  base, so the selection was moved by `snap − grab` and not by `snap − pivot`. This happened on every axis and on the
  plane handles. Moving with the G key was correct all along, because there the base is a point the user picked on the
  geometry.
- **Fix:** `MoveTool.Grabbed` measures a snap from the selection, not from the grab point, when `FromGizmo`. Along each
  moved axis, the bounding-box face NEARER the snap lands on it (`MoveTool.Extent`, from `BoundsDbu`). So a box dragged up
  to a corner above it brings its top to the corner's height, a box dragged down onto one brings its bottom there, and a
  sheet goes to the snap's height. The first cut moved the bounding-box centre onto the snap, which put a solid's middle at
  the corner. With no extent it still does that. Without a snap the drag follows the grab point, so the handle stays under
  the cursor. Gate: `GizmoSnapTests`.

## A save deleted every imported copy a Step part did not name (brief-sar-11, 2026-10-18)

- **Trap:** `RemoveUnnamedStepCopies` runs after every save and deletes each file this session copied into the view's folder
  that no Step object names. Brief-sar-11's voxel volumes and surface meshes are copied the same way (`StepImport.CopyInto`),
  so the first save after importing one would have deleted the file the new object names, and the next elaboration would have
  refused it as missing.
- **Fix:** the save keeps whatever a `VoxelBody` or a `Mesh` names as well. Any future object that references a copied file
  must be added to that walk, or its file goes at the next save.

## Session state set after Push is read too late by a synchronous rebuild (brief-sar-12, 2026-10-18)

- **Trap:** `Push` applies the edit, and with a synchronous post (the headless harness, and any rebuild that finishes before
  the call returns) the scene is rebuilt and ADOPTED inside it — `OnSceneAdopted` runs before the next line. Place Against
  Phantom… recorded which objects it had placed (what the separation dimension measures) after `Push`, so the adopt that the
  edit itself caused measured nothing and the dimension never appeared.
- **Fix:** anything `OnSceneAdopted` reads for the edit being pushed is set BEFORE `Push`. Gate:
  `PhoneSarEditorTests.PlaceAgainstPhantom_IsOneEntry_AndDrawsTheSeparation`.

## A layout instance with wires and nowhere to land them — "U1 not found" (2026-10-27)

A wBond pushed from a schematic by Update Layout from Schematic lands its wires where no pad is drawn (often on a
layout with no conductor artwork at all). New 3D View from Layout places that layout as `U1`, and the elaborator
refused the instance on the wire build. Every refusal of a placed cell is drawn as the same tiny "not found" box,
so the user saw a missing cell rather than the reason. `Em3dLayoutSolidsOptions.PadsOptional` already existed for
exactly this case, but only the `.cem` preview passed it. `C3dElaborationOptions.PadsOptional` now carries it to
every placed layout. The 3D editor's picture sets it, and what a solve would refuse on
(`Em3dLayoutSolidsResult.PadRefusal`) is a warning on the instance. Runs leave it off and still refuse.

**And a layout with no conductor artwork contributes its wires and nothing else.** A placed layout's laterally
unbounded slabs and its undrawn ground plane fall back to the bounding box of whatever else was built; with wires
alone that drew a substrate and a bottom-copper plane nobody placed (`U1/<dielectric>`, `U1/Bottom copper`), which
got in the way of objects drawn in the `.c3d` around them. `Em3dLayoutSolids` now builds none of them when nothing is
drawn on a conductor layer (a board outline still bounds them, since that IS drawn), matching the `.cem` planar
preview's rule.

**The placed layout's cache key had only the `.clay`'s stamp.** Wire edits in the layout editor are saved to the
paired `.wBond`, so the file watcher's re-elaboration got a cache hit and kept drawing the old wires. The key now
includes the `.wBond`'s stamp, and a hit is also checked against the stamps of every file the flatten read (its
sub-cells), which had the same flaw. The 3D view follows a wire edit when the layout is SAVED. Following it as
soon as a drag ends, without a save, would mean handing the open layout editor's in-memory design to the
elaborator. That is not done.

**A wire that cannot be built no longer makes its layout "not found".** One wire whose shape the build refuses (a
turn it cannot sweep, a wire with no plan direction) refused the whole wire build, the whole placed layout with it,
and the 3D view labelled every refused instance `'<cell>' not found`, so the user went looking for a file that was
there. In a picture (`PadsOptional`) the wire is now left out with a warning that says "invalid wBond geometry" and
why, and the rest of the layout and its other wires are drawn. A `.wBond` that cannot be built at all leaves the
artwork drawn without wires. A run is unchanged and still refuses. `C3dElaboration.UnresolvedWhy` carries the reason
for each instance that is still not drawn: "invalid wBond geometry" when the layout has nothing else to draw, and
"cannot be drawn in 3D" for any other refusal. The label says "not found" only when the cell really did not resolve.

## Designer round 32 — a `.c3d` stayed on the old layout after the layout was saved (2026-10-30)

- **The workspace's `FileSystemWatcher` was the only cue** that a placed `.clay` (or its technology, or a material
  library) had changed: the elaborator reads children from disk, keyed by stamp, and re-elaborates only when told.
  The watcher covers the whole workspace with the default 8 KB buffer and had no `Error` handler, so an overflow
  (a Palace run writing hundreds of field files under `results/`, a synced cloud folder) dropped the save silently.
  The field report needed closing and reopening the `.c3d`, sometimes more than once.
- **Fix:** `C3dEditorViewModel` keeps the stamps of each adopted elaboration's `FilesRead` (the document's own file
  left out) and `RecheckInputs()` re-elaborates when any differ. The workspace calls it when the `.c3d`'s tab is
  activated and on the watcher's `Error`. The buffer is 64 KB. Gate:
  `HierarchyGateTests.RecheckInputs_APlacedLayoutChangedUnseen_IsElaboratedAgain`.

## DFR32-3 — an air-box padding typed in Properties did not show; Delete Saved Fields (2026-10-30)

- **Reproduced headlessly (R-dfr32-3-1), on the panel's own calls.** One face, all six one after another, six fast
  commits while a rebuild was in flight, and a commit from the setup's panel after a padding edit all reached the
  document and the drawn box (`AirBoxEditShowsTests`). What dropped the edit: Enter on one face starts a rebuild, the
  next face is typed while it is in flight, and when that scene is adopted `OnSceneAdopted` → `Properties.Reload()`
  → `LoadAirBox` writes every percentage box back from the document. The text being typed was replaced by the old
  default percentage, so its Enter compared that against `_padLoaded`, found it unchanged and stated nothing. On a
  large board, where a rebuild takes seconds, that is the next face being typed almost every time.
- **Fix:** `C3dPropertiesViewModel.Reload` keeps any padding box whose text differs from what was loaded into it
  (typed, not committed) across a reload that is still the air box. Gate:
  `AirBoxEditShowsTests.TypingTheNextFace_WhileThePreviousEditsSceneIsAdopted_IsNotWipedByTheReload` (fails before).
- **Not verified in a live window:** `Load()` sets `IsAirBox` false and back to true, which hides the padding grid for
  an instant. If Avalonia takes focus off a box hidden that way, the box keeps its text now but loses the caret, and a
  click out of it commits.
- **Delete Saved Fields (R-dfr32-3-2)** is on a setup card's menu (shown while the run folder holds fields, read as
  the menu opens) and on a `.c3d`'s or `.cem`'s Project Tree row. The work is `PalaceFieldFiles.Delete` in
  `src/Design`. The 3D view passes `C3dEditorViewModel.MoveToTrash` (the system Trash; a test seam), then
  `RunFinished()` reads every plot's run again. A plot of that run then says the fields were deleted, through
  `FieldDiscovery.FieldsDeleted` → `PlotProblem(..., fieldsDeleted)`. Gate: `SavedFieldsTests`.

## A first corner snapped above the drawing plane started the object on the plane (2026-10-31)

- **Symptom:** with the plane at z = 0, a Box's first corner snapped onto a substrate's top corner showed the glyph on
  the top, and the click put the corner on the bottom. `PlanePoint` projects every snap along the normal onto the
  plane, and Box, Sheet, Cylinder, Sphere, Ellipsoid, Polygon and Polyline all take their first point through it.
- **Fix:** those tools say `TakesPlaneAtFirstClick`; on that click a geometry snap off the plane moves the editor's
  plane through it (`PlaneThroughFirstSnap`, announced in the status line) before the tool reads the click. The plane
  itself moves, not only the gesture's copy, so the grid, the grid snap and the rubber band's ray hit the same plane
  as the object; projecting onto a gesture-only plane would have left the second corner's grid snap on the old one.
  A grid snap, an edge-on view or a snap already on the plane changes nothing. Gate:
  `DrawToolGateTests.Draw1b_AFirstCornerSnappedAboveThePlane_MovesThePlaneThroughIt`.

## An electrostatic setup's Pick never filled a terminal (2026-10-31)

- **Cause:** the setup panel's list of metal pieces came from the elaboration the 3D view held when the panel was BUILT.
  `WorkspaceViewModel.OpenC3dEditor` calls `RestoreActiveSetup` before `Start`, so the panel is built before the first
  elaboration, gets no lister, and is reused afterwards (`ReloadSetupEditor` keeps a panel whose setup is unchanged).
  The list stayed empty, so `PickConductor` matched nothing, said nothing, and left the Pick armed. Selecting another
  setup card (which builds a panel after the elaboration) made it work, which is why the VM-level test never saw it.
- **Fix:** every adopted elaboration hands the open panel a fresh lister (`RefreshSetupBodies` → `SetBodyLister`), which
  re-lists while a window shows the panel, so an object drawn since is pickable too. A listing overtaken by a later one
  lands nothing.
- **Three more ways a click did nothing, found with UiSnapshot's `--c3d-pick`:** a click on a dielectric was silently
  ignored (now the line under the table names it, and while armed says which terminal waits); a Pick stayed armed after
  the window closed and took the next object selected in the view, unasked (detaching the view now disarms it); and an
  armed draw tool or measurement takes the click before the selection sees it (arming a Pick now puts both away).
- **Layout:** the hidden magnetostatic source-port column kept its fixed 70 px, which pushed each row's ✕ past the
  setup window's 520 px edge; the column is Auto with a fixed-width box now.
- Gate: `SetupTerminalPickTests` (fails before: no conductors listed).
