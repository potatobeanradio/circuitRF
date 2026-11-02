# Brief LOGX — a logarithmic frequency axis, so a sweep the engine writes in decades can be read in decades

**Tags:** `R-logx-n` · **Area:** `src/Render/DataDisplay`, `src/Ui/DataDisplay`

**Read first**, end to end, before planning anything: `src/Render/DataDisplay/Renderers/PlotRenderer.cs`
— `TransformSet` at the top (the whole of the present axis model is four lines of it) and
`ViewportClipRect`; `src/Render/DataDisplay/Models/Axes.cs`, especially the `Window` setter and
`Ticks`; `AxesRenderer.DrawRectGrid` and `DrawTitleAndAxisLabels`; `PlotControl.cs`'s
`TryAddMarkerNearPoint`, `HitTestMarker` and `ZoomedWindow`, because all three invert the transform
and none of them knows it; `src/Ui/DataDisplay/ViewModels/AxesLimitsViewModel.cs` and
`src/Ui/Views/DataDisplay/AxesLimitsView.axaml`, which are the entry point and are live-applied
per keystroke; and `src/Render/DataDisplay/PlotConfigLoader.cs` plus `DataDisplayViewModel`'s `.cdd`
writer, which are the two hand-maintained halves of the format.

---

## The problem

**circuitRF's EM kernel writes log-spaced sweeps by default and its Data Display cannot draw one.**
A `.cem` carries `Frequency.Kind` and an extraction that spans a decade or more is normally written
`Log` — it is the only sane way to sample a band whose interesting behaviour is spread over
frequency ratios. The rect plot then maps it with

```csharp
public SKPoint PrimaryToCanvas(float wx, float wy) =>
    new SKPoint((float)(wx * Primary.XScale + Primary.XOffset), ...);
```

— an affine map, and the only one there is. A grep across `src/Render/DataDisplay` and
`src/Ui/DataDisplay` finds no logarithmic axis concept of any kind; `PolarRadialMode` is the single
log/dB axis in the whole display and it is a polar radial.

**What that costs, measured on a real extraction.** An 11-point log sweep from 1 MHz to 2 GHz,
autoscaled to a window of −1e-6 to 2.19990 GHz:

| point | % of axis span | px on a ~700 px plot area |
|---|---|---|
| 1 MHz | 0.046 % | 0.3 |
| 2.14 MHz | 0.097 % | 0.7 |
| 4.57 MHz | 0.208 % | 1.5 |
| 9.78 MHz | 0.445 % | 3.1 |
| 20.9 MHz | 0.951 % | 6.7 |

**Five of the eleven points — the whole bottom two decades — are inside the first seven pixels.**
The line is drawn correctly and is unreadable; the table view beside it shows every row, which is how
a reader discovers the points exist at all. The workaround is to turn Autoscale X off and window the
bottom decade by hand, which works and means keeping one plot per decade on the tab and re-typing the
window to move between them.

**This is not a niche request.** Any extraction meant to be usable from DC-ish to a few GHz is a
decade-spanning sweep; so is any noise, stability or matching study; so is the band edge of every EM
run whose bottom the engine has an opinion about.

## What exists, and why the change is small in the middle and fiddly at the edges

The good news is structural: **every consumer of the axis goes through one transform.**
`PlotRenderer.BuildTransforms` has 22 call sites across 7 files, and the grid, the ticks, the traces,
the marker glyphs, the marker hit-test, the marker drag, the VSWR locus, the contour renderer and the
`render` CLI verb's composer all obtain their mapping from it. Make the transform log-aware and they
follow. `Axes.Ticks` is likewise the single source of `MajorX`/`MinorX`.

The bad news is that three places do arithmetic the transform does not own, and each is a trap:

1. **`Axes.Window`'s setter rewrites a left edge of exactly 0 to −1e-6** (Axes.cs:127 and again at
   143 for the secondary window), *"so that the zero grid line renders correctly"*. On a linear axis
   that is a cosmetic nudge. **On a log axis it is an undefined point, and it arrives by default**:
   the autoscaled window in the file measured above literally begins at −1e-6 GHz. Either the setter
   learns the mode or the log path must not go through it — and whichever is chosen, the linear
   path's behaviour must not move a pixel.
