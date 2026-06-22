# Brief — Markers Gate 5: Marker Properties editor (VSWR, contour mode, ShowInfoBox)

**Status:** Ready to implement
**Scope:** Surface the marker state added in Gates 0–3 in the UI: a **VSWR enable** checkbox + **VSWR value** field, a **contour mode** toggle (Mode 1 free / Mode 2 snapped), and a **`ShowInfoBox`** toggle. Wire `ShowInfoBox=false` to suppress the InfoBox render + selectability while leaving the glyph (and any VSWR locus) intact. This gate is what lets a user **turn VSWR circles on without a hardcode** — Gate 3's payoff.
**Design ref:** `/docs/design/trace-markers-design.md` §7 (ShowInfoBox), §8 (editor), §6.5 (VSWR value range), D6/D7. Read those first.
**Depends on:** Gates 0–3 landed. Gate 0 added all the marker fields; Gate 2b added the "Snap to Point" context item + `ContourSnapped` re-snap; Gate 3 draws the VSWR locus from `VswrEnabled`/`VswrValue`.

---

## Context (already verified — do not re-investigate)

- **Editor VM:** `MarkerEditorViewModel.cs` — `[ObservableProperty]` + `partial void On…Changed` writing to `_marker.<Field>` then `NotifyParent()`, guarded by `MarkerIsLive`. `_parent` is a `MarkerInfoBoxViewModel` (null at design time — guard with `_parent is not null`). `_parent.PlotType` gives the host plot type. `_parent.Container.RequestPlotRedraw()` invalidates the plot canvas (used by `OnIsMultiChanged`).
- **Editor view:** `MarkerEditorView.axaml` — compact controls with styles `seg-btn` (ToggleButton), `label` (TextBlock), compact `TextBox`/`NumericUpDown`. Existing gated rows use `IsVisible="{Binding ShowMultiDeltaControls}"` / `{Binding ShowFormatSelector}` patterns.
- **Serialization is already done** (Gate 0): `MarkerConfig` round-trips `ShowInfoBox`, `VswrEnabled`, `VswrValue`, `ContourSnapped`, `MarkerKind` in both `DataDisplayViewModel.BuildTraceConfig` (save) and `LoadPlotContainerConfigAsync` (load). **Do not touch serialization.**
- **InfoBox lifecycle:** `DataDisplayViewModel._markerInfoBoxes` is the `ObservableCollection<MarkerInfoBoxViewModel>` the view binds to and every selection path iterates. It is rebuilt per container in `RebuildMarkerInfoBoxesForContainer`, which loops `trace.Markers` and creates one VM per marker. The **glyph** is drawn separately by `PlotRenderer` looping `trace.Markers` (NOT the InfoBox VMs), so removing an InfoBox VM hides the box but keeps the glyph.
- **Context menu:** `MarkerInfoBoxView.PopulateMarkerMenu` (static) builds the shared marker menu; Gate 2b added the "Snap to Point" contour item there with an optional `onContourModeToggled` callback. The `VswrAvailableFor`/domain gate lives in `PlotRenderer` (Gate 3a; ideally `internal static`).

## UI build gate

UI builds with `TreatWarningsAsErrors=true`. Capture nullable into locals; no unused usings/fields. New `[ObservableProperty]` backing fields are referenced by generated props (no warning).

---

## Task 1 — `ShowInfoBox` lifecycle (DataDisplayViewModel)

Make `ShowInfoBox=false` mean "no InfoBox VM" — the cleanest suppression (hides render AND drops it from every selection path, since they all iterate `_markerInfoBoxes`).

In `RebuildMarkerInfoBoxesForContainer`, skip markers whose box is hidden:

```csharp
foreach (var trace in plot.Traces)
{
    foreach (var marker in trace.Markers)
    {
        if (!marker.ShowInfoBox) continue;   // NEW — no box, no selectable entry; glyph still drawn by PlotRenderer
        if (double.IsNaN(marker.InfoBoxPos.X))
            PlaceInfoBoxInLogicalCoords(marker, trace, plot, container);
        ...
    }
}
```

That's the whole suppression. The glyph + VSWR locus are unaffected (separate render path). Toggling `ShowInfoBox` back on must trigger a rebuild so the box reappears — Task 3 handles the redraw/rebuild call.

## Task 2 — Editor controls (VM)

Add to `MarkerEditorViewModel`:

### 2a. ShowInfoBox
```csharp
[ObservableProperty] private bool _showInfoBox;

partial void OnShowInfoBoxChanged(bool value)
{
    if (!MarkerIsLive) return;
    _marker.ShowInfoBox = value;
    NotifyParent();
    // Rebuild this container's info boxes so the box appears/disappears.
    _parent?.Container.RequestInfoBoxRebuild();   // see Task 3 for this method
}
```

