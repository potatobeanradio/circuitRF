# Sonnet Brief — Phase L1b: drawing tools, snap, and undo

**Design:** `docs/design/layout-view.md` §6.1 (tools), §3.1 (primitives), §3.3 R10 (angle mode),
§1.5 R5 (snap governs future edits only), §1 R6 (live physical readout and unit-suffixed entry),
§10.10 (the 30-second target these tools have to serve). **Consumes L1a** — the canvas, renderer, viewport,
grid and rulers all exist.

**Scope is L1b ONLY: create geometry, and be able to undo it.** No selection, no hit-testing, no handles,
no move, no delete-by-picking, no clipboard, no boolean ops — L1c (selection + editing) and L1d (Clipper2 +
clipboard) follow.

## Goal

Pick a layer, pick a tool, draw a shape, and see it land in the layout's color with a live dimension readout
throughout. Ctrl+Z removes it. This is the phase that introduces layout's undo stack, so the command
plumbing matters more than the number of tools.

## Verified substrate (consume — already exists)

- **L1a**: `LayoutCanvas` (owns `_panX`/`_panY`/`_zoom`, has a marked seam where tool dispatch goes),
  `LayoutRenderer`, `LayoutViewport`, `LayoutGridMath`, `LayoutRulerControl`, the metadata bar.
- **`SymbolEditorViewModel`** is the pattern for everything in this brief: the `Tool` enum,
  `SetActiveToolCommand` as `RelayCommand<string>`, `EnumEqualsToBoolConverter` for toolbar active-state
  styling with zero code-behind, the `Overlay` property carrying the live ghost primitive, the two-point-drag
  vs. multi-point-click gesture split, and Escape-cancels. **Clone the shape, do not invent a new one.**
- **`IUiCommand` / `UndoRedoStack` / `Execute(...)`** — the house mutation pattern. Existing command
  implementations live under `src/Ui/Commands/`; layout's go in `src/Ui/Commands/Layout/`.
- **L0**: `LayoutUnits.TryParse`/`Format`, `Technology.Layers`, `FallbackPalette`.

## Code changes

### 1. Undo comes first

`LayoutDocument` implements whatever undoable-document interface `SymbolEditorDocument` implements, owning
its own `UndoRedoStack`. `LayoutEditorViewModel.Execute(IUiCommand)` applies and pushes.

**Fine-grained commands, not snapshots.** L0d's `.ctech` editor deliberately used snapshot undo because a
`Technology` is tiny; a layout is not, and cloning one per edit is exactly what §5's budget forbids. Say so
in the class header so the two documents' differing choices read as deliberate.

L1b needs only one command — `AddShapeCommand` — but build the plumbing properly, because L1c and L1d land
a dozen more on top of it:

- Undo/redo restore the shape **at its original index** in `LayoutView.Shapes`, not appended to the end.
  Z-order within a layer is list order, and an undo that quietly reorders geometry is a bug that surfaces
  much later as a rendering difference.
- One user gesture is **one** undo entry, however many vertices it took to draw.
- `IsDirty` tracks the stack against the last saved point.
- Ctrl/Cmd+Z / Ctrl+Y (and Cmd+Shift+Z) key bindings on both the docked view and `LayoutEditorWindow`,
  plus Edit-menu items.

### 2. Current layer

You cannot draw without choosing a layer, and there is no layer UI yet.

Add a **current-layer ComboBox to the layout toolbar**, populated from `Technology.Layers` ordered by
`ZOrder`, each item showing a color swatch and name. Persist the selection on the document (in-memory only —
**do not** add it to `.clay`; it is a session preference, not layout data).

- With **no technology**, the combo offers a small fixed set of fallback layers (e.g. `1/0` … `4/0`) resolved
  through `FallbackPalette`, so an untechnologied workspace is still usable.
