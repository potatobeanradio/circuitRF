using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;

namespace CircuitRF.Engine;

/// <summary>
/// Executes a <see cref="ParametricSweepAnalysis"/> by re-elaborating and running the inner
/// analysis for each sweep point, then stacking the resulting DataSets along a new axis.
///
/// Composable: the inner analysis may itself be a ParametricSweepAnalysis, producing
/// N nested axes. Each nesting level prepends one named axis to every cube.
///
/// Implementation note: the swept global variable is overridden by temporarily mutating
/// TestBench.GlobalVariables (restored in a finally block) so that Elaborator.Elaborate()
/// sees the overridden value without an API change to Elaborator.
/// </summary>
public static class ParametricSweepEngine
{
    /// <summary>
    /// Runs the parametric sweep, re-elaborating from <paramref name="lib"/> at each point.
    /// Returns a DataSet whose every cube has <paramref name="sweep"/>.SweepVarName prepended
    /// as a new axis.
    /// </summary>
    public static DataSet Run(
        ParametricSweepAnalysis sweep,
        Library lib,
        TestBench tb,
        AnalysisSettings? settings = null,
        string? baseDirectory = null)
    {
        // Locate the inner analysis, skipping disabled sweeps (collapse): a disabled inner sweep is
        // transparent — its dimension is dropped and ITS inner runs here instead.
        var inner = AnalysisChain.ResolveEffectiveInner(sweep.InnerAnalysisName, tb)
            ?? throw new InvalidOperationException(
                $"Parametric sweep '{sweep.Name}': inner analysis " +
                $"'{sweep.InnerAnalysisName}' not found (or its chain is disabled).");

        // Find the variable in GlobalVariables so we can restore it.
        int varIdx   = tb.GlobalVariables.FindIndex(v => v.Name == sweep.SweepVarName);
        var origVar  = varIdx >= 0 ? tb.GlobalVariables[varIdx] : null;

        // Effective unit = the unit Brief 2 scaled by: the sweep's Spec.Unit, else the VAR's
        // declared unit. BaseUnit reduces it to scale-1 (e.g. "GHz"→"Hz") so injecting it leaves
        // the value unchanged while marking the variable as unit-bearing (var-unit-wins, Part A).
        string effUnit  = sweep.Spec?.Unit is { Length: > 0 } su ? su : (origVar?.Unit ?? "");
        string baseUnit = Units.BaseUnit(effUnit);

        var datasets = new List<DataSet>(sweep.SweepValues.Length);

        for (int si = 0; si < sweep.SweepValues.Length; si++)
        {
            double val = sweep.SweepValues[si];
            // SweepValues are already in base SI (scaled by ParametricSweepAnalysis spec ctor).
            // Attach the base unit (scale-1) so the Elaborator calls MarkGlobalHasUnit, which
            // puts the variable into GlobalsWithExplicitUnit → FreqUnit.ResolveHz fires
            // var-unit-wins → ToneUnit/site-unit is not re-applied (fixes swept-freq double-unit).
            // When effUnit is empty (no sweep unit, no VAR unit), baseUnit="" → override stays
            // unit-less (unmarked), values were never scaled, use sites apply their unit once.
            var overrideVar = new Variable(
                sweep.SweepVarName,
                val.ToString("G17", CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(baseUnit) ? null : baseUnit);

            // Inject override into GlobalVariables (add if absent).
            if (varIdx >= 0)
                tb.GlobalVariables[varIdx] = overrideVar;
            else
            {
                tb.GlobalVariables.Add(overrideVar);
                varIdx = tb.GlobalVariables.Count - 1;
            }

            try
            {
                var netlist = new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
                datasets.Add(RunInner(inner, lib, tb, netlist, settings, baseDirectory));
            }
            finally
            {
                // Restore original variable (or remove if it was added).
                if (origVar is not null)
                    tb.GlobalVariables[varIdx] = origVar;
                else
                {
                    tb.GlobalVariables.RemoveAt(varIdx);
                    varIdx = -1;  // re-search on next iteration if needed
                }
            }
        }

        // Build sweep axis; tag with base SI unit so marker readouts show "freq=2 GHz".
        // SweepValues are already in base SI — the unit tag is for display only.
        // Use the same baseUnit computed above (prefers Spec.Unit over origVar.Unit).
        var sweepAxis = new Axis(sweep.SweepVarName, sweep.SweepValues, baseUnit);
        return DataSet.StackSweepAxis(sweepAxis, datasets);
    }

    // ── Inner dispatch ────────────────────────────────────────────────────────

    private static DataSet RunInner(
        Analysis inner,
        Library lib,
        TestBench tb,
        ElaboratedNetlist netlist,
        AnalysisSettings? settings,
        string? baseDirectory)
    {
        switch (inner)
        {
            case HarmonicBalanceAnalysis hba:
                var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
                return (DataSet)new HbEngine(netlist, tb, settings).Run(p);

            case SParameterAnalysis spa:
                return RunSParam(spa, netlist, settings);

            case DcAnalysis dca:
                return RunDc(dca, netlist, settings);

            case ParametricSweepAnalysis psa:
                // Recursive: outer override already injected in tb.GlobalVariables.
                // This call re-elaborates for each of its own sweep values on top of that.
                return Run(psa, lib, tb, settings, baseDirectory);

            default:
                throw new NotSupportedException(
                    $"ParametricSweepEngine: inner analysis type " +
                    $"'{inner.GetType().Name}' is not supported. " +
                    $"Supported: HarmonicBalanceAnalysis, SParameterAnalysis, DcAnalysis, ParametricSweepAnalysis.");
        }
    }

    // ── Per-inner-type helpers ────────────────────────────────────────────────

    private static DataSet RunSParam(
        SParameterAnalysis spa,
        ElaboratedNetlist  netlist,
        AnalysisSettings?  settings)
    {
        var freqs = spa.Expand(netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        return SParameterEngine.Run(netlist, freqs, settings);
    }

    private static DataSet RunDc(
        DcAnalysis        _,
        ElaboratedNetlist netlist,
        AnalysisSettings? settings)
    {
        var result = NonlinearDcEngine.Run(netlist, settings);
        return DcResultPacker.Pack(result, netlist);
    }
}
