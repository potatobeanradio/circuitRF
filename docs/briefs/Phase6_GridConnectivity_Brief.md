# Phase 6 — Grid & Connectivity Robustness (pins/wires on-grid; fine authoring grid; cross-grid paste)

Make component pins, wires, and connection points land on a sacred coarse **connection grid** so connectivity
is exact and the 6e netlist is correct — while adding a fine **authoring grid** for non-electrical placement,
and a **warn+snap** path for pasting foreign-grid content. **Read `docs/design/grid-and-connectivity.md` first
— it is the authority**; this brief is the build plan. Sub-gated; report and stop between layers. Firewall
green; every edit undoable; values must not change on refactor.

> Read first: `docs/design/grid-and-connectivity.md` (the two-grid model, rules R1–R7, cross-grid paste §5,
> persistence §6, order §7). Also: `ui-design.md` §4.1/§4.3/§5/§5.1/§5B; `src/Ui/CLAUDE.md`. Context code:
> `src/Ui/Renderers/SchematicSymbols.cs` (library geometry — pins currently at ±150/150), `src/Ui/Schematic/
> EditableSchematic.cs` (`SymbolPortDefs.For`, `GeneratePorts`, `SnapToGrid`, `GridSize`, `ConnectTolerance`,
> `ComputeConnectivityGeometry`/`QuantKey`, `PortCount`), `src/Ui/Schematic/SchematicGeometry.cs`
> (`LocalToWorld`, `CoincidentPoints`, `PointOnSegment*`), `src/Ui/ViewModels/SchematicViewModel.cs` (wire
> draw/drag/merge/segment-drag/nudge snap call sites — all should route through `EditModel.SnapToGrid`),
> `src/Ui/Schematic/SchematicPersistence.cs` (`CschFile.GridSize`/`GridSnap`), `src/Ui/Commands/Schematic/
> SchematicPasteCommand.cs` (paste; today resolves names only, no grid handling), `src/Ui/Clipboard/
> SchematicClipboard.cs` (the copied payload — add the source grid here). Design doc wins on any conflict.

## The spine (do not violate)
- **Two grids, two jobs.** Connection grid `P` (= `GridSize`, default 100): every pin-in-world, wire
  endpoint, wire bend, junction dot lands on it **exactly** (integer multiple — equality, not tolerance).
  Authoring grid `p = P/k` (default `k=20`, `p=5`): body art, labels, net-labels, canvas objects only. `p` is always
  a refinement of `P`. (See grid-and-connectivity.md §1.3 for why `P=100, k=20, p=5` and not a finer `P`:
  `P` controls connection ease, not authoring freedom; only `p`/`k` give freedom.)
- **Connection = coordinate equality on `P`**, established at input (snap at placement/draw), not patched by
  tolerance afterward. `ConnectTolerance` demotes to a float-dust guard.
- **Net labels are NOT on any grid** (position carries no electrical meaning) — they use `p`/free.
- Every mutation undoable through the command stack; cross-grid paste snap is part of the **one** paste
  command (one undo). Firewall green (model framework-free).

---

## LAYER 1 — R2: fix library pin offsets to `P` multiples (the immediate fix)

Pins currently sit off-grid: leads at local `±150`, FET drain/source at `(150, ±100)`, with `P=100` → every
pin is half a cell off `P`. This is the root cause of "hard to connect."

1. In `SchematicSymbols.cs` and `SymbolPortDefs.For`/`GeneratePorts` (`EditableSchematic.cs`): change pin
   **lead endpoints / pin tips** from `±150 → ±200` (2-terminal R/L/C/V/Tone/Port), FET gate `-150→-200` and
   drain/source `150→200` (keep `±100` y — already a multiple), and `ZPort`/`Sdd` generated pins use a
   `P`-multiple lead length (`±200`). **The pin coordinate is what must be a `P` multiple in local space.**
2. **Body art is unconstrained** — do NOT move plate gaps, arc bumps, the FET box, arrows, etc. Only the lead
   **tips** (the pin coordinates) move out to `±200`; extend the lead lines to reach the new tip. Keep symbols
   looking right (a slightly longer lead is fine and standard).
3. Keep `SymbolPortDefs` and `SchematicSymbols` **consistent** — the port tip in `SymbolPortDefs` must equal
   the lead-end coordinate the art draws to (they're read by different code; they must agree or the pin marker
   floats off the lead).
