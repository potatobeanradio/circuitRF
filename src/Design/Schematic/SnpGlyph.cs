namespace CircuitRF.Design.Schematic;

/// <summary>
/// An SnP drawn as a built-in part (brief vendorlib-11 R-vl11-2d): its artwork-only <c>Symbol</c> parameter names a
/// built-in kind, and SnP port k binds to that glyph's pin k. Export to Library converts a technology-bound part to an
/// SnP sampled on the author's technology, and this is what keeps the converted instance's pins where its wires are —
/// an SnP's own box puts them somewhere else.
///
/// <para><b>What draws the glyph:</b> a <c>Symbol</c> that names a built-in kind exactly, whose pin count is the
/// SnP's port count, with no reference pin. Anything else draws the SnP's own box, as it always has — an SnP saved
/// before the parameter existed carries none. The parameter never reaches the netlist.</para>
/// </summary>
public static class SnpGlyph
{
    /// <summary>The instance parameter naming the glyph.</summary>
    public const string SymbolParameter = "Symbol";

    /// <summary>The glyph <paramref name="symbol"/> names for an SnP of <paramref name="ports"/> ports, or null for
    /// the SnP's own box.</summary>
    public static SymbolKind? KindFor(string? symbol, int ports, bool refNode)
    {
        string name = (symbol ?? "").Trim().Trim('"').Trim();
        if (refNode || name.Length == 0) return null;
        if (!Enum.TryParse<SymbolKind>(name, ignoreCase: false, out var kind) || !Enum.IsDefined(kind)
            || int.TryParse(name, out _) || kind is SymbolKind.Snp or SymbolKind.EmFieldData or SymbolKind.Unknown)
            return null;
        return SymbolPortDefs.For(kind, ports).Length == ports ? kind : null;
    }

    /// <summary>The glyph of a placed SnP, or null for its own box.</summary>
    public static SymbolKind? Of(EditableComponent comp)
        => comp.Symbol != SymbolKind.Snp ? null
         : KindFor(comp.Parameters.FirstOrDefault(p => p.Name == SymbolParameter)?.Expression, comp.PortCount,
                   comp.Parameters.FirstOrDefault(p => p.Name == "RefNode")?.Expression?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The glyphs the Parameter Editor lists for an SnP of <paramref name="ports"/> ports: the
    /// technology-bound kinds with that many pins — the parts Export converts. Any other built-in kind of that pin count
    /// is still ACCEPTED when written.</summary>
    public static IReadOnlyList<SymbolKind> Choices(int ports)
        => [.. Enum.GetValues<SymbolKind>()
                .Where(k => TechnologyBound.Is(k) && SymbolPortDefs.For(k, ports).Length == ports)
                .OrderBy(k => ComponentTypeRegistry.EngineReference(k), StringComparer.Ordinal)];
}
