# Brief — harmonicaRF R9B: the Appearance tab becomes circuitRF's Color Theme layout

**Read first, in this order:**
`src/Ui/Views/Dialogs/SettingsView.axaml:162–284` — **this is the layout being copied; read it before
writing a line** — and its code-behind `SettingsView.axaml.cs:272–330` (the role list and
`RoleRowModel` population), `:336–472` (sliders, the RGBA boxes, the hex field, `ApplyRgbaToActiveRole`),
`:570–610` (`OnRoleDoubleTapped` — **the double-click-a-swatch gesture the owner named** — and the
namespace-level `RoleRowModel` class),
`src/Ui/Views/Dialogs/HarmonicaAppearanceSettingsView.axaml` (all 58 lines — what is being replaced)
and its `.axaml.cs` (all 199 lines),
`src/Ui/Harmonica/HarmonicaColorEditor.cs` (all of it — the model this view renders),
`src/Ui/Views/Dialogs/ColorPickerDialog.axaml.cs` (24 lines),
`src/Ui/Views/Dialogs/HarmonicaSettingsDialog.axaml` (the host).

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` only. **No screenshot verification.**

Tag new comments `R9B`.

---

## 1. What the owner asked for

> "The layout of the UI for the Appearance Settings for harmonicaRF color options needs to be the same
> as the Color Theme UI layout of circuitRF. Note that circuitRF allows user to double click on a
> rectangle color sample to show a Color picker. Use that. Mimic the circuitRF Color Theme UI layout
> as much as possible to be consistent for the user."

So this is a **layout and gesture** change, not a model change. `HarmonicaColorEditor` is the model and
does not move; `Rgba`, `ColorVariant`, `ColorThemeIo` and `ColorPickerDialog` are all already shared.

---

## 2. The one structural difference that must NOT be copied

circuitRF's Color Theme tab edits **working copies** (`_workingLight`/`_workingDark`), previews through
`ThemeService`, and commits or discards at the footer's Close/Cancel — it also carries a theme-NAME
combo, `Save Theme…`, and `ForkToCustomIfNeeded`.

harmonicaRF has none of that, deliberately and for a stated reason (`HarmonicaColorEditor`'s own header,
R-h7-15): **it runs with no workspace open and ships standalone, so a theme name plus a search path has
nothing to resolve against.** The `.charm` stores the resolved map for both variants, and every edit is
written through `HarmonicaColorEditor.Set` immediately — which is also what makes live preview free
(R-h7-16: nothing on that path can reach a `ContourGrid`, a `HarmonicaContext` or a scheduler).

**Therefore:**

- **Do not** add a theme combo, a `Save Theme…` button, `ForkToCustomIfNeeded`, `ThemeService`
  preview, or working-copy dictionaries to this view.
- **Do not** make the harmonicaRF edits deferred/cancellable. Every write still goes straight through
  `_editor.Set(role, Variant, colour)`, exactly as today.
- The `Import .ccolor…` / `Export .ccolor…` / `Reset All Colours` row **keeps its place** — it is
  harmonicaRF's answer to the theme combo, and the owner did not ask for it to go.

Say all of this in a comment at the top of the new `.axaml`, so the next person does not "finish the
parity job" by adding a theme combo that cannot work here.

---

## 3. The new `HarmonicaAppearanceSettingsView.axaml`

Transcribe `SettingsView.axaml:164–283`'s shape, with harmonicaRF's own header/footer rows around it.

### 3.1 Row 0 — the variant row

Keep today's `LightRadio`/`DarkRadio` pair and its `IsCheckedChanged="OnVariantChanged"` wiring, but
lay it out like circuitRF's: a `DockPanel` with the radios `DockPanel.Dock="Right"`,
`FontSize="12"`, `Spacing="4"`. The left half of that `DockPanel` carries the section label
(`"Colours"` or similar at `FontSize="12"`) instead of circuitRF's theme combo — the slot exists in the
copied layout and an empty one reads as a mistake.

### 3.2 Row 1 — `ColumnDefinitions="200,10,*"`, the two panes

**Left: the role list, with a colour swatch per row.** This is the half the owner is actually asking
for and today's view does not have it at all (its `ListBox` shows bare label strings). Copy
`SettingsView.axaml:189–214` verbatim, changing only the `x:DataType`:

```xml
<Border Grid.Column="0" BorderBrush="{DynamicResource ThemeBorderMidBrush}"
        BorderThickness="1" CornerRadius="4" ClipToBounds="True">
  <ListBox x:Name="RoleList" SelectionMode="Single"
           SelectionChanged="OnRoleSelected" DoubleTapped="OnRoleDoubleTapped"
           FontSize="11.5" Padding="2"
           ToolTip.Tip="Double-click a colour sample to open the picker. Right-click a role to reset
                        just that one, in both variants.">
    <ListBox.ItemTemplate>
      <DataTemplate x:DataType="dialogs:RoleRowModel">
        <StackPanel Orientation="Horizontal" Spacing="7" Margin="2,1">
          <Rectangle Width="14" Height="14" RadiusX="2" RadiusY="2" VerticalAlignment="Center">
            <Rectangle.Fill><SolidColorBrush Color="{Binding SwatchColor}"/></Rectangle.Fill>
          </Rectangle>
          <TextBlock Text="{Binding Label}" VerticalAlignment="Center"/>
        </StackPanel>
      </DataTemplate>
    </ListBox.ItemTemplate>
  </ListBox>
