using System.Globalization;
using System.Text;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>A pair of nets a behavioural source senses, in the order the file wrote them.</summary>
public sealed record SpiceSensePair(string Plus, string Minus)
{
    public override string ToString() => $"({Plus},{Minus})";
}

/// <summary>
/// A behavioural source's transfer expression, rewritten into the equation-defined device's own
/// terms.
/// </summary>
/// <param name="Equation">
/// The expression with every <c>V(…)</c> replaced by a port voltage <c>_v1.._vn</c> and every
/// <c>I(…)</c> by a control current <c>_c1.._cm</c>. Whitespace-free, because a value with a space
/// in it becomes a value plus phantom nets when it is written back out.
/// </param>
/// <param name="Pairs">
/// The device's ports, in order. <b>Port 1 is always the source's OWN pair</b> — the two nets the
/// line binds — whether or not the expression senses it. Ports 2..N are the other node pairs the
/// expression reads, each of which draws no current at all.
/// </param>
/// <param name="ControlSources">
/// The instance names whose branch current the expression reads, parallel to <c>_c1.._cm</c>.
/// </param>
/// <param name="AffineGain">
/// Non-null when the transfer is a plain constant-coefficient multiple of ONE sensed quantity and
/// nothing else — the ideal controlled source this dialect writes positionally. The value is the
/// coefficient's own expression, which may be a parameter rather than a literal.
/// </param>
/// <param name="AffineOf">
/// Which quantity <paramref name="AffineGain"/> multiplies: a port index ≥ 2 into
/// <paramref name="Pairs"/>, or, when <paramref name="AffineIsCurrent"/>, an index into
/// <paramref name="ControlSources"/>. −1 when the transfer is not affine.
/// </param>
/// <param name="Refusal">Why the expression cannot be read at all. Null when it can.</param>
public sealed record SpiceBehaviouralForm(
    string                          Equation,
    IReadOnlyList<SpiceSensePair>   Pairs,
    IReadOnlyList<string>           ControlSources,
    string?                         AffineGain,
    int                             AffineOf,
    bool                            AffineIsCurrent,
    string?                         Refusal)
{
    /// <summary>True when this is an ideal controlled source — one sensed quantity, one coefficient.</summary>
    public bool IsAffine => Refusal is null && AffineGain is not null;
}

