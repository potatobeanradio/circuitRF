# Library Palette — tile border (real root cause) + component-pin drag-follow (Claude Code / Sonnet)

Two items. **(A)** The tile border STILL won't show after multiple brush changes — because the likely real
cause is a **Color-vs-Brush** resource mismatch on `BorderBrush`, not the chosen color. **(B, the significant
one)** Component placement connectivity-follow: a pin that lands on a **wire's mid-span (a T-junction)** is
detected as connected at placement, but a **subsequent drag of that component does NOT carry the wire** — only
pin-on-wire-ENDPOINT follows today. Read `docs/design/placement-connectivity-and-drag-follow.md` first.
Sub-gated; **B is instrument-first** (the drag/connectivity code is intricate and load-bearing). Report
between layers. Firewall green.

> Read first: `docs/design/placement-connectivity-and-drag-follow.md` (the follow semantics + what already
> works). Context code: `src/Ui/ViewModels/SchematicViewModel.cs` —
> `UpdateConnectedWireEndpointsLive` + `CommitDragAsCommand`'s follow-wire block (**match only `orig[0]`/
> `orig[^1]` to moved ports — the endpoint-only gap**), `BuildPortMoves`, the wire-segment **stem-follow**
> logic `FindStemsOnSegment`/`RouteStem`/`StemFollow` (the structurally identical "wire attached to a moving
> line follows" solution to REUSE), `CommitDragAsCommand` (the undoable `MoveCommand` + `followWireSnaps`).
> `src/Ui/Schematic/EditableSchematic.cs` — `BuildRenderModel.IsConnected` (already detects pin-on-segment;
> the single connectivity source), `ComputeConnectivityGeometry`, `SchematicGeometry.PointOnSegmentInterior`/
> `CoincidentPoints`, `ConnectTolerance`, `LiveDotMaxObjects` perf gate. Border: `src/Ui/Controls/
> PaletteTile.axaml` (`BorderBrush="{DynamicResource SystemBaseLowColor}"` — `SystemBaseLowColor` is a
> **Color**, not a Brush), `src/Ui/App.axaml` (`CrfWarningBrush` is an example of a real `SolidColorBrush`
> resource).

## Part A — tile border: fix the Color-vs-Brush mismatch (do this first, it's small)

`BorderBrush` requires a **Brush**. `{DynamicResource SystemBaseLowColor}` (and the other `System*Color`
keys) resolve to a **`Color`**, not a `SolidColorBrush` — assigning a Color to a Brush property can silently
fail to render, which is why **no color change helped**. (Note `Background` often auto-converts a Color, which
is why the panel backgrounds work — but `BorderBrush` may not.)

1. Define an explicit **`SolidColorBrush`** resource (e.g. in `App.axaml` resources, like the existing
   `CrfWarningBrush`): e.g. `CrfTileBorderBrush` with a visible subtle color (a mid-low gray, ~30–40% against
   the panel — pick one that reads in light AND dark; you may need a theme-variant pair or a semitransparent
   gray like `#55808080` that works in both).
2. Point the tile's `BorderBrush="{DynamicResource CrfTileBorderBrush}"` (and the armed-state border) at the
   **brush** resource. Keep `BorderThickness=1`, `CornerRadius=3`.
3. Verify the border now renders in both light + dark; armed accent still overrides.

**Part A gate:** the tile border is **visibly rendered** (light + dark) — confirming the Color→Brush fix was
the issue. Report (screenshot description). 

## Part B — component-pin drag-follow for T-junctions

### Spine
- **Reuse the single connectivity source** — detect "pin on wire body" with the SAME
  `PointOnSegmentInterior`/`CoincidentPoints` + `ConnectTolerance` the connectivity pass uses; never a second
  predicate.
- **Reuse the stem-follow approach** — `FindStemsOnSegment`/`RouteStem` already solve "a wire attached to a
  moving line must follow + re-route orthogonally with its far end anchored." The component-drag follow is the
  same problem with the moving line being a component PORT instead of a dragged wire segment. Mirror it; don't
  invent a parallel re-route.
- **One undoable MoveCommand** — the followed wire re-routes inside the same `MoveCommand` (mirror
  `followWireSnaps`), so one Undo restores component + every followed wire.
- **O(N), perf-gated** — match the existing live-connectivity gating (`LiveDotMaxObjects`); no O(N²) scan.
- **Don't regress** the working endpoint-follow, wire-segment drag, pinning, or merge logic.
- **Scope fence (B):** make pin-on-wire-BODY connections follow a component drag (live + commit). NOT
  autorouting, NOT pin-to-pin component coupling, NOT extraction changes.

### LAYER B1 — INSTRUMENT: confirm the gap + the follow geometry
The drag/connectivity code is intricate; **diagnose before editing**:
1. Add temporary logging in `UpdateConnectedWireEndpointsLive` + `CommitDragAsCommand`'s follow block: for a
   component placed with a pin **on a wire's mid-span**, confirm the current code finds **no** following wire
   (because it only matches `orig[0]`/`orig[^1]`). Confirm a pin on a wire **endpoint** DOES follow (the
   working case).
2. Confirm the detection that SHOULD fire: the port's ORIGINAL world position lies on a wire's
   `PointOnSegmentInterior`. Log which wire + which segment for the mid-span case.
**Report findings — no fix yet.** (Remove the logging in B2.)

**Layer B1 gate:** instrumentation confirms (a) endpoint-pin wires follow, (b) mid-span-pin wires do NOT
follow today, (c) the mid-span pin's original position is detectable via `PointOnSegmentInterior` on a
specific wire/segment. Report.

### LAYER B2 — implement the T-junction follow (live + commit), reusing stem-follow
1. **Detection:** extend the follow logic so, in addition to matching wire endpoints to moved ports, it
   detects wires whose **segment interior** contains a moved port's ORIGINAL world position (the same
   `PointOnSegmentInterior` + tolerance the connectivity pass uses).
