# Brief J — Auto-generate a symbol when placing a symbol-less cell

**Scope:** when the user drops a cell that has **no symbol at all** (`NoView` — empty `symbol/`
folder), prompt: *"A symbol for {cell} has not been created. Do you want one to be auto-generated?"*
**Yes** → generate a default `.csym`, save it, refresh the tree, and finish the placement rendering
the new symbol. **No / dismissed** → cancel the placement entirely (nothing is added).

**Prereq:** Brief I landed (cell placement core). This brief slots into the seam Brief I marked in
`CommitCellPlacement` for the `NoView` case.

**Firewall:** UI layer; symbol model/persistence are framework-free; the prompt is Avalonia (UI).

---

## Read first (real names)

- **`docs/design/symbol-editor.md` §9 "Auto-generator" — the AUTHORITATIVE spec for the generated
  symbol's layout. Implement Layer 2 to match it exactly.** Also relevant: §4 (orientation table —
  the auto-gen box is **horizontal**, left/right ports), §2.4 (pin tips on `P`, body art on `p`),
  §10 (`.csym` persistence), and §11 step 7 (the auto-generator is a planned step; this brief's
  placement-time prompt is an additional entry point to the SAME generator, so build it reusable).
- `src/Ui/ViewModels/SchematicViewModel.cs` — `CommitCellPlacement` (Brief I). The `NoView` seam is
  where this brief hooks. Note how the VM raises user prompts / toasts today (grep `Messages.` /
  dialog service) — reuse that channel for the Yes/No prompt; **No-response/dismiss must CANCEL**
  (no instance placed, no symbol written).
- `src/Ui/Schematic/CellFolder.cs` — `ResolvePrimary(cellDir, ViewType.Symbol)` →
  `PrimaryState.NoView` is the "no .csym at all" case (vs `NoPrimary`/`MissingNamedPrimary`, which
  are NOT auto-gen — symbols exist). `SubFolderPath(cellDir, ViewType.Symbol)`,
  `ViewExtension(ViewType.Symbol)` = `.csym`.
- `src/Ui/Schematic/CellPersistence.cs` — `CcellFile.NumPorts` (Brief E2; the port count the
  generated symbol must match) and `CcellFile.PrimarySymbol`.
- `src/Ui/Schematic/SymbolPersistence.cs` — `SaveToFile(path, Symbol|EditableSymbol)` (confirm the
  exact signature) — how a `.csym` is written.
- `src/Ui/Schematic/SymbolModel.cs` — `Symbol`, `SymbolPin`, the primitive types (`RectPrimitive`/
  `RoundedRectPrimitive`, `LinePrimitive`, `TextPrimitive`), `SymbolColorRole` (use `SymbolLine`/
  `SymbolText` — never literal colors, §2.3), `SymbolStrokeTier` (`Normal`/`Thin`/`Thick` — the inner
  rectangle uses a **thinner** tier than the outer, per §9).
- `src/Ui/Schematic/EditableSymbol.cs` — if `SaveToFile` wants an `EditableSymbol`, how to build one
  with primitives + pins.
- `src/Ui/Schematic/BuiltInSymbols.cs` — `BuildGeneric()` (a horizontal box) and `GeneratePorts(n)`
  (the existing `P`-multiple, 200-unit port spacing) for reference. **NOTE:** `GeneratePorts` uses a
  ceil/floor left-right split — the §9 auto-generator uses **odd-left / even-right** instead, so do
  NOT copy `GeneratePorts`' split; follow §9. Reuse only its `P`-spacing convention.
- `src/Ui/Schematic/CellSymbolResolver.cs` — `Invalidate(cellAbsDir)` / `InvalidateAll()` to refresh
  after the new `.csym` is written so the placed instance resolves to it.
- The Project Tree refresh hook — `ProjectTreeTool.Refresh()` (used elsewhere after file writes).

---

## Spine (do-not-violate)

