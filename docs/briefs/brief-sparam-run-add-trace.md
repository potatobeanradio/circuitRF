# Sonnet Brief — First-class S-parameter cube in the trace card (sim S plots as a cube, with sweeps)

Supersedes the earlier network/SNP version of this brief (that approach was wrong: rebuilding one SNP
from the grouped S can't sweep and can't back cube measurement expressions). **Do not build an SNP for
simulated S.** The SNP/network path stays reserved for **legacy Touchstone files**; simulated S-params
are a **first-class DataCube** in the trace card.

## Problem
After an S-parameter run, "Add Trace" is disabled. The run writes a **grouped** DataSet
(`AddToGroup(<analysis>, "S", …)` in `SchematicRunService`), so the result is `SP1.S` `[freq, i, j]`
(+ `SP1.Z0`), with `entry.Snp == null` (no Touchstone). But the cube picker **skips any cube named
`S`/`Z0`** in *every* group (two parallel skip lists), so the S cube is offered nowhere and
`CanAddTrace` is false. Touchstone still works because it builds `entry.Snp` and puts `S` in the
**default** group.

The cube machinery already does everything S needs — complex cubes on Smith/Rect/Polar, `i`/`j` rendered
as port selectors ("i (port)" / "j (port)", options = port numbers), families over an axis
(`ResolveFamily`), and transforms (dB20/Mag/…). Two gaps: (1) the picker excludes the S cube; (2) the
default-X logic would pick the **sweep** axis instead of **freq** for a swept S `[sweep, freq, i, j]`.

## Fix (UI only — no RfCore changes, no SNP build for sim S)
1. Offer the S cube **only when it's in a named analysis group** (sim result, no SNP). Keep skipping
   `Z0` everywhere, and keep skipping **default-group** `S` (Touchstone — owned by the network path).
2. Default the X axis to **freq** when a cube has one (parameter / freq-swept cubes), else the first
   non-label axis. So `SP1.S` seeds as **S(1,1) over frequency**, sweep pinned, i/j pinned — the user
   promotes the sweep to **Family** (curve per sweep point) or repins i/j via the axis-role editor.
3. First-add nicety: an S/Y/Z parameter cube on Rect defaults to **dB20** (instead of generic mag()).

Scope: `src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs`,
`src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`, tests. Build 0W/0E
(`TreatWarningsAsErrors=true`); tests green.

Read first: `PlotInspectorViewModel.FirstPlottableCubeName` + `BuildSeedCubeTrace`;
`TraceRowViewModel.RebuildSignals` (cube-enumeration block) + `BuildCarriedSliceFromCube`.

---

## 1. `TraceRowViewModel.cs` — shared default-slice helpers

Add as `internal static` near `BuildCarriedSliceFromCube` (called from `PlotInspectorViewModel` too):
```csharp
    /// <summary>
    /// Index of the default X axis for a cube: the "freq" axis when present (S/Y/Z parameter cubes and
    /// any freq-swept cube), else the first non-label (node/branch) axis. Returns -1 when only label
    /// axes exist (→ no X → scalar, valid for no-sweep DC).
    /// </summary>
    internal static int DefaultXAxis(RfCore.Data.DataCube cube)
    {
        for (int d = 0; d < cube.Rank; d++)
            if (cube.Axes[d].Name == "freq") return d;
        for (int d = 0; d < cube.Rank; d++)
            if (cube.Axes[d].Name is not "node" and not "branch") return d;
        return -1;
    }

    /// <summary>
    /// Default slice for a cube: <see cref="DefaultXAxis"/> → KeepAsX, every other axis pinned at index 0
    /// (carrying its first label for quoted net names). Rank-0 → empty slice. For an S cube
    /// [freq, i, j] (+ optional swept prefix) this is S(1,1) over frequency with i/j and the sweep pinned.
    /// </summary>
    internal static AxisSlice[] BuildDefaultSlice(RfCore.Data.DataCube cube)
    {
        int rank = cube.Rank;
        if (rank == 0) return Array.Empty<AxisSlice>();
        int xIdx = DefaultXAxis(cube);
        var slice = new AxisSlice[rank];
        for (int d = 0; d < rank; d++)
        {
            var ax = cube.Axes[d];
            if (d == xIdx)
                slice[d] = new AxisSlice(ax.Name, AxisRole.KeepAsX, 0);
            else
            {
                string lbl = (ax.Labels is { Length: > 0 }) ? ax.Labels[0] : "";
                slice[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, 0, Label: lbl);
            }
        }
        return slice;
    }

    /// <summary>True for an S/Y/Z parameter cube (axes "freq", "i", "j") — used to pick the dB20
    /// first-add transform on Rect.</summary>
    internal static bool IsParameterCube(RfCore.Data.DataCube cube)
        => cube.Axes.Any(a => a.Name == "freq")
        && cube.Axes.Any(a => a.Name == "i")
        && cube.Axes.Any(a => a.Name == "j");
```

