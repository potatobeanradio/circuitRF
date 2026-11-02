# Sonnet Brief — 7.3b family role (REVISED, self-contained): one Trace → N curves, with auto-recognition

> Supersedes the earlier `brief-7.3b-family-role.md` draft, which was never implemented and did not cover
> the expression/spec path. Implement THIS document. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

## Goal
A single `Trace` whose one **family** axis is iterated renders **N curves** (one trace row, one style, one
delete). Canonical case: FET curve tracer — `I:Ids` from a nested Vgs×Vds DC sweep → one curve of Id-vs-Vds
per Vgs. Two entry points must both produce a family:
- **Picker:** a third axis-role **Family** (X / Pinned / Family) in the axis-role editor.
- **Typed spec / auto-recognition:** `I:Ids[:, :]` (two kept axes) **and** bare `I:Ids` (no brackets, a 2-D
  cube) auto-render as a family. Today both throw `<invalid>` because `CubeTraceSpecParser` requires exactly
  one `:` axis and requires brackets.

Hard cap **101 curves** (one constant). Markers on families are out of scope (disable; follow-on).

## Convention for auto-assigning X vs Family (2 kept axes)
When a single-cube spec keeps exactly two axes, assign: **last kept axis → X (KeepAsX), earlier kept axis →
Family (FamilyIterate).** For a `[Vgs, Vds]` cube, `I:Ids[:, :]` → Vds = X, Vgs = Family → Id-vs-Vds, one
curve per Vgs (the curve-tracer the user wants). The picker lets the user swap roles explicitly. >2 kept axes
→ a parse error (pin or family-reduce the extras; this brief supports ≤1 family + 1 X).

---

## 1. Model — `src/Ui/DataDisplay/Models/Trace.cs`

(a) Extend the role enum (the `// Phase 7.2c-a` block):
```csharp
public enum AxisRole { PinToIndex, KeepAsX, FamilyIterate }
```

(b) Add the cap constant + family types near the top of `class Trace`:
```csharp
    // ── Performance guardrail (Phase 7.3) ────────────────────────────────────
    // Max curves a single family trace renders. Single source of truth — clamp +
    // one Message past it. Raise/lower here for perf testing.
    public const int MaxFamilyCurves = 101;

    /// <summary>One curve of a family trace: its iterated-axis value (for the legend) + its points.</summary>
    public sealed class FamilyCurve
    {
        public double  AxisValue { get; init; }
        public string? AxisLabel { get; init; }
        public List<Vector2> Points { get; } = new();
    }

    /// <summary>N curves when IsFamily; empty otherwise. Derived (never serialized) — rebuilt on load.</summary>
    public List<FamilyCurve> FamilyCurves { get; } = new();

    /// <summary>Name of the iterated (family) axis — the legend title.</summary>
    public string? FamilyAxisName { get; set; }

    /// <summary>True when the slice marks an axis FamilyIterate.</summary>
    public bool IsFamily => Slice is not null && Array.Exists(Slice, s => s.Role == AxisRole.FamilyIterate);
```

(c) **Factor the per-sample value map out of `BuildCubePath`** so the single-curve and family paths use
identical transform logic. Add these two private helpers (lift the exact switch bodies already in
`BuildCubePath`):
```csharp
    // Rect scalar Y from one sample (returns null → skip point; matches BuildCubePath exactly).
    private double? RectY(Complex? cz, double? rv)
    {
        if (cz is Complex z)
        {
            double y = Transform switch
            {
                CubeTransform.dB20  => 20.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                CubeTransform.dB10 or CubeTransform.dB => 10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                CubeTransform.Mag   => z.Magnitude,
                CubeTransform.Phase => z.Phase * 180.0 / Math.PI,
                CubeTransform.Real  => z.Real,
                CubeTransform.Imag  => z.Imaginary,
                _                   => z.Magnitude,
            };
            return double.IsFinite(y) ? y : (double?)null;
        }
        double v = rv!.Value;
        double yr = Transform switch
        {
            CubeTransform.dB20 => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
            CubeTransform.dB10 or CubeTransform.dB => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
            CubeTransform.Mag  => Math.Abs(v),
            _                  => v,
        };
        return double.IsFinite(yr) ? yr : (double?)null;
    }
```
Refactor `BuildCubePath`'s Rect loop to call `RectY(...)` (behavior identical; this is the shared mapper). The
Smith/Polar complex→(Real,Imag) mapping (with Conj) is trivial — inline it in both paths or a tiny helper; your
call. Keep the existing `RectValueInvalid` guard (complex + None/Conj on Rect) for both single and family.

