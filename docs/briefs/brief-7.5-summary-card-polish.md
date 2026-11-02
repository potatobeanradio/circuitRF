# Brief 7.5-fix — Summary-table card & header polish (bugs 7/8/9 + per-card Comp range)

**Phase:** 7.5 first-real-run polish. **Layer:** `src/Ui` only (AXAML + one VM bool + one tiny converter).
**No RfCore, no renderer changes** — bugs 1,2,3,4,6,10 are already direct-edited and landed.
**File (AXAML):** `circuitRF/src/Ui/Views/DataDisplay/PlotInspectorView.axaml`
**File (VM):**   `circuitRF/src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs`
**File (new converter):** `circuitRF/src/Ui/DataDisplay/Converters/EnumUpperConverter.cs`

Build gate after EACH item; UI/Core have **TreatWarningsAsErrors=true** (unused field → `_ = x;`,
nullable property warnings → capture to locals). The owner builds/runs.

---

## Item A — bug 8: "Optimum"→"Load" label + Mxp/Mxe shown as MXP/MXE

The table-wide controls StackPanel (visible when `IsSummaryTable`) currently has:
```xml
<TextBlock Text="Optimum" Classes="label"/>
<ComboBox ItemsSource="{x:Static vm:PlotInspectorViewModel.AllTableOptima}"
          SelectedItem="{Binding TableOptimum, Mode=TwoWay}" Width="64" .../>
```
Do NOT rename the `TableOptimum` enum (JsonStringEnumConverter persists it). Uppercase at the VIEW layer.

**A1. New converter** `EnumUpperConverter` (one-way):
```csharp
using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.DataDisplay.Converters;

/// <summary>Displays an enum as its upper-cased ToString(). View-only; never write-back.
/// Used so TableOptimum {Mxp,Mxe} renders as MXP/MXE without renaming the persisted enum.</summary>
public sealed class EnumUpperConverter : IValueConverter
{
    public static readonly EnumUpperConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToUpperInvariant();
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

**A2. AXAML** — add to `UserControl.Resources` (near other `cv:` converters at top):
```xml
<cv:EnumUpperConverter x:Key="EnumUpper"/>
```
Then change the Optimum block to:
```xml
<TextBlock Text="Load" Classes="label"/>
<ComboBox ItemsSource="{x:Static vm:PlotInspectorViewModel.AllTableOptima}"
          SelectedItem="{Binding TableOptimum, Mode=TwoWay}"
          Width="64"
          ToolTip.Tip="MXP = max power optimum; MXE = max efficiency optimum">
    <ComboBox.ItemTemplate>
        <DataTemplate>
            <TextBlock Text="{Binding Converter={StaticResource EnumUpper}}" FontSize="10"/>
        </DataTemplate>
    </ComboBox.ItemTemplate>
</ComboBox>
```
Avalonia ComboBox reuses ItemTemplate for the collapsed selection face, so it should show MXP/MXE there
too. Verify; if the face doesn't uppercase, the dropdown items doing so is acceptable.

---

## Item B — bug 9: "Read" combo → "Interp" checkbox

Replace:
```xml
<TextBlock Text="Read" Classes="label"/>
<ComboBox ItemsSource="{x:Static vm:PlotInspectorViewModel.AllTableReadModes}"
          SelectedItem="{Binding TableReadMode, Mode=TwoWay}" Width="72" .../>
```
with a checkbox bound to a new VM bool `IsInterp` (checked = Interp, unchecked = Nearest).

**B1. VM bool** in `PlotInspectorViewModel.cs`, near the `TableReadMode` ObservableProperty:
```csharp
/// <summary>Checkbox-friendly view of TableReadMode: true = Interp, false = Nearest.</summary>
public bool IsInterp
{
    get => TableReadMode == TableReadMode.Interp;
    set
    {
        var target = value ? TableReadMode.Interp : TableReadMode.Nearest;
        if (TableReadMode == target) return;
        TableReadMode = target;            // setter → OnTableReadModeChanged → RebuildSummary
        OnPropertyChanged();
    }
}
```
In the existing `partial void OnTableReadModeChanged(TableReadMode value)` body append:
```csharp
OnPropertyChanged(nameof(IsInterp));
```

**B2. AXAML** — replace the Read label+combo with:
```xml
<CheckBox Content="Interp"
          IsChecked="{Binding IsInterp, Mode=TwoWay}"
          FontSize="10"
          VerticalAlignment="Center"
          ToolTip.Tip="On = interpolated value; Off = nearest measured node"/>
