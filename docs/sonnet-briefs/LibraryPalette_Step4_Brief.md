# Library Palette — Step 4: the placement state machine (the core) (Claude Code / Sonnet)

The heart of the Palette: clicking a tile **arms** a placement; any schematic canvas shows a **ghost** that
follows the cursor, **rotates** (R/Ctrl-R), commits on click (creating a connected component instance), and
**stays armed** for repeat placement; **Esc / click-armed-tile / switch-tile** cancels-or-switches. The armed
state is **app-level** so it works across any schematic (tabbed or torn-off). **This brief is step 4.**
Drag-and-drop is step 5. Read `library-palette.md` §6 first. Sub-gated; **report and stop between every
layer.** Firewall green.

> Read first: `docs/design/library-palette.md` §6 (the placement state machine — palette arms, schematic owns
> the interaction, app-level armed state, stay-armed, connectivity-union on commit). Context code:
> `src/Ui/ViewModels/SchematicViewModel.cs` (**already has** `Tool.Place` + `_placementSymbol`/`_placementRot`/
> `_placementMirrorX`; `Overlay.Ghost`; `CancelCurrentOp`; `Execute(IUiCommand)`; `GetComponentAtPoint` —
> the per-schematic placement path to drive), `src/Ui/Commands/Schematic/PlaceComponentCommand.cs` (the commit
> primitive — adds an `EditableComponent`), `src/Ui/Schematic/EditableSchematic.cs` (`EditableComponent` +
> `SchematicEditModel`; `ComputeConnectivityGeometry` — the **on-`P` union** to reuse for connect-on-commit),
> `src/Ui/Controls/SchematicCanvas.cs` (pointer handling + `ActiveTool` cursor; where ghost-follow + commit
> click live), `src/Ui/ViewModels/Dock/PaletteTool.cs` + `src/Ui/Controls/PaletteTile.axaml(.cs)` (the tiles
> that arm), `src/Ui/Schematic/ComponentTypeRegistry.cs` (`InstancePrefix`, `DefaultParameters` — auto-name +
> defaults on commit), `src/Ui/ViewModels/WorkspaceViewModel.cs` (app-level state lives here / on a shared
> service; `_factory`, the active document). Design docs win on any conflict.

## The spine (do not violate)
- **The Palette ARMS; the schematic OWNS the interaction** (§6). Arming sets an **app-level**
  `PendingPlacement { SymbolKind Kind, int PortCount, SymbolRotation Rotation }` (observable), NOT per-canvas
  state — so the armed part can drop into **any** schematic in view (tabbed or torn-off). Each schematic
  canvas *reads* the armed state.
- **Reuse the existing `Tool.Place` path** — the schematic already has placement plumbing
  (`_placementSymbol`/`_placementRot`, `Overlay.Ghost`, `PlaceComponentCommand`). Drive it from the app-level
  armed state; don't fork a second placement mechanism.
- **Connectivity on commit reuses the on-`P` union** — a placed component's pins landing on a wire/pin connect
  by the **same `ComputeConnectivityGeometry`/extraction union logic** (single source). Do NOT write a second
  connectivity path; placement just adds the component at a snapped position and the existing union handles
  the rest (verify pins-on-wires read as connected after commit).
- **Stay armed (owner decision)** — after a commit, placement remains armed (place several); only Esc /
  click-the-armed-tile / switch-tile disarms-or-switches.
- **Recently-Used MRU on commit** — each commit pushes the type to a persisted MRU (AppPreferences), feeding
  the step-3 Recently-Used category.
- **Scope fence (step 4):** arm/ghost/rotate/commit/connect/stay-armed + MRU. NO drag-and-drop (step 5).

---

## LAYER 1 — app-level armed state + tile arming

1. **`PendingPlacement`** (app-level, observable) — on `WorkspaceViewModel` or a small shared service the
   canvases can read: `{ SymbolKind Kind, int PortCount, SymbolRotation Rotation }` or null (nothing armed).
2. **Tile arming:** clicking a `PaletteTile` sets `PendingPlacement` to that item's kind/portcount (rotation
   R0); the tile shows **armed/depressed** (the `IsArmed` visual from step 2, now driven). Clicking the
   **armed** tile → disarm (clear). Clicking a **different** tile → switch (re-point). Only one armed at a
   time (the Palette reflects which by comparing each tile to `PendingPlacement.Kind`).
3. **Esc** anywhere clears `PendingPlacement` (and un-depresses the tile).

