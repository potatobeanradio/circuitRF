# Sonnet Brief — Per-swept-variable units on markers and axis labels

**Motivation.** Markers (and X-axis labels) on parametric-sweep traces read out raw numbers
(`RFfreq=2000000000`) instead of units (`freq=2 GHz`, `Vds=48 V`). The Data Display already consumes
per-axis units — cube-bound traces carry `CubeXUnit` / `FamilyAxisUnit`, and `BuildCubeMarkerBoxLines`
already (a) scales a *frequency* X axis by the plot's `FreqUnit` and prints `freq=… GHz`, and (b) for a
non-frequency X axis prints `{name}={value} {unit}`. The X-axis *label* (`Plot.XLabel`) does the same.

**Root cause.** `ParametricSweepEngine.Run` builds the swept axis with **no unit**:
`new Axis(sweep.SweepVarName, sweep.SweepValues)` → `unit=""`. So `CubeXUnit`/`FamilyAxisUnit` come up
empty for swept axes and every readout falls through to the bare-number branch. Tag the axis and the
existing machinery lights up — for the X readout, the axis label, **and** measurement-trace markers
(measurement cubes inherit the swept axis via stacking).

**Owner decision (LOCKED).**
1. Tag the swept axis with the swept variable's **base SI unit**, sourced from the variable's own
   declared unit (`origVar.Unit`). No Analysis-Editor sweep-UI change — units live on the VAR. A VAR
   with no unit → untagged axis → raw readout (by design).
2. **Base unit, not display unit.** Values stay base SI (required so measurement-cube broadcasting still
   aligns). Frequency scales to the plot's `FreqUnit` at render time (existing path); non-frequency
   shows the base unit with no SI prefixing (`48 V`, not `48000 mV`) — there is no general prefix enum
   and adding one is out of scope.

This is independent of the Brief-2 sweep-range-unit work (that governs how Start/Stop/Step are
*interpreted*; this governs how the resulting axis is *labelled*). Both source the unit from the VAR,
so they agree.

---

## Part A — Engine: tag the swept axis (`src/Engine/ParametricSweepEngine.cs`)

In `Run`, `origVar` (the original `Variable` being swept) is already captured before the override loop
and is in scope where the axis is built. Derive the base unit and pass it to the `Axis`:

```csharp
string axisUnit = Units.BaseUnit(origVar?.Unit ?? "");        // Part B helper; "" when VAR has no unit
var sweepAxis = new Axis(sweep.SweepVarName, sweep.SweepValues, axisUnit);
return DataSet.StackSweepAxis(sweepAxis, datasets);
```

- `origVar` is the pre-override variable, so `origVar.Unit` is the declared unit (e.g. `"GHz"`), even
  though the per-point override variable is unit-less. When `origVar` is null (variable not in
  `GlobalVariables`) or has no unit, pass `""` (untagged — unchanged behavior).
- For **frequency** the tagged string is only used downstream as a freq/not-freq *flag* (scaling uses
  the plot's `FreqUnit`), so `"Hz"` is correct and sufficient. For **non-frequency** the tag must equal
  the values' unit, which is base (Brief 2 materialized base values) — hence base-unit derivation.
- The unit is pure metadata on the axis: it does not affect the measurement-cube `UnionAxes`
  broadcasting (which aligns by axis *name* + values), so no interaction with the prior fix.

## Part B — Core: `Units.BaseUnit` helper (`src/Core/Expressions/Units.cs`)

Add `public static string BaseUnit(string unit)` returning the scale-1 base symbol of a unit string:
- Frequency units (`Hz`/`kHz`/`MHz`/`GHz`/`THz`) → `"Hz"`.
- SI-prefixed linear units → strip the prefix to the base (`mV`→`V`, `kV`→`V`, `pF`→`F`, `mA`→`A`,
  `kOhm`/`kΩ`→`Ohm`, `uH`→`H`, …), using the existing `Units` prefix/scale knowledge.
- Log / dimensionless units with no SI prefix (`dBm`, `dB`, `Ohm`, `V`, `A`, `W`, `F`, `H`, `S`) →
  unchanged.
- Empty/unknown → return the input unchanged (`""` stays `""`).

Implement against the `Units` table's existing prefix handling; for the units that actually occur
(frequency + the common dimensions above) a small explicit fallback map is acceptable if the table
doesn't already expose a base-symbol lookup. Keep it framework-free (Core).

## Part C — Family-axis unit in the marker readout (`src/Ui/DataDisplay/Models/Trace.cs`)

The X-axis readout already appends a non-frequency unit; the **family-axis** readout does not. Two
small changes:

1. **Pass the family axis's unit through.** `Trace.SetFamilyData(…, string? familyAxisUnit = null)`
   already has the parameter. Find its caller (the cube→family-trace builder; grep `SetFamilyData`) and
   pass the family axis's `Unit` from the cube (it's now populated by Part A). If the caller currently
   passes `null`, switch it to the family `Axis.Unit`.
