// Text <-> DrcLayerExpr (docs/design/layout-view.md §9A.5).
//
// <b>Why text and not polymorphic JSON.</b> A layer expression is stored in the `.ctech`, which is
// a file people read, diff and hand-edit — the same argument that keeps the rest of that format
// plain. A `$type`-discriminated object tree for `and(1/0, not(2/0, 3/0))` runs to a dozen nested
// objects and is unreadable in a diff; the text form is one field. It also gives the Technology
// Editor something to show and validate directly, instead of a tree widget nobody wants to build.
// The cost is a parser, which is fifty lines and is exercised by every round-trip test.
//
// <b>The grammar is deliberately function-call form throughout</b>, including for the boolean
// operations that would read more naturally as infix. Infix would need precedence rules, and a
// precedence mistake in a DRC rule is invisible: `a.not(b).and(c)` and `a.not(b.and(c))` both
// parse, both evaluate, and produce different regions. Function-call form has no precedence to get
// wrong — the parentheses ARE the structure.

using System.Globalization;
using System.Text;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// Parses and formats <see cref="DrcLayerExpr"/> in the `.ctech` text syntax.
///
/// <para>Grammar (whitespace insignificant):</para>
/// <code>
///   expr    := layer | call
///   layer   := INT '/' INT                          e.g.  8/0
///   call    := NAME '(' expr (',' expr)* [',' INT] ')'
///   NAME    := and | or | not | xor | sized | merged | holes
///            | interacting | not_interacting | inside | outside | covering | not_covering
/// </code>
/// </summary>
public static class DrcLayerExprParser
{
    /// <summary>
    /// Parses <paramref name="text"/>. Returns false with a specific <paramref name="error"/>
    /// rather than throwing — a malformed expression in a `.ctech` must degrade to "this one rule
    /// is unusable and says why", never to "the technology failed to load".
    /// </summary>
    public static bool TryParse(string? text, out DrcLayerExpr? expr, out string? error)
    {
        expr = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Expression is empty.";
            return false;
        }

