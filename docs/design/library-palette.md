# circuitRF — Library Palette Design

**Status:** Draft (rev 1) for review · **Date:** 2026-06-09 · **Phase:** 6 (GUI; follows symbol editor +
workspace + analysis authoring)

The **Library Palette** ("the Palette") is a grid of rendered component symbols from which the user **places
component instances** into a schematic. It is the primary "get a part onto the canvas" surface. Companions:
`standard-library-symbols.md` (the built-in symbol set), `symbol-editor.md` (`SchematicRenderer.DrawSymbol` —
the glyph render reused for tiles), `ui-architecture.md` (Dock tools + tear-off), `grid-and-connectivity.md`
(on-`P` connectivity at placement commit), `color-themes.md` (`AppPreferences` for Recently-Used),
`workspace-and-project-tree.md` (the *cell* placement path — related but distinct, see §1).

**The governing decisions (owner-confirmed):**
- **v1 sources the built-in standard library only** — the compiled-model primitives keyed in
  `ComponentTypeRegistry` (R/L/C/sources/lines/data-files/…). **Placing library *cells* in the Palette is
  deferred** (the cell-reference placement path from workspace step 5 is newer; the Palette can grow to show
  cells later — §9).
- **One library + category metadata**, NOT many separate libraries. Filtering by category (a header ComboBox)
  + search realize "Lumped / Transmission Line / Microstrip / Sources / Data Files"; **Common** and
  **Recently Used** are **virtual categories**, not separate stores.
- **Placement stays armed after a click-place** (place several of one part in a row) until Esc / click the
  armed tile again / switch tiles.
- **The Palette only ARMS a placement; the schematic owns the placement state machine** (ghost, rotate,
  commit, connect). One armed-placement state is app-level so it works across any schematic (tabbed or
  torn-off).

---

## 1. What a Palette entry is (and what it is not)

circuitRF has **two** kinds of placeable things, and the Palette must be honest about which it shows:
- **Built-in primitives** — compiled models keyed by `SymbolKind`, with metadata in `ComponentTypeRegistry`
  (display name, instance prefix, default parameters, default label visibility) and geometry in
  `BuiltInSymbols.Primitives`. **This is the Palette's v1 source.**
- **Library cells** — folder-based cells referenced by relative path (`workspace-and-project-tree.md` §4),
  resolved through `.ccell` → primary `.csym`. **Deferred** for the Palette (§9); these are placed via the
  project tree / cell-reference path for now.

So the Palette's data source is **the component-type registry**, *not* the project-tree cell scanner. (Stated
explicitly so the two never get cross-wired: the tree shows on-disk cells; the Palette shows compiled
built-ins.) The registry comment already anticipates this: *"Lives here (Avalonia-free) so the renderer,
palette, and auto-naming all share it."*

---

## 2. The library data model (one library, category + search metadata)

Extend `ComponentTypeRegistry` (Avalonia-free) so each component type carries **Palette metadata**:
- **Category** — an enum/string: `Lumped` (R, L, C, Mutual, Term), `TransmissionLine` (TLIN, Lossy TLIN,
  Coax, …), `Microstrip` (MLIN, MSTEP, MTEE, …), `Sources` (VTone, VDC, VnTone, ITone, IDC, InTone, …),
  `DataFiles` (SNP, .npy), `Ground/Port` (or folded into a "Terminals" category) — extensible as the library
  grows.
- **Search terms** — the display name + type name + category + optional aliases/keywords (so "cap" finds the
  capacitor). **Parameter search is OUT for v1** (low value; type/category/name covers it — §6).
- **Common flag** — marks the subset shown under the **Common** virtual category (a curated everyday mix).
- Display name + instance prefix + default params already exist (reused for the tile label + the placed
  instance).

A **`LibraryCatalog`** (Avalonia-free) projects the registry into an ordered list of **palette items**
(`{ SymbolKind, DisplayName, Category, SearchTerms, IsCommon }`), the single source the Palette VM binds to.
Adding a component to the library = **adding one registry entry** (+ its `BuiltInSymbols` geometry + engine
type string) → it appears in the Palette automatically (§8).

**Virtual categories** (filters, not stores):
- **All** — every item.
- **Common** — `IsCommon` items.
- **Recently Used** — the MRU list (below).
- the real categories (Lumped/TLine/Microstrip/Sources/DataFiles/…).

