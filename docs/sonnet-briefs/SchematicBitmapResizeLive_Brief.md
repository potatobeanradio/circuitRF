# circuitRF — Schematic bitmap: resize gripper + live drag/resize (Claude Code / Sonnet)

The SYMBOL editor's bitmap resize/aspect/live is already fixed (Opus did it directly: SymbolGeometry.ScaleBy now
handles BitmapPrimitive; DropBitmap sizes to native aspect). The SCHEMATIC side uses a SEPARATE model
(`EditableBitmap`/`EditableCanvasObject`, rendered by `SchematicRenderer.DrawBitmaps`) and still needs work.
Skew is already fixed there too (DropBitmap now sizes to native aspect via `SchematicRenderer.TryGetBitmapPixelSize`).
Two gaps remain, both schematic-only. Sub-gated; report+STOP between layers. Firewall green; build/test green.

## What's already done (don't redo)
- SymbolGeometry.ScaleBy handles BitmapPrimitive (symbol resize works, live + committed).
- Symbol DropBitmap + Schematic DropBitmap both size to native aspect (no skew).
- `SchematicRenderer.TryGetBitmapPixelSize(path)` exists (native dims via the shared decode cache).

## Read first (verified on disk)
- ViewModels/SchematicViewModel.cs —
  - `HandleSelectPress` / `HandleSelectDrag` / `HandleSelectRelease`: the Select tool. Canvas objects DRAG via
    `_dragStartObjPositions` (snapshot) → live `obj.X/obj.Y` mutate → `UpdateDragOverlay` → commit
    `MoveCommand` with `CanvasObjectMoveSnapshot`. **There is NO resize-gripper path for canvas objects.**
  - `UpdateDragOverlay`: builds `ComponentDragPositions` + `WireDragPoints` overlay overrides — but NO
    canvas-object position override. So a dragged bitmap's live position isn't pushed to the renderer.
  - `SnapshotDragStartPositions`, `CommitDragAsCommand` (uses `CanvasObjectMoveSnapshot`), `ClearDragState`.
- Renderers/SchematicRenderer.cs —
  - `DrawBitmaps(model.Bitmaps, …)`: draws each `SchematicBitmap{X,Y,Width,Height,ImagePath,Opacity}` with a
    simple `DrawBitmap(skBmp, SKRect)` (+ `DrawBrokenBitmapBox`). **Reads the RenderModel only** — no overlay
    override, so during a drag (RenderModel not rebuilt until drag-end) the bitmap doesn't move live.
  - `DrawOverlay`: draws selection boxes, ghosts, rubber-band, etc. — where a resize gripper handle would be
    drawn for a selected canvas object.
- Schematic model: `EditableBitmap : EditableCanvasObject` (`ImagePath, X, Y, Width, Height, Opacity`);
  `SchematicBitmap` is its render-model projection (in EditModel.BuildRenderModel). `SchematicOverlay` record
  (overlay override fields). Find `CanvasObjectMoveSnapshot` + the canvas-object move command.
