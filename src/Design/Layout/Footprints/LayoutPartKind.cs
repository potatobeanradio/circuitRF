// What a layout-first PLACEMENT is a part OF — brief-footprint-6 R-fp6-2d.
//
// It lives here rather than on LayoutInstance because LayoutModel.cs's own first line says the
// layout model references no Schematic types, and that rule is worth more than the convenience of a
// property. The FIELD is a string, which is what the file stores; this is the one place that string
// is turned back into a SymbolKind, so there is one answer to "what does an unreadable value mean".

using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// Reads and writes <see cref="LayoutInstance.PartKind"/> — the <see cref="SymbolKind"/> a
/// layout-first placement declares itself to be.
/// </summary>
public static class LayoutPartKind
{
    /// <summary>The stored spelling of <paramref name="kind"/> — its NAME, never its number
    /// (R-fp6-2d), so the file stays readable when the enum gains a member in the middle.</summary>
    public static string Name(SymbolKind kind) => kind.ToString();

    /// <summary>
    /// The kind <paramref name="stored"/> names, or null when it names none.
    ///
    /// <para><b>An unrecognised value is ABSENT, not an error</b> (R-fp6-2d). A <c>.clay</c> written
    /// by a later version can name a part this one has never heard of, and the right outcome is a
    /// board that opens with that placement degraded to a bare land pattern — the R-fp6-1a case,
    /// which creates nothing and says so — rather than a board that will not open at all.</para>
    ///
    /// <para>A NUMBER is refused too, though <see cref="Enum.TryParse{T}(string, out T)"/> would
    /// happily accept one: the file states a name, so a file stating <c>"7"</c> was written by
    /// something that did not follow the rule and its meaning would move the next time the enum
    /// does.</para>
    /// </summary>
    public static SymbolKind? Parse(string? stored)
    {
        if (stored is not { Length: > 0 } s) return null;
        if (char.IsAsciiDigit(s[0]) || s[0] == '-' || s[0] == '+') return null;
        return Enum.TryParse<SymbolKind>(s, ignoreCase: false, out var kind) && Enum.IsDefined(kind)
            ? kind
            : null;
    }

    /// <summary><paramref name="inst"/>'s declared part kind, or null — including for every instance
    /// the schematic owns, which stores none because the schematic knows.</summary>
    public static SymbolKind? Of(LayoutInstance? inst) => Parse(inst?.PartKind);
}
