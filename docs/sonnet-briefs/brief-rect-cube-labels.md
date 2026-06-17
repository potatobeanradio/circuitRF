# Sonnet Brief — Rect plots: cube-aware X axis (label + data) and cube-aware Y labels

Rect plots must stop hard-coding "Freq" and instead reflect the cube's X-axis slice, plot the X data in
the right units, label each trace with the same shorthand the Table uses (net names, `<invalid>`), and
softly flag traces that can't render. Five coordinated changes across three files:

- `src/Ui/DataDisplay/Models/Trace.cs` — X-data scaling + a Rect-validity flag.
- `src/Ui/DataDisplay/Models/Plot.cs` — `XLabel` reflects the cube X-axis.
- `src/Ui/DataDisplay/Renderers/AxesRenderer.cs` — per-trace Rect Y labels = cube shorthand + soft
  `<invalid>` / `dimension mismatch` suffixes.

Background facts already true on disk (no change needed, but rely on them):
- The HB **harmonic** axis is `Axis("harmonic", values = {0, f0, 2f0, …}, unit "Hz")`; the two-tone
  **mixIndex** axis is `Axis("mixIndex", values = {k1·f1+k2·f2}, unit "Hz", labels "(k1,k2)")`. So a
  cube X-axis that is frequency already carries true Hz values (non-uniform for 2-tone) and `Unit=="Hz"`.
- A **Pin** sweep axis has a non-freq unit (e.g. "W"/"dBm") and `Name=="Pin"`.
- `Trace` already stores the resolved X axis via `SetCubeData(..., xAxisName, xUnit, ...)` →
  `CubeXAxisName` / `CubeXUnit`.
- The Table already does exactly the freq-scaling + header pattern we want (see
  `TableRenderer.BuildColumns` / `FormatColumnCell`): freq unit → header `"{axisName} ({plot.FreqUnits})"`
  and cell `xVal * plot.FreqUnits.Scale()`; else `"{axisName} ({unit})"` and raw `xVal`.
- The Table trace-value header is `trace.CubeShorthand` (+ source prefix when `showFilePrefix`).
  `CubeShorthand` returns `Expression ?? BuildPickerExpression()` and appends `" <invalid>"` when
  `InvalidSpecText` is set; `BuildPickerExpression` already emits quoted **net names** for pinned
  label-bearing axes.

Build 0W/0E after the batch.

---

## Change 1 (Trace.cs) — scale Rect cube X data to the plot freq unit

In `Trace.BuildCubePath`, the Rect branch plots `x = _cubeXValues[i]` raw. When the cube X-axis is a
frequency axis (unit Hz/kHz/MHz/GHz) the X must be scaled by `freqUnit.Scale()` so the spectrum reads in
the plot's display unit, with correct (possibly non-uniform) tone spacing. Non-freq X axes (Pin sweep)
stay raw.

Add a tiny helper and apply it in the Rect branch:
```csharp
private static bool IsFreqUnit(string? unit) => unit is "Hz" or "kHz" or "MHz" or "GHz";
```
In `BuildCubePath`, Rectangular section, compute the x scale once:
```csharp
// Rectangular
double xScale = IsFreqUnit(_cubeXUnit) ? freqUnit.Scale() : 1.0;
for (int i = 0; i < n; i++)
{
    double x = _cubeXValues[i] * xScale;   // ← was: _cubeXValues[i]
    double y;
    …
}
```
(Leave the Smith/Polar branch unchanged — it plots complex Re/Im, no X scaling.)

## Change 2 (Trace.cs) — Rect can only plot scalars: flag complex results as invalid

Rect needs a scalar Y. A cube trace whose transform leaves the value **complex** (`Transform == None` or
`Conj` on a Complex cube) cannot be drawn on Rect. Today the Rect branch silently plots `z.Magnitude`
for `None`/`Conj` — replace that with a soft error: render no points and set a flag the renderer turns
into a subtle `<invalid>` on the Y label.

Add a transient flag (computed each BuildPath; not serialized):
```csharp
/// <summary>True when the last BuildPath produced a Rect plot but the cube value is complex with no
/// scalar transform (None/Conj) — Rect can only plot scalars. Drives a soft "<invalid>" Y-axis label.</summary>
public bool RectValueInvalid { get; private set; }
```
In `BuildCubePath`, set it at the top of the Rect section and clear it otherwise:
```csharp
private void BuildCubePath(PlotType plotType, FreqUnit freqUnit)
{
    Points.Clear();
    RectValueInvalid = false;                 // ← reset each build
    if (_cubeXValues is null) return;
    if (_cubeComplexValues is null && _cubeRealValues is null) return;

    int  n         = _cubeXValues.Length;
    bool isComplex = _cubeComplexValues is not null;

    if (!plotType.IsRect())
    {
        // Smith / Polar … (unchanged)
        …
        return;
    }

    // Rectangular — Rect needs a scalar. A complex cube with a non-scalar transform is invalid.
    if (isComplex && (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
    {
        RectValueInvalid = true;   // leave Points empty; renderer marks the Y label "<invalid>"
        return;
    }

    double xScale = IsFreqUnit(_cubeXUnit) ? freqUnit.Scale() : 1.0;
    for (int i = 0; i < n; i++) { … }   // existing scalar transforms (dB20/Mag/Phase/Real/Imag)
}
```
> Note: this removes the old silent `None/Conj → magnitude` behavior on Rect. That is intended — the
> user must pick mag/dB/re/im/phase. (Smith/Polar still use `None`/`Conj` to plot the complex locus.)

