// HZN-2 R-hzn2-3 — a Hertzon expression, translated token by token into circuitRF's one expression engine.
//
// NEVER STRING SUBSTITUTION. The text is tokenised, parsed into a tree, and the tree is walked for what the
// translation must know (does it carry a unit, which names does it reference, does it mix a bare number with a
// quantity). The output is the input's own tokens in the input's own order and spacing, with only the tokens whose
// SPELLING differs replaced: a unit (`1m` is a metre in Hertzon and a milli in circuitRF, so it becomes `1metre`),
// `**`, `and`/`or`, `log` (a base-10 logarithm in Hertzon, natural here, so it becomes `log10`), the constants,
// and a project variable's name (reserved prefix, reversible). An identifier, constant, unit or function circuitRF
// does not have, or whose meaning differs and cannot be carried exactly, is a refusal item naming it.
//
// What each spelling means was established by evaluating it in `hertzon-cli describe` (src/Design/RESOLVED.md §HZN).

using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>A translated expression.</summary>
/// <param name="Text">circuitRF's spelling.</param>
/// <param name="Bearing">The value carries a unit (a quantity), as opposed to a bare number.</param>
/// <param name="IsZero">The expression is the literal zero, with or without a unit.</param>
/// <param name="LiteralSi">Set when the whole expression is one number with an optional sign and unit (no name, no
/// arithmetic): its value in base SI. A literal opens as a DBU number, not as an expression (brief-hzn-0 D5).</param>
/// <param name="Refs">The Hertzon names (design or project variables) it references, as written.</param>
public sealed record HznExpr(string Text, bool Bearing, bool IsZero, double? LiteralSi, IReadOnlyList<string> Refs)
{
    public bool IsLiteral => LiteralSi is not null;
}

/// <summary>The variables an expression is read in: a design sees its own, then the project's; the project only its own.</summary>
public sealed class HznNames
{
    /// <summary>The reserved prefix a project variable's name takes in a <c>.c3d</c>, so brief 5 can write it back.</summary>
    public const string ProjectPrefix = "hznp_";

    public IReadOnlyDictionary<string, string> Design { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Project { get; init; } = new Dictionary<string, string>();

    public static string C3dProjectName(string name) => ProjectPrefix + name;

    /// <summary>What an object the document cannot hold under its Hertzon name is called: 'airbox' is reserved in a <c>.c3d</c>, so a Hertzon object of
    /// that name becomes <c>AirBox_hzn</c>. The translator, the writer and the patcher all name it through these two.</summary>
    public const string RenamedSuffix = "_hzn";

    public static string C3dObjectName(string hznName) =>
        string.Equals(hznName, CircuitRF.Design.ThreeD.C3dValidation.ReservedName, StringComparison.OrdinalIgnoreCase) ? hznName + RenamedSuffix : hznName;

    public static string HznObjectName(string c3dName) =>
        c3dName.EndsWith(RenamedSuffix, StringComparison.Ordinal)
        && string.Equals(c3dName[..^RenamedSuffix.Length], CircuitRF.Design.ThreeD.C3dValidation.ReservedName, StringComparison.OrdinalIgnoreCase)
            ? c3dName[..^RenamedSuffix.Length] : c3dName;

    public static string? HznProjectName(string c3dName) =>
        c3dName.StartsWith(ProjectPrefix, StringComparison.Ordinal) ? c3dName[ProjectPrefix.Length..] : null;
}

public sealed class HznExpressionTranslator
{
    private readonly HznNames _names;
    private readonly bool _projectScope;
    private readonly HznRefusalBuilder _refusals;
    private readonly Dictionary<(bool, string), Dims?> _bearingMemo;
    private readonly HashSet<(bool, string)> _inProgress;

    public HznExpressionTranslator(HznNames names, HznRefusalBuilder refusals, bool projectScope = false)
        : this(names, refusals, projectScope, [], []) { }

    private HznExpressionTranslator(HznNames names, HznRefusalBuilder refusals, bool projectScope,
                                    Dictionary<(bool, string), Dims?> memo, HashSet<(bool, string)> inProgress)
    {
        _names = names;
        _refusals = refusals;
        _projectScope = projectScope;
        _bearingMemo = memo;
        _inProgress = inProgress;
    }

