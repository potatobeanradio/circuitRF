using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// R7B §3 — parses and validates the text an SDD's <i>Set DUT…</i> editor holds: variables and
/// equations, one <c>name = expression</c> per line, the same grammar and lexical layer
/// <see cref="VarTextParser"/> already gives the VAR dialog's own Text mode (§3.3 — reused, not
/// copied). Framework-free (no Avalonia type appears anywhere in this file), so it is directly
/// testable without a window — see <c>HarmonicaDutEditor</c>'s own remarks on why logic like this
/// never lives in a <c>Window</c> subclass.
///
/// <para><b>Never throws.</b> The dialog re-runs <see cref="Parse"/> on every keystroke; a throw
/// would crash the editor instead of annotating the offending line, so every failure this checks for
/// becomes a <see cref="Problem"/> instead.</para>
/// </summary>
public static class HarmonicaSddText
{
    /// <summary>One thing wrong with the text, anchored to a 1-based source line — 0 for a
    /// whole-document problem with no single line to point at (§3.6.6's "no current equation").</summary>
    public readonly record struct Problem(int Line, string Text, string Message);

    public sealed record ParseResult(
        IReadOnlyList<(string Name, string Expression)> Variables,
        IReadOnlyList<(string Name, string Expression)> Equations,
        IReadOnlyList<Problem> Problems)
    {
        public bool IsValid => Problems.Count == 0;
    }

    // §3.6.5 — the two equation shapes that carry a port index, for the range check.
    private static readonly Regex RxIndexed2 = new(@"^I\[(\d+),(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex RxIndexed1 = new(@"^I\[(\d+)\]$",       RegexOptions.Compiled);
    private static readonly Regex RxCharge1  = new(@"^Q\[(\d+)\]$",       RegexOptions.Compiled);

    private static readonly Regex RxPortVoltage    = new(@"^_v(\d+)$", RegexOptions.Compiled);
    private static readonly Regex RxControlCurrent = new(@"^_c\d+$",   RegexOptions.Compiled);

    /// <summary>Runs every §3.6 check against <paramref name="text"/> for an SDD with
    /// <paramref name="portCount"/> ports.</summary>
    public static ParseResult Parse(string text, int portCount)
    {
        text = Sanitize(text);
        var lines = VarTextParser.ParseLines(text);

        var problems    = new List<Problem>();
        var variables    = new List<(string Name, string Expression)>();
        var equations    = new List<(string Name, string Expression)>();
        var astOf        = new Dictionary<string, Expr>(StringComparer.Ordinal);
        var firstLineOf  = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            int lineNo = i + 1;
            if (line.IsBlank || line.IsComment) continue;

            if (!line.IsValid)
            {
                problems.Add(new Problem(lineNo, line.RawText, line.ErrorMessage ?? "invalid line"));
                continue;
            }

            string name = line.Name!;
            string expr = line.Expression!;

            // §3.6.1 — duplicate name (lexical: VarTextParser already rejects an empty name / a
            // missing '=' above; a repeat needs its own check because it spans lines).
            if (firstLineOf.TryGetValue(name, out int firstLine))
            {
                problems.Add(new Problem(lineNo, line.RawText,
                    $"'{name}' is already declared at line {firstLine}"));
                continue;
            }
            firstLineOf[name] = lineNo;

            // §3.6.2 — syntax.
            Expr ast;
            try { ast = Parser.Parse(expr); }
            catch (ExpressionException ex)
            {
                problems.Add(new Problem(lineNo, line.RawText, ex.Message));
                continue;
            }
            astOf[name] = ast;

            if (ComponentModelFactory.IsSddEquationName(name))
            {
                // §3.6.5 — port range, for the shapes that carry one.
                int? port = IndexedPort(name);
                if (port is { } p && (p < 1 || p > portCount))
                    problems.Add(new Problem(lineNo, line.RawText,
                        $"'{name}' references port {p}, but this SDD has {portCount} port(s)"));

                equations.Add((name, expr));
            }
            else
            {
                // §3.6.7 — reserved names.
                if (RxPortVoltage.IsMatch(name) || RxControlCurrent.IsMatch(name)
                    || string.Equals(name, "freq", StringComparison.Ordinal))
                {
                    problems.Add(new Problem(lineNo, line.RawText,
                        $"'{name}' is reserved (a port voltage, a control current, or the frequency) " +
                        "and cannot be used as a variable name"));
                    continue;
                }

                variables.Add((name, expr));
            }
        }

        // §3.6.3 — every variable must resolve to a constant Real: bind them all into one scope and
        // resolve each, which gets cycle detection and unresolved-name reporting from the evaluator's
        // own resolution stack rather than a second implementation of either.
        var scope     = new Scope("sdd-editor");
        var evaluator = new Evaluator();
        foreach (var (name, expr) in variables) scope.Bind(name, expr);

        foreach (var (name, _) in variables)
        {
            int lineNo = firstLineOf[name];
            try
            {
                var val = evaluator.Resolve(name, scope);
                if (val.Kind == ValueKind.Complex)
                    problems.Add(new Problem(lineNo, name,
                        $"'{name}' resolves to a Complex value; SDD variables are real-only"));
                else if (val.Kind != ValueKind.Real)
                    problems.Add(new Problem(lineNo, name,
                        $"'{name}' does not resolve to a number ({val.Kind})"));
            }
            catch (CycleException ex)
            {
                problems.Add(new Problem(lineNo, name, ex.Message));
            }
            catch (UnresolvedNameException ex)
                when (RxPortVoltage.IsMatch(ex.Name) || RxControlCurrent.IsMatch(ex.Name))
            {
                // A variable is evaluated once at elaboration, not per bias point, so a reference to a
                // port quantity is a distinct, more useful error than a bare "unresolved name".
                problems.Add(new Problem(lineNo, name,
                    $"'{name}' references '{ex.Name}', a port quantity available only inside an " +
                    "equation — a variable is evaluated once, not per bias point"));
            }
            catch (UnresolvedNameException ex)
            {
                problems.Add(new Problem(lineNo, name,
                    $"'{name}' references '{ex.Name}', which is not declared above"));
            }
            catch (ExpressionException ex)
            {
                problems.Add(new Problem(lineNo, name, ex.Message));
            }
        }

        // §3.6.4 — every equation's free names must be a declared variable, a port voltage in range,
        // or a control current.
        var declaredVars = new HashSet<string>(variables.Select(v => v.Name), StringComparer.Ordinal);
        foreach (var (name, _) in equations)
        {
            int lineNo = firstLineOf[name];
            foreach (var refName in AstWalker.CollectRefs(astOf[name]))
            {
                if (declaredVars.Contains(refName)) continue;
                if (RxControlCurrent.IsMatch(refName)) continue;

                var pv = RxPortVoltage.Match(refName);
                if (pv.Success)
                {
                    int port = int.Parse(pv.Groups[1].Value);
                    if (port < 1 || port > portCount)
                        problems.Add(new Problem(lineNo, name,
                            $"'{name}' references '{refName}', but this SDD has {portCount} port(s)"));
                    continue;
                }

                problems.Add(new Problem(lineNo, name,
                    $"line {lineNo}: '{refName}' is not a variable declared above, a port voltage " +
                    $"(_v1…_v{portCount}) or a control current"));
            }
        }

        // §3.6.6 — at least one current equation.
        if (!equations.Any(e => IsCurrentEquation(e.Name)))
            problems.Add(new Problem(0, "",
                "at least one current equation (I[p,0] or I[p]) is required"));

        return new ParseResult(variables, equations, problems);
    }

