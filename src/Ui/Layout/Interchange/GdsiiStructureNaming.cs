// Deterministic cell-name <-> GDSII structure-name mangling (§8: "Structure names: the spec's limit
// is short (§8 notes 200 chars). Cell names must be mangled deterministically, collisions resolved,
// and the mapping reported so a user can trace a fab's structure name back to their cell."). Used by
// both the writer (cell name -> structure name, GDSII's own legal charset) and the reader (structure
// name -> circuitRF cell name, this codebase's NameValidator charset) — shared collision-resolution
// logic, two different legality predicates.

using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Interchange;

public static class GdsiiStructureNaming
{
    /// <summary>The spec's practical structure-name limit, as this brief documents it (§8).</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Cell name → deterministic, collision-free GDSII structure name. GDSII's legal charset is
    /// letters, digits, underscore, question mark, and dollar sign; any other character is replaced
    /// with '_'. A collision is resolved by truncating the sanitized name to make room for a numeric
    /// suffix (<c>_2</c>, <c>_3</c>, …) — never by silently dropping one of the two cells.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MangleForExport(IReadOnlyList<string> cellNames)
        => Mangle(cellNames, IsGdsiiLegal);

    /// <summary>
    /// GDSII structure name → deterministic, collision-free circuitRF cell name. Uses this codebase's
    /// own <see cref="NameValidator"/> charset (a structure name may legally contain characters that
    /// are not safe filesystem path components, e.g. '?').
    /// </summary>
    public static IReadOnlyDictionary<string, string> NameCellsForImport(IReadOnlyList<string> structureNames)
        => Mangle(structureNames, IsCellNameLegal);

    private static IReadOnlyDictionary<string, string> Mangle(IReadOnlyList<string> names, Func<char, bool> isLegal)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var map = new Dictionary<string, string>(names.Count);

        foreach (var original in names)
        {
            var sanitized = Sanitize(original, isLegal);
            var candidate = sanitized;
            int suffixNum = 2;
            while (!used.Add(candidate))
            {
                var suffix = $"_{suffixNum}";
                int baseLen = Math.Min(sanitized.Length, MaxLength - suffix.Length);
                candidate = string.Concat(sanitized.AsSpan(0, Math.Max(baseLen, 0)), suffix);
                suffixNum++;
            }
            map[original] = candidate;
        }
        return map;
    }

    private static string Sanitize(string name, Func<char, bool> isLegal)
    {
        var chars = new char[Math.Min(name.Length, MaxLength)];
        int n = 0;
        foreach (var c in name)
        {
            if (n >= chars.Length) break;
            chars[n++] = isLegal(c) ? c : '_';
        }
        return n == 0 ? "_" : new string(chars, 0, n);
    }

    private static bool IsGdsiiLegal(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '_' or '?' or '$';

    private static bool IsCellNameLegal(char c) =>
        c > 0x1F && c is not ('<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*');
}