</Border>
```

**`RoleRowModel` is reused, not re-declared.** `SettingsView.axaml.cs:592–610` already declares it
`internal sealed` at NAMESPACE level in `CircuitRF.Ui.Views.Dialogs` — the same namespace this view is
in — and its own comment says why ("Must be namespace-level (not nested) so XAML `DataTemplate`
`x:DataType` can reference it"). Add
`xmlns:dialogs="clr-namespace:CircuitRF.Ui.Views.Dialogs"` to this file's root and bind to it.
**Delete** the private `RoleRow` record at `HarmonicaAppearanceSettingsView.axaml.cs:49–52` — two row
models for one list is exactly the drift this reuse avoids.

**Right: the editor pane**, `SettingsView.axaml:217–279` transcribed as-is:

1. the 40-px preview `Border`/`Rectangle` named `ColorPreviewRect` — **note the type changes from
   `Border` to `Rectangle`**, so the code-behind writes `.Fill`, not `.Background`;
2. the RGBA block: `ColumnDefinitions="14,*,46"`, four rows of
   `TextBlock` / `Slider (0–255, snap-to-tick)` / monospace `TextBox`, named `SliderR..A` and
   `LabelR..A`, wired to `OnSliderChanged` / `OnRgbaBoxLostFocus` / `OnRgbaBoxKeyDown`;
3. the hex row: a `#` label and `HexBox` (`Width="88"`, `MaxLength="8"`, monospace), wired to the
   EXISTING `OnHexLostFocus` / `OnHexKeyDown`;
4. `RoleNameLabel` at `FontSize="10.5" Opacity="0.5"` — the role path, which is where today's
   `"  (edited)"` suffix keeps living.

### 3.3 Row 2 — the footer

Unchanged from today: `Import .ccolor…`, `Export .ccolor…`, `Reset All Colours`, `StatusLabel`. Add
`Reset This Role` here too — it currently sits beside the hex field, and the copied editor pane has no
slot for it. `PickButton` ("Pick…") **goes away**: double-clicking a swatch is the gesture now, which
is the owner's own instruction, and a button that duplicates it is the second way to do one thing.

---

## 4. The code-behind

`HarmonicaAppearanceSettingsView.axaml.cs`. **Everything that talks to `HarmonicaColorEditor` stays;
everything that renders gets replaced.**

### 4.1 Keep, unchanged