(d) Family injection — sibling to `SetCubeData`:
```csharp
    /// <summary>Injects N pre-sliced family curves (each a rank-1 X/value pair) and builds their Points.
    /// xValues are shared across curves (same X axis). Each curve carries its own complex/real values.</summary>
    public void SetFamilyData(double[] xValues, string xAxisName, string? xUnit, string familyAxisName,
        IReadOnlyList<(double axisValue, string? axisLabel, Complex[]? cz, double[]? rv)> curves,
        PlotType plotType, FreqUnit freqUnit)
    {
        _cubeXValues = xValues; _cubeXAxisName = xAxisName; _cubeXUnit = xUnit;
        _cubeComplexValues = null; _cubeRealValues = null;   // family uses FamilyCurves, not the single arrays
        FamilyAxisName = familyAxisName;
        FamilyCurves.Clear();
        Points.Clear();
        RectValueInvalid = false;

        bool isRect = plotType.IsRect();
        double xScale = IsFreqUnit(xUnit) ? freqUnit.Scale() : 1.0;

        foreach (var (axisValue, axisLabel, cz, rv) in curves)
        {
            var fc = new FamilyCurve { AxisValue = axisValue, AxisLabel = axisLabel };
            int n = xValues.Length;
            bool isComplex = cz is not null;
            if (isRect && isComplex && (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
            { RectValueInvalid = true; FamilyCurves.Add(fc); continue; }   // soft-invalid, like single

            for (int i = 0; i < n; i++)
            {
                if (isRect)
                {
                    double? y = RectY(isComplex ? cz![i] : (Complex?)null, isComplex ? (double?)null : rv![i]);
                    if (y is double yy) fc.Points.Add(new Vector2((float)(xValues[i] * xScale), (float)yy));
                }
                else if (isComplex)   // Smith/Polar
                {
                    var z = Transform == CubeTransform.Conj ? Complex.Conjugate(cz![i]) : cz![i];
                    fc.Points.Add(new Vector2((float)z.Real, (float)z.Imaginary));
                }
            }
            FamilyCurves.Add(fc);
        }
    }
```

(e) `PathBoundingRect()` — include all family curves so autoscale frames the fan:
```csharp
    public Rect PathBoundingRect()
    {
        if (IsFamily)
        {
            bool any = false; float minX=0,minY=0,maxX=0,maxY=0;
            foreach (var c in FamilyCurves)
                foreach (var p in c.Points)
                {
                    if (!any) { minX=maxX=p.X; minY=maxY=p.Y; any=true; }
                    else { minX=Math.Min(minX,p.X); maxX=Math.Max(maxX,p.X); minY=Math.Min(minY,p.Y); maxY=Math.Max(maxY,p.Y); }
                }
            return any ? new Rect(minX, minY, maxX-minX, maxY-minY) : default;
        }
        if (Points.Count == 0) return default;
        float aX=Points.Min(p=>p.X), bX=Points.Max(p=>p.X), aY=Points.Min(p=>p.Y), bY=Points.Max(p=>p.Y);
        return new Rect(aX, aY, bX-aX, bY-aY);
    }
```

(f) Copy ctor: also copy `FamilyAxisName` (FamilyCurves are rebuilt by the owner on bind, but copying
`FamilyAxisName` keeps the label correct before the first rebuild). Marker-add guard: treat `IsFamily` like
`IsCubeBound` wherever markers are blocked (markers on families are out of scope).

