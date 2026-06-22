# Brief — Markers Gate 4: Spectrum (stem) markers — type 2

**Status:** Ready to implement
**Scope:** Markers on harmonic-order **stem** traces (HB spectrum). The marker snaps stem-to-stem (discrete harmonic index), shows a triangle glyph, and its InfoBox reports the harmonic order + the stem's value. **No VSWR, no Γ/Z** (Cartesian harmonic axis).
**Design ref:** `/docs/design/trace-markers-design.md` §5.2 (type 2), §9 (glyph), §12 "Gate 4". Read those first.
**Depends on:** Gates 0–2b landed. (Gate 3 VSWR is independent — stem markers never get VSWR.)

---

## The shape of the problem (read once)

A harmonic stem trace is **cube-bound** (`IsCubeBound`) with `CubeXAxisName == "harmonic"` → `Trace.IsHarmonicStem` is true. Unlike contours, **a stem trace HAS populated `Points`**: `BuildCubePath` fills `Points` on the Rect path with `X = harmonic index`, `Y = transformed value`. So the geometry is already there; the blockers are the cube-bound guards:
- `GetMarkerDataLocation` returns `Vector2.Zero` for `IsCubeBound` → a stem marker would render at the origin.
- `BuildMarkerBoxLines` standard path calls `GetMarkerValString`/`DataPointScalar`, which return NaN for cube-bound → no readout.
- `AddMarkerAtFreqIndex` indexes `trace.Data.Frequencies` (empty/wrong for cube-bound) → can't add via the freq path.
- `FindNearestTraceData` **works** for stems (returns nearest `Points` index, X-distance metric for non-Complex YAxis) — reuse it.

