// Text <-> WasmPredicate / WasmValue (docs/design/wbond.md §8.1).
//
// ── Why this is a separate file from DrcLayerExprParser rather than an edit to it ────────────────
//
// §8.1's requirement is that the wirebond vocabulary is added as new operands and functions INSIDE
// the existing language, not as a second rule language — and it is: one grammar family, one error
// style, one set of parsing conventions, and REGION operands are still parsed by
// `DrcLayerExprParser` itself (`wire_to_layer(G1, and(8/0, 9/0))` hands its second argument straight
// to it, text and all).
//
// What is NOT shared is the entry point, and that is deliberate. The 2D language is region-valued
// and this one is scalar/boolean-valued; a single `TryParse` returning "either a region or a
// predicate" would make every caller ask which it got. More importantly, the gate for this milestone
// is that an expression using only the pre-existing 2D vocabulary parses BYTE-IDENTICALLY to before.
// Leaving `DrcLayerExprParser` untouched makes that true by construction rather than by a test that
// happens to pass — and the test is still pinned, because "by construction" is a claim that stops
// being true the first time someone edits the other file.
//
// ── Grammar ──────────────────────────────────────────────────────────────────────────────────────
//
//   predicate := orExpr
//   orExpr    := andExpr ( '||' andExpr )*
//   andExpr   := unary  ( '&&' unary  )*
//   unary     := '!' unary | '(' predicate ')' | comparison
//   comparison:= value ( '<' | '<=' | '>' | '>=' | '==' | '!=' ) value
//   value     := literal | call
//   literal   := NUMBER [ UNIT ]                       4mil · 100um · 30deg · 3
//   call      := wire_spacing '(' SET [',' SET] ')'
//              | foot_pitch   '(' SET [',' SET] ')'
//              | loop_height  '(' SET ')'
//              | span         '(' SET ')'
//              | angle_change '(' SET ')'
//              | dist_to_edge '(' SET ')'
//              | wire_to_layer'(' SET ',' REGION ')'
//              | envelope     '(' NAME ',' value ')'
//   SET       := IDENT                                 an array name, or `all`
//   REGION    := a DrcLayerExprParser expression
//
// Arguments are read as balanced raw spans and then interpreted according to the function's own
// signature. That is what lets a REGION argument be handed to the other parser unmodified, and it is
// what makes the errors specific ("'span' takes one wire set") instead of "unexpected token".

using System.Globalization;
using System.Text;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>Parses and formats the `.wasm` predicate language.</summary>
public static class DrcPredicateParser
{
    /// <summary>The wire set that means "every wire in the design", regardless of array.</summary>
    public const string AllWiresSet = "all";

    /// <summary>
    /// Parses <paramref name="text"/> as a rule predicate. Returns false with a specific
    /// <paramref name="error"/> rather than throwing — a malformed rule in a `.wasm` must degrade to
    /// "this one rule is unusable and says why", never to "the rule file failed to load".
    /// </summary>
    public static bool TryParse(string? text, out WasmPredicate? predicate, out string? error)
    {
        predicate = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Expression is empty.";
            return false;
        }

