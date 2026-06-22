# Brief — Markers Gate 2b: Contour marker behavior (the headline ask) ★

**Status:** Ready to implement
**Scope:** Make markers actually work on contour traces — add, hit-test, drag (Mode 1 free / Mode 2 snapped), InfoBox readout, and the "Snap to Point" context-menu toggle. Consumes the Gate 2a evaluation seam.
**Design ref:** `/docs/design/trace-markers-design.md` §5.5, §9, §12 "Gate 2". Read those first.
**Depends on:** Gates 0, 1, 2a (all landed). Gate 0 added `Marker.MarkerKind`/`ContourSnapped`; Gate 1 added the ringed-circle glyph dispatch; Gate 2a added `ContourData.EvaluateMetric` and `ContourData.NearestNode`.

VSWR circles are **Gate 3** — out of scope here. The inspector mode toggle is **Gate 5** — out of scope; this brief wires only the context-menu toggle.

---

## The core problem (read once)

Contour traces are **cube-bound** (`IsContourTrace ⇒ IsCubeBound`). Three methods currently block contour markers because they assume SNP/`Points` data:
1. `Trace.GetMarkerDataLocation(m)` returns `Vector2.Zero` for `IsCubeBound` → contour markers render at the origin.
2. `Trace.FindNearestTraceData(q)` returns null when `Points` is empty (contours have none) → the add/drag paths bail.
3. `PlotControl.AddMarkerAtFreqIndex` indexes `trace.Data.Frequencies` (empty for contours) → would throw/clamp wrong.

A contour marker is positioned by a **world Γ/Z coordinate**, stored in `Marker.PositionStatic` (reused per Gate 0 — same field stability circles use). Its value comes from `ContourData.EvaluateMetric(coord, snapped)` (Gate 2a). Mode 1 = free roam (`PositionStatic` = cursor world point, `ContourSnapped == false`); Mode 2 = snapped (`PositionStatic` = `ContourData.NearestNode(cursor)`, `ContourSnapped == true`).

## Context (already verified — do not re-investigate)

- `Trace.cs`: `IsContourTrace => ContourData != null`; `IsCubeBound => CubeName is not null || Expression is not null` (true for contours). `GetMarkerDataLocation` has the cube-bound guard at the top. `BuildMarkerBoxLines` builds 3 base lines (`MarkerString`, `FreqString`, `GetMarkerValString`) then SNP-only extras; everything SNP-specific is gated on `!IsCubeBound`/`IsStabilityCircle`.
- `ContourData` (Gate 2a): `Func<Complex,bool,double>? EvaluateMetric` and `Func<Complex,Complex>? NearestNode`, both null until first successful fit. Always null-check.
- `PlotControl.cs`: `HitTestMarker` already uses `GetMarkerDataLocation` + `SymbolHitRadius` — it will work for contours **once `GetMarkerDataLocation` returns the Γ**, no change needed there. `TryAddMarkerNearPoint` and `AddMarkerAtCanvasPoint` both route through `FindNearestTraceData`. `MoveMarkerToCanvasPoint` (static) has `IsStabilityCircle` vs polyline branches. `ShowMarkerContextMenu` calls `MarkerInfoBoxView.PopulateMarkerMenu`.
- `MarkerInfoBoxView.PopulateMarkerMenu` is `static internal`, shared by the InfoBox right-click menu and PlotControl's `ShowMarkerContextMenu`. Adding an item there covers both surfaces.
- `Marker` primary ctor is `Marker(Trace, double freq, bool isMulti, bool isDelta, int index, FreqUnit)`. For contours, `freq` is irrelevant (set it from `ContourData.FreqIndex`'s frequency or 0; the InfoBox won't show a meaningful freq line for contours — see Task 4).
- `Complex` = `System.Numerics.Complex`. `Trace.cs`, `ContourData.cs`, `PlotControl.cs` all already `using System.Numerics;` (PlotControl uses fully-qualified `System.Numerics.Vector2` in places — match the local style).

