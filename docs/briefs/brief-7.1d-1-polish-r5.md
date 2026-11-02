# Sonnet Brief — Phase 7.1d-1 (polish Round 5): fix clipped slider thumb

**File:** `src/Ui/Views/DataDisplay/PlotInspectorView.axaml` — the `Slider` style only. One fix.

## Problem
The two trace-card sliders sit low in their row and the bottom half of the thumb circle is clipped. **Cause:**
the inspector's `Slider` style forces `Height="20"`. Avalonia's Fluent `Slider` template does **not** re-center
its track/thumb when the height is shrunk below its natural value — the track ends up near the bottom of the
20 px box, so the thumb (centered on the track) spills past the bottom edge and is clipped. (R4 also removed the
old `TranslateTransform` that had been hiding this.)

## Fix
Let the slider keep its natural, thumb-centered height and pull the row back tight with a **negative vertical
margin** instead of a forced small height:

```xml
<Style Selector="Slider">
    <!-- Remove the explicit small Height so the Fluent template centers the track+thumb. -->
    <Setter Property="Margin"              Value="2,-7"/>   <!-- negative top/bottom keeps the row tight -->
    <Setter Property="VerticalAlignment"   Value="Center"/>
    <Setter Property="IsSnapToTickEnabled" Value="True"/>
    <Setter Property="TickPlacement"       Value="None"/>
    <Setter Property="TickFrequency"       Value="0.1"/>
</Style>
```
- **Drop the `Height="20"` setter** (let the slider be its natural height so the thumb is centered and fully
  drawn). The **negative vertical margin** (`2,-7` → start here, tune) shrinks the slider's layout footprint so
  the line/symbol rows stay as tight as they are now.
- Add `ClipToBounds="False"` to the **nested col-1 `Grid`** (`ColumnDefinitions="30,*"`) in both the line and
  symbol rows, so nothing clips the thumb if it slightly overhangs. (The outer row grids already have
  `ClipToBounds="False"`.)

## Tuning (verify in the running app)
Adjust the negative margin (e.g. `-6` to `-8`) until: the thumb circle is **fully visible and vertically
centered** on the track, and the line/symbol rows are the **same height** as the identity and Z0 rows (no taller
than before). The slider track line should sit at the row's vertical center.

## Gate
Slider thumbs render fully (no clipping), centered on the track; line/symbol row heights unchanged from the
current (good) look; sliders still drag and update width/size live.

## On completion
Note "Phase 7.1d-1 polish R5 — COMPLETE" in `src/Ui/CLAUDE.md`; screenshot for owner. This should close out the
7.1d-1 inspector look — next is **7.1d-2** (Properties-dock surface).