## 2. Parser — `src/Ui/DataDisplay/CubeTraceSpecParser.cs`
Replace the `xCount != 1` rejection with family-aware role assignment, and accept a bare cube name.

(a) **Bare cube name** (no `[`): before the `Missing '['` error, if the whole trimmed `text` (after an optional
`transform(...)`/`transform ` prefix) is just a cube name that `ds.Contains`, synthesize a token list of all
`:` (one per axis) and proceed as if the user typed `Name[:, :, …]`. (Simplest: when `bracketPos < 0`, set
`tokens = Enumerable.Repeat(":", cube.Rank)` after resolving `cubeName` from the prefix.)

(b) Role assignment after building `slice[]`: count kept axes (KeepWhole or KeepRange).
```csharp
    var keptDims = Enumerable.Range(0, slice.Length)
        .Where(i => slice[i].Role == AxisRole.KeepAsX).ToList();
    if (keptDims.Count == 0)
    { error = "Need at least one swept axis (':', 'All', or a range)."; return false; }
    if (keptDims.Count == 1) { /* single curve — leave as is */ }
    else if (keptDims.Count == 2)
    {
        // Convention: last kept axis = X; earlier kept axis = Family.
        int xDim = keptDims[^1], fDim = keptDims[0];
        slice[fDim] = slice[fDim] with { Role = AxisRole.FamilyIterate };
        // slice[xDim] stays KeepAsX
    }
    else
    { error = "A family supports one swept (Family) axis + one X axis. Pin the extra axes."; return false; }
```
(Remove the old `if (xCount != 1)` block.) Note `AxisSlice` is a record struct, so `with { Role = … }` works.

## 3. Owner routing + family resolver — `src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs`
`TrySetCubeData` currently runs the Expression path first whenever `t.Expression != null`. Picker AND typed
single-cube specs set `CubeName`+`Slice`, so route those through the **slice resolver** (now family-aware), and
reserve `TraceExpression` for genuine multi-cube expressions.

(a) At the top of the method, after computing `entry`/`ds`, change the dispatch:
```csharp
        // Single-cube specs (picker or typed Name[...]) resolve via the slice path (family-aware).
        // Only multi-cube element-wise expressions go through TraceExpression.
        bool singleCube = t.CubeName is not null && t.Slice is not null;
        if (t.Expression is not null && !singleCube)
        {
            // …existing TraceExpression branch unchanged…
            return;
        }
        // …existing single-slice branch follows (ds/CubeName/Slice null-guards unchanged)…
```

