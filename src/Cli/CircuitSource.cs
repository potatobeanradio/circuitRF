using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Cli;

/// <summary>
/// The <c>(Library, TestBench)</c> a document holds — read the way the APPLICATION reads it, which
/// for a schematic is not the obvious way.
///
/// <para><b>The schematic half is <see cref="SchematicCircuit"/> and this file keeps no copy of
/// it.</b> It lived here until brief-lvs-4-schematic-netlist.md; it moved to <c>src/Design</c>
/// because LVS reads a schematic too and cannot reference <c>src/Cli</c>, and a second copy of
/// <c>NetExtractor.Extract → CnlWriter.Write → CnlReader.Read</c> would eventually mean the
/// netlist LVS compares is not the netlist the design runs as. That type's own remarks say why
/// the round trip is load-bearing and why nothing is written.</para>
///
/// <para>What is left here is what a CLI VERB needs on top of it: classification by document kind,
/// a run verb's refusal, and the "does this bench declare anything runnable" question.</para>
/// </summary>
internal static class CircuitSource
{
    /// <summary>
    /// Reads <paramref name="path"/> as a circuit, or returns null when it holds none.
    /// <paramref name="onError"/> receives the reader's own message; a caller that wants to report
    /// it as its own diagnostic supplies one.
    /// </summary>
    public static (Library Lib, TestBench Tb)? Read(
        string path, DocumentKind kind, Action<string>? onError = null)
        => Read(path, kind, out _, onError);

