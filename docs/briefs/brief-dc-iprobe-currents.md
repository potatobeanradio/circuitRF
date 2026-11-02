# Sonnet Brief — DC analysis: IProbe branch currents (I:&lt;probe&gt; cubes) + unify the DC DataSet packer

## Goal
Enable a FET I–V family of curves (Id vs Vds, one curve per Vgs) from a nested parametric DC sweep.
The sweep plumbing already works (`ParametricSweepEngine` stacks DC results into a `[Vgs, Vds, node]`
cube — see `ParametricSweepDcSParamTests.Sweep_Nested_DcCurveTracer`), and the family-role renderer
(brief-7.3b) turns a 2-axis cube into N curves. The missing pieces:

1. **No current.** `NonlinearDcEngine.DcResult` exposes only `NodeVoltages`. A curve tracer needs `Id`.
   The DC engine already *solves* for branch currents (voltage-source / IProbe branches are unknowns in
   its state vector `x = [V | I_branch]`) — it just slices them off (`x[.._nodeCount]`) and discards
   them. We will retain the IProbe branch currents and pack them as `I:<probe>` cubes, exactly like HB.
2. **Two divergent DC packers.** `SchematicRunService` (standalone DC — being added in
   `brief-dc-analysis-wiring.md`) and `ParametricSweepEngine.RunDc` (swept DC) build *different* `V`
   cubes. Unify them into one shared helper so both paths produce identical cube shapes (and so the
   swept path carries `__LabeledNodes` → the node-picker shows friendly net names like `n_drain`).

Scope per the user's choice: **IProbe currents only** (not every voltage-source branch). The user adds
an `IProbe` in the drain leg; `I:<probe>` becomes plottable as the family Y.

Files: `src/Engine/NonlinearDcEngine.cs`, a new shared packer, and two call sites
(`src/Engine/ParametricSweepEngine.cs`, `src/Ui/Schematic/SchematicRunService.cs`).
Build 0W/0E (`TreatWarningsAsErrors=true`).

---

## Part A — engine: retain IProbe branch currents on `DcResult`

### A1. Capture the full solution vector
`Solve*` currently returns only `x[.._nodeCount]`. Keep the branch part too. In `DcResult` add a
probe-current map (instance name → DC current), and have the engine populate it from the solved vector.

In `NonlinearDcEngine.DcResult`, add:
```csharp
/// <summary>
/// DC branch current through each IProbe, keyed by the probe's instance path (e.g. "IPd").
/// Sign convention: positive current flows from the probe's first node to its second
/// (IProbe:IPd n_plus n_minus → positive = n_plus → n_minus), matching MnaSystem.AddBranchCurrent.
/// Empty when the circuit has no IProbes.
/// </summary>
public IReadOnlyDictionary<string, double> ProbeCurrents { get; }
```
Extend the internal ctor to accept it (default to an empty dict for existing call sites if you prefer,
but simplest is to pass it explicitly from every return):
```csharp
internal DcResult(double[] v, bool converged, int iters, double residual,
    ConvergenceTrace trace, IReadOnlyDictionary<string, double> probeCurrents)
{
    NodeVoltages  = v;
    Converged     = converged;
    Iterations    = iters;
    FinalResidual = residual;
    Trace         = trace;
    ProbeCurrents = probeCurrents;
}
```

### A2. Extract probe currents from the solved vector
Each `IProbeModel` records `LastBranchIndex` (the absolute matrix row of its branch unknown) on its
Stamp call — and each `ElaboratedComponent` carries its own `Model` instance, so after the constructor's
stamp loop every probe's `LastBranchIndex` is stable and correct (this is the same mechanism HB's
exporter uses via `VdcModel.LastBranchIndex`). The branch unknowns live at `x[_nodeCount .. _systemSize)`,
and `x[LastBranchIndex]` is that branch's current directly.

Add a helper that builds the map from a solved state vector:
```csharp
private IReadOnlyDictionary<string, double> ExtractProbeCurrents(double[] x)
{
    var map = new Dictionary<string, double>(StringComparer.Ordinal);
    foreach (var ec in _nl.Components)
    {
        if (ec.Model is not CircuitRF.Core.Devices.IProbeModel probe) continue;
        int br = probe.LastBranchIndex;
        if (br >= _nodeCount && br < _systemSize)
            map[ec.InstancePath] = x[br];   // branch current, np→nm sign convention
    }
    return map;
}
```
> `using CircuitRF.Core.Devices;` is already imported at the top of `NonlinearDcEngine.cs` (it
> references `PortModel`/`TermModel` in the constructor) — confirm and add only if missing.

