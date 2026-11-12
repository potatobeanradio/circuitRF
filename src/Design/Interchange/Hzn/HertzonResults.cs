// HZN-8 R-hzn8-2 — a Hertzon run's Touchstone as circuitRF's own result: the S DataSet every EM run hands the Data Display,
// plus a `hertzon` group carrying what Hertzon said of the solve. Hertzon's solution stays in <project>.hznresults/ and is
// never written to; circuitRF reads the Touchstone that -o named and writes its own .npy beside the workspace's results.

using System.Text.Json.Nodes;
using RfCore;
using RfCore.Data;
using CircuitRF.Design.Results;

namespace CircuitRF.Design.Interchange.Hzn;

public static class HertzonResults
{
    /// <summary>The diagnostics group a Hertzon result carries beside S.</summary>
    public const string Group = "hertzon";

    /// <summary>The stem of the Touchstone and the .npy a run of <paramref name="design"/> writes. Predictable, so a schematic's
    /// SnP reference to the Touchstone survives a re-run.</summary>
    public static string ResultKey(string design) => ResultsWriter.SanitizeFileNameComponent(design) + "_hertzon";

    /// <summary>Where a run of a project's design puts its results by default: the nearest workspace's <c>results</c> folder,
    /// else a <c>results</c> folder beside the project.</summary>
    public static string ResultsFolder(string project)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(project))!;
        string? cws = CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(dir);
        return Path.Combine(cws is null ? dir : Path.GetDirectoryName(cws)!, "results");
    }

    /// <summary>The Touchstone path a run writes, before its port count is known: <c>…_hertzon.s&lt;rows&gt;p</c>.</summary>
    public static string TouchstonePath(string resultsFolder, string design, int rows)
        => Path.Combine(resultsFolder, ResultKey(design) + $".s{rows}p");

    /// <summary>S and Z0 from the Touchstone, and Hertzon's facts in the <see cref="Group"/> group.</summary>
    public static DataSet Build(string touchstone, HertzonRunResult run)
    {
        var ds = DataSetBuilder.FromSnp(TouchstoneIO.ReadFile(touchstone, readComments: false));
        void Scalar(string name, double? v)
        {
            if (v is { } x && double.IsFinite(x)) ds.AddToGroup(Group, name, DataCube.Scalar(x));
        }
        Scalar("Passes", run.Passes);
        Scalar("Tetrahedra", run.Tetrahedra);
        Scalar("Seconds", run.Seconds);
        Scalar("Converged", run.Converged is { } c ? (c ? 1 : 0) : null);
        Scalar("Cached", run.Cached ? 1 : 0);
        if (run.Summary?["reference_impedance_ohm"] is JsonValue z && z.TryGetValue<double>(out double z0)) Scalar("ReferenceImpedanceOhm", z0);
        return ds;
    }

    /// <summary>Writes the .npy of <paramref name="data"/> under <paramref name="workspaceDir"/>/results; the path, or null with
    /// <paramref name="error"/>.</summary>
    public static string? WriteNpy(string workspaceDir, string design, DataSet data, out string? error)
    {
        var outcome = ResultsWriter.WriteRun(workspaceDir, ResultKey(design) + "_em", data);
        error = outcome.Error;
        return outcome.Written.Count > 0 ? outcome.Written[0] : null;
    }
}
