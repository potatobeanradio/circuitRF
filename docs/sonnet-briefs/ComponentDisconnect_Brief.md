# circuitRF — Component Disconnect (sanctioned in-place detach) (Claude Code / Sonnet)

Implements **Disconnect**: the explicit way to free a component from its connections WITHOUT disturbing any
wires. Design is in `docs/design/placement-connectivity-and-drag-follow.md` rev 7 (read the "Disconnect"
section — if that section is missing because of a tooling glitch, the full spec is reproduced below; this
brief is authoritative). This introduces the **first persistent connectivity override** in the system, so the
spine and scope fence matter. Sub-gated; **instrument-first with a headless oracle** (it touches extraction +
drag-follow, both load-bearing). Report and STOP between layers. Firewall green.

## What Disconnect does (the spec)
`Disconnect` (component context menu; the `OnCtxDisconnect` handler is currently a TODO stub) marks **every
pin** of the selected component(s) as **detached**. A detached pin:
- renders its red **"unconnected"** box EVEN when geometrically coincident with a wire or another pin;
- is **excluded from net extraction** (its own floating net; never unioned with what it overlaps);
- on a subsequent drag, does **not** make wires follow and does **not** auto-form a wire — the component
  slides free, leaving all wires untouched.
Identical for pin-on-pin and pin-on-wire contacts. This does NOT violate the drag invariant ("a drag never
silently unconnects") — the pins were detached BEFORE the drag, so there's nothing to preserve.

## The architecture (read before coding — this is the whole point)
Connectivity is otherwise a **pure function of geometry** (`ComputeConnectivityGeometry`), recomputed every
build — single source of truth. A detached pin is connected *geometrically* but treated as unconnected, which
geometry cannot express. So it is **stored** and **persisted**, and honored as a **FILTER at each consumer
boundary** — the geometric core stays pure. Four filter points + storage + command + menu + lifecycle.

## The spine (do-not-violate)
- **The geometric core stays pure.** Do NOT add "detached" logic inside `IsConnected` /
  `ComputeConnectivityGeometry`'s coincidence math. Apply the detached flag as a filter at the four consumer
  boundaries listed below. (`IsConnected` stays a pure geometry predicate; the render mapping decides
  Unconnected for a detached port *before* calling it.)
- **One stored set, one lifecycle.** Detached state = a per-component set of detached port indices.
  Persisted in `.csch`. Clears on the component's next move (see lifecycle). No other clearing path in v1.
- **All pins, all selected components.** v1 disconnects every pin of every selected component (no per-pin UI).
- **Undoable; reuse existing command plumbing.** `DisconnectCommand` mirrors the other schematic commands
  (notify in Execute AND Undo). The clears-on-move folds into the existing `MoveCommand`.
- **Scope fence:** the detached-flag model + its four filters + the command + menu wiring + persistence +
  lifecycle. NO change to routing, dot rules, or the connectivity math itself.

## LAYER 1 — data model + persistence + the command (no behavior wired yet)
1. **Model:** add to `EditableComponent` a `HashSet<int> DetachedPorts` (port indices that are detached),
   default empty. Add helpers: `IsPortDetached(int)`, and treat empty-set as the common fast path. Include it
   in `Clone()` (copy the set). **Do NOT** let `DetachedPorts` affect anything yet.
2. **Persistence** (`SchematicPersistence` / the `.csch` writer+reader): serialize `DetachedPorts` per
   component (e.g. a small array of ints; omit when empty). Bump `format_version` (alpha: WRITTEN +
   REJECTED-on-mismatch, never migrated — per standing rule). Round-trip through save+load. `Id` still never
   persisted.
3. **Command:** `Commands/Schematic/DisconnectCommand.cs` — takes the target component ids; Execute sets each
   target's `DetachedPorts` to "all port indices" (snapshot prior sets for Undo); Undo restores the prior
   sets. Notify in BOTH (via `EditModel.NotifyChanged()`), like the other commands. Route through
   `Execute(new DotRevalidationCommand(...))` like all edits.
4. Wire `OnCtxDisconnect` (in `SchematicView.axaml.cs`, currently a stub) to call a VM method
   `DisconnectSelection()` that builds + executes the `DisconnectCommand` for the selected component(s).

**Layer 1 gate:** `DetachedPorts` exists, clones, and round-trips through `.csch` (write → read → equal);
`DisconnectCommand` sets/clears it undoably; the menu item invokes it. Nothing visually changes yet (no
filter wired). `dotnet build`/`dotnet test` green. Report.

## LAYER 2 — INSTRUMENT + the four filters (the behavior), oracle-first
Write a headless oracle FIRST documenting the target behavior, then wire the filters to make it pass.

### L2a — oracle (write, expect RED until L2b)
Add `DisconnectOracleTests` (headless, no Avalonia). For each of pin-on-pin and pin-on-wire:
- build the contact (two coincident pins; or a pin on a wire), assert BOTH ends read Connected (baseline);
- run `DisconnectCommand` on one component;
- assert: the disconnected component's pins read **Unconnected** (`BuildRenderModel`); a `NetExtractor.Extract`
  gives the detached pin its **own net** (not unioned with the overlapped wire/pin); and (pin-on-pin) the
  *other* component's pin, if the detached pin was its only neighbor, also reads Unconnected.
- Drag test: after Disconnect, `SimulateDragCommit(0, +Δ)` on the disconnected component → assert NO wire
  followed and NO auto-wire was created (wire list unchanged), and after the move the detached flags are
  CLEARED (next-move lifecycle) so geometry rules at the new spot.
Report the RED matrix. No filters yet.

### L2b — wire the four filters
1. **Render mapping** (`EditableComponent.ToRenderComponent`): for each port, if `IsPortDetached(portIndex)`
   → emit `PortConnectionState.Unconnected` WITHOUT calling the geometric `isPointConnected`. (This draws the
   red box even on overlap.)
2. **Connectivity seeding** (`ComputeConnectivityGeometry`): when adding component port positions, SKIP
   detached ports — they must not contribute to `conPointCounts` and must not be fed to the port-coincidence
   dot pass (`AddPortDot`). (Consequence, by design: a pin whose only neighbor was a now-detached pin
   correctly becomes Unconnected too — the contact is mutually gone.)
3. **Extraction seeding** (`NetExtractor.Extract`): when seeding component pins into the union-find, a detached
   port seeds a **unique synthetic key** (e.g. keyed by component id + port index, not its P-cell) so it never
   unions with the wire/pin it overlaps; it extracts as its own floating net. Its terminal binding uses that
   unique key. (Short-disable union etc. must skip detached ports too — a detached pin participates in nothing.)
4. **Drag-follow** (`SchematicViewModel`): `BuildPortMoves` and the pin-on-pin scan in
   `SnapshotDragStartPositions` must SKIP detached ports — no follow, no auto-wire from a detached pin. Also
   the live `UpdateConnectedWireEndpointsLive` portMoves build.

**L2b gate:** the L2a oracle goes GREEN (pin-on-pin + pin-on-wire both: detached → Unconnected render + own
net + no follow/auto-wire on drag). The pure connectivity math is untouched (no detached logic inside the
coincidence predicates). Existing connectivity/extraction/drag oracles still green. Report.

## LAYER 3 — the clears-on-next-move lifecycle
A detached flag persists at rest UNTIL the component is next moved, then clears (geometry re-rules at the new
position). Only the MOVED component's flags clear.
1. In `CommitDragAsCommand` (and `NudgeSelection`'s move path), for each moved component that has detached
   ports, clear its `DetachedPorts` as PART of the same `MoveCommand` (snapshot the set for Undo so one Undo
   of the move restores BOTH the position and the detached flags). Do this only for components that actually
   moved.
2. Because filters skip detached ports during the drag (Layer 2.4), the detach-drag itself adds no wires; on
   commit the flags clear so the NEXT interaction sees clean geometry.
3. Add an oracle row: Disconnect → drag onto a DIFFERENT wire → after commit the pin is Connected to the new
   wire (flags cleared, geometry rules); Disconnect → drag to empty space → Unconnected (geometry); Disconnect
   → don't move → still detached (flags persist); Undo of the move restores the detached state.

**Layer 3 gate:** lifecycle oracle green (clear-on-move; persist-at-rest; Undo restores; only-moved-clears);
save-while-detached → reload → still detached (persistence + lifecycle agree). Report.

## Acceptance
1. Disconnect (context menu, all selected) detaches all pins: red boxes even on overlap; excluded from
   extraction (own net); no wire follow / no auto-wire on the next drag — pin-on-pin AND pin-on-wire.
2. Persisted in `.csch` (format_version bumped); undoable; clears on the component's next move (Undo of the
   move restores it); persists at rest across save/load.
3. The geometric connectivity core is unchanged (detached handled only as a boundary filter at the four
   consumers). All prior oracles (pin-on-pin detection, drag-invariant, shared-point) still green.
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Geometric core stays pure** — detached is a filter at render/seed/extract/follow boundaries, never inside
  the coincidence math.
- **One stored set, persisted, clears-on-next-move** — no second clearing path; only the moved component
  clears; fold the clear into the move's `MoveCommand` for correct Undo.
- **All pins of all selected components**; undoable; notify in Execute AND Undo.
- **Instrument-first**; oracles are permanent regression tests; report+STOP between layers.
- **Scope fence:** detached model + four filters + command + menu + persistence + lifecycle only. No routing,
  dot-rule, or connectivity-math changes.
- Update `placement-connectivity-and-drag-follow.md` (mark Disconnect implemented; ensure the rev-7
  "Disconnect" section is present) + `project-file-formats.md` (the new `.csch` detached-ports field +
  version bump) + `src/Ui/CLAUDE.md` (the persistent-override seam: detached is the FIRST stored connectivity
  override; the four filter points; clears-on-move lifecycle).

## Full spec (authoritative if the design-note section is missing)
- **Detached pin** = connected geometrically, treated as unconnected. Stored as per-component
  `HashSet<int> DetachedPorts`; persisted; clears on next move of that component.
- **Four filters:** render mapping (→Unconnected/red box), connectivity seeding (skip in conPointCounts +
  dots), extraction seeding (unique key → own net), drag-follow (skip in portMoves + pin-on-pin scan).
- **Lifecycle:** persist at rest (serialized, red, excluded from netlist); clear on the component's next move
  (folded into `MoveCommand`, Undo restores); only the moved component clears.
- **v1 out of scope:** standalone "Reconnect in place" (reconnect by moving back onto the target); per-pin
  selective disconnect (v1 = all pins).
- **Does not violate the drag invariant** (pins detached before the drag).

*Exit: a user can Disconnect a component (all pins go red, it drops out of the netlist), then slide it off its
wires with zero disturbance to the wiring; moving it re-evaluates connectivity fresh. Proven by a headless
oracle; the geometric connectivity core is untouched.*
