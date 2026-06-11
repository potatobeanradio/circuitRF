# circuitRF — Drag-Invariant: a connection never silently breaks on drag (Claude Code / Sonnet)

Implements the governing invariant from `docs/design/placement-connectivity-and-drag-follow.md` (rev 3):

> **A connection, once made, is never silently broken by a drag.** Dragging a component or a wire makes the
> geometry adapt — wire segments form, stretch, and re-route live — so every existing pin contact
> (pin-on-wire and pin-on-pin) is preserved. Connection beats tidiness: extra bends/jogs are acceptable.

**PREREQUISITE — do not start until landed & confirmed:** pin-on-pin **detection**
(`docs/LibraryPalette_PinOnPinConnectivity_Brief.md`). The editor must *know* two pins are connected before a
drag can preserve that connection. If detection isn't in yet, stop and say so.

This is intricate, load-bearing code with a history of "fixed but unchanged." **Every layer is
instrument-first with a permanent headless oracle test** that asserts the connection survives a simulated drag
commit — "fixed" is proven by a green test, never by a claim. Sub-gated; report and STOP between each layer.
Firewall green.

> Read first: `docs/design/placement-connectivity-and-drag-follow.md` (rev 3 — the invariant + the four
> cases). Context code (all `src/Ui/`):
> - `ViewModels/SchematicViewModel.cs` — the drag system: `HandleSelectDrag` (component live drag →
>   `UpdateConnectedWireEndpointsLive`), `CommitDragAsCommand` (commit + `followWireSnaps` + the landed
>   T-body-follow block + `RouteBodyFollow`), `UpdateDragOverlay`/`WireDragPoints` (fast live overlay,
>   perf-gated `LiveDotMaxObjects`), `BuildPortMoves`; the wire/segment drag paths
>   `ApplyWireDragLive`/`ComputeWireDragEndPoints`/`HandleSegmentDragLive`/`CommitSegmentDragAsCommand`,
>   pinning `ShouldPinDraggedEndpoint`/`IsWireEndpointConnectedToUnselected`/`ComputeSlideClamp`, stem-follow
>   `FindStemsOnSegment`/`RouteStem`/`StemFollow`, merge `TryBuildMergeCommand`; `SnapshotDragStartPositions`,
>   `ClearDragState`, `CancelCurrentOp`. Every edit wraps `Execute(new DotRevalidationCommand(...))`.
> - `Schematic/EditableSchematic.cs` — `BuildRenderModel`/`IsConnected` (post-detection-fix: ports
>   participate), `ComputeConnectivityGeometry`, `SymbolPortDefs.For(kind,portCount)`,
>   `EditableComponent.GetPortWorldCoord`, `ConnectTolerance`, `QuantKey`.
> - `Schematic/WireGeometry.cs` — `OrthogonalRoute`, `NormalizePoints` (the only routing/simplify primitives
>   to use). `Schematic/SchematicGeometry.cs` — `CoincidentPoints`/`PointOnSegmentInterior`.
> - `Commands/Schematic/MoveCommand.cs`, `PlaceWireCommand.cs`, `CompositeCommand.cs` (the undoable units).

## The spine (do-not-violate)
- **The invariant is the spec.** A contact that exists before a drag exists during and after it. No drag path
  may drop a pin contact.
- **One connectivity source.** Detect/preserve contacts with the SAME
  `QuantKey`/`CoincidentPoints`/`PointOnSegmentInterior` + `ConnectTolerance` the connectivity pass uses.
  Never a second predicate.
- **Reuse existing routing.** `WireGeometry.OrthogonalRoute` + `NormalizePoints` only; reuse
  `RouteStem`/`RouteBodyFollow` for follows; the auto-wire (case 2) is a normal `EditableWire` routed by
  `OrthogonalRoute`. No new routing math.
- **One undoable action per drag.** Component move + every re-routed wire + any auto-created wire commit as a
  single `MoveCommand`/`CompositeCommand`; one Undo restores the entire pre-drag state.
- **Live = overlay, commit = model.** Preserve connections live via the fast overlay
  (`UpdateDragOverlay`/`WireDragPoints`, perf-gated); fold final geometry into the commit. No full
  `BuildRenderModel` per tick.
- **O(N), perf-gated** (`LiveDotMaxObjects`); above the cap, live preview may simplify but the COMMIT must
  still preserve the connection.
- **Scope fence:** the three drag cases below + their oracle tests. NOT rigid component coupling, NOT
  autorouting/obstacle avoidance, NOT extraction changes, NOT the detection fix (prerequisite).

## LAYER 0 — INSTRUMENT: oracle harness + status of each case
Before any change, build a headless (no-Avalonia) test harness that constructs a `SchematicEditModel`,
simulates a drag by invoking the SAME commit path the VM uses (factor a testable commit entry if needed —
e.g. call into the move/commit logic with a given delta), and asserts on the rebuilt model's connectivity.
Use it to **document current behavior** for each case:
1. **Case 1 (pin on wire endpoint):** component + wire endpoint on its pin; drag component; assert wire
   endpoint tracks the pin and the pin stays Connected. *(expect: PASS today.)*
2. **Case 1 (pin on wire body / T):** component pin mid-span on a wire; drag component; assert the wire
   re-routes through the moved pin and stays Connected. *(landed code — confirm PASS or FAIL.)*
3. **Case 2 (pin-on-pin):** two components pin-to-pin; drag one away; assert a wire now connects the two pins
   and both stay Connected. *(expect: FAIL today — no auto-wire.)*
4. **Case 3 (wire dragged off a pin):** wire endpoint on a component pin; drag the WIRE body; assert the
   endpoint stays pinned to the pin (new segments form) and stays Connected. *(confirm PASS or FAIL.)*