## UI/Core build gate

UI builds with `TreatWarningsAsErrors=true`. Capture nullable props into locals before use; no unused usings/fields.

---

## Task 1 — `Trace.GetMarkerDataLocation`: return the contour marker's Γ

In `Trace.cs`, at the top of `GetMarkerDataLocation`, add a contour branch **before** the existing `IsCubeBound` guard (contours are cube-bound, so it must come first):

```csharp
public Vector2 GetMarkerDataLocation(Marker m)
{
    if (IsContourTrace)    return m.PositionStatic;   // contour markers positioned by world Γ/Z
    if (IsCubeBound)       return Vector2.Zero;
    if (IsStabilityCircle) return m.PositionStatic;
    ...
}
```

This alone makes `HitTestMarker` and glyph rendering work for contour markers (the glyph dispatch from Gate 1 already keys on `MarkerKind`/`ContourSnapped`).

## Task 2 — `Trace`: a contour add/move helper

The PlotControl add/move paths need to turn a world point into a contour marker position. Add two small public methods to `Trace.cs` (keep the surface logic in `ContourData`'s delegates; `Trace` just routes):

```csharp
/// <summary>Resolves a world Γ/Z point to the position a contour marker should take,
/// honoring the marker's mode: Mode 1 (free) returns the point unchanged; Mode 2 (snapped)
/// returns the nearest measured grid-node coordinate. No-op fallback when no fit yet.</summary>
public Vector2 ResolveContourMarkerPosition(Marker m, Vector2 worldPt)
{
    if (!IsContourTrace) return worldPt;
    if (m.ContourSnapped && ContourData?.NearestNode is { } snap)
    {
        var c = snap(new Complex(worldPt.X, worldPt.Y));
        return new Vector2((float)c.Real, (float)c.Imaginary);
    }
    return worldPt;
}
```

(`EvaluateMetric` is read in Task 4 for the InfoBox, not here.)

## Task 3 — `PlotControl`: add + drag contour markers

### 3a. Add path
Both `TryAddMarkerNearPoint` and `AddMarkerAtCanvasPoint` currently call `trace.FindNearestTraceData(...)` and bail for contours. Add a contour fast-path. Factor a small private helper and call it from both:

```csharp
// Returns true if it added a contour marker at the cursor world point.
private bool TryAddContourMarker(Trace trace, Point canvasPt)
{
    if (_plot is null || !trace.IsContourTrace) return false;

    var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
    var (wx, wy) = tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
    var world = new System.Numerics.Vector2((float)wx, (float)wy);

    int idx  = NextMarkerIndexProvider?.Invoke() ?? (trace.Markers.Count + 1);
    var marker = new Marker(trace, 0.0, false, false, idx, _plot.FreqUnits)
    {
        MarkerKind            = MarkerKind.Contour,
        MaximumFractionDigits = AppSettingsViewModel.Instance.MarkerMaxFractionDigits,
        FormatString          = AppSettingsViewModel.Instance.MarkerPrecisionFormat,
    };
    marker.PositionStatic = trace.ResolveContourMarkerPosition(marker, world);

    trace.Markers.Add(marker);
    _renderDetail = PlotDetail.Full;
    InvalidateVisual();
    PlotChanged?.Invoke(this, EventArgs.Empty);
    MarkerAdded?.Invoke(marker, trace);
    return true;
}
```

- In `AddMarkerAtCanvasPoint(trace, canvasPt)`: at the very top, `if (TryAddContourMarker(trace, canvasPt)) return;` before the `FindNearestTraceData` logic.
- In `TryAddMarkerNearPoint(canvasPt)`: contour traces have no `Points`, so the nearest-data loop skips them. Add a pass that, if the best non-contour candidate is beyond `SnapPx`, checks whether the cursor is over a contour trace and adds there. Simplest correct approach: **before** the existing loop, if any single contour trace exists under the cursor region, prefer the right-click/explicit path. To keep behavior predictable, only auto-add to a contour from a double-tap when there is at least one contour trace and the tap missed all polyline data:

```csharp
// After the existing loop computes bestTrace/bestPixDist, before returning false:
if (bestTrace is null || bestPixDist > SnapPx)
{
    // Fall back to a contour trace if one is present (free-roam add at the cursor).
    var contour = _plot.Traces.FirstOrDefault(t => t.IsContourTrace);
    if (contour is not null) return TryAddContourMarker(contour, canvasPt);
    return false;
}
```

(If multiple contour traces overlap, first-wins is acceptable for now; right-click "Add Marker → <trace>" already targets a specific trace via `AddMarkerAtCanvasPoint`.)

### 3b. Drag path
In `MoveMarkerToCanvasPoint` (static), add a contour branch **first**:

```csharp
if (trace.IsContourTrace)
{
    var (wx, wy) = tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
    var snapped  = tf.ToCanvas(wx, wy, false);
    if (!clipRect.Contains(snapped.X, snapped.Y)) return;
    marker.PositionStatic = trace.ResolveContourMarkerPosition(
        marker, new System.Numerics.Vector2((float)wx, (float)wy));
    return;
}
```

Mode 1 → position follows the cursor; Mode 2 → `ResolveContourMarkerPosition` snaps to the nearest node. Glyph follows automatically (Gate 1 dispatch). Clip guard mirrors the existing branches.

## Task 4 — `Trace.BuildMarkerBoxLines`: contour InfoBox branch

Per D5, Mode 1 reports **only** the interpolated surface value at the marker's Γ/Z; Mode 2 reports the exact grid value. Add a contour branch at the **top** of `BuildMarkerBoxLines`, returning early (don't fall through to the SNP lines):

