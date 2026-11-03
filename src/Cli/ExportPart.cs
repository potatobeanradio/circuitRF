// `circuitrf export part` — Export to Library, headlessly (brief vendorlib-10 R-vl10-4).
//
// THERE IS NO EXPORT HERE. The packaging, the copy, the netlist, the check and the self-check are all
// `ModelLibraryExport` in src/Design, the function the dialog calls — `Authoring.cs`' rule, held by a
// comment-stripped source scan (ModelLibraryExportTests). What is here is argument parsing, refusals and
// reporting.
//
// `export` is a verb with a noun, as `new` and `import` are, so a later export has a noun to be rather than
// another top-level verb. Every default is the dialog's; anything the dialog would have ASKED (replacing a part
// already exported, carrying a vendor library's parts and terms into the model) is a refusal naming the flag that
// answers it.

using CircuitRF.Design.VendorLibraries.ModelLibrary;

namespace CircuitRF.Cli;

public static class ExportPart
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            JsonRun.Report(CliDiagnostics.ExportNounRequired());
            return Usage();
        }
        string noun = args[0].ToLowerInvariant();
        JsonRun.Verb = "export " + noun;
        if (noun != "part")
        {
            JsonRun.Report(CliDiagnostics.ExportUnknownNoun(noun));
            return Usage();
        }

        string? cell = null, library = null, kind = null, symbol = null, part = null;
        string category = "", description = "", value = "", low = "", high = "", conditions = "", footprint = "";
        string libraryName = "", publisher = "", license = "", libraryVersion = "1.0.0";
        var docs = new List<string>();
        var sweeps = new List<CircuitRF.Core.Design.FrequencySpec>();
        bool replace = false, includeVendor = false;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            bool more = i + 1 < args.Length;
            switch (a)
            {
                case "--library" when more:         library = args[++i]; break;
                case "--category" when more:        category = args[++i]; break;
                case "--part" when more:            part = args[++i]; break;
                case "--description" when more:     description = args[++i]; break;
                case "--kind" when more:            kind = args[++i]; break;
                case "--value" when more:           value = args[++i]; break;
                case "--freq-low" when more:        low = args[++i]; break;
                case "--freq-high" when more:       high = args[++i]; break;
                case "--conditions" when more:      conditions = args[++i]; break;
                case "--footprint" when more:       footprint = args[++i]; break;
                case "--doc" when more:             docs.Add(Path.GetFullPath(args[++i])); break;
                case "--symbol" when more:          symbol = args[++i]; break;
                case "--library-name" when more:    libraryName = args[++i]; break;
                case "--publisher" when more:       publisher = args[++i]; break;
                case "--license" when more:         license = args[++i]; break;
                case "--library-version" when more: libraryVersion = args[++i]; break;
                case "--sweep" when more:
                {
                    // One segment per --sweep, in the sparam directive's own keys: everything up to the next option.
                    var words = new List<string>();
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        words.AddRange(args[++i].Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    if (ParseSweep(words) is not { } segment)
                        return JsonRun.Fail(CliDiagnostics.ExportBadValue("--sweep", string.Join(' ', words),
                            "start=… stop=… and npts=… or step=…, with unit=… and log optional"));
                    sweeps.Add(segment);
                    break;
                }
                case "--replace":                   replace = true; break;
                case "--include-vendor-parts":      includeVendor = true; break;
                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.ExportUnknownOption(a)); return Usage(); }
                    if (cell is not null) { JsonRun.Report(CliDiagnostics.ExportCellRequired()); return Usage(); }
                    cell = a;
                    break;
            }
        }

        if (cell is null) { JsonRun.Report(CliDiagnostics.ExportCellRequired()); return Usage(); }
        JsonRun.InputPath = cell;
        if (!Directory.Exists(cell)) return JsonRun.Fail(CliDiagnostics.ExportCellNotFound(cell));
        if (library is null) { JsonRun.Report(CliDiagnostics.ExportLibraryRequired()); return Usage(); }

        string? kindWord = kind?.ToLowerInvariant() switch
        {
            null      => null,
            "active"  => ModelPartManifest.KindActive,
            "passive" => ModelPartManifest.KindPassive,
            _         => "",
        };
        if (kindWord == "") return JsonRun.Fail(CliDiagnostics.ExportBadValue("--kind", kind!, "active or passive"));

        string? symbolWord = symbol is null ? null
            : ModelLibraryExport.SymbolChoices.FirstOrDefault(c => c.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (symbol is not null && symbolWord is null)
            return JsonRun.Fail(CliDiagnostics.ExportBadValue("--symbol", symbol,
                string.Join(", ", ModelLibraryExport.SymbolChoices.Select(c => c.ToLowerInvariant()))));

        var options = new ExportOptions
        {
            LibraryDir = Path.GetFullPath(library), Category = category, PartNumber = part, Description = description,
            Kind = kindWord, Value = value, FrequencyLow = low, FrequencyHigh = high, Conditions = conditions,
            Footprint = footprint, Documents = docs, Symbol = symbolWord, Replace = replace,
            IncludeVendorParts = includeVendor, LibraryName = libraryName, Publisher = publisher,
            LibraryVersion = libraryVersion, License = license,
            Sweep = sweeps.Count == 0 ? null : sweeps,
        };

        // The plan first, so a refusal the dialog would have ASKED about can name the flag that answers it.
        var plan = ModelLibraryExport.Plan(cell, options);
        if (plan.Refusals.Count > 0)
        {
            foreach (string r in plan.Refusals) JsonRun.Report(CliDiagnostics.ExportRefused(r));
            if (plan.Exists && !replace)
                JsonRun.Report(CliDiagnostics.ExportRefused("Pass --replace to replace the exported part."));
            if (plan.VendorParts.Any() && !includeVendor)
                JsonRun.Report(CliDiagnostics.ExportRefused("Pass --include-vendor-parts to copy them into the model."));
            return 1;
        }

        ExportResult result;
        try { result = ModelLibraryExport.Export(cell, options, RunHost.Control); }
        catch (OperationCanceledException) { JsonRun.Report(CliDiagnostics.ExportCancelled()); return 130; }
        if (result.Cancelled) { JsonRun.Report(CliDiagnostics.ExportCancelled()); return 130; }
        if (!result.Succeeded) return JsonRun.Fail(CliDiagnostics.ExportRefused(result.Refusal ?? "Nothing was exported."));

        foreach (string w in result.Warnings)
        { Console.Error.WriteLine($"warning: {w}"); JsonRun.Note(CliDiagnostics.ExportWarning(w)); }
        foreach (var v in plan.VendorParts)
            Console.Error.WriteLine($"[circuitRF] {v.Describe()} — copied in; its library's terms travel with this model.");

        // The created paths ARE the result.
        Console.WriteLine(result.ModelDir);
        JsonRun.AddOutput("model", result.ModelDir!);
        if (result.LibraryManifest is { } clib) { Console.WriteLine(clib); JsonRun.AddOutput("library", clib); }
        foreach (string f in result.Created) { Console.WriteLine(f); JsonRun.AddOutput("file", f); }
        Console.Error.WriteLine($"[circuitRF] {result.Message}");
        return 0;
    }

    /// <summary>
    /// One <c>--sweep</c> segment, read by the <c>sparam</c> directive's own parser so its keys, its <c>unit=</c>
    /// and its <c>log</c> mean exactly what they mean there. Null when it is not a segment: start, stop, and npts or
    /// step are all required, since the directive's own fallbacks are not a plan anyone wrote.
    /// </summary>
    private static CircuitRF.Core.Design.FrequencySpec? ParseSweep(IReadOnlyList<string> words)
    {
        var keys = words.Select(w => w.Split('=', 2)[0].ToLowerInvariant()).ToHashSet();
        if (!keys.Contains("start") || !keys.Contains("stop") || !(keys.Contains("npts") || keys.Contains("step"))) return null;
        if (words.Any(w => w.Contains('"') || !(w.Contains('=') || w.Equals("log", StringComparison.OrdinalIgnoreCase)))) return null;
        if (keys.Contains("type")) return null;
        try
        {
            var (_, tb) = new CircuitRF.Core.Netlist.CnlReader().Read(
                "analysis SWEEP type=sparam " + string.Join(' ', words) + "\n", "sweep", sourceDirectory: null);
            return tb.Analyses.OfType<CircuitRF.Core.Design.SParameterAnalysis>().FirstOrDefault()?.Freq;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf export part <cell folder> --library <dir> [--category a/b] [--part PN]\n" +
            "         [--description …] [--kind active|passive] [--value …] [--freq-low … --freq-high …]\n" +
            "         [--conditions …] [--footprint …] [--doc <file>]… [--symbol own|fet|r|c|l|box]\n" +
            "         [--sweep start=… stop=… npts=…|step=… [unit=GHz] [log]]…   (technology parts; default 0-20 GHz, 1001 points)\n" +
            "         [--replace] [--include-vendor-parts]\n" +
            "         [--library-name … --publisher … --license … --library-version …]   (a new library only)\n" +
            "         [--json]");
        return 1;
    }
}
