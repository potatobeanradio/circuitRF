using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>One part a vendor part's <c>Part</c> tunable may take (brief vendorlib-4 R-vl4-1b).</summary>
/// <param name="Part">The part number, as the library writes it.</param>
/// <param name="Cell">The cell its circuit is emitted under — what an evaluation rebinds the instance to.
/// Empty for a candidate listed from the index but not emitted (the entry is neither tuned nor optimized).</param>
/// <param name="Value">The stated value in SI, or null when the header states none.</param>
/// <param name="ValueText">The stated value with its prefix and unit (<c>10 pF</c>), or empty.</param>
/// <param name="Description">The header's Description, or null.</param>
/// <param name="Series">The file stem — one file holds one series.</param>
public sealed record VendorPartCandidate(
    string  Part,
    string  Cell,
    double? Value,
    string  ValueText,
    string? Description,
    string  Series,
    VendorLibraryEntry Entry);

/// <summary>
/// A placed vendor part as a tunable (brief vendorlib-4 R-vl4-1): the part it is, and the candidate parts an
/// evaluation may put in its place, in value order (ties by part name — brief 3's order).
///
/// <para>Computed at EXTRACTION, because that is the one place that knows both the placed part (a
/// <c>.cnl</c> holds only the cell it binds to) and the tuning entry that chooses the candidates; it rides on
/// <see cref="Schematic.NetExtractor.ExtractionResult.PartTunables"/> to the catalog, the validator and the
/// optimizer.</para>
/// </summary>
/// <param name="Key">The tunable's key: <c>C1.Part</c>, <c>DUT:C1.Part</c>.</param>
/// <param name="Cell">The cell the instance lives in, as the netlist spells it; null at the top.</param>
/// <param name="Selection">How the candidates were chosen, as the <c>tune</c> line spells it
/// (<c>kind=same</c>, <c>parts="0603 C0G"</c>), with the value bounds when there are any.</param>
/// <param name="Found">How many parts the selection found after the value bounds and the port rule.</param>
/// <param name="RemovedForPorts">Parts the selection found that have a different port count from the
/// placed part (R-vl4-2c).</param>
/// <param name="Unreadable">Candidates whose block did not read, each with why — dropped from
/// <see cref="Candidates"/> and reported, never a failure of the run (R-vl4-2a).</param>
/// <param name="Emitted">Whether the candidates' cells are in the netlist (the entry is tuned or optimized).</param>
public sealed record VendorPartTunable(
    string  Key,
    string? Cell,
    string  Instance,
    string  Library,
    VendorLibraryEntry Placed,
    string  PlacedCell,
    string  Glyph,
    string  Selection,
    IReadOnlyList<VendorPartCandidate> Candidates,
    int     Found,
    int     RemovedForPorts,
    IReadOnlyList<string> Unreadable,
    bool    Emitted)
{
    public string PlacedPart => Placed.Part;

    public VendorPartQuantity Quantity => Placed.Header.Quantity;

    /// <summary>The unit a value of this part is in: <c>F</c>, <c>H</c>, <c>Ohm</c>, or "" when the header
    /// states no quantity. What <c>min=</c>/<c>max=</c> are read in.</summary>
    public string Unit => UnitOf(Quantity);

    public static string UnitOf(VendorPartQuantity q) => q switch
    {
        VendorPartQuantity.Capacitance => "F",
        VendorPartQuantity.Inductance  => "H",
        VendorPartQuantity.Resistance  => "Ohm",
        _                              => "",
    };

    /// <summary>
    /// Whether the continuous stage of a mixed search may stand an IDEAL component in the part's place
    /// (R-vl4-3b, owner decision D3): a C or L the glyph already names, and every candidate stating a positive
    /// value to place it among. A part with no proxy is searched on its candidates only.
    /// </summary>
    public bool HasProxy => GlyphKind is not null && Candidates.Count > 1 && Candidates.All(c => c.Value is > 0);

    /// <summary>The primitive an ideal stand-in is (<c>C</c> / <c>L</c>), or null.</summary>
    public string? GlyphKind => Glyph.Trim().ToUpperInvariant() switch { "C" => "C", "L" => "L", _ => null };

    /// <summary>Where the placed part sits among the candidates, or −1 when the selection left it out.</summary>
    public int PlacedIndex
    {
        get
        {
            for (int i = 0; i < Candidates.Count; i++)
                if (Candidates[i].Part.Equals(PlacedPart, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>The candidate a value is placed nearest when the placed part is not one: by value, else the first.</summary>
    public int StartIndex
    {
        get
        {
            int at = PlacedIndex;
            if (at >= 0 || Candidates.Count == 0) return Math.Max(at, 0);
            if (Placed.Header.Value is not { } v || v <= 0) return 0;
            int best = 0;
            for (int i = 1; i < Candidates.Count; i++)
                if (Distance(Candidates[i].Value, v) < Distance(Candidates[best].Value, v)) best = i;
            return best;
        }
    }

    private static double Distance(double? a, double v) => a is > 0 ? Math.Abs(Math.Log(a.Value / v)) : double.PositiveInfinity;

    /// <summary>The candidate emitted under <paramref name="cell"/>, or null.</summary>
    public VendorPartCandidate? CandidateOfCell(string? cell)
        => cell is null ? null
         : Candidates.FirstOrDefault(c => c.Cell.Length > 0 && c.Cell.Equals(cell, StringComparison.Ordinal))
           ?? Candidates.FirstOrDefault(c => c.Part.Equals(cell, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value span as text: <c>1 pF – 47 pF</c>, one value alone, or empty when none is stated.</summary>
    public string SpanText
    {
        get
        {
            var stated = Candidates.Where(c => c.Value is not null).ToList();
            if (stated.Count == 0) return "";
            string lo = stated[0].ValueText, hi = stated[^1].ValueText;
            return lo == hi ? lo : $"{lo} – {hi}";
        }
    }

    /// <summary>The series (file stems) the candidates span, in first-seen order.</summary>
    public IReadOnlyList<string> Series => [.. Candidates.Select(c => c.Series).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The candidates in one line (R-vl4-1c): their count, value span and series — <c>12 candidates,
    /// 1 pF – 47 pF, series TSTC0402</c> — with what the selection removed.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Plural(Candidates.Count, "candidate", "candidates") };
            if (SpanText.Length > 0) parts.Add(SpanText);
            var series = Series;
            if (series.Count > 0)
                parts.Add(series.Count <= 3 ? $"series {string.Join(", ", series)}" : $"{series.Count} series");
            if (RemovedForPorts > 0) parts.Add($"{RemovedForPorts} with another port count left out");
            if (Unreadable.Count > 0) parts.Add($"{Unreadable.Count} unreadable left out");
            return string.Join(", ", parts);
        }
    }

    /// <summary>What a row and <c>explain --tunables</c> show for the placed part: <c>TSTC0402A100 · 10 pF · 0402/C0G/…</c>.</summary>
    public string PlacedText => Describe(PlacedPart, Placed.Header.ValueText(), Placed.Header.Description);

    /// <summary>A part as one line: its number, its value and its Description, each when stated.</summary>
    public static string Describe(string part, string? valueText, string? description)
    {
        var bits = new List<string> { part };
        if (!string.IsNullOrWhiteSpace(valueText)) bits.Add(valueText.Trim());
        if (!string.IsNullOrWhiteSpace(description)) bits.Add(description.Trim());
        return string.Join(" · ", bits);
    }

    private static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
}

/// <summary>
/// Which parts a placed vendor part's <c>Part</c> entry may take (brief vendorlib-4 R-vl4-1b/1c/2c), from
/// the INDEX alone — header entries, never a circuit. The one rule the extractor, <c>check</c>,
/// <c>explain --tunables</c> and the Tuning window share.
/// </summary>
public static class VendorPartCandidates
{
    /// <summary>
    /// The parts <paramref name="entry"/> chooses among for <paramref name="placed"/> — <c>kind=same</c>
    /// (also with no entry, D1) or <c>parts="…"</c> over the part's own library — narrowed by
    /// <c>min=</c>/<c>max=</c> on the stated value, without the parts of another port count, in value order.
    /// </summary>
    /// <param name="removedForPorts">How many the port rule removed.</param>
    /// <param name="selection">The selection as the <c>tune</c> line spells it.</param>
    public static IReadOnlyList<VendorLibraryEntry> Select(
        TunableEntry? entry, VendorPartSource.Resolved placed, out int removedForPorts, out string selection)
    {
        ArgumentNullException.ThrowIfNull(placed);
        IEnumerable<VendorLibraryEntry> found;
        if (entry?.Parts is { } query)
        {
            selection = $"parts=\"{query}\"";
            var build = VendorLibraryIndex.Latest(placed.LibraryRoot);
            if (build is null)
            {
                // Headless — or the GUI before its background index finished: a foreground build, which re-scans
                // only the files whose time or length changed since they were last read.
                build = VendorLibraryIndex.Build(placed.LibraryRoot);
                if (build.IsUsable) VendorLibraryIndex.Remember(build);
            }
            var parsed = VendorLibrarySearch.Parse(query);
            found = build.Entries.Where(e => VendorLibrarySearch.Match(e, parsed));
        }
        else
        {
            selection = "kind=same";
            var file = VendorLibraryIndex.ScanFile(placed.LibraryRoot, placed.Entry.File) ?? [placed.Entry];
            found = VendorPartChange.SameKind(file, placed.Entry);
        }

        string unit = VendorPartTunable.UnitOf(placed.Entry.Header.Quantity);
        double? lo = Bound(entry?.Min, unit), hi = Bound(entry?.Max, unit);
        if (entry?.Min is { } minText) selection += $" min={minText}";
        if (entry?.Max is { } maxText) selection += $" max={maxText}";
        if (lo is not null || hi is not null)
            found = found.Where(e => e.Header.Value is { } v
                                  && (lo is null || v >= lo.Value * (1 - VendorLibrarySearch.ValueTolerance))
                                  && (hi is null || v <= hi.Value * (1 + VendorLibrarySearch.ValueTolerance)));

        var list = found.ToList();
        removedForPorts = list.RemoveAll(e => e.PortCount != placed.Entry.PortCount);
        return [.. list.OrderBy(e => e.Header.Value ?? double.PositiveInfinity)
                       .ThenBy(e => e.Part, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A bound's value in SI, read in the part's own unit (<c>1 pF</c>; a bare number is in the unit).</summary>
    private static double? Bound(string? text, string unit)
    {
        if (text is null || TunableValue.InUnit(text, unit) is not { } n) return null;
        return unit.Length == 0 ? n : n * (CircuitRF.Core.Expressions.Units.Scale(unit) ?? 1);
    }

    /// <summary>A candidate record for an index entry, under <paramref name="cell"/> ("" when not emitted).</summary>
    public static VendorPartCandidate Candidate(VendorLibraryEntry e, string cell)
        => new(e.Part, cell, e.Header.Value, e.Header.ValueText().Trim(), e.Header.Description, e.FileStem, e);

    /// <summary>The value text an ideal stand-in of <paramref name="kind"/> (<c>C</c>/<c>L</c>) is written with
    /// in an evaluation's values: a number in base SI and its unit (<c>1.2e-11 F</c>).</summary>
    public static string ProxyText(double valueSi, string kind)
        => valueSi.ToString("G6", CultureInfo.InvariantCulture) + (kind == "L" ? " H" : " F");

    /// <summary>An ideal stand-in's value text (<see cref="ProxyText"/>) as a person reads it — <c>2.3 pF</c> —
    /// or the text itself when it is not one.</summary>
    public static string ProxyDisplay(string proxyText)
    {
        if (!TunableValue.TryParse(proxyText, out _, out string unit, out double si)) return proxyText;
        var quantity = CircuitRF.Core.Expressions.Units.BaseUnit(unit) switch
        {
            "F" => VendorPartQuantity.Capacitance,
            "H" => VendorPartQuantity.Inductance,
            _   => VendorPartQuantity.None,
        };
        return quantity == VendorPartQuantity.None ? proxyText
            : new VendorLibraryHeader { Value = si, Quantity = quantity }.ValueText().Trim();
    }
}
