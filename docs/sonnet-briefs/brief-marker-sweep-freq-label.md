# Sonnet Brief — Marker readout: a frequency-variable sweep axis is not the harmonic axis

**Bug.** Plotting a measurement as a family over a swept frequency variable (e.g.
`Pout_dBm[~, :]` with `~` = `RFfreq`, `:` = `Pin`), the MarkerInfoBox shows `freq=…` instead of the
swept variable name `RFfreq=…`, and also shows a bogus `harmonic=…` row. Both are wrong: the user swept
`RFfreq`, so the readout must name `RFfreq`, and there is no harmonic concept for a frequency *sweep*.

**Root cause.** In `Trace.BuildCubeMarkerBoxLines` (`src/Ui/DataDisplay/Models/Trace.cs`), the family
branch keys the harmonic-style display purely on `IsFreqUnit(FamilyAxisUnit)`:

```csharp
if (IsFreqUnit(FamilyAxisUnit))   // line ~1325
{
    double scaled = fc.AxisValue * freqUnit.Scale();
    lines.Add(($"freq={scaled:G6} {freqUnit.Description()}", false));        // hardcoded "freq"
    lines.Add(($"harmonic={HarmonicOrderOf(fc.AxisValue)}", false));         // harmonic — wrong here
}
```

That branch was meant for the HB **harmonic** axis (which stores physical frequencies, tagged `Hz`).
But a parametric sweep over a frequency variable is *also* a frequency-unit axis (`RFfreq` tagged `Hz`
once it carries a unit), so it falls into the same branch and is mislabelled `freq` + `harmonic`. The
discriminator must be the **axis name** (`HarmonicAxisName == "harmonic"`), not the unit.

The X-axis branch already gates its `harmonic=` row on `xIsHarmonicAxis`, but still hardcodes the label
`"freq"` for any frequency-unit X axis, so an `RFfreq` *X* axis would read `freq=…` instead of
`RFfreq=…` — the same naming bug, lesser severity (the user's case is the family axis).

**Owner decision (LOCKED).** The `freq` + `harmonic` readout applies **only** to the genuine harmonic
axis (name == `HarmonicAxisName`). Every other axis — including a frequency-variable sweep — shows its
own **name**, scaled to the plot's frequency unit when it carries a frequency unit, with no harmonic
row.

---

## Part A — Family branch (`Trace.BuildCubeMarkerBoxLines`)

Replace the family-axis `if/else if/else` (the block starting `if (IsFreqUnit(FamilyAxisUnit))`) with:

```csharp
bool familyIsHarmonic = string.Equals(FamilyAxisName, HarmonicAxisName, StringComparison.Ordinal);
if (familyIsHarmonic && IsFreqUnit(FamilyAxisUnit))
{
    // Genuine HB "harmonic" family axis (stores physical frequencies):
    // unit-scaled freq row + integer harmonic order.
    double scaled = fc.AxisValue * freqUnit.Scale();
    lines.Add(($"freq={scaled:G6} {freqUnit.Description()}", false));
    lines.Add(($"harmonic={HarmonicOrderOf(fc.AxisValue)}", false));
}
else if (familyIsHarmonic)
{
    // Unitless "harmonic" axis whose values are integer harmonic indices.
    lines.Add(($"harmonic={(int)Math.Round(fc.AxisValue)}", false));
}
else
{
    // Any other family axis — including a sweep over a frequency variable (e.g. RFfreq).
    // Show the swept variable's NAME (never "freq"/"harmonic"); scale by the plot's freq unit
    // when the axis carries a frequency unit, else append the axis's own unit.
    string axisName = string.IsNullOrEmpty(FamilyAxisName) ? "curve" : FamilyAxisName;
    if (IsFreqUnit(FamilyAxisUnit))
    {
        double scaled = fc.AxisValue * freqUnit.Scale();
        lines.Add(($"{axisName}={scaled:G6} {freqUnit.Description()}", false));
    }
    else
    {
        string axisVal = !string.IsNullOrEmpty(fc.AxisLabel)
            ? fc.AxisLabel
            : fc.AxisValue.ToString($"{m.FormatString}{m.MaximumFractionDigits}");
        string unit = string.IsNullOrEmpty(FamilyAxisUnit) ? "" : $" {FamilyAxisUnit}";
        lines.Add(($"{axisName}={axisVal}{unit}", false));
    }
}
```

(The non-frequency `else` now also appends `FamilyAxisUnit`, completing the family-row unit append the
marker brief intended.)

## Part B — X-axis branch (same method): use the axis name, not hardcoded "freq"

In the `if (IsFreqUnit(_cubeXUnit))` block of the X-axis row, replace the hardcoded `freq` label:

```csharp
if (IsFreqUnit(_cubeXUnit))
{
    double scaledX = xRaw * freqUnit.Scale();
    // "freq" only for the genuine harmonic axis; a frequency-variable sweep shows its own name.
    string label = (xIsHarmonicAxis || string.IsNullOrEmpty(_cubeXAxisName))
        ? "freq" : _cubeXAxisName;
    lines.Add(($"{label}={scaledX:G6} {freqUnit.Description()}", false));
    if (xIsHarmonicAxis)
        lines.Add(($"harmonic={rawIdx}", false));   // unchanged: only for the harmonic axis
}
```

The `else` (unitless harmonic X axis) and the final non-frequency `else` (`{xName}={xRaw} {unit}`) are
unchanged.

---

## Tests (`tests/Ui.Tests`)
1. **Marker_Family_FreqVarSweep_UsesVarName (regression):** a family trace whose family axis is
   `RFfreq` with unit `Hz`; the readout contains `RFfreq=2 GHz` and does **not** contain `freq=` or
   `harmonic=`.
2. **Marker_Family_FreqVarSweep_Untagged:** same but `FamilyAxisUnit == ""` → `RFfreq=2` (no unit, no
   harmonic).
3. **Marker_Family_HarmonicAxis_Preserved:** family axis name `harmonic`, unit `Hz` → still
   `freq=… GHz` + `harmonic=<order>` (no regression to HB spectrum families).
4. **Marker_Family_NonFreqVar:** family axis `Vds`, unit `V` → `Vds=48 V`.
5. **Marker_X_FreqVarSweep_UsesVarName:** X axis `RFfreq` (unit `Hz`) → `RFfreq=2 GHz`, not `freq=…`;
   X axis `harmonic` → `freq=… GHz` + `harmonic=<idx>` preserved.

## Gate
Build 0W/0E; all Ui tests green. **Manual (the reported case):** sweep `RFfreq` (outer) over `Pin`
(inner), plot `Pout_dBm[~, :]` as a family over `RFfreq`, drop a marker → the family row reads
`RFfreq=<value>` (with its unit when the axis is tagged), and there is **no** `harmonic=` row. Plotting
an actual HB harmonic spectrum still shows `freq=… GHz` + `harmonic=<order>`.

## On completion
Note in `src/Ui/CLAUDE.md`: marker readouts now treat the `freq`/`harmonic` display as specific to the
**HB harmonic axis** (matched by `HarmonicAxisName`); any other frequency-unit axis — notably a
parametric sweep over a frequency variable like `RFfreq` — is labelled with its own axis/variable name
(scaled to the plot's frequency unit) and shows no harmonic row. Applies to both the family-axis and
X-axis readout rows.
