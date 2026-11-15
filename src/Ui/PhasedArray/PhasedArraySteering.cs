// brief-paa-9 R-paa9-3 — what dragging the beam marker writes. The weights steer when Phase (or, with none, Delay) holds a steer() or
// steerDelay() call (R-paa4-3: the PARSED call, never the text); its first two arguments are θ₀ and φ₀, and each is either a VAR of the
// schematic — the drag writes the VAR — or a literal — the drag rewrites the literal in place. Anything else (an expression, a VAR defined
// nowhere in the drawing) leaves the marker undraggable, and Refusal says why in one sentence, which the marker's tooltip shows.
//
// A literal is found by TOKEN POSITION: the call's argument is the run of tokens between its parenthesis and the first comma at its own
// depth, so `steer(30 deg, 0, f0)` rewrites `30 deg` and nothing else of the field's text.
//
// The value written keeps the form it replaces: a VAR whose unit column says deg takes a number, rad takes radians; any other VAR and
// every literal take `<value> deg`, since a bare number in a trig argument is radians and would mean something else.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.PhasedArray;

/// <summary>One steering argument: a VAR of the schematic, or a literal at a span of the field's text.</summary>
public sealed record SteerArgument(string? Var, int Start, int Length, string Text)
{
    public bool IsVar => Var is not null;
}

/// <summary>R-paa9-3 — the steering of one Phased Antenna Array instance.</summary>
public sealed record PhasedArraySteering(string Field, string Function, SteerArgument? Theta, SteerArgument? Phi, string? Refusal)
{
    /// <summary>True when a drag can write both angles.</summary>
    public bool Draggable => Refusal is null && Theta is not null && Phi is not null;

    public const string NotSteered = "The weights are not a steer() or steerDelay() call, so the beam is not steered from here.";

    /// <summary>The steering of <paramref name="array"/> in a drawing whose components are <paramref name="components"/>.</summary>
    public static PhasedArraySteering Of(EditableComponent array, IEnumerable<EditableComponent> components)
    {
        var vars = VarNames(components);
        foreach (string field in (string[])["Phase", "Delay"])
        {
            if (array.Parameters.FirstOrDefault(p => p.Name == field) is not { } param || string.IsNullOrWhiteSpace(param.Expression)) continue;
            string text = param.Expression;
            Token[] tokens;
            try { tokens = new Tokenizer(text).Tokenize(); Parser.Parse(text); }
            catch (Exception e) when (e is ExpressionException or ParseException) { continue; }
            int at = -1;
            for (int i = 0; i + 1 < tokens.Length && at < 0; i++)
                if (tokens[i].Kind == TokenKind.Identifier && tokens[i].Text is "steer" or "steerDelay" && tokens[i + 1].Kind == TokenKind.LParen) at = i;
            if (at < 0) continue;
            string fn = tokens[at].Text;
            var args = Arguments(tokens, at + 1);
            if (args.Count < 2) return new(field, fn, null, null, $"The {fn}() in {field} has no θ0 and φ0 to write.");
            var theta = Argument(text, tokens, args[0], vars);
            var phi = Argument(text, tokens, args[1], vars);
            string? why = theta is null ? $"θ0 in {field} is '{Span(text, tokens, args[0])}', neither a VAR of this schematic nor a number, so a drag cannot write it."
                        : phi is null ? $"φ0 in {field} is '{Span(text, tokens, args[1])}', neither a VAR of this schematic nor a number, so a drag cannot write it."
                        : null;
            return new(field, fn, theta, phi, why);
        }
        return new("", "", null, null, NotSteered);
    }