(b) In the single-slice branch, **before** the existing "build args / one X" logic, branch on family:
```csharp
        if (Array.Exists(slice, s => s.Role == AxisRole.FamilyIterate))
        {
            ResolveFamily(t, cube, slice, plotType, freqUnit);
            return;
        }
```
(c) Add `ResolveFamily` (static, mirrors the single-slice arg-building, looping the family axis):
```csharp
    private static void ResolveFamily(Trace t, DataCube cube, AxisSlice[] slice,
                                      PlotType plotType, FreqUnit freqUnit)
    {
        int fDim = Array.FindIndex(cube.Axes.ToArray... );   // axis whose name == the FamilyIterate slice entry
        // Resolve fDim/xDim by axis NAME (slice is name-keyed, order-independent — same pattern as the
        // single path). fDim = axis matching the FamilyIterate entry; xDim = axis matching KeepAsX.
        var fAxis = cube.Axes[fDim];
        int count = Math.Min(fAxis.Length, Trace.MaxFamilyCurves);
        // TODO message sink: if fAxis.Length > MaxFamilyCurves, surface a once-per-trace Message via the
        // same display→workspace seam the 7.2e Z0 warning uses (AddOnce). If no sink is reachable here,
        // match how the single path reports (do not invent a new dependency).

        double[]? xVals = null; string xName = ""; string? xUnit = null;
        var curves = new List<(double, string?, Complex[]?, double[]?)>(count);

        for (int k = 0; k < count; k++)
        {
            var args = new object[cube.Rank];
            for (int d = 0; d < cube.Rank; d++)
            {
                var ax = cube.Axes[d];
                var s  = Array.Find(slice, z => z.AxisName == ax.Name);
                if (s.Role == AxisRole.FamilyIterate) args[d] = k;
                else if (s.Role == AxisRole.KeepAsX)
                    args[d] = s.IsNarrowedRange ? new Range(s.RangeStart, s.RangeEndExclusive) : Range.All;
                else args[d] = Math.Clamp(s.Index, 0, Math.Max(0, ax.Length - 1));
            }
            var res = cube[args];
            if (!res.IsCube || res.Cube!.Rank != 1) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }
            var sliced = res.Cube!;
            if (xVals is null) { var xa = sliced.Axes[0]; xVals = xa.Values; xName = xa.Name;
                                 xUnit = string.IsNullOrEmpty(xa.Unit) ? null : xa.Unit; }
            curves.Add((fAxis.Values[k],
                        fAxis.Labels is { } L && k < L.Length ? L[k] : null,
                        sliced.DataKind == DataKind.Complex ? sliced.ComplexValues : null,
                        sliced.DataKind == DataKind.Real    ? sliced.RealValues    : null));
        }
        if (xVals is null) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }
        t.SetFamilyData(xVals, xName, xUnit, fAxis.Name, curves, plotType, freqUnit);
    }
```
> `DataCube.Axes` indexing + `cube[args]` SliceResult are the same APIs the single path already uses.

(d) `RebuildAndNotify`/`OnLibraryChanged` already call `TrySetCubeData` for cube traces — families rebuild
through the same calls, no extra wiring.

## 4. Renderer — `src/Ui/DataDisplay/Renderers/TraceRenderer_MarkerRenderer.cs`
In `TraceRenderer.Draw`, when `trace.IsFamily`, draw one stroked path per `FamilyCurve` with a stepped color,
then a legend; skip the single-path + markers blocks. Reuse the existing line paint setup.
```csharp
        if (trace.IsFamily)
        {
            if (!props.LineEnabled) return;   // (families: line only, markers out of scope)
            float strokeW = lw * (float)props.LineWidth;
            bool useSecondary = trace.UseSecondaryAxis;
            int baseIdx = props.LineColorIndex;
            int paletteN = TraceProperties.ColorLUT.Count;
            for (int k = 0; k < trace.FamilyCurves.Count; k++)
            {
                var curve = trace.FamilyCurves[k];
                var color = TraceProperties.ColorLUT[(baseIdx + k) % paletteN];   // step color per curve
                using var p = new SKPath();
                bool first = true;
                foreach (var pt in curve.Points)
                { var px = tf.ToCanvas(pt.X, pt.Y, useSecondary); if (first){p.MoveTo(px);first=false;} else p.LineTo(px); }
                using var paint = new SKPaint { Color = RenderTheme.ToSKColor(color, props.LineOpacity),
                    StrokeWidth = strokeW, Style = SKPaintStyle.Stroke, IsAntialias = true,
                    StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
                if (props.LineType == LineType.Dashed)
                    paint.PathEffect = SKPathEffect.CreateDash(new[]{strokeW*3f, strokeW*2f}, 0);
                canvas.DrawPath(paint is null ? p : p, paint);
            }
            DrawFamilyLegend(canvas, canvasSize, trace, tf, theme, baseIdx, paletteN);
            return;
        }
        // …existing single-curve line + marker blocks unchanged…
```
Add a `DrawFamilyLegend(...)` that renders a small box in a viewport corner: title = `trace.FamilyAxisName`,
then one swatch+label row per curve where the label is `AxisLabel ?? AxisValue.ToString("G4")` plus the family
axis unit if available. Cap the visible legend rows (e.g. first ~12) with a "…(+N)" tail so a 101-curve family
doesn't overflow — the curves still all draw; only the legend is capped. Match `MarkerRenderer` text style
(`SkiaFonts.PlexRegular`, `theme.TextColor`).
> Confirm the exact `TraceProperties.ColorLUT` type/length and `RenderTheme.ToSKColor` overloads (used above
> for the single path already) before building.