**Position key (the owner's decision):** a stem marker stores the **harmonic X-value in `Marker.PositionStatic.X`** (the same field contour/stability markers reuse). This survives rebuild (harmonic X-values are stable) and is already serialized as `PositionStaticX/Y`. The marker re-locates by finding the `Points`/`_cubeXValues` entry whose X equals `PositionStatic.X`. `Freq` is unused for stem markers.

## Context (already verified — do not re-investigate)

- `Trace.IsHarmonicStem => IsCubeBound && CubeXAxisName == HarmonicAxisName` (`"harmonic"`).
- `Trace.CubeXValues` (`IReadOnlyList<double>?`) holds the harmonic orders; `Points[i].X` equals `_cubeXValues[i] * xScale` where `xScale == 1` for the harmonic axis (not a freq unit). So `Points[i].X == CubeXValues[i]` for stems.
- Stem rendering: `PlotRenderer.Draw` → `TraceRenderer.Draw(..., stemMode: plotIsRect && trace.IsHarmonicStem)`. Stem plots are always **Rect**.
- `PlotControl`: `TryAddMarkerNearPoint`/`AddMarkerAtCanvasPoint` route through `FindNearestTraceData`; `AddMarkerAtFreqIndex` is the freq-based add (wrong for stems); `MoveMarkerToCanvasPoint` has stability/contour/polyline branches.
- Gate 2b already added an `IsContourTrace` early-return at the top of `GetMarkerDataLocation` and `BuildMarkerBoxLines`. Stem branches go alongside those, gated on `IsHarmonicStem`.

## UI/Core build gate

UI builds with `TreatWarningsAsErrors=true`. Capture nullable into locals; no unused usings/fields.

---

## Task 1 — `Trace.GetMarkerDataLocation`: stem branch

Add a stem branch alongside the contour branch (before the generic `IsCubeBound → Zero` guard). It returns the `Points` entry whose X matches the stored harmonic value:

```csharp
public Vector2 GetMarkerDataLocation(Marker m)
{
    if (IsContourTrace)    return m.PositionStatic;
    if (IsHarmonicStem)    return StemPointFor(m);     // NEW
    if (IsCubeBound)       return Vector2.Zero;
    if (IsStabilityCircle) return m.PositionStatic;
    ...
}
```

Add a private helper that finds the nearest stem by X (defensive: exact match expected, nearest as fallback):

```csharp
private Vector2 StemPointFor(Marker m)
{
    if (Points.Count == 0) return Vector2.Zero;
    float targetX = m.PositionStatic.X;
    int best = 0; float bestD = float.PositiveInfinity;
    for (int i = 0; i < Points.Count; i++)
    {
        float d = Math.Abs(Points[i].X - targetX);
        if (d < bestD) { bestD = d; best = i; }
    }
    return Points[best];
}
```

## Task 2 — `Trace`: a stem add/move helper + value accessor

Add a public method to resolve a world point to the snapped stem (used by add + drag) and return both the position and the stem's X-value to store:

```csharp
/// <summary>For a harmonic-stem trace, snaps a world point to the nearest stem and returns
/// (snapped Points position, harmonic X-value to store in Marker.PositionStatic.X).
/// Returns null when not a stem trace or no points.</summary>
public (Vector2 Pos, float HarmonicX)? SnapToStem(Vector2 worldPt)
{
    if (!IsHarmonicStem || Points.Count == 0) return null;
    int best = 0; float bestD = float.PositiveInfinity;
    for (int i = 0; i < Points.Count; i++)
    {
        float d = Math.Abs(Points[i].X - worldPt.X);   // snap by X (harmonic order)
        if (d < bestD) { bestD = d; best = i; }
    }
    return (Points[best], Points[best].X);
}
```

And a value-string accessor for the InfoBox (reads the transformed cube value at the matched index):

```csharp
/// <summary>Marker value string for a harmonic-stem marker: the stem's transformed value
/// at the harmonic order stored in the marker. Uses the cube cell formatter.</summary>
public string GetStemValString(Marker m, bool showFilePrefix)
{
    string desc = showFilePrefix ? Description : ShortDescription;
    if (CubeXValues is not { } xs || xs.Count == 0) return $"{desc}=NaN";
    // Find the index whose X matches the stored harmonic value.
    int idx = 0; double bestD = double.PositiveInfinity;
    for (int i = 0; i < xs.Count; i++)
    {
        double d = Math.Abs(xs[i] - m.PositionStatic.X);
        if (d < bestD) { bestD = d; idx = i; }
    }
    string val = FormatCubeCell(idx, m.FormatString, m.MaximumFractionDigits);
    return $"{desc}={val}";
}

/// <summary>Harmonic order (integer-ish X) the stem marker sits on, for the InfoBox.</summary>
public string GetStemOrderString(Marker m)
    => $"harmonic={m.PositionStatic.X:G4}";
```

(`FormatCubeCell` already exists and applies the trace's `Transform`; reuse it — do not re-derive the value.)

## Task 3 — `Trace.BuildMarkerBoxLines`: stem branch

Add a stem branch alongside the contour branch, returning early:

```csharp
if (IsHarmonicStem)
{
    return new List<(string, bool)>
    {
        (m.MarkerString,                 true),
        (GetStemOrderString(m),          false),
        (GetStemValString(m, showFilePrefix), false),
    };
}
```

(No freq line — the X axis is harmonic order, not frequency. No impedance, no VSWR, no multi/delta for stems in this gate.)

## Task 4 — `PlotControl`: add + drag stem markers

### 4a. Add
Stem traces have `Points`, so `FindNearestTraceData` already returns the nearest stem index — but the existing add path (`AddMarkerAtFreqIndex`) then indexes `Data.Frequencies`, which is wrong for cube-bound. Add a stem fast-path mirroring the contour one. Factor a helper and call it from both `AddMarkerAtCanvasPoint` and `TryAddMarkerNearPoint`:

```csharp
private bool TryAddStemMarker(Trace trace, Point canvasPt)
{
    if (_plot is null || !trace.IsHarmonicStem) return false;

    var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
    var (wx, wy) = trace.UseSecondaryAxis
        ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
        : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

    var snap = trace.SnapToStem(new System.Numerics.Vector2((float)wx, (float)wy));
    if (snap is null) return false;

    int idx = NextMarkerIndexProvider?.Invoke() ?? (trace.Markers.Count + 1);
    var marker = new Marker(trace, 0.0, false, false, idx, _plot.FreqUnits)
    {
        MarkerKind            = MarkerKind.Spectrum,
        MaximumFractionDigits = AppSettingsViewModel.Instance.MarkerMaxFractionDigits,
        FormatString          = AppSettingsViewModel.Instance.MarkerPrecisionFormat,
        PositionStatic        = new System.Numerics.Vector2(snap.Value.HarmonicX, 0f),
    };

    trace.Markers.Add(marker);
    _renderDetail = PlotDetail.Full;
    InvalidateVisual();
    PlotChanged?.Invoke(this, EventArgs.Empty);
    MarkerAdded?.Invoke(marker, trace);
    return true;
}
```

- In `AddMarkerAtCanvasPoint`: `if (TryAddStemMarker(trace, canvasPt)) return;` at the top (after the contour guard added in 2b, if present there; order between contour/stem doesn't matter — they're mutually exclusive trace kinds).
- In `TryAddMarkerNearPoint`: stems DO produce a `FindNearestTraceData` hit, so they already flow into the snap logic. But that ends in `AddMarkerAtFreqIndex` (wrong). Simplest correct fix: at the **start** of `TryAddMarkerNearPoint`, after computing the best candidate, if `bestTrace?.IsHarmonicStem == true`, route to `TryAddStemMarker(bestTrace, canvasPt)` instead of `AddMarkerAtFreqIndex`. Concretely, just before the final `AddMarkerAtFreqIndex(bestTrace, bestFi, bestNearPt)`:

```csharp
if (bestTrace.IsHarmonicStem)
    return TryAddStemMarker(bestTrace, canvasPt);
```

### 4b. Drag
In `MoveMarkerToCanvasPoint` (static), add a stem branch (after the contour branch):

```csharp
if (trace.IsHarmonicStem)
{
    var (wx, wy) = tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
    var snap = trace.SnapToStem(new System.Numerics.Vector2((float)wx, (float)wy));
    if (snap is null) return;
    var snappedPx = tf.ToCanvas(snap.Value.Pos.X, snap.Value.Pos.Y, trace.UseSecondaryAxis);
    if (!clipRect.Contains(snappedPx.X, snappedPx.Y)) return;
    marker.PositionStatic = new System.Numerics.Vector2(snap.Value.HarmonicX, 0f);
    return;
}
```

The marker hops stem-to-stem as the cursor moves (snapped, per §5.2). `HitTestMarker` already works once `GetMarkerDataLocation` returns the stem point (Task 1).

## Out of scope (do NOT do in Gate 4)

- No VSWR for stem markers (Cartesian harmonic axis — §6.1 excludes them; Gate 3a's `VswrAvailableFor` already returns false for Rect-non-contour, so nothing to change).
- No multi-marker / delta support for stems in this gate.
- No glyph change — stems use the **triangle** (Gate 1 dispatch already gives triangle for `MarkerKind.Spectrum`, since only contour-Mode1 selects the ringed circle). Confirm the dispatch doesn't accidentally special-case Spectrum.
- No editor/context-menu changes (Gate 5 owns the properties editor).
- Don't touch contour/stability/polyline/table marker paths.

## Acceptance / verification

1. **Build green** (UI + Core, warnings-as-errors).
2. Open the harmonic-stem (HB spectrum) plot. Double-tap near a stem → a **triangle** marker appears **on that stem**; the InfoBox shows `harmonic=<order>` and `<desc>=<value>` (the stem's dB/mag/etc. value).
3. Drag the marker → it **hops stem-to-stem** (snaps to the nearest harmonic, never sits between stems).
4. Save/reload → the stem marker persists on the same harmonic order (PositionStatic.X round-trips).
5. The InfoBox value matches the stem height (cross-check against the Table view of the same cube if handy).
6. No VSWR option appears/draws for the stem marker. Other marker types unaffected.

## Report back

- Confirm build green; stem marker adds on a stem, hops stem-to-stem on drag, InfoBox shows harmonic order + value.
- Confirm the glyph is the triangle (not the ringed circle).
- Confirm save/reload keeps the marker on the same harmonic.
- Note whether `Points[i].X == CubeXValues[i]` held (xScale==1 for the harmonic axis) — if the harmonic axis ever carries a non-unit scale, flag it so the X-match logic can be revisited.
