using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
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
        AnalysisSettings? settings = null)
    {
        // Locate the inner analysis by name.
        var inner = tb.Analyses.FirstOrDefault(a => a.Name == sweep.InnerAnalysisName)
            ?? throw new InvalidOperationException(
                $"Parametric sweep '{sweep.Name}': inner analysis " +
                $"'{sweep.InnerAnalysisName}' not found in TestBench.");

        // Find the variable in GlobalVariables so we can restore it.
        int varIdx   = tb.GlobalVariables.FindIndex(v => v.Name == sweep.SweepVarName);
        var origVar  = varIdx >= 0 ? tb.GlobalVariables[varIdx] : null;

        var datasets = new List<DataSet>(sweep.SweepValues.Length);

        for (int si = 0; si < sweep.SweepValues.Length; si++)
        {
            double val = sweep.SweepValues[si];
            var overrideVar = new Variable(
                sweep.SweepVarName,
                val.ToString("G17", CultureInfo.InvariantCulture));

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
                var netlist = new Elaborator(lib).Elaborate(tb);
                datasets.Add(RunInner(inner, lib, tb, netlist, settings));
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

        // Build sweep axis using the exact values provided.
        var sweepAxis = new Axis(sweep.SweepVarName, sweep.SweepValues);
        return DataSet.StackSweepAxis(sweepAxis, datasets);
    }

    // ── Inner dispatch ────────────────────────────────────────────────────────

    private static DataSet RunInner(
        Analysis inner,
        Library lib,
        TestBench tb,
        ElaboratedNetlist netlist,
        AnalysisSettings? settings)
    {
        switch (inner)
        {
            case HarmonicBalanceAnalysis hba:
                var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
                return (DataSet)new HbEngine(netlist, tb, settings).Run(p);

            case SParameterAnalysis spa:
                return RunSParam(spa, netlist, settings);

            case DcAnalysis dca:
                return RunDc(dca, netlist, settings);

            case ParametricSweepAnalysis psa:
                // Recursive: outer override already injected in tb.GlobalVariables.
                // This call re-elaborates for each of its own sweep values on top of that.
                return Run(psa, lib, tb, settings);

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
        var freqs = spa.Expand(netlist.ResolvedGlobals);
        return SParameterEngine.Run(netlist, freqs, settings);
    }

    private static DataSet RunDc(
        DcAnalysis        _,
        ElaboratedNetlist netlist,
        AnalysisSettings? settings)
    {
        var result = NonlinearDcEngine.Run(netlist, settings);

        // Pack node voltages into a V cube with a node axis carrying net names.
        int nodeCount = result.NodeVoltages.Length;  // non-ground nodes (circuit nodes 1..n)
        var nodeVals  = new double[nodeCount];
        var nodeNames = new string[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            nodeVals[i]  = i;
            nodeNames[i] = netlist.Nodes.NameOf(i + 1);
        }
        var nodeAxis = new Axis("node", nodeVals, "", nodeNames);

        var ds = new DataSet();
        ds.Add("V", new DataCube([nodeAxis], result.NodeVoltages));
        return ds;
    }
}
