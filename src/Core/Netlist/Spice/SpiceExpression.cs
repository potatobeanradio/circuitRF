using System.Text;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// One distribution call a rewritten expression held — reported whichever form it was written in
/// (<see cref="SpiceDistributions"/>), because a kit's statistical content is something a run report has to be
/// able to say is there.
/// </summary>
/// <param name="Function">The distribution function.</param>
/// <param name="Nominal">Its nominal argument — what every ordinary run evaluates it to.</param>
public sealed record SpiceStatisticalUse(string Function, string Nominal);

/// <summary>
/// What a translated value does with a distribution call (<c>agauss</c>, <c>gauss</c>, <c>aunif</c>, <c>unif</c>,
/// two-argument <c>limit</c>) — the one decision docs/design/spice-models.md §8.4 records per path.
/// </summary>
public enum SpiceDistributions
{
    /// <summary>Kept as circuitRF's own call, which evaluates to its nominal outside a Monte Carlo trial and draws
    /// inside one (yield overview D6). For text only circuitRF regenerates — the extraction's <c>netlist.cnl</c>.</summary>
    Live,

    /// <summary>Reduced to the nominal argument. For text a USER keeps (an imported cell, a kit part's seeded
    /// defaults), which must still simulate in a circuitRF that predates the distribution functions.</summary>
    Nominal,
}

/// <summary>
/// The one place an expression written in the SPICE dialect is translated into circuitRF's own
/// grammar. Every rewrite here is a SPELLING change with one deliberate exception, called out below,
/// so a caller never has to know which dialect a value came from.
/// </summary>
public static class SpiceExpression
{
    /// <summary>
    /// Statistical distribution functions. Their first argument is the nominal value; the rest
    /// describe a spread.
    ///
    /// <para><c>limit</c> is in this set for its TWO-argument reading only — see
    /// <see cref="ReplaceStatistical"/>, which separates the two meanings by arity.</para>
    /// </summary>
    private static readonly HashSet<string> Statistical =
        new(StringComparer.OrdinalIgnoreCase) { "agauss", "gauss", "aunif", "unif", "limit" };

    /// <summary>The simulator's own temperature variable, which circuitRF spells lower-case.</summary>
    public const string TemperatureIdentifier = "temp";

    /// <summary>The transient time variable. Readable in a CONDITION only — see <see cref="RewriteTimeConditions"/>.</summary>
    public const string TimeIdentifier = "time";