1. The prompt fires **only** for `NoView` (truly no symbol). `NoPrimary`/`MissingNamedPrimary` are
   handled by Brief I (placeholder + warning), NOT auto-gen.
2. **No / dismiss ⇒ cancel the whole placement** — no `.csym` written, no instance added, tree
   unchanged. (The placement command must not have run yet, or must be rolled back.)
3. **Yes ⇒** generate a `.csym` whose pin count = the cell's `NumPorts`, **laid out per
   `symbol-editor.md` §9** (outer + inset inner rect, odd-left/even-right, leads out + pins outside,
   port-number text inside the inner rect, height grows with N, 3-port special case), save it to the
   cell's `symbol/` folder, set it primary (or rely on `SoleFile` since it's the only symbol), refresh
   the tree, invalidate the resolver, and finish the placement so the instance renders the new symbol.
4. The generated symbol is a sensible default the user can edit later — the **§9 box**, NOT an
   ad-hoc layout. Build the generator as a **reusable function** matching §9 so the future
   "Rebuild Symbol Automatically" command (§11 step 7) calls the same code — docs stay consistent.
5. Alpha persistence: write `.csym` with the current `format_version`; never migrate.

---

## Layer 1 — Hook the `NoView` case in `CommitCellPlacement`

Restructure the Brief-I seam so that, on `NoView`:
- Show the prompt: title/body *"A symbol for `{cellName}` has not been created. Do you want one to
  be auto-generated?"* with **Yes** / **No** (and treat window-dismiss as No).
- **No/dismiss:** return without placing anything (cancel).
- **Yes:** run Layer 2 (generate + save), then Layer 3 (refresh + resolve), then continue the normal
  placement (build the `EditableComponent` + `PlaceComponentCommand`) — now resolving to the new
  symbol.
- Order matters: do NOT add the instance before the user answers. Either resolve the symbol state
  first and only build/commit the component after Yes, or build it but only `Execute` the place
  command after the symbol exists. Simplest: decide symbol availability up front, then place once.

**Gate 1:** Dropping a cell with an empty `symbol/` folder shows the prompt. No → nothing happens
(no instance, no file). Yes → proceeds to generate.

---

## Layer 2 — Generate the symbol per `symbol-editor.md` §9

Add a framework-free generator (e.g. `AutoSymbolGenerator.Generate(cellName, numPorts)` in
`src/Ui/Schematic/`) returning a `Symbol`/`EditableSymbol`. **Read §9 and implement it precisely**;
the rules (do not approximate):

- **Two rectangles:** an **outer rectangle** plus a **slightly inset inner rectangle** drawn at a
  **thinner** stroke tier (outer `Normal`, inner `Thin`). Both stroked, not filled; `SymbolLine` role.
