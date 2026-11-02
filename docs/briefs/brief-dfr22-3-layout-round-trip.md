# Brief DFR22-3 — Layout round trip: snapping lines together, picking over a plane, guides, the tee's impedance

**Series:** designer feedback round 22 (2026-10-20) — DFR22-1 units, DFR22-2 artwork → schematic drawing, DFR22-3
layout round trip. · **Tag:** `R-dfr22-3-<n>`
**Area:** `src/Ui/Layout/LayoutEditorViewModel.cs` (pick stack ~1650-1680, `RecomputeMoveDelta` ~2801),
`LayoutEditorViewModel.Snap.cs`, `src/Render/Layout/LayoutSnapQuery.cs`, `src/Design/Layout/PCells/GroundPourPlanner.cs`,
`src/Ui/Layout/SchematicToLayoutGenerator.cs` + `src/Ui/ViewModels/WorkspaceViewModel.SchematicToLayout.cs`,
`src/Render/Renderers/LayoutRenderer*` (overlay only), `src/Design/Layout/Em/TraceImpedanceAnalysis.cs`,
`src/Ui/Layout/Impedance/ImpedancePanelViewModel.cs`, `src/Render/Renderers/TraceImpedanceReportDocument.cs`,
`src/Cli/Impedance.cs`; `docs/design/layout-view.md` §9, `docs/user/src/reference/layout-editor.md`
**Tests:** `tests/Ui.Tests` (new `LayoutRoundTripRound22Tests`), the layout snap/selection classes named in §5

---

## 1. Goal

The designer drew a stub match in the schematic (two lines and an open stub on a tee), ran Update Layout from
Schematic, arranged the generated MLIN/MTEE cells by hand, and read the layout back. Five things got in the way:

1. **Gaps.** Lines dragged next to the tee by their body landed a hair apart; only a drag started on an edge or corner
   snap marker snaps. The gaps were invisible until zoomed in, and they broke recognition (three "open end"s).
2. **Parts on the ground plane could not be picked.** He moved the plane away to move the lines. Cause, found in
   round 22: the press's pick stack adds placed instances only `if (shapes.Count == 0)`, so any shape under the point
   — a plane — makes the instance unpickable, even by cycling.
3. **The ground plane was tight** after Update Layout (it covers 5 substrate heights past the lines — about 2.6 mm on
   20 mil — and he wanted more room above them).
4. **No guide** to which line goes where: he placed two lines in each other's places on the first try. The design
   note (§9 step 4) calls for a ratsnest; R-L5h-3 removed it because it was written as real artwork, and nothing
   replaced it as an overlay.
5. **The tee is not reviewed** by the Impedance panel: `impedance` lists the three lines (all 50.2 Ω, pass) and nothing
   for the tee, whose arms are too short to be traces.

## 2. Requirements

### Snapping lines together

**R-dfr22-3-1 — A body drag snaps edges and pins.** With geometry snap on (S/F3), a drag of a selection started on a
shape's or instance's body (not a marker) is pulled, when within the snap tolerance, so that:
- a **pin** of a dragged instance lands on a **pin** of a stationary instance or a port (strongest — this is the
  line-to-line case, and `LayoutPin` positions are already known for every generated cell), else
- a dragged **edge** lands on a stationary **parallel edge** facing it (closing a gap or removing an overlap), measured
  perpendicular to the edge; the along-edge position stays the pointer's.
The pull is computed in `RecomputeMoveDelta` from the selection's moved pins/edges against `LayoutSnapQuery`'s
candidates near them — reuse the query; do not add a second spatial search. The snap marker shows where the contact
is. Grid snap still applies to the other axis. Holding nothing new: the existing toggle turns it off for fine moves.

**R-dfr22-3-2 — Cost stays bounded.** The search runs over the dragged selection's own pins and boundary edges only
(cap it — e.g. 64 edges, then pins only), against candidates within tolerance of each; assert a COUNTER for the queries
per pointer move, not a timing (memory rule: no new timing benchmarks).

