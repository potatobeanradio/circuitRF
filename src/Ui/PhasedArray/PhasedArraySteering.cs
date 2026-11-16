// brief-paa-9 R-paa9-3 — what dragging the beam marker writes. The weights steer when Phase (or, with none, Delay) holds a steer() or
// steerDelay() call (R-paa4-3: the PARSED call, never the text); its first two arguments are θ₀ and φ₀, and each is either a VAR of the
// schematic — the drag writes the VAR — or a literal — the drag rewrites the literal in place. Anything else (an expression, a VAR defined
// nowhere in the drawing) leaves the marker undraggable, and Refusal says why in one sentence, which the marker's tooltip shows.
//
// A literal is found by TOKEN POSITION: the call's argument is the run of tokens between its parenthesis and the first comma at its own
// depth, so `steer(30 deg, 0, f0)` rewrites `30 deg` and nothing else of the field's text.
//
// The value written keeps the form it replaces: a VAR whose unit column says deg takes a number, rad takes radians; any other VAR and
// every literal take `<value>deg` (a literal glued, `<value> deg` for a VAR row), since a bare number in a trig argument is radians.

using System.Globalization;
using CircuitRF.Core.Devices.Arrays;
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

    public const string NotSteered = SteerCall.NotSteered;

    /// <summary>The steering of <paramref name="array"/> in a drawing whose components are <paramref name="components"/>: the call as
    /// <see cref="SteerCall"/> reads it (brief-pch-9 — the one reading a channel's beam selection shares).</summary>
    public static PhasedArraySteering Of(EditableComponent array, IEnumerable<EditableComponent> components)
    {
        var call = SteerCall.Find(field => array.Parameters.FirstOrDefault(p => p.Name == field)?.Expression, VarNames(components));
        if (call is null) return new("", "", null, null, NotSteered);
        static SteerArgument? Arg(SteerSpan? s) => s is null ? null : new SteerArgument(s.Var, s.Start, s.Length, s.Text);
        return new(call.Field, call.Function, Arg(call.Theta), Arg(call.Phi), call.Refusal);
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
        // glued (30deg): inside an expression `30 deg` is a number followed by a name, which the parser refuses, and a space would also split
        // the netlist line the field is written on
        if (Theta is { IsVar: false }) spans.Add((Theta, Number(thetaDeg) + "deg"));
        if (Phi is { IsVar: false }) spans.Add((Phi, Number(phiDeg) + "deg"));
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
}
