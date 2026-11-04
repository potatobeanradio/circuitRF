// Export to Library's Compile Verilog-A models, for a ComposedFET: the .cmodel turned into Verilog-A by Model Composer's
// own writer (CmodelVerilogA, the same text File ▸ Export Verilog-A writes), compiled, and shipped as this platform's
// build in place of the .cmodel. The placed part becomes a VerilogA instance of that build, keeping its values. A
// translation is not a copy, so the export proves the result against the source cell at bias points — Compile netlist's
// proof — instead of the copy's self-check, and writes nothing when they differ.

using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

public static partial class ModelLibraryExport
{
    /// <summary>A placed ComposedFET: its <c>.cmodel</c>, absolute; how many pins it draws; and its name as refusals say it.</summary>
    internal sealed record ComposedFetSite(string Cmodel, int Ports, string Name);

    /// <summary>No ComposedFET converted: a refresh re-copies a model folder, whose FETs were converted when it was exported.</summary>
    private static readonly IReadOnlyDictionary<(string Cell, string Instance), ComposedFetSite> NoFets
        = new Dictionary<(string, string), ComposedFetSite>();

    /// <summary>The <c>.cmodel</c> a placed ComposedFET loads, absolute, or null when its File names none on disk.</summary>
    private static string? ComposedFetFile(EditableComponent comp, string schDir)
    {
        string v = (comp.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression ?? "").Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Trim();
        if (v.Length == 0) return null;
        string? resolved = SnpPathPolicy.Resolve(v, OwningRoot.Of(schDir), schDir);
        return resolved is not null && File.Exists(resolved) ? Path.GetFullPath(resolved) : null;
    }

