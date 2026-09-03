# circuitRF — UI Design

**Status:** Draft (rev 2) for review · **Date:** 2026-06-06 · **Phase:** 6a (rev 2 adds §3.1 canvas objects, §5A symbol editor, §5B component-type registry)

This document defines how the circuitRF GUI works: the interaction model, the window/region structure, the
two canvases, the schematic editor, the schematic→engine bridge, the data display, and the workflows. It is
the authoritative interaction spec for Phases 6b–6g; the companion `ui-architecture.md` defines the
layering/firewall, and `src/Ui/CLAUDE.md` holds the standing UI rules (architectural constraints + the
design-quality bar). Where this and those disagree, flag it.

**Guiding intent (owner):** the GUI must be *a joy to use* — responsive, familiar to RF engineers, clean by
default with advanced settings reachable but not cluttering. A bad GUI would sink an otherwise strong engine.

---

## 1. Overview — the Workspace model

A **Workspace** is a window. It represents a user's working context and holds:
- the project's **TestBenches** and **schematics**,
- **cells** (each with a symbol view, a schematic view, and — future — a layout view),
- references to external user **Libraries** (each with their own cells),
- **data-display configurations** (plot/table layouts over result `DataSet`s).

A user may have several Workspace windows open at once (e.g. `File → Open` an existing workspace into a new
window; `File → New Workspace` for an empty one). The Workspace is the top-level unit the user saves and
reopens.

The GUI **never simulates directly**. It builds and edits the *design layer* (cells, schematics, nets,
parameters), then asks the engine to elaborate and run; results come back as a **`DataSet`** of `DataCube`s
(the one result currency, Phase 5). This separation is load-bearing — see `ui-architecture.md`.

---

## 2. Regions & docking

A Workspace window is divided into dockable **regions**. **All region management uses the AvaloniaUI `Dock`
library** — resize, rearrange, tear-out-to-window, re-dock, minimize — rather than a hand-rolled docking
system. The four default regions:

| Region | Default position | Default size | Purpose |
|---|---|---|---|
| **Project Tree** | top-left | ~10% width | nodal tree of libraries, cells, data-display configs |
| **Properties** | left, below the tree | tree width | hosts the **Component Palette** first (see §2.2); the region is named generically because it will grow other property/inspector uses |
| **Content** | center | most of the area | a **tab view** of schematic / symbol-editor / data-display views |
| **Messages** | bottom | short, full width | engine messages: color+icon coded, clickable file links (§8) |

**Dock behaviors (all via Dock):** each region is resizable by dragging its edge handles; rearrangeable by
dragging its title (with live visual feedback as the layout adapts); **poppable** out to its own window by
dragging it out and releasing; **minimizable** to give an adjacent region more area; re-dockable by dragging
a popped window back. Small scroll bars appear within regions as needed.

### 2.0 Dock mapping (how the regions use AvaloniaUI `Dock`)
The regions map onto Dock's dockable layout primitives as follows:
- **Content** = a Dock **DocumentDock** (the tabbed document area). Schematic / symbol-editor / data-display
  views are **Documents**; Dock provides their tab strip, tear-out, and re-dock natively.
- **Project Tree**, **Properties**, **Messages** = Dock **ToolDocks** (dockable tool panels) anchored
  left/left-below/bottom respectively. Tools get Dock's pin/float/close/resize and drag-to-rearrange.
- The Workspace window's root is a Dock **RootDock** containing a **ProportionalDock** (the splitter layout)
  that arranges the left tool column (Tree over Properties) | the Content document area, with Messages as a
  bottom tool dock. Proportional splitters give the user-draggable resize.
- **Layout persistence:** Dock serializes its layout tree; the Workspace **saves the serialized Dock layout**
  (region sizes, arrangement, floating windows) as part of the workspace file, and restores it on reopen. A
  **"Reset Layout"** View-menu command restores the default arrangement (the table below).
- **Default arrangement** (what a fresh Workspace opens with):

| Region | Dock type | Anchor | Default size |
|---|---|---|---|
| **Project Tree** | ToolDock | left, top | ~10% width, upper portion |
| **Properties** (palette first) | ToolDock | left, below tree | tree width, lower portion |
| **Content** | DocumentDock | center | remaining area (most of the window) |
| **Messages** | ToolDock | bottom | full width, short height |

Do not hand-roll any of the docking, tear-out, re-dock, float, or resize behavior — it is all Dock. circuitRF
supplies the *contents* of each dock and the default layout; Dock supplies the mechanics.

### 2.1 Project Tree
A nodal tree (there may be many libraries/cells/configs). Right-click an item for operations: **Open**, **Open
in New Window**, **Delete**, and context-appropriate others. Double-click opens the item in the Content
region (a schematic/symbol/data-display tab).

#### 2.1.1 Dropping onto a Project Tree — including from another workspace window (MW3)

The tree is one drop surface with one pair of handlers; `TreeDrop` (`src/Ui/Schematic/TreeDropIntent.cs`)
holds the rule, and both drag-over and drop ask it, so the effect the cursor promises and the thing that
happens cannot drift apart. Four outcomes:

