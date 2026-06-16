# Sonnet Brief — Sweep Fix 2/5: close the DC/S-param producer gap in ParametricSweepEngine.RunInner

**Goal.** `ParametricSweepEngine` can wrap **any** analysis in a parametric sweep (re-elaborate per point, stack
along a named axis), but `RunInner` only dispatches `HarmonicBalanceAnalysis` + nested `ParametricSweepAnalysis`.
`DcAnalysis` and `SParameterAnalysis` throw `NotSupportedException`. Wire them so DC and S-param can be swept
(e.g. S-params vs a bias/geometry variable, a DC curve-tracer Vds×Vgs).

**Confirmed.** `src/Engine/ParametricSweepEngine.cs:RunInner` switch covers `HarmonicBalanceAnalysis` and
`ParametricSweepAnalysis`; `default` throws. The sweep override + stack machinery (`Run`) is already generic over
any `GlobalVariable` (with the VAR work, any schematic/cell variable is reachable).

## Required reads first (confirm engine entry points + return types)
- The **DC engine** entry: `NonlinearDcEngine.Run(_netlist, settings)` exists (used by HbEngine). Confirm whether
  there is a DC analysis that returns a **DataSet** (sweepable cube), or only the `DcResult` operating point. If
  no DataSet-producing DC analysis exists yet, see "DC dispatch" below.
- The **S-param engine** entry: `SParameterEngine` (you worked on it for the wave-port fix). Confirm its public
  run signature and that it returns a `DataSet` (it writes an `S` cube + `Z0`). Find how the workspace currently
  runs a standalone `SParameterAnalysis` (the non-swept path) — reuse that exact call.

## Changes — `RunInner`
Add cases mirroring the HB case (which does `HbEngine.Resolve` → `new HbEngine(...).Run(p)` → DataSet):

```csharp
case SParameterAnalysis spa:
    return RunSParam(spa, netlist, tb, settings);   // → DataSet with the S/Z0 cubes for THIS sweep point

case DcAnalysis dca:
    return RunDc(dca, netlist, tb, settings);       // → DataSet (see DC dispatch)
```

### S-param dispatch
Implement `RunSParam` by calling the same engine path the workspace uses to run a standalone S-param analysis:
- `var freqs = spa.Expand(netlist.ResolvedGlobals);` (already the flat freq array).
- Run `SParameterEngine` over `freqs` against `netlist` (per-point: the outer `Run` already re-elaborated with
  the swept variable injected, so the network reflects this point's value).
- Return its `DataSet` (the `S` cube + `Z0` etc.). The outer `Run` will `StackSweepAxis` it.
- Confirm the S DataSet's existing axes (freq, port, port) stack cleanly under a prepended sweep axis (the
  stacker prepends one axis to every cube — fine).

### DC dispatch
`NonlinearDcEngine.Run` returns an operating point (`DcResult`), not a DataSet. Two options — **pick based on
what you find; flag to the owner if ambiguous:**
- **(A) If a DataSet-producing DC analysis already exists** (a thin wrapper that packs node voltages / device
  currents into cubes), call it and return its DataSet.
- **(B) If not**, add a minimal `DcResult → DataSet` packer in the engine (cubes: `V` over a `node` axis with
  node-name labels; optionally `I:<branch>` if readily available from `DcResult`). Keep it small and analogous
  to how HB packs `V`/`INl`. A DC curve-tracer then comes from wrapping this in two nested sweeps (Vgs outer,
  Vds inner) — **note:** Vds-as-an-axis means the *innermost* sweep is the Vds variable, so DC itself produces a
  single operating point per point and the sweep axes supply Vds/Vgs. That's the intended design (DC has no
  internal frequency axis). Confirm this matches the §7.3 DC curve-tracer plan and flag if the owner wants DC to
  own an internal Vds sweep instead.

Keep the `default:` throw for genuinely unsupported inner types (e.g. Loadpull, which has its own engine).

## Tests (`tests/Engine.Tests`)
1. **Sweep_SParam_OverVariable:** a 2-port with a resistor `R = Rval`, `Rval` a global; a `ParametricSweepAnalysis`
   over `Rval ∈ {25,50,100}` wrapping an S-param analysis → result `S` cube has a prepended `Rval` axis of length
   3; `S11` at each slice matches a direct S-param run at that `Rval`.
2. **Sweep_Dc_OverVariable:** sweep a bias variable wrapping a DC analysis → `V` cube gains the bias axis; node
   voltage at each slice matches a direct DC solve.
3. **Sweep_Nested_DcCurveTracer (if DC packer added):** Vgs outer × Vds inner → 2 prepended axes; spot-check one
   (Vgs,Vds) cell.
4. **Unsupported_StillThrows:** an inner type with no engine (e.g. Loadpull) still throws a clear message.

## Gate
Build 0W/0E; tests green. Manual: wrap an S-param analysis in a parametric sweep over a VAR variable and run →
the data display shows an `S` cube with the sweep axis (named correctly per Brief 1/3); same for a DC sweep.

## On completion
Note in `src/Engine/CLAUDE.md`: `ParametricSweepEngine.RunInner` now dispatches `SParameterAnalysis` and
`DcAnalysis` (DC packed to a DataSet via {existing wrapper | new minimal packer}), so any analysis can be wrapped
in nested parametric sweeps. Loadpull and other engine-owning analyses remain out of the generic sweep.
