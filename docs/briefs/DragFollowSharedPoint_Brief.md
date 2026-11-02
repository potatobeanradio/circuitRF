# circuitRF — Drag-follow bug: wire endpoint follows the WRONG pin at a shared point (Claude Code / Sonnet)

A connection-preservation bug with a precise root cause (traced from the code). **Repro:** C1, C2 side by
side; wire from C1-bottom to C2-bottom (so the wire's ENDPOINT is at C1-bottom). Place C3; drag C3-top onto
C1-bottom (pin-on-pin) — connects. Drag C3 down/away → the C1–C2 wire detaches from **C1** (stays on C2+C3).
**Violates the invariant: a drag must never unconnect a component.** The wire should keep its C1 endpoint AND
a new segment should form to C3 (C1, C2, C3 all stay connected). Read
`docs/design/placement-connectivity-and-drag-follow.md`. **Instrument-first with a headless oracle** for this
exact 3-component geometry (the drag code is load-bearing and has regressed before). Firewall green.

## Root cause (two compounding faults at the SHARED point)
At C1-bottom THREE things coincide: the stationary **C1 pin**, the **C1–C2 wire endpoint**, and (at drag
start) **C3-top's original position**. When C3 is dragged:

1. **The wire follows the WRONG pin.** `UpdateConnectedWireEndpointsLive` (live) and the follow-wire block in
   `CommitDragAsCommand` match a wire endpoint to a **moved** port's ORIGINAL position via
   `CoincidentPoints(orig[0], ox, oy)`. The C1–C2 wire endpoint at C1-bottom coincides with C3-top's original
   position (a moved port) — so the follow logic drags that endpoint DOWN to track C3, pulling it OFF the
   stationary C1 pin. The endpoint was C1's connection, not C3's; it must NOT follow C3.
2. **The compensating auto-wire is suppressed.** `SnapshotDragStartPositions`' pin-on-pin detection skips any
   moving port that is "already on a wire" (`onWire` guard) — intending to defer to Case-1 wire-follow. But
   C3-top IS on a wire endpoint (the C1–C2 wire), so the pin-on-pin contact is NOT recorded → no auto-wire is
   created when C3 separates. So fault 1 detaches C1 and fault 2 fails to bridge it.

**Net:** the wire wrongly rides C3 away from C1, and nothing reconnects C1.

## The correct behavior
When a moving component pin separates from a point where it was coincident with a **stationary** pin AND a
wire endpoint terminates there:
- the **wire endpoint stays put** (it belongs to the stationary C1 pin — do NOT drag it with C3), and
- a **new wire forms** from that point to C3's moved pin (so C3 stays connected too).
Result: C1–C2 wire unchanged at C1; new C1-bottom→C3-top wire; all three connected.

## Spine
- **Disambiguate the follow match.** A wire endpoint coincident with BOTH a stationary pin and a moving pin's
  original position must be attributed to the **stationary** pin → it does NOT follow the moving component.
  Only follow a wire endpoint when its coincident pin is actually the one moving AND no stationary pin/ख wire
  also holds that point. (A point held by a stationary connection is pinned, exactly like the wire-drag
  `IsWireEndpointConnectedToUnselected` pinning rule.)
- **Fix the auto-wire suppression.** The pin-on-pin `onWire` skip is too broad: it must still record the
  contact (and form the auto-wire on separation) when the shared point is held by a stationary pin. "On a
  wire" should defer to Case-1 ONLY when that wire endpoint will actually follow the moving pin — which, per
  the disambiguation above, it won't when a stationary pin also holds the point.
- **One undoable action**, O(N), reuse `OrthogonalRoute`/existing predicates; don't fork.
- **Scope fence:** this shared-point disambiguation + auto-wire suppression fix. No other drag changes.

## LAYER 1 — INSTRUMENT with the exact-geometry oracle (prove the bug)
Add a headless test reproducing the repro precisely:
- C1 at (x1,y), C2 at (x2,y); a wire whose endpoints are C1-bottom and C2-bottom.
- C3 placed so C3-top coincides with C1-bottom (pin-on-pin).
- Select C3, `SimulateDragCommit(0, +Δ)` (drag down).
- **Assert CURRENT (buggy) result and report:** does the C1–C2 wire's C1 endpoint move off C1-bottom? Is a
  new C1→C3 wire created? Log the wire point lists + whether C1/C2/C3 pins read Connected after.
Expected current findings: C1–C2 endpoint wrongly moved to C3; no auto-wire; C1 unconnected. **Report — no
fix yet.** This becomes the permanent regression oracle.

**Layer 1 gate:** oracle reproduces the bug headlessly (C1 endpoint follows C3 / C1 ends unconnected).
Report the assertions + the actual wire geometry.

## LAYER 2 — fix: stationary pin wins the shared point; auto-wire forms
1. **Follow disambiguation** (`UpdateConnectedWireEndpointsLive` + `CommitDragAsCommand` follow block): before
   moving a wire endpoint to track a moved port, check whether that endpoint is ALSO held by a **stationary**
   (unselected) component pin or unselected wire at its ORIGINAL position. If so, the endpoint is pinned to
   the stationary connection → **do not move it** to follow the moving port. (Reuse the existing
   stationary-pin test, like `IsWireEndpointConnectedToUnselected`/`ShouldPinDraggedEndpoint`'s component-pin
   scan, against `orig` positions.)
2. **Auto-wire suppression fix** (`SnapshotDragStartPositions` pin-on-pin loop): change the `onWire` skip so a
   moving port coincident with another **component pin** still records a `PinOnPinContact` even if a wire
   endpoint also terminates there — because (per step 1) that wire will NOT follow, so the auto-wire is needed
   to keep the moving component connected. Only skip when the moving port is on a wire and NOT pin-on-pin with
   a stationary component pin (true Case-1, where the wire legitimately follows).
3. Keep everything one undoable `MoveCommand`(+`PlaceWireCommand` for the auto-wire), O(N), via
   `OrthogonalRoute`. Don't regress endpoint-follow (the genuine Case-1: pin on a wire with NO stationary pin
   there), T-body follow, segment drag, pinning, or merge.