```

---

## Item C — bug 7: collapse the summary trace card to ONE row

Current summary card body (`IsVisible="{Binding IsSummaryColumn}"`) is THREE stacked Grids: Metric
(`50,*`), Format/Digits (`50,Auto,Auto,Auto` with "Format"+"Digits" + `MaximumFractionDigits` NUD),
Comp (`50,Auto,Auto` with "Comp" + disabled `SummaryCompressionDisplay` NUD + "dB"). Replace ALL THREE
with one horizontal row: **[ Metric combo (narrower) | Digits NUD | Comp NUD (disabled) | "dB" ]**.

- Remove the literal "Format" and "Comp" text labels.
- The trash button is the card's col-1 (`Grid ColumnDefinitions="*,Auto"`), already top-right — do NOT
  add another.

Replace the whole summary-body StackPanel with:
```xml
<!-- ── Summary column card body (Phase 7.5 — single compact row) ─────── -->
<Grid ColumnDefinitions="*,Auto,Auto,Auto"
      ColumnSpacing="4"
      IsVisible="{Binding IsSummaryColumn}">

    <ComboBox Grid.Column="0"
              ItemsSource="{Binding SummaryMetricOptions}"
              SelectedItem="{Binding SummaryMetricSelection, Mode=TwoWay}"
              MinWidth="70"
              HorizontalAlignment="Stretch"
              ToolTip.Tip="Summary metric / impedance / bias"/>

    <NumericUpDown Grid.Column="1"
                   Value="{Binding MaximumFractionDigits, Mode=TwoWay}"
                   Minimum="0" Maximum="6" Increment="1"
                   Width="40" FormatString="0"
                   ToolTip.Tip="Decimal digits (real columns)"/>

    <NumericUpDown Grid.Column="2"
                   Value="{Binding SummaryCompressionDisplay}"
                   IsEnabled="{Binding SummaryCompressionEditable}"
                   Minimum="0" Maximum="20"
                   FormatString="F1"
                   Width="44"
                   ToolTip.Tip="Compression is set table-wide (change it in the header)"/>

    <TextBlock Grid.Column="3" Text="dB" Classes="label" VerticalAlignment="Center"/>
</Grid>
```

Per-card Comp NUD now uses `Minimum="0" Maximum="20"` (old card had `-20..0`, which clamped the
positive default to 0). Even disabled, the corrected range shows the true value.

Optional unit label: "dB" is wrong for non-dB metrics (%, °, Ω, V, mA). If cheap, add a read-only
`string SummaryUnitLabel` on `TraceRowViewModel` deriving from the SummaryColumn kind/metric
(Pout→"dBm", DE/PAE→"%", AMPM→"°", Gt/Gp/IRL→"dB", Z*→"Ω", BiasVLoad→"V", BiasILoad→"mA") and bind
`Text="{Binding SummaryUnitLabel}"`. If it balloons, ship static "dB" and note it. Do NOT block on this.

---

## Verification (owner-run)
1. Build after each item (A, B, C).
2. Auto-fill: each card is ONE compact row (Metric | Digits | Comp | dB), trash top-right, no
   "Format"/"Comp" labels.
3. Header: "Load" label; optimum combo shows MXP/MXE (dropdown + collapsed face).
4. Header: "Interp" checkbox replaces the Read combo; toggling flips Interp/Nearest and recomputes
   (watch a Zin/Zload column change between interpolated and nearest).
5. Regression: header Comp NUD (0–20) updates the title and recomputes (bug 10 already fixed).

## Notes
- `SummaryMetricSelection` / `SummaryMetricOptions` / `MaximumFractionDigits` /
  `SummaryCompressionDisplay` / `SummaryCompressionEditable` already exist on `TraceRowViewModel`.
- Keep `TableReadMode` enum + `Plot.TableReadMode` as-is (persistence). `AllTableReadModes` may become
  unused after Item B; harmless (public static, no warning).
