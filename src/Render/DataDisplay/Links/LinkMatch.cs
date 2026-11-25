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
        => Match(value, axis, storedIndex, data: null);

    /// <summary>
    /// As <see cref="Match(LinkValue, Axis, int)"/>, with the consumer's own file: when rules 1–3 find nothing and
    /// <paramref name="axis"/> has 1-D companion cubes in <paramref name="data"/>, the case whose companions are nearest
    /// the source's coordinates is taken (brief-ddl-4 R-ddl4-1) before rule 4.
    /// </summary>
    public static LinkMatchResult Match(LinkValue value, Axis axis, int storedIndex, DataSet? data)
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

        // A button's 0 or 1 (brief-ddl-6 R-ddl6-6): the first or second case of the axis — two corners, two bias points
        // written as an axis. A longer axis is said to be one, since only its first two cases are reachable.
        if (value.Scalar is { IsIndex: true } ix && axis.Length > 0)
        {
            int i = Math.Clamp((int)Math.Round(ix.Value), 0, axis.Length - 1);
            return new(i, LinkMatchKind.SameAxis,
                       axis.Length == 2 ? null : $"{value.Name} picks case 0 or 1; {axis.Name} has {axis.Length}.");
        }

        // Companions — a list of cases (a bias `point`) described by 1-D cubes over it (`vgs[point]`, `vds[point]`):
        // the case nearest the source's coordinates. A nearest match by design; interpolating between bias points
        // would be a model, and is not here.
        if (data is not null && ByCompanions(value, axis, data) is { } byCompanion)
            return byCompanion;

        // Rule 4 — nothing fits: drawn where it was, and the note says what each side is.
        return new(fallback, LinkMatchKind.Unresolved, Mismatch(value, axis));
    }

    /// <summary>A 1-D cube over a consumer axis, named like a coordinate a source may supply.</summary>
    /// <param name="Name">The cube's name without its group: <c>vgs</c>.</param>
    public sealed record Companion(string Name, double[] Values, string Unit);

    /// <summary>
    /// The real 1-D cubes of <paramref name="data"/> whose only axis is <paramref name="axis"/> — its name and its
    /// grid. Bookkeeping cubes (<c>__…</c>) are not companions.
    /// </summary>
    public static IReadOnlyList<Companion> CompanionsOf(DataSet data, Axis axis)
    {
        var list = new List<Companion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in data.Groups)
            foreach (var (name, cube) in data.CubesIn(g))
            {
                if (name.StartsWith("__", StringComparison.Ordinal) || cube.Rank != 1 || cube.DataKind != DataKind.Real) continue;
                var a = cube.Axes[0];
                if (a.Name != axis.Name || !SameGrid(a.Values, axis.Values) || !seen.Add(name)) continue;
                list.Add(new Companion(name, cube.RealValues, cube.Unit ?? ""));
            }
        return list;
    }

    private static string Bare(string name) => name.LastIndexOf('.') is var dot and >= 0 ? name[(dot + 1)..] : name;

    private static LinkMatchResult? ByCompanions(LinkValue value, Axis axis, DataSet data)
    {
        if (value.Coordinates.Count == 0) return null;
        var companions = CompanionsOf(data, axis);
        if (companions.Count == 0) return null;

        // Each coordinate pairs with the companion of its name (case-insensitively), else with the one companion left
        // in its unit. A coordinate that pairs with nothing takes no part.
        var pairs = new List<(LinkCoordinate C, Companion K)>();
        var used  = new HashSet<Companion>(ReferenceEqualityComparer.Instance);
        foreach (var c in value.Coordinates)
            if (companions.FirstOrDefault(k => !used.Contains(k)
                    && string.Equals(k.Name, Bare(c.Axis), StringComparison.OrdinalIgnoreCase)) is { } k)
            { pairs.Add((c, k)); used.Add(k); }
        foreach (var c in value.Coordinates)
        {
            if (pairs.Any(p => ReferenceEquals(p.C, c)) || (c.Unit ?? "").Length == 0) continue;
            var inUnit = companions.Where(k => !used.Contains(k) && string.Equals(k.Unit, c.Unit, StringComparison.Ordinal)).ToList();
            if (inUnit.Count == 1) { pairs.Add((c, inUnit[0])); used.Add(inUnit[0]); }
        }
        if (pairs.Count == 0) return null;

        // Each dimension scaled by its companion's span, so a volt of Vgs and a volt of Vds weigh by how far each
        // ranges here, not by their numbers.
        var spans = pairs.Select(p =>
        {
            double lo = p.K.Values.Where(double.IsFinite).DefaultIfEmpty(0).Min();
            double hi = p.K.Values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
            return hi > lo ? hi - lo : 1.0;
        }).ToArray();
        int best = -1; double bestD = double.PositiveInfinity;
        for (int i = 0; i < axis.Length; i++)
        {
            double d = 0;
            for (int k = 0; k < pairs.Count; k++)
            {
                double x = (pairs[k].K.Values[i] - pairs[k].C.Value) / spans[k];
                d += x * x;
            }
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return null;

        string took  = string.Join(", ", pairs.Select(p => $"{p.K.Name} {Format(p.K.Values[best], UnitOf(p))}"));
        string asked = string.Join(", ", pairs.Select(p => Format(p.C.Value, UnitOf(p))));
        return new(best, LinkMatchKind.Companion, $"{axis.Name} {best} ({took}; {value.Name} at {asked}).");
    }

    private static string UnitOf((LinkCoordinate C, Companion K) p) => p.K.Unit.Length > 0 ? p.K.Unit : p.C.Unit ?? "";

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

    /// <summary>What a source supplies, in a few words: "freq (Hz)", "gridPoint and pinStep (dBm)".</summary>
    public static string Supplies(LinkValue value)
        => value.Coordinates.Count > 0
            ? string.Join(" and ", value.Coordinates.Select(c => c.Unit.Length > 0 ? $"{c.Axis} ({c.Unit})" : c.Axis))
            : value.Scalar is { IsIndex: true } ? "0 or 1"
            : value.Scalar is { } s ? (s.Unit.Length > 0 ? $"a value in {s.Unit}" : "a plain value")
            : value.Position is not null ? "a point on the Smith chart"
            : "nothing";

    private static string Mismatch(LinkValue value, Axis axis)
    {
        string gives = Supplies(value);
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
