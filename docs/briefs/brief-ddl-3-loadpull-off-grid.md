# Brief DDL-3 — loadpull at a marker anywhere on the Γ plane

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl3-<n>` · **Depends on:** DDL-1, DDL-2
**Area:** `src/RfCore/Loadpull/LoadpullSurface.cs`, `src/Render/DataDisplay/` (`Links/`, `TraceResolve`,
`ContourResolve.cs`, `SummaryResolve.cs`, `Models/SummaryTableTypes.cs`, `Models/Trace.cs`), the trace card and
summary table controls in `src/Ui/DataDisplay/ViewModels/`, `docs/design/loadpull-summary-table.md`.

## The gap

A loadpull is measured, or simulated, at a finite set of loads (`GammaLoad[gridPoint]`). The load a designer wants to
look at is very often between them. After DDL-1 a marker snapped to a grid point drives its detail plots exactly, but
a marker placed between points can only give the nearest one.

The machinery to do better is already in RfCore and is not reached from the display:
- `LoadpullSurface.GetPowerSweep(freqIdx, coord, metricX, metricY, compressionVal, plane, z0, …)` synthesises a
  drive-up (metric Y against metric X) at an arbitrary Γ or Z, from a stack of RBF surfaces spanning the back-off
  range. It is tested in `tests/RfCore.Tests/LoadpullPowerSweepTests.cs`, and `src/Ui/DataDisplay/RESOLVED.md` records
  that nothing in the UI calls it.
- `LoadpullSurface.MetricAtCoord(freqIdx, metric, coord, constraint, plane, z0, nearest)` reads one metric at a
  coordinate. The summary table reads every column through it at MXP or MXE.
- A marker on a contour trace already sits anywhere on the plane (`PositionStatic`, free) or on a node
  (`ContourSnapped`), and reads the contour's metric there (`ContourData.EvaluateMetric`).

## Requirements

**R-ddl3-1 — A position link.** A marker on a contour trace, and a marker on a Smith trace of a loadpull's `GammaLoad`
or `ZLoad`, gives a `LinkValue.Position` (DDL-1 R-ddl1-3). This holds the coordinate, its plane and reference
impedance, the loadpull group and frequency index it was read against, and whether it is snapped to a node (and which
node). A contour marker already has both modes: free roam (`ContourSnapped == false`) and snapped to a node, toggled by
its context menu's **Snap to Point**. On the `GammaLoad` Smith trace a marker is snapped by construction, so it gets the
same **Snap to Point** toggle. Unticked, the marker behaves as a free marker does under `Plot.FreeMarkers`: it is
dragged anywhere, Shift-drag snaps it back onto the grid, and it shows the free-marker ring glyph.

**R-ddl3-2 — Which read a consumer gets.** A trace on the same loadpull source whose `gridPoint` axis is pinned by a
position link resolves:
- **snapped marker** → the node's index: the measured curve, as DDL-1 does;
- **free marker, read mode Nearest** → the nearest node's index, measured, and the card names the node and its
  distance;
- **free marker, read mode Interp** (the default) → the synthesized curve (R-ddl3-3).

The read mode is the summary table's existing `TableReadMode` (Interp | Nearest). It is offered on any plot holding
such a trace, in the same place the table offers it.

**R-ddl3-3 — The synthesized drive-up.** For an Interp read, the trace's Y quantity and its X are mapped to
`LoadpullSurface` metric names through `LoadpullFomDialect`, as the contour and summary paths map them. The trace's X
is its versus operand when it has one (Gt against Pout), and otherwise the drive axis, which becomes the sweep key
`PavlDbm`, labelled as available input power. The curve is
`GetPowerSweep(freqIdx, Γ, metricX, metricY, compression, plane, z0)`, with the plot's compression and the RBF kernel
and smoothing the contours use. A quantity `LoadpullSurface` cannot fit is refused by name on the card ("ZLoad is not
a fitted metric; this trace takes the nearest node"), and that trace falls back to Nearest.
`GetPowerSweep`'s low-power clamp compares `resultX[0]` to the sweep key's floor. That is only meaningful when metric
X is on the drive's dBm basis, so the brief checks it for an X that is not, and fixes or guards it in RfCore with a
test.

**R-ddl3-4 — It looks like a model.** A synthesized curve draws dashed, and its legend reads
`Pout @ m1 (interpolated, Γ 0.42∠151°)`. When the position lies outside the convex hull of the measured loads at that
frequency, the card says the curve is extrapolated and how far outside the hull it is. That needs a hull test, added
to `LoadpullSurface` as `IsInsideMeasured(freqIdx, coord, plane)` with its own RfCore test. A marker dragged across
the hull boundary changes the note live.

**R-ddl3-5 — The table at the marker.** The summary table's operating point (`TableOptimum`: MXP | MXE) gains
**At marker**, with a link picker. Every column is then read at the marker's position through the path the table
already uses (`MetricAtCoord`, with the table's compression, Interp or Nearest). The table carries no new per-cell
logic. ZL is the marker's position converted to Z. A snapped marker reads the node's measured values in either mode.
That gives use case 1's "table of Zload and efficiency at that load" with no new table code.

**R-ddl3-6 — Frequency.** A position link carries its frequency index. A consumer on a multi-frequency loadpull reads
at that index unless its own frequency pin is set or linked (DDL-4). A marker on one frequency's contours therefore
drives detail plots at the same frequency.

**R-ddl3-7 — Cost.** The first read at a new frequency builds the RBF stacks (`BuildStackAtCompression`, cached by
`StackKey`). Later reads at a new Γ only evaluate. Until the stacks for a consumer are cached, it updates on release
(DDL-2 R-ddl2-3). After that it follows the drag. This is measured once with a scratch harness and recorded in
`src/Render/DataDisplay/RESOLVED.md`. No timing test is added.

## Gates

1. **A known surface.** A synthetic loadpull (`Pout`, `Gt`, `PAE` analytic in Γ and drive, on a 7×7 grid). At a grid
   node, the Interp drive-up matches the measured `Pout[node, :]` within 0.05 dB over the measured drive range. At the
   centre between four nodes it matches the analytic truth within 0.2 dB, and it lies between the neighbours' curves.
2. **Snapped is measured.** Snap the marker to a node: the trace is exactly `Pout[node, :]`, solid, and has no
   "interpolated" label.
3. **Nearest.** Read mode Nearest with a free marker: the nearest node's measured curve. The card names the node.
4. **Outside the hull.** A marker beyond the outermost loads: the note says "extrapolated" and the curve is dashed.
5. **Table at marker.** With At marker, every summary column equals `MetricAtCoord` at the marker's coordinate, in both
   read modes.
6. **Headless.** `render --marker m1=...` cannot place a free position, so `render` draws from the saved
   `PositionStatic`. A `.cdd` with a free m1 renders the same dashed curve as the window.

## Not here

Scattered-data interpolation for other sources (a FET bias plane takes the nearest point: DDL-4); interpolation
between frequencies; a source-pull's source Γ driving a load-pull read (both are positions, but on different
terminations — refused by name).
