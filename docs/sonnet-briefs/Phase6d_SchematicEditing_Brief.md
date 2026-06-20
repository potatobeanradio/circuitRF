# Phase 6d — Schematic Editing: place, move, wire, edit, undo (Claude Code / Sonnet)

**Goal:** turn the read-only 6c canvas into the **interactive schematic editor** — place components from the
palette, move/rotate/mirror, draw wires with drag-to-connect, inline-edit parameters, the parameter dialog,
copy/paste, and **undo/redo across every mutation** — plus the **shared canvas-object family** (bitmaps,
text, shape primitives) that rides the same select/move/resize/rotate/undo machinery. **No net extraction or
engine run yet** (6e) and **no plots-on-canvas** (Phase 7). This is the interactive heart of the editor.

> Read first: `docs/design/ui-design.md` rev 2 — §3.1 (canvas objects — the shared family), §4 (the whole
> schematic editor: 4.1 nav/grid, 4.2 place/move, 4.3 connections, 4.4 nets, 4.5 parameters, 4.6 selection/
> keyboard grammar, 4.7 save/restore), §7.2 (the schematic toolbar this wires up), §10 (cross-cutting:
> command-pattern undo, copy/paste, drag-drop). Also `docs/design/ui-architecture.md` (firewall),
> `src/Ui/CLAUDE.md`. **Mine splotRF's interaction + undo** (cited below). Design notes win.

## Build on 6b + 6c
- 6b established the **command/undo-redo infrastructure** (the global commands route through it). 6d adds the
  **editor mutation commands** to that same stack — do not create a second undo system.
- 6c built the **canvas** (custom SkiaSharp control, world↔pixel transform, pan/zoom, spatial index, LOD,
  >120 fps at 10k) and the **read model**. 6d adds **mutation + interaction** on top; the spatial index 6c
  built is now used for **hit-testing** (click→select among 10k).
- The firewall holds: all of 6d is in `src/Ui`; the 6a test stays green.

## Mine splotRF (cite these)
- **`splotRF/src/ViewModels/UndoCommands.cs`** + its `IUndoableCommand` (Execute/Undo, self-contained
  per-action snapshot, commands call internal view-model helpers rather than mutating private state). This is
  the **template** for 6d's commands (`AddPlotCommand`/`RemovePlotsCommand` → `PlaceComponentCommand`/
  `DeleteCommand`, incl. the single + multi-select and re-select-on-undo handling).
- **`splotRF/src/Controls/PlotControl.cs` / `DragSelectOverlay.cs`** — pointer interaction, drag, and the
  rubber-band select overlay pattern (adapt for schematic selection).
- **`splotRF/src/Controls/PlotContainerView`** + `PlotContainerViewModel` — the selectable/movable/resizable
  placed-object pattern (the model for the §3.1 canvas-object family).
- **`splotRF/src/PlotExporter.cs`** — the MAIN clipboard/serialization hub: it serializes a plot config to/
  from the clipboard (System.Text.Json) AND handles the rich image/vector clipboard formats (the `DataFormat`
  platform formats) plus file export. This is what to mine for circuitRF's copy/paste (adapt the
  config-to-clipboard JSON approach to schematic selections).
- **`splotRF/src/WindowsClipboard.cs`** — NARROW, Windows-specific helper: generates EMF (Enhanced Metafile,
  `CF_ENHMETAFILE`) vector content for the Windows clipboard, which Avalonia's cross-platform clipboard can't
  produce; `PlotExporter` delegates to it on Windows. **Borrow but MERGE:** circuitRF should fold the
  PlotExporter clipboard logic and this Windows-EMF helper into ONE clipboard helper (the config-to-clipboard
  serialization for paste-within/across-schematics, plus the optional EMF/image representation for pasting
  into other apps). All in `src/Ui` (firewall).

## Scope

### STEP 1 — selection + hit-testing (the foundation for everything else)
- **Hit-testing** via the 6c spatial index: click → topmost object under the cursor (component, wire, port,
  net label, or canvas object). Z-order aware (§3.1 stack: components/wires above decorations).
- **Selection model** (§4.6): click selects; Shift+click adds/removes; **rubber-band drag on empty canvas**
  multi-selects (adapt `DragSelectOverlay`); Esc clears; Ctrl/Cmd+A selects all. Selection is a first-class
  state the renderer shows (highlight/handles).
- Selected objects render with selection affordances (highlight outline; resize handles for resizable canvas
  objects, §3.1).

