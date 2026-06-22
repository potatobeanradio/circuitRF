# Brief — Gate 6 Round 1 / B: marker context-menu (VSWR item, ordering) + VSWR locus clipping

**Status:** Ready to implement
**Scope:** Three related changes to the marker context menu, plus clipping the VSWR locus to the plot region. All UI-layer.
**Depends on:** Gates 0–5 landed; Brief A may land first or in parallel (no overlap).

**Context (already verified):**
- `MarkerInfoBoxView.PopulateMarkerMenu` (static) is the single source for the marker context menu, used by both the InfoBox right-click and `PlotControl.ShowMarkerContextMenu`. It currently adds, in order: Edit, Change-to-Trace, Separator, **Remove**, then (contour only) Separator + "Snap to Point", then Separator + "Show Info Box".
- VSWR enable lives on `marker.VswrEnabled` / value on `marker.VswrValue`. The §6.1 availability gate is `PlotRenderer.VswrAvailableFor(plot, trace, marker)` (internal static).
- The locus is drawn in `PlotRenderer.Draw`'s Full-detail marker pass, currently inside `canvas.Save(); canvas.ClipRect(new SKRect(0,0,(float)canvasSize.W,(float)canvasSize.H)); … canvas.Restore();` — i.e. clipped to the **whole control**, not the plot viewport. That's why the locus spills outside the plot.

## UI build gate
UI builds with `TreatWarningsAsErrors=true`.

---

## Change 1 — Add a "VSWR" toggle item to the marker menu

In `PopulateMarkerMenu`, add a VSWR checkbox item **only when VSWR is available for this marker** (same gate the editor uses). When enabled, the header shows the value to 3 decimals.

```csharp
// Needs the host plot to evaluate the §6.1 gate. PopulateMarkerMenu currently
// receives `allTraces` (IList<Trace>) but not the Plot. Add a `Plot hostPlot`
// parameter (see "Signature change" below) and use it here.
if (PlotRenderer.VswrAvailableFor(hostPlot, trace, marker))
{
    var vswrItem = new MenuItem
    {
        Header = marker.VswrEnabled
            ? $"VSWR: {marker.VswrValue.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)}"
            : "VSWR",
        Icon = new MaterialIcon
        {
            Kind = marker.VswrEnabled
                ? MaterialIconKind.CheckboxOutline
                : MaterialIconKind.CheckboxBlankOutline,
        },
    };
    vswrItem.Click += (_, _) =>
    {
        marker.VswrEnabled = !marker.VswrEnabled;
        onVswrToggled?.Invoke();
    };
    // (added into the grouped block — see Change 2 for exact placement)
}
```

`"0.###"` gives up to 3 decimals with no trailing zeros (e.g. `2`, `2.5`, `3.142`). If the owner prefers fixed 3 decimals (`2.000`), use `"F3"` instead — note which you used in report-back.

### Signature change
`PopulateMarkerMenu` needs the host `Plot` (for the gate) and an `onVswrToggled` callback. Add both:
```csharp
internal static void PopulateMarkerMenu(
    ContextMenu menu, Marker marker, Trace trace,
    IList<Trace> allTraces, Plot hostPlot,                 // NEW: hostPlot
    Action? openEditorFlyout, Action<Trace> changeToTrace, Action removeMarker,
    bool showFilePrefix = true,
    Action? onContourModeToggled = null,
    Action? onShowInfoBoxToggled = null,
    Action? onVswrToggled = null)                          // NEW
```
Update both call sites:
- `MarkerInfoBoxView.RebuildContextMenu`: pass `Vm.Container.PlotVM.Plot` as `hostPlot`; `onVswrToggled: () => { Vm.RequestRedraw(); Vm.Container.RequestPlotRedraw(); }`.
- `PlotControl.ShowMarkerContextMenu`: pass `_plot` as `hostPlot`; `onVswrToggled: () => { InvalidateVisual(); PlotChanged?.Invoke(this, EventArgs.Empty); }`.

## Change 2 — Menu ordering + grouping

Target order (top → bottom):
1. Edit … Properties
2. Change to Trace …
3. *(grouped toggles, NO separators between them, in this order)*: **VSWR** (if available), **Snap to Point** (contour only), **Show Info Box**
4. Separator
5. **Remove … Marker** — always last

