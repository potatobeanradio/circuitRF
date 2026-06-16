# Sonnet Brief — 7.2f-2: Z0 box default-locked + Override checkbox; "Multiple Port Normalization" for non-uniform

**Context.** Refines the 7.2f Z0-textbox behavior so users aren't confused. Compute logic from 7.2f is
unchanged — this is purely the trace-card Z0 control (`TraceRowViewModel` + the trace-card XAML). Drop the
orange warning glyph from the Z0 area (the badge from 7.2e is being superseded here by clearer inline text — see
"Badge" note at the end).

## Target behavior (LOCKED)
For a **scattering trace** (network/S-kind; not cube-bound):
1. **Default (uniform source, no override):** the Z0 box is **shown but disabled (read-only)**, displaying the
   source's **uniform port-1 reference** (`Re,Im` of `SourceZ0PerPort[0]`, or `Data.Z0` when no per-port vector).
   To its **right: an "Override" checkbox**, unchecked by default.
2. **Override checked (uniform source only):** the Z0 box becomes **editable** — advanced users renormalize
   freely (the existing uniform-renorm path; `Z0String` drives `_trace.Z0`). Unchecking reverts the box to the
   source port-1 value and disables it (no renorm).
3. **Non-uniform source (`SourceZ0IsUnusual` from a `NonUniform` classification):** **no Z0 box and no Override
   checkbox.** Instead show subtle grey text **"Multiple Port Normalization"**. No editing, no glyph.
4. **No orange warning icon** anywhere in the Z0 area.

Note on UniformComplex: it is **uniform** across ports, so it follows case 1/2 (box shown, port-1 complex value,
Override allowed). Only `NonUniform` triggers case 3. (This narrows 7.2f, which disabled the box for
UniformComplex too — now UniformComplex is editable-under-override like any uniform source. The "unusual"
predicate that *blocks the box entirely* is now **NonUniform only**.)

## TraceRowViewModel changes
Current members: `Z0String` (drives `_trace.Z0`), `IsZ0Editable`, `Z0DisabledReason`, `ShowZ0Badge`,
`Z0BadgeTooltip`, `ApplySourceZ0`, and `_trace.SourceZ0PerPort`/`SourceZ0IsUnusual`. Replace the gating with:

```csharp
/// <summary>The source's uniform reference (port-1), shown read-only unless Override is on. Drives _trace.Z0
/// only while editing is allowed.</summary>
// Z0String stays; ensure it is (re)seeded from the source port-1 value on bind/refresh (see ApplySourceZ0).

/// <summary>True when this source uses genuinely non-uniform-across-ports normalization — the box is replaced
/// by the "Multiple Port Normalization" label. (Complex-but-uniform is NOT this.)</summary>
public bool IsMultiPortNormalization =>
    !_trace.IsCubeBound && _trace.SourceZ0IsUnusual && SourceZ0IsNonUniform;

/// <summary>Z0 control (box + Override) is shown only for scattering traces that are NOT multi-port-normalized.</summary>
public bool ShowZ0Control => !_trace.IsCubeBound && IsScatteringTrace && !IsMultiPortNormalization;

[ObservableProperty] private bool _z0OverrideEnabled;   // the Override checkbox

/// <summary>Box editable only when Override is on (and the source is uniform → ShowZ0Control true).</summary>
public bool IsZ0Editable => ShowZ0Control && Z0OverrideEnabled;

partial void OnZ0OverrideEnabledChanged(bool value)
{
    if (!value)
    {
        // Revert to the source uniform reference; no renorm.
        SeedZ0FromSource();                 // sets Z0String + _trace.Z0 = source port-1 value
        _parent.RebuildAndNotify();         // recompute at source reference
    }
    OnPropertyChanged(nameof(IsZ0Editable));
}
```
- **`SourceZ0IsNonUniform`**: the row needs to distinguish NonUniform from UniformComplex. Cheapest: have the
  binding point (`ApplySourceZ0`) also stash the `Z0Kind` (from `entry.Z0Kind`, 7.2e) on the row, e.g.
  `private Z0Kind? _sourceZ0Kind;` and `SourceZ0IsNonUniform => _sourceZ0Kind == Z0Kind.NonUniform;`. Keep
  `_trace.SourceZ0IsUnusual` meaning "NonUniform OR UniformComplex" (7.2f compute relies on it); add the finer
  kind only for the UI gate.
- **`IsScatteringTrace`**: network-bound, S-kind trace (the same predicate that gated `IsZ0Editable` in 7.2f —
  reuse it; e.g. `!_trace.IsCubeBound && _trace.Derived == DerivedParameters.None && _trace.MatrixType ==
  MatrixType.S`, matching the existing rule).