**Recently Used** — a persisted MRU of placed component **types** (not instances) in `AppPreferences`
(mirrors Recent Workspaces): most-recent-first, de-duped, capped (~12), updated on every placement commit.

---

## 3. The tile (how one component renders)

Each item is a **square button** showing the component's **symbol glyph only**:
- **Glyph-only render** — reuse `SchematicRenderer.DrawSymbol` with a **glyph-only flag** (no pins, no
  parameter text, no instance name — a tile is an *icon*, not a schematic preview), auto-scaled to fit the
  tile with padding, centered. Honors the active color theme (symbol-line role).
- **Type label underneath** — the registry `DisplayName` (e.g. "R", "TLIN", "VTone") as the tile caption —
  the searchable, novice-friendly identity (a bare glyph is ambiguous to newcomers).
- **Armed state** — when a tile's placement is armed, the button renders **depressed/highlighted** (a clear
  "this is the part you're placing"); only one tile armed at a time.
- **Tooltip** — full display name + category (and maybe a one-line description) on hover.

---

## 4. Layout (width-driven; one rule for dock + tear-off)

- **Column count = `max(1, floor(availableWidth / tileWidth))`** — a single responsive rule. The dock default
  width yields **~2 columns**; tearing off into a resizable window and widening **adapts** the column count
  automatically. No docked-vs-floating special-case.
- **Scrollable** vertically when items exceed the visible area.
- **Dock tool** by default (a Palette region, like Project Tree / Properties); **tear-off** → a resizable
  window (min width ~2 tiles) whose columns reflow on resize (the dock tear-off machinery already exists).

---

## 5. Header (category filter + search)

A header bar at the top of the Palette:
- **Category ComboBox** — All / Common / Recently Used / Lumped / Transmission Line / Microstrip / Sources /
  Data Files / … — selecting filters the grid to that (virtual or real) category.
- **Search field** — live filter by **type name / display name / category / aliases** (NOT parameters, v1).
  Search composes with the category filter (search within the selected category; or search-all when a
  "search clears category" affordance is chosen — pick the simpler: **search filters within All**, and
  selecting a category narrows; state the final interaction at build).
- Empty result → a quiet "No matching components."

---

## 6. The placement state machine (the core — owned by the schematic/app, not the Palette)

**Critical separation:** the Palette **arms** a pending placement; the **schematic canvas** runs the
placement interaction. The armed-placement state is **app/workspace-level** (not per-canvas) so a single armed
part can be dropped into **any** schematic in view (tabbed or torn-off).

**Arming:**
- Click a tile → **arm** that component type (tile depresses). Clicking the **armed** tile again → **disarm**.
  Clicking a **different** tile → **switch** the armed type (old tile un-depresses, new depresses).

**The interaction (in any schematic canvas, reading the armed state):**
- **Ghost follow** — a live ghost rendering of the component (its symbol, snapped to the authoring grid `p`)
  follows the cursor wherever it moves over a schematic canvas.
- **Rotate** — `R` / `Ctrl-R` rotates the ghost (the placement rotation) before commit.
- **Cancel** — `Esc` cancels the placement (ghost disappears, tile un-depresses); also clicking the armed tile
  again cancels.
- **Commit** — clicking in the schematic **places** a component instance at the snapped cursor position with
  the current rotation. **On commit, connectivity is resolved**: pins landing on a wire or another pin
  **union** into those nets (reuse the on-`P` union logic from `ComputeConnectivityGeometry` / the extraction
  path — *the same single source*, never a re-derivation). The placement is one undoable command on the
  schematic's stack; the component gets an auto-named instance (registry prefix + next free number) and the
  registry's default parameters.