        var p = new Cursor(text);
        try
        {
            predicate = ParseOr(ref p);
            p.SkipWhitespace();
            if (!p.AtEnd)
            {
                predicate = null;
                error = $"Unexpected text after the expression at position {p.Index}.";
                return false;
            }
            return true;
        }
        catch (FormatException ex)
        {
            predicate = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Hard-failing convenience for tests and generated expressions, never user text.</summary>
    public static WasmPredicate Parse(string text) =>
        TryParse(text, out var e, out var err) && e is not null
            ? e
            : throw new FormatException(err ?? "Invalid assembly rule expression.");

    /// <summary>Parses a bare value (no comparison) — used by the envelope argument and by tests.</summary>
    public static bool TryParseValue(string? text, out WasmValue? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Expression is empty."; return false; }

        var p = new Cursor(text);
        try
        {
            value = ParseValue(ref p);
            p.SkipWhitespace();
            if (!p.AtEnd) { value = null; error = $"Unexpected text after the value at position {p.Index}."; return false; }
            return true;
        }
        catch (FormatException ex)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    // ── Format ───────────────────────────────────────────────────────────────

    public static string Format(WasmPredicate p)
    {
        var sb = new StringBuilder();
        Write(p, sb, top: true);
        return sb.ToString();
    }

    public static string Format(WasmValue v)
    {
        var sb = new StringBuilder();
        Write(v, sb);
        return sb.ToString();
    }

    private static void Write(WasmPredicate p, StringBuilder sb, bool top)
    {
        switch (p)
        {
            case WasmPredicate.Compare c:
                Write(c.Left, sb);
                sb.Append(' ').Append(OpText(c.Op)).Append(' ');
                Write(c.Right, sb);
                break;

            // Parentheses are written around every nested boolean rather than only where precedence
            // demands them. A re-formatted rule is read by a person checking it against the house's
            // own document; explicit grouping is worth more there than terseness.
            case WasmPredicate.And a:
                if (!top) sb.Append('(');
                Write(a.A, sb, top: false); sb.Append(" && "); Write(a.B, sb, top: false);
                if (!top) sb.Append(')');
                break;

            case WasmPredicate.Or o:
                if (!top) sb.Append('(');
                Write(o.A, sb, top: false); sb.Append(" || "); Write(o.B, sb, top: false);
                if (!top) sb.Append(')');
                break;

            case WasmPredicate.Not n:
                sb.Append("!(");
                Write(n.A, sb, top: true);
                sb.Append(')');
                break;

            default:
                throw new NotSupportedException($"Unhandled predicate node {p.GetType().Name}.");
        }
    }

    private static void Write(WasmValue v, StringBuilder sb)
    {
        switch (v)
        {
            case WasmValue.Literal l:
                sb.Append(FormatLiteral(l));
                break;

            case WasmValue.PairCall p:
                sb.Append(NameOf(p.Fn)).Append('(').Append(p.SetA);
                if (!string.Equals(p.SetA, p.SetB, StringComparison.OrdinalIgnoreCase))
                    sb.Append(", ").Append(p.SetB);
                sb.Append(')');
                break;

            case WasmValue.WireCall w:
                sb.Append(NameOf(w.Fn)).Append('(').Append(w.Set);
                if (w.Region is { } r) sb.Append(", ").Append(DrcLayerExprParser.Format(r));
                sb.Append(')');
                break;

            case WasmValue.EnvelopeCall e:
                sb.Append("envelope(").Append(e.Table).Append(", ");
                Write(e.Arg, sb);
                sb.Append(')');
                break;

            default:
                throw new NotSupportedException($"Unhandled value node {v.GetType().Name}.");
        }
    }

    /// <summary>
    /// A length round-trips through the unit it is most readable in rather than raw nanometres: a
    /// rule re-formatted as <c>101600</c> is unrecognisable next to the <c>4mil</c> the house wrote.
    /// </summary>
    private static string FormatLiteral(WasmValue.Literal l)
    {
        if (l.Kind == WasmQuantity.Angle)
            return l.Value.ToString("0.###", CultureInfo.InvariantCulture) + "deg";

        if (l.Kind == WasmQuantity.Number)
            return l.Value.ToString("0.######", CultureInfo.InvariantCulture);

        // MIL FIRST, deliberately. A `.wasm` is written by an assembly house against a bonder set up
        // in mil, so a rule re-formatted as `3.048mm` is unrecognisable next to the `120mil` they
        // wrote. The remaining order is what keeps a value someone typed in another unit readable in
        // that unit rather than being dragged into mil with six decimals.
        foreach (var unit in new[] { WBondUnit.Mil, WBondUnit.Mm, WBondUnit.Um, WBondUnit.Inch })
        {
            long per = WBondUnits.NmPerUnit(unit);
            double inUnit = l.Value / per;

            // At least one whole unit (so a length never reads as "0.01mm"), exact in it, and short
            // enough to be the number a person wrote rather than a conversion artefact.
            if (Math.Abs(inUnit) < 1.0) continue;
            if (Math.Abs(inUnit * per - l.Value) > 1e-6) continue;
            if (Math.Abs(inUnit - Math.Round(inUnit, 3)) > 1e-9) continue;

            return inUnit.ToString("0.###", CultureInfo.InvariantCulture) + WBondUnits.Suffix(unit);
        }

        return l.Value.ToString("0.###", CultureInfo.InvariantCulture) + "nm";
    }

    private static string OpText(WasmCompareOp op) => op switch
    {
        WasmCompareOp.Lt => "<",
        WasmCompareOp.Le => "<=",
        WasmCompareOp.Gt => ">",
        WasmCompareOp.Ge => ">=",
        WasmCompareOp.Eq => "==",
        WasmCompareOp.Ne => "!=",
        _ => throw new NotSupportedException($"Unhandled comparison {op}."),
    };

    private static string NameOf(WasmPairFunction fn) => fn switch
    {
        WasmPairFunction.WireSpacing => "wire_spacing",
        WasmPairFunction.FootPitch   => "foot_pitch",
        _ => throw new NotSupportedException($"Unhandled pair function {fn}."),
    };

    private static string NameOf(WasmWireFunction fn) => fn switch
    {
        WasmWireFunction.LoopHeight  => "loop_height",
        WasmWireFunction.Span        => "span",
        WasmWireFunction.AngleChange => "angle_change",
        WasmWireFunction.DistToEdge  => "dist_to_edge",
        WasmWireFunction.WireToLayer => "wire_to_layer",
        _ => throw new NotSupportedException($"Unhandled wire function {fn}."),
    };

    /// <summary>Every function name the language offers, for an "unknown function" message that can
    /// list the alternatives instead of only refusing.</summary>
    public static IReadOnlyList<string> FunctionNames { get; } =
    [
        "angle_change", "dist_to_edge", "envelope", "foot_pitch",
        "loop_height", "span", "wire_spacing", "wire_to_layer",
    ];

    // ── Recursive descent ────────────────────────────────────────────────────

    private static WasmPredicate ParseOr(ref Cursor p)
    {
        var left = ParseAnd(ref p);
        while (true)
        {
            p.SkipWhitespace();
            if (!p.TryConsume("||")) return left;
            left = new WasmPredicate.Or(left, ParseAnd(ref p));
        }
    }

    private static WasmPredicate ParseAnd(ref Cursor p)
    {
        var left = ParseUnary(ref p);
        while (true)
        {
            p.SkipWhitespace();
            if (!p.TryConsume("&&")) return left;
            left = new WasmPredicate.And(left, ParseUnary(ref p));
        }
    }

    private static WasmPredicate ParseUnary(ref Cursor p)
    {
        p.SkipWhitespace();
        if (p.AtEnd) throw new FormatException("Expected a comparison.");

        // `!` is negation only when it is not the start of `!=` — that one is a comparison operator
        // and belongs to whatever value precedes it, so it can never legitimately start a term.
        if (p.Current == '!' && !(p.Index + 1 < p.Text.Length && p.Text[p.Index + 1] == '='))
        {
            p.Advance();
            return new WasmPredicate.Not(ParseUnary(ref p));
        }

        // A parenthesised group is a nested predicate only if it actually contains one. `(a && b)` is
        // a group; `(span(G1))` would be a parenthesised VALUE, which the language does not offer —
        // saying so is better than parsing it as a predicate and failing further along.
        if (p.Current == '(' && LooksLikePredicateGroup(p))
        {
            p.Advance();
            var inner = ParseOr(ref p);
            p.SkipWhitespace();
            p.Expect(')');
            return inner;
        }

        var left = ParseValue(ref p);
        p.SkipWhitespace();

        var op = ParseCompareOp(ref p);
        var right = ParseValue(ref p);

        RequireComparable(left, right, p.Index);
        return new WasmPredicate.Compare(left, op, right);
    }

    /// <summary>
    /// True when the parenthesised group starting at the cursor contains a top-level comparison or
    /// boolean operator — i.e. it is a predicate group rather than something that merely starts with
    /// a parenthesis.
    /// </summary>
    private static bool LooksLikePredicateGroup(in Cursor p)
    {
        int depth = 0;
        for (int i = p.Index; i < p.Text.Length; i++)
        {
            char c = p.Text[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; if (depth == 0) return false; continue; }
            if (depth != 1) continue;
            if (c is '<' or '>' or '=') return true;
            if ((c == '&' || c == '|') && i + 1 < p.Text.Length && p.Text[i + 1] == c) return true;
            if (c == '!' && i + 1 < p.Text.Length && p.Text[i + 1] == '=') return true;
        }
        return false;
    }

    private static WasmCompareOp ParseCompareOp(ref Cursor p)
    {
        p.SkipWhitespace();
        if (p.TryConsume("<=")) return WasmCompareOp.Le;
        if (p.TryConsume(">=")) return WasmCompareOp.Ge;
        if (p.TryConsume("==")) return WasmCompareOp.Eq;
        if (p.TryConsume("!=")) return WasmCompareOp.Ne;
        if (p.TryConsume("<"))  return WasmCompareOp.Lt;
        if (p.TryConsume(">"))  return WasmCompareOp.Gt;

        throw new FormatException(
            $"Expected a comparison (<, <=, >, >=, ==, !=) at position {p.Index}. " +
            "An assembly rule states a limit, so every term has to be compared with something.");
    }

    /// <summary>
    /// Refuses a comparison between two different quantities — see <see cref="WasmQuantity"/> for the
    /// specific silent failure this prevents.
    /// </summary>
    private static void RequireComparable(WasmValue a, WasmValue b, int pos)
    {
        var qa = a.Quantity;
        var qb = b.Quantity;
        if (qa == qb) return;

        // A bare number against an angle is read as degrees: `angle_change(G1) <= 30` is what a house
        // writes, and there is no other quantity a plain number could mean beside an angle.
        if ((qa == WasmQuantity.Number && qb == WasmQuantity.Angle) ||
            (qb == WasmQuantity.Number && qa == WasmQuantity.Angle)) return;

        if (qa == WasmQuantity.Number || qb == WasmQuantity.Number)
            throw new FormatException(
                $"A length has to be compared against a length with a stated unit — write '4mil' or " +
                $"'100um', not a bare number (at position {pos}). A bare number would be read as " +
                "nanometres, which is almost never what was meant.");

        throw new FormatException(
            $"Cannot compare a {qa.ToString().ToLowerInvariant()} with a " +
            $"{qb.ToString().ToLowerInvariant()} (at position {pos}).");
    }

    private static WasmValue ParseValue(ref Cursor p)
    {
        p.SkipWhitespace();
        if (p.AtEnd) throw new FormatException("Expected a value.");

        // A literal is the only thing that starts with a digit, a sign or a decimal point.
        if (char.IsDigit(p.Current) || p.Current is '-' or '+' or '.')
            return ParseLiteral(ref p);

        string name = p.ReadIdentifier();
        if (name.Length == 0)
            throw new FormatException($"Expected a value at position {p.Index}.");

        p.SkipWhitespace();
        if (p.AtEnd || p.Current != '(')
            throw new FormatException(
                $"'{name}' is not a value on its own — a wire set only appears inside a function, " +
                $"e.g. loop_height({name}) (at position {p.Index}).");

        var args = ReadArguments(ref p, name);
        return Build(name, args, p.Index);
    }

    private static WasmValue ParseLiteral(ref Cursor p)
    {
        int start = p.Index;

        // The numeric run, then whatever letters follow it, handed to the ONE number-with-unit parser
        // the wBond editor already uses for every "type a new value" prompt. A second unit table here
        // is exactly how "4mil" would come to mean two different things in two places.
        while (!p.AtEnd && (char.IsDigit(p.Current) || p.Current is '-' or '+' or '.' or 'e' or 'E'))
        {
            if (p.Current is 'e' or 'E')
            {
                int next = p.Index + 1;
                if (next >= p.Text.Length || !(char.IsDigit(p.Text[next]) || p.Text[next] is '+' or '-')) break;
            }
            p.Advance();
        }

        int numEnd = p.Index;
        while (!p.AtEnd && (char.IsLetter(p.Current) || p.Current == 'µ' || p.Current == 'μ')) p.Advance();

        string numText = p.Text[start..numEnd];
        string suffix  = p.Text[numEnd..p.Index].Trim();

        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
            throw new FormatException($"'{p.Text[start..p.Index]}' is not a number (at position {start}).");

        if (suffix.Length == 0)
            return new WasmValue.Literal(value, WasmQuantity.Number);

        if (suffix.Equals("deg", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("degrees", StringComparison.OrdinalIgnoreCase))
            return new WasmValue.Literal(value, WasmQuantity.Angle);

        if (WBondUnits.TryParseUnit(suffix, out var unit))
            return new WasmValue.Literal(value * WBondUnits.NmPerUnit(unit), WasmQuantity.Length);

        throw new FormatException(
            $"Unknown unit '{suffix}' (at position {numEnd}). Lengths take nm, um, mm, mil or in; " +
            "angles take deg.");
    }

    /// <summary>
    /// Reads a call's arguments as balanced raw spans. Interpreting them is
    /// <see cref="Build"/>'s job, per the function's own signature — which is what lets a REGION
    /// argument be handed to <see cref="DrcLayerExprParser"/> exactly as written.
    /// </summary>
    private static List<string> ReadArguments(ref Cursor p, string fnName)
    {
        p.Expect('(');
        var args = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;

        while (true)
        {
            if (p.AtEnd) throw new FormatException($"Unterminated '{fnName}(' — expected ')'.");

            char c = p.Current;
            if (c == '(') depth++;
            if (c == ')')
            {
                if (depth == 0) { p.Advance(); args.Add(sb.ToString().Trim()); break; }
                depth--;
            }
            if (c == ',' && depth == 0) { p.Advance(); args.Add(sb.ToString().Trim()); sb.Clear(); continue; }

            sb.Append(c);
            p.Advance();
        }

        // `f()` reads as no arguments rather than one empty one.
        if (args.Count == 1 && args[0].Length == 0) args.Clear();
        return args;
    }

    private static WasmValue Build(string name, List<string> args, int pos)
    {
        switch (name)
        {
            case "wire_spacing": return Pair(WasmPairFunction.WireSpacing, name, args, pos);
            case "foot_pitch":   return Pair(WasmPairFunction.FootPitch,   name, args, pos);

            case "loop_height":  return Wire(WasmWireFunction.LoopHeight,  name, args, pos);
            case "span":         return Wire(WasmWireFunction.Span,        name, args, pos);
            case "angle_change": return Wire(WasmWireFunction.AngleChange, name, args, pos);
            case "dist_to_edge": return Wire(WasmWireFunction.DistToEdge,  name, args, pos);

            case "wire_to_layer":
            {
                Require(args.Count == 2,
                    "'wire_to_layer' takes a wire set and a layer region, e.g. wire_to_layer(G1, 8/0).", pos);
                string set = RequireSet(args[0], name, pos);
                if (!DrcLayerExprParser.TryParse(args[1], out var region, out string? err) || region is null)
                    throw new FormatException(
                        $"'wire_to_layer''s second argument is not a layer region: {err} (at position {pos})");
                return new WasmValue.WireCall(WasmWireFunction.WireToLayer, set, region);
            }

            case "envelope":
            {
                Require(args.Count == 2,
                    "'envelope' takes a table name and a value, e.g. envelope(max_loop, span(G1)).", pos);
                string table = RequireIdentifier(args[0], "envelope", "table name", pos);
                if (!TryParseValue(args[1], out var arg, out string? err) || arg is null)
                    throw new FormatException($"'envelope''s second argument is not a value: {err} (at position {pos})");
                return new WasmValue.EnvelopeCall(table, arg);
            }

            default:
                throw new FormatException(
                    $"Unknown function '{name}' (at position {pos}). " +
                    $"The language offers: {string.Join(", ", FunctionNames)}.");
        }
    }

    private static WasmValue Pair(WasmPairFunction fn, string name, List<string> args, int pos)
    {
        Require(args.Count is 1 or 2,
            $"'{name}' takes one or two wire sets — one measures within a set, two measure between them.", pos);
        string a = RequireSet(args[0], name, pos);
        string b = args.Count == 2 ? RequireSet(args[1], name, pos) : a;
        return new WasmValue.PairCall(fn, a, b);
    }

    private static WasmValue Wire(WasmWireFunction fn, string name, List<string> args, int pos)
    {
        Require(args.Count == 1, $"'{name}' takes one wire set, e.g. {name}(G1).", pos);
        return new WasmValue.WireCall(fn, RequireSet(args[0], name, pos), null);
    }

    private static string RequireSet(string text, string fn, int pos) =>
        RequireIdentifier(text, fn, "wire set", pos);

    private static string RequireIdentifier(string text, string fn, string what, int pos)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new FormatException($"'{fn}' is missing a {what} (at position {pos}).");

        // Array names are the symbol's own pin names (G1, D1, MT); an envelope name is the house's.
        // Both are plain identifiers, so anything else is a mistake worth naming rather than accepting.
        if (!(char.IsLetter(text[0]) || text[0] == '_') ||
            !text.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new FormatException(
                $"'{text}' is not a valid {what} for '{fn}' — names are letters, digits and " +
                $"underscores, starting with a letter (at position {pos}).");

        return text;
    }

    private static void Require(bool ok, string message, int pos)
    {
        if (!ok) throw new FormatException($"{message} (at position {pos})");
    }

    /// <summary>Minimal scanner. A struct so parsing allocates nothing beyond the nodes it builds.</summary>
    private struct Cursor(string text)
    {
        public readonly string Text = text;
        public int Index = 0;

        public readonly bool AtEnd => Index >= Text.Length;
        public readonly char Current => Text[Index];

        public void Advance() => Index++;

        public void SkipWhitespace()
        {
            while (Index < Text.Length && char.IsWhiteSpace(Text[Index])) Index++;
        }

        public bool TryConsume(string s)
        {
            SkipWhitespace();
            if (Index + s.Length > Text.Length) return false;
            if (string.CompareOrdinal(Text, Index, s, 0, s.Length) != 0) return false;
            Index += s.Length;
            return true;
        }

        public void Expect(char c)
        {
            SkipWhitespace();
            if (AtEnd || Current != c)
                throw new FormatException($"Expected '{c}' at position {Index}.");
            Advance();
        }

        public string ReadIdentifier()
        {
            SkipWhitespace();
            int start = Index;
            while (Index < Text.Length && (char.IsLetterOrDigit(Text[Index]) || Text[Index] == '_')) Index++;
            return Text[start..Index];
        }
    }
}
