using System.Linq;

namespace CircuitRF.Core.Design;

/// <summary>
/// Resolves parametric-sweep chains honoring <see cref="Analysis.Enabled"/>.
/// A disabled sweep "collapses": its axis is dropped and its own inner is adopted in its place.
/// A disabled base analysis makes the whole chain inert.
/// The chain is linked by <see cref="ParametricSweepAnalysis.InnerAnalysisName"/>.
/// </summary>
public static class AnalysisChain
{
    private const int MaxDepth = 64;   // cycle guard

    private static Analysis? Find(string name, TestBench tb)
        => tb.Analyses.FirstOrDefault(x => x.Name == name);

    /// <summary>
    /// The next analysis to actually run when descending into <paramref name="innerName"/>, skipping
    /// disabled parametric sweeps. Returns the first ENABLED sweep or ANY base analysis reached, or null
    /// if the name resolves to nothing.
    /// </summary>
    public static Analysis? ResolveEffectiveInner(string innerName, TestBench tb)
    {
        Analysis? a = Find(innerName, tb);
        int guard = 0;
        while (a is ParametricSweepAnalysis ps && !a.Enabled && guard++ < MaxDepth)
            a = Find(ps.InnerAnalysisName, tb);
        return a;
    }

    /// <summary>
    /// From a chain root, descend past disabled OUTER sweeps to the outermost analysis that runs
    /// (an enabled sweep, or a base). Null if it runs off the end.
    /// </summary>
    public static Analysis? ResolveEffectiveTop(Analysis root, TestBench tb)
    {
        Analysis? a = root;
        int guard = 0;
        while (a is ParametricSweepAnalysis ps && !a.Enabled && guard++ < MaxDepth)
            a = Find(ps.InnerAnalysisName, tb);
        return a;
    }

    /// <summary>
    /// True when <paramref name="top"/> bottoms out at an ENABLED base analysis after skipping disabled
    /// sweeps. A disabled base ⇒ the whole chain is inert ⇒ false.
    /// </summary>
    public static bool IsChainRunnable(Analysis top, TestBench tb)
    {
        Analysis? a = top;
        int guard = 0;
        while (a is ParametricSweepAnalysis ps && guard++ < MaxDepth)
            a = ResolveEffectiveInner(ps.InnerAnalysisName, tb);
        return a is { Enabled: true };
    }
}
