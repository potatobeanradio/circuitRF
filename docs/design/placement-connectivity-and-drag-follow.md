# circuitRF — Placement Connectivity & Drag-Follow (design note)

**Status:** Implemented (rev 8) · **Date:** 2026-06-10 · Companion to `grid-and-connectivity.md` and
`library-palette.md`. **rev 3:** promotes the behavior to a single governing **invariant** (a connection,
once made, survives any drag) and folds in the user's two decisions: dragging a component off a pin-on-pin
contact **auto-forms a wire**; re-routing may add bends (connection beats tidiness).
**rev 4:** pin-on-pin detection prerequisite implemented — component ports now participate in
`IsConnected` and the junction-dot pass.
**rev 5:** Case 2 (pin-on-pin → auto-wire on separation) fully implemented. `SnapshotDragStartPositions`
records coincident pin pairs with no wire between them; `CommitDragAsCommand` creates `PlaceWireCommand`
entries for each pair that separated (chained into the same undoable `CompositeCommand` as the move); live
preview in `UpdateDragOverlay` draws synthetic `"pop-preview-N"` wires while the drag is in flight.
Proven by `DragInvariantOracleTests` (headless oracle — all 5 cases pass).
**rev 6:** Shared-point rule (Case 4) implemented — stationary pin wins when a moving pin, a stationary
pin, and a wire endpoint coincide at one point. See Case 4 below.
**rev 7:** **Disconnect** added — the sanctioned in-place detach. Introduces the first PERSISTENT
connectivity override (a per-component set of detached port indices) that suppresses an otherwise-geometric
connection. Bounded by a clears-on-next-move lifecycle so the steady state stays pure-geometry. See
"Disconnect" below.
**rev 8:** the follow RE-ROUTE is shape-preserving. Case 1's two branches used to redraw a followed
wire as a bare `OrthogonalRoute` L, which is a different wire wherever the original had more than one
bend — it does not pass through the original's interior, so every mid-span tap on it was silently
dropped. `WireGeometry.FollowEndpoints` deforms only the legs at the moved end; Case 1b no longer
bends the tapped wire at all. See Case 1 below and `src/Ui/Schematic/RESOLVED.md`.

## The governing invariant (the rule everything else serves)

> **A connection, once made, is never silently broken by a drag.** Whenever the user drags a component or a
> wire, the geometry **adapts** — wire segments form, stretch, and re-route live during the drag — so every
> existing pin contact (pin-on-wire and pin-on-pin) is preserved. Keeping the connection takes priority over
> wire tidiness: extra bends/jogs are acceptable.

This is an **invariant**, not a set of special cases. The cases below are consequences of it. Any drag
scenario not explicitly listed still obeys the invariant: if a contact exists before the drag, it exists
after (and during) the drag, with geometry adapted to hold it.

A connection is established **at input by snapping to the connection grid P** (pins and wire vertices land on
P), and detected by the single connectivity pass (`ComputeConnectivityGeometry`) using
`QuantKey`/`CoincidentPoints`/`PointOnSegmentInterior` + `ConnectTolerance`. Detection and preservation use
the **same** predicate — never a second notion of "connected."

## Prerequisite: pin-on-pin DETECTION (implemented)
The invariant's pin-on-pin clause depends on the editor *knowing* two pins are connected.
**Detection is now implemented:** `BuildRenderModel.IsConnected` uses `conPointCounts >= 2` (the port
itself contributes 1; count ≥ 2 means something else is at that P-cell). `ComputeConnectivityGeometry`
runs a port-coincidence dot pass after the wire auto-dot loop, emitting a junction dot wherever a port
coincides with another port, a wire vertex, or a wire body interior. The wire-only paths and the existing
T-junction / crossing-dot rules are unchanged.

Proven by `PinOnPinConnectivityTests` (headless oracle): pin-on-pin → Connected + one dot;
pin-on-wire-vertex → Connected; lone port → Unconnected / no dot.

## The cases (all consequences of the invariant)

