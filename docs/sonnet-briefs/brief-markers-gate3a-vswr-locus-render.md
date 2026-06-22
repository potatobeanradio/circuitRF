# Brief — Markers Gate 3a: VSWR locus API + overlay rendering (no interaction)

**Status:** Ready to implement
**Scope:** (A) Promote the existing VSWR locus math in RfCore to a **public, points-emitting** API. (B) Draw the red VSWR locus around any Z/Γ-domain marker that has `VswrEnabled`. **No interaction in this brief** — the locus is static (driven by `marker.VswrValue`, default 2). Dragging-to-resize + live readout is Gate 3b.
**Design ref:** `/docs/design/trace-markers-design.md` §6 (esp. §6.1 gating, §6.2 geometry, §6.3 rendering) and §12 "Gate 3". Read those first.
**Depends on:** Gates 0–2b (landed). Gate 0 added `Marker.VswrEnabled`/`VswrValue`.

---

## Q3 resolved (read once)

The VSWR locus formula already exists in `RfCore/src/Loadpull/LoadpullSurface.cs` but is **private** and only ever used to compute a bounding box:
- `VswrCircleZ(Complex zCenter, double vswr, int nPoints)` → `Complex[]` of the locus **in the Z-plane** (parametrization `Z(θ) = (Zc + ρ·e^{jθ}·conj(Zc))/(1 − ρ·e^{jθ})`, `ρ = (vswr−1)/(vswr+1)`).
- `VswrBoundingBox(center, vswr, plane, z0ref)` builds the **Γ-plane** locus inline (Γ_center → Z via `RfHelpers.G2Z(center)·z0` → `VswrCircleZ` → back to Γ via `RfHelpers.Z2G(zPt/z0)`), then throws the points away into a `ViewBox`.

The renderer needs the **actual locus points** in the marker's plane (not a bbox). So Gate 3a promotes a public points API and rebuilds `VswrBoundingBox` on top of it (no math change).

**Z0 rule (from the owner, non-negotiable):** the Γ→Z mapping MUST use the **host trace's own Z0** (`Trace.Z0`, a `Complex`). The reference impedance is the **full complex value** — do NOT take `.Real` or otherwise drop the imaginary part. (The locus formula already depends on a complex reference: `VswrCircleZ` uses `conj(Zc)`, which only matters when Zc is complex.) Never substitute another Z0. The only fallback — used solely if there were no trace at all (not possible here) — is `50 + 0j`.

## UI/Core build gate

RfCore has **no** `TreatWarningsAsErrors` (safe there). The **Ui** project does — watch unused usings/locals in the renderer half. `PlotRenderer.cs` already has `using RfCore.Loadpull;` and uses `SurfacePlane`.

---

## Part A — RfCore: public `VswrLocus` (LoadpullSurface.cs)

Add a **public static** method that returns the locus points in the requested plane, and refactor the two existing private helpers to call it (pure promotion — identical math).

```csharp
/// <summary>
/// Constant-VSWR locus around <paramref name="center"/>, as a closed ring of points
/// in the requested plane. For <see cref="SurfacePlane.Z"/> the locus is computed directly
/// in the Z-plane. For <see cref="SurfacePlane.Gamma"/> the Γ center is mapped to Z using
/// <paramref name="z0ref"/> (the host trace's reference impedance — a FULL complex value,
/// imaginary part included), the Z-plane circle is built, then each point is mapped back to Γ
/// normalized to z0ref. The locus is generally NOT a literal circle in the displayed plane and
/// the center is not necessarily its centroid. vswr is unclamped (negative permitted).
/// </summary>
public static Complex[] VswrLocus(
    Complex center, double vswr, SurfacePlane plane, Complex z0ref, int nPoints = VswrNPoints)
{
    if (plane == SurfacePlane.Gamma)
    {
        // z0ref is the full complex reference impedance; keep it complex end-to-end.
        Complex zActual = RfHelpers.G2Z(center) * z0ref;
        Complex[] zPts  = VswrCircleZ(zActual, vswr, nPoints);
        var pts = new Complex[zPts.Length];
        for (int i = 0; i < zPts.Length; i++)
            pts[i] = RfHelpers.Z2G(zPts[i] / z0ref);
        return pts;
    }
    return VswrCircleZ(center, vswr, nPoints);
}
```

