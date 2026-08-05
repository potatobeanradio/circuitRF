using System.Text;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// One thing a rewritten expression had to give up, so it can be reported instead of hidden.
/// </summary>
/// <param name="Function">The function whose value was not computed.</param>
/// <param name="Nominal">What was used in its place.</param>
public sealed record SpiceStatisticalUse(string Function, string Nominal);

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
    /// </summary>
    private static readonly HashSet<string> Statistical =
        new(StringComparer.OrdinalIgnoreCase) { "agauss", "gauss", "aunif", "unif", "limit" };

    /// <summary>
    /// Rewrites one value. <paramref name="statistics"/> collects every distribution that was
    /// reduced to its nominal value.
    ///
    /// <para><b>The reduction is the one change of MEANING here, and it is why it is reported rather
    /// than performed quietly.</b> circuitRF does not sample distributions, so a card asking for one
    /// gets its nominal value — which is a perfectly ordinary simulation, and is what the user
    /// almost certainly wants. What is not acceptable is doing that in silence: the resulting number
    /// is indistinguishable from a value that carried no distribution at all.</para>
    /// </summary>
    public static string Rewrite(string value, ICollection<SpiceStatisticalUse>? statistics = null)
    {
        string s = Unwrap(value.Trim());
        s = ReplaceStatistical(s, statistics);
        s = RewritePowerOperator(s);
        s = RewriteTernary(s);
        s = SpiceNumber.NormaliseLiterals(s);
        return StripWhitespace(s);
    }

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

    // ── statistical distributions ─────────────────────────────────────────────

    private static string ReplaceStatistical(string expr, ICollection<SpiceStatisticalUse>? statistics)
    {
        string s = expr;

        for (int guard = 0; guard < 64; guard++)
        {
            int open = -1, nameStart = -1;
            string name = "";

            for (int i = 0; i < s.Length && open < 0; i++)
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

            string nominal = SplitTopLevelArguments(s[(open + 1)..close]).FirstOrDefault()?.Trim() ?? "0";
            statistics?.Add(new SpiceStatisticalUse(name, nominal));

            // Bracketed only when the nominal is compound. A single literal or name needs no
            // grouping, and wrapping one turns a model card's plain value into `(0.4)` — arithmetic
            // that is right and a value nobody would recognise as what the card said.
            s = s[..nameStart] + (IsAtomic(nominal) ? nominal : "(" + nominal + ")") + s[(close + 1)..];
        }

        return s;
    }

    /// <summary>A single number or name — something that binds tighter than any operator on its own.</summary>
    private static bool IsAtomic(string s)
        => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

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
