# Brief PDG-1 — Plot digitiser: a y axis on the right, alone or beside the left one

**Series:** PDG-1 … PDG-3 (plot digitiser improvements) · **Tag:** `R-pdg1-<n>` · **Written:** 2026-10-18
**Follows:** `brief-img-14-sparameter-plots-to-touchstone.md` (the reader), `brief-ptu-4-axis-label-click.md` (label clicks)
**Before:** PDG-2 (curve fit) and PDG-3 (snap to the curve) both read which axis a trace is on, so this one lands first.

---

## 1. The problem

Datasheets often draw two quantities against one shared x axis: dB(S21) against the left axis and its phase against the
right, or gain left and noise figure right. Some draw a single y axis on the **right** only.

The reader knows one y axis, the one on the left. `RectAxes.Calibrate` takes y labels only from words whose box lies
left of `frame.Left` (`RectAxes.cs` ~line 100). The y title is likewise only looked for on the left, and
`PlotFrame.YTicks` comes from the left axis. As a result:

- **Two y axes:** the right axis's labels are ignored, and every trace, including the ones drawn against the right
  axis, is mapped through the left calibration (`PlotReading.Values`, ~line 409: `ya.ValueAt(py)` for every trace). A
  phase curve drawn against −180…180° on the right comes out in the left axis's dB. Nothing reports it.
- **Right axis only:** the y axis has no labels, so it waits for clicks. Worse, `PlotAxisLabels.Find`
  (`PlotSessionViewModel.cs` ~line 1284) treats a label right of the frame as a y label. The click calibration then
  works, but only by accident, and it stops working as soon as both sides carry labels.

## 2. What to build

### R-pdg1-1 — The calibration has a right axis (`RectAxes`, `PlotFrame`)

- `RectCalibration` gains `Y2` (`AxisCalibration?`), `Y2Labels` and `Y2Title`, mirroring `Y`/`YLabels`/`YTitle`.
  `Calibrated` stays "x and at least one y axis are calibrated". A plot with no right-hand labels has `Y2 = null`, and
  for it nothing changes. **Every existing gate must still pass unchanged.**
- Right labels are the mirror image of the left: number words whose box lies right of `frame.Right`, within the same
  distance and span limits, keeping only the innermost column. The right title is a turned word right of those labels.
  It reuses the same `Pair` and consensus `Fit`, so a misread right label is rejected the way a left one is.
- **Right-hand ticks.** A right axis often has tick stubs of its own that do not line up with the left grid. Add
  `PlotFrame.Y2Ticks`: stubs on the right frame line, plus any grid lines crossing it. `Pair` pairs right labels with
  `Y2Ticks`. When the right axis shares the left grid, the two lists coincide, and that is fine.
- **A right-only y axis.** When no left labels read and right labels do, the right axis is the plot's only y axis.
  Store it as `Y` (not `Y2`) and record `YSide = Right` on the calibration, so every downstream consumer keeps reading
  `Y`. Check that `PlotFrames` accepts such a frame: line ~321 requires `MinTicksPerAxis` y ticks from the LEFT axis,
  and a right-only plot with no grid has its ticks on the right. Count right ticks there too. If frame detection needs
  more than that, **stop and report**; frame detection belongs to a later recogniser series.

### R-pdg1-2 — Every trace says which axis it is on (`PlotReading`)

`PlotTraceRead` gains `Axis` (`Left` / `Right`). On a plot with no `Y2` every trace is `Left`, with no evidence needed.
On a plot with both axes, evidence, strongest first:

1. **The user's override** (R-pdg1-4).
2. **The legend entry's own axis mark**: an arrow pointing at one side, or a word such as `right`, `(R)` or `Y2` in the
   entry. Read only what the existing legend reader already gives; do not build a new one.
3. **Colour.** Datasheets very often colour the right axis's labels or title like its trace. A trace whose colour
   matches the right labels' or title's ink (the same clustering tolerance `PlotTraces` uses) is `Right`, and one
   matching the left is `Left`.
4. **Quantity against title.** The trace's assigned quantity (`PlotAssignment.Quantity`) agrees with one title and
   not the other: a phase trace against a `Phase (deg)` right title, a dB trace against a `dB` left one. Use the
   existing title grammar (`PlotQuantity`).
5. **Range.** Map the trace through both calibrations. A trace that lands outside one axis's labelled span but inside
   the other's belongs to the other. This is weak, because both mappings are linear across the same pixels, so use it
   only to break a tie.
6. Otherwise `Left`, **marked ambiguous** with an amber swatch and a finding naming the trace and the control that
   answers it. This follows the same rule as an unassigned trace: never guess silently.

`Values` maps each trace through its own axis. Resolution and `ResolutionUnit` come from that axis. The quantity
evidence (`PlotReading.cs` ~line 358, `"axis title"`) reads the trace's own axis title, so a right-axis trace is no
longer named by the left title.

