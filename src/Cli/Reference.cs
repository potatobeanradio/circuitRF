using System.Text;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Reference;
using CircuitRF.Design.Schematic;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf reference [topic] [item]</c> — what a caller may WRITE, before it writes it
/// (brief-automation-6-reference-and-components.md).
///
/// <para><b>Why this verb exists.</b> <c>check</c> tells a caller that what it wrote is wrong;
/// <c>explain</c> tells it what circuitRF made of what it wrote. Neither tells it what it is
/// ALLOWED to write — the primitive type names, how many nets each takes, what its parameters are
/// called, what a unit suffix means, where a <c>define … end</c> block goes. A client that cannot
/// spell <c>MLIN</c> is blocked before <c>check</c> can help it, and the failure is quiet: a
/// component given a plausible-but-wrong parameter name resolves to that parameter's default and
/// simulates, producing a converged, complete-looking, wrong answer.</para>
///
/// <para><b>Two halves, and they are different in kind.</b> The prose topics are AUTHORED and
/// maintained (<see cref="ReferenceLibrary"/>); the rest are GENERATED at every call from the thing
/// that enforces them — the component catalogue from the live registries
/// (<see cref="ComponentCatalog"/>), the analysis directives from the schema <c>CnlReader</c>
/// validates against (<see cref="AnalysisDirectiveSchema"/>), and the document formats by
/// reflection over the types their readers deserialise into (<see cref="DocumentSchema"/>). There is
/// no third thing — in particular no grammar, schema or BNF WRITTEN BY HAND, because that would be a
/// second description of a reader that drifts from it silently, which is the exact failure this
/// whole series exists to prevent (§5).</para>
///
/// <para><b>It is read-only in every sense</b> — no analysis, no document, nothing written. Like
/// <c>check</c> and <c>explain</c> it runs on a read-only tree and on a workspace another process
/// has open (R-aut4-6), and unlike them it does not even take a path: a catalogue is about no
/// document, which is also why it is not a mode of <c>explain</c> (§5).</para>
///
/// <para><b>One verb with a topic, not one verb per topic</b> (R-aut6-1). An unknown topic is a
/// refusal LISTING the real ones, following <c>--tech</c>'s precedent from AUT-3 — never a
/// fallback, because a fallback here answers a different question than the one asked and says
/// nothing about it.</para>
/// </summary>
internal static class Reference
{
    /// <summary>The topic name the generated catalogue answers to. The prose page about components
    /// ships as <see cref="ReferenceLibrary.ComponentNotesTopic"/> — see there for why the machine
    /// answer keeps the plain name.</summary>
    public const string ComponentsTopic = "components";

    /// <summary>
    /// The second generated topic: every <c>analysis type=</c> token and every key it may carry,
    /// read from <see cref="AnalysisDirectiveSchema"/> — the same table <c>CnlReader</c> validates
    /// against (R-aut10-1).
    ///
    /// <para><b>It is generated for the reason the catalogue is.</b> An out-of-process client
    /// reconstructed one directive's key names in about fifteen round trips and found one
    /// <c>type=</c> token in eight guesses, because the only written description of the directives
    /// covered roughly two of the six. A page that can fall behind the registry is the problem it
    /// was written to solve, so the gate asserts the two sets are equal in both directions.</para>
    /// </summary>
    public const string AnalysesTopic = "analyses";

    /// <summary>Every topic a caller may ask for, in the order the list prints them. Built from the
    /// library rather than written down, so a topic added there appears here with nothing to
    /// remember.</summary>
    public static IEnumerable<string> AllTopics
        => ReferenceLibrary.TopicNames
                           .Concat(DocumentSchema.All.Select(f => f.Topic))
                           .Concat([AnalysesTopic, ComponentsTopic]);

