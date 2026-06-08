# Phase 6d-fix — Wire Draw Mode + Per-Segment Wire Editing (Claude Code / Sonnet)

Two related wire-editing improvements. Part A (draw-mode polish) is small; Part B (per-segment selection and
drag) is a real interaction-model change with one design convention pinned below. Both live in the wire
subsystem (`SchematicViewModel` wire handling, `WireGeometry`, the hit-test, the overlay/renderer). Every
mutation stays an undoable command; firewall green; the 10k frame rate must not regress. Sub-gate A then B.

> Context: `src/Ui/ViewModels/SchematicViewModel.cs` (the `Tool.Wire` handlers, `_wirePoints`, `FinishWire`,
> drag handling, `OnKeyDown`), `src/Ui/Schematic/WireGeometry.cs` (`OrthogonalRoute`, segment helpers),
> `src/Ui/Schematic/SchematicHitTest.cs`, `src/Ui/Schematic/EditableSchematic.cs` (`EditableWire.Points`),
> the overlay/renderer for selection highlight. Design: `ui-design.md` §4.3 (simple-ortho wires).

## Part A — Wire draw-mode polish

**A1. Cursor changes in wire-draw mode.** When the wire tool is active (`Tool.Wire`), set the canvas cursor
to a wire/crosshair cursor (`StandardCursorType.Cross` is fine) so the mode is visually obvious. Revert to
the default arrow when the tool changes away from Wire (including after the wire is finished — see A2). This
is the same cursor-management pattern as the pan-hand and zoom-box cursors from prior rounds.

**A2. Double-click OR Enter finishes the wire, KEEPING what's drawn.** While drawing a wire (one or more
points placed), the user can complete it two ways that **keep the drawn wire** and exit to the Select tool:
- **Double-click** on the canvas, or
- **Enter** keystroke.
On either, commit the wire as it currently stands (the existing `FinishWire()` path — it already builds the
`EditableWire` from `_wirePoints` and runs `PlaceWireCommand`), then switch `ActiveTool` to `Select` and
revert the cursor (A1). A double-click's final point should be included before finishing (don't drop the
last segment).

**A3. Esc discards the in-progress wire (unchanged intent, make sure it differs from A2).** Esc while drawing
clears `_wirePoints` and exits wire mode **without** placing any wire — the in-progress wire disappears.
(This already largely exists via `CancelCurrentOp`; just confirm Esc-while-drawing discards and A2
finishes-and-keeps — the two must behave differently.)

**A4. Minimum-wire guard.** If the user finishes (A2) with fewer than 2 distinct points, discard (nothing to
place) rather than committing a degenerate wire — the existing `FinishWire` already guards `< 2` points;
keep that.

## Part B — Per-segment wire selection and drag

Today a wire is selected and dragged as a whole. Change it so **each orthogonal segment** (each horizontal
or vertical run between two consecutive points) of a wire is **independently selectable and draggable**.

**B1. Segment hit-testing + selection.** Hit-testing a wire returns *which segment* was hit (the index `i` of
the segment between `Points[i]` and `Points[i+1]`), using the thin-line hitbox from the prior round
(point-to-segment distance ≤ ~half linewidth + tolerance). Clicking a segment selects **that segment**, not
the whole wire. Selection state must be able to represent "segment i of wire W" (extend the selection model
to hold a wire-segment selection, e.g. `(wireId, segmentIndex)`, alongside the existing object selections).
The selection highlight (the thicker stroke from last round) highlights just the selected segment.
- Rubber-band/marquee selection of a wire may still select the whole wire (all its segments) — segment-level
  granularity is for direct click + drag. (Keep marquee behavior as-is; don't overcomplicate it.)

**B2. The drag convention (THE design decision — implement exactly this).** A segment drags **only along its
perpendicular axis**:
- A **horizontal** segment can be dragged **vertically** (up/down); its X-extent is unchanged.
- A **vertical** segment can be dragged **horizontally** (left/right); its Y-extent is unchanged.
- **Dragging a segment along its own axis is NOT offered** — constrain the drag delta to the perpendicular
  direction (zero out the parallel component, like the axis-lock helper already does). This is the standard
  EDA segment-drag model and it keeps everything orthogonal automatically.

This is why the convention is clean: moving a segment perpendicular to itself only **lengthens/shortens its
two adjacent (perpendicular) neighbors** — it never creates a diagonal and never detaches a shared endpoint.
Concretely, dragging horizontal segment `[P_i, P_i+1]` vertically by `dy`:
- `P_i.Y += dy` and `P_i+1.Y += dy` (the segment moves),
- which automatically lengthens/shortens the vertical neighbor segments `[P_i-1, P_i]` and `[P_i+1, P_i+2]`
  (their other ends `P_i-1`, `P_i+2` stay put),
- everything remains orthogonal with no new bends.
Vertical segment dragged horizontally by `dx` is the mirror (move `P_i.X`, `P_i+1.X`).

**B3. Endpoint segments (pinned ends).** If a segment is the **first or last** segment of the wire and its
outer endpoint is **connected to a component port or another wire** (the existing endpoint-pinning detection,
`IsWireEndpointConnectedToUnselected`), the pinned outer point must **stay put**. Moving such a segment
perpendicular then requires re-routing so the pinned end stays connected:
- Use the existing `OrthogonalRoute` to re-route between the pinned outer point and the moved inner point,
  inserting the one bend that keeps it orthogonal (same machinery whole-wire pinned-drag already uses).
- If both ends of a 2-point wire are pinned, the segment is fully constrained — don't move it (same as the
  whole-wire both-pinned case today).
