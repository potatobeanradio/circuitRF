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

namespace CircuitRF.Ui.DataDisplay;

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
        out string     error)
    {
        xValues       = Array.Empty<double>();
        complexValues = null;
        realValues    = null;
        xAxisName     = "";
        xUnit         = null;
        error         = "";

        expression = expression.Trim();
        if (string.IsNullOrEmpty(expression))
        {
            error = "Empty expression.";
            return false;
        }

        // ── Step 1: Extract cube refs ─────────────────────────────────────────
        // Sort cube names descending by length so longer names match first (e.g. "VGain" before "V").
        var cubeNames = ds.Cubes.Keys.OrderByDescending(n => n.Length).ToList();

        // refMap:   originalRefStr → placeholder index
        // uniqueRefs: in order of first appearance
        var refMap      = new Dictionary<string, int>(StringComparer.Ordinal);
        var uniqueRefs  = new List<CubeRefInfo>();
        var substitutions = new List<(int start, int end, int pIdx)>();

        int pos = 0;
        while (pos < expression.Length)
        {
            bool matched = false;
            foreach (var name in cubeNames)
            {
                if (pos + name.Length > expression.Length) continue;
                if (!expression.AsSpan(pos, name.Length).SequenceEqual(name.AsSpan())) continue;

                // Require '[' immediately after the cube name (no whitespace).
                int after = pos + name.Length;
                if (after >= expression.Length || expression[after] != '[') continue;

                // Find the matching ']' — no nesting in slice syntax.
                int closeBracket = expression.IndexOf(']', after + 1);
                if (closeBracket < 0) continue;

                string refStr = expression[pos..(closeBracket + 1)];
                if (!refMap.TryGetValue(refStr, out int pIdx))
                {
                    pIdx = uniqueRefs.Count;
                    refMap[refStr] = pIdx;
                    uniqueRefs.Add(new CubeRefInfo(name, refStr, expression[(after + 1)..closeBracket]));
                }
                substitutions.Add((pos, closeBracket + 1, pIdx));
                pos = closeBracket + 1;
                matched = true;
                break;
            }
            if (!matched) pos++;
        }

        if (uniqueRefs.Count == 0)
        {
            error = "No cube references found. Use the form CubeName[:, 0, …].";
            return false;
        }

        // ── Step 2: Slice each unique ref to a rank-1 array ──────────────────
        foreach (var info in uniqueRefs)
        {
            if (!ds.Contains(info.CubeName))
            {
                error = $"No cube '{info.CubeName}' in dataset.";
                return false;
            }
            var cube   = ds[info.CubeName];
            var tokens = info.SliceTokensStr
                .Split(',')
                .Select(t => t.Trim())
                .ToArray();

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
                var t = SliceTokenParser.Parse(tokens[d], axis.Length, axis.Labels, axis.Name, out error);
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
                        args[d] = t.Index; break;
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

        // ── Step 6: Evaluate per X-sample ─────────────────────────────────────
        var results    = new Value[n];
        bool anyComplex = false;

        for (int i = 0; i < n; i++)
        {
            var ev = new Evaluator();
            for (int k = 0; k < uniqueRefs.Count; k++)
                ev.InjectResolved("te", $"__c{k}", new Value(uniqueRefs[k].Data![i]));

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
                error = ex.Message;
                return false;
            }

            if (results[i].Kind == ValueKind.Complex) anyComplex = true;
        }

        // ── Step 7: Build output ──────────────────────────────────────────────
        if (anyComplex)
        {
            complexValues = results.Select(v =>
                v.Kind == ValueKind.Complex ? v.AsComplex() : new Complex(v.AsReal(), 0)).ToArray();
            realValues = null;
        }
        else
        {
            realValues    = results.Select(v => v.AsReal()).ToArray();
            complexValues = null;
        }

        // Smith/Polar require a complex result.
        if (!plotType.IsRect() && realValues != null)
        {
            error = "Smith/Polar needs a complex expression; result is real-valued.";
            return false;
        }

        xValues   = uniqueRefs[0].XAxis!.Values;
        xAxisName = uniqueRefs[0].XAxis!.Name;
        xUnit     = string.IsNullOrEmpty(uniqueRefs[0].XAxis!.Unit)
            ? null
            : uniqueRefs[0].XAxis!.Unit;
        return true;
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
