# Brief — Gate 6 Round 1 / D: MarkerEditorView changes (contour + spectral fields, layout, NaN fixes)

**Status:** Ready to implement
**Scope:** A batch of MarkerEditorView changes, grouped: (1) per-kind field gating (contour/spectral hide irrelevant rows), (2) the contour impedance entry replacing Frequency, (3) the NaN data-line bugs for contour + spectral, (4) VSWR row layout + label, (5) width reductions and moving Precision/Digits to the bottom row.
**Depends on:** Gates 0–5 landed; Brief C's stem value fix may land first or in parallel (this brief's spectral data-line fix reuses the same `BuildMarkerBoxLines` logic — coordinate if both touch `Trace`).

## Context (already verified)
- **Editor VM:** `MarkerEditorViewModel.cs`. `OwnDataLine => _parent.Trace.GetMarkerValString(_marker, false)` — and `GetMarkerValString` has **no contour/stem branch**, so it returns `…=NaN` for those kinds (the reported bug). `OwnZ0Line => $"Z0={…}"`. `ShowMultiDeltaControls => PlotType == Rect`. `ShowFormatSelector => PlotType != Rect`. The freq field is buffered (`FreqDisplayText` + `CommitFrequency()`).
- **Editor view:** `MarkerEditorView.axaml`. Rows in order: Name; Frequency; data-readout border (`OwnDataLine` + `OwnZ0Line`); Format/Precision/Digits grid; Norm Z toggle + Size; Multi/Δ row; (Gate 5) Info Box/VSWR toggles; VSWR value; Snap-to-Point. Styles: `seg-btn`, `label`, compact `TextBox`/`ComboBox`/`NumericUpDown`.
- **Complex parser to reuse:** `ComplexStringHelper.TryParse(string, out Complex)` and `.Format(Complex, "G6")` — the robust Z0-entry parser (handles `50`, `j5`, `5+j2`, `5+2j`, etc.). This is what `TraceRowViewModel` uses for Z0.
- **Marker kinds:** `marker.MarkerKind` ∈ {Polyline, Spectrum, StabilityCircle, Table, Contour}. Contour position is `PositionStatic` = Γ on Smith/Polar, Z on Rect. Contour value comes from `ContourData.EvaluateMetric` (already used by `BuildMarkerBoxLines`'s contour branch).

## UI build gate
UI builds with `TreatWarningsAsErrors=true`. Capture nullable into locals; XML doc comments must not contain raw `<`/`>`.

---

## Fix 1 — Per-kind field gating (VM computed props + AXAML `IsVisible`)

Add to `MarkerEditorViewModel`:
```csharp
public bool IsContour  => _parent is not null && _parent.Trace.IsContourTrace;
public bool IsSpectrum => _parent is not null && _parent.Trace.IsHarmonicStem;
public bool IsRectPlot => _parent is not null && _parent.PlotType == PlotType.Rect;

// Multi/Delta already gated by ShowMultiDeltaControls (Rect). Contour must ALSO hide them
// even on a Rect contour plot:
public bool ShowMultiDeltaControls2 => ShowMultiDeltaControls && !IsContour;   // rename/replace existing

// Z0 line: hide for contour-on-Rect and for spectral.
public bool ShowZ0Line => _parent is not null && !IsSpectrum
    && !(IsContour && IsRectPlot);

// Norm Z toggle: hide for contour-on-Rect (and it's already meaningless for spectral).
public bool ShowNormZ => _parent is not null && !(IsContour && IsRectPlot) && !IsSpectrum;
```
Replace the existing `ShowMultiDeltaControls` binding usage with the contour-excluding version (either rename the property or add `&& !IsContour` into the existing getter — simplest is to fold it in: `public bool ShowMultiDeltaControls => _parent is not null && _parent.PlotType == PlotType.Rect && !_parent.Trace.IsContourTrace;`).

AXAML bindings:
- Multi/Δ row: already `IsVisible="{Binding ShowMultiDeltaControls}"` → now also false for contour (via the folded-in change). Good.
- The `OwnZ0Line` `SelectableTextBlock` (Grid.Column=1 in the data-readout border): wrap its visibility with `IsVisible="{Binding ShowZ0Line}"`.
- Norm Z `ToggleButton`: add `IsVisible="{Binding ShowNormZ}"`.

## Fix 2 — Contour & spectral data line (NaN bug)

`OwnDataLine` must show the loadpull surface value for contour and the stem value for spectral, not `…=NaN`. Reuse the already-correct logic in `Trace.BuildMarkerBoxLines` rather than duplicating formatting.

Add a `Trace` method that returns the single "value" line for the editor, dispatching by kind:
```csharp
/// <summary>The marker's value line for the compact editor readout, matching the InfoBox.
/// Contour → "<metric>=<val>[ (interp)]"; spectrum → "<desc>=<value>"; else → GetMarkerValString.</summary>
public string GetEditorDataLine(Marker m, bool showFilePrefix)
{
    if (IsContourTrace && ContourData is { } cd)
    {
        var coord  = new Complex(m.PositionStatic.X, m.PositionStatic.Y);
        double val = cd.EvaluateMetric?.Invoke(coord, m.ContourSnapped) ?? double.NaN;
        string metric = string.IsNullOrEmpty(cd.MetricName) ? "value" : cd.MetricName;
        string fmt    = $"{m.FormatString}{m.MaximumFractionDigits}";
        string valStr = double.IsFinite(val) ? val.ToString(fmt) : "NaN";
        string cue    = m.ContourSnapped ? "" : " (interp)";
        return $"{metric}={valStr}{cue}";
    }
    if (IsHarmonicStem) return GetStemValString(m, showFilePrefix);
    return GetMarkerValString(m, showFilePrefix);
}
```
Then in the VM:
```csharp
public string OwnDataLine => _parent is null
    ? "dB(S(2,1)) = −3.45 dB ∠−45°"
    : _parent.Trace.GetEditorDataLine(_marker, showFilePrefix: false);
```
(`GetStemValString` is Gate 4 / refined in Brief C — if Brief C hasn't landed, the stem path still returns a value, just possibly with the older label; the contour NaN fix is independent and the important one here.)

Ensure `NotifyParent()` already raises `OnPropertyChanged(nameof(OwnDataLine))` (it does) so the line refreshes on edits.

## Fix 3 — Contour impedance entry (replaces Frequency field)

For a **contour** marker, the Frequency row is meaningless. Replace it (contour only) with an editable **impedance** field that drives the marker's position, reusing the robust complex parser.

### VM
```csharp
// Buffered impedance text for contour markers. Commit on Enter.
[ObservableProperty] private string _impedanceText = "";

// Called from ctor for contour markers and after a commit, to reflect the marker position.
private void SyncImpedanceText()
{
    if (_parent is null || !_parent.Trace.IsContourTrace) return;
    // Contour position: Γ on Smith/Polar, Z on Rect. Present IMPEDANCE to the user.
    var pos = _marker.PositionStatic;
    Complex shown = _parent.PlotType == PlotType.Rect
        ? new Complex(pos.X, pos.Y)                          // already Z
        : RfCore.RfHelpers.G2Z(new Complex(pos.X, pos.Y)) * RealZ0();   // Γ → normalized Z → ohms
    ImpedanceText = ComplexStringHelper.Format(shown, "G6");
}

private Complex RealZ0()
{
    var z0 = _parent!.Trace.Z0;
    return z0 == Complex.Zero ? new Complex(50, 0) : z0;
}

public void CommitImpedance()
{
    if (!MarkerIsLive || _parent is null || !_parent.Trace.IsContourTrace) return;
    if (!ComplexStringHelper.TryParse(ImpedanceText, out Complex z)) { SyncImpedanceText(); return; }

    // Convert the entered impedance back to the marker's plane.
    Complex posC = _parent.PlotType == PlotType.Rect
        ? z                                                  // Z-plane marker
        : RfCore.RfHelpers.Z2G(z / RealZ0());                // Γ-plane marker (normalize then Γ)
    var world = new System.Numerics.Vector2((float)posC.Real, (float)posC.Imaginary);
    // Honor contour mode (free vs snapped) just like a drag.
    _marker.PositionStatic = _parent.Trace.ResolveContourMarkerPosition(_marker, world);
    SyncImpedanceText();                                     // reflect any snap
    NotifyParent();
    _parent.Container.RequestPlotRedraw();
}

public bool ShowImpedanceField => IsContour;
public bool ShowFrequencyField => _parent is not null && !IsContour && !IsSpectrum;
```
Initialize `_impedanceText` in **both** constructors (design-time: `""` is fine; live: call `SyncImpedanceText()` after `_parent` is set, guarded for contour).

**Spectral frequency field (Fix relates to the "Frequency field not related to actual X" bug):** for a **spectrum** marker the Frequency row is also wrong (the marker has no `Freq`; its X is a harmonic). Hide the Frequency field for spectrum too (`ShowFrequencyField` excludes `IsSpectrum`). If the owner wants a harmonic/freq field in the editor for spectrum later, that's a follow-up — for now, hiding the misleading Frequency field resolves the bug. (The InfoBox shows freq/harmonic per Brief C.)

### AXAML
Replace the single Frequency `StackPanel` with two mutually-exclusive rows:
```xml
<!-- Frequency — network/stability markers only -->
<StackPanel Spacing="3" IsVisible="{Binding ShowFrequencyField}">
    <TextBlock Text="{Binding FreqUnitLabel, StringFormat='Frequency ({0})'}" Classes="label"/>
    <TextBox x:Name="FreqTextBox" Text="{Binding FreqDisplayText}"
             Width="120" HorizontalAlignment="Left" KeyDown="OnFreqTextBoxKeyDown"/>
</StackPanel>

<!-- Impedance — contour markers only -->
<StackPanel Spacing="3" IsVisible="{Binding ShowImpedanceField}">
    <TextBlock Text="Impedance (Ω)" Classes="label"/>
    <TextBox x:Name="ImpedanceTextBox" Text="{Binding ImpedanceText}"
             Width="120" HorizontalAlignment="Left" KeyDown="OnImpedanceKeyDown"/>
</StackPanel>
```
Code-behind handler (mirror `OnFreqTextBoxKeyDown`):
```csharp
private void OnImpedanceKeyDown(object? sender, KeyEventArgs e)
{
    if (e.Key == Key.Enter && DataContext is MarkerEditorViewModel vm)
    { vm.CommitImpedance(); e.Handled = true; }
}
```

## Fix 4 — VSWR row layout + label

- Put the **VSWR toggle** and the **VSWR value TextBox** on the **same line** (they're related).
- Change the value label from "VSWR value" to just "VSWR" (the toggle already says VSWR; the textbox is the value — keep one "VSWR" label on the row).
- The value TextBox width need only fit **8 characters**.

Replace the Gate 5 VSWR toggle + separate value block with a single row:
```xml
<!-- Info Box + VSWR (toggle and value together) -->
<Grid ColumnDefinitions="Auto,8,Auto,6,Auto" IsVisible="{Binding ShowVswrControls}">
    <ToggleButton Grid.Column="0" Classes="seg-btn"
                  IsChecked="{Binding VswrEnabled, Mode=TwoWay}"
                  ToolTip.Tip="Draw a constant-VSWR circle">VSWR</ToggleButton>
    <TextBlock Grid.Column="2" Text="VSWR" Classes="label"/>
    <TextBox  Grid.Column="4" x:Name="VswrValueTextBox"
              Text="{Binding VswrValueText}" Width="64"
              KeyDown="OnVswrValueKeyDown"/>
</Grid>
```
Keep the **Info Box** toggle on its own short row (or beside it) — don't lose it. (`Width="64"` ≈ 8 chars at FontSize 10; tune if needed.)

## Fix 5 — Widths + move Precision/Digits to bottom

- **Digits** NumericUpDown: width to fit **2 chars** (≈ `Width="40"`; it's `Minimum=1 Maximum=16` so two digits max).
- **Precision** ComboBox: width only as wide as "Scientific" (the longest item). Set an explicit `Width` sized to that text (≈ `Width="92"` at FontSize 10 — verify visually; the combo has no chevron so it can be tight).
- **Move Precision + Digits to the bottom row** (least important). Pull them out of the Format/Precision/Digits grid near the top and place a new row at the **end** of the outer StackPanel:
  ```xml
  <!-- Precision + Digits (least important — bottom) -->
  <Grid ColumnDefinitions="Auto,8,Auto" HorizontalAlignment="Left">
      <StackPanel Grid.Column="0" Spacing="3">
          <TextBlock Text="Precision" Classes="label"/>
          <ComboBox Width="92"
                    ItemsSource="{x:Static vm:MarkerEditorViewModel.AllPrecisionFormats}"
                    SelectedItem="{Binding FormatString}">
              <ComboBox.ItemTemplate>
                  <DataTemplate>
                      <TextBlock Text="{Binding Converter={x:Static vm:PrecisionFormatConverter.Instance}}" FontSize="10"/>
                  </DataTemplate>
              </ComboBox.ItemTemplate>
          </ComboBox>
      </StackPanel>
      <StackPanel Grid.Column="2" Spacing="3">
          <TextBlock Text="Digits" Classes="label"/>
          <NumericUpDown Value="{Binding Digits}" Minimum="1" Maximum="16"
                         Increment="1" FormatString="0" Width="40"/>
      </StackPanel>
  </Grid>
  ```
  The top grid then holds just **Format** (still gated by `ShowFormatSelector`, Smith/Polar only). If removing Precision/Digits leaves the Format grid with one element, simplify it to a plain gated StackPanel.

## Out of scope
- Persistence/crash/delete (Brief A), context menu + clipping (Brief B), spectral InfoBox rows (Brief C).
- No new VSWR math/drag. Don't change the glyph or selection.
- A dedicated harmonic/freq editor field for spectrum (future) — for now the misleading Frequency field is just hidden for spectrum.

## Acceptance / verification
1. Build green.
2. **Contour marker editor:** no Multi/Δ; no Norm Z and no Z0 line on a Rect contour; the data line shows the **loadpull metric value** (not NaN); a **Impedance (Ω)** field replaces Frequency, accepts `50`, `40+j10`, etc., and on Enter moves the marker (snapped if Snap-to-Point on), updating the plot + readout.
3. **Spectral marker editor:** no Z0 line; the misleading Frequency field is gone; the data line shows the stem value (not NaN).
4. **VSWR row:** toggle + value share one line; label reads "VSWR"; value box ~8 chars wide.
5. **Precision/Digits:** moved to the bottom row; Digits ~2 chars wide; Precision combo only as wide as "Scientific".
6. Other marker kinds (network polyline, stability) unchanged.

## Report back
- Confirm build green and each kind's editor shows the right fields (contour impedance commits & moves the marker; contour/spectral data lines are non-NaN).
- Confirm the impedance entry reused `ComplexStringHelper.TryParse` and handled Γ-plane (Smith) vs Z-plane (Rect) correctly.
- Note any width values you tuned (VSWR box, Precision combo, Digits).
