# Brief 96 — Several field plots drawn at once

**Tag:** `R-em3d96-n` · **Series:** 3D editor round 9 follow-ups.
**Area:** `src/Ui/ThreeD/C3dEditorViewModel.FieldPlots.cs` (the drawn-plot rule, `ApplyVisiblePlot`),
`src/Ui/Viewer3D/Viewer3DViewModel.{Fields,Plot,Temperature}.cs` (one plot's state becomes one LAYER's),
`src/Ui/Viewer3D/Viewer3DOverlay.cs` (legends), `src/Ui/Viewer3D/Shaders/scene.wgsl` + the three generated shaders,
`src/Render/Scene3D/Scene3DFramePlan.cs` (field draws), `src/Render/Scene3D/Fields/FieldView.cs` (`FieldUniforms`,
`Scene3DFieldGeometry`), `src/Ui/ThreeD/C3dPropertiesViewModel.FieldPlot.cs` (phase controls, `PlotIsDrawn`),
`src/Ui/Viewer3D/FieldPicture*.cs` (Export picture), `docs/user/src/reference/em-3d.md`
**Depends on:** the 2026-09-29 field-plot fixes (offset slider, unclipped ClipPlane slices, `Scene3DDepthTie.Field`,
`FieldSliceIndex`, the held legend width, the section independent of the plot) · **Blocks:** —

## Why

The owner wants two slices of one quantity at two positions, or two quantities on orthogonal planes, on screen together.
Today **one plot is drawn at a time**: ticking a plot unticks the one drawn before (`ShowOnly`), the viewer holds exactly
one plot's state (`_plot`, `SelectedFieldSolution`, `SelectedFieldQuantity`, `_fieldVolume`, `FieldScale`,
`FieldText`, `FieldPlotProblem`, the legend), and the GPU has one field colour block.

## Owner decisions already taken (2026-09-29)

- **D1 — up to four plots drawn at once.** Ticking a fifth is refused, and the refusal names the four that are drawn
  ("Four plots are drawn: untick one of Field1, Field2, Field3, Cut to draw Field5"). Show All ticks the first four
  in list order and says so.
- **D2 — one colour range per quantity.** Plots of the **same quantity at the same solution, in the same dB and
  percentile**, share one range: the percentile is taken over the union of their triangles, so colours compare between
  the two slices, and **one legend** serves the group (titled with every plot name in it). Anything else ranges on its
  own. A temperature group shares its true min/max, and "fixed across the sweep" when every member says so.
- **D3 — legends stacked down the right,** where today's single legend is, one below the next, each titled with its
  plot name(s) and quantity. All the legends share **one held width**, the column's widest (the held-width rule
  `Viewer3DViewModel.HeldLegendWidth` introduced for the slider twitch), so a drag of any one plot moves none of them
  sideways. A legend that would run past the bottom of the view is not drawn; the last one drawn ends with "+N more".

## 1. `R-em3d96-1` — the document: at most four drawn

- `C3dFieldPlot.Hidden` stays the per-plot state and the file format does not change. A document saved before this
  brief (one plot shown) opens identically.
- `ShowOnly` goes. `SetPlotShown(name, true)` shows that plot and leaves the others as they are, **unless four are
  already drawn**: then it is refused with D1's sentence (posted where the tree's refusals go) and nothing changes.
- `NewFieldPlot`, `DuplicateFieldPlot` and a face's *Plot Field* / *Plot Temperature* show the new plot. With four
  already drawn, the new one is **added hidden** and the same sentence says so. It is never allowed to untick one of
  the user's.
- Show all / Hide all include the plots (brief 90). Show all with more than four plots ticks the first four in list
  order and reports it.
- `VisibleFieldPlot` becomes `VisibleFieldPlots` (list order, at most four), and `ApplyVisiblePlot` becomes
  `ApplyVisiblePlots`: one request per drawn plot, each keyed as today (`_appliedPlotKey` per plot name), so a plot
  whose request did not change is not sent again. The offset preview (`_plotOffsetPreview`) is already keyed by plot
  name and applies to whichever drawn plot it names.

