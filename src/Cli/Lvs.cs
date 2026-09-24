using System.Linq;
using System.Text;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf lvs &lt;path&gt;</c> — does the artwork implement the drawing?
/// (<c>brief-lvs-11-cli-verb.md</c>; <c>docs/design/lvs.md</c> §8.3.)
///
/// <para><b>This file holds no comparison logic and it must never start</b> (R-lvs11-1a). Every
/// finding it prints comes out of <see cref="LvsRun.Run"/> in <c>src/Design/Layout/Lvs</c> — the
/// same function the GUI panel calls, with the same arguments — and what is here is argument
/// parsing, refusals, reporting and one call, on <c>src/Cli/Authoring.cs</c>' terms: <i>an
/// operation that lives only in a verb is not a capability, and a verb that re-implements one
/// diverges from it silently</i>. A second extraction, partition, terminal derivation or
/// correspondence in this project would mean a design passed headlessly and was refused when
/// someone opened it, and nothing would report the drift. A comment-stripped source scan over
/// <c>src/Cli</c> holds it (R-lvs11-1b).</para>
///
/// <para><b>It is its own verb and is NOT folded into <c>check</c></b> (R-lvs11-1c, note R-lvs-54).
/// R-aut4-1 is explicit that <c>check</c> must be cheap enough to call after every edit and stops
/// at elaboration; an LVS on a real board is seconds rather than milliseconds, and a <c>check</c>
/// that had become slow is a <c>check</c> people stop running. What <c>check</c> gained from this
/// series is brief 1's terminal-map validation, which is cheap and static.</para>
///
/// <para><b>It writes exactly one thing and only when asked</b> (R-lvs11-3d): the human report,
/// under <c>-o</c>. With no <c>-o</c> it writes nothing at all — LVS is read-only on <c>check</c>'s
/// terms (R-aut4-6), so it runs on a read-only tree and on a workspace another process has
/// open.</para>
///
/// <para><b>It runs no analysis, ever.</b> There is no solve anywhere below this verb, which is why
/// §7's exit codes reduce to 0, 1 and 130: nothing here converges, so there is no 2
/// (R-lvs11-4e).</para>
/// </summary>
internal static class Lvs
{
    /// <summary>The severity at which a finding decides the exit code (R-lvs11-4a).</summary>
    private enum Threshold { Warning, Error }

    private sealed class Options
    {
        public string? Path;

        /// <summary>The only file this verb ever writes, and only when it is given (R-lvs11-3d).</summary>
        public string? Output;

        public bool Flat;
        public readonly List<string> FlattenCells = [];
        public bool TestBench;
        public bool NoReduce;

        /// <summary>R-lvs14-1d. Tier-3 recognition, OFF unless asked for — see the switch's own
        /// note in <see cref="Parse"/>.</summary>
        public bool Recognize;

        /// <summary>Repeatable, in the order given — <c>cli.md</c> §5's own spelling.</summary>
        public readonly List<LvsGlobalOverride> Sets = [];

        public Threshold Severity = Threshold.Error;
    }