    /// <inheritdoc cref="Read(string, DocumentKind, Action{string}?)"/>
    /// <param name="parts">The placed vendor parts the schematic's extraction found (<see cref="NetExtractor.ExtractionResult.PartTunables"/>),
    /// which the <c>.cnl</c> round trip cannot carry: a run constructor asked about this bench needs them to know a
    /// part's value (<c>C1.C</c>) is a tunable. For a netlist, the parts its own <c>SpiceModel</c> lines place.</param>
    public static (Library Lib, TestBench Tb)? Read(
        string path, DocumentKind kind, out IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable> parts,
        Action<string>? onError = null)
    {
        parts = [];
        try
        {
            switch (kind)
            {
                case DocumentKind.Netlist:
                {
                    var (lib, tb) = CnlTechnologyBinding.ReadFile(path, out parts);
                    return (lib, tb);
                }

                case DocumentKind.Schematic:
                    return FromSchematic(path, out parts);

                case DocumentKind.Cell:
                {
                    // A cell folder's PRIMARY schematic is what a reference to that cell resolves
                    // to, so it is the one this question is about.
                    var primary = CellFolder.ResolvePrimary(path, ViewType.Schematic);
                    if (primary.ResolvedName is not { } file) return null;
                    return FromSchematic(Path.Combine(
                        CellFolder.SubFolderPath(path, ViewType.Schematic), file), out parts);
                }

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
            return null;
        }
    }

    /// <inheritdoc cref="SchematicCircuit.FromSchematic(string, string?)"/>
    /// <remarks>With this invocation's <c>--workspace</c> as the host, and the cells it chose reported
    /// (<see cref="ReportHostCellChoices"/>).</remarks>
    public static (Library Lib, TestBench Tb) FromSchematic(string cschPath) => FromSchematic(cschPath, out _);

    private static (Library Lib, TestBench Tb) FromSchematic(
        string cschPath, out IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable> parts)
    {
        var (extracted, name) = ExtractFile(cschPath);
        parts = extracted.PartTunables;
        return SchematicCircuit.FromExtraction(extracted, name, cschPath, CliEntry.HostWorkspace);
    }

    /// <summary><see cref="SchematicCircuit.ExtractFile"/> with this invocation's <c>--workspace</c>,
    /// its host-cell choices reported.</summary>
    public static (NetExtractor.ExtractionResult Extracted, string Name) ExtractFile(string cschPath)
    {
        WorkspaceKits.EnsureLoadedFor(cschPath);
        var result = SchematicCircuit.ExtractFile(cschPath, CliEntry.HostWorkspace);
        ReportHostCellChoices(result.Extracted);
        return result;
    }

    /// <summary><see cref="PreparedCircuit.FromSchematic"/> with this invocation's <c>--workspace</c>, its
    /// host-cell choices reported — what <c>opt</c>, <c>yield</c> and <c>fields</c> read a schematic with.</summary>
    public static PreparedCircuit PrepareSchematic(string cschPath)
    {
        WorkspaceKits.EnsureLoadedFor(cschPath);
        var circuit = PreparedCircuit.FromSchematic(cschPath, CliEntry.HostWorkspace);
        ReportHostCellChoices(circuit.HostCellChoices);
        ReportConflicts(circuit.ExtractionConflicts, circuit.PartRefusals);
        ReportVendorParts(circuit.PartRefusals, circuit.PartRangeWarnings);
        return circuit;
    }

    /// <summary>
    /// What a RUN says on stderr about its placed vendor parts — the sentences the GUI posts to
    /// Messages: each part that could not be read (R-vl3-2c: a refusal, never a silent skip) and each
    /// part the analyses take outside its characterised range (R-vl3-3b). Warnings, as the GUI posts
    /// them: the exit code is the run's own. Only a run says them — <c>explain</c> and <c>check</c>
    /// extract too, and report the same facts their own way.
    /// </summary>
    public static void ReportVendorParts(IReadOnlyList<string> refusals, IReadOnlyList<string> rangeWarnings)
    {
        foreach (var text in refusals)
        {
            var d = CliDiagnostics.VendorPartUnreadable(text);
            Console.Error.WriteLine("warning: " + d.Render());
            JsonRun.Note(d);
        }
        foreach (var text in rangeWarnings)
        {
            var d = CliDiagnostics.VendorPartOutOfRange(text);
            Console.Error.WriteLine("warning: " + d.Render());
            JsonRun.Note(d);
        }
    }

    /// <summary>
    /// What the extraction could not build, as the GUI posts it after Simulate — an instance it SKIPPED
    /// above all, which is otherwise simply absent from the netlist with nothing said (brief-gdf-1: a kit
    /// part whose symbol and subcircuit disagree on their terminal count vanished from a headless run).
    /// Warnings; the exit code is the run's own. A vendor part's refusal is also a conflict and is said
    /// by <see cref="ReportVendorParts"/>, so it is not said twice.
    /// </summary>
    public static void ReportConflicts(NetExtractor.ExtractionResult extracted)
        => ReportConflicts(extracted.Conflicts, extracted.PartRefusals);

    /// <summary><see cref="ReportConflicts(NetExtractor.ExtractionResult)"/> for a circuit already
    /// prepared — what <c>opt</c>, <c>yield</c> and <c>fields</c> read a schematic as.</summary>
    public static void ReportConflicts(IReadOnlyList<string> conflicts, IReadOnlyList<string> partRefusals)
    {
        var saidElsewhere = new HashSet<string>(partRefusals, StringComparer.Ordinal);
        foreach (var text in conflicts)
        {
            if (saidElsewhere.Contains(text)) continue;
            var d = CliDiagnostics.RunExtractionConflict(text);
            Console.Error.WriteLine("warning: " + d.Render());
            JsonRun.Note(d);
        }
    }

    /// <summary>
    /// A schematic read from disk for a verb, with this invocation's <c>--workspace</c> as its host
    /// (<see cref="SchematicEditModel.HostWorkspaceRoot"/>). Every verb that RESOLVES a schematic's
    /// cells reads it through here, so a loose schematic finds the same cells in every verb that it
    /// finds in the workspace's window.
    /// </summary>
    public static (SchematicEditModel Model, CschViewState View, string CellName) LoadSchematic(string cschPath)
    {
        var loaded = SchematicPersistence.LoadFromFile(cschPath);
        loaded.model.HostWorkspaceRoot = CliEntry.HostWorkspace;
        return loaded;
    }

    /// <summary>
    /// Says on stderr which cell each loose-schematic instance was run with, where the
    /// <c>--workspace</c> it is run from was asked — the same sentences the GUI posts to Messages
    /// when it simulates. <c>warning: </c> for two candidates, <c>note: </c> for the one used.
    /// </summary>
    public static void ReportHostCellChoices(NetExtractor.ExtractionResult extracted)
        => ReportHostCellChoices(extracted.HostCellChoices);

    /// <inheritdoc cref="ReportHostCellChoices(NetExtractor.ExtractionResult)"/>
    public static void ReportHostCellChoices(IReadOnlyList<NetExtractor.HostCellChoice> choices)
    {
        foreach (var choice in choices)
        {
            var d = CliDiagnostics.HostCellChoice(choice.Text, choice.IsWarning);
            Console.Error.WriteLine((choice.IsWarning ? "warning: " : "note: ") + d.Render());
            JsonRun.Note(d);
        }
    }

    /// <inheritdoc cref="SchematicCircuit.FromSchematic(SchematicEditModel, string, string?)"/>
    public static (Library Lib, TestBench Tb) FromSchematic(
        SchematicEditModel model, string testBenchName, string? sourceDir)
        => SchematicCircuit.FromSchematic(model, testBenchName, sourceDir);

    /// <inheritdoc cref="SchematicCircuit.CnlTextOf(SchematicEditModel, string)"/>
    public static string CnlTextOf(SchematicEditModel model, string testBenchName)
        => SchematicCircuit.CnlTextOf(model, testBenchName);

    /// <inheritdoc cref="SchematicCircuit.CnlTextOf(string, string?)"/>
    public static string CnlTextOf(string cschPath)
    {
        var (extracted, name) = ExtractFile(cschPath);
        ReportConflicts(extracted);
        return SchematicCircuit.CnlTextOf(extracted, name);
    }

    /// <summary>
    /// What a run verb's input resolves its relative file references against — a <c>.cnl</c> its own folder, as
    /// <c>CnlReader.ReadFile</c> reads it; a <c>.csch</c> the workspace root, as Simulate elaborates it
    /// (<see cref="SchematicCircuit.ReferenceBaseOf"/>). Every elaborator and every sweep engine of one run takes this
    /// one base: a schematic's folder handed to a sweep while the first elaboration read the working directory found a
    /// ComposedFET's <c>.cmodel</c> only when the verb ran from the workspace root.
    /// </summary>
    public static string? ReferenceBaseOf(string inputPath) =>
        DocumentKinds.Classify(inputPath) == DocumentKind.Schematic
            ? SchematicCircuit.ReferenceBaseOf(inputPath, CliEntry.HostWorkspace)
            : Path.GetDirectoryName(Path.GetFullPath(inputPath));

    /// <summary>
    /// A run verb's input: a `.cnl` read as itself, or a `.csch` EXTRACTED in memory (R-aut11-1).
    ///
    /// <para><b>Why a run verb takes a schematic at all.</b> Until this landed the automation
    /// surface could not simulate any design a person had actually drawn — it ran hand-authored
    /// netlists only, while <c>check</c> and <c>explain</c> both accepted a `.csch` happily, so the
    /// surface read as though a run would too. What it did instead was hand the JSON document to
    /// <c>CnlReader</c> and report its first key as a missing cell name.</para>
    ///
    /// <para><b>Any other kind is refused BY KIND</b>, naming what the path holds and what the verb
    /// takes. Returns null having already reported; <paramref name="refusal"/> is the exit code.
    /// Reader exceptions are NOT caught here — every caller already wraps its read in the try that
    /// turns one into <c>RunFailed</c>.</para>
    /// </summary>
    public static (Library Lib, TestBench Tb)? ReadRunInput(string verb, string path, out int refusal)
    {
        refusal = 0;
        var kind = DocumentKinds.Classify(path);

        switch (kind)
        {
            case DocumentKind.Netlist:
                // A netlist inside a workspace runs against that workspace's kits, as Simulate's own
                // written netlist does: a kit device names its provider and nothing else serves it.
                WorkspaceKits.EnsureLoadedFor(path);
                return CnlTechnologyBinding.ReadFile(path);

            case DocumentKind.Schematic:
            {
                var (extracted, name) = ExtractFile(path);
                ReportConflicts(extracted);
                ReportVendorParts(extracted.PartRefusals, extracted.PartRangeWarnings);
                // A placed cell the netlist would leave out is a refusal, never a run of a different circuit:
                // an amplifier with no model left out reads as an open and nothing else says so.
                if (extracted.RunRefusal is { } dropped)
                {
                    refusal = JsonRun.Fail(CliDiagnostics.RunFailed(dropped));
                    return null;
                }
                return SchematicCircuit.FromExtraction(extracted, name, path, CliEntry.HostWorkspace);
            }

            default:
                refusal = JsonRun.Fail(CliDiagnostics.RunWrongDocumentKind(
                    verb, path, DocumentKinds.Name(kind)));
                return null;
        }
    }

    /// <summary>
    /// Whether the netlist declares anything a run verb could dispatch. The GUI's own
    /// <c>RunStatus.NoAnalysis</c> test (<c>SchematicRunService.Prepare</c> step 2): a typed analysis,
    /// or a RAW <c>analysis … type=sparam</c> directive, which never becomes a typed one and is
    /// dispatched straight from its text.
    ///
    /// <para>Asked this way rather than through <c>ChainSelector</c> because chain selection is
    /// per-KIND: a bench declaring only an S-parameter sweep has no HB chain, and reporting that as
    /// "no analysis will dispatch" would warn about every S-parameter and DC bench in the tree.</para>
    /// </summary>
    public static bool DeclaresARunnableAnalysis(TestBench tb)
    {
        if (tb.Analyses.Count > 0) return true;

        foreach (var raw in tb.RawDirectives)
            if (raw.Kind == "analysis" && IsSparamRaw(raw.RawLine))
                return true;

        return false;
    }

    private static bool IsSparamRaw(string rawLine)
    {
        foreach (var t in rawLine.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
            if (t.Equals("type=sparam", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
