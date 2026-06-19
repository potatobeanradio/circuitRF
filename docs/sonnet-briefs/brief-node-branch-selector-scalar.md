# Sonnet Brief F1 — node/branch is always a selector; no-sweep cubes resolve to a scalar

Fixes the no-parametric-sweep DC edge case: with `V[node]` / `I[branch]` (rank-1), the card promotes the
sole axis to X, so there is **no node/branch selector**, and typing `DC1.I["Iout"]` / `DC1.I[0]` errors
("Need at least one swept axis"). Root unification: the **node/branch label axis is always a pinned
selector**, the X axis is the first **non-label** axis, and a cube with no non-label axis resolves to a
**scalar** (which already renders on a Table via the scalars-on-Table work, and shows a soft `<invalid>`
on Rect/Smith/Polar). This also improves the no-sweep HB default (spectrum vs harmonic at a selected node,
instead of "V vs node").

Scope: `ViewModels/TraceRowViewModel.cs` (`RebuildSignals` default slice, `RebuildAxisRolesCore` +
`FlushSliceAndRebuild` + `BuildCarriedSliceFromCube` X-fallbacks), `ViewModels/PlotInspectorViewModel.cs`
(`TrySetCubeData`), `CubeTraceSpecParser.cs`. Build 0W/0E; tests green.

Read first: the listed methods. A "label axis" below means `axis.Name is "node" or "branch"` — the
selector axes that carry net/branch name labels.

## 1. Default slice — X = first non-label axis; label axis = pinned (`RebuildSignals`)
Replace the current default-slice builder (`defaultSlice[0] = KeepAsX; rest pinned`) with:
```csharp
defaultSlice = new AxisSlice[rank];
// X defaults to the first NON-label axis (harmonic / sweep / freq …). The node/branch label axis is a
// selector (pinned). If there is no non-label axis (no-sweep DC), nothing is X → the cube resolves to a
// scalar (Table value; <invalid> elsewhere).
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
```
(Swept cubes are unchanged: e.g. `[Pin_avail, node, harmonic]` still gets `Pin_avail`→X since it is the
first non-label axis. Only no-sweep cubes — where a label axis was axis 0 — change.)

## 2. X-fallbacks must not promote a label axis
Three spots force a label axis to X when none is set. Each must promote only a **non-label, non-family**
axis, and otherwise leave **no X** (→ scalar).

**`RebuildAxisRolesCore`** (end of method):
```csharp
if (!AxisRoles.Any(r => r.IsX))
{
    var fallback = AxisRoles.FirstOrDefault(r =>
        !r.IsFamily && r.AxisName is not "node" and not "branch");
    fallback?.SetIsXSilent(true);     // null ⇒ leave no X (scalar) — valid for no-sweep DC
}
```

**`FlushSliceAndRebuild`** (the `if (!hasX …)` guard):
```csharp
bool hasX = Array.Exists(slice, s => s.Role == AxisRole.KeepAsX);
if (!hasX && slice.Length > 0)
{
    int fb = Array.FindIndex(slice, s =>
        s.Role != AxisRole.FamilyIterate && s.AxisName is not "node" and not "branch");
    if (fb >= 0)
    {
        slice[fb] = new AxisSlice(slice[fb].AxisName, AxisRole.KeepAsX, 0);
        AxisRoles[fb].SetIsXSilent(true);
    }
    // else: only label/family axes → no X → scalar. Leave as-is.
}
```

**`BuildCarriedSliceFromCube`** (the `if (!anyX && rank > 0)` guard):
```csharp
if (!anyX && rank > 0)
{
    int fb = Array.FindIndex(result, s => s.AxisName is not "node" and not "branch");
    if (fb >= 0) result[fb] = result[fb] with { Role = AxisRole.KeepAsX, Label = "" };
    // else: all label axes → no X → scalar.
}
else { /* existing dedup-extra-X logic unchanged */ }
```