- **A cell dragged from ANOTHER workspace's tree** — the receiving window comes forward and asks whether to
  **copy the cell in** or to **reference it where it is**. Under Copy, a nested choice says whether the
  sub-cells are copied too or kept referenced in their own workspace; under Reference it is disabled,
  because a referenced cell's sub-cells are *always* by reference and there is no third combination. The
  last choice is pre-selected for the rest of the session and deliberately not remembered across launches.
  **Reference is unavailable when the two workspaces resolve to different technologies** — a layout's whole
  instance hierarchy is drawn with one layer table — and the dialog shows that refusal rather than letting
  the mode be chosen and then fail. A **copy is a file operation and is not undoable**; a copied cell that
  places parts from a kit the receiving workspace has not imported is warned about *before* the copy, since
  a `pdk://` reference is not a path and is not rewritten.
- **A cell dragged within its own workspace's tree** does nothing, exactly as it did before this existed.
- **A loose file dragged from another tree** is copied into the folder it was dropped on (or the workspace
  root), with a name prompt on collision. There is no Reference option for a file: a `.s2p`, `.npy` or
  `.ctech` has no reference semantics in a `.cws`.
- **A `.cws`** opens that workspace in a window of its own and is never copied. Everything else in an
  ordinary OS file drop is bookmarked as a Known File, as before.

`File ▸ Add Cell to Workspace…` is the same gesture reached by keyboard: it picks a cell folder and shows
the identical prompt, for when the other project is not on screen to drag from.

### 2.2 Component Palette (first occupant of the Properties region)
A ComboBox selects a standard component library (lumped components, sources, simulation directives, …); the
palette below shows that library's components as **graphical depictions**. The user **drags components from
the palette into a schematic** (§4). The region is named **"Properties"** (not "Palette") because it is
expected to grow other inspector/property roles later; for v1 it shows the palette.

### 2.3 Content region
Holds **only a tab view**; tab items are schematic views, symbol-editor views, or data-display views. A tab
**tears out** to its own window by dragging it out; a torn-out window **re-docks** as a new tab by dragging it
back (Dock document semantics).

---

## 3. The two canvases

circuitRF has **two distinct canvases** that share the same underlying machinery but render different things:

1. **The schematic canvas** (§4) — renders symbols, wires, nets, grid; the editing surface.
2. **The data-display canvas** (§6) — renders plots (rectangular / Smith / polar / **contour**) and tables;
   the placeable-plot surface.

**Shared machinery (build once, use for both):** the **three-coordinate-space transform pipeline**
(data/world → fractional viewport → Skia pixels), pan/zoom, hit-testing via a **spatial index**, and the
**SkiaSharp custom-control rendering** with viewport virtualization. This is exactly splotRF's proven
approach (`PlotRenderer` transforms, `PlotControl` interaction, `Axes` viewport) — **mined as reference and
re-implemented cleanly** (see `ui-architecture.md` §display; splotRF is reference, not a dependency). Keep the
Skia *rendering* separable from the Avalonia *control hosting* (the firewall + future re-skin).

**Performance target (testable):** smooth (**≥ 30 fps**) pan/zoom on a **10,000-component** schematic on a
mid-range laptop, verified with a generated stress schematic in `testdata/` — not a toy. The schematic canvas
**must not** render components as individual styled controls (it would die at 10k); it renders itself via the
custom control with viewport virtualization + spatial-index hit-testing (`src/Ui/CLAUDE.md`). *(Achieved in
Phase 6c: >120 fps at 10k components.)*

