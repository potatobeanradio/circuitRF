# Phase 6d-fix — Schematic Editor Punch-List (Claude Code / Sonnet)

Fifteen fixes/additions to the 6d editor from owner review. Most are straightforward; two need a design
decision (display names, workspace definition) — both are specified below, so implement as written. Work them
in the **numbered order** (performance first — it's the worst user-facing issue), sub-gate, and report after
each group. Do NOT refactor beyond what each item needs.

> Context files: `src/Ui/Schematic/EditableSchematic.cs` (the edit model + `BuildRenderModel`),
> `src/Ui/ViewModels/SchematicViewModel.cs` (drag/tool state, render-snapshot rebuild),
> `src/Ui/Commands/Schematic/*` (the command set), `src/Ui/Controls/SchematicCanvas.cs`,
> `src/Ui/Renderers/SchematicRenderer.cs`, `src/Ui/Schematic/SchematicPersistence.cs`. Design docs:
> `docs/design/ui-design.md` rev 2, `docs/design/project-file-formats.md` rev 2. Firewall stays green
> (all UI in `src/Ui`; the schematic model stays Avalonia-free).

---

## 1. PERFORMANCE: move/drag/arrow on 10k is 1.5–2 s (do this FIRST)
**Root cause (confirmed):** every `SchematicEditModel.NotifyChanged()` calls `BuildRenderModel()`, which on
each call (a) rebuilds the entire immutable `SchematicModel` for all 10k components, (b) recomputes port
connection state by **linear-scanning every wire point for every port** (≈ O(N²) at 10k), and (c) rebuilds
the **entire spatial index** from scratch. This fires on **every drag tick and every arrow-key nudge** — so
one nudge does an O(N²) connectivity pass + a full snapshot + a full index rebuild. The 6c *renderer* is
fast (>120 fps); the *edit→snapshot rebuild* is the bottleneck.

**Fix — make the edit→render path incremental, and stop recomputing connectivity mid-drag:**
- **During an active drag/nudge, do NOT call the full `BuildRenderModel()` per tick.** Move is a pure
  geometric transform of a known, small set of selected objects. Update the render snapshot **in place** for
  just the moved objects (their positions / wire points), and `InvalidateVisual()` — do not rebuild the
  10k-item list or the spatial index on each tick.
- **Defer connectivity + spatial-index rebuild to drag-END** (mouse-up / nudge sequence settle), not every
  tick. Connection dots/red-boxes only need to be correct when the move *commits*; during the drag the
  user is watching geometry move, not connectivity.
- **Connectivity is O(N²) even at drag-end — fix it too.** `IsConnected`/`IsEndpointConnected` linear-scan
  all wire points per port. Use a **spatial hash / point index** (snap candidate points to a grid-cell key,
  look up coincident points in O(1)) so the whole-model connectivity pass is ~O(N), not O(N²). (The
  `SchematicSpatialIndex` 6c built may already give you the structure; if not, a dictionary keyed by
  quantized (x,y) is enough.)
- **Incremental spatial-index update:** moving k objects should update k entries, not rebuild all N. If the
  index doesn't support remove/re-insert, add it; full rebuild only when the model's object *set* changes
  (add/delete), not when positions change.
- **Acceptance:** arrow-key nudge and drag of a selection in the 10k stress schematic feels immediate
  (target < ~50 ms to repaint, i.e. no visible lag); the full 10k frame rate is unchanged; connection
  state is correct after the move commits. Report the measured nudge-to-repaint time.

## 2. Don't export `Id` to `.csch` (assign fresh on import)
`Id` (the `Guid.NewGuid()` on `EditableComponent`/`EditableWire`/etc.) is **runtime identity only** — it has
no meaning across sessions. **Do not serialize `Id` to `.csch`** (nor to any circuitRF file). On **import,
assign a fresh `Id`** to each object. Apply this rule across all the file formats (`.csch` now; keep the
principle for `.csym`/`.cdd`/`.cws` later). Update `SchematicPersistence` accordingly; the round-trip test
compares *content* (positions, types, params, wires), not `Id`s.

## 3. Esc stops wiring/placement → Select tool
Pressing **Esc** in the schematic cancels any in-progress wiring or component placement (discard the partial
wire / pending placement) **and switches `ActiveTool` to `Select`**. (Esc also cancels the Move-Labels op,
item 14, and the zoom-box, item 5.) One consistent rule: Esc = abort current op + return to Select.

## 4. Pan cursor = hand
While the **Pan** tool is active and/or a pan-drag is in progress, set the canvas mouse cursor to a **hand**
(`StandardCursorType.Hand`, or `SizeAll`/closed-hand while actively dragging if you prefer). Revert to the
default arrow when panning stops / tool changes.

## 5. Add a zoom-box (marquee-zoom) tool to the schematic toolbar
Add a **magnifier / zoom-box** tool to the §7.2 schematic toolbar: the user marquees a rectangle and the
canvas **zooms to fit that rectangle**. While the tool is active, set the cursor to a **magnifier** (if no
suitable `StandardCursorType`, a custom cursor or the closest standard one — `Cross` is an acceptable
fallback; note it if so). **Esc** reverts to the Select tool and the arrow cursor (item 3). This is a view
operation — not undoable.

## 6. Rotating a component keeps its wires connected (reroute slightly)
When a component is rotated (R/Shift+R), its **port world-positions move**; any wire currently connected to a
port must **stay connected** — reroute the wire's attached endpoint to follow the port to its new position
(simple-ortho: move the endpoint, re-run the simple orthogonal segment to the wire's next vertex). Fold the
wire-endpoint adjustment into the **same `RotateCommand`** (so undo restores both the rotation and the wire
geometry atomically). Only wires whose endpoint was coincident with a moved port are affected. (Same spirit
as Keep-Connect for moves, applied to rotation.)

## 7. Message panel: "Saved: <path>" on every file save
Every successful file save must post an info message `Saved: <full path>` to the Messages panel. Currently a
`.csch` save posts nothing. Wire this for **all** save paths (`.csch`, `.cws`, and future `.cdd`/`.csym`) —
route saves through one place that posts the message, so no save type is missed. (The file path should be a
clickable file link per §8 — reveals in OS file manager.)

## 8. Component type DISPLAY NAME — central registry, render uses it
**Problem:** the on-schematic type label uses `SymbolKind.ToString()` (e.g. "FetSdd", "TwoTerminal") — ugly.
The user wants "C" for a capacitor, "L" for an inductor, "R" for a resistor, etc.
**DECISION (storage location):** a **central component-type metadata registry in core** (NOT scattered in
the UI, NOT per-symbol literals in the renderer). Add a small lookup keyed by component type — e.g.
`ComponentTypeInfo { string DisplayName; string InstancePrefix; … }` — living with the design-model/component
definitions (it is design knowledge, reusable by the renderer, the palette, and instance auto-naming alike,
and must be Avalonia-free per the firewall). Note: today the schematic uses `SymbolKind`; map the *component
type* → display name. If the type system is currently only `SymbolKind`, put the registry keyed by that for
now and leave a note that it should key off the real component type when the component model is richer.
- **The renderer reads `DisplayName` from the registry** for the type label (replacing `Symbol.ToString()`
  in `EditableComponent.ToRenderComponent` / wherever the type label is built).
- Seed the registry: R (resistor), L (inductor), C (capacitor), and sensible names for source, ground, the
  FET/SDD, generic cell — fill in what the current `SymbolKind`s map to; flag any you're unsure of rather
  than guessing a wrong abbreviation.

## 9. Workspace `.cws` save writes no file (bug) + define "workspace"
**Bug:** saving a workspace updates the Messages panel but **writes no file**. Implement the actual `.cws`
write (it was stubbed).
**Definition (implement to this):** a **workspace is the collection of files that make up a project** — the
`.csch`, `.cdd`, `.csym`, `.npy`, etc. that belong to it. The **`.cws` file is the workspace manifest**: it
records (a) the **Dock layout** (§2.0), (b) the **referenced libraries**, and (c) the **set of files /
project members** in the workspace (references to the `.csch`/`.cdd`/`.csym`/… by path — relative preferred,
per the bitmap-path policy). It **references, never embeds** (`project-file-formats.md`). So "Save Workspace"
writes the `.cws` manifest (layout + library refs + member file list); the member files are saved as their
own documents. Update `project-file-formats.md`'s `.cws` description to this sharper definition if it's
vaguer there.

## 10. Orthogonal wire corner gaps → miter join (cheap)
Wire polyline corners show a small gap at right-angle bends. Fix as cheaply as possible: set the Skia stroke
**join to miter** (`SKPaint.StrokeJoin = SKStrokeJoin.Miter`) and draw each wire as a **single connected
`SKPath`** (not independent per-segment line draws, which is what produces the corner gaps). Round/square cap
on the path ends is fine; the join is the fix.

## 11. All schematic text must respect zoom (it stops scaling past some zoom)
Some rendered text stops changing size beyond a certain zoom level — likely a min/max font-size clamp or a
fixed pixel size that isn't multiplied by the zoom scale. **All schematic text** (type label, instance name,
displayed parameters, net labels, port labels) must scale continuously with the canvas zoom like the rest of
the geometry. Remove/relax the clamp so text scales with zoom across the whole range (a sanity floor to avoid
zero-size is OK, but it must not visibly "stick" at normal working zooms). Verify by zooming in past where it
currently sticks.

## 12. Marquee direction semantics (left-to-right vs right-to-left)
The current code is close; two corrections. **The component hitbox used for marquee selection is the SAME for
both directions** — the **symbol glyph hitbox** (the glyph bounding box, not the larger placement/bounding
box). Both modes test a component for selection against that same glyph hitbox; what differs between the two
modes is only *what else* gets pulled in (wires), per below.

- **Left-to-right drag = window select:** select objects whose **glyph hitbox is partially enclosed by the
  rectangle** (the rectangle overlaps the glyph hitbox at all). (Note: this is "partially enclosed," not
  "fully enclosed" — per owner; the rectangle touching/overlapping a component's glyph selects it.)
- **Right-to-left drag = crossing select:** select objects the rectangle **touches/intersects** (same glyph
  hitbox test as above), **plus** the **wire segments connected to those selected objects**, **plus** the
  **next component connected to those wire segments**. I.e. crossing mode expands the selection along the
  wires by one hop: selected component → its connected wire segments → the components at the far ends of
  those segments. (Sonnet's existing crossing logic for the wire/next-component expansion is believed close;
  the fix is to use the **glyph hitbox** for the component test, matching window mode.)

Render the two marquees distinctly (the usual convention: **solid** outline for window, **dashed** for
crossing — optionally different fill tints), so the user sees which mode is active. (Owner gave this concept;
implement both directions.)

## 13. Wire selection highlight is too thin
Increase the **line width of the selection highlight for wires** so a selected wire is clearly distinguished
from an unselected one (a clearly thicker stroke and/or the selection-highlight color). Components already
highlight visibly; bring wires up to match.

## 14. Movable component labels (Move Labels: context menu + F5)
Component label text (type, instance name, displayed parameters) must be **individually movable** (offset
from their default position) and the offsets **persist in `.csch`** (per-label offset on the component).
- **Data:** add a per-label position offset to the component model (and to `.csch` — but not `Id`, item 2).
  Default offset = current auto-position.
- **Move Labels op:** add a **"Move Labels"** item to the component **context menu**, and an **F5** shortcut.
  Behavior:
  - If components are selected when F5 / Move-Labels is invoked, **all selected components' labels** move
    together (preserving their relative positions), starting on the **next click**; the move **commits on a
    second click** (registers one undoable command). **Esc cancels** (no move).
  - If **no** component is selected, the **next click selects which component's label** to move (then the
    move proceeds as above).
  - The label position **renders in real time** as the user moves (between the first and second click).
  - **Messages panel gives instructions:** on F5 with nothing selected, post `Select component label`; when
    moving, an appropriate prompt (e.g. `Click to place labels, Esc to cancel`).
- One `MoveLabelsCommand` records the per-label offset deltas; undo restores prior offsets.

## 15. Copy/Paste: finish it (UI wiring + image/vector clipboard)
6d only implemented JSON-config copy/paste in `SchematicClipboard` and it is **not wired into the UI**, and
the **image/vector (PDF/SVG/EMF) clipboard representation is missing**. Per the corrected 6d brief
("Mine splotRF" + Step 6), implement the full clipboard, **merging** splotRF's `PlotExporter.cs` (the main
clipboard/serialization logic — config-to-clipboard via System.Text.Json) and `WindowsClipboard.cs` (the
narrow Windows-EMF helper) into **one circuitRF clipboard helper**:
- **(a) Config copy/paste — WIRE IT INTO THE UI.** Ctrl/Cmd+C / +X / +V and the Edit-menu/toolbar items must
  actually copy the selection (components, wires, canvas objects) to the clipboard as JSON and paste it
  (into the same or another schematic), pasted items selected, **undoable** (the `SchematicPasteCommand`
  exists — wire it). This is the primary path; make it work end to end.