### A3. Populate it in every return path
`Solve()` dispatches to `SolveDirect` / `SolveRamped` / `SolveIfNecessary`. Each builds `nodeV =
x[.._nodeCount]` and returns a `DcResult`. At each `return new DcResult(...)`, pass
`ExtractProbeCurrents(x)` (or `xNew` in `SolveDirect`). Concretely:
- `SolveDirect`: the solved vector is `xNew` → `ExtractProbeCurrents(xNew)`.
- `SolveRamped`: the solved vector is `x` at both the success return and the early non-converged return →
  `ExtractProbeCurrents(x)`.
- `SolveIfNecessary`: returns whichever sub-result; no change needed (it forwards a `DcResult` that
  already carries its own probe currents).

(There are exactly three `new DcResult(...)` sites — in `SolveDirect` and the two in `SolveRamped`.
Update all three. `SolveIfNecessary` just returns `direct` or `SolveRamped()`, untouched.)

> Even a non-converged result reports the last-iterate currents — fine; the cube's `Converged` scalar
> tells the user whether to trust them.

## Part B — one shared DC → DataSet packer

Create a single packer both call sites use, so standalone and swept DC produce identical cube sets.
Put it where both the Engine and the UI can call it. `ParametricSweepEngine` (Engine) and
`SchematicRunService` (UI) both already reference the Engine assembly, so add a small public static
class in the Engine project:

`src/Engine/DcResultPacker.cs`:
```csharp
using RfCore.Data;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine;

/// <summary>
/// Packs a nonlinear-DC operating-point result into a DataSet, in the same cube shape the
/// HB engine uses so the Data Display node-picker and the V("name") / I("probe") accessors work.
/// Single source of truth — both the standalone run (SchematicRunService) and the swept run
/// (ParametricSweepEngine.RunDc) call this so their cube shapes are identical and stackable.
///
/// Cubes:
///   "V"            Real, axis [node] (Values 0..n-1, Unit "V", Labels = net names).
///   "I:<probe>"    Real, scalar (0-rank) per IProbe — DC branch current (np→nm sign).
///   "Converged"    scalar 1.0/0.0.
///   "Residual"     scalar (final ‖F‖).
///   "__LabeledNodes"  provenance: user-named nets (node-picker filter). "__"-prefixed ⇒
///                     StackSweepAxis passes it through sweep-invariantly.
/// A standalone DC run yields scalars per node/probe (an operating-point table); wrapping DC in a
/// ParametricSweep prepends the sweep axes via StackSweepAxis → a plottable [sweep…, node] cube and
/// [sweep…] I:<probe> cubes (the family of curves).
/// </summary>
public static class DcResultPacker
{
    public static DataSet Pack(NonlinearDcEngine.DcResult dc, ElaboratedNetlist nl)
    {
        int n = dc.NodeVoltages.Length;          // non-ground nodes; node k → circuit node k+1
        var nodeVals  = new double[n];
        var nodeNames = new string[n];
        for (int k = 0; k < n; k++)
        {
            nodeVals[k]  = k;
            nodeNames[k] = nl.Nodes.NameOf(k + 1);
        }
        var nodeAxis = new Axis("node", nodeVals, "V", nodeNames);

        var ds = new DataSet();
        ds.Add("V",         new DataCube([nodeAxis], (double[])dc.NodeVoltages.Clone()));
        ds.Add("Converged", DataCube.Scalar(dc.Converged ? 1.0 : 0.0));
        ds.Add("Residual",  DataCube.Scalar(dc.FinalResidual));

        // One scalar I cube per IProbe — keyed I:<instancePath>, matching ds.I("probe").
        foreach (var (probeName, current) in dc.ProbeCurrents)
            ds.Add("I:" + probeName, DataCube.Scalar(current));

        // Provenance (node-picker friendly-name filter), "__"-prefixed → sweep-invariant.
        var labeled = nodeNames.Where(nm => nl.Nodes.LabeledNames.Contains(nm)).Distinct().ToArray();
        if (labeled.Length > 0)
        {
            var lIdx = new double[labeled.Length];
            for (int i = 0; i < labeled.Length; i++) lIdx[i] = i;
            ds.Add("__LabeledNodes", new DataCube(
                [new Axis("label", lIdx, "", labeled)],
                new double[labeled.Length]));
        }
        return ds;
    }
}
```