## 5. Picker — `AxisRoleRowViewModel` + `TraceRowViewModel`
Make the axis-role toggle 3-state (X / Pinned / Family):
- `AxisRoleRowViewModel`: add an `IsFamily` state alongside `IsX` (mutually exclusive; both off = Pinned).
  Setting Family calls back into the parent to demote any other Family row (≤1 family), mirroring the existing
  `OnAxisSetToX` demotion. A Family axis hides its pin combo (like X does).
- `TraceRowViewModel.FlushSliceAndRebuild`: map row state → `AxisRole.FamilyIterate` when `IsFamily`, else the
  existing KeepAsX/PinToIndex. Guard: at most one Family, exactly one X (if the user sets Family but no X,
  promote the last non-family kept axis, or fall back to axis 0 as X — mirror the existing "ensure one X").
- `RebuildAxisRolesCore`: read `FamilyIterate` back from `_trace.Slice` into the row's `IsFamily` so the picker
  reflects a loaded/auto-detected family.
- The unified spec text box already round-trips through `CommitSpec` → `CubeTraceSpecParser`; with §2 it now
  accepts `I:Ids[:, :]` and bare `I:Ids` and yields a FamilyIterate slice.

## 6. Persistence (`.cdd`)
`AxisSlice.Role` serializes by enum value, so `FamilyIterate` round-trips with no schema change. Do NOT
serialize `FamilyCurves`/`Points` (derived; rebuilt on load via `TrySetCubeData`). Confirm a family trace
saves + reloads and re-expands to N curves.

## Tests (`tests/Ui.Tests`, headless)
1. **Parser_TwoKept_AssignsFamily:** `CubeTraceSpecParser.TryParse("I:Ids[:, :]", ds)` on a `[Vgs,Vds]` cube →
   slice has Vds=KeepAsX, Vgs=FamilyIterate (last-kept=X convention). Bare `"I:Ids"` → same.
3. **Parser_ThreeKept_Errors:** three `:` axes → false with the "pin the extra axes" error.
4. **Family_RendersNCurves:** `I:Ids{Vgs(5),Vds(20)}`, Vds=X, Vgs=Family → `FamilyCurves.Count==5`, each 20 pts;
   values match `cube[g, ..]`.
5. **Family_Cap101:** family axis length 250 → `FamilyCurves.Count==101`; changing `Trace.MaxFamilyCurves`
   changes the count.
6. **Family_Roundtrips_Cdd:** save+reload → same N curves.
7. **OneTraceOneDelete:** a family is one `TraceRowViewModel`; removing it removes all curves.
8. **Family_Autoscale:** `PathBoundingRect` spans all curves (min/max across the fan).

## Gate (manual)
From the nested Vgs×Vds sweep: typing `I:Ids[:, :]` or bare `I:Ids`, OR setting Vds=X + Vgs=Family in the
picker, draws a fan of Id-vs-Vds curves (one per Vgs) with stepped colors + a Vgs legend; a >101 family clamps
to 101 with one Message; save/reload preserves it; restyling the single row restyles the whole family.

## On completion
Note in `src/Ui/CLAUDE.md`: a family trace is ONE `Trace` with a `FamilyIterate` axis → N curves
(`FamilyCurves`), reached via the picker's Family role OR an auto-recognized 2-kept-axis spec (`Name[:, :]` or
bare `Name`, convention last-kept=X / earlier-kept=Family); single-cube specs resolve via the slice path,
multi-cube expressions via TraceExpression; hard cap `Trace.MaxFamilyCurves=101`; markers on families deferred.
This completes Phase 7.3.
```
