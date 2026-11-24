// ================================================================
//  LinkMatch.cs  —  a link value → an index on one consumer axis
//  (brief-ddl-1 R-ddl1-6, overview D3)
//
//  Same axis is an index; anything else is a value; different units
//  are refused by name. Nothing here re-derives a sample by comparing
//  doubles with ==: rule 1 compares two whole GRIDS (is this the same
//  sweep?), and rule 2 takes the NEAREST sample to a value.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class LinkMatch
{
    /// <summary>
    /// The index a pin on <paramref name="axis"/> draws at when it follows <paramref name="value"/>, by the four rules
    /// in order. <paramref name="storedIndex"/> is the pin's saved index, drawn when nothing fits (rule 4).
    /// </summary>
    public static LinkMatchResult Match(LinkValue value, Axis axis, int storedIndex)
    {
        int fallback = Math.Clamp(storedIndex, 0, Math.Max(0, axis.Length - 1));
        if (value.Frozen)
            return new(fallback, LinkMatchKind.Frozen, Gone(value.Name, axis, fallback));

        // Rule 1 — the same axis: same name, same length, same values. The same run, or the same sweep written
        // twice. Its index is exact, and is the common case.
        foreach (var c in value.Coordinates)
            if (c.Axis == axis.Name && SameGrid(c.AxisValues, axis.Values))
                return new(Math.Clamp(c.Index, 0, axis.Length - 1), LinkMatchKind.SameAxis, null);

        // Rule 2 — a coordinate in this axis's unit: the consumer's sample nearest its value. A same-named axis is
        // preferred to another one in the same unit (a marker on freq and on a family of temperatures, both "").
        var inUnit = value.Coordinates.Where(c => UnitsMatch(c.Unit, c.Axis, axis))
                                      .OrderBy(c => c.Axis == axis.Name ? 0 : 1)
                                      .FirstOrDefault();
        if (inUnit is not null)
            return Nearest(value.Name, inUnit.Value, axis);

        // Rule 3 — a control's own value, by the same rule.
        if (value.Scalar is { } sc && UnitsMatch(sc.Unit, axis.Name, axis))
            return Nearest(value.Name, sc.Value, axis);

        // Rule 4 — nothing fits: drawn where it was, and the note says what each side is.
        return new(fallback, LinkMatchKind.Unresolved, Mismatch(value, axis));
    }

    /// <summary>
    /// A unit is a match when it is the same unit, NAMED. Two unitless axes match only when they are the same-named
    /// axis: <c>gridPoint</c> 17 is not <c>pinStep</c> 17, and reading a load index as a drive step would draw a
    /// plausible, wrong curve.
    /// </summary>
    private static bool UnitsMatch(string unit, string sourceAxis, Axis axis)
        => string.Equals(unit ?? "", axis.Unit ?? "", StringComparison.Ordinal)
           && ((unit ?? "").Length > 0 || sourceAxis == axis.Name);

    private static bool SameGrid(IReadOnlyList<double> a, double[] b)
    {
        if (a.Count != b.Length) return false;
        for (int i = 0; i < b.Length; i++)
        {
            double x = a[i], y = b[i];
            if (x == y) continue;
            double scale = Math.Max(Math.Abs(x), Math.Abs(y));
            if (!(Math.Abs(x - y) <= 1e-12 * scale)) return false;
        }
        return true;
    }

    private static LinkMatchResult Nearest(string name, double v, Axis axis)
    {
        var xs = axis.Values;
        if (xs.Length == 0 || !double.IsFinite(v)) return new(0, LinkMatchKind.Unresolved, $"{name} has no value to follow.");
        int best = 0; double bestD = double.PositiveInfinity;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < xs.Length; i++)
        {
            double d = Math.Abs(xs[i] - v);
            if (d < bestD) { bestD = d; best = i; }
            lo = Math.Min(lo, xs[i]); hi = Math.Max(hi, xs[i]);
        }
        // Outside the range by more than half a step is outside — a value a hair past the last sample, from a grid
        // offset by rounding, is simply its nearest sample.
        double halfStep = xs.Length > 1 ? 0.5 * (hi - lo) / (xs.Length - 1) : 0;
        if (v > hi + halfStep)
            return new(best, LinkMatchKind.Clamped, $"{name} is {Format(v, axis.Unit)}; this axis ends at {Format(hi, axis.Unit)}.");
        if (v < lo - halfStep)
            return new(best, LinkMatchKind.Clamped, $"{name} is {Format(v, axis.Unit)}; this axis starts at {Format(lo, axis.Unit)}.");
        return new(best, LinkMatchKind.Nearest, null);
    }

    private static string Gone(string name, Axis axis, int index)
        => $"{name} is gone — showing {axis.Name} {index}, where {name} last was.";

    private static string Mismatch(LinkValue value, Axis axis)
    {
        string gives = value.Coordinates.Count > 0
            ? string.Join(" and ", value.Coordinates.Select(c => c.Unit.Length > 0 ? $"{c.Axis} ({c.Unit})" : c.Axis))
            : value.Scalar is { } s ? (s.Unit.Length > 0 ? $"a value in {s.Unit}" : "a plain value")
            : value.Position is not null ? "a point on the Smith chart"
            : "nothing";
        string axisText = (axis.Unit ?? "").Length > 0 ? $"{axis.Name} ({axis.Unit})" : axis.Name;
        return $"{value.Name} gives {gives}; this axis is {axisText}.";
    }

    /// <summary>A value with its unit, in engineering notation: <c>3.1 GHz</c>, <c>17</c>.</summary>
    internal static string Format(double v, string? unit)
    {
        unit ??= "";
        if (!double.IsFinite(v)) return "NaN";
        int group = unit.Length > 0 ? EngineeringFormat.EngineeringGroup(Math.Abs(v)) : 0;
        double m = group == 0 ? v : v / Math.Pow(10.0, group);
        string num = m.ToString("0.####", CultureInfo.InvariantCulture);
        string prefix = group == 0 ? "" : EngineeringFormat.Prefix(group);
        return unit.Length == 0 && prefix.Length == 0 ? num : $"{num} {prefix}{unit}";
    }
}