        var p = new Cursor(text);
        try
        {
            expr = ParseExpr(ref p);
            p.SkipWhitespace();
            if (!p.AtEnd)
            {
                expr = null;
                error = $"Unexpected text after the expression at position {p.Index}.";
                return false;
            }
            return true;
        }
        catch (FormatException ex)
        {
            expr = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Convenience for callers that have already validated, or that want a hard failure
    /// (tests, and the importer's own generated expressions, which are never user text).</summary>
    public static DrcLayerExpr Parse(string text) =>
        TryParse(text, out var e, out var err) && e is not null
            ? e
            : throw new FormatException(err ?? "Invalid layer expression.");

    /// <summary>Renders an expression back to the parse syntax. Round-trips exactly.</summary>
    public static string Format(DrcLayerExpr expr)
    {
        var sb = new StringBuilder();
        Write(expr, sb);
        return sb.ToString();
    }

    private static void Write(DrcLayerExpr e, StringBuilder sb)
    {
        switch (e)
        {
            case DrcLayerExpr.Layer l:
                sb.Append(l.Key.Layer.ToString(CultureInfo.InvariantCulture))
                  .Append('/')
                  .Append(l.Key.Datatype.ToString(CultureInfo.InvariantCulture));
                break;

            case DrcLayerExpr.And a:    Binary("and", a.A, a.B, sb); break;
            case DrcLayerExpr.Or o:     Binary("or", o.A, o.B, sb); break;
            case DrcLayerExpr.Not n:    Binary("not", n.A, n.B, sb); break;
            case DrcLayerExpr.Xor x:    Binary("xor", x.A, x.B, sb); break;

            case DrcLayerExpr.Sized s:
                sb.Append("sized(");
                Write(s.A, sb);
                sb.Append(", ").Append(s.ByDbu.ToString(CultureInfo.InvariantCulture)).Append(')');
                break;

            case DrcLayerExpr.Select sel:
                Binary(NameOf(sel.Op), sel.A, sel.B, sb);
                break;

            case DrcLayerExpr.Holes h:  Unary("holes", h.A, sb); break;
            case DrcLayerExpr.Merged m: Unary("merged", m.A, sb); break;

            case DrcLayerExpr.WithArea wa:      Ranged("with_area", wa.A, wa.MinDbu2, wa.MaxDbu2, sb); break;
            case DrcLayerExpr.WithPerimeter wp: Ranged("with_perimeter", wp.A, wp.MinDbu, wp.MaxDbu, sb); break;

            default:
                throw new NotSupportedException($"Unhandled expression node {e.GetType().Name}.");
        }

        static void Binary(string name, DrcLayerExpr a, DrcLayerExpr b, StringBuilder into)
        {
            into.Append(name).Append('(');
            Write(a, into);
            into.Append(", ");
            Write(b, into);
            into.Append(')');
        }

        static void Unary(string name, DrcLayerExpr a, StringBuilder into)
        {
            into.Append(name).Append('(');
            Write(a, into);
            into.Append(')');
        }

        // An open bound writes as an empty slot rather than a sentinel number: `with_area(1/0, 100, )`
        // reads as "at least 100" to a person, where a magic large integer would not.
        static void Ranged(string name, DrcLayerExpr a, long? lo, long? hi, StringBuilder into)
        {
            into.Append(name).Append('(');
            Write(a, into);
            into.Append(", ").Append(lo?.ToString(CultureInfo.InvariantCulture) ?? "");
            into.Append(", ").Append(hi?.ToString(CultureInfo.InvariantCulture) ?? "");
            into.Append(')');
        }
    }

    private static string NameOf(DrcSelectOp op) => op switch
    {
        DrcSelectOp.Interacting     => "interacting",
        DrcSelectOp.NotInteracting  => "not_interacting",
        DrcSelectOp.Inside          => "inside",
        DrcSelectOp.Outside         => "outside",
        DrcSelectOp.Covering        => "covering",
        DrcSelectOp.NotCovering     => "not_covering",
        _                           => throw new NotSupportedException($"Unhandled select op {op}."),
    };

    private static DrcSelectOp? SelectOpFor(string name) => name switch
    {
        "interacting"       => DrcSelectOp.Interacting,
        "not_interacting"   => DrcSelectOp.NotInteracting,
        "inside"            => DrcSelectOp.Inside,
        "outside"           => DrcSelectOp.Outside,
        "covering"          => DrcSelectOp.Covering,
        "not_covering"      => DrcSelectOp.NotCovering,
        _                   => null,
    };

    // ── Recursive descent ────────────────────────────────────────────────────

    private static DrcLayerExpr ParseExpr(ref Cursor p)
    {
        p.SkipWhitespace();
        if (p.AtEnd) throw new FormatException("Expected a layer or an operation.");

        // A leaf is the only thing that starts with a digit.
        if (char.IsDigit(p.Current)) return ParseLayer(ref p);

        string name = p.ReadIdentifier();
        if (name.Length == 0)
            throw new FormatException($"Expected a layer or an operation at position {p.Index}.");

        p.SkipWhitespace();
        p.Expect('(');

        var args = new List<DrcLayerExpr>();
        var ranged = new List<long?>();
        long? intArg = null;
        bool afterComma = false;

        while (true)
        {
            p.SkipWhitespace();
            if (p.AtEnd) throw new FormatException($"Unterminated '{name}(' — expected ')'.");

            // A closing paren immediately after a comma is a TRAILING empty slot, not the end of
            // the list — `with_area(1/0, 100, )` states an open upper bound and must be read as two
            // bounds, not one. Dropping it silently turns "at least 100" into a malformed rule.
            if (p.Current == ')')
            {
                if (afterComma) ranged.Add(null);
                p.Advance();
                break;
            }

            // An EMPTY slot between two commas is likewise an open bound, not a syntax error.
            if (p.Current == ',') { p.Advance(); ranged.Add(null); afterComma = true; continue; }

            // `sized` takes a trailing integer distance; every other operand is an expression.
            // Distinguish by position rather than by lookahead: only the LAST argument may be an
            // integer, and only for an operation that declares one.
            if (args.Count > 0 && IsIntegerAhead(p))
            {
                long n = p.ReadInteger();
                intArg = n;
                ranged.Add(n);
            }
            else
            {
                args.Add(ParseExpr(ref p));
            }

            p.SkipWhitespace();
            if (p.AtEnd) throw new FormatException($"Unterminated '{name}(' — expected ')'.");
            if (p.Current == ',') { p.Advance(); afterComma = true; continue; }
            if (p.Current == ')') { p.Advance(); break; }
            throw new FormatException($"Expected ',' or ')' at position {p.Index}.");
        }

        return Build(name, args, intArg, ranged, p.Index);
    }

    /// <summary>True when the next token is an integer that is NOT the start of a `layer/datatype`
    /// leaf. The `/` is what tells them apart, so the whole run of digits has to be looked past.</summary>
    private static bool IsIntegerAhead(in Cursor p)
    {
        int i = p.Index;
        string s = p.Text;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        int digitsStart = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == digitsStart) return false;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i >= s.Length || s[i] != '/';
    }

