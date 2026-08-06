// Reads a process's own DESIGN-RULE DECK — the script a process ships that states, per layer, how
// narrow a feature may be and how close two of them may come.
//
// Recognised by GRAMMAR, never by extension, a tool's name or a folder convention — the same rule the
// stack and layer-table readers already follow, and for the same reason: a process arranges its own
// tree however it likes, and circuitRF must hold no knowledge of any particular authoring tool.
//
// ── What this reader deliberately does NOT try to be ────────────────────────────────────────────
//
// A rule deck is a PROGRAM. It has variables, arrays, loops, conditionals, and a large vocabulary of
// geometric operations (enclosure, separation with projection limits, angle and area filters, antenna
// ratios, density windows). Interpreting it in general is a language project, and a half-interpreted
// deck is worse than none: a rule silently mapped onto the wrong layer, or a conditional exclusion
// quietly dropped, produces a check that passes a design it should have failed.
//
// So this reads exactly the two rule shapes circuitRF can express today — minimum width and minimum
// spacing on one drawn layer — and COUNTS AND REPORTS everything else by operation name. The report
// is the point: a user who imports a process with 300 rules and gets 20 needs to see that number, not
// discover it by trusting a checker that only ever looked at a fourteenth of the deck.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>One rule the deck states in a form circuitRF can check.</summary>
/// <param name="Name">The deck's own name for the rule, so a violation traces back to the process's
/// own documentation rather than to a name circuitRF invented.</param>
/// <param name="StreamLayer">Stream layer number of the drawn layer the rule applies to.</param>
/// <param name="StreamDatatype">Stream datatype of that layer.</param>
/// <param name="ValueUm">The rule's value, in micrometres (the unit a deck states lengths in).</param>
/// <param name="Description">The deck's own one-line description, when it states one.</param>
public sealed record RuleDeckRule(
    string      Name,
    DrcRuleKind Kind,
    int         StreamLayer,
    int         StreamDatatype,
    double      ValueUm,
    string?     Description);

/// <summary>A rule shape circuitRF cannot express, and how many of them the deck states.</summary>
public sealed record RuleDeckUnsupported(string Operation, int Count);

/// <summary>What reading a process's rule deck turned up.</summary>
/// <param name="Rules">Every width/spacing rule that resolved to a real drawn layer.</param>
/// <param name="Unsupported">
/// Everything else, grouped by the operation the deck used, largest first. Never silently dropped —
/// see this file's header for why.
/// </param>
/// <param name="Notes">Anything the read could not do, stated rather than swallowed.</param>
/// <param name="ChosenValueTable">
/// Index of the rule-value table the read actually used, or -1 when it used none. A process ships one
/// table per corner and may ship unrelated configuration that looks like one, so the table is chosen
/// by which one the deck's own rule keys resolve against — never by file name or scan order.
/// </param>
public sealed record ProcessRuleDeck(
    IReadOnlyList<RuleDeckRule>        Rules,
    IReadOnlyList<RuleDeckUnsupported> Unsupported,
    IReadOnlyList<string>              Notes,
    int                                ChosenValueTable = -1)
{
    public static readonly ProcessRuleDeck Empty = new([], [], []);

    public int UnsupportedTotal => Unsupported.Sum(u => u.Count);
}

public static partial class RuleDeckReader
{
    // ── Recognition ───────────────────────────────────────────────────────────

