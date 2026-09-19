# Brief 5 — the chart, the grippers, and the drag that must be one undo entry

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith5-n` · **Phase:** P1
**Area:** `src/Ui/Smith/`, `src/Ui/DataDisplay/Controls/PlotControl.cs` · **Depends on:** 3, 4 · **Blocks:** 7
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §5.4, §4.3, §3.4, §3.6, §4.2

---

## 0. What this brief delivers

The chart pane: a `Plot` built from `SmithCascade`, hosted in a `PlotControl`, plus **one new overlay seam
on that control** carrying the grippers. It is the brief where the tool starts being the tool.

### `R-smith5-1` — one Smith renderer, and this is not a second one

The chart is a Data Display `Plot` in `PlotType.Smith` rendered by `PlotControl`, which brings the grid,
the arcs, the zoom-aware numbering, pan, zoom, marker add/drag/hit-test, the inspector, the context menu
and axis limits — none of it re-implemented.

**This is railRF's choice, not harmonicaRF's, and the reason is specific**: harmonicaRF wrote its own
canvas because it had a frame budget to defend and a contour pipeline to schedule. This tool has neither —
a whole re-evaluation is a few hundred complex divides (brief 2 `R-smith2-1`). A second Smith renderer
would drift from the first and the difference would be invisible until someone compared a screenshot with
an export.

---

## 1. `R-smith5-2` — the traces

The view model rebuilds the `Plot` from the evaluator. Its traces, in draw order:

| trace | source | style |
|---|---|---|
| one per **enabled element** | brief 2's trajectory sampler | the element's colour, arrowhead at the midpoint |
| **load points** | `Evaluate(d, f)` last node, per generator-table row | markers with label boxes |
| **conjugate targets** | `Γ(conj(Zgen(f)))`, per row | faint, **un-selectable** glyph |
| **swept band** (brief 9) | the band | thin continuous locus |
| **overlays** (brief 8) | Touchstone / cubes | per-row colour and style |

- **The design frequency's load point is drawn emphasised**; the others are secondary.
- **`S1P`/`S2P` elements draw a two-point dashed chord** and carry no gripper (brief 2 `R-smith2-6`).
- **The arrowhead's midpoint and tangent come from brief 2**, which reports them. Do not re-derive them
  from the polyline here; two adjacent arcs sharing a gripper are otherwise ambiguous about direction and
  a second derivation is a second chance to get the sign wrong.

### `R-smith5-3` — the label boxes are the loadpull ones

The per-frequency load point's label is drawn by `ContourRenderer.DrawIsoLineLabel`, placed by
`ComputeLabelAnchors` — the same padded, world-unit-spaced, staggered box the loadpull iso-lines use.
**Call it; do not draw a box.** The two surfaces cannot then drift apart in appearance, which is the whole
reason the note names this reuse specifically.

### `R-smith5-4` — autoscale, and the thing not to clamp

The Smith plot's own autoscale already enforces a unit-circle **minimum**
(`Plot.AutoscaleEnforceUnityMinimum`) and grows past it when the data asks. A node outside the unit circle
— an active `S2P`, a Z1P with negative R — **is drawn**. Clamping to the disc would be a lie about a
stability result.

The constant-Q arcs (brief 9) are chrome and are **excluded from autoscale**; so are the conjugate
targets, which would otherwise let a wildly mismatched generator set the window.

---

## 2. `R-smith5-5` — THE TRAP: a `PlotControl` with no container copies nothing

`PlotExporter.CopyPlotToClipboardAsync` opens with `if (container is null) return;`, and `PlotControl`
obtains its container from `ContainerProvider?.Invoke()`. **A `PlotControl` hosted without a
`PlotContainerViewModel` produces no clipboard content, raises nothing, and looks exactly like a
successful copy.**

railRF sets `plot.ContainerProvider = () => container` in `RailRfWindow.axaml.cs`. **Do the same here, in
this brief**, even though the copy itself is brief 7 — the wiring belongs with the hosting, and brief 7's
gate is written to fail if this is missed.

Set `NextMarkerIndexProvider`, `FindMarkerInfoBoxVmProvider` and `SelectedMarkersProvider` at the same
time, for brief 8's markers. **And give the marker info boxes somewhere to be drawn**: railRF shipped a
round where markers existed before a panel could host their boxes in the control's own coordinate space.
Host them from the start.

---

## 3. `R-smith5-6` — the overlay seam

`PlotControl` has no overlay mechanism. Add one, following the shape the control already uses for its four
`Func<>` hooks rather than inventing a control-extension pattern this codebase does not otherwise have:

```csharp
public IPlotOverlay? Overlay { get; set; }

