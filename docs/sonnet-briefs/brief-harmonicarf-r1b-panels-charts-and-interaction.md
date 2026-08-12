# Brief — harmonicaRF Round 1B: the panels, the charts, and the gestures

**Read first:** `docs/design/harmonicarf.md` (**§4.2**, **§6.4–6.5**, **§7.1–7.4**, **§7.7**), then
`src/Harmonica/CLAUDE.md` and `src/Ui/CLAUDE.md`'s **H4–H5**, **H6** and **H7** entries.

**Round 1 is three briefs and they are independent.** **1A** is the crash, the menu policy and colour.
This one is the panels: two dead gestures, the Smith titles, a user-settable Z₀, the grid-point
toggle, the power-sweep axes, the DCIV panel and the loadline. **1C** is the toolbar removal, the
readouts, Set DUT and the export.

**§1 is two diagnoses, and one of them is already solved by reading.** Do §1 first — the rest of this
brief is unusable in practice until dragging works again.

---

## 0. What already exists

| you need | it is here |
|---|---|
| the pointer gesture | `HarmonicaGesture` (`src/Ui/Harmonica/HarmonicaPointer.cs`) — framework-free, takes doubles |
| the marker/glyph/grid hit test | `HarmonicaHitTest.Resolve` — three passes, z-ordered |
| the Edit Display hit test | `HarmonicaEditTarget.Resolve` (`HarmonicaEditDisplay.cs`) |
| the control that drives both | `HarmonicaCanvas` (`src/Ui/Controls/HarmonicaCanvas.cs`) |
| the ONE Γ↔canvas transform pair | `HarmonicaPanelRenderer.GammaToCanvas` / `CanvasToGamma` / `MarkerToCanvas` / `CanvasToMarker` |
| panel placement, in fractions | `CharmLayout` (`src/Harmonica/CharmLayout.cs`) — `DefaultPanels` is §7.1 transcribed |
| the panel renderers | `HarmonicaPanelRenderer` (`src/Ui/Harmonica/Renderers/`) |
| the panel composer | `HarmonicaCanvasRenderer` — ONE composer, live canvas + Copy Plot |
| the frame's contents | `HarmonicaSolver.Solve` → `HarmonicaFrame` |
| the Γ grid and its optima | `ContourGrid` (`src/Harmonica/ContourGrid.cs`) — **already takes a `z0` ctor arg** |
| the fitted metric surface | `ContourGrid.Fit(metric)` → `Rbf2D`, factorization cached across frames (R-hrf-9) |
| the rasterised surface | `ContourGrid.Raster(metric, resolution)` — already computed per panel per frame |
| one Pin drive-up at a termination | `PinSearch.Run(ctx, terminations)` — ~4.6 HB solves per Γ point (measured) |
| Γ ⇄ Z | `HarmonicaDataSet.GammaOf` / `ImpedanceOf` — **`Z0` is a `const 50.0`** |
| the DCIV family and its cache key | `DcivFamily` (`src/Harmonica/DcivFamily.cs`) — `Key`, `DefaultKey`, `Compute` |
| the time-domain loadline | `IntrinsicPlane.Loadline(dut, v, nodes, k, gridN, drainPort, sourcePort)` |
| the compression setting | `CircuitModel.Settings.CompressionDb`, edited as input `settings.compression` |
| the efficiency metric | `HarmonicaViewModel.EfficiencyMetric` (`GridMetric.DrainEfficiency` / `Pae`) |
| the X-unit cycle | `PowerSweepXUnit` + `HarmonicaViewModel.CyclePowerSweepXUnitCommand` |
| `.charm` read/write | `CharmIo` (`src/Harmonica/CharmIo.cs`) — absent field ⇒ default, no version bump |

---

## 1. Two dead gestures

### 1.1 R-h9b-1 — Edit Display: the cause is known, and it is two lines

> **owner:** *"edit display shows grippers but the grippers don't resize anything and I can't drag any
> of the UI elements to a new position."*

`HarmonicaGesture` keeps the two grabs in two separate fields, deliberately (its own doc comment: the
two hit tests answer different questions over different sets). In edit mode `PointerDown` sets
`EditGrab` and explicitly sets **`Grab = HarmonicaGrab.None`**.