## 3. `TrySetCubeData` — no-X slice resolves to a scalar (`PlotInspectorViewModel`)
In the single-slice path, **replace** the force-X fallback:
```csharp
// Fallback: if no axis mapped to X, keep the first axis.
if (xDim < 0 && cube.Rank > 0) { args[0] = Range.All; xDim = 0; }
```
with a scalar resolution (all axes are pinned indices, so `cube[args]` is a bare element):
```csharp
// No axis is X → every axis is pinned → scalar (operating-point value). Renders on a Table;
// <invalid> on Rect/Smith/Polar (handled by SetScalarCubeData).
if (xDim < 0)
{
    var sr = cube[args];
    t.InvalidSpecText = null;
    t.ExpressionError = null;
    t.SetScalarCubeData(
        sr.IsComplex ? sr.ComplexValue : (System.Numerics.Complex?)null,
        sr.IsReal    ? sr.RealValue    : (double?)null,
        plotType, freqUnit);
    return;
}
```
(The rank-0 branch above is unchanged; this handles rank≥1 fully-pinned. `args` already holds clamped pin
indices for every axis when `xDim < 0`.)

## 4. `CubeTraceSpecParser` — allow a fully-pinned (zero-X) slice
The `else if (keptDims.Count == 0)` branch currently errors "Need at least one swept axis". Allow it as a
scalar (only when there is no family — a family still needs an X):
```csharp
else if (keptDims.Count == 0)
{
    // Fully pinned → scalar (operating-point value). Valid: renders on a Table.
}
```
Leave the `famCount > 0` block's "A family needs one swept X axis" check intact. Now `DC1.I["Iout"]`,
`DC1.I[0]`, `DC1.V["Vout"]` parse to fully-pinned slices → scalars.

## Result
- No-sweep DC: picking `V` or `I` shows a node/branch **selector** (the label axis is pinned, not X). The
  selected value renders as a Table cell; on Rect/Smith/Polar it shows the soft `<invalid>` (use a Table
  for operating-point values). Typing `DC1.I["Iout"]` / `DC1.I[0]` works.
- No-sweep HB `V[node, harmonic]`: now defaults to harmonic→X, node→selector (spectrum at a node).
- Swept cubes: unchanged.
- `DC1.I("Iout")*DC1.V("Vout")` measurements were always fine (Evaluator scalar path) and stay fine.

## Tests
1. **DcNoSweep_NodeIsSelector:** DC `V[node]` (rank-1) → the trace's default slice pins `node`
   (PinToIndex), no `KeepAsX`; the axis-role row for `node` shows the pin selector
   (`ShowPinPicker == true`).
2. **DcNoSweep_BranchScalarOnTable:** on a Table, a trace bound to `I` with `branch` pinned to `Iout`
   renders that probe's current as a value cell (not empty, no throw).
3. **SpecParser_FullyPinned_Scalar:** `CubeTraceSpecParser.TryParse("DC1.I[\"Iout\"]", …)` and
   `("DC1.I[0]", …)` succeed with a fully-pinned slice (no `KeepAsX`), no error.
4. **TrySetCubeData_NoX_Scalar:** a rank-1 cube with its only axis pinned → `SetScalarCubeData` is invoked
   (trace is scalar), not a forced node-as-X line.
5. **HbNoSweep_HarmonicIsX:** HB `V[node, harmonic]` default slice → `harmonic` is `KeepAsX`, `node` is
   pinned (regression of the new default).
6. **Swept_Unchanged:** `[Pin_avail, node, harmonic]` default → `Pin_avail` is X, `node`/`harmonic`
   pinned (regression).

## Gate (manual)
No-sweep DC run with an IProbe `Iout` and labeled nodes. Add a trace on a **Table**: pick `V` → a node
selector appears; pick a node → its voltage shows. Pick `I` → a branch selector → `Iout`'s current shows.
Type `DC1.I["Iout"]` in the spec box → binds, shows the value (no "swept axis" error). Switch the plot to
Rect → the scalar trace shows the soft `<invalid>` (expected).

## On completion
Note in `src/Ui/DataDisplay/CLAUDE.md`: the node/branch label axis is always a pinned selector; X defaults
to the first non-label axis; a cube with no non-label axis (e.g. no-sweep DC) resolves to a scalar
(Table-only). `CubeTraceSpecParser` accepts fully-pinned (zero-X) specs as scalars.
