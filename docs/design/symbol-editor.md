# circuitRF — Symbol Editor & Symbol Model Design

**Status:** Draft (rev 2) · **Date:** 2026-06-10 · **Phase:** 6f (symbol editor + standard library) + cleanup sprint (13 layers)

Specifies (1) the **symbol model** — upgrading symbols from hardcoded line-segment arrays to a real
primitive model that is data, not code; (2) the **Symbol Editor** that authors them; (3) the **standard
library** symbol set (translating the provided Core Graphics art into circuitRF's model); and (4) the
**auto-generator**. Companions: `grid-and-connectivity.md` (P/p grids — pins on P, body on p),
`project-file-formats.md` (`.csym`, `.cws`, cell model), `color-themes.md` (theme roles — symbols draw in
roles, not literal colors), `ui-design.md` §5A (symbol editor), §5B (component-type registry),
`src/Ui/CLAUDE.md` (command-pattern undo, design bar), the `frontend-design` skill.

**Owner decisions locked for this design:**
- **Pin-move reconnection = Option A (positional) for v1, with a hard commitment to migrate to Option B
  (logical pin binding).** See §6 — the Option B commitment is explicit and strong, not aspirational.
- **No color editing in the symbol editor.** Symbols draw in **theme roles** (symbol-line, symbol-text) so
  they stay correct in light/dark. **Font is editable** by style — regular / bold / italic / condensed
  (where the bundled font provides the face; graceful fallback otherwise). Stroke width and font size are
  editable. Literal per-element color is **not** offered in v1 (deferred escape hatch, §10).
- **Orientation = VERTICAL for 2-terminal elements** (R, L, C, sources, Term, Ground) per the Core Graphics
  art and common EDA convention; **horizontal (left/right ports) for multi-terminal box/device symbols** (FET,
  ZPort, Sdd, Generic, and the auto-generated box). See §4.

---

## 1. Ownership model (the scope fence — read first)

The symbol editor is deliberately **not** a cell editor. The boundaries:

- A **cell** is the unit of identity. It owns: the cell name, the **port count**, the **parameter set**
  (via the component-type registry, §5B), and **which `.csym` is its primary symbol**. A cell may have
  **multiple** `.csym` symbols; exactly **one is primary**. The **cell** decides which — not the `.csym`,
  not the schematic. (The user sets the primary symbol via Project-Tree commands in the Workspace — that
  system is not yet implemented; the symbol editor does not set primary.)
- A **`.csym` symbol** is a **dumb glyph**: a list of drawing primitives + a set of **pins**, where each pin
  maps to one of the cell's ports. It owns geometry only. It does **not** own the port count (it learns that
  from its cell), the parameters, or its own primacy.
- A **schematic instance** references a cell; it draws the cell's **primary** symbol. It does not pick a
  symbol or know symbol internals.

The symbol editor edits the **graphical representation** (the `.csym`) of all instances of a cell's glyph.
It never changes the cell's port count, parameters, or identity. This fence is what stops the symbol editor
from sprawling into a cell editor; honor it.

> **Why this matters:** "the cell knows its port count; the symbol maps pins to those ports" is what makes a
> half-finished symbol safe (§3, unmapped port = open) and what lets a cell carry several alternative glyphs.
> The symbol is downstream of the cell, always.

---

## 2. The symbol model — primitives (the core upgrade)

Today `SchematicSymbols.For(kind)` returns a flat `float[]` of line segments; circles/arcs are faked as
chords and there are no fills or curves. This **cannot** represent the standard-library art (inductor arcs,
capacitor curve, filled dot/box/triangle) and gives the editor nothing real to manipulate. Replace it with a
**primitive model**: a symbol is an ordered list of typed primitives in component-LOCAL coordinates
(100 units = 1 connection-grid square `P`).

### 2.1 Primitive types
A `SymbolPrimitive` is one of:
- **Line** — two points.
- **Polyline / Path** — an ordered point list, open (may reuse the schematic wire polyline geometry for
  hit-testing/rendering, but **free-angle**, NOT orthogonal-routed — symbol art is not Manhattan).
- **Rect** and **RoundedRect** — origin/size (+ corner radius); stroked, optionally filled.
- **Circle / Ellipse** — center + radius/radii; stroked, optionally filled.
- **Arc** — center, radius, start/sweep angle (the inductor coils).
- **Triangle / Polygon** — closed point list; stroked, optionally filled (ground triangle).
- **QuadCurve / CubicCurve** — Bézier control points (the capacitor curved plate).
- **Sine** and **HalfWave** — *parameterized smart-paths* (§7): amplitude, cycles/length, sampled to a path.
- **Text** — string, anchor point, font size, **font style** (regular/bold/italic/condensed), alignment.
- **Bitmap** — an imported raster image placed, moved, and resized freely (§2.5). Special among primitives:
  it has the **lowest Z-index** (always behind all vector primitives), an editable **opacity**, a **locked**
  flag (resists accidental click/drag), and **aspect-ratio-preserving** resize. Primarily a tracing aid for
  reference artwork; rendered in the schematic but **not recommended** (perf, §2.5).