- **Horizontal box** (§4): ports on the left and right edges.
- **Port assignment:** **odd port numbers down the LEFT edge** (1, 3, 5, …), **even down the RIGHT**
  (2, 4, 6, …), each side **descending**. (Do NOT use `GeneratePorts`' ceil/floor split.)
- **Odd total N:** the last odd port sits alone on the left (unbalanced) — keep this; it's the
  conventional generated-box look.
- **3-port special case (N=3 ONLY):** **port 1 on the LEFT at vertical center**, **ports 2 and 3 on
  the RIGHT** (FET-like). This overrides odd-left/even-right for N=3 only; all other N use the
  general rule.
- **Per port:** a **short lead** drawn **outward** from the outer rectangle, with the **pin placed at
  the lead's END, OUTSIDE** the rectangle. **Port-number text** (`TextPrimitive`, `SymbolText` role)
  **inside the inner rectangle**, near its port.
- **Height grows with port count** to fit the per-side ports (more ports → taller box).
- **Grids (§2.4 / R6):** **pin tips on `P`** — port spacing a `P` multiple (reuse the 200-unit / 2-cell
  spacing `GeneratePorts` uses); the rectangles, leads, and text are body art on the fine grid `p`.
- **Pins:** one `SymbolPin(localX, localY, portIndex, name)` per port, `portIndex` 0-based mapping to
  cell port `Num` (Brief G), name = the port number/name. Pin tip = lead end, on `P`.

`numPorts` comes from `.ccell` `NumPorts`. If `NumPorts == 0` (unset), default to 2 and note it
(prefer a usable 2-port box over a 0-pin box).

Save via `SymbolPersistence.SaveToFile` to
`Path.Combine(CellFolder.SubFolderPath(cellAbsDir, ViewType.Symbol), "{cellName}.csym")`. Since it's
the only `.csym`, `ResolvePrimary` returns `SoleFile` → automatically primary; optionally also write
`CcellFile.PrimarySymbol` for explicitness.

**§9 conformance is the gate, not just "a box with pins."** If anything in §9 is ambiguous against the
code, follow §9's text and note the interpretation in your report.

**Gate 2:** Yes generates `symbol/{cellName}.csym` matching §9: outer+inner rect (inner thinner),
odd-left/even-right ports with leads outward and pins outside, port numbers inside the inner rect,
height scaled to N, pin tips on `P`; **N=3 uses the 1-left / 2,3-right special case**. The file loads
via `SymbolPersistence.LoadFromFile`.

---

## Layer 3 — Refresh, resolve, and finish placement

After the `.csym` is written:
- `CellSymbolResolver.Invalidate(cellAbsDir)` (or `InvalidateAll()`).
- `ProjectTreeTool.Refresh()` so the new `.csym` appears under the cell's `symbol/` node.
- Continue the placement: build the `EditableComponent` (CellRef, `X{n}`, seeded params) and run
  `PlaceComponentCommand`. `BuildRenderModel` now resolves to the generated symbol → the instance
  renders with the box + pins.

**Gate 3:** After Yes, the placed cell renders with the generated box/pins; the project tree shows
the new `.csym`; opening it in the Symbol Editor works; the user can edit it and the placed instance
updates (Brief I, Layer 6).

---

## Acceptance
- Symbol-less cell drop → prompt; No/dismiss cancels everything; Yes generates + saves a `.csym`
  **conforming to `symbol-editor.md` §9** (outer+inner rect, odd-left/even-right, leads+outside pins,
  port numbers in inner rect, height ∝ N, N=3 special case, pin tips on `P`) matching `NumPorts`,
  refreshes the tree, and finishes placement rendering the new symbol. ✅
- `NoPrimary`/`MissingNamedPrimary` cells are NOT auto-generated (Brief I's behavior unchanged). ✅
- The generated symbol is a valid, editable §9 box; the generator is reusable by the future
  "Rebuild Symbol Automatically" command. ✅

## Guardrails
- Only `NoView` triggers auto-gen. No/dismiss must leave the workspace byte-for-byte unchanged.
- One `.csym` write via `SymbolPersistence.SaveToFile`; honor `format_version`; no migration.
- Keep the generator framework-free; the prompt is the only UI piece.
- Don't change Brief I's happy-path placement; just fill the `NoView` seam.
- Minimal diff; list files touched.

## Scope fence (NOT here)
- No symbol-editor changes beyond saving a valid `.csym`. No Push-Into.
- **Not** the explicit "Rebuild Symbol Automatically" menu command (§9 / §11 step 7) — this brief only
  adds the **placement-time** prompt entry point. Build the generator reusable so that command can
  call it later, but do not implement the menu command, the regeneration warning, or any "replace
  existing symbol" flow here (only the no-symbol `NoView` case).

## Exit / report
State: the generator API + a brief confirmation it matches each §9 rule (two rects, odd-left/even-
right, leads+outside pins, inner-rect port text, height∝N, **N=3 special case**, pin tips on `P`);
any §9 interpretation you had to make; the default `.csym` name; how No/dismiss guarantees a full
cancel; and the refresh/resolve/finish sequence. Confirm the 3 gates run mentally.
