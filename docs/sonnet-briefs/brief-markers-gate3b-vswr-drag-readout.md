# Brief — Markers Gate 3b: VSWR circle drag-to-resize + live readout

**Status:** Ready to implement
**Scope:** Make the VSWR locus drawn in Gate 3a **interactive**: drag the locus to change the marker's `VswrValue` (computed from the marker's Z/Γ vs. the pointer's Z/Γ), with a live readout that follows the pointer on the side away from the stroke and disappears on release. Dragging the locus does **not** move the marker.
**Design ref:** `/docs/design/trace-markers-design.md` §6.4 (interaction), §6.5 (value range), D7. Read those first.
**Depends on:** Gate 3a (landed) — `LoadpullSurface.VswrLocus`, `MarkerRenderer.DrawVswrLocus`, the `VswrAvailableFor` gate, and the full-complex-Z0 plumbing all exist.

---

## What already exists (reuse — do not rebuild)

- **Locus rendering:** `MarkerRenderer.DrawVswrLocus(canvas, canvasSize, marker, trace, tf, plane, z0Ref)` draws the red locus from `marker.VswrValue`. The drag only changes `marker.VswrValue` and lets the existing render redraw it live.
- **VSWR-from-two-terminations math already exists in RfCore** (`RfHelpers.cs`, both public):
  - `RfHelpers.VswrFromGamma(Complex g1, Complex g2)` → VSWR between two reflection coefficients.
  - `RfHelpers.VswrFromZ(Complex z1, Complex z2)` → VSWR between two **normalized** impedances.
  These are exactly the "marker-vs-pointer" computation in §6.4. **Do not write a new VSWR formula.**
- **Pointer state machine:** `PlotControl.OnPointerPressed/Moved/Released` already manages `_draggingMarker`/`_draggingTrace` (glyph drag) and panning. The locus drag is a new parallel state added alongside these.

## Normalization rule (critical — get this right)

`VswrFromGamma`/`VswrFromZ` both internally use **normalized** quantities (`VswrFromGamma` calls `G2Z`, which yields normalized Z; `VswrFromZ` expects already-normalized Z). The marker/pointer coordinates and the trace Z0 must be fed consistently:

- **Γ plane (Smith/Polar):** the marker coord and pointer coord are both Γ **in the plot's Γ plane** (same Z0 for both). Call `RfHelpers.VswrFromGamma(markerGamma, pointerGamma)` directly — both are referenced to the same plane, so the relative VSWR is correct regardless of the absolute Z0. **No Z0 needed for the Γ-plane drag math.** (Z0 only mattered in 3a for drawing the locus shape.)
- **Z plane (Rect contour):** the marker coord and pointer coord are **actual ohms** (Z), but `VswrFromZ` expects **normalized** Z. Normalize both by the trace's **full complex** Z0 first: `VswrFromZ(markerZ / trace.Z0, pointerZ / trace.Z0)` (use `trace.Z0`, fall back to `50+0j` if all-zero — same rule as 3a).

If unsure which plane at the call site, resolve it the same way 3a did: `plot.PlotType is Smith or Polar ⇒ Γ`, else `Z`.

## UI build gate

Ui builds with `TreatWarningsAsErrors=true`. Capture nullable into locals; no unused usings/fields. `RfHelpers` is in `RfCore` (already referenced by `src/Ui`).

---

## Task 1 — Locus hit-test (PlotControl)

Add a helper that decides whether a press at a screen point is on (near) a marker's VSWR locus. A press is "on the locus" when it's within a few px of the locus polyline **and** the marker has VSWR enabled+available. Reuse `LoadpullSurface.VswrLocus` to get the same points the renderer draws, map them to canvas, and test segment distance.

```csharp
// Returns the (marker, trace) whose VSWR locus is within grab distance of screenPt, or null.
private (Marker Marker, Trace Trace)? HitTestVswrLocus(Point screenPt)
{
    if (_plot is null) return null;
    var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
    float grabPx = (float)(Math.Min(Bounds.Width, Bounds.Height) / 200.0) * 4f; // ~glyph stroke × tolerance

    for (int ti = _plot.Traces.Count - 1; ti >= 0; ti--)
    {
        var trace = _plot.Traces[ti];
        for (int mi = trace.Markers.Count - 1; mi >= 0; mi--)
        {
            var marker = trace.Markers[mi];
            if (!marker.VswrEnabled || !PlotRendererVswrAvailable(_plot, trace, marker)) continue;

            var (plane, z0Ref) = ResolveVswrPlaneAndZ0(trace);
            var dl     = trace.GetMarkerDataLocation(marker);
            var center = new System.Numerics.Complex(dl.X, dl.Y);
            var pts    = RfCore.Loadpull.LoadpullSurface.VswrLocus(center, marker.VswrValue, plane, z0Ref);
            if (pts is null || pts.Length < 2) continue;

            for (int i = 0; i < pts.Length; i++)
            {
                var a = tf.ToCanvas(pts[i].Real, pts[i].Imaginary, trace.UseSecondaryAxis);
                var b = tf.ToCanvas(pts[(i + 1) % pts.Length].Real, pts[(i + 1) % pts.Length].Imaginary, trace.UseSecondaryAxis);
                if (DistPointToSegment((float)screenPt.X, (float)screenPt.Y, a.X, a.Y, b.X, b.Y) <= grabPx)
                    return (marker, trace);
            }
        }
    }
    return null;
}
```