`Attach`, `Variant`, `SelectedRole`, `OnVariantChanged`, `OnHexLostFocus`, `OnHexKeyDown`,
`ParseAndApplyHex`, `OnRevertClick`, `OnResetAllClick`, `OnImportClick`, `OnExportClick`, and the
`_updating` guard. The hex field's key handling in particular is load-bearing and its comment says why
(Return must set `e.Handled` or the window's default button closes the dialog) — **do not rewrite it to
match `SettingsView`'s copy of the same fix.** They are two instances of one inherited lesson.

### 4.2 Replace

**`PopulateRoles`** builds `List<RoleRowModel>` instead of `RoleRow`, with
`Role = r`, `Label = HarmonicaColorEditor.LabelFor(r)`, and
`SwatchColor` from `_editor.Resolve(r, Variant)`. Keep the "restore the previous selection index"
behaviour it already has.

**A new `RefreshAllSwatches()`**, the counterpart of `SettingsView.axaml.cs:317–323`: re-reads every
row's `SwatchColor` for the CURRENT variant. Call it from `OnVariantChanged` — without it, flipping
Light/Dark leaves the whole list showing the other variant's colours, which is a bug circuitRF already
has the fix for.

**`RefreshEditor`** writes the sliders, the four boxes, `ColorPreviewRect.Fill`, `HexBox.Text`,
`RoleNameLabel.Text` (role path + `"  (edited)"` when `_editor.IsOverridden(role, Variant)`) and the
two buttons' enablement — all inside the existing `_updating` window, since writing a `Slider.Value`
raises `ValueChanged`.

**New: `OnSliderChanged` / `OnRgbaBoxLostFocus` / `OnRgbaBoxKeyDown` / `ApplyRgbaBox` / `RevertBox` /
`BoxToSlider` / `ApplyCurrentSliders`** — transcribed from `SettingsView.axaml.cs:357–455`, with one
substitution: `ApplyRgbaToActiveRole`'s body becomes

```csharp
private void ApplyRgbaToActiveRole(Rgba c)
{
    if (SelectedRole is not { } role) return;
    _editor.Set(role, Variant, c);                       // live, immediate — §2
    if (RoleList.SelectedItem is RoleRowModel row) row.SwatchColor = ToAvaloniaColor(c);
    // RoleNameLabel's "(edited)" suffix and RevertButton's enablement both move with this.
    RefreshRoleStateLabels();
}
```

No `ForkToCustomIfNeeded`, no `PushLivePreview` — §2.

**New: `OnRoleDoubleTapped`** — transcribed from `SettingsView.axaml.cs:570–583`:

```csharp
private async void OnRoleDoubleTapped(object? sender, TappedEventArgs e)
{
    if (SelectedRole is not { } role) return;
    if (TopLevel.GetTopLevel(this) is not Window owner) return;

    // ColorPickerDialog already carries the ColorView Fluent-theme include §7.9.4 warns about —
    // ColorView instantiates BLANK without it, and fails silently.
    var picked = await new ColorPickerDialog(_editor.Resolve(role, Variant)).ShowDialog<Rgba?>(owner);
    if (picked is { } c) { SetSlidersFromRgba(c); ApplyRgbaToActiveRole(c); }
}
```

**Delete `OnPickClick`** with its button.

### 4.3 The trap worth naming

`ListBox.DoubleTapped` fires for a double-click anywhere in the row, not only on the 14-px rectangle —
that is true in `SettingsView` today and is the behaviour being copied. Do not add a hit test to narrow
it to the swatch; the owner's wording ("double click on a rectangle color sample") describes where the
affordance reads from, and a row-wide target is strictly easier to hit.

---

## 5. Standalone harmonicaRF still needs the ColorView theme include

`HarmonicaAppearanceSettingsView.axaml`'s existing header records this and it stays true: **the
standalone harmonicaRF entry point has its own `Application.Styles` and must carry the same ColorView
Fluent-theme include the main app does, or `ColorPickerDialog` renders blank — silently.** Carry that
paragraph across to the new file verbatim; this brief makes the picker the ONLY way to reach a colour
wheel, so the failure mode gets worse, not better.

Check `src/Ui/HarmonicaApp.axaml` for the include while you are here, and say in `RESOLVED.md` whether
it is present. Do not add unrelated styles.

---

## 6. Gate

`HarmonicaAppearanceSettingsView` is a `UserControl` and **cannot be constructed in `Ui.Tests`** — the
same limitation `HarmonicaSetTerminationDialogTests` records, and R8B §1.3's lesson applies: do not
write a hand-built simulation of the handlers and call it a test.

What IS testable, and what the gate must be:

- **`tests/Ui.Tests/Harmonica/HarmonicaColorEditorTests.cs`** (exists) — unchanged behaviour, so it must
  stay green with no edits. If you find yourself changing it, you have changed the model.
- **A source-scan test** over `HarmonicaAppearanceSettingsView.axaml` (comments stripped) asserting the
  layout parity the owner asked for, each as a named assertion so a failure says which part regressed:
  `DoubleTapped="OnRoleDoubleTapped"` present; `SliderR`/`SliderG`/`SliderB`/`SliderA` present;
  `LabelR`..`LabelA` present; `HexBox` present; a `Rectangle` bound to `SwatchColor` inside the
  `ListBox.ItemTemplate`; `PickButton` absent; and `ThemeCombo`/`SaveThemeButton` absent (§2's
  refusal, pinned rather than merely written down).
- **A second source-scan** asserting `SettingsView.axaml` still carries `DoubleTapped="OnRoleDoubleTapped"`
  and the `SwatchColor` binding — this brief copies from it, and a silent divergence there is what
  "consistent for the user" eventually loses to.

Then:

```
dotnet build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Write the outcome to `src/Ui/RESOLVED.md`. **No `CLAUDE.md` edits.**
