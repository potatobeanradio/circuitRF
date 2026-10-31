using System.Globalization;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.Workspace;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf import library</c> and <c>circuitrf find --parts</c> — the headless spellings of
/// File ▸ Add Vendor Library… and the palette's part search (brief vendorlib-1 R-vl1-5).
///
/// <para><b>Argument parsing, refusals and reporting only.</b> Adding the reference is
/// <see cref="VendorLibraryRefs.AddOrRepair"/> and matching a part is
/// <see cref="VendorLibrarySearch.Match(VendorLibraryEntry, string?)"/> — the functions the GUI
/// calls — so a library added headlessly is the library the GUI opens, and a query finds the parts
/// the palette finds.</para>
/// </summary>
internal static class VendorLibraryVerbs
{
    // ── import library ───────────────────────────────────────────────────────────────────────────

    public static int ImportLibrary(string[] args)
    {
        string? source = null, into = null, name = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--into" when i + 1 < args.Length: into = args[++i]; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(args[i])); return Usage(); }
                    if (source is not null) { JsonRun.Report(CliDiagnostics.ImportMultipleSources()); return Usage(); }
                    source = args[i];
                    break;
            }
        }

        if (source is null) { JsonRun.Report(CliDiagnostics.ImportSourceRequired()); return Usage(); }
        JsonRun.InputPath = source;
        if (!Directory.Exists(source)) return JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(source));
        if (into is null) { JsonRun.Report(CliDiagnostics.ImportLibraryIntoRequired()); return Usage(); }

        if (WorkspaceOf(into) is not { } wsRoot)
            return JsonRun.Fail(CliDiagnostics.ImportLibraryNotAWorkspace(into));

        string cwsPath = Path.Combine(wsRoot, WorkspacePersistence.FileName);
        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ImportLibraryRefused($"{cwsPath} could not be read: {ex.Message}")); }

        var refs  = cws.VendorLibraries ?? [];
        var build = VendorLibraryRefs.AddOrRepair(wsRoot, refs, Path.GetFullPath(source), name, out string? problem);
        if (build is null) return JsonRun.Fail(CliDiagnostics.ImportLibraryRefused(problem ?? "The library could not be added."));

        cws.VendorLibraries = refs;
        if (!WorkspacePersistence.SaveToFileAtomic(cwsPath, cws))
            return JsonRun.Fail(CliDiagnostics.NewRefused(
                $"The library was not added — '{wsRoot}' is read-only on this machine."));

        // AddOrRepair replaces by name and appends, so the reference it wrote is the last one.
        var added = refs[^1];
        string libName = added.Name;

        Console.WriteLine($"{libName}\t{build.Root}");
        foreach (string line in build.SummaryLines())
        {
            Console.WriteLine(line);
            JsonRun.Note(CliDiagnostics.ImportMessage(line));
        }
        JsonRun.AddOutput("cws", cwsPath);
        JsonRun.Library = new VendorLibraryJson(
            libName, added.Path, build.Entries.Count, build.FilesScanned, build.EncryptedFiles,
            build.EmptyFiles, build.UnreadableFiles, build.Duplicates.Count,
            build.Groups.ToDictionary(g => g.Folder, g => g.Parts, StringComparer.Ordinal));
        return 0;
    }

    /// <summary>The workspace a path belongs to — a <c>.cws</c>, its folder, or any folder inside one.</summary>
    private static string? WorkspaceOf(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
        return Directory.Exists(full) ? WorkspaceRootFinder.WorkspaceDirOf(full) : null;
    }

    public const string ImportUsageText =
        "       circuitrf import library <folder> --into <workspace> [--name N]";

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf import library <folder> --into <workspace> [--name N]");
        return 1;
    }

    // ── find --parts ─────────────────────────────────────────────────────────────────────────────

    public const int DefaultLimit = 50;

    /// <summary>
    /// The parts of the workspace's vendor libraries matching <paramref name="query"/>, each with the
    /// instance parameters to write. Sorted by library order, then by stated value, then by name.
    /// </summary>
    public static int FindParts(string root, string query, string? library, int limit)
    {
        if (WorkspaceOf(root) is not { } wsRoot) return JsonRun.Fail(CliDiagnostics.FindPartsNotAWorkspace(root));

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(Path.Combine(wsRoot, WorkspacePersistence.FileName)); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.FindPartsNotAWorkspace($"{wsRoot} ({ex.Message})")); }

        var refs = (cws.VendorLibraries ?? []).ToList();
        if (library is not null)
        {
            var named = refs.Where(r => string.Equals(r.Name, library, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
                return JsonRun.Fail(CliDiagnostics.FindPartsNoLibrary(library,
                    refs.Count == 0 ? "none" : string.Join(", ", refs.Select(r => r.Name))));
            refs = named;
        }

        // The footprint a part would take is the workspace's default technology's answer — the same
        // one a palette drop into a schematic of this workspace gets (R-vl2-3d).
        var technology = CircuitRF.Design.Schematic.SchematicTechnology.Resolve(wsRoot, null).Technology;

        var q = VendorLibrarySearch.Parse(query);
        var hits = new List<(string Library, VendorLibraryEntry Entry)>();
        foreach (var r in refs)
        {
            if (VendorLibraryRefs.ResolveLibraryRoot(wsRoot, r.Name, out string? why) is not { } libRoot)
            {
                var d = CliDiagnostics.FindPartsLibraryUnavailable(why ?? r.Name);
                Console.Error.WriteLine($"warning: {d.Render()}");
                JsonRun.Note(d);
                continue;
            }
            var build = VendorLibraryIndex.Build(libRoot);
            if (build.Refusal is { } refusal)
            {
                var d = CliDiagnostics.FindPartsLibraryUnavailable($"{r.Name}: {refusal}");
                Console.Error.WriteLine($"warning: {d.Render()}");
                JsonRun.Note(d);
                continue;
            }
            // What the index found about tolerances (R-vl5-1g), said as the build says it, on stderr beside the count.
            foreach (string line in build.SummaryLines().Where(l => l.StartsWith("Tolerance known", StringComparison.Ordinal)
                                                                  || build.ToleranceRefusals.Any(t => l.StartsWith(t.File + ":", StringComparison.Ordinal))))
            {
                Console.Error.WriteLine($"[circuitRF] {r.Name}: {line}");
                JsonRun.Note(CliDiagnostics.ImportMessage($"{r.Name}: {line}"));
            }
            hits.AddRange(build.Entries
                .Where(e => VendorLibrarySearch.Match(e, q))
                .OrderBy(e => e.Header.Value ?? double.MaxValue)
                .ThenBy(e => e.Part, StringComparer.Ordinal)
                .Select(e => (r.Name, e)));
        }

        var shown = hits.Take(limit).ToList();
        var rows  = new List<FoundPartJson>(shown.Count);
        foreach (var (lib, e) in shown)
        {
            var row = ToJson(lib, e, technology);
            rows.Add(row);
            Console.WriteLine(string.Join('\t',
                lib, e.Part, e.Header.ValueText(), ToleranceColumn(e), e.Header.Description ?? e.FileStem,
                e.Header.FrequencyRangeText(),
                string.Join(' ', row.Parameters.Select(p => $"{p.Key}={p.Value}"))));
        }

        Console.Error.WriteLine($"[circuitRF] {hits.Count} part(s) match in {refs.Count} librar{(refs.Count == 1 ? "y" : "ies")}");
        if (hits.Count > shown.Count) JsonRun.Note(CliDiagnostics.FindPartsLimited(shown.Count, hits.Count));

        JsonRun.Find = new FindReportJson(wsRoot, 0, hits.Count > shown.Count, [], rows);
        return 0;
    }

    private static FoundPartJson ToJson(string library, VendorLibraryEntry e, CircuitRF.Design.Layout.Technology? technology)
    {
        var part = VendorPartRef.For(library, e);
        // The instance parameters to write are VendorPartPlacement's — the ones a palette drop writes.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in VendorPartPlacement.Parameters(part)) parameters[name] = value;
        if (VendorPartPlacement.Footprint(part, technology) is { } footprint) parameters["Footprint"] = footprint;

        return new(library, e.Part, e.File, e.PortCount, e.Header.Value,
               e.Header.Quantity switch
               {
                   VendorPartQuantity.None        => null,
                   VendorPartQuantity.Capacitance => "C",
                   VendorPartQuantity.Inductance  => "L",
                   VendorPartQuantity.Resistance  => "R",
                   _                              => "other",
               },
               e.Header.Description, e.Header.FrequencyLowHz, e.Header.FrequencyHighHz,
               e.Header.CaseMetric, e.Header.CaseImperial, parameters,
               e.Tolerance is { } t ? new(t.SpreadText, t.KindWord, t.SourceWord) : null,
               e.ToleranceNote);
    }

    /// <summary>The tolerance column (R-vl5-5a): <c>±0.1 pF</c>, <c>not known</c>, or empty for a part with no C or L
    /// value.</summary>
    private static string ToleranceColumn(VendorLibraryEntry e)
        => e.Tolerance?.DisplayText ?? (e.ToleranceNote is not null ? "not known" : "");

    internal static bool TryParseLimit(string text, out int limit)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) && limit > 0;
}