    /// <summary>
    /// True when the text is a rule deck: it binds at least one drawn layer to a stream number, or
    /// reads at least one value out of a rule-value table. Both markers are the deck language's own
    /// grammar, not a file name.
    /// </summary>
    public static bool LooksLikeRuleDeck(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (LayerBindRegex().IsMatch(text) || ValueBindRegex().IsMatch(text));

    /// <summary>
    /// True when the text is a rule-VALUE table: JSON carrying a top-level object of rule name →
    /// number. Structural, so a table that spells its keys differently still reads.
    /// </summary>
    public static bool LooksLikeRuleValueTable(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return TryFindValueObject(doc.RootElement, out _);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Reads a rule-value table into name → value. Values are the deck's own numbers, in whatever unit
    /// the deck later applies to them (micrometres, for every length rule this reader recognises).
    /// Returns an empty map rather than throwing on anything unreadable.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ReadRuleValues(string json)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryFindValueObject(doc.RootElement, out var obj)) return values;
            foreach (var p in obj.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double v))
                    values[p.Name] = v;
        }
        catch (JsonException) { /* an unreadable table yields no values, never an exception */ }
        return values;
    }

    private static bool TryFindValueObject(JsonElement root, out JsonElement obj)
    {
        obj = default;
        if (root.ValueKind != JsonValueKind.Object) return false;

        // A table either IS the map, or wraps exactly one object that is. Both spellings appear;
        // neither is a name this reader depends on.
        int numeric = 0;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Number) numeric++;
            else if (p.Value.ValueKind == JsonValueKind.Object)
            {
                int inner = p.Value.EnumerateObject().Count(q => q.Value.ValueKind == JsonValueKind.Number);
                if (inner >= MinValuesForATable) { obj = p.Value; return true; }
            }
        }
        if (numeric >= MinValuesForATable) { obj = root; return true; }
        return false;
    }

    /// <summary>Below this a JSON object is some other configuration that happens to hold numbers.</summary>
    private const int MinValuesForATable = 8;

    // ── The read ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a set of deck files against a rule-value table.
    ///
    /// <para>All files are scanned for layer bindings FIRST, then for rules — a deck routinely
    /// declares its layers in one file and states its rules in a dozen others, so a single pass in
    /// file order would resolve nothing.</para>
    /// </summary>
    public static ProcessRuleDeck Read(
        IEnumerable<string>                  deckTexts,
        IReadOnlyDictionary<string, double>? ruleValues)
        => Read(deckTexts, ruleValues is null ? [] : [ruleValues]);

    /// <summary>
    /// Reads a set of deck files against every candidate rule-value table, using the one the deck's
    /// own keys actually resolve against.
    ///
    /// <para><b>The table is chosen by coverage, not by name or position.</b> A process ships one
    /// table per corner, and a kit routinely also ships unrelated configuration that is structurally
    /// a map of numbers. Picking the first candidate found would silently read a deck against a table
    /// that answers none of its keys — every rule would fall out as "value not stated" and the import
    /// would look like the deck was unreadable. Counting how many of the deck's OWN rule keys each
    /// table defines settles it with no knowledge of any process's file names.</para>
    /// </summary>
    public static ProcessRuleDeck Read(
        IEnumerable<string>                             deckTexts,
        IReadOnlyList<IReadOnlyDictionary<string, double>> valueTables)
    {
        var texts = deckTexts.ToList();
        var notes = new List<string>();

        var (ruleValues, chosen) = ChooseValueTable(texts, valueTables, notes);

        var layerByName = new Dictionary<string, (int Layer, int Datatype)>(StringComparer.Ordinal);
        foreach (var text in texts) CollectLayerBindings(text, layerByName);

        if (layerByName.Count == 0)
        {
            notes.Add("The rule deck binds no drawn layer to a stream number, so no rule in it could be " +
                      "resolved to a layer. Nothing was imported from it.");
            return new ProcessRuleDeck([], [], notes, chosen);
        }

        var rules       = new List<RuleDeckRule>();
        var unsupported = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen        = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in texts)
            ReadRules(text, layerByName, ruleValues, rules, unsupported, seen, notes);

        var grouped = unsupported
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new RuleDeckUnsupported(kv.Key, kv.Value))
            .ToList();

        return new ProcessRuleDeck(rules, grouped, notes, chosen);
    }

    private static (IReadOnlyDictionary<string, double>? Values, int Index) ChooseValueTable(
        List<string>                                       texts,
        IReadOnlyList<IReadOnlyDictionary<string, double>> tables,
        List<string>                                       notes)
    {
        if (tables.Count == 0) return (null, -1);
        if (tables.Count == 1) return (tables[0], 0);

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
            foreach (var m in ValueBindRegex().Matches(text).Cast<Match>())
                referenced.Add(m.Groups[2].Value);

        int best = 0, bestCover = -1;
        for (int i = 0; i < tables.Count; i++)
        {
            int cover = referenced.Count(k => tables[i].ContainsKey(k));
            if (cover > bestCover) { bestCover = cover; best = i; }
        }

        if (bestCover <= 0)
        {
            notes.Add("None of the rule-value tables found defines a value the rule deck asks for, so " +
                      "only rules stating their value in place could be read.");
            return (null, -1);
        }

        return (tables[best], best);
    }

    // ── Pass 1: what the deck's own names mean ────────────────────────────────

    /// <summary>
    /// Layer bindings are GLOBAL: a deck declares every drawn layer once, in its own definitions file,
    /// and every other file refers to those names.
    /// </summary>
    private static void CollectLayerBindings(string text, Dictionary<string, (int, int)> layerByName)
    {
        foreach (var m in LayerBindRegex().Matches(text).Cast<Match>())
            if (int.TryParse(m.Groups[2].Value, out int lay) &&
                int.TryParse(m.Groups[3].Value, out int dt))
                layerByName[m.Groups[1].Value] = (lay, dt);
    }

    /// <summary>
    /// Array bindings are FILE-LOCAL, deliberately — and this is not a nicety. Two files of one deck
    /// routinely bind the SAME ordinary name (a list called "the metals") to genuinely different
    /// lists, because each is a local variable in its own program. Collecting them globally lets the
    /// last file read win, and the rules of every earlier file silently resolve against another
    /// file's list — measured on a real process, that is the difference between reading eight
    /// back-end metal rules and reading none.
    /// </summary>
    private static Dictionary<string, List<string>> CollectArrayBindings(string text)
    {
        var arrays = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var m in ArrayBindRegex().Matches(text).Cast<Match>())
        {
            var members = m.Groups[2].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            if (members.Count > 0) arrays[m.Groups[1].Value] = members;
        }
        return arrays;
    }

    // ── Pass 2: the rules themselves ──────────────────────────────────────────

    /// <summary>
    /// A loop binding in scope: <c>arr.each do |v|</c> makes <c>v</c> stand for every member of
    /// <c>arr</c> in turn, so one written rule states several. Tracked with a plain block-depth
    /// counter rather than a parser — see <see cref="IsBlockOpener"/> for what that costs.
    /// </summary>
    private readonly record struct LoopBinding(int Depth, string Variable, string ArrayName);

    private static void ReadRules(
        string                                       text,
        Dictionary<string, (int Layer, int Datatype)> layerByName,
        IReadOnlyDictionary<string, double>?         ruleValues,
        List<RuleDeckRule>                           rules,
        Dictionary<string, int>                      unsupported,
        HashSet<string>                              seen,
        List<string>                                 notes)
    {
        var lines       = text.Split('\n');
        var arrayByName = CollectArrayBindings(text);
        var valueVars = new Dictionary<string, string>(StringComparer.Ordinal);   // local var -> rule key
        var loops     = new List<LoopBinding>();
        int depth     = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw  = lines[i];
            string line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            // Block bookkeeping first, so a rule inside a loop sees its own binding.
            if (line == "end")
            {
                depth = Math.Max(0, depth - 1);
                loops.RemoveAll(l => l.Depth > depth);
                continue;
            }

            if (LoopOpenRegex().Match(line) is { Success: true } loop)
            {
                depth++;
                loops.Add(new LoopBinding(depth, loop.Groups[2].Value, loop.Groups[1].Value));
                continue;
            }

            if (IsBlockOpener(line)) { depth++; continue; }

            if (ValueBindRegex().Match(line) is { Success: true } vb)
            {
                valueVars[vb.Groups[1].Value] = vb.Groups[2].Value;
                continue;
            }

            var call = RuleCallRegex().Match(line);
            if (!call.Success)
            {
                if (UnsupportedOpRegex().Match(line) is { Success: true } op)
                {
                    string name = op.Groups[1].Value;
                    unsupported[name] = unsupported.GetValueOrDefault(name) + 1;
                }
                continue;
            }

            string receiver = call.Groups[1].Value;
            var    kind     = call.Groups[2].Value == "width" ? DrcRuleKind.MinWidth : DrcRuleKind.MinSpacing;
            string operand  = call.Groups[3].Value;

            if (!TryResolveValue(operand, valueVars, ruleValues, out double valueUm))
            {
                unsupported[$"{call.Groups[2].Value} (value not stated in the rule table)"] =
                    unsupported.GetValueOrDefault($"{call.Groups[2].Value} (value not stated in the rule table)") + 1;
                continue;
            }

            var targets = ResolveReceiver(receiver, loops, arrayByName, layerByName);
            if (targets.Count == 0)
            {
                // A derived expression (one layer minus another, an angle- or size-filtered subset).
                // The rule is real; the LAYER it applies to is not one circuitRF draws on, so mapping
                // it onto the base layer would widen the rule silently.
                string label = $"{call.Groups[2].Value} on a derived layer";
                unsupported[label] = unsupported.GetValueOrDefault(label) + 1;
                continue;
            }

            var (ruleName, description) = ReadOutputLabel(lines, i);

            foreach (var (layer, datatype, suffix) in targets)
            {
                string name = suffix is null ? ruleName : $"{ruleName} ({suffix})";
                if (!seen.Add($"{name}|{layer}/{datatype}|{kind}")) continue;
                rules.Add(new RuleDeckRule(name, kind, layer, datatype, valueUm, description));
            }
        }

        // Only worth saying when a loop binding was STILL IN SCOPE at end of file — that is the one
        // state where the depth counter's approximation could have carried a layer list past its own
        // block and resolved a rule against the wrong layers. An unbalanced count with no live loop
        // changes nothing and reporting it would be noise on every import.
        if (loops.Count > 0)
            notes.Add("A rule-deck file's blocks did not balance while a layer list was in scope, so a " +
                      "rule may have been read against the wrong layers. Rules from it were still read.");
    }

    /// <summary>
    /// Whether a line opens a block that a later <c>end</c> closes.
    ///
    /// <para><b>The limit, stated rather than hidden:</b> this counts keywords, it does not parse. A
    /// trailing-condition line (<c>next if X</c>) correctly opens nothing because the test is on the
    /// line's START; a construct this list does not name would leave the depth counter low and could
    /// carry a loop binding past its own <c>end</c>. The consequence is bounded — a rule would resolve
    /// to the wrong LAYER SET, never to a wrong value — and it is reported: an unbalanced file adds a
    /// note. Recognising more constructs is a one-line addition here, not a redesign.</para>
    /// </summary>
    private static bool IsBlockOpener(string line) =>
        line.StartsWith("if ", StringComparison.Ordinal)     ||
        line.StartsWith("unless ", StringComparison.Ordinal) ||
        line.StartsWith("while ", StringComparison.Ordinal)  ||
        line.StartsWith("case ", StringComparison.Ordinal)   ||
        line.StartsWith("def ", StringComparison.Ordinal)    ||
        line == "begin"                                      ||
        BlockOpenRegex().IsMatch(line);

    /// <summary>
    /// Which drawn layers a rule's receiver names. One symbol resolves to one layer; a loop variable
    /// resolves to every member of the array it iterates, each carrying its own name suffix so the
    /// imported rules stay distinguishable.
    /// </summary>
    private static List<(int Layer, int Datatype, string? Suffix)> ResolveReceiver(
        string                                       receiver,
        List<LoopBinding>                            loops,
        Dictionary<string, List<string>>             arrayByName,
        Dictionary<string, (int Layer, int Datatype)> layerByName)
    {
        var result = new List<(int, int, string?)>();

        for (int i = loops.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(loops[i].Variable, receiver, StringComparison.Ordinal)) continue;
            if (!arrayByName.TryGetValue(loops[i].ArrayName, out var members)) return result;

            foreach (string member in members)
                if (layerByName.TryGetValue(member, out var lk))
                    result.Add((lk.Layer, lk.Datatype, member));
            return result;
        }

        if (layerByName.TryGetValue(receiver, out var single))
            result.Add((single.Layer, single.Datatype, null));

        return result;
    }

    private static bool TryResolveValue(
        string                               operand,
        Dictionary<string, string>           valueVars,
        IReadOnlyDictionary<string, double>? ruleValues,
        out double                           valueUm)
    {
        // A literal, stated in place.
        if (double.TryParse(operand, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out valueUm))
            return true;

        valueUm = 0;
        if (!valueVars.TryGetValue(operand, out string? key)) return false;
        if (ruleValues is null) return false;
        if (!ruleValues.TryGetValue(key, out double v)) return false;

        valueUm = v;
        return true;
    }

    /// <summary>
    /// A deck states a rule's own name and description in the call that REPORTS it, a line or two
    /// below the geometric test. Looked for forward from the test rather than assumed adjacent.
    /// </summary>
    private static (string Name, string? Description) ReadOutputLabel(string[] lines, int at)
    {
        for (int i = at; i < Math.Min(lines.Length, at + OutputLookahead); i++)
        {
            var m = OutputRegex().Match(StripComment(lines[i]));
            if (!m.Success) continue;

            string name = CleanRuleName(m.Groups[1].Value);
            string? desc = m.Groups[2].Success ? CleanDescription(m.Groups[2].Value) : null;
            if (name.Length > 0) return (name, desc);
        }
        return ("Rule", null);
    }

    private const int OutputLookahead = 4;

    /// <summary>
    /// A rule stated inside a loop names itself with an interpolation (<c>"M#{n}.a"</c>) that stands
    /// for the level it is currently on. The interpolation is dropped rather than guessed at; which
    /// layer each imported copy actually applies to is carried by the suffix
    /// <see cref="ResolveReceiver"/> attaches, which is the deck's own name for that layer.
    /// </summary>
    private static string CleanRuleName(string s)
    {
        string cleaned = InterpolationRegex().Replace(s, "").Trim();
        return cleaned.Length > 0 ? cleaned : s.Trim();
    }

    /// <summary>Strips the deck language's own interpolation markers so a description reads as prose.</summary>
    private static string? CleanDescription(string s)
    {
        string cleaned = InterpolationRegex().Replace(s, "…").Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }

    /// <summary>
    /// Drops a trailing comment. Quote-aware, and it has to be: a deck states a rule's own
    /// description as an interpolated string (<c>"… width: #{value} µm"</c>), so cutting at the first
    /// <c>#</c> would truncate the string mid-quote and lose every rule's NAME and description — the
    /// two things that let a violation be traced back to the process's own documentation.
    /// </summary>
    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;                 // escaped character inside a string
                else if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == '#') return line[..i];
        }
        return line;
    }

    // ── Grammar ───────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^\s*(\w+)\s*=\s*get_polygons\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.Multiline)]
    private static partial Regex LayerBindRegex();

    [GeneratedRegex(@"^\s*(\w+)\s*=\s*\[\s*([\w\s,]+?)\s*\]\s*$", RegexOptions.Multiline)]
    private static partial Regex ArrayBindRegex();

    [GeneratedRegex(@"^\s*(\w+)\s*=\s*\w+\[\s*['""](\w+)['""]\s*\]\s*\.\s*to_f\s*$", RegexOptions.Multiline)]
    private static partial Regex ValueBindRegex();

    [GeneratedRegex(@"^\s*(?:\w+\s*=\s*)?(\w+)\.(width|space)\s*\(\s*([\w.]+?)(?:\.um)?\s*[,)]")]
    private static partial Regex RuleCallRegex();

    [GeneratedRegex(@"\.\s*(enclosed|enclosing|separation|sep|overlap|inside|outside|interacting|area|angle|edges|isolated|notch|holes|covering|density|extent|drc)\s*\(")]
    private static partial Regex UnsupportedOpRegex();

    [GeneratedRegex(@"^\s*(\w+)\s*\.\s*each(?:_with_index)?\s+do\s*\|\s*(\w+)")]
    private static partial Regex LoopOpenRegex();

    [GeneratedRegex(@"\bdo\s*(\|[^|]*\|)?\s*$")]
    private static partial Regex BlockOpenRegex();

    [GeneratedRegex(@"\.\s*output\s*\(\s*['""]([^'""]+)['""]\s*(?:,\s*[""']([^""']*)[""'])?")]
    private static partial Regex OutputRegex();

    [GeneratedRegex(@"#\{[^}]*\}")]
    private static partial Regex InterpolationRegex();
}
