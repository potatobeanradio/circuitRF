// Which symbol each word names, and which wire end — brief-img-9-text-and-values.md R-im9-4, R-im9-6, R-im9-7.
//
// THE ASSIGNMENT IS AS-10's (RefdesAssociation): the one-to-one matching of words to symbols that minimises the total
// distance from each word's centre to its symbol's box, per cluster of words and symbols that can reach one another,
// within three of the symbol's diagonals. It runs once per class, so a designator and a value can name one symbol:
//   designators → every symbol but a ground;
//   values      → resistors, inductors, capacitors and the unread two-pin kind; then the value's dimension is checked
//                 against the symbol's — a value in henries assigned to a capacitor is a MISMATCH, reported and left
//                 unattached, never applied;
//   line sets   → transmission lines and stubs only.
// A designator that contradicts its symbol's kind (L3 on a capacitor's drawing) is kept, and the kind's confidence
// drops one step: the parts table shows both and the person decides. Net and port names need no assignment — IM-7
// already put each name word at the wire end it labels (NetLabel, SupplyMark).
//
// D10: a word that could not be read is not a wrong word. A symbol with no designator is given the next free one of
// its kind's prefix, and the report says so; a symbol with no value gets its variable in IM-10.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A symbol a word may name.</summary>
/// <param name="Symbol">Its index among the reading's symbols.</param>
public sealed record LabelTarget(int Symbol, PixelBox Box, ImageSymbolKind Kind, PartConfidence Confidence);

/// <summary>What the words say about one symbol.</summary>
/// <param name="Designator">The designator read — or generated, when none was (R-im9-6); null for a ground.</param>
/// <param name="Generated">The designator was generated, not read.</param>
/// <param name="Contradicts">The designator's prefix names another kind than the symbol's drawing.</param>
/// <param name="Confidence">The kind's confidence, one step lower when the designator contradicts it.</param>
public sealed record SymbolLabels(int Symbol, ImageSymbolKind Kind, string? Designator, bool Generated,
                                  ImageWord? DesignatorWord, ImageWord? ValueWord, ImageWord? LineWord,
                                  bool Contradicts, PartConfidence Confidence)
{
    public WordValue? Value => ValueWord?.Reading?.Value;
    public IReadOnlyList<LineParameter>? LineParameters => LineWord?.Reading?.Parameters;
}

/// <summary>A net or port name at a wire end (R-im9-4).</summary>
/// <param name="Node">The <see cref="WireGraph"/> node it names.</param>
/// <param name="Port">The name is a port word, or labels a supply mark's net.</param>
public sealed record NodeName(int Node, string Name, ImageWord Word, bool Port);

/// <summary>A value assigned to a symbol of another kind, left unattached.</summary>
public sealed record KindMismatch(ImageWord Word, int Symbol, ImageSymbolKind Kind);

/// <summary>Everything the words say, attached.</summary>
public sealed record ImageTextLabels(
    IReadOnlyList<SymbolLabels> Symbols, IReadOnlyList<NodeName> Names, IReadOnlyList<KindMismatch> Mismatches,
    IReadOnlyList<ImageWord> Unattached, ImageTextReport Report);

public static class LabelAssociation
{
    // Pixel coordinates are carried to the shared assignment in sixteenths, so a box's edge is not rounded to a pixel.
    private const long Sub = 16;