    /// <summary>The tunable values a drag to (θ₀, φ₀) evaluates with — a VAR by its name, a literal as the whole field rewritten
    /// (<c>PA1.Phase</c>) — so nothing is written until the drag ends.</summary>
    public Dictionary<string, string> Tunables(string instance, IReadOnlyList<EditableParameter> fields, double thetaDeg, double phiDeg)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Draggable) return values;
        if (Theta!.IsVar) values[Theta.Var!] = Degrees(thetaDeg);
        if (Phi!.IsVar) values[Phi.Var!] = Degrees(phiDeg);
        if (!Theta.IsVar || !Phi.IsVar) values[$"{instance}.{Field}"] = Rewritten(fields, thetaDeg, phiDeg);
        return values;
    }

    /// <summary>The field's text with each literal argument replaced by its new value.</summary>
    public string Rewritten(IReadOnlyList<EditableParameter> fields, double thetaDeg, double phiDeg)
    {
        string text = fields.First(p => p.Name == Field).Expression;
        var spans = new List<(SteerArgument Arg, string Value)>();
        if (Theta is { IsVar: false }) spans.Add((Theta, Degrees(thetaDeg)));
        if (Phi is { IsVar: false }) spans.Add((Phi, Degrees(phiDeg)));
        foreach (var (arg, value) in spans.OrderByDescending(s => s.Arg.Start))
            text = text[..arg.Start] + value + text[(arg.Start + arg.Length)..];
        return text;
    }

    /// <summary>The VAR rows a drag ending at (θ₀, φ₀) writes, with the expression and unit each takes.</summary>
    public IEnumerable<(EditableParameter Row, string Expression, string Unit)> VarWrites(IEnumerable<EditableComponent> components, double thetaDeg, double phiDeg)
    {
        foreach (var (arg, deg) in new[] { (Theta, thetaDeg), (Phi, phiDeg) })
        {
            if (arg is not { IsVar: true } || VarRow(components, arg.Var!) is not { } row) continue;
            string unit = row.Unit.Trim();
            string expr = unit switch
            {
                "deg" or "°" => Number(deg),
                "rad" => Number(deg * Math.PI / 180),
                _ => Degrees(deg),
            };
            yield return (row, expr, unit is "deg" or "°" or "rad" ? row.Unit : "");
        }
    }

    /// <summary>The first enabled VAR row called <paramref name="name"/>, as the netlist takes the first definition.</summary>
    public static EditableParameter? VarRow(IEnumerable<EditableComponent> components, string name)
        => components.Where(c => c.Symbol == SymbolKind.Var && c.Disable is not (DisableState.Open or DisableState.Short))
                     .SelectMany(c => c.Parameters).FirstOrDefault(p => p.Name.Trim() == name);

    /// <summary><paramref name="deg"/> written to a hundredth of a degree.</summary>
    public static string Degrees(double deg) => Number(deg) + " deg";

    private static string Number(double x) => Math.Round(x, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static HashSet<string> VarNames(IEnumerable<EditableComponent> components)
        => components.Where(c => c.Symbol == SymbolKind.Var && c.Disable is not (DisableState.Open or DisableState.Short))
                     .SelectMany(c => c.Parameters).Select(p => p.Name.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

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
    private static SteerArgument? Argument(string text, Token[] tokens, (int First, int Last) r, HashSet<string> vars)
    {
        int s = tokens[r.First].Position, e = tokens[r.Last].Position + tokens[r.Last].Text.Length;
        string span = text[s..Math.Min(e, text.Length)];
        var kinds = Enumerable.Range(r.First, r.Last - r.First + 1).Select(i => tokens[i]).ToList();
        if (kinds.Count == 1 && kinds[0].Kind == TokenKind.Identifier)
            return vars.Contains(kinds[0].Text) ? new SteerArgument(kinds[0].Text, s, e - s, span) : null;
        int k = 0;
        if (kinds[0].Kind is TokenKind.Minus or TokenKind.Plus) k = 1;
        bool literal = (kinds.Count - k) switch
        {
            1 => kinds[k].Kind is TokenKind.Number or TokenKind.Quantity,
            2 => kinds[k].Kind == TokenKind.Number && kinds[k + 1].Kind == TokenKind.Identifier && Units.IsRecognizedUnit(kinds[k + 1].Text),
            _ => false,
        };
        return literal ? new SteerArgument(null, s, e - s, span) : null;
    }
}
