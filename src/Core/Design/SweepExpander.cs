using System;
using System.Collections.Generic;
using System.Globalization;

namespace CircuitRF.Core.Design;

/// <summary>
/// Sweep-axis expansion mode: how start/stop/stepOrCount map to a double[].
/// </summary>
public enum SweepAxisMode { StepSize, PointCount, List }

/// <summary>
/// Headless sweep-axis expander.  Converts (start, stop, stepOrCount, mode, kind) → double[].
/// Mirrors the LinSpace/LogSpace/LinearStepSpace/LogStepSpace helpers in FrequencySpec but
/// operates on pre-resolved doubles (no expression strings).
/// </summary>
public static class SweepExpander
{
    /// <summary>
    /// Expands a parametric sweep axis to the concrete value array.
    /// </summary>
    /// <param name="start">Start value (inclusive).</param>
    /// <param name="stop">Stop value (inclusive).</param>
    /// <param name="stepOrCount">Step size (StepSize mode) or point count (PointCount mode).</param>
    /// <param name="mode">How start/stop/stepOrCount are interpreted.</param>
    /// <param name="kind">Linear or Log spacing.</param>
    public static double[] ExpandSweep(double start, double stop, double stepOrCount,
                                       SweepAxisMode mode, SweepKind kind)
    {
        if (mode == SweepAxisMode.PointCount)
        {
            int n = Math.Max(1, (int)Math.Round(stepOrCount));
            return kind == SweepKind.Log ? LogSpace(start, stop, n) : LinSpace(start, stop, n);
        }

        // StepSize
        return kind == SweepKind.Log
            ? LogStepSpace(start, stop, stepOrCount)
            : LinearStepSpace(start, stop, stepOrCount);
    }

    /// <summary>
    /// Parses a comma-separated list of double literals into a value array.
    /// Whitespace around commas is ignored.
    /// </summary>
    public static double[] ExpandList(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [];

        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<double>(parts.Length);
        foreach (var p in parts)
        {
            if (double.TryParse(p.Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture, out double v))
                result.Add(v);
        }
        return [.. result];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static double[] LinSpace(double start, double stop, int n)
    {
        if (n == 1) return [start];
        var pts = new double[n];
        for (int i = 0; i < n; i++)
            pts[i] = start + (stop - start) * i / (n - 1);
        return pts;
    }

    private static double[] LogSpace(double start, double stop, int n)
    {
        if (n == 1) return [start];
        var pts = new double[n];
        double logRatio = Math.Log10(stop / start);
        for (int i = 0; i < n; i++)
            pts[i] = start * Math.Pow(10, logRatio * i / (n - 1));
        return pts;
    }

    private static double[] LinearStepSpace(double start, double stop, double step)
    {
        if (step <= 0) step = (stop - start) / 100.0;
        var list = new List<double>();
        for (double v = start; v <= stop + step * 1e-9; v += step)
            list.Add(v);
        return [.. list];
    }

    private static double[] LogStepSpace(double start, double stop, double step)
    {
        if (step <= 1.0) return LogSpace(start, stop, 100);
        var list = new List<double>();
        for (double v = start; v <= stop * (1.0 + 1e-9); v *= step)
            list.Add(v);
        return [.. list];
    }
}