## 2. `R-em3d96-2` — the viewer: one plot's state becomes a layer's

Carve today's single-plot state out of `Viewer3DViewModel` into a `FieldLayer` class (one per drawn plot, keyed by plot
name). Each layer holds its request, its solution item, its quantity, its surfaces and packed vertices, its scale, its
text and problem, its build token (`_fieldCts`, `_fieldBuildingCts`, `_fieldBuildPending`), its geometry version and its
hot spot. `SetPlot(request)` becomes `SetPlots(IReadOnlyList<FieldPlotRequest>)`; a layer whose plot is no longer drawn
is disposed and its builds cancelled.

- **Loaded steps are shared.** The loaded `FieldStep`s are cached by (run directory, solution), so two plots of one
  solution read the volume once. `FieldStep.ResidentBytes` of each step counts once. A step no layer uses is released.
  The index `FieldSliceIndex` is already per mesh, so two slices of one mesh share it.
- **Builds are per layer**: each layer's own cancel-and-restart, and its own drag coalescing (`FieldPlaneDragging`
  becomes per layer, set by the offset preview for the plot it names). A layer's build never cancels another's.
- **One clock**: φ, Play and the loop period (s/cycle) stay single and apply to every animated layer. The thermal sweep
  step control stays single and applies to every temperature layer on that run.
  - **Animated layers are always in phase (owner, 2026-09-29).** Every animated layer's block is written with the SAME
    φ in the same frame (`WriteFieldUniforms` writes all four from one value), so two plots animate in lock-step and
    never drift. It is one clock, not per-plot clocks that happen to share a period. That is also the physics: for two
    quantities at one frequency (E on one plane, B on another, or two slices of E), a common φ in Re{v·e^{jφ}} shows
    their true relative phase, which is what comparing them animated is for. There is no per-plot s/cycle.
  - **Two plots at DIFFERENT frequencies** also share φ, so each shows one cycle of its own per loop period. That is a
    phase-for-phase comparison, not the true time relation (which would advance each at its own ω). The legend's φ line
    for such a pair says "φ is each plot's own cycle". **Owner decision D5:** whether true-time animation (φₖ = ωₖ·t,
    scaled so the lowest frequency takes one loop period) is wanted. The recommendation is not in this brief.
- **What the Inspector and the tests read today** (`Viewer.Plot`, `FieldScale`, `SelectedFieldQuantity`,
  `FieldQuantities`, `FieldText`, `FieldPlotProblem`, `FieldGeometry`): keep them as the **focused** layer's, which is the
  plot selected in the tree when it is drawn, else the first drawn plot. That keeps `LoadFieldPlot` and the existing
  gates reading one plot, and it is the plot the Inspector shows. Add `FieldLayers` for everything that needs all of
  them.
- The value under the cursor (`R-em3d29-3e`): **owner decision D4.** The recommendation is the value of the layer whose
  surface is **frontmost** under the cursor, named with its plot (`Field2 · |E| = 3.1 kV/m`). The alternative is one line
  per drawn layer that has a surface under the cursor.

## 3. `R-em3d96-3` — the GPU: one buffer, one draw per layer, one colour block per layer

- **One field buffer** holds every layer's packed vertices, concatenated in drawn-plot order. The upload happens when
  any layer's geometry changes, as today.
- **`FieldUniforms` becomes four blocks** (`MaxLayers = 4`), each today's: φ, range, mode, dB, stop count, the
  unclipped flag (`fmode.w`) and its sixteen stops. The uniform block grows by 3 × 288 bytes, from 1,152 to 2,016. That
  stays **under Metal's 4 KB `setVertexBytes` limit**, and the Metal backend must assert it. `Scene3DFramePlan.UniformBytes`
  stays derived, so D3D11's constant buffer and Vulkan's descriptor range follow.
