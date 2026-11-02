# Sonnet Brief — Phase 7.1d-2 (follow-up): make the Plot Inspector width-flexible (430 = max)

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-2. Owner feedback: in the Properties pane the inspector is
too wide. Make it **shrink to fit its host, with the current 430 px as the MAX** (in both the Properties pane
and the in-document flyout/docked inspector). The data-source combo and the sliders (the `*` columns) should get
narrower; Freq should move left as it narrows; the plot-type header must stay centered at any width. Three small
XAML edits — no VM changes, no inspector redesign.

## Why this works (one root change + two host fixes)
The inspector is currently pinned to `Width="430"`, which is what stops it shrinking. Switching to a **MaxWidth**
+ stretch makes everything the owner asked for fall out automatically: the `*` columns (signal combo;
NUD+slider) shrink; the right-aligned Freq group (Row 2 `*` pushes it right) slides left as the panel narrows;
and the plot-type header (`HorizontalAlignment="Center"`) stays centered within whatever width the host gives.

## 1. `PlotInspectorView.axaml` — flexible width
On the root `UserControl`, change `Width="430"` → **`MaxWidth="430"`** (leave `HorizontalAlignment` at its
default `Stretch`). Nothing else in this file needs to change — the existing centered header, the `*` columns,
and the right-aligned Freq group all respond correctly once the control can stretch/shrink.

## 2. `Views/DataDisplay/DataDisplayView.axaml` — keep the flyout at 430 (its max)
The docked/flyout inspector lives in an **`Auto`** column, so with `MaxWidth` (and no fixed `Width`) the control
would collapse to its minimum content width. Pin it to 430 here so the flyout looks exactly as it does today:
on the inner host border
`<Border IsVisible="{Binding ViewModel.Window.HasSingleSelection}">` add **`Width="430"`**:
```xml
<Border IsVisible="{Binding ViewModel.Window.HasSingleSelection}" Width="430">
    <v:PlotInspectorView DataContext="{Binding ViewModel.Window.ActiveInspector}"/>
</Border>
```

## 3. `Views/Properties/PropertiesView.axaml` — let it shrink to the dock
The 7.1d-2 host wraps the inspector in a `ScrollViewer` with `HorizontalScrollBarVisibility="Auto"`, which gives
the control unbounded width → it renders at 430 and scrolls instead of shrinking. Change the **horizontal**
scrollbar to `Disabled` (keep vertical `Auto`) so the viewport constrains the inspector to the dock width and it
shrinks (down to the dock width, capped at 430):
```xml
<ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
    <ddv:PlotInspectorView DataContext="{Binding PlotInspectorVm}"/>
</ScrollViewer>
```

## Gate (verify in the running app)
1. **Properties pane:** the inspector now fills the dock and is **narrower than 430** at the dock's default
   width — the data-source combo and sliders are visibly tighter; Freq sits further left; the plot-type header
   is centered. Widening the Properties dock grows the inspector up to **430 max** (no wider), header still
   centered.
2. **Flyout / docked inspector** (in the Data Display document) is **unchanged** — renders at 430 as before.
3. Plot-type header stays centered at every width; nothing clips at the dock's practical width (if an extreme-
   narrow dock collides the fixed columns, that's the practical minimum — the user can widen; flag it if it
   looks bad at normal widths).
4. Builds green; live redraw and the dual-surface sync from 7.1d-2 still work.

## On completion
Note the width-flex follow-up under 7.1d-2 in `src/Ui/CLAUDE.md`; screenshot the inspector in a narrow Properties
dock and in the flyout side by side. Next: **7.1d-3** (marker editor polish).
