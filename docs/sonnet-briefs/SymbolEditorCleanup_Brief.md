# circuitRF — Symbol Editor cleanup (Claude Code / Sonnet)

A multi-area cleanup of the Symbol Editor. Grouped into sub-gated layers by subsystem; **report and STOP
between layers** so each is verified before the next. Several items touch the same files
(`SymbolEditorViewModel`, `SymbolEditorCanvas`, `SymbolGeometry`, `SymbolEditorRenderer`,
`SymbolEditorOverlay`, `SymbolEditorView.axaml(.cs)`, `WorkspaceViewModel`). Read each before editing. Where a
schematic-editor equivalent already solves the problem well (selection highlight, snap toggle, live drag
overlay), **mirror it** rather than invent. Firewall green; every mutation undoable (notify in Execute AND
Undo); pins/connectivity must stay on the connection grid P.

## Read first (real types — all exist on disk)
- `src/Ui/ViewModels/SymbolEditorViewModel.cs` — `Tool` enum, `OnActiveToolChanged`, pointer handlers
  (`OnPointerPressed/Moved/Released`), `SelectToolPress`, `_isDragging`/`_liveDx/_liveDy`, `_isDrawingTwoPoint`,
  pin drag (`PinToolPress`, `_isPinDragging`), `OnKeyDown`, `CancelOp`, `RebuildOverlay`, `_selection`,
  `SnapToP`(5)/`SnapToConnectionGrid`(100), `SaveSymbolAsAsync`, `PortCount`, `CurrentFontSize/Style`.
- `src/Ui/Controls/SymbolEditorCanvas.cs` — pointer/key routing, world↔screen, `OnPointerMoved` (note it does
  NOT `InvalidateVisual()` after a VM move — see Layer 1).
- `src/Ui/Schematic/SymbolGeometry.cs` — `BboxOf`, `HitTest` (per-primitive), `TranslateBy`, `ComputeBb`,
  `PointToSegDist`. Ellipse hit-test uses avg-radius (Layer 2); Text uses `TextHalf=30` square (Layer 7).
- `src/Ui/Renderers/SymbolEditorRenderer.cs` — `DrawSelectionOverlay` (dashed bbox + fill — Layer 8 replaces
  with a wire-style stroke), `DrawGrid`, `DrawPinMarkers`.
- `src/Ui/Schematic/SymbolEditorOverlay.cs` — `SelectedIndices`, `LiveDragOffset`, `SelectedPinIndex`,
  `PinLiveDragOffset`, `InProgressPrimitive`, `RubberBand`. Add fields as needed (resize handle, etc.).
- `src/Ui/Views/Content/SymbolEditorView.axaml(.cs)` — toolbar (`ScrollViewer`+`StackPanel`), Font
  `NumericUpDown`+`ComboBox` (Layer 9 moves these), Undo/Redo buttons, metadata bar, Save/SaveAs.
- `src/Ui/ViewModels/WorkspaceViewModel.cs` — `OnDockableClosed` (only handles `SchematicDocument` →
  Layer 10 bug), `_openDocsByPath`, `ActivateIfOpen`, `OpenOrActivateSymbol`, `NewSymbolAsync`,
  `SaveSymbolAsAsync` owner. Schematic-editor reference impls for parity: `SchematicViewModel` drag-overlay,
  `SchematicView` snap toggle + selection highlight, `SchematicCanvas` `InvalidateVisual` after VM calls.
- For the Properties pane: `ViewModels/Dock/PropertiesTool.cs` (schematic-only today), `PropertiesView`,
  `ParameterEditorViewModel` (pattern reference). The symbol primitive types live in `SymbolModel.cs`
  (`LinePrimitive`, `PolylinePrimitive`, `RectPrimitive`, `EllipsePrimitive`, `TextPrimitive`, etc.).

---