Rewrite the tail of `PopulateMarkerMenu` so Remove is built but added **last**. Concretely:

```csharp
menu.Items.Add(editItem);
menu.Items.Add(changeItem);

// ---- Grouped toggles (no separators between) ----
// VSWR (only when available)
if (PlotRenderer.VswrAvailableFor(hostPlot, trace, marker))
{
    // ...vswrItem as in Change 1...
    menu.Items.Add(vswrItem);
}
// Snap to Point (contour markers only)
if (marker.MarkerKind == MarkerKind.Contour)
{
    // ...snapItem (unchanged logic)...
    menu.Items.Add(snapItem);
}
// Show Info Box (always)
// ...showBoxItem (unchanged logic)...
menu.Items.Add(showBoxItem);

// ---- Remove — always last, separator above ----
menu.Items.Add(new Separator());
menu.Items.Add(removeItem);
```

Remove the old separators that previously surrounded Snap/ShowInfoBox and the old early Remove placement. There should be exactly **one** separator in the menu: the one directly above Remove. (Edit / Change / the toggle group sit together with no separators; only Remove is fenced off.)

## Change 3 — Clip the VSWR locus to the plot region

In `PlotRenderer.Draw`, the Full-detail marker block currently clips to the whole control:
```csharp
canvas.Save();
canvas.ClipRect(new SKRect(0, 0, (float)canvasSize.W, (float)canvasSize.H));
foreach (var trace in plot.Traces)
    foreach (var marker in trace.Markers) { …locus…; DrawSymbol(…); }
canvas.Restore();
```

The **glyph** (triangle / ringed circle / InfoBox leader) should keep drawing across the whole control (glyphs near the edge are fine). Only the **locus** must be clipped to the plot viewport so it can't spill outside the plot rectangle (corners of the Smith disk are acceptable — we clip to the rectangular viewport, not the Smith circle).

Wrap **only the locus call** in its own viewport clip:

```csharp
var viewportClip = ViewportClipRect(tf.Viewport, canvasSize);
foreach (var trace in plot.Traces)
    foreach (var marker in trace.Markers)
    {
        if (marker.VswrEnabled && VswrAvailableFor(plot, trace, marker))
        {
            var vplane = plot.PlotType is PlotType.Smith or PlotType.Polar
                ? SurfacePlane.Gamma : SurfacePlane.Z;
            var z0Ref = trace.Z0 == System.Numerics.Complex.Zero
                ? new System.Numerics.Complex(50.0, 0.0) : trace.Z0;

            canvas.Save();
            canvas.ClipRect(viewportClip);
            MarkerRenderer.DrawVswrLocus(canvas, canvasSize, marker, trace, tf, vplane, z0Ref);
            canvas.Restore();
        }
        MarkerRenderer.DrawSymbol(canvas, canvasSize, marker, trace, tf, theme,
            isSelected:     selectedMarkers?.Contains(marker) ?? false,
            selectionColor: selectionColor);
    }
```

(`ViewportClipRect` already exists in `PlotRenderer`. The glyph `DrawSymbol` stays outside the clip, unchanged.)

**Note:** the transient VSWR drag readout text is drawn after this loop; leave it unclipped so the readout near the pointer is never cut off.

## Out of scope
- No new VSWR math/drag (Gate 3). No editor changes (Brief D). No spectral changes (Brief C).
- Don't change the glyph clip or the selection-highlight.

## Acceptance / verification
1. Build green.
2. Right-click a Smith marker → menu shows: Edit, Change to Trace, **VSWR**, **Show Info Box**, ──, **Remove** (Remove last, one separator above it). Contour marker also shows **Snap to Point** between VSWR and Show Info Box.
3. Click **VSWR** → locus appears, menu header becomes `VSWR: 2` (or `2.000`); toggling again hides it.
4. Enlarge a VSWR circle near the plot edge (drag) → the red locus is **clipped at the plot boundary**, not spilling into the surrounding canvas; Smith-disk corners may show locus (acceptable).
5. A Rect (non-contour) marker's menu shows **no** VSWR item.

## Report back
- Confirm build green and the new menu order (Remove last, single separator, grouped toggles).
- State whether VSWR header uses `0.###` (trim zeros) or `F3` (fixed 3dp).
- Confirm the locus is clipped to the viewport and the glyph/readout are not.