Each drawable primitive carries: a **stroke width**, a **fill flag** (filled vs. stroked-only), and a
**color role** (NOT a literal color — see §2.3). Text carries font size + style instead of stroke width.
The Bitmap carries none of these (it is a raster, not a stroked/filled/colored vector) — it carries its image
reference, placement rect, opacity, and locked flag instead (§2.5).

### 2.2 One model, three consumers (single source of truth)
The primitive list is authored by the **symbol editor**, rendered by the **schematic renderer** (and the
editor's own canvas), and persisted to **`.csym`**. All three read the *same* primitive model — no second
representation. This kills the drift class (the §-wide circuitRF principle): there is exactly one definition
of "what this symbol looks like."
- The renderer gains a generic `DrawSymbol(primitiveList, transform, theme)` that walks primitives → Skia
  draw calls (replacing the per-`float[]` segment loop). It honors rotation/mirror/zoom as today.
- The hardcoded `SchematicSymbols.For(kind) → float[]` is **replaced** by "load the cell's primary `.csym`
  → primitive list." Built-in library symbols ship as built-in primitive lists / `.csym` resources (§8),
  not C# segment constants.

### 2.3 Color = theme role, never literal (the theming constraint)
A primitive's color is a **role** from the theme (e.g. `SymbolLine`, `SymbolText`, and the existing
`SymbolPlus` for polarity marks), resolved through `SchematicRenderTheme` at draw time. This keeps symbols
correct across light/dark themes. The editor does **not** expose an RGB picker. (If a literal-color override
is ever needed, it is an explicit per-primitive "override role with literal" — deferred, §10.) Stroke width
and font size/style are theme-independent and **are** user-editable.

### 2.4 Coordinates & the grid (ties to grid-and-connectivity.md)
- **Pin coordinates are exact `P` multiples** in local space (R2 of the grid rules). The editor snaps placed
  pins to `P`.
- **Body-art primitives snap to the fine authoring grid `p = P/20`** (or free), never constrained to `P`.
  This is exactly the two-grid split: pins on `P` (connectable), art on `p` (free-ish). The editor uses
  `SnapToGrid` for pins and `SnapToAuthorGrid` for art.
- **Bitmaps are placed and resized freely** — NOT grid-snapped (they are reference/tracing artwork, not
  electrical or aligned geometry). They are exempt from both grids (§2.5).

### 2.5 The Bitmap primitive (reference / tracing artwork)
Mirrors the Data Display's image design. A Bitmap is an imported raster the user places to **trace over** with
the vector primitives (e.g. import a datasheet symbol image, then draw lines/arcs on top). Behaviors:
- **Import** a raster image; **place freely**, **select**, **drag-move**, and **resize freely** on the canvas
  (not grid-snapped — §2.4).