2. **`PlotControl.ZoomedWindow` is linear arithmetic** — `wx − (wx − window.Left)·factor`. A log
   axis zooms and pans in the ratio, not the difference. Wheel-zoom, drag-pan and box-zoom all route
   through it.
3. **`PrimaryFromCanvas` / `SecondaryFromCanvas` invert the transform**, and `TryAddMarkerNearPoint`
   and `HitTestMarker` both depend on the inverse being exact. A marker that cannot be placed where
   it is drawn is worse than no log axis.

## Milestones

### M1 — the axis mode, the transform, and nothing else

One `AxisScale { Linear, Log }` on `Axes` for the X axis, defaulting to `Linear`. `TransformSet`
gains the forward and inverse maps. **`Axes.Window`'s zero-nudge is settled here**, with the linear
path's arithmetic bit-identical to today.

**Gate before writing any UI: every existing `.cdd` in the repo renders BIT-IDENTICALLY.** The
`render` verb makes this cheap and exact — it is the same renderer, so a byte comparison of the SVG
before and after is the whole test, and `tests/Ui.Tests/Render/RenderCliVerbTests.cs` already
establishes that pattern.

### M2 — ticks, labels and autoscale

- **Major ticks are decades**; minor ticks are the 2…9 within each decade and are not evenly spaced.
  `Axes.Ticks` returns both today and the shape does not change.
- **The SI-prefix rule has to be re-decided.** `AxesRenderer` picks *one* prefix per axis from the
  window, deliberately (*"never per tick: picking per value…"*). A log axis spanning MHz to GHz has
  no single right prefix. Either label each decade in its own prefix — which breaks that rule for
  this mode only, explicitly and in a comment — or keep one prefix and accept `0.001` at the left
  end. **Decide it, write down why, and do not leave both spellings reachable.**
- **Autoscale snaps to enclosing decades**, not to the 1/2/5 lattice it uses now.
- **Zero and negative X.** A DC point is a legal circuitRF frequency and is in the `.sNp` by
  construction — `PlanarSolve`'s LF1 splices 0 Hz back on *first* because that is where a Touchstone
  wants it. A log axis cannot draw it. **Drop it with a visible note, never silently**; a point that
  vanishes without a sentence is the defect this whole brief exists to stop.

### M3 — interaction

`ZoomedWindow`, pan, box-zoom, `TryAddMarkerNearPoint`'s 20 px snap and `HitTestMarker`'s radius, all
on a log axis. **The gate is behavioural, not visual:** place a marker by double-click at each of the
11 points of a decade-spanning sweep and assert the marker's `Freq` equals the data frequency exactly,
then drag it one point left and right and assert it lands on the neighbour. Marker placement already
takes its frequency from `trace.Data.Frequencies[fi]` rather than from the cursor, which is why this
is an assertion about the index and not about a tolerance.

### M4 — persistence and the UI entry point

**The entry point is the "Axes Limits" flyout, and it is the only one.** Right-click on a plot →
`MenuItem { Header = "Axes Limits" }` (`PlotControl.cs:424`) → `OnMenuActionTwo` →
`ShowAxesLimitsFlyout` (`PlotControl.cs:665`), which builds an `AxesLimitsViewModel` over the plot and
shows `src/Ui/Views/DataDisplay/AxesLimitsView.axaml` in a `Flyout`. It is a **flyout, not a modal
dialog**, and that matters for two of the three items below.

- **The control goes in the X Axis block of `AxesLimitsView.axaml`** — lines 20-53, which already
  carry the "X Axis" header, the `XUnitLabel`, the `Autoscale` checkbox bound to `XAutoscale`, and
  the Min/Max boxes. A `Linear | Log` selector belongs beside that Autoscale checkbox, bound to a new
  `XScale` on `AxesLimitsViewModel` next to `XAutoscale`. The Y and Y2 blocks below it are untouched
  and must stay untouched.
- **`TryApplyX` (`AxesLimitsViewModel.cs:146`) needs a positive-minimum guard, and the reason is not
  the obvious one.** `OnXMinTextChanged` and `OnXMaxTextChanged` call it on **every keystroke** —
  there is no OK button and no commit-on-blur. So a user typing `0.001` into Min passes through `0`
  on the way, and `0` parses. On a linear axis that is a harmless transient; on a log axis it is the
  undefined point, arriving a few times per second while someone types. Today the only guard is
  `|xMax − xMin| < 1e-15`. **Reject rather than coerce:** silently rewriting the typed value and
  pushing it back into `XMinText` is a coercing control writing over the user's own edit, which is a
  defect this repo has already paid for once (`match-undo-slider-writeback`).
