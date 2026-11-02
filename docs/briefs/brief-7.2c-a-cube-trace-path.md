# Sonnet Brief — 7.2c-a: cube-native trace path + ≤2-D signal picker + identity components

**Context.** This is the first of three 7.2c briefs (then **7.2c-b** minimal-label display names, **7.2c-c**
`Snp*→DataSource*` rename — rename comes last). Goal of *this* brief: let a `Trace` plot a **1-D slice of any
`DataCube`** from a data source's `DataSet`, **in addition to** today's SNP/matrix/derived path — which stays
exactly as-is. New capability unlocked: plotting HB `V(node)`/`I(branch)` vs a sweep axis, named measurement
cubes, and any non-`S` cube — things the current `(MatrixType, row, col)` picker can't express.

**Do NOT change the `DataSet`/`DataCube` in-process API.** It is lockstep with splotRF — consume it only. If you
find you *need* a new introspection member (e.g. to enumerate cube names or an axis list) and one doesn't exist,
**stop and flag it for the owner** rather than adding it.

## Required reads first (confirm real APIs before coding)
1. `RfCore/src/Data/DataCube.cs` and `RfCore/src/Data/DataSet.cs` — confirm the **exact** members for:
   enumerating cube names in a DataSet; a cube's axis names + sizes + units; rank; `DataKind`; slicing
   (`int` pins/collapses, `Range`/`All` keeps, end-exclusive); transforms (`dB20`/`dB10`/`dB`/`mag`/`phase`/
   `real`/`imag`/`conj`); and `.Values` / `.Axis(name)`. The contract is in `src/Core/Data/CLAUDE.md` but it
   names accessors, not every introspection member — adapt to the actual code.
2. `src/Ui/DataDisplay/ViewModels/SnpEntryViewModel.cs` — confirm it exposes the loaded **`DataSet`** (the
   summary calls it `Data`) plus `FilePath`/`Kind`/`IsBroken`. The cube path reads from this DataSet.
3. `src/Ui/DataDisplay/Models/Trace.cs` and `…/ViewModels/TraceRowViewModel.cs` (you have these) — the new
   fields and picker items hang off these.