- When the technology changes under an open document (L0c's seam), re-populate and keep the current
  selection if its `LayerKey` still exists, otherwise fall back to the first layer.

**A full layer panel** — per-layer visibility and lock toggles, filtering, reordering — **is a later brief.**
This is a picker, nothing more.

### 3. Tools

`LayoutEditorViewModel.Tool`: `Select` (inert in L1b — it is L1c's), `Rect`, `RoundedRect`, `Circle`,
`Polygon`, `Path`, `Label`.

**Two-point drag** — press, drag, release, one undo entry:
- `Rect` → `RectShape`, normalized.
- `Circle` → `CircleShape`; the press point is the centre, the drag distance the radius. (A two-corner
  bounding-box variant can wait; centre-radius is what RF pads and via barrels want.)
- `RoundedRect` → `RoundedRectShape`; corner radius comes from a toolbar field, clamped to half the shorter
  side.

**Multi-point click** — click to place each vertex, live ghost following the pointer:
- `Polygon` → `PolygonShape`, closed. Double-click or Enter closes; Backspace removes the last vertex;
  Escape cancels the whole gesture.
- `Path` → `PathShape`, open, using the toolbar's **width** and **end style** fields.

**Single click**: `Label` → `LabelShape` at the click point, with an inline text prompt; text height from a
toolbar field. `IsPort` stays false — port placement is its own thing and belongs with the EM work.

**No `Curve` tool in L1b, deliberately.** §6.1 lists one, but the interaction that creates a curved edge —
drag a segment's midpoint to set its bulge — is *the same interaction* as L1c's bulge handle. Building it
once in L1c and reusing it at draw time is less code and one consistent gesture, rather than two
implementations that drift. So in L1b, `Polygon` and `Path` produce straight edges only, and the **promotion
rule** (§4) is how a `Curve` first comes into existence. Note this in the completion write-up so the doc's
tool list can be reconciled.

### 4. The promotion rule (specify now, implement in L1c)

`PolygonShape` and `CurveShape` differ only in carrying an edge list. **A `PolygonShape` whose edge is
converted to an arc or cubic is replaced by an equivalent `CurveShape`**; a `PathShape` already carries an
edge list and gains the curved edge in place. Write this down in `LayoutModel.cs`'s header now, even though
L1c does the work — it is the kind of rule that gets decided twice, differently, if left implicit.

### 5. Snap and angle mode

- **Snap** every placed point to `LayoutView.SnapDbu` (`Math.Round(dbu / snap) * snap`). Holding a modifier
  (suggest Alt) suspends snapping for that point. Snap of 0 or less means no snapping.
- **Angle mode** (§3.3 R10) constrains `Polygon` and `Path` segments during drawing: `Manhattan` → axis-
  aligned, `Deg45` → multiples of 45°, `AnyAngle` → free. Constrain the *candidate* point before snapping,
  then snap, then re-check — and prefer the axis-aligned result when the two fight, rather than emitting an
  off-mode segment. Angle mode never applies to `Circle` or `RoundedRect`.
- Both are read from the document; **neither re-snaps existing geometry** (§1.5 R5).

### 6. Live readout and typed entry (§1 R6)

- **Mandatory throughout every gesture**: the metadata bar shows the live dimension in the display unit —
  `W × H` for `Rect`/`RoundedRect`, radius for `Circle`, running segment length and total for
  `Polygon`/`Path` — updating on every pointer move, formatted with `LayoutUnits.Format`.
- **Typed entry** on the toolbar fields (`Path` width, `RoundedRect` corner radius, `Label` height) accepts
  unit suffixes through `LayoutUnits.TryParse`: `2.9mm`, `115 mil`, `50u`. Invalid text reverts, it does not
  throw.
- **Typed commit for `Rect`**: while a rect drag is live, the W and H readouts are editable; entering a value
  commits the shape at exactly that size. This one interaction is a direct §10.10 dependency — *"click start,
  click end, type W = 2.9mm"* is the transmission-line step of the 30-second budget.

### 7. Canvas wiring

Fill L1a's marked seam: left-press/move/release and key events dispatch to the VM's tool state machine;
middle-drag pan and wheel zoom keep working **during** a gesture (panning mid-polygon is normal). Crosshair
cursor for every drawing tool, arrow for `Select` — mirror `SymbolEditorCanvas.UpdateCursor`.

The in-progress ghost renders through the VM's `Overlay`, drawn by `LayoutRenderer` **above** all layers in
the current layer's color with a dashed outline, so it reads as provisional.

## Scope guardrails (do NOT do in L1b)

- **No selection, hit-testing, handles, move, or delete-by-picking** (L1c). `Select` is a registered tool
  that does nothing yet.
- **No `Curve` tool, no arc/bézier drawing, no edge conversion, no bulge handles** (L1c — see §3).
- **No clipboard, no booleans/offsets, no Clipper2, no flattener, no Flatten-to-Polygon** (L1d).
- No layer panel, no visibility/lock toggles, no object snapping (vertex/edge/midpoint) — grid snap only.
- No `Via` or port placement, no instances (L3), no properties panel (L1c).
- No spatial index, no caching, no LOD, no R8b merge tier (L2).
- Don't touch `src/Core`, `src/Engine`, `RfCore`, `SchematicRenderer`, or the symbol editor.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Every tool produces the right primitive** with the right field values, on the current layer — headless
   VM-level tests driving the tool state machine with synthetic pointer events, no rendering required.
3. **One gesture, one undo entry.** A 12-vertex polygon undoes in a single Ctrl+Z, and redo restores it
   identically (assert via `LayoutPersistence.Serialize` equality).
4. **Undo restores list position.** Draw A, draw B, draw C; undo C; undo B; redo B — B is back at index 1,
   not index 2. This is the ordering bug the §1 rule exists to prevent.
5. **Snap** — with a 1 µm snap, every placed vertex is a multiple of 1000 DBU; the Alt modifier suspends it;
   `SnapDbu = 0` places raw coordinates.
6. **Angle mode** — in `Manhattan`, every polygon segment is axis-aligned; in `Deg45`, every segment angle is
   a multiple of 45°; in `AnyAngle`, an arbitrary segment survives unchanged. Assert that constrain-then-snap
   never emits an off-mode segment.
7. **Changing snap or angle mode moves no existing geometry** (§1.5 R5) — byte-identical serialization
   before and after.
8. **Typed entry parses and reverts** — `2.9mm` in the width field yields 2,900,000 DBU at 1 nm resolution;
   `2.9 furlongs` reverts to the previous value without throwing.
9. **Typed rect commit** — a live rect drag plus `W = 2.9mm`, `H = 20mm` produces exactly those dimensions
   regardless of where the pointer was.
10. **Escape and Backspace** — Escape mid-polygon leaves the model untouched and clears the overlay;
    Backspace drops exactly one vertex.
11. **Current layer** — new shapes carry the selected `LayerKey`; with no technology the fallback layers are
    offered and usable; a technology change that removes the current layer falls back to the first layer
    without throwing.
12. **Dirty and save** — drawing dirties the document; undoing back to the saved state clears the dirty
    state; save + reload round-trips every drawn shape.

## On completion

1. Add a "Phase L1b — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: **fine-grained
   commands here vs. L0d's snapshot undo and why**, the **restore-at-original-index** rule, that the
   **current-layer combo is session state and deliberately not persisted in `.clay`**, that there is
   **no `Curve` tool by design** and the **promotion rule** that replaces it, and the test file names.
2. Report back before L1c (selection with overlap cycling, vertex/edge/bulge/control-point editing, move,
   delete, edge conversion, and the properties panel with layer and net) is briefed.
