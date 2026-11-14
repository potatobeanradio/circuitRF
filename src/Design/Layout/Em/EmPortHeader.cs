// What each port of an EM result is, one header line per port in the result's own Touchstone, for Planar and 3D alike:
//
//     ! Port 5 = P05  Q1.B  net=b  ref=ground  uncalibrated
//     ! DC: none, the lowest row is 1 GHz
//
// The port's number and a fixed-width name (P05), what it is (a drawn label's text, a footprint part's refdes or pin, a cut
// instance's path.pin), the net the layout's schematic puts on it ("?" where none says), what it returns through (the run's
// ground, a pin of the same instance, or the conductor a 3D sheet lands on), and whether it was calibrated. Then whether the file
// has a 0 Hz row, which decides what a circuit's DC solve through it reads. The older lines (Port[N] = name, and each port's
// geometry line) stay as they are, for whatever reads them.

using System.Globalization;
using CircuitRF.Design.Layout.Pdn;
using RfCore.Data;

namespace CircuitRF.Design.Layout.Em;

/// <summary>One port of a result as its header line states it.</summary>
/// <param name="Number">The port's number in the file.</param>
/// <param name="What">What it is: a drawn label's text, a part's refdes (or refdes.pin), a cut instance's path.pin.</param>
/// <param name="Net">The net the layout's schematic puts on it; null where none says.</param>
/// <param name="Reference">What it returns through: <c>ground</c>, a pin, or a conductor.</param>
/// <param name="Calibrated">Whether its launch was de-embedded.</param>
public sealed record EmPortHeaderRow(int Number, string What, string? Net, string Reference, bool Calibrated);

public static class EmPortHeader
{
    /// <summary>The DC line's opening.</summary>
    public const string DcPrefix = "DC: ";

    /// <summary>One line per port, in number order, then the DC line. None for a result with no ports.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<EmPortHeaderRow> rows, IReadOnlyList<double> frequenciesHz)
    {
        if (rows.Count == 0) return [];
        string digits = "D" + Math.Max(2, rows.Max(r => r.Number).ToString(CultureInfo.InvariantCulture).Length).ToString(CultureInfo.InvariantCulture);
        int what = rows.Max(r => Ascii(r.What).Length), net = rows.Max(r => Ascii(r.Net ?? "?").Length);
        var lines = rows.OrderBy(r => r.Number)
            .Select(r => $"Port {r.Number.ToString(CultureInfo.InvariantCulture)} = P{r.Number.ToString(digits, CultureInfo.InvariantCulture)}  " +
                         $"{Ascii(r.What).PadRight(what)}  net={Ascii(r.Net ?? "?").PadRight(net)}  ref={Ascii(r.Reference)}  " +
                         (r.Calibrated ? "calibrated" : "uncalibrated"))
            .ToList();
        lines.Add(DcLine(frequenciesHz));
        return lines;
    }

    /// <summary>Whether the file has a 0 Hz row, and when it has none, the row a circuit's DC solve through it reads instead.</summary>
    public static string DcLine(IReadOnlyList<double> frequenciesHz)
    {
        if (frequenciesHz.Count == 0) return DcPrefix + "none, the file has no rows";
        double low = frequenciesHz.Min();
        return low == 0 ? DcPrefix + "yes, the first row is 0 Hz"
                        : DcPrefix + $"none, the lowest row is {Hz(low)}; a circuit's DC solve through this file takes that row as DC";
    }

    /// <summary>The frequencies of the group holding S in <paramref name="data"/>, what the Touchstone is written from.</summary>
    public static IReadOnlyList<double> FrequenciesOf(DataSet? data)
    {
        if (data is null) return [];
        foreach (string g in data.Groups)
            if (data.CubesIn(g).TryGetValue("S", out var s) && s.Axes.Count > 0) return s.Axes[0].Values;
        return [];
    }

    /// <summary>
    /// The net the schematic beside <paramref name="clayPath"/> puts on a placement's pin: the instance its <c>SchematicId</c> names,
    /// at <paramref name="pin"/> by name (or number), or its first net when <paramref name="pin"/> is null (a drawn port label linked
    /// to the schematic's port). Null wherever the schematic does not say.
    /// </summary>
    public static Func<string?, string?, string?> SchematicNets(string? clayPath)
    {
        var nets = PdnSchematicNets.Resolve(clayPath);
        return (schematicId, pin) =>
        {
            if (nets.For(schematicId) is not { } part || part.Nets.Count == 0) return null;
            if (pin is null) return part.Nets[0] is { Length: > 0 } n0 ? n0 : null;
            int k = part.PortNames.ToList().FindIndex(p => p.Equals(pin, StringComparison.OrdinalIgnoreCase));
            if (k < 0 && int.TryParse(pin, NumberStyles.None, CultureInfo.InvariantCulture, out int num)) k = num - 1;
            return k >= 0 && k < part.Nets.Count && part.Nets[k] is { Length: > 0 } net ? net : null;
        };
    }

    private static string Hz(double f)
        => f >= 1e9 ? $"{(f / 1e9).ToString("G6", CultureInfo.InvariantCulture)} GHz"
         : f >= 1e6 ? $"{(f / 1e6).ToString("G6", CultureInfo.InvariantCulture)} MHz"
         : f >= 1e3 ? $"{(f / 1e3).ToString("G6", CultureInfo.InvariantCulture)} kHz"
         : $"{f.ToString("G6", CultureInfo.InvariantCulture)} Hz";

    private static string Ascii(string s) => new([.. s.Select(c => c is >= ' ' and < (char)127 ? c : '?')]);
}