## Design — additive, two trace "modes"
A `Trace` is either **network-bound** (today's path: `Data` SNP + `MatrixType` + `Row`/`Col` + `Derived`) or
**cube-bound** (new). A single discriminator selects which path `BuildPath` takes. Network-bound traces are
completely unchanged.

### 1. Trace identity components — stored as SEPARATE fields (§2.3), never a joined string
Add to `Trace` (cube-bound mode; null/default for network-bound traces):
```csharp
// --- Cube-native binding (Phase 7.2c). Null ⇒ this trace uses the legacy SNP/matrix path. ---
public string?      SourcePath2   { get; set; }   // data-source file path (identity: source)
public string?      CubeName      { get; set; }   // identity: cube/quantity, e.g. "V", "PAE", "S"
public AxisSlice[]? Slice         { get; set; }   // identity: per-axis slice spec (see below)
public CubeTransform Transform    { get; set; } = CubeTransform.None;  // identity: transform
public bool IsCubeBound => CubeName is not null;
```
(Reuse the existing `SourcePath` field if it cleanly doubles as the source identity for both modes — your call
after reading Trace.cs; if reuse is clean, drop `SourcePath2` and just use `SourcePath`. Keep the *other* three
as new distinct fields regardless. Do not concatenate them into a label — 7.2c-b computes the display name from
these components.)

New small types (UI/DataDisplay/Models, near Trace):
```csharp
public enum CubeTransform { None, dB20, dB10, dB, Mag, Phase, Real, Imag, Conj }

/// <summary>Per-axis slice directive for a cube-bound trace.</summary>
public readonly record struct AxisSlice(string AxisName, AxisRole Role, int Index);
public enum AxisRole { PinToIndex, KeepAsX }   // FamilyIterate is 7.3 — not in this brief
```
The `Slice` array has one entry per cube axis, in cube axis order. For a 1-D trace exactly **one** axis is
`KeepAsX`; all others are `PinToIndex` with a concrete `Index`.

### 2. Cube read path in Trace
Add `BuildCubePath(PlotType, FreqUnit)` and dispatch to it from `BuildPath` when `IsCubeBound`:
```csharp
public void BuildPath(PlotType plotType, FreqUnit freqUnit)
{
    if (IsCubeBound)   BuildCubePath(plotType, freqUnit);
    else if (IsDerived) BuildDerivedPath(plotType, freqUnit);
    else                BuildMatrixPath(plotType, freqUnit);
}
```
`BuildCubePath` needs the source DataSet. **Trace must not hold a DataSet reference** (keep it serialization-clean
and consistent with how `Data`/SNP is injected). Instead, have the caller (PlotInspector/DataDisplay, which owns
the library) **resolve `SourcePath`+`CubeName`+`Slice` to the 1-D cube and hand the trace its numeric arrays** —
mirror how `Data` (SNP) is set today. Concretely add:
```csharp
// Set by the owner after slicing: the X axis values (already freq-unit-scaled if the X axis is frequency)
// and the dependent values + kind. Trace just maps these to Points using Transform + plotType.
public void SetCubeData(double[] xValues, Complex[]? complexValues, double[]? realValues,
                        string xAxisName, string? xUnit, /*…*/ PlotType plotType, FreqUnit freqUnit);
```
`BuildCubePath` then fills `Points`:
- **Rect:** `x = xValues[i]`; `y =` apply `Transform` to the value (`dB20 = 20·log10(|z|)`, `dB10`/`dB = 10·log10`,
  `Mag`, `Phase` in degrees, `Real`, `Imag`; `None` on a Real cube = the value, on Complex = magnitude). Skip
  non-finite, exactly like `BuildMatrixPath`.
- **Smith/Polar:** require a Complex cube; `x = z.Real`, `y = z.Imag` (Transform `Conj`/`None`); a Real cube on
  Smith/Polar yields no points (guard).
- **Table:** values feed the table the same way the matrix path does.
Keep `dB20` vs `dB10` honest (see the contract's footgun note): amplitude/wave/ratio/voltage ⇒ default `dB20`;
power-like ⇒ `dB10`. Default the picker's transform to `dB20` for Complex network/voltage cubes, `None` for Real.

Markers/derived/stability-circle code paths are **network-bound only** — do not wire them for cube-bound traces
in this brief (a cube-bound trace has `Derived == None`, no stability circles). Marker *readout* on a cube-bound
trace can be a follow-up; for now a cube-bound trace renders line/symbol but the existing marker add path may be
left disabled for it (guard `IsCubeBound`). Flag this scoping in the PR notes.

### 3. The ≤2-D signal picker (TraceRowViewModel / PlotInspectorViewModel)
Extend `RebuildSignals()` so `AvailableSignals` includes **cube signals** alongside the existing matrix/derived
items. For each library entry's DataSet, enumerate cubes and offer a signal **only when it can yield a 1-D trace
from a ≤2-D cube**:
- **rank-1 cube** → one signal: keep that axis as X. (e.g. a measurement vs sweep.)
- **rank-2 cube** → for each axis `a`, signals that keep `a` as X and pin the other axis to each of its indices
  — but cap to keep the list sane: offer "keep axis A as X, pin axis B = index k" for each k of the *shorter*
  pinned axis. (e.g. `V {node, Pin}` → "V(node=…) vs Pin" per node, and/or "V vs node at Pin=k".) Use the axis
  **names/values** for labels (node names, harmonic index, etc.) from `.Axis(name)`.
- **rank ≥3 cube** → **skip** (family-iterate lands in 7.3). Network `S`/`Y`/`Z` data continues through the
  existing matrix picker; to avoid duplicate entries, **skip a cube named `"S"`** in the cube enumeration when the
  entry is already offered via the matrix path (your judgment after reading SnpEntryViewModel — simplest is: skip
  `"S"`, `"Z0"`, and any cube the matrix/derived picker already covers).

Extend `TraceDataItem` with the cube-bound variant (CubeName + the resolved `AxisSlice[]` + a Complex/Real flag +
an `IsEnabled` gate by plot type, same pattern as the derived items). On selection
(`OnSelectedSignalChanged`), set the trace's cube identity fields (`SourcePath`, `CubeName`, `Slice`, `Transform`)
instead of `Row`/`Col`/`Derived`, then `_parent.RebuildAndNotify()`. The owner's `RebuildAndNotify`/`BuildPath`
must resolve the slice against `entry.Data` and call `SetCubeData` (point 2). Keep `_suppressDataCallback` and the
"already applied" short-circuit behavior intact for the cube case.

The **transform** (`CubeTransform`) gets a small ComboBox in the trace row (reuse the YAxis combo slot or add a
sibling) — only shown for cube-bound traces; network-bound rows keep the existing `AvailableYAxes`.

### 4. Persistence (`.cdd`)
`BuildTraceConfig`/`LoadPlotContainerConfigAsync` in `DataDisplayViewModel` must round-trip the new fields
(`CubeName`, `Slice` as a small serializable list of `{AxisName, Role, Index}`, `Transform`). Alpha rule: just add
the fields (defaulted/nullable → safe), no format-version bump, no migration. A trace with `CubeName == null`
loads exactly as today (network-bound).

## Tests (`tests/Ui.Tests`, headless)
1. **`CubeTrace_Rect_BuildsPoints`**: build a small Real rank-1 cube (e.g. `PAE {Pin}`), bind a cube trace
   keep-Pin-as-X, `Transform=None`, `BuildPath(Rect)` → `Points.Count == Pin length`, X matches the axis values.
2. **`CubeTrace_Complex_dB20`**: a Complex rank-2 cube `{node, Pin}`, pin node=0, keep Pin, `Transform=dB20` →
   Y values equal `20·log10(|z|)` within tolerance.
3. **`CubeTrace_RankGE3_NotOffered`**: a rank-3 cube produces **no** signals in `AvailableSignals`.
4. **`CubeTrace_Roundtrips_Cdd`**: a cube-bound trace saved + reloaded reproduces `CubeName`/`Slice`/`Transform`;
   a legacy network-bound trace still loads unchanged.

## Gate
Build 0W/0E (TreatWarningsAsErrors); tests green. Manual: load a `.npy` HB-with-sweep result, add a plot, and via
the picker plot `V(node) vs Pin` in dB — renders a curve; existing Touchstone S-param plots, stability circles,
markers, and Table are unchanged.

## On completion
Note in `src/Ui/CLAUDE.md`: traces are now either network-bound (SNP/matrix/derived, unchanged) or **cube-bound**
(`SourcePath`+`CubeName`+`Slice`+`Transform`, identity stored as separate fields per §2.3); cube-bound traces
slice a ≤2-D `DataCube` from the source DataSet to a 1-D trace (family-iterate for rank≥3 deferred to 7.3);
markers/derived remain network-only for now. No `DataSet`/`DataCube` API changes. Next: 7.2c-b (minimal-label
display names over these identity components), then 7.2c-c (`Snp*→DataSource*` rename).
