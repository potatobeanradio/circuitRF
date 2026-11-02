# Brief: Stem-plot rendering for harmonic-index traces (Data Display, Rect)

Stack/rules: .NET 10, C# 14, Avalonia 12, SkiaSharp. `src/Ui/CircuitRF.Ui.csproj` has
**TreatWarningsAsErrors=true** — capture nullable-property reads into locals; never put raw `<`/`>` in XML doc
comments. Build must end **0W/0E**. Newest-first changelog entry in `src/Ui/DataDisplay/CLAUDE.md` after
landing.

## Goal
When a trace is plotted on a **Rect** plot **with the X-axis being harmonic index** (an HB spectral result),
render it as a **discrete stem ("lollipop"/spectrum) plot** instead of a connected polyline: for each data
point, a vertical stem from the baseline (y=0) up to the point's y-value, capped by a small filled triangle
("arrowhead") at the stem tip. This produces a clean spectrum. Applies to **any** quantity plotted vs harmonic
index (magnitude, phase, dB, real, imag) — it's a function of the X-axis being harmonic index, not of what's on
Y.

## Detection — what "X-axis is harmonic index" means (verified on disk)
HB single-tone results expose their spectral axis as a DataCube axis named **`"harmonic"`** (see
`HbEngine.BuildSingleToneDataSet`: `new Axis("harmonic", harmVals, "Hz")`). Cube-bound traces carry that axis
name through to the renderer: `Trace.CubeXAxisName` (the `xAxisName` passed into `SetCubeData`/`SetFamilyData`).
So the stem condition is:

```
plot.PlotType == PlotType.Rect  &&  trace.IsCubeBound  &&  trace.CubeXAxisName == HarmonicAxisName
```

- Add a single public constant for the magic string so detection and any future producers stay in sync. Put it
  somewhere both the renderer and the model can see — e.g. `public const string HarmonicAxisName = "harmonic";`
  on `Trace` (or a small `DataDisplayAxes` static). Match it **case-sensitively** against `CubeXAxisName`
  (that's how it's emitted). Do NOT also try to match `"mixIndex"` (two-tone) in this brief — two-tone spectra
  are a separate axis and out of scope here; only single-tone `"harmonic"`.
- Network/SNP (non-cube) traces never qualify (their X is frequency) — `IsCubeBound` guards that.
- Family traces (multiple curves vs harmonic) also qualify; render each curve's points as stems (see §Family).

## Where to wire it
`TraceRenderer.Draw(canvas, canvasSize, trace, tf, theme)` currently has no plot-type argument, and the
detection needs BOTH the plot type (Rect) and the trace's axis identity. `PlotRenderer.Draw` already knows
`plot.PlotType` and loops `plot.Traces`. So:

1. Compute the flag at the call site in `PlotRenderer.Draw` and pass it down. Add a parameter to
   `TraceRenderer.Draw`:
   ```csharp
   public static void Draw(SKCanvas canvas, (double W, double H) canvasSize, Trace trace,
                           TransformSet tf, RenderTheme theme, bool stemMode = false)
   ```
   In `PlotRenderer.Draw`, the trace loop becomes:
   ```csharp
   bool plotIsRect = plot.PlotType == PlotType.Rect;
   foreach (var trace in plot.Traces)
       TraceRenderer.Draw(canvas, canvasSize, trace, tf, theme,
           stemMode: plotIsRect && trace.IsHarmonicStem);
   ```
   Add a small computed helper on `Trace` to keep the condition in one place:
   ```csharp
   /// <summary>True when this trace's X-axis is harmonic index (HB spectrum) — drives stem rendering.</summary>
   public bool IsHarmonicStem => IsCubeBound
       && string.Equals(CubeXAxisName, HarmonicAxisName, StringComparison.Ordinal);
   ```
   (The Rect gate stays at the call site, since `Trace` doesn't know the plot type.)

2. The default `stemMode = false` keeps every other caller/plot type byte-identical.

## Rendering the stems (in `TraceRenderer.Draw`)
When `stemMode` is true, **replace the line/curve branch** (the `props.LineEnabled` polyline via `BuildPath`)
with stem rendering. Keep the existing point-marker branch (`props.MarkerEnabled`) working as-is — symbol
markers on stem tips are fine and additive. Specifics:

- **Gate on `props.LineEnabled`** exactly like the polyline does (if the user turned the line off, draw no
  stems — markers still draw). Reuse `props.LineColor`/`LineOpacity`/`LineWidth`/`LineType` for the stems
  (dashed stems if LineType.Dashed — harmless, follow the existing paint setup).
- **Baseline** is world y=0, mapped through the transform: `tf.ToCanvas(x, 0, useSecondary)`. The tip is
  `tf.ToCanvas(x, y, useSecondary)`. (Because y=0 maps through the same axis transform, the stems sit on the
  plotted zero line even when the viewport doesn't include 0 — that's correct; the clip rect already trims
  anything outside the viewport.)
- **Stem** = a 2-point line from baseline to tip:
  ```csharp
  canvas.DrawLine(basePx.X, basePx.Y, tipPx.X, tipPx.Y, stemPaint);
  ```
- **Arrowhead** = a 3-point filled triangle at the tip, pointing **away from the baseline** (i.e. in the
  direction the stem grew): apex at the tip, base two corners back toward the baseline. Size it off the line
  width / canvas like other glyphs (e.g. `float ah = lw * 3f;` then clamp so a tiny stem doesn't get a huge
  head — see Judgment call 1). For a stem going up (tip above base in world → tip.Y < base.Y in canvas, since
  canvas Y grows downward), the apex is at the tip and the two base corners are `ah` *below* the apex
  (toward baseline), offset ±`ah*0.5` in X:
  ```csharp
  float dir = Math.Sign(basePx.Y - tipPx.Y); // +1 when tip is above baseline on canvas (value>0), -1 when below
  if (dir == 0) dir = 1;                       // zero-height stem: default arrow up
  using var head = new SKPath();
  head.MoveTo(tipPx.X,            tipPx.Y);                       // apex at tip
  head.LineTo(tipPx.X - ah*0.5f,  tipPx.Y + dir*ah);             // toward baseline, left
  head.LineTo(tipPx.X + ah*0.5f,  tipPx.Y + dir*ah);             // toward baseline, right
  head.Close();
  canvas.DrawPath(head, headPaint);   // headPaint = Fill, same color as stem
  ```
  The `dir` term makes the head point up for positive values and down for negative values (so a phase trace
  with negative samples reads correctly). **See Judgment call 2** — if the owner wants the literal "always points
  up" from the proposal, drop the `dir` (always use `+ah`), but then negative-value heads overlap their stems.
- Paints: one `SKPaint` Stroke for the stems (mirror the existing line paint: `StrokeCap.Round`,
  `RenderTheme.ToSKColor(props.LineColor, props.LineOpacity)`, `StrokeWidth = lw * props.LineWidth`), one
  `SKPaint` Fill for the heads (same color/opacity, `IsAntialias = true`). Build them once before the loop,
  not per point.
- Iterate `trace.Points` (each is world `(x, y)`); skip nothing extra (Points already excludes non-finite y).

## Family traces (multiple curves vs harmonic)
If `trace.IsFamily`, the existing family branch draws each `FamilyCurve.Points` as a polyline. Under
`stemMode`, render each curve's points as stems too (same color for the whole family, matching the current
"family reads as one trace" rule). Factor the per-point stem+head drawing into a small local helper and call it
from both the single-trace and family paths so there's one implementation. (If you prefer to keep family
out-of-scope for v1, gate `stemMode` to `!trace.IsFamily` and note it — but a harmonic family is a plausible
real case, so doing it is preferred.)