## Change 3 (Trace.cs) — a Rect Y-label builder (cube shorthand, net names, soft flags)

Add a method the renderer calls for cube-bound Rect Y labels. It reuses `CubeShorthand` (net names +
`<invalid>` for parse errors) and appends the soft suffixes. Keep it prefix-aware to match the Table.
```csharp
/// <summary>
/// Y-axis label for this trace on a Rect plot: the cube shorthand (net-name form, e.g.
/// mag(V[:, "Vout2", 2])), optionally source-prefixed, with soft suffixes:
///   • " &lt;invalid&gt;" when the value can't render (parse error OR complex-on-Rect),
///   • " dimension mismatch" when this trace's cube X-axis differs from the plot's X-axis.
/// Network (SNP) traces fall back to the supplied minimal label.
/// </summary>
public string RectYLabel(string networkFallback, bool showFilePrefix, bool dimensionMismatch)
{
    string baseLabel;
    if (IsCubeBound)
    {
        baseLabel = CubeShorthand;                              // net names + "<invalid>" if InvalidSpecText
        if (showFilePrefix && SourcePath != null)
            baseLabel = System.IO.Path.GetFileNameWithoutExtension(SourcePath) + ".." + baseLabel;
        // Complex-on-Rect soft error (separate from parse-error <invalid> already in CubeShorthand).
        if (RectValueInvalid && !baseLabel.Contains("<invalid>"))
            baseLabel += " <invalid>";
    }
    else
    {
        baseLabel = networkFallback;
    }
    if (dimensionMismatch) baseLabel += " dimension mismatch";
    return baseLabel;
}
```

## Change 4 (Plot.cs) — `XLabel` reflects the cube X-axis

`Plot.XLabel` currently returns `freq (…)` always. When the **first** trace is cube-bound, derive the
label from its cube X-axis (mirrors the Table header). First trace defines the plot X-axis (the simple
rule for the mismatch case). Keep the existing network/SNP behavior when trace[0] is network-bound.

Replace the `XLabel` getter body's post-`CustomXLabelOn` section:
```csharp
public string XLabel
{
    get
    {
        if (CustomXLabelOn) return CustomXLabel;

        // Cube-bound first trace: label from the cube's X axis (the plot's X axis).
        if (Traces.Count > 0 && Traces[0].IsCubeBound)
        {
            string axisName = Traces[0].CubeXAxisName;
            string? unit    = Traces[0].CubeXUnit;
            if (string.IsNullOrEmpty(axisName)) axisName = "x";
            bool isFreq = unit is "Hz" or "kHz" or "MHz" or "GHz";
            if (isFreq)
                return $"freq ({FreqUnits.Description()})";
            return string.IsNullOrEmpty(unit) ? axisName : $"{axisName} ({unit})";
        }

        // Network/SNP behavior (unchanged).
        string u = FreqUnits.Description();
        if (Traces.Count == 0 || !SupportsComplex)
            return $"freq ({u})";
        string min = (FreqUnits.Scale() * Traces[0].MinFreq).ToString($"G{Axes.NumDigitsXAxis}");
        string max = (FreqUnits.Scale() * Traces[0].MaxFreq).ToString($"G{Axes.NumDigitsXAxis}");
        return $"freq ({min} to {max} {u})";
    }
}
```
> The harmonic/mixIndex X axis is freq-unit → renders `freq (GHz)` etc.; a Pin sweep renders
> `Pin (W)`. (Naming "freq" for the harmonic spectrum axis matches what the user asked for.)

## Change 5 (AxesRenderer.cs) — per-trace Rect Y labels use the cube shorthand + mismatch flag

In `DrawTitleAndAxisLabels`, the per-trace Y labels currently come from
`TraceLabeler.ComputeMinimalLabels` / `ShortDescription`. For cube-bound traces, use `RectYLabel`
instead, and compute the dimension-mismatch flag against the **first trace's** cube X-axis name.

