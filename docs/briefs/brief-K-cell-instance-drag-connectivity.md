# Brief K — Cell instances behave like components on drag/move (pins stick)

**The bug (confirmed):** when the user drags/moves a placed **cell** instance, wires and pins that
were connected to its pins **do not follow** — the connection silently breaks. Built-in components
are fine. This violates the standing invariant: *no schematic connection may become unconnected from
a drag or move.* A cell instance must behave exactly like a built-in component.

**Root cause (single source of truth violated):** a component's pin geometry has **two** sources —
built-ins use `SymbolPortDefs.For(comp.Symbol, comp.PortCount)`; cell-ref components use the resolved
`.csym` `Symbol.Pins`. The **render + connectivity pass** (`SchematicEditModel.ComputeConnectivityGeometry`)
already dispatches on `comp.CellRef != null` and uses the resolved pins. But **every drag/connectivity
helper in `SchematicViewModel` calls `SymbolPortDefs.For(...)` unconditionally** — for a cell that
returns the `SymbolKind.Generic` placeholder's pins `[(0,−200),(0,+200)]`, NOT the cell's real pins.
So the drag computes "old port world positions" at the wrong places; the wire-follow/pin-follow logic
finds nothing at those positions and leaves the wires behind.

**The fix:** consolidate to ONE cell-ref-aware pin-geometry accessor and route every connectivity/
drag site through it (the project's "single source of truth, enforce at the boundary" rule).

**Firewall:** all changes are in `src/Ui/Schematic` (framework-free model) and
`src/Ui/ViewModels/SchematicViewModel.cs` — no Avalonia/Skia leakage into the model.

---

## Read first (real names)

- `src/Ui/Schematic/EditableSchematic.cs`:
  - `SymbolPortDefs.For(SymbolKind, int portCount)` → `(string Name, float LocalX, float LocalY)[]`
    (the built-in source; `Generic`/default = vertical `[("1",0,−200),("2",0,200)]`).
  - `SchematicEditModel.ComputeConnectivityGeometry(Dictionary<string,CellSymbolResolution>?)` —
    **the model to copy.** It already does, per component:
    `if (comp.CellRef is not null) { …use cellRefResolutions[comp.Id].Symbol.Pins… } else { …SymbolPortDefs.For… }`.
  - `EditableComponent.GetPortWorldCoord(int portIndex)` — built-in only (uses `SymbolPortDefs.For`).
    `EditableComponent.IsPortDetached(int portIndex)`, `.CellRef`, `.PortCount`, `.Rotation`, `.MirrorX`.
  - `SchematicEditModel.SchematicDirectory`, `CellSymbolResolver.Resolve(cellRef, baseDir)` →
    `Resolved` (carries `Symbol` with `Pins`) / `NotFound` / `PrimaryMissing`.
  - `SchematicGeometry.LocalToWorld(localX, localY, X, Y, Rotation, MirrorX)`.
- `src/Ui/Schematic/SymbolModel.cs` — `SymbolPin { double LocalX; double LocalY; int PortIndex; string? Name; }`;
  `Symbol.Pins`.
- `src/Ui/ViewModels/SchematicViewModel.cs` — the drag/connectivity helpers that must be routed
  through the new accessor (the bug sites):
  1. `SnapshotDragStartPositions` — pin-on-pin contact detection (`SymbolPortDefs.For(selComp…)`,
     `SymbolPortDefs.For(other…)`, and `other.GetPortWorldCoord(opi)`).
  2. `ShouldPinDraggedEndpoint` (`SymbolPortDefs.For(comp…)` + `comp.GetPortWorldCoord(pi)`).
  3. `IsPointHeldByStationaryPin` (same).
  4. `IsWireEndpointConnectedToUnselected` (same).
  5. `UpdateConnectedWireEndpointsLive` (`SymbolPortDefs.For(comp…)` + `LocalToWorld`).
  6. `BuildPortMoves` (`SymbolPortDefs.For(cs.Component…)` + `LocalToWorld`).
  7. `CommitDragAsCommand` — the pin-on-pin auto-wire block (`SymbolPortDefs.For(snap.Component…)`,
     indexes `portDefs[contact.MovingPortIndex]`).
  8. `UpdateDragOverlay` — the pin-on-pin live-preview block (same indexing).
  - The `PinOnPinContact` record stores `MovingPortIndex` — see Layer 3 for the index-meaning fix.

---

## Spine (do-not-violate)

1. **One accessor for pin geometry.** A single cell-ref-aware method returns a component's pins
   (local coords + port index); every connectivity/drag site uses it. No site calls
   `SymbolPortDefs.For` directly for a component that might be a cell.
2. **Match the render pass exactly.** The accessor returns the SAME pins the render/connectivity pass
   uses, so what the user sees connected IS what the drag treats as connected. Resolved → resolved
   symbol pins; NotFound/PrimaryMissing → no pins (exactly as the render path shows no pins).
3. **No connection breaks on drag/move/nudge.** After the fix, dragging a wired cell keeps every
   connection — identical to a built-in.
4. Honor the perf rule: the per-frame live-drag path must not do filesystem IO per pin (snapshot at
   drag start — Layer 2).
5. Built-in components must behave exactly as before (zero behavior change for non-cell comps).

---

## Layer 1 — One cell-ref-aware pin-geometry accessor

Add to `SchematicEditModel` (framework-free), mirroring `ComputeConnectivityGeometry`'s dispatch:

```csharp
/// The single source of a component's pin geometry. Cell-ref-aware:
///   CellRef + Resolved → resolved .csym Symbol.Pins
///   CellRef + NotFound/PrimaryMissing → empty (matches the no-pins render)
///   built-in → SymbolPortDefs.For(Symbol, PortCount), PortIndex = slot
internal IReadOnlyList<(float LocalX, float LocalY, int PortIndex)> PortDefsOf(EditableComponent comp);

/// World coords of one pin def for a component (applies LocalToWorld with rotation/mirror).
internal (double X, double Y) PortWorldOf(EditableComponent comp,
    (float LocalX, float LocalY, int PortIndex) def)
    => SchematicGeometry.LocalToWorld(def.LocalX, def.LocalY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);
```

`PortDefsOf` resolves cell-refs via `CellSymbolResolver.Resolve(comp.CellRef, SchematicDirectory)`
(only when `comp.CellRef != null && SchematicDirectory != null`). The resolver is cached, so this is
cheap; per-frame callers use the Layer-2 snapshot regardless.

**Refactor `ComputeConnectivityGeometry` to call `PortDefsOf`** for its per-component pin loop too,
so there is literally ONE definition of "this component's pins" shared by render and drag. (If a
pre-resolved `cellRefResolutions` map is already in hand there, `PortDefsOf` may accept an optional
map param to reuse it and avoid a second resolve — your choice; keep one code path.)

**Gate 1:** `PortDefsOf` returns a built-in's pins identically to `SymbolPortDefs.For` (PortIndex =
slot), and a Resolved cell's pins identically to what the renderer draws. The connectivity pass still
produces the same dots/connection state as before for both kinds.