### 2b. VSWR enable + value
```csharp
[ObservableProperty] private bool _vswrEnabled;

partial void OnVswrEnabledChanged(bool value)
{
    if (!MarkerIsLive) return;
    _marker.VswrEnabled = value;
    NotifyParent();
    _parent?.Container.RequestPlotRedraw();   // locus appears/disappears
}

// Buffered text entry (graceful invalid-input handling per §8). Committed on Enter/lost-focus.
[ObservableProperty] private string _vswrValueText = "2";

public void CommitVswrValue()
{
    if (!MarkerIsLive) return;
    if (double.TryParse(VswrValueText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.CurrentCulture, out double v))
    {
        _marker.VswrValue = v;                 // §6.5: unclamped, negatives allowed
        VswrValueText = v.ToString("G6");
        NotifyParent();
        _parent?.Container.RequestPlotRedraw();
    }
    else
    {
        // Reject invalid input — revert text to last valid model value (no crash, no state change).
        VswrValueText = _marker.VswrValue.ToString("G6");
    }
}
```

### 2c. Contour mode toggle (Mode 1 free vs Mode 2 snapped)
Bind a bool that mirrors `_marker.ContourSnapped`. Toggling re-snaps the position (same as the context-menu "Snap to Point" in Gate 2b) and redraws:

```csharp
[ObservableProperty] private bool _contourSnapped;

partial void OnContourSnappedChanged(bool value)
{
    if (!MarkerIsLive) return;
    _marker.ContourSnapped = value;
    // Re-resolve position so glyph + readout switch modes immediately (mirror Gate 2b menu item).
    _marker.PositionStatic = _parent!.Trace.ResolveContourMarkerPosition(_marker, _marker.PositionStatic);
    NotifyParent();
    _parent.Container.RequestPlotRedraw();
}
```

### 2d. Visibility gates (computed props)
```csharp
/// <summary>VSWR controls show only when the marker has a Z/Γ value (§6.1).
/// Reuse the renderer's single-source gate.</summary>
public bool ShowVswrControls =>
    _parent is not null &&
    CircuitRF.Ui.DataDisplay.PlotRenderer.VswrAvailableFor(_parent.Container.PlotVM.Plot, _parent.Trace, _marker);

/// <summary>Contour mode toggle shows only for contour markers.</summary>
public bool ShowContourModeToggle => _parent is not null && _parent.Trace.IsContourTrace;
```

(If `PlotRenderer.VswrAvailableFor` is still `private` from Gate 3a, make it `internal static` now — single source of truth for the §6.1 gate. If Gate 3a never added it as a shared method, add it there.)

### 2e. Initialize the new fields in **both** constructors
In the design-time ctor and the `MarkerInfoBoxViewModel` ctor, seed:
```csharp
_showInfoBox    = marker.ShowInfoBox;     // or _marker in the parent ctor
_vswrEnabled    = marker.VswrEnabled;
_vswrValueText  = marker.VswrValue.ToString("G6");
_contourSnapped = marker.ContourSnapped;
```
(Use `#pragma warning disable MVVMTK0034` around direct backing-field writes if the file already does — match the existing `_freqDisplayText` pattern.)

## Task 3 — `PlotContainerViewModel.RequestInfoBoxRebuild`

The editor needs to ask the display to rebuild this container's info boxes (so a `ShowInfoBox` toggle adds/removes the box). Add a thin pass-through on `PlotContainerViewModel` that calls the existing `DataDisplayViewModel.OnContainerPlotChanged(this)` (which calls `RebuildMarkerInfoBoxesForContainer`). If a similar method already exists (e.g. the path `MarkerAdded` uses), reuse it instead of adding a new one. Confirm the name in report-back.

