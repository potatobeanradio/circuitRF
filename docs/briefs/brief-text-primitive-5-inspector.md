# Brief: text-primitive (5/5) — inspector controls (VAlign, Rotation, ForceReadable)

Final brief of the 5-brief Text sequence (1–4 landed). Adds three controls to the Properties inspector's
text section so the author can set the vertical anchor (`VAlign`), the literal `Rotation`, and the
per-text `ForceReadable` flag. Purely additive — mirrors the existing `Align` ComboBox and `Filled`
CheckBox patterns exactly. No new commands; reuses `SetSymbolPrimitiveFieldCommand<T>`.

Size: **S**. Files: `SymbolPrimitiveInspectorViewModel.cs`, `SymbolPrimitiveInspectorView.axaml`.

## 1. `SymbolPrimitiveInspectorViewModel.cs`

### Static option arrays
Next to the existing `AlignOptions` line, add:
```csharp
    public static SymbolTextVAlign[] VAlignOptions   { get; } = Enum.GetValues<SymbolTextVAlign>();
    public static SymbolRotation[]   RotationOptions { get; } = Enum.GetValues<SymbolRotation>();
```

### Observable fields
In the "Text fields" region, after `_textAlign`, add:
```csharp
    [ObservableProperty] private SymbolTextVAlign _textVAlign;
    [ObservableProperty] private SymbolRotation   _textRotation;
    [ObservableProperty] private bool             _textForceReadable;
```

### Change partials
After `OnTextAlignChanged`, add three handlers in the same shape (guard `_isRefreshing`, require
`_prim is TextPrimitive tp` and `_vm`, skip no-ops):
```csharp
    partial void OnTextVAlignChanged(SymbolTextVAlign oldValue, SymbolTextVAlign newValue)
    {
        if (_isRefreshing || _prim is not TextPrimitive tp || _vm is null || oldValue == newValue) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<SymbolTextVAlign>(
            _vm.EditableSymbol, "VAlign", oldValue, newValue, v => tp.VAlign = v));
    }
    partial void OnTextRotationChanged(SymbolRotation oldValue, SymbolRotation newValue)
    {
        if (_isRefreshing || _prim is not TextPrimitive tp || _vm is null || oldValue == newValue) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<SymbolRotation>(
            _vm.EditableSymbol, "Rotation", oldValue, newValue, v => tp.Rotation = v));
    }
    partial void OnTextForceReadableChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _prim is not TextPrimitive tp || _vm is null || oldValue == newValue) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<bool>(
            _vm.EditableSymbol, "ForceReadable", oldValue, newValue, v => tp.ForceReadable = v));
    }
```

### Populate on selection
In `SetPrimView`, in `case TextPrimitive t:`, after the existing `TextAlign = t.Align;` line, add:
```csharp
                TextVAlign        = t.VAlign;
                TextRotation      = t.Rotation;
                TextForceReadable = t.ForceReadable;
```
(`HideAllGroups` already resets the whole text section via `IsTextPrimitive = false`; the three fields are
re-read here on every (re)selection, so no extra reset is needed.)

## 2. `SymbolPrimitiveInspectorView.axaml`

In the Text `<StackPanel IsVisible="{Binding IsTextPrimitive}">`, **after** the existing `Align` row's
closing `</Grid>` and **before** the `</StackPanel>`, add a VAlign combo, a Rotation combo, and a
ForceReadable checkbox (copy the Align-row / Filled-checkbox styling):
```xml
                    <Grid ColumnDefinitions="56,*">
                        <TextBlock Grid.Column="0" Text="VAlign" VerticalAlignment="Center" FontSize="11"
                                   Foreground="{DynamicResource SystemControlForegroundBaseMediumBrush}"
                                   ToolTip.Tip="Vertical anchor. With Align this is the (horizontal, vertical) anchor point used for snapping. Baseline = legacy text behaviour."/>
                        <ComboBox Grid.Column="1"
                                  ItemsSource="{x:Static vm:SymbolPrimitiveInspectorViewModel.VAlignOptions}"
                                  SelectedItem="{Binding TextVAlign, Mode=TwoWay}"
                                  HorizontalAlignment="Stretch"
                                  FontSize="11" Padding="4,2"/>
                    </Grid>

                    <Grid ColumnDefinitions="56,*">
                        <TextBlock Grid.Column="0" Text="Rotation" VerticalAlignment="Center" FontSize="11"
                                   Foreground="{DynamicResource SystemControlForegroundBaseMediumBrush}"
                                   ToolTip.Tip="Literal text rotation in the editor (R0/R90/R180/R270). The R key also steps this."/>
                        <ComboBox Grid.Column="1"
                                  ItemsSource="{x:Static vm:SymbolPrimitiveInspectorViewModel.RotationOptions}"
                                  SelectedItem="{Binding TextRotation, Mode=TwoWay}"
                                  HorizontalAlignment="Stretch"
                                  FontSize="11" Padding="4,2"/>
                    </Grid>

                    <CheckBox Content="Force readable"
                              IsChecked="{Binding TextForceReadable, Mode=TwoWay}"
                              FontSize="11" Padding="0,2"
                              ToolTip.Tip="When a schematic instance is rotated upside-down/back-to-front, auto-flip this text 180° so it stays right-way-up. Off = rotates rigidly with the symbol."/>
```

## Notes / decision

- **Two combos for the anchor, not a 3×3 grid.** Matches every other inspector row, zero new converters,
  minimal risk. The unified `(Align, VAlign)` 3×3 button picker is deferred as optional polish.
- All three route through `SetSymbolPrimitiveFieldCommand<T>` → undoable, live, consistent with the rest of
  the inspector.

## Verification (runtime)

1. Select a text primitive → inspector shows Content, AX/AY, Font size, Style, Align, **VAlign**,
   **Rotation**, **Force readable**.
2. Change **VAlign** → the text's vertical anchor moves (snap reference shifts); undo reverts.
3. Change **Rotation** → editor shows the text at that literal angle; the **R** key still steps it and the
   combo reflects the new value after rotating.
4. Toggle **Force readable** on a text used in a schematic cell, then rotate an *instance* of that cell
   upside-down → the text auto-flips to stay readable; off → it rotates rigidly. (Editor view always shows
   the literal authored rotation regardless.)
5. Non-text primitives and pins: section hidden, no regression.

## Acceptance

- VAlign, Rotation, ForceReadable are editable in the inspector, undoable, and persist on save/reload.
- Existing text rows unchanged; the 5-brief Text feature is complete.