    /// <summary>Attaches <paramref name="words"/> to the named symbols of <paramref name="symbols"/> and to the wire
    /// ends of <paramref name="read"/>. Pure.</summary>
    public static ImageTextLabels Associate(SchematicImageRead read, SchematicSymbolsRead symbols, IReadOnlyList<ImageWord> words)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(symbols);
        var targets = symbols.Symbols.Select((s, i) => new LabelTarget(i, s.Region.Box, s.Kind, s.Confidence)).ToList();
        return Associate(targets, words, read.NetLabels, read.SupplyMarks);
    }

    /// <summary>Attaches <paramref name="words"/> to <paramref name="targets"/> and, through IM-7's net labels and
    /// supply marks, to wire ends.</summary>
    public static ImageTextLabels Associate(IReadOnlyList<LabelTarget> targets, IReadOnlyList<ImageWord> words,
                                            IReadOnlyList<NetLabel>? labels = null, IReadOnlyList<SupplyMark>? supplies = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(words);
        var used = new HashSet<int>();

        // Names first: a word IM-7 put at a wire end names that net, whatever else it could be (P1 is a port there).
        var names = new List<NodeName>();
        void Name(int node, int region, bool supply)
        {
            foreach (var w in words.Where(w => w.Region == region && w.Reading is { } r
                                                && r.Class is WordClass.Name or WordClass.Designator))
            {
                names.Add(new NodeName(node, w.Reading!.Text, w, supply || w.Reading.IsPortWord));
                used.Add(w.Id);
            }
        }
        foreach (var l in labels ?? []) Name(l.Node, l.Text, false);
        foreach (var s in supplies ?? []) if (s.Text is { } t) Name(s.Node, t, true);

        var designator = new ImageWord?[targets.Count];
        var value = new ImageWord?[targets.Count];
        var line = new ImageWord?[targets.Count];
        var mismatches = new List<KindMismatch>();

        Assign(WordClass.Designator, k => k != ImageSymbolKind.Ground, (w, t) => designator[t] = w);
        Assign(WordClass.Value, TakesValue, (w, t) =>
        {
            if (w.Reading!.Value!.Fits(Dimension(targets[t].Kind)) || targets[t].Kind == ImageSymbolKind.Unknown) value[t] = w;
            else mismatches.Add(new KindMismatch(w, targets[t].Symbol, targets[t].Kind));   // reported as that, not as unattached
        });
        Assign(WordClass.LineParameters, IsLine, (w, t) => line[t] = w);

        // Designators for the symbols without one (D10).
        var taken = designator.Where(d => d is not null).Select(d => d!.Reading!.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<SymbolLabels>(targets.Count);
        for (int t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            string? name = designator[t]?.Reading!.Text;
            bool generated = false;
            if (name is null && Prefix(target.Kind) is { } prefix)
            {
                int n = 1;
                while (taken.Contains(prefix + n.ToString(CultureInfo.InvariantCulture))) n++;
                name = prefix + n.ToString(CultureInfo.InvariantCulture);
                taken.Add(name);
                generated = true;
            }
            bool contradicts = !generated && name is not null && KindOfDesignator(name) is { } k && Contradicts(k, target.Kind);
            var confidence = contradicts && target.Confidence > PartConfidence.Low ? target.Confidence - 1 : target.Confidence;
            result.Add(new SymbolLabels(target.Symbol, target.Kind, name, generated, designator[t], value[t], line[t],
                                        contradicts, confidence));
        }

        var unattached = words.Where(w => w.Class != WordClass.Unread && !used.Contains(w.Id)).ToList();
        return new ImageTextLabels(result, names, mismatches, unattached, ImageTextReport.Of(words, result, names, mismatches, unattached));

        // One class's words to the targets that take it, by AS-10's assignment.
        void Assign(WordClass cls, Func<ImageSymbolKind, bool> takes, Action<ImageWord, int> apply)
        {
            var ws = words.Where(w => w.Class == cls && !used.Contains(w.Id)).ToList();
            var ts = Enumerable.Range(0, targets.Count).Where(t => takes(targets[t].Kind)).ToList();
            if (ws.Count == 0 || ts.Count == 0) return;
            var claims = ws.Select(w => new PartClaim(PartEvidenceSource.Silkscreen,
                (long)Math.Round(w.Box.CentreX * Sub), (long)Math.Round(w.Box.CentreY * Sub), w.Text, 0)).ToList();
            var candidates = ts.Select(t => new RefdesCandidate(Box(targets[t].Box))).ToList();
            var assigned = RefdesAssociation.Assign(claims, candidates);
            for (int i = 0; i < ws.Count; i++)
            {
                if (assigned[i] < 0) continue;
                used.Add(ws[i].Id);
                apply(ws[i], ts[assigned[i]]);
            }
        }
    }

    private static Bbox Box(PixelBox b) => new(b.Left * Sub, b.Top * Sub, (b.Right + 1) * Sub, (b.Bottom + 1) * Sub);

    private static bool TakesValue(ImageSymbolKind k) =>
        k is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor or ImageSymbolKind.Capacitor or ImageSymbolKind.Unknown;

    private static bool IsLine(ImageSymbolKind k) =>
        k is ImageSymbolKind.TransmissionLine or ImageSymbolKind.ShortStub or ImageSymbolKind.OpenStub;

    private static UnitDimension Dimension(ImageSymbolKind k) => k switch
    {
        ImageSymbolKind.Resistor => UnitDimension.Resistance,
        ImageSymbolKind.Inductor => UnitDimension.Inductance,
        ImageSymbolKind.Capacitor => UnitDimension.Capacitance,
        _ => UnitDimension.None,
    };

    /// <summary>The prefix a generated designator takes; null for a ground. The unread two-pin kind is generated as a
    /// capacitor, as the artwork series generates it.</summary>
    public static string? Prefix(ImageSymbolKind k) => k switch
    {
        ImageSymbolKind.Resistor => "R",
        ImageSymbolKind.Inductor => "L",
        ImageSymbolKind.Capacitor or ImageSymbolKind.Unknown => "C",
        ImageSymbolKind.TransmissionLine or ImageSymbolKind.ShortStub or ImageSymbolKind.OpenStub => "TL",
        ImageSymbolKind.Diode => "D",
        ImageSymbolKind.Transistor => "Q",
        ImageSymbolKind.Amplifier or ImageSymbolKind.Ic => "U",
        ImageSymbolKind.Port => "P",
        _ => null,
    };

    /// <summary>The kind a designator's prefix names, where it names one plainly.</summary>
    private static ImageSymbolKind? KindOfDesignator(string designator) =>
        new string([.. designator.TakeWhile(char.IsAsciiLetter)]).ToUpperInvariant() switch
        {
            "R" => ImageSymbolKind.Resistor,
            "L" or "FB" => ImageSymbolKind.Inductor,
            "C" => ImageSymbolKind.Capacitor,
            "D" => ImageSymbolKind.Diode,
            "Q" => ImageSymbolKind.Transistor,
            _ => null,
        };

    /// <summary>A designator's kind contradicts a drawing's when both are among the kinds a prefix names plainly and
    /// they differ. An unread drawing contradicts nothing.</summary>
    private static bool Contradicts(ImageSymbolKind byName, ImageSymbolKind drawn) =>
        drawn is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor or ImageSymbolKind.Capacitor
            or ImageSymbolKind.Diode or ImageSymbolKind.Transistor
        && byName != drawn;
}