- **Resize preserves aspect ratio** — scaling a bitmap never distorts it (corner-drag scales uniformly; the
  stored placement keeps the image's intrinsic aspect).
- **Opacity** is editable (so the user can dim the reference while tracing over it).
- **Locked flag** — when locked, an accidental click/drag does **not** bump its position (it must be
  explicitly unlocked to move). Independent of selection; protects the tracing reference from nudges.
- **Lowest Z-index** — a bitmap always renders **behind** every vector primitive, regardless of insertion
  order, so traced vectors sit on top of the reference. (If multiple bitmaps exist, they order among
  themselves but all stay below the vectors.)
- **Rendered in the schematic** too (a symbol with a bitmap shows the bitmap in placed instances), but this
  is **explicitly not recommended** for performance reasons. **Performance benchmarking excludes
  bitmap-bearing symbols** — the ≥30fps / 10k-component targets assume vector-only symbols; bitmaps are a
  tracing/authoring aid, not a production-art path.
- **Color/theme:** a bitmap is raster pixels — it is **not** theme-roled or recolored (it ignores §2.3); only
  its opacity is adjustable.
- **Persistence:** stored as a **path reference, not embedded bytes** (consistent with the references-not-
  payloads policy in project-file-formats.md and the Data Display's bitmap handling) — the `.csym` records
  the image file path, placement rect, opacity, and locked flag. A missing referenced file renders a
  placeholder, never a crash.

---

## 3. Pins — mapping to ports; unmapped = open

A **pin** in the symbol editor is a placed marker at an on-`P` local coordinate that is **mapped to one of
the cell's ports** (by port index / name). Rules:
- The editor shows the cell's **port count** (from the cell) as the set of ports that *can* be mapped. The
  user places a pin and assigns it to a port.
- **An unmapped port is legal.** If a port has no pin, the symbol still works: that port is treated as an
  **open circuit** in the schematic and simulation. (This makes a half-authored symbol safe and gives a
  natural non-blocking validation surface: "port 3 is unmapped → open.")
- A pin's **position** is body-authoring on `P`; its **identity** is the port mapping. (This identity-vs-
  position split is the seed of Option B, §6.)
- Pin coordinates must be on `P` (the editor enforces the snap); the lead the user draws from the body to the
  pin is ordinary art (on `p`), but the **pin tip** lands on `P`.

---

## 4. Orientation rule (vertical passives, horizontal boxes)

Per the owner decision, the standard library uses **vertical** 2-terminal elements (matching the Core
Graphics art and other EDA tools), and **horizontal** multi-terminal devices. Concretely, the **default** (rotation 0)
local pin geometry:

| Symbol | Orientation | Port local coords (rotation 0) |
|---|---|---|
| Resistor, Inductor, Capacitor | **vertical** | port 1 `(0,-200)` top, port 2 `(0,+200)` bottom |
| Voltage Source, Tone/AC Source | **vertical** | `(0,-200)` / `(0,+200)` |
| Term (S-param port) | **vertical** | 1 signal pin `(0,-200)` top |
| Ground | **vertical** | 1 pin `(0,-?)` top (stem up to pin; triangle below) |
| FET (3-term) | **horizontal** | gate `(-200,0)`, drain `(200,-100)`, source `(200,100)` (unchanged) |
| ZPort / Sdd / Generic / auto-gen box | **horizontal** | left/right ports (odd-left/even-right, §9) |

This is the conventional EDA look: signal-flow boxes have side ports; discrete 2-terminal parts stand
vertical. **Consequence to plan for:** circuitRF's current placeholders are horizontal (`SymbolPortDefs`
returns 2-terminal ports at `(±200,0)`), and the GUI demo (`SchematicModelBuilder.BuildHero2PA`) is laid out
for horizontal pins. Flipping the standard passives to vertical means:
- `SymbolPortDefs` 2-terminal entries change `(-200,0)/(200,0)` → `(0,-200)/(0,200)`.
- The GUI demo schematic must be **re-laid-out** for vertical passives (series elements rotated 90° where the
  signal path is horizontal — normal EDA workflow). The **engine** Hero fixtures (netlist-level) are
  unaffected — only the *visual* demo geometry changes. **Timing is favorable:** 6e (extraction oracle that
  draws a Hero schematic) is not built yet, so nothing downstream depends on the current horizontal layout.
- Label anchor defaults (`LabelBaseOffsetX/Y`) assume a horizontal glyph; vertical glyphs conventionally
  label to the right. Labels are movable (cosmetic), so this is a default-tuning follow-on, not a blocker.

---

## 5. The Symbol Editor (UI)

- **Hosting:** a window that can live **docked** in the Workspace (AvaloniaUI Dock) **or** as a **tear-off**
  window. Same view either way.
- **Toolbar:** its own toolbar (parallel to the schematic toolbar, different tools): the primitive tools
  (line, path, rect, rounded-rect, circle/ellipse, arc, triangle/polygon, curve, **sine**, **half-wave**,
  text, **bitmap import**), the **pin** tool, select/move, and the property controls (**stroke width**,
  **font size**, **font style** regular/bold/italic/condensed). For a selected **bitmap**: an **opacity**
  control and a **lock/unlock** toggle (§2.5). No color control (§2.3).
- **Canvas:** selectable, movable primitives (like the schematic canvas). Primitives snap to `p`; pins snap
  to `P`. Standard select/move/delete/rotate, all **undoable** through the command stack (every mutation a
  command, notifying in both Execute and Undo — the standing rule).
- **Pin mapping UI:** placing a pin prompts/assigns a port from the cell's port set; unmapped ports are shown
  as available (and flagged "open" if left unmapped).
- **Live update (§require):** any schematic showing an instance of the cell whose symbol is being edited
  **re-renders in real time** as the symbol changes — the user sees live schematic changes while editing the
  glyph. (Mechanism: the symbol model fires changed → the schematic render model rebuilds for affected
  instances, same NotifyChanged→rebuild path the editor already uses.)

---

## 6. Pin-move reconnection — Option A now, **committed migration to Option B**

**Option A (v1 — positional).** circuitRF connectivity is purely positional today (wires connect by
coordinate coincidence on `P`; there is no logical pin binding anywhere in the model). So in v1, if the user
**moves a pin** in the symbol editor, every schematic instance of that cell has its pin relocated, and wires
that were attached at the old pin location **become unconnected** (the pin moved out from under them). The
editor must **warn** before applying a pin move that affects placed instances: *"Moving this pin will
disconnect N wire(s) across M schematic(s). Continue?"* — a warning, not a silent change. (Editing a
standard-library symbol's pins is forbidden anyway — §7's lock — and a user symbol's pins are usually fixed
after first authoring, so the disconnect case is rare in practice.)