- **`TryApplyX` writes FOUR places, not one**, and all four go through the `Axes.Window` setter that
  M1 settles: `Axes.Window`, `Axes.WindowState`, and — when `ShowSecondary` is on for a Rect plot —
  `WindowSecondary` and `WindowSecondaryState`, because the secondary axis shares the X range. The
  secondary window has its own copy of the zero-nudge at `Axes.cs:143`.
- **`XUnitLabel` is where the M2 SI-prefix decision becomes visible to a user**, because Min and Max
  are typed in the axis's displayed unit. Whatever M2 decides, this label has to still be true.
- **One new `.cdd` field, defaulting to `Linear`**, written and read in both hand-maintained halves.
  `AxisSliceConfig`'s own header records what happens when those two lists drift; this field must be
  added to both in the same commit.
- `--window` on the `render` verb is unaffected — it names world coordinates, which do not change.

## Gates

- **Every existing `.cdd` renders byte-identically** through `render`, at M1 and again at the end.
  This is the primary gate and it is nearly free.
- A decade-spanning sweep on a log axis shows all 11 points distinguishable, and the same document
  switched back to Linear reproduces today's picture exactly.
- Marker placement, hit-test and drag asserted on a log axis by data index, per M3.
- **Typing a minimum into the Axes Limits flyout character by character never reaches the renderer
  with a non-positive left edge**, asserted by driving `AxesLimitsViewModel.XMinText` through the
  prefixes of a typed value (`""`, `"0"`, `"0."`, `"0.0"`, `"0.001"`) rather than by setting the
  final string — which is the sequence `OnXMinTextChanged` actually sees and the only one that
  reproduces the transient.
- **`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs` is a `BuildTransforms` caller and must not
  change behaviour.** Its panels are not frequency plots.
- No new `Category=Benchmark` test.

## Must NOT

- **Do not make this a per-trace property.** It is an axis, and two traces on one axis cannot disagree
  about its scale.
- **Do not add a log Y axis in this brief.** The Y axis is already logarithmic where it matters
  (`YAxis: Db`), the traps are different, and scope here is the X axis that carries frequency.
- **Do not touch the Table plot type.** It is already correct and is what a reader falls back to today.
- Do not change the linear path's arithmetic anywhere, for any reason, including "tidying" the
  zero-nudge.

## While you are here — one adjacent defect, independent of this brief

`Trace.cs:2778` resolves a marker to its data point with

```csharp
int fi = Array.FindIndex(Data.Frequencies, f => f >= m.Freq - 1e-6);
```

**An ABSOLUTE tolerance of 1e-6 Hz** — a relative 5e-13 at 2 MHz and roughly two ULPs at 2 GHz.
Measured: a marker asked for 2138469.2 Hz against a stored 2138469.1999823763 Hz — the same number
as written by the run's own `.s2p` to ten significant figures — **missed its point and silently
snapped to the next one**, 4.573 MHz, landing on top of another marker. Nothing reports it. In-app
placement is safe today because `AddMarkerAtFreqIndex` copies `Data.Frequencies[fi]` verbatim, so
this bites only a frequency that has been round-tripped, typed, or unit-converted — which is exactly
what a marker editor and a re-imported Touchstone do. A relative tolerance fixes it; if the fix lands
here, say so in the commit rather than folding it into the axis work.

## Cost

M1 is small and is most of the risk, because the bit-identical gate either holds immediately or says
the transform is not the single point of change after all. M2 is the fiddly one and the SI-prefix
decision is the part worth thinking about before typing. M3 is mechanical once the inverse is right.
**Stop and report if M1's byte-identical gate fails** — that means some consumer is doing its own
axis arithmetic and the survey above missed it, and the shape of the whole brief changes.

## On completion

- `src/Render/DataDisplay/RESOLVED.md` gains a `§LOGX` section: the zero/DC decision and why, the
  SI-prefix decision and why, every `BuildTransforms` consumer that turned out to need its own change,
  and every trap found.
- `docs/design/` gains the axis-mode description wherever the Data Display's plot model is written
  down. It is a design note and is edited normally.
- **Do not write to any `CLAUDE.md`.** Report stale lines to the owner by file and line instead.