    // ── entry ────────────────────────────────────────────────────────────────

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Path is null) { JsonRun.Report(CliDiagnostics.LvsPathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;

        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.LvsPathNotFound(o.Path));

        var (kind, cells, refusal) = Resolve(o.Path);
        if (refusal is { } no) return no;

        try
        {
            return Compare(o, kind, cells);
        }
        catch (OperationCanceledException)
        {
            // R-lvs11-4d, and it is `em`'s and `render`'s rule: the report is written after the
            // whole comparison has finished, so a cancelled run leaves no file rather than half of
            // one.
            JsonRun.Report(CliDiagnostics.LvsCancelled());
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf lvs <path> [--flat] [--flatten-cell <name>] [--testbench] [--no-reduce]\n" +
            "                     [--recognize] [--set var=expr] [--severity warning|error]\n" +
            "                     [-o report.txt]\n" +
            "  <path> is a cell folder, a workspace, a .clay or a .csch.");
        return 1;
    }

    // ── arguments ────────────────────────────────────────────────────────────

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; continue;

                case "--flat":      o.Flat      = true; continue;
                case "--testbench": o.TestBench = true; continue;
                case "--no-reduce": o.NoReduce  = true; continue;

                // R-lvs14-1d. Reading devices out of COPPER rather than out of instances. It is
                // opt-in per run because circuitRF's layout is instance-bearing: a design this
                // application authored already says what each part is, and re-recognising those
                // from geometry is less reliable than reading the instance that is right there.
                // It is also opt-in per TECHNOLOGY — a process with no DeviceRules block
                // recognises nothing, and saying so is not an error.
                case "--recognize" or "--recognise": o.Recognize = true; continue;

                // Repeatable, and by cell FOLDER name — R-lvs9-3d's own spelling. Using it is
                // reported at info by the run itself, so a design that quietly flattens everything
                // is visible in the report rather than only in the command line that produced it.
                case "--flatten-cell" when i + 1 < args.Length:
                    o.FlattenCells.Add(args[++i]);
                    continue;

                case "--set" when i + 1 < args.Length:
                {
                    // The same override every run verb takes, in the same place (cli.md §5): it
                    // REPLACES the variable in the design's own scope, so everything derived from
                    // it re-derives. R-lvs11-2d — a design whose values depend on a swept global
                    // has more than one correct layout, and a caller must be able to say which.
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) { JsonRun.Report(CliDiagnostics.SetMalformed("lvs", kv)); return Usage(); }
                    o.Sets.Add(new LvsGlobalOverride(kv[..eq].Trim(), kv[(eq + 1)..].Trim()));
                    continue;
                }

                case "--severity" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "warning": o.Severity = Threshold.Warning; break;
                        case "error":   o.Severity = Threshold.Error;   break;
                        default: return JsonRun.Fail(CliDiagnostics.LvsUnknownSeverity(args[i]));
                    }
                    continue;

                default:
                    if (args[i].StartsWith('-'))
                    { JsonRun.Report(CliDiagnostics.LvsUnknownOption(args[i])); return Usage(); }
                    if (o.Path is not null)
                    { JsonRun.Report(CliDiagnostics.LvsMultiplePaths()); return Usage(); }
                    o.Path = args[i];
                    continue;
            }
        }
        return null;
    }

    // ── what the path IS (R-lvs11-2a) ────────────────────────────────────────

    /// <summary>
    /// The cell folders to compare, in a stable order.
    /// </summary>
    /// <remarks>
    /// <b>The kind comes from the path</b>, through <see cref="DocumentKinds.Classify"/> — exactly
    /// as <c>check</c> and <c>render</c> infer it, and for a foreign extension through
    /// <c>convert</c>'s own content classifier, so a GDSII or Gerber file is NAMED rather than
    /// called unreadable (R-lvs11-2c).
    ///
    /// <para>A view file names the cell folder that holds it, because the unit of comparison is the
    /// CELL (note R-lvs-29): a <c>.clay</c> is compared against its sibling schematic and a
    /// <c>.csch</c> against its sibling layout, which is one call either way.</para>
    /// </remarks>
    private static (DocumentKind Kind, IReadOnlyList<string> Cells, int? Refusal) Resolve(string path)
    {
        var kind = DocumentKinds.Classify(path);

        switch (kind)
        {
            case DocumentKind.Cell:
                return (kind, [path], null);

            case DocumentKind.Workspace:
            {
                string root = Directory.Exists(path)
                    ? Path.GetFullPath(path)
                    : Path.GetDirectoryName(Path.GetFullPath(path))!;
                // Every cell the workspace holds, by SHAPE — `CellLookup` is the walk `render
                // --cell` and `explain --cells` already resolve through, so the three verbs cannot
                // disagree about which folders are cells.
                return (kind, CellLookup.All(root), null);
            }

            case DocumentKind.Layout:
            case DocumentKind.Schematic:
            {
                if (OwningCell(path) is { } cell) return (kind, [cell], null);
                return (kind, [], JsonRun.Fail(CliDiagnostics.LvsNotInACellFolder(
                    path, DocumentKinds.Name(kind))));
            }

            case DocumentKind.Interchange:
                return (kind, [], JsonRun.Fail(CliDiagnostics.LvsNotComparable(
                    path, DocumentKinds.InterchangeFormat(path) ?? "interchange")));

            default:
                return (kind, [], JsonRun.Fail(CliDiagnostics.LvsNotComparable(
                    path, DocumentKinds.Name(kind))));
        }
    }

    /// <summary>The cell folder a view file belongs to — its view sub-folder's parent, or the
    /// folder itself for a document sitting loose in one. Null when neither is a cell.</summary>
    private static string? OwningCell(string viewFile)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(viewFile));
        if (dir is null) return null;
        if (DocumentKinds.LooksLikeCellFolder(dir)) return dir;

        string? parent = Path.GetDirectoryName(dir);
        return parent is not null && DocumentKinds.LooksLikeCellFolder(parent) ? parent : null;
    }

    // ── the run: one call per cell, and nothing else (R-lvs11-1a) ────────────

    private static int Compare(Options o, DocumentKind kind, IReadOnlyList<string> cells)
    {
        var run = new LvsRunOptions
        {
            Flat           = o.Flat,
            FlattenCells   = new HashSet<string>(o.FlattenCells, StringComparer.OrdinalIgnoreCase),
            IncludeFixture = o.TestBench,
            Reduce         = o.NoReduce ? LvsReduceOptions.NoReduce : LvsReduceOptions.Default,
            Recognize      = o.Recognize,
            Set            = o.Sets,
        };

        foreach (var (name, expression) in o.Sets)
            Console.Error.WriteLine($"[circuitRF] set {name} = {expression}");

        var report   = new StringBuilder();
        var rows     = new List<LvsCellJson>();
        int errors   = 0, warnings = 0, waived = 0, compared = 0, skipped = 0, notes = 0;

        foreach (string cell in cells)
        {
            RunHost.Control?.ThrowIfCancellationRequested();
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(cell));

            // R-lvs11-2b. A cell with only one of the two views is the ordinary mid-design state,
            // so it is INFO and skipped — a verb that failed on it would be a verb nobody runs
            // mid-design. The sentence is `src/Design`'s own, not a second one authored here.
            if (Missing(cell) is { } absent)
            {
                var note = LvsDiagnostics.ViewMissing(name, CellFolder.SubFolderName(absent));
                notes++;
                skipped++;
                JsonRun.Note(note);
                rows.Add(Skipped(cell, name, note));
                report.AppendLine($"{name}: {note.Render()}");
                continue;
            }

            Console.Error.WriteLine($"[circuitRF] comparing '{name}'");
            var result = LvsRun.Run(cell, run, RunHost.Control);

            compared++;
            errors   += result.ErrorCount;
            warnings += result.WarningCount;
            waived   += result.WaivedCount;
            notes    += result.Findings.Count(f => !f.Waived && f.Severity <= DiagnosticSeverity.Info);

            foreach (var finding in result.Findings) JsonRun.Note(finding.Diagnostic);

            rows.Add(Project(cell, name, result));
            Write(report, name, result);
        }

        bool failed = o.Severity == Threshold.Error
            ? errors > 0
            : errors > 0 || warnings > 0;

        // R-lvs11-4b: warnings are reported either way, and only the threshold decides the code.
        // The alternative — hiding them to keep the exit code clean — makes the exit code useless,
        // which is `check`'s own rule for the same reason.
        report.AppendLine(
            $"{compared} cell(s) compared" +
            (skipped > 0 ? $", {skipped} skipped" : "") + ": " +
            $"{errors} error(s), {warnings} warning(s), {notes} note(s)" +
            (waived > 0 ? $", {waived} waived" : "") + ".");

        JsonRun.Lvs = new LvsReportJson(
            o.Path!, DocumentKinds.Name(kind),
            o.Severity == Threshold.Error ? "error" : "warning",
            o.Flat, !o.NoReduce, o.TestBench,
            compared, skipped, errors, warnings, waived,
            errors == 0 && warnings == 0,
            rows);

        // stdout is the RESULT (R-lvs11-3a) and the report is what this verb produces. Under
        // --json it goes to the sink `JsonRun` installed, which is how "nothing else on stdout"
        // stays structural rather than a rule every printer has to remember.
        Console.Write(report.ToString());

        if (o.Output is { } path)
        {
            // The ONLY write (R-lvs11-3d), and it happens here — after the whole comparison — so a
            // cancelled run has written nothing at all.
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, report.ToString());
            }
            catch (Exception ex)
            {
                return JsonRun.Fail(CliDiagnostics.LvsOutputFailed(path, ex.Message));
            }

            JsonRun.AddOutput("report", path);
            Console.Error.WriteLine($"[circuitRF] Wrote {path}");
        }

        return failed ? 1 : 0;
    }

    /// <summary>Which of the two views a cell folder does not resolve one primary file for, or
    /// null when it has both. <c>CellFolder.ResolvePrimary</c>'s answer and no other.</summary>
    private static ViewType? Missing(string cellDir)
    {
        if (CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName is not { Length: > 0 })
            return ViewType.Schematic;
        if (CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName is not { Length: > 0 })
            return ViewType.Layout;
        return null;
    }

    // ── the human report (R-lvs11-3b) ────────────────────────────────────────
    //
    // LvsReportText, in src/Design — the LVS panel's Copy writes the same text.

    private static void Write(StringBuilder into, string name, LvsRunResult result) =>
        LvsReportText.Write(into, name, result);

    // ── the --json projection (R-lvs11-3c) ───────────────────────────────────
    //
    // A projection of LvsRunResult and nothing else: every number below is one of its fields or one
    // of its own derived accessors. Nothing is recounted here, because a second tally is a second
    // answer to "what did the comparison find".

    private static LvsCellJson Project(string cellDir, string name, LvsRunResult result) =>
        new(cellDir, name, true,
            result.TechnologyName,
            result.Reduction == ReductionMode.On ? "on" : "off",
            Side(result.Counts.Schematic), Side(result.Counts.Layout),
            result.ErrorCount, result.WarningCount, result.WaivedCount, result.IsClean,
            result.Hierarchy.Extractions, result.Hierarchy.CacheHits,
            [.. result.Cells.Select(c => c.CellName)],
            [.. result.Findings.Select(Finding)],
            [.. result.Turned.Select(t => new LvsTurnedPartJson(
                t.Refdes, t.InstanceIndex, [t.Land1.X, t.Land1.Y], [t.Land2.X, t.Land2.Y], t.Agreeing))]);

    private static LvsCellJson Skipped(string cellDir, string name, Diagnostic note) =>
        new(cellDir, name, false, null, "on",
            Side(default), Side(default),
            0, 0, 0, true, 0, 0, [], [Finding(note)], []);

    private static LvsSideCountsJson Side(LvsSideCounts c) =>
        new(c.DevicesBefore, c.DevicesAfter, c.NetsBefore, c.NetsAfter);

    private static LvsFindingJson Finding(LvsFinding f)
    {
        var d = DiagnosticJson.From(f.Diagnostic);
        return new LvsFindingJson(
            d.Id, d.Severity, d.Message,
            f.Objects, f.Waived, f.WaiverReason,
            f.Marker.IsEmpty ? null : [f.Marker.MinX, f.Marker.MinY, f.Marker.MaxX, f.Marker.MaxY],
            d.Arguments);
    }

    /// <summary>A bare diagnostic as a finding — the skipped cell's one line, which is about the
    /// run and so has no objects and nowhere to look.</summary>
    private static LvsFindingJson Finding(Diagnostic diagnostic)
    {
        var d = DiagnosticJson.From(diagnostic);
        return new LvsFindingJson(d.Id, d.Severity, d.Message, [], false, null, null, d.Arguments);
    }
}