**Report the pass/fail matrix — no fixes yet.** This matrix tells us exactly which cases need work and becomes
the permanent regression suite.

**Layer 0 gate:** harness runs headless; a 4-row pass/fail matrix is reported. Report and STOP.

## LAYER 1 — Case 1 hardening (pin-on-wire follow): make the landed code provably correct
For any Case-1 row that FAILED in Layer 0 (esp. the T-body follow), fix it reusing
`RouteStem`/`RouteBodyFollow` + the existing follow blocks — do not rewrite the routing. Ensure the live path
(`UpdateConnectedWireEndpointsLive` + `UpdateDragOverlay`/`WireDragPoints`) and the commit path
(`CommitDragAsCommand` follow-snaps) BOTH preserve the contact, and both fold into one `MoveCommand`.

**Layer 1 gate:** both Case-1 oracle rows green (endpoint + T-body), live + committed; endpoint-follow and
existing wire/segment drag/pin/merge unregressed. Report and STOP.

## LAYER 2 — Case 3 hardening (wire dragged, pin held): never drop a pin contact
Ensure every WIRE-drag path keeps a connected endpoint pinned to its component pin. `ShouldPinDraggedEndpoint`
already pins endpoints to ports for *segment* drags; confirm the same holds for whole-wire drags
(`ApplyWireDragLive`/`ComputeWireDragEndPoints`) — a wire endpoint on a component pin must stay on that pin,
with jogs/new segments forming to bridge the rest of the wire's motion. Fix gaps reusing the existing pinning
+ `OrthogonalRoute` jog approach.

**Layer 2 gate:** Case-3 oracle row green (drag a wire connected to a pin → endpoint stays on the pin, new
segments adapt, stays Connected); segment-drag pinning + slide-clamp unregressed. Report and STOP.

## LAYER 3 — Case 2 (the new behavior): auto-form a wire when a pin-on-pin contact separates
This is the genuinely new behavior and depends on the detection prerequisite. When a dragged component has a
pin that, at drag START, was **coincident with another (unselected) component's pin**, and the drag separates
them, **auto-create a wire** from the stationary pin to the moving pin and re-route it live; commit it as part
of the same undoable action.
1. **Snapshot at drag start:** in `SnapshotDragStartPositions`, record each moving-component port that is
   coincident (`CoincidentPoints`, `ConnectTolerance`) with an **unselected** component port — store
   (stationaryPinWorld, movingPin identity). Reuse the connectivity predicate; exclude ports already on a wire
   (those are Case 1 — avoid double-handling).
2. **Live:** while dragging, for each such pair where the pins have separated, show a preview wire
   (stationary pin → current moving-pin position, `OrthogonalRoute`) in the overlay `WireDragPoints` (perf
   gated). If the drag keeps them coincident, no wire.
3. **Commit:** for each pair still separated at release, create an `EditableWire`
   (`OrthogonalRoute(stationaryPin, movedPin)` → `NormalizePoints`) and include it in the SAME undoable unit
   as the move (`CompositeCommand(MoveCommand, PlaceWireCommand…)` or fold into the move) so one Undo removes
   the wire and restores the component. The new wire is a normal editable/deletable wire; both pins read
   Connected (now via a wire). Guard: don't duplicate a wire if one already exists between those pins.
4. **Multi-pin / multi-pair:** a component with several pin-on-pin contacts auto-forms one wire per separated
   pair.

**Layer 3 gate:** Case-2 oracle row green (two pins pin-on-pin → drag apart → exactly one wire connects them,
both Connected, one Undo restores; staying coincident forms no wire; no duplicate wires); all earlier rows
still green. Report and STOP.

## Acceptance
1. The 4-row oracle matrix is all green and committed as a permanent headless regression suite: Case 1
   (endpoint + T-body), Case 2 (pin-on-pin auto-wire on separation), Case 3 (wire-drag keeps pin).
2. In-app: dragging a component never disconnects a pin (wire follows / re-routes; pin-on-pin spawns a
   stretching wire); dragging a wire never disconnects it from a pin (segments adapt). One Undo per drag
   restores everything.
3. No regression to endpoint-follow, wire/segment drag, pinning, slide-clamp, merge, auto-dots, crossing/user
   dots, or extraction agreement.
4. `dotnet build`/`dotnet test` green (incl. the new oracle suite); firewall green.

## Guardrails
- **Invariant first** — if any path drops a contact, that's the bug; preservation outranks routing neatness.
- **One connectivity source; reuse routing** (`OrthogonalRoute`/`NormalizePoints`/`RouteStem`/
  `RouteBodyFollow`) — no parallel predicate, no new routing math.
- **One undoable action per drag**; live=overlay, commit=model; O(N) perf-gated.
- **Case 2 only on separation** — coincident-staying pins form no wire; never duplicate an existing wire; only
  pins that were pin-on-pin at drag START (ports already on a wire are Case 1).
- **Don't over-couple** — components never drag each other; the WIRE preserves the contact.
- **Instrument-first, permanent oracle tests**; report+STOP between layers.
- **Clean rebuild** (`dotnet clean` + build) when verifying, given the history.
- Update `placement-connectivity-and-drag-follow.md` (mark cases implemented), `grid-and-connectivity.md` if
  the invariant is restated there, and `src/Ui/CLAUDE.md` (the drag invariant; the auto-wire-on-separation
  behavior; the oracle suite location).

*Exit: every drag — component or wire — preserves every pin connection (wire follows/re-routes; pin-on-pin
spawns a stretching wire), proven by a headless oracle suite. The "a connection never silently breaks"
invariant holds.*