    private static DrcLayerExpr ParseLayer(ref Cursor p)
    {
        long layer = p.ReadInteger();
        p.SkipWhitespace();
        p.Expect('/');
        p.SkipWhitespace();
        long datatype = p.ReadInteger();

        if (layer is < 0 or > int.MaxValue || datatype is < 0 or > int.MaxValue)
            throw new FormatException($"Layer '{layer}/{datatype}' is out of range.");

        return new DrcLayerExpr.Layer(new LayerKey((int)layer, (int)datatype));
    }

    private static DrcLayerExpr Build(
        string name, List<DrcLayerExpr> args, long? intArg, List<long?> ranged, int pos)
    {
        if (name is "with_area" or "with_perimeter")
        {
            Require(args.Count == 1, $"'{name}' takes one layer operand.", pos);
            Require(ranged.Count == 2, $"'{name}' takes a minimum and a maximum, either of which may be empty.", pos);
            Require(ranged[0].HasValue || ranged[1].HasValue, $"'{name}' needs at least one bound.", pos);

            return name == "with_area"
                ? new DrcLayerExpr.WithArea(args[0], ranged[0], ranged[1])
                : new DrcLayerExpr.WithPerimeter(args[0], ranged[0], ranged[1]);
        }

        if (SelectOpFor(name) is { } op)
        {
            Require(args.Count == 2, $"'{name}' takes two layer operands.", pos);
            return new DrcLayerExpr.Select(args[0], args[1], op);
        }

        switch (name)
        {
            case "and":
                Require(args.Count == 2, "'and' takes two layer operands.", pos);
                return new DrcLayerExpr.And(args[0], args[1]);
            case "or":
                Require(args.Count == 2, "'or' takes two layer operands.", pos);
                return new DrcLayerExpr.Or(args[0], args[1]);
            case "not":
                Require(args.Count == 2, "'not' takes two layer operands (it is a difference, not a complement).", pos);
                return new DrcLayerExpr.Not(args[0], args[1]);
            case "xor":
                Require(args.Count == 2, "'xor' takes two layer operands.", pos);
                return new DrcLayerExpr.Xor(args[0], args[1]);
            case "sized":
                Require(args.Count == 1, "'sized' takes one layer operand.", pos);
                Require(intArg.HasValue, "'sized' needs a distance in DBU, e.g. sized(1/0, 100).", pos);
                return new DrcLayerExpr.Sized(args[0], intArg!.Value);
            case "holes":
                Require(args.Count == 1, "'holes' takes one layer operand.", pos);
                return new DrcLayerExpr.Holes(args[0]);
            case "merged":
                Require(args.Count == 1, "'merged' takes one layer operand.", pos);
                return new DrcLayerExpr.Merged(args[0]);
            default:
                throw new FormatException($"Unknown operation '{name}'.");
        }
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

        public void Expect(char c)
        {
            SkipWhitespace();
            if (AtEnd || Current != c)
                throw new FormatException($"Expected '{c}' at position {Index}.");
            Advance();
        }

        public string ReadIdentifier()
        {
            int start = Index;
            while (Index < Text.Length && (char.IsLetter(Text[Index]) || Text[Index] == '_')) Index++;
            return Text[start..Index];
        }

        public long ReadInteger()
        {
            SkipWhitespace();
            int start = Index;
            if (Index < Text.Length && (Text[Index] == '-' || Text[Index] == '+')) Index++;
            int digits = Index;
            while (Index < Text.Length && char.IsDigit(Text[Index])) Index++;
            if (Index == digits) throw new FormatException($"Expected a number at position {start}.");
            return long.Parse(Text[start..Index], CultureInfo.InvariantCulture);
        }
    }
}
