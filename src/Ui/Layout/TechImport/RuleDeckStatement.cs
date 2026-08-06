// Reading ONE statement of a process rule deck (docs/design/layout-view.md §9A.5a).
//
// A deck statement is a chain: a base symbol, then a run of `.op(args)` calls. Some of those ops
// build a region; the last one may instead MEASURE it. Both of the deck's own shapes fall out of
// that one structure:
//
//     derived = metal.not(keepout).interacting(pad)   -> a derived-layer binding
//     derived.width(0.42.um, euclidian)               -> a measurement of it
//     metal.not(filler).space(0.2.um)                 -> both at once
//
// <b>Why a scanner and not a regex.</b> The reader's first version matched ONE `.op(arg)` per line,
// which reads `a.not(b)` and silently fails on `a.not(b).and(c)` — and a deck chains constantly.
// Measured on a real deck, chained operands were the single largest remaining category of unread
// rules, larger than any missing rule kind. A regex cannot count nested parentheses, so the chain
// has to be walked.
//
// This is deliberately NOT a Ruby parser. It recognises the one statement shape a deck states rules
// in and reports everything else as unread — see RuleDeckReader's own header for why a
// half-interpreted deck is worse than none.

using System.Text;

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>One `.name(arg, arg)` link in a statement chain.</summary>
/// <param name="Name">The operation, lower-case as the deck writes it.</param>
/// <param name="Args">Its arguments, trimmed, in source order. Empty for a bare `.merged`.</param>
internal sealed record DeckOp(string Name, IReadOnlyList<string> Args);

/// <summary>
/// A parsed deck statement: an optional assignment target, a base symbol, and the chain applied to
/// it.
/// </summary>
internal sealed record DeckStatement(string? AssignTo, string Base, IReadOnlyList<DeckOp> Ops);

internal static class RuleDeckStatement
{
    /// <summary>
    /// Region-building operations, mapped to circuitRF's own expression vocabulary.
    ///
    /// <para><c>join</c> is a deck's union of two RESULT sets, which is the same operation
    /// <c>or</c> names here — the deck distinguishes them by intent, not by geometry.</para>
    /// </summary>
    private static readonly Dictionary<string, string> RegionOps = new(StringComparer.Ordinal)
    {
        ["and"] = "and",
        ["or"] = "or",
        ["join"] = "or",
        ["not"] = "not",
        ["xor"] = "xor",
        ["interacting"] = "interacting",
        ["not_interacting"] = "not_interacting",
        ["inside"] = "inside",
        ["outside"] = "outside",
        ["covering"] = "covering",
        ["not_covering"] = "not_covering",
        ["merged"] = "merged",
        ["holes"] = "holes",
        ["sized"] = "sized",
        ["size"] = "sized",
        ["with_area"] = "with_area",
    };

