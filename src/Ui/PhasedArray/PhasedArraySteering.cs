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
//
// AN ARRAY STEERED BY WHAT DRIVES IT (brief-paa-12). An array in Elements mode has no weights of its own: each element is driven through its
// own chain, and the steering is in the drives (a Phase Shifter's Phase=-steer(theta0,0,f0,x1,0)). When the array's own fields hold no call,
// the calls in the drawing's other components are its steering, provided they agree: each angle the same VAR in every call, or the same
// literal in every call. A drag then writes the VAR, or rewrites the literal at every call (Sites); anything else is a refusal naming them.

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

/// <summary>brief-paa-12 — one steer() call in a component that drives an Elements-mode array: the instance, the field and its text.</summary>
public sealed record SteerSite(string Instance, string Field, string Text, SteerArgument Theta, SteerArgument Phi);

/// <summary>R-paa9-3 — the steering of one Phased Antenna Array instance.</summary>
public sealed record PhasedArraySteering(string Field, string Function, SteerArgument? Theta, SteerArgument? Phi, string? Refusal)
{
    /// <summary>brief-paa-12 — the calls in the drives that steer an Elements-mode array, when its own fields hold none; empty otherwise.</summary>
    public IReadOnlyList<SteerSite> Sites { get; init; } = [];

    /// <summary>True when a drag can write both angles.</summary>
    public bool Draggable => Refusal is null && Theta is not null && Phi is not null;

    public const string NotSteered = SteerCall.NotSteered;

    /// <summary>The steering of <paramref name="array"/> in a drawing whose components are <paramref name="components"/>: the call as
    /// <see cref="SteerCall"/> reads it (brief-pch-9 — the one reading a channel's beam selection shares).</summary>
    public static PhasedArraySteering Of(EditableComponent array, IEnumerable<EditableComponent> components)
    {
        var vars = VarNames(components);
        var call = SteerCall.Find(field => array.Parameters.FirstOrDefault(p => p.Name == field)?.Expression, vars);
        if (call is null) return Driven(array, components, vars) ?? new("", "", null, null, NotSteered);
        return new(call.Field, call.Function, Arg(call.Theta), Arg(call.Phi), call.Refusal);
    }

    private static SteerArgument? Arg(SteerSpan? s) => s is null ? null : new SteerArgument(s.Var, s.Start, s.Length, s.Text);

    /// <summary>
    /// brief-paa-12 — the steering of an array whose own fields hold no call, from the calls in the drawing's other components (not a VAR,
    /// not another array, not disabled); null when none holds one.
    /// </summary>
    private static PhasedArraySteering? Driven(EditableComponent array, IEnumerable<EditableComponent> components, HashSet<string> vars)
    {
        var calls = new List<(string Instance, SteerCall Call)>();
        foreach (var c in components)
        {
            if (ReferenceEquals(c, array) || c.Symbol is SymbolKind.Var or SymbolKind.PhasedArray || c.Disable is DisableState.Open or DisableState.Short) continue;
            if (SteerCall.Find(field => c.Parameters.FirstOrDefault(p => p.Name == field)?.Expression, vars) is { } found) calls.Add((c.InstanceName, found));
        }
        if (calls.Count == 0) return null;
        var first = calls[0].Call;
        string names = string.Join(", ", calls.Select(c => c.Instance));
        if (calls.FirstOrDefault(c => !c.Call.Settable) is { Call: not null } bad)
            return new(first.Field, first.Function, Arg(first.Theta), Arg(first.Phi), $"{bad.Instance}: {bad.Call.Refusal ?? NotSteered}");
        // each angle the same VAR at every call, or the same literal: a drag writes one steering, not one per drive
        static string Key(SteerSpan s) => s.Var is { } v ? "var:" + v : "lit:" + s.Text.Replace(" ", "", StringComparison.Ordinal);
        foreach (var (label, pick) in new (string, Func<SteerCall, SteerSpan>)[] { ("θ0", c => c.Theta!), ("φ0", c => c.Phi!) })
            if (calls.Select(c => Key(pick(c.Call))).Distinct().Count() > 1)
                return new(first.Field, first.Function, Arg(first.Theta), Arg(first.Phi),
                           $"The steer() calls on {names} do not write {label} alike ({string.Join(", ", calls.Select(c => $"{c.Instance}: {pick(c.Call).Text.Trim()}"))}), so a drag cannot write one steering.");
        return new(first.Field, first.Function, Arg(first.Theta), Arg(first.Phi), null)
        {
            Sites = [.. calls.Select(c => new SteerSite(c.Instance, c.Call.Field, c.Call.Text, Arg(c.Call.Theta)!, Arg(c.Call.Phi)!))],
        };
    }

