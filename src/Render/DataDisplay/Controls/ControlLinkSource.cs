// ================================================================
//  ControlLinkSource.cs  —  a display control, asked where it is
//  (brief-ddl-5 R-ddl5-1, R-ddl5-3, R-ddl5-8; overview D6)
//
//  A bound slider supplies exactly the coordinate a marker on its axis
//  would — the axis, the stop's index into it, the value, the unit and
//  the whole axis — so a consumer of the same run takes the index
//  (rule 1). Every control also supplies its value as a scalar, in base
//  SI (a range written in MHz supplies Hz), which a consumer on another
//  axis in that unit takes the nearest sample to (rule 3).
// ================================================================

using System;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class ControlLinkSource
{
    /// <summary>The link value of <paramref name="c"/> as it now stands.</summary>
    public static LinkValue Resolve(DisplayControl c, IPlotDataSources? sources)
    {
        if (c is not SliderControl s) return new LinkValue(c.Id, c.Name, []);
        if (s.Domain == SliderDomain.Bound)
        {
            var (path, group) = Refresh(s, sources);
            var stops = s.Stops;
            int i = s.StopIndex;
            string unit = s.BoundUnit ?? "";
            var coord = new LinkCoordinate(path, group, s.AxisName ?? "", i, stops[i], unit, stops.ToArray());
            return new LinkValue(c.Id, c.Name, [coord], Scalar: new LinkScalar(stops[i], unit));
        }
        var (scale, baseUnit) = SiUnit.Split(s.Unit);
        return new LinkValue(c.Id, c.Name, [], Scalar: new LinkScalar(s.CurrentValue * scale, baseUnit));
    }

    /// <summary>
    /// Reads a bound slider's axis from its source into <see cref="SliderControl.BoundValues"/> and
    /// <see cref="SliderControl.BoundUnit"/>, and returns the file and group it read. When the source, the cube or the
    /// axis is gone the slider keeps what it last read (R-ddl5-7) and the path is the stored one.
    /// </summary>
    public static (string? Path, string Group) Refresh(SliderControl s, IPlotDataSources? sources)
    {
        string? abs = s.SourcePath is { } sp && sources is not null ? sources.ResolveAbs(sp) ?? sp : s.SourcePath;
        if (AxisOf(s, sources, abs) is not { } axis) return (abs, GroupOf(s.CubeName, null));
        if (s.BoundValues is null || !s.BoundValues.SequenceEqual(axis.Values)) s.BoundValues = (double[])axis.Values.Clone();
        s.BoundUnit = axis.Unit ?? "";
        s.Index = Math.Clamp(s.Index, 0, Math.Max(0, axis.Length - 1));
        return (abs, GroupOf(s.CubeName, sources?.DataFor(abs!)));
    }

    /// <summary>Why a bound slider cannot read its axis now, or null when it can (or is not bound).</summary>
    public static string? Problem(SliderControl s, IPlotDataSources? sources)
    {
        if (s.Domain != SliderDomain.Bound) return null;
        if (s.SourcePath is null || s.CubeName is null || s.AxisName is null) return "Choose a source, quantity and axis.";
        string abs = sources?.ResolveAbs(s.SourcePath) ?? s.SourcePath;
        // Short enough to read whole on one line of a narrow panel; "last range kept" says what the slider is doing.
        if (sources?.DataFor(abs) is not { } ds) return $"{System.IO.Path.GetFileName(abs)} not loaded; last range kept.";
        if (!ds.Contains(s.CubeName)) return $"No {s.CubeName} here; last range kept.";
        if (ds[s.CubeName].Axes.All(a => a.Name != s.AxisName)) return $"No axis {s.AxisName}; last range kept.";
        return null;
    }

    private static Axis? AxisOf(SliderControl s, IPlotDataSources? sources, string? abs)
    {
        if (sources is null || abs is null || s.CubeName is null || s.AxisName is null) return null;
        if (sources.DataFor(abs) is not { } ds || !ds.Contains(s.CubeName)) return null;
        return ds[s.CubeName].Axes.FirstOrDefault(a => a.Name == s.AxisName);
    }

    private static string GroupOf(string? cubeName, DataSet? ds)
    {
        if (cubeName is null) return "";
        int dot = cubeName.IndexOf('.');
        return dot > 0 && (ds is null || ds.Groups.Contains(cubeName[..dot])) ? cubeName[..dot] : "";
    }

    private static readonly System.Text.RegularExpressions.Regex NumberWithUnit = new(
        @"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(\S*)\s*$");

    /// <summary>
    /// A range end typed into the slider's inline editor — <c>800 MHz</c>, <c>0.8 GHz</c>, or a bare number in the
    /// slider's own unit — as a number in that unit. Null, with the reason, for anything else.
    /// </summary>
    public static double? ParseRangeEnd(SliderControl s, string? text, out string? error)
    {
        error = null;
        var m = NumberWithUnit.Match(text ?? "");
        if (!m.Success) { error = "Type a number, with or without its unit."; return null; }
        double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string typed = m.Groups[2].Value;
        if (typed.Length == 0) return v;
        var (scale, baseUnit) = SiUnit.Split(typed);
        var (ownScale, ownBase) = SiUnit.Split(s.Unit);
        if (!string.Equals(baseUnit, ownBase, StringComparison.Ordinal))
        {
            error = s.Unit.Length > 0 ? $"This slider is in {s.Unit}." : "This slider has no unit.";
            return null;
        }
        return v * scale / ownScale;
    }

    /// <summary>
    /// Sets a control's value from <c>render --control</c>'s right-hand side (R-ddl5-10): a stop number (<c>#5</c>) or a
    /// value with an optional unit, taken to the nearest stop. Null when done, else why not.
    /// </summary>
    public static string? SetFromText(DisplayControl c, int? index, double value, string unit)
    {
        if (c is not SliderControl s) return $"{c.Name} has no value to set.";
        var stops = s.Stops;
        if (index is { } i)
        {
            if (i < 0 || i >= stops.Count) return $"{c.Name} has stops 0..{stops.Count - 1}.";
            s.SetStop(i);
            return null;
        }
        // A value with a unit is compared in base SI; one without is in the slider's own unit.
        var (scale, baseUnit) = SiUnit.Split(s.DisplayUnit);
        double v = value;
        if (unit.Length > 0)
        {
            if (!string.Equals(unit, baseUnit, StringComparison.Ordinal))
                return $"{c.Name} is in {(s.DisplayUnit.Length > 0 ? s.DisplayUnit : "no unit")}, not {unit}.";
            v = value / scale;
        }
        s.SetStop(SliderControl.NearestStop(stops, v));
        return null;
    }
}