2. **Follow + re-route:** for such a wire, the contact point must track the port's NEW position. Mirror
   `RouteStem`/the stem-follow re-route: split/treat the contact as the junction, keep the wire's far
   structure anchored, re-route orthogonally so the junction sits on the moved port. Apply live
   (`UpdateConnectedWireEndpointsLive`) and fold a snapshot into the commit `MoveCommand` (mirror
   `followWireSnaps`) so it's one undoable action.
3. **Multi-pin:** every connected pin (endpoint or body) of the dragged component follows its wire.
4. **Remove** the B1 instrumentation.

**Layer B2 gate:** place a component with a pin on a wire's mid-span (T) → the pin shows connected (already
works); **drag the component → the wire follows, staying attached** (re-routing orthogonally), and on
drag-end the connection persists; one Undo restores everything; the endpoint-follow case + wire-segment drag +
pinning + merge are unregressed; perf gate respected. Report (screenshot description of before/after drag).

## Acceptance
1. The tile border renders (Color→Brush fix), light + dark, armed accent intact.
2. A component pin on a wire **mid-span** (T-junction) follows the component on drag — live + committed, one
   undoable MoveCommand — reusing the single connectivity predicate + the stem-follow re-route; endpoint-pin
   follow and all existing drag/pin/merge behavior unregressed; O(N) perf gate respected.
3. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **A:** `BorderBrush` needs a `SolidColorBrush` resource, not a `*Color`; define + use a real brush; verify
  light + dark.
- **B:** reuse the single connectivity predicate (`PointOnSegmentInterior`/`CoincidentPoints`/
  `ConnectTolerance`) and the stem-follow re-route (`RouteStem`); don't fork either.
- **B:** one undoable `MoveCommand` (mirror `followWireSnaps`); O(N), perf-gated; instrument before editing the
  drag code.
- **Scope fence:** border brush + T-junction drag-follow only — no autorouting, no pin-to-pin coupling, no
  extraction changes.
- Sub-gate the layers; report and stop between each (Part A, then B1 instrument, then B2).
- Update `placement-connectivity-and-drag-follow.md` (implemented), `library-palette.md`, `src/Ui/CLAUDE.md`
  (the Color-vs-Brush border gotcha; pin-on-body drag-follow reuses stem-follow).

*Exit: tiles show a visible border, and a component connected to a wire mid-span carries that wire when
dragged — the connection survives the move, matching the endpoint-connection behavior.*