`HarmonicaGesture.IsDragging` is `Grab.IsGrab` — so it is **false for the whole of an Edit Display
drag**. And `HarmonicaCanvas` gates both follow-up handlers on it:

```csharp
protected override void OnPointerMoved(...)    { ...; if (Gesture is not { IsDragging: true } g) return; ... }
protected override void OnPointerReleased(...) {       if (Gesture is not { IsDragging: true } g) return; ... }
```

So `PointerMoved` and `PointerUp` are never reached during an edit drag: nothing moves, nothing
resizes, and `EditDisplay.EndGesture()` never runs so no undo entry is ever pushed. The gesture class
itself is correct — `ApplyEdit` handles both Move and Resize and `HarmonicaEditDisplay.Write` does the
clamping — it is simply never called.

**Fix the gate, not the gesture.** The canvas must follow up whenever the gesture holds *anything*.
Add one property to `HarmonicaGesture` that means "this gesture is live" (`IsDragging ||
EditGrab != None`) and have the canvas test that; do not make `IsDragging` itself true for an edit
grab — `HarmonicaHitTest`'s own callers and `PointerUp`'s marker/glyph/grid branches read
`Grab.Kind`, and conflating the two would make an edit drag look like a marker drag to them.

**Gate it with a counter, not a screenshot:** `HarmonicaGesture.MoveCount` already increments in both
branches, and `HarmonicaEditDisplay.Undo` exposes the stack. A headless test that drives
`PointerDown` / N × `PointerMoved` / `PointerUp` **through `HarmonicaCanvas`'s own handlers is not
possible** (it is a `Control`), so drive `HarmonicaGesture` directly for the placement maths and pin
the canvas's gate by source scan — the pattern `LayoutCanvasActivationTests` already uses for exactly
this situation.

### 1.2 R-h9b-2 — `HarmonicaCanvas` is not focusable, so its keyboard handler is dead

Found while diagnosing the above, and it is a second real defect: **`HarmonicaCanvas` never sets
`Focusable = true`.** `SchematicCanvas` (line 203) and `LayoutCanvas` (line 229) both do, in their
constructors; `HarmonicaCanvas` has no constructor at all.

Consequences: `HarmonicaCanvas.OnKeyDown` can never fire, so **Escape-cancels-a-drag and
Delete-removes-the-panel-under-the-pointer (§7.7) are both unreachable**, and
`HarmonicaView.FocusCanvas()`'s `Canvas.Focus()` is a no-op — which also means `HarmonicaDocument`'s
`IActivatableDocument` activation-focus wiring lands nowhere. Set `Focusable = true` and confirm all
four work.

### 1.3 R-h9b-3 — marker and grid-point drags: diagnose, do not guess

> **owner:** *"Can't click-drag marker terminations using move. Same with the grid point glyphs. They
> don't move when I click and drag on them."*

Unlike §1.1, this one is **not** provable by reading. Work down this list and **report which it
actually was** — the completion note must say what was found, not what was assumed:

1. **The canvas may not be receiving pointer events at all.** That would explain both this report
   *and* §1.1 with one cause. Check first, because it is the cheapest to rule out: does
   `OnPointerPressed` run? `HarmonicaCanvas` is a bare `Control` with no `Background`; `PanelHost` is
   a `Panel` with none either; `ReadoutHost` is a `Border` with `Background="Transparent"` (which IS
   hit-testable) sized and positioned by `PlaceReadoutStrip` — confirm its rect really is the
   readout-strip fraction and not covering the Smith panels (e.g. when `PanelHost.Bounds.Width <= 0`
   on the first pass and `Width`/`Height` are left unset).
2. **`HarmonicaHitTest.Resolve` may be missing.** It resolves the panel first (`PanelAt`, which only
   knows the two Smith panels), then tests markers, glyphs and grid points in that z-order. The
   transform pair is R-h6-1's and is shared with the renderer, so a systematic offset is unlikely —
   but the grab radius is `GrabRadiusDevicePixels / renderScaling` = 7 DIP on a 2× display against a
   marker drawn at `min(W,H) * 0.020` DIP, which on a short panel is a genuinely small target.
3. **The drag may be applying and not surviving.** `Apply` → `SetMarkerGamma` → `SetMarkerImpedance`
   raises `RedrawRequested`, then `RequestScheduledFrame` submits a solve whose completed frame is
   published on the UI thread. A published frame that overwrote the marker positions would look
   exactly like "it does not move". `PublishFrame` writes `Frame`, and `Frame.Markers` is the SAME
   list by reference (R-h45-3) — check that this still holds after `ApplyInverseOutcome`.