```csharp
if (IsContourTrace && ContourData is { } cd)
{
    var lines = new List<(string, bool)> { (m.MarkerString, true) };

    var coord = new Complex(m.PositionStatic.X, m.PositionStatic.Y);
    double val = cd.EvaluateMetric?.Invoke(coord, m.ContourSnapped) ?? double.NaN;

    string metric = string.IsNullOrEmpty(cd.MetricName) ? "value" : cd.MetricName;
    string fmt    = $"{m.FormatString}{m.MaximumFractionDigits}";
    string valStr = double.IsFinite(val) ? val.ToString(fmt) : "NaN";
    string cue    = m.ContourSnapped ? "" : " (interp)";
    lines.Add(($"{metric}={valStr}{cue}", false));

    // Coordinate readout in the plane the contour lives in.
    bool gammaPlane = PlotType is PlotType.Smith or PlotType.Polar;   // see note below
    string coordLbl = gammaPlane ? "Γ" : "Z";
    lines.Add(($"{coordLbl}={m.FormatComplex(coord)}", false));
    return lines;
}
```

**Plane note:** `Trace` does not hold its host `PlotType`. Resolve the plane without it: a contour on a Smith/Polar plot stores Γ in `PositionStatic`; on Rect it stores Z. The cheapest reliable signal already on the trace is **the same one `RebuildContour` used** — but that lives in the VM. To avoid plumbing `PlotType` into `Trace`, label the coordinate generically as the value's domain using `ContourData`. If `ContourData` does not already carry a plane/`IsGammaPlane` flag, **add a `bool GammaPlane` to `ContourData`** (defaulted false, set in `RebuildContour` from `plane == SurfacePlane.Gamma`, non-serialized like the other derived fields) and read it here. Confirm whether such a flag already exists before adding; if `MxpCoord`/plane info is already inferable, reuse it. Keep this minimal.

`MarkerShowsImpedance(m)` returns false for cube-bound traces, so the SNP impedance line won't appear — good. `SetMarkerFreq`/`Increment`/`Decrement` already early-return for `IsCubeBound`, so freq-stepping a contour marker is a safe no-op.