## 2. `TraceRowViewModel.RebuildSignals` — offer grouped S; use the shared default

**2a.** Skip list — replace:
```csharp
                    if (bareName is "S" or "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
```
with:
```csharp
                    if (bareName == "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
                    // Default-group S belongs to the network/SNP path (Touchstone). S in a named analysis
                    // group is a simulated S cube (no SNP) — offer it as a first-class cube.
                    if (bareName == "S" && group == DataSet.DefaultGroup) continue;
```

**2b.** Default-slice block — replace:
```csharp
                    AxisSlice[] defaultSlice;
                    if (rank == 0)
                    {
                        defaultSlice = Array.Empty<AxisSlice>();
                    }
                    else
                    {
                        defaultSlice = new AxisSlice[rank];
                        int xAxisIdx = -1;
                        for (int d = 0; d < rank; d++)
                            if (cube.Axes[d].Name is not "node" and not "branch") { xAxisIdx = d; break; }
                        for (int d = 0; d < rank; d++)
                        {
                            var ax = cube.Axes[d];
                            if (d == xAxisIdx)
                                defaultSlice[d] = new AxisSlice(ax.Name, AxisRole.KeepAsX, 0);
                            else
                            {
                                string lbl = (ax.Labels is { Length: > 0 }) ? ax.Labels[0] : "";
                                defaultSlice[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, 0, Label: lbl);
                            }
                        }
                    }
```
with:
```csharp
                    // freq → X when present (parameter / freq-swept cubes), else first non-label axis;
                    // all other axes pinned at index 0.
                    AxisSlice[] defaultSlice = BuildDefaultSlice(cube);
```
(`int rank = cube.Rank;` above stays — still used by the rank-0 scalar skip.)

## 3. `TraceRowViewModel.BuildCarriedSliceFromCube` — prefer freq for the X fallback

Replace:
```csharp
        if (!anyX && rank > 0)
        {
            int fb = Array.FindIndex(result, s => s.AxisName is not "node" and not "branch");
            if (fb >= 0) result[fb] = result[fb] with { Role = AxisRole.KeepAsX, Label = "" };
            // else: all label axes → no X → scalar.
        }
```
with:
```csharp
        if (!anyX && rank > 0)
        {
            int fb = DefaultXAxis(cube);
            if (fb >= 0) result[fb] = result[fb] with { Role = AxisRole.KeepAsX, Label = "" };
            // else: all label axes → no X → scalar.
        }
```

## 4. `PlotInspectorViewModel.FirstPlottableCubeName` — same skip rule

Replace:
```csharp
                if (bareName is "S" or "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
```
with:
```csharp
                if (bareName == "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
                // Default-group S is owned by the network/SNP path (Touchstone); grouped S is a
                // simulated S cube offered as a first-class cube.
                if (bareName == "S" && group == DataSet.DefaultGroup) continue;
```

## 5. `PlotInspectorViewModel.BuildSeedCubeTrace` — shared default + dB20 for S

Replace the slice/transform tail (keep the rank-0 scalar branch above it unchanged):
```csharp
        var slice = new AxisSlice[rank];
        slice[0] = new AxisSlice(cube.Axes[0].Name, AxisRole.KeepAsX, 0);
        for (int d = 1; d < rank; d++)
        {
            var ax  = cube.Axes[d];
            string lbl = (ax.Labels is { Length: > 0 }) ? ax.Labels[0] : "";
            slice[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, 0, Label: lbl);
        }

        trace.Slice = slice;

        // First-add nicety: a complex cube on a Rect plot would render <invalid>; default to mag() so the user
        // sees something. Seed-time only — never re-applied on later edits.
        if (_plot.PlotType == PlotType.Rect && cube.DataKind == DataKind.Complex)
            trace.Transform = CubeTransform.Mag;

        trace.Expression = trace.BuildPickerExpression();
        return trace;
```
with:
```csharp
        // Default slice: freq → X when present (S/Y/Z parameter cubes and freq-swept cubes), else the
        // first non-label axis; every other axis pinned at index 0. For an S cube [freq, i, j] (+ optional
        // swept prefix) this yields S(1,1) over frequency with the sweep pinned — the user promotes the
        // sweep to Family or repins i/j via the axis-role editor.
        trace.Slice = TraceRowViewModel.BuildDefaultSlice(cube);

        // First-add nicety on Rect: complex cubes would render <invalid>. S/Y/Z parameter cubes default to
        // dB20 (the natural S-parameter view); other complex cubes to mag(). Seed-time only.
        if (_plot.PlotType == PlotType.Rect && cube.DataKind == DataKind.Complex)
            trace.Transform = TraceRowViewModel.IsParameterCube(cube) ? CubeTransform.dB20 : CubeTransform.Mag;

        trace.Expression = trace.BuildPickerExpression();
        return trace;
```