### 1. Drag a COMPONENT whose pin is on a WIRE
The connected wire adapts live so its contact tracks the moved pin:
- **pin on a wire ENDPOINT** → the endpoint follows the pin, and **the rest of the wire deforms as
  little as the geometry allows** (`WireGeometry.FollowEndpoints`, rev 8). The delta at the moved end
  splits into a part along that end's leg (absorbed by lengthening it) and a part across it, handed to
  the ONE neighbouring vertex whose next leg — perpendicular by construction — absorbs it. Nothing
  past the second vertex moves. When the neighbour is the far endpoint (a two-point wire), an elbow is
  inserted at the moved end instead, leaving the original leg where it was.
  *(`UpdateConnectedWireEndpointsLive` + commit follow-snaps, from the same snapshot.)*
- **pin on a wire BODY (T-junction)** → **the wire is not touched**; the moved pin grows its own stub
  back to it (`BuildTapStubs`, a `PlaceWireCommand` in the same composite). This branch is only
  reached when neither endpoint of the wire moved — both ends are anchored by something staying put —
  so re-routing it, which is what `RouteBodyFollow` did until rev 8, dragged a run the user placed
  (and every other tap on it) on behalf of a part with no claim on either end. The stub leaves the
  wire at a right angle so it never runs along it. Same mechanism the segment-drag path already used
  (`BuildInteriorPortStubs`).

**Preserving the SHAPE is part of the invariant, not tidiness.** A wire's mid-span taps are geometric,
so a re-route that does not pass through the original's interior is a re-wire: it drops every tap
silently. Extra bends remain acceptable; discarding the user's bends is not. One undoable
`MoveCommand` (component + every followed wire), plus any stub it had to create.

### 2. Drag a COMPONENT whose pin is on ANOTHER COMPONENT'S pin (pin-on-pin)
The two pins were a direct contact (no wire). Dragging one component **away auto-forms a wire** between the
two pins and **stretches/re-routes it live** as the drag proceeds, so the contact becomes a *wired* contact
instead of breaking. The new wire connects the stationary pin to the moving pin; it re-routes orthogonally
each tick (bends acceptable). On commit it is a real `EditableWire`, created as part of the same undoable
action as the move (one Undo removes the wire and returns the component).
- The wire is auto-formed **only when the drag actually separates the pins** (drag that keeps them coincident
  needs no wire). The auto-formed wire is a normal wire thereafter (editable, deletable).
- Components do **not** drag each other (no rigid coupling); the *wire* is what preserves the connection.

### 3. Drag a WIRE connected to a COMPONENT pin
The wire's connection to the pin is preserved: the connected endpoint stays pinned to the component pin and
**new segments form/adapt** to hold it as the rest of the wire moves. *(Partly works today via
`ShouldPinDraggedEndpoint`/pinning + jog re-route; must hold for every wire-drag and segment-drag path so a
pin contact is never dropped.)* Bends acceptable.

### 4. (Detection, all cases) pin coincidences show connected + a dot
Independent of dragging: pin-on-pin and pin-on-wire show **connected** pins and a **junction dot** where they
touch, on placement and after any move (re-evaluated by the connectivity pass each `BuildRenderModel`).

### 5. Shared-point rule — stationary pin wins
When a moving pin, a stationary (unselected) pin, AND a wire endpoint are all coincident at one point
(three things sharing one grid cell), the **stationary connection wins**:
- The wire endpoint is attributed to the stationary pin and **stays put** — it does NOT follow the moving component.
- A **new auto-wire forms** from that point to the moving pin's landed position (same `PlaceWireCommand` mechanism as Case 2).

**Rationale:** the wire endpoint was placed to connect the stationary component, not the moving one.
"On a wire" is not sufficient reason to suppress auto-wire recording when the shared point is independently
held by a stationary pin — the wire isn't following, so the moving component needs the new wire.

**Implementation:** `IsPointHeldByStationaryPin(x, y)` in `SchematicViewModel` scans unselected component
ports within `ConnectTolerance`. Guards are added to:
- `UpdateConnectedWireEndpointsLive` (per-tick follow): skip endpoint-follow when stationary pin holds the point.
- `CommitDragAsCommand` follow block (commit): same guard.
- `SnapshotDragStartPositions` `onWire` skip: only defer to Case-1 wire-follow when the shared point
  is NOT also held by a stationary pin.

