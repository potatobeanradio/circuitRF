using System.Globalization;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries;

namespace CircuitRF.Design.Filters;

/// <summary>
/// A placed Filter block as the ladder the LC Filter Designer designed for it: the same synthesis,
/// the same value rule (<see cref="FilterValues.Choose"/>) and the same library parts the Designer's
/// table shows. A designed Filter whose <c>Values</c> is not Exact simulates as this ladder rather than
/// as the ideal transfer function, so what its Properties say (an E-series, a vendor library) is what
/// the run uses. Exact keeps the ideal block, which is the ladder's own response.
/// </summary>
public static class FilterBlockLadder
{
    /// <summary>One element of the ladder: its value as built, and the library part when one is used.</summary>
    public sealed record Part(FilterElement Element, VendorPartRef? Vendor);

    /// <summary>The ladder: its elements and the through-path node the output port sits on (the input is <c>p1</c>).</summary>
    public sealed record Ladder(IReadOnlyList<Part> Parts, string OutputNode);

    /// <summary>
    /// True when the block carries an LC Filter Designer design: Apply to Filter and Place component
    /// always write <c>Ap</c> or a mask edge, and nothing else writes them.
    /// </summary>
    public static bool IsDesigned(IEnumerable<EditableParameter> parameters) =>
        parameters.Any(p => p.Name is "Ap" or "Fp" or "Fs" or "Fp1" or "Fs1" or "Fs2" or "Fp2"
                            && !string.IsNullOrWhiteSpace(p.Expression));