- Net result: dragging the perpendicular of an endpoint segment may add/adjust a bend so the wire stays
  connected and orthogonal — visually clean, no diagonals.

**B4. Undoable + live preview.** The drag shows a live preview each tick (overlay override, like the existing
component/wire drag — no full `BuildRenderModel` per tick, keep the 10k perf), and commits on mouse-up as a
single undoable command (a wire-points change snapshot: old points → new points, mirroring the existing
`WireMoveSnapshot`/`MoveCommand` shape). Undo restores the exact prior points.

**B5. Connectivity at commit.** After a segment drag commits, the deferred connectivity pass (drag-end, O(N)
spatial hash from the perf fix) recomputes connection dots / unconnected-port boxes — a segment moved away
from a port should reflect the new connection state. (This already happens at drag-end for whole-wire moves;
ensure segment-drag commits go through the same drag-end rebuild.)

## Acceptance
1. **A:** wire tool shows the crosshair cursor and reverts to arrow on exit; double-click and Enter both
   finish-and-keep the in-progress wire then return to Select; Esc discards the in-progress wire; `<2`-point
   finishes are discarded.
2. **B:** clicking a wire segment selects just that segment (thicker highlight); dragging a horizontal
   segment moves it vertically (vertical segment → horizontally), neighbors stretch, everything stays
   orthogonal, no diagonals; parallel-axis drag is not possible (constrained out); endpoint segments with a
   pinned connected end re-route (add/adjust a bend) keeping the pinned end connected; both-pinned 2-point
   wire doesn't move.
3. Each segment drag is one undoable command with live preview; the 10k frame rate is unaffected (report it);
   connection state recomputes correctly on commit.
4. Firewall green; `dotnet build`/`dotnet test` green; nothing in prior phases regresses.

## Guardrails
- **Wires stay orthogonal — always.** The perpendicular-only drag convention (B2) guarantees this; never
  produce a diagonal segment. Constrain the drag delta to the perpendicular axis at the source, don't
  "fix up" diagonals after.
- **Every mutation undoable** (segment drag = one command); live preview via overlay override, not per-tick
  full rebuild (keep the perf win).
- Reuse the existing machinery: `OrthogonalRoute` for pinned re-routes, the endpoint-pinning detection, the
  drag-overlay/commit pattern, the thin-line wire hitbox, the drag-end O(N) connectivity rebuild. Don't
  invent parallel versions.
- Segment selection extends the selection model (a `(wireId, segmentIndex)` notion) — keep it framework-free.
- Diagnostics over grinding; if the pinned-endpoint re-route gets visually awkward in a case, flag it with
  the specific geometry rather than hacking a special case.
- Update `src/Ui/CLAUDE.md` with the per-segment wire-drag convention (perpendicular-only) so it's not
  re-litigated.

*Exit: wire draw mode feels right (cursor + finish-keeps / esc-discards), and wires are edited segment by
segment with a clean orthogonal-preserving drag — the editor's wire UX is now professional-grade.*
