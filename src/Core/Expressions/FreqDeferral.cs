using System.Globalization;
using System.Text;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Raised when a frequency-dependent value reaches somewhere that cannot evaluate one.
/// </summary>
public sealed class FrequencyDependentValueException(string message) : Exception(message);

/// <summary>
/// Lets a frequency-dependent expression stay an EXPRESSION as it flows down through cell
/// parameters and variables, instead of being forced to a number at each cell boundary.
///
/// <para><b>Why this exists.</b> The elaborator resolves parameters once, with no frequency bound —
/// that is what lets the numeric layer see only fully-resolved values. But <c>freq</c> IS bound, at
/// stamp time, inside the three models that are defined as functions of it (<c>Z_Port</c>'s
/// <c>Z[i,j]</c>, <c>Chain</c>'s A/B/C/D, the SDD's <c>H[w]</c>). A kit's frequency-dependent
/// transmission line computes its RLGC — skin effect, dielectric loss — in ordinary cell variables
/// and passes them down to one of those models, so the value has to survive the trip as an
/// expression to be evaluated at the far end.</para>
///
/// <para><b>The rule.</b> A value is deferred ONLY if it is transitively frequency-dependent, and it
/// MUST terminate at a model that binds <c>freq</c>. Everything else still resolves to a number
/// exactly as before — deferring indiscriminately would turn every parameter into a growing
/// expression tree and put that cost in the HB inner loop, which evaluates every device once per
/// harmonic sample per Newton iteration.</para>
///
/// <para><b>Inlining is AST-level, never textual.</b> Splicing expression text gets precedence
/// wrong the first time a substituted body is anything but a single term — the whole reason this
/// codebase parses rather than substitutes.</para>
/// </summary>
public sealed class FreqDeferral
{
    /// <summary>The one reserved name. Bound by the models that evaluate per frequency, never by a scope.</summary>
    public const string FreqName = "freq";

    /// <summary>Guards a self-referential binding that no cycle check upstream would have caught.</summary>
    private const int MaxInlineDepth = 64;

    // Keyed "{owningScope}::{name}" — the same identity Evaluator.Resolve memoises on, so a name
    // that means two different things in two scopes is never conflated.
    private readonly Dictionary<string, bool> _isFreqDependent = new(StringComparer.Ordinal);

    /// <summary>
    /// Does this expression depend on <c>freq</c>, directly or through any name it references?
    /// A name that does not resolve is NOT treated as frequency-dependent — it is somebody else's
    /// error to report, with a better message than this class could give.
    /// </summary>
    public bool IsFreqDependent(string expression, Scope scope)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        // Cheap reject: nothing can be frequency-dependent without SOME name to carry it.
        if (!expression.Contains(FreqName, StringComparison.Ordinal) && !HasAnyLetter(expression))
            return false;

        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch { return false; }   // unparseable is not this class's error to raise

