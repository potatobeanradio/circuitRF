// Each symbol region named — brief-img-8-schematic-symbols.md R-im8-5, R-im8-7, R-im8-8 (overview D9).
//
// SchematicSymbols.Read asks the classifier (R-im8-6) what each region of a schematic picture's reading is, and applies
// D9 to the answer, the same whichever classifier gave it:
//  * three or more attachments — a transistor, an IC box, or anything not matched — is CUT OUT: each attachment
//    becomes a port in IM-10, exactly as the artwork series cuts out a multi-pin part. So is an amplifier triangle,
//    though it has two;
//  * two attachments and no match is kind `?` with its two pins (generated as a C, as the artwork series does);
//  * one attachment and no match is an UNREAD TERMINAL, reported and left open.
// Nothing here reads a value or a designator (IM-9) or makes a circuit (IM-10).

using System.Globalization;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>What becomes of a region (R-im8-5).</summary>
public enum SymbolDisposition
{
    /// <summary>A part of a known kind.</summary>
    Recognised,

    /// <summary>Two pins, nothing matched: kind <c>?</c>.</summary>
    Unknown,

    /// <summary>A multi-pin device: each pin becomes a port (D9).</summary>
    CutOut,

    /// <summary>One pin, nothing matched: left open.</summary>
    UnreadTerminal,
}

/// <summary>One pin of a recognised symbol, in the template's pin order.</summary>
/// <param name="Name">The template pin's name; for a symbol no template named, its number from 1.</param>
/// <param name="X">Where its wire reaches the symbol, in the read picture's pixels.</param>
/// <param name="Node">The wire node (<see cref="WireGraph"/>) it connects to.</param>
public sealed record RecognisedPin(int Index, string Name, double X, double Y, int Node);

/// <summary>One symbol region, named (R-im8-7).</summary>
/// <param name="Template">The template that matched — and so the drawing convention — or null.</param>
/// <param name="Orientation">How the template lies on the picture; R0 when none matched.</param>
/// <param name="Score">The classifier's distance (the template matcher's in units of w); +∞ when nothing was tried.</param>
/// <param name="RunnerUp">The best reading of another kind.</param>
/// <param name="Confidence">The parts table's dot.</param>
/// <param name="Demotions">Better-scoring readings a structure check set aside.</param>
public sealed record RecognisedSymbol(
    SymbolRegion Region, ImageSymbolKind Kind, SymbolDisposition Disposition, SymbolTemplate? Template,
    SymbolOrientation Orientation, IReadOnlyList<RecognisedPin> Pins, double Score, SymbolCandidate? RunnerUp,
    PartConfidence Confidence, IReadOnlyList<SymbolDemotion> Demotions)
{
    /// <summary>The drawing convention the matched template follows, or null.</summary>
    public string? Convention => Template?.Convention;

    /// <summary>The kind as the parts table shows it: <c>?</c> for <see cref="ImageSymbolKind.Unknown"/>.</summary>
    public string KindLabel => Kind == ImageSymbolKind.Unknown ? "?" : Kind.ToString();
}

/// <summary>Every region of a reading, named, and the report.</summary>
public sealed record SchematicSymbolsRead(IReadOnlyList<RecognisedSymbol> Symbols, SymbolReport Report);

public static class SchematicSymbols
{
    /// <summary>The kinds that are cut out whatever their pin count (D9).</summary>
    public static bool IsDevice(ImageSymbolKind k) => k is ImageSymbolKind.Transistor or ImageSymbolKind.Amplifier or ImageSymbolKind.Ic;

    /// <summary>Names every symbol region of <paramref name="read"/> with <paramref name="classifier"/> (the template
    /// matcher when null). Pure.</summary>
    public static SchematicSymbolsRead Read(SchematicImageRead read, IImageSymbolClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        classifier ??= new TemplateSymbolClassifier();
        var symbols = read.Symbols.Select(r => Name(r, classifier.Classify(r, read.StrokeWidth))).ToList();
        return new SchematicSymbolsRead(symbols, SymbolReport.Of(symbols));
    }

