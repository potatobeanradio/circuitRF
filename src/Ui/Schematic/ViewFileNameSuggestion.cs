namespace CircuitRF.Ui.Schematic;

/// <summary>
/// brief-cell-first-and-ui-fixes.md §3 (R-cc-3): the default name offered when creating a new view
/// file (schematic/symbol/layout) inside a cell — the cell's own name, or the next free bare-numeral
/// suffix (<c>Amp</c>, <c>Amp2</c>, <c>Amp3</c>, …) when that's already taken.
///
/// <b>One convention, never mixed (R-cc-3's own explicit instruction): bare numerals, always,
/// including when the cell's own name already ends in a digit.</b> A cell named <c>Amp2</c> suggests
/// <c>Amp2</c> first, then <c>Amp22</c>, then <c>Amp23</c> — never <c>Amp2_2</c> (a second, underscore
/// convention) and never <c>Amp3</c> (which would rename the base itself rather than suffix it,
/// and would misleadingly read as an unrelated cell's own name). This isn't a special case at all —
/// it's the same "append the next integer, as a bare string" rule applied uniformly; letting it run
/// without a digit-boundary special case is what keeps it unambiguous, since each candidate string is
/// checked directly against the files that actually exist rather than parsed back apart.
/// </summary>
public static class ViewFileNameSuggestion
{
    /// <summary>
    /// Scans every existing file of <paramref name="viewType"/> in the cell — not just its resolved
    /// primary — and returns the cell's own name if free, else the lowest-numbered bare-numeral
    /// suffix not already taken.
    /// </summary>
    public static string Suggest(string cellDir, string cellName, ViewType viewType)
    {
        string subFolder = CellFolder.SubFolderPath(cellDir, viewType);
        string ext       = CellFolder.ViewExtension(viewType);

        var existingStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(subFolder))
        {
            foreach (var file in Directory.GetFiles(subFolder, $"*{ext}"))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem is not null) existingStems.Add(stem);
            }
        }

        if (!existingStems.Contains(cellName)) return cellName;

        for (int n = 2; ; n++)
        {
            var candidate = $"{cellName}{n}";
            if (!existingStems.Contains(candidate)) return candidate;
        }
    }
}