    /// <summary>The tunable values a drag to (θ₀, φ₀) evaluates with — a VAR by its name, a literal as the whole field rewritten
    /// (<c>PA1.Phase</c>) — so nothing is written until the drag ends.</summary>
    public Dictionary<string, string> Tunables(string instance, IReadOnlyList<EditableParameter> fields, double thetaDeg, double phiDeg)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Draggable) return values;
        if (Theta!.IsVar) values[Theta.Var!] = Degrees(thetaDeg);
        if (Phi!.IsVar) values[Phi.Var!] = Degrees(phiDeg);
        if (Theta.IsVar && Phi.IsVar) return values;
        if (Sites.Count == 0) values[$"{instance}.{Field}"] = Rewritten(fields, thetaDeg, phiDeg);
        foreach (var site in Sites) values[$"{site.Instance}.{site.Field}"] = Rewrite(site.Text, site.Theta, site.Phi, thetaDeg, phiDeg);
        return values;
    }

    /// <summary>
    /// The fields a drag ending at (θ₀, φ₀) rewrites, beside its VAR writes: the array's own field when an argument is a literal there, or
    /// each drive's call (brief-paa-12); nothing when both angles are VARs.
    /// </summary>
    public IEnumerable<(EditableComponent Component, string Field, string Text)> FieldWrites(EditableComponent array, IEnumerable<EditableComponent> components,
                                                                                          double thetaDeg, double phiDeg)
    {
        if (!Draggable || (Theta!.IsVar && Phi!.IsVar)) yield break;
        if (Sites.Count == 0) { yield return (array, Field, Rewritten(array.Parameters, thetaDeg, phiDeg)); yield break; }
        foreach (var site in Sites)
            if (components.FirstOrDefault(c => c.InstanceName == site.Instance) is { } comp)
                yield return (comp, site.Field, Rewrite(site.Text, site.Theta, site.Phi, thetaDeg, phiDeg));
    }

    /// <summary>The field's text with each literal argument replaced by its new value.</summary>
    public string Rewritten(IReadOnlyList<EditableParameter> fields, double thetaDeg, double phiDeg)
        => Rewrite(fields.First(p => p.Name == Field).Expression, Theta, Phi, thetaDeg, phiDeg);

    /// <summary><paramref name="text"/> with each literal argument replaced by its new value.</summary>
    private static string Rewrite(string text, SteerArgument? theta, SteerArgument? phi, double thetaDeg, double phiDeg)
    {
        var spans = new List<(SteerArgument Arg, string Value)>();
        // glued (30deg): inside an expression `30 deg` is a number followed by a name, which the parser refuses, and a space would also split
        // the netlist line the field is written on
        // a literal the drag left where it was keeps its own spelling (a 0 written in the φ0 of a linear array's drives stays 0)
        static bool Moved(SteerArgument arg, double deg)
            => SchematicNominal.Of([]).Real(arg.Text, null) is not { } rad || Math.Abs(rad * 180 / Math.PI - Math.Round(deg, 2)) > 1e-6;
        if (theta is { IsVar: false } && Moved(theta, thetaDeg)) spans.Add((theta, Number(thetaDeg) + "deg"));
        if (phi is { IsVar: false } && Moved(phi, phiDeg)) spans.Add((phi, Number(phiDeg) + "deg"));
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
