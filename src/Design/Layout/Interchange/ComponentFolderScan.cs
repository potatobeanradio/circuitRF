// Scans a folder for the files a component import can use, and reports what it found
// (docs/sonnet-briefs/brief-PL1-component-library-import.md R-PL1-4).
//
// Walks the tree recursively, classifies every file by CONTENT (ComponentClassifier), and returns one
// ranked list of candidates plus a count of what was not readable.
//
// ── What a candidate may be made of, and what it may NOT ─────────────────────────────────────────
//
// A candidate is ONE component. A folder chosen for import routinely holds several — a library folder
// of parts, or one part written out once per target format in sibling folders — and files from two of
// those are never one component, however well their kinds happen to complement each other. Ranking the
// whole tree as a single pool merges them: a symbol from one folder pairs with every footprint in the
// tree, and a multi-file set reads the first file of each kind and silently drops the rest.
//
// So the tree is split into GROUPS first (Group), and each group is ranked on its own.
//
// The split is not one group per directory, because a symbol and the footprints it pairs with genuinely
// do sit in different folders — the S-expression pair keeps its land patterns in a child folder of the
// one holding the symbol. The rule instead: a directory that directly holds a file which can BEGIN a
// component — anything but a standalone footprint — is a group root, and every other file joins the
// nearest group root above it. A footprint child folder therefore joins its symbol's folder, while two
// sibling format folders stay two groups.

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
/// Which reader a candidate belongs to.
///
/// <para>PL1 could infer this from the symbol file's own kind, because each of its formats was one
/// file plus optional footprints. Three of PL2's are multi-FILE sets whose members are separate kinds
/// (<c>ComponentFileKind.HkpParts</c> and <c>HkpSymbols</c> are one candidate), so the family is
/// stated on the candidate rather than re-derived from its members at every use.</para>
/// </summary>
public enum ComponentFormatFamily
{
    /// <summary>PL1's own formats, read through their existing per-file readers.</summary>
    Pl1,

    /// <summary>The <c>.p</c>/<c>.d</c>/<c>.c</c> triple.</summary>
    Records,

    /// <summary>The <c>.hkp</c> set — up to four files, two grammars.</summary>
    Hkp,

    /// <summary>The <c>.PLX</c>/<c>.DSL</c> dialect.</summary>
    Plx,

    /// <summary>The flat <c>.cxf</c> library.</summary>
    Cxf,

    /// <summary>The <c>.scr</c> command script.</summary>
    Script,
}

/// <summary>
/// One importable option, as offered to the user.
/// </summary>
/// <param name="Description">The completeness, in words.</param>
/// <param name="FormatSummary">What it is made of, by extension and count.</param>
/// <param name="Location">The group's folder, relative to the scanned root — empty when the group IS
/// the root. A folder holding one part written out once per target format produces one candidate per
/// format, and they differ only in where they came from, so the list has to say.</param>
public sealed record ComponentCandidate(
    ComponentCompleteness Completeness,
    string Description,
    string FormatSummary,
    IReadOnlyList<ComponentFile> Files,
    ComponentFormatFamily Family = ComponentFormatFamily.Pl1,
    string Location = "")
{
    /// <summary>The one file of <paramref name="kind"/> this candidate holds, if any.</summary>
    public ComponentFile? Of(ComponentFileKind kind) => Files.FirstOrDefault(f => f.Kind == kind);

    /// <summary>The symbol file, when this candidate has one.</summary>
    public ComponentFile? SymbolFile => Files.FirstOrDefault(f =>
        f.Kind is ComponentFileKind.SymbolSexpr or ComponentFileKind.SymbolLegacyText or ComponentFileKind.LibraryXml);

    public IReadOnlyList<ComponentFile> FootprintFiles =>
        [.. Files.Where(f => f.Kind == ComponentFileKind.FootprintSexpr)];

    public override string ToString() => Location.Length == 0
        ? $"{Description}   ({FormatSummary})"
        : $"{Description}   ({FormatSummary})   in {Location}";
}