---

## Tests
Cube helpers (`TraceRowViewModel`):
1. **DefaultXAxis_Sparam:** `[freq,i,j]` → 0; `[sweep,freq,i,j]` → index of `freq` (1), not sweep.
2. **BuildDefaultSlice_Sparam:** `[freq,i,j]` → freq=KeepAsX, i=Pin0, j=Pin0. Swept `[sweep,freq,i,j]`
   → freq=KeepAsX, sweep=Pin0, i=Pin0, j=Pin0.
3. **IsParameterCube:** true for `[freq,i,j]`; false for an HB V cube `[node,harmonic]`.

Picker / inspector:
4. **Picker_OffersGroupedS:** a DataSet with `AddToGroup("SP1","S",…)`/`"Z0"` → `SP1.S` appears under
   group "SP1"; `Z0` does not.
5. **Picker_HidesTouchstoneDefaultS:** a Touchstone-derived entry (S in default group + `entry.Snp`) →
   `S` is **not** in the cube list (network path owns it); network S-Parameters items still present.
6. **FirstPlottableCubeName_GroupedS:** sim entry (`entry.Snp == null`, `SP1.S` present) → returns
   `"SP1.S"`.
7. **AddTrace_AfterSparamRun:** library = one sim entry (Snp null, `SP1.S`), empty Rect plot →
   `CanAddTrace` true; `AddTrace` seeds a cube trace with `CubeName=="SP1.S"`, freq as X, `Transform==dB20`.
8. **SweptS_Family:** swept `SP1.S` seeded (freq=X, sweep Pin0); set the sweep axis role to Family →
   `Trace.FamilyCurves` populated (one S11-vs-freq curve per sweep point, capped at `MaxFamilyCurves`).
9. **Smith_ComplexS:** `SP1.S` on a Smith plot resolves to a complex 1-D trace over freq (no transform).
10. **Touchstone_Unchanged:** a Touchstone entry on an empty plot → `AddTrace` still seeds a **network**
    S trace (`!IsCubeBound`, `MatrixType.S`), unaffected by the changes.

## Gate (manual)
Plain S-param run → Data Display on its `run.npy` → Add Trace → S11 in dB on Rect; switch `i (port)`→2 /
`j (port)`→1 to get S21; Smith shows S. Swept S-param run → Add Trace → set the sweep axis to **Family** →
one S-vs-freq curve per sweep point.

## Deferred / notes (do not implement here)
- **Stability-circle measurement expressions:** already reachable — the cube is `SP1.S` (qualified) and
  `DataSet.S(i,j)` (axes freq/i/j). No work now; wire when measurement-on-S-analysis lands.
- **`i`/`j` row labels** read "i (port)" / "j (port)". A friendlier "out port" / "in port" (axis-name →
  display-name map in `AxisRoleRowViewModel.AxisLabel`) is optional polish.
- **Z0 for cube S:** the cube path hides the Z0 row (network-only). Per-port Z0 lives in `SP1.Z0`;
  surfacing it for cube S is future.
- **Sweep default = pinned** (not Family) for consistency with every other cube; Family is one click.

## On completion
Note in `docs/design/trace-card.md` (and/or the data-display design doc): a simulated S-parameter result
is a first-class DataCube (`<analysis>.S`, axes `[ (sweep,) freq, i, j ]`) plotted through the cube path —
freq defaults to X, `i`/`j` are port selectors, a sweep axis becomes a Family; the SNP/network path is
reserved for legacy Touchstone files. dB20 is the default Rect transform for parameter cubes.
