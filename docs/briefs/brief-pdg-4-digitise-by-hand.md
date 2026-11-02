# Brief PDG-4 — Plot digitiser: digitise by hand

**Series:** PDG-1 … PDG-4 (plot digitiser improvements) · **Tag:** `R-pdg4-<n>` · **Written:** 2026-10-21
**Follows:** PDG-1 (two y axes), PDG-2 (curve fit), PDG-3 (snap to curve), PTU-1 … PTU-7 (the plot dialog)
**Prompted by:** designer feedback round 24 — the designer asked for a better digitiser and laid out the workflow below.

---

## 1. What this is, and what it is not

Today the digitiser is **automatic first**: `PlotReading.Read` finds the frame (`PlotFrames.Find`), reads the tick
labels (`PlotText.Read` + `RectAxes.Calibrate`) and traces the curves (`PlotTraces.Find`), and the user can only
correct what it found. A plot the frame finder misses is a dead end ("no plot was found"); a misread label is fixed by
typing `120px=1GHz,…`; a curve the tracer missed cannot be added (`TraceHints.Follow` exists but nothing sets it); a
point placed with the Point tool (PDG-3) is a readout only and never becomes data.

The designer's workflow, in order:

1. bring a picture in (browse, drag and drop, paste);
2. mark the plot area with a box — aligned to the picture, resizable, rotatable when the scan is skewed;
3. say whether the plot has one y scale or two (left and right);
4. type the scale: x-low, x-high, y-low, y-high (and the right axis's), each linear or log — **text in the picture is
   ignored; the scale the user typed is the scale**;
5. say how many curves to take and which y scale each is read on;
6. have each curve found automatically where possible, and place points along it by hand where not;
7. from the confirmed points, make the curve and sample it into x, y pairs;
8. export x, y as CSV;
9. if the curves are S-parameters, offer a Touchstone export.

**This brief adds that workflow as a second mode of the existing dialog: Manual.** It is not a recogniser brief. Manual
mode runs **no frame detection and no OCR** — that is the point of it, and it is what makes it dependable on any
picture. The only image processing it uses is the colour-following tracer that already exists, started from a point
the user clicked. (Owner standing rule: no new image-recognition investment. Nothing here improves the automatic
reader.)

## 2. Design layer — `src/Design/Imaging/Plots/`

### R-pdg4-1 — A plot defined by hand: `ManualPlot`

```csharp
public sealed record ManualAxis(double Low, double High, AxisScale Scale);       // values in the axis's own units (Hz on a frequency x)
public sealed record ManualPlot(
    int Picture, ManualQuad Box,                                                  // four corners, picture px: bottom-left, bottom-right, top-right, top-left
    ManualAxis X, ManualAxis Y, ManualAxis? Y2,                                   // Y2 = the right-hand scale (step 3); null = one y scale
    double XUnit, bool XIsFrequency);
```

- The box's **left edge is x-low, right edge x-high, bottom y-low, top y-high** (and the right edge's bottom/top are
  y2-low/high). Calibration is `AxisCalibration.FromTwoPoints` on the box edges — no `RectAxes.Fit`, no labels.
- **Rotation (step 2).** A rotated box is handled by **resampling, not by teaching the pipeline about rotation**: the
  quad's interior is resampled (bilinear) into an axis-aligned raster whose frame is the box, and everything downstream
  reads that raster with plain axis-aligned calibrations. Points the user places are mapped into it by the same
  transform, and back for drawing. Keep the transform in one place (`ManualPlot.ToBox` / `FromBox`). A quad is
  general enough for a mild perspective (a photographed page); the dialog only produces rotated rectangles in v1.
- `PlotReadingOptions` gains `Manual: IReadOnlyList<ManualPlot>`. **A picture with a manual plot is read without
  `PlotFrames.Find` and without `PlotText.Read`** — its words list is empty, so legend/title evidence is absent by
  construction and every trace's assignment comes from the user (R-pdg4-4). Plots numbered as today; the `PlotRead`
  carries a synthetic axis-aligned `PlotFrame` for the box (in resampled coordinates) so `PlotSession` consumers
  (overlay, probe, CSV, Touchstone assembly) work unchanged.

### R-pdg4-2 — A curve defined by hand: fixpoints

```csharp
public sealed record ManualCurve(string Name, int Plot, IReadOnlyList<(double Px, double Py)> Fixpoints,
                                 PlotSide Axis, PlotAssignment Assignment, int Rgb);
```