**Preserved cases:**
- Case 1a (genuine endpoint follow — no stationary pin at the endpoint): unaffected.
- Case 2 (pin-on-pin, no wire at shared point): `onWire = false` path unchanged.
- Case 3 (wire drag): unchanged — guard is in component-drag paths only.

## Disconnect — the sanctioned in-place detach (rev 7)

See `docs/ComponentDisconnect_Brief.md` for the implementation spec. Summary:

**Purpose.** The explicit way to free a component from its connections WITHOUT disturbing any wires — the
only in-place way (besides Cut-Paste) to slide a part off its wiring. Does NOT violate the drag invariant:
pins are detached BEFORE the drag, so there is nothing to preserve.

**Behavior.** `Disconnect` (component context menu; all selected) marks every pin of the target(s) as
detached. A detached pin: renders its red "unconnected" box even when coincident with a wire/pin; is excluded
from net extraction (own floating net); and on a later drag makes no wire follow and forms no auto-wire — the
component slides free. Identical for pin-on-pin and pin-on-wire.

**Persistent override — the first in the system.** Connectivity is otherwise pure geometry. A detached pin is
geometrically connected but treated as unconnected — not derivable from positions, so it is STORED
(per-component `HashSet<int> DetachedPorts`) and PERSISTED in `.csch`. Honored as a FILTER at four consumer
boundaries; the geometric core stays pure:
- render mapping (`ToRenderComponent`): detached port -> Unconnected (red box) before the geometric check;
- connectivity seeding (`ComputeConnectivityGeometry`): detached ports skipped from conPointCounts + dot pass;
- extraction seeding (`NetExtractor`): detached port seeds a unique key -> own floating net;
- drag-follow (`BuildPortMoves` / pin-on-pin scan): detached ports skipped -> no follow, no auto-wire.

**Lifecycle — clears on next move.** Persists at rest (red, excluded from netlist, serialized) until the
component is next moved; the move's `MoveCommand` clears the moved component's flags (Undo restores them) and
geometry re-rules at the new position. Only the moved component clears.

**v1 out of scope:** standalone "Reconnect in place" (reconnect by moving back onto the target); per-pin
selective disconnect (v1 = all pins).

## Implementation guidance
- **Reuse the single connectivity source + the shared routing primitives.** Case 1a routes via
  `WireGeometry.FollowEndpoints`; case 1b's stub and case 2's auto-wire are new wires built on
  `WireGeometry.OrthogonalRoute`/`NormalizePoints`; case 3 reuses the existing endpoint-pinning + jog
  re-route (`RouteStem`). Do **not** fork the connectivity predicate or invent parallel routing, and
  do **not** re-route a followed wire end to end — `OrthogonalRoute` between two endpoints is a
  different wire from the one the user drew.
- **Live vs commit.** During the drag, preserve connections in the fast overlay path
  (`UpdateDragOverlay`/`WireDragPoints`, perf-gated by `LiveDotMaxObjects`); fold the final geometry
  (followed wires, any auto-formed wire) into the single undoable `MoveCommand`/`CompositeCommand` at commit.
- **O(N)**, perf-gated; no O(N²) scans.
- **Undo:** one keystroke restores the pre-drag state entirely (component position, every re-routed wire, any
  auto-created wire).

## Out of scope
- Rigid component-to-component coupling (dragging one never drags another — the *wire* preserves the contact).
- Autorouting/obstacle-avoidance (only the contact point is guaranteed; the wire stays orthogonal but is not
  re-flowed around obstacles).
- Extraction changes (coincident ports / wired contacts already union into one node per
  `grid-and-connectivity.md`; this note is the edit-time visual that must agree with it).

## Build/verification note
Verify with a **clean rebuild** (`dotnet clean` + build) when XAML/compiled-resource changes are involved, and
back drag-invariant behavior with **headless oracle tests** (build an edit model, simulate the drag commit,
assert the connection survives) so "fixed" is proven, not claimed.
