# circuitRF — Symbol Editor Cleanup Round 3 (Claude Code / Sonnet)

Five items: symbol clipboard (the "copy doesn't work" bug = never implemented), tri-state snap, symbol-editor
zoom defaults, the text-primitive focus bug, and the Bitmap primitive feature (cross-editor drag-drop). Ordered
small/independent first, the two features last (Bitmap is the big one). Sub-gated; report+STOP between layers.
Firewall: SymbolModel/SymbolGeometry/EditableSymbol + SchematicModel stay framework-free (paths + numbers, no
Skia/Avalonia; a bitmap primitive stores a PATH, never pixel data). `dotnet build`/`dotnet test` green each
layer.

## Diagnoses confirmed on disk (read first)
- **Copy bug:** `WorkspaceViewModel.Cut/Copy/Paste/SelectAll` are EMPTY stubs ("each active control handles
  clipboard natively"). True for SchematicCanvas (it raises `ClipboardCopy/Cut/PasteRequested` from its OnKeyDown
  and the code-behind does the work via `SchematicClipboard`), but the **SymbolEditorCanvas has NO clipboard
  handling at all**, and there is **no SymbolClipboard**. So symbol Ctrl-C / Edit-menu Copy do nothing, and
  paste can't work. → IMPLEMENT symbol clipboard (Layer 1). This is a feature, not a regression.
- **Text focus bug:** the symbol editor's text-typing routes through the canvas key handler
  (`SymbolEditorViewModel.OnKeyDown` / `OnTextInput`), but window/canvas-level key bindings (S/W/F/G/R, tool
  shortcuts, undo) compete and steal focus/keys while typing a Text primitive. → suppress global key handling
  while `_isTypingText` (Layer 4).
- **Reusable infra:** `SchematicCanvas` has the DnD drop-target pattern (`DragDrop.SetAllowDrop` +
  OnPaletteDragOver/Drop, `PaletteDragPayload.TryParse`, ghost overlay) and the clipboard-event pattern. Reuse
  both for Layers 1 and 5.

## LAYER 1 — Symbol clipboard (Copy / Cut / Paste of primitives + pins)
Implement a `SymbolClipboard` mirroring `SchematicClipboard`, and wire the symbol canvas + Edit menu to it.
1. **SymbolClipboard.cs** (Ui/Clipboard): serialize the SELECTED primitives + pins to JSON (reuse
   SymbolPersistence's serialization for the primitive/pin records) onto the clipboard as an app-specific text
   format (prefix-guarded, like `PaletteDragPayload`), AND (optional, matching the schematic) a PDF/SVG/PNG
   render of the selection via the existing render path + `ClipboardRenderPolicy` (Force Light/Dark + transparent
   bg). v1 REQUIRED: the JSON round-trip (so paste works); the image formats are a NICE-TO-HAVE this layer —
   implement if cheap, else gate to JSON and note it.
2. **SymbolEditorCanvas:** mirror SchematicCanvas — raise `ClipboardCopyRequested/CutRequested/PasteRequested`
   from its key handler on Ctrl/Cmd+C/X/V (set `e.Handled`); the code-behind does the async clipboard work.
   (Add the events + the OnKeyDown branch; the symbol canvas currently has neither.)
3. **Copy/Cut:** serialize `_selection` + `_selectedPins` to the clipboard. Cut = copy then delete (existing
   DeleteSymbolPrimitivesCommand + DeleteMultipleSymbolPinsCommand), undoable.
4. **Paste:** read the clipboard; if it's our symbol payload, deserialize and place the primitives/pins offset
   slightly (e.g. +1 grid cell) from the originals via Place commands (undoable), and select the pasted items.
   Pins paste P-snapped. If the clipboard isn't our format → no-op (don't throw).
