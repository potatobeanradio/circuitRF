// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 R-sdl6-3 — an analysis run by EM keeps its own name (SP1), so its measures, goals and traces work unchanged;
// what it carries besides is a record of what produced it: one metadata cube, as TunedProvenance's, whose axis labels say
// the model, the cell and setup, and the cache key of every geometry it was solved at. The __ prefix keeps it out of
// every trace picker; a Data Display trace reads it to label the trace "EM".

using CircuitRF.Core.Design;
using RfCore.Data;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

public static class EmResultStamp
{
    public const string CubeName = "__EmModel";
    public const string AxisName = "em";

    /// <summary>A copy of <paramref name="data"/> carrying the record; the cubes themselves are shared, not copied.</summary>
    public static DataSet Stamp(DataSet data, EmSwitched em, IReadOnlyList<EmEvaluationRecord> records)
    {
        var labels = new List<string> { "model = " + Analysis.ModelToken(AnalysisModel.Em) };
        if (em.PathA is { } a)
        {
            labels.Add("cell = " + a.CellName);
            labels.Add("setup = " + Path.GetFileNameWithoutExtension(a.CemPath));
        }
        foreach (var r in records.Where(r => r.Key is not null).GroupBy(r => (r.InstancePath, r.Key)).Select(g => g.First()))
            labels.Add($"{r.InstancePath} = {r.Key}{(r.Cached ? " (cached)" : "")}");

        var stamped = new DataSet();
        foreach (var group in data.Groups)
            foreach (var (name, cube) in data.CubesIn(group))
                if (!(group == DataSet.DefaultGroup && name == CubeName))
                    stamped.AddToGroup(group, name, cube);
        var index = Enumerable.Range(0, labels.Count).Select(i => (double)i).ToArray();
        stamped.Add(CubeName, new DataCube([new Axis(AxisName, index, "", [.. labels])], index));
        return stamped;
    }

    /// <summary>The record lines a stamped DataSet carries; empty for a result the circuit models produced.</summary>
    public static IReadOnlyList<string> Read(DataSet data) =>
        data.Contains(CubeName) && data[CubeName].Axes is [var axis] && axis.Labels is { } labels ? labels : [];

    /// <summary>Whether <paramref name="data"/>, or its group <paramref name="group"/>, was produced by EM.</summary>
    public static bool IsEm(DataSet data, string? group = null) =>
        group is null ? data.Contains(CubeName) : data.CubesIn(group).ContainsKey(CubeName);
}