```csharp
// PlotContainerViewModel
public void RequestInfoBoxRebuild() => _dataDisplay.OnContainerPlotChanged(this);
```
(Use whatever the container's field/ref to the DataDisplayViewModel is — match how `RequestPlotRedraw` reaches it.)

## Task 4 — Editor view (AXAML)

Add controls to `MarkerEditorView.axaml`, using the existing styles. Place them after the Multi/Δ row.

```xml
<!-- Show Info Box + VSWR enable -->
<StackPanel Orientation="Horizontal" Spacing="4">
    <ToggleButton Classes="seg-btn"
                  IsChecked="{Binding ShowInfoBox, Mode=TwoWay}"
                  ToolTip.Tip="Show this marker's info box">
        Info Box
    </ToggleButton>
    <ToggleButton Classes="seg-btn"
                  IsChecked="{Binding VswrEnabled, Mode=TwoWay}"
                  IsVisible="{Binding ShowVswrControls}"
                  ToolTip.Tip="Draw a constant-VSWR circle around this marker">
        VSWR
    </ToggleButton>
</StackPanel>

<!-- VSWR value (only meaningful when VSWR available) -->
<StackPanel Spacing="3" IsVisible="{Binding ShowVswrControls}">
    <TextBlock Text="VSWR value" Classes="label"/>
    <TextBox x:Name="VswrValueTextBox"
             Text="{Binding VswrValueText}"
             Width="80" HorizontalAlignment="Left"
             KeyDown="OnVswrValueKeyDown"/>
</StackPanel>

<!-- Contour mode: Snap to grid point (Mode 2) vs free/interpolated (Mode 1) -->
<ToggleButton Classes="seg-btn"
              IsChecked="{Binding ContourSnapped, Mode=TwoWay}"
              IsVisible="{Binding ShowContourModeToggle}"
              ToolTip.Tip="Snap to grid point (off = free / interpolated)">
    Snap to Point
</ToggleButton>
```

In `MarkerEditorView.axaml.cs`, add the VSWR commit handler (mirror the existing `OnFreqTextBoxKeyDown`):
```csharp
private void OnVswrValueKeyDown(object? sender, KeyEventArgs e)
{
    if (e.Key == Key.Enter && DataContext is MarkerEditorViewModel vm)
    {
        vm.CommitVswrValue();
        e.Handled = true;
    }
}
```
Also commit on lost-focus if the existing freq box does (match the established pattern; if freq only commits on Enter, do the same for parity).

## Task 5 — "Show Info Box" on the glyph context menu (re-enable path)

**Critical UX wrinkle:** once `ShowInfoBox=false`, the box (and its double-tap editor) is gone, so the user can't reopen the editor *through the box*. They must be able to re-enable it from the **glyph**. The glyph right-click menu's "Edit Properties" is disabled when there's no InfoBox VM (`openEditor` is null). So add a **"Show Info Box" checkbox item** to `PopulateMarkerMenu` (always present), mirroring the Gate 2b "Snap to Point" item:

```csharp
// In PopulateMarkerMenu, add an optional callback param (like onContourModeToggled):
//   Action? onShowInfoBoxToggled = null
var showBoxItem = new MenuItem
{
    Header = "Show Info Box",
    Icon   = new MaterialIcon
    {
        Kind = marker.ShowInfoBox ? MaterialIconKind.CheckboxOutline
                                  : MaterialIconKind.CheckboxBlankOutline,
    },
};
showBoxItem.Click += (_, _) =>
{
    marker.ShowInfoBox = !marker.ShowInfoBox;
    onShowInfoBoxToggled?.Invoke();
};
menu.Items.Add(showBoxItem);
```

Wire the callback at both call sites so the box rebuilds:
- `MarkerInfoBoxView.RebuildContextMenu`: `onShowInfoBoxToggled: () => Vm?.Container.RequestInfoBoxRebuild()`.
- `PlotControl.ShowMarkerContextMenu`: `onShowInfoBoxToggled: () => { ContainerProvider?.Invoke()?.RequestInfoBoxRebuild(); InvalidateVisual(); }`.

This makes the glyph menu the authoritative toggle that works whether or not a box currently exists.

## Out of scope (do NOT do in Gate 5)

- No serialization changes (Gate 0 did it).
- No new VSWR math or drag behavior (Gate 3 did it).
- No changes to contour value computation (Gate 2b) beyond calling `ResolveContourMarkerPosition` on mode toggle.
- Don't alter the glyph, selection-highlight, or the multi/delta/normalize/style controls.

## Acceptance / verification

1. **UI builds green** (warnings-as-errors).
2. **VSWR via editor:** on a Smith marker, open the editor → toggle **VSWR** on → the red circle appears (default 2:1). Type a new VSWR value, press Enter → circle resizes. Enter garbage ("abc") → reverts to last value, no crash. Drag the circle (Gate 3b) → value + readout update; reopening the editor shows the new value.
3. **VSWR gated:** the VSWR controls are **hidden** for a marker on a Cartesian Rect trace (no Z/Γ) and shown on Smith/contour markers.
4. **ShowInfoBox:** toggle **Info Box** off (editor or glyph menu) → the box disappears and is no longer selectable/marquee-selectable, but the **glyph (and VSWR circle) remain**. Right-click the glyph → **Show Info Box** (unchecked) → click it → box returns. Save/reload preserves the hidden/shown state.
5. **Contour mode:** on a contour marker, the editor's **Snap to Point** toggle switches glyph (ringed ↔ triangle) and readout (interp ↔ exact), staying in sync with the context-menu item.
6. Other marker types/controls unchanged.

## Report back

- Confirm build green; VSWR enable+value works from the editor (incl. invalid-input revert) and the circle draws.
- Confirm `ShowInfoBox=false` hides the box + removes selectability while keeping glyph/VSWR, and the glyph-menu "Show Info Box" re-enables it.
- Name the `PlotContainerViewModel` rebuild method you used (new `RequestInfoBoxRebuild` or an existing one).
- Confirm `PlotRenderer.VswrAvailableFor` is the single shared §6.1 gate (made `internal static` if needed).
