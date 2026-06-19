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
///   "V"                Real, axis [node] (Values 0..n-1, Unit "V", Labels = net names).
///   "I"                Real, axis [branch] (Labels = probe names) — unified branch current cube.
///   "Converged"        scalar 1.0/0.0.
///   "Residual"         scalar (final ‖F‖).
///   "__ProbeBranches"  provenance: probe-named subset of the branch axis (branch-picker filter).
///   "__LabeledNodes"   provenance: user-named nets (node-picker filter). "__"-prefixed ⇒
///                      StackSweepAxis passes it through sweep-invariantly.
/// A standalone DC run yields scalars per node/probe (an operating-point table); wrapping DC in a
/// ParametricSweep prepends the sweep axes via StackSweepAxis → a plottable [sweep…, node] V cube
/// and [sweep…, branch] I cube.
/// </summary>
public static class DcResultPacker
{
    public static DataSet Pack(NonlinearDcEngine.DcResult dc, ElaboratedNetlist nl)
    {
        int n = dc.NodeVoltages.Length;
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

        if (dc.ProbeCurrents.Count > 0)
        {
            var bNames     = dc.ProbeCurrents.Keys.ToArray();
            var bVals      = Enumerable.Range(0, bNames.Length).Select(i => (double)i).ToArray();
            var branchAxis = new Axis("branch", bVals, "A", bNames);
            var iVals      = bNames.Select(n => dc.ProbeCurrents[n]).ToArray();
            ds.Add("I", new DataCube([branchAxis], iVals));

            var pIdx = Enumerable.Range(0, bNames.Length).Select(i => (double)i).ToArray();
            ds.Add("__ProbeBranches", new DataCube(
                [new Axis("probe", pIdx, "", bNames)], new double[bNames.Length]));
        }

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
