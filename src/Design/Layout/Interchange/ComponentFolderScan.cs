// Scans a folder for the files a component import can use, and reports what it found
// (docs/sonnet-briefs/brief-PL1-component-library-import.md R-PL1-4).
//
// Walks the tree recursively, classifies every file by CONTENT (ComponentClassifier), and returns one
// ranked list of candidates plus a count of what was not readable.
//
// Candidates are grouped by FORMAT FAMILY rather than by directory: a symbol file and the footprint
// files it pairs with may sit in different folders, and grouping by directory would split one
// importable component into two incomplete candidates.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>How much of a component one candidate can produce. Candidates are ranked by this first, so
/// one carrying the pin↔pad map sorts above one that carries only artwork.</summary>
public enum ComponentCompleteness
{
    /// <summary>Symbol, footprint and the map between them — everything a placed part needs.</summary>
    SymbolFootprintAndMap = 0,

    /// <summary>Artwork with pads and no schematic side.</summary>
    FootprintOnly = 1,

    /// <summary>A schematic symbol with no land pattern.</summary>
    SymbolOnly = 2,
}

/// <summary>One file the scan found.</summary>
public sealed record ComponentFile(string Path, ComponentFileKind Kind)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// One importable option, as offered to the user.
/// </summary>
/// <param name="Description">The completeness, in words.</param>
/// <param name="FormatSummary">What it is made of, by extension and count.</param>
public sealed record ComponentCandidate(
    ComponentCompleteness Completeness,
    string Description,
    string FormatSummary,
    IReadOnlyList<ComponentFile> Files)
{
    /// <summary>The symbol file, when this candidate has one.</summary>
    public ComponentFile? SymbolFile => Files.FirstOrDefault(f =>
        f.Kind is ComponentFileKind.SymbolSexpr or ComponentFileKind.SymbolLegacyText or ComponentFileKind.LibraryXml);

    public IReadOnlyList<ComponentFile> FootprintFiles =>
        [.. Files.Where(f => f.Kind == ComponentFileKind.FootprintSexpr)];

    public override string ToString() => $"{Description}   ({FormatSummary})";
}

/// <summary>What the scan found, ready to be shown.</summary>
/// <param name="Candidates">Ranked, best first. The first is preselected.</param>
/// <param name="SkippedSummary">One phrase per category of file that was NOT read, with a count —
/// "4 binary formats", "1 three-dimensional model" (R-PL1-4).</param>
/// <param name="FilesScanned">Counter, never wall clock.</param>
public sealed record ComponentScanResult(
    IReadOnlyList<ComponentCandidate> Candidates,
    IReadOnlyList<string> SkippedSummary,
    int FilesScanned)
{
    public bool Any => Candidates.Count > 0;
}

public static class ComponentFolderScan
{
    /// <summary>How deep the walk descends. Bounded so a folder chosen by mistake (a home directory)
    /// cannot turn a scan into a filesystem crawl.</summary>
    public const int MaxDepth = 8;

    /// <summary>And the ceiling on how many files are looked at at all, for the same reason.</summary>
    public const int MaxFiles = 20_000;

    /// <summary>
    /// Walks <paramref name="root"/> — a folder or a single file — and returns the ranked list.
    /// </summary>
    public static ComponentScanResult Scan(string root)
    {
        var files = new List<ComponentFile>();
        if (File.Exists(root))
        {
            files.Add(new ComponentFile(root, ComponentClassifier.Classify(root)));
        }
        else if (Directory.Exists(root))
        {
            foreach (var path in Walk(root, 0).Take(MaxFiles))
                files.Add(new ComponentFile(path, ComponentClassifier.Classify(path)));
        }

        return new ComponentScanResult(Rank(files), Summarize(files), files.Count);
    }

