using System.Globalization;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The one-line account of what a parametric sweep is actually about to simulate.
///
/// <para><b>It has to walk the CHAIN, and that is the whole point.</b> Only the outermost sweep is
/// dispatched — every sweep below it runs inside <c>ParametricSweepEngine</c>'s own re-elaboration
/// loop and never reaches the dispatcher, so describing the dispatched analysis alone reports one
/// axis for a run that has several. A user sweeping VDS inside VGS was told the VGS count and given
/// no indication that each of those points is itself a VDS sweep.</para>
///
/// <para>Outermost first, because that is the order the axes are actually traversed in: the first
/// listed is the slow one, and the last is the one a plot puts on its X axis.</para>
///
/// <para><b>A disabled sweep contributes nothing</b> — <see cref="AnalysisChain"/>'s own rule is that
/// it collapses and its axis is dropped, so counting it would report points that are never simulated.
/// The chain is still descended through it to reach whatever it wraps.</para>
/// </summary>
public static class ParametricSweepRunSummary
{
    /// <summary>Matches <c>SchematicRunService.RootInnerName</c>'s own bound — a malformed chain must
    /// not be walked forever, and no real design nests anywhere near this deep.</summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Every enabled axis in <paramref name="top"/>'s chain, outermost first.
    /// </summary>
    public static IReadOnlyList<(string Variable, int Points)> Axes(ParametricSweepAnalysis top, TestBench tb)
    {
        var axes = new List<(string, int)>();
        Analysis? cur = top;

        for (int i = 0; cur is ParametricSweepAnalysis ps && i < MaxDepth; i++)
        {
            if (ps.Enabled) axes.Add((ps.SweepVarName, ps.SweepValues.Length));
            cur = string.IsNullOrEmpty(ps.InnerAnalysisName)
                ? null
                : tb.Analyses.FirstOrDefault(
                      a => a.Name.Equals(ps.InnerAnalysisName, StringComparison.OrdinalIgnoreCase));
        }

        return axes;
    }

    /// <summary>
    /// How many leaf sweep points the chain will actually simulate — the product of every enabled
    /// axis's own count. This is the SAME walk the message below reports, so a progress denominator
    /// and the sentence describing it can never disagree about the size of the run.
    /// <para/>
    /// Saturating rather than wrapping: a chain deep enough to overflow is already a design nobody is
    /// going to run, and a negative point count is a worse thing to carry than a very large one.
    /// </summary>
    public static long TotalPoints(ParametricSweepAnalysis top, TestBench tb)
    {
        var axes = Axes(top, tb);
        if (axes.Count == 0) return 0;

        long total = 1;
        foreach (var (_, points) in axes)
        {
            if (points <= 0) return 0;
            if (total > long.MaxValue / points) return long.MaxValue;
            total *= points;
        }
        return total;
    }

    /// <summary>
    /// The note itself. One axis reports only its own count — there is no product to state, and
    /// "= 101 total" beside "101 pt(s)" is the same number said twice.
    /// </summary>
    public static string Describe(ParametricSweepAnalysis top, TestBench tb)
    {
        var axes = Axes(top, tb);
        if (axes.Count == 0) return $"Parametric sweep '{top.Name}': no enabled sweep axis";

        string per = string.Join(" x ", axes.Select(
            a => $"{a.Points} pt(s) over {a.Variable}"));

        if (axes.Count == 1) return $"Parametric sweep '{top.Name}': {per}";

        return $"Parametric sweep '{top.Name}': {per} = " +
               $"{TotalPoints(top, tb).ToString("N0", CultureInfo.InvariantCulture)} total pt(s)";
    }
}