### STEP 2 — place / move / rotate / mirror (components) — all undoable
- **Place:** drag a component from the palette (§2.2) onto the canvas → `PlaceComponentCommand`. Ground/Var/
  Measurement toolbar buttons (§7.2) place their objects too (Var/Measurement render as editable
  name=expression labels; they are design objects, not electrical — §7.2/§5.1).
- **Move:** drag selected object(s) → `MoveCommand` (records old/new positions). Grid-snap when Grid Snap is
  on (§4.1).
- **Rotate / Mirror:** `R`/`Shift+R` rotate 90° CCW/CW, `M`/`Shift+M` mirror H/V (§4.6), both during
  placement and on selection → undoable commands. Symbol geometry + port positions transform together.
- **Nudge:** arrow keys (one grid step), Shift+arrow (coarse) → `MoveCommand`.
- **Delete:** Delete/Backspace → `DeleteCommand` (single + multi-select; undo restores + re-selects, per the
  splotRF pattern).

### STEP 3 — wiring (simple-ortho, drag-to-connect)
- **Draw wires** with the Line / Angled Line tools (§7.2): click-drag to lay **simple orthogonal** segments
  (§4.3 — NOT auto-routed; beautiful routing is deferred). Each wire-draw is an undoable command.
- **Drag-to-connect (§4.2):** when a component is placed/moved so its **ports overlap a wire** (or
  wire-endpoints coincide), they connect; with **Keep Connect** on (§7.2), wires re-attach as parts move.