- **(b) Rich image/vector representation** for pasting a schematic selection **into other apps**: render the
  selection to PDF/SVG (Skia, like splotRF's `PlotExporter` PDF/SVG writers — adapt from plots to schematic
  geometry) and place it on the clipboard alongside the JSON; on **Windows**, also place **EMF**
  (`CF_ENHMETAFILE`) via the merged Windows-EMF helper (no-op on macOS/Linux, where the SVG/PDF/image
  formats carry the cross-app paste). The Windows/non-Windows split lives **inside** the one helper as a
  platform branch.
- All in `src/Ui` (firewall). The JSON path is the must-have; the image/vector path is the "paste into a
  doc/slide" nicety — both are in scope here since 6d missed them.

---

## Acceptance (the group gates)
1. **(item 1)** 10k nudge/drag immediate (< ~50 ms repaint, reported); connectivity correct on commit;
   10k frame rate unchanged; connectivity pass is ~O(N) not O(N²).
2. **(items 2,7,8,9,10,11,13)** Id not exported (fresh on import); "Saved: <path>" on every save (clickable);
   type labels use the central registry display names (C/L/R/…); `.cws` actually writes a manifest (layout +
   library refs + member files); wire corners gap-free (miter, single path); all text scales with zoom;
   wire selection highlight clearly thicker.