    private static bool IsCurrentEquation(string name)
    {
        var m2 = RxIndexed2.Match(name);
        if (m2.Success) return m2.Groups[2].Value == "0";
        return RxIndexed1.IsMatch(name);
    }

    private static int? IndexedPort(string name)
    {
        var m2 = RxIndexed2.Match(name);
        if (m2.Success) return int.Parse(m2.Groups[1].Value);
        var m1 = RxIndexed1.Match(name);
        if (m1.Success) return int.Parse(m1.Groups[1].Value);
        var mq = RxCharge1.Match(name);
        if (mq.Success) return int.Parse(mq.Groups[1].Value);
        return null;
    }

    /// <summary>
    /// §3.7 — strip the invisible characters a paste routinely carries (U+200B–U+200F, U+FEFF; U+00A0
    /// becomes an ordinary space), and normalise CRLF, once, before anything else looks at the text.
    /// Left undone, an identifier with one glued on is a DIFFERENT identifier that fails to resolve
    /// with a message naming a symbol that looks correct on screen — the single most likely way this
    /// feature ships broken (found in the owner's own default text, immediately after
    /// <c>Periphery_mm</c> on its first line).
    /// </summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            if (c is >= '\u200B' and <= '\u200F') continue;   // ZWSP, ZWNJ, ZWJ, LRM, RLM
            if (c == '\uFEFF') continue;                       // BOM
            sb.Append(c == '\u00A0' ? ' ' : c);                // NBSP → ordinary space
        }
        return sb.ToString();
    }

    /// <summary>Merges variables and equations back into the flat map <c>DutSpec.Parameters</c>
    /// carries — the engine's own shape, unaffected by anything in this editor.</summary>
    public static IReadOnlyDictionary<string, string> ToParameters(ParseResult result)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, expr) in result.Variables) map[name] = expr;
        foreach (var (name, expr) in result.Equations) map[name] = expr;
        return map;
    }
}
