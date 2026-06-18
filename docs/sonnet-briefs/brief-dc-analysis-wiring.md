# Sonnet Brief — Wire DC Analysis into the in-app run dispatcher

## The bug
Adding a DC Analysis and running **Simulate → Run** reports **"No supported analysis dispatched."**

## Root cause (confirmed on disk)
`SchematicRunService.RunTypedAnalysis` has a `case DcAnalysis:` that deliberately does nothing — it
adds a note and returns `null`:
```csharp
case DcAnalysis:
    notes.Add($"DC analysis '{analysis.Name}': not wired in-app yet — use CLI dc command.");
    return null;
```
When DC is the only analysis, `results` stays empty and `RunNetlist` returns
`RunStatus.NoAnalysis` with "No supported analysis dispatched." (The note about "use CLI dc command"
is also stale — the CLI `dc` command is itself a stub that prints "DC solve not yet implemented".)

The DC engine itself is real and tested: `CircuitRF.Engine.NonlinearDcEngine.Run(nl, settings)` returns
a `NonlinearDcEngine.DcResult` (node voltages + convergence + residual + trace). It just needs to be
called and its result packed into a `DataSet` (the engine returns `DcResult`, not a `DataSet`, so unlike
the other analyses we must build the DataSet here).

## Fix — call the engine and pack a DataSet

### Step 1 — dispatch DC
In `SchematicRunService.RunTypedAnalysis`, replace the no-op `case DcAnalysis:` with:
```csharp
case DcAnalysis:
{
    var dc = NonlinearDcEngine.Run(nl);   // AnalysisSettings.Default — DcAnalysis carries no overrides
    notes.Add($"DC '{analysis.Name}': {(dc.Converged ? "converged" : "did NOT converge")} " +
              $"in {dc.Iterations} iter, residual={dc.FinalResidual:G3}");
    return BuildDcDataSet(dc, nl);
}
```
Keep the `default:` arm as-is.

### Step 2 — add the DataSet builder
Add this private helper to `SchematicRunService` (it already `using`s `RfCore.Data`,
`CircuitRF.Engine`, `CircuitRF.Core.Elaboration`):
```csharp
// ── DC result → DataSet ───────────────────────────────────────────────────
//
// DC is a single operating point: one real voltage per non-ground node.
// Shape mirrors the HB convention so the Data Display node-picker and the
// V("nodeName") accessor work:
//   • "V"  — Real, one axis "node" (Values 0..n-1, Labels = node names).
//   • "Converged" / "Residual" — scalar cubes (same names HB uses).
//   • "__LabeledNodes" — provenance cube listing user-named nets, so the
//     node-picker filters to friendly names (RebuildSignals reads this).
// A standalone DC run has no sweep axis, so a node slices to a scalar (a
// table of operating-point voltages). Wrapping DC in a ParametricSweep
// prepends the sweep axis via the existing sweep path → a plottable curve.
private static DataSet BuildDcDataSet(NonlinearDcEngine.DcResult dc, ElaboratedNetlist nl)
{
    int n = dc.NodeVoltages.Length;   // non-ground nodes; node k → circuit node k+1

    var nodeVals  = new double[n];
    var nodeNames = new string[n];
    for (int k = 0; k < n; k++)
    {
        nodeVals[k]  = k;
        nodeNames[k] = nl.Nodes.NameOf(k + 1);   // +1: skip ground (node 0)
    }
    var nodeAxis = new Axis("node", nodeVals, "V", nodeNames);

    var ds = new DataSet();
    ds.Add("V",         new DataCube([nodeAxis], (double[])dc.NodeVoltages.Clone()));
    ds.Add("Converged", DataCube.Scalar(dc.Converged ? 1.0 : 0.0));
    ds.Add("Residual",  DataCube.Scalar(dc.FinalResidual));

    // Provenance: which node-axis entries came from a user net label (node-picker filter).
    // Mirrors HbEngine.BuildSingleToneDataSet.
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
```

That's the whole fix — one dispatch arm + one builder. No engine changes, no model changes.

## STOP-and-verify before building
- Confirm the `using` directives already present in `SchematicRunService.cs` cover `RfCore.Data`
  (`DataSet`/`DataCube`/`Axis`), `CircuitRF.Engine` (`NonlinearDcEngine`), and
  `CircuitRF.Core.Elaboration` (`ElaboratedNetlist`). They are all already imported at the top of the
  file — no new `using` needed. (`System.Linq` is also already imported for the `.Where(...)`.)
- Confirm `NonlinearDcEngine.Run(ElaboratedNetlist)` is the single-arg overload (it is:
  `Run(ElaboratedNetlist netlist, AnalysisSettings? settings = null)`), and `DcResult` exposes
  `NodeVoltages` (double[]), `Converged` (bool), `Iterations` (int), `FinalResidual` (double) — all
  public. Confirmed against `NonlinearDcEngine.cs`.
- Confirm `nl.Nodes` is a `NodeMap` with `NameOf(int)` (1-based, 0 = ground) and a
  `LabeledNames` `HashSet<string>`. Confirmed against `NodeMap.cs`.
- `DcAnalysis` carries only `Name`/`Enabled` (no settings), so `AnalysisSettings.Default` is correct.
  Confirmed against `Analysis.cs`.

## Gate / manual checks (build 0W/0E)
1. Build clean (`TreatWarningsAsErrors=true`).
2. In-app: add a DC Analysis to a schematic with a DC operating point (e.g. the resistor-divider or a
   biased FET), Simulate → Run. The run completes with a Success message like
   `DC 'DC1': converged in N iter, residual=…` instead of "No supported analysis dispatched."
3. The produced DataSet has a `V` cube whose `node` axis Labels are the net names; node voltages match
   the expected operating point (e.g. resistor divider Vdd=10, R1=30, R2=20 → V(n1)=4 V).
4. The Data Display node-picker shows user-named nets for the DC result (via `__LabeledNodes`), same as
   an HB result.
5. Existing analyses (S-param, HB, loadpull, parametric sweep) still dispatch unchanged.

## Suggested test (mirrors existing `SchematicRunServiceTests` style)
Write a `.cnl` with a DC operating point + a `type=dc` analysis directive, call
`SchematicRunService.RunNetlist(path)`, assert `Status == RunStatus.Success`, the result contains a `V`
cube, and a known node voltage is correct. (Reuse the resistor-divider from `NonlinearDcTests`:
`PureResistorDivider_MatchesAnalytic`. If the `.cnl` analysis-directive spelling for DC isn't obvious,
check `AnalysisSerialization.cs` / `CnlReader` for how a `DcAnalysis` is written/parsed — confirm a DC
directive actually round-trips into `tb.Analyses` as a `DcAnalysis`, since that's required for the
dispatcher to see it. If DC has **no** directive spelling yet, that's a second gap — report it before
implementing, as it would need a small reader/writer addition.)
```