**Option B (committed target — logical pin binding).** circuitRF **will** migrate to the standard EDA model:
**pin identity is logical (port index/name), and a wire endpoint binds to (instance, port) — not to a
coordinate.** A pin's position becomes a derived attribute; moving a pin moves where bound wires attach, and
**the wires follow** (rubber-band/re-route), so a pin move **never disconnects**. This is how other schematic editors
all behave, and it is the correct end state. **This is a firm commitment, not a maybe:** the v1
positional behavior is an explicit temporary stopgap taken only because logical binding is a real addition to
the connectivity model (an optional logical anchor on wire endpoints + extractor support + follow-routing of
bound endpoints — the follow-drag mechanism already built for T-junctions is the seed). The pin
identity-vs-position split (§3) is designed now specifically so Option B is a clean later addition and not a
rewrite. **Do not design anything that assumes positional pin connection is permanent.**

---

## 7. Primitives detail — sine & half-wave; locked symbols; fonts

### 7.1 Sine & half-wave smart-paths (the owner's nice-to-have — worth it)
Build **Sine** and **HalfWave** as **parameterized** primitives, not frozen point lists: store amplitude,
cycle count (or length), and phase; render by sampling to a path (or a few cubic Béziers — a single sine
hump is almost exactly one cubic Bézier, which is how vector tools approximate it). Storing them parametric
keeps them editable (drag amplitude/cycles) and reusable — ideal for AC/tone source artwork and the improved
Voltage/Tone source. Low effort, high reuse. The half-wave is a single arc/half-sine for rectified-source or
diode-adjacent art.

### 7.2 Locked / non-user-editable symbols
Some symbols are **defined but not user-editable**: the **standard library** (R, L, C, V, Tone, Term, GND,
Port, FET, ZPort, Sdd), the **simulation-analysis** symbol (for the TestBench cell/schematic), and the
**VAR** and **Measurement** equation symbols. Users may **place** these but not edit them. Implement as a
**cell-level flag `UserEditable = false`** (consistent with §1 — the cell owns this, not the `.csym`). The
symbol editor refuses to open a locked cell's symbol for editing (or opens it read-only). The standard
library ships these defined-and-locked.

### 7.3 Fonts
Text primitives support font **style**: regular / bold / italic / condensed, plus size. "Condensed" depends
on the bundled font providing a condensed face (DejaVu / IBM Plex) — if the face is unavailable, **fall back
gracefully** to the nearest available (regular weight, normal stretch) rather than failing. No color (theme
role `SymbolText`).

---

## 8. Standard library (the art — vertical, Core-Graphics grammar)

Translate the provided Core Graphics shapes into the §2 primitive model, **vertical**, reusing their grammar
(parametric proportions, leads-in-path, single-stroke with sparing fills, classic vocabulary). Each ships as
a built-in (locked, §7.2) primitive list / `.csym` resource:

- **Resistor** — vertical 6-segment zigzag (alternating ±amplitude), leads top/bottom on the centerline.
- **Inductor** — vertical stack of 4 semicircle **arcs** (bumps to one side) + the small filled **polarity
  dot**; leads top/bottom. (Now drawable thanks to the Arc primitive.)
- **Capacitor** — flat top plate (line) + curved bottom plate (**quad curve**); short gap; leads top/bottom.
- **Voltage Source (improved)** — vertical circle, leads top/bottom, `+`/`−` marks in the `SymbolPlus` role.
  (Replaces the current crude circle-of-chords with a real **Circle** primitive.)
- **Tone / AC Source** — vertical circle with an inset **Sine** primitive; leads top/bottom.
- **Term** — the resistor zigzag inside a **RoundedRect** (a terminating impedance in a port frame), 1 signal
  pin on top.
- **Ground** — vertical stem to the top pin + a filled downward **Triangle** (Core-Graphics ground), 1 pin.
- **Port** — small circle + lead (keep simple), 1 pin.
- **FET** — horizontal (unchanged geometry), as a primitive list.
- **ZPort / Sdd / Generic** — horizontal boxes (Rect), ports left/right; ZPort keeps the inset "Z" mark.

(The concrete per-symbol primitive definitions — coordinates, proportions, pin assignments — are produced as
a separate deliverable + Sonnet brief; this section fixes the inventory, orientation, and grammar. The owner
will hand-tune some art in the symbol editor once it exists.)

---

## 9. Auto-generator (from the owner's spec)

When the user requests it, circuitRF auto-generates a symbol for a cell:
- **Outer rectangle**, with a **slightly inset inner rectangle** drawn at a **thinner** stroke width.
- **Odd port numbers down the left edge** (1, 3, 5, …), **even port numbers down the right edge** (2, 4, 6,
  …), descending. **Port-number text inside the inner rectangle**, near each port.
- For each port: a **short lead** drawn **outward** from the outer rectangle, with the **pin placed at the
  lead's end, outside** the rectangle.
