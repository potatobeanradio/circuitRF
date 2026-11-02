# Brief — Gate 6 Round 1 / A: persistence, polyline-drag crash, delete-key

**Status:** Ready to implement
**Scope:** Three high-severity bugs found in Gate 6 marker testing. All three are model/control-level and independent of each other. Fix all three in this brief.
**Depends on:** Gates 0–5 landed.

**Context (already root-caused — do not re-investigate):** I traced each bug to a specific line. Apply the fix; don't go re-deriving.

## UI build gate
UI builds with `TreatWarningsAsErrors=true`. Capture nullables into locals; no unused usings/fields.

---

## Bug 1 — Markers don't persist through save/reload

**Root cause:** in `DataDisplayViewModel.LoadPlotContainerConfigAsync`, the marker-restore loop runs **only inside the network-bound `else` branch**. The **contour**, **summary**, and **cube-bound (stem)** branches each `continue` *before* reaching the marker loop:
- contour branch ends with `plot.Traces.Add(trace); continue;`
- summary branch ends with `plot.Traces.Add(trace); continue;`
- cube-bound path ends with `if (isCubeBound) { plot.Traces.Add(trace); continue; } // markers not supported yet`

So markers on contour / stem / summary traces are never restored. (The save side, `BuildTraceConfig`, already writes markers for every trace — confirmed.)

**Fix:** restore markers for **every** trace kind. Factor the marker-restore loop into a local helper and call it before each `continue`, OR (cleaner) restructure so all branches fall through to a single shared marker-restore + `plot.Traces.Add(trace)` tail.

Recommended minimal-risk approach — a local function placed near the top of the trace loop body:

```csharp
void RestoreMarkers(Trace tr, TraceConfig tcfg)
{
    foreach (var mc in tcfg.Markers)
    {
        var marker = new Marker(tr, mc.Freq, mc.IsMulti, mc.IsDelta, mc.Index, mc.FreqUnits)
        {
            Name                  = mc.Name,
            MatrixFormat          = mc.MatrixFormat,
            Style                 = mc.Style,
            UseNormalizedImpedance= mc.UseNormalizedImpedance,
            MaximumFractionDigits = mc.MaximumFractionDigits,
            InfoBoxPos            = new Avalonia.Point(mc.InfoBoxX, mc.InfoBoxY),
            PositionStatic        = new System.Numerics.Vector2(mc.PositionStaticX, mc.PositionStaticY),
            MarkerKind            = mc.MarkerKind,
            ShowInfoBox           = mc.ShowInfoBox,
            ContourSnapped        = mc.ContourSnapped,
            VswrEnabled           = mc.VswrEnabled,
            VswrValue             = mc.VswrValue,
        };
        tr.Markers.Add(marker);
    }
}
```

Then:
- **Contour branch:** before its `continue`, call `RestoreMarkers(trace, traceConfig);`.
- **Summary branch:** before its `continue`, call `RestoreMarkers(trace, traceConfig);` (summary/table markers are type 4 — still restore them).
- **Cube-bound tail:** replace `if (isCubeBound) { plot.Traces.Add(trace); continue; } // markers not supported yet` with:
  ```csharp
  if (isCubeBound) { RestoreMarkers(trace, traceConfig); plot.Traces.Add(trace); continue; }
  ```
- **Network `else` branch:** replace its inline marker loop with `RestoreMarkers(trace, traceConfig);` (dedupe — same code).

**Important ordering note:** the contour/stem branches build their geometry differently. For **stem** markers, `PositionStatic.X` holds the harmonic X-value and the glyph is located by `GetMarkerDataLocation → StemPointFor` (matches by X against `Points`). `Points` for a cube trace are built by `PlotInspectorViewModel.TrySetCubeData(...)`, which the cube branch calls **before** the `continue`. Confirm `TrySetCubeData` runs before `RestoreMarkers` so `Points` exist when the InfoBox first measures. (It does in the current order: cube data is set, then the tail `continue`.) For **contour** markers, position is `PositionStatic` directly (no Points dependency), so order is moot.

**Verify:** add a contour marker, a stem marker, and an ordinary Smith S-param marker; Save (.cdd); close; reopen → all three markers + their InfoBoxes render in the right places. (Ordinary network markers already worked; the contour/stem ones are the fix.)

---

## Bug 2 — Polyline marker drag crashes (IndexOutOfRange)

**Crash:** `System.IndexOutOfRangeException` at `PlotControl.MoveMarkerToCanvasPoint` during `OnPointerMoved`.