    // ── units, constants, functions ───────────────────────────────────────────────────────────────

    /// <summary>Hertzon unit → circuitRF unit. Only the spellings evaluated in `hertzon-cli describe` are here.</summary>
    private static readonly Dictionary<string, string> HznUnits = new(StringComparer.Ordinal)
    {
        ["m"] = "metre", ["mm"] = "mm", ["um"] = "um", ["cm"] = "cm", ["nm"] = "nm", ["mil"] = "mil", ["in"] = "in",
        ["deg"] = "deg", ["rad"] = "rad",
        ["Hz"] = "Hz", ["kHz"] = "kHz", ["MHz"] = "MHz", ["GHz"] = "GHz", ["THz"] = "THz",
        ["ohm"] = "ohm", ["s"] = "s", ["ns"] = "ns", ["ps"] = "ps",
        ["H"] = "H", ["mH"] = "mH", ["uH"] = "uH", ["nH"] = "nH", ["pH"] = "pH",
        ["F"] = "F", ["mF"] = "mF", ["uF"] = "uF", ["nF"] = "nF", ["pF"] = "pF",
    };

    /// <summary>Constants, as the digits `hertzon-cli describe` reports them with, and the unit Hertzon gives them: c0 is a speed, so
    /// <c>c0/f0</c> is a length (a wavelength) and adds to one exactly.</summary>
    private static readonly Dictionary<string, (string Out, Dims D)> Constants = new(StringComparer.Ordinal)
    {
        ["c0"] = ("299792458", new Dims(1, -1, false)),
        ["e0"] = ("8.8541878128e-12", Dims.None),
        ["u0"] = ("1.2566370614359173e-06", Dims.None),
        ["PI"] = ("pi", Dims.None),
        ["pi"] = ("pi", Dims.None),
    };

    /// <summary>What a unit literal is a power of: a length, a time (a frequency is time to the -1), or another kind (an angle, an
    /// impedance, an inductance, a capacitance), which is only known to carry a unit.</summary>
    private static Dims UnitDims(string hznUnit) => hznUnit switch
    {
        "m" or "mm" or "um" or "cm" or "nm" or "mil" or "in" => new Dims(1, 0, false),
        "Hz" or "kHz" or "MHz" or "GHz" or "THz" => new Dims(0, -1, false),
        "s" or "ns" or "ps" => new Dims(0, 1, false),
        _ => new Dims(0, 0, true),
    };

    private enum FunctionShape { Pass, Trig, Min, If }

    private static readonly Dictionary<string, (string Out, FunctionShape Shape)> Functions = new(StringComparer.Ordinal)
    {
        ["sin"] = ("sin", FunctionShape.Trig), ["cos"] = ("cos", FunctionShape.Trig), ["tan"] = ("tan", FunctionShape.Trig),
        ["asin"] = ("asin", FunctionShape.Trig), ["acos"] = ("acos", FunctionShape.Trig), ["atan"] = ("atan", FunctionShape.Trig),
        ["atan2"] = ("atan2", FunctionShape.Trig),
        ["sinh"] = ("sinh", FunctionShape.Trig), ["cosh"] = ("cosh", FunctionShape.Trig), ["tanh"] = ("tanh", FunctionShape.Trig),
        ["exp"] = ("exp", FunctionShape.Trig), ["ln"] = ("ln", FunctionShape.Trig), ["log10"] = ("log10", FunctionShape.Trig),
        // Hertzon's `log` is the base-10 logarithm; circuitRF's is natural.
        ["log"] = ("log10", FunctionShape.Trig),
        ["sign"] = ("sign", FunctionShape.Trig),
        ["sqrt"] = ("sqrt", FunctionShape.Pass), ["abs"] = ("abs", FunctionShape.Pass), ["floor"] = ("floor", FunctionShape.Pass),
        ["ceil"] = ("ceil", FunctionShape.Pass), ["pow"] = ("pow", FunctionShape.Pass),
        ["min"] = ("min", FunctionShape.Min), ["max"] = ("max", FunctionShape.Min),
        ["if"] = ("if", FunctionShape.If),
    };