- **Visual connection state (§4.3):** dark square at connected wire-wire/port-wire points; **unconnected
  port → subtle red box**. NOTE: 6d shows connection state from **local geometric adjacency** (does this
  port/endpoint coincide with a wire?) — it does NOT run full net extraction (that's 6e). Local "is this
  point connected to something" is enough to drive the dots/red-boxes; the global net resolution is 6e.
- **Net labels (§4.4):** place a label on a wire (a label is a placeable object); 6d stores it on the wire/
  net for 6e to consume. (Auto-naming + the global net identity is 6e.)
- **Junction dots:** a wire-wire crossing connects only with a junction dot (§5.1 convention); 6d provides
  placing/showing the dot.

### STEP 4 — parameter editing
- **Inline edit (§4.5):** click an on-schematic parameter label → it becomes an editable text box; commit →
  `EditParameterCommand`. Respects the per-parameter "show on schematic" flag.
- **Parameter dialog (§4.5):** double-click a component/cell → a dialog/popover listing all parameters,
  editable; a **Help** button opens the component's local HTML doc (placeholder HTML for now). Edits are
  undoable. Parameter *values are expressions* (reuse the Phase-1 expression engine — a value can be
  `1/(w0^2*C)`); validate/parse via the existing engine, surface errors (§8 Messages).
- **Disable to Open/Short (§7.2):** the toolbar buttons set the per-instance disable state (None/Open/Short)
  → undoable; the renderer shows the disabled glyph. (Extraction honors it in 6e/§5.1; 6d just sets+shows
  the flag.)

### STEP 5 — the shared canvas-object family (§3.1) — bitmaps, text, primitives
Build the **shared canvas-object abstraction** (the common contract) + the three concrete types. This rides
the SAME select/move/resize/rotate/transparency/undo/clipboard machinery from STEPs 1–2, which is why it's
in 6d:
- **Shared contract (§3.1):** selectable, drag-move, **resize** (handles), **rotate**, transparency, Z-order
  (decoration layer below components, §3.1 stack), **lock/unlock** (locked = not selectable/movable/resizable;
  Unlock→Lock deselects), context menu, Insert-menu placement, cut/copy/paste, undo/redo. Build this once as
  the base; the three types specialize it.
- **Bitmap (§3.1.1):** `Insert → Image…`; **aspect-locked resize via bottom-left gripper**; context menu for
  transparency / relative-size presets (50/75/100/200%) / lock / **Locate…** (System dialog to re-point a
  missing file) / **Refresh** (reload from disk); **persist only the file path** (relative preferred,
  absolute allowed — `project-file-formats.md`); **placeholder box** if the path won't load.
- **Text (§3.1.2):** `Insert → Text`; inline-editable, wrapping; Font / Size / Weight / Color / transparency.
- **Primitives (§3.1.3):** `Insert → Shape…`; rectangle, circle, line; line-width / color / transparency;
  **line** has arrowheads (none/start/end/both) + arrowhead-size + **two draggable endpoint control points**.
- (Plots-on-canvas §3.1.4 is NOT here — Phase 7. SVG import §5A is NOT here — Phase 6f. Build the shared base
  so both slot in later.)
- The **Insert menu** (§8) is added with these.

### STEP 6 — copy/paste + save/restore
- **Copy/paste (§10):** system clipboard; copy a selection (components, wires, canvas objects) and paste —
  into the same or another schematic; pasted items stay selected; undoable. Mine `PlotExporter.cs` (the main
  clipboard/serialization logic — config-to-clipboard via System.Text.Json) and `WindowsClipboard.cs` (the
  narrow Windows-EMF helper), **merging them into one circuitRF clipboard helper**: (a) serialize the
  schematic selection to the clipboard as JSON for paste within/across schematics, and (b) optionally place a
  rich image/vector (EMF on Windows) representation for pasting into other apps. In `src/Ui` (firewall).
- **Save/restore (§4.7):** persist the schematic as **`.csch`** (the circuitRF schematic format, specified in
  `docs/design/project-file-formats.md`) — component placements (position/rotation/mirror), wires,
  net labels, junction dots, disable states, canvas objects (incl. bitmap *paths*), per-parameter show flags,
  and view/zoom state. **`.cnl` stays netlist-only** — 6d does NOT write geometry into `.cnl`; the `.cnl`/
  design model is *derived* from the `.csch` by extraction (6e). Implement `.csch` read/write to the spec
  (System.Text.Json modeled on splotRF's `DataDisplayConfig.cs`, enum-as-string, human-diffable,
  `format_version` with reject-on-mismatch per the alpha no-back-compat policy, the schematic model
  framework-free per the firewall). Restore reproduces the visual state. Flag any schema detail the spec
  left open (`project-file-formats.md` Open items) rather than inventing silently.

## Acceptance
1. Selection (click/shift/rubber-band/Esc/select-all), hit-testing via the spatial index, selection
   affordances — all working at 10k-component scale without losing the 6c frame rate.
2. Place/move/rotate/mirror/nudge/delete components — **all undoable** through the 6b command stack; grid-snap
   honored.
3. Wiring: simple-ortho draw, drag-to-connect, Keep Connect, connection dots + unconnected-port red boxes
   (from local adjacency), net labels stored, junction dots. (No global extraction yet.)
4. Parameter editing: inline + dialog (with Help→placeholder HTML), expression-valued, undoable; Disable-to-
   Open/Short flag set + shown.
5. Canvas-object family (§3.1): shared base + bitmap (path-persist, placeholder, bottom-left aspect gripper),
   text (inline/wrap/font), primitives (rect/circle/line w/ arrowheads + endpoint handles) — select/move/
   resize/rotate/transparency/lock/cut-copy-paste/undo all working; Insert menu added.
6. Copy/paste (system clipboard) + save/restore to **`.csch`** per `project-file-formats.md` (System.Text.Json
   modeled on `DataDisplayConfig.cs`); round-trips a schematic (save → reopen → identical visual state).
7. **6a firewall green**; `dotnet build`/`dotnet test` green; Phases 1–5 + 6b/6c untouched; 10k canvas still
   ≥30 fps (report it).

## Guardrails
- **Every mutation is an undoable command** on the 6b stack (§10) — no direct model mutation from views/
  code-behind. Mirror splotRF's `IUndoableCommand` shape. This is the load-bearing discipline of 6d.
- **No net extraction / no engine run** (6e). Connection visuals come from *local adjacency*, not global net
  resolution. **No plots-on-canvas** (Phase 7), **no SVG import** (6f) — but build the §3.1 shared base so
  they slot in.
- **Performance:** selection/hit-test/edit must not regress the 6c frame rate at 10k; use the spatial index
  for hit-testing, don't linear-scan. Report the frame rate.
- **Firewall:** all in `src/Ui`; extraction/expression logic that's core stays core (the parameter
  expressions reuse the existing engine — call it, don't reimplement).
- **Schematic persistence is `.csch`** (`project-file-formats.md`) — `.cnl` stays netlist-only; 6d writes
  geometry/visuals to `.csch`, never into `.cnl`. The schematic *model* is framework-free (no Avalonia —
  firewall); the canvas renders it.
- Honor `src/Ui/CLAUDE.md` (MVVM, accessibility, drag-drop affordances) and the design-quality bar.
- Diagnostics over grinding; flag interaction/perf issues rather than hacking.
- Update `src/Ui/CLAUDE.md` with the editor command set, the canvas-object base, and the persistence-format
  decision.

*6d exits with a real, interactive, undoable schematic editor (+ the canvas-object family) that saves and
restores — but not yet wired to the engine. 6e adds net extraction + run, closing schematic → simulation.*