        return IsFreqDependent(ast, scope, []);
    }

    private bool IsFreqDependent(Expr ast, Scope scope, HashSet<string> visiting)
    {
        foreach (var name in AstWalker.CollectRefs(ast))
        {
            if (name == FreqName) return true;

            if (scope.Lookup(name) is not { } found) continue;
            var (expression, _, owner) = found;

            string key = $"{owner.DebugName}::{name}";
            if (_isFreqDependent.TryGetValue(key, out bool cached))
            {
                if (cached) return true;
                continue;
            }

            // A cycle here is real but is not this class's to report — Evaluator.Resolve names the
            // chain properly. Treat it as not-freq-dependent and let that path raise.
            if (!visiting.Add(key)) continue;
            try
            {
                Expr sub;
                try { sub = Parser.Parse(expression); }
                catch { continue; }

                bool dep = IsFreqDependent(sub, owner, visiting);
                _isFreqDependent[key] = dep;
                if (dep) return true;
            }
            finally { visiting.Remove(key); }
        }

        return false;
    }

    /// <summary>
    /// For a value crossing a CELL BOUNDARY. Returns one self-contained expression in <c>freq</c>,
    /// with every other name folded to a literal — the expression is about to be bound into the
    /// child's scope, where the parent's names are not visible, so nothing else may be left as a
    /// reference. Returns the text unchanged when nothing is frequency-dependent, so every existing
    /// design takes byte-for-byte the path it always did.
    /// </summary>
    public string InlineForCellBoundary(string expression, Scope scope, Evaluator evaluator)
        => IsFreqDependent(expression, scope)
            ? Render(Inline(Parser.Parse(expression), scope, evaluator, [], 0, foldFreeNames: true))
            : expression;

    /// <summary>
    /// For a value being handed to a model that binds <c>freq</c> itself (<c>Chain</c>'s A/B/C/D,
    /// <c>Z_Port</c>'s <c>Z[i,j]</c>). Only the names that are THEMSELVES frequency-dependent are
    /// inlined; a plain scope variable stays a reference, to be injected by name exactly as it
    /// always was.
    ///
    /// <para>This is deliberately narrower than the cell-boundary rule. Such an expression mentions
    /// <c>freq</c> by definition — that is what the parameter IS — so the broader test would fire on
    /// every one of them and needlessly rewrite expressions that were never a problem.</para>
    /// </summary>
    public string InlineForDevice(string expression, Scope scope, Evaluator evaluator)
        => ReferencesAFreqDependentName(expression, scope)
            ? Render(Inline(Parser.Parse(expression), scope, evaluator, [], 0, foldFreeNames: false))
            : expression;

    /// <summary>
    /// Does this expression reference a NAME that is frequency-dependent? Mentioning <c>freq</c>
    /// directly does not count — that alone is the ordinary form of a per-frequency parameter and
    /// needs no rewriting.
    /// </summary>
    private bool ReferencesAFreqDependentName(string expression, Scope scope)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch { return false; }

        foreach (var name in AstWalker.CollectRefs(ast))
        {
            if (name == FreqName) continue;
            if (scope.Lookup(name) is not { } found) continue;
            if (IsFreqDependent(found.Expression, found.Owner)) return true;
        }
        return false;
    }

    /// <summary>
    /// Rewrites the AST so a frequency-dependent name is replaced by its own (recursively inlined)
    /// definition. Whether an ordinary name is also folded to a literal is the caller's choice —
    /// see the two entry points above.
    ///
    /// <para>Recursion always folds, whatever the caller asked for: once we are inside a binding, its
    /// names resolve in the binding's OWN scope, which need not be the one the result will be
    /// evaluated in. Leaving one as a reference could silently pick up a different binding of the
    /// same name.</para>
    /// </summary>
    private Expr Inline(Expr ast, Scope scope, Evaluator evaluator, HashSet<string> visiting,
                        int depth, bool foldFreeNames)
    {
        if (depth > MaxInlineDepth)
            throw new FrequencyDependentValueException(
                $"A frequency-dependent expression in scope '{scope.DebugName}' nests more than " +
                $"{MaxInlineDepth} levels deep; this is almost certainly a definition that refers to itself.");

        switch (ast)
        {
            case RefExpr r:
                return InlineRef(r, scope, evaluator, visiting, depth, foldFreeNames);

            case UnaryExpr u:
                return u with { Operand = Inline(u.Operand, scope, evaluator, visiting, depth, foldFreeNames) };

            case BinaryExpr b:
                return b with { Left  = Inline(b.Left,  scope, evaluator, visiting, depth, foldFreeNames),
                                Right = Inline(b.Right, scope, evaluator, visiting, depth, foldFreeNames) };

            case CompareExpr c:
                return c with { Left  = Inline(c.Left,  scope, evaluator, visiting, depth, foldFreeNames),
                                Right = Inline(c.Right, scope, evaluator, visiting, depth, foldFreeNames) };

            case LogicExpr lg:
                return lg with { Left  = Inline(lg.Left,  scope, evaluator, visiting, depth, foldFreeNames),
                                 Right = Inline(lg.Right, scope, evaluator, visiting, depth, foldFreeNames) };

            case ConditionalExpr cd:
                return cd with { Condition = Inline(cd.Condition, scope, evaluator, visiting, depth, foldFreeNames),
                                 Then      = Inline(cd.Then,      scope, evaluator, visiting, depth, foldFreeNames),
                                 Else      = Inline(cd.Else,      scope, evaluator, visiting, depth, foldFreeNames) };

            case CallExpr cl:
                return cl with { Args = [.. cl.Args.Select(a => Inline(a, scope, evaluator, visiting, depth, foldFreeNames))] };

            case IndexExpr:
                // Cube indexing belongs to measurements, which run after a result exists — it can
                // never appear inside a value the elaborator is placing into a device.
                throw new FrequencyDependentValueException(
                    $"A frequency-dependent expression in scope '{scope.DebugName}' indexes a result " +
                    "cube. Cube data is not available while the netlist is being built.");

            default:
                return ast;   // NumberExpr, ConstExpr, StringLiteralExpr — already self-contained
        }
    }

    private Expr InlineRef(RefExpr r, Scope scope, Evaluator evaluator, HashSet<string> visiting,
                           int depth, bool foldFreeNames)
    {
        if (r.Name == FreqName) return r;                    // the one name left standing

        if (scope.Lookup(r.Name) is not { } found)
            return r;                                        // unresolved: let the normal path name it

        var (expression, unit, owner) = found;
        string key = $"{owner.DebugName}::{r.Name}";

        if (!IsFreqDependent(expression, owner))
        {
            // Freq-independent. Folding keeps the deferred expression small and the HB inner loop
            // unaffected — but only the cell-boundary caller needs it; a device-level caller injects
            // such a name by value itself, and leaving the reference alone keeps that path identical
            // to what it was before deferral existed.
            return foldFreeNames ? Literal(evaluator.Resolve(r.Name, scope), r) : r;
        }

        if (!visiting.Add(key))
            throw new CycleException($"{string.Join(" → ", visiting)} → {r.Name}");

        try
        {
            // Always folds from here down — see the note on Inline.
            var inlined = Inline(Parser.Parse(expression), owner, evaluator, visiting, depth + 1,
                                 foldFreeNames: true);
            return ApplyUnitScale(inlined, unit);
        }
        finally { visiting.Remove(key); }
    }

    /// <summary>
    /// Applies a binding's own unit the way <see cref="Evaluator"/> would have when resolving it to
    /// a number — a deferred <c>LLINE=1 pH</c> must still mean picohenries.
    /// </summary>
    private static Expr ApplyUnitScale(Expr e, string? unit)
    {
        if (string.IsNullOrEmpty(unit)) return e;
        double scale = Units.Scale(unit) ?? 1.0;
        return scale == 1.0 ? e : new BinaryExpr("*", e, new NumberExpr(scale));
    }

    /// <summary>Turns an already-resolved value back into a literal node.</summary>
    private static Expr Literal(Value v, RefExpr original) => v.Kind switch
    {
        ValueKind.Real    => new NumberExpr(v.AsReal()),
        ValueKind.Complex => new CallExpr("complex",
                                 [new NumberExpr(v.AsComplex().Real), new NumberExpr(v.AsComplex().Imaginary)]),
        // No boolean literal exists in the grammar, so it is spelled as a comparison that is
        // trivially true or false. Only ever produced for a value that already resolved to Bool.
        ValueKind.Bool    => new CompareExpr("==", new NumberExpr(1), new NumberExpr(v.AsBool() ? 1 : 0)),
        ValueKind.String  => new StringLiteralExpr(v.AsString()),
        _ => throw new FrequencyDependentValueException(
                 $"'{original.Name}' resolved to {v.Kind}, which cannot appear inside a " +
                 "frequency-dependent expression."),
    };

    /// <summary>
    /// Renders an AST back to expression text. FULLY PARENTHESISED on purpose: the text is re-parsed
    /// by the device that evaluates it, and precedence that survives the round trip by luck is a bug
    /// waiting for the first expression complicated enough to expose it.
    /// </summary>
    public static string Render(Expr e)
    {
        var sb = new StringBuilder();
        Render(e, sb);
        return sb.ToString();
    }

    private static void Render(Expr e, StringBuilder sb)
    {
        switch (e)
        {
            case NumberExpr n:
                sb.Append(n.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case ConstExpr c:
                sb.Append(c.Name);
                break;
            case RefExpr r:
                sb.Append(r.Name);
                break;
            case StringLiteralExpr s:
                sb.Append('"').Append(s.Value).Append('"');
                break;
            case UnaryExpr u:
                sb.Append('(').Append(u.Op);
                Render(u.Operand, sb);
                sb.Append(')');
                break;
            case BinaryExpr b:
                Infix(sb, b.Left, b.Op, b.Right);
                break;
            case CompareExpr cp:
                Infix(sb, cp.Left, cp.Op, cp.Right);
                break;
            case LogicExpr lg:
                Infix(sb, lg.Left, lg.Op, lg.Right);
                break;
            case ConditionalExpr cd:
                sb.Append("if(");
                Render(cd.Condition, sb); sb.Append(", ");
                Render(cd.Then, sb);      sb.Append(", ");
                Render(cd.Else, sb);      sb.Append(')');
                break;
            case CallExpr cl:
                sb.Append(cl.Name).Append('(');
                for (int i = 0; i < cl.Args.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Render(cl.Args[i], sb);
                }
                sb.Append(')');
                break;
            default:
                throw new FrequencyDependentValueException(
                    $"Cannot render {e.GetType().Name} back to expression text.");
        }
    }

    private static void Infix(StringBuilder sb, Expr l, string op, Expr r)
    {
        sb.Append('(');
        Render(l, sb);
        sb.Append(' ').Append(op).Append(' ');
        Render(r, sb);
        sb.Append(')');
    }

    private static bool HasAnyLetter(string s)
    {
        foreach (char c in s) if (char.IsLetter(c) || c == '_') return true;
        return false;
    }
}