/// <summary>What reading a schematic picture's words found (R-im9-7).</summary>
public sealed record ImageTextReport
{
    public int Words { get; init; }
    public int Designators { get; init; }
    public int Values { get; init; }
    public int LineSets { get; init; }
    public int Names { get; init; }

    /// <summary>Words with no grammatical reading, each with its box.</summary>
    public IReadOnlyList<PixelBox> Unread { get; init; } = [];

    /// <summary>Of <see cref="Unread"/>, those not read because their type is too heavy for its size.</summary>
    public int TooHeavy { get; init; }

    /// <summary>Values assigned to a symbol of another kind, left unattached.</summary>
    public IReadOnlyList<KindMismatch> KindMismatches { get; init; } = [];

    /// <summary>Designators whose prefix names another kind than the symbol's drawing.</summary>
    public IReadOnlyList<string> Contradictions { get; init; } = [];

    /// <summary>Words read that name nothing: no symbol in reach, no wire end — a title, a note.</summary>
    public int Unattached { get; init; }

    /// <summary>Designators generated for symbols none was read for.</summary>
    public IReadOnlyList<string> Generated { get; init; } = [];

    public static ImageTextReport Of(IReadOnlyList<ImageWord> words, IReadOnlyList<SymbolLabels> symbols,
                                     IReadOnlyList<NodeName> names, IReadOnlyList<KindMismatch> mismatches,
                                     IReadOnlyList<ImageWord> unattached) => new()
    {
        Words = words.Count,
        Designators = words.Count(w => w.Class == WordClass.Designator),
        Values = words.Count(w => w.Class == WordClass.Value),
        LineSets = words.Count(w => w.Class == WordClass.LineParameters),
        Names = words.Count(w => w.Class == WordClass.Name),
        Unread = [.. words.Where(w => w.Class == WordClass.Unread).Select(w => w.Box)],
        TooHeavy = words.Count(w => w.TooHeavy),
        KindMismatches = mismatches,
        Contradictions = [.. symbols.Where(s => s.Contradicts).Select(s => s.Designator!)],
        Unattached = unattached.Count,
        Generated = [.. symbols.Where(s => s.Generated).Select(s => s.Designator!)],
    };

    /// <summary>The report in words, one finding per line; a count of nothing is not a line.</summary>
    public IReadOnlyList<string> Lines()
    {
        var r = new List<string>
        {
            $"{Count(Words, "word")} read: {Designators} designator{S(Designators)}, {Values} value{S(Values)}, " +
            $"{LineSets} line parameter set{S(LineSets)}, {Names} name{S(Names)}.",
        };
        if (Unread.Count > 0) r.Add($"{Count(Unread.Count, "word")} could not be read; {(Unread.Count == 1 ? "its box is" : "their boxes are")} kept.");
        if (TooHeavy > 0)
            r.Add($"{Count(TooHeavy, "word")} {(TooHeavy == 1 ? "is" : "are")} in type too heavy for its size to read reliably " +
                  "(bold, or small); they were not read.");
        foreach (var m in KindMismatches)
            r.Add($"'{m.Word.Text}' is not a {Noun(m.Kind)}'s value; it was left unattached.");
        foreach (var c in Contradictions)
            r.Add($"{c} names a different kind than its symbol is drawn as; both are shown.");
        if (Unattached > 0) r.Add($"{Count(Unattached, "word")} named no part and no wire.");
        if (Generated.Count > 0)
            r.Add($"No designator was read for {Count(Generated.Count, "part")}: named {string.Join(", ", Generated.Take(12))}" +
                  (Generated.Count > 12 ? ", …" : "") + ".");
        return r;
    }

    private static string Noun(ImageSymbolKind k) => k.ToString().ToLowerInvariant();
    private static string S(int n) => n == 1 ? "" : "s";
    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + S(n);
}
