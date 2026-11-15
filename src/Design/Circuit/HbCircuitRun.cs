// brief-em3d-79 R-em3d79-3 — a harmonic-balance chain's run and its measurements: THE function the `hb` verb calls, and
// the one a thermal setup driven from a circuit calls, so a design's HB result is the same number either way (CLAUDE.md: an
// operation one surface re-implements diverges from the other silently).
//
// Two halves, because the verb prints between them (engine warnings, worker output, the WSProbe summary) and its stderr is
// a contract a script may be watching: Solve dispatches the chain — at the SWEEP when a parametric_sweep wraps the HB, since
// running the inner alone drops the sweep axis — and Measure evaluates the TestBench's `measure` lines against the result
// exactly as the GUI does, including the linear back-solver so a measure may name a linear-interior node.

using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;

namespace CircuitRF.Design.Circuit;

/// <summary>An HB chain's solved result: the engine's DataSet, the single-point run (null for a sweep), and the name a
/// measurement qualifies its cubes with — the INNER analysis's, as the GUI names a swept result.</summary>
public sealed record HbCircuitSolve(DataSet Data, HbRunResult? Run, string ResultName);

/// <summary>R-em3d79-3 — an HB chain's run, shared by the <c>hb</c> verb and the thermal circuit link.</summary>
public static class HbCircuitRun
{
    /// <summary>
    /// <c>--set name=expr</c>: the global replaced in the TestBench's own variable scope before elaboration, so everything
    /// derived from it re-derives (overriding a drive level moves the source amplitude computed from it).
    /// </summary>
    public static void ApplySet(TestBench tb, string name, string expr)
    {
        // brief-paa-10 — the value keeps the variable's unit column unless it states its own, as a tuning move does
        // (TunableValue.Split): `--set theta0=30` on `theta0 = 0 deg` is 30 degrees. Replacing it with a bare 30 made it
        // 30 radians to every angle-taking function, and `--set theta0=30 deg` did not parse at all.
        string? unit = tb.GlobalVariables.FirstOrDefault(v => v.Name == name)?.Unit;
        var (expression, split) = CircuitRF.Design.Optimization.TunableValue.Split(expr, unit);
        tb.GlobalVariables.RemoveAll(v => v.Name == name);
        tb.GlobalVariables.Add(new Variable(name, expression, split));
    }

    /// <summary>
    /// The solver settings a run verb gives the engine (the <c>hb</c>, <c>lp</c> and <c>dc</c> verbs share them): the default
    /// iteration caps unless <paramref name="maxIter"/> says otherwise, gmin always applied. <paramref name="pinCurrents"/> —
    /// brief-em3d-79's opt-in — names the linear instances whose pin currents HB adds to its <c>I</c> cube.
    /// </summary>
    public static AnalysisSettings Settings(int? maxIter = null, bool diag = false, IReadOnlyList<string>? pinCurrents = null)
    {
        var d = AnalysisSettings.Default;
        return new AnalysisSettings
        {
            HbMaxIter                 = maxIter ?? d.HbMaxIter,
            NonlinearMaxIter          = maxIter ?? d.NonlinearMaxIter,
            HbConsoleDiagnostics      = diag,
            ConductanceRegularization = RegularizationMode.Always,
            HbPinCurrents             = pinCurrents ?? [],
        };
    }

    /// <summary>The tone plan as the <c>hb</c> verb prints it: <c>f0=2 GHz</c>, or the tones of a multi-tone run.</summary>
    public static string DescribeTones(HbAnalysisParams p)
        => p.IsMultiTone
            ? "tones " + string.Join(", ", p.ToneFreqsHz.Select(f => $"{f / 1e9:G6} GHz"))
            : $"f0={p.ToneHz / 1e9:G6} GHz";

    /// <summary>
    /// Runs chain <paramref name="top"/> — an HB analysis, or a <c>parametric_sweep</c> bottoming out in one — of
    /// <paramref name="tb"/>, elaborated as <paramref name="nl"/>. <paramref name="announce"/> receives the line the verb
    /// prints before the solve. A sweep's per-point diagnostics land in <paramref name="nl"/>.
    /// </summary>
    public static HbCircuitSolve Solve(Library lib, TestBench tb, ElaboratedNetlist nl, Analysis top, AnalysisSettings settings,
                                       string? baseDirectory, RunControl? control = null, Action<string>? announce = null)
    {
        DataSet ds;
        HbRunResult? run = null;
        if (top is ParametricSweepAnalysis psa)
        {
            announce?.Invoke($"HB sweep '{psa.Name}': {psa.SweepValues.Length} point(s) over {psa.SweepVarName}");
            ds = ParametricSweepEngine.Run(psa, lib, tb, settings,
                                           baseDirectory: baseDirectory,
                                           control: control,
                                           // Every point elaborates a netlist of its own and throws it away; without this
                                           // the warnings a caller reports are those of a netlist nothing ever stamped.
                                           diagnosticsInto: nl);
        }
        else
        {
            var hba = (HarmonicBalanceAnalysis)top;
            var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
            announce?.Invoke($"HB '{hba.Name}': {DescribeTones(p)}, MaxHarm={p.MaxHarmonic}" +
                             (p.IsMultiTone ? $", MaxMixOrder={p.MaxMixOrder}" : "") + $", tol={p.Tol:G3}");
            // The library and base directory are handed over so a long WSProbe tickle grid can be split across workers
            // (brief-wsprobe-8 R-wsp8-9) — each worker needs a netlist copy of its own, and a copy needs the library it
            // was elaborated from. Nothing else uses them, and a run with no small-signal sweep is unaffected.
            run = new HbEngine(nl, tb, settings, wspCache: null, lib: lib, baseDirectory: baseDirectory).Run(p);
            ds = run.DataSet;
        }
        return new HbCircuitSolve(ds, run, ChainSelector.BaseOfChain(top, tb)?.Name ?? top.Name);
    }

    /// <summary>
    /// The TestBench's measurements over <paramref name="solve"/>, exactly as the GUI evaluates them, and each one that
    /// failed as a sentence. Null when the TestBench declares none.
    /// </summary>
    public static DataSet? Measure(TestBench tb, ElaboratedNetlist nl, string analysisName, DataSet ds, HbRunResult? run,
                                   out IReadOnlyList<string> errors)
    {
        errors = [];
        if (tb.Measurements.Count == 0) return null;
        var results = new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase) { [analysisName] = ds };
        Dictionary<string, ILinearBackSolver>? solvers = null;
        if (run?.BackSolver is not null)
            solvers = new Dictionary<string, ILinearBackSolver>(StringComparer.OrdinalIgnoreCase) { [analysisName] = run.BackSolver };
        var measDs = new DataSet();
        errors = new MeasurementEvaluator(tb, nl, results, solvers).EvaluateInto(measDs);
        return measDs;
    }

    /// <summary>The analysis cubes and the measurement cubes as one DataSet — the GUI's grouped run DataSet (the analysis's
    /// groups, and <c>measurements</c>).</summary>
    public static DataSet Merge(DataSet ds, DataSet? measDs)
    {
        if (measDs is null) return ds;
        var merged = new DataSet();
        foreach (var group in ds.Groups)
            foreach (var (name, cube) in ds.CubesIn(group))
                merged.AddToGroup(group, name, cube);
        foreach (var (name, cube) in measDs.Cubes)
            merged.AddToGroup(DataSet.MeasurementsGroup, name, cube);
        return merged;
    }
}
