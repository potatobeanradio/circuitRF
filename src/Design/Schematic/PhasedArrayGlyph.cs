// brief-paa-5 R-paa5-4 — what a Phased Antenna Array's symbol is drawn from, resolved from an instance's parameters at the nominal point
// (docs/design/phased-array.md §4.6).
//
// THE KEY IS EVERYTHING THE DRAWING SHOWS AND NOTHING ELSE: the mode, the lattice (Rows, Cols, rect or tri), the elements switched off and
// the steering (θ0, φ0 rounded to 1°). A record with value equality, so BuiltInSymbols caches one symbol per key as it caches the
// Combiner's per N, and ten identical instances share one.
//
// VALUES RESOLVE AGAINST THE SCHEMATIC'S OWN VARs (SchematicNominal), so Rows=nr or Phase=steer(theta0,0,f0) draws what the netlist will
// run. A lattice that does not resolve draws 4 × 4 in the instance's mode; a steering that does not, boresight with no text. θ0 and φ0
// are read from the parsed Phase (or, when it has none, Delay) — the first steer() or steerDelay() call in it — never from its text
// (R-paa4-3), and are angles as any trig argument's: a unit-bearing 30 deg is already radians.

using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Schematic;

/// <summary>R-paa5-4 — the variant of a Phased Antenna Array's symbol.</summary>
public sealed record PhasedArrayGlyph(bool Feed, int Rows, int Cols, bool Tri, string Off, int? ThetaDeg, int? PhiDeg)
{
    /// <summary>The variant a caller with no instance draws, and what an unresolved lattice and steering fall back to: Feed mode, 4 × 4,
    /// boresight, no text.</summary>
    public static PhasedArrayGlyph Default { get; } = new(true, 4, 4, false, "", null, null);

    /// <summary>The largest side the thumbnail draws in full; a larger one draws this corner of it and says the size.</summary>
    public const int ThumbnailSide = 8;

    public int Count => Rows * Cols;

    /// <summary>One pin in Feed mode, one per element in Elements mode.</summary>
    public int PinCount => Feed ? 1 : Count;

    /// <summary>The elements switched off, 1-based.</summary>
    /// <remarks>Computed, not kept: a record's equality is over its fields, and a cached set would make two equal keys unequal.</remarks>
    public IReadOnlySet<int> OffSet => Off.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

    /// <summary>Whether a steering was found and resolved: the lobe tilts and the angles are written.</summary>
    public bool Steered => ThetaDeg is not null && PhiDeg is not null;

    /// <summary>The variant of an instance with <paramref name="parameters"/>, its expressions resolved in <paramref name="nominal"/> (literals
    /// only, when null).</summary>
    public static PhasedArrayGlyph Of(IEnumerable<EditableParameter> parameters, SchematicNominal? nominal)
    {
        var p = parameters.ToList();
        nominal ??= SchematicNominal.Empty;
        string Word(string name) => p.FirstOrDefault(q => q.Name == name)?.Expression.Trim().Trim('"').Trim() ?? "";
        bool feed = Word("Mode").Equals("feed", StringComparison.OrdinalIgnoreCase);
        bool tri = Word("Lattice").ToLowerInvariant() is "tri" or "triangular";

        int? Side(string name)
            => p.FirstOrDefault(q => q.Name == name) is { } q && nominal.Real(q.Expression, null) is { } v
               && v == Math.Round(v) && v >= 1 && v <= 64 ? (int)v : null;
        int rows = 4, cols = 4;
        if (Side("Rows") is { } r && Side("Cols") is { } c) (rows, cols) = (r, c);

        var off = new SortedSet<int>();
        foreach (string t in Word("Off").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int k)
                && k >= 1 && k <= rows * cols) off.Add(k);

        var (theta, phi) = feed ? Steering(p, nominal) : (null, null);
        return new PhasedArrayGlyph(feed, rows, cols, tri, string.Join(",", off), theta, phi);
    }

    /// <summary>θ0 and φ0, degrees rounded to 1°, from the first steer() or steerDelay() in Phase (or Delay); nulls when there is none or
    /// its angles do not resolve.</summary>
    private static (int?, int?) Steering(List<EditableParameter> p, SchematicNominal nominal)
    {
        foreach (string name in (string[])["Phase", "Delay"])
        {
            if (p.FirstOrDefault(q => q.Name == name) is not { } q || string.IsNullOrWhiteSpace(q.Expression)) continue;
            Expr ast;
            try { ast = Parser.Parse(q.Expression); }
            catch (Exception e) when (e is ExpressionException or ParseException) { continue; }
            if (FindSteer(ast) is not { Args.Length: >= 2 } call) continue;
            if (nominal.Real(call.Args[0]) is { } t && nominal.Real(call.Args[1]) is { } f)
                return ((int)Math.Round(t * 180 / Math.PI), (int)Math.Round(f * 180 / Math.PI));
            return (null, null);
        }
        return (null, null);
    }

    private static CallExpr? FindSteer(Expr e) => e switch
    {
        CallExpr { Name: "steer" or "steerDelay" } c => c,
        CallExpr c => c.Args.Select(FindSteer).FirstOrDefault(x => x is not null),
        UnaryExpr u => FindSteer(u.Operand),
        BinaryExpr b => FindSteer(b.Left) ?? FindSteer(b.Right),
        ConditionalExpr cd => FindSteer(cd.Then) ?? FindSteer(cd.Else),
        _ => null,
    };
}

/// <summary>
/// brief-paa-5 — a schematic's VARs as an expression scope, for what a symbol draws at the nominal point: every VAR row of the frame's VAR
/// blocks, with its unit as NetExtractor reads it (the unit column, or one written inline). Built per frame; a value that does not resolve
/// is null, never a guess.
/// </summary>
public sealed class SchematicNominal
{
    private readonly Scope _scope;

    private SchematicNominal(Scope scope) => _scope = scope;

    /// <summary>No VARs: literals resolve and names do not.</summary>
    public static SchematicNominal Empty { get; } = new(new Scope("global"));

    /// <summary>The VARs of <paramref name="components"/>' enabled VAR blocks, the first definition of a name winning as the netlist's does.</summary>
    public static SchematicNominal Of(IEnumerable<EditableComponent> components)
    {
        var scope = new Scope("global");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in components)
        {
            if (comp.Symbol != SymbolKind.Var || comp.Disable is DisableState.Open or DisableState.Short) continue;
            foreach (var v in comp.Parameters)
            {
                string name = v.Name.Trim();
                if (name.Length == 0 || !seen.Add(name)) continue;
                string? unit = UnitNormalizer.ToEngineUnit(v.Unit) is { Length: > 0 } u ? u : null;
                string expr = v.Expression;
                if (unit is null) (expr, unit) = Units.LiftInlineUnit(expr);
                scope.Bind(name, expr, unit);
            }
        }
        return new SchematicNominal(scope);
    }

    /// <summary>The real value of <paramref name="expression"/> with site unit <paramref name="unit"/>, or null.</summary>
    public double? Real(string expression, string? unit)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        try
        {
            var v = new Evaluator().Eval(expression, _scope, string.IsNullOrEmpty(unit) ? null : unit);
            return v.Kind == ValueKind.Real && double.IsFinite(v.AsReal()) ? v.AsReal() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The real value of a parsed expression, or null.</summary>
    public double? Real(Expr expression)
    {
        try
        {
            var v = new Evaluator().EvalExpr(expression, _scope);
            return v.Kind == ValueKind.Real && double.IsFinite(v.AsReal()) ? v.AsReal() : null;
        }
        catch (Exception) { return null; }
    }
}