2. **Append the unit in the generic family branch.** In `BuildCubeMarkerBoxLines`, the family `else`
   branch (the non-frequency, non-harmonic case) currently emits `{axisName}={axisVal}` with no unit.
   Append `FamilyAxisUnit`, mirroring the X-axis row:
   ```csharp
   string axisUnit = string.IsNullOrEmpty(FamilyAxisUnit) ? "" : $" {FamilyAxisUnit}";
   lines.Add(($"{axisName}={axisVal}{axisUnit}", false));
   ```
   The frequency family branch (`IsFreqUnit(FamilyAxisUnit)`) already scales and labels correctly, so a
   family over a frequency VAR now shows `freq=2 GHz` once the axis is tagged.

**No change needed** to: the X-axis readout (`{name}={value} {unit}` already appends the unit, frequency
already scales), `Plot.XLabel` (already `freq (GHz)` / `{name} ({unit})`), or measurement-trace markers
(they inherit the now-tagged swept axis automatically).

---

## Tests

Core (`tests/Core.Tests`):
1. **Units_BaseUnit:** `BaseUnit("GHz")=="Hz"`, `"MHz"→"Hz"`, `"mV"→"V"`, `"kV"→"V"`, `"dBm"→"dBm"`,
   `"V"→"V"`, `""→""`.

Engine (`tests/Engine.Tests`):
2. **Sweep_Axis_CarriesBaseUnit:** sweep a VAR `RFfreq = 2 GHz` → the stacked DataSet's swept axis has
   `Name=="RFfreq"`, `Unit=="Hz"`, base-Hz values. Sweep a VAR with no unit → axis `Unit==""`. Sweep a
   `mV`-declared VAR → axis `Unit=="V"` with base-V values.

UI (`tests/Ui.Tests`):
3. **Marker_XReadout_FreqVar:** a cube trace whose X axis is `RFfreq` (unit `Hz`); marker readout line
   is `freq=2 GHz` (scaled by the plot `FreqUnit`), not `RFfreq=2e9`.
4. **Marker_XReadout_NonFreqVar:** X axis `Vds` (unit `V`) → readout `Vds=48 V`.
5. **Marker_FamilyReadout_NonFreqVar:** a family over `Vds` (unit `V`) → the family row reads
   `Vds=48 V` (regression for the Part-C `else`-branch gap). A family over a frequency VAR → `freq=…`.

## Gate
Build 0W/0E (TreatWarningsAsErrors); all Core/Engine/Ui tests green. **Manual:** VAR `RFfreq = 2 GHz`,
HB + inner `Pin` sweep + outer `RFfreq` sweep; in the Data Display, a measurement family over `RFfreq`
with `Pin` on X → drop a marker → the family row reads `freq=2 GHz` and the X row reads the Pin value;
the X-axis label shows its unit. Sweep a voltage-dimensioned VAR (e.g. `Vds = 48 V`) → markers read
`Vds=48 V`. A unit-less VAR still reads raw (unchanged).

## On completion
Note in `src/Engine/CLAUDE.md`: the parametric-sweep axis is now tagged with the swept variable's
**base SI unit** (`Units.BaseUnit(origVar.Unit)`); values stay base, frequency scales to the plot's
`FreqUnit` at render. Note in `src/Ui/CLAUDE.md`: marker info boxes and X-axis labels now show
per-swept-variable units for cube traces (frequency → scaled `GHz`; other dimensions → base unit, no
SI prefixing); the family-axis readout appends its unit (previously only the X-axis row did).
**No Analysis-Editor sweep-UI change** — the unit is sourced from the VAR declaration; a VAR without a
unit reads raw. (Optional deferred nicety: a read-only inherited-unit hint in the sweep row.)