/// <summary>
/// Reads a behavioural source's <c>VALUE={…}</c> expression as an equation-defined device's.
///
/// <para><b>Through the AST, never through the text.</b> Deciding what a source senses, and whether
/// its transfer is affine, from the characters would mean a second expression grammar living beside
/// circuitRF's one — and it would get <c>V</c> as a net name, <c>V</c> as a function and <c>V</c>
/// inside a comment confusably right. The expression is parsed by the one parser, walked, and
/// printed back out.</para>
///
/// <para><b>The device's own pair is port 1 whether the expression names it or not.</b> A port is
/// where the device attaches, and the source attaches at the two nets its line binds; an expression
/// that also reads that pair reads <c>_v1</c> rather than opening a second port onto the same
/// nodes.</para>
/// </summary>
public static class SpiceBehaviouralSource
{
    /// <summary>
    /// Reads <paramref name="valueExpression"/> as the transfer of a source connected across
    /// <paramref name="plus"/> and <paramref name="minus"/>.
    /// </summary>
    public static SpiceBehaviouralForm Read(string valueExpression, string plus, string minus)
    {
        Expr ast;
        try
        {
            ast = Parser.Parse(valueExpression);
        }
        catch (Exception ex)
        {
            return Refuse($"its expression could not be read ({ex.Message})");
        }

        var pairs = new List<SpiceSensePair> { new(plus, minus) };
        var controls = new List<string>();
        string? refusal = null;

        Expr rewritten = Rewrite(ast, pairs, controls, ref refusal);
        if (refusal is not null) return Refuse(refusal);

        string text;
        try
        {
            text = Print(rewritten);
        }
        catch (NotSupportedException ex)
        {
            return Refuse(ex.Message);
        }

        var (gain, of, isCurrent) = Affine(rewritten);
        return new SpiceBehaviouralForm(text, pairs, controls, gain, of, isCurrent, null);

        static SpiceBehaviouralForm Refuse(string why)
            => new("", [], [], null, -1, false, why);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  V(…) and I(…) become ports and control currents
    // ─────────────────────────────────────────────────────────────────────────

    private static Expr Rewrite(Expr e, List<SpiceSensePair> pairs, List<string> controls, ref string? refusal)
    {
        switch (e)
        {
            case CallExpr call when IsNodeVoltage(call):
            {
                if (!TryNetName(call.Args[0], out string a) ||
                    (call.Args.Length == 2 && !TryNetName(call.Args[1], out _)))
                {
                    refusal ??= "a node voltage in its expression does not name a net";
                    return e;
                }
                string b = call.Args.Length == 2 && TryNetName(call.Args[1], out string bn) ? bn : GroundNet;

                int index = pairs.FindIndex(p =>
                    p.Plus.Equals(a, StringComparison.OrdinalIgnoreCase) &&
                    p.Minus.Equals(b, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    // The same pair written the other way round is that pair, negated — a second
                    // port onto the same two nodes would be an extra unknown for no extra physics.
                    int flipped = pairs.FindIndex(p =>
                        p.Plus.Equals(b, StringComparison.OrdinalIgnoreCase) &&
                        p.Minus.Equals(a, StringComparison.OrdinalIgnoreCase));
                    if (flipped >= 0)
                        return new UnaryExpr("-", new RefExpr($"_v{flipped + 1}"));

                    pairs.Add(new SpiceSensePair(a, b));
                    index = pairs.Count - 1;
                }
                return new RefExpr($"_v{index + 1}");
            }

            case CallExpr call when IsBranchCurrent(call):
            {
                if (!TryNetName(call.Args[0], out string source))
                {
                    refusal ??= "a branch current in its expression does not name a source";
                    return e;
                }
                int index = controls.FindIndex(c => c.Equals(source, StringComparison.OrdinalIgnoreCase));
                if (index < 0) { controls.Add(source); index = controls.Count - 1; }
                return new RefExpr($"_c{index + 1}");
            }

            case UnaryExpr u:
                return new UnaryExpr(u.Op, Rewrite(u.Operand, pairs, controls, ref refusal));
            case BinaryExpr b:
                return new BinaryExpr(b.Op, Rewrite(b.Left, pairs, controls, ref refusal),
                                            Rewrite(b.Right, pairs, controls, ref refusal));
            case CompareExpr c:
                return new CompareExpr(c.Op, Rewrite(c.Left, pairs, controls, ref refusal),
                                             Rewrite(c.Right, pairs, controls, ref refusal));
            case LogicExpr l:
                return new LogicExpr(l.Op, Rewrite(l.Left, pairs, controls, ref refusal),
                                           Rewrite(l.Right, pairs, controls, ref refusal));
            case ConditionalExpr cd:
                return new ConditionalExpr(Rewrite(cd.Condition, pairs, controls, ref refusal),
                                           Rewrite(cd.Then, pairs, controls, ref refusal),
                                           Rewrite(cd.Else, pairs, controls, ref refusal));
            case CallExpr call:
            {
                var args = new Expr[call.Args.Length];
                for (int i = 0; i < args.Length; i++) args[i] = Rewrite(call.Args[i], pairs, controls, ref refusal);
                return new CallExpr(call.Name, args);
            }
            default:
                return e;
        }
    }

    /// <summary>The net a <c>V(…)</c>/<c>I(…)</c> argument names, in whatever the parser made of it.</summary>
    private static bool TryNetName(Expr e, out string name)
    {
        switch (e)
        {
            case RefExpr r:   name = r.Name; return true;
            // `pi`, `e` and `j` are reserved constants to the parser and perfectly ordinary net
            // names to a netlist, and `V(a,0)` names ground with a number.
            case ConstExpr c: name = c.Name; return true;
            case NumberExpr n when n.Value == Math.Floor(n.Value) && n.Value >= 0:
                name = ((long)n.Value).ToString(CultureInfo.InvariantCulture);
                return true;
            default: name = ""; return false;
        }
    }

    private const string GroundNet = "0";

    private static bool IsNodeVoltage(CallExpr c)
        => c.Name.Equals("V", StringComparison.OrdinalIgnoreCase) && c.Args.Length is 1 or 2;

    private static bool IsBranchCurrent(CallExpr c)
        => c.Name.Equals("I", StringComparison.OrdinalIgnoreCase) && c.Args.Length == 1;

    // ─────────────────────────────────────────────────────────────────────────
    //  affine detection
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the transfer is <c>k · x</c> for one sensed quantity <c>x</c> and a coefficient that
    /// senses nothing — the ideal controlled source, and the only shape circuitRF's own linear
    /// <c>VCVS</c>/<c>VCCS</c> can state.
    ///
    /// <para>Decided on the AST, not on the text: <c>2*V(a,b)</c>, <c>V(a,b)*2</c>,
    /// <c>V(a,b)/0.5</c> and <c>-V(a,b)</c> are the same element, and a source whose gain is a cell
    /// parameter is still ideal.</para>
    /// </summary>
    private static (string? Gain, int Of, bool IsCurrent) Affine(Expr e)
    {
        switch (e)
        {
            case RefExpr r when Sensed(r.Name) is { } s:
                return ("1", s.Index, s.IsCurrent);

            case UnaryExpr { Op: "-" } u when Affine(u.Operand) is { Gain: not null } inner:
                return ($"-({inner.Gain})", inner.Of, inner.IsCurrent);

            case UnaryExpr { Op: "+" } u:
                return Affine(u.Operand);

            case BinaryExpr { Op: "*" } b:
            {
                if (SensesNothing(b.Left)  && Affine(b.Right) is { Gain: not null } r)
                    return ($"({Print(b.Left)})*({r.Gain})", r.Of, r.IsCurrent);
                if (SensesNothing(b.Right) && Affine(b.Left)  is { Gain: not null } l)
                    return ($"({Print(b.Right)})*({l.Gain})", l.Of, l.IsCurrent);
                return (null, -1, false);
            }

            case BinaryExpr { Op: "/" } b when SensesNothing(b.Right) && Affine(b.Left) is { Gain: not null } l:
                return ($"({l.Gain})/({Print(b.Right)})", l.Of, l.IsCurrent);

            default:
                return (null, -1, false);
        }
    }

    /// <summary>The port or control index a rewritten name refers to, or null when it is an ordinary name.</summary>
    private static (int Index, bool IsCurrent)? Sensed(string name)
    {
        if (name.Length < 3 || name[0] != '_') return null;
        if (name[1] is not ('v' or 'c')) return null;
        if (!int.TryParse(name[2..], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
        return (n - 1, name[1] == 'c');
    }

    /// <summary>Whether an expression reads no port voltage and no control current at all.</summary>
    public static bool SensesNothing(Expr e) => !CollectSensed(e).Any();

    private static IEnumerable<string> CollectSensed(Expr e)
    {
        foreach (string r in AstWalker.CollectRefs(e))
            if (Sensed(r) is not null) yield return r;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  printing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints an AST back as circuitRF source text.
    ///
    /// <para><b>Fully bracketed and whitespace-free, on purpose.</b> Re-deriving the minimum
    /// bracketing an expression needs means re-stating every precedence rule the parser already
    /// owns, and getting one wrong produces text that parses cleanly and computes something else.
    /// Whitespace is removed for the reason the reader removes it everywhere: circuitRF's own
    /// instance-line parser splits on it and reads bare words as nets.</para>
    /// </summary>
    public static string Print(Expr e)
    {
        var sb = new StringBuilder();
        Write(e, sb);
        return sb.ToString();
    }

    private static void Write(Expr e, StringBuilder sb)
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
            case UnaryExpr u:
                sb.Append('(').Append(u.Op);
                Write(u.Operand, sb);
                sb.Append(')');
                break;
            case BinaryExpr b:
                sb.Append('(');  Write(b.Left, sb);  sb.Append(b.Op);  Write(b.Right, sb);  sb.Append(')');
                break;
            case CompareExpr c:
                sb.Append('(');  Write(c.Left, sb);  sb.Append(c.Op);  Write(c.Right, sb);  sb.Append(')');
                break;
            case LogicExpr l:
                sb.Append('(');  Write(l.Left, sb);  sb.Append(l.Op);  Write(l.Right, sb);  sb.Append(')');
                break;
            case ConditionalExpr cd:
                sb.Append("if(");
                Write(cd.Condition, sb); sb.Append(',');
                Write(cd.Then, sb);      sb.Append(',');
                Write(cd.Else, sb);      sb.Append(')');
                break;
            case CallExpr call:
                sb.Append(call.Name).Append('(');
                for (int i = 0; i < call.Args.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(call.Args[i], sb);
                }
                sb.Append(')');
                break;
            default:
                throw new NotSupportedException(
                    $"its expression contains a construct circuitRF cannot write back out ({e.GetType().Name})");
        }
    }
}