## LAYER 1 — live rendering on object drag (+ canvas invalidation) [Bug]
**Symptom:** dragging a primitive shows no live movement.
**Cause:** `SymbolEditorCanvas.OnPointerMoved` calls `_viewModel.OnPointerMoved(...)` but does NOT
`InvalidateVisual()` afterward (unlike `SchematicCanvas`, which invalidates after every VM pointer call). The
VM updates `LiveDragOffset` + `RebuildOverlay`, but the canvas never repaints until the next unrelated
invalidation.
**Fix:** in `SymbolEditorCanvas.OnPointerMoved` (and `OnPointerReleased`/`OnPointerPressed` where the VM may
mutate overlay), call `InvalidateVisual()` after the `_viewModel?.OnPointer*` call (mirror `SchematicCanvas`).
Confirm the VM already raises `Overlay` PropertyChanged (it does via `RebuildOverlay`) — if `SyncFromVm` only
repaints on PropertyChanged, the explicit invalidate is the belt-and-suspenders the schematic canvas uses.
**Gate:** dragging any primitive shows smooth live movement; release commits (undoable). Report.

## LAYER 2 — ellipse (and avg-radius prims) draggable [Bug]
**Symptom:** an ellipse is nearly impossible to grab.
**Cause:** `SymbolGeometry.HitTest` for `EllipsePrimitive` (and `CirclePrimitive` when not filled) tests
`|dist − avgR| <= tol` — only the thin ring at the average radius hits, and for an ellipse with Rx≠Ry that
ring barely touches the actual outline, so most clicks miss.
**Fix:** make the ellipse hit-test use the true ellipse outline distance (normalized
`(x/Rx)²+(y/Ry)²≈1` band within tol), or — simpler and consistent with the new selection model in Layer 8 —
hit-test against the actual outline with linewidth-aware tolerance. Unfilled circle/ellipse should hit near
the drawn curve; filled should hit the interior. Keep it framework-free in `SymbolGeometry`.
**Gate:** an ellipse (incl. non-circular) is grabbed by clicking near its outline at normal zoom; filled
variants grab on the interior. Report.

## LAYER 3 — Undo/Redo via Ctrl+Z / menu (not just toolbar) [Bug]
**Symptom:** toolbar Undo works; ⌘Z/Ctrl+Z (menu/keybinding) does not.
**Cause (confirm by instrument):** the Symbol Editor's `UndoCommand`/`RedoCommand` live on
`SymbolEditorViewModel`, but the window-level ⌘Z keybinding/menu routes to `WorkspaceViewModel`'s
`_activeUndoTarget` (an `IUndoableDocument`). A `SymbolEditorDocument` opened as a tab may not be set as the
active undo target (the active-doc tracking in `OnDocumentDockPropertyChanged` only wires `SchematicDocument`
for Properties, but undo routing should follow any `IUndoableDocument`). Verify whether `SymbolEditorDocument`
implements `IUndoableDocument` and is picked up by `SetActiveUndoTarget`.
**Fix:** ensure `SymbolEditorDocument` is an `IUndoableDocument` exposing its VM's `UndoRedo`, and that
`OnDocumentDockPropertyChanged` sets it as `_activeUndoTarget` when a symbol tab activates (the schematic path
already does `SetActiveUndoTarget(activeDockable as IUndoableDocument)` — confirm symbol docs flow through the
same line). For tear-off `SymbolEditorWindow`, confirm its own keybindings hit the VM stack.
**Gate:** with a symbol tab active, ⌘Z/Ctrl+Z and the Edit-menu Undo/Redo drive the symbol's stack; toolbar
still works; schematic undo unaffected. Report (say which fix applied).

## LAYER 4 — drag-move a Pin (on grid) [Bug]
**Symptom:** a pin can't be drag-moved.
**Cause (confirm):** `PinToolPress` sets `_isPinDragging` and `OnPointerMoved` updates `_pinLiveDx/Dy` only
when `ActiveTool == Tool.Pin` AND `_isPinDragging`; but with Layer 1's missing invalidate it also showed
nothing. Verify the press actually hit-tests the pin (`HitTestPin` tol) and that release commits
`MoveSymbolPinCommand`. If the hit radius is too small or the move path is gated wrong, fix it.
**Fix:** ensure: Pin tool + press on a pin → `_isPinDragging`; move → live ghost (snapped to P=100 via
`SnapToConnectionGrid`); release → `MoveSymbolPinCommand` (undoable). Pins MUST land on the connection grid P
(never the fine grid) — keep `SnapToConnectionGrid`. Pair with Layer 1's invalidate so the drag shows live.
**Gate:** with the Pin tool, a pin drags live, snaps to P, commits undoably, and stays connectable. Report.