4. **`HarmonicaView.Refresh()` runs on every frame and rebuilds the readout strip**, which is a lot of
   layout churn per pointer move. Confirm it is not stealing the pointer capture.

Whatever it is, **add the counter that makes it diagnosable next time**: `MoveCount` exists; expose
whether the last `PointerDown` resolved a grab and what kind. A test asserting a synthetic
press-move-release on a marker's own canvas position produces a changed `Terminations.Z(...)` is the
regression gate, and it is drivable headlessly through `HarmonicaGesture` with no window.

---

## 2. The Smith chart titles

### R-h9b-4 — two rows, and both are live

> **owner:** Power title `"P-xdB Power (dBm)"`, Efficiency title `"P-xdB Efficiency (%)"` or
> `"P-xdB PAE (%)"`, where *x* is the compression setting; a second row beneath, centred with the
> chart, naming the swept plane: `"Fundamental Load Plane, Z0=xΩ"` for 1f₀ load, `"2f0 Source Plane,
> Z0=xΩ"` for 2f₀ source, `"4f0 Load Plane, Z0=xΩ"` for 4f₀ load, and so on. Both rows update when
> the compression setting, the efficiency metric or Z₀ changes.

- *x* is `CircuitModel.Settings.CompressionDb` — format it without trailing zeros (`3` not `3.0`,
  `2.5` as `2.5`).
- Efficiency vs PAE is `HarmonicaViewModel.EfficiencyMetric`.
- Band 1 reads **"Fundamental"**; bands ≥ 2 read `"{n}f0"`. The plane is
  `HarmonicaViewModel.GridSide`; the band is `GridHarmonic`. Both already exist and are already
  document-wide (see the `HarmonicaSolver.Options.GridSide` doc comment for why per-chart was
  rejected on cost) — **do not make them per-chart here**.
- Z₀ is §3's new value, formatted as an integer where it is one.

**`SmithPanelData.Title` is a single string today and `NewSmithPlot` hands it to
`Plot.CustomTitle`.** Two rows with different sizes and a centring rule are more than that carries.
Either add a second field (`Subtitle`) and draw it in `HarmonicaPanelRenderer.DrawSmithPanel` after
`PlotRenderer.Draw`, or draw both rows there and stop using `CustomTitle` at all. **Whichever you
pick, the strings must be built in ONE place** — a formatter next to `SmithPanelData` that both
panels call — so the two charts cannot disagree about how a compression setting is spelled.

**Centring is with the CHART, not the panel.** The Smith viewport is inset by
`PlotRenderer.ComplexSideMargin` and then scaled by `HarmonicaPanelRenderer.AnnulusHeadroom`; a
subtitle centred on the raw panel width will look off. Use the same transform the panel already
computes.

### R-h9b-5 — the title font shrinks by 0.8

> **owner:** *"Reduce the font size rendering of the Smith Chart titles from its current value by a
> factor of 0.8."*

Find where the title's size is actually decided. If it comes from `PlotRenderer`'s own title font,
**do not change `PlotRenderer`** — every Data Display plot in the application reads it. Apply the
factor on harmonicaRF's side (a `RenderTheme`/`Plot` field it already sets per panel, or by drawing
the title in `DrawSmithPanel` yourself per R-h9b-4). State in the completion note which route you took
and what the resulting size is.

---

## 2A. MXP and MXE are the INTERPOLATED optimum

> **owner, asked directly:** *"MXP and MXE are supposed to be the interpolated value."*

**They are not, today, and this is a real defect rather than a wording question.**
`ContourGrid.Extremum(metric)` is a linear scan over `_points` returning
`GridExtremum(Index, Point, Value)` — the best **grid SAMPLE**. `HarmonicaSolver.BuildSmith` puts
`grid.Mxp?.Point.Gamma` / `grid.Mxe?.Point.Gamma` into `SmithPanelData`, and
`HarmonicaPanelRenderer.DrawOptima` draws the crosses there. So on a 5 × 12 grid the glyph can sit up
to half a ring away from the true optimum, and it visibly jumps between ladder rungs as the ring count
changes — which is exactly the artefact an interpolated optimum exists to remove.