- Controls/SchematicCanvas.cs — the gripper hit-test would live here (mirror SymbolEditorCanvas's
  `HitTestGripper` pattern: a 7-screen-px box at the selected object's bbox corner). Right-click bitmap context
  menu (Resolve/Refresh) may already exist on the schematic — check; if not, out of scope here.

## LAYER 1 — Live drag for schematic bitmaps (overlay override + renderer reads it)
A dragged canvas object mutates `obj.X/obj.Y` live, but the renderer reads the stale RenderModel, so it doesn't
move until release. Push a live position override and have DrawBitmaps honor it.
1. **SchematicOverlay**: add `CanvasObjectDragPositions` (`IReadOnlyDictionary<string,(double X,double Y)>?`),
   mirroring `ComponentDragPositions`.
2. **UpdateDragOverlay**: when `_dragStartObjPositions` is non-empty, populate
   `CanvasObjectDragPositions[id] = (obj.X, obj.Y)` for each dragged canvas object (mirror the component-override
   block).
3. **DrawBitmaps**: accept the overlay (or the override dict) and, for each bitmap whose id is in the override,
   draw at the overridden X/Y instead of the model X/Y. (DrawBitmaps currently takes `model.Bitmaps`; pass the
   override through from `Draw` like `ComponentDragPositions` is passed to component drawing. Match a bitmap to
   its EditableCanvasObject id — ensure `SchematicBitmap` carries its `Id` so the override can key on it; add
   `Id` to the projection if missing.)
4. Clear on drag-end (existing full RebuildRenderModel already does this).
**Gate:** drop a bitmap, drag it → it tracks the cursor LIVE (no jump at release); undo restores. Report.

## LAYER 2 — Resize gripper for schematic bitmaps (live + committed, aspect-locked)
No resize exists for canvas objects. Add it, mirroring the symbol editor's gripper.
1. **Gripper handle (render):** in `DrawOverlay`, when exactly one canvas object (bitmap) is selected, draw a
   small resize handle (7-screen-px box) at its bottom-right bbox corner (screen-space, like the symbol
   editor's `ResizeHandle`). Only for canvas objects; don't add gripper to components/wires.
2. **Gripper hit-test + state (SchematicCanvas + VM):** add a `HitTestCanvasObjectGripper(wx,wy)` (mirror
   SymbolEditorCanvas `HitTestGripper`: within half-size of the handle corner). On Select press, check the
   gripper FIRST (before drag/rubber-band) when a single canvas object is selected; if hit, enter resize mode
   with state: `_isObjResizing`, `_resizeObjId`, original bbox (X,Y,W,H).
3. **Live resize (aspect-locked):** on drag in resize mode, compute new W/H from the dragged corner relative to
   the top-left anchor (X,Y fixed). **Lock aspect ratio by default** for bitmaps (use the larger axis ratio, or
   min — match the symbol editor, which uses `Math.Min(|sx|,|sy|)` for aspect-lock). Mutate `obj.Width/Height`
   live (snap to author grid as appropriate, but keep aspect), and push a live override so the renderer shows
   it in-progress (extend the Layer-1 override to carry W/H too, OR mutate the EditableBitmap live and let
   DrawBitmaps read the override position + the live W/H — pick one and keep it consistent with Layer 1).
4. **Commit:** on release, if W/H changed beyond a threshold, execute a resize command. If a
   `ResizeCanvasObjectCommand` doesn't exist, add one (mirror the symbol's `ResizeSymbolPrimitiveCommand`:
   stores before/after W/H (+ X/Y if the anchor isn't top-left), notifies in Execute AND Undo). Undoable; one
   command.
5. **Clear** resize state in `ClearDragState`/`CancelCurrentOp`.
**Gate:** select a schematic bitmap → a resize handle shows at its corner; drag it → the image resizes LIVE with
locked aspect (no skew); release commits; undo restores; the handle doesn't appear for components/wires. Report.

## Acceptance
Schematic bitmaps drag live (track the cursor, no release-jump) and resize via a corner gripper (live,
aspect-locked, undoable); skew is gone (native aspect on drop — already fixed). Parity with the symbol editor.
Firewall green; build/test green; no regression to component/wire drag, selection, or the existing canvas-object
move/undo.

## Guardrails
- Schematic-only — don't touch the symbol editor (already done) or the shared SymbolGeometry.
- Aspect-lock bitmap resize by DEFAULT (mirror the symbol editor's bitmap aspect-lock).
- The renderer must read a live override during drag/resize (it currently reads only the RenderModel, which is
  why there's no live update); don't force a full BuildRenderModel per tick (perf rule) — use the overlay
  override path like components/wires already do.
- Reuse existing patterns: `CanvasObjectMoveSnapshot`/MoveCommand for drag; mirror the symbol's gripper +
  ResizeSymbolPrimitiveCommand for resize. Add `ResizeCanvasObjectCommand` only if none exists.
- Broken-bitmap box (DrawBrokenBitmapBox) must also honor the live position/size override so a broken ref drags
  and resizes too.
- Sub-gate; report+STOP between layers.
- Update docs/design/ui-design.md (schematic canvas-object resize gripper + live drag) if it documents canvas
  objects.

*Exit: schematic bitmaps drag and resize live with locked aspect, matching the symbol editor; no skew, no
release-jump, fully undoable.*
