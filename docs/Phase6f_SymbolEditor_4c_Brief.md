# Phase 6f — Symbol Editor 4c: pins + `.csym` load/save + locked gate (Claude Code / Sonnet)

Complete the Symbol Editor as a **standalone symbol authoring tool**: a **pin tool** (place pins on `P`, map
each to a port, unmapped-port = open), **`.csym` open/save** wired to the editor, and the **locked-symbol
gate** (built-in/system symbols open read-only). **This brief is 4c-now.** The **live schematic update** and
**cell-driven "open this component's symbol"** are **explicitly deferred** — they require a cell→`.csym`
indirection layer that does not exist yet and that is part of the **project-tree/workspace design scheduled
after the symbol editor** (the owner's decision). Do NOT build a cell model or rewire schematic glyph
resolution here. Read `symbol-editor.md` §1 (ownership), §3 (pins), §7.2 (locked), §10 (`.csym`) first.
Sub-gated; **report and stop between every layer.** Firewall green; every edit undoable.

> **Why live-update is deferred (important — do not work around it):** the schematic resolves a component's
> glyph + pins **statically from `SymbolKind`** via `BuiltInSymbols.Primitives(kind)` and `SymbolPortDefs.For
> (kind, portCount)`. There is **no** cell or `.csym` reference on a placed component. Making symbol edits
> flow to schematic instances requires replacing that static path with a cell→primary-`.csym` lookup — i.e.
> the cell model. That is the project-tree/workspace design, scheduled next. 4c builds everything that does
> NOT need it: authoring + persisting a standalone `.csym`. Do not invent a cell layer to force live-update.

> Read first: `docs/design/symbol-editor.md` §1/§3/§7.2/§10. Context code (from 4a/4b):
> `src/Ui/ViewModels/SymbolEditorViewModel.cs` (the `Tool` enum to extend with Pin; pointer handlers;
> `Execute`; `SnapToP` — but pins snap to `P`=100, NOT `p`=5; add a `SnapToConnectionGrid`),
> `src/Ui/Schematic/SymbolModel.cs` (`SymbolPin` already exists — LocalX/LocalY/PortIndex/Name; `EditableSymbol`),
> `src/Ui/Schematic/SymbolPersistence.cs` (`.csym` read/write from steps 1–2 — round-trips primitives + pins
> already), `src/Ui/Schematic/SymbolEditorOverlay.cs` (overlay — add pin markers + unmapped-port hints),
> `src/Ui/Renderers/SchematicRenderer.cs` (`DrawSymbol` — pins are drawn as markers; mirror the schematic
> port-marker style), `src/Ui/Controls/SymbolEditorCanvas.cs` + `Views/Content/SymbolEditorView.axaml(.cs)`
> (canvas + toolbar), `src/Ui/ViewModels/WorkspaceViewModel.cs` (`OpenSymbolEditorDocked/Window` — the open
> path to extend with `.csym` open/save; `EditableSymbol.FromSymbol`), `src/Ui/Commands/Symbol/*` (the command
> pattern). Design docs win on any conflict.

## The spine (do not violate)
- **Pins snap to the connection grid `P` (=100), NOT the fine grid `p`.** Body art is on `p`; pins are on `P`
  (grid-and-connectivity.md R2). Add a `P`-snap for the pin tool distinct from the existing `p`-snap for art.
- **A pin maps to a port index; an unmapped port = open circuit** (§3). The editor shows the symbol's port
  set (for a standalone `.csym`, the port count is a property of the symbol being edited — see Layer 1) and
  which ports are mapped vs. unmapped.
- **Every edit undoable** on the editor's own stack (Execute+Undo both notify) — pin place/move/delete/remap
  are commands like the primitive commands.
- **Locked symbols open read-only** (§7.2): a `UserEditable=false` symbol can be viewed but not edited.
- **Scope fence (4c-now):** pins + `.csym` open/save + locked gate. **NO live schematic update, NO cell
  model, NO cell-driven open, NO rewiring of `SymbolKind`→`BuiltInSymbols`.** Those are the deferred
  project-tree/workspace work.

---

## LAYER 1 — port context for a standalone symbol + pin commands

1. **Port count for the edited symbol.** A standalone `.csym` needs to know how many ports it can map pins
   to. For 4c, carry a **port count on the `EditableSymbol`** (e.g. `int PortCount`) — set when opening
   (a built-in opens with its `SymbolPortDefs.For(kind).Length`; a fresh/`.csym` symbol carries its own,
   persisted). This is the symbol's own port count for authoring; it is NOT the cell model (no cell ref, no
   primary-symbol concept) — just "this symbol has N mappable ports." Persist it in `.csym` (add to the
   schema; alpha policy = fresh write).