    public static int Run(string[] args)
    {
        string? topic = null, item = null;

        foreach (string a in args)
        {
            // No options at all, deliberately. `--json`, `--only` and `--group` are taken before
            // dispatch; anything else is a flag this verb does not have, and a dropped flag's value
            // would become the topic.
            if (a.StartsWith('-'))    return JsonRun.Fail(CliDiagnostics.ReferenceUnknownOption(a));
            if (topic is null)        topic = a;
            else if (item is null)    item  = a;
            else                      return JsonRun.Fail(CliDiagnostics.ReferenceTooManyArguments());
        }

        if (topic is null) return ListTopics(item);

        if (string.Equals(topic, ComponentsTopic, StringComparison.OrdinalIgnoreCase))
            return Components(item);

        if (string.Equals(topic, AnalysesTopic, StringComparison.OrdinalIgnoreCase))
            return Analyses(item);

        if (DocumentSchema.Find(topic) is { } format)
        {
            if (item is not null) return JsonRun.Fail(CliDiagnostics.ReferenceItemNotForTopic(topic));
            return Schema(format);
        }

        if (item is not null) return JsonRun.Fail(CliDiagnostics.ReferenceItemNotForTopic(topic));

        if (ReferenceLibrary.Find(topic) is not { } found)
            return JsonRun.Fail(CliDiagnostics.ReferenceUnknownTopic(topic, string.Join(", ", AllTopics)));

        return Topic(found);
    }

    // ── the topic list ───────────────────────────────────────────────────────

    /// <summary>
    /// Every topic and what it costs. The size is the SERVED size — a client choosing between a
    /// 4.7 kB page and a 37 kB one should be able to choose, and a list that hides the cost makes
    /// the cheap topics and the expensive ones look alike (R-aut6-3).
    /// </summary>
    private static int ListTopics(string? strayItem)
    {
        // `circuitrf reference <nothing> <something>` cannot happen from the command line, but a
        // tool call can name an item with no topic and it is a refusal rather than a silent drop.
        if (strayItem is not null) return JsonRun.Fail(CliDiagnostics.ReferenceItemWithoutTopic());

        var topics = new List<ReferenceTopicJson>();
        foreach (var t in ReferenceLibrary.Topics)
            topics.Add(new ReferenceTopicJson(t.Topic, t.Title, t.Summary, ByteLength(ReferenceLibrary.Read(t))));

        foreach (var f in DocumentSchema.All)
            topics.Add(new ReferenceTopicJson(
                f.Topic, f.Title, SchemaSummary(f), ByteLength(DocumentSchema.Render(f))));

        topics.Add(new ReferenceTopicJson(
            AnalysesTopic,
            "Analysis directives",
            $"Generated from the schema the .cnl reader validates against: the " +
            $"{AnalysisDirectiveSchema.Specs.Count} analysis type= tokens, their other accepted " +
            "spellings, and every key with its default and whether it is required. Name one to get " +
            "just that directive.",
            ByteLength(RenderAnalyses(AnalysisDirectiveSchema.Specs))));

        var catalog = ComponentCatalog.All();
        topics.Add(new ReferenceTopicJson(
            ComponentsTopic,
            "Component types",
            $"Generated from the live registries: the {catalog.Count} .cnl type tokens, how many " +
            "nets each instance line binds, their terminals, and every parameter with its default, " +
            "unit and visibility. Name one to get just that primitive.",
            ByteLength(RenderComponents(catalog))));

        JsonRun.Reference = new ReferenceReportJson(topics, null, null);

        Console.WriteLine("Reference topics — circuitrf reference <topic>");
        Console.WriteLine();
        foreach (var t in topics)
            Console.WriteLine($"  {t.Topic,-17} {Kb(t.Bytes),8}  {t.Title}");
        Console.WriteLine();
        Console.WriteLine("  circuitrf reference components <TYPE>   one primitive");
        Console.WriteLine("  circuitrf reference analyses <TYPE>     one analysis directive");
        return 0;
    }

    // ── one prose topic ──────────────────────────────────────────────────────

    private static int Topic(ReferenceTopic topic)
    {
        string text = ReferenceLibrary.Read(topic);

        JsonRun.Reference = new ReferenceReportJson(
            null, new ReferenceTopicJson(topic.Topic, topic.Title, topic.Summary, ByteLength(text), text),
            null);

        // Write, not WriteLine — the page ends with its own newline and adding a second one would
        // make `circuitrf reference netlist > netlist.md` differ from the page by a byte.
        Console.Out.Write(text);
        return 0;
    }