**A trace's axis is not a Touchstone concern.** `TouchstoneAssembly` reads `Samples`, which are already in the right
units. It should need no change beyond its header (R-pdg1-5). If it seems to need one, look again before changing it.

### R-pdg1-3 — The dialog

- **Frame row** (`PlotFrameRowViewModel`): a `y2 axis (right)` row beside x and y, with the same text spelling
  (`80px=0,560px=-180`) and the same show rule: shown when the right axis waits, or when values were typed or clicked.
  `Calibration` text names the right axis when there is one.
- **Label click** (`PlotAxisLabels.Find`): a label right of the frame is a **y2** label, and a label left of it is
  y. On a right-only plot a right label is y, since that axis is stored as `Y` (R-pdg1-1). `PlotAxisLabel.Axis` and
  `PlotAxisOverride.Axis` are `char` today. Replace them with a small enum `PlotAxis { X, Y, Y2 }` rather than
  inventing a third character.
- **Traces table**: an **Axis** column (`Left` / `Right` combo) shown only on plots with two y axes, with the
  evidence in its tooltip like the entry's. Changing it is undoable through the existing assignment undo (PTU), and
  in the Simple view it applies to every piece of the row, the way entry and quantity do.
- **Overlay**: right-axis labels are coloured green/amber like the left ones. A right-axis trace's tag says `→ R`.

### R-pdg1-4 — CLI and MCP

- `--y2-axis 80px=0,560px=-180` and `--y2-log`, parsed exactly like `--y-axis`/`--y-log` (`src/Cli/PlotFlags.cs`).
- The trace's axis on the existing `--trace` selector grammar: `--trace T3=S21:phase,axis=right` (or whatever the
  current spelling's key=value tail is; extend it, don't add a second flag).
- `ToolCatalog` `PlotOptions`: `y2Axis`, `y2Log`, and `axis` in the trace object. The JSON result's plot carries its
  `y2` calibration like `y`, and each trace carries its `axis`.
- `cli.md` §26.9 documents all three.

### R-pdg1-5 — What the outputs record

- **CSV** (`PlotCsv`): append one column, `y_axis` (`left`/`right`, empty for a chart trace), **at the end** of
  `Columns`. Column order is a format, so don't insert it in the middle.
- **Touchstone header**: each plot's right-axis calibration and residual, and each trace's axis with its evidence.
- **Report** (`PlotReading` ~line 310): the plot line names `y2` alongside x and y, with the flag that answers it.

## 3. Gates — one test per claim, new class `tests/Ui.Tests/PlotRecognition/TwoYAxesTests.cs`

circuitRF's own plotter already draws a right axis: `circuitrf plot … --trace cube=S,i=2,j=1,y=db --trace
cube=S,i=2,j=1,y=<phase spelling>,axis=right --y2 -180:180 --y2label "Phase (deg)" -o x.png`. Draw the pictures in
process the way `PlotPictureSet` does (`JsonRun.Reset(); CliEntry.Run(...)`, `[Collection(SkiaFontsTypefaceCollection.Name)]`).

1. Two axes, dB left and phase right: both traces read within the existing round-trip bound (1.5 × (resolution +
   spread)) against the `.s2p` they were plotted from, and their `Axis` is right.
2. A right-only plot: `Y` is calibrated from the right labels, and `YSide` is `Right`.
3. A plot with no right labels: `Y2` is null and every trace is `Left`. One existing round-trip picture is enough.
4. Colour evidence: right labels in a trace's colour put that trace on the right with no other evidence. Use the
   plotter's per-axis label colour if it has one; otherwise a hand-drawn fixture via `ForeignPlots.cs`.
5. Label click right of the frame gives `PlotAxis.Y2`, and on a right-only plot gives `PlotAxis.Y`.
6. CLI: `--y2-axis` calibrates the right axis, and `axis=right` on `--trace` moves a trace. One `PlotCliVerbTests`
   case, which also keeps that class's `src/Cli` source scan green.
7. CSV: `y_axis` is the last column and is filled.

Run **only** `--filter "FullyQualifiedName~TwoYAxesTests"`, plus the existing `RectAxesTests`, `PlotRoundTripTests`
and `PlotCliVerbTests` classes once at the end, since R-pdg1-1 says nothing they check may move. Never the full suite
or all of `Ui.Tests`. Read failures from the TRX.

## 4. Rules

- Scope is the y axis on the right. **Stop and report** if frame detection, the legend reader or OCR needs
  improving to make a gate pass. That is the recogniser series PTU-0 §5 deferred.
- At most two rendered dialog pictures (before/after), via the headless harness in `brief-ptu-0-overview.md` §4,
  downscaled.
- Docs: `docs/design/image-to-circuit.md` §15.3 (axes) and §15.4/§15.5 (which axis a trace is on),
  `docs/user/src/reference/create-from-image.md` `#plots`, `cli.md` §26.9. No DocGen. Findings go in
  `src/Design/RESOLVED.md` / `src/Ui/RESOLVED.md`, never CLAUDE.md. No commit unless the owner asks.