**Layer 2 gate (flip L1 green):** after dragging C3 away — the C1–C2 wire's C1 endpoint **stays at
C1-bottom**; a **new wire** connects C1-bottom→C3-top; C1, C2, C3 all read Connected; one Undo restores
(removes the new wire, C3 back on C1). The plain pin-on-pin case (no wire at the shared point) still
auto-wires; the plain endpoint-follow case (drag the component the wire IS connected to) still follows.
Report with the oracle green + the in-app repro confirmed.

## Acceptance
1. The exact repro (C1–C2 wire + C3 pin-on-pin on C1, drag C3 away) keeps C1 connected via the unchanged
   wire AND forms a new C1→C3 wire — C1/C2/C3 all connected; proven by the headless oracle.
2. No regression: genuine endpoint-follow (component the wire is connected to is dragged), T-body follow,
   pin-on-pin without a wire, segment drag, pinning, merge — all still correct (their oracles stay green).
3. One undoable action; O(N); `dotnet build`/`dotnet test` green; firewall green.

## Guardrails
- **Stationary connection wins a shared point** — a wire endpoint held by a stationary pin must NOT be dragged
  off it to follow a moving pin that merely started coincident there.
- **Auto-wire must form** when a moving pin separates from a stationary pin, even if a wire also ends at that
  point (that wire isn't following, so the moving component needs the new wire).
- Reuse existing predicates (`CoincidentPoints`/`IsWireEndpointConnectedToUnselected` logic) +
  `OrthogonalRoute`; no parallel routing/connectivity.
- One undoable `MoveCommand`(+auto-wire); O(N); instrument-first with the permanent exact-geometry oracle.
- **Scope fence:** shared-point follow disambiguation + auto-wire suppression only.
- Update `placement-connectivity-and-drag-follow.md` (the shared-point rule: stationary pin wins; auto-wire
  still forms) + `src/Ui/CLAUDE.md`.

*Exit: dragging C3 off a pin it shares with C1 (where a wire also terminates) keeps the C1 wire on C1 and
spawns a new wire to C3 — C1, C2, C3 stay connected; the "never unconnect on drag" invariant holds.*
