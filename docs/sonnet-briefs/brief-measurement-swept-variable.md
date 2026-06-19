# Sonnet Brief — measurements respect parametric sweeps (swept variable → cube)

Bug: a measurement that references a swept variable directly produces a 1-element result instead of
one element per sweep point. Repro: parametric HB sweep over `Pin` (NumPoints=10), measurement
`Pin_avail_dBm = Pin` → result cube has 1 element, expected 10.

Root cause: `MeasurementEvaluator` injects `_netlist.ResolvedGlobals` as **scalars**, and the swept
variable `Pin` is one of them (its base value), so `Pin` resolves to a single number. Meanwhile
`ParametricSweepEngine.Run` builds the sweep axis as `new Axis(sweep.SweepVarName, sweep.SweepValues)`
— so the result cubes carry a prepended axis literally named `"Pin"` with the 10 values.

Fix: inject each **swept** variable as a 1-D cube over its sweep axis (name + values read from the
results), overriding the scalar global. Since the injected cube's axis is named `Pin` with the same
values, the swept measurement aligns/broadcasts with swept analysis cubes too (e.g.
`Gain = HB1.V("out",1,All) - Pin`). Plus a small `ApplyUnit` hardening so cube-valued measurements
with a declared unit don't throw.

Scope: `src/Engine/MeasurementEvaluator.cs` (inject swept cubes), `src/Core/Expressions/Evaluator.cs`
(`ApplyUnit` handles cubes) + tests + a `measurements.md` note. Build 0W/0E
(`TreatWarningsAsErrors=true`); tests green.

Read first: `MeasurementEvaluator.Evaluate`, `ParametricSweepEngine.Run` (axis = `SweepVarName` +
`SweepValues`), and `Evaluator.ApplyUnit`.

## 1. Inject swept variables as cubes (`MeasurementEvaluator.Evaluate`)

In `Evaluate`, **after** the existing globals loop (the `foreach (var (name, value) in
_netlist.ResolvedGlobals)` block), add:

```csharp
// Swept variables: a parametric sweep prepends an axis named after its SweepVarName, carrying the
// swept values (ParametricSweepEngine: new Axis(SweepVarName, SweepValues)). Inject each as a 1-D cube
// so a measurement can reference the sweep variable directly (e.g. "Pin_avail_dBm = Pin") and get one
// element per sweep point — and so it broadcast-aligns (same axis name+values) with swept analysis
// cubes. This OVERRIDES the scalar global injected above.
var sweptVarNames = new HashSet<string>(StringComparer.Ordinal);
foreach (var a in _tb.Analyses)
    if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.SweepVarName))
        sweptVarNames.Add(ps.SweepVarName);

if (sweptVarNames.Count > 0)
{
    // Take the actual axis (name + values) from the results — authoritative even when a sweep was
    // disabled/collapsed (its axis simply won't be present, so it stays a scalar).
    var sweepAxes = new Dictionary<string, Axis>(StringComparer.Ordinal);
    foreach (var ds in _analysisResults.Values)
        foreach (var (_, cube) in ds.Cubes)
            foreach (var ax in cube.Axes)
                if (sweptVarNames.Contains(ax.Name) && !sweepAxes.ContainsKey(ax.Name))
                    sweepAxes[ax.Name] = ax;

    foreach (var (name, ax) in sweepAxes)
    {
        var sweepCube = new DataCube([new Axis(name, ax.Values)], (double[])ax.Values.Clone());
        globalScope.Bind(name, "0");                                  // ensure Lookup succeeds
        eval.InjectResolved("globals", name, new Value(sweepCube));   // override the scalar global
    }
}
```

Notes:
- `InjectResolved("globals", name, …)` overwrites the memo key `globals::name`, so the cube wins over
  the scalar bound earlier. `globalScope.Bind` is required for `Resolve`'s `Lookup` to find the name in
  the `globals` scope (re-binding is idempotent).
- Reading the axis from `_analysisResults` (not from `SweepValues` directly) is deliberate: a disabled
  inner sweep is collapsed and its axis won't appear in the results, so we correctly leave that variable
  a scalar. Gating on `sweptVarNames` avoids matching a non-sweep axis (node/harmonic/branch/freq/…).
- Nested sweeps just work: each level's axis is found and injected as its own 1-D cube.
- `ParametricSweepAnalysis`, `Axis`, `DataCube`, `Value` are all already in scope
  (`CircuitRF.Core.Design`, `RfCore.Data`, `CircuitRF.Core.Expressions`).

## 2. `ApplyUnit` handles cube values (`Evaluator.ApplyUnit`)

Today the tail does `v.Kind == Real ? … : new Value(v.AsComplex() * scale)`, which **throws** for a
cube (`AsComplex()` on a cube). A swept/derived measurement is a cube, so a declared unit
(`Pin_avail_dBm = Pin [dBm]`, `Gain = … [dB]`) would crash. Add a cube branch before the Real/Complex
return:
```csharp
if (v.Kind == ValueKind.Cube)
    return scale == 1.0 ? v : Value.Mul(v, new Value(scale));
```
Measurement-style units (`dBm`, `dB`, `V`, `A`, `W`, …) are identity-scale (`scale == 1.0`) → the cube
passes through unchanged; a linear-scale unit (`uF`, `GHz`, …) scales the cube via the existing
cube×scalar arithmetic. (This mirrors how `0.5 * HB1.V(...)` already scales a cube.)

## Tests (`tests/Engine.Tests` — measurement evaluator)
1. **SweptVar_IsCube:** HB sweep over `Pin` with 10 points + measurement `M = Pin` → result cube rank-1,
   axis name `"Pin"`, 10 values equal to the sweep values.
2. **SweptVar_Aligns:** `M = HB1.V("out", 1, All) - Pin` evaluates to a rank-1 `[Pin]` cube (broadcast by
   matching axis name+values), 10 elements — no shape error.
3. **NestedSweep:** outer `Pa` (3 pts) over inner `Fb` (4 pts) → `M = Pa` is `[Pa]` (3),
   `M2 = Fb` is `[Fb]` (4).
4. **NoSweep_StillScalar:** a non-swept global referenced in a measurement stays a scalar (rank-0).
5. **DisabledSweep_Collapsed:** when the swept analysis is disabled/collapsed (no `Pin` axis in
   results), `M = Pin` falls back to the scalar global (rank-0) — no crash.
6. **CubeMeasurement_WithUnit:** `M = Pin [dBm]` (cube + identity-scale unit) evaluates without throwing
   and returns the 10-element cube unchanged; a linear-scale unit scales the cube values.

## Gate (manual)
The reported case: parametric HB sweep over `Pin` (10 pts) + `Pin_avail_dBm = Pin` → the
`measurements` group's `Pin_avail_dBm` cube has 10 elements over a `Pin` axis and plots as a curve
(e.g. on a Rect plot with `Pin` as X). Mixing the sweep var with analysis data
(`Gain = dB(HB1.V("out",1,All)) - Pin`) also resolves to a swept curve.

## On completion
Add to `docs/design/measurements.md` (near "Referencing analysis cubes" or "Reference contract"): a
**swept variable** referenced by name in a measurement resolves to a 1-D cube over its sweep axis (same
name + values as the swept analysis cubes), so it has one element per sweep point and broadcasts against
swept analysis data; a non-swept global stays a scalar. Note that this overrides the scalar global for
the duration of measurement evaluation.
