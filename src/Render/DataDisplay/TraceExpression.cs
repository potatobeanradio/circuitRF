// ================================================================
//  TraceExpression.cs  —  Evaluates element-wise expressions over
//  one or more DataCube slices using the circuitRF expression engine.
//
//  Pipeline:
//    1. Scan the expression string for CubeName[...] refs
//    2. Slice each ref to a rank-1 array
//    3. Validate dimensions (same X length)
//    4. Substitute refs with __c0, __c1, … placeholders
//    5. Parse the result with Parser.Parse
//    6. Evaluate per X-sample via Evaluator.InjectResolved
//    7. Return (xVals, complexValues?, realValues?, xAxisName, xUnit)
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class TraceExpression
{
    /// <summary>
    /// Evaluates a trace expression string against <paramref name="ds"/> and produces
    /// the 1-D arrays that <c>Trace.SetCubeData</c> expects.
    /// Returns false and sets <paramref name="error"/> on any failure.
    /// </summary>
    public static bool TryEvaluate(
        string      expression,
        DataSet     ds,
        PlotType    plotType,
        out double[]   xValues,
        out Complex[]? complexValues,
        out double[]?  realValues,
        out string     xAxisName,
        out string?    xUnit,
        out string[]?  xLabels,
        out string     error)
        => TryEvaluate(expression, ds, plotType, out xValues, out complexValues, out realValues,
                       out xAxisName, out xUnit, out xLabels, out _, out error);

    /// <summary>
    /// <see cref="TryEvaluate(string, DataSet, PlotType, out double[], out Complex[], out double[], out string, out string, out string[], out string)"/>,
    /// with the <paramref name="companions"/> an axis-building function left beside its result — a histogram's
    /// bin <c>width</c>, a yield sensitivity's <c>count</c> (docs/design/measurements.md "Companions"). Empty for
    /// an element-wise expression.
    /// </summary>
    public static bool TryEvaluate(
        string      expression,
        DataSet     ds,
        PlotType    plotType,
        out double[]   xValues,
        out Complex[]? complexValues,
        out double[]?  realValues,
        out string     xAxisName,
        out string?    xUnit,
        out string[]?  xLabels,
        out IReadOnlyDictionary<string, DataCube> companions,
        out string     error)
        => TryEvaluate(expression, ds, plotType, -1, out xValues, out complexValues, out realValues, out xAxisName,
                       out xUnit, out xLabels, out companions, out _, out error);

    /// <summary>One frequency of a result that keeps a <c>freq</c> axis beside another: the axis and the index pinned.</summary>
    public sealed record FreqPin(Axis Freq, int Index);

    /// <summary>
    /// <see cref="TryEvaluate(string, DataSet, PlotType, out double[], out Complex[], out double[], out string, out string, out string[], out IReadOnlyDictionary{string, DataCube}, out string)"/>
    /// with a frequency pin (brief NZ-2). An expression evaluated once over its cubes — an axis function's, or a network
    /// or circle function's (<c>ga_circle(SP1.S, 12)</c>, <c>[freq, point]</c>) — whose result keeps <c>freq</c> beside
    /// another axis is drawn at one frequency: <paramref name="freqIndex"/>, or the first when it is negative, and
    /// <paramref name="pinned"/> says which. With <paramref name="freqIndex"/> set, any expression is evaluated once and
    /// pinned, so a [freq] quantity such as <c>SP1.Sopt</c> becomes the one point at that frequency.
    /// </summary>
    public static bool TryEvaluate(
        string      expression,
        DataSet     ds,
        PlotType    plotType,
        int         freqIndex,
        out double[]   xValues,
        out Complex[]? complexValues,
        out double[]?  realValues,
        out string     xAxisName,
        out string?    xUnit,
        out string[]?  xLabels,
        out IReadOnlyDictionary<string, DataCube> companions,
        out FreqPin?   pinned,
        out string     error)
    {
        pinned        = null;
        companions    = NoCompanions;
        xValues       = Array.Empty<double>();
        complexValues = null;
        realValues    = null;
        xLabels       = null;
        xAxisName     = "";
        xUnit         = null;
        error         = "";

        expression = PortPairSpelling.Rewrite(expression.Trim(), ds);   // DUT_1(2,1) → DUT_1[:, 2, 1]
        if (string.IsNullOrEmpty(expression))
        {
            error = "Empty expression.";
            return false;
        }

        if (!ScanRefs(expression, ds, out var uniqueRefs, out var substitutions, out error)) return false;

        // A function that reads a whole axis (mean_over, histogram, cdf, …) cannot be evaluated a sample at a
        // time: the expression is evaluated ONCE over the cubes it names, and its result's one axis is the X. Nor can one
        // that reads a whole S cube and its analysis (a noise or gain circle), and a pinned frequency needs the whole
        // result to pin.
        if (Evaluator.CallsAxisFunction(expression) || Evaluator.CallsNetworkFunction(expression) || freqIndex >= 0)
            return TryEvaluateWhole(expression, ds, plotType, uniqueRefs, substitutions, freqIndex,
                                    out xValues, out complexValues, out realValues, out xAxisName, out xUnit, out xLabels,
                                    out companions, out pinned, out error);

        // ── Step 2: Slice each unique ref to a rank-1 array ──────────────────
        foreach (var info in uniqueRefs)
        {
            if (!ds.Contains(info.CubeName))
            {
                error = $"No cube '{info.CubeName}' in dataset.";
                return false;
            }
            var cube   = ds[info.CubeName];
            var tokens = SliceTokenParser.SplitTokens(info.SliceTokensStr);

            if (tokens.Length != cube.Rank)
            {
                error = $"'{info.RefStr}': expected {cube.Rank} axis token(s), got {tokens.Length}.";
                return false;
            }

            int xDim = -1;
            var args = new object[cube.Rank];
            for (int d = 0; d < tokens.Length; d++)
            {
                var axis = cube.Axes[d];
                var t = SliceTokenParser.Parse(tokens[d], axis.Length, axis.Labels, axis.Name, axis.Values, out error);
                switch (t.Kind)
                {
                    case SliceTokenParser.Kind.KeepWhole:
                        args[d] = Range.All;
                        if (xDim >= 0) { error = $"'{info.RefStr}': more than one X axis."; return false; }
                        xDim = d; break;
                    case SliceTokenParser.Kind.KeepRange:
                        args[d] = new Range(t.RangeStart, t.RangeEndExclusive);
                        if (xDim >= 0) { error = $"'{info.RefStr}': more than one X axis."; return false; }
                        xDim = d; break;
                    case SliceTokenParser.Kind.PinIndex:
                        args[d] = PortSlices.PinPosition(axis, t.Index); break;
                    case SliceTokenParser.Kind.Family:
                        error = $"'{info.RefStr}': the family marker '~' is only valid in single-cube picker specs, not multi-cube expressions."; return false;
                    default:
                        error = $"'{info.RefStr}': {error}"; return false;
                }
            }

            if (xDim < 0)
            {
                error = $"'{info.RefStr}': no X axis — use ':', 'All', or a range.";
                return false;
            }

            var result = cube[args];
            if (!result.IsCube || result.Cube!.Rank != 1)
            {
                error = $"'{info.RefStr}' did not yield a rank-1 slice.";
                return false;
            }

            var sliced = result.Cube!;
            info.XAxis = sliced.Axes[0];
            info.Data  = sliced.DataKind == DataKind.Complex
                ? sliced.ComplexValues
                : sliced.RealValues.Select(v => new Complex(v, 0)).ToArray();
        }

        // ── Step 3: Validate dimensions ───────────────────────────────────────
        int n = uniqueRefs[0].Data!.Length;
        for (int k = 1; k < uniqueRefs.Count; k++)
        {
            int nk = uniqueRefs[k].Data!.Length;
            if (nk != n)
            {
                error = $"'{uniqueRefs[k].RefStr}' has {nk} point(s) but '{uniqueRefs[0].RefStr}' has {n} — slices must share the same swept axis.";
                return false;
            }
        }

        // ── Step 4: Substitute placeholders ──────────────────────────────────
        var sb = new System.Text.StringBuilder(expression);
        // Replace from right to left to preserve positions.
        foreach (var (start, end, pIdx) in substitutions.OrderByDescending(s => s.start))
            sb.Remove(start, end - start).Insert(start, $"__c{pIdx}");
        string substituted = sb.ToString();

        // ── Step 5: Parse ─────────────────────────────────────────────────────
        Expr ast;
        try
        {
            ast = Parser.Parse(substituted);
        }
        catch (ParseException ex)
        {
            error = $"Couldn't parse '{expression}': {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Parse error: {ex.Message}";
            return false;
        }

        // Scope with dummy bindings for each placeholder (allows Lookup to succeed;
        // InjectResolved sets the real value in the memo cache before evaluation).
        var scope = new Scope("te");
        for (int k = 0; k < uniqueRefs.Count; k++)
            scope.Bind($"__c{k}", "0");

        // `freq` — the trace's own X axis in Hz, when that axis IS a frequency. A trace expression is
        // evaluated one X-sample at a time, so unlike the measurement scope's cube-valued `freq` this
        // one is the scalar for THIS sample — which is the same arithmetic and the same answer.
        // Bound only when the X axis is frequency: on a Pin- or gS-swept trace there is no single
        // frequency the sample stands at, and binding the sweep value would divide by the wrong number.
        var  xAx      = uniqueRefs[0].XAxis!;
        bool freqIsX  = xAx.Name is "freq" or "ssfreq";
        if (freqIsX) scope.Bind("freq", "0");

        // ── Step 6: Evaluate per X-sample ─────────────────────────────────────
        var results    = new Value[n];
        bool anyComplex = false;

        for (int i = 0; i < n; i++)
        {
            var ev = new Evaluator();
            for (int k = 0; k < uniqueRefs.Count; k++)
                ev.InjectResolved("te", $"__c{k}", new Value(uniqueRefs[k].Data![i]));
            if (freqIsX) ev.InjectResolved("te", "freq", new Value(xAx.Values[i]));

            try
            {
                results[i] = ev.EvalExpr(ast, scope);
            }
            catch (UnknownFunctionException ex)
            {
                error = $"Unknown function '{ex.Name}' in '{expression}'.";
                return false;
            }
            catch (ExpressionException ex)
            {
                // `freq` is available on a frequency-swept trace and on no other; the bare unresolved
                // name reads as "never supported", so say which axis this trace actually has.
                error = ex is UnresolvedNameException { Name: "freq" } && !freqIsX
                      ? $"'freq' is available only on a frequency-swept trace; this one's X axis is '{xAx.Name}'."
                      : ex.Message;
                return false;
            }

            if (results[i].Kind == ValueKind.Complex) anyComplex = true;
        }

        // ── Step 7: Build output ──────────────────────────────────────────────
        if (anyComplex)
        {
            // If every result has Im == 0 (e.g. source cube was Real and wrapped as Complex(v,0)),
            // demote to realValues so BuildCubePath treats the trace as scalar.
            bool allImagZero = results.All(v =>
                v.Kind != ValueKind.Complex || v.AsComplex().Imaginary == 0.0);
            if (allImagZero)
            {
                realValues    = results.Select(v =>
                    v.Kind == ValueKind.Complex ? v.AsComplex().Real : v.AsReal()).ToArray();
                complexValues = null;
            }
            else
            {
                complexValues = results.Select(v =>
                    v.Kind == ValueKind.Complex ? v.AsComplex() : new Complex(v.AsReal(), 0)).ToArray();
                realValues = null;
            }
        }
        else
        {
            realValues    = results.Select(v => v.AsReal()).ToArray();
            complexValues = null;
        }

        // Only the complex-locus plots (Smith / Polar) require a complex result; Rect and Table accept real.
        if (plotType.IsComplex() && realValues != null)
        {
            error = "Smith/Polar needs a complex expression; result is real-valued.";
            return false;
        }

        xValues   = uniqueRefs[0].XAxis!.Values;
        xAxisName = uniqueRefs[0].XAxis!.Name;
        xUnit     = string.IsNullOrEmpty(uniqueRefs[0].XAxis!.Unit)
            ? null
            : uniqueRefs[0].XAxis!.Unit;
        xLabels   = uniqueRefs[0].XAxis!.Labels;   // e.g. the two-tone "(k1,k2)" mix-product labels
        return true;
    }

    /// <summary>
    /// An expression calling an axis function (<see cref="Evaluator.AxisFunctions"/>): each reference is bound as the
    /// cube its slice leaves — any number of axes kept, a bare name the whole cube — and the expression is evaluated
    /// once. The result must keep exactly one axis, which is the trace's X: <c>histogram(x, 20)</c> its <c>bin</c>
    /// axis, <c>mean_over(SP1.S[:, :, 2, 1])</c> the frequency axis the mean over trials leaves.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DataCube> NoCompanions = new Dictionary<string, DataCube>();

    /// <summary>Step 1 of the pipeline: the cube references an expression names, and where each one sits.</summary>
    private static bool ScanRefs(string expression, DataSet ds, out List<CubeRefInfo> uniqueRefs,
                                 out List<(int start, int end, int pIdx)> substitutions, out string error)
    {
        error = "";
        // ── Step 1: Extract cube refs ─────────────────────────────────────────
        // Candidate names: analysis-group cubes qualified ("HB1.V"); default- and measurements-group
        // cubes BARE ("V", "IMD2") — these bare-resolve via DataSet.BareResolve. Sorted longest-first so
        // a longer name matches before a shorter one it contains (e.g. "HB1.V" before "V").
        var cubeNames = ds.Groups
            .SelectMany(g => ds.CubesIn(g).Keys.Select(c =>
                (g == DataSet.DefaultGroup || g == DataSet.MeasurementsGroup) ? c : $"{g}.{c}"))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(n => n.Length)
            .ToList();

        // refMap:   originalRefStr → placeholder index
        // uniqueRefs: in order of first appearance
        var refMap      = new Dictionary<string, int>(StringComparer.Ordinal);
        uniqueRefs    = new List<CubeRefInfo>();
        substitutions = new List<(int start, int end, int pIdx)>();

        static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

        int pos = 0;
        while (pos < expression.Length)
        {
            bool matched = false;
            foreach (var name in cubeNames)
            {
                if (pos + name.Length > expression.Length) continue;
                if (!expression.AsSpan(pos, name.Length).SequenceEqual(name.AsSpan())) continue;

                // Left word boundary: the char before must not continue an identifier, so "V" never
                // matches inside "Vout"/"RFfreq" and a qualified "HB1.V" matches whole.
                if (pos > 0 && IsIdent(expression[pos - 1])) continue;

                int after = pos + name.Length;

                string refStr, body;
                if (after < expression.Length && expression[after] == '[')
                {
                    // Bracketed reference CubeName[…].
                    int closeBracket = expression.IndexOf(']', after + 1);
                    if (closeBracket < 0) continue;        // unterminated — let a later candidate try
                    refStr = expression[pos..(closeBracket + 1)];
                    body   = expression[(after + 1)..closeBracket];
                }
                else if (after >= expression.Length || (!IsIdent(expression[after]) && expression[after] != '('))
                {
                    // Bare reference CubeName (e.g. a measurement like "IMD2"): keep every axis (all ':')
                    // and reuse the bracketed slicing path. A trailing '(' is excluded (call-like, not a cube).
                    refStr = name;
                    body   = string.Join(", ", Enumerable.Repeat(":", ds[name].Rank));
                }
                else
                {
                    continue;   // 'name' is a prefix of a longer identifier we don't recognize
                }

                if (!refMap.TryGetValue(refStr, out int pIdx))
                {
                    pIdx = uniqueRefs.Count;
                    refMap[refStr] = pIdx;
                    uniqueRefs.Add(new CubeRefInfo(name, refStr, body));
                }
                substitutions.Add((pos, pos + refStr.Length, pIdx));
                pos += refStr.Length;
                matched = true;
                break;
            }
            if (!matched) pos++;
        }

        if (uniqueRefs.Count == 0)
        {
            error = "No cube references found. Use a cube name (e.g. IMD2) or the form CubeName[:, 0, …].";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Evaluates an expression that calls an axis function (<see cref="Evaluator.AxisFunctions"/>) ONCE over the
    /// cubes it names, and returns whatever it gives — a single number included. What the Data Display reads a
    /// statistic of a trace's data with (a histogram's normal fit, a live histogram's held bin range,
    /// brief-yield-8), so a number drawn on a plot is the expression engine's and nobody else's.
    /// </summary>
    public static bool TryEvaluateValue(string expression, DataSet ds, out Value value, out string error)
    {
        value = default;
        expression = PortPairSpelling.Rewrite(expression.Trim(), ds);
        if (!ScanRefs(expression, ds, out var refs, out var subs, out error)) return false;
        return TryEvaluateWholeValue(expression, ds, refs, subs, out value, out _, out error);
    }

    private static bool TryEvaluateWhole(
        string expression, DataSet ds, PlotType plotType, List<CubeRefInfo> refs,
        List<(int start, int end, int pIdx)> substitutions, int freqIndex,
        out double[] xValues, out Complex[]? complexValues, out double[]? realValues,
        out string xAxisName, out string? xUnit, out string[]? xLabels,
        out IReadOnlyDictionary<string, DataCube> companions, out FreqPin? pinned, out string error)
    {
        xValues = []; complexValues = null; realValues = null; xAxisName = ""; xUnit = null; xLabels = null; pinned = null;
        if (!TryEvaluateWholeValue(expression, ds, refs, substitutions, out var result, out companions, out error))
            return false;

        // A result that keeps freq beside another axis (a circle's [freq, point]), or any result when a frequency is asked
        // for, is drawn at one frequency; the companions that share the axis are pinned with it.
        if (result.Kind == ValueKind.Cube && result.AsCube().Axes.FirstOrDefault(a => a.Name == FreqAxis) is { } fAxis
            && (freqIndex >= 0 || result.AsCube().Rank >= 2))
        {
            int k = Math.Clamp(Math.Max(freqIndex, 0), 0, fAxis.Length - 1);
            pinned = new FreqPin(fAxis, k);
            result = PinFreq(result.AsCube(), k);
            if (companions.Count > 0)
                companions = companions.ToDictionary(c => c.Key, c => PinFreq(c.Value, k) is { Kind: ValueKind.Cube } v
                    ? v.AsCube()
                    : new DataCube([new Axis("value", [0.0])], [PinFreq(c.Value, k).AsReal()]));
            if (result.Kind is ValueKind.Real or ValueKind.Complex)
            {
                // One point: the quantity at that frequency, a marker on the chart (Sopt).
                xValues = [fAxis.Values[k]]; xAxisName = fAxis.Name; xUnit = string.IsNullOrEmpty(fAxis.Unit) ? null : fAxis.Unit;
                if (result.Kind == ValueKind.Complex) complexValues = [result.AsComplex()];
                else realValues = [result.AsReal()];
                if (plotType.IsComplex() && realValues != null)
                {
                    error = "Smith/Polar needs a complex expression; result is real-valued.";
                    return false;
                }
                return true;
            }
        }

        if (result.Kind != ValueKind.Cube)
        {
            error = $"'{expression}' is one number, not a curve — every axis it read was reduced away.";
            return false;
        }
        var outCube = result.AsCube();
        if (outCube.Rank != 1)
        {
            error = $"'{expression}' keeps {outCube.Rank} axes ({string.Join(", ", outCube.Axes.Select(a => a.Name))}); " +
                    "a trace needs exactly one — pin the others with a slice.";
            return false;
        }

        if (outCube.DataKind == DataKind.Complex) complexValues = outCube.ComplexValues;
        else                                      realValues    = outCube.RealValues;
        if (plotType.IsComplex() && realValues != null)
        {
            error = "Smith/Polar needs a complex expression; result is real-valued.";
            return false;
        }
        var x = outCube.Axes[0];
        xValues   = x.Values;
        xAxisName = x.Name;
        xUnit     = string.IsNullOrEmpty(x.Unit) ? null : x.Unit;
        xLabels   = x.Labels;
        return true;
    }

    private const string FreqAxis = "freq";

    /// <summary><paramref name="cube"/> at index <paramref name="k"/> of its freq axis; a cube without one, unchanged.</summary>
    private static Value PinFreq(DataCube cube, int k)
    {
        int d = cube.Axes.ToList().FindIndex(a => a.Name == FreqAxis);
        if (d < 0) return new Value(cube);
        var args = new object[cube.Rank];
        for (int i = 0; i < args.Length; i++) args[i] = i == d ? Math.Min(k, cube.Axes[i].Length - 1) : Range.All;
        var s = cube[args];
        return s.IsCube ? new Value(s.Cube!) : s.IsComplex ? new Value(s.ComplexValue!.Value) : new Value(s.RealValue!.Value);
    }

    /// <summary>Binds each reference as the cube its slice leaves and evaluates the expression once.</summary>
    private static bool TryEvaluateWholeValue(
        string expression, DataSet ds, List<CubeRefInfo> refs, List<(int start, int end, int pIdx)> substitutions,
        out Value result, out IReadOnlyDictionary<string, DataCube> companions, out string error)
    {
        result = default; companions = NoCompanions; error = "";

        var bound = new Value[refs.Count];
        for (int k = 0; k < refs.Count; k++)
        {
            var info = refs[k];
            if (!ds.Contains(info.CubeName)) { error = $"No cube '{info.CubeName}' in dataset."; return false; }
            var cube   = ds[info.CubeName];
            if (cube.Rank == 0) { bound[k] = new Value(cube); continue; }
            var tokens = SliceTokenParser.SplitTokens(info.SliceTokensStr);
            // A whole cube is bound as the DataSet's own object, so a network function finds the analysis it came from
            // (and its Z0 and Cs) by reference, as a measurement does.
            if (tokens.Length == cube.Rank && tokens.All(t => t.Trim() is ":" || t.Trim().Equals("All", StringComparison.OrdinalIgnoreCase)))
            {
                bound[k] = new Value(cube);
                continue;
            }
            if (tokens.Length != cube.Rank)
            {
                error = $"'{info.RefStr}': expected {cube.Rank} axis token(s), got {tokens.Length}.";
                return false;
            }
            var args = new object[cube.Rank];
            for (int d = 0; d < tokens.Length; d++)
            {
                var axis = cube.Axes[d];
                var t = SliceTokenParser.Parse(tokens[d], axis.Length, axis.Labels, axis.Name, axis.Values, out error);
                switch (t.Kind)
                {
                    case SliceTokenParser.Kind.KeepWhole: args[d] = Range.All; break;
                    case SliceTokenParser.Kind.KeepRange: args[d] = new Range(t.RangeStart, t.RangeEndExclusive); break;
                    case SliceTokenParser.Kind.PinIndex:  args[d] = PortSlices.PinPosition(axis, t.Index); break;
                    case SliceTokenParser.Kind.Family:
                        error = $"'{info.RefStr}': the family marker '~' is only valid in single-cube picker specs, not expressions."; return false;
                    default:
                        error = $"'{info.RefStr}': {error}"; return false;
                }
            }
            var sliced = cube[args];
            bound[k] = sliced.IsCube    ? new Value(sliced.Cube!)
                     : sliced.IsComplex ? new Value(sliced.ComplexValue!.Value)
                     : new Value(sliced.RealValue!.Value);
        }

        var sb = new System.Text.StringBuilder(expression);
        foreach (var (start, end, pIdx) in substitutions.OrderByDescending(s => s.start))
            sb.Remove(start, end - start).Insert(start, $"__c{pIdx}");

        try
        {
            var ast   = Parser.Parse(sb.ToString());
            var scope = new Scope("te");
            var ev    = new Evaluator(AnalysisContext(ds));
            for (int k = 0; k < refs.Count; k++)
            {
                scope.Bind($"__c{k}", "0");
                ev.InjectResolved("te", $"__c{k}", bound[k]);
            }
            result     = ev.EvalExpr(ast, scope);
            companions = ev.TakeCompanions();
        }
        catch (ParseException ex)           { error = $"Couldn't parse '{expression}': {ex.Message}"; return false; }
        catch (UnknownFunctionException ex) { error = $"Unknown function '{ex.Name}' in '{expression}'."; return false; }
        catch (ExpressionException ex)      { error = ex.Message; return false; }
        return true;
    }

    /// <summary>Each group of <paramref name="ds"/> as an analysis's results, holding the group's own cube objects — what a
    /// network function finds the owner of an S cube in.</summary>
    private static MeasurementContext AnalysisContext(DataSet ds)
    {
        var groups = new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in ds.Groups)
        {
            var one = new DataSet();
            foreach (var (name, cube) in ds.CubesIn(g)) one.Add(name, cube);
            groups[g] = one;
        }
        return new MeasurementContext(groups);
    }

    // ── Private helper type ───────────────────────────────────────────────────

    private sealed class CubeRefInfo
    {
        public string    CubeName      { get; }
        public string    RefStr        { get; }  // full original, e.g. "V[:, 0, 0]"
        public string    SliceTokensStr { get; } // content between [ and ], e.g. ":, 0, 0"

        public Complex[]? Data  { get; set; }
        public Axis?      XAxis { get; set; }

        public CubeRefInfo(string cubeName, string refStr, string sliceTokensStr)
        {
            CubeName       = cubeName;
            RefStr         = refStr;
            SliceTokensStr = sliceTokensStr;
        }
    }
}
