using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf netlist &lt;path&gt; [-o out.cnl]</c> — the extraction a schematic goes through on its
/// way to being simulated, as a document a caller can hold (brief-automation-11-missing-verbs.md
/// R-aut11-1).
///
/// <para><b>Why this verb is the most valuable thing in the series.</b> Without it the automation
/// surface could not simulate any design a person had actually drawn: it ran hand-authored netlists
/// only. <c>check</c> and <c>explain</c> both accepted a `.csch` happily, so the surface read as
/// though a run would too — and what a run did instead was hand the JSON document to
/// <c>CnlReader</c> and report its first key as a missing cell name. Worse, an extraction is the
/// REFERENCE ANSWER a client checks its own authoring against: AUT-7 §3's false defect report — a
/// working component written up as broken — came from a one-net instance line that one look at a
/// known-good extraction would have settled.</para>
///
/// <para><b>It owns no extraction.</b> Every byte comes from <see cref="CircuitSource.CnlTextOf"/>,
/// which is <c>NetExtractor.Extract</c> followed by <c>CnlWriter.Write</c> — the first half of the
/// round trip the GUI's own Simulate performs and every run verb now performs in memory. So the file
/// this writes is not merely equivalent to what a run consumes; it is the same bytes. What is here is
/// argument parsing, target resolution, refusals and reporting, on <c>src/Cli/Authoring.cs</c>'
/// terms.</para>
///
/// <para><b>A BOARD is the same sentence about a different document</b>
/// (brief-authored-board-3-companion-writers.md R-ab3-2). <c>netlist</c> writes the extraction
/// Simulate performs; a board netlist, a placement table and a bill of materials are the extraction
/// a LAYOUT performs, so they are this verb and not a fourth noun — and certainly not a
/// <c>convert</c> pair, since <c>convert</c> is artwork to artwork and every one of its readers
/// lands on a cell folder plus a technology. <b><c>-o</c> alone on a <c>.clay</c> is a refusal
/// naming the three flags</b>: two of the three tables are <c>.csv</c> and the extension cannot say
/// which, and "which table" is exactly what a dialog would ask. More than one in a run is ordinary
/// and encouraged — one invocation, ONE projection, three files that cannot disagree.</para>
///
/// <para><b>A cell folder and a workspace resolve as <c>render</c> resolves them</b> — through
/// <see cref="CellLookup"/> and <c>CellFolder.ResolvePrimary</c>, the same two functions, so the cell
/// this extracts is the cell that verb would have drawn. There is no <c>--view</c>: a netlist comes
/// out of a schematic and out of nothing else, so the view is never a question.</para>
/// </summary>
internal static class Netlist
{
    public static int Run(string[] args)
    {
        string? path = null, output = null, cell = null;
        string? ipc = null, placement = null, bom = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output" when i + 1 < args.Length: output    = args[++i]; continue;
                case "--cell" when i + 1 < args.Length:           cell      = args[++i]; continue;
                case "--ipc" when i + 1 < args.Length:            ipc       = args[++i]; continue;
                case "--placement" when i + 1 < args.Length:      placement = args[++i]; continue;
                case "--bom" when i + 1 < args.Length:            bom       = args[++i]; continue;
                default:
                    if (args[i].StartsWith('-'))
                    { JsonRun.Report(CliDiagnostics.NetlistUnknownOption(args[i])); return Usage(); }
                    if (path is not null)
                    { JsonRun.Report(CliDiagnostics.NetlistMultiplePaths()); return Usage(); }
                    path = args[i];
                    continue;
            }
        }

        if (path is null) { JsonRun.Report(CliDiagnostics.NetlistPathRequired()); return Usage(); }
        JsonRun.InputPath = path;

        if (!File.Exists(path) && !Directory.Exists(path))
            return JsonRun.Fail(CliDiagnostics.NetlistPathNotFound(path));

        // R-ab3-2. WHICH DOCUMENT this is decides which of the two extractions runs, and the board
        // flags decide it for a cell folder that holds both views — a cell with a schematic and a
        // layout is the ordinary case, and asking for a placement table out of it is unambiguous.
        bool board = ipc is not null || placement is not null || bom is not null;
        var kind = DocumentKinds.Classify(path);
        if (kind == DocumentKind.Layout || (board && kind == DocumentKind.Cell))
            return Board.Run(path, kind, ipc, placement, bom, output);

        if (board)
            return JsonRun.Fail(CliDiagnostics.NetlistBoardFlagsOnNonBoard(
                path, DocumentKinds.Name(kind)));

        // The extension decides the format everywhere else on this surface, so it decides here too:
        // `-o plot.svg` is a caller that meant a different verb, and writing netlist text into it
        // would be obeyed silently.
        if (output is not null
            && !string.Equals(Path.GetExtension(output), ".cnl", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.NetlistOutputNotCnl(
                output, Path.GetExtension(output) is { Length: > 0 } e ? e : "(none)"));

        var (csch, refusal) = Resolve(path, cell, kind);
        if (refusal is { } r) return r;

        string cnl;
        try { cnl = CircuitSource.CnlTextOf(csch!); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistExtractionFailed(csch!, ex.Message)); }

        if (output is null)
        {
            // Verbatim on stdout, Write rather than WriteLine, so `circuitrf netlist x.csch > x.cnl`
            // is the file and not the file with a newline added to it — `read`'s own rule.
            JsonRun.Document = new RfCore.Export.DocumentJson(
                csch!, DocumentKinds.Name(DocumentKind.Netlist), cnl);
            Console.Out.Write(cnl);
            Console.Error.WriteLine($"[circuitRF] extracted {Path.GetFileName(csch!)} ({cnl.Length:N0} chars)");
            return 0;
        }

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            // No BOM and no re-encoding: CnlWriter's text is what CnlReader reads, and a preamble
            // in front of the first `;` comment is a byte a caller comparing two extractions sees.
            File.WriteAllText(output, cnl, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistWriteFailed(output, ex.Message)); }

        JsonRun.AddOutput("cnl", output);
        Console.WriteLine(output);
        Console.Error.WriteLine($"[circuitRF] extracted {Path.GetFileName(csch!)} -> {output}");
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf netlist <path.csch | cell-folder | workspace --cell N> [-o out.cnl]");
        Console.Error.WriteLine(
            "       circuitrf netlist <path.clay | cell-folder> "
          + "[--ipc out.ipc] [--placement out.csv] [--bom out.csv]");
        return 1;
    }

    /// <summary>
    /// Which schematic this run extracts. The kind comes from <see cref="DocumentKinds.Classify"/>,
    /// exactly as <c>check</c> and <c>render</c> infer it.
    /// </summary>
    private static (string? Csch, int? Refusal) Resolve(string path, string? cell, DocumentKind kind)
    {
        switch (kind)
        {
            case DocumentKind.Schematic:
                return (path, null);

            case DocumentKind.Cell:
                return FromCellFolder(path);

            case DocumentKind.Workspace:
            {
                string root = Directory.Exists(path)
                    ? Path.GetFullPath(path)
                    : Path.GetDirectoryName(Path.GetFullPath(path))!;
                if (cell is null)
                    return (null, JsonRun.Fail(CliDiagnostics.NetlistCellRequired(path)));

                var found = CellLookup.Find(root, cell);
                if (found.Count == 0)
                    return (null, JsonRun.Fail(CliDiagnostics.NetlistNoSuchCell(
                        path, cell, Join(CellLookup.Names(root)))));
                if (found.Count > 1)
                    return (null, JsonRun.Fail(CliDiagnostics.NetlistAmbiguousCell(cell, Join(found))));
                return FromCellFolder(found[0]);
            }

            // A netlist is what this verb PRODUCES. Re-emitting one through the reader and the writer
            // would hand back a file that is not the one given — comments gone, directives reordered —
            // and call it an extraction.
            case DocumentKind.Netlist:
                return (null, JsonRun.Fail(CliDiagnostics.NetlistAlreadyANetlist(path)));

            default:
                return (null, JsonRun.Fail(CliDiagnostics.NetlistNotASchematic(path, DocumentKinds.Name(kind))));
        }
    }

    private static (string? Csch, int? Refusal) FromCellFolder(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        if (primary.State is PrimaryState.NoView)
            return (null, JsonRun.Fail(CliDiagnostics.NetlistNoSchematicView(cellDir)));

        if (primary.ResolvedName is not { Length: > 0 } name)
            return (null, JsonRun.Fail(CliDiagnostics.NetlistNoPrimary(cellDir, primary.State.ToString())));

        return (Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name), null);
    }

    private static string Join(IReadOnlyList<string> items)
        => items.Count == 0 ? "(none)" : string.Join(", ", items);
}