    // ── the generated document formats ───────────────────────────────────────

    /// <summary>
    /// One JSON document format, field by field. It is a TOPIC and not an item-taking one: a format
    /// is read whole, and a caller that wanted one field of it has already paid for the page.
    /// </summary>
    private static int Schema(DocumentSchema.Format format)
    {
        string text = DocumentSchema.Render(format);

        JsonRun.Reference = new ReferenceReportJson(
            null,
            new ReferenceTopicJson(format.Topic, format.Title, SchemaSummary(format), ByteLength(text), text),
            null, null, DocumentSchema.Json(format));

        Console.Out.Write(text);
        return 0;
    }

    /// <summary>The one line the topic list and the resource listing carry for a format. Built from
    /// the format itself so a new one needs nothing written down.</summary>
    public static string SchemaSummary(DocumentSchema.Format f)
        => $"Generated from the type the {f.Extension} reader deserialises into: every field, its " +
           "JSON type and what it is when a document omits it, with a minimal working example.";

    // ── the analysis directives ──────────────────────────────────────────────

    /// <summary>
    /// Every <c>analysis</c> directive, or one named by its <c>type=</c> token.
    ///
    /// <para>An unknown token is resolved through <see cref="AnalysisDirectiveSchema.Find"/> rather
    /// than by string equality, so <c>lpp</c>, <c>LoadpullPursuit</c> and <c>loadpull-pursuit</c>
    /// all reach the one directive they name — the reader accepts them, and a reference that
    /// refused a spelling the reader accepts would be teaching a client the wrong lesson.</para>
    /// </summary>
    private static int Analyses(string? type)
    {
        var all = AnalysisDirectiveSchema.Specs;

        var chosen = type is null
            ? all
            : AnalysisDirectiveSchema.Find(type) is { } found ? [found] : (IReadOnlyList<AnalysisDirectiveSpec>)[];

        if (chosen.Count == 0)
            return JsonRun.Fail(CliDiagnostics.ReferenceUnknownAnalysis(
                type!, string.Join(", ", AnalysisDirectiveSchema.TypeTokens)));

        JsonRun.Reference = new ReferenceReportJson(null, null, null, [.. chosen.Select(ToJson)], null);

        Console.Out.Write(RenderAnalyses(chosen));
        return 0;
    }

    /// <summary>
    /// One directive on the wire. The universal keys are repeated into EVERY entry rather than
    /// listed once beside them: a client that asked for one directive would otherwise be told an
    /// incomplete legal set, and it is two rows.
    /// </summary>
    private static ReferenceAnalysisJson ToJson(AnalysisDirectiveSpec s) => new(
        s.Type,
        s.TypeAliases,
        [
            .. AnalysisDirectiveSchema.UniversalKeys.Select(k => ToJson(k, universal: true)),
            .. s.Keys.Select(k => ToJson(k, universal: false)),
        ],
        s.BareWords,
        [.. s.RequiredOneOf.Select(g => (IReadOnlyList<string>)g)]);

    private static ReferenceAnalysisKeyJson ToJson(AnalysisDirectiveKey k, bool universal) => new(
        // The [i] travels in the NAME, because that is the form a caller writes and a client that
        // copied `Tone` out of an indexed row would have the one spelling the directive refuses.
        k.Indexed ? k.Name + "[i]" : k.Name,
        k.Required, k.Default, k.Summary, k.Indexed, universal);

    /// <summary>The directives' own text — what the topic list and the resource listing measure.
    /// Pure, like <see cref="CatalogText"/>.</summary>
    public static string AnalysesText() => RenderAnalyses(AnalysisDirectiveSchema.Specs);

