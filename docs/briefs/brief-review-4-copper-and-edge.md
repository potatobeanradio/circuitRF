# Brief 4 — copper and the board edge: stacked traces, edge clearance, edge stitching

**Series:** [Layout Review](brief-review-0-overview.md) · **Tag:** `R-rev4-n` · **Phase:** checks
**Area:** `src/Design/Layout/Review/Checks/` (new: `StackedTracesCheck.cs`, `EdgeChecks.cs`; `ReviewBoard.cs` from brief 2),
`src/Design/Layout/Em/TraceImpedanceAnalysis.cs` (one internal entry point, R-rev4-1)
**Depends on:** 2 · **Blocks:** 6

---

## 0. What a reviewer looks for

- **Traces running parallel on top of each other** on adjacent copper layers: broadside coupling between two signals.
- **No traces at the board edge**, and copper pulled back from the edge.
- **The board edge closed with ground**: ground copper along the edge on the outer layers, and stitching vias along it
  close enough together.

## 1. Shared definitions

**R-rev4-1 — Traces come from the trace finder.** A trace is what `TraceImpedanceAnalysis`'s trace finder
(`FindLayer`) says is a trace, pours excluded, with its nominal width. Add ONE internal entry point that returns, per
copper layer, each trace's copper region and nominal width, built on the same `Prepared` the analysis and the survey use.
Nothing about how traces are found changes; this brief adds no second classifier and no morphological stand-in. On board
C a survey of all ten layers costs 25 s (brief-impedance-8), which bounds what this costs.

**R-rev4-2 — The board region: `ReviewBoard`** (built in brief 2, defined here). From the outline layers (`Em3dGenerator.BoardOutlineLayers`): the union
of the closed outline shapes; else the region the outline's strokes enclose (`holes(merged strokes)`, the largest
hole); else the outline does not close and every edge check is *not run* with that sentence and the coordinates of the
largest gap between stroke ends. **Never the outline's bounding box**: a board with a notch or a cut-out corner would
have its edge checked where there is no edge. Internal cut-outs (holes in the board region) are edges too, on D3's
default.

**R-rev4-3 — The edge band.** `not(board, sized(board, −band-width))`: the strip of board within `band-width` of the
edge. A stretch's length along the edge is its area divided by `band-width`.

## 2. The checks

**R-rev4-4 — `stacked-traces`.** For each pair of ADJACENT copper layers (consecutive conductor rows, brief 2's
`ReviewLayers`; a pair with another conductor between them is not adjacent and is not checked, because that conductor
is between them), the overlap in plan of a trace on the upper layer with a trace on the lower, each grown by
`lateral-margin` (default **0**: on top of each other), where the two are on DIFFERENT nets. The overlap's length is its
area divided by the narrower width; a piece at least `min-length` long (default **1.0 mm**) is a finding. A right-angle
crossing is w × w and never reaches a millimetre. Marker: the overlap region. Sentence: "Top Copper and Inner 1 traces
run on top of each other for 4.2 mm (0.15 mm and 0.10 mm wide, different nets)". The finding's layer is the upper one.
Two traces of the SAME net stacked (a doubled supply trace) are not a finding.

**R-rev4-5 — `edge-copper`** (brief 2 built it; this brief owns its sentence and tests). Any copper on any copper layer
inside the band of `pullback` (default **0.25 mm**). Marker: the copper inside that band. Sentence: "Copper 0.12 mm from
the board edge; the review asks for 0.25 mm".

**R-rev4-6 — `edge-trace`.** A trace (R-rev4-1), any copper layer, closer to the edge than `min-distance` (default
**1.0 mm**). Marker: the part of the trace within that distance. Copper that is not a trace (a pour pulled back to the
edge) is `edge-copper`'s business only. A trace that deliberately runs to an edge-launch connector is a finding the
reviewer accepts; the check does not try to recognise connectors.

**R-rev4-7 — `edge-stitching`.** Two kinds of finding, on a band of `band-width` (default **1.0 mm**):
- *No ground copper along the edge:* on each OUTER copper layer, the band minus that layer's ground copper
  (`on_ground`, brief 1); every remaining stretch at least `max-gap` long is a finding ("7.4 mm of board edge on Top
  Copper with no ground copper").
- *Stitching gap:* the band minus a disc of radius `max-gap` / 2 around every ground via (brief 3's definition) whose
  barrel lies in the band; every remaining stretch is a gap at least `max-gap` long, a finding ("6.1 mm of board edge
  with no ground stitching via; the review asks for one every 2.0 mm").
`max-gap` defaults to **2.0 mm**, about a twentieth of a wavelength at 4 GHz in FR-4. No ground reference flagged →
*not run* (brief 2 R-rev2-5); no outer copper layer → only the stitching half runs.

Thresholds (brief 2's table): `stacked-traces.lateral-margin`, `stacked-traces.min-length`, `edge-copper.pullback`,
`edge-trace.min-distance`, `edge-stitching.band-width`, `edge-stitching.max-gap`.

## 3. Owner decisions — the defaults are taken unless the owner changes them

- **D1 — defaults:** 0 lateral margin and 1.0 mm for stacked traces; 0.25 mm pullback; 1.0 mm trace-to-edge; 1.0 mm
  band and 2.0 mm gap for stitching.
- **D2 — stacked traces on ADJACENT layers only.** Two signal layers with a plane between are shielded from each other;
  checking non-adjacent pairs would report exactly the stackups a designer chose to prevent this.
- **D3 — internal cut-outs are edges** (slots, milled openings). The alternative is the outer boundary only.

## 4. Tests (`tests/Ui.Tests/Review/CopperEdgeChecksTests.cs`, one small layout per claim)

1. Two different-net traces stacked on Top/Inner 1 for 3 mm → one `stacked-traces` of ~3 mm; the same traces crossing at
   90° → none; the same two on one net → none; on Top/Inner 2 with Inner 1 between → none.
2. A pour 0.1 mm from the outline → one `edge-copper`; at 0.3 mm → none.
3. A trace 0.5 mm from the outline → one `edge-trace`; a pour at the same distance → `edge-copper` only if inside the
   pullback, never `edge-trace`.
4. A square board ringed with ground vias every 1.5 mm → no stitching gap; remove three adjacent vias → one gap whose
   length is reported within one via pitch.
5. An outer ground pour covering three sides of the band → one "no ground copper" finding on the fourth side.
6. An outline drawn as four stroked lines and the same outline drawn as a closed polygon → identical findings; an
   outline with a gap → edge checks *not run* with the gap's location.

Run only that class.

## 5. By hand on board C (completion note)

All four checks: wall time (the trace finder included), peak memory, counts per check and per layer, and five sample
findings of each with whether a reviewer would agree. If `stacked-traces` or `edge-stitching` produces more than a few
hundred findings, report that and why, rather than moving the default to hide it.