2. **Pin commands** (`Commands/Symbol/`), mirroring the primitive commands (notify both directions):
   - `PlaceSymbolPinCommand` (add a `SymbolPin` at an on-`P` point, mapped to a chosen port index).
   - `MoveSymbolPinCommand` (relocate a pin, snapped to `P`).
   - `DeleteSymbolPinCommand` (remove a pin → that port becomes unmapped/open).
   - `RemapSymbolPinCommand` (change a pin's `PortIndex`).
3. **EditableSymbol pin access:** ensure `EditableSymbol` exposes its `Pins` list mutably and round-trips
   pins through `ToSymbol()/FromSymbol()` (the model `Symbol.Pins` already exists).

**Layer 1 gate:** `EditableSymbol` carries a port count (persisted in `.csym`); the four pin commands exist
and notify both directions; a unit test places/moves/remaps/deletes a pin undoably. Report.

---

## LAYER 2 — the Pin tool (place / move / map) + on-`P` snap

1. **Tool:** add `Pin` to the `SymbolEditorViewModel.Tool` enum. When active:
   - Press on empty space → place a pin at the point **snapped to `P`** (add `SnapToConnectionGrid(v) =>
     Math.Round(v/100)*100`), mapped to the **next unmapped port** (or prompt for the port — pick the simpler;
     "next unmapped, then editable" is fine). Commit via `PlaceSymbolPinCommand`.
   - Press on an existing pin → select it; drag → move (snapped to `P`), commit `MoveSymbolPinCommand` on
     release.
   - A pin's port mapping is editable: a small control (combo/field) on the selected pin sets its `PortIndex`
     → `RemapSymbolPinCommand`. Delete key on a selected pin → `DeleteSymbolPinCommand`.
2. **Pins are distinct from primitives in selection** (a pin is not a `SymbolPrimitive`) — the Select tool may
   also select/move pins, or keep pin editing to the Pin tool (pick one; the Pin tool owning pin
   select/move/remap is simplest and avoids mixing pin/primitive selection). State the choice.
3. **Pins snap to `P`, never `p`** — verify a placed/moved pin lands exactly on a 100-multiple.

**Layer 2 gate:** with the Pin tool, place pins (snapped to `P`), map them to ports, move/remap/delete them —
all undoable; a pin always lands on `P`. Report.

---

## LAYER 3 — pin rendering + unmapped-port surfacing

1. **Render pins** in the editor canvas: draw a pin marker at each pin's location (mirror the schematic's
   port-marker visual — a small dot/box in the connection-point role). Show the **mapped port number/name**
   next to each pin.
2. **Unmapped ports:** surface which of the symbol's ports have no pin — e.g. a small non-blocking note/list
   ("Port 3: unmapped → open") in the editor, and/or a distinct marker. This is informational, never a hard
   error (an unmapped port is legal = open circuit, §3).
3. Add pin info to `SymbolEditorOverlay` (pin positions + mapped indices + the unmapped-port set) so the
   canvas renders it; keep the overlay framework-free.

**Layer 3 gate:** pins render with their port mapping; unmapped ports are clearly (non-blockingly) surfaced;
the display updates live as pins are placed/removed. Report.

---

## LAYER 4 — `.csym` open / save wired to the editor + locked gate

1. **Open `.csym`:** add an "Open Symbol…" path that loads a `.csym` (via `SymbolPersistence`) into a new
   editor (docked or window). Keep the existing built-in demo opens, but add real file open. **Save / Save
   As `.csym`:** write the current `EditableSymbol` (primitives + pins + port count) back via
   `SymbolPersistence`. Wire to the editor's menu/toolbar (and/or the workspace menu next to the existing
   Symbol Editor entries).
2. **Locked gate (§7.2):** add a `UserEditable` flag to the symbol (or to the open call). When a symbol is
   **locked** (built-in standard library, and later sim/VAR/Measurement), the editor opens it **read-only**:
   tools that mutate are disabled (or the canvas rejects edits) and a clear "read-only (system symbol)"
   indicator shows. Built-in symbols opened via the existing demo commands should open **read-only** (they
   are the standard library). A `.csym` opened from a user file opens editable.
3. **Dirty/save affordance:** a minimal "unsaved changes" indicator + save is enough; full document-lifecycle
   (new/save-prompts-on-close) can be light for 4c.

**Layer 4 gate:** open a `.csym` into the editor, edit it, save it back (round-trips); a built-in/locked
symbol opens read-only with the edit tools disabled; a user `.csym` opens editable. Report.

---

## Acceptance (4c-now)
1. A Pin tool places pins snapped to `P`, maps each to a port index, and supports move/remap/delete — all
   undoable; unmapped ports are surfaced non-blockingly as "open".
2. Pins render in the editor with their port mapping; the symbol carries its own port count (persisted).
3. `.csym` open/save is wired to the editor and round-trips primitives + pins + port count.
4. Locked/built-in symbols open read-only (edit tools disabled, indicator shown); user `.csym` opens editable.
5. `dotnet build`/`dotnet test` green; firewall green; **NO live schematic update, NO cell model, NO
   cell-driven open, NO `SymbolKind`→`BuiltInSymbols` rewiring** (deferred to project-tree/workspace design);
   nothing in prior phases regresses.

## Guardrails
- **Pins on `P` (=100); art on `p` (=5)** — separate snaps; never snap a pin to `p`.
- **Unmapped port = open, never an error** (§3) — surface informationally only.
- **Every pin edit undoable** on the editor's own stack (Execute+Undo notify), like the primitive commands.
- **Locked symbols are read-only** — built-ins open read-only; user `.csym` editable.
- **Hard defer:** no live schematic update, no cell model, no cell-driven open, no rewiring of how the
  schematic resolves glyphs. If a task seems to need the cell model, STOP and report — it belongs to the
  project-tree/workspace design, not here.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `symbol-editor.md` §11 status (4c-now done; live-update + cell-open deferred to project-tree design)
  and `src/Ui/CLAUDE.md` (pins-on-P; locked gate; `.csym` I/O; the deferred cell dependency).

*Exit: the Symbol Editor is a complete standalone authoring tool — draw primitives (4b), place pins mapped to
ports (snapped to P), open/save `.csym`, with system symbols read-only — leaving live schematic update and
cell-driven opening to the project-tree/workspace design that follows.*