    /// <summary>True when the design holds the block: designed, and <c>Synthesis</c> is not <c>Off</c>.</summary>
    public static bool IsLocked(IEnumerable<EditableParameter> parameters)
    {
        var list = parameters as IReadOnlyCollection<EditableParameter> ?? parameters.ToList();
        return IsDesigned(list)
            && !string.Equals(list.FirstOrDefault(p => p.Name == "Synthesis")?.Expression.Trim(), "Off",
                              StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The block's <c>Values</c>, Exact when blank or unknown.</summary>
    public static FilterSeriesKind ValuesOf(IEnumerable<EditableParameter> parameters) =>
        Enum.TryParse<FilterSeriesKind>(parameters.FirstOrDefault(p => p.Name == "Values")?.Expression.Trim(),
                                        true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : FilterSeriesKind.Exact;

    /// <summary>
    /// True when the block simulates as its ladder: it is locked to a design and its values are not
    /// Exact. An unlocked block (Use Synthesis off) is a plain transfer function whose rows may have
    /// been edited away from the design, so it keeps the ideal response.
    /// </summary>
    public static bool UsesLadder(IEnumerable<EditableParameter> parameters)
    {
        var list = parameters as IReadOnlyCollection<EditableParameter> ?? parameters.ToList();
        return IsLocked(list) && ValuesOf(list) != FilterSeriesKind.Exact;
    }

    /// <summary>The <c>Parts</c> value as written: <c>element=library: series|part</c>, separated by ';'.</summary>
    public static IEnumerable<(string Element, string SeriesKey, string Part)> ParseParts(string text)
    {
        foreach (string item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            int bar = item.LastIndexOf('|');
            if (eq <= 0 || bar <= eq + 1 || bar == item.Length - 1) continue;
            yield return (item[..eq].Trim(), item[(eq + 1)..bar].Trim(), item[(bar + 1)..].Trim());
        }
    }

    /// <summary>
    /// The block's ladder, or null with <paramref name="refusal"/> when it cannot be built: a value
    /// that is not a plain number, a family with no ladder, or a library series or part that the
    /// workspace at <paramref name="workspaceRoot"/> no longer holds (never silently swapped for the
    /// exact value).
    /// </summary>
    public static Ladder? Build(IReadOnlyList<EditableParameter> parameters, string? workspaceRoot, out string? refusal)
    {
        refusal = null;
        EditableParameter? Param(string name) => parameters.FirstOrDefault(p => p.Name == name);
        string Expr(string name) => Param(name)?.Expression.Trim() ?? "";
        static bool Num(string text, out double v) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        bool Hz(string name, out double hz)
        {
            hz = 0;
            if (!Num(Expr(name), out double v)) return false;
            double? scale = (Param(name)?.Unit ?? "").Trim().ToLowerInvariant() switch
            {
                "" or "hz" => 1, "khz" => 1e3, "mhz" => 1e6, "ghz" => 1e9, "thz" => 1e12, _ => null,
            };
            if (scale is null) return false;
            hz = v * scale.Value;
            return true;
        }

        if (!Enum.TryParse<FilterResponse>(Expr("Response"), true, out var response)
            || !Enum.TryParse<NetworkForm>(Expr("Form"), true, out var form)
            || !int.TryParse(Expr("Order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int order) || order < 1)
        {
            refusal = "its Response, Form or Order is not a plain value";
            return null;
        }
        double fc = 0, f1 = 0, f2 = 0;
        bool edge = form is NetworkForm.Lowpass or NetworkForm.Highpass;
        if (edge ? !Hz("Fc", out fc) : !Hz("F1", out f1) || !Hz("F2", out f2))
        {
            refusal = "its frequency is not a plain number in Hz, kHz, MHz or GHz";
            return null;
        }
        if (!Num(Expr("Zin"), out double rs) || !(rs > 0))
        {
            refusal = "its Zin is not a plain positive number";
            return null;
        }
        double rippleIn = Num(Expr("Ripple"), out double r) ? r : 0.1;
        double ripple = response is FilterResponse.Chebyshev or FilterResponse.Elliptic
            ? (rippleIn > 0 ? rippleIn : 0.1)
            : 0.1;
        double astop = Num(Expr("Astop"), out double a) && a > 0 ? a : 40;
        var first = Enum.TryParse<FilterFirstElement>(Expr("First"), true, out var fe) ? fe : FilterFirstElement.Parallel;
        var type = response == FilterResponse.Elliptic
            ? EllipticTypes.Resolve(EllipticTypes.TryParse(Expr("EllipticType"), out var et) ? et : EllipticType.Auto, order)
            : EllipticType.A;

        FilterLadderResult built;
        try
        {
            built = FilterLadder.Synthesize(response, form, order, fc, f1, f2, ripple, rs, first, astop, type);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            refusal = e.Message;
            return null;
        }
        if (built.Realization is not { } real)
        {
            refusal = built.Refusal?.Message ?? "the family has no ladder";
            return null;
        }

        var kind = ValuesOf(parameters);
        FilterLibrarySeries? inductors = null, capacitors = null;
        var overrides = new Dictionary<string, (FilterLibrarySeries Series, FilterPart Part)>(StringComparer.Ordinal);
        if (kind == FilterSeriesKind.VendorLibrary)
        {
            string lKey = Expr("LSeries"), cKey = Expr("CSeries"), parts = Expr("Parts");
            if (lKey.Length > 0 || cKey.Length > 0 || parts.Length > 0)
            {
                var notes = new List<string>();
                var all = FilterValues.LibrarySeries(workspaceRoot, notes);
                var missing = new List<string>();
                FilterLibrarySeries? Find(string key, bool l)
                {
                    if (key.Length == 0) return null;
                    var s = all.FirstOrDefault(x => x.Inductors == l && x.Key == key);
                    if (s is null) missing.Add(key);
                    return s;
                }
                inductors = Find(lKey, true);
                capacitors = Find(cKey, false);
                foreach (var (element, key, partName) in ParseParts(parts))
                {
                    var s = all.FirstOrDefault(x => x.Key == key);
                    var p = s?.Parts.FirstOrDefault(x => x.Name == partName);
                    if (s is null || p is null) { missing.Add($"{key}|{partName}"); continue; }
                    overrides[element] = (s, p);
                }
                if (missing.Count > 0)
                {
                    refusal = "the vendor library parts it names are not in this workspace's libraries ("
                            + string.Join(", ", missing.Distinct()) + ")"
                            + (notes.Count > 0 ? ": " + notes[0].TrimEnd('.') : "");
                    return null;
                }
            }
        }

        var list = new List<Part>(real.Elements.Count);
        foreach (var e in real.Elements)
        {
            var (value, _, part) = FilterValues.Choose(e, kind, inductors, capacitors, overrides);
            list.Add(new Part(e with { Value = value }, part?.Vendor));
        }
        string output = FilterLadder.LoadNode(list.Select(p => p.Element).ToList());
        if (output == "p1")
        {
            refusal = "its ladder has no series element, so its two ports are one node";
            return null;
        }
        return new Ladder(list, output);
    }
}