**1C's MXP/MXE readout columns consume what this section produces.** It is scoped here because the
surface, the raster and the glyph all live in this brief's files; 1C reads the resulting record.

### R-h9b-15 — the optimum is the argmax of the fitted surface, not of the samples

`ContourGrid.Fit(metric)` already returns the `Rbf2D` interpolant the contours are drawn from, with
its factorization cached across frames (R-hrf-9). **The optimum must be the argmax of that same
surface**, so the glyph, the contours and the readout are all describing one object. Deriving it any
other way puts a cross somewhere the iso-lines say is not the peak.

Three things to get right:

- **Seed from the raster, refine off the surface.** `ContourGrid.Raster(metric, resolution)` is
  already computed once per panel per frame (`BuildSmith` explicitly rasters once and derives both the
  levels and the polylines from it, rather than calling `Contours`, which rasters again — do not
  undo that). Take the raster's argmax cell as the seed, then refine locally on the `Rbf2D` — a few
  steps of a local optimiser or a quadratic fit through the neighbouring cells is enough.
- **The answer must not depend on the raster resolution.** D5 switches between
  `CoarseRasterResolution = 96` while dragging and `FullRasterResolution = 256` on release. If the
  refinement is doing its job, the optimum found at 96 and at 256 agree closely — **that is the gate**,
  and it is what proves the refinement is real rather than a dressed-up cell centre. State the measured
  agreement.
- **Respect the support mask.** `ContourGrid` carries holes (thrown-out Γ points) and the raster is
  masked by them. An optimum refined into a region where nothing converged is a confident answer about
  a place the solver never reached. Refuse to leave the supported region.

Keep `GridExtremum`/`Extremum` — the best-sample answer is still the honest seed and several callers
may read it. **Add** the interpolated result rather than mutating the meaning of the existing one.

### R-h9b-16 — the FOMs at that state come from a SOLVE, not from N separate surfaces

The interpolated Γ is not a solved point, so its Pout / DE / PAE / Gain / Gp / Zin / AM-PM have to come
from somewhere. There are two routes and only one of them is defensible:

- **Interpolate each FOM off its own fitted surface.** Cheap, and wrong in a way that does not show:
  the values would be mutually inconsistent (an interpolated Pout and an interpolated Pdc need not
  satisfy the DE that a third surface reports), and `Zin` / AM-PM have no fitted surface at all —
  they are not grid metrics.