    /// <summary>Every ComposedFET's model written as Verilog-A now, so a model that cannot be (a table constituent) is a
    /// refusal before anything is written; and the proof's bias points.</summary>
    private static List<ProtectBias> PlanFetConversion(Walk walk, ExportOptions options, IReadOnlyList<string> pins, List<string> refusals)
    {
        foreach (var site in walk.Fets.Values.DistinctBy(f => f.Cmodel, DocumentFileRefs.PathComparer))
        {
            try { _ = FetVerilogA(site.Cmodel); }
            catch (VerilogAExportException ex)
            {
                foreach (var r in ex.Refusals)
                    refusals.Add($"{site.Name} (ComposedFET) cannot be compiled to Verilog-A: {r.Field}: {r.Sentence} Refit it, or export without Compile Verilog-A models.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CmodelFormatException)
            {
                refusals.Add($"{site.Name} (ComposedFET): '{site.Cmodel}' could not be read ({ex.Message}).");
            }
        }

        var bias = new List<ProtectBias>();
        foreach (string b in options.CheckBias)
        {
            if (ProtectBias.Parse(b, pins, out string? bad) is { } parsed) bias.Add(parsed);
            else refusals.Add($"The bias point '{b}' cannot be read: {bad}");
        }
        if (options.CheckBias.Count == 0) bias.AddRange(ProtectBias.Defaults(pins));
        return bias;
    }

    /// <summary>The module Model Composer's Verilog-A export writes for <paramref name="cmodel"/>, named after the file.</summary>
    private static string FetVerilogA(string cmodel)
        => CmodelVerilogA.Write(CmodelFile.Read(cmodel),
                                NetlistVerilogA.Options(ModuleNameFor(Path.GetFileNameWithoutExtension(cmodel)), cmodel));

    /// <summary>
    /// The copied schematic's ComposedFET, re-pointed: a VerilogA instance of the compiled build (its File was already
    /// re-pointed at <c>models/&lt;name&gt;.osdi</c> by the file walk). Its values are kept — the module's parameters have
    /// the ComposedFET's names — except a switch's true/false, which a compiled module reads as 1/0.
    ///
    /// <para><b>It must draw the same glyph, pin for pin.</b> A schematic connects by geometry, so a VerilogA box in the
    /// FET's place leaves every wire ending on nothing: the compiled part then simulates unconnected, which the proof
    /// caught. The VerilogA's stored <c>Terminals</c> (the module's g, d, s and the thermal t) make its default symbol
    /// the FET glyph a ComposedFET draws — three pins, or four with the thermal pin — and a library symbol the FET
    /// named is named again.</para>
    /// </summary>
    private static void AsCompiledFet(JsonObject node, ComposedFetSite fet)
    {
        node["Symbol"] = nameof(SymbolKind.VerilogA);
        var parameters = node["Parameters"] as JsonArray ?? [];
        node["Parameters"] = parameters;
        string? symbol = parameters.OfType<JsonObject>().FirstOrDefault(p => p["Name"]?.GetValue<string>() == ComposedFetSymbolProvider.SymbolParameter) is { } sp
            ? Expression(sp)?.Trim().Trim('"') : null;
        if (string.IsNullOrEmpty(symbol)) symbol = ComposedFetSymbolProvider.ModelSymbol(fet.Cmodel, null)?.Trim() ?? "";
        // The standard glyph is what the default draws from the terminals; a .csym was relative to the .cmodel, which
        // does not ship, so it takes the standard glyph too.
        if (symbol == ComposedFetSymbolProvider.ThermalGlyph || symbol.EndsWith(".csym", StringComparison.OrdinalIgnoreCase)) symbol = "";
        foreach (var p in parameters.OfType<JsonObject>()
                                    .Where(p => p["Name"]?.GetValue<string>() is "Symbol" or "Pins" or "Terminals").ToList())
            parameters.Remove(p);
        foreach (var p in parameters.OfType<JsonObject>())
            if (Expression(p)?.Trim() is { } e && (e.Equals("true", StringComparison.OrdinalIgnoreCase) || e.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                p.Remove("Value");
                p["Expression"] = e.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
            }
        // The module has the thermal terminal only when the model self-heats, and so does the standard glyph; a part's
        // stored port count may say four regardless.
        bool thermal = ComposedFetSymbolProvider.ModelSelfHeats(fet.Cmodel, null);
        int pins = thermal ? fet.Ports : Math.Min(fet.Ports, 3);
        parameters.Add(SnpParameter("Pins", pins.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(SnpParameter(VerilogASymbolProvider.TerminalsParameter,
            VerilogASymbolProvider.FormatTerminals(thermal ? ["g", "d", "s", ComposedNetworkTemplate.ThermalNet] : ["g", "d", "s"],
                                                   thermal ? [3] : [])));
        parameters.Add(SnpParameter(VerilogASymbolProvider.SymbolParameter, symbol));
    }

    /// <summary>The proof of an export that converted ComposedFETs: the source cell against the exported model.</summary>
    private static ProtectCheckResult ProveFets(ExportPlan plan, ExportOptions options, string modelDir, ModelPartManifest manifest)
    {
        string? srcText = ModelNetlist.FromCell(plan.CellDir, out string? why);
        if (srcText is null) return new ProtectCheckResult(double.NaN, "", 0, $"The proof could not extract the source cell: {why}");
        string srcSch = PrimaryPath(plan.CellDir, ViewType.Schematic) ?? plan.CellDir;
        string? srcBase = SchematicCircuit.ReferenceBaseOfDirectory(Path.GetDirectoryName(srcSch));
        string srcCell = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.CellDir)));
        string modelText = File.ReadAllText(ModelNetlist.PathOf(modelDir));
        double[] freqs = ProtectCheck.Frequencies(manifest.Frequency ?? FrequencyRange(plan, options));
        // Two ambients: a ComposedFET's temperature defaults to the design's, which the compiled module must follow too.
        return ProtectCheck.Run(srcText, srcCell, srcBase, modelText, manifest.Cell, modelDir, manifest.Pins,
                                plan.FetBias, freqs, thermal: true);
    }

    private sealed partial class FileCopier
    {
        /// <summary>A <c>.cmodel</c> written as Verilog-A in a scratch folder, compiled there, and shipped as this
        /// platform's build under the model's name. The Verilog-A is not kept: the <c>.cmodel</c> it came from is the
        /// source, and it stays with its maker.</summary>
        private string CopyFetBuild(string cmodel, string models)
        {
            string stem = Path.GetFileNameWithoutExtension(cmodel);
            string scratch = Path.Combine(Path.GetTempPath(), "crf-fet-va-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(scratch);
            try
            {
                string va = Path.Combine(scratch, stem + ".va");
                File.WriteAllText(va, FetVerilogA(cmodel));
                string build = VerilogASourceCompiler.Compile(va, out string note);
                CompileNotes.Add(note);
                return CopyBuild(stem, build, models);
            }
            finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