3. **(items 3,4,5,6,12)** Esc → abort + Select; pan cursor = hand; zoom-box tool (marquee-zoom, cursor,
   Esc-reverts); rotate keeps wires connected (atomic with RotateCommand); marquee L→R window vs R→L crossing
   — **same glyph hitbox** for the component test in both, window = partially-enclosed, crossing additionally
   pulls in connected wire segments + the next component on them, rendered distinctly (solid vs dashed).
4. **(item 14)** Movable labels: per-label offset persists in `.csch`; Move-Labels via context menu + F5
   (selected-set or pick-on-click, real-time render, second-click commits undoable, Esc cancels, Messages
   prompts).
5. **(item 15)** Copy/paste wired into the UI end-to-end (JSON config, undoable) **and** image/vector
   (PDF/SVG + Windows EMF) onto the clipboard via the merged helper.
6. **6a firewall green**; `dotnet build`/`dotnet test` green; nothing in Phases 1–6c regresses; `.csch`
   round-trip still passes (now including label offsets, excluding Id).

## Guardrails
- **Performance (item 1) is the priority and the most diagnosable** — do not "tune until it feels OK"; the
  fix is structural (incremental snapshot during drag, deferred + O(N) connectivity, incremental index).
  Report the measured nudge-to-repaint time so we can confirm.
- **Every new mutation is an undoable command** (RotateCommand extended for item 6, MoveLabelsCommand for 14,
  paste for 15) — no direct model mutation, same discipline as 6d.
- **Display names live in core**, Avalonia-free (item 8); the renderer reads them — don't hardcode "C"/"L"
  in the UI renderer.
- **Don't over-reach:** each item is scoped; fix what's listed, don't refactor the canvas or the model
  wholesale. If an item reveals a deeper issue, flag it rather than expanding scope silently.
- **`Id` is never persisted** (item 2) — apply the principle, but only `.csch` is in scope to change now.
- Sub-gate the four acceptance groups; focused test filters; report and stop between groups; don't run the
  full suite into the output limit.
- Update `src/Ui/CLAUDE.md` (incremental-rebuild rule, display-name registry, Move-Labels op) and
  `project-file-formats.md` (sharper `.cws` definition, Id-not-persisted rule) as touched.

*This punch-list brings the 6d editor to "joy to use" on the basics — responsive at 10k, clean wires and
text, proper labels, working copy/paste — before 6e wires extraction + run.*
