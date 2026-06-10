namespace CircuitRF.Core.Expressions;

/// <summary>
/// Normalizes editor glyph unit strings to engine ASCII spellings at the extraction boundary.
/// The editor uses Unicode glyphs (Ω, µ); the engine <see cref="Units"/> table is ASCII-keyed
/// (Ohm, u) with <see cref="StringComparer.Ordinal"/>. Convert at the boundary, once.
///
/// Glyph substitutions (compose with any SI prefix):
///   Ω  (U+03A9) → Ohm   e.g. kΩ→kOhm, MΩ→MOhm, GΩ→GOhm, mΩ→mOhm
///   µ  (U+00B5) → u     e.g. µH→uH, µF→uF, µV→uV, µA→uA, µW→uW, µm→um
///   μ  (U+03BC) → u     defensive: some fonts/inputs produce Greek mu instead of MICRO SIGN
///
/// Already-ASCII units (nH, pF, Hz, deg, mil, …) pass through unchanged.
/// "None" or empty → empty (no unit emitted).
///
/// Table-uncovered units that pass through as-is (engine handles them separately):
///   dBm — measurement function, not a linear scale suffix
///   V, A, W, kV, nV, µV→uV, nA, µA→uA — voltage/current are identity at this scale layer
///   nm, cm — length units not in the linear-scale table (only mm, um, mil present)
/// </summary>
public static class UnitNormalizer
{
    /// <summary>
    /// Maps an editor unit string to the engine ASCII spelling accepted by <see cref="Units.Scale"/>.
    /// </summary>
    /// <returns>
    /// The ASCII engine unit string, or <see cref="string.Empty"/> when the input is null,
    /// empty, or <c>"None"</c>.
    /// </returns>
    public static string ToEngineUnit(string? editorUnit)
    {
        if (string.IsNullOrEmpty(editorUnit) ||
            editorUnit.Equals("None", StringComparison.Ordinal))
            return string.Empty;

        // Fast path: pure ASCII — no glyph characters present.
        bool hasOmega = editorUnit.Contains('Ω');  // Ω
        bool hasMicro = editorUnit.Contains('µ') || editorUnit.Contains('μ'); // µ / μ

        if (!hasOmega && !hasMicro)
            return editorUnit;

        // Character substitution — composes naturally with any SI prefix or suffix.
        var result = editorUnit;
        if (hasMicro)
            result = result.Replace("µ", "u").Replace("μ", "u");
        if (hasOmega)
            result = result.Replace("Ω", "Ohm");

        return result;
    }
}
