using CircuitRF.Core.Expressions;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A sensitivity run as one <see cref="DataSet"/> — <c>&lt;design&gt;.sens.npy</c> (brief-sens-1 R-sens1-4; the names
/// are recorded in docs/design/results-dataset-layout.md §"Sensitivity"). Everything is in group <c>sens</c>:
/// <list type="bullet">
/// <item>per goal or scalar measure <c>r</c>, over a <c>contributor</c> axis labelled with the factor keys, largest
/// |Δ| first: <c>contrib:r</c> (the shares, 0–1), <c>contrib:r:shown</c> (the same values — what
/// <c>ContributionParetoPreset</c> draws), <c>contrib:r:cumulative</c>, <c>delta:r</c> (signed Δ), <c>plus:r</c>,
/// <c>minus:r</c>, <c>nominal:r</c>, <c>asymmetry:r</c>, and <c>status:r</c> (its axis labelled
/// <c>ok|nonlinear|one-sided|failed</c>); the scalars <c>spread:r</c> and, when D5 allows it, <c>estimate:r</c>. A
/// goal's values are normalized margins; a measure's are in its own unit, which its cubes carry;</item>
/// <item>over the factors in step order: <c>factors</c> (labelled with the keys), <c>basis</c> (labelled <c>σ</c>,
/// <c>1 %</c>, …), <c>step</c> (labelled with the value text set at + and −) and <c>status</c> (labelled with whether
/// both steps evaluated for every response);</item>
/// <item><c>notes</c> (each note as a label) and the scalar <c>evaluations</c>.</item>
/// </list>
/// A text is an axis label — a cube holds numbers — so each text cube's values are its items' ordinals.
/// </summary>
internal static class SensitivityDataSet
{
    public const string Group = "sens";

    public static DataSet Build(SensitivityRun run, SensitivityResult r)
    {
        var ds = new DataSet();
        foreach (var resp in r.Goals.Concat(r.Measures)) Response(ds, resp);

        var f = run.Factors;
        Text(ds, "factors", "contributor", f.Select(x => x.Key));
        Text(ds, "basis", "contributor", f.Select(x => x.BasisText));
        Text(ds, "step", "contributor", f.Select(x => $"+ {x.Plus} | − {x.Minus}"));
        Text(ds, "status", "contributor", f.Select(x =>
        {
            var all = r.Goals.Concat(r.Measures).Select(resp => resp.Contributors.First(c => c.Factor == x).Status).ToList();
            return all.Count == 0 ? "ok"
                 : all.All(s => s == SensitivityStatus.Failed) ? "failed"
                 : all.Any(s => s is SensitivityStatus.OneSided or SensitivityStatus.Failed) ? "one-sided" : "ok";
        }));
        if (r.Notes.Count > 0) Text(ds, "notes", "note", r.Notes.Select(n => n.Render()));
        ds.AddToGroup(Group, "evaluations", DataCube.Scalar(r.Evaluations));
        return ds;
    }

    private static void Response(DataSet ds, SensitivityResponse resp)
    {
        var cs = resp.Contributors;
        if (cs.Count == 0) return;
        string n = resp.Name;
        var axis = new Axis(ResultContributions.ContributorAxis, [.. Enumerable.Range(1, cs.Count).Select(i => (double)i)], "",
                            [.. cs.Select(c => c.Factor.Label)]);
        string unit = resp.IsGoal || resp.Unit.Length == 0 ? "" : Units.BaseUnit(resp.Unit);
        DataCube Over(IEnumerable<double> v, string u = "") => new([axis], [.. v]) { Unit = u };
        var shares = cs.Select(c => c.Share).ToArray();
        ds.AddToGroup(Group, $"contrib:{n}", Over(shares));
        ds.AddToGroup(Group, $"contrib:{n}:shown", Over(shares));
        ds.AddToGroup(Group, $"contrib:{n}:cumulative", Over(Contributor.ShownCumulative(shares)));
        ds.AddToGroup(Group, $"delta:{n}", Over(cs.Select(c => c.Delta), unit));
        ds.AddToGroup(Group, $"plus:{n}", Over(cs.Select(c => c.Plus), unit));
        ds.AddToGroup(Group, $"minus:{n}", Over(cs.Select(c => c.Minus), unit));
        ds.AddToGroup(Group, $"nominal:{n}", Over(cs.Select(_ => resp.Nominal), unit));
        ds.AddToGroup(Group, $"asymmetry:{n}", Over(cs.Select(c => c.Asymmetry)));
        ds.AddToGroup(Group, $"status:{n}", new DataCube(
            [new Axis(ResultContributions.ContributorAxis, [.. Enumerable.Range(1, cs.Count).Select(i => (double)i)], "",
                      [.. cs.Select(c => SensitivityRun.StatusWord(c.Status))])],
            [.. cs.Select(c => (double)(int)c.Status)]));
        var spread = DataCube.Scalar(resp.Spread);
        spread.Unit = unit;
        ds.AddToGroup(Group, $"spread:{n}", spread);
        if (resp.Estimate is { } e) ds.AddToGroup(Group, $"estimate:{n}", DataCube.Scalar(e));
    }

    private static void Text(DataSet ds, string name, string axisName, IEnumerable<string> texts)
    {
        var list = texts.ToList();
        if (list.Count == 0) return;
        var ordinals = Enumerable.Range(1, list.Count).Select(i => (double)i).ToArray();
        ds.AddToGroup(Group, name, new DataCube([new Axis(axisName, ordinals, "", [.. list])], ordinals));
    }
}
