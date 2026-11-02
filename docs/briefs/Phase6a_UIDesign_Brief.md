# Phase 6a — UI Design Document + Architectural Firewall (Opus design; minimal code)

**Goal:** before any GUI feature code, (1) write the **circuitRF UI design document** — the interaction model,
the window/region/docking approach, the two canvases, the schematic interaction grammar, net extraction,
responsiveness requirements, and the v1-vs-deferred scope — and (2) establish the **UI-framework-agnostic
architectural firewall** (engine/core/design layers never reference Avalonia), made *enforceable* via a
build/CI check. **No app features in 6a** — the deliverable is the design doc, the firewall, and the
firewall's automated check. This is the "define how the GUI works" phase.

> This is largely a **design + architecture** task (Opus-suited). Read first: `circuitRF/src/Ui/CLAUDE.md`
> (the standing UI rules — both the architectural constraints and the design-quality bar), the owner's GUI
> brainstorm (captured below in §1), the PRD §12 click budgets, and **the splotRF repo**
> (`<workspace>/splotRF`) — it is the proven template for the shell + data-display half.

## Context — splotRF is the template, not greenfield
splotRF is a complete, mature Avalonia 12 app with the exact patterns circuitRF needs, and it is our sibling
(same stack, MIT, shared RfCore). Reuse aggressively; the genuinely new work is the **schematic** side.
What splotRF already proves and circuitRF should mirror:
- **The SkiaSharp canvas with three coordinate spaces** (data/world → fractional viewport → Skia pixels),
  the linear transform pipeline (`PlotRenderer.BuildTransforms`), pan/zoom/hit-test interaction
  (`PlotControl`, `DragSelectOverlay`) — this is the **same machinery the schematic canvas needs**.
- **Pure model / rendering / interaction layering** with a zero-graphics-dependency model layer.
- **A placeable-object canvas** (plots placed/selected/moved/torn-out: `PlotContainerView`,
  `DisplayWindow`), **undo/redo** (`UndoRedo.cs`, `UndoCommands.cs`), inspectors/flyouts, settings,
  and **cross-platform packaging** (`bundleForMacOS.sh`, `linux/`, `package.wx`, entitlements).
- MVVM via CommunityToolkit, `ViewLocator`, semantic styling, fonts/assets.

**A structural fact 6a must resolve:** splotRF's data-display engine (`Models/Plot/Trace/Axes`,
`Renderers/`, `Controls/PlotControl`) currently lives **inside the splotRF app**, not in RfCore — but
`src/Ui/CLAUDE.md` says circuitRF's data display reuses "splotRF controls from RfCore." So the plotting
engine must be **extracted into a shared library** (as RfCore itself was lifted out before) so both apps
consume it, OR an alternative resolved. **6a decides this** (see §4).

## §1 — Capture & elevate the owner's interaction model (the design doc core)
The owner's brainstorm is ~70% of the design doc already; the 6a doc formalizes it. Capture all of it, and
resolve the open interaction-design decisions. The model (owner's words, to be written up cleanly):

**Workspace = a window.** Holds a project's TestBenches/schematics, cells (symbol + schematic + future
layout views), references to external libraries (each with cells), and data-display configs.

**Regions (dockable):** Project Tree (top-left, ~10% width, nodal tree, right-click ops: Open / Open in New
Window / Delete …), Component Palette (below the tree, same width; ComboBox of standard component libraries;
drag-drop components into the schematic), Content (center, most of the area; **a tab view** of schematic /
symbol-editor / data-display views; tabs tear out to windows and re-dock), Messages (bottom; scrollable,
selectable, **color + icon** coded — error/warning/success — with **clickable file links** that reveal the
file in Finder/Explorer). Regions are resizable (drag handles), re-arrangeable (drag the region title with
live visual feedback), pop-out-to-window (drag out & release), and minimizable.
→ **DECISION (settled): use AvaloniaUI's `Dock` library for all of this** — do not hand-roll docking,
tear-out, or re-dock. 6a specifies how the regions map onto Dock's dock/document/tool concepts.

**Menus:** File (New Workspace, Open, Add Library, …), Edit, View, Simulate — standard submenus.

**Schematic editor:** pan/zoom; subtle background grid (user-set resolution; optional on-grid port snapping);
click-drag components/symbols; **drag-release-over-wires auto-connects** ports to wires at overlap (connected
points drawn as dark squares; unconnected ports get a subtle red box that becomes a dark square on connect);
**simple orthogonal wire routing** → **DECISION (settled): v1 = assisted/simple-ortho routing (snap,
auto-connect, basic orthogonal); beautiful dynamic obstacle-aware auto-routing is an explicit DEFERRED
research item, not v1.** Save/restore component+wire placement, zoom, window placement; copy/paste within and
across schematics; user-named nets honored by the engine; on-schematic editable parameters (click text →
inline edit; a per-parameter "show on schematic" flag keeps it clean); double-click → parameter dialog/popover
listing all params with a **Help** button → local html doc (placeholder html for now).