## LAYER 5 — Snap-to-Grid toggle (toolbar button + `G` key) [Feature]
Add a Snap-to-Grid toggle to the Symbol Editor, mirroring the schematic's. v1 symbol drawing snaps to the fine
grid p=5 (`SnapToP`) and pins to P=100. Add a `GridSnap` bool on the VM (default true); when off, `SnapToP`
returns the raw value (pins ALWAYS stay on P regardless — connectivity is non-negotiable; only art primitives
free-move). Toolbar `ToggleButton` (Grid icon) bound to `GridSnap`; `G` key toggles it (add to VM `OnKeyDown`,
guard against text-typing mode). 
**Gate:** `G` and the toolbar toggle flip snapping for art primitives; pins still snap to P; state reflected in
the button. Report.

## LAYER 6 — Rotate selection [Feature]
Add Rotate (90° steps) for selected primitives (and the selected pin). Add a `RotateSelectionCommand`
(undoable; notify both ways) that rotates each selected primitive's control points about the selection centER
(or symbol origin — pick origin for predictability and state it), reusing/adding a `RotateBy` in
`SymbolGeometry` parallel to `TranslateBy`. Wire a toolbar button + `R` key (guard text mode). A rotated pin
must re-snap to P.
**Gate:** selected primitive(s)/pin rotate 90° per invocation, undoably, staying on the correct grid. Report.

## LAYER 7 — Text primitive hit box [Bug]
**Symptom:** text hit box is wrong.
**Cause:** `SymbolGeometry.BboxOf`/`HitTest` model text as a fixed ±30 square at the anchor — independent of
the actual string/font size, and not aligned to how the renderer draws it (anchor + alignment + font size).
**Fix:** compute the text hit box from the rendered extent: width ≈ string length × font advance, height ≈
font size, positioned per the text's anchor/alignment (match `SchematicRenderer`/`SymbolEditorRenderer` text
draw). Framework-free approximation is fine (use a per-char advance factor like the schematic label width
estimate) but it must track font size and align to the drawn glyphs. (Text is exempt from the Layer-8
outline-hit model — it keeps a box, just a correct one.)
**Gate:** clicking on rendered text selects it; clicking the old phantom area does not; box tracks font size.
Report.

## LAYER 8 — outline-based selection hit box + wire-style highlight [Behavior]
**Two coupled changes, applied to all primitives EXCEPT Text:**
1. **Hit box = the primitive's own lines** (accounting for linewidth): selection/hit-test uses the stroke
   geometry with tolerance = half the rendered stroke width + a small constant (so thin lines stay grabbable).
   `SymbolGeometry.HitTest` already does per-edge distance for most prims; make tolerance linewidth-aware and
   ensure ellipse/arc/curve use their true outline (ties into Layer 2).
2. **Selection highlight = a stroke of mostly-transparent System Accent**, drawn on the SAME geometry as the
   primitive (its outline), plus a small padding — exactly like the schematic WIRE selection highlight.
   Replace `DrawSelectionOverlay`'s dashed bbox + fill with: re-stroke each selected primitive's path using a
   semi-transparent accent paint at `strokeWidth + padding`. Reuse the schematic wire-highlight approach
   (`SchematicRenderer` wire-selection) for visual parity — read it and mirror the paint/width/alpha.
   Text keeps its (corrected, Layer 7) box highlight.
**Gate:** selecting a primitive outlines its actual shape in translucent accent (not a dashed rectangle);
hit-testing grabs on the lines; matches the schematic wire-selection look. Report with a screenshot/description.

