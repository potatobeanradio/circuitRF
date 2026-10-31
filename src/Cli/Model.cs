using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Render.DataDisplay;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf model &lt;noun&gt; &lt;doc.cmc&gt;</c> — the Model Composer with no display (brief-modelcomposer-13; overview
/// D13, D15, D16; <c>cli.md</c> §31).
///
/// <para><b>It owns no modelling.</b> Every noun parses its arguments, calls the <c>src/Design/Modeling</c> function the
/// window's command calls — <see cref="ModelCommands"/>, or a step's own <c>FitFile</c>/<c>Export</c>/<c>RunFile</c> —
/// and reports what came back, so the window and the verb write the same bytes. <c>ModelCliNoLogicTests</c> holds that
/// shut with a comment-stripped source scan.</para>
///
/// <para><b>One verb with nouns</b> (the <c>new</c>/<c>history</c> rule): <c>new</c> is a noun of <c>model</c>, never a
/// verb of its own. Results go to stdout one line per item and under <c>--json</c> as <c>result.model.items</c>; progress
/// goes to stderr through a <see cref="RunControl"/>, and to a host's observer through <see cref="RunHost"/>.</para>
///
/// <para><b>Every default is the window's</b> (R-mc13-4). A choice the window would ask for is a refusal naming the flag
/// that answers it — a Touchstone import's bias pattern, a fit's N0, an export's file.</para>
///
/// <para><b>Exit codes (D16):</b> 0 done · 1 refused · 3 finished but a check failed (charge conservation above
/// <c>--threshold</c>, or a validation tolerance missed) · 130 cancelled, writing nothing.</para>
/// </summary>
internal static class Model
{
    private const string Verb = "model";

    /// <summary>The nouns, in the order the recipe uses them.</summary>
    internal static readonly (string Noun, string Summary)[] Nouns =
    [
        ("new",          "Creates a .cmc: --kind, optionally a --network (a stock id or a six-pin .csch) and N0/W0."),
        ("import",       "Adds a data set from files or a folder (--as its kind), read with a --pattern or a --map; --dry-run reads it and writes nothing."),
        ("deembed",      "Removes the pads and the network: one line per data set of intrinsic data."),
        ("fit",          "Fits one --step, or all of them, with the document's settings (--method and --seed change them)."),
        ("check-charge", "Checks charge conservation of a .cmc's fitted charges, or of a device in a .cnl; exit 3 above --threshold."),
        ("explain",      "Data sets and planes, intrinsic ranges, N0/W0, temperature mode, each step's state, network values, trap levels."),
        ("export",       "Writes the model: an Active document (or a .cmodel) as -o's extension says, .cmodel or Verilog-A .va; a Passive one as a cell (--cell)."),
        ("validate",     "Compares a model with the document's data at the measurement plane; exit 3 when a tolerance is missed."),
        ("report",       "Writes the Model Validation Report into a folder, with the document's stored options unless flags change them."),
    ];

    /// <summary>Every flag and the nouns that read it. A flag given to a noun not in its list is refused by name.</summary>
    internal static readonly (string Flag, string Takes, string[] Nouns)[] Flags =
    [
        ("--kind",           "active|passive",            ["new"]),
        ("--network",        "stock id|path.csch",        ["new"]),
        ("--fingers",        "N0",                        ["new", "fit"]),
        ("--unit-width",     "W0",                        ["new", "fit"]),
        ("--as",             "kind",                      ["import"]),
        ("--name",           "name",                      ["import", "export"]),
        ("--format",         "touchstone|csv|npy|mat|npz|ds|loadpull", ["import"]),
        ("--pattern",        "pattern",                   ["import"]),
        ("--pattern-source", "name|comment",              ["import"]),
        ("--map",            "a=b[:unit]",                ["import"]),
        ("--condition",      "Key=Value",                 ["import"]),
        ("--no-header",      "",                          ["import"]),
        ("--delimiter",      "c",                         ["import"]),
        ("--z0",             "ohms",                      ["import"]),
        ("--dry-run",        "",                          ["import"]),
        ("--step",           "step|all",                  ["fit"]),
        ("--method",         "method",                    ["fit"]),
        ("--seed",           "n",                         ["fit"]),
        ("--topology",       "id|path.csch",              ["fit"]),
        ("--device",         "instance",                  ["check-charge"]),
        ("--threshold",      "p%",                        ["check-charge"]),
        ("--bias",           "vgs,vds",                   ["check-charge", "report"]),
        ("--region",         "vgs0,vgs1,vds0,vds1",       ["check-charge"]),
        ("--freq",           "f",                         ["check-charge", "import", "report"]),
        ("-o",               "path",                      ["export", "report"]),
        ("--cell",           "workspace [name]",          ["export"]),
        ("--symbol",         "name|path.csym",            ["export"]),
        ("--created",        "timestamp",                 ["export"]),
        ("--module",         "name",                      ["export"]),
        ("--model",          "x.cmodel",                  ["validate", "report"]),
        ("--cmc",            "doc.cmc",                   ["validate"]),
        ("--against",        "set,set",                   ["validate"]),
        ("--tolerance",      "Key=value",                 ["validate"]),
        ("--pdf",            "file.pdf",                  ["report"]),
        ("--sections",       "s,s",                       ["report"]),
        ("--classes",        "B,J,F,FInverse",            ["report"]),
        ("--source-2f0",     "re,im",                     ["report"]),
        ("--ropt",           "ohms",                      ["report"]),
        ("--disc",           "radius,rings,spokes",       ["report"]),
        ("--pin",            "start,step,max",            ["report"]),
        ("--compression",    "dB",                        ["validate", "report"]),
        ("-q",               "",                          ["new", "import", "deembed", "fit", "check-charge", "explain", "export", "validate", "report"]),
    ];

    private sealed class Args
    {
        public readonly List<string> Positionals = [];
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Repeats = new(StringComparer.Ordinal);
        public readonly HashSet<string> Given = new(StringComparer.Ordinal);
        public string? CellName;
        public bool Quiet => Given.Contains("-q");
        public string? this[string flag] => Values.GetValueOrDefault(flag);
        public IReadOnlyList<string> All(string flag) => Repeats.GetValueOrDefault(flag) ?? [];
    }

    private static readonly HashSet<string> Repeated = ["--map", "--condition", "--tolerance"];
    private static readonly HashSet<string> Switches = ["--no-header", "--dry-run", "-q"];

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-') || Nouns.All(n => n.Noun != args[0].ToLowerInvariant()))
        {
            int code = JsonRun.Fail(CliDiagnostics.ModelNoun(args.Length == 0 ? "" : args[0]));
            Usage();
            return code;
        }
        string noun = args[0].ToLowerInvariant();
        JsonRun.Verb = Verb + " " + noun;

        var a = new Args();
        bool askedForVerilogA = false;
        for (int i = 1; i < args.Length; i++)
        {
            string s = args[i];
            if (!s.StartsWith('-') || s == "-") { a.Positionals.Add(s); continue; }
            // A Verilog-A export asked for by a flag (brief MC-30 R-mc30-7): there is none, because -o's extension
            // picks the format, and "unknown option" did not lead anyone there. Refused after the loop, with the name.
            if (noun == "export" && (s is "--veriloga" or "--va" or "--vams" or "--verilog-a"
                                     || s == "--format" && i + 1 < args.Length && IsVerilogAWord(args[i + 1])))
            {
                askedForVerilogA = true;
                if (s == "--format") i++;
                continue;
            }
            if (Flags.All(f => f.Flag != s)) return JsonRun.Fail(CliDiagnostics.ModelUnknownOption(s));
            a.Given.Add(s);
            if (Switches.Contains(s)) continue;
            if (i + 1 >= args.Length) return JsonRun.Fail(CliDiagnostics.ModelFlagValue(s, "", Flags.First(f => f.Flag == s).Takes));
            string value = args[++i];
            if (Repeated.Contains(s)) { (a.Repeats.TryGetValue(s, out var list) ? list : a.Repeats[s] = []).Add(value); continue; }
            a.Values[s] = value;
            // `--cell <workspace> <name>`: the name is the next word when it is not a flag; `--name` says it otherwise.
            if (s == "--cell" && i + 1 < args.Length && !args[i + 1].StartsWith('-')) a.CellName = args[++i];
        }
        if (askedForVerilogA)
        {
            string stem = a.Positionals.Count > 0 ? Path.GetFileNameWithoutExtension(a.Positionals[0]) : "<name>";
            return JsonRun.Fail(CliDiagnostics.ModelRefused("export", $"the extension of -o picks the format: -o {stem}.va"));
        }
        foreach (string flag in a.Given)
            if (Flags.First(f => f.Flag == flag) is var row && !row.Nouns.Contains(noun))
                return JsonRun.Fail(CliDiagnostics.ModelFlagNotForNoun(flag, noun, Yield.NounList(row.Nouns)));

        if (a.Positionals.Count == 0) return JsonRun.Fail(CliDiagnostics.ModelPathRequired(noun));
        string path = Path.GetFullPath(a.Positionals[0]);
        JsonRun.InputPath = path;
        if (noun != "import" && a.Positionals.Count > 1)
            return JsonRun.Fail(CliDiagnostics.ModelExtraPath(noun, a.Positionals[1]));
        // A dry run reads the sources and writes nothing, so it needs no document yet: the data can be checked first.
        bool dryRun = noun == "import" && a.Given.Contains("--dry-run");
        if (noun != "new" && !dryRun && !File.Exists(path)) return JsonRun.Fail(CliDiagnostics.ModelNotFound(path));

        var rc = Control(a.Quiet);
        try
        {
            return noun switch
            {
                "new"          => New(path, a),
                "import"       => Import(path, a, rc),
                "deembed"      => Report(noun, path, ModelCommands.Deembed(path, rc)),
                "fit"          => Fit(path, a, rc),
                "check-charge" => CheckCharge(path, a, rc),
                "explain"      => Report(noun, path, WithExportSpellings(path, ModelCommands.Explain(path, rc))),
                "export"       => Export(path, a, rc),
                "validate"     => Validate(path, a, rc),
                _              => ReportNoun(path, a, rc),
            };
        }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.ModelCancelled());
            return 130;
        }
        catch (Exception ex) when (ex is ModelingException or CmcFormatException or DataImportException or CmodelFormatException
                                       or VerilogAExportException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JsonRun.Fail(CliDiagnostics.ModelRefused(noun, ex.Message));
        }
    }

    // ── the nouns ────────────────────────────────────────────────────────────────

    private static int New(string path, Args a)
    {
        if (a["--kind"] is not { } kindText) return Missing("new", "--kind", "say what is modelled: --kind active (a FET) or --kind passive (an N-port)");
        if (!Enum.TryParse<CmcKind>(kindText, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            return JsonRun.Fail(CliDiagnostics.ModelFlagValue("--kind", kindText, "active or passive"));
        if (!Periphery(a, "new", out var periphery, out int refused)) return refused;
        string? network = a["--network"] is { } n && n.EndsWith(".csch", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(n) : a["--network"];
        return Report("new", path, ModelCommands.New(path, kind, network, periphery), written: [path]);
    }

    private static int Import(string path, Args a, RunControl rc)
    {
        var inputs = a.Positionals.Skip(1).Select(Path.GetFullPath).ToList();
        if (inputs.Count == 0) return Missing("import", "a source", "name the files, or the folder, the data set is read from");
        if (a["--as"] is not { } kind)
            return Missing("import", "--as", $"say what the data is: --as {string.Join("|", Enum.GetNames<DataSetKind>())}");

        bool dryRun = a.Given.Contains("--dry-run");
        var doc = dryRun && !File.Exists(path) ? new CmcDocument { Kind = CmcKind.Active } : CmcFile.Read(path);
        var sources = ModelCommands.Sources(path, inputs);
        string format = a["--format"] ?? ModelCommands.FormatOf(sources[0]) ?? "";
        var import = new JsonObject { ["Format"] = format };
        if (format == ImportSettings.Touchstone)
        {
            // A passive block has no bias (designer round 28): its pattern only ever names a family's {geometry}, and a
            // single block's file imports as it is — the dialog's empty pattern.
            if (a["--pattern"] is { } pattern)
            {
                import["Pattern"] = pattern;
                import["PatternSource"] = a["--pattern-source"] ?? "name";
            }
            else if (doc.Kind != CmcKind.Passive)
                return Missing("import", "--pattern",
                    "a Touchstone set's bias is read from each file's name (or comments) by a pattern, e.g. --pattern \"Vgs{vgs}_Vds{vds}\"");
        }
        else if (a["--pattern"] is { } pattern) import["Pattern"] = pattern;
        var maps = a.All("--map").ToList();
        var inferred = new List<ModelLine>();
        if (format == ImportSettings.Csv && maps.Count == 0)
            return Missing("import", "--map", "say what each column holds, e.g. --map Vgs=vgs:V --map Id=id:mA");
        if (ImportSettings.IsArrayFormat(format) && maps.Count == 0)
        {
            // No map: the one the array names suggest, when it finds the bias — said on its own line, and written into the
            // .cmc like any map, so the next import reads exactly the same arrays.
            var suggestion = ModelCommands.SuggestArrayMap(path, sources[0], format);
            if (!suggestion.Arrays.ContainsKey("vgs") || !suggestion.Arrays.ContainsKey("vds"))
                return Missing("import", "--map",
                    $"say what each array holds, e.g. --map vg=vgs --map vd=vds --map id=id; {ModelCommands.ArraysHeld(path, sources[0], format)}" +
                    (suggestion.Doubts.Count > 0 ? "; " + string.Join(" ", suggestion.Doubts) : ""));
            maps = [.. suggestion.Text.Split(", ")];
            inferred.Add(new ModelLine("note", "map", $"map from the array names: {suggestion.Text}."));
            inferred.AddRange(suggestion.Doubts.Select(d => new ModelLine("note", "map", d)));
        }
        foreach (string m in maps)
        {
            int eq = m.IndexOf('=');
            if (eq <= 0 || eq == m.Length - 1) return JsonRun.Fail(CliDiagnostics.ModelFlagValue("--map", m, "name=quantity[:unit]"));
            string left = m[..eq], right = m[(eq + 1)..];
            if (format == ImportSettings.Csv)
            {
                var col = new JsonObject { ["Column"] = left };
                int colon = right.IndexOf(':');
                col["Quantity"] = colon < 0 ? right : right[..colon];
                if (colon >= 0) col["Unit"] = right[(colon + 1)..];
                (import["Columns"] ??= new JsonArray()).AsArray().Add(col);
            }
            else
            {
                int colon = right.IndexOf(':');
                string quantity = colon < 0 ? right : right[..colon];
                (import["Arrays"] ??= new JsonObject()).AsObject()[quantity] = left;
                if (colon >= 0) (import["ArrayUnits"] ??= new JsonObject()).AsObject()[quantity] = right[(colon + 1)..];
            }
        }
        if (a.Given.Contains("--no-header")) import["Header"] = false;
        if (a["--delimiter"] is { } delimiter) import["Delimiter"] = delimiter;
        if (a["--freq"] is { } freq) { if (!Number("--freq", freq, out double f, out int r)) return r; import["FrequencyHz"] = f; }
        if (a["--z0"] is { } z0) { if (!Number("--z0", z0, out double z, out int r)) return r; import["Z0"] = z; }

        var set = new JsonObject
        {
            ["Name"] = a["--name"] ?? ModelCommands.DefaultName(doc, inputs),
            ["Kind"] = kind,
            ["Sources"] = new JsonArray([.. sources.Select(s => (JsonNode)s)]),
            ["Import"] = import,
        };
        var conditions = new JsonObject();
        foreach (string c in a.All("--condition"))
        {
            int eq = c.IndexOf('=');
            if (eq <= 0) return JsonRun.Fail(CliDiagnostics.ModelFlagValue("--condition", c, "Key=Value, e.g. ReferencePlane=device or Quiescent.Vds=28"));
            var node = conditions;
            var keys = c[..eq].Split('.');
            foreach (string k in keys[..^1]) node = (node[k] ??= new JsonObject()).AsObject();
            node[keys[^1]] = ConditionValue(c[(eq + 1)..]);
        }
        if (conditions.Count > 0) set["Conditions"] = conditions;

        var parsed = CmcFile.ParseDataSet(set.ToJsonString());
        if (dryRun) return Report("import", path, [.. inferred, .. ModelCommands.DryRun(path, parsed, rc)]);
        return Report("import", path, [.. inferred, .. ModelCommands.Import(path, parsed, rc)], written: [path]);
    }

    private static int Fit(string path, Args a, RunControl rc)
    {
        if (a["--step"] is not { } step)
            return Missing("fit", "--step", $"name the step: --step {string.Join("|", ModelCommands.Steps)}|all");
        ulong? seed = null;
        if (a["--seed"] is { } seedText)
        {
            if (!ulong.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong s))
                return JsonRun.Fail(CliDiagnostics.ModelFlagValue("--seed", seedText, "a whole number, 0 or more"));
            seed = s;
        }
        var doc = CmcFile.Read(path);
        if (!Periphery(a, "fit", out var periphery, out int refused)) return refused;
        if (periphery is null && doc.Kind == CmcKind.Active && doc.BasePeriphery is null)
            return Missing("fit", "--fingers", "the base finger count N0 is not set; give --fingers N0 --unit-width W0 for the measured device");
        var options = new FitOptions(a["--method"], seed, a["--topology"], periphery);
        return Report("fit", path, ModelCommands.Fit(path, step.ToLowerInvariant(), options, rc), written: [path]);
    }

    private static int CheckCharge(string path, Args a, RunControl rc)
    {
        double threshold = ConservationCheck.DefaultThreshold;
        if (a["--threshold"] is { } t)
        {
            string text = t.TrimEnd('%').Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) || !(pct > 0))
                return JsonRun.Fail(CliDiagnostics.ModelFlagValue("--threshold", t, "a percentage of the fundamental gate current, e.g. 0.1%"));
            threshold = pct / 100;
        }
        var settings = new LargeSignalSettings();
        if (a["--freq"] is { } freq) { if (!Number("--freq", freq, out double f, out int r)) return r; settings = settings with { FrequencyHz = f }; }
        if (a["--bias"] is { } biasText)
        {
            if (!Numbers("--bias", biasText, 2, out var b, out int r)) return r;
            settings = settings with { Bias = (b[0], b[1]) };
        }

        ConservationReport report;
        if (Path.GetExtension(path).Equals(".cnl", StringComparison.OrdinalIgnoreCase))
        {
            if (a["--device"] is not { } device) return Missing("check-charge", "--device", "name the instance in the .cnl to check, e.g. --device X1");
            if (settings.Bias is null) return Missing("check-charge", "--bias", "a device in a .cnl has no data to take a bias from; give --bias vgs,vds");
            (double, double, double, double)? region = null;
            if (a["--region"] is { } regionText)
            {
                if (!Numbers("--region", regionText, 4, out var g, out int r)) return r;
                region = (g[0], g[1], g[2], g[3]);
            }
            report = ConservationCheck.RunDevice(path, device, settings, region, rc);
        }
        else report = ConservationCheck.RunFile(path, settings, rc);

        bool failed = ConservationCheck.Exceeds(report, threshold);
        var items = report.Lines.Select(l => new ModelItemJson("check", "", l)).ToList();
        if (report.LargeSignal is { } c)
            items.Add(new ModelItemJson("check", "large-signal",
                $"spurious DC {Pct(c.Relative)} of the fundamental gate current, threshold {Pct(threshold)}: {(failed ? "FAILED" : "passed")}.",
                !failed, new Dictionary<string, double> { ["relative"] = c.Relative, ["threshold"] = threshold, ["spurious"] = c.Spurious }));
        Emit("check-charge", path, items);
        if (!failed) return 0;
        JsonRun.Report(CliDiagnostics.ModelCheckFailed("check-charge", $"the spurious DC is {Pct(report.LargeSignal!.Relative)} of the fundamental gate current, above {Pct(threshold)}"));
        return 3;
    }

    private static bool IsVerilogAWord(string w) => w.ToLowerInvariant() is "va" or "vams" or "veriloga" or "verilog-a" or "verilog";

    /// <summary><c>explain</c> on an Active document ends with the two spellings of its export, so the Verilog-A one is
    /// found from the document rather than from a refused flag (R-mc30-7).</summary>
    private static IReadOnlyList<ModelLine> WithExportSpellings(string path, IReadOnlyList<ModelLine> lines)
    {
        if (path.EndsWith(CmodelFile.Extension, StringComparison.OrdinalIgnoreCase) || CmcFile.Read(path).Kind != CmcKind.Active)
            return lines;
        string stem = Path.GetFileNameWithoutExtension(path);
        return [.. lines,
                new ModelLine("export", "cmodel",   $"export: circuitrf model export {Path.GetFileName(path)} -o {stem}{CmodelFile.Extension}"),
                new ModelLine("export", "veriloga", $"export: circuitrf model export {Path.GetFileName(path)} -o {stem}.va   (Verilog-A)")];
    }

    /// <summary>The formats an Active model exports as, by <c>-o</c>'s extension — <c>render</c>'s rule.</summary>
    private static readonly string[] ExportExtensions = [CmodelFile.Extension, ".va", ".vams"];

    private static int Export(string path, Args a, RunControl rc)
    {
        bool cmodel = path.EndsWith(CmodelFile.Extension, StringComparison.OrdinalIgnoreCase);
        var doc = cmodel ? null : CmcFile.Read(path);
        if (doc is { Kind: CmcKind.Passive })
        {
            if (a["--cell"] is not { } workspace)
                return Missing("export", "--cell", "a passive model exports as a cell; give --cell <workspace> <name>");
            if ((a.CellName ?? a["--name"]) is not { } cell) return Missing("export", "--name", "name the cell: --cell <workspace> <name>");
            string? symbol = a["--symbol"] is { } s ? Path.GetFullPath(s) : null;
            var result = ModelCommands.ExportCell(path, Path.GetFullPath(workspace), cell, symbol);
            return Report("export", path, [.. result.Lines.Select(l => new ModelLine("export", cell, l))],
                          written: [.. result.Cell.Paths]);
        }
        if (a["--cell"] is not null) return JsonRun.Fail(CliDiagnostics.ModelRefused("export", "an Active document exports as a .cmodel or a .va; give -o <out.cmodel> or -o <out.va>."));
        if (a["-o"] is not { } output) return Missing("export", "-o", "say where the model goes: -o <out.cmodel>, or -o <out.va> for Verilog-A");
        string ext = Path.GetExtension(output).ToLowerInvariant();
        if (!ExportExtensions.Contains(ext))
            return JsonRun.Fail(CliDiagnostics.ModelRefused("export",
                $"-o '{Path.GetFileName(output)}': the extension says the format, and it is one of .cmodel, .va or .vams (Verilog-A)."));
        if (ext != CmodelFile.Extension) return ExportVerilogA(path, Path.GetFullPath(output), a, rc);
        if (cmodel) return JsonRun.Fail(CliDiagnostics.ModelRefused("export", "the model is a .cmodel already; there is nothing to export. Give -o <out.va> for Verilog-A."));
        if (a["--module"] is not null) return JsonRun.Fail(CliDiagnostics.ModelRefused("export", "--module names a Verilog-A module; a .cmodel has none. Give -o <out.va>."));
        string? sym = a["--symbol"] is { } sy && sy.EndsWith(".csym", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(sy) : a["--symbol"];
        var export = CmodelExport.Export(path, Path.GetFullPath(output), sym, a["--created"], rc);
        return Report("export", path, [.. export.Lines.Select(l => new ModelLine("export", Path.GetFileName(export.Path), l))],
                      written: [export.Path]);
    }

    /// <summary>A <c>.cmc</c> or a <c>.cmodel</c> as Verilog-A source (brief-modelcomposer-28 R-mc28-2), through the one
    /// function the Export step calls.</summary>
    private static int ExportVerilogA(string path, string output, Args a, RunControl rc)
    {
        if (a["--created"] is not null)
            return JsonRun.Fail(CliDiagnostics.ModelRefused("export", "--created is a .cmodel's; a .va records the model's own Created."));
        if (a["--symbol"] is not null)
            return JsonRun.Fail(CliDiagnostics.ModelRefused("export", "--symbol is a .cmodel's; a Verilog-A module has no symbol."));
        var r = CmodelVerilogA.Export(path, output, new VerilogAOptions { ModuleName = a["--module"] }, rc);
        string file = Path.GetFileName(r.Path);
        var lines = new List<ModelLine> { new("export", file, $"{file}: module {r.ModuleName}, {r.Parameters} parameters.") };
        if (r.InertSwitches.Count > 0)
            lines.Add(new("export", file, $"For this model {Yield.NounList(r.InertSwitches)} change{(r.InertSwitches.Count == 1 ? "s" : "")} nothing."));
        int code = Report("export", path, lines, written: [r.Path]);
        JsonRun.Model = JsonRun.Model! with { VerilogA = new ModelVerilogAJson(r.Path, r.ModuleName, r.Parameters, r.InertSwitches) };
        return code;
    }

    private static int Validate(string path, Args a, RunControl rc)
    {
        string cmc = path;
        string? model = a["--model"] is { } m ? Path.GetFullPath(m) : null;
        if (path.EndsWith(".cmodel", StringComparison.OrdinalIgnoreCase))
        {
            if (a["--cmc"] is not { } c) return Missing("validate", "--cmc", "a .cmodel is validated against a document's data; name it with --cmc <doc.cmc>");
            (cmc, model) = (Path.GetFullPath(c), path);
            if (!File.Exists(cmc)) return JsonRun.Fail(CliDiagnostics.ModelNotFound(cmc));
        }
        var settings = ModelValidation.Settings(CmcFile.Read(cmc));
        if (a["--against"] is not null || a.All("--tolerance").Count > 0)
        {
            var json = settings.ToJson().AsObject();
            if (a["--against"] is { } sets) json["DataSets"] = new JsonArray([.. Split(sets).Select(s => (JsonNode)s)]);
            foreach (string t in a.All("--tolerance"))
            {
                int eq = t.IndexOf('=');
                if (eq <= 0 || !double.TryParse(t[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return JsonRun.Fail(CliDiagnostics.ModelFlagValue("--tolerance", t, "Key=value, e.g. S95=0.02 or IvRelative=0.01"));
                (json["Tolerances"] ??= Tolerances(settings.Tolerances)).AsObject()[t[..eq]] = v;
            }
            settings = ValidationSettings.Parse(json, "--tolerance/--against");
        }
        if (a["--compression"] is { } comp)
        {
            if (!Number("--compression", comp, out double v, out int r)) return r;
            if (!(v > 0)) return JsonRun.Fail(CliDiagnostics.ModelFlagValue("--compression", comp, "a compression in dB above 0"));
            settings = settings with { Compression = v };
        }
        var result = ModelValidation.RunFile(cmc, model, settings, rc);
        var items = result.Items.Select(i => new ModelItemJson("validation", i.Set, i.Line, i.Passed))
                          .Concat(result.Notes.Select(n => new ModelItemJson("note", "", n))).ToList();
        Emit("validate", cmc, items);
        if (result.Passed) return 0;
        JsonRun.Report(CliDiagnostics.ModelCheckFailed("validate",
            $"{result.Items.Count(i => !i.Passed)} of {result.Items.Count} item(s) are outside their tolerances"));
        return 3;
    }

    private static int ReportNoun(string path, Args a, RunControl rc)
    {
        var options = ModelValidationReport.Options(CmcFile.Read(path));
        var json = options.ToJson().AsObject();
        bool changed = false;
        void Set(string key, JsonNode value) { json[key] = value; changed = true; }
        if (a["--sections"] is { } sections) Set("Sections", new JsonArray([.. Split(sections).Select(s => (JsonNode)s)]));
        if (a["--classes"] is { } classes) Set("Modes", new JsonArray([.. Split(classes).Select(s => (JsonNode)s)]));
        if (a["--freq"] is { } freq) { if (!Number("--freq", freq, out double f, out int r)) return r; Set("FrequencyHz", f); }
        if (a["--bias"] is { } bias) { if (!Numbers("--bias", bias, 2, out var b, out int r)) return r; Set("Vgs", b[0]); Set("Vds", b[1]); }
        if (a["--source-2f0"] is { } z) { if (!Numbers("--source-2f0", z, 2, out var v, out int r)) return r; Set("Source2f0", new JsonArray(v[0], v[1])); }
        if (a["--ropt"] is { } ropt) { if (!Number("--ropt", ropt, out double v, out int r)) return r; Set("RoptOhms", v); }
        if (a["--disc"] is { } disc)
        {
            if (!Numbers("--disc", disc, 3, out var d, out int r)) return r;
            Set("GammaRadius", d[0]); Set("Rings", d[1]); Set("Spokes", d[2]);
        }
        if (a["--pin"] is { } pin)
        {
            if (!Numbers("--pin", pin, 3, out var p, out int r)) return r;
            Set("PinStart", p[0]); Set("PinStep", p[1]); Set("PinMax", p[2]);
        }
        if (a["--compression"] is { } comp) { if (!Number("--compression", comp, out double v, out int r)) return r; Set("Compression", v); }
        if (changed) options = ReportOptions.Parse(json, "report");

        string? model = a["--model"] is { } m ? Path.GetFullPath(m) : null;
        string folder = a["-o"] is { } o ? Path.GetFullPath(o) : ModelValidationReport.DefaultFolder(path);
        var report = ModelValidationReport.Generate(path, model, options, rc);
        rc.ThrowIfCancellationRequested();
        var written = ModelValidationReport.Write(report, folder).ToList();
        // The report's Data Display document beside the folder, and its PDF — the same two functions the window calls.
        string cdd = ModelReportDisplayPreset.WriteDocument(report, folder);
        written.Add(cdd);
        if (a["--pdf"] is { } pdf) written.Add(ModelReportDisplayPreset.WritePdf(cdd, Path.GetFullPath(pdf)));
        var card = report.Card;
        var items = new List<ModelLine> { new("scorecard", "Overall", card.Overall.ToString()) };
        items.AddRange(card.Scores.Select(r => new ModelLine("scorecard", $"{r.Section} · {r.Test}", $"{r.Grade}: {r.Result}")));
        items.AddRange(report.Sections.SelectMany(s => s.Lines.Select(l => new ModelLine("section", s.Name, l))));
        return Report("report", path, items, written: [.. written]);
    }

    // ── reporting ────────────────────────────────────────────────────────────────

    private static int Report(string noun, string document, IReadOnlyList<ModelLine> lines, IReadOnlyList<string>? written = null)
    {
        Emit(noun, document, [.. lines.Select(l => new ModelItemJson(l.Kind, l.Name, l.Text))]);
        foreach (string w in written ?? [])
        {
            Console.Error.WriteLine($"Wrote {w}");
            JsonRun.AddOutput(Path.GetExtension(w) switch { ".cmc" => "cmc", ".cmodel" => "cmodel", ".va" or ".vams" => "veriloga", _ => JsonRun.KindOf(w) }, w);
        }
        return 0;
    }

    private static void Emit(string noun, string document, IReadOnlyList<ModelItemJson> items)
    {
        foreach (var item in items) Console.WriteLine(item.Line);
        JsonRun.Model = new ModelReportJson(noun, document, items);
    }

    /// <summary>Progress on stderr — a changed line only — and to a host's observer, cancellable through the host's token.</summary>
    private static RunControl Control(bool quiet)
    {
        var observer = RunHost.Observer;
        string last = "";
        return new RunControl
        {
            Token = RunHost.Cancellation,
            Progress = new Inline(p =>
            {
                observer?.Invoke(p);
                if (quiet) return;
                string line = p.StageTotal > 0 ? $"[{p.StageCompleted}/{p.StageTotal}] {p.Stage}" : $"[{p.Completed}] {p.Stage}";
                if (line == last) return;
                last = line;
                Console.Error.WriteLine(line);
            }),
        };
    }

    /// <summary>Delivered on the reporting thread, as RunHost's own progress is: <c>Progress&lt;T&gt;</c> would post it to the
    /// thread pool and race the result.</summary>
    private sealed class Inline(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }

    // ── argument helpers ─────────────────────────────────────────────────────────

    private static int Missing(string noun, string flag, string why) => JsonRun.Fail(CliDiagnostics.ModelMissing(noun, flag, why));

    private static bool Periphery(Args a, string noun, out BasePeriphery? periphery, out int refused)
    {
        periphery = null; refused = 0;
        string? fingers = a["--fingers"], width = a["--unit-width"];
        if (fingers is null && width is null) return true;
        if (fingers is null) { refused = Missing(noun, "--fingers", "the unit width needs the finger count it belongs to"); return false; }
        if (width is null) { refused = Missing(noun, "--unit-width", "the finger count needs the unit gate width, e.g. --unit-width \"125 um\""); return false; }
        if (!int.TryParse(fingers, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1)
        {
            refused = JsonRun.Fail(CliDiagnostics.ModelFlagValue("--fingers", fingers, "a whole number of fingers, 1 or more"));
            return false;
        }
        periphery = new BasePeriphery(n, width);
        return true;
    }

    /// <summary>A number, with a unit when it has one ("2GHz", "1us"), in base SI.</summary>
    private static bool Number(string flag, string text, out double value, out int refused)
    {
        refused = 0;
        if (TunableValue.TryParse(text.Trim(), out double number, out string unit, out double si))
        {
            value = unit.Length > 0 ? si : number;
            return true;
        }
        value = 0;
        refused = JsonRun.Fail(CliDiagnostics.ModelFlagValue(flag, text, "a number"));
        return false;
    }

    private static bool Numbers(string flag, string text, int count, out double[] values, out int refused)
    {
        var parts = Split(text);
        values = new double[count];
        refused = 0;
        if (parts.Count == count)
        {
            int k = 0;
            for (; k < count; k++)
            {
                if (!TunableValue.TryParse(parts[k], out double n, out string unit, out double si)) break;
                values[k] = unit.Length > 0 ? si : n;
            }
            if (k == count) return true;
        }
        refused = JsonRun.Fail(CliDiagnostics.ModelFlagValue(flag, text, $"{count} numbers separated by commas"));
        return false;
    }

    private static List<string> Split(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>A condition's value as the document spells it: a number (base SI), a list of numbers, true/false, or text.</summary>
    private static JsonNode ConditionValue(string text)
    {
        if (TunableValue.TryParse(text.Trim(), out double n, out string unit, out double si)) return unit.Length > 0 ? si : n;
        if (text is "true" or "false") return text == "true";
        var parts = Split(text);
        if (parts.Count > 1 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return new JsonArray([.. parts.Select(p => (JsonNode)double.Parse(p, CultureInfo.InvariantCulture))]);
        return text;
    }

    private static JsonObject Tolerances(ValidationTolerances t) => new()
    {
        ["SMedian"] = t.SMedian, ["S95"] = t.S95, ["IvRelative"] = t.IvRelative,
        ["PowerDb"] = t.PowerDb, ["EfficiencyPoints"] = t.EfficiencyPoints,
        ["AmPmDegrees"] = t.AmPmDegrees, ["ImpedanceVswr"] = t.ImpedanceVswr,
    };

    private static string Pct(double v) => (100 * v).ToString("G3", CultureInfo.InvariantCulture) + " %";

    private static void Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf model <noun> <doc.cmc> [options]");
        foreach (var (noun, summary) in Nouns) Console.Error.WriteLine($"  {noun,-13} {summary}");
    }
}