    // ── tokens ────────────────────────────────────────────────────────────────────────────────────

    private enum T { Space, Num, Ident, Op, LParen, RParen, Comma, End }

    private sealed class Tok(T kind, string raw)
    {
        public T Kind { get; } = kind;
        public string Raw { get; } = raw;
        public string Out { get; set; } = raw;
        public string? Unit { get; set; }
        public double Value { get; set; }
    }

    private sealed class Failure(string what, string why) : Exception(why)
    {
        public string What { get; } = what;
    }

    private static List<Tok> Tokenise(string s)
    {
        var toks = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                int j = i;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                toks.Add(new Tok(T.Space, s[i..j]));
                i = j;
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j < s.Length && s[j] == '.') { j++; while (j < s.Length && char.IsDigit(s[j])) j++; }
                if (j < s.Length && (s[j] is 'e' or 'E'))
                {
                    int k = j + 1;
                    if (k < s.Length && (s[k] is '+' or '-')) k++;
                    if (k < s.Length && char.IsDigit(s[k]))
                    {
                        while (k < s.Length && char.IsDigit(s[k])) k++;
                        j = k;
                    }
                }
                var t = new Tok(T.Num, s[i..j]);
                t.Value = double.Parse(t.Raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                // A unit glued to the number.
                int u = j;
                while (u < s.Length && (char.IsLetter(s[u]) || s[u] is '%' or '°')) u++;
                if (u > j) { t.Unit = s[j..u]; t = Retext(t, s[i..u]); }
                toks.Add(t);
                i = u;
            }
            else if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                toks.Add(new Tok(T.Ident, s[i..j]));
                i = j;
            }
            else if (c == '(') { toks.Add(new Tok(T.LParen, "(")); i++; }
            else if (c == ')') { toks.Add(new Tok(T.RParen, ")")); i++; }
            else if (c == ',') { toks.Add(new Tok(T.Comma, ",")); i++; }
            else
            {
                string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "**" or "<=" or ">=" or "==" or "!=" or "&&" or "||" or "//")
                {
                    toks.Add(new Tok(T.Op, two));
                    i += 2;
                }
                else
                {
                    toks.Add(new Tok(T.Op, c.ToString()));
                    i++;
                }
            }
        }
        // A unit separated from its number by spaces (`5 mm`), when the word is a unit and not a name.
        for (int k = 0; k + 2 < toks.Count; k++)
        {
            if (toks[k].Kind == T.Num && toks[k].Unit is null && toks[k + 1].Kind == T.Space && toks[k + 2].Kind == T.Ident
                && HznUnits.ContainsKey(toks[k + 2].Raw) && !(k + 3 < toks.Count && toks[k + 3].Kind == T.LParen))
            {
                toks[k].Unit = toks[k + 2].Raw;
                toks[k + 1].Out = "";
                toks[k + 2].Out = "";
                toks[k + 2] = new Tok(T.Space, toks[k + 2].Raw) { Out = "" };
            }
        }
        return toks;
    }

    private static Tok Retext(Tok t, string raw)
    {
        var n = new Tok(T.Num, raw) { Unit = t.Unit, Value = t.Value };
        return n;
    }

    // ── the tree walk ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A value's unit, as far as the translation needs it: its powers of length and time, and whether another unit (an
    /// angle, an impedance, ...) is in it. A quotient of two lengths is a plain number; a speed over a frequency is a length.</summary>
    private readonly record struct Dims(int L, int T, bool Other)
    {
        public bool Any => L != 0 || T != 0 || Other;
        public static Dims None => default;
        public Dims Times(Dims o) => new(L + o.L, T + o.T, Other || o.Other);
        public Dims Over(Dims o) => new(L - o.L, T - o.T, Other != o.Other);
    }

    private readonly record struct Node(Dims D, bool Zero)
    {
        public bool Bearing => D.Any;
    }

    private sealed class Reader(List<Tok> toks, HznExpressionTranslator owner)
    {
        private int _p;
        public readonly HashSet<string> Refs = new(StringComparer.Ordinal);
        public readonly List<string> Mixes = [];

        private int NextIndex(int from)
        {
            while (from < toks.Count && toks[from].Kind == T.Space) from++;
            return from;
        }

        private Tok Peek() { int i = NextIndex(_p); return i < toks.Count ? toks[i] : new Tok(T.End, ""); }

        private Tok Take() { int i = NextIndex(_p); _p = i + 1; return i < toks.Count ? toks[i] : new Tok(T.End, ""); }

        public bool AtEnd => Peek().Kind == T.End;

        private bool IsOp(string op) => Peek() is { Kind: T.Op } t && t.Raw == op;

        private bool IsWord(string w) => Peek() is { Kind: T.Ident } t && t.Raw == w;

        public Node Expression() => OrExpr();

        private Node OrExpr()
        {
            var l = AndExpr();
            while (IsWord("or")) { Take().Out = "||"; AndExpr(); l = new Node(Dims.None, false); }
            return l;
        }

        private Node AndExpr()
        {
            var l = Compare();
            while (IsWord("and")) { Take().Out = "&&"; Compare(); l = new Node(Dims.None, false); }
            return l;
        }

        private Node Compare()
        {
            var l = Add();
            if (Peek() is { Kind: T.Op } t && t.Raw is "<" or ">" or "<=" or ">=" or "==" or "!=")
            {
                Take();
                var r = Add();
                Agree(l, r, "compares");
                return new Node(Dims.None, false);
            }
            return l;
        }

        private void Agree(Node l, Node r, string what)
        {
            if (l.Bearing == r.Bearing) return;
            Node bare = l.Bearing ? r : l;
            if (bare.Zero) return;
            Mixes.Add(what);
        }

        private Node Add()
        {
            var l = Mul();
            while (Peek() is { Kind: T.Op } t && t.Raw is "+" or "-")
            {
                Take();
                var r = Mul();
                Agree(l, r, t.Raw == "+" ? "adds" : "subtracts");
                l = new Node(l.Bearing ? l.D : r.D, false);
            }
            return l;
        }

        private Node Mul()
        {
            var l = Unary();
            while (Peek() is { Kind: T.Op } t && t.Raw is "*" or "/" or "%" or "//")
            {
                if (t.Raw is "%" or "//")
                    throw new Failure($"operator {t.Raw}", $"'{t.Raw}' has no equivalent in circuitRF's expression language");
                Take();
                var r = Unary();
                // A quotient of two lengths is a plain number (w/h is 0 in the engine's own power count, and `w/h + 1` adds exactly 1);
                // a speed over a frequency (c0/f0) is a length.
                l = new Node(t.Raw == "/" ? l.D.Over(r.D) : l.D.Times(r.D), false);
            }
            return l;
        }

        private Node Unary()
        {
            if (Peek() is { Kind: T.Op } t && t.Raw is "-" or "+")
            {
                Take();
                var o = Unary();
                return new Node(o.D, o.Zero);
            }
            if (Peek() is { Kind: T.Op } n && n.Raw is "!")
                throw new Failure("operator !", "Hertzon does not accept '!' in an expression, so it has no meaning to carry across");
            return Power();
        }

        private Node Power()
        {
            var b = Primary();
            if (Peek() is { Kind: T.Op } t && t.Raw is "^" or "**")
            {
                Take();
                if (t.Raw == "**") t.Out = "^";
                var e = Unary();
                return new Node(b.D, false);
            }
            return b;
        }

        private Node Primary()
        {
            var t = Take();
            switch (t.Kind)
            {
                case T.Num:
                    return Number(t);
                case T.LParen:
                {
                    var inner = Expression();
                    if (Take().Kind != T.RParen) throw new Failure("expression", "an opening bracket is never closed");
                    return inner;
                }
                case T.Ident:
                    if (Peek().Kind == T.LParen) return Call(t);
                    return Identifier(t);
                case T.Op when t.Raw is "&&" or "||":
                    throw new Failure($"operator {t.Raw}", $"Hertzon does not accept '{t.Raw}' in an expression (it spells these 'and' and 'or')");
                default:
                    throw new Failure("expression", t.Kind == T.End ? "the expression ends where a value is expected" : $"unexpected '{t.Raw}'");
            }
        }

        private Node Number(Tok t)
        {
            if (t.Unit is null) return new Node(Dims.None, t.Value == 0);
            if (HznUnits.TryGetValue(t.Unit, out string? mapped))
            {
                t.Out = NumberText(t) + mapped;
                return new Node(UnitDims(t.Unit), t.Value == 0);
            }
            throw new Failure($"unit {t.Unit}", $"the unit '{t.Unit}' is not one circuitRF translates exactly");
        }

        private static string NumberText(Tok t)
        {
            // The raw text of a merged token may carry the unit and spaces after the digits; the digits end at the unit.
            int end = 0;
            string r = t.Raw;
            while (end < r.Length && (char.IsDigit(r[end]) || r[end] == '.')) end++;
            if (end < r.Length && r[end] is 'e' or 'E')
            {
                int k = end + 1;
                if (k < r.Length && r[k] is '+' or '-') k++;
                if (k < r.Length && char.IsDigit(r[k])) { while (k < r.Length && char.IsDigit(r[k])) k++; end = k; }
            }
            return r[..end];
        }

        private Node Identifier(Tok t)
        {
            string name = t.Raw;
            switch (owner.Resolve(name, out string outName, out Dims dims))
            {
                case Resolution.Variable:
                    Refs.Add(name);
                    t.Out = outName;
                    return new Node(dims, false);
                case Resolution.Constant:
                    t.Out = outName;
                    return new Node(dims, false);
                default:
                    throw new Failure($"identifier {name}", $"'{name}' is neither a variable of the design or project nor a constant circuitRF has");
            }
        }

        private Node Call(Tok name)
        {
            if (!Functions.TryGetValue(name.Raw, out var f))
                throw new Failure($"function {name.Raw}", $"circuitRF has no '{name.Raw}' with Hertzon's meaning");
            name.Out = f.Out;
            Take();   // (
            var args = new List<Node>();
            var commas = new List<Tok>();
            if (Peek().Kind != T.RParen)
            {
                args.Add(Expression());
                while (Peek().Kind == T.Comma) { commas.Add(Take()); args.Add(Expression()); }
            }
            var close = Take();
            if (close.Kind != T.RParen) throw new Failure("expression", "a call's bracket is never closed");
            // Hertzon's min and max take any number of values; circuitRF's take two. max(a,b,c) is max(a,max(b,c)).
            if (f.Shape == FunctionShape.Min && args.Count > 2)
            {
                for (int i = 0; i < args.Count - 2; i++) commas[i].Out = "," + f.Out + "(";
                close.Out = new string(')', args.Count - 1);
            }
            switch (f.Shape)
            {
                case FunctionShape.Trig:
                    return new Node(Dims.None, false);
                case FunctionShape.Pass:
                    return new Node(args.Count > 0 ? args[0].D : Dims.None, false);
                case FunctionShape.Min:
                    for (int i = 1; i < args.Count; i++) Agree(args[0], args[i], $"compares in {name.Raw}()");
                    return new Node(args.FirstOrDefault(a => a.Bearing).D, false);
                default:   // if(cond, then, else)
                    if (args.Count == 3) Agree(args[1], args[2], "chooses between");
                    return new Node(args.Skip(1).FirstOrDefault(a => a.Bearing).D, false);
            }
        }
    }

    // ── names ─────────────────────────────────────────────────────────────────────────────────────

    private enum Resolution { None, Variable, Constant }

    private Resolution Resolve(string name, out string outName, out Dims bearing)
    {
        outName = name;
        bearing = Dims.None;
        if (!_projectScope && _names.Design.ContainsKey(name))
        {
            bearing = Bearing(false, name);
            return Resolution.Variable;
        }
        if (_names.Project.ContainsKey(name))
        {
            outName = HznNames.C3dProjectName(name);
            bearing = Bearing(true, name);
            return Resolution.Variable;
        }
        if (Constants.TryGetValue(name, out var c))
        {
            outName = c.Out;
            bearing = c.D;
            return Resolution.Constant;
        }
        return Resolution.None;
    }

    /// <summary>The unit a variable's value carries — by translating its own expression, quietly.</summary>
    private Dims Bearing(bool project, string name)
    {
        var key = (project, name);
        if (_bearingMemo.TryGetValue(key, out var known)) return known ?? Dims.None;
        if (!_inProgress.Add(key)) return Dims.None;
        Dims result = Dims.None;
        try
        {
            string? expr = project ? _names.Project.GetValueOrDefault(name) : _names.Design.GetValueOrDefault(name);
            if (expr is not null)
            {
                var quiet = new HznExpressionTranslator(_names, new HznRefusalBuilder(), project, _bearingMemo, _inProgress);
                result = quiet.TranslateNode(expr) ?? Dims.None;
            }
        }
        finally { _inProgress.Remove(key); }
        _bearingMemo[key] = result;
        return result;
    }

    // ── entry points ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Translates <paramref name="source"/>; null when it holds anything circuitRF cannot carry exactly, in which
    /// case every such item has been added to the refusals, naming <paramref name="user"/>.</summary>
    public HznExpr? Translate(string source, string user) => TranslateFull(source, user)?.Expr;

    private Dims? TranslateNode(string source) => TranslateFull(source, "")?.D;

    private (HznExpr Expr, Dims D)? TranslateFull(string source, string user)
    {
        List<Tok> toks;
        try { toks = Tokenise(source); }
        catch (FormatException)
        {
            _refusals.Add(HznRefusalKind.Expression, "expression", $"'{source}' could not be read as a number or a name", user);
            return null;
        }
        var reader = new Reader(toks, this);
        Node node;
        try
        {
            node = reader.Expression();
            if (!reader.AtEnd) throw new Failure("expression", "it has text left over after a complete expression");
        }
        catch (Failure f)
        {
            _refusals.Add(HznRefusalKind.Expression, f.What, f.Message + $" (in '{source}')", user);
            return null;
        }
        if (reader.Mixes.Count > 0)
        {
            _refusals.Add(HznRefusalKind.Expression, "bare number with a unit",
                $"'{source}' {reader.Mixes[0]} a bare non-zero number to a quantity; whether Hertzon reads the bare number in the design's units there is not established, so circuitRF does not guess", user);
            return null;
        }
        string text = string.Concat(toks.Select(t => t.Out)).Trim();
        // The translation must be something circuitRF's parser accepts; this catches a spelling the walk above let through.
        try { Parser.Parse(text); }
        catch (ExpressionException e)
        {
            _refusals.Add(HznRefusalKind.Expression, "expression", $"'{source}' became '{text}', which circuitRF does not parse ({e.Message})", user);
            return null;
        }
        double? literal = LiteralOf(toks);
        return (new HznExpr(text, node.Bearing, node.Zero, literal, [.. reader.Refs]), node.D);
    }

    private static double? LiteralOf(List<Tok> toks)
    {
        var t = toks.Where(x => x.Kind != T.Space).ToList();
        int i = 0;
        double sign = 1;
        if (t.Count > 0 && t[0].Kind == T.Op && t[0].Raw is "-" or "+") { sign = t[0].Raw == "-" ? -1 : 1; i = 1; }
        if (t.Count != i + 1 || t[i].Kind != T.Num) return null;
        var n = t[i];
        double v = n.Value;
        if (n.Unit is not null)
        {
            if (!HznUnits.TryGetValue(n.Unit, out string? mapped)) return null;
            v *= CircuitRF.Core.Expressions.Units.Scale(mapped) ?? 1.0;
        }
        return sign * v;
    }

    /// <summary>Splits at the top-level occurrences of <paramref name="separator"/> only, so a call's own commas stay
    /// inside it: <c>if(x,1,2),0,0</c> is three parts, not five.</summary>
    public static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }
}
