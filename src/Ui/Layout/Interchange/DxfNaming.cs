// Deterministic cell-name <-> DXF BLOCK/LAYER-name mangling — the same truncate-then-suffix
// collision-resolution algorithm GdsiiStructureNaming already established (src/Ui/CLAUDE.md's own L4a
// note: "only the legality predicate is GDSII-specific — a DXF/Gerber mangler needs only a new
// predicate, not a new algorithm"). GdsiiStructureNaming.Mangle is private to that file, so this is a
// small, deliberate duplication of the ~15-line algorithm behind a DXF-specific legality predicate,
// rather than widening that audited file's visibility.

namespace CircuitRF.Ui.Layout.Interchange;

public static class DxfNaming
{
    /// <summary>DXF symbol-table names (BLOCK, LAYER) are conventionally capped at 255 chars in
    /// modern (post-R14) DXF — generous, but still finite.</summary>
    public const int MaxLength = 255;

    /// <summary>Cell name -> deterministic, collision-free DXF BLOCK name. DXF symbol-table names
    /// disallow <c>&lt; &gt; / \ " : ; ? * | , = `</c> and control characters; spaces are legal.</summary>
    public static IReadOnlyDictionary<string, string> MangleForExport(IReadOnlyList<string> cellNames) =>
        Mangle(cellNames, IsDxfNameLegal);

    /// <summary>DXF BLOCK name -> deterministic, collision-free circuitRF cell name. Uses this
    /// codebase's own filesystem-safe charset (a block name may legally contain characters, like a
    /// comma, that are not safe path components).</summary>
    public static IReadOnlyDictionary<string, string> NameCellsForImport(IReadOnlyList<string> blockNames) =>
        Mangle(blockNames, IsCellNameLegal);

    private static IReadOnlyDictionary<string, string> Mangle(IReadOnlyList<string> names, Func<char, bool> isLegal)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    private static bool IsDxfNameLegal(char c) =>
        c > 0x1F && c is not ('<' or '>' or '/' or '\\' or '"' or ':' or ';' or '?' or '*' or '|' or ',' or '=' or '`');

    private static bool IsCellNameLegal(char c) =>
        c > 0x1F && c is not ('<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*');
}