Then simplify `VswrBoundingBox` to reuse it. Its existing signature passes `double? z0ref` (the internal auto-framing path uses a real reference); widen that to `Complex` at the call so the internal path is unchanged and the new API is fully complex:

```csharp
private static ViewBox VswrBoundingBox(
    Complex center, double vswr, SurfacePlane plane, double? z0ref)
    => BoundingBox(VswrLocus(center, vswr, plane, new Complex(z0ref ?? 50.0, 0.0)));
```

Leave `VswrCircleZ`, `BoundingBox`, `VswrCirclePoints`, `VswrNPoints` as-is. The internal `RecommendedBox`/MXX-search behavior is preserved exactly (real z0ref widened to `re + 0j` reproduces the old `double` math). The new public `VswrLocus` is the only complex-aware entry point, which is what the overlay calls.

**Verify A in isolation:** RfCore builds; existing contour view-box behavior (auto-frame on contour open) is unchanged (because `VswrBoundingBox` produces identical points → identical bbox).

## Part B — Ui: draw the locus overlay (MarkerRenderer + PlotRenderer)

### B1. New renderer method `MarkerRenderer.DrawVswrLocus`
Add to `MarkerRenderer` (in `TraceRenderer_MarkerRenderer.cs`). It draws a red, no-fill closed polyline through the locus points. It receives the already-resolved plane + z0 so it has no plot/firewall dependency itself:

```csharp
/// <summary>
/// Draws the constant-VSWR locus (red stroke, no fill) around a marker that carries a Z/Γ value.
/// plane/z0Ref are resolved by the caller (PlotRenderer) from the host plot + trace.
/// Drawn inside the plot clip. No-op when the marker has no usable coordinate.
/// </summary>
public static void DrawVswrLocus(
    SKCanvas canvas, (double W, double H) canvasSize,
    Marker marker, Trace trace, TransformSet tf,
    RfCore.Loadpull.SurfacePlane plane, System.Numerics.Complex z0Ref)
{
    if (!marker.VswrEnabled) return;

    var dl = trace.GetMarkerDataLocation(marker);                  // marker coord in the plane
    var center = new System.Numerics.Complex(dl.X, dl.Y);

    var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(
        center, marker.VswrValue, plane, z0Ref);
    if (pts is null || pts.Length < 2) return;

    float lw = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
    using var paint = new SKPaint
    {
        Color       = SKColors.Red,
        StrokeWidth = lw * 1.1f,
        Style       = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeJoin  = SKStrokeJoin.Round,
    };

    using var path = new SKPath();
    var p0 = tf.ToCanvas(pts[0].Real, pts[0].Imaginary, trace.UseSecondaryAxis);
    path.MoveTo(p0);
    for (int i = 1; i < pts.Length; i++)
    {
        var p = tf.ToCanvas(pts[i].Real, pts[i].Imaginary, trace.UseSecondaryAxis);
        path.LineTo(p);
    }
    path.Close();
    canvas.DrawPath(path, paint);
}
```

Notes:
- `System.Numerics.Complex` — add `using System.Numerics;` to the file if not already present (the file may only `using SkiaSharp;`/`System`; check and add if needed; an unused-using would warn so only add if you reference it — you do).
- The locus lives in the same world plane as the marker glyph (`GetMarkerDataLocation` returns Γ on Smith/Polar, Z on Rect, or the contour `PositionStatic`), so `tf.ToCanvas` maps it correctly with no extra transform.

### B2. Call it from `PlotRenderer.Draw`
In `PlotRenderer.Draw`, the marker-symbol pass (the `detail == PlotDetail.Full` block that loops `trace.Markers` and calls `MarkerRenderer.DrawSymbol`) has `plot`, `trace`, and `tf` in scope. Resolve plane + z0 there and draw the locus **before** the glyph (so the glyph sits on top of the locus):