### Picking

**R-dfr22-3-3 — Instances are in every press's pick stack.** Remove the `shapes.Count == 0` guard. Order: ports first
(as today); then an instance whose bbox lies inside a shape that contains the press point ranks **above** that shape
(a part placed on a plane is the thing being pointed at); otherwise ZOrder as today. Repeated presses cycle through all
of them. Marquee and Select All are unchanged. Gate: with a plane on the bottom layer under a placed MLIN, one press
on the line selects the instance; a second press at the same point selects the plane.

### The ground plane

**R-dfr22-3-4 — The pour's margin is a choice.** `GroundPourPlanner.MarginHeights` (5) becomes the default of a margin
the user can set, in substrate heights, in the Update Layout from Schematic flow (beside where the pour is offered),
remembered in the workspace's `.cws`. The planner takes it as an argument (`src/Design` rule: a preference is an
argument). A board outline, when there is one, still wins (`OutlineBox`). Re-running Update Layout with a new margin
updates the pour it drew last time and nothing else.

### The guide

**R-dfr22-3-5 — A ratsnest overlay.** Draw thin straight lines between pins that share a net and are not yet joined by
copper — an OVERLAY in the layout canvas, never a shape, never saved (the lesson of R-L5h-3). Nets come from the net
names Update Layout already stamps on instance pins; "joined by copper" from the layout's own connectivity
(`DrcConnectivity.Partition`). Each net is drawn as a minimum spanning tree over its unjoined pin groups. On by
default after Update Layout from Schematic; View ▸ Show Ratsnest toggles it; it updates as parts move and disappears for
a net once copper joins it. A pin with a net name and no partner draws nothing.

### The tee's impedance

**R-dfr22-3-6 — Junctions are reviewed.** `TraceImpedanceAnalysis` already returns `Junctions` per layer. The report
gains a row per junction: its kind (tee/cross/plain, by arm count), each arm's width and Z0 solved at that width
with the layer's own cross-section (the same solver call a trace uses — cache by width), and pass/warn/fail per arm
against the target. A placed MTEE/MCROSS cell is reviewed through its copper the same way. The panel lists them under
the traces, the canvas colours the junction by its worst arm, and the CLI `impedance` text and `--json` carry them.
"N traces" stays N traces; the summary line adds "and M junctions".

## 3. Not in this brief

Auto-routing (still out of scope, §9 step 3); a ratsnest in the schematic; snapping in the 3D editor.

## 4. Docs (sources only, no DocGen)

`layout-editor.md`: body-drag snapping, picking over a plane, the ratsnest and its toggle; the Update Layout page:
the margin; the impedance page: junction rows. `layout-view.md` §9 step 4 says the ratsnest is an overlay.

## 5. Gates

- `LayoutRoundTripRound22Tests`:
  - body-dragging an MLIN cell to within tolerance of a tee's pin closes the gap exactly (pin coincident); with
    geometry snap off it lands where dropped;
  - an edge dragged near a parallel edge lands on it; the query counter per move is bounded;
  - pick stack: instance on a plane is picked first, the plane second;
  - pour margin: 5 by default (byte-identical to today), 10 when set, outline still wins;
  - ratsnest: drawn between two unjoined pins of a net, gone once a shape joins them, never in `Model.Shapes`, never
    in the saved `.clay`;
  - impedance: the stub-match tee reports three arms at 50 Ω ± tolerance; CLI text and JSON include the junction.
- Re-run the existing classes this touches: the layout selection/cycle tests, the snap tests
  (`*Snap*`), `LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect` (the overlay must not bypass culling),
  `SchematicToLayout*`, `TraceImpedance*`, `ImpedanceCli*`. `Firewall.Tests`.

## 6. Open for the owner

- The default margin: keep 5 heights and make it settable (recommended), or raise the default.
- Ratsnest on by default after Update Layout (recommended) or only on request.