    /// <summary>
    /// The human form, from the same table the JSON reads — one computation, so the two cannot
    /// disagree (R-aut1-9's rule again).
    /// </summary>
    private static string RenderAnalyses(IReadOnlyList<AnalysisDirectiveSpec> specs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analysis directives — one line each, in a .cnl or a TestBench:");
        sb.AppendLine();
        sb.AppendLine("    analysis <Name> type=<token> Key=value ...");
        sb.AppendLine();
        sb.AppendLine("A key this table does not list is REFUSED, not ignored, and so is a type= token.");
        sb.AppendLine("Frequencies and swept values are bare numbers in base SI unless a unit key says");
        sb.AppendLine("otherwise; a unit may also follow the number inline.");
        sb.AppendLine();

        foreach (var spec in specs)
        {
            sb.Append(spec.Type);
            if (spec.TypeAliases.Count > 0)
                sb.Append("   also: ").Append(string.Join(", ", spec.TypeAliases));
            sb.AppendLine();

            foreach (var group in spec.RequiredOneOf)
                sb.Append("  one of: ").AppendLine(string.Join(" | ", group));
            if (spec.BareWords.Count > 0)
                sb.Append("  bare words: ").AppendLine(string.Join(" ", spec.BareWords));

            foreach (var k in AnalysisDirectiveSchema.UniversalKeys.Concat(spec.Keys))
            {
                string name = k.Indexed ? k.Name + "[i]" : k.Name;
                string note = k.Required ? "required" : k.Default ?? "-";
                sb.AppendLine($"  {name,-22} {note,-14} {k.Summary}".TrimEnd());
            }

            // A directive with no keys of its own is a statement, not an omission — `type=dc` takes
            // nothing, which is what makes `type=dc Start=0` a refusal.
            if (spec.Keys.Count == 0)
                sb.AppendLine("  (no keys beyond the universal two: the bias point is a property of the circuit)");

            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── the catalogue ────────────────────────────────────────────────────────

    private static int Components(string? type)
    {
        var all = ComponentCatalog.All();

        var chosen = type is null
            ? all
            : [.. all.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))];

        if (chosen.Count == 0)
            return JsonRun.Fail(CliDiagnostics.ReferenceUnknownComponent(
                type!, string.Join(", ", all.Select(e => e.Type))));

        JsonRun.Reference = new ReferenceReportJson(null, null, [.. chosen.Select(ToJson)], null);

        Console.Out.Write(RenderComponents(chosen));
        return 0;
    }

    private static ReferenceComponentJson ToJson(CatalogEntry e) => new(
        e.Type, e.Simulatable, e.Placeable,
        e.Note.Length == 0 ? null : e.Note,
        new ReferenceNetsJson(e.Nets.Count, e.Nets.DeterminedBy,
                              e.Nets.Rule.Length == 0 ? null : e.Nets.Rule),
        ToJson(e.Ports),
        [.. e.Symbols.Select(s => new ReferenceSymbolJson(
            s.Kind, s.DisplayName, s.Category, s.SearchTerms, ToJson(s.Ports),
            [.. s.Parameters.Select(p => new ReferenceParameterJson(
                p.Name, p.Expression, p.Unit, p.Dimension, p.ShowOnSchematic,
                p.Meaning.Length == 0 ? null : p.Meaning))]))]);

    private static ReferencePortsJson ToJson(CatalogPorts p)
        => new(p.Count, p.Names, p.DeterminedBy, p.ListedAt,
               p.OrderNote.Length == 0 ? null : p.OrderNote);

    /// <summary>The catalogue's own text, rendered without running the verb — what the topic list
    /// and the resource listing measure to state a size. Pure: it touches neither
    /// <see cref="JsonRun"/> nor <see cref="Console"/>.</summary>
    public static string CatalogText() => RenderComponents(ComponentCatalog.All());