## Task 5 — "Snap to Point" context-menu toggle

In `MarkerInfoBoxView.PopulateMarkerMenu`, add a checkbox item for contour markers only, and give callers a way to react (re-snap + redraw). Add an optional parameter so existing callers compile unchanged:

```csharp
internal static void PopulateMarkerMenu(
    ContextMenu menu, Marker marker, Trace trace,
    IList<Trace> allTraces,
    Action? openEditorFlyout, Action<Trace> changeToTrace, Action removeMarker,
    bool showFilePrefix = true,
    Action? onContourModeToggled = null)   // NEW (optional)
{
    ...
    // After the Remove item, before/after the separator as fits:
    if (marker.MarkerKind == MarkerKind.Contour)
    {
        var snapItem = new MenuItem
        {
            Header = "Snap to Point",
            Icon   = new MaterialIcon
            {
                Kind = marker.ContourSnapped
                    ? MaterialIconKind.CheckboxOutline
                    : MaterialIconKind.CheckboxBlankOutline,
            },
        };
        snapItem.Click += (_, _) =>
        {
            marker.ContourSnapped = !marker.ContourSnapped;
            // Re-resolve position so the glyph + readout switch modes immediately.
            marker.PositionStatic = trace.ResolveContourMarkerPosition(marker, marker.PositionStatic);
            onContourModeToggled?.Invoke();
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(snapItem);
    }
}
```

Wire the callback at both call sites:
- In `MarkerInfoBoxView.RebuildContextMenu`, pass `onContourModeToggled: () => Vm?.NotifyMarkerChanged()` (or whatever the VM's existing "marker moved/redraw" notify is — reuse the one `MarkerMoved`/redraw path already uses; if unsure, call the same method the drag uses to refresh the box + plot).
- In `PlotControl.ShowMarkerContextMenu`, pass `onContourModeToggled: () => { InvalidateVisual(); PlotChanged?.Invoke(this, EventArgs.Empty); MarkerMoved?.Invoke(this, EventArgs.Empty); }`.

## Out of scope (do NOT do in 2b)

- No VSWR (Gate 3). Leave `VswrEnabled`/`VswrValue` untouched.
- No inspector mode toggle (Gate 5) — only the context-menu "Snap to Point" here.
- No spectrum/table/type-1 marker changes.
- Don't change the Gate 1 glyph dispatch or the selection-highlight block.
- Don't add a `DataSet`/`LoadpullSurface` reference to `Trace` — read values only through `ContourData`'s delegates.

## Acceptance / verification

1. **Build green** (UI + Core, warnings-as-errors).
2. Open a load-pull contour (Smith). Double-tap on the contour → a marker appears with the **ringed-circle** glyph (Mode 1); the InfoBox shows `<metric>=<value> (interp)` and `Γ=…`. Drag it → it roams freely and the interpolated value updates live.
3. Right-click the marker → **"Snap to Point"** (unchecked). Click it → glyph switches to the **triangle**, the marker jumps to the nearest grid node, the readout loses "(interp)" and shows the exact node value. Right-click again → checkbox shows checked.
4. Toggle back to free → ringed circle returns, value goes interpolated again.
5. Save/reload the plot → contour marker persists at its Γ with its mode (Gate 0 serialization carries `MarkerKind`/`ContourSnapped`/`PositionStaticX/Y`).
6. Non-contour markers (polyline, Smith point, stability circle, table) are unchanged.
7. A contour on a **Rect/Z** plot (if available) labels the coord `Z=…` and reads sensible values.

## Report back

- Confirm build green and the Mode 1 ↔ Mode 2 toggle works (glyph + value both switch).
- Confirm whether you added `ContourData.GammaPlane` or found an existing plane signal (name it).
- Confirm save/reload round-trips a contour marker with its mode.
- Note any place the `FreqString` line looks wrong for contour markers (we may suppress it in a later polish pass).