## Autoscale / baseline visibility
`Plot.Autoscale()` already fits Y to the data's min/max via `PathBoundingRect()` (which reads `Points`). Stems
draw down to y=0, which may be **outside** the autoscaled Y range (e.g. an all-positive magnitude spectrum
autoscaled to [min,max] with min>0 would clip the stem bases). Two acceptable options — pick one and note it:
- (A) **Simplest, recommended:** leave autoscale as-is. Stems clip at the viewport bottom; for magnitude/dB
  this looks like bars rising from the axis floor, which is the conventional spectrum look. No code change.
- (B) If the owner wants the full stem always visible, extend the autoscale Y-min to include 0 **only for harmonic-
  stem plots**. That's a change in `Plot.Autoscale()` keyed on the same `IsHarmonicStem` condition. More work,
  changes axis limits behavior — do NOT do this without confirmation.
  
Go with (A) for this brief; mention (B) in the PR as a follow-up toggle if the floor-clipping looks off.

## What NOT to change
- Don't touch Smith/Polar/Table rendering, the marker symbol/info-box code, or `BuildPath`/`BuildCubePath`
  (the world-space Points are identical; only their on-canvas representation changes).
- Don't change `PathBoundingRect` (keep autoscale fed by the same points).
- Don't add a user toggle/property for stem-vs-line in this brief (it's automatic for harmonic-index Rect). If
  the owner later wants an opt-out, that's a `TraceProperties` flag — separate brief.

## Tests
Renderer output isn't unit-tested in this codebase (Skia canvas), so:
- Add a tiny model-level test that `Trace.IsHarmonicStem` is true for a cube trace whose `CubeXAxisName ==
  "harmonic"` on construction via `SetCubeData(..., xAxisName: "harmonic", ...)`, and false for `xAxisName:
  "freq"` and for a network/SNP trace. (Construct a Trace, call `SetCubeData` with a 2–3 point real array,
  assert the flag.)
- Manual verification: run a single-tone HB, plot `mag(V[:, "..."])` (or any cube) vs the harmonic axis on a
  Rect plot → stems with up-arrows; switch Y to phase → stems with up/down arrows tracking sign; confirm a
  normal S-param-vs-frequency Rect trace is unchanged (still a connected line).
- Report total test count.

## Judgment calls for the owner (call out in the PR)
1. **Arrowhead size clamping.** A fixed `ah = lw*3` head can look oversized on a very short stem. Recommend
   clamping the head height to at most ~⅓ of the stem's canvas length: `ah = Math.Min(lw*3f, stemLenPx*0.33f)`
   (and a small floor so it never vanishes). Confirm the proportions look right; tune the `3f`/`0.33f`.
2. **Arrow direction for negative values (phase).** The proposal says "arrow pointing up." Taken literally that
   only suits non-negative Y (magnitude). For phase (and real/imag), values go negative; this brief points the
   head **away from the baseline** (up for +, down for −) so negative samples read correctly. If you truly want
   always-up, it's a one-line change (drop `dir`), but negative heads will then point back along their own stem.
   Recommend the sign-aware version.