5. **Edit menu / window Copy/Cut/Paste:** the WorkspaceViewModel stubs stay no-ops BUT must route to the active
   document. Simplest: when the active dockable is a SymbolEditorDocument, the menu Copy/Cut/Paste invoke the
   canvas's clipboard path (raise the same events, or call a public VM method). Mirror however the schematic menu
   path reaches SchematicClipboard. (If the schematic Edit-menu Copy currently ALSO relies only on the canvas
   key handler and the menu items are dead, say so — but the user reported the MENU Copy not working too, so
   wire the menu to the active document's clipboard for both editors.)
**Gate:** in the symbol editor, select prims+pins → Ctrl/Cmd+C → Ctrl/Cmd+V pastes an offset copy (undoable);
Cut removes + pastes; Edit-menu Copy/Paste work too; pasting in an empty area works; non-symbol clipboard content
is ignored. Report whether image formats were included.

## LAYER 2 — Tri-state Snap to Grid (P / p / off) with transparency-graded icon
Currently `GridSnap` is a bool (p=5 art grid on/off; pins always P). Make snapping tri-state:
- New enum `SnapMode { ConnectionGrid, FineGrid, None }` (P-grid, p-grid, no snap). Replace the `GridSnap` bool
  on SymbolEditorViewModel; `SnapToP` honors it (ConnectionGrid→snap to P=100, FineGrid→snap to p=5, None→no
  snap). **Pins ALWAYS snap to P regardless** (unchanged — pin snap uses SnapToConnectionGrid directly).
- The G key cycles P → p → None → P.
- Toolbar button: replace the ToggleButton with a cycling Button whose Grid icon background gets MORE
  TRANSPARENT for finer/no snap: P = full color (opaque accent bg), p = partial transparency, None = no color
  (transparent bg). Tooltip shows the current mode. Follow HIG.
**Gate:** cycle the snap button (and G key) through P / p / off; the icon bg transparency steps accordingly;
drawing/moving primitives snaps to P, p, or not at all; pins still always land on P. Report.

## LAYER 3 — Symbol-editor zoom defaults (larger default; own min/max)
Symbols are smaller than schematics, so the symbol editor should open more zoomed-in and have its own zoom
limits (distinct from the schematic's MinZoom=0.0005 / MaxZoom=50).
- In SymbolEditorCanvas: set a larger DEFAULT zoom for a blank/new symbol (so a typical symbol fills the view —
  pick a value suited to a symbol spanning a few P=100 cells, e.g. the fit logic with a higher floor, or a
  sensible default zoom when there's no content to fit).
- Give the symbol canvas its OWN MinZoom/MaxZoom appropriate for symbol scale (tighter range than the
  schematic; e.g. a higher MinZoom since you never zoom as far out, and a MaxZoom that lets you see fine grid
  detail). Don't reuse the schematic constants.
- A new blank symbol document should open at the larger default (not zoomed way out).
**Gate:** open a new blank symbol → it's comfortably zoomed for authoring (not tiny); zoom limits feel right for
symbol scale; zoom-to-fit (F) still frames content. Report the chosen default/min/max.

## LAYER 4 — Text primitive: fix focus loss while typing
Adding a Text primitive loses focus / keystrokes get intercepted by global shortcuts while the user types.
- While `_isTypingText` is true, the canvas/window key handlers must NOT process tool shortcuts (S/W/F/G/R/etc.),
  undo, or other global keys — the keystrokes belong to the text buffer. `SymbolEditorViewModel.OnKeyDown`
  already intercepts when `_isTypingText` at the top, but the WINDOW/CANVAS-level handlers (and any
  KeyBindings) may fire first and steal the key (and focus). Ensure the text-typing state takes precedence:
  the canvas keeps focus, and global key handling is suppressed until the text is committed/cancelled
  (Enter/Escape).
- Confirm the canvas retains keyboard focus when the Text tool starts (Focus() on text-start) so TextInput
  events flow to OnTextInput.
**Gate:** pick the Text tool, click, type a multi-word label including letters that are tool shortcuts
(s, w, f, g, r) → all characters appear in the text, no tool switches, focus stays; Enter commits; Escape
cancels. Report.

## LAYER 5 — Bitmap primitive (drag-drop into Symbol AND Schematic editors)
A bitmap is a placeable primitive that stores a PATH (never pixel data — .csch/.csym store paths). v1: the only
way to PLACE one is DRAG-AND-DROP a bitmap file onto the editor. Reuse the schematic DnD lessons (the gotchas).
1. **Model (framework-free):** add a Bitmap primitive to BOTH SymbolModel (SymbolPrimitive) and the schematic
   model's equivalent — fields: `string Path`, position (Cx,Cy or top-left), `W`, `H` (so aspect/scale persist),
   `Rotation`, `double Opacity` (0..1, default 1). NO pixel data. (Alpha: bump .csym AND .csch format_version —
   reject-on-mismatch, no migration.)
2. **Drop target:** in BOTH SymbolEditorCanvas and SchematicCanvas, accept a FILE drop (not the palette text
   payload). Reuse the SchematicCanvas DnD pattern but read the FILE path from the drop:
   - `e.DataTransfer` — extract a file path (DataFormat.File / storage items). **The gotcha:** the drop must
     yield a real filesystem PATH (because we persist paths, not data); if no path can be extracted, or the path
     isn't a readable image, **create NO bitmap primitive** (silently reject, like foreign palette drops).
   - On a valid image drop: load the image to get its native pixel dimensions, create the Bitmap primitive at
     the drop point sized to the image's aspect (at a sensible default scale), via a Place command (undoable).
3. **Render:** load the image from Path and draw it at (Cx,Cy,W,H,Rotation,Opacity). Cache decoded images by
   path (invalidate on refresh). A BROKEN reference (missing file / decode fail) renders as a SUBTLE OUTLINE BOX
   (thin, muted) at the primitive's W×H — not an error splash.
4. **Select / resize / rotate / delete / opacity:** the bitmap participates in the editor like other primitives
   — selectable, gripper-resize with CONSTANT ASPECT RATIO (lock aspect; the resize already supports Shift-aspect
   — for bitmaps make aspect-lock the default), rotate (R), delete, and an editable **Opacity** field in the
   inspector (0–100%). 
5. **Broken-link context menu:** right-click inside a bitmap's outline box (broken OR valid) → ContextMenu with:
   - **"Resolve Path…"** → file picker to pick a new image file; updates the primitive's Path.
   - **"Refresh"** → re-load the file from Path; on success, ADJUST W/H (aspect) in case the new file differs
     from the old dimensions (re-fit to native aspect at the current scale).
   (Mirror the schematic's right-click ContextMenu plumbing — SchematicCanvas already tracks
   `ContextMenuTargetId`; add a symbol-canvas equivalent hit-test for the bitmap region.)
6. **Persistence:** save/load the Bitmap primitive (path + geometry + opacity) in both .csym and .csch. Paths:
   store as given (absolute) for v1; relative-to-document is a v2 nicety — note it, don't build it.
**Gate (both editors):** drag a PNG/JPG onto the symbol editor AND the schematic → a bitmap primitive appears at
the drop point at the image's aspect; resize keeps aspect; rotate/delete/opacity work; save+reopen restores it;
a drop with no extractable path or a non-image → nothing created; rename the file on disk + reopen → subtle
outline box; right-click → Resolve Path picks a new file (renders); Refresh reloads + re-fits aspect. Report;
note what file-path extraction method worked for the drop (the gotcha).

## Acceptance
Symbol copy/cut/paste works (canvas + Edit menu); snap is tri-state (P/p/off) with a transparency-graded icon
and pins always on P; the symbol editor opens at an appropriate larger default zoom with its own min/max; the
Text primitive no longer loses focus while typing; a Bitmap primitive can be drag-dropped into both editors,
stores a path (broken→outline box, Resolve/Refresh context menu), resizes with locked aspect, and persists.
Firewall green; build/test green; no regression to existing draw/select/move/rotate/save or schematic DnD.

## Guardrails
- Models stay framework-free; the Bitmap primitive stores a PATH only — never pixel data.
- Reuse the schematic DnD + clipboard + context-menu patterns (the gotchas were already solved there); don't
  reinvent. The drop MUST yield a real filesystem path or no bitmap is created.
- Pins ALWAYS snap to P regardless of the tri-state snap mode.
- Bitmap resize locks aspect ratio by default; broken reference = subtle outline box, not an error.
- Alpha format_version bump (reject-on-mismatch, no migration) for .csym AND .csch shape changes (Bitmap
  primitive).
- Symbol clipboard JSON is the v1 requirement; image formats nice-to-have — state what shipped.
- Sub-gate; report+STOP between layers; don't batch (esp. Layer 5).
- Update docs/design/symbol-editor.md (clipboard, tri-state snap, zoom defaults, Bitmap primitive) +
  ui-design.md / project-file-formats.md (Bitmap in .csch/.csym; path-not-data; broken-link UX).

*Exit: symbol copy/paste works, snap is tri-state with a graded icon, the symbol editor opens zoomed for
authoring, Text typing keeps focus, and bitmaps can be dropped into both editors as path-backed, resizable,
opacity-controlled primitives with broken-link resolution.*
