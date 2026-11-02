# Brief AS-3 — The recognised board: ground, vias, signal islands, ports, scope

**Series:** `brief-artsch-0-overview.md` (D2, D3, D6, D7, D8, D9, D11, D16) · **Tag:** `R-as3-<m>`
**Depends on:** — (independent of AS-1 and AS-2)
**Area:** new `src/Design/Layout/Recognition/` (`ArtworkRecognition.cs` entry point, `BoardGraph.cs`,
`GroundReading.cs`, `ViaClassification.cs`, `PortDiscovery.cs`, `RecognitionScope.cs`, `RecognitionReport.cs`,
`RecognitionOptions.cs`); reads through `src/Design/Layout/Extraction/` (`CopperPieces`, `Regions`, `PlacedPins`),
`src/Design/Layout/Drc/DrcConnectivity.cs`, `src/Design/Layout/Em/EmPortExtraction.cs`,
`src/Design/Layout/Em/EmSetupResolver.cs`; writes `docs/design/artwork-to-schematic.md`

---

## 1. Goal

The first stage of recognition, and the one that answers the field report directly: from a flattened board, find
**what is ground**, **which vias matter**, **which copper islands carry signal**, and **where the ports are** — so a
board with hundreds of stitching vias becomes a handful of signal islands, a ground node and a few ports. Everything
later phases do is stated in terms of this graph.

## 2. Requirements

**R-as3-1 — Entry point and inputs.** `ArtworkRecognition` takes a `RecognitionInput`: the layout view and its
resolved technology (the walk `TraceImpedanceAnalysis.LoadLayout` and `RailArtwork` already use — not a third
one), optional `.cem` setup, optional placement and BOM tables, optional parts CSV (AS-4), a `RecognitionScope`
and `RecognitionOptions`; plus a `RunControl` for progress and cancellation. It returns a `RecognitionResult`
holding the board graph, later stages' outputs (null until those phases land) and a `RecognitionReport`. Pure,
side-effect free, posting nothing.

**R-as3-2 — The partition is the existing one.** Islands come from `CopperPieces.Build` / `DrcConnectivity` — the
one partition DRC, railRF and LVS share. No second connectivity model. Each island records its layer(s), area,
bounding box, the vias on it, the placed pins and pads on it, and any net name its shapes state.

**R-as3-3 — Ground (D6).** `GroundReading` chooses the ground piece: a stated ground-named net; else the piece
holding the largest-area copper on the stackup's reference conductor. Pours the trace review would skip are folded
into ground when connected to it. A galvanically separate pour is a signal island and is reported by name and
location. `RecognitionOptions.GroundNet` / `GroundAt` (a point) overrides, and an override that lands on no copper
is a refusal naming the point.

**R-as3-4 — Via classes (D7).** Every via is classified by the islands at its two ends: **stitching**
(ground–ground, dropped and counted), **signal transition** (signal–signal on different layers → a VIA element),
**pad ground** (a ground pad that is its own small island, to ground → VIAGND elements, nearest four per pad, the
rest counted), **signal short** (signal island to ground → VIAGND). "Small island" means not a pour by the trace
review's own pour rule, so there is one definition. Each kept via records its drill, pad, span and position; AS-6
turns those into stackup-bound parameters through `ViaSubstrateInjection`. The `vias=ground` option turns every
VIAGND into GND.

**R-as3-5 — Signal islands.** Every non-ground island in scope that touches at least one part pad, port or line is
a **signal island**: one node of the circuit before AS-5 subdivides it into line elements. An island touching
nothing is listed as *copper read as nothing* (a test point, a logo, a fiducial) and dropped.

**R-as3-6 — Ports (D8, D9).** `PortDiscovery`, in the priority the overview states:
1. the `.cem`'s ports (`EmSetupResolver` + `EmPortExtraction` — the ports the user already placed for EM);
2. layout pins and numbered port labels (`EmPortExtraction.NumberPorts`);
3. pads of multi-pin parts (more than two pads) that land on a signal island — a part read from a placed instance,
   a placement row or a land-pattern cluster; the full parts reading is AS-4's, and AS-3 only needs "this island
   ends at a multi-pin part";
4. a signal island that reaches the board outline (the outline layer the importer marked, else the copper
   bounding box), or a connector footprint (a placed instance or placement row whose refdes starts `J`, `P`, `X`);
5. a crossing of the scope boundary (R-as3-7).
Each port records its position, its island, its source class and a name (`P1`, …, or the label's own text). No
port in scope is a refusal: *"nothing in scope reaches a port — add port labels, a .cem, or widen the selection"*.

**R-as3-7 — Scope (D11).** `RecognitionScope` is the whole layout, a set of polygons (the GUI selection's
outline), or a rectangle. Copper is clipped to the scope with `LayoutClipper`; a signal island cut by the boundary
gets a port at each crossing, centred on the cut and named `X1`, `X2`, …. Ground is still read from the **whole**
board (a clipped region's ground is the board's ground), and the report says when the scope cut a ground pour.

**R-as3-8 — The report.** `RecognitionReport` is a list of `RecognitionFinding(Class, Count, Sentence,
Anchors)` — never free text alone — so the GUI can expand a class and cross-probe its anchors and the CLI can
print one line per class. AS-3's classes: ground chosen (and how), vias dropped, VIAGNDs kept and capped, VIAs kept,
separate pours, copper read as nothing, ports by source, scope cuts.

**R-as3-9 — The design note.** `docs/design/artwork-to-schematic.md`, from the overview: the pipeline, every locked
decision with its reason, and AS-3's rules in full. Later phases each add their section.

## 3. Not in this phase
Parts (AS-4), line elements (AS-5), emitting anything (AS-6). AS-3 is gated on the graph and the report alone.

## 4. Gates (minimal tests, run only these classes)
Synthetic boards built in memory (a `LayoutView` + a two-layer and a four-layer synthetic technology):
- `BoardGraphGroundTests` — a top pour stitched by 200 vias to a bottom plane, a 50 Ω line through a gap in it:
  ground is the pour + plane, all 200 vias are stitching, the line is one signal island; a stated `GND` net wins
  over area; an override point on empty board is the refusal.
- `ViaClassificationTests` — a shunt pad on its own island with six vias → four VIAGND, two counted; the same
  pad on the pour → GND, no VIAGND; a signal via between layers → VIA; `vias=ground` → no VIAGND at all.
- `PortDiscoveryTests` — a `.cem` port wins over a label at the same place; a line to the board edge is a port; a
  scope rectangle cutting a line makes `X1`/`X2`; a board with no port is the refusal sentence.
- `RecognitionCountersTests` — the 200-via board reads its partition **once** (a counter on the
  `ConnectivityCache`), not once per via.
- `ArtworkRecognitionFieldTests` — `FixtureFact` on `testdata/artwork-boards/`: on each board present, ground is
  found, stitching vias are dropped, and the port count equals the count recorded in that fixture's
  `expected.json` (written by hand from the board; git-ignored with the board).