Add two small helpers:
- `DistPointToSegment(px,py, ax,ay, bx,by)` → standard point-to-segment distance (private static float).
- `ResolveVswrPlaneAndZ0(Trace trace)` → `(SurfacePlane plane, Complex z0Ref)` using the same logic as 3a (Smith/Polar ⇒ Gamma; else Z; `z0Ref = trace.Z0 == Complex.Zero ? 50+0j : trace.Z0`).

**Sharing the §6.1 gate:** 3a added a private `VswrAvailableFor` in `PlotRenderer`. Either (a) make that method `internal static` and call it here as `PlotRenderer.VswrAvailableFor(...)`, or (b) duplicate the tiny gate in `PlotControl`. Prefer (a) — single source of truth. (`PlotRendererVswrAvailable` above is a stand-in name for whichever you pick.)

## Task 2 — Begin the drag (OnPointerPressed)

In `OnPointerPressed`, **left-button branch**, the locus grab must be checked in the right priority order: a press on the **glyph** should still move the marker (glyph wins), but a press on the **locus ring** (away from the glyph) starts a VSWR drag. Since the glyph is small and central and the locus is a ring around it, check the marker-glyph hit **first** (existing `HitTestMarker`), and only if that misses, check `HitTestVswrLocus`:

```csharp
// (existing) var hit = HitTestMarker(e.GetPosition(this)); ... if (hit.HasValue) { ...glyph drag... return; }

// NEW — after the glyph-hit block, before the panning block:
var vswrHit = HitTestVswrLocus(e.GetPosition(this));
if (vswrHit.HasValue)
{
    _draggingVswrMarker = vswrHit.Value.Marker;
    _draggingVswrTrace  = vswrHit.Value.Trace;
    _renderDetail = PlotDetail.Full;
    e.Pointer.Capture(this);
    e.Handled = true;
    return;
}
```

Add fields near `_draggingMarker`:
```csharp
private Marker? _draggingVswrMarker;
private Trace?  _draggingVswrTrace;
private Point   _vswrReadoutPt;       // last pointer pos (screen) for the readout
private bool    _vswrReadoutActive;   // true while dragging a locus
```

## Task 3 — Update during drag (OnPointerMoved)