## LAYER 9 — toolbar overflow: remove Font controls; resize fix [Bug/Layout]
**Symptom:** a horizontal scrollbar appears on the toolbar at some window sizes, covering content.
**Fix:** remove the **Font size `NumericUpDown`** and **Font style `ComboBox`** (and the "Font:" label) from
the toolbar. Font size + style must instead be available in the **symbol parameters inspector** (the Layer 11
properties pane) when a Text primitive is selected (and as the default for the Text tool — keep
`CurrentFontSize`/`CurrentFontStyle` on the VM, just surface them in the inspector, not the toolbar). With the
two widest controls gone, confirm the toolbar fits common window sizes without the horizontal scrollbar
intruding (the `ScrollViewer` can stay as overflow insurance, but should not normally show).
**Gate:** no intruding horizontal scrollbar at typical sizes; Font controls gone from toolbar; still reachable
in the inspector. Report.

## LAYER 10 — closed symbol/schematic document can't reopen [Bug]
**Symptom:** after closing a Symbol Editor tab (and Schematic tab), double-clicking the file in the Project
Tree doesn't reopen it.
**Cause:** `WorkspaceViewModel.OnDockableClosed` early-returns unless the dockable is a `SchematicDocument`,
so `_openDocsByPath` is never cleared for `SymbolEditorDocument` (and `CellParameterEditorDocument`). The
stale entry makes `ActivateIfOpen` find a closed dockable and `SetActiveDockable` it (no-op) instead of
re-opening. (Schematic: the entry is removed only when `doc.FilePath is not null` — verify the path key used
on open matches the one removed on close so it always clears.)
**Fix:** in `OnDockableClosed`, remove the closed dockable from `_openDocsByPath` for ALL tracked document
types (match by the dockable reference, not just `SchematicDocument` + FilePath): iterate and remove any
`_openDocsByPath` entry whose value `ReferenceEquals` the closed dockable; also remove from `_scratchDocs` if
present. This makes reopen work for symbol, schematic, and cell docs uniformly.
**Gate:** open a symbol → close it → double-click in tree → it reopens; same for a schematic and a cell
parameter editor. Report.

## LAYER 11 — primitive properties in the Properties pane (live, with position; polyline coords) [Feature — largest]
Today the Properties pane (`PropertiesTool` → `ParameterEditorViewModel`) shows only schematic component
parameters. Add a **symbol-primitive inspector** surface.
1. **Active-context routing:** when a `SymbolEditorDocument` is the active tab, `PropertiesTool` shows a new
   `SymbolPrimitiveInspectorViewModel` (parallel to the schematic path in `OnDocumentDockPropertyChanged`);
   when a single primitive (or pin) is selected, it binds that primitive's editable fields.
2. **Editable fields per primitive type** — including **position** (e.g. Cx/Cy for center-based prims; X1/Y1/
   X2/Y2 for Line; anchor X/Y for Text) so the user can type exact coordinates. Also expose the type-specific
   geometry (W/H, R, Rx/Ry, radius, sweep, etc.), stroke tier, color role, and — for Text — content, font
   size, font style (the controls removed from the toolbar in Layer 9 live here).
3. **Live render:** edits in the inspector mutate via undoable commands (a `SetSymbolPrimitiveFieldCommand` or
   reuse `MoveSymbolPrimitivesCommand` for position) and call `NotifyChanged()` so the canvas re-renders live
   as the user types/commits (debounce or commit-on-enter is fine; state which).
4. **Polyline coordinate list:** for `PolylinePrimitive`/`PolygonPrimitive`, show a **scrollable, virtualized
   (lazy) list** of the point coordinates, each row editable (X, Y), with live update. Use a virtualizing
   `ItemsControl`/`ListBox` so large polylines don't build hundreds of rows eagerly.
5. **Selected pin:** when a pin is selected, show its LocalX/LocalY (P-snapped) and PortIndex (reuse the
   existing remap path).