4. **Do not special-case per symbol in connectivity** — this is purely a geometry-data fix.

**Layer 1 gate:** all built-in symbols have pin tips on `P` multiples in local coords; `SymbolPortDefs` agrees
with `SchematicSymbols` lead ends; symbols still render correctly. Report (list the changed offsets).

---

## LAYER 2 — R3/R4: confirm origin-snap and wire-snap to `P` (audit + fix)

1. **R3:** confirm component placement/drag/nudge snaps the **origin** to `P` via `EditModel.SnapToGrid`
   (uses `GridSize`). With Layer 1, origin-on-`P` ⇒ pins-on-`P`. Verify rotation/mirror preserve `P`-multiples
   (they should — 90° rotation maps multiples to multiples). Fix any placement path that bypasses the snap.
2. **R4:** audit every wire path in `SchematicViewModel.cs` — draw (`HandleWirePress`/`FinishWire`), drag
   (`ApplyWireDragLive`/`ComputeWireDragEndPoints`), segment-drag (`HandleSegmentDragLive`/
   `ComputeSegmentDragPoints`), merge (`WireGeometry`), nudge — and confirm **every endpoint and bend** is
   snapped to `P`. Fix any path that produces an off-`P` vertex. (Endpoint→pin snap on release must snap to
   the pin's exact on-`P` coord.)

**Layer 2 gate:** placement snaps origin to `P`; all wire endpoints/bends are on `P`; rotation/mirror keep
pins on `P`. Report any call site that was bypassing the snap (the diagnosis).

---

## LAYER 3 — R7: the on-grid invariant test (the oracle — lands before the deeper changes)

Add a **headless** test (no Avalonia) that asserts the §2 R7 invariant: after a battery of edits, **every** pin
world-coordinate, wire endpoint, wire bend, and junction dot is an exact multiple of `P` (within float-dust
ε). Battery: place each component type, move, rotate (all 4), mirror, draw wires, drag a segment, paste, nudge.
A helper `IsOnGrid(coord, P)` (e.g. `|coord/P − round(coord/P)| < 1e-6`). This guards Layers 4–6.

**Layer 3 gate:** invariant test passes against the current model after Layers 1–2. If it FAILS, that's a real
off-grid bug Layers 1–2 missed — fix it before proceeding (this is the test doing its job). Report.

---

## LAYER 4 — R1/§3: tighten connectivity to exact on-`P` equality

1. In `ComputeConnectivityGeometry`/`QuantKey` (`EditableSchematic.cs`): since connection points are now
   exactly on `P` (Layers 1–3), make union/coincidence decided by **exact on-`P` equality** (snap to `P`,
   compare integer cell indices). Keep a **tiny** float-dust tolerance as belt-and-suspenders; **demote
   `ConnectTolerance`** so it is no longer the mechanism that bridges real gaps.
2. **Values must not change:** Hero schematics / existing connectivity tests must produce the **same** nets/
   dots as before (this is a robustness tightening, not a behavior change). If any connectivity test changes
   result, STOP and report — it means something was relying on the loose tolerance (a latent off-grid point).

**Layer 4 gate:** connectivity decided by exact on-`P` equality; `ConnectTolerance` demoted to float-dust;
all existing connectivity/dot/T-junction/crossing tests still pass unchanged. Report.

---

## LAYER 5 — R5 + §6: the fine authoring grid `p = P/k`

1. **Model:** add the fine grid to `SchematicEditModel` (e.g. `AuthorGridDivisor k` (default 20) or
   `AuthorGridSize p`; `p = P/k`, default `p=5`). Add a `SnapToAuthorGrid(coord)` helper. Persist it in `.csch` (`CschFile`,
   default `k=20` when absent — within-version graceful load).
2. **Use `p` for non-electrical placement only:** label offsets (move-labels), **net-label** positions, and
   canvas-object positions snap to `p` (or free) — NOT to `P`. Net labels explicitly never participate in the
   connection-grid invariant.
3. **Do NOT** let `p` touch any connection point — pins, wire endpoints/bends, dots stay on `P` (Layers 1–4).
   The fine grid is quarantined to decoration/labels.

**Layer 5 gate:** `p` exists, persists, defaults to `P/20` (`p=5`); labels/net-labels/canvas-objects snap to `p`;
connection points unaffected (R7 invariant still passes). Report.

---

## LAYER 6 — §5: cross-grid paste (warn + snap + validate)

1. **Clipboard payload carries source grid.** In `SchematicClipboard` (copy path), add the source
   `GridSize` (= `P_src`) to the copied payload; the paste path reads it. (This is the one net-new persisted
   field.)
2. **On paste, compare `P_src` to `P_dst`** (in/feeding `SchematicPasteCommand`):
   - Equal → paste as today (name-collision resolution only).
   - Different → (a) **snap** pasted connection points to `P_dst`: snap each pasted component **origin** to
     `P_dst` (lands pins on `P_dst` via R2/R3), snap pasted wire endpoints/bends and dots to `P_dst`; labels/
     net-labels/canvas-objects snap to `p_dst` or keep relative offset; (b) **warn** via Messages (§8): e.g.
     *"Pasted content was created on a {P_src}-unit grid; this schematic uses {P_dst}. Pins were snapped to
     this grid — verify connections."* (warning, not block, not silent).
3. **Preserve intra-group coincidence:** snapping must keep relative connectivity within the pasted group — if
   two pasted pins were coincident on `P_src`, they remain coincident on `P_dst` (snap the group by one
   consistent transform / snap origins so intra-group coincidences hold). If some intra-group connection can't
   be preserved (incommensurable grids), **report the specific offenders to Messages** — never silently drop.
4. **Validate after snap:** run the R7 invariant over the pasted-and-snapped content; any point still off
   `P_dst` is a reported warning, never a silent off-grid pin.
5. The snap is **part of the paste command** (one undoable action).

**Layer 6 gate:** copy from a design on a different `GridSize`, paste into this one → pins snap to `P_dst`,
connections work, a warning appears; same-grid paste is unchanged; intra-group coincidences preserved; the
snap is one undo. Report.

---

## Acceptance (whole feature)
1. Every built-in component's pins land on `P` after placement; a wire endpoint snaps exactly onto a pin and
   connects (easy to connect — the owner's hard requirement).
2. Placement/rotation/mirror/drag/nudge keep all pins on `P`; all wire endpoints/bends on `P`; junction dots
   on `P` — verified by the R7 invariant test after a full edit battery.
3. Connectivity is decided by exact on-`P` equality; `ConnectTolerance` is a float-dust guard only; all
   existing connectivity/Hero tests pass **unchanged** (values must not change).
4. A fine authoring grid `p = P/20` (`p=5`) exists, persists in `.csch`, and governs labels/net-labels/canvas-objects
   only — never connection points. Net labels are not grid-constrained.
5. Pasting content authored on a different grid snaps connection points to this design's `P`, warns the user,
   preserves intra-group coincidence, and is one undoable action; same-grid paste is unchanged.
6. `dotnet build`/`dotnet test` green; firewall green; nothing in prior phases regresses.

## Guardrails
- **Two grids, never conflated:** `P` for all connection points (exact equality); `p` for decoration/labels
  only. Net labels never on the connection grid.
- **Connection correctness is established at INPUT** (snap at placement/draw/paste), not by tolerance after.
  Demote `ConnectTolerance`; don't lean on it.
- **Values must not change:** existing connectivity/Hero tests pass unchanged through Layer 4. If one changes,
  STOP and report — it's a latent off-grid point, not a test to update.
- **Body art is free; only pin tips are on `P`** (R2). Don't redraw symbols; just move lead tips to `P`
  multiples.
- **Cross-grid paste = warn + snap + validate, not block, not silent.** Report offenders to Messages.
- Sub-gate the six layers; report and stop between them; don't run the full suite into the output limit.
- Update `docs/design/grid-and-connectivity.md` STATUS and `src/Ui/CLAUDE.md` (the two-grid rule, the on-grid
  invariant, net-labels-not-on-grid, cross-grid-paste warn+snap) to match what was built.

*Exit: pins, wires, and connection points are exactly on the coarse connection grid `P` so connection is easy
and the 6e netlist will be correct; a fine authoring grid `p` gives placement freedom for bodies/labels/
decorations without touching connectivity; pasting foreign-grid content warns and snaps onto `P`; and a
headless invariant test guarantees the on-grid property holds after any edit.*