    /// <summary>D9 applied to one classification.</summary>
    public static RecognisedSymbol Name(SymbolRegion region, SymbolClassification c)
    {
        int n = region.Attachments.Count;
        var best = c.Best;
        var kind = best?.Kind ?? ImageSymbolKind.Unknown;
        var disposition = n >= 3 || IsDevice(kind) ? SymbolDisposition.CutOut
                        : best is not null ? SymbolDisposition.Recognised
                        : n == 2 ? SymbolDisposition.Unknown
                        : SymbolDisposition.UnreadTerminal;

        IReadOnlyList<RecognisedPin> pins = best is not null
            ? [.. best.PinAttachments.Select((a, i) => Pin(i, best.Template.Pins[i].Name, region.Attachments[a]))]
            : [.. region.Attachments.Select((a, i) => Pin(i, (i + 1).ToString(CultureInfo.InvariantCulture), a))];
        return new RecognisedSymbol(region, kind, disposition, best?.Template, best?.Orientation ?? default, pins,
                                    best?.Score ?? c.RunnerUp?.Score ?? double.PositiveInfinity, c.RunnerUp,
                                    best is null ? PartConfidence.Low : c.Confidence, c.Demotions);
    }

    private static RecognisedPin Pin(int i, string name, SymbolAttachment a) => new(i, name, a.X, a.Y, a.Node);
}

/// <summary>What naming the symbols found (R-im8-8).</summary>
public sealed record SymbolReport
{
    /// <summary>Recognised parts by kind.</summary>
    public IReadOnlyDictionary<ImageSymbolKind, int> ByKind { get; init; } = new Dictionary<ImageSymbolKind, int>();

    /// <summary>Two-pin regions nothing matched: kind <c>?</c>.</summary>
    public int Unknown { get; init; }

    /// <summary>Each cut-out device: its kind (<see cref="ImageSymbolKind.Unknown"/> when unmatched) and pin count.</summary>
    public IReadOnlyList<(ImageSymbolKind Kind, int Pins)> CutOut { get; init; } = [];

    /// <summary>Readings set aside by a structure check, by the check.</summary>
    public IReadOnlyDictionary<StructureCheck, int> Demotions { get; init; } = new Dictionary<StructureCheck, int>();

    public int UnreadTerminals { get; init; }

    public static SymbolReport Of(IReadOnlyList<RecognisedSymbol> symbols) => new()
    {
        ByKind = symbols.Where(s => s.Disposition == SymbolDisposition.Recognised).GroupBy(s => s.Kind)
                        .OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
        Unknown = symbols.Count(s => s.Disposition == SymbolDisposition.Unknown),
        CutOut = [.. symbols.Where(s => s.Disposition == SymbolDisposition.CutOut).Select(s => (s.Kind, s.Pins.Count))],
        Demotions = symbols.SelectMany(s => s.Demotions).GroupBy(d => d.Failed).OrderBy(g => g.Key)
                           .ToDictionary(g => g.Key, g => g.Count()),
        UnreadTerminals = symbols.Count(s => s.Disposition == SymbolDisposition.UnreadTerminal),
    };

    /// <summary>The report in words, one finding per line; a count of nothing is not a line.</summary>
    public IReadOnlyList<string> Lines()
    {
        var r = new List<string>();
        if (ByKind.Count > 0)
            r.Add("Symbols: " + string.Join(", ", ByKind.Select(kv => $"{kv.Value} {Noun(kv.Key)}")) + ".");
        if (Unknown > 0) r.Add($"{Count(Unknown, "two-pin symbol")} matching no drawing, read as ?.");
        foreach (var (kind, pins) in CutOut)
            r.Add($"{(kind == ImageSymbolKind.Unknown ? "Multi-pin device" : Noun(kind))} cut out: {Count(pins, "pin")} become ports.");
        foreach (var (check, n) in Demotions)
            r.Add($"{Count(n, "reading")} set aside by the {check.ToString().ToLowerInvariant()} structure check.");
        if (UnreadTerminals > 0) r.Add($"{Count(UnreadTerminals, "unread terminal")} left open.");
        return r;
    }

    private static string Noun(ImageSymbolKind k) => k switch
    {
        ImageSymbolKind.TransmissionLine => "transmission line",
        ImageSymbolKind.ShortStub => "short stub",
        ImageSymbolKind.OpenStub => "open stub",
        ImageSymbolKind.Ic => "IC",
        _ => k.ToString().ToLowerInvariant(),
    };

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}