- **The fixpoints ARE the curve.** A manual curve becomes a `PlotTraceRead` whose `Curve.Points` are the fixpoints
  joined (in x order on a rectangular plot), so the probe, overlay, fit and CSV need no new path.
- **Find curve (step 6):** one call, `ManualCurves.Follow(ink, plot, seed, rgb, spacingPx)`, runs the existing tracer
  (`PlotTraces.Find` with `TraceHints.Follow = [seed]`, inside the box only, matched to the seed pixel's colour) and
  returns the followed curve **thinned to fixpoints** every `spacingPx` (default 20 px, plus every turning point), so
  the user edits a handful of points, not hundreds of pixels. A follow that finds nothing returns nothing, with a
  sentence; it never invents points.
- `TraceHints.Follow` is wired for the first time here — check `PlotTraces.cs:134` actually seeds from a click on
  a curve the automatic pass did not already take, and fix it if it does not (state which in RESOLVED).

### R-pdg4-3 — Sampling the curve (step 7): `ManualSampling`

- Inputs: the fixpoints in axis values, an x grid (**reuse `AssemblyGrid`** — start, stop, points, log — or a step
  `Δx`, converted to points), and a method:
  - **Linear** — straight between fixpoints (in log x on a log x axis, in log y on a log y axis);
  - **Smooth** — monotone piecewise cubic (Fritsch–Carlson / PCHIP) in the same transformed coordinates. Not a
    natural cubic spline: a digitised curve must not overshoot between two points the user placed.
- **No extrapolation**: grid points outside the first..last fixpoint are dropped, and the report says how many.
- Default grid: from the curve's first to last fixpoint, 101 points (lin or log following the x axis).
- PDG-2's fits still apply on top (a fit of the fixpoints, `y_fit` in the CSV) — unchanged.

### R-pdg4-4 — Output (steps 8, 9)

- **CSV**: the existing `PlotCsv` long table, one row per sampled point. A manual curve's `exact` column is `false`;
  a row at a fixpoint itself is not marked specially. Add nothing to the column list.
- **Touchstone proposal**: in manual mode a curve's **Is** combo offers `Data` (the default — just x, y) plus the
  existing entries/quantities (S21 dB, S11 ∠, …). When at least one curve is an S-parameter quantity and the x axis
  is frequency, the dialog's result step offers **Also save as Touchstone…**, assembled by the existing
  `TouchstoneAssembly` from the sampled curves (the grid of R-pdg4-3 is the assembly grid). It is an offer, not a
  default: a CSV run never writes a `.sNp` the user did not ask for.

## 3. The dialog — `src/Ui/Recognition/`

Opened exactly as now (**Tools ▸ Digitise Plot…**, **File ▸ Import ▸ Image ▸ As Touchstone…**, Project panel). The
plot reading options gain **Read: Automatic | Manual**, remembered per user (`image_dialog_plot_read_mode`).
**Digitise Plot… opens in Manual**; the two Touchstone entries open in Automatic. Switching keeps the picture.

### R-pdg4-5 — Steps on screen

Manual mode shows its steps as a short numbered column in the right pane, each one enabled when the one before it is
done, so nobody has to read the help to find calibration (the round-23 complaint):

1. **Plot area** — the canvas arms a box tool on entry. Drag draws the box; corner and edge handles resize it; a
   rotation handle above the top edge (and an **Angle** field, degrees) rotates it about its centre. A sensible start:
   if the automatic frame finder happens to find a frame on this picture, pre-place the box there — it is a starting
   suggestion only, and nothing else from the automatic reader is used.
2. **Scales** — `One y scale | Two y scales`. Fields **x low / x high**, **y low / y high** (and **right y low /
   high**), each with a **Log** toggle. x takes units (`1 GHz`, the axis-row grammar); y is a plain number with an
   optional unit label (`dB`, `°`, free text, used for the CSV's `y_unit`). Each field's value is drawn at its edge of
   the box on the canvas, so a wrong corner is visible at once.
3. **Curves** — **Add curve** (and a count shown); each row: name (`Curve 1` …), colour swatch, **Axis** (Left/Right,
   only with two scales), **Is** (R-pdg4-4), point count, ✕.
4. **Points** — with a curve selected:
   - **double-click** adds a fixpoint; **drag** moves one; **right-click** or select + **Delete** removes one
     (right-click alone is awkward on a trackpad, so both). **Escape** stops placing.
   - **Find curve**: click a pixel of the curve → `ManualCurves.Follow` fills fixpoints the user can then edit.
   - a **magnifier** (a 4× loupe at the pointer while a point is placed or dragged) — the precision a hand-placed point
     needs; PDG-3's hover crosshair already shows the value under the pointer.
5. **Sample** — grid (start, stop, points or step, log) and method (Linear | Smooth); a small preview of the sampled
   curve drawn over the picture in the curve's colour, dashed.
6. **Create** — the Make choice's output (CSV, or Touchstone), and R-pdg4-4's Touchstone offer.

### R-pdg4-6 — CSV mode stops showing Touchstone controls (both modes)

With **Make: CSV** (automatic or manual) hide **Ports**, the **Matrix**, **Output grid / Format** and the
Touchstone-worded Fit tooltip (`PlotTracesPanel.axaml` ~101, 237, 325, 349-358; `PlotSessionViewModel.HasMatrix`
ignores `MakeCsv` today). The grid that matters for a CSV is R-pdg4-3's, in manual mode only.

### R-pdg4-7 — Automatic mode gets Log toggles too

Each axis row in automatic mode gains a **Log** toggle feeding `PlotReadingOptions.Scales` (the CLI's `--x-log` /
`--y-log` / `--y2-log`). Today log is only what the labels fit; round 24 made typed values follow that, but a log axis
whose labels did not read still cannot be said to be log in the dialog.

## 4. CLI and MCP — the same capability headless

`recognize plot.png --manual-box x0,y0,x1,y1[@deg] --x-range 100MHz..10GHz[:log] --y-range -40..0
[--y2-range -180..180] --curve "S21=px,py;px,py;…[,axis=right][,is=S21:db]" [--follow name=px,py]
[--sample 101|--sample-step 50MHz] [--method linear|smooth] --csv out.csv [-o out.s2p]`. Picture coordinates carry
`px` as everywhere else. `--manual-box` with no `--x-range`/`--y-range` is a refusal naming them. MCP `PlotOptions`
gains the same fields. `docs/design/cli.md` §26 and the user CLI page. The dialog and the CLI build the same
`ManualPlot`/`ManualCurve` records — no second path.

## 5. Gates — one test per claim

Design layer, `tests/Ui.Tests/PlotRecognition/ManualDigitiseTests.cs`, on a picture rendered by the CLI's own `plot`
verb (known curve, known axes) — at most two pictures:

1. A manual box with typed scales reads a hand-placed point to the axis value within one pixel's worth, on a picture
   **whose tick labels have been painted over** (proves no OCR is used).
2. A box rotated 3° on a rotated render reads the same values as the unrotated one (within one pixel's worth).
3. Log x and log y: a fixpoint halfway between two decades reads the geometric mean.
4. Two scales: a curve on the right axis reads through Y2.
5. `Follow` from one click on a curve returns fixpoints within 1 px of it, and nothing from a click on blank paper.
6. Sampling: Linear and Smooth pass through every fixpoint; Smooth does not overshoot on a step-shaped set; no grid
   point beyond the fixpoints.
7. CSV has the existing columns; an S-parameter-tagged pair on a frequency axis assembles to the expected `.s2p`.

View model, `tests/Ui.Tests/Recognition/ManualDigitiseDialogTests.cs`: step enabling; add / move / delete a fixpoint
(both right-click and Delete) with Undo; Make: CSV hides the Touchstone controls (R-pdg4-6). CLI:
`ManualDigitiseCliTests` — one run end to end, CSV byte for byte against the in-process call.

Render at most two dialog screenshots with the headless harness (PTU-0 §4) — light and dark of the Manual steps.
Run only these test classes plus `PlotRecognition`, not the full suite.

## 6. Docs

`docs/user/src/reference/create-from-image.md` gains **Digitise by hand** under Plots (the six steps, the mouse
gestures, the Touchstone offer); the Tools menu table names the mode. DocGen at the end of the series, not per brief.

## 7. Questions for the owner (answer before starting)

1. **Default mode for Tools ▸ Digitise Plot…**: Manual (proposed — the menu item exists for exactly this job) or
   Automatic with Manual one click away?
2. **JSON export** in the dialog (the designer mentioned it alongside CSV), or CSV only (the CLI already prints JSON)?
3. **Smooth = monotone cubic** (proposed, no overshoot) — or also offer PDG-2's smoothing spline through the fixpoints?