Add a branch **before** the `_draggingMarker` branch (so a locus drag takes precedence over nothing-yet, but glyph drag is a separate state so order between them is moot — they're mutually exclusive). Compute the new VSWR from marker-vs-pointer and store the readout point:

```csharp
if (_draggingVswrMarker is not null && _draggingVswrTrace is not null)
{
    var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
    var (plane, z0Ref) = ResolveVswrPlaneAndZ0(_draggingVswrTrace);

    var dl     = _draggingVswrTrace.GetMarkerDataLocation(_draggingVswrMarker);
    var (pwx, pwy) = _draggingVswrTrace.UseSecondaryAxis
        ? tf.SecondaryFromCanvas((float)current.X, (float)current.Y)
        : tf.PrimaryFromCanvas((float)current.X, (float)current.Y);

    double vswr;
    if (plane == SurfacePlane.Gamma)
    {
        vswr = RfCore.RfHelpers.VswrFromGamma(
            new System.Numerics.Complex(dl.X, dl.Y),
            new System.Numerics.Complex(pwx, pwy));
    }
    else
    {
        var z0 = z0Ref == System.Numerics.Complex.Zero ? new System.Numerics.Complex(50,0) : z0Ref;
        vswr = RfCore.RfHelpers.VswrFromZ(
            new System.Numerics.Complex(dl.X, dl.Y) / z0,
            new System.Numerics.Complex(pwx, pwy) / z0);
    }

    if (double.IsFinite(vswr))
        _draggingVswrMarker.VswrValue = vswr;   // marker does NOT move; only its VSWR changes

    _vswrReadoutPt     = current;
    _vswrReadoutActive = true;
    InvalidateVisual();
    MarkerMoved?.Invoke(this, EventArgs.Empty);  // refresh InfoBox if it shows VSWR later
    return;
}
```

Note: the marker's `PositionStatic`/`Freq` are untouched — only `VswrValue` changes (§6.4: "Dragging the VSWR circle does not move the marker").

## Task 4 — End the drag (OnPointerReleased)

Add a teardown branch (mirror the `_draggingMarker` release):

```csharp
if (_draggingVswrMarker is not null)
{
    _draggingVswrMarker = null;
    _draggingVswrTrace  = null;
    _vswrReadoutActive  = false;     // readout disappears on release (§6.4)
    _renderDetail       = PlotDetail.Full;
    e.Pointer.Capture(null);
    InvalidateVisual();
    PlotChanged?.Invoke(this, EventArgs.Empty);   // persist the new VswrValue (undo/dirty)
    return;
}
```

## Task 5 — Live readout rendering

The readout is transient (only while dragging), so it is **not** part of the persistent `Plot`. Thread it through the existing render path as an optional parameter.

### 5a. Readout payload
Define a tiny struct (top of `PlotControl.cs` or a small shared file):
```csharp
public readonly record struct VswrReadout(string Text, SkiaSharp.SKPoint PointerPx);
```

### 5b. Thread it into the draw op
`PlotControl.Render` builds `PlotDrawOperation`. Add a `VswrReadout?` field to `PlotDrawOperation` and pass it into `PlotRenderer.Draw`. In `Render`, build it from drag state:
```csharp
VswrReadout? readout = null;
if (_vswrReadoutActive && _draggingVswrMarker is not null)
    readout = new VswrReadout(
        $"VSWR {_draggingVswrMarker.VswrValue:G4}:1",
        new SkiaSharp.SKPoint((float)_vswrReadoutPt.X, (float)_vswrReadoutPt.Y));
```
Pass `readout` through to `PlotRenderer.Draw(... , vswrReadout: readout)`.

### 5c. Draw it (PlotRenderer.Draw, end of the Full-detail block)
After the marker pass, if `vswrReadout` is non-null, draw the text near the pointer, offset to the side **away from** the locus stroke. Simplest robust rule for "away from the stroke": offset the text from the pointer by a fixed vector pointing **away from the marker center** (the locus surrounds the marker, so the outward direction from the marker is reliably off-stroke):

```csharp
if (vswrReadout is { } ro)
{
    using var font = new SKFont(SkiaFonts.PlexBold, (float)(Math.Min(canvasSize.W, canvasSize.H) * 0.028));
    using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true };
    // Offset outward (down-right default); callers may refine direction later.
    float ox = 10f, oy = -10f;
    canvas.DrawText(ro.Text, ro.PointerPx.X + ox, ro.PointerPx.Y + oy, SKTextAlign.Left, font, paint);
}
```

(If you can cheaply pass the marker-center canvas point too, compute the outward unit vector `pointer − center` and offset along it so the text is always on the far side of the ring from the marker. That's the §6.4 "away from the stroke" intent. If that's more than a couple of lines, the fixed outward offset above is acceptable for 3b; note it in report-back.)

`SkiaFonts.PlexBold` is already used in this file (watermark/markers) — reuse it; no new font seam.

## Out of scope (do NOT do in 3b)

- No property-editor VSWR field (Gate 5) — the value is set only by dragging here.
- No enable/disable UI (Gate 5) — keep using a saved file or a temporary `VswrEnabled=true` to test (same as 3a).
- Don't move the marker during a locus drag. Don't change the glyph, selection, or InfoBox content.
- Don't clamp the value in the drag path; the marker-vs-pointer formula naturally yields ≥1, and the unclamped-negative requirement (D7) concerns the property field + the locus formula (already tolerant from 3a).

## Acceptance / verification

1. **Ui builds green** (warnings-as-errors).
2. With a VSWR-enabled marker on a **Smith** plot: grab the red ring (away from the triangle/ringed glyph) and drag outward → the ring **grows**, the VSWR readout near the pointer climbs (e.g. `VSWR 3.2:1`), and the **marker itself does not move**. Drag inward → ring shrinks toward 1:1.
3. Release → the readout **disappears**; the ring stays at the new size; re-grabbing resumes from the new value.
4. Grabbing the **glyph** (not the ring) still moves the marker (glyph drag unaffected).
5. Save/reload → the dragged `VswrValue` persists (Gate 0 serialized it).
6. A **Rect contour** marker (Z plane), if available: dragging the ring updates VSWR using normalized Z; readout sane.

## Report back

- Confirm build green; ring drag changes VSWR with the marker stationary; readout appears during drag and vanishes on release.
- Confirm glyph drag still works and is not hijacked by the locus grab.
- State whether you implemented the "away-from-stroke" readout as the outward-from-center vector or the fixed offset.
- Confirm `RfHelpers.VswrFromGamma`/`VswrFromZ` were reused (no new VSWR formula) and the Z-plane path normalizes by the full complex `trace.Z0`.