- **`SeedZ0FromSource()`**: set `Z0String = ComplexStringHelper.Format(sourcePort1Z0)` where `sourcePort1Z0 =
  _trace.SourceZ0PerPort?[0] ?? _trace.Data.Z0`, and assign `_trace.Z0 = sourcePort1Z0` so compute uses the
  source reference while not overriding. Call it from `ApplySourceZ0` (so a bind/auto-refresh reseeds the
  displayed value) **and** when Override is turned off.
- In `ApplySourceZ0(...)` and `RebuildSignals()`/`RefreshDescription()`, raise `OnPropertyChanged` for the new
  members: `ShowZ0Control`, `IsMultiPortNormalization`, `IsZ0Editable` (and stop relying on the removed
  `Z0DisabledReason`/`ShowZ0Badge` in the Z0 area — see Badge note). When a new source is bound, reset
  `Z0OverrideEnabled = false` (don't carry an override across a source change).

Remove `Z0DisabledReason` if now unused. Keep `Z0String`'s existing setter→`_trace.Z0`/`RebuildAndNotify` path
for the editable case.

## Trace-card XAML (the Z0 row)
Replace the current Z0 box + badge with:
```xml
<!-- Multi-port normalization: no box, subtle grey label -->
<TextBlock Text="Multiple Port Normalization"
           IsVisible="{Binding IsMultiPortNormalization}"
           Foreground="{DynamicResource SystemBaseMediumColor}"  <!-- subtle grey; use a Brush resource, not a *Color key on Foreground -->
           FontSize="11" Opacity="0.6" VerticalAlignment="Center"/>

<!-- Uniform source: read-only-by-default box + Override checkbox -->
<StackPanel Orientation="Horizontal" Spacing="6"
            IsVisible="{Binding ShowZ0Control}">
    <TextBox Text="{Binding Z0String}" IsEnabled="{Binding IsZ0Editable}" Width="120"/>
    <CheckBox Content="Override" IsChecked="{Binding Z0OverrideEnabled}" VerticalAlignment="Center"/>
</StackPanel>
```
- Use a real `SolidColorBrush` resource for the grey (a `System*Color` key resolves to `Color` and **silently
  fails** on `Foreground`/`IBrush`). Match the §2.8 idiom's ~0.6-opacity secondary-label grey.
- No `MaterialIcon`/glyph in this row.

## Badge (7.2e) reconciliation
The Z0-area orange glyph is removed. Decide minimally: **drop the per-trace `ShowZ0Badge` glyph** (the inline
"Multiple Port Normalization" text now conveys non-uniform; uniform-complex no longer needs an alarm since the
box shows the complex value). **Keep the one-time Messages warning** (still useful on load). If `ShowZ0Badge`/
`Z0BadgeTooltip` become unused after removing the glyph from XAML, delete them; if they're referenced elsewhere,
leave the members but remove the glyph from the trace card. Don't expand scope hunting other usages — just the
trace card.

## Tests (`tests/Ui.Tests`)
1. **UniformSource_BoxLockedShowsPort1:** uniform-real source → `ShowZ0Control` true, `IsMultiPortNormalization`
   false, `IsZ0Editable` false, `Z0String` == port-1 value.
2. **Override_EnablesEditing:** set `Z0OverrideEnabled = true` → `IsZ0Editable` true; editing `Z0String`
   renorms; clearing it reverts `Z0String`/`_trace.Z0` to the source port-1 value and recomputes.
3. **UniformComplex_TreatedAsUniform:** a uniform-complex source → `ShowZ0Control` true (box shown with the
   complex value), `IsMultiPortNormalization` false.
4. **NonUniform_ShowsLabelNoBox:** a non-uniform source → `IsMultiPortNormalization` true, `ShowZ0Control` false.
5. **NonScattering_NoControl:** a cube-bound trace and a Z/Y or derived trace → `ShowZ0Control` false.

## Gate
Build 0W/0E; tests green. Manual: a normal 50 Ω trace shows a greyed Z0 box reading "50, 0" with an unchecked
Override; checking Override lets you renorm; a uniform-complex source shows its complex value (box greyed, Override
available); a non-uniform source shows grey "Multiple Port Normalization" with no box and no orange icon.

## On completion
Note in `src/Ui/CLAUDE.md`: the trace-card Z0 box is read-only by default (shows the source's port-1 uniform
reference) with an Override checkbox that unlocks uniform renormalization for advanced users; non-uniform sources
replace the box with subtle "Multiple Port Normalization" text (no warning glyph); the one-time Messages warning
on load is retained.