This is the biggest item — keep the VM framework-free where possible; the View is Avalonia. Mirror
`ParameterEditorViewModel`'s structure (rows, two-way binding, undoable commits). Sub-gate INTERNALLY: (11a)
routing + read-only display of selected primitive fields incl. position; (11b) live editable commits; (11c)
polyline lazy coord list; (11d) Text font size/style here.
**Gate (each sub-step):** selecting a primitive shows its fields incl. position; editing a field moves/edits
it live and undoably; polyline shows a virtualized editable coord list; Text font controls work here. Report
per sub-step.

## LAYER 12 — primitive resize gripper (bottom-right; Shift = keep aspect) [Feature]
Add a resize handle (gripper) at the **bottom-right of the selected primitive's bbox**. Dragging it resizes
the primitive; holding **Shift** preserves aspect ratio. Applies to resizable prims (Rect/RoundedRect/Circle/
Ellipse/Sine/HalfWave/Arc; Line/Polyline/Polygon resize by scaling their points about the bbox's top-left
anchor). 
1. Overlay: add a handle rect to `SymbolEditorOverlay` (bottom-right of the selected single primitive's
   bbox); render it (small filled square, accent) in `SymbolEditorRenderer`.
2. Hit + drag: press on the handle → resize mode; move → scale the primitive's geometry so its bottom-right
   tracks the cursor (snap to grid per Layer 5), top-left anchored; Shift → uniform scale (keep W:H). Live
   render (Layer 1). Release → undoable `ResizeSymbolPrimitiveCommand` (or reuse a scale command).
3. Single-selection only for v1 (handle shown only when exactly one resizable primitive is selected).
**Gate:** a gripper appears at bottom-right of a selected primitive; dragging resizes live + undoably; Shift
keeps aspect; grid snap respected. Report.

## LAYER 13 — Save-As default directory = the cell's symbol dir [Polish]
**Symptom:** Save As starts in an arbitrary directory.
**Fix:** in `SaveSymbolAsAsync`, set the picker's `SuggestedStartLocation` to the cell's **symbol** directory
when known. The symbol dir is derivable from `CurrentSymbolPath` (its containing folder) when the symbol came
from a cell; for a brand-new unsaved symbol created via `NewSymbolAsync`, thread the cell's symbol dir through
(the document already knows its path, or pass the dir at creation). Use
`StorageProvider.TryGetFolderFromPathAsync(symbolDir)` like the workspace pickers do. Fall back to the current
behavior when no cell dir is known.
**Gate:** Save As on a cell symbol opens the picker in that cell's `symbol/` folder. Report.

---

## Acceptance
All 13 layers green at their gates; the Symbol Editor: drags live (prims + pins), grabs ellipses and outlines,
rotates, snaps via a toggle (`G`), resizes via a gripper (Shift=aspect), shows live-editable primitive
properties incl. position and a polyline coord list, has correct Text hit box, wire-style selection highlight,
working ⌘Z/menu undo, no intruding toolbar scrollbar (Font moved to inspector), reopenable closed docs, and
Save-As defaulting to the cell symbol dir. `dotnet build`/`dotnet test` green; firewall green; every mutation
undoable; pins/connectivity stay on P.

## Guardrails
- **Mirror the schematic editor** for: live drag invalidate, snap toggle, selection highlight (wire-style),
  active-undo-target routing — don't invent parallel mechanisms.
- **Pins always snap to the connection grid P** regardless of the art-grid snap toggle.
- Every edit undoable; notify in Execute AND Undo; keep the geometry/VM framework-free (no Avalonia in
  `SymbolGeometry`/VM core).
- **Instrument-first** for the two "confirm cause" bugs (Undo routing L3, pin drag L4) before changing code.
- Sub-gate every layer (and L11 internally); report+STOP between each. Don't batch unrelated layers.
- Update `docs/design/symbol-editor.md` (rev bump): outline-hit + wire-style highlight, resize gripper, rotate,
  snap toggle, primitive inspector incl. position + polyline coord list, Font moved off the toolbar, the
  reopen + undo-routing fixes.

*Exit: the Symbol Editor feels like the schematic editor's sibling — live, precise (typed coordinates + grid),
outline-accurate selection, resize/rotate, a real primitive inspector, and no dead reopen/undo paths.*
