// The built-in footprint reference grammar — R-fp1-5.
//
//     smt:<case>            density defaults to N
//     smt:<case>@<density>  density is M, N or L
//
// The parser is TOTAL: it returns a refusal sentence, never throws and never falls back. An unknown
// case lists the codes it does know, which is the rule `--tech` already follows.
//
// The grammar is deliberately NOT a path (R-fp1-5b). A Footprint value that parses as `smt:` is a
// built-in; ANYTHING else is a relative path to a .clay or a cell folder, resolved by brief 3
// exactly as CellRef is. One discriminator, no third case — which is why TryParse's refusal for a
// string with no scheme says only "this is not a built-in" and does not speculate about what it is.

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// A reference to one of circuitRF's built-in land patterns: a case size and a density level.
/// </summary>
public sealed record FootprintRef(SmtCase Case, DensityLevel Density)
{
    /// <summary>
    /// Reserved (R-fp1-5c). A user's own <c>.clay</c> named <c>smt:something</c> cannot exist —
    /// <c>:</c> is not a path character on Windows and is a foot-gun on the others — so the built-in
    /// space and the path space cannot collide.
    /// </summary>
    public const string Scheme = "smt:";

    /// <summary>The default density when a reference names none — general commercial reflow.</summary>
    public const DensityLevel DefaultDensity = DensityLevel.Nominal;

    /// <summary><c>M</c>, <c>N</c> or <c>L</c>.</summary>
    public string DensityCode => CodeOf(Density);

    /// <summary>
    /// The cell-view suffix for this density — <c>-M</c>, <c>""</c> (nominal) and <c>-L</c>.
    ///
    /// <para>R-fp1-2c: this is <c>ComponentImport</c>'s existing variant concept, not a second one.
    /// <c>BuiltLayout.Variant</c> is already R-PL1-25's density suffix, and the nominal pattern is
    /// already the one that becomes <c>PrimaryLayout</c>. A generated pattern and an imported one
    /// must spell a density the same way or the picker lists the same thing twice.</para>
    /// </summary>
    public string VariantSuffix => Density switch
    {
        DensityLevel.Most  => "-M",
        DensityLevel.Least => "-L",
        _ => "",
    };

    /// <summary>The canonical spelling, density always stated: <c>smt:0402@N</c>. This is the
    /// generator id, so two densities of one case are two ids and therefore two generated cells
    /// (R-fp1-2b) rather than one that silently serves both.</summary>
    public override string ToString() => $"{Scheme}{Case.Code}@{DensityCode}";

    /// <summary>What a list, a tooltip or a refusal reads — the case's own §1e spelling with the
    /// density named.</summary>
    public string Display => $"{Case.Display}   density {DensityCode}";

    public static string CodeOf(DensityLevel d) => d switch
    {
        DensityLevel.Most  => "M",
        DensityLevel.Least => "L",
        _ => "N",
    };

    /// <summary>True when <paramref name="text"/> claims to be a built-in reference at all — i.e.
    /// whether the <c>smt:</c> discriminator fired. A true here with a failed
    /// <see cref="TryParse"/> is a MALFORMED built-in, which is a refusal; a false is a path, which
    /// is brief 3's business.</summary>
    public static bool IsBuiltInReference(string? text)
        => text is not null && text.AsSpan().TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a built-in footprint reference. Total: returns either a reference or a sentence, never
    /// both and never neither, and never throws (R-fp1-5a).
    /// </summary>
    public static bool TryParse(string? text, out FootprintRef? reference, out string? refusal)
    {
        reference = null;
        refusal = null;

        string s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            refusal = "a footprint reference is empty. A built-in land pattern is spelled " +
                      "smt:<case>[@<density>] — for example smt:0402@N.";
            return false;
        }

        if (!s.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"'{s}' is not a built-in footprint reference. A built-in is spelled " +
                      "smt:<case>[@<density>] — for example smt:0402@N.";
            return false;
        }

        string body = s[Scheme.Length..].Trim();
        if (body.Length == 0)
        {
            refusal = "'smt:' names no case size. " + CodeList();
            return false;
        }

        string casePart = body;
        var density = DefaultDensity;

        int at = body.IndexOf('@');
        if (at >= 0)
        {
            if (body.IndexOf('@', at + 1) >= 0)
            {
                refusal = $"'{s}' states a density more than once. A built-in is spelled " +
                          "smt:<case>[@<density>] — for example smt:0402@N.";
                return false;
            }

            casePart = body[..at].Trim();
            string densityPart = body[(at + 1)..].Trim();
            if (!TryParseDensity(densityPart, out density))
            {
                refusal = densityPart.Length == 0
                    ? $"'{s}' ends in '@' and names no density level. {DensityList()}"
                    : $"'{densityPart}' is not a density level. {DensityList()}";
                return false;
            }
        }

        if (casePart.Length == 0)
        {
            refusal = $"'{s}' names no case size. " + CodeList();
            return false;
        }

        var found = SmtCaseTable.Find(casePart);
        if (found is null)
        {
            refusal = $"'{casePart}' is not a case size circuitRF knows. " + CodeList();
            return false;
        }

        reference = new FootprintRef(found, density);
        return true;
    }

    /// <summary>The reference for a case at a density, with no parsing — for a picker, which has the
    /// case in hand and would otherwise have to spell a string in order to read it back.</summary>
    public static FootprintRef For(SmtCase c, DensityLevel density = DefaultDensity) => new(c, density);

    private static bool TryParseDensity(string text, out DensityLevel density)
    {
        density = DefaultDensity;
        if (text.Length == 0) return false;
        switch (text.ToUpperInvariant())
        {
            case "M": density = DensityLevel.Most;    return true;
            case "N": density = DensityLevel.Nominal; return true;
            case "L": density = DensityLevel.Least;   return true;
            default: return false;
        }
    }

    private static string CodeList()
        => "The case sizes circuitRF generates are: " + string.Join(", ", SmtCaseTable.Codes) + ".";

    private static string DensityList()
        => "IPC-7351B density levels are M (most land protrusion), N (nominal — the default) and " +
           "L (least). Omit '@<density>' for N.";
}