```csharp
foreach (var trace in plot.Traces)
    foreach (var marker in trace.Markers)
    {
        if (marker.VswrEnabled && VswrAvailableFor(plot, trace, marker))
        {
            var vplane = plot.PlotType is PlotType.Smith or PlotType.Polar
                ? SurfacePlane.Gamma : SurfacePlane.Z;
            // Full complex trace Z0 is the reference — never drop the imaginary part.
            var z0Ref = trace.Z0 == System.Numerics.Complex.Zero
                ? new System.Numerics.Complex(50.0, 0.0)
                : trace.Z0;
            MarkerRenderer.DrawVswrLocus(canvas, canvasSize, marker, trace, tf, vplane, z0Ref);
        }
        MarkerRenderer.DrawSymbol(canvas, canvasSize, marker, trace, tf, theme,
            isSelected:     selectedMarkers?.Contains(marker) ?? false,
            selectionColor: selectionColor);
    }
```

Add a small private gate helper in `PlotRenderer` implementing §6.1 (domain availability — NOT roaming):

```csharp
// §6.1: VSWR locus is available only when the marker has a well-defined Z/Γ value.
//  - Smith/Polar plot: any marker on a complex plane qualifies (type 1-on-Smith, type 3, type 5).
//  - Rect plot: only a contour marker (Z-plane); ordinary Rect traces are Cartesian (no Z/Γ).
//  - Table excluded (handled elsewhere — tables don't reach this renderer path).
private static bool VswrAvailableFor(Plot plot, Trace trace, Marker marker)
{
    if (plot.PlotType is PlotType.Smith or PlotType.Polar) return true;
    if (plot.PlotType == PlotType.Rect) return trace.IsContourTrace;
    return false;
}
```

(This mirrors §6.1 without over-reaching; type-2 spectrum is Rect-non-contour → excluded; type-1 on a Cartesian Rect → excluded.)

**Z0 note:** `z0Ref` is the **full complex** `trace.Z0` — honoring the owner's rule; `50 + 0j` only guards a degenerate all-zero Z0. (Contour traces default `Z0 = 50` already.) Passing `trace.Z0.Real` would be wrong: the imaginary part is part of the reference impedance and the `conj(Zc)` term in the locus formula depends on it.

## Out of scope (do NOT do in 3a)

- **No interaction** — no drag-to-resize, no live readout, no pointer hit-testing of the locus. Static locus from `marker.VswrValue` only. (Gate 3b.)
- No way yet for the user to *enable* VSWR from the UI (that checkbox is Gate 5). For testing, toggle `VswrEnabled` via a temporary hardcode or set it in a saved file — see verification.
- Don't change the glyph dispatch, the selection-highlight block, or any InfoBox content.
- Don't add VSWR to tables or to type-1 markers on Cartesian Rect plots.

## Acceptance / verification

1. **RfCore builds; Ui builds** (Ui warnings-as-errors).
2. Contour auto-framing unchanged (open a contour — the view box frames as before; proves the `VswrBoundingBox` refactor is behavior-preserving).
3. **Temporary enable for eyeball (then revert):** in `PlotRenderer.Draw`, just before the locus call, temporarily force `marker.VswrEnabled = true` for one marker (or hardcode `VswrAvailableFor` to true) on:
   - a **Smith** contour marker → a **red locus** draws around it; it is generally **not** a perfect circle and the marker is **not** at its centroid (expected per §6.2); with `VswrValue = 2` it's a modest ring.
   - a **Smith type-1** marker (S11 point) → red locus draws around the point.
   Confirm the locus uses the **trace's full complex Z0** (try a trace whose Z0 has a non-zero imaginary part if available; the locus shape should reflect it). **Then revert the hardcode.**
4. A marker on a **Cartesian Rect** trace (e.g. dB vs freq) draws **no** locus even with `VswrEnabled` forced (gated out by `VswrAvailableFor`).

## Report back

- Confirm RfCore + Ui build green and contour framing is unchanged.
- Confirm the red locus renders for a Smith contour marker and a Smith S11 marker, and is correctly absent on Cartesian Rect.
- Confirm `VswrLocus` takes a full `Complex z0ref` and the `PlotRenderer` call passes the full `trace.Z0` (no `.Real`).
- Confirm you reverted the temporary `VswrEnabled` hardcode.
- Note the `VswrLocus` point count you used (default `VswrNPoints` = 100) in case 3b wants it denser for smooth dragging.