`showFilePrefix` is already passed into `DrawTitleAndAxisLabels`. Right before the
`leftTraces`/`rightTraces` label loops (where `labelLookup` is built), add the reference axis + a local
label resolver:
```csharp
// Reference X-axis = first trace's cube X-axis (the plot's X axis). Cube traces whose X-axis
// name differs are softly flagged "dimension mismatch" but still attempt to render.
string? refCubeXAxis = plot.Traces.Count > 0 && plot.Traces[0].IsCubeBound
    ? plot.Traces[0].CubeXAxisName
    : null;

string LabelFor(Trace t)
{
    string networkFallback = labelLookup.GetValueOrDefault(t, t.ShortDescription);
    bool mismatch = t.IsCubeBound
                    && refCubeXAxis != null
                    && !string.Equals(t.CubeXAxisName, refCubeXAxis, StringComparison.Ordinal);
    return t.RectYLabel(networkFallback, showFilePrefix, mismatch);
}
```
Then in the left-axis loop replace the `DrawAt(labelLookup.GetValueOrDefault(...), …)` call with:
```csharp
DrawAt(LabelFor(leftTraces[i]),
       RenderTheme.ToSKColor(leftTraces[i].Properties.LineColor),
       cx, false);
```
and likewise the right-axis loop:
```csharp
DrawAt(LabelFor(rightTraces[i]),
       RenderTheme.ToSKColor(rightTraces[i].Properties.LineColor),
       cx, true);
```
Leave the custom-Y-label (`plot.YLabel`) branch and the network `TraceLabeler` computation intact —
`LabelFor` falls back to the minimal network label for SNP traces.

> The existing `DrawAt` already middle-truncates with "…" to fit the strip, so a long
> `mag(HB1.V[:, "Vout2", 2]) dimension mismatch` won't overflow — it shrinks gracefully.

---

## Why this satisfies each requirement
- **X label not hard-coded:** Change 4 — freq axis → `freq (GHz)`, Pin sweep → `Pin (W)`.
- **Y label same as Table, net names, `<invalid>`:** Change 3/5 reuse `CubeShorthand` (net-name form,
  parse-error `<invalid>`) with the source prefix exactly like the Table header.
- **Rect scalars only; complex → soft error + `<invalid>`:** Change 2 (+ the `<invalid>` suffix in 3).
- **Harmonic-index X renders as freq with true (non-uniform) spacing:** the harmonic/mixIndex axis
  already carries Hz values; Change 1 scales them to the display unit and the renderer plots each point
  at its true x, so 2-tone tone spacing is correct automatically. Change 4 labels it `freq (unit)`.
- **Mixed X-axis types:** simple rule — first trace owns the plot X axis; others render anyway and get a
  `dimension mismatch` note on their Y label (Change 5). No new X-axis negotiation logic.

## STOP-and-verify before building
- Confirm `Trace.CubeXAxisName` / `CubeXUnit` are populated for cube traces (they are, via `SetCubeData`
  ← `TrySetCubeData`, which passes `sliced.Axes[0].Name/Unit`). For the **harmonic** X-axis the name is
  `"harmonic"` and unit `"Hz"`; for **mixIndex** name `"mixIndex"` unit `"Hz"`. Both are freq → labelled
  `freq (...)`. Good. (If you'd rather the label say `freq` only for these two names, the unit check
  already covers it.)
- Confirm `DrawTitleAndAxisLabels` receives `showFilePrefix` (it does) and that
  `RenderTheme.ToSKColor(trace.Properties.LineColor)` is the existing per-trace color accessor used in
  that method (it is).
- Confirm no other caller depended on the old Rect `None/Conj → magnitude` cube behavior. Search usages
  of `BuildCubePath`; the only producers are `BuildPath`/`SetCubeData`. The Table path uses
  `FormatCubeCell` (unaffected). Smith/Polar branch unchanged.

## Gate / manual checks (build 0W/0E)
1. Rect plot, cube trace `mag(V[:, "Vout2", 2])` over a **Pin** sweep: X label `Pin (W)` (or the sweep
   unit); Y-axis trace label reads `mag(HB1.V[:, "Vout2", 2])` (net name, source prefix when multiple
   sources). Data plots vs Pin.
2. Rect plot, cube trace with **harmonic** as X (spectrum): X label `freq (GHz)`; X data are the harmonic
   frequencies scaled to GHz; markers/points sit at the true frequencies. For a 2-tone sim the mix
   products are at non-uniform x positions and spacing looks correct.
3. Put a **complex** cube value on Rect (Transform = None): the trace draws nothing and its Y label gets
   a subtle ` <invalid>`; switching the transform to dB/mag/etc. clears it and the trace draws.
4. Two cube traces with **different** X-axis variables (one harmonic-freq, one Pin): first trace's axis
   labels the X; the second still renders and its Y label shows ` dimension mismatch`.
5. Network (SNP) Rect traces unchanged — freq X label and minimal Y labels as before.
6. Table plot unchanged (regression check): headers and freq-scaled cells identical to before.

If practical, add a unit test: `BuildCubePath` on Rect sets `RectValueInvalid=true` for a complex
cube+None and `false` after setting `Transform=Mag`; and `Plot.XLabel` returns `freq (GHz)` for a
harmonic-X cube trace and `Pin (W)` for a Pin-X cube trace.
