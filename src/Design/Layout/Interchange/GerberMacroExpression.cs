// The aperture-macro modifier evaluator (docs/sonnet-briefs/brief-L4e-gerber-import-reader.md
// R-L4e-8). Deliberately a purpose-built, ~90-line recursive-descent parser rather than a call into
// circuitRF's own expression engine, and the reason is a genuine grammar collision, not convenience:
//
//   * 'x' AND 'X' ARE THE MULTIPLICATION OPERATOR HERE. `$1x1.5` is a product. In the circuit
//     language `x` is an ordinary identifier and that text is either a syntax error or, worse, a
//     reference to a variable someone happens to have named `x`.
//   * There are no named variables at all — operands are the positional macro arguments `$1`, `$2`,
//     ... and `$n` assignments made by an earlier block of the same macro.
//   * There are no functions, no conditionals, and no Real/Complex/Bool kinding: every value is a
//     plain double in the file's own length unit (millimetres or inches), and the only operators the
//     format defines are + - x / and parentheses.
//
// Root CLAUDE.md's "one expression engine, never string substitution" invariant is about the CIRCUIT
// expression language (globals, cell parameters, SDD equations, measurements). Teaching that engine a
// second, conflicting tokenizer so it could also read this foreign grammar would make it worse at its
// real job. This file is not string substitution either — it tokenizes and evaluates properly; it is
// simply a second, small, self-contained grammar for a second, small, foreign language.

using System.Globalization;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>A macro modifier that could not be parsed or evaluated — caught by the reader and turned
/// into a counted, named diagnostic rather than an exception that escapes to the caller.</summary>
internal sealed class GerberMacroExpressionException(string message) : Exception(message);

internal static class GerberMacroExpression
{
    /// <summary>Evaluates one macro modifier expression. <paramref name="vars"/> maps <c>$n</c> to its
    /// current value — the macro's instantiation arguments, plus anything a preceding <c>$n=…</c>
    /// block of the same macro assigned. An undefined <c>$n</c> reads as 0, which is what the format
    /// specifies for an argument the instantiating <c>%ADD</c> did not supply.</summary>
    internal static double Evaluate(string text, IReadOnlyDictionary<int, double> vars)
    {
        int pos = 0;
        double value = ParseExpression(text, ref pos, vars);
        SkipSpace(text, ref pos);
        if (pos != text.Length)
            throw new GerberMacroExpressionException(
                $"Unexpected '{text[pos]}' at position {pos} in macro modifier \"{text}\".");
        return value;
    }

    private static double ParseExpression(string s, ref int pos, IReadOnlyDictionary<int, double> vars)
    {
        double value = ParseTerm(s, ref pos, vars);
        while (true)
        {
            SkipSpace(s, ref pos);
            if (pos >= s.Length) return value;
            char c = s[pos];
            if (c != '+' && c != '-') return value;
            pos++;
            double rhs = ParseTerm(s, ref pos, vars);
            value = c == '+' ? value + rhs : value - rhs;
        }
    }

    private static double ParseTerm(string s, ref int pos, IReadOnlyDictionary<int, double> vars)
    {
        double value = ParseFactor(s, ref pos, vars);
        while (true)
        {
            SkipSpace(s, ref pos);
            if (pos >= s.Length) return value;
            char c = s[pos];
            // 'x' and 'X' are BOTH multiplication (the format's own spelling; 'X' is the older one and
            // is also the separator between whole modifiers, which is why the caller splits on 'X'
            // only at the top level of a %ADD instantiation and never inside a parenthesised group).
            if (c != 'x' && c != 'X' && c != '/') return value;
            pos++;
            double rhs = ParseFactor(s, ref pos, vars);
            if (c == '/')
            {
                if (rhs == 0) throw new GerberMacroExpressionException($"Division by zero in macro modifier \"{s}\".");
                value /= rhs;
            }
            else value *= rhs;
        }
    }

    private static double ParseFactor(string s, ref int pos, IReadOnlyDictionary<int, double> vars)
    {
        SkipSpace(s, ref pos);
        if (pos >= s.Length) throw new GerberMacroExpressionException($"Macro modifier \"{s}\" ends mid-expression.");

        char c = s[pos];
        if (c == '+') { pos++; return ParseFactor(s, ref pos, vars); }
        if (c == '-') { pos++; return -ParseFactor(s, ref pos, vars); }

        if (c == '(')
        {
            pos++;
            double inner = ParseExpression(s, ref pos, vars);
            SkipSpace(s, ref pos);
            if (pos >= s.Length || s[pos] != ')')
                throw new GerberMacroExpressionException($"Unclosed '(' in macro modifier \"{s}\".");
            pos++;
            return inner;
        }

        if (c == '$')
        {
            pos++;
            int start = pos;
            while (pos < s.Length && char.IsAsciiDigit(s[pos])) pos++;
            if (pos == start) throw new GerberMacroExpressionException($"'$' with no index in macro modifier \"{s}\".");
            int index = int.Parse(s[start..pos], CultureInfo.InvariantCulture);
            return vars.TryGetValue(index, out double v) ? v : 0.0;
        }

        if (char.IsAsciiDigit(c) || c == '.')
        {
            int start = pos;
            while (pos < s.Length && (char.IsAsciiDigit(s[pos]) || s[pos] == '.')) pos++;
            return double.Parse(s[start..pos], CultureInfo.InvariantCulture);
        }

        throw new GerberMacroExpressionException($"Unexpected '{c}' in macro modifier \"{s}\".");
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }
}