/// <summary>What the scan found, ready to be shown.</summary>
/// <param name="Candidates">Ranked, best first. The first is preselected.</param>
/// <param name="SkippedSummary">One phrase per category of file that was NOT read, with a count —
/// "4 binary formats", "1 three-dimensional model" (R-PL1-4).</param>
/// <param name="FilesScanned">Counter, never wall clock.</param>
/// <param name="Truncated">The walk hit one of its own ceilings and did not see the whole tree —
/// <see cref="ComponentFolderScan.MaxFiles"/> or <see cref="ComponentFolderScan.MaxDepth"/>. A capped
/// scan that reports nothing is indistinguishable from a folder that holds nothing, so it is said.</param>
public sealed record ComponentScanResult(
    IReadOnlyList<ComponentCandidate> Candidates,
    IReadOnlyList<string> SkippedSummary,
    int FilesScanned,
    bool Truncated = false)
{
    public bool Any => Candidates.Count > 0;

    /// <summary>The one sentence a truncated scan is reported with, or null when it saw everything.</summary>
    public string? TruncationNote => Truncated
        ? $"This folder is larger than a component library: the scan stopped after {FilesScanned:N0} " +
          $"file(s) and {ComponentFolderScan.MaxDepth} level(s) of subfolder, so what is listed may not " +
          "be all of it. Point the import at the folder holding the part rather than at one above it."
        : null;
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
    /// <param name="token">
    /// Answered between files. The ceilings above bound the walk, but they bound it at twenty thousand
    /// OPENS: on a network share that is minutes, and a scan the user started by mistake has to be
    /// stoppable rather than merely finite.
    /// </param>
    /// <param name="onProgress">Files classified so far, called periodically. A counter, never a clock.</param>
    public static ComponentScanResult Scan(
        string root, CancellationToken token = default, Action<int>? onProgress = null)
    {
        var files = new List<ComponentFile>();
        bool truncated = false;

        if (File.Exists(root))
        {
            files.Add(new ComponentFile(root, ComponentClassifier.Classify(root)));
        }
        else if (Directory.Exists(root))
        {
            var walk = new WalkState();
            foreach (var path in Walk(root, 0, walk))
            {
                token.ThrowIfCancellationRequested();
                if (files.Count >= MaxFiles) { truncated = true; break; }
                files.Add(new ComponentFile(path, ComponentClassifier.Classify(path)));
                if (files.Count % 250 == 0) onProgress?.Invoke(files.Count);
            }
            truncated |= walk.DepthCapped;
        }

        return new ComponentScanResult(RankGrouped(root, files), Summarize(files), files.Count, truncated);
    }

    /// <summary>
    /// The ranking, one group at a time — see this file's header for why a group is not a directory and
    /// not the whole tree.
    /// </summary>
    /// <para>Groups keep their candidates together and are ordered by the best candidate in each, so a
    /// folder holding one part in eight formats reads as eight blocks rather than as an interleaving of
    /// them.</para>
    public static IReadOnlyList<ComponentCandidate> RankGrouped(string root, IReadOnlyList<ComponentFile> files)
    {
        var component = files.Where(f => IsComponentKind(f.Kind)).ToList();
        if (component.Count == 0) return [];

        // A directory that DIRECTLY holds a file which can begin a component. A standalone footprint
        // cannot: its folder is routinely a child of the one holding the symbol it belongs to.
        var groupRoots = new HashSet<string>(
            component.Where(f => BeginsAComponent(f.Kind)).Select(DirOf), PathComparer);

        var groups = new List<(string Dir, List<ComponentFile> Files)>();
        foreach (var f in component)
        {
            string key = NearestGroupRoot(DirOf(f), groupRoots) ?? DirOf(f);
            int i = groups.FindIndex(g => string.Equals(g.Dir, key, PathComparison));
            if (i < 0) { groups.Add((key, [f])); }
            else groups[i].Files.Add(f);
        }

        string rootDir = Directory.Exists(root) ? root : DirOf(root) ?? root;

        return
        [
            .. groups
                .Select(g => (Scored: RankScored(g.Files), g.Dir))
                .Where(g => g.Scored.Count > 0)
                .OrderBy(g => g.Scored[0].Tier)
                .ThenBy(g => g.Scored[0].Confidence)
                .ThenBy(g => g.Dir, StringComparer.Ordinal)
                .SelectMany(g => g.Scored.Select(r => r.Candidate with { Location = Label(rootDir, g.Dir) })),
        ];
    }

    /// <summary>The group folder as the chooser shows it: relative to the scanned root, and empty when
    /// the group IS the root.</summary>
    private static string Label(string root, string dir)
    {
        if (string.Equals(root, dir, PathComparison)) return "";
        try
        {
            string rel = Path.GetRelativePath(root, dir);
            return rel is "." ? "" : rel;
        }
        catch (ArgumentException) { return Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)); }
    }

    private static string? NearestGroupRoot(string dir, HashSet<string> roots)
    {
        for (string? d = dir; d is { Length: > 0 }; d = Path.GetDirectoryName(d))
            if (roots.Contains(d)) return d;
        return null;
    }

    private static string DirOf(ComponentFile f) => DirOf(f.Path);

    private static string DirOf(string path) => Path.GetDirectoryName(path) ?? path;

    /// <summary>Case-insensitive on the two platforms whose filesystems are, so a group root reached by
    /// a differently-cased ancestor is still the same group.</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Every kind <see cref="Rank"/> can build a candidate out of.</summary>
    private static bool IsComponentKind(ComponentFileKind kind) => kind
        is ComponentFileKind.SymbolSexpr or ComponentFileKind.SymbolLegacyText or ComponentFileKind.LibraryXml
        or ComponentFileKind.FootprintSexpr
        or ComponentFileKind.PartRecords or ComponentFileKind.FootprintRecords or ComponentFileKind.SymbolRecords
        or ComponentFileKind.HkpParts or ComponentFileKind.HkpCells or ComponentFileKind.HkpPadstacks
        or ComponentFileKind.HkpSymbols
        or ComponentFileKind.PlxLibrary or ComponentFileKind.CxfLibrary or ComponentFileKind.ScriptLibrary;

    /// <summary>Every component kind but the standalone footprint, which only ever completes one.</summary>
    private static bool BeginsAComponent(ComponentFileKind kind)
        => IsComponentKind(kind) && kind != ComponentFileKind.FootprintSexpr;

    /// <summary>The ranking, over an already-classified file list — the part a test drives directly.</summary>
    public static IReadOnlyList<ComponentCandidate> Rank(IReadOnlyList<ComponentFile> files)
        => [.. RankScored(files).Select(r => r.Candidate)];

    /// <summary>The same, with the sort keys kept: <see cref="RankGrouped"/> orders GROUPS by the best
    /// candidate in each, and "best" is the same completeness-then-reader-confidence pair used within
    /// one. Ordering groups by path instead would put whichever format folder sorts first at the top of
    /// the list and preselect it (R-PL1-4).</summary>
    private static IReadOnlyList<(int Tier, int Confidence, ComponentCandidate Candidate)> RankScored(
        IReadOnlyList<ComponentFile> files)
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

        // ── PL2's formats, after PL1's ──────────────────────────────────────────────────────────
        //
        // Ranked below PL1's on reader maturity, which is the same ground PL1's own internal order
        // rests on — and honest about what it is: a claim about readers, not about files.
        RankPl2(files, ranked);

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
                .Select(r => (r.Tier, r.Confidence, r.Candidate)),
        ];
    }

    /// <summary>
    /// PL2's five families. Each produces at most one candidate per SET of files, because a set is one
    /// importable component — the <c>.hkp</c> set's four files and the triple's three are not four and
    /// three separate options.
    /// </summary>
    private static void RankPl2(
        IReadOnlyList<ComponentFile> files, List<(int Tier, int Confidence, ComponentCandidate Candidate)> ranked)
    {
        // ── The `.p`/`.d`/`.c` triple ───────────────────────────────────────────────────────────
        var parts = Of(files, ComponentFileKind.PartRecords);
        var decals = Of(files, ComponentFileKind.FootprintRecords);
        var schematics = Of(files, ComponentFileKind.SymbolRecords);
        if (parts.Count > 0 || decals.Count > 0)
        {
            List<ComponentFile> set = [.. parts, .. decals, .. schematics];
            var completeness = Completeness(schematics.Count > 0, decals.Count > 0);
            ranked.Add(((int)completeness, 3,
                new ComponentCandidate(
                    completeness,
                    Describe(schematics.Count > 0, decals.Count > 0),
                    Summary(set), set, ComponentFormatFamily.Records)));
        }

        // ── The `.hkp` set — four files, two grammars, one candidate (R-PL2-6) ───────────────────
        var hkpParts = Of(files, ComponentFileKind.HkpParts);
        var hkpCells = Of(files, ComponentFileKind.HkpCells);
        var hkpPads = Of(files, ComponentFileKind.HkpPadstacks);
        var hkpSymbols = Of(files, ComponentFileKind.HkpSymbols);
        if (hkpParts.Count > 0 || hkpCells.Count > 0 || hkpSymbols.Count > 0)
        {
            List<ComponentFile> set = [.. hkpParts, .. hkpCells, .. hkpPads, .. hkpSymbols];
            ranked.Add((
                (int)Completeness(hkpSymbols.Count > 0, hkpCells.Count > 0),
                4,
                new ComponentCandidate(
                    Completeness(hkpSymbols.Count > 0, hkpCells.Count > 0),
                    Describe(hkpSymbols.Count > 0, hkpCells.Count > 0),
                    Summary(set), set, ComponentFormatFamily.Hkp)));
        }

        // ── The single-file formats ─────────────────────────────────────────────────────────────
        AddSingles(files, ComponentFileKind.PlxLibrary, ComponentFormatFamily.Plx, 5, ranked);
        AddSingles(files, ComponentFileKind.CxfLibrary, ComponentFormatFamily.Cxf, 6, ranked);
        AddSingles(files, ComponentFileKind.ScriptLibrary, ComponentFormatFamily.Script, 7, ranked);
    }

    private static void AddSingles(
        IReadOnlyList<ComponentFile> files,
        ComponentFileKind kind,
        ComponentFormatFamily family,
        int confidence,
        List<(int Tier, int Confidence, ComponentCandidate Candidate)> ranked)
    {
        foreach (var file in Of(files, kind))
            ranked.Add(((int)ComponentCompleteness.SymbolFootprintAndMap, confidence,
                new ComponentCandidate(
                    ComponentCompleteness.SymbolFootprintAndMap,
                    "symbol + footprint + pin map",
                    Summary([file]), [file], family)));
    }

    private static ComponentCompleteness Completeness(bool hasSymbol, bool hasFootprint)
        => hasSymbol && hasFootprint ? ComponentCompleteness.SymbolFootprintAndMap
         : hasFootprint ? ComponentCompleteness.FootprintOnly
         : ComponentCompleteness.SymbolOnly;

    private static string Describe(bool hasSymbol, bool hasFootprint)
        => hasSymbol && hasFootprint ? "symbol + footprint + pin map"
         : hasFootprint ? "footprint only"
         : "symbol only";

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

    /// <summary>Whether the walk stopped short of the whole tree, so <see cref="Scan"/> can say so. An
    /// iterator cannot take a <c>ref</c>, which is the only reason this is a class.</summary>
    private sealed class WalkState { public bool DepthCapped; }

    private static IEnumerable<string> Walk(string dir, int depth, WalkState state)
    {
        if (depth > MaxDepth) { state.DepthCapped = true; yield break; }

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
            foreach (var f in Walk(sub, depth + 1, state))
                yield return f;
    }
}