- **Solve one Pin drive-up at the interpolated termination.** `PinSearch.Run(ctx, terminations)` with
  the optimum's Γ substituted into the swept band, then `HarmonicaDataSet.Build(ctx, at.Point,
  terminations)` for `Zin` and the intrinsic quantities. Every number is then the same state,
  consistently, and Zin/AM-PM exist at all.

**Take the second.** The cost is two extra drive-ups per frame (MXP and MXE) at a measured ~4.6 HB
solves each, against ~280 for a 61-point grid — roughly 3%. **Measure it and report the real figure**
rather than repeating that estimate; `HarmonicaSolver.LastSolveCount` already counts everything.

Two conditions on when it runs:

- **Not while dragging.** §6.8's ladder exists to shed exactly this kind of work. Solve the optima on
  full-quality frames; on a degraded rung, carry the previous frame's answer or omit it. Say which.
- **Not when `SkipContours` is set.** Tier A alone sweeps no grid, so there is no surface and no
  optimum. `Extremum` already returns null when every point is a hole; the same "no optimum" state
  must be representable and must be reported rather than drawn as a cross at the origin.

### R-h9b-17 — one record, both consumers

Put the resolved optimum on `SmithPanelData` as a small record carrying at least: the interpolated Γ,
the interpolated metric value, and the solved `PinStep` (plus whatever `HarmonicaDataSet.Build`
produced for Zin/AM-PM). `SmithPanelData.Mxp`/`Mxe` are `Complex?` today; widen them or add a sibling
field — either way **the glyph and 1C's readout column must read the SAME record**, so the cross and
the numbers can never describe different states. That is the one invariant this section exists to
create.

---

## 3. A user-settable Z₀

### R-h9b-6 — one value, on the model, persisted

> **owner:** *"The Z0 normalization reference impedance for the Smith Charts needs to be adjustable by
> the user… For best visualization, the user should set Z0 = Ropt… Contours and termination
> impedances/gammas need to respect the user-defined Z0. This Z0 needs to persist within the .charm."*

**Where it lives:** `HarmonicaSettings` (`src/Harmonica/CircuitModel.cs`), default 50.0.
`CharmIo`'s `CharmSettings` gets a nullable field so an older `.charm` takes the default — **no
`FormatVersion` bump**, exactly like every other setting there.

**Is it structural?** It changes no circuit — Γ is a *display and grid* parameterisation, and the
terminations the engine reads are impedances. So it must **NOT** move `CircuitModel.StructuralKey`, or
every Z₀ nudge would rebuild the context and reset the frame ladder (R-h7-3). **But it does change the
Γ grid**, so a change must re-solve. Check `StructuralKey` explicitly and say which way it went.

**The four places 50 Ω is currently hardcoded, all of which must read the setting:**

| site | what it does |
|---|---|
| `HarmonicaDataSet.Z0` (`const 50.0`) + `GammaOf` / `ImpedanceOf` | the Γ ⇄ Z conversion the inverse solve and the published cubes use |
| `HarmonicaViewModel.SetMarkerImpedance` (`(z - 50.0) / (z + 50.0)`) | a marker's Γ from its impedance |
| `HarmonicaViewModel.SetMarkerGamma` (`50.0 * (1+Γ)/(1-Γ)`) | a drag's impedance from its Γ |
| `HarmonicaViewModel.RebuildMarkersFromTerminations` (same expression) | markers rebuilt on load |

Plus `ContourGrid`'s `z0` constructor argument and `SolvePool`'s — **both already exist and both
default to 50**; the pool creates the workers' grids, so the value has to reach it.

**The rule that matters: a Z₀ change must not move any impedance.** The `TerminationSet` holds
impedances; changing the reference re-expresses them as different Γ values at the same physical
terminations. A marker must therefore **stay at the same Z and move on the chart**, not stay at the
same chart position and silently become a different termination. Pin that as a test: set a marker to
80 + j10 Ω, change Z₀, assert `Terminations.Z(...)` is bit-identical and `marker.Gamma` changed.

**Where the user sets it:** a §7.5 input row (`HarmonicaInputs`, key `settings.z0`, unit `Ω`) is the
cheapest correct answer and inherits the commit/revert contract for free. `HarmonicaInputs.Build`
derives `Structural` by applying a probe and comparing the key, so it will classify itself correctly
with no second table.

**`HarmonicaDataSet.Z0` is `public const`** — check for external readers before changing its shape.

---

## 4. The grid-point visibility toggle

### R-h9b-7

> **owner:** *"User should be able to toggle whether the grid points are visible or not. This setting
> needs to persist within the .charm file. Default setting is off (invisible)."*

- A `bool` on `CharmAppearance` (it already carries `ShowIsoLineLabels`, which is the same shape of
  display-only toggle, and it already round-trips through `CharmIo`'s appearance block). Default
  **false**.
- A Display-menu item on **both** menu surfaces, mirroring `ToggleIsoLineLabelsCommand` exactly.
- `HarmonicaPanelRenderer.DrawGridPoints` returns early when off.
- **Hit-testing must follow visibility.** `HarmonicaHitTest.Resolve`'s third pass must not grab an
  invisible grid point — grabbing something the user cannot see is the exact failure the z-ordered
  passes exist to prevent. Thread the flag in rather than letting the caller pass `null` for
  `gridPoints`, so the reason is legible.
- **The default is now OFF, so R-h7-12's grid-point drag is invisible by default.** Say so in the
  completion note: the drag still works when the points are shown, and `.gam` import/export is
  unaffected.

---

## 5. The power-sweep panel

### R-h9b-8 — the right-hand Y label is wrong

> **owner:** *"The power sweep plot right y-axis label says 'real(S(1,1))'. It should say 'Efficiency
> (%)' or 'PAE (%)', depending on the user efficiency metric setting."*

`HarmonicaPanelRenderer.BuildPowerSweepPlot` sets `CustomYLabelOn`/`CustomYLabel` for the primary axis
and **nothing for the secondary** — so the right axis falls back to the trace's own auto-derived
label, which for the placeholder `SNP`-backed `Trace` reads `real(S(1,1))`. Set the secondary label
explicitly from `EfficiencyMetric`. `PowerSweepPanelData` does not carry the metric today; add it (the
solver already has `opt.EfficiencyMetric` in hand).

### R-h9b-9 — the right axis is drawn in the efficiency colour

> **owner:** *"The right y-axis line and numerical axis text color should be Harmonica.EfficiencyTrace."*

`HarmonicaRenderTheme.ToPlotTheme` maps one `TickColor`/`TextColor` for the whole plot. Per-axis
colour is either a `Plot`/`Axes` capability that already exists (check `AxesRenderer` first) or a
harmonicaRF-side draw. **Do not add a per-axis colour to the shared `AxesRenderer` for a harmonicaRF
requirement** unless it is genuinely general — the `AnnulusHeadroom` precedent is the rule here. If it
turns out `AxesRenderer` already supports it, say so and use it.

Note `Harmonica.EfficiencyTrace` is **reserved red** in both variants, and there is an existing test
asserting red is reserved to it and the loadline. Colouring the right axis with it is consistent with
that reservation, not a violation — but re-run the test.

### R-h9b-10 — right-click the X-axis label to choose its unit

> **owner:** *"Right-clicking on the power sweep plot x-axis label should create a context menu with
> the following settings: Pout (dBm), Pout (W), Pin (dBm), Pin (W). The plot is to use whatever this
> is set to as the x-axis of the plot."*

The four values already exist as `PowerSweepXUnit` and are already cycled by
`CyclePowerSweepXUnitCommand` (§7.4's click-to-cycle). This adds a direct pick.

- `PowerSweepXUnit.Label()` already gives the menu text; drive the menu from the enum, not a
  hand-typed list, or the two will drift.
- Setting the unit is a **relabel of data already in hand, never a re-solve** —
  `OnPowerSweepXUnitChanged` already states and enforces that. Keep it.
- **Hit-testing "the X-axis label" needs the plot's own transform**, and the panel is placed by
  `CharmLayout`. Resolve the panel first (the same two-step `HarmonicaHitTest.ToPanel` already does),
  then the label rect within it. If the label rect is not exposed by `AxesRenderer`, a generous band
  along the bottom of the power-sweep panel is an acceptable target — say which you used.
- **A right-click must not begin a drag.** `HarmonicaCanvas.OnPointerPressed` does not currently check
  which button was pressed; check it, or the context menu gesture will also grab a marker.

---

## 6. The DCIV panel

### R-h9b-11 — it must line up with the power sweep, and the cause is the plot rect, not the panel rect

> **owner:** *"The DCIV plot needs to be horizontally align with and the same size (width and height)
> as the power sweep plot. Currently, it is neither."*

**Check the premise before changing the layout.** `CharmLayout.DefaultPanels` already gives
`Loadline` and `PowerSweep` the *identical* width (`RightColumnWidth`) and identical height
(`LoadlineHeight` = 0.50 each). So the **panels** are already the same size and already aligned.

What differs is the **plot area inside each panel**: `PlotRenderer` computes its own margins per plot
from the axis labels, and the power sweep has a secondary axis (right-hand ticks and an "Efficiency
(%)" label) that the loadline does not. So the two data rectangles have different widths and start at
different x — which is exactly what the owner is seeing.

So the fix is to make the two plots reserve the same margins, not to move the panels. Options, in
order of preference:

1. Give the loadline plot a secondary axis window/label reservation matching the power sweep's, so
   both reserve the same right margin.
2. Compute a common margin for the pair in harmonicaRF and hand it to both plots.

**Do not fix it by nudging `CharmLayout` fractions** — that would compensate for a margin at one
window size and be wrong at every other, and it would fight Edit Display.

Verify with a pixel oracle: render both panels at a known size and assert the two data rects have
equal width, equal height and equal left edge.

### R-h9b-12 — the DCIV Sweeps dialog

> **owner:** *"If user right-clicks anywhere on the DCIV plot, show a context menu called 'DCIV
> Sweeps', a DCIV Curve Tracer evaluator dialog appears that allows the user to adjust the VGS and VDS
> curve family. It updates in real time as the user changes setting. The textedit input needs to have
> a robust validator so that a DCIV trace is always shown to the user. Invalid input keeps the old
> trace visible."*

**The parameter set already exists and is exactly right: `DcivFamily.Key`** — `VgsMin`, `VgsMax`,
`VgsSteps`, `VdsMin`, `VdsMax`, `VdsSteps` (plus `StructuralKey` and `DrainPort`, which the dialog
must not offer). `DefaultKey` is what the solver uses today. So the dialog edits an override of the
six numbers, and `HarmonicaSolver` uses `override ?? DefaultKey(model)`.

Four things to get right:

- **The key IS the cache**, and its equality is tier C's rule (`DcivFamily`'s own doc comment). A
  changed key recomputes the family once and holds it — which is also what makes "updates in real
  time" affordable: the family is ~1,800 direct model evaluations, not an HB solve, and it does not
  depend on terminations.
- **Invalid input keeps the old trace.** The validator must reject and *not* write: min < max on both
  axes, steps ≥ 2 on both, all finite. This is the same commit-on-Return/LostFocus, revert-on-Escape
  contract `ReadoutStripView.SetInputs` already implements — reuse the shape.
- **Persist the override in the `.charm`** if it is set (absent ⇒ `DefaultKey`, per `CharmIo`'s own
  rule). The owner did not ask for this explicitly; do it anyway and say so, because a curve family
  the user tuned and lost on reload is the same complaint in a different place.
- **`DrainPort` stays out of the dialog.** It is resolved from the intrinsic port map, and offering it
  invites a wrong answer that draws perfectly.

**A right-click "anywhere on the DCIV plot" and R-h9b-10's right-click on the power-sweep X axis are
the same gesture arriving at two panels.** Resolve the panel once
(`HarmonicaEditTarget.Resolve`/`PanelUnderPointer` already does exactly this and is already reused by
Copy Plot — reuse it a third time rather than adding a fourth hit test).

---

## 7. The loadline's time resolution

### R-h9b-13 — 64 samples, and it is exact rather than interpolated

> **owner:** *"the load line time domain data needs more time samples. Try 64 samples for now. Have a
> user setting that allows user to change it."*

`IntrinsicPlane.Loadline` takes `gridN` and `HarmonicaSolver.BuildLoadline` currently passes
`HbFft.GridSize(K, FftOverSample)` — the solve's own FFT grid, which is why the locus is coarse.

**Do not raise `FftOverSample` to get more points.** That is a structural setting: it changes the time
grid the HB solve runs on, rebuilds the context, resets the frame ladder and changes the answer.

The spectrum carries harmonics 0…K, so evaluating the inverse transform at **any** number of time
points is exact, not interpolation — the loadline can be rendered at 64 (or 256) samples with no
accuracy claim to defend. Check whether `HbFft.Inverse` will produce an arbitrary-length grid or
whether it is tied to a power-of-two FFT size; if it is, resample by direct evaluation of the
truncated Fourier series (`Σ_h Re(spec[h] · e^{jhθ})`) at the display grid, which is a handful of
terms per point.

**The cost is not the transform, it is `dut.Evaluate` per sample** — `Loadline` calls it once per time
point. 64 points is 64 device evaluations per frame; state the measured delta in the completion note
rather than assuming it is free.

The setting belongs beside the other display settings (a §7.5 input or `CharmAppearance` — pick one
and say why), defaults to 64, and must be clamped to something sane at both ends.

---

## 8. The default marker set

### R-h9b-14

> **owner:** *"for a new harmonicaRF document, the default marker terminations shown on the Smith chart
> are: f0 load, f0 source, 2f0 load, 2f0 source, and 3f0 load"*

`HarmonicaViewModel`'s constructor currently adds S1 and L1 only, then sets two arbitrary impedances
(25 Ω and 80 + j10 Ω). Add S2, L2 and L3.

Three constraints:

- **`AddMarkerBand` is the only way to create one** — it creates the marker and its `TerminationSet`
  entry together (R-h7-2), inserts in the order `RebuildMarkersFromTerminations` would produce, and
  refuses a band above `Terminations.HarmonicCount`. The default model has `HarmonicCount = 3`, so
  all five fit — but assert that rather than relying on it.
- **§4.2's rule that S1 and L1 are ALWAYS present is unchanged.** This adds three more defaults; it
  does not make any of them unremovable. Band 1 still refuses removal on both sides.
- **A loaded `.charm` is unaffected.** `RebuildMarkersFromTerminations` derives markers from the
  file's own marked bands, and an unmarked band is the *absence* of a marker, not a default. Pin that:
  loading a `.charm` with only S1/L1 marked must still produce exactly two markers.

Give the three new bands sensible starting impedances rather than leaving them at the unmarked
near-short — a marker sitting on the rim at Γ ≈ 1 on first open is not a useful default. State the
values chosen.

---

## 9. Scope guardrails

- No menu-policy work, no crash fix, no colour-role changes (**1A**). This brief consumes 1A's roles
  only if it needs them; it adds none.
- No toolbar removal, no readout changes, no message line, no Set DUT, no export change (**1C**).
- **Do not widen `PlotRenderer` / `AxesRenderer` for a harmonicaRF-only need.** `AnnulusHeadroom`'s own
  comment states the rule: widening the shared renderer's margins would have moved every Data Display
  Smith plot to solve a harmonicaRF problem. Same for title fonts and per-axis colours.
- **`HarmonicaPanelId` strings and `ColorRole` names are file-format keys.** Renaming one silently
  drops that panel's placement or that colour for every existing `.charm`.
- **No `.charm` `FormatVersion` bump.** Z₀, the grid-point toggle, the DCIV override and the loadline
  sample count are all additive-with-a-default.
- **§6.5's plane and harmonic selectors stay document-wide.** Making them per-chart doubles the
  dominant term of a frame; `HarmonicaSolver.Options.GridSide`'s own comment records the decision.
- `src/Core`, `src/Engine`, `RfCore` untouched.

---

## 10. Gates

1. **Build + `dotnet test` green** — `tests/Ui.Tests` and `tests/Harmonica.Tests` while working, full
   solution at the end.
2. **Edit Display drags work**: unlock, drag a panel body to move it, drag a corner grip to resize it,
   release — one undo entry per gesture, Escape mid-drag restores the start layout, Delete removes the
   panel under the pointer. Counter-gated (`MoveCount`, `Undo` depth), not screenshot-gated.
3. **A marker drag moves the marker and its termination**, and a grid-point drag moves that one Γ
   sample — with the cause of the original failure named in the completion note.
4. **The canvas is focusable** and its keyboard handler fires.
5. **Both Smith titles show two rows** with the right compression value, the right metric, the right
   plane/band wording and the right Z₀ — and all four update live when their source changes. Title
   font is 0.8 × its previous size.
6. **Changing Z₀ leaves every termination impedance bit-identical** while every marker's Γ, the
   contour grid and the published `Gamma_*` cubes all move to the new reference. It survives a
   `.charm` round trip; an older `.charm` opens at 50 Ω.
7. **Grid points are hidden by default**, toggle on both menu surfaces, invisible points are not
   hit-testable, and the state round-trips.
8. **The power-sweep right axis reads "Efficiency (%)" / "PAE (%)"** following the metric, drawn in
   `Harmonica.EfficiencyTrace`; right-clicking the X-axis label offers the four units and picking one
   relabels without a re-solve.
9. **The DCIV and power-sweep DATA rectangles are identical in width, height and left edge** — pixel
   oracle, at more than one window size.
10. **Right-clicking the DCIV panel opens the curve-tracer dialog**, edits update the family live,
    invalid input leaves the previous family on screen, and the family recomputes once per distinct
    `DcivFamily.Key` (`DcivComputeCount` is the existing counter).
11. **The loadline draws 64 samples by default**, adjustable, with the measured per-frame cost
    reported.
12. **A new document opens with exactly five markers** — S1, S2, L1, L2, L3 — while a loaded `.charm`
    still produces exactly the markers its file marked.
13. **The MXP/MXE glyphs sit at the interpolated argmax of the fitted surface**, inside the supported
    region, and the answer at raster 96 and raster 256 agrees — with the measured agreement reported.
    A grid where every point is a hole, and a `SkipContours` frame, both produce "no optimum" rather
    than a cross at the origin.
14. **The FOMs at each optimum come from one solve at that state**, not from N interpolated surfaces,
    with the measured per-frame solve cost reported and the optima skipped on degraded ladder rungs.

**Interactive verification is required** for the drag gestures, the context menus and the DCIV dialog
— no visual driver here, matching every prior harmonicaRF phase. List the exact gestures in the
completion note under "please confirm on your end".