> **Stackability note (important):** `DataSet.StackSweepAxis` stacks every non-`__` cube present in
> all points and throws if one is missing. IProbe set, node set, and the scalars are
> topology-invariant across a parametric sweep (only a global *value* changes, not the netlist), so all
> sweep points produce the same cube keys — they stack cleanly. The scalar `I:<probe>`/`Converged`/
> `Residual` cubes become `[sweep…]`-shaped (per-point), which is exactly what we want for the family.

## Part C — call sites use the shared packer

### C1. `ParametricSweepEngine.RunDc`
Replace its hand-rolled V-only packing with the shared packer:
```csharp
private static DataSet RunDc(DcAnalysis _, ElaboratedNetlist netlist, AnalysisSettings? settings)
{
    var result = NonlinearDcEngine.Run(netlist, settings);
    return DcResultPacker.Pack(result, netlist);
}
```

### C2. `SchematicRunService` (standalone DC)
This depends on `brief-dc-analysis-wiring.md`. Whichever lands, the standalone `case DcAnalysis:` must
call the shared packer instead of an inline builder:
```csharp
case DcAnalysis:
{
    var dc = NonlinearDcEngine.Run(nl);
    notes.Add($"DC '{analysis.Name}': {(dc.Converged ? "converged" : "did NOT converge")} " +
              $"in {dc.Iterations} iter, residual={dc.FinalResidual:G3}");
    return DcResultPacker.Pack(dc, nl);
}
```
If you already implemented `BuildDcDataSet` from the wiring brief, delete it and route through
`DcResultPacker.Pack` so there is exactly one packer.

## STOP-and-verify before building
- `IProbeModel.LastBranchIndex` is `public` and set on Stamp (it is). Each `ElaboratedComponent` has its
  own `Model` instance, so the index is per-probe and stable after the constructor stamp loop. (If you
  find models are shared/pooled, stop and report — the whole approach depends on per-instance
  `LastBranchIndex`. The `VdcModel.LastBranchIndex` doc comment explicitly states it's used to build the
  branch-index↔name map, which confirms per-instance.)
- `_nodeCount` / `_systemSize` are accessible inside `ExtractProbeCurrents` (same class — they are
  private fields). The branch slice is `x[_nodeCount.._systemSize]`.
- DC engine sign convention: `IProbeModel.Stamp` does `AddBranchCurrent(br, np, nm)` → positive current
  np→nm. Document on the cube (done in the XML comment). Confirm against `MnaSystem.AddBranchCurrent`.
- `ds.I("probe")` resolves `I:<probe>` (data model + Evaluator `EvalBranchCurrentAccessor` builds
  `"I:" + branch`). So an IProbe named `IPd` is plotted as `I("IPd")` / cube `I:IPd`. Good.
- Existing `ParametricSweepDcSParamTests` only read `ds["V"]` / `ds["S"]` and never assert cube counts,
  so adding `I:`/scalar/`__LabeledNodes` cubes won't break them. Verify quickly.

## Gate / manual checks (build 0W/0E)
1. Engine unit test (`tests/Engine.Tests/Nonlinear/`): a resistor + `IProbe` in series with a DC source
   → `DcResult.ProbeCurrents["IP1"]` equals V/R within tol, correct sign (np→nm).
2. Swept-DC test (extend `ParametricSweepDcSParamTests`): a FET-like nested sweep with an IProbe in the
   drain leg → `ds["I:IPd"]` is a `[Vgs, Vds]` cube; spot-check a couple of `(Vgs,Vds)` entries.
3. In-app family of curves: schematic with drain IProbe, nested `SW_Vgs ⊃ SW_Vds ⊃ DC1`. In Data
   Display, add a trace on `I:IPd`, set Vds = X (KeepAsX), Vgs = Family → a fan of Id–Vds curves, one
   per Vgs, with the node-picker showing friendly names.
4. Standalone DC (no sweep) still runs and now also reports `I:<probe>` scalars; node-picker shows
   friendly names (now that `__LabeledNodes` is emitted on the standalone path too).
5. Existing S-param / HB / loadpull / parametric-sweep paths unchanged.

## On completion
Note in `src/Engine/CLAUDE.md` (or the DC section): DC now packs IProbe branch currents as scalar
`I:<probe>` cubes (np→nm sign), via the shared `DcResultPacker` used by both the standalone and swept
run paths; family-of-curves = nested parametric DC sweep + IProbe + the 7.3b family role on `I:<probe>`.