**Layer 1 gate:** clicking a tile arms it (depressed) + sets `PendingPlacement`; clicking it again disarms;
clicking another switches; Esc clears. (No ghost/commit yet — just the armed state + tile visual.) Report.

---

## LAYER 2 — ghost-follow + rotate in any schematic canvas

1. When `PendingPlacement` is non-null, each **`SchematicCanvas`** enters placement mode: a **ghost** of the
   armed symbol (snapped to grid `p`) follows the cursor (reuse `Overlay.Ghost` + the existing `Tool.Place`
   ghost render). The canvas reads the app-level armed state (subscribe to it), so **any** open schematic —
   active tab or torn-off window — shows the ghost when hovered.
2. **Rotate:** `R` / `Ctrl-R` rotates the armed `PendingPlacement.Rotation` (updates the ghost live). (R =
   CW, Ctrl-R = CCW, matching the existing component rotate convention.)
3. **Esc** cancels (clears armed state + ghost) — already wired in L1; ensure the ghost disappears.

**Layer 2 gate:** arming a tile then moving over a schematic shows a grid-snapped ghost following the cursor;
R/Ctrl-R rotates it; moving to a torn-off schematic window shows the ghost there too; Esc clears it. Report.

---

## LAYER 3 — commit (place + connect) + stay-armed + MRU

1. **Commit:** clicking in a schematic with `PendingPlacement` set **places** an `EditableComponent` at the
   snapped cursor position with the current rotation, via `PlaceComponentCommand` (one undoable command on
   that schematic's stack). Auto-name from `ComponentTypeRegistry.InstancePrefix` (next free number) + apply
   `DefaultParameters`. After commit, the existing **on-`P` connectivity union** connects pins landing on
   wires/pins (verify — reuse, don't re-derive).
2. **Stay armed:** after commit, `PendingPlacement` **remains set** (place another); the tile stays depressed.
   A status hint (Messages or a small indicator) may show "Placing R — Esc to stop".
3. **MRU:** each commit pushes the kind to a persisted Recently-Used list in `AppPreferences` (MRU, dedup,
   cap ~12) — the step-3 Recently-Used category reads this. Wire the Palette's Recently-Used to the live MRU.
4. **Cancel paths:** Esc / click-armed-tile / switch-tile all behave per L1 mid-placement.

**Layer 3 gate:** arming R and clicking places a connected R (pins on a wire read connected), auto-named with
defaults, undoable; placement stays armed so a second click places another; Esc stops; the placed kinds appear
in the Recently-Used category (persisted across restart). Report.

## Acceptance (step 4)
1. An app-level `PendingPlacement` (observable) is armed by tiles (depressed/disarm/switch/Esc) and read by
   every schematic canvas (tabbed + torn-off).
2. A ghost follows the cursor in any schematic, rotates with R/Ctrl-R; click commits a placed, auto-named,
   default-parametered, **connected** `EditableComponent` (reusing the on-`P` union), undoable; placement
   **stays armed**.
3. Commits feed a persisted Recently-Used MRU (AppPreferences) that the step-3 Recently-Used category shows.
4. `dotnet build`/`dotnet test` green; firewall green (app-level state framework-free or in src/Ui; no SKColor
   leak); **no drag-and-drop** (step 5); nothing else regresses.

## Guardrails
- **Palette arms, schematic owns the interaction; armed state is app-level** (works across tabbed + torn-off).
- **Reuse `Tool.Place`/`PlaceComponentCommand`** — don't fork placement; **reuse the on-`P` connectivity
  union** — don't re-derive connect-on-commit.
- **Stay armed after commit** (owner decision); Esc / armed-tile / switch-tile cancel-or-switch.
- **MRU persisted** in AppPreferences (mirror Recent Workspaces).
- **Scope fence:** arm/ghost/rotate/commit/connect/stay-armed/MRU only — no drag-and-drop.
- Sub-gate the three layers; report and stop between each.
- Update `library-palette.md` §10 status (step 4 done) and `src/Ui/CLAUDE.md` (app-level PendingPlacement;
  Palette arms, canvas commits + connects via the on-`P` union; stay-armed; Recently-Used MRU).

*Exit: the Palette places components — arm a tile, ghost-follow across any schematic, rotate, click to place a
connected instance, stay armed for repeats — the core interaction; drag-and-drop (step 5) adds the second
path, then docs (step 6).*