---

## Layer 2 — Route every drag/connectivity site through the accessor (+ snapshot)

Replace each `SymbolPortDefs.For(comp.Symbol, comp.PortCount)` and `comp.GetPortWorldCoord(pi)` in the
eight sites listed in "Read first" with `EditModel.PortDefsOf(comp)` + `PortWorldOf(comp, def)`,
iterating the returned defs and using `def.PortIndex` for `IsPortDetached`.

**Perf (snapshot, honor the 10k/30fps rule):** the per-frame live helpers
(`UpdateConnectedWireEndpointsLive`, `UpdateDragOverlay`) must not resolve per pin per frame. In
`SnapshotDragStartPositions`, build a `Dictionary<string, (float,float,int)[]> _dragPortDefs` for the
relevant components (at minimum every selected component; include any unselected component the
per-frame path touches) and have the live helpers read the snapshot. The once-per-gesture paths
(`SnapshotDragStartPositions` all-component scan, `CommitDragAsCommand`) may call `PortDefsOf`
directly. Clear `_dragPortDefs` in `ClearDragState`.

**Gate 2:** Dragging a wired **cell** instance: connected wires follow its pins; a wire T-ed onto a
cell pin stays attached; pin-on-pin auto-wires form when a cell pin separates from a stationary pin
and don't form when they stay coincident. Built-in drags are byte-for-byte unchanged. No per-frame
filesystem IO during the drag (the snapshot is consulted, not the resolver).

---

## Layer 3 — Pin-on-pin contact index meaning

`PinOnPinContact.MovingPortIndex` is later used as an **array index** into the port-defs
(`portDefs[contact.MovingPortIndex]`) in `CommitDragAsCommand` and `UpdateDragOverlay`. With
`PortDefsOf`, that index must be the **slot into the `PortDefsOf(comp)` array**, which is stable for a
component throughout a drag (the symbol doesn't change mid-gesture). Ensure the snapshot
(`SnapshotDragStartPositions`), the live preview (`UpdateDragOverlay`), and the commit
(`CommitDragAsCommand`) all index the SAME `PortDefsOf` ordering. Use `def.PortIndex` only for the
`IsPortDetached` check, never as the array index. (Rename the field to `MovingPinSlot` if it reduces
confusion — optional.)

**Gate 3:** A cell whose resolved symbol pins are NOT a contiguous 0..N (e.g. PortIndex values from
the `.csym` differ from slot order) still auto-wires the correct pin on separation — the slot/index
and the detached-check port index never get crossed.

---

## Acceptance
- Dragging/moving a placed cell instance keeps every wire and pin connection — identical to a
  built-in component; no connection becomes unconnected. ✅
- One accessor (`PortDefsOf`) is the sole source of a component's pin geometry; render and drag share
  it; no drag site calls `SymbolPortDefs.For` directly for a possibly-cell component. ✅
- Built-in component drag behavior is unchanged; perf rule honored (no per-frame IO). ✅

## Guardrails
- Don't fork a cell-only drag path — fix the shared accessor and route through it (single source).
- Resolved/NotFound/PrimaryMissing must match the render path's pin set exactly.
- Keep `SchematicEditModel`/`SymbolPortDefs` framework-free.
- Minimal diff; list every site changed.

## Scope fence (NOT here)
- The drag *ghost* art and the Symbol Editor port-index display are **Brief L**.
- Wire-drawing/hit-test snapping to a cell's pins (`SchematicHitTest.NearestPort` etc.) is a related
  latent item — **audit and note** whether it uses `SymbolPortDefs.For` for cell comps, but only fix
  here if it's trivially the same accessor swap; otherwise report it for a follow-up.
- Arrow-key `NudgeSelection` follow-wire behavior (if built-ins don't drag wires on nudge either,
  it's a separate pre-existing question) — note, don't change unless it's the same accessor swap.

## Exit / report
State: the `PortDefsOf` signature and how it dispatches; every drag/connectivity site routed through
it; how the snapshot avoids per-frame IO; the `MovingPortIndex`/slot resolution; and the audit result
for `NearestPort`/nudge. Confirm the 3 gates run mentally and that built-in drags are unchanged.
