# Brief 4 — scope on the canvas: regions, picked copper, nets

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp4-n` · **Phase:** feature
**Area:** `src/Design/Layout/Em/TraceImpedanceScope.cs`, `TraceImpedanceAnalysis.cs` (the scope filter),
`src/Design/Layout/Drc/DrcConnectivity.cs` (read only — its islands), the Impedance panel and its view
model (brief 3), `src/Ui/Layout/LayoutEditorViewModel.cs` (a scope tool), `src/Render/Layout/LayoutOverlay.cs`,
`src/Cli/Impedance.cs`, `src/Cli/Serve/ToolCatalog.cs`
**Depends on:** 2, 3 · **Blocks:** nothing
**Rule:** the boards, their vendors and the reporter must not be named anywhere in the repo.

---

## 0. What width cannot do

Width (brief 2) fails when an RF trace shares its width with other traces — a 50 Ω line and a
50 Ω-width digital bus on the same layer — and when the RF path is several widths (a taper, a
different width per layer). The designer asked for exactly the two other ways a reviewer points at a
board: **draw round the RF area** ("a lasso or something like this"), and **mark these traces as
RF**. Where the artwork carries nets (Gerber X2 `%TO.N`, or a board netlist applied on import —
both already fill `LayoutShape.Net`), a net name is the third.

All three are **selectors** under brief 2's rule: in scope = layer analysed AND (no selector OR any
selector matches) AND width filter. They add to `TraceImpedanceScope` and persist with it.

## 1. `R-imp4-1` — regions

- **`R-imp4-1a`** `TraceImpedanceScope.Regions`: polygons in DBU, each with an optional name. A region
  is a BOARD area and applies to every layer.
- **`R-imp4-1b`** A trace matches a region when **any part** of its centre line lies inside it. This is
  deliberately generous: a lasso that clips the end of the RF trace must not drop the trace, and a
  digital trace that wanders in can be removed by the width filter. Whole traces are analysed —
  a region never cuts a trace short (that would invent an "open end" at the region's edge).
- **`R-imp4-1c`** Drawing: the panel's Scope section has **Add region ▸ Rectangle / Lasso**. Each arms
  a canvas tool: Rectangle is a drag; Lasso is a freehand drag closed on release, simplified to a
  polygon (Douglas–Peucker at a few screen pixels, converted to DBU) and refused below three
  vertices. Esc cancels the gesture. Follow how the editor arms and disarms its other tools, and clear
  held-key latches on focus loss (project memory: a latched Space broke marquee select).
- **`R-imp4-1d`** Regions are drawn as a dashed outline with a light tint while the Impedance panel is
  open, listed in the panel (name, delete), and selecting one in the list highlights it. Editing is
  delete and redraw; no vertex editing in this brief. Not undoable, per brief 2's R-imp2-3b.

## 2. `R-imp4-2` — picked copper

- **`R-imp4-2a`** `TraceImpedanceScope.Picks`: `(LayerName, X, Y, Extent)` with `Extent = Trace |
  Connected`. A pick is a POINT, resolved at every run — never a stored list of shapes — so it
  survives edits and re-imports as long as copper is still there.
- **`R-imp4-2b`** `Trace`: the traces (chains) whose copper contains the point on that layer.
  `Connected`: every trace lying on the copper galvanically joined to the point, across layers through
  vias — `DrcConnectivity`'s islands, the same connectivity railRF and DRC use, never a second walk. A
  series part breaks an island, so a path through a DC-block capacitor is two picks: say so in the
  panel's hint text.
- **`R-imp4-2c`** Gesture: **Pick traces** arms a click tool; click = `Trace`, Shift-click =
  `Connected`. Also a canvas context-menu item on copper, **Add to impedance review**, beside the
  existing **Trace Impedance** probe item.
- **`R-imp4-2d`** A pick that resolves to no copper at a later run is kept and listed as
  *"no copper here now"* (brief 2's R-imp2-3c rule), never silently dropped.

## 3. `R-imp4-3` — nets, when the artwork has them

- **`R-imp4-3a`** `TraceImpedanceScope.Nets`: net names. The panel shows a searchable Nets list only
  when some shape on an analysed layer carries a `Net`; otherwise the section is absent, not empty.
- **`R-imp4-3b`** A trace's net is the `Net` of the drawn shape containing its middle station on its
  layer (the layout's spatial index; a point query, not a scan). A trace whose shapes disagree or carry
  none has no net and matches no net selector — count those in the panel so the gap is visible.
- **`R-imp4-3c`** This is where schematic back-annotation lands later: anything that writes
  `LayoutShape.Net` makes the RF net selectable here. Say so in the type's doc comment. It is not
  built in this series.

## 4. `R-imp4-4` — report and CLI

- **`R-imp4-4a`** `ScopeText` (brief 2) names each selector: *"2 regions ('RF front end', 'antenna');
  1 pick (Top Copper, connected); net RF_OUT; width 457 µm on Top Copper."* The PDF's maps draw the
  regions as a dashed outline so a reader sees what was reviewed.
- **`R-imp4-4b`** CLI: `--region x0,y0,x1,y1` (a rectangle; **every coordinate carries a unit and a
  bare number is refused**, `render --window`'s rule, for the same reason), `--net <name>`,
  `--pick <layer>@<x>,<y>[:connected]` — each repeatable, each REPLACING the saved selectors of its
  kind for that run. Saved lasso polygons apply by default (brief 2's rule); there is no CLI spelling
  for drawing one. `explain --extents` already emits coordinates in the spelling `--region` needs —
  confirm and say so in cli.md.

## 5. Tests (minimal — one per claim)

1. A region around one of two same-width traces scopes to that one; a region clipping only its end
   still includes it whole (its length unchanged).
2. A `Trace` pick selects one chain; a `Connected` pick through a via selects the traces on both
   layers.
3. A net selector selects the traces on shapes carrying that net; a trace on unnetted copper is
   counted as having no net.
4. A pick on empty board is kept, reported as resolving to nothing, and the run still succeeds.
5. Scope round-trips through the `.clay` with all three selector kinds.
6. The verb: `--region` with a bare number is refused; with units it matches test 1.

## 6. Scope

- No vertex editing of regions; no per-layer regions.
- No exclude-selectors ("everything but…"): the width filter is the pruning tool. Record in
  `src/Design/RESOLVED.md` if a real board shows it is needed.
- No schematic back-annotation (overview §4).

## 7. On completion

Findings in `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`. Update the user reference (the Scope
section of the panel; the three CLI flags).