    /// <summary>The ranking, over an already-classified file list — the part a test drives directly.</summary>
    public static IReadOnlyList<ComponentCandidate> Rank(IReadOnlyList<ComponentFile> files)
    {
        var symbolsSexpr = Of(files, ComponentFileKind.SymbolSexpr);
        var symbolsLegacy = Of(files, ComponentFileKind.SymbolLegacyText);
        var libraries = Of(files, ComponentFileKind.LibraryXml);
        var footprints = Of(files, ComponentFileKind.FootprintSexpr);

        var ranked = new List<(int Tier, int Confidence, ComponentCandidate Candidate)>();

        // ── The complete options, best reader first ─────────────────────────────────────────────
        //
        // Within one completeness tier the tie-break is reader confidence, lowest number first: the
        // S-expression symbol pairs with L4d's own footprint reader, the XML library carries both
        // halves through this phase's reader, and the older text symbol format pairs its own line
        // reader with L4d's footprint reader.
        foreach (var symbol in symbolsSexpr)
            ranked.Add(footprints.Count > 0
                ? (0, 0, Complete(symbol, footprints))
                : (2, 0, SymbolOnly(symbol)));

        foreach (var library in libraries)
            ranked.Add((0, 1, Complete(library, [])));

        foreach (var symbol in symbolsLegacy)
            ranked.Add(footprints.Count > 0
                ? (0, 2, Complete(symbol, footprints))
                : (2, 2, SymbolOnly(symbol)));

        // ── And the footprints on their own, once ───────────────────────────────────────────────
        if (footprints.Count > 0)
            ranked.Add((1, 0, new ComponentCandidate(
                ComponentCompleteness.FootprintOnly,
                "footprint only",
                Summary(footprints),
                footprints)));

        return
        [
            .. ranked
                .Select((r, i) => (r.Tier, r.Confidence, Index: i, r.Candidate))
                .OrderBy(r => r.Tier).ThenBy(r => r.Confidence).ThenBy(r => r.Index)
                .Select(r => r.Candidate),
        ];
    }

    private static ComponentCandidate Complete(ComponentFile symbol, IReadOnlyList<ComponentFile> footprints)
        => new(ComponentCompleteness.SymbolFootprintAndMap,
               "symbol + footprint + pin map",
               Summary([symbol, .. footprints]),
               [symbol, .. footprints]);

    private static ComponentCandidate SymbolOnly(ComponentFile symbol)
        => new(ComponentCompleteness.SymbolOnly, "symbol only", Summary([symbol]), [symbol]);

    /// <summary>"<c>.kicad_sym, .kicad_mod ×3</c>" — by extension and count, in the order the kinds
    /// were found. Counted rather than listed, so a candidate holding many files stays one short
    /// line.</summary>
    private static string Summary(IReadOnlyList<ComponentFile> files)
    {
        var counts = new List<(string Ext, int Count)>();
        foreach (var f in files)
        {
            string ext = Path.GetExtension(f.Path).ToLowerInvariant();
            if (ext.Length == 0) ext = "(no extension)";
            int i = counts.FindIndex(c => c.Ext == ext);
            if (i < 0) counts.Add((ext, 1));
            else counts[i] = (ext, counts[i].Count + 1);
        }
        return string.Join(", ", counts.Select(c => c.Count == 1 ? c.Ext : $"{c.Ext} ×{c.Count}"));
    }

    /// <summary>
    /// What was NOT read, by category with a count and a reason.
    ///
    /// <para>A dimensioned drawing (DXF, Gerber) is listed here rather than offered as a candidate
    /// (R-PL1-30). circuitRF reads those formats elsewhere, but a drawing states no pad identifiers and
    /// no pin names, so it cannot supply the pin↔pad map this import is built around — the phrase says
    /// exactly that.</para>
    /// </summary>
    public static IReadOnlyList<string> Summarize(IReadOnlyList<ComponentFile> files)
    {
        var lines = new List<string>();
        void Say(ComponentFileKind kind, string singular, string plural)
        {
            int n = files.Count(f => f.Kind == kind);
            if (n > 0) lines.Add($"{n} {(n == 1 ? singular : plural)}");
        }

        Say(ComponentFileKind.Binary, "binary format", "binary formats");
        Say(ComponentFileKind.Model3D, "three-dimensional model", "three-dimensional models");
        Say(ComponentFileKind.Drawing, "dimensioned drawing (no pad identifiers or pin names in it)",
                                       "dimensioned drawings (no pad identifiers or pin names in them)");
        Say(ComponentFileKind.Board, "board file (File ▸ Import ▸ Board… reads it)",
                                     "board files (File ▸ Import ▸ Board… reads them)");
        Say(ComponentFileKind.UnreadableText, "text format circuitRF has no reader for",
                                              "text formats circuitRF has no reader for");
        return lines;
    }

    private static List<ComponentFile> Of(IReadOnlyList<ComponentFile> files, ComponentFileKind kind)
        => [.. files.Where(f => f.Kind == kind).OrderBy(f => f.Path, StringComparer.Ordinal)];

    private static IEnumerable<string> Walk(string dir, int depth)
    {
        if (depth > MaxDepth) yield break;

        string[] entries;
        try { entries = Directory.GetFiles(dir); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        Array.Sort(entries, StringComparer.Ordinal);
        foreach (var f in entries) yield return f;

        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        Array.Sort(subs, StringComparer.Ordinal);
        foreach (var sub in subs)
            foreach (var f in Walk(sub, depth + 1))
                yield return f;
    }
}