interface IPlotOverlay
{
    void Draw(SKCanvas canvas, PlotTransform tf, RenderTheme theme);
    object? HitTest(double canvasX, double canvasY, PlotTransform tf);   // a handle, or null
    void DragBegin(object handle);
    void DragTo(Complex gammaWorld);
    void DragEnd(bool cancelled);
}
```

Rules on the seam, each one a defect somewhere else in this repository:

- **The canvas is an argument, never an assumption.** `ContourRenderer` once drew every contour on every
  Smith plot to the first target it was given.
- **The overlay never mutates the `Plot`.** It draws transient chrome and calls back; the view model
  rebuilds the plot. This is `ILayoutCanvasOverlay`'s own rule and it is why that seam has held.
- **Hit-test order is overlay first, then the control's own markers.** A gripper under a marker is
  unreachable otherwise, and the marker is the thing a user can move out of the way.
- **The overlay returns `false`/null when it does not want an event**, and the control's existing
  behaviour runs — pan, zoom and marker drag must be unaffected where no handle is under the cursor.

### `R-smith5-7` — grippers

One at **every node** of the walk: N+1 for N enabled elements.

- **Node 0 is the generator and is an ANCHOR, not a gripper.** It is drawn, so the walk has a visible
  start, and it does not drag: a drag there would have to guess which generator-table row it meant.
- **Node k, for k ≥ 1, drags element k−1**, changing exactly that element's `ActiveParameter` (brief 6
  sets it; brief 3 solves it). Nodes k+1 … N follow. **This is the feature** — dragging a mid-cascade
  element and watching the load point move is the single most-named behaviour in the specification.
- **Subtle**, per the specification: a small hollow ring in the trajectory's own colour, brightening on
  hover, filled while dragging. **Above the trajectories, below the markers** — harmonicaRF's own z-order
  rule.
- **A pinned drag keeps tracking** (brief 3 `R-smith3-4`): the value pins, the gripper stays under the
  cursor along the boundary, and the status strip names the parameter and the limit. A handle that stops
  moving reads as a broken drag.

---

## 4. `R-smith5-8` — one drag is ONE undo entry

Every pointer-move during a drag mutates the design and redraws. The undo entry is pushed **on release**,
carrying the before-value captured **on press**. `DragEnd(cancelled: true)` — an Escape mid-drag — restores
the before-value and pushes nothing.

**This is a requirement, not a style preference.** The Match Designer shipped a defect where a two-way-bound
slider's coercing write-back reached an unguarded setter *during* `Undo`, so every undo **added** an entry,
redo was wiped, and eight edits took fourteen undos to unwind. It is recorded in `src/Ui/Match/RESOLVED.md`.

The rule that comes out of it, and it applies to every control in this window:

> **A control's write-back is not an edit.** Publish bounds before value, and the model's value setter must
> be able to tell whether it is being driven by the user or by a restore.

---

## 5. The gate

`tests/Ui.Tests/Smith/SmithChartTests.cs` — one test per claim:

1. **The plot is what the evaluator said.** Build a known three-element design; assert the trace count,
   the load-point count (one per table row), and that the last trajectory's end point equals
   `Γ(Evaluate(...).Last())` exactly.
2. **A drag moves everything downstream.** Drag node 1 of a four-element cascade; assert elements 1-3 keep
   their values and nodes 2-4 all moved. This is the specification's headline behaviour.
3. **One drag is one undo entry** (`R-smith5-8`): synthesise press → 20 moves → release, count entries
   (exactly one), Undo once, and land exactly on the before-state. Then *n* drags, *n* undos.
4. **Escape mid-drag restores and pushes nothing.**
5. **Node 0 does not drag**, and hit-testing it returns no handle.
6. **Hit-test order**: a gripper coincident with a marker is reachable, and a click on empty chart still
   pans (`R-smith5-6`).
7. **`ContainerProvider` is set** (`R-smith5-5`) — asserted directly, because brief 7's copy silently
   produces nothing without it.
8. **Chrome is excluded from autoscale**, and a node outside the unit circle is **not** clamped
   (`R-smith5-4`).

---

## 6. What this brief must NOT do

- **No second Smith renderer**, no bespoke canvas, no `FrameScheduler`, no adaptive quality.
- **No clipboard call.** Brief 7. This brief only sets the provider that makes one possible.
- **No overlays, no markers beyond wiring the providers.** Brief 8.
- **No constant-Q arcs.** Brief 9 adds them to the overlay this brief creates.
- **No change to `PlotControl`'s existing behaviour.** The seam is additive: with `Overlay = null` the
  control must behave exactly as it does today, and the Data Display's own tests must still pass.
- **No palette of its own.** Colours come from the active circuitRF theme through
  `RenderTheme`/`ThemeService`. harmonicaRF has a phosphor-green theme because it is a standalone
  instrument; this is a document, and a document that ignores the user's theme is a document that looks
  broken in dark mode.

---

## 7. On completion

Findings to `src/Ui/RESOLVED.md`. **Never a `CLAUDE.md`.**