    /// <summary>
    /// The human form. Same catalogue, same order, same facts — the two forms read one computation,
    /// so they cannot disagree (R-aut1-9's rule, applied again).
    /// </summary>
    private static string RenderComponents(IReadOnlyList<CatalogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(e.Type);
            if (!e.Simulatable) sb.Append("   [not simulatable]");
            else if (!e.Placeable) sb.Append("   [no palette entry]");
            sb.AppendLine();

            // The NETLIST contract first, and on its own line, because it is the question a caller
            // about to write a .cnl is asking and the terminals below are not an answer to it
            // (R-aut10-2). Absent only where the token is not an instance line at all, and the note
            // then says which kind of thing it is instead.
            if (Nets(e.Nets) is { Length: > 0 } nets) sb.Append("  nets: ").AppendLine(nets);
            sb.Append("  terminals: ").AppendLine(TokenTerminals(e));
            // The ORDER note comes before the type note: it is about the thing the caller is holding
            // (which net goes where), while the type note is about the catalogue's own bookkeeping.
            if (e.Ports.OrderNote.Length > 0) sb.Append("  order: ").AppendLine(e.Ports.OrderNote);
            if (e.Note.Length > 0) sb.Append("  note: ").AppendLine(e.Note);

            foreach (var s in e.Symbols)
            {
                sb.Append($"  {s.Kind} ({s.DisplayName}) — {s.Category}");
                if (s.SearchTerms.Count > 0) sb.Append("   search: ").Append(string.Join(", ", s.SearchTerms));
                sb.AppendLine();
                sb.Append("    terminals: ").AppendLine(Ports(s.Ports));
                // Repeated per symbol rather than only at the token, because a token whose tiles
                // disagree drops it above and the tile is then the only place it is stated.
                if (s.Ports.OrderNote.Length > 0) sb.Append("    order: ").AppendLine(s.Ports.OrderNote);

                if (s.Parameters.Count == 0)
                {
                    sb.AppendLine("    parameters: none declared");
                    continue;
                }
                foreach (var p in s.Parameters)
                {
                    string row = $"    {p.Name,-18} {(p.Expression.Length == 0 ? "-" : p.Expression),-16} " +
                                 $"{(p.Unit.Length == 0 ? "-" : p.Unit),-6} {(p.ShowOnSchematic ? "shown" : "-"),-6}";
                    if (p.Meaning.Length > 0) row += "  " + p.Meaning;
                    // Trimmed, because the column padding is there to line the NEXT column up and a
                    // row with nothing after it would otherwise carry the padding into the output —
                    // trailing whitespace a caller diffs against and a reader cannot see.
                    sb.AppendLine(row.TrimEnd());
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The netlist net contract, in one line. Empty for a token that is not an instance line at all
    /// — a blank there is the honest answer and the entry's note says what the token IS.
    /// </summary>
    private static string Nets(CatalogNets n)
    {
        // The rule NAMES its own parameter — "SddPortCount=N binds 2N nets" — so prefixing it with
        // "set by SddPortCount" would say it twice. `determinedBy` travels in the JSON for a reader
        // that wants the parameter without parsing a sentence.
        if (n.Count is { } fixedCount) return fixedCount.ToString();
        return n.Rule;
    }

    /// <summary>
    /// The token's own terminal line. A blank answer has two causes and they are different facts, so
    /// they are worded differently: nothing draws the type at all, or several tiles draw it and
    /// disagree — in which case each tile's own line below IS the answer.
    /// </summary>
    private static string TokenTerminals(CatalogEntry e)
    {
        if (e.Ports is not { Count: null, DeterminedBy: null }) return Ports(e.Ports);
        return e.Symbols.Count == 0
            ? "no symbol draws this type, so nothing states its terminal names or their order"
            : "the tiles below draw different pin sets; each one's own line is the answer";
    }

    /// <summary>What a blank pin fact prints as. <see cref="TokenTerminals"/> replaces it at
    /// the TOKEN level, where the two reasons for a blank are worth telling apart; a SYMBOL
    /// reaching it would be a kind whose pin count varies with no parameter naming it.</summary>
    private const string Unstated = "not stated by the symbol registry";

    private static string Ports(CatalogPorts p)
    {
        if (p.DeterminedBy is { } by)
            return p.Names.Count == 0
                ? $"set by {by}"
                : $"set by {by} — at {by}={p.ListedAt}: {string.Join(" ", p.Names)}";
        // TrimEnd because an unnamed terminal set leaves the separator with nothing after it —
        // trailing whitespace a caller diffs against and a reader cannot see.
        if (p.Count is { } n) return $"{n}  {string.Join(" ", p.Names)}".TrimEnd();
        return Unstated;
    }

    private static int ByteLength(string text) => Encoding.UTF8.GetByteCount(text);

    private static string Kb(int bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} kB";
}
