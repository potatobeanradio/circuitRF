// Reads one CANDIDATE — the set of files chosen from ComponentFolderScan's ranked list — into a single
// ComponentPart (docs/sonnet-briefs/brief-PL1-component-library-import.md §§3, 8).
//
// Reads only: no filesystem writes, no CellFolder, no Technology and no Messages. ComponentImport does
// all of those.

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentRead
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    /// <summary>The file-name suffixes that mark a density level of a land pattern rather than a
    /// separate pattern (R-PL1-25). A name carrying none of them is the nominal pattern.</summary>
    private static readonly string[] DensitySuffixes = ["-M", "-L", "_M", "_L"];

    public static ReadResult Read(ComponentCandidate candidate, int dbuPerMicron)
    {
        // PL2's families read whole SETS rather than a symbol file plus footprint files, so they
        // dispatch here — before the PL1 path below, which is unchanged.
        //
        // R-PL2-2: none of these is a fallback for another. A reader that quietly retried in a second
        // format after the first refused would hide a reader bug behind an apparent success, and the
        // two results would differ in ways nobody looks for. A refusal is returned, not worked around.
        if (candidate.Family != ComponentFormatFamily.Pl1)
            return ReadPl2(candidate, dbuPerMicron);

        var part = new ComponentPart();

        // ── The symbol half (and, for the XML library, everything at once) ──────────────────────
        if (candidate.SymbolFile is { } symbolFile)
        {
            string text;
            try { text = File.ReadAllText(symbolFile.Path); }
            catch (IOException ex) { return new ReadResult(null, $"{symbolFile.Name}: {ex.Message}"); }

            var read = symbolFile.Kind switch
            {
                ComponentFileKind.SymbolSexpr => ToResult(ComponentSymbolSexprReader.Read(text)),
                ComponentFileKind.SymbolLegacyText => ToResult(ComponentSymbolLegacyReader.Read(text)),
                ComponentFileKind.LibraryXml => ToResult(ComponentLibraryXmlReader.Read(text, dbuPerMicron)),
                _ => new ReadResult(null, Refusal(symbolFile.Name)),
            };
            if (read.Refusal is { } refusal) return new ReadResult(null, refusal);
            part = read.Part!;
            part.SourceFiles.Add(symbolFile.Path);
            foreach (var f in part.Footprints) f.SourceFileName = symbolFile.Name;
        }

        // ── The footprint half ─────────────────────────────────────────────────────────────────
        foreach (var file in candidate.FootprintFiles)
        {
            string text;
            try { text = File.ReadAllText(file.Path); }
            catch (IOException ex) { part.Messages.Add($"{file.Name}: {ex.Message}"); continue; }

            var read = PcbReader.ReadFootprint(text, dbuPerMicron);
            if (read.Refusal is { } refusal) { part.Messages.Add($"{file.Name}: {refusal}"); continue; }

            var (baseName, variant) = SplitDensityVariant(Path.GetFileNameWithoutExtension(file.Path));
            var footprint = new ComponentFootprint
            {
                Name = baseName,
                Variant = variant,
                Cell = read.Cell!,
                LayerTable = read.LayerTable,
                SourceFileName = file.Name,
            };
            foreach (var pin in read.Cell!.Pins)
                if (pin.Pin.Name.Length > 0) footprint.PadNames.Add(pin.Pin.Name);

            part.Footprints.Add(footprint);
            part.SourceFiles.Add(file.Path);
            if (read.Board is { } board) CarryDiagnostics(board, file.Name, part);
        }

        if (part.Name.Length == 0)
            part.Name = Path.GetFileNameWithoutExtension(
                candidate.SymbolFile?.Path ?? candidate.FootprintFiles.FirstOrDefault()?.Path ?? "component");

        if (part.Symbol is null && part.Footprints.Count == 0)
            return new ReadResult(null, Refusal(candidate.Files.FirstOrDefault()?.Name ?? "This file"));

        OrderFootprints(part);
        NoteDistinctPatterns(part);
        return new ReadResult(part, null);
    }

    /// <summary>
    /// PL2's five families (brief-PL2-component-library-breadth.md). Each reads its whole set at once
    /// and returns the same <see cref="ComponentPart"/> PL1's readers do, so everything downstream —
    /// <c>ComponentTerminals</c>, <c>ComponentImport</c>, the cell folder — is untouched.
    /// </summary>
    private static ReadResult ReadPl2(ComponentCandidate candidate, int dbuPerMicron)
    {
        string? Text(ComponentFileKind kind)
        {
            var file = candidate.Of(kind);
            if (file is null) return null;
            try { return File.ReadAllText(file.Path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        ReadResult result = candidate.Family switch
        {
            ComponentFormatFamily.Records when Text(ComponentFileKind.PartRecords) is { } p =>
                ToResult(ComponentRecordsReader.Read(
                    p, Text(ComponentFileKind.FootprintRecords), Text(ComponentFileKind.SymbolRecords), dbuPerMicron)),

            ComponentFormatFamily.Hkp =>
                ToResult(ComponentHkpReader.Read(
                    Text(ComponentFileKind.HkpParts), Text(ComponentFileKind.HkpCells),
                    Text(ComponentFileKind.HkpPadstacks), Text(ComponentFileKind.HkpSymbols), dbuPerMicron)),

            ComponentFormatFamily.Plx when Text(ComponentFileKind.PlxLibrary) is { } t =>
                ToResult(ComponentPlxReader.Read(t, dbuPerMicron)),

            ComponentFormatFamily.Cxf when Text(ComponentFileKind.CxfLibrary) is { } t =>
                ToResult(ComponentCxfReader.Read(t, dbuPerMicron)),

            ComponentFormatFamily.Script when Text(ComponentFileKind.ScriptLibrary) is { } t =>
                ToResult(ComponentScrReader.Read(t, dbuPerMicron)),

            _ => new ReadResult(null, Refusal(candidate.Files.FirstOrDefault()?.Name ?? "This file")),
        };

        if (result.Part is not { } part) return result;

        // A multi-file set reads ONE file of each kind. ComponentFolderScan's grouping keeps a
        // candidate to one folder, so a second file of the same kind means that folder holds two
        // parts' worth of set — which is reported rather than silently resolved by taking the first.
        foreach (var kind in candidate.Files.Select(f => f.Kind).Distinct())
        {
            var same = candidate.Files.Where(f => f.Kind == kind).ToList();
            if (same.Count > 1)
                part.Messages.Add(
                    $"This folder holds {same.Count:N0} files of the same kind for one set — "
                    + $"{string.Join(", ", same.Select(f => f.Name))}. Only {same[0].Name} was read. "
                    + "Point the import at the folder holding one part if these are different parts.");
        }

        foreach (var file in candidate.Files)
        {
            // The encrypted twins are never in a candidate, so nothing here copies one into the cell
            // folder — and R-PL2-7 keeps them out of the chooser's noise too.
            part.SourceFiles.Add(file.Path);
            foreach (var footprint in part.Footprints)
                if (footprint.SourceFileName.Length == 0) footprint.SourceFileName = file.Name;
        }

        if (part.Name.Length == 0)
            part.Name = Path.GetFileNameWithoutExtension(candidate.Files.FirstOrDefault()?.Path ?? "component");

        NoteDistinctPatterns(part);
        return result;
    }

    /// <summary>R-PL1-25's ordering: the nominal pattern first, so <c>ComponentImport</c> writes it as
    /// <c>PrimaryLayout</c>. Every variant is written; this decides only which one opens.</summary>
    private static void OrderFootprints(ComponentPart part)
    {
        var ordered = part.Footprints
            .Select((f, i) => (Footprint: f, Index: i))
            .OrderBy(t => t.Footprint.Variant.Length == 0 ? 0 : 1)
            .ThenBy(t => t.Index)
            .Select(t => t.Footprint)
            .ToList();
        part.Footprints.Clear();
        part.Footprints.AddRange(ordered);
    }

    /// <summary>Land patterns whose base names differ are not density levels of one another. They still
    /// become sibling views of one cell; the difference is reported rather than left silent.</summary>
    private static void NoteDistinctPatterns(ComponentPart part)
    {
        var names = part.Footprints.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 1)
            part.Messages.Add(
                $"These {part.Footprints.Count} land patterns are not density levels of one pattern — " +
                $"{string.Join(", ", names)}. All were imported as sibling layout views of one cell; the " +
                "first is primary.");
    }

    /// <summary>
    /// <c>PART-M</c> → (<c>PART</c>, <c>-M</c>). A name that carries no density suffix is the nominal
    /// pattern and comes back with an empty variant.
    /// </summary>
    internal static (string BaseName, string Variant) SplitDensityVariant(string fileBaseName)
    {
        foreach (var suffix in DensitySuffixes)
            if (fileBaseName.EndsWith(suffix, StringComparison.Ordinal) && fileBaseName.Length > suffix.Length)
                return (fileBaseName[..^suffix.Length], suffix);
        return (fileBaseName, "");
    }

    /// <summary>The footprint reader's own counters and skip reasons, prefixed by the file they came
    /// from — several footprint files may be read in one import, so an unattributed count says nothing
    /// about which one it belongs to.</summary>
    private static void CarryDiagnostics(PcbBoard board, string fileName, ComponentPart part)
    {
        foreach (var d in board.Diagnostics) part.Messages.Add($"{fileName}: {d}");
        foreach (var (what, n) in board.SkippedCounts.OrderByDescending(kv => kv.Value))
            part.Messages.Add($"{fileName}: {n:N0} × {what} — not imported.");
        foreach (var (what, n) in board.DegradedCounts.OrderByDescending(kv => kv.Value))
            part.Messages.Add($"{fileName}: {n:N0} × {what}.");
        foreach (var (token, n) in board.UnknownTokenCounts.OrderByDescending(kv => kv.Value))
            part.Messages.Add($"{fileName}: unrecognized token \"{token}\" ({n:N0} occurrence(s)) — skipped.");
    }

    /// <summary>R-PL1-29: a refusal names the formats circuitRF DOES read, by extension, so the message
    /// says what would work rather than only what did not.</summary>
    public static string Refusal(string what)
        => $"{what} is not a component file circuitRF can read. circuitRF reads " +
           $"{string.Join(", ", ComponentClassifier.ReadableExtensions)}.";

    private static ReadResult ToResult(ComponentSymbolSexprReader.ReadResult r) => new(r.Part, r.Refusal);
    private static ReadResult ToResult(ComponentSymbolLegacyReader.ReadResult r) => new(r.Part, r.Refusal);
    private static ReadResult ToResult(ComponentLibraryXmlReader.ReadResult r) => new(r.Part, r.Refusal);
    private static ReadResult ToResult(ComponentRecordsReader.ReadResult r) => new(r.Part, r.Refusal);
    private static ReadResult ToResult(ComponentHkpReader.ReadResult r) => new(r.Part, r.Refusal);
    private static ReadResult ToResult(ComponentPlxReader.ReadResult r) => new(r.Part, r.Refusal);
    private static ReadResult ToResult(ComponentCxfReader.ReadResult r) => new(r.Part, r.Refusal);
    private static ReadResult ToResult(ComponentScrReader.ReadResult r) => new(r.Part, r.Refusal);
}
