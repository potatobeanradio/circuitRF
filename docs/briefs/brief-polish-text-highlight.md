# Brief: polish-text-highlight — selection foreground color for all text boxes

**Goal.** Every text edit box in circuitRF (including the schematic inline edit box and the symbol
text-primitive editor) renders highlighted (selected) text using the Avalonia system color
`TextControlSelectionHighlightColor` via the `SelectionForegroundBrush` property.

Authority: laundry-list item "text-highlight". Size: **S**.

## Step 1 — global TextBox style

`src/Ui/App.axaml` already has a global `TextBox` style in `<Application.Styles>`:

```xml
<Style Selector="TextBox">
    <Setter Property="FontSize" Value="{DynamicResource FontSize}"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
</Style>
```

Add the selection-foreground setter:

```xml
<Style Selector="TextBox">
    <Setter Property="FontSize" Value="{DynamicResource FontSize}"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="SelectionForegroundBrush" Value="{DynamicResource TextControlSelectionHighlightColor}"/>
</Style>
```

This cascades to **all** standard `TextBox` instances app-wide.

**Verify the resource type.** `TextControlSelectionHighlightColor` must resolve to a *Brush* for a
`SelectionForegroundBrush` (IBrush) setter. In the Avalonia Fluent theme it is a `SolidColorBrush`
resource, so `{DynamicResource TextControlSelectionHighlightColor}` binds directly. If the build/run
shows a binding error indicating it resolved to a `Color`, switch to the brush form (or wrap in a
`SolidColorBrush`). Don't guess — confirm at runtime.

> Intent note: the user specified `SelectionForegroundBrush = TextControlSelectionHighlightColor`
> verbatim (the *foreground* of selected text). If the visible result looks wrong and they actually
> wanted the selection *background*, that's the `SelectionBrush` property — a one-line swap. Implement
> as written; flag if the result is clearly not what's intended.

## Step 2 — custom inline editors

The global style covers any plain `TextBox`. Confirm the two custom edit surfaces are standard
`TextBox`es and pick up the style (they will unless they set `SelectionForegroundBrush` themselves):

- **Schematic inline edit box** — the in-canvas editor used for net-label / component-label / net-name
  editing (look in `src/Ui/Controls/SchematicCanvas.cs` and `src/Ui/Schematic/SchematicOverlay.cs`;
  it's an Avalonia `TextBox` overlaid on the canvas).
- **Symbol editor text primitive editor** — the text-edit box in the symbol editor
  (`src/Ui/Controls/SymbolEditorCanvas.cs` / `SymbolEditorOverlay.cs`).

If either is a code-instantiated `TextBox` that the App-level style doesn't reach (e.g. it sets its
own brushes, or lives outside the styled visual tree), set `SelectionForegroundBrush` on it directly
to the same `TextControlSelectionHighlightColor` resource. If both are normal `TextBox`es in the
styled tree, no extra work — note that in the PR.

## Acceptance

- Selecting text in any properties/parameter field, dialog text box, the schematic inline editor,
  and the symbol text editor shows the selected text in the system selection-highlight color.
- No binding errors in the run log referencing `SelectionForegroundBrush` /
  `TextControlSelectionHighlightColor`.
