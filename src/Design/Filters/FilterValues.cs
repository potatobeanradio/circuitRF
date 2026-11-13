using CircuitRF.Core.Systems;
using CircuitRF.Design.Matching;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Filters;

/// <summary>Which preferred-number ladder a value is snapped onto. Exact leaves it alone.</summary>
public enum FilterSeriesKind
{
    /// <summary>The theoretical value.</summary>
    Exact,
    /// <summary>IEC 60063, six values a decade.</summary>
    E6,
    /// <summary>IEC 60063, twelve values a decade. The ladder the Smith chart ships.</summary>
    E12,
    /// <summary>IEC 60063, twenty-four values a decade.</summary>
    E24,
    /// <summary>IEC 60063, ninety-six values a decade.</summary>
    E96,
}

/// <summary>One part a library series offers, in base SI.</summary>
public sealed record FilterPart(string Name, double Value);

/// <summary>
/// One series of a vendor library: the parts of one file that are inductors, or the parts that are
/// capacitors. A series is that file, which is how a placed part's own "same kind" list is cut.
/// </summary>
public sealed record FilterLibrarySeries(
    string Library,
    string File,
    string Label,
    bool Inductors,
    IReadOnlyList<FilterPart> Parts);

/// <summary>
/// The three value columns. Exact is the ladder. An E-series is <see cref="PreferredValues"/>, the
/// same snap the Smith chart and the optimizer use, nearest by ratio. A library series is the
/// nearest stated value in a file of real parts.
/// </summary>
public static class FilterValues
{
    /// <summary>The value <paramref name="exact"/> snaps to, or itself for <see cref="FilterSeriesKind.Exact"/>.</summary>
    public static double Snap(bool inductor, double exact, FilterSeriesKind kind)
    {
        if (kind == FilterSeriesKind.Exact) return exact;
        var mantissas = kind switch
        {
            FilterSeriesKind.E6  => PreferredValues.E6,
            FilterSeriesKind.E12 => PreferredValues.E12,
            FilterSeriesKind.E24 => PreferredValues.E24,
            FilterSeriesKind.E96 => PreferredValues.E96,
            _ => PreferredValues.E12,
        };
        // The same spans the shipped ladders use: 0.1 nH…100 µH, 0.1 pF…100 nF.
        var ladder = PreferredValues.Series(mantissas, inductor ? -10 : -13, inductor ? -5 : -8);
        return PreferredValues.Snap(exact, ladder);
    }

    /// <summary>
    /// The part whose stated value is nearest <paramref name="exact"/>, nearest by ratio. Null when
    /// the series has no positive value.
    /// </summary>
    public static FilterPart? Nearest(double exact, IReadOnlyList<FilterPart> parts)
    {
        if (!(exact > 0) || parts.Count == 0) return null;
        FilterPart? chosen = null;
        double bestRatio = double.PositiveInfinity;
        foreach (var p in parts)
        {
            if (!(p.Value > 0)) continue;
            double ratio = Math.Abs(Math.Log(p.Value / exact));
            if (ratio < bestRatio)
            {
                bestRatio = ratio;
                chosen = p;
            }
        }
        return chosen;
    }

    /// <summary>
    /// The inductor series and the capacitor series in the vendor libraries a workspace names.
    /// A library that does not index is skipped and named in <paramref name="notes"/>.
    /// </summary>
    public static IReadOnlyList<FilterLibrarySeries> LibrarySeries(string? workspaceRoot, List<string> notes)
    {
        var found = new List<FilterLibrarySeries>();
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return found;

        string rootDir = Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : Path.GetDirectoryName(workspaceRoot) ?? "";
        if (rootDir.Length == 0) return found;

        string? cws = Directory.EnumerateFiles(rootDir, "*.cws").FirstOrDefault();
        if (cws is null) return found;

        CwsFile ws;
        try { ws = WorkspacePersistence.LoadFromFile(cws); }
        catch (Exception e)
        {
            notes.Add($"The workspace could not be read: {e.Message}");
            return found;
        }

        foreach (var lib in ws.VendorLibraries ?? [])
        {
            string? libRoot = VendorLibraryRefs.ResolveLibraryRoot(rootDir, lib.Name, out string? why);
            if (libRoot is null)
            {
                notes.Add(why ?? $"Vendor library '{lib.Name}' could not be resolved.");
                continue;
            }
            var build = VendorLibraryIndex.Build(libRoot);
            if (!build.IsUsable)
            {
                notes.Add($"Vendor library '{lib.Name}' was not indexed" +
                          (build.Refusal is { } r ? $": {r}" : "."));
                continue;
            }
            foreach (var group in build.Entries.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
            {
                Add(lib.Name, group.Key, group, VendorPartQuantity.Inductance, inductors: true);
                Add(lib.Name, group.Key, group, VendorPartQuantity.Capacitance, inductors: false);
            }

            void Add(string library, string file, IEnumerable<VendorLibraryEntry> entries,
                     VendorPartQuantity quantity, bool inductors)
            {
                var parts = entries
                    .Where(e => e.Header.Quantity == quantity && e.Header.Value is > 0)
                    .Select(e => new FilterPart(e.Part, e.Header.Value!.Value))
                    .OrderBy(p => p.Value)
                    .ToList();
                if (parts.Count == 0) return;
                string stem = Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/').Last());
                found.Add(new FilterLibrarySeries(
                    library, file, $"{library} / {stem}", inductors, parts));
            }
        }
        return found;
    }
}
