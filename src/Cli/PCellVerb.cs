// `circuitrf pcell new | check | render | export | import` — the PCell Designer with no window (brief-pcd-7).
//
// ONE verb with nouns, as `new`, `model` and `yield` are, and it owns no designing: every noun calls the src/Design
// function the window's own command calls — PCellDesign.Create, PCellDesignCheck.Run, DesignedCellLayout.At (drawn by
// `render`'s own layout path), PCellPackage.Export and PCellPackage.Import. What is here is argument parsing, refusals
// and reporting, on Authoring.cs' terms. Pcd7PCellVerbTests scans this file, comments stripped, for exactly that.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

internal static class PCellVerb
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            if (args.Length == 0) JsonRun.Report(Error("pcell.args.noun-required", "pcell: say what to do: new, check, render, export or import."));
            return Usage();
        }
        string noun = args[0].ToLowerInvariant();
        JsonRun.Verb = "pcell " + noun;
        return noun switch
        {
            "new"    => New(args[1..]),
            "check"  => Check(args[1..]),
            "render" => RenderAt(args[1..]),
            "export" => Export(args[1..]),
            "import" => Import(args[1..]),
            _        => Refused(Error("pcell.args.unknown-noun", $"pcell: '{noun}' is not a noun: new, check, render, export or import.")),
        };
    }

    // ── new ──────────────────────────────────────────────────────────────────

    /// <summary>R-pcd7-1: Tools ▸ PCell Designer…'s new cell, through the one function the window calls.</summary>
    private static int New(string[] args)
    {
        if (Positionals(args, 2, [], out _) is not { } pos) return Usage();
        if (pos.Count < 2) return Refused(Error("pcell.args.required", "pcell new: a workspace (or a folder inside one) and a cell name are required."));
        JsonRun.InputPath = pos[0];

        string parentDir = Path.GetFullPath(pos[0]);
        if (File.Exists(parentDir) && Path.GetFileName(parentDir).Equals(DocumentKinds.CwsFileName, StringComparison.OrdinalIgnoreCase))
            parentDir = Path.GetDirectoryName(parentDir)!;
        if (!Directory.Exists(parentDir))
            return JsonRun.Fail(Error("pcell.cell.not-found", $"No such folder: {parentDir}"));
        if (NameValidator.Validate(pos[1]) is { } bad)
            return JsonRun.Fail(Error("pcell.cell.invalid-name", $"pcell new: '{pos[1]}' is not a cell name: {bad}"));
        if (Directory.Exists(Path.Combine(parentDir, pos[1])))
            return JsonRun.Fail(Error("pcell.cell.exists", $"A cell named '{pos[1]}' already exists here. Nothing was created."));

        try
        {
            var created = PCellDesign.Create(parentDir, pos[1]);
            foreach (string path in created.Paths) Console.WriteLine(path);
            JsonRun.AddOutput("cell", created.CellDir);
            if (created.SchematicPath is { } sch) JsonRun.AddOutput("schematic", sch);
            if (created.SymbolPath is { } sym) JsonRun.AddOutput("symbol", sym);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JsonRun.Fail(Error("pcell.new.refused", $"The designed cell was not created: {ex.Message}"));
        }
    }

    // ── check ────────────────────────────────────────────────────────────────

    /// <summary>R-pcd7-2: <see cref="PCellDesignCheck.Run"/>, printed and exited as <c>check</c> prints and exits.</summary>
    private static int Check(string[] args)
    {
        if (Positionals(args, 1, ["--severity"], out var flags) is not { } pos) return Usage();
        bool warningsFail = false;
        if (flags.TryGetValue("--severity", out string? severity))
        {
            if (severity.ToLowerInvariant() is not ("warning" or "error"))
                return JsonRun.Fail(Error("pcell.args.unknown-severity", $"pcell check: --severity takes warning or error, got '{severity}'."));
            warningsFail = severity.Equals("warning", StringComparison.OrdinalIgnoreCase);
        }
        if (DesignedCell(pos, "check") is not { } cellDir) return 1;

        var (errors, warnings, notes) = Forward(PCellDesignCheck.Run(cellDir));
        Console.WriteLine($"{Path.GetFileName(cellDir)}: {errors} error(s), {warnings} warning(s), {notes} note(s).");
        return errors > 0 || (warningsFail && warnings > 0) ? 1 : 0;
    }

    // ── render ───────────────────────────────────────────────────────────────

    /// <summary>
    /// R-pcd7-3: the cell's layout at the values (<see cref="DesignedCellLayout.At"/>), drawn by <c>render</c>'s own layout
    /// path as the cell's <c>.clay</c> would be (brief-pcd-7 D2). Every argument but the cell and <c>--set</c> is
    /// <c>render</c>'s, and <c>render</c> reads it.
    /// </summary>
    private static int RenderAt(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            return Refused(Error("pcell.args.required", "pcell render: the cell folder comes first: pcell render <cell> -o <out.svg|.pdf|.png>."));

        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        var forwarded = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] != "--set") { forwarded.Add(args[i]); continue; }
            string set = i + 1 < args.Length ? args[++i] : "";
            int eq = set.IndexOf('=');
            Value? value = null;
            try { if (eq > 0) value = new Evaluator().Eval(set[(eq + 1)..], new Scope("pcell")); }
            catch (Exception ex) when (ex is ExpressionException or InvalidOperationException or ArgumentException) { }
            if (value is not { Kind: ValueKind.Real } real)
                return JsonRun.Fail(Error("pcell.args.set-malformed",
                    $"pcell render: --set takes name=value, the value a number with its unit (Lpad=200um), got '{set}'."));
            values[set[..eq].Trim()] = real;
        }

        if (DesignedCell([args[0]], "render") is not { } cellDir) return 1;
        if (WorkspaceRootFinder.FindAncestorCws(cellDir) is not { } cws)
            return JsonRun.Fail(Error("pcell.cell.no-workspace", $"{cellDir} is in no workspace: a designed cell is planned in one."));

        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string clay = Path.Combine(layoutDir, CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName
                                              ?? Path.GetFileName(cellDir) + ".clay");
        string? techRef = File.Exists(clay) ? LayoutPersistence.LoadFromFile(clay).TechRef : null;
        var (tech, _) = TechnologyResolver.ResolveForDocument(techRef, clay, cws, new TechnologyCache());
        if (tech.Tech is null)
            return JsonRun.Fail(Error("pcell.cell.no-technology",
                $"{Path.GetFileName(cellDir)} resolves no technology, and the planner needs one: {string.Join(" ", tech.Diagnostics)}"));

        var planned = DesignedCellLayout.At(cellDir, values, Path.GetDirectoryName(cws)!, tech.Tech, tech.ResolvedPath);
        if (planned.Layout is not { } layout)
            return JsonRun.Fail(Error("pcell.render.refused", planned.Refusal ?? "The layout could not be planned."));
        Forward(planned.Lines.Select(l => Diagnostic.Create("pcell.render.plan",
            l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
            "{line}", ("line", l.InstanceName.Length > 0 ? $"{l.InstanceName}: {l.Text}" : l.Text))));

        return Render.RunLayout([.. forwarded], clay, layout);
    }

    // ── export, import ───────────────────────────────────────────────────────

    /// <summary>R-pcd7-4: Export… — <see cref="PCellPackage.Export"/>, which runs the design check first.</summary>
    private static int Export(string[] args)
    {
        if (Positionals(args, 1, ["-o", "--output"], out var flags) is not { } pos) return Usage();
        string? output = flags.GetValueOrDefault("-o") ?? flags.GetValueOrDefault("--output");
        if (output is null) return Refused(Error("pcell.args.required", "pcell export: -o <file.cpcell> is required."));
        if (DesignedCell(pos, "export") is not { } cellDir) return 1;
        return Published(PCellPackage.Export(cellDir, Path.GetFullPath(output)), "cpcell");
    }

    /// <summary>R-pcd7-4: File ▸ Import ▸ PCell Package… — <see cref="PCellPackage.Import"/>.</summary>
    private static int Import(string[] args)
    {
        if (Positionals(args, 1, ["--into"], out var flags) is not { } pos) return Usage();
        if (pos.Count == 0 || !flags.TryGetValue("--into", out string? into))
            return Refused(Error("pcell.args.required", "pcell import: a .cpcell and --into <workspace-or-library-folder> are required."));
        JsonRun.InputPath = pos[0];
        if (!File.Exists(pos[0])) return JsonRun.Fail(Error("pcell.cell.not-found", $"No such file: {pos[0]}"));
        string libraryDir = Path.GetFullPath(into);
        if (File.Exists(libraryDir) && Path.GetFileName(libraryDir).Equals(DocumentKinds.CwsFileName, StringComparison.OrdinalIgnoreCase))
            libraryDir = Path.GetDirectoryName(libraryDir)!;
        if (!Directory.Exists(libraryDir)) return JsonRun.Fail(Error("pcell.cell.not-found", $"No such folder: {libraryDir}"));
        return Published(PCellPackage.Import(Path.GetFullPath(pos[0]), libraryDir), "cell");
    }

    private static int Published(PCellPackageOutcome outcome, string kind)
    {
        Forward(outcome.Findings);
        if (outcome.Path is not { } path) return 1;
        Console.WriteLine(path);
        JsonRun.AddOutput(kind, path);
        return 0;
    }

    // ── the shell ────────────────────────────────────────────────────────────

    /// <summary>The positionals, at most <paramref name="max"/>, and the value of each of <paramref name="valued"/>.
    /// Null after reporting an unknown option or one positional too many.</summary>
    private static List<string>? Positionals(string[] args, int max, string[] valued, out Dictionary<string, string> flags)
    {
        flags = new(StringComparer.Ordinal);
        var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (valued.Contains(args[i]) && i + 1 < args.Length) { flags[args[i]] = args[++i]; continue; }
            if (args[i].StartsWith('-'))
            { JsonRun.Report(Error("pcell.args.unknown-option", $"pcell: unknown option '{args[i]}'.")); return null; }
            if (pos.Count == max)
            { JsonRun.Report(Error("pcell.args.extra-argument", $"pcell: unexpected argument '{args[i]}'.")); return null; }
            pos.Add(args[i]);
        }
        return pos;
    }

    /// <summary>The cell folder named first, when it is a designed cell; null after the refusal.</summary>
    private static string? DesignedCell(IReadOnlyList<string> pos, string noun)
    {
        if (pos.Count == 0)
        { JsonRun.Report(Error("pcell.args.required", $"pcell {noun}: a designed cell's folder is required.")); Usage(); return null; }
        JsonRun.InputPath = pos[0];
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pos[0]));
        if (!Directory.Exists(dir)) { JsonRun.Fail(Error("pcell.cell.not-found", $"No such folder: {dir}")); return null; }
        if (!PCellDesign.IsDesigned(dir))
        {
            JsonRun.Fail(Error("pcell.cell.not-designed",
                $"{Path.GetFileName(dir)} is not a designed cell (its {CellFolder.CcellFileName} has no PCellDesign). "
              + "'circuitrf render' and 'circuitrf check' take any cell."));
            return null;
        }
        return dir;
    }

    /// <summary>Each finding on stderr as <c>check</c> prints one, and in <c>--json</c>; the counts by severity.</summary>
    private static (int Errors, int Warnings, int Notes) Forward(IEnumerable<Diagnostic> findings)
    {
        int e = 0, w = 0, n = 0;
        foreach (var d in findings)
        {
            if (d.Severity == DiagnosticSeverity.Error) e++;
            else if (d.Severity == DiagnosticSeverity.Warning) w++;
            else n++;
            Console.Error.WriteLine(d.Severity switch
            {
                DiagnosticSeverity.Error   => "error: ",
                DiagnosticSeverity.Warning => "warning: ",
                _                          => "note: ",
            } + d.Render());
            JsonRun.Note(d);
        }
        return (e, w, n);
    }

    private static Diagnostic Error(string id, string sentence)
        => Diagnostic.Create(id, DiagnosticSeverity.Error, "{why}", ("why", sentence));

    private static int Refused(Diagnostic d)
    {
        JsonRun.Report(d);
        return Usage();
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf pcell new <workspace-or-dir> <name>\n" +
            "       circuitrf pcell check <cell> [--severity warning|error]\n" +
            "       circuitrf pcell render <cell> -o <out.svg|.pdf|.png> [--set name=value]... [render's viewport, layer\n" +
            "                        and theme flags: --fit | --window | --center --span, --size, --layers, --theme, ...]\n" +
            "       circuitrf pcell export <cell> -o <file.cpcell>\n" +
            "       circuitrf pcell import <file.cpcell> --into <workspace-or-library-folder>");
        return 1;
    }
}
