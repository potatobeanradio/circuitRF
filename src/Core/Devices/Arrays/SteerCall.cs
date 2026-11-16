// brief-pch-9 R-pch9-5 — where a Phased Antenna Array's steer() or steerDelay() call holds its two angles, found as PAA-9's drag finds them
// (brief-paa-9 R-paa9-3): the PARSED call in Phase (or, when Phase holds none, Delay), the first one in the field; its first two arguments are
// θ0 and φ0, each the run of tokens between the call's parenthesis (or a comma at its own depth) and the next comma at that depth.
//
// An argument a selection may set is a VAR of the design or a literal (a number or a quantity, signed, or a number and a separate unit word);
// anything else — an expression, a name defined nowhere — is refused with the sentence the tool's marker shows, since setting it would mean
// guessing what the expression was for. The Phased Array tool (src/Ui PhasedArraySteering) and a channel's beam selection both read the call
// through this one type, so a beam the tool can drag is a beam a run can select and the converse.
//
// Rewritten replaces each argument's span with a literal number of degrees (`12.5deg`, full precision), so the field is the same text with
// the two angles changed and nothing else: the taper, f0, a Phase offset written beside the call and the quantisation all stay the array's.

using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.Arrays;

/// <summary>One argument of the call: a VAR (its name) or a literal, at a span of the field's text.</summary>
public sealed record SteerSpan(string? Var, int Start, int Length, string Text)
{
    public bool IsVar => Var is not null;
}

/// <summary>R-pch9-5 — the steer() or steerDelay() call in one field of a Phased Antenna Array.</summary>
public sealed record SteerCall(string Field, string Function, string Text, SteerSpan? Theta, SteerSpan? Phi, string? Refusal)
{
    /// <summary>True when both angles can be set.</summary>
    public bool Settable => Refusal is null && Theta is not null && Phi is not null;

    public const string NotSteered = "The weights are not a steer() or steerDelay() call, so the beam is not steered from here.";

    /// <summary>
    /// The call in the first of Phase, Delay whose text holds one, with <paramref name="vars"/> the names that count as VARs; null when
    /// neither field holds one (<see cref="NotSteered"/>).
    /// </summary>
    public static SteerCall? Find(Func<string, string?> fieldText, IReadOnlySet<string> vars)
    {
        foreach (string field in (string[])["Phase", "Delay"])
        {
            if (fieldText(field) is not { } text || string.IsNullOrWhiteSpace(text)) continue;
            Token[] tokens;
            try { tokens = new Tokenizer(text).Tokenize(); Parser.Parse(text); }
            catch (Exception e) when (e is ExpressionException or ParseException) { continue; }
            int at = -1;
            for (int i = 0; i + 1 < tokens.Length && at < 0; i++)
                if (tokens[i].Kind == TokenKind.Identifier && tokens[i].Text is "steer" or "steerDelay" && tokens[i + 1].Kind == TokenKind.LParen) at = i;
            if (at < 0) continue;
            string fn = tokens[at].Text;
            var args = Arguments(tokens, at + 1);
            if (args.Count < 2) return new(field, fn, text, null, null, $"The {fn}() in {field} has no θ0 and φ0 to write.");
            var theta = Argument(text, tokens, args[0], vars);
            var phi = Argument(text, tokens, args[1], vars);
            string? why = theta is null ? $"θ0 in {field} is '{Span(text, tokens, args[0])}', neither a VAR of this schematic nor a number, so a drag cannot write it."
                        : phi is null ? $"φ0 in {field} is '{Span(text, tokens, args[1])}', neither a VAR of this schematic nor a number, so a drag cannot write it."
                        : null;
            return new(field, fn, text, theta, phi, why);
        }
        return null;
    }

    /// <summary>The field's text with θ0 and φ0 replaced by literal degrees (every argument, a VAR's included, so only this field changes).</summary>
    public string Rewritten(double thetaDeg, double phiDeg)
    {
        if (!Settable) throw new InvalidOperationException(Refusal ?? NotSteered);
        string text = Text;
        foreach (var (arg, value) in new[] { (Theta!, thetaDeg), (Phi!, phiDeg) }.OrderByDescending(s => s.Item1.Start))
            text = text[..arg.Start] + Degrees(value) + text[(arg.Start + arg.Length)..];
        return text;
    }

    /// <summary><paramref name="deg"/> as a literal angle at full precision, the unit glued to the number (inside an expression `30 deg`
    /// is a number followed by a name, which the parser refuses; `30deg` is one quantity).</summary>
    public static string Degrees(double deg) => deg.ToString("R", CultureInfo.InvariantCulture) + "deg";

    /// <summary>The token ranges [first, last] of each argument of the call whose '(' is at <paramref name="open"/>.</summary>
    private static List<(int First, int Last)> Arguments(Token[] tokens, int open)
    {
        var args = new List<(int, int)>();
        int depth = 0, start = open + 1;
        for (int i = open; i < tokens.Length; i++)
        {
            var k = tokens[i].Kind;
            if (k is TokenKind.LParen or TokenKind.LBracket) { depth++; continue; }
            if (k is TokenKind.RParen or TokenKind.RBracket)
            {
                depth--;
                if (depth == 0) { if (i > start) args.Add((start, i - 1)); return args; }
                continue;
            }
            if (k == TokenKind.Comma && depth == 1) { args.Add((start, i - 1)); start = i + 1; }
        }
        return args;
    }

    private static string Span(string text, Token[] tokens, (int First, int Last) r)
    {
        int s = tokens[r.First].Position, e = tokens[r.Last].Position + tokens[r.Last].Text.Length;
        return text[s..Math.Min(e, text.Length)].Trim();
    }

    /// <summary>A VAR reference or a literal (a number, a quantity, either signed, or a number and a separate unit word), else null.</summary>
    private static SteerSpan? Argument(string text, Token[] tokens, (int First, int Last) r, IReadOnlySet<string> vars)
    {
        int s = tokens[r.First].Position, e = tokens[r.Last].Position + tokens[r.Last].Text.Length;
        string span = text[s..Math.Min(e, text.Length)];
        var kinds = Enumerable.Range(r.First, r.Last - r.First + 1).Select(i => tokens[i]).ToList();
        if (kinds.Count == 1 && kinds[0].Kind == TokenKind.Identifier)
            return vars.Contains(kinds[0].Text) ? new SteerSpan(kinds[0].Text, s, e - s, span) : null;
        int k = 0;
        if (kinds[0].Kind is TokenKind.Minus or TokenKind.Plus) k = 1;
        bool literal = (kinds.Count - k) switch
        {
            1 => kinds[k].Kind is TokenKind.Number or TokenKind.Quantity,
            2 => kinds[k].Kind == TokenKind.Number && kinds[k + 1].Kind == TokenKind.Identifier && Units.IsRecognizedUnit(kinds[k + 1].Text),
            _ => false,
        };
        return literal ? new SteerSpan(null, s, e - s, span) : null;
    }
}
