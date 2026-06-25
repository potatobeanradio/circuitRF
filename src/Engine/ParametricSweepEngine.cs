using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
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
    /// <summary>
    /// Coordinates OutputGrid <c>.gam</c> writes across a (possibly nested) sweep: the FIRST pursuit
    /// write across the whole run truncates the file; every subsequent write appends a freq-tagged block.
    /// One instance is created at the outermost <see cref="Run"/> and threaded through nesting so a nested
    /// sweep does not re-truncate per outer point.
    /// </summary>
    private sealed class OutputWriteState { public bool FirstGamWriteDone; }

    public static DataSet Run(
        ParametricSweepAnalysis sweep,
        Library lib,
        TestBench tb,
        AnalysisSettings? settings = null,
        string? baseDirectory = null)
        => Run(sweep, lib, tb, settings, baseDirectory, new OutputWriteState());

    private static DataSet Run(
        ParametricSweepAnalysis sweep,
        Library lib,
        TestBench tb,
        AnalysisSettings? settings,
        string? baseDirectory,
        OutputWriteState writeState)
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

        // Continuation (§11): warm-start each HB point from the previous point's converged spectrum.
        // The seed chains only along THIS (innermost) axis; for a nested-sweep inner the per-point
        // RunInner returns a null seed, so each outer step's inner sweep restarts cold. Reset to null
        // whenever a point does not converge (or the inner is non-HB) so a bad seed never propagates.
        bool warmStart = settings?.HbSweepWarmStart ?? true;
        Complex[,]? seed = null;

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
                datasets.Add(RunInner(inner, lib, tb, netlist, settings, baseDirectory, writeState,
                    warmStart ? seed : null, out var nextSeed));
                seed = warmStart ? nextSeed : null;
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

        // Loadpull frequency sweep: when every per-point DataSet carries a __Freq tone carrier and the
        // tone varies across points, the swept variable IS the tone frequency. Stack with a "freq" axis
        // (Hz) built from the resolved per-point tones — LoadpullSurface keys the frequency dimension on
        // an axis literally named "freq". Reading the resolved __Freq (not the raw swept values) keeps
        // both a unit-bearing VAR (`RFfreq = 2 GHz`) and a unit-less one (`RFfreq = 2`, freq via ToneUnit)
        // correct and Hz-valued. Otherwise: the generic variable-named axis.
        if (TryBuildToneFreqAxis(datasets, out var freqAxis))
        {
            // A freq-tagged .gam from a swept pursuit can carry a DIFFERENT number of recommended
            // terminations per frequency (more points go unscorable at some freqs — e.g. a reactive
            // output cap), and per-grid-point Pin compression can produce a different pinStep count per
            // freq. The resulting per-freq loadpull cubes are ragged and won't stack into [freq, …]. Pad
            // every grid/pinStep axis up to the across-freq maximum with NaN; LoadpullSurface drops NaN
            // scatter points, so each freq's fit sees only its real terminations.
            datasets = PadRaggedGridsToCommon(datasets);
            return DataSet.StackSweepAxis(freqAxis, datasets);
        }
        // Tag with base SI unit so marker readouts show "freq=2 GHz". SweepValues are already base SI.
        return DataSet.StackSweepAxis(new Axis(sweep.SweepVarName, sweep.SweepValues, baseUnit), datasets);
    }

    /// <summary>
    /// Pads ragged per-frequency loadpull cubes to a uniform shape so they stack. For each (non-metadata)
    /// cube present in every dataset with a consistent rank, computes the per-axis maximum length across
    /// datasets and rebuilds any shorter cube at that shape, filling the new cells (and extended index
    /// axes) with NaN. Cubes already uniform — and the common non-loadpull case — pass through unchanged.
    /// </summary>
    private static List<DataSet> PadRaggedGridsToCommon(List<DataSet> datasets)
    {
        if (datasets.Count < 2) return datasets;

        var targets = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var key in datasets[0].Cubes.Keys)
        {
            if (key.StartsWith("__", StringComparison.Ordinal)) continue;   // metadata: never padded
            var c0 = datasets[0][key];
            var max = new int[c0.Rank];
            for (int d = 0; d < c0.Rank; d++) max[d] = c0.Axes[d].Length;

            bool ragged = false, ok = true;
            for (int n = 1; n < datasets.Count && ok; n++)
            {
                if (!datasets[n].Contains(key)) { ok = false; break; }      // missing cube — let stack report it
                var cn = datasets[n][key];
                if (cn.Rank != c0.Rank) { ok = false; break; }
                for (int d = 0; d < c0.Rank; d++)
                {
                    if (cn.Axes[d].Length != max[d]) ragged = true;
                    if (cn.Axes[d].Length > max[d]) max[d] = cn.Axes[d].Length;
                }
            }
            if (ok && ragged) targets[key] = max;
        }
        if (targets.Count == 0) return datasets;   // nothing ragged

        var result = new List<DataSet>(datasets.Count);
        foreach (var ds in datasets)
        {
            var nds = new DataSet();
            foreach (var (name, cube) in ds.Cubes)
                nds.Add(name, targets.TryGetValue(name, out var tgt) ? PadCubeTo(cube, tgt) : cube);
            result.Add(nds);
        }
        return result;
    }

    /// <summary>Rebuilds <paramref name="src"/> at <paramref name="target"/> per-axis lengths, copying
    /// existing elements into their row-major positions and filling the remainder with NaN. Index-like
    /// padded axis slots take their position as value.</summary>
    private static DataCube PadCubeTo(DataCube src, int[] target)
    {
        int rank = src.Rank;
        bool needs = false;
        for (int d = 0; d < rank; d++) if (src.Axes[d].Length != target[d]) { needs = true; break; }
        if (!needs) return src;

        var axes = new Axis[rank];
        for (int d = 0; d < rank; d++)
        {
            var a = src.Axes[d];
            if (a.Length == target[d]) { axes[d] = a; continue; }
            var vals = new double[target[d]];
            for (int i = 0; i < target[d]; i++) vals[i] = i < a.Length ? a.Values[i] : i;
            axes[d] = new Axis(a.Name, vals, a.Unit);
        }

        var srcLen = new int[rank];
        for (int d = 0; d < rank; d++) srcLen[d] = src.Axes[d].Length;
        var tStride = new int[rank];
        tStride[rank - 1] = 1;
        for (int d = rank - 2; d >= 0; d--) tStride[d] = tStride[d + 1] * target[d + 1];
        int total = 1; for (int d = 0; d < rank; d++) total *= target[d];
        int srcTotal = 1; for (int d = 0; d < rank; d++) srcTotal *= srcLen[d];

        // Map source flat index → target flat index (same axis order, larger lengths).
        int TargetIndex(int s)
        {
            int t = 0, rem = s;
            for (int d = 0; d < rank; d++)
            {
                int stride = 1; for (int e = d + 1; e < rank; e++) stride *= srcLen[e];
                int idx = rem / stride; rem %= stride;
                t += idx * tStride[d];
            }
            return t;
        }

        if (src.DataKind == DataKind.Complex)
        {
            var sd = src.ComplexValues;
            var td = new Complex[total];
            var nan = new Complex(double.NaN, double.NaN);
            for (int i = 0; i < total; i++) td[i] = nan;
            for (int s = 0; s < srcTotal; s++) td[TargetIndex(s)] = sd[s];
            return new DataCube(axes, td);
        }
        else
        {
            var sd = src.RealValues;
            var td = new double[total];
            for (int i = 0; i < total; i++) td[i] = double.NaN;
            for (int s = 0; s < srcTotal; s++) td[TargetIndex(s)] = sd[s];
            return new DataCube(axes, td);
        }
    }

    /// <summary>
    /// Builds a "freq" (Hz) sweep axis from the per-point <c>__Freq</c> tone carriers when this is a
    /// loadpull frequency sweep — i.e. EVERY per-point DataSet carries <c>__Freq</c> and the tone value
    /// actually varies across points. Returns false otherwise (caller uses the variable-named axis: a
    /// non-tone sweep of a loadpull, e.g. a bias sweep, correctly stays single-frequency).
    /// </summary>
    private static bool TryBuildToneFreqAxis(List<DataSet> datasets, out Axis axis)
    {
        axis = null!;
        if (datasets.Count == 0) return false;
        var freqs = new double[datasets.Count];
        for (int i = 0; i < datasets.Count; i++)
        {
            if (!datasets[i].Contains("__Freq")) return false;
            var vals = datasets[i]["__Freq"].RealValues;
            if (vals.Length == 0) return false;
            freqs[i] = vals[0];
        }
        bool varies = false;
        for (int i = 1; i < freqs.Length; i++)
            if (System.Math.Abs(freqs[i] - freqs[0]) > 1e-3) { varies = true; break; }
        if (!varies) return false;
        axis = new Axis("freq", freqs, "Hz");
        return true;
    }

    // ── Inner dispatch ────────────────────────────────────────────────────────

    private static DataSet RunInner(
        Analysis inner,
        Library lib,
        TestBench tb,
        ElaboratedNetlist netlist,
        AnalysisSettings? settings,
        string? baseDirectory,
        OutputWriteState writeState,
        Complex[,]? hbWarmStart,
        out Complex[,]? hbConvergedSeed)
    {
        // Only a single-tone HB inner produces a chainable seed; every other inner leaves it null so
        // the sweep does not warm-start across it (continuation is innermost-axis only — §11).
        hbConvergedSeed = null;

        switch (inner)
        {
            case HarmonicBalanceAnalysis hba:
            {
                var p  = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
                var rr = new HbEngine(netlist, tb, settings).Run(p, hbWarmStart);
                // Chain this point's converged spectrum into the next point's seed. Two-tone returns a
                // null InterfaceV → no chaining; a non-converged point also resets the chain.
                hbConvergedSeed = rr.Converged ? rr.InterfaceV : null;
                return rr.DataSet;
            }

            case SParameterAnalysis spa:
                return RunSParam(spa, netlist, settings);

            case DcAnalysis dca:
                return RunDc(dca, netlist, settings);

            case ParametricSweepAnalysis psa:
                // Recursive: outer override already injected in tb.GlobalVariables.
                // This call re-elaborates for each of its own sweep values on top of that.
                // Same writeState threads down so a nested sweep truncates the OutputGrid only once.
                return Run(psa, lib, tb, settings, baseDirectory, writeState);

            case LoadpullAnalysis lpa:
            {
                // Loadpull owns its own Γ-grid × Pin sweep; here it runs once at this point's resolved
                // tone (LoadpullEngine.Resolve reads the swept tone via var-unit-wins). Enrich per point
                // so every freq slice carries the canonical display metrics before stacking.
                var lp = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
                return RfCore.Loadpull.LoadpullPostProcessor.Enrich(new LoadpullEngine(netlist, tb).Run(lp));
            }

            case LoadpullPursuitAnalysis lppa:
            {
                // Pursuit runs the full MXP/MXE search + (optional) follow-on at this point's resolved
                // tone (Resolve reads the swept tone via var-unit-wins). The result DataSet carries __Freq,
                // so a freq sweep stacks the MXP/MXE optima into per-frequency trends; the LP_-prefixed
                // follow-on cubes stack when their per-freq grid shapes match.
                var pp = LoadpullPursuitEngine.Resolve(lppa, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
                // OutputGrid .gam: the FIRST pursuit write across the whole (possibly nested) sweep truncates
                // the file; every later write appends a freq-tagged block. This produces one multi-frequency
                // file (ragged per-freq grids are independent blocks) and survives nested sweeps without
                // re-truncating per outer point. The non-swept path (SchematicRunService) writes a single
                // block (append=false, its own truncate).
                bool hasOutput = !string.IsNullOrEmpty(pp.OutputGridPath);
                bool append    = hasOutput && writeState.FirstGamWriteDone;
                if (hasOutput) writeState.FirstGamWriteDone = true;
                return new LoadpullPursuitEngine(new LoadpullEngine(netlist, tb))
                    .Run(pp, appendOutputBlock: append);
            }

            default:
                throw new NotSupportedException(
                    $"ParametricSweepEngine: inner analysis type " +
                    $"'{inner.GetType().Name}' is not supported. " +
                    $"Supported: HarmonicBalanceAnalysis, SParameterAnalysis, DcAnalysis, " +
                    $"ParametricSweepAnalysis, LoadpullAnalysis, LoadpullPursuitAnalysis.");
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