- **Which block a fragment uses: one draw per layer, the layer index in the per-draw `MX` uniform** (`mx.id.y`; `id.x`
  is already the id offset an array element adds). Each layer's draw takes a transform slot holding the identity and
  its index, which is the mechanism all three backends already set per draw (brief-em3d-46), so no backend learns
  anything new. `vs_field` passes `mx.id.y` to `fs_field` as a flat varying, and `fs_field` reads that layer's block.
  - *Rejected:* choosing the layer from `vertex_index` against per-layer ranges in the uniform block. That works without
    new slots, but it relies on `SV_VertexID` / `gl_VertexIndex` / `vertex_id` including the draw's first vertex on every
    backend, and D3D11 and Vulkan cannot be run on this machine to prove it.
- **Depth**: every layer's draw takes `Scene3DDepthTie.Field`. Two coincident layers resolve by draw order (later wins
  under LessEqual), so the focused layer is drawn last.
- Regenerate the shaders with `tools/ShaderGen` (`Viewer3DFrameGateTests.Gate1b` holds the hash).

## 4. `R-em3d96-4` — ranges and legends (D2, D3)

- Group the drawn layers by (quantity symbol, solution key, dB, percentile, temperature?). Each group's range is
  `FieldColorScale.Auto` over the union of its members' surfaces (a temperature's `MinMax`), recomputed when any member
  is rebuilt. Every member's block gets that group's range.
- `Viewer3DOverlay.Legend` draws one legend per group, stacked from the top right, 8 DIPs apart, with one held width
  (D3). The title line lists the group's plot names (`Field1, Field3`).
- **Export picture** draws the same stack (`FieldPictureShot` takes a list of legends instead of one).
- `render --field` stays one plot per invocation. That is out of scope and should be said in `cli.md`'s `--field`
  paragraph only if it is ever asked about.

## 5. `R-em3d96-5` — the Inspector

- `PlotIsDrawn` is `!Hidden`, as today. φ, Play and the loop period sit under every drawn plot, and each one's controls
  drive the one clock, so moving φ under Field1 moves Field2's too. The tooltip says so.
- The tick in the tree, not the Inspector, is where a plot is shown. D1's refusal appears in the Inspector's error line
  when the tick came from there.

## 6. Gates (headless: counters and records, no pixels seen)

1. Two ClipPlane plots of |E| at two offsets and a third of B on an orthogonal axis are drawn together. The viewer holds
   three layers, the frame plan has three field draws with layer indices 0, 1 and 2, and the uniform block's three
   blocks hold the expected modes.
2. D2: the two |E| plots share one range, equal to `Auto` over the union of their triangles. The B plot's range is its
   own. `FieldLegendGroups` has two entries, the first titled `Field1, Field2`.
3. D1: a fifth tick is refused with the sentence, and nothing changes. New Field Plot with four drawn adds it hidden.
   Show all with six plots ticks four.
4. A drag of one plot's slider rebuilds that layer only (the other layers' build counters are unchanged), and no
   layer's build is cancelled by another's.
5. Two plots of one solution load the step once (a read counter, not a timing).
6. A document from before this brief (one plot shown) opens and draws exactly as it did: same triangles and range as
   `FieldPlotTests.Gate1`.
7. Hide one of two drawn plots: its layer is disposed, its build cancelled, and the other layer's geometry is unchanged
   (by version, not by rebuild).
8. The shader hash gate, and the Metal backend's `UniformBytes <= 4096` assertion.
9. Phase lock: with two animated plots drawn and Play on, every frame's uniform block carries the same cos φ / sin φ in
   both layers' blocks, sampled over a run of animation ticks. Changing the loop period, or moving φ from either plot's
   Inspector, moves both.

## 7. On completion

Record findings in `src/Ui/RESOLVED.md`, never `CLAUDE.md`. Update `docs/user/src/reference/em-3d.md`'s "One plot is
drawn at a time" paragraph: up to four, shared ranges, the stacked legends. Do not run DocGen; the owner regenerates at
the end of the series.