**Root cause:** in the **non-stability `else` branch** of `MoveMarkerToCanvasPoint`:
```csharp
marker.Freq = trace.Data.Frequencies[hit.Value.FreqIndex];
```
`FindNearestTraceData` returns `FreqIndex` as an index into `trace.Points` (0..`Points.Count`-1). For a **cube-bound polyline** (a complex cube on a Smith/Polar plot — `MarkerKind.Polyline`, not stem, not contour), `Points` is populated from the cube but `trace.Data.Frequencies` is the placeholder `{1e9}` (length 1). So `Frequencies[FreqIndex]` with `FreqIndex > 0` throws. (Network polylines don't crash because there `Points.Count == Frequencies.Length`.)

**Fix:** only write `marker.Freq` from `Data.Frequencies` when the index is in range; for cube-bound traces, `Freq` is not the locator anyway (the glyph for a cube polyline is located via `Points`, and `GetMarkerDataLocation`'s cube branch returns `Vector2.Zero` for a plain cube polyline — see note below). Guard the write:

```csharp
else
{
    var (wx, wy) = trace.UseSecondaryAxis
        ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
        : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

    var hit = trace.FindNearestTraceData(new System.Numerics.Vector2((float)wx, (float)wy));
    if (!hit.HasValue) return;

    var snapped = tf.ToCanvas(hit.Value.NearestPoint.X, hit.Value.NearestPoint.Y, trace.UseSecondaryAxis);
    if (!clipRect.Contains(snapped.X, snapped.Y)) return;

    // Network traces: Freq locates the marker. Cube-bound polylines have no real
    // Frequencies array (placeholder length 1), so guard the index.
    var freqs = trace.Data.Frequencies;
    if (!trace.IsCubeBound && hit.Value.FreqIndex >= 0 && hit.Value.FreqIndex < freqs.Length)
        marker.Freq = freqs[hit.Value.FreqIndex];
}
```

**Note / open question for report-back:** a cube-bound **polyline** (complex cube on Smith) currently has `GetMarkerDataLocation → IsCubeBound → Vector2.Zero`, so even after this crash fix its glyph would sit at the origin and not track the drag. If the owner is actually dragging a *network* Smith polyline (not cube), the guard above is sufficient and correct. If he's dragging a *cube* Smith polyline, that's a separate "cube polyline markers aren't located" gap (out of scope here — the immediate bug is the crash). **In report-back, state which trace kind reproduced the crash** (network S-param Smith vs cube complex Smith) so we know whether a follow-up is needed. The guard fixes the crash either way.

**Verify:** add a marker to the Smith polyline that crashed; drag it — no crash; it snaps along the trace as before.

---

## Bug 3 — Delete key doesn't delete a selected marker

**Root cause:** `DataDisplayView.axaml` binds the Delete gesture to the wrong command:
```xml
<KeyBinding Gesture="Delete" Command="{Binding ViewModel.Window.RemovePlotCommand}"/>
```
`RemovePlotCommand → DataDisplay.RemoveSelected()` removes only selected **plots** (`_plots.Where(p => p.IsSelected)`). A selected marker InfoBox is never touched. The correct method — `DataDisplayViewModel.DeleteSelected()` — already exists and composites marker-removes + plot-removes into one undoable action, but **no command is wired to it**.

**Fix (two parts):**

### 3a. Add a Window-level `DeleteSelected` command (DisplayWindowViewModel)
```csharp
[RelayCommand(CanExecute = nameof(CanDeleteSelected))]
private void DeleteSelected() => DataDisplay?.DeleteSelected();
private bool CanDeleteSelected() => DataDisplay?.HasAnySelection ?? false;
```
And refresh its CanExecute alongside the others. In `OnActiveDisplayPropertyChanged`, where `HasAnySelection` already notifies `RemovePlotCommand`/`CutCommand`, add:
```csharp
DeleteSelectedCommand.NotifyCanExecuteChanged();
```
Also add `DeleteSelectedCommand.NotifyCanExecuteChanged();` in `OnActiveTabChanged` next to the other `NotifyCanExecuteChanged()` calls.

### 3b. Rebind the Delete gesture (DataDisplayView.axaml)
```xml
<KeyBinding Gesture="Delete" Command="{Binding ViewModel.Window.DeleteSelectedCommand}"/>
```
(Leave `Ctrl+Shift+D` / `Meta+Shift+D` bound to `RemovePlotCommand` if you want a plot-only delete; or repoint them to `DeleteSelectedCommand` too — the owner's call. Default: repoint Delete only, leave the Ctrl+Shift+D variants on RemovePlot.)

**Why this is correct:** `DeleteSelected()` removes selected markers (each via `RemoveMarkerCommand`) **and** selected plots (via `RemovePlotsCommand`) as one `CompositeCommand`, so a selected marker is deleted, a selected plot is deleted, and a mixed selection is deleted together — all undoable.

**Verify:** select a marker InfoBox (single click) → press Delete → marker + its InfoBox removed; Ctrl+Z restores it. Select a plot → Delete still removes the plot. Select both → Delete removes both, one undo restores both.

---

## Out of scope (this brief)
- Context-menu VSWR item / ordering / locus clipping → Brief B.
- Spectral InfoBox content → Brief C.
- MarkerEditorView changes → Brief D.

## Report back
- Confirm build green.
- Bug 1: contour + stem + network markers all persist across save/reload.
- Bug 2: polyline drag no longer crashes; **state which trace kind reproduced it** (network Smith S-param vs cube complex Smith).
- Bug 3: Delete removes a selected marker and is undoable; plot-delete still works.