- **Rectangle height grows with the port count** to accommodate the per-side ports.
- **Grid:** **pin tips on `P`** (port spacing a `P` multiple — current `GeneratePorts` uses 200 = 2 cells,
  good); the rectangles/leads/text are body art on `p`. (Honors §2.4 / grid rules by construction — R6.)
- **One-shot, then editable:** after generation the symbol is an ordinary editable `.csym`; the user freely
  edits it. It does **not** silently re-generate. Re-running is an **explicit** menu command (e.g. "Rebuild
  Symbol Automatically") that regenerates from scratch (discarding manual edits, with a warning).
- **Odd total port count:** odd-left/even-right leaves the last odd port alone on the left (unbalanced) — the
  conventional generated-box look; keep it.
- **3-port special case (pin locations):** a **3-port** symbol is laid out specifically — **port 1 on the
  left at the vertical center**, **ports 2 and 3 on the right** (the conventional 3-terminal-device look,
  matching the FET gate-left / drain+source-right arrangement). This overrides the plain odd-left/even-right
  rule **for N=3 only** (which would otherwise place 1,3 left and 2 right). All other port counts use the
  general rule. (Pin tips still on `P`, body on `p`, as above.)

---

## 10. Persistence (`.csym`) & open/deferred

### `.csym` (realizes the project-file-formats.md stub)
A `.csym` stores: format_version (reject-on-mismatch, alpha policy — no migration), the **primitive list**
(typed, local coords, stroke width / fill / color-**role** / font size+style per primitive), the **pins**
(local coord on `P` + port mapping), and the grid it was authored on (`GridSize` = its `P_src`, for the
cross-grid paste/insert check in grid-and-connectivity.md §5). `Id` never persisted. The **cell** (in the
workspace / project file) records which `.csym` is **primary** and the `UserEditable` flag — not the `.csym`.

### Open / deferred
- **Option B (logical pin binding)** — committed (§6), built after the symbol editor + 6e are stable.
- **Literal per-primitive color override** — deferred (§2.3); v1 is theme-role only.
- **Project-Tree primary-symbol commands** — the Workspace UI to set a cell's primary symbol among multiple
  `.csym` (§1) — not yet implemented; the symbol editor does not set primary.
- **Vertical-glyph label-anchor defaults** (§4) — tune label default positions for vertical symbols; cosmetic
  follow-on (labels are movable).
- **SVG import** for symbols (ui-design.md §5A) — deferred; the primitive model is the import target when
  built.

---

## 11. Implementation order (smallest correct first)

1. **Symbol primitive model** (§2) — the typed primitive list + generic `DrawSymbol`, replacing the `float[]`
   path. Render the **existing** (placeholder) symbols through it first to prove parity, before changing art.
2. **`.csym` persistence** (§10) — read/write the primitive model; built-in library as resources.
3. **Standard library art** (§8) — translate the Core Graphics shapes, **vertical**, into primitives; flip
   `SymbolPortDefs` 2-terminal entries to vertical; re-lay-out the GUI demo (§4 consequence). ✅ **Done (Phase 6f step 3).**
   R/L/C/V/Tone/Port/GND are vertical (pins at `(0,∓200)`); FET/ZPort/Sdd/Generic unchanged (horizontal).
4. **Symbol editor canvas + tools + pins** (§5) — primitives, pin mapping, stroke/font controls, undo;
   docked + tear-off; **live schematic update**.
   - **4a done (Phase 6f).** `EditableSymbol` (mutable working copy) + `SymbolGeometry.BboxOf/HitTest/
     TranslateBy` (framework-free); `SymbolEditorViewModel` (Select tool, selection, move-drag, delete,
     rubber-band, per-document `UndoRedoStack`); `SymbolEditorCanvas` (Skia control, pan/zoom, fine-grid `p=5`,
     selection overlay, reuses `SchematicRenderer.DrawSymbol`); `SymbolEditorDocument` + `SymbolEditorView`
     + `SymbolEditorWindow` (dockable + tear-off); `MoveSymbolPrimitivesCommand` + `DeleteSymbolPrimitivesCommand`
     (both directions notify). Open via View → "Open Symbol Editor (docked/window)".
   - **4b done (Phase 6f).** Drawing tools: `PlaceSymbolPrimitiveCommand` (append + undo-remove, notify
     both); `Tool` enum extended with all 14 drawing tools; `CurrentColorRole/StrokeTier/FontSize/FontStyle`
     properties; two-point drag gestures (Line/Rect/RoundedRect/Circle/Ellipse/Arc/Sine/HalfWave),
     multi-point click gestures (Polyline/Polygon/Triangle/QuadCurve/CubicCurve), click-then-type text
     gesture; `InProgressPrimitive` on `SymbolEditorOverlay`; ghost-dashed preview via `DrawSymbol` with
     `overridePaint`; tool cursors (crosshair for drawing tools, arrow for Select); `TextPrimitive` real
     rendering in `SchematicRenderer.DrawSymbol` (IBM Plex Sans, align, font-style); toolbar: 14 tool
     buttons (data-driven `Classes.ToolActive` via `EnumEqualsToBoolConverter` + `SetActiveToolCommand`),
     stroke-tier Normal/Thin buttons, font-size `NumericUpDown`, font-style `ComboBox`; all placements
     undoable; art snaps to `p = 5`; no RGB picker (color is `SymbolColorRole` role only).
   - **4c done (Phase 6f).** Pin tool, `.csym` I/O, locked gate:
     - `PortCount` on `Symbol` + `EditableSymbol` + `CsymFile` (backward-compat: 0 → inferred from pins.Count).
     - Four pin commands: `PlaceSymbolPinCommand`, `MoveSymbolPinCommand`, `DeleteSymbolPinCommand`,
       `RemapSymbolPinCommand` — all undoable, both directions notify.
     - `SymbolEditorViewModel.Tool.Pin` — click to place (snapped to P=100, not fine p=5); click existing
       pin to select + drag; `RemapSymbolPinCommand` fires on `SelectedPinPortIndex` change.
     - `SymbolEditorOverlay` extended: `SelectedPinIndex`, `PinLiveDragOffset`, `UnmappedPortIndices`.
     - `SymbolEditorRenderer.DrawPinMarkers` — filled circles at pin locations, port labels, selected
       highlight, live drag ghost (dashed); `DrawUnmappedPortPanel` — soft-yellow info panel (non-blocking).
     - `.csym` open via File → "Open Symbol…" (Workspace window) → `SymbolPersistence.LoadFromFile` →
       `EditableSymbol.FromSymbol`; save/save-as via `SaveSymbolCommand`/`SaveSymbolAsCommand` in the VM.
     - Locked gate: built-in symbols (`OpenSymbolEditorDocked/Window`) marked `UserEditable = false` →
       `SymbolEditorViewModel.IsLocked = true` → all mutation paths gated; edit tools disabled in toolbar;
       cross cursor reverts to arrow; "Read-only" indicator shown in metadata bar.
     - Metadata bar in `SymbolEditorView`: dirty indicator (●), file path, port count plain integer `TextBox` (no spinner; digit-only; empty → 0; two-way sync via code-behind), Save/Save-As buttons.
     - **Hard deferred at 4c time; now resolved:** live schematic update and cell-ref rendering
       delivered in Phase 6g Step 5 (`CellSymbolResolver`, `CellRefState` render dispatch,
       `SymbolSaved` event + `TriggerRebuild`). Still deferred: cell-driven double-click open,
       rewiring `SymbolKind → BuiltInSymbols` to the `.ccell` loader (§6 migration).
5. **Sine / half-wave smart-paths** (§7.1), **font styles** (§7.3), and the **Bitmap** primitive (§2.5 —
   import/place/resize-with-aspect-lock/opacity/locked-flag/lowest-Z; path-reference persistence).
6. **Locked-symbol flag** (§7.2) — cell-level `UserEditable`; library/sim/VAR/Measurement locked.
7. **Auto-generator** (§9) — generate + explicit Rebuild; include the **3-port special case** (port 1 left-
   center, ports 2 & 3 right).
8. **Option A pin-move warning** (§6) — the disconnect warning; (Option B is a later phase).

Steps 1–3 give correct, richer standard-library art on a real model; 4–5 give the editor; 6–7 the lock and
auto-gen; 8 the v1 pin-move safety. Each lands behind the firewall (model framework-free; SkiaSharp only in
the renderer below `src/Ui`).

---

## 12. Cleanup sprint — 13 layers (rev 2, 2026-06-10)

Post-6f cleanup hardening all editor subsystems to match the schematic editor's quality bar.

| Layer | Area | What changed |
|-------|------|-------------|
| 1 | Live-drag invalidate | `InvalidateVisual()` called on every pointer-move in drag and pin-drag paths (was deferred until release) |
| 2 | Hit-test accuracy | Ellipse hit-test replaced with concentric-ellipses formula; arc/circle unchanged |
| 3 | Undo target routing | Instrumented — already correct; no code change |
| 4 | Pin-drag invalidate | Instrumented — already correct via Layer 1 fix |
| 5 | Snap toggle | `GridSnap` bool property; G key; ToggleButton in toolbar; `SnapToP` respects toggle; pins always on P=100 |
| 6 | Rotate | `RotateSelectionCommand` + `RotateBy90About` in `SymbolGeometry`; R key + toolbar button; rotates 90° CW about bbox CENTER (matches schematic in-place rotation); center is rotation-invariant so N×90°=identity; pins rotate with full precision (no per-step snap) — grid-aligned selections keep pins on P=100 with no drift; non-grid-aligned pins may snap on rotate (Undo reverses exactly) |
| 7 | Text bbox | `BboxOf(TextPrimitive)` now font-size + length aware; accounts for `Align` |
| 8 | Wire-style selection highlight | Re-strokes actual primitive geometry with semi-transparent accent paint; matches schematic editor selection style |
| 9 | Font toolbar | Removed redundant Font-size NumericUpDown and Font-style ComboBox from toolbar (moved to inspector pane) |
| 10 | `OnDockableClosed` | Fixed stale `_openDocsByPath` entries for symbol editor tabs (used `ReferenceEquals` scan) |
| 11a | Inspector routing | `PropertiesTool.SetActiveSymbolEditor`; `WorkspaceViewModel` routes on tab switch; `SymbolPrimitiveInspectorViewModel` created |
| 11b | Inspector position | X/Y editable fields commit via `MoveSymbolPrimitivesCommand` |
| 11c | Inspector polyline | Read-only coord list for `PolylinePrimitive` / `PolygonPrimitive` |
| 11d | Inspector text | Content/FontSize/FontStyle editable via `SetTextPrimitiveCommand` |
| 12 | Resize gripper | Bottom-right handle; Shift=aspect lock; `ResizeSymbolPrimitiveCommand` (undoable); rendered as small accent square |
| 13 | Save-As folder | `SaveSymbolAsAsync` now seeds the file picker to the current file's directory |

### Cleanup sprint — round 2 (rev 3, 2026-06-11)

| Layer | Area | What changed |
|-------|------|-------------|
| 5 | Port-count box | Replaced `NumericUpDown` with plain `TextBox` (`PortCountBox`); digit-only input via tunnel `TextInputEvent`; paste stripped in `TextChanged`; empty → 0; two-way sync via code-behind (`_portCountVm` + `_portCountChanging` guard); no spinners |
| 6 | Toolbar + selection highlight | Toolbar `ScrollViewer` `HorizontalScrollBarVisibility` changed from `Auto` → `Disabled` (no scrollbar at any width); selection outline stroke (layer 8 paint) confirmed matching schematic wire-select style (`SelectionBox.WithAlpha(120)`, `StrokeJoin.Round`, `StrokeCap.Round`, `DrawSymbol` reuse); Text keeps correct bbox box |

### Key invariants reinforced
- Pins snap exclusively to P=100 (`SnapToConnectionGrid`); art grid toggle never affects pin coordinates.
- Every mutation is undoable on the document's own `UndoRedoStack`; `NotifyChanged()` in both Execute and Undo.
- `SymbolGeometry` and `SymbolPrimitiveInspectorViewModel` are framework-free (no Avalonia types).
- `DrawSymbol` is shared — selection highlight re-uses `SchematicRenderer.DrawSymbol` with `overridePaint`.
- Firewall green — no Avalonia reference in Core, Engine, Cli, or RfCore.

### Pin pick tolerance and gesture model (PinSelectCorrectFix — done)
**Root cause confirmed by instrumentation:** pins snap to P=100 on placement. A click that placed a pin can
land up to ~70 world units (diagonal) from the resulting grid point, so the old 12-px-only tolerance (~17
world units at default zoom) was unreachable from the pin's own placement click.

**`HitTestPin` tolerance:**
```
double tol = Math.Max(12.0 / Math.Max(CanvasZoom, 1e-6), PinGrid * 0.5);
```
- `PinGrid * 0.5` = 50 world units — covers a full half-cell in every direction, which is exactly the
  worst-case distance from a placement click to the snapped pin position.
- `12.0 / CanvasZoom` ensures the pick radius is never smaller than 12 screen-pixels when zoomed in.
- Because pins are always ≥ `PinGrid` apart, a `PinGrid/2` radius is unambiguous: at most one pin's
  half-cell contains any click. **Use `PinGrid * 0.5`, never a literal `50`** — `PinGrid` is the
  single named constant; if P changes the floor scales automatically.

**Tool ownership (PinToolOwnership — done):**
- **Pin tool = place only.** `PinToolPress` has two gestures: click empty space → place a new pin
  at `SnapToConnectionGrid(lx/ly)`, becomes selected; click an existing pin → select it (inspector
  shows it), no drag. Pin tool never initiates a drag.
- **Select tool owns pin manipulation.** `SelectToolPress` hit-tests pins *before* primitives. A pin
  hit sets `_selectedPinIndex`, `_isPinDragging = true`, and grabs `_pinGrabX/_pinGrabY`. Drag lives
  in the Select branch of `OnPointerMoved`; commit lives in the Select branch of `OnPointerReleased`.
  Clicking a non-pin clears `_selectedPinIndex` so primitive selection deselects any pin.
- **Delta-from-grab math (correct):** `destX = SnapToConnectionGrid(_pinOrigX + (lx − _pinGrabX))`;
  `_pinLiveDx = destX − _pinOrigX`. Never snap the absolute cursor position.
- **Delete (Del/Backspace) and R (rotate 90° CW)** fire whenever `_selectedPinIndex.HasValue`,
  regardless of whether the active tool is Pin or Select.
- **v1:** pin selection is independent of primitive selection; no mixed multi-select.

### Pin selection feedback (PinSelectFeedbackFix — done)

**Root cause confirmed:** selection was working all along (transcript: 2nd click → HIT i=0 →
selIdx=0). The problem was invisible feedback — a selected pin looked identical to an unselected
one, and the inspector pin fields were uneditable-looking.

**Selection ring (`SymbolEditorRenderer.DrawPinMarkers`):**
For the selected pin (`i == selIdx`), two additional circles are drawn before the pin dot:
- **Halo fill** — `theme.SelectionBox` at 30-alpha, radius `r + 6f` screen-px.
- **Ring stroke** — `theme.SelectionBox`, 2 px stroke, same radius.
The ring is drawn behind the dot so the dot sits inside the halo. All unselected pins remain unchanged.
Ring tracks the live drag offset via the same `drawX/drawY` calculation as the dot.

**Inspector Pin panel (editable — `SymbolPrimitiveInspectorView.axaml`):**
Pin X / Y and Port are already bound TwoWay to `PinX`, `PinY`, `PinPortIndex` in
`SymbolPrimitiveInspectorViewModel`, which commit through `MoveSymbolPinCommand` (P-snapped) and
`RemapSymbolPinCommand`. Port field uses `ShowButtonSpinner="False"` (integer box, HIG: no spinner).

**Instrumentation removed:** all `[PIN]` `Console.Error.WriteLine` lines removed from
`SymbolEditorRenderer`, `SymbolEditorViewModel`, and `SymbolEditorCanvas`.

### Cleanup sprint — round 3, Layer 1: Symbol clipboard (done)

**Root cause:** `WorkspaceViewModel.Copy/Cut/Paste` were empty stubs. `SymbolEditorCanvas` had no
clipboard events. No `SymbolClipboard` existed. Copy/cut/paste never worked.

**Implementation:**
- **`SymbolClipboard`** (`src/Ui/Clipboard/SymbolClipboard.cs`) — `CopyAsync`/`PasteAsync` helpers.
  Format: JSON text via `SymbolPrimitive` polymorphic serialization + `CsymPin` list, guarded by the
  `"circuitrf/symbol-clipboard-v1"` marker (foreign text silently ignored). Paste offsets primitives
  by one P=100 grid cell (via `SymbolGeometry.TranslateBy`); pins are P-snapped after offset.
  Image formats (PDF/SVG/PNG) **not shipped in v1** — JSON-only; see `SchematicClipboard` for that pattern.
- **`PasteSymbolSelectionCommand`** (`src/Ui/Commands/Symbol/`) — undoable; appends primitives/pins,
  calls `onPasted(primIndices, pinIndices)` callback so the canvas selection updates immediately.
- **`SymbolEditorCanvas`** — three `ClipboardCopyRequested/CutRequested/PasteRequested` events raised
  from `OnKeyDown` on Ctrl/Cmd+C/X/V (mirrors `SchematicCanvas`).
- **`SymbolEditorView.axaml.cs`** — wires the three events to async handlers that call
  `doc.ViewModel.ClipboardCopyAsync/PasteAsync(clipboard)` (mirrors `SchematicView`).
- **`SymbolEditorViewModel`** — `ClipboardCopyAsync(IClipboard, cut=false)` and `ClipboardPasteAsync(IClipboard)`
  public methods; reads `_selection` / `_selectedPins`, delegates to `SymbolClipboard`.
  Cut = copy then `DeleteSymbolPrimitivesCommand` + `DeleteMultipleSymbolPinsCommand`.
- **`SchematicViewModel`** — `ClipboardCopyAsync` / `ClipboardPasteAsync` public methods, factoring out
  the copy/paste logic that was private in `SchematicView.axaml.cs` (so Edit menu works for schematics too).
- **`WorkspaceViewModel`** — `Copy`/`Cut`/`Paste` commands changed from empty `void` stubs to `async Task`
  methods that route to the active document (SymbolEditorDocument or SchematicDocument) via the new VM
  methods. `GetClipboard()` obtains `IClipboard` via `ResolveOwner` + `TopLevel.GetTopLevel`.

**Key invariants:**
- Clipboard format guarded by marker — foreign text is silently ignored.
- Paste is undoable via `PasteSymbolSelectionCommand` (one undo removes all pasted items).
- Cut = copy then delete (existing delete commands, already undoable).
- Pasted items are immediately selected (onPasted callback).
- Pins always P-snapped after offset.
- Works via Ctrl/Cmd+C/X/V on canvas AND via Edit menu.

**Gate:** build green, 1076 tests pass, firewall clean.