    /// <summary>
    /// A deck writes an open range bound as <c>nil</c> — <c>with_area(0, limit)</c>,
    /// <c>with_length(nil, x)</c>. Zero is open for a lower bound too, since no polygon has
    /// negative area and "at least 0" selects everything.
    /// </summary>
    private static bool IsOpenBound(string token) =>
        token is "nil" or "0" or "" || token.Equals("nil", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Operations that pass a region through unchanged as far as this model is concerned.
    ///
    /// <para><c>polygons</c> converts an edge-pair result back to polygons and <c>forget</c> frees
    /// memory — neither changes which region is being talked about, so dropping them keeps a chain
    /// readable instead of failing on bookkeeping the deck needs and circuitRF does not.</para>
    /// </summary>
    private static readonly HashSet<string> PassThroughOps =
        new(StringComparer.Ordinal) { "polygons", "forget", "merged_semantics", "raw", "flatten" };

    /// <summary>
    /// Geometric operations this model recognises as RULE-SHAPED but cannot express.
    ///
    /// <para>The catch-all counter is restricted to this list on purpose. A deck is a program: most
    /// of its statements are logging, counting, duplication and iteration, and counting those as
    /// "rules circuitRF cannot check" turned an honest report of 128 into a meaningless 2,348. A
    /// number a user cannot act on is worse than no number — it makes the real figure unfindable.
    /// Adding an operation here is how it becomes visible in that report.</para>
    /// </summary>
    private static readonly HashSet<string> KnownGeometricOps = new(StringComparer.Ordinal)
    {
        "area", "isolated", "edges", "angle", "with_angle", "with_length", "with_area",
        "with_distance", "drc", "density", "extent", "extents", "rectangles", "non_rectangles",
        "coincident_edges", "with_coincident_edges", "coincident_part", "interacting_with_text",
        "enclosed_at_intersecting_edges", "middle", "extended", "centers", "corners",
    };

    /// <summary>
    /// Strips a deck's own wrapper prefix.
    ///
    /// <para>A process may ship a second, self-contained "maximal" deck that wraps every primitive
    /// in an <c>ext_</c> method of its own — <c>ext_and</c>, <c>ext_width</c>, <c>ext_separation</c>
    /// — defined in that file as thin forwarders. They are the same operations under a different
    /// spelling, so stripping the prefix reads that deck with no second vocabulary to maintain.
    /// A name that is not a wrapper is returned unchanged, so this can never rename a real op.</para>
    /// </summary>
    public static string Canonical(string name) =>
        name.StartsWith("ext_", StringComparison.Ordinal) ? name[4..] : name;

    public static bool IsRegionOp(string name) => RegionOps.ContainsKey(Canonical(name));
    public static bool IsPassThroughOp(string name) => PassThroughOps.Contains(Canonical(name));
    public static bool IsKnownGeometricOp(string name) => KnownGeometricOps.Contains(Canonical(name));

    /// <summary>The circuitRF expression name for a deck region op.</summary>
    public static string ExpressionNameFor(string deckOp) => RegionOps[Canonical(deckOp)];

    /// <summary>
    /// Parses `[target =] base.op(args).op(args)...`. Returns false for anything that is not that
    /// shape — a bare expression, a control-flow line, a method definition.
    /// </summary>
    public static bool TryParse(string line, out DeckStatement? statement)
    {
        statement = null;
        var s = new Scanner(line);

        s.SkipWhitespace();
        string first = s.ReadIdentifier();
        if (first.Length == 0) return false;

        string? assignTo = null;
        string baseSymbol = first;

        s.SkipWhitespace();
        if (s.Peek() == '=' && s.Peek(1) != '=')
        {
            s.Advance();
            s.SkipWhitespace();
            assignTo = first;
            baseSymbol = s.ReadIdentifier();
            if (baseSymbol.Length == 0) return false;
        }

        var ops = new List<DeckOp>();
        while (true)
        {
            s.SkipWhitespace();
            if (s.Peek() != '.') break;
            s.Advance();
            s.SkipWhitespace();

            string name = s.ReadIdentifier();
            if (name.Length == 0) return false;

            var args = new List<string>();
            s.SkipWhitespace();
            if (s.Peek() == '(' && !s.TryReadArgs(args)) return false;

            ops.Add(new DeckOp(name, args));
        }

        if (ops.Count == 0) return false;

        statement = new DeckStatement(assignTo, baseSymbol, ops);
        return true;
    }

    /// <summary>
    /// Builds the region expression for the first <paramref name="opCount"/> links of a chain.
    ///
    /// <para>Returns null the moment a link cannot be resolved — an operand that is not a known
    /// layer, or an operation this model has no expression for. <b>A partially-applied chain is
    /// never returned</b>: it would name a region the deck never meant, and a rule measured against
    /// the wrong region is exactly the silent-wrong-answer failure this whole area is built to
    /// avoid.</para>
    /// </summary>
    public static string? TryBuildRegion(
        DeckStatement statement,
        int opCount,
        Func<string, string?> lookupRegion)
    {
        string? expr = lookupRegion(statement.Base);
        if (expr is null) return null;

        for (int i = 0; i < opCount; i++)
        {
            var op = statement.Ops[i];

            string canonical = Canonical(op.Name);
            if (PassThroughOps.Contains(canonical)) continue;
            if (!RegionOps.TryGetValue(canonical, out string? exprOp)) return null;

            if (exprOp == "sized")
            {
                if (op.Args.Count == 0 || !TryReadMicrons(op.Args[0], out double um)) return null;

                // A technology's own DBU are always LayoutUnits.DefaultDbuPerMicron — neither
                // Technology nor the `.ctech` carries a resolution, the same fixed convention
                // SubstrateResolver already relies on for stackup thicknesses.
                long dbu = (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron, MidpointRounding.AwayFromZero);
                expr = $"sized({expr}, {dbu})";
                continue;
            }

            if (exprOp == "with_area")
            {
                if (op.Args.Count < 2) return null;

                string? lo = IsOpenBound(op.Args[0]) ? null : ToSquareDbu(op.Args[0]);
                string? hi = IsOpenBound(op.Args[1]) ? null : ToSquareDbu(op.Args[1]);
                if (lo is null && hi is null) return null;

                expr = $"with_area({expr}, {lo ?? ""}, {hi ?? ""})";
                continue;
            }

            if (exprOp is "merged" or "holes")
            {
                expr = $"{exprOp}({expr})";
                continue;
            }

            if (op.Args.Count == 0) return null;
            string? other = lookupRegion(op.Args[0]);
            if (other is null) return null;

            expr = $"{exprOp}({expr}, {other})";
        }

        return expr;
    }

    /// <summary>
    /// An area bound in square DBU, or null when the token is not a literal.
    ///
    /// <para>Areas scale by the SQUARE of the resolution. Read as a length this is wrong by a
    /// million at the default DBU, which would select everything or nothing with nothing in the
    /// output to suggest the unit was the problem.</para>
    /// </summary>
    private static string? ToSquareDbu(string token)
    {
        if (!TryReadMicrons(token, out double um2)) return null;
        double perUm = LayoutUnits.DefaultDbuPerMicron;
        return ((long)Math.Round(um2 * perUm * perUm, MidpointRounding.AwayFromZero))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads `0.42.um` / `0.42` / `v.um` — the forms a deck writes a length in.</summary>
    private static bool TryReadMicrons(string token, out double um)
    {
        string t = token.Trim();
        if (t.EndsWith(".um2", StringComparison.Ordinal)) t = t[..^4];
        else if (t.EndsWith(".um", StringComparison.Ordinal)) t = t[..^3];
        return double.TryParse(t, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out um);
    }

    /// <summary>
    /// Minimal scanner. Argument lists are read by counting parentheses and skipping string
    /// literals, so a nested call or a description containing a bracket does not truncate the list.
    /// </summary>
    private struct Scanner(string text)
    {
        private readonly string _text = text;
        private int _i = 0;

        public readonly char Peek(int ahead = 0) =>
            _i + ahead < _text.Length ? _text[_i + ahead] : '\0';

        public void Advance() => _i++;

        public void SkipWhitespace()
        {
            while (_i < _text.Length && char.IsWhiteSpace(_text[_i])) _i++;
        }

        public string ReadIdentifier()
        {
            int start = _i;
            while (_i < _text.Length && (char.IsLetterOrDigit(_text[_i]) || _text[_i] == '_')) _i++;
            return _text[start.._i];
        }

        public bool TryReadArgs(List<string> into)
        {
            if (Peek() != '(') return false;
            Advance();

            var current = new StringBuilder();
            int depth = 1;
            char quote = '\0';

            while (_i < _text.Length)
            {
                char c = _text[_i];

                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    current.Append(c);
                    _i++;
                    continue;
                }

                switch (c)
                {
                    case '"' or '\'':
                        quote = c;
                        current.Append(c);
                        break;

                    case '(' or '[' or '{':
                        depth++;
                        current.Append(c);
                        break;

                    case ')' when depth == 1:
                        Flush(into, current);
                        _i++;
                        return true;

                    case ')' or ']' or '}':
                        depth--;
                        current.Append(c);
                        break;

                    case ',' when depth == 1:
                        Flush(into, current);
                        break;

                    default:
                        current.Append(c);
                        break;
                }

                _i++;
            }

            return false;   // unterminated argument list — the statement is not readable
        }

        private static void Flush(List<string> into, StringBuilder sb)
        {
            string arg = sb.ToString().Trim();
            if (arg.Length > 0) into.Add(arg);
            sb.Clear();
        }
    }
}