The doc must also resolve these **open interaction decisions** (not yet settled — propose + decide with
rationale):
- Selection model & keyboard grammar (rubber-band select, multi-select, delete, nudge, rotate/mirror, the
  hotkeys). Mirror splotRF's selection/drag idioms where they fit.
- How **hierarchy navigation** works (push into a sub-cell schematic, pop back) and how editing a cell vs an
  instance-override is presented.
- The **net naming/auto-naming** scheme (auto-named nets vs user-named; how a user promotes/renames).
- What "responsive at thousands of components" concretely requires (target: smooth pan/zoom at N≈10k) and how
  it's verified (a large stress schematic in `testdata/`, not a toy).

## §2 — Net extraction (the schematic→engine bridge — call it out explicitly)
The owner's brainstorm doesn't name it, but it's the critical seam: **how placed symbols + wires + named nets
become the design model the engine elaborates and runs.** 6a specifies the approach (connectivity extraction
from port/wire geometry → nets → the elaborated design model → engine run → `DataSet` back), because this is
what realizes the PRD §12 click budget ("placed FET → running HB sweep in ≤ 8 actions"). It need not be
*implemented* in 6a, but the design must exist so 6b–6e build toward it. Keep extraction **headless/testable**
(no UI dependency) — it's core logic, not view logic (ties to §3 firewall).

## §3 — The architectural firewall (future-proofing, made enforceable)
The owner wants circuitRF re-skinnable if Avalonia is ever replaced. Make the intent a **verifiable
invariant**, not an aspiration:
- **Rule:** the engine, core, design-model, netlist, and result-model (`DataSet`/`DataCube`) layers — and
  RfCore — **must never reference Avalonia or any UI framework.** All UI-framework code lives under
  `src/Ui` (+ the shared data-display library, §4). The contract between them is: design model in, `DataSet`
  out. A re-skin replaces `src/Ui` only.
- **Make it checkable:** add a build-time / CI assertion that the engine/core/RfCore assemblies have **zero
  Avalonia (and SkiaSharp-via-Avalonia) references** — e.g. a test that inspects assembly references, or a
  csproj/analyzer rule that fails the build if `Avalonia.*` is referenced from a non-UI project. This is the
  firewall that keeps the separation from eroding over a long GUI build.
- **Write a short architecture note** (`circuitRF/docs/design/ui-architecture.md`): the layering, the
  agnostic-core rule, the in/out contract, the "re-skin = replace src/Ui" statement, and the firewall check.
  Add a one-line pointer in the root `CLAUDE.md`.
- Note the nuance: SkiaSharp itself is fine in the core *if used headlessly* (it's a 2D lib, not a UI
  framework) — but the canvas *controls* (Avalonia-integrated) are UI. Keep the renderers' Skia drawing
  separable from the Avalonia control hosting, mirroring splotRF's Renderer-vs-PlotControl split.

## §4 — The display layer: build fresh, `DataCube`-native, mining splotRF as reference (DECISION: Option C / C1)
**DECISION (settled): splotRF is REFERENCE MATERIAL, not a dependency.** circuitRF builds its own display
layer, fresh and `DataCube`-native, living **in circuitRF** (C1 — `src/Ui`, e.g. a `src/Ui/Display` area; not
a shared lib for now). Rationale (owner): splotRF was developed ad-hoc with no planning — a useful prototype,
but it only plots S-parameters and has no knowledge of `DataCube`. Consuming it directly would shackle
circuitRF's clean engine to an unplanned, SNP-shaped prototype. Instead:
- **Mine the proven techniques, port the algorithms, do NOT take the code as a dependency.** The hard-won,
  valuable parts to study and re-implement cleanly: the **three-coordinate-space transform pipeline**
  (data/world → fractional viewport → Skia pixels), **Smith / polar / rectangular** rendering math, the
  **placeable-plot canvas** (place/select/move/tear-out), **plot/trace rendering**, **tables**, the
  **MarkerInfoBox** and marker interaction, **autoscale with marker preservation**, tick-interval snapping,
  and the pan/zoom/hit-test interaction grammar. Cite the specific splotRF files in the design doc as the
  reference for each (e.g. `PlotRenderer.cs`, `Axes.cs`, `PlotControl.cs`, `TraceRenderer_MarkerRenderer.cs`,
  `TableRenderer.cs`, `Marker.cs`).