### 3.1 Canvas objects (non-electrical decorations) — one shared family
Beyond the electrical content (symbols, wires) and the data content (plots, tables), every canvas supports a
family of **canvas objects**: non-electrical, placeable decorations. **Bitmaps, text boxes, shape primitives,
and plots-placed-on-a-schematic are ONE concept** — they share an identical interaction/lifecycle contract
and differ only in what they render and a few per-type properties. Build the shared abstraction once; the
per-type objects are thin specializations. (This mirrors how splotRF already treats plots/MarkerInfoBoxes as
selectable placeable objects — the owner's framing: "selectable, just like a plot.")

**The shared canvas-object contract** (every object below has all of this):
- **Selectable** (click; part of rubber-band/multi-select), **drag-move**, **resize**, **rotate** (by the
  user), adjustable **transparency**.
- **Cut / Copy / Paste** (system clipboard, §10) and **undo/redo** (command pattern, §10) like any edit.
- **Render size respects the canvas zoom level** (an object scales with pan/zoom like everything else).
- **Z-order** in the canvas stack (below). **Lock / Unlock** state via context menu: when **locked**, the
  object cannot be selected, moved, or resized; changing Unlock→Lock **deselects** it.
- **Inserted** via the **Insert** menu (`Insert → Image…`, `Insert → Text`, `Insert → Shape…`, plots via the
  data-display/Insert path) and via the context toolbar where applicable.
- A **context menu** exposes the per-type properties (transparency, relative size presets, lock/unlock, etc.).

**Which canvases host which objects:**
| Object | Schematic canvas | Data-display canvas | Symbol-editor canvas |
|---|---|---|---|
| **Bitmap** (§3.1.1) | ✓ | ✓ | — |
| **Text** (§3.1.2) | ✓ | ✓ | ✓ |
| **Primitive** rect/circle/line (§3.1.3) | ✓ | ✓ | ✓ |
| **Plot** + MarkerInfoBox (§3.1.4) | ✓ | ✓ (native home) | — |

**Z-order (the canvas stack, bottom → top):** grid → **bitmaps** → **plots** / **text** / **primitives**
(decoration layer) → **schematic components + wires** (top). Decorations sit *above the grid but below the
electrical content*, so a background image or a callout never obscures the circuit. (Within the decoration
layer, normal per-object Z-ordering applies; the rule is that decorations as a group are below components.)

#### 3.1.1 Bitmaps
A placed raster image, **purely cosmetic** (no electrical/symbolic meaning). In addition to the shared
contract: **aspect-ratio-locked resize** via a gripper in the **bottom-left corner**; a context menu for
**transparency**, **relative size** presets (50% / 75% / 100% / 200% / …), **lock/unlock**, **Locate…** (a
System file dialog to re-point a missing/moved image), and **Refresh** (reload the image from disk at its
current path); **rotate**. **Persistence: only the image file *path* is saved** in the schematic/data-display
config — not the pixels (relative path preferred/default, absolute allowed — `project-file-formats.md`).
If the image can't be loaded from that path on open, render a **placeholder box** marking where an image
should be (use **Locate…** to re-point it). Inserted via `Insert → Image…`.

#### 3.1.2 Text objects
A placed text box. **Inline-editable** (same interaction as editing a schematic symbol's parameter, §4.5);
the text **wraps** within the box; box is selectable/movable/resizable/rotatable (shared contract).
Per-type properties: **Font, Font Size, Font Weight (regular/bold/italic/…), Color, transparency**. Inserted
via `Insert → Text`. Available on the schematic, data-display, AND symbol-editor canvases.

#### 3.1.3 Shape primitives (rectangle, circle, line)
Placed vector primitives, selectable/movable/resizable/rotatable (shared contract). Per-type properties:
**line width, color, transparency**; the **line** primitive adds **arrowheads** (none / start / end / both)
with an **arrowhead-size** property, and has **two control points** (one at each end) the user drags to set
its orientation/length. Inserted via `Insert → Shape…`. Available on schematic, data-display, AND
symbol-editor canvases.

#### 3.1.4 Plots on any canvas (no schematic/data-display distinction)
In circuitRF a **plot object** (and its associated **MarkerInfoBox**) is a canvas object that can be placed
on the **schematic canvas as well as** the data-display canvas — *there is no distinction*. A plot on a
schematic **behaves exactly** as one on a data-display canvas (select/move/resize/rotate, full plot
interaction, §6). Z-order: a plot sits in the decoration layer (above grid, below schematic components), same
as the other canvas objects. (The plot's own rendering/behavior is §6; §3.1 only states that it is a
placeable canvas object usable on either canvas.)

---

## 4. Schematic editor

The schematic canvas may hold thousands of component primitives or cell instances, so responsiveness (§3) is
a first-class constraint, not an afterthought.

### 4.1 Navigation & grid
- **Pan** and **zoom** (zoom centered on cursor; pan by drag — exact button mapping in §4.6).
- A **subtle background grid**; the user sets grid resolution and may require component **ports to be
  on-grid** (snap) via a flag.

### 4.2 Placing & moving
- **Drag a component from the Palette** (§2.2) into the schematic to place it; **drag placed components/cell
  symbols** to move them.
- **Drag-release-over-wires auto-connects:** when a component is released so its ports overlap wires, the
  ports connect to those wires at the overlap location.

### 4.3 Connections & visual state
- A **connected** point (wire-wire or port-wire) renders as a **dark square** at the connection.
- An **unconnected port** renders with a **subtle red box** outline; on connection the red box becomes the
  standard dark square.
- **Wire routing is simple orthogonal for v1** — snap + auto-connect + basic orthogonal segments. **Beautiful
  dynamic obstacle-aware auto-routing is explicitly DEFERRED** (a research item, possibly never fully solved;
  do not attempt in v1). The router is kept separable and headless-testable (`src/Ui/CLAUDE.md`).

### 4.4 Nets & naming
See §5 for the extraction details. In the editor: the user may **explicitly name a net** by placing a **net
label** on a wire; labeled nets are the names the engine and measurements address. Unlabeled nets receive
deterministic auto-generated names at extraction (§5).

### 4.5 Parameters on the schematic
- Most components display a few editable parameters on the schematic (e.g. an inductor shows instance name +
  inductance with units). **Click** the parameter text → it becomes an **inline editable text box**. A
  parameter line renders as `<Name> = <Expression> <Unit>` (e.g. `L = 2.5 nH`); double-clicking it opens the
  inline editor on the `<Expression> <Unit>` portion (the name is fixed). Which parameters a freshly-placed
  component has, and which show by default, comes from the **component-type registry's default-parameter
  template** (§5B) — e.g. a tone source defaults to two shown parameters (`V`, `Freq`).
- A **per-parameter "show on schematic" flag** controls which parameters are displayed (keeps the schematic
  clean); not all parameters show by default.
- The **type label** (the component's type, e.g. `C`/`L`/`R`) is also editable inline; typing a **short type
  code** (§5B / §7.2) changes the component's type. (Editing the type label vs a parameter line is keyed off
  which label was clicked — they are different edits.)
- **Double-click** a component (or cell) → a **parameter dialog/popover** listing *all* parameters, editable.
  The dialog always has a **Help** button → opens a local HTML doc for that component (placeholder HTML for
  now; real docs later).

### 4.6 Selection & keyboard grammar (standard EDA conventions)
Mirrors the idioms RF engineers know from other EDA tools so the editor feels native:
- **Click** selects; **Shift+Click** adds/removes from selection; **rubber-band drag** (on empty canvas)
  multi-selects.
- **Esc** cancels the current operation (placement, wiring, drag) and clears selection.
- **Delete / Backspace** deletes the selection.
- **Arrow keys** nudge the selection by one grid step; **Shift+Arrow** nudges by a coarse step.
- **R** rotates the selection (90° CCW; **Shift+R** CW); **M** mirrors (horizontal; **Shift+M** vertical) —
  active both during placement and on a selected object.
- **Ctrl/Cmd+C / +X / +V** copy / cut / paste (§9); **Ctrl/Cmd+Z / +Shift+Z** undo / redo (§9).
- **Ctrl/Cmd+A** select all; **F** or scroll-to-fit zooms to the design extent.
- (Platform modifier: Cmd on macOS, Ctrl on Windows/Linux — Avalonia's platform-aware gesture handling.)

### 4.7 Save / restore
Component placement, wire placement, net labels, junction dots, disable states, canvas objects, zoom level,
and (for a torn-out schematic window) window placement are tracked and **saved to disk at the user's will**,
restoring the visual state on reopen. This is the **`.csch`** (circuitRF schematic) format — the schematic's
visual/geometry layer. **`.cnl` stays netlist-only** (no placement/geometry); the engine-facing netlist is
*derived* from the `.csch` by net extraction (§5). See `project-file-formats.md` for the full format family
(`.cnl` netlist, `.csch` schematic, `.csym` symbol, `.cdd` data-display, `.cws` workspace, and how Library/
Cell/TestBench map to folders).

---

## 5. Net extraction — the schematic→engine bridge

This is the critical seam that turns a drawn schematic into something the engine runs. It is **core logic,
not view logic** — headless and unit-testable, behind the firewall (no UI dependency).

**Flow:** placed symbols (with port geometry) + wires + net labels → **connectivity extraction** (which ports
and wire endpoints are electrically common) → **nets** → the **design model** the engine already elaborates →
engine run → **`DataSet`** back to the data display.

**Net naming scheme:**
- At extraction, **every net gets a deterministic, stable auto-generated name** (stable across re-extraction
  so results/measurements don't churn).
- A user **promotes** a net by placing a **net label** (§4.4) on it; labeled nets are addressed by that name
  in the engine, in measurements (`HB1.V(X1.drain)`), and in the design model.
- Unlabeled nets keep their auto-names (a user cannot measure a net they never named — consistent with the
  node-retention policy, `src/Core/Data/CLAUDE.md`).

**Connectivity rules:** ports overlapping a wire (or a wire-wire crossing with a junction dot) are common;
the extractor resolves the equivalence classes into nets. Hierarchical instances expose their cell's
interface ports as connection points at this level (the interior is a separate schematic — §4 hierarchy).

This is what realizes the PRD §12 click budget (placed FET → running HB power sweep in ≤ 8 actions): place,
wire, drop a source + an HB directive, label the ports you'll measure, run.

### 5.1 The extraction algorithm
Connectivity extraction is a **union-find over geometry**, run headless (no UI types):
1. **Collect connection points:** every component/cell-instance **port** (at its placed, possibly
   rotated/mirrored coordinate) and every **wire endpoint** and **wire-wire junction** (a junction exists
   where wires meet *and* a junction dot is present — a crossing without a dot is not a connection).
2. **Union by coincidence:** points that are geometrically coincident (within a small tolerance, snapped to
   the grid when on-grid snapping is on) are unioned. A port lying on a wire segment (not just its endpoint)
   unions with that wire (the drag-release-over-wire connect, §4.2).
3. **Resolve nets:** each union-find equivalence class is one **net**. Net labels (§4.4) placed on any member
   of a class give that net its user-facing name; multiple labels on one class that disagree → a **user
   error** surfaced in Messages (§8), not a silent pick.
4. **Name:** labeled class → the label name; unlabeled class → a deterministic auto-name derived from a
   stable ordering of its members (so re-extraction yields the same name and results/measurements don't
   churn).
5. **Emit the design model:** components/instances become design-model elements with their parameters; nets
   become the connectivity; the result is exactly the design model the engine **already elaborates** (root
   `CLAUDE.md` / elaboration) — extraction produces the *same* model an authored `.cnl` produces, so the
   engine path downstream is unchanged. **Port-index caution:** emit each component's connected nets **in the
   symbol's terminal order** (symbol terminals are **1-based**, user-facing; the `.cnl`/engine infers the
   terminal from net-node *position*, not an explicit number). Do not shift by one when crossing 1-based
   symbol ↔ 0-based engine, and do not reorder terminals — a multi-terminal device (FET d/g/s) is where a
   transposed/off-by-one terminal silently makes a wrong-but-plausible netlist. See `project-file-formats.md`
   “Port-index conventions”; the extraction oracle catches this.
6. **Honor disabled components (Disable to Open / Short, §7.2):** before emitting, the extractor inspects each
   component's **disable state** and bridges accordingly — no engine change, the netlist simply comes out as
   if the part were open or shorted:
   - **`Open`** — the component is **omitted** from the emitted netlist; its ports are left as they would be
     with the part removed (the nets it touched remain, simply no longer joined *through* the component). An
     open two-terminal part therefore breaks the connection it bridged; a port left dangling by the removal
     is handled by the normal floating-port validation (below).
   - **`Short`** — the component's ports are **unioned into a single net** (steps 2–3 treat the part as a
     zero-impedance bridge), so the emitted netlist connects them directly with no element between. For a
     two-terminal part this merges its two nets into one; for a **multi-terminal** part, **all ports short
     together** into one net (no user choice on which terminals — always all of them).
   This keeps Disable-to-Open/Short purely a **schematic-side, extraction-time** transform: the design model
   the engine receives looks exactly like one drawn with the part genuinely removed or replaced by a wire,
   so the engine and elaboration need no awareness of the feature.

**Hierarchy:** a placed cell instance contributes its **interface ports** as connection points at this level;
its interior schematic is a separate design (§9 in-place descend). Extraction is per-schematic; the engine's
elaboration flattens the hierarchy as it already does for authored designs.

**Validation surfaced to Messages (§8):** conflicting net labels, an unconnected required port, a port left
floating — reported as warnings/errors with the offending instance/net named, never silently dropped.

**Incremental vs full:** v1 may re-extract the whole schematic on run (simple, correct); incremental
re-extraction on edit is a later optimization (the extractor staying headless/testable makes either viable).
Keep extraction deterministic and pure so it is unit-testable against authored-`.cnl` equivalents — a strong
oracle: a schematic drawn to match a hero `.cnl` must extract to an equivalent design model and produce the
same `DataSet`.

---

## 5A. Symbol editor

Each cell has a **symbol view** (its schematic-level representation) edited in a **symbol-editor canvas** — a
Content tab/view like the schematic and data-display canvases, sharing the same canvas core (§3). It is for
drawing the cell's **symbol geometry** (the body shape) and placing its **ports/pins** (the connection points
that appear when the cell is instanced in a schematic, §5 hierarchy).

- **Canvas objects:** the symbol editor hosts **text** (§3.1.2) and **shape primitives** (§3.1.3) — the same
  shared canvas-object family — for drawing/labeling the symbol. (Bitmaps and plots are not symbol content.)
- **SVG import:** the symbol editor can **import an SVG file** into its canvas, converting the SVG paths/
  shapes into editable primitives the user can then refine (move, resize, restyle, delete). This lets a user
  start from existing vector art for a symbol rather than drawing from scratch. (Import maps SVG primitives
  to the §3.1.3 primitive family where they map cleanly; complex SVG features that don't map are
  approximated or flagged — detail settled when built, Phase 6f.)
- **Ports/pins:** placing named ports that become the instance's connection points; the port names are what
  the schematic's extraction (§5) uses for the cell-instance interface.
- Standard editor behaviors (select/move/resize/rotate, undo/redo, copy/paste, zoom) per §10.

---

## 5B. The component-type registry (single source of per-type knowledge)

Every component type (resistor, inductor, capacitor, tone source, FET/SDD, Z-port, …) carries a bundle of
**per-type knowledge** the GUI needs in several places at once: its short display name, its instance-name
prefix, its short type code (what the user types to set the type), and — importantly — its **default
parameter set** (which parameters a freshly-placed instance has, and which show on the schematic). The
**component-type registry** is the **one place** all of that lives, so the renderer, the palette, the inline
type editor, instance auto-naming, and parameter seeding all read from a single authority rather than each
hard-coding its own copy.

**What it stores, per type:**
- **Display name** — the short label rendered as the component's type on the schematic: `R`, `L`, `C`, `FET`,
  and for a Z-port the **port-count-aware** `Z1P`/`Z2P`/… (a 2-node Z-port is `Z1P`, 4-node `Z2P`, i.e.
  `Z{nodes/2}P`). (This replaced the old `SymbolKind.ToString()` which rendered ugly enum names like
  "FetSdd".)
- **Instance-name prefix** — used when auto-naming a placed instance (`R1`, `C3`, `X2`).
- **Short type code** — the case-insensitive code the user types in the inline type editor to set/change the
  type: `R`, `L`, `C`, `V`, `VTone`, `SDD`/`FetSDD` (aliases for the same device), `Z1P`/`Z2P`/… (any `Z{N}P`
  → Z-port with the matching node count), `GND`, `Port`/`P`, `X`.
- **Default parameter template** — the list of parameters a freshly-placed instance gets: each with a
  **name**, a default **expression** (often blank), a **unit**, and a **ShowOnSchematic** flag. This is
  per-type and may be **more than one** parameter: a resistor's template is one parameter (`R`, ohm); a
  **tone source's is two** (`V` in volts *and* `Freq` in Hz), both shown. **There is no notion of a single
  "primary" parameter** — a type shows whatever set its template says, of any length.

**Why it matters — it's the seam where the GUI, the renderer, parameter defaults, and the future standard
library all meet.** Three concrete payoffs:
- **One source of truth, no drift.** When the same per-type fact (the prefix, the display name, the default
  params) is duplicated across the renderer, a placement path, and a demo builder, they *diverge* — and that
  divergence is a bug factory (e.g. a tone source rendering `V = 2 GHz V` because a builder hand-typed a
  single "2 GHz" label while the registry knew the type has two params). Centralizing kills that class of
  bug: there is exactly one definition of "what is a capacitor, on the schematic."
- **Multi-parameter defaults done right.** Because the template is a *list*, a type that should display
  several parameters by default (the tone source's amplitude **and** frequency) just lists them — no special
  case, no "primary parameter" fiction. Placement seeds an instance's parameters straight from the template
  (correct names/units/show-flags), so a freshly-placed tone source shows both lines immediately.
- **The standard component library is the registry, scaled up.** The shipped component libraries (Phase 6f+)
  will use these *same* codes and defaults (`R`, `L`, `C`, `Z1P`, `Z2P`, `SDD`, `FetSDD`, …). The registry is
  the small, in-code seed of that library; growing the library is adding entries, not inventing a parallel
  scheme. The codes a user types today are the codes the library uses tomorrow.

**Where it lives & the firewall.** The registry is **design-model knowledge, not view knowledge** — "a
capacitor is called C and has a capacitance parameter" is true regardless of how it's drawn — so it is
**Avalonia-free** and read by the renderer rather than owned by it. (It currently sits in the schematic
layer keyed by `SymbolKind`; when the component model gains a richer type system it should re-key off the
real component type and move toward core with the standard-library work. The principle holds either way: one
registry, framework-free, every consumer reads from it.)

**The parameter names must match the engine.** A type's default parameter names (e.g. the tone source's `V`
and `Freq`) must be the names the device model and elaboration actually resolve (`src/Core/Devices/*Model.cs`)
— otherwise a placed component carries parameters the engine doesn't recognize, a mismatch that wouldn't
surface until extraction + run (and which the §5.1 extraction oracle is designed to catch). The registry's
default names are therefore chosen against the device models, not invented.

**Consumers (all read the one registry):** the renderer (type label, §4.5), the inline type editor (parses
the short code → type, §4.5), component placement (seeds default parameters), instance auto-naming (the
prefix), and — later — the palette and the standard component library.

---

## 6. Data Display — `DataCube`-native, mining splotRF

The data-display canvas renders results from a run's **`DataSet`**. It is **circuitRF's own**, built fresh and
**`DataCube`-native** (a trace is a *slice of a cube*), living under `src/Ui` (C1). splotRF is **reference
material** — its proven techniques are mined and re-implemented cleanly, not consumed as a dependency
(`ui-architecture.md`).

### 6.1 The placeable-plot canvas
A data-display view is a canvas on which the user **places plots at arbitrary locations**, **selects**,
**moves**, and resizes them; a plot tears out to its own window and re-docks (mirroring splotRF's
`PlotContainer`/`DisplayWindow` pattern). The canvas shares the §3 transform/interaction machinery.

### 6.2 Plot types (1-D traces over a sweep/freq axis)
Re-implement, against `DataCube`, the proven splotRF plot types:
- **Rectangular** (e.g. a measurement vs Pin or vs frequency — a 1-D cube slice).
- **Smith chart** (reflection/impedance — complex cube data).
- **Polar**.
- **Tables** (`TableRenderer` reference).
Plus markers and the **MarkerInfoBox** (splotRF `Marker`/`MarkerInfoBox` reference), **autoscale with marker
preservation**, tick-interval snapping, and the three-space transforms (§3). A trace binds to a `DataCube`
slice (the cube's axes supply the x-axis and units automatically — the Phase-5 payoff).

### 6.3 Contour plots — the loadpull-contour feature (MAJOR new item)
**The signature data-display feature for circuitRF, and net-new work** (splotRF has nothing like it — no
reference to mine). Contour plots visualize **loadpull data**: a scalar measurement (Pout, PAE, DE, gain)
over the **2-D termination grid** (the Γ/Z load-pull plane), drawn as **contours on a Smith chart**. This is
the visual payoff of the entire Phase-4b loadpull/pursuit engine — a loadpull-pursuit run that can't show its
contours is only half-delivered.

What it entails (details deferred — "sort out when we build it"; flagged here as a major §6 scope item):
- Input is a **2-D `DataCube` slice** over the termination-grid axes (the loadpull DataSet already carries
  this — §5-5 of Phase 5), i.e. a scalar FOM (Real cube) sampled across the load Γ/Z plane.
- **Contour extraction** from the gridded scalar field (marching-squares or equivalent) → contour paths at
  chosen levels. The extraction is **coordinate-agnostic** (it operates on the scalar field over the grid);
  only the final path rendering differs between the two display modes below.
- **Two display modes** (the termination plane can be viewed either way — user-selectable):
  - **Γ-plane on a Smith chart** — contours drawn on the Smith coordinate system (reuse the Smith transform,
    §6.2). The natural RF view of loadpull.
  - **Z-plane on a rectangular plot** — contours drawn in the impedance plane on rectangular axes (reuse the
    rectangular transform, §6.2), Re(Z) vs Im(Z). Useful when the user thinks in impedance.
  Both modes share the same extracted contour paths; the Γ↔Z mapping uses RfCore (the same Z↔Γ the engine
  uses), so switching modes is a re-projection, not a re-extraction.
- Level labels, optional fill/shading, and overlay of the **MXP/MXE optima markers** and recommended
  terminations from the `LoadpullPursuitResult`.
- Interaction: choose which FOM to contour, the contour levels (auto or user), the Γ/Z display mode, and the
  optima/recommendation overlays.
- Likely its own design sub-note when Phase 7 reaches it.

### 6.4 Measured-vs-simulated overlay
Support overlaying a **measured** lab Touchstone over a **simulated** result cube (a common RF validation
workflow) — a measured trace and a simulated `DataCube` slice on the same plot.

---

## 7. Toolbars

Two tiers of toolbar, matching the window structure: **one global Workspace toolbar** (always present) and a
**context toolbar that swaps with the active Content tab** (the schematic toolbar below; a data-display
toolbar later). Toolbar buttons are quick-access duplicates of menu actions (§8) — same commands, same
undo/command-pattern path (§10); the toolbar never has behavior the menus/commands don't.

**The context toolbar belongs to the view, not the Workspace frame.** A schematic (or data-display) view
carries its own context toolbar/command bar, so when the view is **torn out into its own window** (§2.3),
its toolbar **travels with it** — a popped-out schematic window has the full schematic toolbar (§7.2),
zoom/draw/select tools and all, and is fully editable on its own. (A torn-out window does **not** carry the
global Workspace toolbar of §7.1; it gets the view's own context toolbar plus whatever minimal
window/file/edit affordances a standalone editor window needs — e.g. Save, Undo/Redo, Help.) Within the
main Workspace window, the active Content tab's context toolbar is shown; tearing the tab out moves that
toolbar into the new window with it, and re-docking returns it to the tab-context position.

### 7.1 Workspace toolbar (global controls)
Always present at the workspace level; acts on the whole workspace (not just the Project Tree). File/window
management and run control:
- **Start Page / New**, **Open / Save**
- **Cut / Copy / Paste**, **Undo / Redo**
- **Print / Help**
- **Hide/Show Dockers** — toggle region visibility (Project Tree, Properties, Messages, and the future Tune
  window); the View-menu equivalent of showing/hiding Dock tools (§2.0).
- **Fit Windows to Frame** — reset/fit the dock layout to the frame.
- **Run / Stop Analysis** — launches the active TestBench's configured simulation, or raises the analysis-
  selection menu if none is configured. Same action as `Simulate → Run/Stop` (§8); toolbar = quick access.
- **Status Messages** — expands the Messages region if minimized, or pops it out to its own window if it is
  not minimized (§9).

### 7.2 Schematic toolbar (the schematic-tab context toolbar)
Shown when a **schematic** Content tab is active (the first of the per-editor context toolbars; the
data-display tab gets its own later — §10). Drawing tools, view controls, and schematic-specific edits:

*Tools & selection*
- **Select** — default cursor; highlight/select components or text (§4.6).
- **Part Selector** — opens the component palette / library browser to add parts (drives the Properties-region
  palette, §2.2).
- **Ground** — place a **ground** symbol. Promoted to its own toolbar button (not buried in the palette)
  because it is by far the most frequently placed element — it must be one click away. A ground marks its
  net as the circuit reference (node 0); extraction (§5) unions all ground-tied nets into the single global
  reference node the engine expects. (If multiple ground symbols touch distinct nets, those nets are all the
  one reference node — the usual schematic convention.)
- **Var** — place a **variable/expression** component on the schematic: a named parameter defined by an
  expression (the Phase-1 expression engine — root `CLAUDE.md`/`expressions.md`), e.g. `RFfreq = 2e9` or
  `ToneSpacing = 10e6`. Vars are design-level definitions other components' parameters reference by name (the
  inductor's value can be `1/(w0^2*C)`). On the schematic a Var renders as an editable name=expression label
  (inline-editable like any parameter, §4.5). Extraction (§5) carries Vars into the design model as the
  variable definitions the engine already resolves — it is the schematic face of a `.cnl` variable
  declaration, not a new engine concept.
- **Measurement** — place a **measurement** on the schematic: a `measure Name = expression` (the composable
  cube-algebra measurement layer — `measurements.md`), e.g. `Pout_dBm = ...`. It renders as an editable
  name=expression label and is carried by extraction into the TestBench's measurement set, evaluated
  post-run against the result `DataSet` (§6). Placing a measurement on the schematic is a convenience entry
  point for the same measurement objects the TestBench holds; the analysis-qualified operands
  (`HB1.V(X1.drain,1,All)`) reference nets/branches by the names extraction assigns (§5). A measurement IS
  part of the netlist/design (a `.cnl` legitimately contains `measure` statements the engine evaluates) —
  it just isn't part of the *electrical connectivity*: it has no ports, contributes no nets, and is invisible
  to the connectivity union-find (§5.1). Extraction collects it as a TestBench measurement statement, not as
  a component or net.
- **Part Group** — toggles display of specific component toolbars/groups (lumped elements, transmission lines,
  …).
- **Annotation** — toggles the annotation toolbar (text, lines, shapes on the schematic — decoration, not
  electrical; ignored by extraction).
- **Text Visibility (eye)** — pull-down to toggle visible text on the schematic (net names, part parameters);
  the global counterpart to the per-parameter “show on schematic” flag (§4.5).

*Edit modifiers (toggles)*
- **Keep Connect** — automatic wire re-connection when moving parts (the drag-release-over-wire behavior,
  §4.2, kept live during moves).
- **Grid Snap** — toggle grid alignment on/off (§4.1).

*Drawing, zoom & manipulation*
- **Pan / Zoom Area / Zoom to Fit / Zoom to Page** — navigation (§4.1; Zoom to Fit = the `F` shortcut, §4.6).
- **Line / Angled Line** — draw net connections (orthogonal and angled wire tools; routing is simple-ortho,
  §4.3).
- **Rotate** — rotate the selected part 90° (the `R` shortcut, §4.6).
- **Tune** — mark selected parts tunable for real-time parameter sweeping. **PLACEHOLDER** — tuning is not yet
  implemented; build the button/affordance stub but no backing behavior until the tuning feature exists.
- **Disable to Open / Disable to Short** — mark the selected components to simulate as an open or short
  circuit without deleting them. This sets a per-instance **disable state** (`None` / `Open` / `Short`) on
  the component in the design model; the visual shows the disabled state (e.g. greyed with an open/short
  glyph). **No engine change is needed** — the mark is honored entirely by **net extraction** (§5.2): the
  extractor bridges the component to an open or short when building the netlist. The toolbar only sets the
  flag and reflects it; the bridging lives in the schematic→engine seam.

---

## 8. Menus & workflows

Standard menu bar: **File** (New Workspace, Open, Add Library [reference an existing cell library], Save,
Save As, Import/Export DataSet, …), **Edit** (Undo/Redo, Cut/Copy/Paste, Select All, …), **Insert** (Image…,
Text, Shape… — the canvas objects of §3.1; the available items depend on the active canvas), **View** (region
show/hide, zoom-to-fit, theme), **Simulate** (Run, Stop, the analysis the active TestBench defines, …).

The default path must honor the PRD §12 **click budgets** (e.g. placed FET → running HB power sweep in ≤ 8
actions). Advanced settings remain reachable but must not clutter the default workflow (progressive
disclosure — `src/Ui/CLAUDE.md`).

---

## 9. Messages region

A scrollable, selectable text area showing engine/netlist messages. **Color + icon coded** (never color
alone — accessibility, `src/Ui/CLAUDE.md`): error (red + error icon), warning (yellow + warning icon),
success (green + check). Color *meaning* may be localized, but the icon carries the meaning regardless of
locale or color vision.

**Clickable file links:** any file name in a message is underlined; clicking it **reveals the file in the OS
file manager** (Finder / Explorer). Engine errors that reference a netlist file + line should link to it.

---

## 10. Cross-cutting editor behaviors

- **Undo/redo via the command pattern** across all editors (schematic, symbol, data-display). Every mutation
  is a reversible command; nothing mutates model state directly (`src/Ui/CLAUDE.md`).
- **Copy/paste via the system clipboard** — all or a selectable subset of a schematic, to/from other
  schematics; pasted items stay selected.
- **Hierarchy navigation — in-place descend.** Pushing into a sub-cell's schematic **descends the canvas into
  that cell in place**, with a **breadcrumb** to pop back out (not a new tab). Editing a cell affects every
  instance; instance-level parameter **overrides** stay per-instance (root `CLAUDE.md` → expressions).
- **Drag-and-drop** broadly (palette→schematic, tree items, tabs) with drag previews, valid/invalid-target
  feedback, and undo support (`src/Ui/CLAUDE.md` §11).

---

## 11. Open items / deferred

- **Beautiful auto-routing** (obstacle-aware, aesthetic) — deferred research item; v1 is simple-ortho (§4.3).
- **Canvas objects** (§3.1) — the shared bitmap/text/primitive/plot family is built with schematic editing
  (Phase 6d, which builds the select/move/resize/undo machinery they share); plots-on-canvas (§3.1.4) lands
  when the plot object exists (Phase 7). SVG import (§5A) is Phase 6f (symbol editor).
- **Layout views** for cells — future (cells today have symbol + schematic).
- **Contour plot** (§6.3) — major item, design sub-note when Phase 7 reaches it.
- **Properties region** (§2.2) — additional inspector roles beyond the palette — future.
- **Tune** (§7.2) — placeholder button; real behavior arrives with the tuning feature.
- **Data-display context toolbar** (§7.2) — the plot/contour-tools toolbar for the data-display tab — built
  with the data display (Phase 7).
- **Component HTML docs** (§4.5) — placeholder now, written later.
- The Dock layout serialization (§2.0) and the net-extraction algorithm (§5.1) are now specified to the
  level 6b/6e need; finer details (exact serialized schema, junction-dot UX) refined during implementation.
