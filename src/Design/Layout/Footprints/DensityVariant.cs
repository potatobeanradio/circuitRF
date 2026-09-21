// The ONE density-suffix vocabulary — brief-footprint-4-picker-and-import.md R-fp4-2c.
//
// Three places spelled a density level before this file existed: FootprintRef.VariantSuffix
// (-M / "" / -L, for a GENERATED pattern), ComponentRead.DensitySuffixes and
// ComponentRecordsReader.SplitVariant (both of which also accept the underscore spelling a decal
// NAME carries, for an IMPORTED one). Three spellings of one idea is how an imported nominal
// pattern and a generated nominal pattern come to sort into two different rows of one picker
// reading the same word — so the strings live here and those three read them.
//
// THE UNDERSCORE SPELLING IS AN INPUT, NEVER AN OUTPUT. A reader that meets `PART_M` keeps the
// name it was given, because the view file it writes is named after it and renaming somebody's
// artwork on import is not this feature's business. What the CATALOG does is normalise before it
// LABELS, which is the only place the two spellings had to agree.

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// The cell-view suffix that marks a land pattern's IPC-7351B density level, and the translation
/// between that suffix and <see cref="DensityLevel"/>.
/// </summary>
public static class DensityVariant
{
    /// <summary>The nominal pattern carries NO suffix, and that absence is the spelling — it is what
    /// <c>ComponentImport</c> writes for the primary view and what
    /// <see cref="FootprintRef.VariantSuffix"/> returns for <see cref="DensityLevel.Nominal"/>.</summary>
    public const string Nominal = "";

    /// <summary>Most land protrusion.</summary>
    public const string Most = "-M";

    /// <summary>Least land protrusion.</summary>
    public const string Least = "-L";

    /// <summary>Every suffix a READER may meet, canonical spellings first. The underscore forms are
    /// the same two levels as a decal name writes them; nothing emits them.</summary>
    public static readonly string[] Suffixes = [Most, Least, "_M", "_L"];

    /// <summary>The suffix a generated view of <paramref name="density"/> carries.</summary>
    public static string SuffixOf(DensityLevel density) => density switch
    {
        DensityLevel.Most  => Most,
        DensityLevel.Least => Least,
        _                  => Nominal,
    };

    /// <summary>
    /// The level a stored suffix names — the ONE reading, so <c>-M</c> and <c>_M</c> cannot end up
    /// in two rows. Anything unrecognised is <see cref="DensityLevel.Nominal"/>, which is what an
    /// absent suffix already means.
    /// </summary>
    public static DensityLevel LevelOf(string? variant)
    {
        string s = (variant ?? "").Trim();
        if (s.Length == 0) return DensityLevel.Nominal;
        if (s[0] is '-' or '_') s = s[1..];
        return s.ToUpperInvariant() switch
        {
            "M" => DensityLevel.Most,
            "L" => DensityLevel.Least,
            _   => DensityLevel.Nominal,
        };
    }

    /// <summary>A stored suffix in the canonical spelling — <c>_M</c> reads back as <c>-M</c>.</summary>
    public static string Canonical(string? variant) => SuffixOf(LevelOf(variant));

    /// <summary>What a picker row, a report line and a tooltip all read for a density level. One
    /// wording, because a list that says "nominal" in one row and "N" in the next reads as two
    /// different things.</summary>
    public static string Label(DensityLevel density) => density switch
    {
        DensityLevel.Most  => "density M (most)",
        DensityLevel.Least => "density L (least)",
        _                  => "density N (nominal)",
    };
}
