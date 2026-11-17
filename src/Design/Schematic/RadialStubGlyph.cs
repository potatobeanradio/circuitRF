// What an MRADIAL's symbol is drawn from: the sector's angle and whether it is a butterfly, resolved from an instance's parameters at
// the nominal point. THE KEY IS EVERYTHING THE DRAWING SHOWS AND NOTHING ELSE — a record with value equality, so BuiltInSymbols caches
// one symbol per key, as it does PhasedArrayGlyph's. The pin never moves: only the artwork right of it follows the parameters.
//
// Angle is an angle as any trig argument's (ComponentModelFactory reads it in radians): a unit-bearing 60 deg resolves to π/3. It is
// rounded to a whole degree, so a sweep of Angle does not build a symbol per sample. A value that does not resolve, or lies outside
// (0°, 180°], draws the default 60°; Sides other than 2 draws one sector.

using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Schematic;

/// <summary>The variant of an MRADIAL's symbol: the sector's angle in whole degrees, and one sector or two (a butterfly).</summary>
public sealed record RadialStubGlyph(int AngleDeg, bool Butterfly)
{
    /// <summary>What a caller with no instance draws, and the fallback: the registry's 60°, one sector.</summary>
    public static RadialStubGlyph Default { get; } = new(60, false);

    /// <summary>The variant of an instance with <paramref name="parameters"/>, resolved in <paramref name="nominal"/> (literals only, when
    /// null).</summary>
    public static RadialStubGlyph Of(IEnumerable<EditableParameter> parameters, SchematicNominal? nominal)
    {
        var p = parameters.ToList();
        nominal ??= SchematicNominal.Empty;
        double? Value(string name)
        {
            if (p.FirstOrDefault(q => q.Name == name) is not { } q) return null;
            string? unit = UnitNormalizer.ToEngineUnit(q.Unit) is { Length: > 0 } u ? u : null;
            var (expr, inline) = unit is null ? Units.LiftInlineUnit(q.Expression) : (q.Expression, null);
            return nominal.Real(expr, unit ?? inline);
        }
        int angle = Default.AngleDeg;
        if (Value("Angle") is { } rad && (int)Math.Round(rad * 180.0 / Math.PI) is var deg && deg is >= 1 and <= 180) angle = deg;
        bool butterfly = Value("Sides") is { } sides && Math.Round(sides) == 2;
        return new RadialStubGlyph(angle, butterfly);
    }
}