    /// <summary>
    /// Every function circuitRF's expression engine implements, in circuitRF's own spelling.
    ///
    /// <para><b>This exists because the two dialects disagree about CASE, and the disagreement is
    /// silent.</b> A netlist written in this dialect is case-insensitive and routinely writes
    /// <c>IF(…)</c>, <c>MAX(…)</c>, <c>LIMIT(…)</c>; circuitRF's parser matches <c>if</c> as a
    /// keyword and its evaluators match a function name ordinally. So an uppercase call parses
    /// cleanly as a call to an unknown function, and fails at SIMULATE time with "unknown function",
    /// long after the file was read and in a place that says nothing about the netlist.</para>
    ///
    /// <para>Only a name in this set is re-spelled, and only where it is immediately followed by an
    /// opening bracket. A parameter or a net called <c>MAX</c> is not a function call and is left
    /// exactly as the file wrote it.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> KnownFunctions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "if",
            "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "sinh", "cosh", "tanh",
            "exp", "log", "ln", "log10", "sqrt", "pow",
            "floor", "ceil", "round", "int", "abs", "min", "max", "sign",
        };

    /// <summary>
    /// The functions a DEVICE EQUATION may call — a strict subset of <see cref="KnownFunctions"/>.
    ///
    /// <para><b>Two evaluators, two function sets, and the difference is not an oversight in either
    /// of them.</b> A parameter expression is evaluated once by <c>Evaluator</c>, which can afford
    /// the rounding family. A device equation is evaluated by <c>SddEvaluator</c> and its two
    /// compiled counterparts, which carry a DERIVATIVE alongside every value — and
    /// <c>floor</c>/<c>ceil</c>/<c>round</c>/<c>int</c> have a derivative of zero almost everywhere
    /// and none at all at each step, so they are absent there by design.</para>
    ///
    /// <para>It has to be asked separately at import, because the two are indistinguishable in the
    /// text: <c>INT(x)</c> in a resistor's value is fine and <c>INT(x)</c> in a behavioural source
    /// is an "unknown function" thrown from inside the solver, long after the file was read.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> DeviceEquationFunctions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "if",
            "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "sinh", "cosh", "tanh",
            "exp", "log", "ln", "log10", "sqrt", "pow",
            "abs", "min", "max", "sign",
        };

    /// <summary>
    /// Rewrites one value. <paramref name="statistics"/> collects every distribution call met.
    ///
    /// <para><b>A distribution is the one construct whose translation depends on where the text is going</b>
    /// (<paramref name="distributions"/>). Kept live, it is circuitRF's own call with the dialect's meaning: its
    /// nominal in every ordinary run, a draw in a Monte Carlo trial. Reduced, it is its nominal argument — an
    /// ordinary run all the same, and what an older circuitRF can read. Either way each call is REPORTED, because a
    /// kit's statistical content is something a run report and <c>explain</c> have to be able to say is there.</para>
    /// </summary>
    public static string Rewrite(string value, ICollection<SpiceStatisticalUse>? statistics = null,
                                 ICollection<string>? notes = null,
                                 SpiceDistributions distributions = SpiceDistributions.Live)
    {
        var found = new List<SpiceStatisticalUse>();
        string live = Translate(value, found, notes, distributions);
        foreach (var use in found) statistics?.Add(use);

        // A distribution whose SPREAD does not read — a kit's own typo, `gauss(x, 0.0235  n)` with a comma
        // missing — reads at its nominal and says so. Reduced, it parsed and ran before distributions were kept
        // live, and every nominal result must stay what it was; kept live it would fail at Simulate, in a
        // generated file, for a mistake in a library the user cannot edit.
        if (distributions == SpiceDistributions.Live && found.Count > 0 && !Parses(live))
        {
            string nominal = Translate(value, null, null, SpiceDistributions.Nominal);
            if (Parses(nominal))
            {
                notes?.Add($"'{found[0].Function}(…)' has a spread that does not read, so it is taken at its " +
                           "nominal value and does not vary in a Monte Carlo trial.");
                return nominal;
            }
        }
        return live;
    }

    private static bool Parses(string expr)
    {
        try { Parser.Parse(expr); return true; }
        catch (Exception) { return false; }
    }

    private static string Translate(string value, ICollection<SpiceStatisticalUse>? statistics,
                                    ICollection<string>? notes, SpiceDistributions distributions)
    {
        string s = Unwrap(value.Trim());
        s = ReplaceStatistical(s, statistics, distributions);
        s = RewritePowerOperator(s);
        s = RewriteLogicalOperators(s);
        s = RewriteTernary(s);
        s = StripGroupingBraces(s);
        s = RewriteIdentifiers(s);
        // Literals are normalised BEFORE the time rule, because that rule parses the expression and
        // circuitRF's parser cannot read this dialect's engineering suffixes — `time < 1n` would
        // fail to parse, be left exactly as written, and then be refused for naming `time` outside a
        // comparison, which is not what the file says.
        s = SpiceNumber.NormaliseLiterals(s);
        s = RewriteTimeConditions(s, notes);
        s = RewriteTruthValues(s);
        return StripWhitespace(s);
    }

    /// <summary>
    /// <paramref name="text"/> with every distribution call reduced to its nominal argument — the form every value
    /// took before distributions were kept live, and still the form <see cref="SpiceDistributions.Nominal"/> writes.
    /// Applies to any text holding circuitRF expressions, a whole <c>.cnl</c> included: a distribution is a call by
    /// name, and nothing else in the grammar is spelled that way.
    /// </summary>
    public static string ReduceDistributions(string text)
        => ReplaceStatistical(text, null, SpiceDistributions.Nominal);

    /// <summary>
    /// Whether a rewritten expression still names the transient time variable.
    ///
    /// <para>Asked AFTER <see cref="Rewrite"/>, because a <c>time</c> inside a condition has already
    /// been read as steady state and is gone. What is left is a genuine transient stimulus — a ramp,
    /// a <c>time*k</c> — for which there is no steady-state reading at all, so the caller refuses the
    /// line rather than inventing one.</para>
    /// </summary>
    public static bool ReferencesTime(string expr) => MentionsIdentifier(expr, TimeIdentifier);

    /// <summary>
    /// Removes every space outside a quoted string.
    ///
    /// <para><b>This is a hard requirement of what the value is for, not tidying.</b> circuitRF's
    /// own generic instance-line parser splits on whitespace and reads bare words as nets, so an
    /// unquoted value containing a space becomes a value plus phantom nets — which shifts every
    /// later node index and still runs. Whitespace carries no meaning in this dialect's expressions,
    /// so removing it changes nothing; leaving it in changes the circuit.</para>
    /// </summary>
    public static string StripWhitespace(string expr)
    {
        if (!expr.Any(char.IsWhiteSpace)) return expr;

        var sb = new StringBuilder(expr.Length);
        bool inQuotes = false;
        foreach (char c in expr)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (inQuotes || !char.IsWhiteSpace(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strips the delimiters this dialect wraps an expression in — <c>'…'</c> or <c>{…}</c> —
    /// repeatedly, since both spellings appear nested in real files. Only a matched pair spanning
    /// the WHOLE value is stripped: a brace in the middle belongs to the expression.
    /// </summary>
    public static string Unwrap(string value)
    {
        string s = value;
        while (s.Length >= 2 &&
               ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '{' && s[^1] == '}')) &&
               IsMatchedPair(s))
            s = s[1..^1].Trim();
        return s;
    }

    private static bool IsMatchedPair(string s)
    {
        if (s[0] == '\'')
        {
            // A quote pair is matched when no other quote lies between them.
            for (int i = 1; i < s.Length - 1; i++) if (s[i] == '\'') return false;
            return true;
        }

        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i == s.Length - 1;
        }
        return false;
    }

    // ── ternary ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>cond ? a : b</c> becomes <c>if(cond, a, b)</c>.
    ///
    /// <para>Split on the FIRST top-level <c>?</c> and then on the <c>:</c> that matches it, counting
    /// intervening <c>?</c>s. Taking the first <c>:</c> instead would mis-associate <c>a ? b ? c : d
    /// : e</c> and produce an expression that parses cleanly and computes something else.</para>
    /// </summary>
    public static string RewriteTernary(string expr)
    {
        int question = IndexAtTopLevel(expr, '?', 0);
        if (question < 0) return expr;

        int pending = 0;
        int colon   = -1;
        for (int i = question + 1; i < expr.Length; i++)
        {
            if (!IsTopLevel(expr, i)) continue;
            if (expr[i] == '?') pending++;
            else if (expr[i] == ':')
            {
                if (pending == 0) { colon = i; break; }
                pending--;
            }
        }
        if (colon < 0) return expr;      // not a conditional after all; left exactly as written

        string cond = expr[..question].Trim();
        string then = expr[(question + 1)..colon].Trim();
        string els  = expr[(colon + 1)..].Trim();

        return $"if({RewriteTernary(cond)}, {RewriteTernary(then)}, {RewriteTernary(els)})";
    }

    // ── power ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>**</c> becomes <c>^</c>. Both are right-associative and bind tighter than the arithmetic
    /// operators, so this is a spelling change and not a change of meaning. Quoted text is skipped —
    /// the same values carry file paths, where a <c>**</c> is data.
    /// </summary>
    public static string RewritePowerOperator(string expr)
    {
        var sb = new StringBuilder(expr.Length);
        bool inQuotes = false;

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '"') inQuotes = !inQuotes;

            if (!inQuotes && c == '*' && i + 1 < expr.Length && expr[i + 1] == '*')
            {
                sb.Append('^');
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── logical operators ─────────────────────────────────────────────────────

    /// <summary>
    /// Single <c>&amp;</c> and <c>|</c> become <c>&amp;&amp;</c> and <c>||</c>.
    ///
    /// <para><b>A spelling change, and unambiguous because circuitRF has no bitwise operators at
    /// all.</b> This dialect writes the logical connectives with one character; circuitRF writes them
    /// with two, and there is nothing else either character could mean in its grammar — so nothing is
    /// being guessed at here.</para>
    ///
    /// <para>Worth stating what it costs to leave alone: the parser stops at the character with
    /// "Expected '&amp;&amp;'", the value is unreadable, the element is refused, and one refused
    /// element refuses the whole subcircuit. Measured on a real gate-driver library, that one
    /// character accounted for <b>28 of its 34 subcircuits</b> — every logic block in it is written
    /// this way.</para>
    /// </summary>
    public static string RewriteLogicalOperators(string expr)
    {
        if (!expr.Contains('&') && !expr.Contains('|')) return expr;

        var sb = new StringBuilder(expr.Length + 8);
        char quote = '\0';

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }

            if (c is '&' or '|')
            {
                sb.Append(c).Append(c);
                if (i + 1 < expr.Length && expr[i + 1] == c) i++;   // already doubled
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── statistical distributions ─────────────────────────────────────────────

    private static string ReplaceStatistical(
        string expr, ICollection<SpiceStatisticalUse>? statistics, SpiceDistributions distributions)
    {
        string s = expr;
        int from = 0;

        // Each pass rewrites one call, and there cannot be more calls than characters — so this bounds the loop
        // without capping how many calls a long text (a whole .cnl) may hold.
        for (int guard = 0; guard <= expr.Length; guard++)
        {
            int open = -1, nameStart = -1;
            string name = "";

            for (int i = from; i < s.Length && open < 0; i++)
            {
                if (s[i] != '(') continue;

                int j = i;
                while (j > 0 && (char.IsAsciiLetterOrDigit(s[j - 1]) || s[j - 1] == '_')) j--;
                if (j == i) continue;

                string candidate = s[j..i];
                if (!Statistical.Contains(candidate)) continue;

                open = i; nameStart = j; name = candidate;
            }

            if (open < 0) return s;

            int close = MatchingParen(s, open);
            if (close < 0) return s;          // unbalanced; left exactly as written

            var args = SplitTopLevelArguments(s[(open + 1)..close]);

            // ── the two readings of `limit`, separated by ARITY ───────────────
            //
            // One dialect writes `limit(nominal, spread)` and means a distribution. Another writes
            // `LIMIT(x, lo, hi)` and means an ordinary clamp — a guard on a normalised knob, on a
            // temperature before it reaches a power law, or wrapped round a function body against
            // overflow. Reading the clamp as a distribution reduces it to its FIRST argument, which
            // deletes the guard: the expression still parses, still evaluates, and no longer bounds
            // anything.
            //
            // Nothing but the argument count distinguishes them, and the argument count settles it
            // completely: a distribution states a spread, a clamp states two limits.
            if (name.Equals("limit", StringComparison.OrdinalIgnoreCase) && args.Count == 3)
            {
                string x  = args[0].Trim();
                string lo = args[1].Trim();
                string hi = args[2].Trim();
                s    = s[..nameStart] + $"min(max({x},{lo}),{hi})" + s[(close + 1)..];
                from = nameStart + "min(max(".Length;      // a distribution inside the clamp is next
                continue;
            }

            string nominal = args.FirstOrDefault()?.Trim() ?? "0";
            statistics?.Add(new SpiceStatisticalUse(name, nominal));

            if (distributions == SpiceDistributions.Live)
            {
                // circuitRF's spelling of the same call: the name lower-case, the arguments as written. The
                // scan resumes INSIDE it, so a distribution nested in an argument is found and kept too.
                string lower = name.ToLowerInvariant();
                s    = s[..nameStart] + lower + "(" + string.Join(",", args.Select(a => a.Trim())) + ")" + s[(close + 1)..];
                from = nameStart + lower.Length + 1;
                continue;
            }

            // Bracketed only when the nominal is compound. A single literal or name needs no
            // grouping, and wrapping one turns a model card's plain value into `(0.4)` — arithmetic
            // that is right and a value nobody would recognise as what the card said.
            s    = s[..nameStart] + (IsAtomic(nominal) ? nominal : "(" + nominal + ")") + s[(close + 1)..];
            from = nameStart;                                 // the nominal may itself hold a distribution
        }

        return s;
    }

    /// <summary>A single number or name — something that binds tighter than any operator on its own.</summary>
    private static bool IsAtomic(string s)
        => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

    // ── grouping braces, identifiers, and the time variable ───────────────────

    /// <summary>
    /// Turns a brace that survived <see cref="Unwrap"/> into an ordinary bracket.
    ///
    /// <para>An expression in this dialect writes an interpolated parameter in braces INSIDE a
    /// larger expression — <c>(TX1*((T+t0)/300)**{ETX1})</c>. <see cref="Unwrap"/> only strips a
    /// matched pair spanning the whole value, so the inner pair reaches circuitRF's parser, which
    /// has no brace in its grammar and stops at the character. The braces group and nothing else, so
    /// a bracket says exactly what they said.</para>
    /// </summary>
    public static string StripGroupingBraces(string expr)
    {
        if (!expr.Contains('{') && !expr.Contains('}')) return expr;

        var sb = new StringBuilder(expr.Length);
        char quote = '\0';
        foreach (char c in expr)
        {
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            sb.Append(c switch { '{' => '(', '}' => ')', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Spells the dialect's own identifiers the way circuitRF spells them.
    ///
    /// <para><c>sgn</c> is circuitRF's <c>sign</c>. <c>TEMP</c> is the simulator's temperature
    /// variable, which circuitRF holds as the ambient under the lower-case name the elaborator
    /// already reserves for it — so a file that drives its own thermal node from the global ambient
    /// resolves instead of failing on an unknown name.</para>
    /// </summary>
    public static string RewriteIdentifiers(string expr)
    {
        string s = expr;
        foreach (var (from, to) in Aliases) s = ReplaceIdentifier(s, from, to);
        return CanonicaliseFunctionNames(s);
    }

    /// <summary>
    /// Names this dialect spells differently from circuitRF, and nothing else.
    ///
    /// <para>Every one is a pure SPELLING change onto a function circuitRF already implements with
    /// the same meaning and the same arity — a name, not a definition. Anything whose semantics
    /// differ even slightly belongs nowhere near this table: an alias that is not quite the same
    /// function is a wrong answer that evaluates.</para>
    ///
    /// <para><c>temp</c> maps to itself, which is not a no-op — <see cref="ReplaceIdentifier"/>
    /// matches case-insensitively and emits the spelling given here, so this is what turns the
    /// dialect's <c>TEMP</c> into the lower-case name circuitRF's elaborator reserves for the
    /// ambient.</para>
    ///
    /// <para><c>temper</c> is the dialect's other spelling of the same variable — the circuit
    /// temperature in °C, as a kit's model cards write it inside a temperature law — so it lands on
    /// the same name. Left alone it is an unbound name, and the whole parameter reaches a compiled
    /// model as text.</para>
    /// </summary>
    private static readonly (string From, string To)[] Aliases =
    [
        ("sgn",    "sign"),
        ("arctan", "atan"),
        ("arcsin", "asin"),
        ("arccos", "acos"),
        (TemperatureIdentifier, TemperatureIdentifier),
        ("temper", TemperatureIdentifier),
    ];

    /// <summary>
    /// Spells a call to one of circuitRF's own functions the way circuitRF spells it.
    /// See <see cref="KnownFunctions"/> for why this is needed and why it is limited to that set.
    /// </summary>
    public static string CanonicaliseFunctionNames(string expr)
    {
        var sb = new StringBuilder(expr.Length);
        char quote = '\0';

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }

            if (!IsIdentifierStart(c)) { sb.Append(c); continue; }

            int j = i;
            while (j < expr.Length && IsIdentifierPart(expr[j])) j++;
            string word = expr[i..j];

            // A NAME is only a function call when a bracket follows it.
            int k = j;
            while (k < expr.Length && char.IsWhiteSpace(expr[k])) k++;
            bool isCall = k < expr.Length && expr[k] == '(';

            string lower = word.ToLowerInvariant();
            sb.Append(isCall && KnownFunctions.Contains(lower) ? lower : word);
            i = j - 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads a COMPARISON against <c>time</c> at its steady-state value.
    ///
    /// <para><b>This is an interpretation, not a spelling change, which is why every one is
    /// noted.</b> circuitRF has no transient analysis; DC, S-parameter and harmonic-balance runs are
    /// all steady state, where any elapsed time has passed. These constructs exist to suppress a
    /// start-up transient or to gate a differentiator, and the steady state is what the circuit
    /// settles to.</para>
    ///
    /// <para><b>The comparison is what is read, not the conditional around it, and that distinction
    /// is the whole correctness of this.</b> <c>time &gt; 0</c> is TRUE in steady state and
    /// <c>time &lt; 1n</c> is FALSE — so an <c>if</c> whose condition merely MENTIONS time cannot
    /// simply be replaced by its then-branch. A real file writes
    /// <c>if((V(r) &gt; 0.5) | time &lt; 1n, 0, …)</c>, where taking the then-branch means the
    /// device's output is stuck at 0 forever, which converges and is a different circuit. Replacing
    /// only the comparison leaves the rest of the condition intact and lets it decide.</para>
    ///
    /// <para>A <c>time</c> that is not one side of a comparison — a ramp, <c>time*k</c> — has no
    /// steady-state value at all. It is left exactly as written, so
    /// <see cref="ReferencesTime"/> reports it and the caller refuses the line by name.</para>
    /// </summary>
    public static string RewriteTimeConditions(string expr, ICollection<string>? notes = null)
    {
        if (!MentionsIdentifier(expr, TimeIdentifier)) return expr;

        Expr ast;
        try { ast = Parser.Parse(expr); }
        catch { return expr; }        // unreadable for other reasons; the caller's own error wins

        var said = new List<string>();
        Expr rewritten = SteadyState(ast, said);
        if (said.Count == 0) return expr;

        foreach (string n in said) notes?.Add(n);

        try { return SpiceBehaviouralSource.Print(rewritten); }
        catch (NotSupportedException) { return expr; }
    }

    private static Expr SteadyState(Expr e, List<string> notes)
    {
        switch (e)
        {
            case CompareExpr c when MentionsTime(c.Left) != MentionsTime(c.Right):
            {
                bool timeOnLeft = MentionsTime(c.Left);

                // Steady state is "after everything has happened": time is past any finite instant a
                // netlist can name. So a lower bound holds and an upper bound does not, whichever
                // side of the operator time is written on.
                bool? truth = c.Op switch
                {
                    ">" or ">=" => timeOnLeft,
                    "<" or "<=" => !timeOnLeft,
                    "==" => false,
                    "!=" => true,
                    _    => null,
                };
                if (truth is not { } value) return e;

                notes.Add(
                    $"'{Shorten(SafePrint(e))}' compares against the transient time variable. "
                    + "circuitRF has no transient analysis and solves in steady state, where that "
                    + $"comparison is {(value ? "true" : "false")}; the rest of the expression is "
                    + "unchanged.");

                return value
                    ? new CompareExpr(">", new NumberExpr(1), new NumberExpr(0))
                    : new CompareExpr(">", new NumberExpr(0), new NumberExpr(1));
            }

            case UnaryExpr u:
                return new UnaryExpr(u.Op, SteadyState(u.Operand, notes));
            case BinaryExpr b:
                return new BinaryExpr(b.Op, SteadyState(b.Left, notes), SteadyState(b.Right, notes));
            case CompareExpr c:
                return new CompareExpr(c.Op, SteadyState(c.Left, notes), SteadyState(c.Right, notes));
            case LogicExpr l:
                return new LogicExpr(l.Op, SteadyState(l.Left, notes), SteadyState(l.Right, notes));
            case ConditionalExpr cd:
                return new ConditionalExpr(SteadyState(cd.Condition, notes),
                                           SteadyState(cd.Then, notes),
                                           SteadyState(cd.Else, notes));
            case CallExpr call:
            {
                var args = new Expr[call.Args.Length];
                for (int i = 0; i < args.Length; i++) args[i] = SteadyState(call.Args[i], notes);
                return new CallExpr(call.Name, args);
            }
            default:
                return e;
        }
    }

    /// <summary>
    /// A comparison used as a NUMBER becomes <c>if(cmp,1,0)</c>; a number used as a CONDITION becomes
    /// <c>x!=0</c>.
    ///
    /// <para><b>This dialect has one kind of value and circuitRF has two.</b> Here a comparison is 1 or 0
    /// and a condition is any non-zero number, so a kit writes <c>r/w-(flag&gt;0)*rq/w</c> to switch a
    /// term off. circuitRF keeps truth values apart from numbers, in the parameter evaluator and the
    /// device-equation one alike, so that line evaluated to nothing — and what reached the user was a
    /// model card refusing a parameter that still read as the bare NAME it was assigned from, nowhere
    /// near the line that caused it.</para>
    ///
    /// <para>Decided on the AST, by POSITION, because the text cannot say where a comparison stands:
    /// <c>(a&gt;0)*b</c>, <c>b*(a&gt;0)</c>, <c>-(a&gt;0)</c>, <c>max(a&gt;0,b)</c> and <c>(a&gt;0)==(b&gt;0)</c> all use
    /// one as a number. The whole value is a number too — a <c>.param</c> holds one — so a <c>.func</c>
    /// returning a comparison returns 1 or 0, and the converse half is what keeps such a function usable
    /// as a condition. An expression that needs neither is returned exactly as written.</para>
    /// </summary>
    public static string RewriteTruthValues(string expr)
    {
        if (expr.IndexOfAny(TruthCharacters) < 0 && !expr.Contains("if", StringComparison.Ordinal)) return expr;

        try
        {
            Expr ast = Parser.Parse(expr);
            string before = SpiceBehaviouralSource.Print(ast);
            string after  = SpiceBehaviouralSource.Print(AsNumber(ast));
            return after == before ? expr : after;
        }
        catch { return expr; }        // unreadable, or holds a node the printer cannot write; left as written
    }

    private static readonly char[] TruthCharacters = ['<', '>', '=', '!', '&', '|', '?'];

    private static bool IsTruth(Expr e) => e is CompareExpr or LogicExpr or UnaryExpr { Op: "!" };

    private static Expr AsNumber(Expr e) => e switch
    {
        _ when IsTruth(e)  => new ConditionalExpr(AsTruth(e), new NumberExpr(1), new NumberExpr(0)),
        UnaryExpr u        => new UnaryExpr(u.Op, AsNumber(u.Operand)),
        BinaryExpr b       => new BinaryExpr(b.Op, AsNumber(b.Left), AsNumber(b.Right)),
        ConditionalExpr cd => new ConditionalExpr(AsTruth(cd.Condition), AsNumber(cd.Then), AsNumber(cd.Else)),
        CallExpr call      => new CallExpr(call.Name, [.. call.Args.Select(AsNumber)]),
        _                  => e,
    };

    private static Expr AsTruth(Expr e) => e switch
    {
        CompareExpr c => new CompareExpr(c.Op, AsNumber(c.Left), AsNumber(c.Right)),
        LogicExpr l   => new LogicExpr(l.Op, AsTruth(l.Left), AsTruth(l.Right)),
        UnaryExpr { Op: "!" } u => new UnaryExpr("!", AsTruth(u.Operand)),
        _             => new CompareExpr("!=", AsNumber(e), new NumberExpr(0)),
    };

    private static bool MentionsTime(Expr e)
    {
        foreach (string r in AstWalker.CollectRefs(e))
            if (r.Equals(TimeIdentifier, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string SafePrint(Expr e)
    {
        try { return SpiceBehaviouralSource.Print(e); }
        catch (NotSupportedException) { return "…"; }
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..37] + "\u2026";

    /// <summary>
    /// Replaces one whole identifier, case-insensitively, leaving the spelling of everything it is
    /// merely a substring of alone — <c>sgn</c> must not reach into <c>design</c>.
    /// </summary>
    private static string ReplaceIdentifier(string expr, string from, string to)
    {
        var sb = new StringBuilder(expr.Length);
        char quote = '\0';

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }

            if (IsIdentifierStart(c))
            {
                int j = i;
                while (j < expr.Length && IsIdentifierPart(expr[j])) j++;
                string word = expr[i..j];
                sb.Append(word.Equals(from, StringComparison.OrdinalIgnoreCase) ? to : word);
                i = j - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Whether a whole identifier of that name appears outside every quoted run.</summary>
    private static bool MentionsIdentifier(string expr, string name)
    {
        char quote = '\0';
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (!IsIdentifierStart(c)) continue;

            int j = i;
            while (j < expr.Length && IsIdentifierPart(expr[j])) j++;
            if (expr[i..j].Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            i = j - 1;
        }
        return false;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentifierPart(char c)  => char.IsLetterOrDigit(c) || c == '_';

    // ── scanning helpers ──────────────────────────────────────────────────────

    /// <summary>Splits a comma-separated argument list, ignoring commas inside nested brackets.</summary>
    public static List<string> SplitTopLevelArguments(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool inQuotes = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' || c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (c == '(' || c == '{')) depth++;
            else if (!inQuotes && (c == ')' || c == '}')) depth--;
            else if (!inQuotes && depth == 0 && c == ',') { parts.Add(s[start..i]); start = i + 1; }
        }
        if (start <= s.Length) parts.Add(s[start..]);
        return parts;
    }

    private static int MatchingParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static int IndexAtTopLevel(string s, char target, int from)
    {
        for (int i = from; i < s.Length; i++)
            if (s[i] == target && IsTopLevel(s, i)) return i;
        return -1;
    }

    /// <summary>
    /// Whether position <paramref name="index"/> sits outside every bracket and quote. Also rejects
    /// a <c>:</c> or <c>?</c> that is part of a two-character operator, so <c>a &lt;= b ? c : d</c>
    /// is read correctly.
    /// </summary>
    private static bool IsTopLevel(string s, int index)
    {
        int depth = 0;
        bool inQuotes = false;

        for (int i = 0; i < index; i++)
        {
            char c = s[i];
            if (c == '\'' || c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (c == '(' || c == '{')) depth++;
            else if (!inQuotes && (c == ')' || c == '}')) depth--;
        }
        return depth == 0 && !inQuotes;
    }
}