- **The input is `DataCube`, from day one.** The fresh trace model is "a slice of a cube" — no SNP-shaped
  adapter, no retrofit. This is the payoff of the Phase-5 unification: the display layer consumes the one
  currency every analysis already returns. (splotRF's SNP→Trace→Plot flow is the *pattern* to learn from;
  circuitRF's is `DataCube`-slice→Trace→Plot.)
- **C1 now, but keep it lift-out-ready.** The display engine lives in circuitRF for now (no shared-lib
  ceremony until something needs to share). Keep it **UI-framework-light** (clean Skia-render vs thin
  Avalonia-host split, per §3) and `DataCube`-native so it *could* later be lifted into a shared lib (the
  "don't preclude it" discipline) — e.g. if a future splotRF v2 wanted to adopt circuitRF's better engine.
  Don't build that now.
- **splotRF stays independent.** It continues (or is discontinued) on its own; circuitRF does not depend on
  it and does not constrain it. No lockstep.

The design doc (§1) hosts this display canvas in the Content region's data-display tabs; Phase 7 builds it
out against the `DataSet`/`DataCube` contract and the consumer guide already written in Phase 5-8.

## §5 — The UI phase plan (lay out the remaining sub-phases)
Document the arc so each later phase is scoped and independently demonstrable, riskiest/foundational first:
- **6b — Application shell:** Workspace window, Dock-based regions (Tree/Palette/Content-tabs/Messages),
  menus, Messages with color+icon+clickable-file-links, tear-out/re-dock — driven by a stub project tree.
  Mirror splotRF's `App`/`ViewLocator`/window/packaging scaffolding.
- **6c — Schematic canvas core (read-only):** the virtualized SkiaSharp canvas + spatial index, pan/zoom,
  grid, render symbols from a design model loaded from `.cnl` — display only, stress-tested at N≈10k. Reuse
  splotRF's coordinate-space + renderer patterns.
- **6d — Schematic editing:** palette drag-place, move, simple-ortho wire + drag-to-connect + port
  indicators, inline param edit, double-click param dialog, undo/redo (command pattern, mirror splotRF),
  copy/paste.
- **6e — Net extraction + run integration:** schematic → design model → elaborate → engine run → `DataSet`
  back; realizes the PRD click budgets.
- **6f — Symbol editor + hierarchy:** symbol views, cell push/pop, instance overrides.
- **6g — Parameter dialogs + Help scaffolding + design-quality polish pass** (against `src/Ui/CLAUDE.md`).
- **(Phase 7 — splotRF Data Display)** integrates the shared plotting lib (§4) into the Content data-display
  tabs; partly parallel since it's shared-lib work.

## Acceptance (6a)
1. `circuitRF/docs/design/ui-design.md` — the full interaction model (§1), with the open interaction
   decisions resolved (selection/keyboard grammar, hierarchy nav, net naming, responsiveness target+verification),
   the Dock mapping, and v1-vs-deferred scope (simple-ortho v1, beautiful auto-route deferred) stated.
2. `circuitRF/docs/design/ui-architecture.md` — the firewall: agnostic-core rule, in/out contract, re-skin
   statement, the Skia-headless-vs-Avalonia-host nuance, and §4's shared-plotting decision.
3. **The firewall CHECK exists and passes** — an automated assertion (test or build rule) that engine/core/
   RfCore reference no Avalonia. (This is the one bit of *code* in 6a, because the firewall is worthless
   un-enforced.)
4. §2 net-extraction approach and §5 phase plan documented.
5. Flag for owner review before any 6b code. No GUI features built in 6a.

## Guardrails
- **Design-and-firewall only; no GUI features in 6a.** The temptation is to start the shell — don't; 6b does.
- Mirror splotRF wherever it already solved a problem (canvas, layering, undo, tear-out, packaging) — cite
  the specific splotRF file/pattern in the design doc rather than inventing.
- The firewall must be *enforced* (the check), not just *described* — an un-checked rule erodes over a long
  build.
- `src/Ui/CLAUDE.md` distinguishes load-bearing architectural rules (engine separation, canvas performance)
  from design-quality bars (typography, motion) — honor that weighting; a performance/firewall violation is a
  bug, a polish miss is debt.
- Settled decisions are settled: Dock for docking; simple-ortho routing for v1 (beautiful auto-route
  deferred). Don't reopen them; do resolve the still-open interaction decisions in §1.
- Update `src/Ui/CLAUDE.md` (add the design-doc/architecture-note pointers) and the root `CLAUDE.md` (the
  firewall pointer).