- **Stay armed (owner decision)** — after a commit, the placement **remains armed** so the user can place
  several of the same part; only Esc / click-again / switch-tile disarms. (A status hint can show "placing
  R — Esc to stop".)
- **Recently-Used update** — each commit pushes the type to the MRU (§2).

**Why app-level state:** the user can arm a part, then move the mouse across a torn-off schematic window and
place there — so "what is armed" cannot live on one canvas. A small app/workspace-level
`PendingPlacement { SymbolKind, Rotation }` (observable) is read by every schematic canvas's pointer handling.

---

## 7. Drag-and-drop (system DnD — the second path to the same commit)

The Palette supports **system drag-and-drop** as an alternative to click-arm:
- **Drag** a tile → a system drag with a **circuitRF palette drop-type** payload (the `SymbolKind` / catalog
  item id). The OS drag image is the ghost (no separate follow needed during the drag).
- **Drop** onto a schematic canvas → the canvas (which **registers the palette drop-type**) **commits** a
  placement at the drop point — the **same create-instance + connectivity-union commit** as the click path.
- DnD and click-arm **converge** on one commit routine; only the *pre-commit* affordance differs (OS drag
  image vs. live ghost + rotate). (Rotation during a raw OS drag is limited; the click-arm path is the one
  with R/Ctrl-R. State whether drop uses the last-used rotation.)

---

## 8. Extensibility — the registry is the single contribution point (first-class)

A stated goal: **adding components must be straightforward**, and **devs may ship their own compiled-model
libraries**. The Palette derives entirely from registry/catalog metadata, so:

**To add a built-in component (the recipe — to be documented for contributors):**
1. Add a **`SymbolKind`** (or, post-v2, an entry in the richer component-type system — see below).
2. Add its **geometry** to `BuiltInSymbols.Primitives` (the single geometry source the renderer + tiles use).
3. Add a **`ComponentTypeRegistry`** entry: display name, instance prefix, default label visibility,
   **default parameters** (name/expr/unit/dimension), **category**, **search terms**, **Common flag**.
4. Add the **engine type string** mapping (the `Reference` the netlist/extractor emits — `net-extraction-and-
   run.md`).
→ The component now appears in the Palette (right category, searchable), places with correct defaults, renders
its glyph, and extracts to the right netlist type. **No Palette code changes.**

**The richer-type migration (v2, anticipated):** the registry comment notes it is *"keyed by SymbolKind for
now; when the component model gains a richer type system (v2, real component-model factory), re-key off that
type instead."* The Palette must bind to the **catalog projection**, not `SymbolKind` directly, so this
re-key is a catalog-internal change, not a Palette rewrite.

**Custom compiled-model libraries (devs):** a future mechanism for third-party compiled models to register
catalog entries (distinct from folder-based *cell* libraries) — deferred (§9), but the single-contribution-
point design is what makes it tractable.

**Docs:** the contribution recipe (above) is documented for devs; the Palette UX (filter/search/arm/place/DnD)
is documented for users. *(Doc deliverables tracked with the build.)*

---

## 9. Open / deferred
- **Library *cells* in the Palette** — showing folder-based cells (not just compiled built-ins) as placeable
  Palette items; v1 places cells via the project tree. Deferred.
- **Custom dev compiled-model libraries** — third-party registration of catalog entries + packaging;
  deferred (§8 design makes it tractable).
- **Parameter search** — searching by parameter name/value; dropped for v1 (low value).
- **Rotation during OS drag-drop** — raw OS drag can't easily rotate; click-arm is the rotate path; drop uses
  last-used rotation (confirm at build).
- **Tile descriptions / richer tooltips / favorites** — beyond Common/Recently-Used; later polish.
- **Per-category ordering / custom user ordering** — v1 uses a stable catalog order; custom ordering later.

---

## 10. Implementation order (smallest correct first)
1. **Catalog metadata** (§2): extend `ComponentTypeRegistry` with category + search terms + Common flag; a
   framework-free `LibraryCatalog` projection; tests. (Headless.)
2. **The tile render** (§3): glyph-only `DrawSymbol` flag + the square-button tile (symbol + caption); a
   non-interactive grid first.
3. **Layout + header** (§4/§5): width-driven column count, scroll, dock tool + tear-off; category ComboBox +
   search filter.
4. **Placement state machine** (§6): app-level `PendingPlacement`; arm/disarm/switch from tiles; ghost-follow
   + rotate + Esc + click-commit + **connectivity union on commit** + auto-name + defaults; **stay-armed**;
   Recently-Used MRU.
5. **Drag-and-drop** (§7): palette drop-type; schematic accepts the drop; converge on the commit routine.
6. **Docs** (§8): the contributor recipe + the user UX.

Steps 1–3 stand up a visible (inert) Palette; 4 is the core placement; 5 adds DnD; 6 documents. The
connectivity-union-on-commit (step 4) must **reuse** the existing on-`P` union — no second connectivity path.
